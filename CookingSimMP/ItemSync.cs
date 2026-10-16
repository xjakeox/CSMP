using System.Collections.Generic;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using WIUtils;

namespace CookingSimMP
{
    // Syncs every item a player can pick up (anything implementing the game's IMovable: containers, loose food,
    // knives and other tools, oil bottles and spice shakers, dummies) while someone moves it, plus buying new ones.
    //
    // Matching: the host numbers every item and sends (number, type, asset ID, GlobalId, position). The client
    // loaded the host's world (WorldSync), and the game saves and restores each item's GlobalId, so it pairs
    // each entry with its own item of the same type, asset ID and GlobalId. Items without a matching GlobalId
    // (scene objects the save doesn't cover) fall back to the nearest item of the same type and asset ID.
    //
    // Authority: whoever last touched an item sends its pose while it moves. The other copy freezes its
    // physics and follows.
    //
    // Grab conflicts: an item the other player is holding (or carrying in a pan, plate or pot) can't be picked
    // up; the click does nothing and a short message says who has it. If both players grab the same item within
    // the network delay, the host wins and the client's copy drops it and follows the host.
    internal sealed partial class ItemSync
    {
        private const float SendInterval = 0.05f;
        private const float SettleTime = 1f;          // After letting go, keep sending at least this long...
        private const float StillTime = 0.3f;         // ...and until it has stopped moving for this long.
        private const float WatchTime = 60f;          // Keep watching a resting item this long in case the game moves it again.
        private const float WakeDistance = 0.005f;    // A watched item that moves this far (metres)...
        private const float WakeAngle = 2f;           // ...or turns this much (degrees) starts sending again.
        private const float ContentWakeDelay = 0.15f; // Food in a watched pan waits this long before it counts as moving.
        private const float MatchDistance = 0.5f;     // Max metres between host and client copies of an item.
        private const float SmoothingSpeed = 20f;

        // What the other copy has to create when someone buys an item.
        internal enum SpawnKind : byte
        {
            Container = 1,
            Tool = 2,
            Dummy = 3,
            ProductPart = 4,
        }

        private const float BlockedMessageCooldown = 2f;

        internal static ItemSync Instance;

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly HarmonyLib.Harmony _harmony = new HarmonyLib.Harmony(Plugin.PluginGuid + ".grab");
        private readonly Dictionary<int, string> _holderName = new Dictionary<int, string>();
        private float _blockedMessageTimer;
        private readonly List<int> _pendingYield = new List<int>();

        private readonly Dictionary<int, IMovable> _byNetId = new Dictionary<int, IMovable>();
        private readonly Dictionary<IMovable, int> _netIdOf = new Dictionary<IMovable, int>();
        private readonly Dictionary<int, LocalTrack> _localTracks = new Dictionary<int, LocalTrack>();
        private readonly Dictionary<int, RemoteHeldItem> _remoteHeld = new Dictionary<int, RemoteHeldItem>();
        private readonly List<int> _scratchIds = new List<int>();

        private string _registryScene;       // Scene the current ID table belongs to.
        private string _readySentForScene;   // Client: scene we already asked the host about.
        private string _pendingReadyScene;   // Host: client is waiting for a table for this scene.

        private PlayerHandSpawnPoint _subscribedHand;
        private IMovable _unknownInHand;     // Picked up something without an ID; warn if it never gets one.
        private float _unknownSince;
        private float _sendTimer;

        // Items created during play get IDs from separate ranges so the two copies never pick the same one.
        // (The host's starting table uses 0, 1, 2...)
        private int _nextLocalId;

        // An item this copy is in charge of: in our hand, still moving after we let go, or resting but watched,
        // because using a tool (chopping, shaking, pouring) moves it in and out of the hand repeatedly.
        private sealed class LocalTrack
        {
            public IMovable Movable;
            public bool InHand;
            public bool Sending;
            public float SinceRelease;
            public float StillFor;
            public float IdleFor;
            public Vector3 LastPosition;
            public Quaternion LastRotation;
            public Vector3 RestPosition;
            public Quaternion RestRotation;
            public float WakeDelay;              // How long it has looked moved while sitting in a pan we watch.
        }

        private sealed class RemoteHeldItem
        {
            public IMovable Movable;
            public Transform Main;               // The transform whose pose is synced (the item's main rigidbody).
            public Rigidbody MainBody;
            public bool? MainEndKinematic;       // From the other copy's final pose: does the game keep it frozen?
            public Vector3 TargetPosition;
            public Quaternion TargetRotation;
            public List<PinnedPart> Pinned = new List<PinnedPart>();
            public List<Rigidbody> Bones = new List<Rigidbody>();
            public List<JointCorrections> Joints = new List<JointCorrections>();  // Paused while held, so they don't tug the bones we place.
            public int ContentCount;
        }

        // Anything that has to move rigidly with a remote item: its root object, its physics bones
        // (meat has a ragdoll), and food or items sitting in a container.
        private sealed class PinnedPart
        {
            public Transform Transform;
            public Rigidbody Body;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public bool UnderMain;               // Child of the main transform, so it must be placed after it.
        }

        public ItemSync(NetSession session, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
            PatchPick();
            PatchLifecycle();
            _session.PeerLeft += _ => Reset();
            _session.Stopped += Reset;
            _session.WorldReset += Reset;
            // Stacks (plates, bowls, tools...) create the bought item and put it in the hand.
            Stack.OnBuy += OnStackBuy;
            // The shopping box (GUIBoxOfStuff) creates the bought item the same way and raises its own OnBuy.
            CS.GUI.GUIBoxOfStuff.OnBuy += OnStackBuy;
            // Clicking food in a fridge or on a shelf creates a Product that spawns the actual pieces.
            Product.OnBuy += OnProductBuy;
        }

        public void Tick(float deltaTime)
        {
            string scene = SceneManager.GetActiveScene().name;
            bool inKitchen = WorldSync.InKitchen;

            // A new scene destroys every item, so the old ID table is useless.
            if (_registryScene != null && _registryScene != scene)
                ClearTable();

            SubscribeToHand();
            WarnIfUnknownStillHeld(deltaTime);
            if (_blockedMessageTimer > 0f)
                _blockedMessageTimer -= deltaTime;
            foreach (int id in _pendingYield)
            {
                if (_byNetId.TryGetValue(id, out IMovable held) && IsAlive(held))
                    YieldToHost(id, held);
            }
            _pendingYield.Clear();

            if (_session.IsConnected && inKitchen)
            {
                if (!_session.IsHost && _readySentForScene != scene)
                {
                    _readySentForScene = scene;
                    _session.BeginMessage(MessageType.ClientReady).Put(scene);
                    _session.SendToAll(DeliveryMethod.ReliableOrdered);
                }
                if (_session.IsHost && _pendingReadyScene == scene)
                {
                    _pendingReadyScene = null;
                    BuildAndSendRegistry(scene);
                }
            }

            UpdateLocalTracks(deltaTime);
            TickLifecycle(deltaTime);
            SendPendingSeats(deltaTime);
        }

        private void SendPendingSeats(float deltaTime)
        {
            _seatClock += deltaTime;
            for (int i = _pendingSeats.Count - 1; i >= 0; i--)
            {
                PendingSeat seat = _pendingSeats[i];
                if (_seatClock < seat.Due)
                    continue;
                if (!_session.IsConnected || !_byNetId.TryGetValue(seat.NetId, out IMovable m) || !IsAlive(m)
                    || !_byNetId.TryGetValue(seat.HolderId, out IMovable holder) || !IsAlive(holder))
                {
                    _pendingSeats.RemoveAt(i);
                    continue;
                }
                Transform ht = holder.gameObject.transform;
                Transform t = m.gameObject.transform;
                NetDataWriter writer = _session.BeginMessage(MessageType.ItemSeated);
                writer.Put(seat.NetId);
                writer.Put(seat.HolderId);
                PutVector(writer, ht.InverseTransformPoint(t.position));
                PutQuaternion(writer, Quaternion.Inverse(ht.rotation) * t.rotation);
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
                seat.SendsLeft--;
                if (seat.SendsLeft <= 0)
                    _pendingSeats.RemoveAt(i);
                else
                    seat.Due = _seatClock + SeatDelays[SeatDelays.Length - seat.SendsLeft] - SeatDelays[SeatDelays.Length - seat.SendsLeft - 1];
            }
        }

        // The other player's purchase settled in a container they're carrying: move our copy to the same spot in it.
        private void OnRemoteSeated(NetPacketReader reader)
        {
            int netId = reader.GetInt();
            int holderId = reader.GetInt();
            Vector3 localPosition = GetVector(reader);
            Quaternion localRotation = GetQuaternion(reader);
            // Only while it's carried; once put down, the final positions come with the drop.
            if (!_byNetId.TryGetValue(netId, out IMovable m) || !IsAlive(m)
                || !_remoteHeld.TryGetValue(holderId, out RemoteHeldItem carried) || carried.Main == null)
                return;
            Transform ht = carried.Movable.gameObject.transform;
            Transform root = m.gameObject.transform;
            root.SetPositionAndRotation(ht.TransformPoint(localPosition), ht.rotation * localRotation);
            // Its pinned parts now sit somewhere else relative to the carried container; record the new spots.
            Quaternion inverse = Quaternion.Inverse(carried.Main.rotation);
            foreach (PinnedPart pinned in carried.Pinned)
            {
                if (pinned.Transform == null || (pinned.Transform != root && !pinned.Transform.IsChildOf(root)))
                    continue;
                pinned.LocalPosition = inverse * (pinned.Transform.position - carried.Main.position);
                pinned.LocalRotation = inverse * pinned.Transform.rotation;
            }
        }

        // Runs after every game script's Update, so nothing the game does this frame
        // (e.g. a package snapping its food back into place) can override where we put remote items.
        public void LateTick(float deltaTime)
        {
            FollowRemotePoses(deltaTime);
            CheckRestGuards(deltaTime);
        }

        // ---------- ID table ----------

        // Every pickable item in the scene. Containers, food, tools, bottles and dummies all derive from Clickable.
        private static List<IMovable> FindSceneMovables()
        {
            var result = new List<IMovable>();
            foreach (Clickable clickable in Object.FindObjectsOfType<Clickable>())
            {
                if (clickable is IMovable movable)
                    result.Add(movable);
            }
            return result;
        }

        private static string TypeKey(IMovable movable) => movable.GetType().Name;

        private void BuildAndSendRegistry(string scene)
        {
            ClearTable();
            _registryScene = scene;

            List<IMovable> movables = FindSceneMovables();

            // Type names go once in a small table; each item then refers to its type by index.
            var typeNames = new List<string>();
            var typeIndex = new Dictionary<string, int>();
            foreach (IMovable m in movables)
            {
                string key = TypeKey(m);
                if (!typeIndex.ContainsKey(key))
                {
                    typeIndex[key] = typeNames.Count;
                    typeNames.Add(key);
                }
            }

            NetDataWriter writer = _session.BeginMessage(MessageType.ItemRegistry);
            writer.Put(scene);
            writer.Put(typeNames.Count);
            foreach (string name in typeNames)
                writer.Put(name);
            writer.Put(movables.Count);
            for (int i = 0; i < movables.Count; i++)
            {
                IMovable m = movables[i];
                _byNetId[i] = m;
                _netIdOf[m] = i;
                writer.Put(i);
                writer.Put(typeIndex[TypeKey(m)]);
                writer.Put(m.AssetId);
                writer.Put(m.GlobalId);
                PutVector(writer, m.gameObject.transform.position);
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"Sent item table: {movables.Count} items of {typeNames.Count} types in {scene}.");

            // Broken pieces aren't in the world save, so the client's copy has none; it rebuilds them from this list.
            BreakSync.Instance?.SendPieces(_netIdOf);

            // Something already in the host's hand (held while the client joined) keeps syncing.
            if (Player.Me != null && Player.Me.ActiveMovable is IMovable held && IsAlive(held) && _netIdOf.ContainsKey(held))
                OnLocalHandChanged(held, false);
        }

        private void ApplyRegistry(NetPacketReader reader)
        {
            string scene = reader.GetString(128);
            string localScene = SceneManager.GetActiveScene().name;
            if (scene != localScene)
            {
                _log.LogWarning($"Ignoring item table for {scene}; this copy is in {localScene}.");
                return;
            }

            int typeCount = reader.GetInt();
            var typeNames = new string[typeCount];
            for (int i = 0; i < typeCount; i++)
                typeNames[i] = reader.GetString(128);

            ClearTable();
            _registryScene = scene;

            // Group local items by (type, asset) so matching doesn't scan every item for every entry.
            var unclaimed = new Dictionary<string, List<IMovable>>();
            var byGlobalId = new Dictionary<int, IMovable>();
            var duplicateIds = new HashSet<int>();
            int localTotal = 0;
            foreach (IMovable m in FindSceneMovables())
            {
                string key = TypeKey(m) + "#" + m.AssetId;
                if (!unclaimed.TryGetValue(key, out List<IMovable> list))
                    unclaimed[key] = list = new List<IMovable>();
                list.Add(m);
                localTotal++;
                if (byGlobalId.ContainsKey(m.GlobalId))
                    duplicateIds.Add(m.GlobalId);
                byGlobalId[m.GlobalId] = m;
            }

            int count = reader.GetInt();
            int matched = 0;
            int matchedById = 0;
            var missedByType = new Dictionary<string, int>();
            for (int i = 0; i < count; i++)
            {
                int netId = reader.GetInt();
                string typeName = typeNames[reader.GetInt()];
                int assetId = reader.GetInt();
                int globalId = reader.GetInt();
                Vector3 position = GetVector(reader);

                IMovable best = null;
                float bestDistance = MatchDistance;
                unclaimed.TryGetValue(typeName + "#" + assetId, out List<IMovable> candidates);
                if (candidates != null && !duplicateIds.Contains(globalId) && byGlobalId.TryGetValue(globalId, out IMovable same)
                    && candidates.Contains(same))
                {
                    best = same;
                    matchedById++;
                }
                else if (candidates != null)
                {
                    foreach (IMovable c in candidates)
                    {
                        float d = Vector3.Distance(c.gameObject.transform.position, position);
                        if (d <= bestDistance)
                        {
                            best = c;
                            bestDistance = d;
                        }
                    }
                }
                if (best == null)
                {
                    missedByType.TryGetValue(typeName, out int missed);
                    missedByType[typeName] = missed + 1;
                    continue;
                }

                candidates.Remove(best);
                _byNetId[netId] = best;
                _netIdOf[best] = netId;
                matched++;
            }

            _log.LogInfo($"Matched {matched} of {count} host items, {matchedById} by saved ID ({localTotal - matched} local items unmatched).");
            foreach (KeyValuePair<string, int> pair in missedByType)
            {
                if (pair.Key == nameof(FracturedDummyMovable))
                    _log.LogInfo($"  {pair.Value} broken piece(s) on the host aren't in the save; rebuilding them here next.");
                else
                    _log.LogWarning($"  Couldn't match {pair.Value} host item(s) of type {pair.Key}.");
            }
        }

        private void ClearTable()
        {
            ReleaseAllRemoteHeld();
            _byNetId.Clear();
            _netIdOf.Clear();
            _localTracks.Clear();
            _registryScene = null;
            _unknownInHand = null;
            ResetLifecycle();
        }

        // Food bought onto a container: when to send where it settled in it (it's created just above and drops in).
        private sealed class PendingSeat
        {
            public int NetId;
            public int HolderId;
            public float Due;
            public int SendsLeft;
        }

        private static readonly float[] SeatDelays = { 0.5f, 1.5f };
        private readonly List<PendingSeat> _pendingSeats = new List<PendingSeat>();
        private float _seatClock;

        private void Reset()
        {
            ClearTable();
            _pendingSeats.Clear();
            _readySentForScene = null;
            _pendingReadyScene = null;
            _nextLocalId = 0;
            _holderName.Clear();
            _pendingYield.Clear();
        }

        // IMovable is an interface, so "== null" skips Unity's destroyed-object check; this does it properly.
        private static bool IsAlive(IMovable movable) => movable is Object obj && obj != null;

        // ---------- Lookups for other syncs (CookingSync) ----------

        // True once both copies share an ID table for the current scene.
        public bool HasTable => _registryScene != null && _registryScene == SceneManager.GetActiveScene().name;

        public bool TryGetNetId(IMovable movable, out int netId) => _netIdOf.TryGetValue(movable, out netId);

        // Gives an item this copy just rebuilt (a broken piece) the host's ID for it.
        internal void Bind(int netId, IMovable movable)
        {
            _byNetId[netId] = movable;
            _netIdOf[movable] = netId;
        }

        public bool TryGetItem(int netId, out IMovable movable) =>
            _byNetId.TryGetValue(netId, out movable) && IsAlive(movable);

        public IEnumerable<KeyValuePair<int, IMovable>> AllItems => _byNetId;

        // True while the other player is moving this item and this copy only follows it.
        public bool IsRemoteHeld(IMovable movable) =>
            _netIdOf.TryGetValue(movable, out int netId) && _remoteHeld.ContainsKey(netId);

        // ---------- Used by CutSync: cutting replaces one item with new pieces ----------

        // A fresh ID from this copy's range, so the two copies never hand out the same one.
        public int NewLocalId() => (_session.IsHost ? 1_000_000 : 2_000_000) + _nextLocalId++;

        public void RegisterItem(int netId, IMovable movable)
        {
            _byNetId[netId] = movable;
            _netIdOf[movable] = netId;
            _cutPieces.Add(netId);
        }

        // The item is gone (cut into pieces). Stop following or sending it without telling the other copy,
        // which removes it the same way.
        public void DropItem(int netId)
        {
            if (_remoteHeld.TryGetValue(netId, out RemoteHeldItem item))
            {
                ReleaseRemoteHeld(item, snap: true);
                _remoteHeld.Remove(netId);
            }
            ForgetItem(netId);
        }

        // Send this item's pose until it comes to rest, as if the local player had just let go of it.
        public void SendUntilRest(int netId, IMovable movable)
        {
            if (_remoteHeld.ContainsKey(netId))
                return;
            LocalTrack track = TakeAuthority(netId, movable);
            track.InHand = false;
        }

        private static Rigidbody MainBodyOf(IMovable m) =>
            m.PhysicsController != null ? m.PhysicsController.MoveRigidbody : null;

        // The pose that gets synced. The player's hand holds the main rigidbody, which for some items
        // (meat with bones) isn't the root object, so its pose is the one that matters.
        private static Transform PoseTransformOf(IMovable m)
        {
            Rigidbody body = MainBodyOf(m);
            return body != null ? body.transform : m.gameObject.transform;
        }

        // Every rigidbody an item owns apart from its main one, e.g. the bones in a piece of meat.
        // Index i must be the same bone on both copies. The game re-parents bones under the main body while
        // a player moves the item (MovablePhysics.SetKinematicBonesWhenMove), so the hierarchy differs between
        // copies. ProductPart.Rigidbodies comes from the prefab's bone list and doesn't change, so it goes first.
        private static List<Rigidbody> SecondaryBodiesOf(IMovable m)
        {
            Rigidbody main = MainBodyOf(m);
            var result = new List<Rigidbody>();
            var seen = new HashSet<Rigidbody>();
            if (main != null)
                seen.Add(main);
            // Food: only the meat bones the game itself tracks (JointCorrections fills this list in Awake,
            // so it's ready even on a copy created this frame; ProductPart.Rigidbodies only fills in Start).
            // Helper bodies such as PredictCollisionCollider are left to the game. Other food has no bones.
            IEnumerable<Rigidbody> bodies;
            if (m is ProductPart part)
            {
                JointCorrections joints = part.JointCorrection;
                bodies = joints != null && joints.BonesRigidbodies != null
                    ? (IEnumerable<Rigidbody>)joints.BonesRigidbodies
                    : new Rigidbody[0];
            }
            else
            {
                // Its own bodies only: food or a pot sitting in it (the game can parent them there) belongs to
                // that item, and the two copies needn't agree on it.
                var own = new List<Rigidbody>();
                foreach (Rigidbody body in m.gameObject.GetComponentsInChildren<Rigidbody>(true))
                {
                    IMovable owner = body.GetComponentInParent<IMovable>();
                    if (owner == null || ReferenceEquals(owner, m))
                        own.Add(body);
                }
                bodies = own;
            }
            foreach (Rigidbody body in bodies)
            {
                if (body != null && seen.Add(body))
                    result.Add(body);
            }
            return result;
        }

        private static string Describe(int netId, IMovable movable) =>
            $"{movable.GetType().Name} {netId} '{movable.gameObject.name}' (asset {movable.AssetId})";

        // ---------- Local player picking up and dropping ----------

        private void SubscribeToHand()
        {
            PlayerHandSpawnPoint hand = Player.Me != null ? Player.Me.HandSpawnPoint : null;
            if (hand == _subscribedHand)
                return;
            if (_subscribedHand != null)
                _subscribedHand.OnChangePlayerHandMovable -= OnLocalHandChanged;
            _subscribedHand = hand;
            if (hand != null)
                hand.OnChangePlayerHandMovable += OnLocalHandChanged;
        }

        // Fired by PlayerHandSpawnPoint.RegisterMovable (unregister = false) and UnregisterMovable (true).
        private void OnLocalHandChanged(IMovable movable, bool unregister)
        {
            if (!IsAlive(movable))
                return;
            if (!_netIdOf.TryGetValue(movable, out int netId))
            {
                // A just-bought item gets its ID a moment later, so only warn if it still has none next frame.
                if (!unregister && _registryScene != null)
                {
                    _unknownInHand = movable;
                    _unknownSince = 0f;
                }
                return;
            }
            if (!unregister && _remoteHeld.ContainsKey(netId))
            {
                // Both grabbed it at nearly the same moment (the pick-up started before the other player's grab
                // arrived). The host wins.
                if (_session.IsHost)
                {
                    if (_remoteHeld.TryGetValue(netId, out RemoteHeldItem lost))
                    {
                        ReleaseRemoteHeld(lost, snap: false);
                        _remoteHeld.Remove(netId);
                    }
                    _session.BeginMessage(MessageType.HoldDenied).Put(netId);
                    _session.SendToAll(DeliveryMethod.ReliableOrdered);
                }
                else
                {
                    // Dropping it from inside the hand's own event could confuse the game; do it next frame.
                    _pendingYield.Add(netId);
                    return;
                }
            }

            if (!unregister)
            {
                LocalTrack track = TakeAuthority(netId, movable);
                track.InHand = true;
                _log.LogInfo($"Picked up {Describe(netId, movable)}.");
            }
            else if (_localTracks.TryGetValue(netId, out LocalTrack track))
            {
                track.InHand = false;
                track.SinceRelease = 0f;
                track.StillFor = 0f;
                _log.LogInfo($"Let go of {Describe(netId, movable)}; syncing until it comes to rest.");
            }
        }

        // A bought item only gets its ID once it has finished moving into the hand, so wait a moment.
        private void WarnIfUnknownStillHeld(float deltaTime)
        {
            if (_unknownInHand == null)
                return;
            _unknownSince += deltaTime;
            if (_unknownSince < 2f)
                return;
            IMovable m = _unknownInHand;
            _unknownInHand = null;
            if (IsAlive(m) && !_netIdOf.ContainsKey(m))
                HandleUnknownItem(m);
        }

        // Start (or resume) sending an item's pose and tell the other copy to follow it.
        private LocalTrack TakeAuthority(int netId, IMovable movable)
        {
            if (!_localTracks.TryGetValue(netId, out LocalTrack track))
            {
                track = new LocalTrack { Movable = movable };
                _localTracks[netId] = track;
            }
            if (!track.Sending)
            {
                track.Sending = true;
                NetDataWriter start = _session.BeginMessage(MessageType.HoldStart);
                start.Put(netId);
                start.Put(PauseSync.LocalName());
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
            }
            Transform t = PoseTransformOf(movable);
            track.LastPosition = t.position;
            track.LastRotation = t.rotation;
            track.SinceRelease = 0f;
            track.StillFor = 0f;
            track.IdleFor = 0f;
            return track;
        }

        private void UpdateLocalTracks(float deltaTime)
        {
            _sendTimer += deltaTime;
            bool sendNow = _sendTimer >= SendInterval && _session.IsConnected;
            if (sendNow)
                _sendTimer = 0f;

            // Containers first, so a pan that starts moving this frame is already known to be moving when the food
            // in it is checked.
            _scratchIds.Clear();
            foreach (KeyValuePair<int, LocalTrack> pair in _localTracks)
            {
                if (pair.Value.Movable is Container)
                    _scratchIds.Add(pair.Key);
            }
            foreach (KeyValuePair<int, LocalTrack> pair in _localTracks)
            {
                if (!(pair.Value.Movable is Container))
                    _scratchIds.Add(pair.Key);
            }
            foreach (int netId in _scratchIds)
            {
                LocalTrack track = _localTracks[netId];

                // The game removed it while we were in charge, e.g. a plate put back on its stack.
                if (!IsAlive(track.Movable))
                {
                    SendRemoved(netId, "removed");
                    continue;
                }

                // Frozen because it rides in a pan or tray the other player is carrying: they move it, not us.
                // Treating that as our own movement made both copies send its position at once; the carrier saw
                // it lag behind the pan, and our final pose could drop it inside their pan and fling it away.
                if (!track.InHand && IsFrozen(MainBodyOf(track.Movable)))
                {
                    if (track.Sending)
                        SendPose(netId, track.Movable, MessageType.HoldEnd, DeliveryMethod.ReliableOrdered);
                    _localTracks.Remove(netId);
                    continue;
                }

                Transform t = PoseTransformOf(track.Movable);
                bool moved = (t.position - track.LastPosition).sqrMagnitude > 0.001f * 0.001f  // over 1 mm this frame
                             || Quaternion.Angle(t.rotation, track.LastRotation) > 0.5f;
                track.LastPosition = t.position;
                track.LastRotation = t.rotation;

                if (track.Sending)
                {
                    if (sendNow)
                        SendPose(netId, track.Movable, MessageType.HoldPose, DeliveryMethod.Sequenced);
                    if (track.InHand)
                        continue;

                    track.SinceRelease += deltaTime;
                    track.StillFor = moved ? 0f : track.StillFor + deltaTime;
                    if (track.SinceRelease >= SettleTime && track.StillFor >= StillTime)
                    {
                        SendPose(netId, track.Movable, MessageType.HoldEnd, DeliveryMethod.ReliableOrdered);
                        if (track.Movable is Container carried)
                            SendContentRest(carried);
                        track.Sending = false;
                        track.IdleFor = 0f;
                        track.RestPosition = t.position;
                        track.RestRotation = t.rotation;
                        _log.LogInfo($"{Describe(netId, track.Movable)} came to rest.");
                    }
                }
                else
                {
                    // Resting but watched: if the game moves it again (the next chop, another shake), resume.
                    if ((t.position - track.RestPosition).magnitude > WakeDistance
                        || Quaternion.Angle(t.rotation, track.RestRotation) > WakeAngle)
                    {
                        // Riding in the pan or tray this player is carrying: the other copy carries it along
                        // with the container, so it doesn't need poses of its own.
                        if (CarriedByLocalPlayer(track.Movable))
                        {
                            track.RestPosition = t.position;
                            track.RestRotation = t.rotation;
                            track.IdleFor = 0f;
                            track.WakeDelay = 0f;
                            continue;
                        }
                        // In a pan or tray we're watching: the game settles the food a frame or two before the
                        // pan itself starts moving when it's picked up. Wait a moment to see if the pan follows.
                        if (track.WakeDelay < ContentWakeDelay && InWatchedContainer(track.Movable))
                        {
                            track.WakeDelay += deltaTime;
                            continue;
                        }
                        track.WakeDelay = 0f;
                        TakeAuthority(netId, track.Movable);
                        _log.LogInfo($"{Describe(netId, track.Movable)} is moving again; syncing.");
                        continue;
                    }
                    track.IdleFor += deltaTime;
                    if (track.IdleFor >= WatchTime)
                        _localTracks.Remove(netId);
                }
            }
        }

        private void ForgetItem(int netId)
        {
            _localTracks.Remove(netId);
            _goneHere.Add(netId);
            if (_byNetId.TryGetValue(netId, out IMovable m))
            {
                _byNetId.Remove(netId);
                _netIdOf.Remove(m);
            }
        }

        private void SendPose(int netId, IMovable m, MessageType type, DeliveryMethod deliveryMethod)
        {
            Transform t = PoseTransformOf(m);
            NetDataWriter writer = _session.BeginMessage(type);
            writer.Put(netId);
            PutVector(writer, t.position);
            PutQuaternion(writer, t.rotation);
            // On the final pose, also say whether the game froze it in place (e.g. a knife parked mid-chop),
            // so the other copy doesn't let it fall.
            if (type == MessageType.HoldEnd || type == MessageType.ContentRest)
            {
                Rigidbody body = MainBodyOf(m);
                writer.Put(body != null && body.isKinematic);
                // Where the root and each bone ended up. When the game sets a part down it moves the root
                // separately from the main body (ProductPart.OnMoveEnd), so both have to be sent.
                Transform root = m.gameObject.transform;
                PutVector(writer, root.position);
                PutQuaternion(writer, root.rotation);
                List<Rigidbody> bones = SecondaryBodiesOf(m);
                writer.Put(bones.Count);
                foreach (Rigidbody bone in bones)
                {
                    writer.Put(bone.name);
                    PutVector(writer, bone.transform.position);
                    PutQuaternion(writer, bone.transform.rotation);
                    // On a set-down the game frees the bones (unparents them, physics on). The other copy must
                    // match, or its loose main body is tugged by joints to frozen bones and flies off.
                    writer.Put(bone.isKinematic);
                    writer.Put(bone.transform.parent == null);
                }
            }
            _session.SendToAll(deliveryMethod);
        }

        // ---------- Grab conflicts ----------

        private string HolderName(int netId) =>
            _holderName.TryGetValue(netId, out string name) && !string.IsNullOrEmpty(name) ? name : "The other player";

        // Client lost a simultaneous grab: drop the item where it is; the host's poses take it from here.
        private void YieldToHost(int netId, IMovable movable)
        {
            _localTracks.Remove(netId);
            if (Player.Me != null && ReferenceEquals(Player.Me.ActiveMovable, movable))
            {
                if (movable.CanThrow)
                {
                    Singleton<ActionsManager>.Me.Throw(movable, addForce: false, withoutSound: true);
                    // Dropping turns its physics back on; if we're already following the host, freeze it again.
                    if (_remoteHeld.TryGetValue(netId, out RemoteHeldItem following) && following.MainBody != null)
                    {
                        following.MainBody.isKinematic = true;
                        following.MainBody.detectCollisions = false;
                    }
                }
                else
                    _log.LogWarning($"Lost a grab of {Describe(netId, movable)} to the host but couldn't drop it; expect a desync.");
            }
            PauseSync.Instance?.Notify($"{HolderName(netId)} grabbed that first");
        }

        // Host: "I already had that." Only matters if the client still holds it.
        private void OnHoldDenied(int netId)
        {
            if (_session.IsHost || !_byNetId.TryGetValue(netId, out IMovable m) || !IsAlive(m))
                return;
            if (_localTracks.TryGetValue(netId, out LocalTrack track) && track.InHand)
                YieldToHost(netId, m);
        }

        // True when the item sits in a pan or tray this player is carrying or has just moved. The container's
        // pose covers it, so it needs no poses of its own. A container starts moving a moment before it counts
        // as in the hand, so a container this copy is watching that has left its resting spot counts too.
        private bool CarriedByLocalPlayer(IMovable movable)
        {
            if (Player.Me != null && Player.Me.ActiveMovable is Container held && Contains(held, movable))
                return true;
            foreach (LocalTrack track in _localTracks.Values)
            {
                if (!(track.Movable is Container c) || !IsAlive(c) || !Contains(c, movable))
                    continue;
                if (track.InHand || track.Sending)
                    return true;
                Transform t = PoseTransformOf(c);
                if ((t.position - track.RestPosition).magnitude > WakeDistance || Quaternion.Angle(t.rotation, track.RestRotation) > WakeAngle)
                    return true;
            }
            return false;
        }

        private bool InWatchedContainer(IMovable movable)
        {
            foreach (LocalTrack track in _localTracks.Values)
            {
                if (track.Movable is Container c && Contains(c, movable))
                    return true;
            }
            return false;
        }

        // A carried pan or tray came to rest. The other copy carried its food rigidly, but here the food could
        // roll or slide in the pan on the way (a cucumber on a tray), so where it really ended up is sent too.
        // Otherwise the other copy lets go of it at its old spot, partly inside the pan, and it drops through.
        private void SendContentRest(Container container)
        {
            var contents = new List<IMovable>(container.MovablesRecursive);
            foreach (ProductPart p in container.GetPartsRecursive())
                contents.Add(p);
            var sent = new HashSet<int>();
            foreach (IMovable content in contents)
            {
                if (!IsAlive(content) || !_netIdOf.TryGetValue(content, out int id) || !sent.Add(id))
                    continue;
                // Its own pose updates cover it.
                if (_localTracks.TryGetValue(id, out LocalTrack own) && (own.Sending || own.InHand))
                    continue;
                SendPose(id, content, MessageType.ContentRest, DeliveryMethod.ReliableOrdered);
            }
        }

        private static bool Contains(Container c, IMovable movable) =>
            IsAlive(c) && !ReferenceEquals(c, movable)
            && ((movable is ProductPart p && c.GetPartsRecursive().Contains(p)) || c.MovablesRecursive.Contains(movable));

        // True when the other player is holding this item, or carrying the container it sits in.
        private bool IsHeldByOther(IMovable movable, out int holderNetId)
        {
            holderNetId = -1;
            if (!_session.IsConnected || !IsAlive(movable))
                return false;
            if (_netIdOf.TryGetValue(movable, out int netId) && _remoteHeld.ContainsKey(netId))
            {
                holderNetId = netId;
                return true;
            }
            foreach (KeyValuePair<int, RemoteHeldItem> pair in _remoteHeld)
            {
                if (!(pair.Value.Movable is Container c) || !IsAlive(c))
                    continue;
                if ((movable is ProductPart p && c.GetPartsRecursive().Contains(p)) || c.MovablesRecursive.Contains(movable))
                {
                    holderNetId = pair.Key;
                    return true;
                }
            }
            return false;
        }

        // Prefix on ActionsManager.Pick: returning false cancels the pick-up, as if it couldn't be moved.
        internal bool AllowPick(IMovable movable)
        {
            if (!IsHeldByOther(movable, out int holder))
                return true;
            if (_blockedMessageTimer <= 0f)
            {
                _blockedMessageTimer = BlockedMessageCooldown;
                PauseSync.Instance?.Notify($"{HolderName(holder)} is holding that");
            }
            return false;
        }

        private void PatchPick()
        {
            try
            {
                System.Reflection.MethodInfo pick = HarmonyLib.AccessTools.Method(typeof(ActionsManager), "Pick",
                    new[] { typeof(IMovable), typeof(OnMoveEndCallback), typeof(SpawnPoint), typeof(bool), typeof(bool) });
                _harmony.Patch(pick, prefix: new HarmonyLib.HarmonyMethod(typeof(ItemSync), nameof(PickPrefix)));
            }
            catch (System.Exception e)
            {
                _log.LogError($"Patching ActionsManager.Pick failed; items the other player holds can still be grabbed: {e}");
            }
        }

        private static bool PickPrefix(IMovable movable, ref bool __result)
        {
            try
            {
                if (Instance == null || Instance.AllowPick(movable))
                    return true;
                __result = false;
                return false;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[CookingSimMP] grab patch error: " + e);
                return true;
            }
        }

        // ---------- Buying ----------

        // Stack.OnLeftClick creates the item with ObjectsFactory and charges for it, then raises OnBuy.
        // For food stacks it hands us the Product that spawns the pieces instead.
        private void OnStackBuy(Clickable bought)
        {
            if (bought is Product product)
                OnProductBuy(product);
            else if (bought is IMovable movable)
                AnnounceNewItem(movable);
        }

        // Product.TryCreateProduct raises OnBuy right after ProductsManager.CreateProduct. The pieces are moved
        // into place by a coroutine, and ProductPartSpawned fires for each one once it has arrived.
        private void OnProductBuy(Product product)
        {
            if (product == null)
                return;
            product.ProductPartSpawned += part =>
            {
                if (part != null)
                    AnnounceNewItem(part);
            };
        }

        // Give a newly created item an ID and tell the other copy to create a free copy of it.
        private void AnnounceNewItem(IMovable movable)
        {
            if (!IsAlive(movable) || _netIdOf.ContainsKey(movable))
                return;
            if (!_session.IsConnected || _registryScene == null)
                return;
            if (!TryGetSpawnKind(movable, out SpawnKind kind))
            {
                _log.LogWarning($"Bought a {movable.GetType().Name}, which can't be copied to the other player yet.");
                return;
            }

            int netId = NewLocalId();
            _byNetId[netId] = movable;
            _netIdOf[movable] = netId;

            Transform t = movable.gameObject.transform;
            NetDataWriter writer = _session.BeginMessage(MessageType.ItemSpawned);
            writer.Put(netId);
            writer.Put((byte)kind);
            writer.Put(movable.AssetId);
            PutVector(writer, t.position);
            PutQuaternion(writer, t.rotation);
            PutVector(writer, t.localScale);
            // Bought straight into a container (food onto the plate in your hand): say which one, and where in it,
            // so the other copy puts it there and carries it along instead of dropping it to the floor.
            Container holder = ContainerHolding(movable);
            if (holder != null && _netIdOf.TryGetValue(holder, out int holderId))
            {
                Transform ht = holder.gameObject.transform;
                writer.Put(holderId);
                PutVector(writer, ht.InverseTransformPoint(t.position));
                PutQuaternion(writer, Quaternion.Inverse(ht.rotation) * t.rotation);
                _pendingSeats.Add(new PendingSeat { NetId = netId, HolderId = holderId, Due = _seatClock + SeatDelays[0], SendsLeft = SeatDelays.Length });
            }
            else
            {
                writer.Put(-1);
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"Bought {Describe(netId, movable)}; sent it to the other player.");

            // The pick-up may have happened before this item had an ID, so start the hold here.
            if (Player.Me != null && ReferenceEquals(Player.Me.ActiveMovable, movable))
            {
                LocalTrack track = TakeAuthority(netId, movable);
                track.InHand = true;
            }
        }

        // The container a newly bought item landed in: the one whose spawn point holds it, or the container in the
        // local player's hand when the item is among its contents.
        private const float NearHeldContainer = 0.6f;

        private static Container ContainerHolding(IMovable movable)
        {
            if (movable.OwnerSpawnPoint != null && movable.OwnerSpawnPoint.Owner is Container owner && IsAlive(owner)
                && !ReferenceEquals(owner, movable))
                return owner;
            if (Player.Me != null && Player.Me.ActiveMovable is Container held && IsAlive(held) && !ReferenceEquals(held, movable))
            {
                if (held.MovablesRecursive.Contains(movable))
                    return held;
                if (movable is ProductPart part && held.GetPartsRecursive().Contains(part))
                    return held;
                // Food bought while holding a container always goes into it, but only the first piece gets the
                // container's spawn point; later pieces land on top of it and join the contents a physics step
                // later. So anything that arrived right next to the held container counts as in it.
                if (Vector3.Distance(held.gameObject.transform.position, movable.gameObject.transform.position) < NearHeldContainer)
                    return held;
            }
            return null;
        }

        internal static bool TryGetSpawnKind(IMovable movable, out SpawnKind kind)
        {
            switch (movable)
            {
                case Container _:
                    kind = SpawnKind.Container;
                    return true;
                case MovableTool _:
                    kind = SpawnKind.Tool;
                    return true;
                case DummyMovable _:
                    kind = SpawnKind.Dummy;
                    return true;
                case ProductPart _:
                    kind = SpawnKind.ProductPart;
                    return true;
                default:
                    kind = default;
                    return false;
            }
        }

        // Creates the same kind of item the game would, with pay: false so only the buyer is charged.
        internal static IMovable CreateCopy(SpawnKind kind, int assetId, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            IObjectsFactoryService factory = Game.KitchenServices.ObjectsFactory;
            switch (kind)
            {
                case SpawnKind.Container:
                {
                    Container c = factory.CreateContainer(assetId, position, Vector3.one, rotation, pay: false);
                    if (c != null)
                        c.OnCreate(null);
                    return c;
                }
                case SpawnKind.Tool:
                    return factory.CreateTool(assetId, position, rotation, scale, pay: false) as IMovable;
                case SpawnKind.Dummy:
                    return factory.CreateDummy(assetId, position, rotation, pay: false);
                case SpawnKind.ProductPart:
                    return CreateProductPart(assetId, position, rotation);
                default:
                    return null;
            }
        }

        // Mirrors the piece-spawning part of Product.CreateCoroutine, without moving it into a hand.
        private static ProductPart CreateProductPart(int productId, Vector3 position, Quaternion rotation)
        {
            GameObject template = Game.AssetBundles.LoadAsset<GameObject>(AssetBundleType.PARTS, productId.ToString());
            if (template == null)
                return null;
            ProductPart part = Object.Instantiate(template).GetComponent<ProductPart>();
            if (part == null)
                return null;
            MovableBackup.AddBackup(part.gameObject);
            part.transform.SetPositionAndRotation(position, rotation);
            part.transform.localScale = template.transform.lossyScale;
            part.gameObject.SetActive(true);
            ProductGroup group = TemporalSingleton<ProductsManager>.Me.GetProductGroup(new ProductGroup.ID(productId));
            if (group != null)
                part.CopyStatsFromGroup(group);
            return part;
        }

        // ---------- Messages from the other player ----------

        private void OnMessage(int peerId, MessageType type, NetPacketReader reader)
        {
            switch (type)
            {
                case MessageType.ClientReady:
                    if (_session.IsHost)
                    {
                        _pendingReadyScene = reader.GetString(128);
                        _log.LogInfo($"Client is in {_pendingReadyScene}; will send the item table once this copy is there too.");
                    }
                    break;
                case MessageType.ItemRegistry:
                    ApplyRegistry(reader);
                    break;
                case MessageType.HoldStart:
                {
                    int netId = reader.GetInt();
                    _holderName[netId] = reader.GetString(64);
                    OnRemoteHoldStart(netId);
                    break;
                }
                case MessageType.HoldDenied:
                    OnHoldDenied(reader.GetInt());
                    break;
                case MessageType.HoldPose:
                    OnRemotePose(reader, end: false);
                    break;
                case MessageType.HoldEnd:
                    OnRemotePose(reader, end: true);
                    break;
                case MessageType.ContentRest:
                    OnRemotePose(reader, end: true, contentRest: true);
                    break;
                case MessageType.ItemSpawned:
                    OnRemoteSpawn(reader);
                    break;
                case MessageType.ItemSeated:
                    OnRemoteSeated(reader);
                    break;
                case MessageType.ItemRemoved:
                    OnRemoteRemoved(reader.GetInt());
                    break;
                case MessageType.ItemMissing:
                    OnItemMissing(reader.GetInt());
                    break;
                case MessageType.ItemRespawn:
                    OnItemRespawn(reader);
                    break;
                default:
                    // Every other known message type belongs to one of the other syncs.
                    if (System.Enum.IsDefined(typeof(MessageType), type))
                        break;
                    _log.LogWarning($"Unknown message type {(byte)type} from peer {peerId}.");
                    break;
            }
        }

        private void OnRemoteSpawn(NetPacketReader reader)
        {
            int netId = reader.GetInt();
            var kind = (SpawnKind)reader.GetByte();
            int assetId = reader.GetInt();
            Vector3 position = GetVector(reader);
            Quaternion rotation = GetQuaternion(reader);
            Vector3 scale = GetVector(reader);
            int holderId = reader.GetInt();
            Vector3 localPosition = Vector3.zero;
            Quaternion localRotation = Quaternion.identity;
            if (holderId >= 0)
            {
                localPosition = GetVector(reader);
                localRotation = GetQuaternion(reader);
            }
            IMovable holder = null;
            if (holderId >= 0 && _byNetId.TryGetValue(holderId, out holder) && IsAlive(holder))
            {
                Transform ht = holder.gameObject.transform;
                position = ht.TransformPoint(localPosition);
                rotation = ht.rotation * localRotation;
            }
            else
            {
                holder = null;
            }

            if (_registryScene == null || Player.Me == null)
            {
                _log.LogWarning($"Other player bought item {netId}, but this copy isn't synced to a kitchen yet.");
                return;
            }

            IMovable copy = CreateCopy(kind, assetId, position, rotation, scale);
            if (!IsAlive(copy))
            {
                _log.LogWarning($"Couldn't create a copy of the other player's {kind} (asset {assetId}).");
                return;
            }
            _byNetId[netId] = copy;
            _netIdOf[copy] = netId;
            // The container is in the other player's hands here: carry the new item with it.
            if (holder != null && _remoteHeld.TryGetValue(holderId, out RemoteHeldItem carried) && carried.Main != null)
            {
                PinContent(carried, copy, new HashSet<Transform>());
                _log.LogInfo($"Other player bought {Describe(netId, copy)} into item {holderId} in their hands; created a copy there.");
                return;
            }
            _log.LogInfo($"Other player bought {Describe(netId, copy)}; created a copy here.");
        }

        private void OnRemoteRemoved(int netId)
        {
            if (_remoteHeld.TryGetValue(netId, out RemoteHeldItem item))
            {
                ReleaseRemoteHeld(item, snap: false);
                _remoteHeld.Remove(netId);
            }
            bool known = _byNetId.TryGetValue(netId, out IMovable m) && IsAlive(m);
            if (known)
            {
                try
                {
                    _applyingRemoval = true;
                    Singleton<ActionsManager>.Me.Remove(m);
                }
                finally
                {
                    _applyingRemoval = false;
                }
            }
            ForgetItem(netId);
            if (known)
                _log.LogInfo($"Other player removed item {netId}; removed it here too.");
        }

        // The other player served this plate: it left the kitchen on their copy, so it goes here too, with the food on it.
        public void RemoveServedPlate(int netId)
        {
            if (!_byNetId.TryGetValue(netId, out IMovable plate) || !IsAlive(plate))
            {
                _log.LogWarning($"Other player served plate {netId}, which this copy couldn't match.");
                return;
            }
            // Food sits on the plate as parts (GetPartsRecursive), not only as movables; both go.
            var parts = new List<IMovable>();
            if (plate is Container container)
            {
                parts.AddRange(container.MovablesRecursive);
                foreach (ProductPart food in container.GetPartsRecursive())
                {
                    if (!parts.Contains(food))
                        parts.Add(food);
                }
            }
            parts.Add(plate);
            try
            {
                _applyingRemoval = true;
                foreach (IMovable part in parts)
                {
                    if (!IsAlive(part))
                        continue;
                    if (_netIdOf.TryGetValue(part, out int partId))
                    {
                        if (_remoteHeld.TryGetValue(partId, out RemoteHeldItem held))
                        {
                            ReleaseRemoteHeld(held, snap: false);
                            _remoteHeld.Remove(partId);
                        }
                        ForgetItem(partId);
                    }
                    Singleton<ActionsManager>.Me.Remove(part);
                }
            }
            finally
            {
                _applyingRemoval = false;
            }
            _log.LogInfo($"Other player served plate {netId}; removed it here ({parts.Count - 1} items on it).");
        }

        private void OnRemoteHoldStart(int netId)
        {
            if (!_byNetId.TryGetValue(netId, out IMovable m) || !IsAlive(m))
            {
                _log.LogWarning($"Other player picked up item {netId}, which this copy couldn't match.");
                RequestMissing(netId);
                return;
            }
            if (_remoteHeld.ContainsKey(netId))
                return;
            // Both grabbed it at nearly the same moment and the other player's grab got here after ours.
            if (_localTracks.TryGetValue(netId, out LocalTrack mine) && mine.InHand)
            {
                if (_session.IsHost)
                {
                    // The host wins: keep it, and tell the client to let go.
                    _session.BeginMessage(MessageType.HoldDenied).Put(netId);
                    _session.SendToAll(DeliveryMethod.ReliableOrdered);
                    return;
                }
                YieldToHost(netId, m);
            }
            // They're in charge of it now.
            _localTracks.Remove(netId);

            Transform main = PoseTransformOf(m);
            Rigidbody mainBody = MainBodyOf(m);
            var item = new RemoteHeldItem
            {
                Movable = m,
                Main = main,
                MainBody = mainBody,
                TargetPosition = main.position,
                TargetRotation = main.rotation,
            };
            // Kinematic: physics stops pushing it around, so it only moves where we put it.
            // No collisions: we teleport it every frame, and a teleport into a plate shoves the plate hard
            // enough to break it on this copy only.
            Freeze(mainBody);
            PinParts(item);
            if (m is ProductPart heldPart && heldPart.JointCorrection != null)
            {
                // Its LateUpdate pulls stretched bones back together, which fights our placement and makes
                // held meat shake. The game parents bones to the hand while carrying, so it's idle there too.
                PauseJoints(item, heldPart.JointCorrection);
            }
            _remoteHeld[netId] = item;

            string carrying = item.ContentCount > 0 ? $", carrying {item.ContentCount} items" : string.Empty;
            _log.LogInfo($"Other player picked up {Describe(netId, m)}{carrying}.");
        }

        private void OnRemotePose(NetPacketReader reader, bool end, bool contentRest = false)
        {
            int netId = reader.GetInt();
            Vector3 position = GetVector(reader);
            Quaternion rotation = GetQuaternion(reader);
            bool endKinematic = false;
            Pose rootPose = default;
            List<string> boneNames = null;
            List<Pose> bonePoses = null;
            List<bool> boneKinematic = null;
            List<bool> boneDetached = null;
            if (end)
            {
                endKinematic = reader.GetBool();
                rootPose = new Pose(GetVector(reader), GetQuaternion(reader));
                int boneCount = reader.GetInt();
                boneNames = new List<string>(boneCount);
                bonePoses = new List<Pose>(boneCount);
                boneKinematic = new List<bool>(boneCount);
                boneDetached = new List<bool>(boneCount);
                for (int i = 0; i < boneCount; i++)
                {
                    boneNames.Add(reader.GetString(256));
                    bonePoses.Add(new Pose(GetVector(reader), GetQuaternion(reader)));
                    boneKinematic.Add(reader.GetBool());
                    boneDetached.Add(reader.GetBool());
                }
            }

            if (contentRest)
            {
                ApplyContentRest(netId, position, rotation, endKinematic, rootPose, boneNames, bonePoses, boneKinematic, boneDetached);
                return;
            }

            // A late pose can arrive after the drop; ignore it.
            if (!_remoteHeld.TryGetValue(netId, out RemoteHeldItem item))
            {
                // Moved by the other player but missing here: ask for it.
                if (!TryGetItem(netId, out _))
                    RequestMissing(netId);
                return;
            }
            // Died here while they hold it (the spatula fell out of the kitchen on this copy): get it back.
            if (!IsAlive(item.Movable) || item.Main == null)
            {
                ReleaseRemoteHeld(item, snap: false);
                _remoteHeld.Remove(netId);
                _log.LogWarning($"Item {netId} vanished here while the other player was holding it; asking them for it.");
                if (_byNetId.TryGetValue(netId, out IMovable dead))
                {
                    _byNetId.Remove(netId);
                    _netIdOf.Remove(dead);
                }
                RequestMissing(netId);
                return;
            }

            item.TargetPosition = position;
            item.TargetRotation = rotation;
            if (end)
            {
                item.MainEndKinematic = endKinematic;
                PlaceAll(item, position, rotation);
                bool bonesLineUp = BonesLineUp(boneNames, item.Bones);
                if (bonesLineUp)
                {
                    // Root first: bones may be its children, and moving it afterwards would drag them along.
                    Transform root = item.Movable.gameObject.transform;
                    if (root != item.Main)
                        root.SetPositionAndRotation(rootPose.position, rootPose.rotation);
                    item.Main.SetPositionAndRotation(position, rotation);
                    for (int i = 0; i < bonePoses.Count; i++)
                    {
                        Rigidbody bone = item.Bones[i];
                        if (bone == null)
                            continue;
                        if (boneDetached[i] && bone.transform.parent != null)
                            bone.transform.SetParent(null, worldPositionStays: true);
                        bone.transform.SetPositionAndRotation(bonePoses[i].position, bonePoses[i].rotation);
                    }
                }
                ReleaseRemoteHeld(item, snap: false);
                if (bonesLineUp)
                {
                    // Bones get the physics state they have on the other copy, not the one they had here at pickup.
                    // A bone still carried in a container the other player holds stays frozen until that lets go.
                    for (int i = 0; i < item.Bones.Count; i++)
                    {
                        if (!IsFrozen(item.Bones[i]))
                            RestoreBody(item.Bones[i], boneKinematic[i]);
                    }
                }
                _remoteHeld.Remove(netId);
                if (!endKinematic)
                {
                    Transform root = item.Movable.gameObject.transform;
                    GuardRest(item.Movable, position, rotation, new Pose(root.position, root.rotation));
                }
                _log.LogInfo($"Other player let go of item {netId}.");
            }
        }

        // Food that rode in the pan the other player just put down: move it to where it really ended up there.
        private void ApplyContentRest(int netId, Vector3 position, Quaternion rotation, bool kinematic, Pose rootPose,
            List<string> boneNames, List<Pose> bonePoses, List<bool> boneKinematic, List<bool> boneDetached)
        {
            if (!_byNetId.TryGetValue(netId, out IMovable m) || !IsAlive(m))
            {
                RequestMissing(netId);
                return;
            }
            Rigidbody main = MainBodyOf(m);
            // Still followed on its own, or still riding something else they hold: those finish it off.
            if (_remoteHeld.ContainsKey(netId) || IsFrozen(main))
                return;
            List<Rigidbody> bones = SecondaryBodiesOf(m);
            bool bonesLineUp = boneNames.Count == bones.Count;
            for (int i = 0; bonesLineUp && i < bones.Count; i++)
                bonesLineUp = bones[i] != null && bones[i].name == boneNames[i];

            Transform root = m.gameObject.transform;
            Transform mainTransform = PoseTransformOf(m);
            if (root != mainTransform)
                root.SetPositionAndRotation(rootPose.position, rootPose.rotation);
            mainTransform.SetPositionAndRotation(position, rotation);
            if (bonesLineUp)
            {
                for (int i = 0; i < bonePoses.Count; i++)
                {
                    if (boneDetached[i] && bones[i].transform.parent != null)
                        bones[i].transform.SetParent(null, worldPositionStays: true);
                    bones[i].transform.SetPositionAndRotation(bonePoses[i].position, bonePoses[i].rotation);
                    RestoreBody(bones[i], boneKinematic[i]);
                }
            }
            RestoreBody(main, kinematic);
            if (!kinematic)
                GuardRest(m, position, rotation, rootPose);
        }

        // Safety net: only move bones if both copies list the same bones in the same order.
        // Moving bones onto each other's spots makes the physics explode (meat falls through the counter).
        private bool BonesLineUp(List<string> names, List<Rigidbody> bones)
        {
            if (names.Count != bones.Count)
            {
                _log.LogWarning($"Other player's item has {names.Count} bones but this copy has {bones.Count}; keeping the bones as they are.");
                return false;
            }
            for (int i = 0; i < names.Count; i++)
            {
                if (bones[i] == null || bones[i].name != names[i])
                {
                    _log.LogWarning($"Bone {i} is '{names[i]}' on the other copy but '{(bones[i] != null ? bones[i].name : "missing")}' here; keeping the bones as they are.");
                    return false;
                }
            }
            return true;
        }

        private void FollowRemotePoses(float deltaTime)
        {
            if (_remoteHeld.Count == 0)
                return;
            float t = 1f - Mathf.Exp(-SmoothingSpeed * deltaTime);
            foreach (RemoteHeldItem item in _remoteHeld.Values)
            {
                if (!IsAlive(item.Movable) || item.Main == null)
                    continue;
                Vector3 position = Vector3.Lerp(item.Main.position, item.TargetPosition, t);
                Quaternion rotation = Quaternion.Slerp(item.Main.rotation, item.TargetRotation, t);
                PlaceAll(item, position, rotation);
            }
        }

        private void ReleaseAllRemoteHeld()
        {
            foreach (RemoteHeldItem item in _remoteHeld.Values)
                ReleaseRemoteHeld(item, snap: false);
            _remoteHeld.Clear();
            _frozen.Clear();
            _pausedJoints.Clear();
        }

        private static void ReleaseRemoteHeld(RemoteHeldItem item, bool snap)
        {
            if (!IsAlive(item.Movable) || item.Main == null)
                return;
            if (snap)
                PlaceAll(item, item.TargetPosition, item.TargetRotation);
            foreach (PinnedPart pinned in item.Pinned)
                Thaw(pinned.Body, null);
            Thaw(item.MainBody, item.MainEndKinematic);
            foreach (JointCorrections joints in item.Joints)
                ResumeJoints(joints);
        }

        // Bodies frozen to follow the other player, with how they were before the first freeze. Food in a carried
        // tray can be frozen twice at once: by the tray and by its own pose updates. If each restored what it saw
        // when it started, the second would "restore" the first one's freeze (no collisions), and the food fell
        // through the counter once the tray was put down. So a body thaws only when the last follower lets go.
        private sealed class FrozenBody
        {
            public int Count;
            public bool Kinematic;
            public bool Collisions;
        }

        private static readonly Dictionary<Rigidbody, FrozenBody> _frozen = new Dictionary<Rigidbody, FrozenBody>();
        private static readonly Dictionary<JointCorrections, KeyValuePair<int, bool>> _pausedJoints =
            new Dictionary<JointCorrections, KeyValuePair<int, bool>>();

        // Kinematic: physics stops pushing it around, so it only moves where we put it.
        // No collisions: we teleport it every frame, and a teleport into a plate shoves the plate hard
        // enough to break it on this copy only.
        private static void Freeze(Rigidbody body)
        {
            if (body == null)
                return;
            if (!_frozen.TryGetValue(body, out FrozenBody f))
                _frozen[body] = f = new FrozenBody { Kinematic = body.isKinematic, Collisions = body.detectCollisions };
            f.Count++;
            body.isKinematic = true;
            body.detectCollisions = false;
        }

        // kinematic: how the other copy says it ends up, or null to go back to how it was here.
        private static void Thaw(Rigidbody body, bool? kinematic)
        {
            if (body == null)
                return;
            if (!_frozen.TryGetValue(body, out FrozenBody f))
                return;
            if (--f.Count > 0)
                return;
            _frozen.Remove(body);
            body.detectCollisions = f.Collisions;
            RestoreBody(body, kinematic ?? f.Kinematic);
        }

        private static bool IsFrozen(Rigidbody body) => body != null && _frozen.ContainsKey(body);

        private static void PauseJoints(RemoteHeldItem item, JointCorrections joints)
        {
            if (joints == null)
                return;
            item.Joints.Add(joints);
            _pausedJoints[joints] = _pausedJoints.TryGetValue(joints, out KeyValuePair<int, bool> p)
                ? new KeyValuePair<int, bool>(p.Key + 1, p.Value)
                : new KeyValuePair<int, bool>(1, joints.enabled);
            joints.enabled = false;
        }

        private static void ResumeJoints(JointCorrections joints)
        {
            if (joints == null || !_pausedJoints.TryGetValue(joints, out KeyValuePair<int, bool> p))
                return;
            if (p.Key > 1)
            {
                _pausedJoints[joints] = new KeyValuePair<int, bool>(p.Key - 1, p.Value);
                return;
            }
            _pausedJoints.Remove(joints);
            joints.enabled = p.Value;
        }

        private static void RestoreBody(Rigidbody body, bool kinematic)
        {
            if (body == null)
                return;
            body.isKinematic = kinematic;
            if (!kinematic)
            {
                // Without this, bodies keep whatever speed physics last gave them and fly off.
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                // Asleep, it stays exactly where the other player left it until something touches it.
                body.Sleep();
            }
        }

        // Everything that must move rigidly with the remote item: its root object (when the main rigidbody
        // is a child), every other rigidbody it owns (meat bones), and for containers whatever sits in them.
        // When the game moves a container itself (Container.OnPick / SetAllAsKinematic) it freezes the contents
        // and carries them; on this copy we do the same by hand.
        private static void PinParts(RemoteHeldItem item)
        {
            var seen = new HashSet<Transform> { item.Main };
            IMovable m = item.Movable;

            Transform root = m.gameObject.transform;
            if (seen.Add(root))
                AddPinned(item, root, null);

            item.Bones = SecondaryBodiesOf(m);
            foreach (Rigidbody body in item.Bones)
            {
                if (seen.Add(body.transform))
                    AddPinned(item, body.transform, body);
            }

            if (m is Container container)
            {
                var contents = new List<IMovable>(container.MovablesRecursive);
                foreach (ProductPart p in container.GetPartsRecursive())
                    contents.Add(p);
                foreach (IMovable content in contents)
                    PinContent(item, content, seen);
            }
        }

        // One item sitting in a carried container, pinned so it moves with it.
        private static void PinContent(RemoteHeldItem item, IMovable content, HashSet<Transform> seen)
        {
            if (!IsAlive(content))
                return;
            Rigidbody body = MainBodyOf(content);
            Transform tr = body != null ? body.transform : content.gameObject.transform;
            if (!seen.Add(tr))
                return;
            item.ContentCount++;
            // Food with bones (fish, meat) must be carried whole: root, main body and every bone.
            // Pinning only the main body left the other bones loose, with collisions on, tugged by
            // joints to a body we teleport every frame; the food was flung away and vanished.
            Transform contentRoot = content.gameObject.transform;
            if (contentRoot != tr && seen.Add(contentRoot))
                AddPinned(item, contentRoot, null);
            AddPinned(item, tr, body);
            foreach (Rigidbody bone in SecondaryBodiesOf(content))
            {
                if (bone != null && seen.Add(bone.transform))
                    AddPinned(item, bone.transform, bone);
            }
            if (content is ProductPart part)
                PauseJoints(item, part.JointCorrection);
        }

        private static void AddPinned(RemoteHeldItem item, Transform transform, Rigidbody body)
        {
            Quaternion inverse = Quaternion.Inverse(item.Main.rotation);
            item.Pinned.Add(new PinnedPart
            {
                Transform = transform,
                Body = body,
                LocalPosition = inverse * (transform.position - item.Main.position),
                LocalRotation = inverse * transform.rotation,
                UnderMain = transform.IsChildOf(item.Main),
            });
            Freeze(body);
        }

        // Parents must be placed before their children, or moving the parent drags the child off its spot.
        // So: parts outside the main body (e.g. its root), then the main body, then parts under it.
        // Within each group the list is already parent-first (GetComponentsInChildren order).
        private static void PlaceAll(RemoteHeldItem item, Vector3 position, Quaternion rotation)
        {
            PlacePinned(item, position, rotation, underMain: false);
            item.Main.SetPositionAndRotation(position, rotation);
            PlacePinned(item, position, rotation, underMain: true);
        }

        private static void PlacePinned(RemoteHeldItem item, Vector3 position, Quaternion rotation, bool underMain)
        {
            foreach (PinnedPart pinned in item.Pinned)
            {
                if (pinned.Transform == null || pinned.UnderMain != underMain)
                    continue;
                pinned.Transform.SetPositionAndRotation(position + rotation * pinned.LocalPosition, rotation * pinned.LocalRotation);
            }
        }

        // ---------- Serialization helpers ----------

        private static void PutVector(NetDataWriter w, Vector3 v)
        {
            w.Put(v.x);
            w.Put(v.y);
            w.Put(v.z);
        }

        private static Vector3 GetVector(NetDataReader r) => new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());

        private static void PutQuaternion(NetDataWriter w, Quaternion q)
        {
            w.Put(q.x);
            w.Put(q.y);
            w.Put(q.z);
            w.Put(q.w);
        }

        private static Quaternion GetQuaternion(NetDataReader r) => new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
    }
}
