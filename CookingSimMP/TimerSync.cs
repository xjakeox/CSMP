using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using CS.GUI;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CookingSimMP
{
    // Kitchen timers (the wind-up ones you set with the special action), kept by the host.
    //
    // Host: whenever one of its timers starts counting, rings or stops, it sends the new state to the clients, and
    // every 2 seconds it resends all timers so the remaining time can't drift. Only the host's timers ring when
    // time runs out.
    // Client: a timer it sets, cancels or silences is sent to the host as a request; the host applies it and sends
    // the result to everyone. Its own copy counts down for a smooth popup but waits at 0:00 for the host's ring.
    internal sealed class TimerSync
    {
        private const float SnapshotInterval = 2f;
        private const float IgnoreSnapshotsAfterRequest = 1f;
        private const float CorrectAbove = 0.5f;   // Seconds of drift before a snapshot corrects a running timer.

        private enum TimerMode : byte { Idle, Counting, Ringing }

        internal static TimerSync Instance;

        // True while applying the other player's change, so the patches don't send it straight back.
        internal static bool Applying;

        private readonly NetSession _session;
        private readonly ItemSync _items;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".timers");

        private static readonly MethodInfo StartCountingDownMethod = AccessTools.Method(typeof(KitchenTimer), "StartCountingDown");
        private static readonly MethodInfo EndCountingDownMethod = AccessTools.Method(typeof(KitchenTimer), "EndCountingDown");
        private static readonly MethodInfo StartRingingMethod = AccessTools.Method(typeof(KitchenTimer), "StartRinging");
        private static readonly MethodInfo UpdateRotationMethod = AccessTools.Method(typeof(KitchenTimer), "UpdateRotation");

        // Client: when it last asked the host to change each timer.
        private readonly Dictionary<int, float> _requestedAt = new Dictionary<int, float>();
        private readonly HashSet<int> _warnedMissing = new HashSet<int>();
        private float _snapshotTimer;
        private float _clock;

        public TimerSync(NetSession session, ItemSync items, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _items = items;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.Stopped += Reset;
            _session.WorldReset += Reset;

            Patch("StartCountingDown", postfix: nameof(TimerPatches.StartCountingDownPostfix));
            Patch("EndCountingDown", prefix: nameof(TimerPatches.EndCountingDownPrefix), postfix: nameof(TimerPatches.EndCountingDownPostfix));
            Patch("StartRinging", postfix: nameof(TimerPatches.StartRingingPostfix));
            Patch(nameof(KitchenTimer.EndRinging), postfix: nameof(TimerPatches.EndRingingPostfix));
            // The game stops its clock (Time.timeScale = 0) while you turn a timer's dial. In a session that froze
            // everyone (the host's stop pauses the clients), so the clock keeps running instead.
            Patch("StartSetting", prefix: nameof(TimerPatches.StartSettingPrefix), postfix: nameof(TimerPatches.StartSettingPostfix));
            Patch("ExitSetting", postfix: nameof(TimerPatches.ExitSettingPostfix));
        }

        public static bool Handles(MessageType type) => type == MessageType.TimerState || type == MessageType.TimerRequest;

        private void Patch(string method, string prefix = null, string postfix = null)
        {
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(KitchenTimer), method),
                    prefix: prefix != null ? new HarmonyMethod(typeof(TimerPatches), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(TimerPatches), postfix) : null);
            }
            catch (Exception e)
            {
                _log.LogError($"[Timers] Patching KitchenTimer.{method} failed; timer sync is off: {e}");
            }
        }

        private void Reset()
        {
            _requestedAt.Clear();
            _warnedMissing.Clear();
            _snapshotTimer = 0f;
        }

        // Clock speed when the dial was opened, put back when it closes. -1 when the dial isn't open in a session.
        private float _scaleBeforeSetting = -1f;

        internal void BeforeStartSetting()
        {
            _scaleBeforeSetting = _session.IsConnected && WorldSync.InKitchen ? Time.timeScale : -1f;
        }

        internal void AfterStartSetting()
        {
            // Only undo the game's own stop; a clock that was already stopped (paused by the host) stays stopped.
            if (_scaleBeforeSetting > 0f)
                Time.timeScale = _scaleBeforeSetting;
        }

        internal void AfterExitSetting()
        {
            // The game sets the clock to 1 on close; if it was stopped when the dial opened (paused by the host),
            // keep it stopped so this copy doesn't run ahead.
            if (_scaleBeforeSetting >= 0f)
                Time.timeScale = _scaleBeforeSetting;
            _scaleBeforeSetting = -1f;
        }

        // The client's timers wait for the host's ring instead of ringing on their own.
        internal bool ClientFollows => _session.IsConnected && !_session.IsHost && WorldSync.InKitchen;

        public void Tick(float deltaTime)
        {
            _clock += deltaTime;
            if (!_session.IsConnected || !_session.IsHost || !WorldSync.InKitchen)
                return;
            _snapshotTimer += deltaTime;
            if (_snapshotTimer < SnapshotInterval)
                return;
            _snapshotTimer = 0f;
            foreach (KeyValuePair<int, IMovable> pair in _items.AllItems)
            {
                if (pair.Value is KitchenTimer timer && timer != null)
                    SendState(pair.Key, timer, snapshot: true);
            }
        }

        private static TimerMode ModeOf(KitchenTimer timer) =>
            timer.IsRinging ? TimerMode.Ringing : timer.IsCountingDown ? TimerMode.Counting : TimerMode.Idle;

        private static string Describe(float seconds)
        {
            int s = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{s / 60}:{s % 60:00}";
        }

        // ---------- A timer changed on this copy ----------

        internal void OnLocalChange(KitchenTimer timer)
        {
            if (Applying || !_session.IsConnected || !WorldSync.InKitchen || timer == null)
                return;
            if (!_items.TryGetNetId(timer, out int netId))
            {
                if (_warnedMissing.Add(timer.GetInstanceID()))
                    _log.LogWarning("[Timers] A timer changed here, but it isn't in the item table; the other player won't see it.");
                return;
            }
            if (_session.IsHost)
            {
                SendState(netId, timer, snapshot: false);
                return;
            }
            TimerMode mode = ModeOf(timer);
            // Only the host's timers ring; a client's never get there on their own.
            if (mode == TimerMode.Ringing)
                return;
            _requestedAt[netId] = _clock;
            NetDataWriter writer = _session.BeginMessage(MessageType.TimerRequest);
            writer.Put(netId);
            writer.Put((byte)mode);
            writer.Put(timer.RemainingTime);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        private void SendState(int netId, KitchenTimer timer, bool snapshot)
        {
            NetDataWriter writer = _session.BeginMessage(MessageType.TimerState);
            writer.Put(netId);
            writer.Put((byte)ModeOf(timer));
            writer.Put(timer.RemainingTime);
            writer.Put(snapshot);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        // ---------- The other player's change ----------

        private void OnTimerRequest(NetDataReader reader)
        {
            int netId = reader.GetInt();
            var mode = (TimerMode)reader.GetByte();
            float remaining = reader.GetFloat();
            if (!TryGetTimer(netId, out KitchenTimer timer))
                return;
            Apply(timer, mode, remaining, snapshot: false);
            if (mode == TimerMode.Counting)
                _log.LogInfo($"[Timers] Other player set timer {netId} to {Describe(remaining)}.");
            // Everyone gets the result, including the client that asked.
            SendState(netId, timer, snapshot: false);
        }

        private void OnTimerState(NetDataReader reader)
        {
            int netId = reader.GetInt();
            var mode = (TimerMode)reader.GetByte();
            float remaining = reader.GetFloat();
            bool snapshot = reader.GetBool();
            // A snapshot sent before the host got our own request would undo it for a moment.
            if (snapshot && _requestedAt.TryGetValue(netId, out float at) && _clock - at < IgnoreSnapshotsAfterRequest)
                return;
            if (!TryGetTimer(netId, out KitchenTimer timer))
                return;
            TimerMode before = ModeOf(timer);
            Apply(timer, mode, remaining, snapshot);
            if (!snapshot && before != mode && mode == TimerMode.Counting)
                _log.LogInfo($"[Timers] Host started timer {netId} at {Describe(remaining)}.");
        }

        private bool TryGetTimer(int netId, out KitchenTimer timer)
        {
            timer = _items.TryGetItem(netId, out IMovable m) ? m as KitchenTimer : null;
            if (timer != null)
                return true;
            if (_warnedMissing.Add(netId))
                _log.LogWarning($"[Timers] The other player changed timer {netId}, which this copy couldn't match.");
            return false;
        }

        // Brings this copy's timer to the given state the way the game would: the popup, the "set" sound and the
        // ring all come from the game's own methods.
        private void Apply(KitchenTimer timer, TimerMode mode, float remaining, bool snapshot)
        {
            // This player is turning the dial right now; their result goes out when they finish.
            if (timer.IsSettingTime)
                return;
            Applying = true;
            try
            {
                switch (mode)
                {
                    case TimerMode.Idle:
                        if (timer.IsCountingDown)
                            EndCountingDownMethod.Invoke(timer, new object[] { false });
                        if (timer.IsRinging)
                            timer.EndRinging();
                        break;

                    case TimerMode.Counting:
                        if (timer.IsRinging)
                            timer.EndRinging();
                        if (!timer.IsCountingDown || !snapshot || Mathf.Abs(timer.RemainingTime - remaining) > CorrectAbove)
                        {
                            timer.RemainingTime = remaining;
                            UpdateRotationMethod.Invoke(timer, null);
                        }
                        if (!timer.IsCountingDown)
                            StartCountingDownMethod.Invoke(timer, null);
                        break;

                    case TimerMode.Ringing:
                        if (timer.IsRinging)
                            break;
                        if (timer.IsCountingDown)
                        {
                            // Same as running out here: stops counting and rings, keeping the popup.
                            EndCountingDownMethod.Invoke(timer, new object[] { true });
                        }
                        else
                        {
                            StartRingingMethod.Invoke(timer, null);
                            if (EnviroGUI.Me != null)
                                EnviroGUI.Me.ShowTimer(timer);
                        }
                        break;
                }
            }
            finally
            {
                Applying = false;
            }
        }

        // ---------- Messages ----------

        private void OnMessage(int peerId, MessageType type, NetDataReader reader)
        {
            try
            {
                switch (type)
                {
                    case MessageType.TimerRequest:
                        if (_session.IsHost && WorldSync.InKitchen)
                            OnTimerRequest(reader);
                        break;
                    case MessageType.TimerState:
                        if (!_session.IsHost && WorldSync.InKitchen)
                            OnTimerState(reader);
                        break;
                }
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Timers] Applying the other player's timer failed: {e.Message}");
            }
        }
    }

    // Harmony entry points. Each forwards to TimerSync and never lets an exception reach the game.
    internal static class TimerPatches
    {
        private static void Changed(KitchenTimer timer)
        {
            try
            {
                TimerSync.Instance?.OnLocalChange(timer);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Timers] patch error: " + e);
            }
        }

        public static void StartCountingDownPostfix(KitchenTimer __instance) => Changed(__instance);

        // Client: time ran out here, but only the host rings. Hold at 0:00 until the host's ring arrives.
        public static bool EndCountingDownPrefix(KitchenTimer __instance, bool withTime)
        {
            try
            {
                if (withTime && !TimerSync.Applying && TimerSync.Instance != null && TimerSync.Instance.ClientFollows)
                {
                    __instance.RemainingTime = 0f;
                    return false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Timers] patch error: " + e);
            }
            return true;
        }

        // Stopped without ringing: the player started turning the dial of a running timer.
        public static void EndCountingDownPostfix(KitchenTimer __instance, bool withTime)
        {
            if (!withTime)
                Changed(__instance);
        }

        public static void StartRingingPostfix(KitchenTimer __instance) => Changed(__instance);

        public static void StartSettingPrefix() => Safe(() => TimerSync.Instance?.BeforeStartSetting());

        public static void StartSettingPostfix() => Safe(() => TimerSync.Instance?.AfterStartSetting());

        public static void ExitSettingPostfix() => Safe(() => TimerSync.Instance?.AfterExitSetting());

        private static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Timers] patch error: " + e);
            }
        }

        public static void EndRingingPostfix(KitchenTimer __instance) => Changed(__instance);
    }
}
