using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using WIUtils;
using Object = UnityEngine.Object;

namespace CookingSimMP
{
    // Keeps dirt and cleaning on surfaces (counters, floor, stoves, plates) the same on both copies.
    //
    // How the game does it: every paintable surface has a PaintData and a stain mask texture. Spills, splashes, egg
    // and melting stains paint into that mask with a "Draw" brush; the sponge and mop paint over it with a "Clear"
    // brush. All of these go through PaintManager.Paint(PaintInfoData), one call per stroke, with the raycast hit
    // that says where on the surface it lands.
    //
    // Each stroke is sent as (where it hit, which direction, which brush, colour). The other copy casts a short ray
    // at the same spot and paints the same stroke through the same method, so the mask changes the same way.
    //
    // Stains from things that happen on both copies anyway (an egg breaking, butter melting) get painted twice,
    // once locally and once from the other copy. They land in nearly the same spot, so that only darkens the stain
    // a little; cleaning it once still clears it on both.
    internal sealed class CleaningSync
    {
        private const float SendInterval = 0.05f;
        private const int MaxStrokesPerMessage = 200;

        private static CleaningSync _instance;

        // True while painting the other player's stroke, so it isn't sent straight back.
        private static bool _replaying;

        private readonly NetSession _session;
        private readonly ItemSync _items;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".cleaning");

        private readonly List<Stroke> _pending = new List<Stroke>();
        private readonly Dictionary<string, BrushData> _brushes = new Dictionary<string, BrushData>();
        private readonly HashSet<string> _warnedBrush = new HashSet<string>();
        private float _sendTimer;

        private struct Stroke
        {
            public Vector3 Point;
            public Vector3 Direction;
            public int Layer;
            public string Brush;
            public Color BrushColor;
            public bool HasColor;
            public Color Color;
            public bool HasSpecificId;
            public int SpecificId;
        }

        public CleaningSync(NetSession session, ItemSync items, ManualLogSource log)
        {
            _instance = this;
            _session = session;
            _items = items;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.PeerLeft += _ => Reset();
            _session.Stopped += Reset;

            try
            {
                MethodInfo paint = AccessTools.Method(typeof(PaintManager), "Paint", new[] { typeof(PaintInfoData) });
                _harmony.Patch(paint, prefix: new HarmonyMethod(typeof(CleaningSync), nameof(PaintPrefix)));
            }
            catch (Exception e)
            {
                _log.LogError($"[Clean] Patching PaintManager.Paint failed; dirt and cleaning won't sync: {e}");
            }
        }

        public static bool Handles(MessageType type) => type == MessageType.PaintStrokes;

        private bool Active => _session.IsConnected && _items.HasTable;

        private void Reset()
        {
            _pending.Clear();
            _brushes.Clear();
            _warnedBrush.Clear();
        }

        // Runtime copies of a brush (a spill tinted to the liquid's colour) are named "<asset>(Clone)".
        private static string BrushName(BrushData brush)
        {
            string name = brush.name ?? string.Empty;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return clone >= 0 ? name.Substring(0, clone).TrimEnd() : name;
        }

        // ---------- Local stroke: queue it ----------

        private void OnLocalPaint(PaintInfoData info)
        {
            if (_replaying || !Active || info == null || info.BrushData == null)
                return;
            RaycastHit hit = info.RaycastHit;
            if (hit.collider == null || !(hit.collider is MeshCollider))
                return;
            PaintData target = hit.collider.GetComponent<PaintData>();
            if (target == null || !target.IsPaintingEnabled)
                return;

            Vector3 direction = info.Direction.sqrMagnitude > 1e-8f ? info.Direction.normalized : -hit.normal;
            _pending.Add(new Stroke
            {
                Point = hit.point,
                Direction = direction,
                Layer = hit.collider.gameObject.layer,
                Brush = BrushName(info.BrushData),
                BrushColor = info.BrushData.BrushColor,
                HasColor = info.Color.HasValue,
                Color = info.Color ?? Color.white,
                HasSpecificId = info.SpecificIdFeasibleToPaint.HasValue,
                SpecificId = info.SpecificIdFeasibleToPaint ?? 0,
            });
        }

        public void Tick(float deltaTime)
        {
            if (!Active)
            {
                _pending.Clear();
                return;
            }
            _sendTimer += deltaTime;
            if (_sendTimer < SendInterval || _pending.Count == 0)
                return;
            _sendTimer = 0f;

            for (int start = 0; start < _pending.Count; start += MaxStrokesPerMessage)
            {
                int count = Math.Min(MaxStrokesPerMessage, _pending.Count - start);
                NetDataWriter writer = _session.BeginMessage(MessageType.PaintStrokes);
                writer.Put(count);
                for (int i = start; i < start + count; i++)
                    WriteStroke(writer, _pending[i]);
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
            }
            _pending.Clear();
        }

        private static void WriteStroke(NetDataWriter w, Stroke s)
        {
            w.Put(s.Point.x);
            w.Put(s.Point.y);
            w.Put(s.Point.z);
            w.Put(s.Direction.x);
            w.Put(s.Direction.y);
            w.Put(s.Direction.z);
            w.Put((byte)s.Layer);
            w.Put(s.Brush);
            PutColor(w, s.BrushColor);
            w.Put(s.HasColor);
            if (s.HasColor)
                PutColor(w, s.Color);
            w.Put(s.HasSpecificId);
            if (s.HasSpecificId)
                w.Put(s.SpecificId);
        }

        private static void PutColor(NetDataWriter w, Color c)
        {
            w.Put(c.r);
            w.Put(c.g);
            w.Put(c.b);
            w.Put(c.a);
        }

        private static Color GetColor(NetDataReader r) => new Color(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());

        // ---------- Other player's stroke: paint it here ----------

        private void OnMessage(int peerId, MessageType type, NetPacketReader reader)
        {
            if (type != MessageType.PaintStrokes)
                return;
            int count = reader.GetInt();
            for (int i = 0; i < count; i++)
            {
                var s = new Stroke
                {
                    Point = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat()),
                    Direction = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat()),
                    Layer = reader.GetByte(),
                    Brush = reader.GetString(256),
                    BrushColor = GetColor(reader),
                };
                s.HasColor = reader.GetBool();
                if (s.HasColor)
                    s.Color = GetColor(reader);
                s.HasSpecificId = reader.GetBool();
                if (s.HasSpecificId)
                    s.SpecificId = reader.GetInt();
                if (Active)
                    Replay(s);
            }
        }

        private void Replay(Stroke s)
        {
            BrushData asset = FindBrush(s.Brush);
            if (asset == null)
                return;
            // Cast from just above the surface so the ray finds the same spot (and the same UV) as on the other copy.
            if (!Physics.Raycast(s.Point - s.Direction * 0.05f, s.Direction, out RaycastHit hit, 0.1f, 1 << s.Layer, QueryTriggerInteraction.Ignore))
                return;

            // A spill tinted to the liquid's colour is a runtime copy of the brush asset with another colour.
            BrushData brush = asset;
            if (asset.BrushColor != s.BrushColor)
            {
                brush = Object.Instantiate(asset);
                brush.BrushColor = s.BrushColor;
            }
            _replaying = true;
            try
            {
                var info = new PaintInfoData(hit, brush, s.Point - s.Direction * 0.05f, s.Direction,
                    s.HasColor ? s.Color : (Color?)null, s.HasSpecificId ? s.SpecificId : (int?)null);
                Singleton<PaintManager>.Me.Paint(info);
            }
            catch (Exception e)
            {
                if (_warnedBrush.Add("paint:" + s.Brush))
                    _log.LogWarning($"[Clean] Painting the other player's '{s.Brush}' stroke failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
                if (brush != asset)
                    Object.Destroy(brush);
            }
        }

        private BrushData FindBrush(string name)
        {
            if (_brushes.TryGetValue(name, out BrushData brush) && brush != null)
                return brush;
            foreach (BrushData b in Resources.FindObjectsOfTypeAll<BrushData>())
            {
                if (b != null && b.name == name)
                {
                    _brushes[name] = b;
                    return b;
                }
            }
            if (_warnedBrush.Add(name))
                _log.LogWarning($"[Clean] The other player painted with brush '{name}', which this copy hasn't loaded; skipping those strokes.");
            return null;
        }

        // ---------- Harmony ----------

        private static void PaintPrefix(PaintInfoData paintInfoData)
        {
            try
            {
                _instance?.OnLocalPaint(paintInfoData);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Clean] patch error: " + e);
            }
        }
    }
}
