using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CookingSimMP
{
    // One shared wallet, kept by the host. Every cash change in the game goes through PlayerStats.ChangeCash
    // (buying, dish payments, tips, repairs, recipes), so that's the one method we watch.
    //
    // Host: every change it makes is sent to the clients with the new balance.
    // Client: a change it makes shows up at once and is sent to the host, which applies it once and sends the
    // new balance back. Until the host confirms, the client shows the host's balance plus its own unconfirmed
    // changes, so the number doesn't flicker. Items bought by one player are copied on the other with
    // pay: false (ItemSync), so nobody is charged twice.
    internal sealed class MoneySync
    {
        private const float SettleTime = 2f;

        internal static MoneySync Instance;

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".money");
        private readonly MethodInfo _cashSetter = AccessTools.PropertySetter(typeof(PlayerStats), nameof(PlayerStats.Cash));

        // True while we apply a change that came from the network, so it isn't sent back out.
        private bool _applying;

        // Host side.
        private float _lastSentBalance = float.NaN;
        private bool _readyRequested;

        // Client side.
        private readonly int _token = UnityEngine.Random.Range(1, int.MaxValue);
        private int _nextSeq = 1;
        private readonly List<KeyValuePair<int, float>> _unconfirmed = new List<KeyValuePair<int, float>>();
        private string _kitchenScene;
        private float _inKitchenFor;
        private string _readySentForScene;
        private bool _firstSyncLogged;

        public MoneySync(NetSession session, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.Stopped += Reset;
            _session.WorldReset += Reset;
            _session.PeerLeft += _ => { if (!_session.IsHost) Reset(); };

            try
            {
                MethodInfo changeCash = AccessTools.Method(typeof(PlayerStats), nameof(PlayerStats.ChangeCash));
                _harmony.Patch(changeCash,
                    prefix: new HarmonyMethod(typeof(MoneyPatches), nameof(MoneyPatches.ChangeCashPrefix)),
                    postfix: new HarmonyMethod(typeof(MoneyPatches), nameof(MoneyPatches.ChangeCashPostfix)));
            }
            catch (Exception e)
            {
                _log.LogError($"[Money] Patching PlayerStats.ChangeCash failed; money won't sync: {e}");
            }
            if (_cashSetter == null)
                _log.LogError("[Money] Couldn't find the PlayerStats.Cash setter; client balances may drift.");
        }

        public static bool Handles(MessageType type) =>
            type == MessageType.MoneyReady || type == MessageType.MoneyRequest || type == MessageType.MoneyChange;

        private static bool InKitchen => WorldSync.InKitchen;

        private void Reset()
        {
            _unconfirmed.Clear();
            _readySentForScene = null;
            _readyRequested = false;
            _lastSentBalance = float.NaN;
            _firstSyncLogged = false;
        }

        public void Tick(float deltaTime)
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != _kitchenScene)
            {
                _kitchenScene = scene;
                _inKitchenFor = 0f;
            }
            _inKitchenFor = InKitchen ? _inKitchenFor + deltaTime : 0f;

            if (!_session.IsConnected || _inKitchenFor < SettleTime)
                return;

            if (_session.IsHost)
            {
                // Catches changes that don't go through ChangeCash (loading a save, sandbox's fixed cash).
                float cash = PlayerStats.Me.Cash;
                if (_readyRequested || !Mathf.Approximately(cash, _lastSentBalance))
                {
                    _readyRequested = false;
                    SendChange(0f, PlayerStats.CashType.PROFIT, 0, 0);
                }
            }
            else if (_readySentForScene != scene)
            {
                // Once this copy has loaded its kitchen (and its own save's money), ask for the shared balance.
                _readySentForScene = scene;
                _unconfirmed.Clear();
                _session.BeginMessage(MessageType.MoneyReady);
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
            }
        }

        // ---------- Local changes ----------

        internal void OnLocalChange(float before, PlayerStats.CashType type)
        {
            if (_applying || !_session.IsConnected)
                return;
            // Sandbox and challenges ignore cash changes; only send what actually happened.
            float delta = PlayerStats.Me.Cash - before;
            if (delta == 0f)
                return;

            if (_session.IsHost)
            {
                SendChange(delta, type, 0, 0);
                return;
            }
            int seq = _nextSeq++;
            _unconfirmed.Add(new KeyValuePair<int, float>(seq, delta));
            NetDataWriter writer = _session.BeginMessage(MessageType.MoneyRequest);
            writer.Put(_token);
            writer.Put(seq);
            writer.Put(delta);
            writer.Put((byte)type);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        // Host -> clients: "the wallet changed by delta; it now holds balance". origin/seq name the client
        // whose request this was (0 for the host's own changes).
        private void SendChange(float delta, PlayerStats.CashType type, int originToken, int originSeq)
        {
            float balance = PlayerStats.Me.Cash;
            _lastSentBalance = balance;
            NetDataWriter writer = _session.BeginMessage(MessageType.MoneyChange);
            writer.Put(delta);
            writer.Put((byte)type);
            writer.Put(balance);
            writer.Put(originToken);
            writer.Put(originSeq);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        // ---------- Receiving ----------

        private void OnMessage(int peerId, MessageType type, NetDataReader reader)
        {
            switch (type)
            {
                case MessageType.MoneyReady:
                    if (_session.IsHost)
                        _readyRequested = true;
                    break;
                case MessageType.MoneyRequest:
                    if (_session.IsHost)
                        OnClientRequest(reader);
                    break;
                case MessageType.MoneyChange:
                    if (!_session.IsHost)
                        OnHostChange(reader);
                    break;
            }
        }

        private void OnClientRequest(NetDataReader reader)
        {
            int token = reader.GetInt();
            int seq = reader.GetInt();
            float delta = reader.GetFloat();
            var type = (PlayerStats.CashType)reader.GetByte();
            if (InKitchen)
                Apply(() => PlayerStats.Me.ChangeCash(delta, type));
            // Confirm even if nothing changed here, so the client stops counting it as unconfirmed.
            SendChange(delta, type, token, seq);
        }

        private void OnHostChange(NetDataReader reader)
        {
            float delta = reader.GetFloat();
            var type = (PlayerStats.CashType)reader.GetByte();
            float balance = reader.GetFloat();
            int originToken = reader.GetInt();
            int originSeq = reader.GetInt();
            if (!InKitchen)
                return;

            if (originToken == _token)
                _unconfirmed.RemoveAll(p => p.Key <= originSeq);
            else if (delta != 0f)
                // Goes through the game so the day's income/expense totals and the money animation match.
                Apply(() => PlayerStats.Me.ChangeCash(delta, type));

            float target = balance;
            foreach (KeyValuePair<int, float> p in _unconfirmed)
                target += p.Value;
            if (!Mathf.Approximately(PlayerStats.Me.Cash, target))
                Apply(() => SetCash(target));

            if (!_firstSyncLogged)
            {
                _firstSyncLogged = true;
                _log.LogInfo($"[Money] Sharing the host's wallet: {balance:0.##}.");
            }
        }

        private void Apply(Action action)
        {
            _applying = true;
            try
            {
                action();
            }
            finally
            {
                _applying = false;
            }
        }

        // The setter also updates the money display; ForceSetCash doesn't.
        private void SetCash(float value)
        {
            if (_cashSetter != null)
                _cashSetter.Invoke(PlayerStats.Me, new object[] { value });
            else
                PlayerStats.Me.ForceSetCash(value);
        }
    }

    // Harmony entry points. Each forwards to MoneySync and never lets an exception reach the game.
    internal static class MoneyPatches
    {
        public static void ChangeCashPrefix(PlayerStats __instance, out float __state) => __state = __instance.Cash;

        public static void ChangeCashPostfix(float __state, PlayerStats.CashType type)
        {
            try
            {
                MoneySync.Instance?.OnLocalChange(__state, type);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Money] patch error: " + e);
            }
        }
    }
}
