using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using WIUtils;
using Object = UnityEngine.Object;

namespace CookingSimMP
{
    // Items coming and going outside a player's hands (v0.11).
    //
    // Removed by the game: anything the game takes away on this copy (the trash bin, a plate put back on its stack,
    // food blended into a mixture) is removed on the other copy too. Before, only items this copy had touched in the
    // last minute were reported, so food the host trashed off a cutting board, or blended into a soup, stayed on the
    // client's screen.
    //
    // Vanished here only: an item can disappear on one copy alone (it fell through the counter there and the game
    // destroyed it under the floor). When the other player then uses it, this copy asks for it back and the other
    // copy sends a fresh one where it has it. Pieces of cut food can't be rebuilt that way and are left out.
    //
    // Falling through: food or a tool the other player put down gets the exact spot they left it in. For a few
    // seconds after that, this copy checks it stays there, and puts it back if it drops through the surface.
    //
    // Spatula: food the local player lifts, flips or pushes with a spatula moves without being picked up, so it
    // was never sent. Now it's sent like anything the player moves.
    //
    // Duplicates: a second tool the game made on this copy alone (a second hand blender) is removed when picked up.
    internal sealed partial class ItemSync
    {
        private const float RemovalWait = 1f;        // A removed item is reported once it's really gone (Destroy runs at the end of the frame).
        private const float ScanInterval = 0.5f;     // How often to look for items that vanished here.
        private const float AskAgainAfter = 10f;     // Don't ask the other copy for the same item more often than this.
        private const float GuardTime = 4f;          // How long an item the other player put down is checked for falling through.
        private const float FallDistance = 0.1f;     // Metres below its spot that count as falling through.
        private const int MaxSnaps = 1;              // After this many falls, it's frozen in place (v0.11.2: at once,
                                                     // since snapping back only made it fall again, like a bounce).
        private const float PushRadius = 0.15f;      // Food this close to the spatula's blade is watched while it moves.
        private const float PushSpeed = 0.05f;       // ...once it moves at least this fast (m/s).

        // True while removing an item because the other copy did, so it isn't reported straight back.
        private static bool _applyingRemoval;

        // Removed by the game here, waiting to be gone for good before the other copy is told: ID -> (item, time left).
        private readonly Dictionary<int, KeyValuePair<IMovable, float>> _removedHere = new Dictionary<int, KeyValuePair<IMovable, float>>();
        // IDs this copy dropped on purpose (removed, served, cut, broken): never asked back from the other copy.
        private readonly HashSet<int> _goneHere = new HashSet<int>();
        // Pieces of cut food: the other copy can't rebuild these from their asset.
        private readonly HashSet<int> _cutPieces = new HashSet<int>();
        private readonly Dictionary<int, float> _askedAt = new Dictionary<int, float>();
        private readonly HashSet<int> _cantRebuild = new HashSet<int>();
        private readonly List<int> _scratchRemoved = new List<int>();
        private float _scanTimer;
        private float _lifeClock;

        private sealed class RestGuard
        {
            public IMovable Movable;
            public Transform Main;
            public Rigidbody Body;
            public Pose MainPose;
            public Pose RootPose;
            public float Left;
            public int Snaps;
        }

        private readonly List<RestGuard> _restGuards = new List<RestGuard>();
        private readonly HashSet<int> _watchedByTool = new HashSet<int>();
        private readonly Collider[] _pushHits = new Collider[32];

        private void PatchLifecycle()
        {
            ProductPart.OnBlend += OnLocalBlend;
            try
            {
                System.Reflection.MethodInfo remove = HarmonyLib.AccessTools.Method(typeof(ActionsManager), nameof(ActionsManager.Remove), new[] { typeof(IMovable) });
                _harmony.Patch(remove, prefix: new HarmonyLib.HarmonyMethod(typeof(ItemSync), nameof(RemovePrefix)));
            }
            catch (Exception e)
            {
                _log.LogError($"Patching ActionsManager.Remove failed; items the game removes here (trash) won't be removed for the other player: {e}");
            }
        }

        private void ResetLifecycle()
        {
            _removedHere.Clear();
            _goneHere.Clear();
            _cutPieces.Clear();
            _askedAt.Clear();
            _cantRebuild.Clear();
            _restGuards.Clear();
            _watchedByTool.Clear();
        }

        private void TickLifecycle(float deltaTime)
        {
            _lifeClock += deltaTime;
            if (!_session.IsConnected || _registryScene == null)
            {
                _removedHere.Clear();
                return;
            }
            FlushRemovals(deltaTime);
            _scanTimer += deltaTime;
            if (_scanTimer >= ScanInterval)
            {
                _scanTimer = 0f;
                ScanForVanished();
            }
            WatchSpatula();
        }

        // ---------- Removed by the game ----------

        private static void RemovePrefix(IMovable movable)
        {
            try
            {
                Instance?.OnGameRemoving(movable);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] remove patch error: " + e);
            }
        }

        // ActionsManager.Remove: the trash bin, a plate going back on its stack, a container being cleared.
        private void OnGameRemoving(IMovable movable)
        {
            if (_applyingRemoval || !_session.IsConnected || _registryScene == null || !IsAlive(movable))
                return;
            MarkRemoved(movable);
            // What sits on it goes with it (the game destroys it with the container or drops it in the bin too).
            if (movable is Container container)
            {
                foreach (IMovable inner in container.MovablesRecursive)
                    MarkRemoved(inner);
                foreach (ProductPart part in container.GetPartsRecursive())
                    MarkRemoved(part);
            }
        }

        // Food blended into a mixture (hand blender, stand mixer): the game destroys the piece and adds a liquid,
        // which ContentsSync sends. The piece has to go on the other copy too.
        private void OnLocalBlend(ProductPart part)
        {
            try
            {
                if (_applyingRemoval || !_session.IsConnected || _registryScene == null || part == null)
                    return;
                MarkRemoved(part);
            }
            catch (Exception e)
            {
                _log.LogWarning($"Noting a blended piece failed: {e.Message}");
            }
        }

        private void MarkRemoved(IMovable movable)
        {
            if (IsAlive(movable) && _netIdOf.TryGetValue(movable, out int netId))
                _removedHere[netId] = new KeyValuePair<IMovable, float>(movable, RemovalWait);
        }

        // Reports removed items once they're gone. One the game only moved (still there after a second) is dropped.
        private void FlushRemovals(float deltaTime)
        {
            if (_removedHere.Count == 0)
                return;
            _scratchRemoved.Clear();
            _scratchRemoved.AddRange(_removedHere.Keys);
            foreach (int netId in _scratchRemoved)
            {
                KeyValuePair<IMovable, float> entry = _removedHere[netId];
                if (!_byNetId.TryGetValue(netId, out IMovable current) || !ReferenceEquals(current, entry.Key))
                {
                    // Already dealt with elsewhere (cut, broken, served).
                    _removedHere.Remove(netId);
                    continue;
                }
                if (!IsAlive(entry.Key))
                {
                    _removedHere.Remove(netId);
                    SendRemoved(netId, "removed by the game");
                    continue;
                }
                float left = entry.Value - deltaTime;
                if (left <= 0f)
                    _removedHere.Remove(netId);
                else
                    _removedHere[netId] = new KeyValuePair<IMovable, float>(entry.Key, left);
            }
        }

        private void SendRemoved(int netId, string why)
        {
            _session.BeginMessage(MessageType.ItemRemoved).Put(netId);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"Item {netId} was {why}; told the other player.");
            ForgetItem(netId);
        }

        // Served here: the plate and the food on it are gone. OrderSync tells the other copy with the order.
        internal void ForgetServed(Container plate)
        {
            if (!IsAlive(plate))
                return;
            var gone = new List<IMovable>(plate.MovablesRecursive);
            gone.AddRange(plate.GetPartsRecursive());
            gone.Add(plate);
            foreach (IMovable m in gone)
            {
                if (IsAlive(m) && _netIdOf.TryGetValue(m, out int netId))
                    ForgetItem(netId);
            }
        }

        // ---------- Vanished here only ----------

        // An item that died here without the game removing it. Nothing is sent: if the other player still has it
        // and uses it, this copy asks for it back then.
        private void ScanForVanished()
        {
            _scratchRemoved.Clear();
            foreach (KeyValuePair<int, IMovable> pair in _byNetId)
            {
                if (!IsAlive(pair.Value) && !_removedHere.ContainsKey(pair.Key) && !_localTracks.ContainsKey(pair.Key))
                    _scratchRemoved.Add(pair.Key);
            }
            foreach (int netId in _scratchRemoved)
            {
                if (_remoteHeld.TryGetValue(netId, out RemoteHeldItem held))
                {
                    ReleaseRemoteHeld(held, snap: false);
                    _remoteHeld.Remove(netId);
                    _log.LogWarning($"Item {netId} vanished here while the other player was holding it; asking them for it.");
                    RequestMissing(netId);
                }
                else
                {
                    _log.LogInfo($"Item {netId} vanished here without the game removing it (it may have fallen out of the kitchen); it comes back if the other player uses it.");
                }
                IMovable dead = _byNetId[netId];
                _byNetId.Remove(netId);
                _netIdOf.Remove(dead);
            }
        }

        // Ask the other copy to send a fresh copy of an item this copy doesn't have.
        internal void RequestMissing(int netId)
        {
            if (!_session.IsConnected || _registryScene == null || _goneHere.Contains(netId) || TryGetItem(netId, out _))
                return;
            if (_askedAt.TryGetValue(netId, out float at) && _lifeClock - at < AskAgainAfter)
                return;
            _askedAt[netId] = _lifeClock;
            _session.BeginMessage(MessageType.ItemMissing).Put(netId);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"Asked the other player for item {netId}, which this copy doesn't have.");
        }

        // The other copy lost this item: send what it takes to make it again, where it is now.
        private void OnItemMissing(int netId)
        {
            if (!TryGetItem(netId, out IMovable m))
                return;
            if (_cutPieces.Contains(netId) || m is FracturedDummyMovable || !TryGetSpawnKind(m, out SpawnKind kind))
            {
                if (_cantRebuild.Add(netId))
                    _log.LogWarning($"The other player lost {Describe(netId, m)}, which can't be rebuilt there (a cut or broken piece).");
                return;
            }
            Transform root = m.gameObject.transform;
            NetDataWriter writer = _session.BeginMessage(MessageType.ItemRespawn);
            writer.Put(netId);
            writer.Put((byte)kind);
            writer.Put(m.AssetId);
            PutVector(writer, root.position);
            PutQuaternion(writer, root.rotation);
            PutVector(writer, root.localScale);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"The other player lost {Describe(netId, m)}; sent it again.");
            // Still moving or in our hand here: start following it there again.
            if (_localTracks.TryGetValue(netId, out LocalTrack track) && track.Sending)
            {
                NetDataWriter start = _session.BeginMessage(MessageType.HoldStart);
                start.Put(netId);
                start.Put(PauseSync.LocalName());
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
            }
        }

        private void OnItemRespawn(NetPacketReader reader)
        {
            int netId = reader.GetInt();
            var kind = (SpawnKind)reader.GetByte();
            int assetId = reader.GetInt();
            Vector3 position = GetVector(reader);
            Quaternion rotation = GetQuaternion(reader);
            Vector3 scale = GetVector(reader);
            if (TryGetItem(netId, out _) || _registryScene == null || Player.Me == null)
                return;
            IMovable copy = CreateCopy(kind, assetId, position, rotation, scale);
            if (!IsAlive(copy))
            {
                _log.LogWarning($"Couldn't rebuild item {netId} ({kind}, asset {assetId}) from the other player.");
                return;
            }
            _byNetId[netId] = copy;
            _netIdOf[copy] = netId;
            _askedAt.Remove(netId);
            _log.LogInfo($"Got {Describe(netId, copy)} back from the other player; it had vanished here.");
        }

        // ---------- Falling through after a put-down ----------

        // The other player put this down here; make sure it stays there for a few seconds.
        private void GuardRest(IMovable movable, Vector3 mainPosition, Quaternion mainRotation, Pose rootPose)
        {
            if (!IsAlive(movable))
                return;
            Transform main = PoseTransformOf(movable);
            _restGuards.RemoveAll(g => ReferenceEquals(g.Movable, movable));
            _restGuards.Add(new RestGuard
            {
                Movable = movable,
                Main = main,
                Body = MainBodyOf(movable),
                MainPose = new Pose(mainPosition, mainRotation),
                RootPose = rootPose,
                Left = GuardTime,
            });
        }

        private void CheckRestGuards(float deltaTime)
        {
            for (int i = _restGuards.Count - 1; i >= 0; i--)
            {
                RestGuard g = _restGuards[i];
                g.Left -= deltaTime;
                if (g.Left <= 0f || !IsAlive(g.Movable) || g.Main == null || IsMovedByAnyone(g.Movable))
                {
                    _restGuards.RemoveAt(i);
                    continue;
                }
                if (g.Main.position.y > g.MainPose.position.y - FallDistance)
                    continue;
                Transform root = g.Movable.gameObject.transform;
                if (root != g.Main)
                    root.SetPositionAndRotation(g.RootPose.position, g.RootPose.rotation);
                g.Main.SetPositionAndRotation(g.MainPose.position, g.MainPose.rotation);
                if (g.Body != null && !g.Body.isKinematic)
                {
                    g.Body.velocity = Vector3.zero;
                    g.Body.angularVelocity = Vector3.zero;
                    g.Body.Sleep();
                }
                g.Snaps++;
                string name = _netIdOf.TryGetValue(g.Movable, out int netId) ? Describe(netId, g.Movable) : g.Movable.gameObject.name;
                if (g.Snaps >= MaxSnaps)
                {
                    if (g.Body != null)
                        g.Body.isKinematic = true;
                    _restGuards.RemoveAt(i);
                    _log.LogWarning($"{name} fell through where the other player put it; put it back and holding it in place there.");
                }
                else
                {
                    _log.LogInfo($"{name} fell through where the other player put it; put it back.");
                }
            }
        }

        private bool IsMovedByAnyone(IMovable movable)
        {
            if (Player.Me != null && ReferenceEquals(Player.Me.ActiveMovable, movable))
                return true;
            if (!_netIdOf.TryGetValue(movable, out int netId))
                return false;
            return _remoteHeld.ContainsKey(netId) || (_localTracks.TryGetValue(netId, out LocalTrack track) && (track.InHand || track.Sending));
        }

        // ---------- Spatula ----------

        // Food on the local player's spatula, or pushed by it, is sent like anything the player moves.
        private void WatchSpatula()
        {
            if (!(Player.Me != null && Player.Me.ActiveMovable is SpatulaController spatula) || !IsAlive(spatula) || spatula.TTrigger == null)
            {
                _watchedByTool.Clear();
                return;
            }
            List<IMovable> lifted = spatula.TTrigger.Movables;
            if (lifted != null)
            {
                foreach (IMovable m in lifted)
                    WatchMovedByTool(m);
            }
            int count = Physics.OverlapSphereNonAlloc(spatula.TTrigger.transform.position, PushRadius, _pushHits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider hit = _pushHits[i];
                if (hit == null)
                    continue;
                MovablePhysics physics = MovableBackup.TryGetComponent<MovablePhysics>(hit.gameObject);
                if (physics == null || !(physics.Movable is ProductPart part))
                    continue;
                Rigidbody body = hit.attachedRigidbody;
                if (body != null && !body.isKinematic && body.velocity.sqrMagnitude > PushSpeed * PushSpeed)
                    WatchMovedByTool(part);
            }
        }

        private void WatchMovedByTool(IMovable m)
        {
            if (!IsAlive(m) || !_netIdOf.TryGetValue(m, out int netId) || _remoteHeld.ContainsKey(netId) || _localTracks.ContainsKey(netId))
                return;
            SendUntilRest(netId, m);
            if (_watchedByTool.Add(netId))
                _log.LogInfo($"Moving {Describe(netId, m)} with the spatula; syncing.");
        }

        // ---------- Items nobody announced ----------

        // Picked up something that isn't in the item table. A second copy of a tool that is in the table is a
        // duplicate the game made on this copy alone, so it goes. Anything else is only reported.
        private void HandleUnknownItem(IMovable m)
        {
            string what = $"{m.GetType().Name} '{m.gameObject.name}' (asset {m.AssetId})";
            if (!_session.IsConnected || _registryScene == null || !(m is MovableTool))
            {
                _log.LogWarning($"Picked up {what}, which isn't in the item table, so it won't sync.");
                return;
            }
            int twinId = -1;
            foreach (KeyValuePair<int, IMovable> pair in _byNetId)
            {
                if (IsAlive(pair.Value) && !ReferenceEquals(pair.Value, m) && pair.Value.GetType() == m.GetType() && pair.Value.AssetId == m.AssetId)
                {
                    twinId = pair.Key;
                    break;
                }
            }
            if (twinId < 0)
            {
                _log.LogWarning($"Picked up {what}, which isn't in the item table, so it won't sync.");
                return;
            }
            Transform parent = m.gameObject.transform.parent;
            _log.LogWarning($"Picked up a second {what} that only exists on this copy (parent '{(parent != null ? parent.name : "none")}'); " +
                            $"the shared one is item {twinId}. Removing the extra copy.");
            try
            {
                if (Player.Me != null && ReferenceEquals(Player.Me.ActiveMovable, m) && m.CanThrow)
                    Singleton<ActionsManager>.Me.Throw(m, addForce: false, withoutSound: true);
                _applyingRemoval = true;
                Singleton<ActionsManager>.Me.Remove(m);
            }
            catch (Exception e)
            {
                _log.LogWarning($"Removing the extra {what} failed: {e.Message}");
            }
            finally
            {
                _applyingRemoval = false;
            }
            PauseSync.Instance?.Notify("Removed a duplicate tool that only existed on this screen");
        }
    }
}
