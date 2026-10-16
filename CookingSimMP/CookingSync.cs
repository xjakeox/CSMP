using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CookingSimMP
{
    // Keeps cooking progress (doneness) of food identical on both copies, with the host in charge.
    //
    // Both copies still run the game's own cooking, since the stoves, ovens and food positions are synced.
    // That keeps the client's visuals smooth, but the two simulations drift apart over time (pausing one copy,
    // frame rate, food landing on the heat a moment earlier). So the host sends each food item's thermal state
    // several times a second and the client overwrites its own with it.
    //
    // Where doneness lives: ProductPart.stats.thermalGoodness has one ThermalGoodnessArea per heat-sensing
    // patch of the food (ThermalAreaChecker). Each area holds a point value and a "threshold met" flag per
    // cooking type (boiling, frying, baking, microwave, deep frying, grilled) and a visual value that drives
    // the browning. Those private fields are copied as-is; the game's setters would reset the threshold flags.
    //
    // Items are identified with ItemSync's network IDs. Cut pieces get theirs from CutSync.
    internal sealed class CookingSync
    {
        private const float SendInterval = 0.25f;     // Host sends changed items 4 times a second.
        private const float ChangeEpsilon = 0.001f;

        private readonly NetSession _session;
        private readonly ItemSync _items;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".cooking");

        private static readonly FieldInfo PointValuesField = AccessTools.Field(typeof(ThermalGoodness.ThermalGoodnessArea), "pointValues");
        private static readonly FieldInfo ThresholdMetField = AccessTools.Field(typeof(ThermalGoodness.ThermalGoodnessArea), "thresholdMet");
        private static readonly FieldInfo VisualValueField = AccessTools.Field(typeof(ThermalGoodness.ThermalGoodnessArea), "visualValue");
        private static readonly MethodInfo BlendTexturesMethod = AccessTools.Method(typeof(BlendablePart), "BlendTextures");

        // Test switch (F9 on the client): stop the client's own cooking so any doneness on the client
        // can only have come from the host.
        internal static bool ClientCookingBlocked;
        internal static bool IsClientForBlock;

        private float _sendTimer;
        private bool _sendAll;   // Next send includes every food item, not only the changed ones.
        private readonly Dictionary<int, float> _lastSentSignature = new Dictionary<int, float>();
        private readonly HashSet<int> _warnedAreaMismatch = new HashSet<int>();
        private readonly HashSet<int> _warnedUnknown = new HashSet<int>();
        private readonly HashSet<Object> _warnedNoId = new HashSet<Object>();

        public CookingSync(NetSession session, ItemSync items, ManualLogSource log)
        {
            _session = session;
            _items = items;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.PeerLeft += _ => Reset();
            _session.Stopped += Reset;
            _session.WorldReset += Reset;

            if (PointValuesField == null || ThresholdMetField == null || VisualValueField == null)
                _log.LogError("[Cook] Couldn't find ThermalGoodnessArea's fields; cooking sync can't work.");
            if (BlendTexturesMethod == null)
                _log.LogWarning("[Cook] Couldn't find BlendablePart.BlendTextures; synced doneness won't recolour food until the game does.");

            try
            {
                MethodInfo gainHeat = AccessTools.Method(typeof(ProductPart), "GainHeat", new[] { typeof(float), typeof(GoodnesType), typeof(bool) });
                _harmony.Patch(gainHeat, prefix: new HarmonyMethod(typeof(CookingSync), nameof(GainHeatPrefix)));
            }
            catch (Exception e)
            {
                _log.LogError($"[Cook] Patching ProductPart.GainHeat failed; the F9 test switch won't work: {e}");
            }
        }

        public static bool Handles(MessageType type) => type == MessageType.CookState;

        // Host: resend every item's doneness on the next tick (after a pause, for example).
        public void SendAllOnNextTick() => _sendAll = true;

        private void Reset()
        {
            _lastSentSignature.Clear();
            _warnedAreaMismatch.Clear();
            _warnedUnknown.Clear();
            _warnedNoId.Clear();
            if (ClientCookingBlocked)
                _log.LogInfo("[Cook] Disconnected; client's own cooking is running again.");
            ClientCookingBlocked = false;
        }

        public void Tick(float deltaTime)
        {
            IsClientForBlock = _session.IsConnected && !_session.IsHost;

            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (!_session.IsConnected || _session.IsHost)
                {
                    _log.LogInfo("[Cook] F9 only works on the connected client: it stops the client's own cooking so only the host's updates cook its food.");
                }
                else
                {
                    ClientCookingBlocked = !ClientCookingBlocked;
                    _log.LogInfo(ClientCookingBlocked
                        ? "[Cook] F9: client's own cooking is OFF. Food on this copy now only cooks from the host's updates."
                        : "[Cook] F9: client's own cooking is ON again (host still corrects it).");
                }
            }

            if (!_session.IsConnected || !_session.IsHost || !_items.HasTable)
                return;
            _sendTimer += deltaTime;
            if (_sendTimer < SendInterval)
                return;
            _sendTimer = 0f;
            SendChangedItems();
            WarnAboutUnsyncedHeating();
        }

        // ---------- Reading and writing thermal state ----------

        private static Dictionary<GoodnesType, float> PointValues(ThermalGoodness.ThermalGoodnessArea a) =>
            (Dictionary<GoodnesType, float>)PointValuesField.GetValue(a);

        private static Dictionary<GoodnesType, bool> ThresholdMet(ThermalGoodness.ThermalGoodnessArea a) =>
            (Dictionary<GoodnesType, bool>)ThresholdMetField.GetValue(a);

        private static float RawVisual(ThermalGoodness.ThermalGoodnessArea a) => (float)VisualValueField.GetValue(a);

        // One number that changes whenever anything worth sending changes.
        private static float Signature(ProductPart part)
        {
            ThermalGoodness tg = part.stats.thermalGoodness;
            float sum = part.stats.temperature + tg.Areas.Count * 1000f;
            foreach (ThermalGoodness.ThermalGoodnessArea area in tg.Areas)
            {
                sum += RawVisual(area) * 3f + (area.VisualThresholdMet ? 7f : 0f);
                foreach (KeyValuePair<GoodnesType, float> pair in PointValues(area))
                    sum += pair.Value * (2f + (int)pair.Key);
                foreach (KeyValuePair<GoodnesType, bool> pair in ThresholdMet(area))
                    sum += pair.Value ? 11f * (1 + (int)pair.Key) : 0f;
            }
            return sum;
        }

        private static bool HasThermal(ProductPart part) =>
            part != null && part.stats != null && part.stats.thermalGoodness != null;

        private static string Describe(int netId, ProductPart part) => $"item {netId} '{part.name}'";

        // ---------- Host: sending ----------

        private void SendChangedItems()
        {
            var changed = new List<KeyValuePair<int, ProductPart>>();
            foreach (KeyValuePair<int, IMovable> pair in _items.AllItems.ToList())
            {
                var part = pair.Value as ProductPart;
                if (part == null || !HasThermal(part))
                    continue;
                float signature = Signature(part);
                bool known = _lastSentSignature.TryGetValue(pair.Key, out float last);
                if (known && !_sendAll && Mathf.Abs(signature - last) < ChangeEpsilon)
                    continue;
                _lastSentSignature[pair.Key] = signature;
                // The first look at an item only records it; it was loaded from the same save on both copies.
                if (!known)
                    continue;
                changed.Add(new KeyValuePair<int, ProductPart>(pair.Key, part));
            }
            _sendAll = false;
            if (changed.Count == 0)
                return;

            NetDataWriter writer = _session.BeginMessage(MessageType.CookState);
            writer.Put(changed.Count);
            foreach (KeyValuePair<int, ProductPart> pair in changed)
                WriteItem(writer, pair.Key, pair.Value);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        private static void WriteItem(NetDataWriter writer, int netId, ProductPart part)
        {
            ThermalGoodness tg = part.stats.thermalGoodness;
            writer.Put(netId);
            writer.Put(part.stats.temperature);
            writer.Put(tg.Areas.Count);
            foreach (ThermalGoodness.ThermalGoodnessArea area in tg.Areas)
            {
                writer.Put(area.areaCheckerId);
                writer.Put(RawVisual(area));
                writer.Put(area.VisualThresholdMet);
                Dictionary<GoodnesType, float> points = PointValues(area);
                Dictionary<GoodnesType, bool> met = ThresholdMet(area);
                writer.Put(points.Count);
                foreach (KeyValuePair<GoodnesType, float> pair in points)
                {
                    writer.Put((byte)pair.Key);
                    writer.Put(pair.Value);
                    writer.Put(met.TryGetValue(pair.Key, out bool m) && m);
                }
            }
        }

        // Food being heated on the host that has no network ID can't be synced.
        private void WarnAboutUnsyncedHeating()
        {
            foreach (ProductPart part in Object.FindObjectsOfType<ProductPart>())
            {
                if (_warnedNoId.Contains(part) || _items.TryGetNetId(part, out _))
                    continue;
                if (part.PhysicsController != null && part.PhysicsController.HeatTaken(out _, out _))
                {
                    _warnedNoId.Add(part);
                    _log.LogWarning($"[Cook] '{part.name}' is cooking on the host but has no network ID (e.g. a piece whose cut the other copy couldn't repeat), so its doneness isn't synced.");
                }
            }
        }

        // ---------- Client: receiving ----------

        private sealed class AreaState
        {
            public int CheckerId;
            public float Visual;
            public bool VisualMet;
            public List<KeyValuePair<GoodnesType, float>> Points = new List<KeyValuePair<GoodnesType, float>>();
            public Dictionary<GoodnesType, bool> Met = new Dictionary<GoodnesType, bool>();
        }

        private void OnMessage(int peerId, MessageType type, NetPacketReader reader)
        {
            if (type != MessageType.CookState)
                return;
            int count = reader.GetInt();
            for (int i = 0; i < count; i++)
            {
                int netId = reader.GetInt();
                float temperature = reader.GetFloat();
                int areaCount = reader.GetInt();
                var areas = new List<AreaState>(areaCount);
                for (int a = 0; a < areaCount; a++)
                {
                    var state = new AreaState { CheckerId = reader.GetInt(), Visual = reader.GetFloat(), VisualMet = reader.GetBool() };
                    int typeCount = reader.GetInt();
                    for (int t = 0; t < typeCount; t++)
                    {
                        var goodness = (GoodnesType)reader.GetByte();
                        state.Points.Add(new KeyValuePair<GoodnesType, float>(goodness, reader.GetFloat()));
                        state.Met[goodness] = reader.GetBool();
                    }
                    areas.Add(state);
                }
                ApplyItem(netId, temperature, areas);
            }
        }

        private void ApplyItem(int netId, float temperature, List<AreaState> hostAreas)
        {
            if (!_items.TryGetItem(netId, out IMovable movable) || !(movable is ProductPart part) || !HasThermal(part))
            {
                if (_warnedUnknown.Add(netId))
                    _log.LogWarning($"[Cook] Got doneness for item {netId}, but this copy has no food item with that ID.");
                return;
            }

            ThermalGoodness tg = part.stats.thermalGoodness;

            if (tg.Areas.Count != hostAreas.Count && _warnedAreaMismatch.Add(netId))
                _log.LogWarning($"[Cook] {Describe(netId, part)} has {tg.Areas.Count} heat area(s) here but {hostAreas.Count} on the host; matching by position in the list.");

            part.stats.temperature = temperature;
            int n = Math.Min(tg.Areas.Count, hostAreas.Count);
            for (int i = 0; i < n; i++)
            {
                ThermalGoodness.ThermalGoodnessArea area = tg.Areas[i];
                AreaState host = hostAreas[i];
                VisualValueField.SetValue(area, host.Visual);
                area.VisualThresholdMet = host.VisualMet;
                Dictionary<GoodnesType, float> points = PointValues(area);
                Dictionary<GoodnesType, bool> met = ThresholdMet(area);
                foreach (KeyValuePair<GoodnesType, float> pair in host.Points)
                {
                    points[pair.Key] = pair.Value;
                    met[pair.Key] = host.Met[pair.Key];
                }
            }

            // Redraw the browning the same way the game does after heating (ProductPart.GainHeat).
            try
            {
#pragma warning disable CS0618 // The game itself still uses this to scale food after heating.
                part.TransformScale(tg.AverageVisualValue);
#pragma warning restore CS0618
                BlendTexturesMethod?.Invoke(part, null);
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Cook] Redrawing {Describe(netId, part)} failed: {e.Message}");
            }

        }

        // ---------- F9 test switch ----------

        public static bool GainHeatPrefix() => !(ClientCookingBlocked && IsClientForBlock);
    }
}
