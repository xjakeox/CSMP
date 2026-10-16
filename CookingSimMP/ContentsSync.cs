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
    // Both players pouring into the same pot at the same moment: the last update wins (TODO #4).
    internal sealed class ContentsSync
    {
        private const float DirtySendInterval = 0.1f;   // Local pours and seasoning go out at most 10 times a second.
        private const float DriftInterval = 0.5f;       // Host checks for cooking drift twice a second.
        private const float KeepLocalFor = 1f;          // Client ignores the host's older view of an item it just changed.
        private const float ChangeEpsilon = 0.01f;

        private const byte KindContainer = 1;
        private const byte KindPart = 2;

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
            // out, emptying, tasting, and the helpers that throw contents away.
            PatchDirty(typeof(Container), "AddVirtualProductToContainer", new[] { typeof(ProductPartStats), typeof(float) });
            PatchDirty(typeof(Container), "OnFluidLeak", new[] { typeof(float), typeof(WaterSurfaceLeakController) });
            PatchDirty(typeof(Container), "ClearContaierFromFluid", Type.EmptyTypes);
            PatchDirty(typeof(Container), "TasteContent", Type.EmptyTypes);
            PatchDirty(typeof(ContainerHelpers), "ClearAllContains", new[] { typeof(Container), typeof(SpawnPoint) }, nameof(HelperPostfix));
            PatchDirty(typeof(ContainerHelpers), "ClearUnmovableContains", new[] { typeof(Container) }, nameof(HelperPostfix));
            // Seasoning landing on food.
            PatchDirty(typeof(ProductPart), "AddQuantityToProduct", new[] { typeof(ProductPartStats), typeof(ProductPartStats), typeof(float) });
            // A pan the other player is tilting spills on their copy; here it only follows, so it mustn't spill too.
            Patch(typeof(WaterSurface), "LateUpdate", Type.EmptyTypes, prefix: nameof(WaterSurfacePrefix));
            if (MaxLeakField == null || MaxLeakInsideField == null)
                _log.LogWarning("[Contents] Couldn't find WaterSurface's leak fields; a pan the other player tilts may spill twice.");
        }

        public static bool Handles(MessageType type) => type == MessageType.ContentsState;

        private bool Active => _session.IsConnected && _items.HasTable;

        private void Reset()
        {
            _dirty.Clear();
            _lastSignature.Clear();
            _changedLocallyAt.Clear();
            _warnedUnknown.Clear();
            _warnedBroken.Clear();
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
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
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

        private void OnMessage(int peerId, MessageType type, NetPacketReader reader)
        {
            if (type != MessageType.ContentsState)
                return;
            int count = reader.GetInt();
            for (int i = 0; i < count; i++)
            {
                int netId = reader.GetInt();
                byte kind = reader.GetByte();
                byte[] data = reader.GetBytesWithLength();
                Apply(netId, kind, data);
            }
        }

        private void Apply(int netId, byte kind, byte[] data)
        {
            if (!_items.TryGetItem(netId, out IMovable movable))
            {
                if (_warnedUnknown.Add(netId))
                    _log.LogWarning($"[Contents] Got contents for item {netId}, but this copy doesn't have it.");
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
