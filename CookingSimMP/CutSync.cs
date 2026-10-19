using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CookingSimMP
{
    // Cutting food on one copy cuts it the same way on the other.
    //
    // Every cut in the game (knife on a board, both automatic cutters, guests cutting food as they eat) ends up in
    // ProductPart.KnifeCut(plane). It destroys the piece and returns the new pieces. The plane is given in the
    // piece's own local space, so the other copy can repeat the exact same cut on its copy of the piece and get
    // identical pieces, as long as both copies of the piece have the same mesh, which they do when every earlier
    // cut was repeated too (or the piece was loaded from the same save).
    //
    // The copy that cut sends (piece ID, plane, new IDs). The other copy repeats the cut and gives the new pieces
    // the same IDs, in the order KnifeCut returned them. Then the cutting copy sends where the pieces end up
    // (ItemSync, as if a player had just let go of each), so both copies agree on their positions.
    //
    // The automatic cutters run when their button is pressed, and InteractableSync replays that press on the other
    // copy. There the cutter must not cut on its own, or both copies would cut with their own random planes and
    // their pieces wouldn't match. So a replayed press doesn't start the cutter; the presser's cuts arrive instead.
    internal sealed class CutSync
    {
        private static CutSync _instance;

        // True while repeating the other player's cut, so it isn't sent straight back.
        private static bool _replaying;

        // Inside a guest's meal: guests eat on each copy separately, cutting their food as they go, so those
        // cuts stay local.
        private static int _eatingDepth;

        private readonly NetSession _session;
        private readonly ItemSync _items;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".cutting");
        private readonly HashSet<int> _warnedUnknown = new HashSet<int>();
        private readonly HashSet<Object> _warnedNoId = new HashSet<Object>();

        public CutSync(NetSession session, ItemSync items, ManualLogSource log)
        {
            _instance = this;
            _session = session;
            _items = items;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.AllPeersLeft += Reset;   // v0.12.2: not when one of several clients leaves.
            _session.Stopped += Reset;
            _session.WorldReset += Reset;

            Patch(typeof(ProductPart), "KnifeCut", new[] { typeof(Vector4) }, postfix: nameof(KnifeCutPostfix));
            Patch(AccessTools.TypeByName("Stands.AutomaticCutter"), "OnButtonPushed", Type.EmptyTypes, prefix: nameof(CutterPrefix));
            Patch(typeof(AutomaticCutterStand), "Cut", Type.EmptyTypes, prefix: nameof(CutterPrefix));
            Patch(AccessTools.TypeByName("Consumption.ContainerContentConsumptionController"), "SliceSingleProduct", new[] { typeof(ProductPart) },
                prefix: nameof(EatingPrefix), finalizer: nameof(EatingFinalizer));
        }

        public static bool Handles(MessageType type) => type == MessageType.CutDone;

        private void Reset()
        {
            _warnedUnknown.Clear();
            _warnedNoId.Clear();
        }

        private bool Active => _session.IsConnected && _items.HasTable;

        // ---------- Local cut: tell the other copy ----------

        private void OnLocalCut(ProductPart source, Vector4 plane, List<ProductPart> result)
        {
            if (_replaying || _eatingDepth > 0 || !Active || result == null)
                return;
            var pieces = new List<ProductPart>();
            foreach (ProductPart p in result)
            {
                if (p != null)
                    pieces.Add(p);
            }
            // Fewer than two pieces means the knife missed; KnifeCut then leaves the item alone.
            if (pieces.Count < 2)
                return;
            if (!_items.TryGetNetId(source, out int sourceId))
            {
                if (_warnedNoId.Add(source))
                    _log.LogWarning($"[Cut] Cut '{source.name}', which has no network ID, so the other player won't see this cut.");
                return;
            }

            NetDataWriter writer = _session.BeginMessage(MessageType.CutDone);
            writer.Put(sourceId);
            writer.Put(plane.x);
            writer.Put(plane.y);
            writer.Put(plane.z);
            writer.Put(plane.w);
            writer.Put(pieces.Count);
            var ids = new int[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                ids[i] = _items.NewLocalId();
                writer.Put(ids[i]);
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);

            _items.DropItem(sourceId);
            for (int i = 0; i < pieces.Count; i++)
                _items.RegisterItem(ids[i], pieces[i]);
            // Sent after the cut message, so the other copy already knows the new IDs.
            for (int i = 0; i < pieces.Count; i++)
                _items.SendUntilRest(ids[i], pieces[i]);
        }

        // ---------- Other player's cut: repeat it ----------

        private void OnMessage(int peerId, MessageType type, NetDataReader reader)
        {
            if (type != MessageType.CutDone)
                return;
            int sourceId = reader.GetInt();
            var plane = new Vector4(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            int count = reader.GetInt();
            var ids = new int[count];
            for (int i = 0; i < count; i++)
                ids[i] = reader.GetInt();

            if (!_items.TryGetItem(sourceId, out IMovable movable) || !(movable is ProductPart part))
            {
                if (_warnedUnknown.Add(sourceId))
                    _log.LogWarning($"[Cut] Other player cut item {sourceId}, which this copy doesn't have; its pieces won't sync.");
                return;
            }

            _items.DropItem(sourceId);
            List<ProductPart> result;
            _replaying = true;
            try
            {
                result = part.KnifeCut(plane);
            }
            catch (Exception e)
            {
                _log.LogError($"[Cut] Repeating the other player's cut of item {sourceId} '{part.name}' threw: {e}");
                return;
            }
            finally
            {
                _replaying = false;
            }

            var pieces = new List<ProductPart>();
            if (result != null)
            {
                foreach (ProductPart p in result)
                {
                    if (p != null)
                        pieces.Add(p);
                }
            }
            if (pieces.Count != count)
                _log.LogWarning($"[Cut] Other player's cut of '{part.name}' made {count} pieces there but {pieces.Count} here.");
            for (int i = 0; i < Math.Min(count, pieces.Count); i++)
                _items.RegisterItem(ids[i], pieces[i]);
        }

        // ---------- Harmony ----------

        private void Patch(Type type, string method, Type[] args, string prefix = null, string postfix = null, string finalizer = null)
        {
            try
            {
                MethodInfo original = type != null ? AccessTools.Method(type, method, args) : null;
                if (original == null)
                {
                    _log.LogError($"[Cut] Couldn't find {type?.Name ?? "a type"}.{method}; that part of cut syncing is off.");
                    return;
                }
                _harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(CutSync), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(CutSync), postfix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(CutSync), finalizer));
            }
            catch (Exception e)
            {
                _log.LogError($"[Cut] Patching {type?.Name}.{method} failed; that part of cut syncing is off: {e}");
            }
        }

        private static void KnifeCutPostfix(ProductPart __instance, Vector4 planeInLocalSpace, List<ProductPart> __result)
        {
            try
            {
                _instance?.OnLocalCut(__instance, planeInLocalSpace, __result);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Cut] patch error: " + e);
            }
        }

        private static void EatingPrefix() => _eatingDepth++;

        private static Exception EatingFinalizer(Exception __exception)
        {
            _eatingDepth--;
            return __exception;
        }

        // A cutter button pressed by the other player only moves the button here; their copy does the cutting.
        private static bool CutterPrefix() => !(InteractableSync.Applying && _instance != null && _instance.Active);
    }
}
