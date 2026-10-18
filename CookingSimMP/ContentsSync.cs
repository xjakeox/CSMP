using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using Helpers;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using UnityEngine;

namespace CookingSimMP
{
    // Keeps what's inside pans and pots (oil, water, sauces, spices) and the seasoning on food the same on both copies.
    //
    // Where it lives: a Container keeps everything poured into it as a list of "virtual products"
    // (Container.virtualProductPartStats): one mixed liquid plus any loose spices. Seasoning sprinkled on food sits
    // in ProductPart.virtualProductPartStats, and its taste in ProductPart.stats.takenFlavours. The game saves and
    // loads these lists as JSON (ContainerSave, ProductPartSave), so they're sent with the game's own serializer
    // and applied the way loading a save does (Container.ApplySaveData).
    //
    // Pouring only happens on the pourer's copy (the other copy's bottle just follows the hand), so whoever pours
    // or seasons sends the item's whole contents as soon as they change. Sending the whole state rather than
    // "add this much" means an update can never be applied twice. The host also sends anything whose contents
    // drift while cooking (temperature, flavour soaking into food) twice a second.
    //
    // Both players pouring into the same pot at once (v0.10): a client doesn't send its pours as whole contents.
    // It sends each pour as "add this much of this" (ContentsAdd), the host adds it to its own pot, and the host's
    // merged contents come back to everyone. The client keeps showing its own view until the host's contents
    // include its last pour, so neither player's pour is lost.
    //
    // Bottles and shakers (InfiniteProduct): how full they are and whether the cap is on. Both copies look for
    // changes five times a second; whoever's copy changed one sends it.
    internal sealed class ContentsSync
    {
        private const float DirtySendInterval = 0.1f;   // Local pours and seasoning go out at most 10 times a second.
        private const float DriftInterval = 0.5f;       // Host checks for cooking drift twice a second.
        private const float KeepLocalFor = 1f;          // Client ignores the host's older view of an item it just changed.
        private const float ChangeEpsilon = 0.01f;
        private const float BottleEpsilon = 0.00001f;   // Bottle amounts are small numbers (a few ml is a few thousandths).
        private const float BottleScanInterval = 0.2f;  // Both copies check bottles and shakers five times a second.
        private const float PendingAddTimeout = 3f;     // Client: stop waiting for the host to confirm a pour after this.

        private const byte KindContainer = 1;
        private const byte KindPart = 2;
        private const byte KindBottle = 3;

        private static ContentsSync _instance;

        // True while applying the other player's update, so the patches don't send it straight back.
        private static bool _applying;

        private readonly NetSession _session;
        private readonly ItemSync _items;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".contents");

        private static readonly FieldInfo MaxLeakField = AccessTools.Field(typeof(WaterSurface), "maxLeak");
        private static readonly FieldInfo MaxLeakInsideField = AccessTools.Field(typeof(WaterSurface), "maxLeakInside");

        private readonly HashSet<IMovable> _dirty = new HashSet<IMovable>();
        private readonly Dictionary<int, float> _lastSignature = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _changedLocallyAt = new Dictionary<int, float>();
        private readonly HashSet<int> _warnedUnknown = new HashSet<int>();
        private readonly HashSet<int> _warnedBroken = new HashSet<int>();
        private float _clock;
        private float _dirtyTimer;
        private float _driftTimer;
        private float _bottleTimer;

        // Pours from a client, merged by the host (see the top). The client numbers its pours; the host remembers
        // the last one it added to each pot and sends it with that pot's contents.
        private readonly int _token = UnityEngine.Random.Range(1, int.MaxValue);
        private int _nextAddSeq = 1;
        private readonly Dictionary<int, KeyValuePair<int, float>> _pendingAdd = new Dictionary<int, KeyValuePair<int, float>>();  // Client: pot -> (last pour sent, when).
        private readonly Dictionary<int, KeyValuePair<int, int>> _lastAdd = new Dictionary<int, KeyValuePair<int, int>>();       // Host: pot -> (client token, pour number).
        private readonly List<PendingAdd> _adds = new List<PendingAdd>();

        private struct PendingAdd
        {
            public int NetId;
            public int Seq;
            public float Quantity;
            public byte[] Stats;
        }

        // What gets sent for each kind of item. Field names are short because they end up in every message.
        private sealed class ContainerContents
        {
            public List<ProductPartStats> V;
            public float T;
        }

        private sealed class PartSeasoning
        {
            public List<ProductPartStats> V;
            public Dictionary<int, float> F;
        }

        private sealed class BottleState
        {
            public float Q;
            public bool O;
        }

        public ContentsSync(NetSession session, ItemSync items, ManualLogSource log)
        {
            _instance = this;
            _session = session;
            _items = items;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.PeerLeft += _ => Reset();
            _session.Stopped += Reset;
            _session.WorldReset += Reset;

            // Everything that changes a container's contents: pouring and pouring in loose spices, spilling or pouring
            // out, emptying, tasting, and the helpers that throw contents away. Pours on a client go to the host as adds.
            Patch(typeof(Container), "AddVirtualProductToContainer", new[] { typeof(ProductPartStats), typeof(float) },
                prefix: nameof(AddPrefix), postfix: nameof(AddPostfix));
            PatchDirty(typeof(Container), "OnFluidLeak", new[] { typeof(float), typeof(WaterSurfaceLeakController) });
            PatchDirty(typeof(Container), "ClearContaierFromFluid", Type.EmptyTypes);
            PatchDirty(typeof(Container), "TasteContent", Type.EmptyTypes);
            PatchDirty(typeof(ContainerHelpers), "ClearAllContains", new[] { typeof(Container), typeof(SpawnPoint) }, nameof(HelperPostfix));
            PatchDirty(typeof(ContainerHelpers), "ClearUnmovableContains", new[] { typeof(Container) }, nameof(HelperPostfix));
            // Seasoning landing on food.
            PatchDirty(typeof(ProductPart), "AddQuantityToProduct", new[] { typeof(ProductPartStats), typeof(ProductPartStats), typeof(float) });
            // A pan the other player is tilting spills on their copy; here it only follows, so it mustn't spill too.
            Patch(typeof(WaterSurface), "LateUpdate", Type.EmptyTypes, prefix: nameof(WaterSurfacePrefix));
            // Same for a bottle the other player is pouring: it tilts here too, and once its cap synced open (v0.10)
            // it poured here as well, so the pot got both pours. Here it only shows the stream (v0.10.2).
            Patch(typeof(InfiniteProduct), "StartAdd", Type.EmptyTypes, prefix: nameof(StartPourPrefix));
            Patch(typeof(InfiniteProduct), "StartAddAmount", new[] { typeof(float), typeof(float), typeof(Action) }, prefix: nameof(StartPourPrefix));
            // v0.11.5: the sink runs on both copies (its taps are synced), so its water reached the pot twice: on the host,
            // and on the client, which sent it on to the host. v0.11.7: on a client, sink water that hits a pot only
            // shows the pouring tooltip (Container.ReportDelta) and changes nothing; the host's water comes back with
            // the pot's contents. (v0.11.5 made the stream show-only, which lost the tooltip. v0.11.6 let the water
            // into the client's pot without sending it, but any later change to that pot sent the whole pot, client
            // water included, and the host's pot doubled.)
            Patch(typeof(Container), "OnVirtualProductSphereHit", new[] { typeof(VirtualProductSphereTrigger) },
                prefix: nameof(SphereHitPrefix));
            // v0.11.8: turning the tap off and on again quickly (both players at the sink, or a synced switch landing
            // right after a local one) started a second stream of spheres next to the first, so the sink ran double.
            Patch(typeof(VirtualProductTransporter), "SetParticle", null, prefix: nameof(SetParticlePrefix));
            if (VisualOnlyField == null)
                _log.LogWarning("[Contents] Couldn't find VirtualProductTransporter.isVisualsOnly; a bottle the other player pours may pour twice.");
            if (MaxLeakField == null || MaxLeakInsideField == null)
                _log.LogWarning("[Contents] Couldn't find WaterSurface's leak fields; a pan the other player tilts may spill twice.");
        }

        public static bool Handles(MessageType type) => type == MessageType.ContentsState || type == MessageType.ContentsAdd;

        private bool Active => _session.IsConnected && _items.HasTable;

        private void Reset()
        {
            IsSinkStream.Clear();
            _dirty.Clear();
            _lastSignature.Clear();
            _changedLocallyAt.Clear();
            _warnedUnknown.Clear();
            _warnedBroken.Clear();
            _pendingAdd.Clear();
            _lastAdd.Clear();
            _adds.Clear();
        }

        private void MarkDirty(IMovable movable)
        {
            if (_applying || !Active || !(movable is UnityEngine.Object obj) || obj == null)
                return;
            _dirty.Add(movable);
            if (_items.TryGetNetId(movable, out int netId))
                _changedLocallyAt[netId] = _clock;
        }

        public void Tick(float deltaTime)
        {
            _clock += deltaTime;
            if (!Active)
            {
                _dirty.Clear();
                return;
            }

            _dirtyTimer += deltaTime;
            if (_dirtyTimer >= DirtySendInterval && _adds.Count > 0)
                SendAdds();
            if (_dirtyTimer >= DirtySendInterval && _dirty.Count > 0)
            {
                _dirtyTimer = 0f;
                var toSend = new List<KeyValuePair<int, IMovable>>();
                foreach (IMovable m in _dirty)
                {
                    if (m is UnityEngine.Object obj && obj != null && _items.TryGetNetId(m, out int netId))
                        toSend.Add(new KeyValuePair<int, IMovable>(netId, m));
                }
                _dirty.Clear();
                Send(toSend);
            }

            _bottleTimer += deltaTime;
            if (_bottleTimer >= BottleScanInterval)
            {
                _bottleTimer = 0f;
                ScanBottles();
            }

            if (!_session.IsHost)
                return;
            _driftTimer += deltaTime;
            if (_driftTimer < DriftInterval)
                return;
            _driftTimer = 0f;
            SendDrift();
        }

        // Host: anything whose contents changed without a pour (heat, flavour soaking in), e.g. a pot of soup cooking.
        private void SendDrift()
        {
            var changed = new List<KeyValuePair<int, IMovable>>();
            foreach (KeyValuePair<int, IMovable> pair in _items.AllItems.ToList())
            {
                IMovable m = pair.Value;
                if (!(m is UnityEngine.Object obj) || obj == null || !(m is Container || m is ProductPart))
                    continue;
                float signature = Signature(m);
                bool known = _lastSignature.TryGetValue(pair.Key, out float last);
                if (known && Mathf.Abs(signature - last) < ChangeEpsilon)
                    continue;
                _lastSignature[pair.Key] = signature;
                // The first look only records it: both copies loaded it from the same save.
                // A pan the client is carrying (or just changed) is the client's to send.
                if (!known || _items.IsRemoteHeld(m) || RecentlyChangedHere(pair.Key))
                    continue;
                changed.Add(pair);
            }
            Send(changed);
        }

        private bool RecentlyChangedHere(int netId) =>
            _changedLocallyAt.TryGetValue(netId, out float at) && _clock - at < KeepLocalFor;

        // ---------- Reading and writing contents ----------

        private static float Signature(IMovable m)
        {
            if (m is Container c)
                return Signature(c.virtualProductPartStats) + c.FluidsTemperature * 0.7f;
            if (m is InfiniteProduct bottle)
                return bottle.stats != null ? bottle.stats.quantity + (bottle.IsOpened ? 0.5f : 0f) : 0f;
            if (m is ProductPart p)
            {
                float sum = Signature(p.virtualProductPartStats);
                if (p.stats != null && p.stats.takenFlavours != null)
                {
                    foreach (KeyValuePair<int, float> f in p.stats.takenFlavours)
                        sum += f.Key * 0.013f + f.Value * 1.7f;
                }
                return sum;
            }
            return 0f;
        }

        private static float Signature(List<ProductPartStats> list)
        {
            if (list == null)
                return 0f;
            float sum = list.Count * 1000f;
            foreach (ProductPartStats pps in list)
            {
                if (pps == null)
                    continue;
#pragma warning disable CS0618 // productID is what the game itself still uses everywhere.
                sum += pps.productID * 0.37f + pps.quantity * 1.3f + pps.temperature * 0.9f;
#pragma warning restore CS0618
                if (pps.takenFlavours != null)
                {
                    foreach (KeyValuePair<int, float> f in pps.takenFlavours)
                        sum += f.Key * 0.011f + f.Value * 1.9f;
                }
                if (pps.thermalGoodness != null)
                    sum += pps.thermalGoodness.AverageThermalGoodness * 2.1f;
            }
            return sum;
        }

        private void Send(List<KeyValuePair<int, IMovable>> items)
        {
            if (items.Count == 0)
                return;
            var encoded = new List<KeyValuePair<int, KeyValuePair<byte, byte[]>>>();
            foreach (KeyValuePair<int, IMovable> pair in items)
            {
                try
                {
                    byte kind;
                    object state;
                    if (pair.Value is Container c)
                    {
                        kind = KindContainer;
                        state = new ContainerContents { V = c.virtualProductPartStats, T = c.FluidsTemperature };
                    }
                    else if (pair.Value is ProductPart p)
                    {
                        kind = KindPart;
                        state = new PartSeasoning { V = p.virtualProductPartStats, F = p.stats?.takenFlavours };
                    }
                    else if (pair.Value is InfiniteProduct bottle && bottle.stats != null)
                    {
                        kind = KindBottle;
                        state = new BottleState { Q = bottle.stats.quantity, O = bottle.IsOpened };
                    }
                    else
                    {
                        continue;
                    }
                    encoded.Add(new KeyValuePair<int, KeyValuePair<byte, byte[]>>(pair.Key, new KeyValuePair<byte, byte[]>(kind, Pack(state))));
                    _lastSignature[pair.Key] = Signature(pair.Value);
                }
                catch (Exception e)
                {
                    if (_warnedBroken.Add(pair.Key))
                        _log.LogWarning($"[Contents] Couldn't read the contents of item {pair.Key} '{pair.Value.gameObject.name}': {e.Message}");
                }
            }
            if (encoded.Count == 0)
                return;

            NetDataWriter writer = _session.BeginMessage(MessageType.ContentsState);
            writer.Put(encoded.Count);
            foreach (var e in encoded)
            {
                writer.Put(e.Key);
                writer.Put(e.Value.Key);
                writer.PutBytesWithLength(e.Value.Value);
                // Host: the last client pour this pot's contents include, so that client knows it can take them.
                if (_session.IsHost && _lastAdd.TryGetValue(e.Key, out KeyValuePair<int, int> add))
                {
                    writer.Put(add.Key);
                    writer.Put(add.Value);
                }
                else
                {
                    writer.Put(0);
                    writer.Put(0);
                }
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        // ---------- Bottles and shakers ----------

        // Pouring and shaking only use up a bottle on the pourer's copy, and the cap only turns there.
        private void ScanBottles()
        {
            var changed = new List<KeyValuePair<int, IMovable>>();
            foreach (KeyValuePair<int, IMovable> pair in _items.AllItems)
            {
                if (!(pair.Value is InfiniteProduct bottle) || bottle == null || bottle.stats == null)
                    continue;
                float signature = Signature(bottle);
                bool known = _lastSignature.TryGetValue(pair.Key, out float last);
                if (known && Mathf.Abs(signature - last) < BottleEpsilon)
                    continue;
                _lastSignature[pair.Key] = signature;
                // The first look only records it, and a bottle in the other player's hand is theirs to send.
                if (!known || _items.IsRemoteHeld(bottle))
                    continue;
                changed.Add(pair);
            }
            Send(changed);
        }

        private static void ApplyBottle(InfiniteProduct bottle, BottleState state)
        {
            if (state == null || bottle.stats == null)
                return;
            bottle.stats.quantity = state.Q;
            if (bottle.IsOpened == state.O || bottle.capGameObject == null)
                return;
            if (state.O)
                bottle.ForceOpen();
            else
                bottle.ForceClose();
        }

        // ---------- Pours from a client (host merges) ----------

        // Client: queue this pour for the host instead of sending the pot's whole contents.
        private bool QueueAdd(Container c, ProductPartStats pps, float quantity)
        {
            if (_applying || !Active || _session.IsHost || pps == null || !_items.TryGetNetId(c, out int netId))
                return false;
            int seq = _nextAddSeq++;
            _adds.Add(new PendingAdd { NetId = netId, Seq = seq, Quantity = quantity, Stats = Pack(pps) });
            _pendingAdd[netId] = new KeyValuePair<int, float>(seq, _clock);
            return true;
        }

        private void SendAdds()
        {
            NetDataWriter writer = _session.BeginMessage(MessageType.ContentsAdd);
            writer.Put(_token);
            writer.Put(_adds.Count);
            foreach (PendingAdd add in _adds)
            {
                writer.Put(add.NetId);
                writer.Put(add.Seq);
                writer.Put(add.Quantity);
                writer.PutBytesWithLength(add.Stats);
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _adds.Clear();
        }

        // Host: add the client's pours to this copy's pots, then send everyone the merged contents.
        private void OnAdds(NetDataReader reader)
        {
            int token = reader.GetInt();
            int count = reader.GetInt();
            for (int i = 0; i < count; i++)
            {
                int netId = reader.GetInt();
                int seq = reader.GetInt();
                float quantity = reader.GetFloat();
                byte[] data = reader.GetBytesWithLength();
                if (!_items.TryGetItem(netId, out IMovable movable) || !(movable is Container c))
                {
                    if (_warnedUnknown.Add(netId))
                        _log.LogWarning($"[Contents] Got a pour into item {netId}, but this copy doesn't have that pot.");
                    continue;
                }
                _applying = true;
                try
                {
                    ProductPartStats pps = Unpack<ProductPartStats>(data);
                    pps?.thermalGoodness?.InitAllAreas();
                    if (pps != null)
                        c.AddVirtualProductToContainer(pps, quantity);
                    c.OnFluidQuantityChange?.Invoke();
                    // AddVirtualProductToContainer only changes the numbers. The game's own pours and blends go
                    // through AddFluidToContainer, which also raises the liquid and colours it, so do that here too
                    // (v0.11.2: a mixture the client blended showed in the host's tooltip but not in the pot).
                    RefreshLiquid(c);
                }
                catch (Exception e)
                {
                    if (_warnedBroken.Add(netId))
                        _log.LogWarning($"[Contents] Adding the client's pour to item {netId} failed: {e.Message}");
                }
                finally
                {
                    _applying = false;
                }
                _lastAdd[netId] = new KeyValuePair<int, int>(token, seq);
                MarkDirty(c);
            }
        }

        private static byte[] Pack(object state)
        {
            byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(state, SaveUtility.JsonSerializerSettings));
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress))
                    deflate.Write(json, 0, json.Length);
                return output.ToArray();
            }
        }

        private static T Unpack<T>(byte[] data)
        {
            using (var input = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress))
            using (var reader = new StreamReader(input, Encoding.UTF8))
                return JsonConvert.DeserializeObject<T>(reader.ReadToEnd(), SaveUtility.JsonSerializerSettings);
        }

        // ---------- Receiving ----------

        private void OnMessage(int peerId, MessageType type, NetDataReader reader)
        {
            if (type == MessageType.ContentsAdd)
            {
                if (_session.IsHost && Active)
                    OnAdds(reader);
                return;
            }
            if (type != MessageType.ContentsState)
                return;
            int count = reader.GetInt();
            for (int i = 0; i < count; i++)
            {
                int netId = reader.GetInt();
                byte kind = reader.GetByte();
                byte[] data = reader.GetBytesWithLength();
                int ackToken = reader.GetInt();
                int ackSeq = reader.GetInt();
                if (!_session.IsHost && WaitingForOwnPour(netId, ackToken, ackSeq))
                    continue;
                Apply(netId, kind, data);
            }
        }

        // Client: true while the host's contents for this pot don't include this copy's last pour yet.
        private bool WaitingForOwnPour(int netId, int ackToken, int ackSeq)
        {
            if (!_pendingAdd.TryGetValue(netId, out KeyValuePair<int, float> pending))
                return false;
            if ((ackToken == _token && ackSeq >= pending.Key) || _clock - pending.Value > PendingAddTimeout)
            {
                _pendingAdd.Remove(netId);
                // The host's contents include everything poured here; take them even if something else changed here.
                _changedLocallyAt.Remove(netId);
                return false;
            }
            return true;
        }

        private void Apply(int netId, byte kind, byte[] data)
        {
            if (!_items.TryGetItem(netId, out IMovable movable))
            {
                if (_warnedUnknown.Add(netId))
                    _log.LogWarning($"[Contents] Got contents for item {netId}, but this copy doesn't have it.");
                _items.RequestMissing(netId);
                return;
            }
            // The client keeps its own view of something it just poured into; the host's update is older.
            if (!_session.IsHost && RecentlyChangedHere(netId))
                return;

            _applying = true;
            try
            {
                if (kind == KindContainer && movable is Container c)
                    ApplyContainer(c, Unpack<ContainerContents>(data));
                else if (kind == KindPart && movable is ProductPart p)
                    ApplyPart(p, Unpack<PartSeasoning>(data));
                else if (kind == KindBottle && movable is InfiniteProduct bottle)
                    ApplyBottle(bottle, Unpack<BottleState>(data));
                _lastSignature[netId] = Signature(movable);
            }
            catch (Exception e)
            {
                if (_warnedBroken.Add(netId))
                    _log.LogWarning($"[Contents] Applying contents to item {netId} '{movable.gameObject.name}' failed: {e.Message}");
            }
            finally
            {
                _applying = false;
            }
        }

        private static void FixUp(List<ProductPartStats> list)
        {
            foreach (ProductPartStats pps in list)
                pps?.thermalGoodness?.InitAllAreas();
        }

        // Same as loading a save: replaces the list, then redraws the liquid (its level animates next frame).
        private static void ApplyContainer(Container c, ContainerContents state)
        {
            List<ProductPartStats> list = state?.V ?? new List<ProductPartStats>();
            FixUp(list);
            // The hover window keeps the liquid's stats object it opened with, so swapping in new objects froze its
            // heat colour until the player looked away. When the same liquids are still there, update them in place.
            if (SameProducts(c.virtualProductPartStats, list))
            {
                for (int i = 0; i < list.Count; i++)
                    CopyStats(list[i], c.virtualProductPartStats[i]);
                list = c.virtualProductPartStats;
            }
            c.ApplySaveData(list, state != null ? state.T : 0f);
            c.OnFluidQuantityChange?.Invoke();
            // Loading never empties a pot, so hide the liquid by hand when it's all gone.
            if (c.FluidsQuantity <= 0f && c.water != null)
                c.water.Active = false;
        }

        // Redraws a pot's liquid from its contents, the way loading a save does (Container.ApplySaveData).
        private static void RefreshLiquid(Container c)
        {
            c.ChangeWaterPhysicals();
            foreach (ProductPartStats pps in c.virtualProductPartStats)
            {
                if (pps != null)
                    c.ChangeWaterVisuals(pps);
            }
        }

        private static readonly FieldInfo[] StatsFields =
            typeof(ProductPartStats).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static bool SameProducts(List<ProductPartStats> current, List<ProductPartStats> incoming)
        {
            if (current == null || current.Count != incoming.Count)
                return false;
            for (int i = 0; i < current.Count; i++)
            {
#pragma warning disable CS0618 // The save data itself still uses productID.
                if (current[i] == null || incoming[i] == null || current[i].productID != incoming[i].productID)
                    return false;
#pragma warning restore CS0618
            }
            return true;
        }

        private static void CopyStats(ProductPartStats from, ProductPartStats to)
        {
            foreach (FieldInfo field in StatsFields)
                field.SetValue(to, field.GetValue(from));
        }

        private static void ApplyPart(ProductPart p, PartSeasoning state)
        {
            List<ProductPartStats> list = state?.V ?? new List<ProductPartStats>();
            FixUp(list);
            p.virtualProductPartStats = list;
            if (p.stats != null)
                p.stats.takenFlavours = state?.F ?? new Dictionary<int, float>();
        }

        // ---------- Harmony ----------

        private void PatchDirty(Type type, string method, Type[] args, string postfix = nameof(MarkDirtyPostfix)) =>
            Patch(type, method, args, postfix: postfix);

        private void Patch(Type type, string method, Type[] args, string prefix = null, string postfix = null)
        {
            try
            {
                MethodInfo original = AccessTools.Method(type, method, args);
                if (original == null)
                {
                    _log.LogError($"[Contents] Couldn't find {type.Name}.{method}; that part of contents syncing is off.");
                    return;
                }
                _harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(ContentsSync), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(ContentsSync), postfix));
            }
            catch (Exception e)
            {
                _log.LogError($"[Contents] Patching {type.Name}.{method} failed; that part of contents syncing is off: {e}");
            }
        }

        private static void MarkDirtyPostfix(object __instance)
        {
            try
            {
                if (__instance is IMovable m)
                    _instance?.MarkDirty(m);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Contents] patch error: " + e);
            }
        }

        // Client: a pour becomes an add for the host (QueueAdd), so it isn't also sent as whole contents.
        private static void AddPrefix(Container __instance, ProductPartStats newPPS, float pQuantity, out bool __state)
        {
            __state = false;
            try
            {
                __state = _instance != null && _instance.QueueAdd(__instance, newPPS, pQuantity);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Contents] patch error: " + e);
            }
        }

        private static void AddPostfix(Container __instance, bool __state)
        {
            try
            {
                if (!__state)
                    _instance?.MarkDirty(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Contents] patch error: " + e);
            }
        }

        private static void HelperPostfix(Container container)
        {
            try
            {
                _instance?.MarkDirty(container);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Contents] patch error: " + e);
            }
        }

        private static readonly FieldInfo VisualOnlyField = AccessTools.Field(typeof(VirtualProductTransporter), "isVisualsOnly");

        // Transporters this sync switched to show-only, so they can be switched back when this player pours.
        private static readonly HashSet<VirtualProductTransporter> ShowOnly = new HashSet<VirtualProductTransporter>();

        // Runs before the bottle starts its stream: in show-only mode the stream carries nothing into the pot and
        // doesn't empty the bottle. The pourer's copy does both, and sends the result.
        private static void StartPourPrefix(InfiniteProduct __instance)
        {
            try
            {
                VirtualProductTransporter transporter = __instance.virtualProductTransporter;
                if (transporter == null || VisualOnlyField == null)
                    return;
                bool followOnly = _instance != null && _instance.Active && _instance._items.IsRemoteHeld(__instance);
                if (followOnly)
                {
                    if (ShowOnly.Add(transporter))
                        transporter.SwitchModeToVisualOnly();
                }
                else if (ShowOnly.Remove(transporter))
                {
                    VisualOnlyField.SetValue(transporter, false);
                }
                ShowOnly.RemoveWhere(t => t == null);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Contents] patch error: " + e);
            }
        }

        private static readonly Dictionary<VirtualProductTransporter, bool> IsSinkStream = new Dictionary<VirtualProductTransporter, bool>();
        private static readonly MethodInfo ReportDeltaMethod = AccessTools.Method(typeof(Container), "ReportDelta", new[] { typeof(VirtualProductSphereTrigger) });

        private static readonly FieldInfo IsSinkField = AccessTools.Field(typeof(VirtualProductTransporter), "isSink");
        private static readonly FieldInfo CreateRoutineField = AccessTools.Field(typeof(VirtualProductTransporter), "createCorutine");

        // v0.11.8: v0.11.7 only looked for a SinkTap above the stream, and the sink's stream isn't always under
        // it, so the client's sink water still went into the pot (and on to the host). Now the taps themselves say
        // which stream is theirs.
        private static bool IsSink(VirtualProductTransporter transporter)
        {
            if (IsSinkStream.TryGetValue(transporter, out bool sink))
                return sink;
            sink = transporter.GetComponentInParent<SinkTap>() != null
                   || (IsSinkField != null && IsSinkField.GetValue(transporter) is bool flag && flag);
            if (!sink)
            {
                foreach (SinkTap tap in UnityEngine.Object.FindObjectsOfType<SinkTap>())
                {
                    if (tap != null && tap.fluid == transporter)
                    {
                        sink = true;
                        break;
                    }
                }
            }
            IsSinkStream[transporter] = sink;
            if (sink)
                _instance?._log.LogInfo($"[Contents] Found the sink's water stream '{transporter.name}'." +
                    (_instance._session.IsHost ? "" : " Here it only shows the pouring tooltip; the host's sink fills the pot."));
            return sink;
        }

        // A sink stream that's still running when the tap turns on again keeps running; don't start a second one.
        private static void SetParticlePrefix(VirtualProductTransporter __instance)
        {
            try
            {
                if (_instance == null || !_instance.Active || CreateRoutineField == null || !IsSink(__instance))
                    return;
                if (CreateRoutineField.GetValue(__instance) is Coroutine running && running != null)
                {
                    __instance.StopCoroutine(running);
                    CreateRoutineField.SetValue(__instance, null);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Contents] patch error: " + e);
            }
        }

        // Returns false to skip the hit: a client's sink water shows the tooltip but doesn't go into the pot.
        private static bool SphereHitPrefix(Container __instance, VirtualProductSphereTrigger sphere)
        {
            try
            {
                if (_instance == null || !_instance.Active || _instance._session.IsHost || ReportDeltaMethod == null)
                    return true;
                if (sphere == null || sphere.virtualProducts.Count <= 0 || sphere.Owner == null)
                    return true;
                VirtualProductTransporter transporter = sphere.Owner.ownerTransporter;
                if (transporter == null)
                    return true;
                if (!IsSink(transporter))
                    return true;
                // What the game does first on a hit: count the sphere as caught, and show the tooltip.
                transporter.spheresRemovedAtContainer += sphere.quantity;
                ReportDeltaMethod.Invoke(__instance, new object[] { sphere });
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Contents] patch error: " + e);
                return true;
            }
        }

        private static void WaterSurfacePrefix(WaterSurface __instance)
        {
            try
            {
                if (_instance == null || !_instance.Active || !(__instance.ownerContainer is IMovable owner))
                    return;
                if (!_instance._items.IsRemoteHeld(owner))
                    return;
                MaxLeakField?.SetValue(__instance, 0f);
                MaxLeakInsideField?.SetValue(__instance, 0f);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Contents] patch error: " + e);
            }
        }
    }
}
