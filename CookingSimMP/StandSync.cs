using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using WIUtils;
using Object = UnityEngine.Object;

namespace CookingSimMP
{
    // Appliance wear and repairs (v0.10).
    //
    // How the game does it: every appliance (stove, oven, fryer...) has a DestructibleBase with a condition
    // (CurrentHP out of MaxHP). While it's switched on, ActionDestructable.Update wears it down, and when its
    // condition is low it can also break at random. Knocks (DestructibleBase.CollisionEnter) wear it too. Picking up
    // the phone turns on repair mode; clicking a worn appliance calls RepairMode.StartRepair, which pays (through
    // PlayerStats.ChangeCash, so MoneySync shares the cost) and starts the repair with the chosen company.
    //
    // Wear and breakdowns are random, so only the host runs them. The client's own wear is switched off; knocks on
    // the client's copy are sent to the host as damage. The host sends the condition of every appliance that
    // changed twice a second, and the client copies it, including breakdowns.
    //
    // A repair started on either copy starts on the other with the same company and without paying again. Stopping
    // a repair works the same way. Each copy then runs the repair's progress itself; the host's condition wins.
    internal sealed class StandSync
    {
        private const float StateInterval = 0.5f;
        private const float HitInterval = 0.25f;
        private const float HpEpsilon = 0.5f;

        internal static StandSync Instance;

        // True while applying the other copy's change, so it isn't sent back.
        private static bool _applying;

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".stands");

        private readonly Dictionary<int, SentState> _sent = new Dictionary<int, SentState>();   // Host: last condition sent for each appliance.
        private readonly Dictionary<int, float> _hits = new Dictionary<int, float>();           // Client: damage not yet sent.
        private readonly HashSet<int> _warnedMissing = new HashSet<int>();
        private float _stateTimer;
        private float _hitTimer;

        private struct SentState
        {
            public float Hp;
            public bool Destroyed;
            public bool Repairing;
        }

        public StandSync(NetSession session, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.Stopped += Reset;
            _session.WorldReset += Reset;
            _session.PeerJoined += _ => _sent.Clear();

            Patch(typeof(ActionDestructable), "Update", Type.EmptyTypes, prefix: nameof(WearPrefix));
            Patch(typeof(DestructibleBase), nameof(DestructibleBase.CollisionEnter), new[] { typeof(Collision) },
                prefix: nameof(HitPrefix), postfix: nameof(HitPostfix));
            Patch(typeof(DestructibleBase), nameof(DestructibleBase.CollisionEnter), new[] { typeof(Rigidbody), typeof(Vector3) },
                prefix: nameof(HitPrefix), postfix: nameof(HitPostfix));
            Patch(typeof(RepairMode), nameof(RepairMode.StartRepair), new[] { typeof(Repairable), typeof(bool) },
                prefix: nameof(RepairPrefix), postfix: nameof(StartRepairPostfix));
            Patch(typeof(RepairMode), nameof(RepairMode.TryStopRepair), new[] { typeof(Repairable) },
                prefix: nameof(RepairPrefix), postfix: nameof(StopRepairPostfix));
        }

        public static bool Handles(MessageType type) =>
            type == MessageType.StandState || type == MessageType.StandHit || type == MessageType.RepairStart || type == MessageType.RepairStop;

        private bool Active => _session.IsConnected && WorldSync.InKitchen;

        private bool ClientFollows => Active && !_session.IsHost;

        private void Reset()
        {
            _sent.Clear();
            _hits.Clear();
            _warnedMissing.Clear();
        }

        private void Patch(Type type, string method, Type[] args, string prefix = null, string postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method, args);
                if (original == null)
                {
                    _log.LogError($"[Stands] Couldn't find {type.Name}.{method}; that part of appliance syncing is off.");
                    return;
                }
                _harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(StandSync), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(StandSync), postfix));
            }
            catch (Exception e)
            {
                _log.LogError($"[Stands] Patching {type.Name}.{method} failed; that part of appliance syncing is off: {e}");
            }
        }

        private static DestructibleBase FindBase(int id) =>
            DestructibleBase.DestructibleBases.FirstOrDefault(b => b != null && b.ID == id);

        private static Repairable FindRepairable(int id) =>
            Object.FindObjectsOfType<Repairable>().FirstOrDefault(r => r != null && r.destructibleBase != null && r.destructibleBase.ID == id);

        // ---------- Tick ----------

        public void Tick(float deltaTime)
        {
            if (!Active)
                return;
            if (_session.IsHost)
            {
                _stateTimer += deltaTime;
                if (_stateTimer >= StateInterval)
                {
                    _stateTimer = 0f;
                    SendStates();
                }
                return;
            }
            _hitTimer += deltaTime;
            if (_hitTimer >= HitInterval && _hits.Count > 0)
            {
                _hitTimer = 0f;
                NetDataWriter writer = _session.BeginMessage(MessageType.StandHit);
                writer.Put(_hits.Count);
                foreach (KeyValuePair<int, float> hit in _hits)
                {
                    writer.Put(hit.Key);
                    writer.Put(hit.Value);
                }
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
                _hits.Clear();
            }
        }

        // Host: the condition of every appliance that changed since it was last sent.
        private void SendStates()
        {
            var changed = new List<KeyValuePair<int, SentState>>();
            foreach (DestructibleBase b in DestructibleBase.DestructibleBases)
            {
                if (b == null)
                    continue;
                Repairable r = b.GetComponent<Repairable>();
                var state = new SentState { Hp = b.CurrentHP, Destroyed = b.IsDestroyed, Repairing = r != null && r.DuringRepair };
                if (_sent.TryGetValue(b.ID, out SentState last) && Mathf.Abs(last.Hp - state.Hp) < HpEpsilon
                    && last.Destroyed == state.Destroyed && last.Repairing == state.Repairing)
                    continue;
                _sent[b.ID] = state;
                changed.Add(new KeyValuePair<int, SentState>(b.ID, state));
            }
            if (changed.Count == 0)
                return;
            NetDataWriter writer = _session.BeginMessage(MessageType.StandState);
            writer.Put(changed.Count);
            foreach (KeyValuePair<int, SentState> pair in changed)
            {
                writer.Put(pair.Key);
                writer.Put(pair.Value.Hp);
                writer.Put(pair.Value.Destroyed);
                writer.Put(pair.Value.Repairing);
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        // ---------- Client: copy the host's condition ----------

        private void OnStates(NetPacketReader reader)
        {
            int count = reader.GetInt();
            for (int i = 0; i < count; i++)
            {
                int id = reader.GetInt();
                float hp = reader.GetFloat();
                bool destroyed = reader.GetBool();
                bool repairing = reader.GetBool();
                DestructibleBase b = FindBase(id);
                if (b == null)
                {
                    if (_warnedMissing.Add(id))
                        _log.LogWarning($"[Stands] The host has appliance {id}, which this copy doesn't.");
                    continue;
                }
                _applying = true;
                try
                {
                    ApplyState(b, hp, destroyed, repairing);
                }
                catch (Exception e)
                {
                    _log.LogWarning($"[Stands] Copying the host's condition of appliance {id} failed: {e.Message}");
                }
                finally
                {
                    _applying = false;
                }
            }
        }

        private static void ApplyState(DestructibleBase b, float hp, bool destroyed, bool repairing)
        {
            Repairable r = b.GetComponent<Repairable>();
            // A repair running here moves the condition up each frame; let it, and take the host's value once it's done.
            if (!(r != null && r.DuringRepair && repairing))
                b.CurrentHP = hp;

            if (destroyed && !b.IsDestroyed)
            {
                // The host's appliance broke down (worn out, or the random breakdown): break it the same way here.
                ActionDestructable action = b.GetComponent<ActionDestructable>();
                if (action != null)
                    action.SetBreak();
                else
                    b.Destroy();
            }
            else if (!destroyed && b.IsDestroyed && !(r != null && r.DuringRepair))
            {
                // Fixed on the host, but this copy never saw the repair: finish it the way Repairable.StopRepair does.
                b.IsDestroyed = false;
                if (r != null)
                {
                    foreach (DestructibleComponent component in r.destructibleComponents)
                        component?.OnRepairEnd();
                    if (r.guiRepairMarker != null)
                        r.guiRepairMarker.gameObject.SetActive(false);
                }
                Game.KitchenServices.Stands.UpdateStandState(b.gameObject, StandState.Fixed);
            }
            b.UpdateState();
        }

        // ---------- Host: knocks on the client's copy ----------

        private void OnHits(NetPacketReader reader)
        {
            int count = reader.GetInt();
            for (int i = 0; i < count; i++)
            {
                int id = reader.GetInt();
                float damage = reader.GetFloat();
                DestructibleBase b = FindBase(id);
                if (b == null)
                    continue;
                b.CurrentHP -= damage;
                b.UpdateState();
            }
        }

        // ---------- Repairs (both ways) ----------

        private void SendRepair(MessageType type, Repairable r)
        {
            if (_applying || !Active || r == null || r.destructibleBase == null)
                return;
            int company = 0;
            RepairCompanyList list = Singleton<RepairCompanyList>.Me;
            if (list != null && r.SelectedCompany != null)
                company = Mathf.Max(0, list.AvibleCompany.IndexOf(r.SelectedCompany));
            NetDataWriter writer = _session.BeginMessage(type);
            writer.Put(r.destructibleBase.ID);
            writer.Put(company);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"[Stands] {(type == MessageType.RepairStart ? "Called a repair for" : "Stopped the repair of")} {r.gameObject.name} ({r.destructibleBase.ID}) here; told the other player.");
        }

        private void OnRepair(MessageType type, NetPacketReader reader)
        {
            int id = reader.GetInt();
            int company = reader.GetInt();
            Repairable r = FindRepairable(id);
            if (r == null)
            {
                _log.LogWarning($"[Stands] The other player repaired appliance {id}, which this copy doesn't have.");
                return;
            }
            _applying = true;
            try
            {
                if (type == MessageType.RepairStop)
                {
                    if (r.DuringRepair)
                        r.StopRepair();
                    return;
                }
                if (r.DuringRepair)
                    return;
                // StartRepair hires the company picked in this copy's phone; use the other player's for this call.
                RepairCompanyList list = Singleton<RepairCompanyList>.Me;
                RepairCompany savedCompany = list.SelectedToHire;
                int savedId = list.SelectedCompanyID;
                try
                {
                    if (company >= 0 && company < list.AvibleCompany.Count)
                    {
                        list.SelectedCompanyID = company;
                        list.SelectedToHire = list.AvibleCompany[company];
                    }
                    // The other player already paid; MoneySync shares that.
                    Singleton<RepairMode>.Me.StartRepair(r, withoutChangeCash: true);
                }
                finally
                {
                    list.SelectedCompanyID = savedId;
                    list.SelectedToHire = savedCompany;
                }
                _log.LogInfo($"[Stands] The other player called a repair for {r.gameObject.name} ({id}); started it here too.");
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Stands] Starting the other player's repair of appliance {id} failed: {e.Message}");
            }
            finally
            {
                _applying = false;
            }
        }

        // ---------- Messages ----------

        private void OnMessage(int peerId, MessageType type, NetPacketReader reader)
        {
            if (!Handles(type) || !Active)
                return;
            switch (type)
            {
                case MessageType.StandState:
                    if (!_session.IsHost)
                        OnStates(reader);
                    break;
                case MessageType.StandHit:
                    if (_session.IsHost)
                        OnHits(reader);
                    break;
                case MessageType.RepairStart:
                case MessageType.RepairStop:
                    OnRepair(type, reader);
                    break;
            }
        }

        // ---------- Harmony ----------

        // Client: no wear and no random breakdowns of its own; the host's condition comes in instead.
        private static bool WearPrefix()
        {
            StandSync sync = Instance;
            return sync == null || !sync.ClientFollows;
        }

        private static void HitPrefix(DestructibleBase __instance, out float __state) => __state = __instance.CurrentHP;

        private static void HitPostfix(DestructibleBase __instance, float __state)
        {
            try
            {
                StandSync sync = Instance;
                if (sync == null || _applying || !sync.ClientFollows)
                    return;
                float damage = __state - __instance.CurrentHP;
                if (damage <= 0f)
                    return;
                sync._hits.TryGetValue(__instance.ID, out float total);
                sync._hits[__instance.ID] = total + damage;
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Stands] patch error: " + e);
            }
        }

        private static void RepairPrefix(Repairable r, out bool __state) => __state = r != null && r.DuringRepair;

        private static void StartRepairPostfix(Repairable r, bool __state)
        {
            try
            {
                // In sandbox the repair is instant, so DuringRepair stays false; it still started.
                if (!__state && r != null)
                    Instance?.SendRepair(MessageType.RepairStart, r);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Stands] patch error: " + e);
            }
        }

        private static void StopRepairPostfix(Repairable r, bool __state)
        {
            try
            {
                if (__state && r != null && !r.DuringRepair)
                    Instance?.SendRepair(MessageType.RepairStop, r);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Stands] patch error: " + e);
            }
        }
    }
}
