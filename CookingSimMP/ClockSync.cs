using System;
using BepInEx.Logging;
using CS.GUI.TV;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using WIUtils;

namespace CookingSimMP
{
    // The career day clock (StandardTimeManager), owned by the host (v0.10). Before this, each copy ran its own
    // clock, so the two were close but drifted, and each changed phase (serving starts, cleaning starts) on its own.
    //
    // Host: sends its time of day, phase and whether the clock runs once a second, and at once when the phase
    // changes or someone skips.
    // Client: keeps its clock moving between messages so the TV clock ticks smoothly, but never into a phase the host
    // isn't in yet and never more than a moment ahead of the host's last message. Each message sets the client's time
    // to the host's; a phase change then happens through the game's own UpdateDayTime, so the serving-start and
    // cleaning-start effects play as usual.
    //
    // The client's TV skip buttons don't skip; they ask the host ("<name> is asking to skip ahead"), like the pause
    // request.
    internal sealed class ClockSync
    {
        private const float SendInterval = 1f;
        private const float MaxAheadOfHost = 1.5f;   // Client: real seconds the clock may run on after the host's last message.
        private const float RequestCooldown = 5f;

        internal static ClockSync Instance;

        // True while applying the host's time, so the client's UpdateDayTime patch lets it through untouched.
        private static bool _applying;

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".clock");

        private static readonly AccessTools.FieldRef<StandardTimeManager, DateTime> DayRef = DayField();

        // Host side.
        private float _sendTimer;
        private DayTimeType _sentPhase;
        private bool _sentRunning;
        private bool _sendNow;

        // Client side.
        private bool _hostKnown;
        private DayTimeType _hostPhase;
        private float _sinceHost;
        private float _requestCooldown;
        private bool _loggedFirst;
        private bool _endHandled;   // The host's day has reached its end and this copy has ended its day for it.

        private static AccessTools.FieldRef<StandardTimeManager, DateTime> DayField()
        {
            try
            {
                return AccessTools.FieldRefAccess<StandardTimeManager, DateTime>("<Day>k__BackingField");
            }
            catch (Exception)
            {
                return null;
            }
        }

        public ClockSync(NetSession session, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.Stopped += Reset;
            _session.WorldReset += Reset;
            _session.PeerJoined += _ => _sendNow = true;

            if (DayRef == null)
                _log.LogError("[Clock] Couldn't find StandardTimeManager's day field; the day clock isn't synced.");
            Patch(typeof(StandardTimeManager), nameof(StandardTimeManager.UpdateDayTime), nameof(UpdateDayTimePrefix));
            Patch(typeof(StandardTimeManager), nameof(StandardTimeManager.SkipToNextDayPhase), nameof(SkipPrefix), nameof(SkipPostfix));
            Patch(typeof(StandardTimeManager), nameof(StandardTimeManager.SkipToCleaning), null, nameof(SkipPostfix));
            Patch(typeof(TVUIDaySkipping), "SkipFirstPhase", nameof(TvSkipPrefix));
            Patch(typeof(TVUIDaySkipping), "SkipServePhase", nameof(TvSkipPrefix));
            Patch(typeof(TVUIDaySkipping), "SkipLastPhase", nameof(TvSkipPrefix));
            Patch(typeof(TVUIDaySkipping), "EndAllRecipes", nameof(TvSkipPrefix));
        }

        public static bool Handles(MessageType type) => type == MessageType.DayClock || type == MessageType.SkipRequest;

        private void Patch(Type type, string method, string prefix, string postfix = null)
        {
            try
            {
                _harmony.Patch(AccessTools.Method(type, method),
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(ClockSync), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(ClockSync), postfix));
            }
            catch (Exception e)
            {
                _log.LogError($"[Clock] Patching {type.Name}.{method} failed; that part of the day clock isn't synced: {e}");
            }
        }

        private void Reset()
        {
            _endHandled = false;
            _hostKnown = false;
            _sendNow = true;
            _loggedFirst = false;
        }

        private static StandardTimeManager Time => Game.CurrentClassicGameService?.StandardTime;

        private static bool IsSandbox => Singleton<GameManager>.Me != null && Singleton<GameManager>.Me.GameState.HasFlag(GameState.Sandbox);

        private bool Active => _session.IsConnected && WorldSync.InKitchen && DayRef != null && Time != null && !IsSandbox;

        internal bool ClientFollows => Active && !_session.IsHost;

        // ---------- Host ----------

        public void Tick(float deltaTime)
        {
            if (_requestCooldown > 0f)
                _requestCooldown -= deltaTime;
            if (!Active)
                return;
            if (!_session.IsHost)
            {
                _sinceHost += deltaTime;
                return;
            }
            StandardTimeManager time = Time;
            _sendTimer += deltaTime;
            if (_sendNow || _sendTimer >= SendInterval || time.GetCurrentDayTime != _sentPhase || time.timeRunning != _sentRunning)
                SendClock(time);
        }

        private void SendClock(StandardTimeManager time)
        {
            _sendNow = false;
            _sendTimer = 0f;
            _sentPhase = time.GetCurrentDayTime;
            _sentRunning = time.timeRunning;
            TimeSpan t = DayRef(time).TimeOfDay;
            NetDataWriter writer = _session.BeginMessage(MessageType.DayClock);
            writer.Put(t.Ticks);
            writer.Put((byte)time.GetCurrentDayTime);
            writer.Put(time.timeRunning);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        internal void OnHostSkipped() => _sendNow = true;

        // ---------- Client ----------

        // How far the client's clock may move on its own this tick (0 = hold until the host's next message).
        internal float LimitLocalTick(StandardTimeManager time, float seconds)
        {
            if (!_hostKnown || _sinceHost > MaxAheadOfHost || CareerSync.ClientWaitingForDay)
                return 0f;
            DateTime next = DayRef(time).AddSeconds(seconds * time.TimeAcceleration);
            if (Constants.Me.GetDayPhaseOfTime(next.Hour) != _hostPhase
                || next.TimeOfDay.Hours >= Constants.Me.GetDayTypeTimeHour(DayTimeType.EndDay))
                return 0f;
            return seconds;
        }

        private void OnClock(NetPacketReader reader)
        {
            long ticks = reader.GetLong();
            var phase = (DayTimeType)reader.GetByte();
            bool running = reader.GetBool();
            StandardTimeManager time = Time;
            _hostKnown = true;
            _hostPhase = phase;
            _sinceHost = 0f;

            // While this copy runs its own end-of-day screens, leave its clock alone. Once the host's clock reaches
            // the end of the day, end it here once; the host keeps sending that time until its own next day starts.
            // v0.11: the client no longer ends its day (CareerSync); it waits for the host's next day to arrive with
            // the world, so the host's new morning mustn't be applied to this copy's old day.
            if (CareerSync.ClientWaitingForDay)
                return;
            if (Game.CurrentGameService?.DayFlow != null && Game.CurrentGameService.DayFlow.DuringNextDayCoroutine)
                return;
            bool hostAtEnd = new TimeSpan(ticks).Hours >= Constants.Me.GetDayTypeTimeHour(DayTimeType.EndDay);
            if (hostAtEnd && _endHandled)
                return;
            _endHandled = hostAtEnd;

            DayRef(time) = DateTime.Today.AddTicks(ticks);
            // With no time passing, UpdateDayTime only does the phase change and the TV clock, as the game would.
            _applying = true;
            try
            {
                time.timeRunning = true;
                time.UpdateDayTime(0f);
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Clock] Applying the host's time failed: {e.Message}");
            }
            finally
            {
                time.timeRunning = running;
                _applying = false;
            }
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                _log.LogInfo($"[Clock] Following the host's clock: {DateTime.Today.AddTicks(ticks):HH:mm}, {phase}.");
            }
        }

        // Client: a TV skip button asks the host instead.
        internal void RequestSkip()
        {
            if (_requestCooldown > 0f)
                return;
            _requestCooldown = RequestCooldown;
            string name = PauseSync.LocalName();
            PauseSync.Instance?.Notify($"{name} is asking to skip ahead");
            _session.BeginMessage(MessageType.SkipRequest).Put(name);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        // ---------- Messages ----------

        private void OnMessage(int peerId, MessageType type, NetPacketReader reader)
        {
            switch (type)
            {
                case MessageType.DayClock:
                    if (ClientFollows)
                        OnClock(reader);
                    break;
                case MessageType.SkipRequest:
                    string name = reader.GetString(64);
                    PauseSync.Instance?.Notify($"{name} is asking to skip ahead (use the TV)");
                    if (_session.IsHost)
                    {
                        _session.BeginMessage(MessageType.SkipRequest).Put(name);
                        _session.SendToAll(DeliveryMethod.ReliableOrdered);
                    }
                    break;
            }
        }

        // ---------- Harmony ----------

        private static bool UpdateDayTimePrefix(StandardTimeManager __instance, ref float timeInRealSecondsSinceLastCheck)
        {
            try
            {
                ClockSync sync = Instance;
                if (_applying || sync == null || !sync.ClientFollows)
                    return true;
                timeInRealSecondsSinceLastCheck = sync.LimitLocalTick(__instance, timeInRealSecondsSinceLastCheck);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Clock] patch error: " + e);
            }
            return true;
        }

        // Client: only the host skips (OrderSync's kitchen-closed message still runs SkipToCleaning here).
        private static bool SkipPrefix()
        {
            ClockSync sync = Instance;
            if (_applying || sync == null || !sync.ClientFollows)
                return true;
            sync.RequestSkip();
            return false;
        }

        private static void SkipPostfix()
        {
            ClockSync sync = Instance;
            if (sync != null && sync._session.IsHost)
                sync.OnHostSkipped();
        }

        private static bool TvSkipPrefix()
        {
            ClockSync sync = Instance;
            if (sync == null || !sync.ClientFollows)
                return true;
            try
            {
                sync.RequestSkip();
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Clock] patch error: " + e);
            }
            return false;
        }
    }
}
