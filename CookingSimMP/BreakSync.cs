using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CookingSimMP
{
    // Breaking: plates, glasses, bottles and anything else that shatters (MovablePhysics.Break). The copy where it
    // breaks tells the other one, which moves the item to the same spot and breaks it the same way, so the shards,
    // the sound and the food that falls off happen on both screens. An item the other player is holding or throwing
    // only breaks when their copy says so; this copy just follows it.
    //
    // Broken pieces aren't in the game's save, so a client loading the host's world (joining, or RESYNC) has none.
    // Each copy remembers which item every piece came from; after the item table the host sends where its pieces
    // are, and the client rebuilds them from a fresh copy of the same item (v0.10.1).
    internal sealed class BreakSync
    {
        internal static BreakSync Instance;

        // True while breaking an item because the other player's copy did, so it isn't sent back.
        internal static bool Applying;

        // Break's arguments, kept by the prefix for the MovableBroken event that fires inside it.
        private static string _pendingSound;
        private static float _pendingStrength;

        private readonly NetSession _session;
        private readonly ItemSync _items;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".breaks");

        // Where each broken piece came from: which item, which break, and which piece of that item's shards.
        private struct PieceOrigin
        {
            public ItemSync.SpawnKind Kind;
            public int AssetId;
            public int BreakNo;
            public int Index;
        }

        private static readonly Dictionary<FracturedDummyMovable, PieceOrigin> Origins = new Dictionary<FracturedDummyMovable, PieceOrigin>();

        // v0.11.7: pieces of a break during play had no IDs, so picking one up wasn't sent (the other screen's piece
        // stayed put). Both copies break the same item into the same pieces in the same order, so each piece now
        // gets an ID made from the broken item's ID and its place in that order, the same on both copies.
        private const int PieceIdBase = 100_000_000;
        private const int MaxPiecesPerItem = 64;
        private static readonly Dictionary<IMovable, int> Breaking = new Dictionary<IMovable, int>();
        // Item IDs up to about 32 million fit before a piece ID overflows; pieces themselves (100,000,000 and up) don't.
        private const int MaxItemIdWithPieces = (int.MaxValue - PieceIdBase) / MaxPiecesPerItem;
        internal static int PieceId(int itemId, int index) => PieceIdBase + itemId * MaxPiecesPerItem + index;
        private static int _breakNo;

        public BreakSync(NetSession session, ItemSync items, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _items = items;
            _log = log;
            _session.MessageReceived += OnMessage;
            MovablePhysics.MovableBroken += OnLocalBroken;

            try
            {
                _harmony.Patch(AccessTools.Method(typeof(MovablePhysics), nameof(MovablePhysics.Break)),
                    prefix: new HarmonyMethod(typeof(BreakPatches), nameof(BreakPatches.BreakPrefix)));
            }
            catch (Exception e)
            {
                _log.LogError($"[Breaks] Patching MovablePhysics.Break failed; breaking isn't synced: {e}");
            }
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(FracturedObjectsService), nameof(FracturedObjectsService.SetFracturedObject)),
                    prefix: new HarmonyMethod(typeof(BreakPatches), nameof(BreakPatches.ShatterPrefix)));
            }
            catch (Exception e)
            {
                _log.LogError($"[Breaks] Patching FracturedObjectsService.SetFracturedObject failed; broken pieces won't be rebuilt after RESYNC: {e}");
            }
        }

        public static bool Handles(MessageType type) => type == MessageType.ItemBroken || type == MessageType.PieceList;

        // ---------- Broken pieces ----------

        // An item is about to shatter into its pieces (still children of its shards object, in a fixed order).
        internal static void RememberPieces(GameObject fracturedObject, GameObject parentObject)
        {
            if (fracturedObject == null || parentObject == null)
                return;
            IMovable parent = parentObject.GetComponent<IMovable>();
            if (parent == null || !ItemSync.TryGetSpawnKind(parent, out ItemSync.SpawnKind kind))
                return;
            FracturedDummyMovable[] pieces = fracturedObject.GetComponentsInChildren<FracturedDummyMovable>(true);
            if (pieces.Length == 0)
                return;
            int breakNo = ++_breakNo;
            foreach (KeyValuePair<FracturedDummyMovable, PieceOrigin> dead in Origins.Where(o => o.Key == null).ToList())
                Origins.Remove(dead.Key);
            for (int i = 0; i < pieces.Length; i++)
                Origins[pieces[i]] = new PieceOrigin { Kind = kind, AssetId = parent.AssetId, BreakNo = breakNo, Index = i };

            // Pieces of pieces would overflow the ID range; those stay local. (v0.11.8: this used to stop at
            // 1,562,500, which left out everything a client bought, IDs 2,000,000 and up, so their pieces never synced.)
            if (Breaking.TryGetValue(parent, out int itemId) && itemId >= 0 && itemId < MaxItemIdWithPieces)
            {
                Breaking.Remove(parent);
                int bound = 0;
                for (int i = 0; i < pieces.Length && i < MaxPiecesPerItem; i++)
                {
                    if (pieces[i] == null)
                        continue;
                    Instance?._items.Bind(PieceId(itemId, i), pieces[i]);
                    bound++;
                }
                Instance?._log.LogInfo($"[Breaks] Item {itemId} broke into {pieces.Length} piece(s); {bound} can be picked up by either player.");
            }
        }

        // Host, right after the item table: every piece in it that this copy knows the origin of.
        internal void SendPieces(Dictionary<IMovable, int> netIds)
        {
            var list = new List<KeyValuePair<int, FracturedDummyMovable>>();
            foreach (KeyValuePair<FracturedDummyMovable, PieceOrigin> pair in Origins)
            {
                if (pair.Key != null && netIds.TryGetValue(pair.Key, out int netId))
                    list.Add(new KeyValuePair<int, FracturedDummyMovable>(netId, pair.Key));
            }
            if (list.Count == 0)
                return;
            NetDataWriter writer = _session.BeginMessage(MessageType.PieceList);
            writer.Put(list.Count);
            foreach (KeyValuePair<int, FracturedDummyMovable> pair in list)
            {
                PieceOrigin o = Origins[pair.Value];
                Transform t = pair.Value.transform;
                writer.Put(pair.Key);
                writer.Put((byte)o.Kind);
                writer.Put(o.AssetId);
                writer.Put(o.BreakNo);
                writer.Put(o.Index);
                writer.Put(t.position.x); writer.Put(t.position.y); writer.Put(t.position.z);
                writer.Put(t.rotation.x); writer.Put(t.rotation.y); writer.Put(t.rotation.z); writer.Put(t.rotation.w);
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"[Breaks] Sent {list.Count} broken piece(s) for the other player to rebuild.");
        }

        private struct PieceEntry
        {
            public int NetId;
            public ItemSync.SpawnKind Kind;
            public int AssetId;
            public int BreakNo;
            public int Index;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        // Client: rebuild the host's pieces. One fresh copy of the broken item per break gives its shards; the
        // pieces needed are taken out of them and placed where the host has them, and the copy is thrown away.
        private void OnPieces(NetDataReader reader)
        {
            int count = reader.GetInt();
            var entries = new List<PieceEntry>();
            for (int i = 0; i < count; i++)
            {
                entries.Add(new PieceEntry
                {
                    NetId = reader.GetInt(),
                    Kind = (ItemSync.SpawnKind)reader.GetByte(),
                    AssetId = reader.GetInt(),
                    BreakNo = reader.GetInt(),
                    Index = reader.GetInt(),
                    Position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat()),
                    Rotation = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat()),
                });
            }
            int rebuilt = 0;
            int failed = 0;
            foreach (IGrouping<int, PieceEntry> group in entries.Where(e => !_items.TryGetItem(e.NetId, out _)).GroupBy(e => e.BreakNo))
            {
                PieceEntry first = group.First();
                IMovable copy = null;
                try
                {
                    copy = ItemSync.CreateCopy(first.Kind, first.AssetId, first.Position + Vector3.up * 50f, Quaternion.identity, Vector3.one);
                    GameObject shards = copy?.PhysicsController != null ? copy.PhysicsController.FracturedObject : null;
                    if (shards == null)
                    {
                        failed += group.Count();
                        continue;
                    }
                    FracturedDummyMovable[] pieces = shards.GetComponentsInChildren<FracturedDummyMovable>(true);
                    foreach (PieceEntry e in group)
                    {
                        if (e.Index < 0 || e.Index >= pieces.Length || pieces[e.Index] == null)
                        {
                            failed++;
                            continue;
                        }
                        FracturedDummyMovable piece = pieces[e.Index];
                        piece.transform.SetParent(null, true);
                        piece.transform.SetPositionAndRotation(e.Position, e.Rotation);
                        piece.gameObject.SetActive(true);
                        Origins[piece] = new PieceOrigin { Kind = e.Kind, AssetId = e.AssetId, BreakNo = e.BreakNo, Index = e.Index };
                        Game.KitchenServices.FracturedObjects.AddFracturedObject(piece.gameObject);
                        _items.Bind(e.NetId, piece);
                        rebuilt++;
                    }
                }
                catch (Exception ex)
                {
                    failed += group.Count();
                    _log.LogWarning($"[Breaks] Rebuilding the pieces of a broken {first.Kind} (asset {first.AssetId}) failed: {ex.Message}");
                }
                finally
                {
                    if (copy is Object obj && obj != null)
                        Object.Destroy(copy.gameObject);
                }
            }
            _log.LogInfo($"[Breaks] Rebuilt {rebuilt} of the host's broken pieces here" + (failed > 0 ? $" ({failed} couldn't be rebuilt)." : "."));
        }

        private bool Active => _session.IsConnected && WorldSync.InKitchen;

        // Called by the prefix. Returns false to stop the break on this copy.
        internal bool AllowBreak(MovablePhysics physics, string breakSound, float strength)
        {
            _pendingSound = breakSound;
            _pendingStrength = strength;
            if (Applying || !Active)
                return true;
            // The other player has it in hand or in flight: their copy decides whether it breaks.
            IMovable m = physics.Movable;
            if (m != null && _items.IsRemoteHeld(m))
                return false;
            if (m != null && _items.TryGetNetId(m, out int netId))
            {
                // A break that made no pieces never clears its entry; don't let those pile up.
                if (Breaking.Count > 32)
                    Breaking.Clear();
                Breaking[m] = netId;
            }
            return true;
        }

        private void OnLocalBroken(IMovable movable)
        {
            try
            {
                if (Applying || !Active || movable == null || !_items.TryGetNetId(movable, out int netId))
                    return;
                Transform t = movable.gameObject.transform;
                NetDataWriter writer = _session.BeginMessage(MessageType.ItemBroken);
                writer.Put(netId);
                writer.Put(t.position.x); writer.Put(t.position.y); writer.Put(t.position.z);
                writer.Put(t.rotation.x); writer.Put(t.rotation.y); writer.Put(t.rotation.z); writer.Put(t.rotation.w);
                writer.Put(_pendingStrength);
                writer.Put(_pendingSound ?? "");
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
                // It's gone here; stop tracking it so the item table doesn't also report it as removed.
                _items.DropItem(netId);
                _log.LogInfo($"[Breaks] Item {netId} broke here; told the other player.");
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Breaks] Sending a break failed: {e.Message}");
            }
        }

        private void OnRemoteBroken(NetDataReader reader)
        {
            int netId = reader.GetInt();
            var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            var rotation = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            float strength = reader.GetFloat();
            string sound = reader.GetString();

            // Already gone here (it broke on both copies at once, or was removed): nothing to do.
            if (!_items.TryGetItem(netId, out IMovable m) || m.PhysicsController == null)
                return;
            _items.DropItem(netId);
            Breaking[m] = netId;
            m.gameObject.transform.SetPositionAndRotation(position, rotation);
            Applying = true;
            try
            {
                m.PhysicsController.Break(string.IsNullOrEmpty(sound) ? null : sound, strength);
            }
            finally
            {
                Applying = false;
            }
            _log.LogInfo($"[Breaks] Other player broke item {netId}; broke it here too.");
        }

        private void OnMessage(int peerId, MessageType type, NetDataReader reader)
        {
            if (!Handles(type) || !Active)
                return;
            try
            {
                if (type == MessageType.PieceList)
                {
                    if (!_session.IsHost)
                        OnPieces(reader);
                    return;
                }
                OnRemoteBroken(reader);
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Breaks] Applying the other player's break failed: {e.Message}");
            }
        }
    }

    internal static class BreakPatches
    {
        public static void ShatterPrefix(GameObject fracturedObject, GameObject parentObject)
        {
            try
            {
                BreakSync.RememberPieces(fracturedObject, parentObject);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Breaks] patch error: " + e);
            }
        }

        public static bool BreakPrefix(MovablePhysics __instance, string breakSound, float strength)
        {
            try
            {
                return BreakSync.Instance == null || BreakSync.Instance.AllowBreak(__instance, breakSound, strength);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Breaks] patch error: " + e);
                return true;
            }
        }
    }
}
