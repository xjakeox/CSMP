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
    // Matching: the game's own GlobalId is random per copy and never saved (SaveLoadManager.GetGlobalId),
    // so it can't identify the same item on both machines. Instead the host numbers every item and sends
    // (number, type, asset ID, position); the client pairs each with its own nearest item of the same type
    // and asset ID. This only works when both copies loaded the same save and nothing has moved yet.
    //
    // Authority: whoever last touched an item sends its pose while it moves. The other copy freezes its
    // physics and follows.
    internal sealed class ItemSync
    {
        private const float SendInterval = 0.05f;
        private const float SettleTime = 1f;          // After letting go, keep sending at least this long...
        private const float StillTime = 0.3f;         // ...and until it has stopped moving for this long.
        private const float WatchTime = 60f;          // Keep watching a resting item this long in case the game moves it again.
        private const float WakeDistance = 0.005f;    // A watched item that moves this far (metres)...
        private const float WakeAngle = 2f;           // ...or turns this much (degrees) starts sending again.
        private const float MatchDistance = 0.5f;     // Max metres between host and client copies of an item.
        private const float SmoothingSpeed = 20f;

        // What the other copy has to create when someone buys an item.
        private enum SpawnKind : byte
        {
            Container = 1,
            Tool = 2,
            Dummy = 3,
            ProductPart = 4,
        }

        private readonly NetSession _session;
        private readonly ManualLogSource _log;

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
        }

        private sealed class RemoteHeldItem
        {
            public IMovable Movable;
            public Transform Main;               // The transform whose pose is synced (the item's main rigidbody).
            public Rigidbody MainBody;
            public bool MainWasKinematic;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation;
            public List<PinnedPart> Pinned = new List<PinnedPart>();
            public List<Rigidbody> Bones = new List<Rigidbody>();
            public JointCorrections Joints;      // Paused while held, so it doesn't tug the bones we place.
            public bool JointsWereEnabled;
            public int ContentCount;
        }

        // Anything that has to move rigidly with a remote item: its root object, its physics bones
        // (meat has a ragdoll), and food or items sitting in a container.
        private sealed class PinnedPart
        {
            public Transform Transform;
            public Rigidbody Body;
            public bool WasKinematic;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public bool UnderMain;               // Child of the main transform, so it must be placed after it.
        }

        public ItemSync(NetSession session, ManualLogSource log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.PeerLeft += _ => Reset();
            _session.Stopped += Reset;
            // Stacks (plates, bowls, tools...) create the bought item and put it in the hand.
            Stack.OnBuy += OnStackBuy;
            // Clicking food in a fridge or on a shelf creates a Product that spawns the actual pieces.
            Product.OnBuy += OnProductBuy;
        }

        public void Tick(float deltaTime)
        {
            string scene = SceneManager.GetActiveScene().name;
            bool inKitchen = Player.Me != null;

            // A new scene destroys every item, so the old ID table is useless.
            if (_registryScene != null && _registryScene != scene)
                ClearTable();

            SubscribeToHand();
            WarnIfUnknownStillHeld(deltaTime);

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
        }

        // Runs after every game script's Update, so nothing the game does this frame
        // (e.g. a package snapping its food back into place) can override where we put remote items.
        public void LateTick(float deltaTime)
        {
            FollowRemotePoses(deltaTime);
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
                PutVector(writer, m.gameObject.transform.position);
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"Sent item table: {movables.Count} items of {typeNames.Count} types in {scene}.");
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
            int localTotal = 0;
            foreach (IMovable m in FindSceneMovables())
            {
                string key = TypeKey(m) + "#" + m.AssetId;
                if (!unclaimed.TryGetValue(key, out List<IMovable> list))
                    unclaimed[key] = list = new List<IMovable>();
                list.Add(m);
                localTotal++;
            }

            int count = reader.GetInt();
            int matched = 0;
            var missedByType = new Dictionary<string, int>();
            for (int i = 0; i < count; i++)
            {
                int netId = reader.GetInt();
                string typeName = typeNames[reader.GetInt()];
                int assetId = reader.GetInt();
                Vector3 position = GetVector(reader);

                IMovable best = null;
                float bestDistance = MatchDistance;
                if (unclaimed.TryGetValue(typeName + "#" + assetId, out List<IMovable> candidates))
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

            _log.LogInfo($"Matched {matched} of {count} host items ({localTotal - matched} local items unmatched).");
            foreach (KeyValuePair<string, int> pair in missedByType)
                _log.LogWarning($"  Couldn't match {pair.Value} host item(s) of type {pair.Key}.");
        }

        private void ClearTable()
        {
            ReleaseAllRemoteHeld();
            _byNetId.Clear();
            _netIdOf.Clear();
            _localTracks.Clear();
            _registryScene = null;
            _unknownInHand = null;
        }

        private void Reset()
        {
            ClearTable();
            _readySentForScene = null;
            _pendingReadyScene = null;
            _nextLocalId = 0;
        }

        // IMovable is an interface, so "== null" skips Unity's destroyed-object check; this does it properly.
        private static bool IsAlive(IMovable movable) => movable is Object obj && obj != null;

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
                bodies = m.gameObject.GetComponentsInChildren<Rigidbody>(true);
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
            if (_remoteHeld.ContainsKey(netId))
            {
                _log.LogWarning($"Picked up item {netId} while the other player holds it. Expect a desync.");
                return;
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
                _log.LogWarning($"Picked up {m.GetType().Name} '{m.gameObject.name}' (asset {m.AssetId}), which isn't in the item table, so it won't sync.");
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
                _session.BeginMessage(MessageType.HoldStart).Put(netId);
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

            _scratchIds.Clear();
            _scratchIds.AddRange(_localTracks.Keys);
            foreach (int netId in _scratchIds)
            {
                LocalTrack track = _localTracks[netId];

                // The game removed it while we were in charge, e.g. a plate put back on its stack.
                if (!IsAlive(track.Movable))
                {
                    _session.BeginMessage(MessageType.ItemRemoved).Put(netId);
                    _session.SendToAll(DeliveryMethod.ReliableOrdered);
                    _log.LogInfo($"Item {netId} was removed; told the other player.");
                    ForgetItem(netId);
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
            if (type == MessageType.HoldEnd)
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

            int netId = (_session.IsHost ? 1_000_000 : 2_000_000) + _nextLocalId++;
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
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"Bought {Describe(netId, movable)}; sent it to the other player.");

            // The pick-up may have happened before this item had an ID, so start the hold here.
            if (Player.Me != null && ReferenceEquals(Player.Me.ActiveMovable, movable))
            {
                LocalTrack track = TakeAuthority(netId, movable);
                track.InHand = true;
            }
        }

        private static bool TryGetSpawnKind(IMovable movable, out SpawnKind kind)
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
        private static IMovable CreateCopy(SpawnKind kind, int assetId, Vector3 position, Quaternion rotation, Vector3 scale)
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
                    OnRemoteHoldStart(reader.GetInt());
                    break;
                case MessageType.HoldPose:
                    OnRemotePose(reader, end: false);
                    break;
                case MessageType.HoldEnd:
                    OnRemotePose(reader, end: true);
                    break;
                case MessageType.ItemSpawned:
                    OnRemoteSpawn(reader);
                    break;
                case MessageType.ItemRemoved:
                    OnRemoteRemoved(reader.GetInt());
                    break;
                default:
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
            _log.LogInfo($"Other player bought {Describe(netId, copy)}; created a copy here.");
        }

        private void OnRemoteRemoved(int netId)
        {
            if (_remoteHeld.TryGetValue(netId, out RemoteHeldItem item))
            {
                ReleaseRemoteHeld(item, snap: false);
                _remoteHeld.Remove(netId);
            }
            if (_byNetId.TryGetValue(netId, out IMovable m) && IsAlive(m))
                Singleton<ActionsManager>.Me.Remove(m);
            ForgetItem(netId);
            _log.LogInfo($"Other player removed item {netId}; removed it here too.");
        }

        private void OnRemoteHoldStart(int netId)
        {
            if (!_byNetId.TryGetValue(netId, out IMovable m) || !IsAlive(m))
            {
                _log.LogWarning($"Other player picked up item {netId}, which this copy couldn't match.");
                return;
            }
            if (_remoteHeld.ContainsKey(netId))
                return;
            // They're in charge of it now.
            _localTracks.Remove(netId);

            Transform main = PoseTransformOf(m);
            Rigidbody mainBody = MainBodyOf(m);
            var item = new RemoteHeldItem
            {
                Movable = m,
                Main = main,
                MainBody = mainBody,
                MainWasKinematic = mainBody != null && mainBody.isKinematic,
                TargetPosition = main.position,
                TargetRotation = main.rotation,
            };
            // Kinematic: physics stops pushing it around, so it only moves where we put it.
            if (mainBody != null)
                mainBody.isKinematic = true;
            PinParts(item);
            if (m is ProductPart heldPart && heldPart.JointCorrection != null)
            {
                // Its LateUpdate pulls stretched bones back together, which fights our placement and makes
                // held meat shake. The game parents bones to the hand while carrying, so it's idle there too.
                item.Joints = heldPart.JointCorrection;
                item.JointsWereEnabled = item.Joints.enabled;
                item.Joints.enabled = false;
            }
            _remoteHeld[netId] = item;

            string carrying = item.ContentCount > 0 ? $", carrying {item.ContentCount} items" : string.Empty;
            _log.LogInfo($"Other player picked up {Describe(netId, m)}{carrying}.");
        }

        private void OnRemotePose(NetPacketReader reader, bool end)
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

            // A late pose can arrive after the drop; ignore it.
            if (!_remoteHeld.TryGetValue(netId, out RemoteHeldItem item))
                return;

            item.TargetPosition = position;
            item.TargetRotation = rotation;
            if (end)
            {
                item.MainWasKinematic = endKinematic;
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
                    for (int i = 0; i < item.Bones.Count; i++)
                        RestoreBody(item.Bones[i], boneKinematic[i]);
                }
                _remoteHeld.Remove(netId);
                _log.LogInfo($"Other player let go of item {netId}.");
            }
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
        }

        private static void ReleaseRemoteHeld(RemoteHeldItem item, bool snap)
        {
            if (!IsAlive(item.Movable) || item.Main == null)
                return;
            if (snap)
                PlaceAll(item, item.TargetPosition, item.TargetRotation);
            foreach (PinnedPart pinned in item.Pinned)
                RestoreBody(pinned.Body, pinned.WasKinematic);
            RestoreBody(item.MainBody, item.MainWasKinematic);
            if (item.Joints != null)
                item.Joints.enabled = item.JointsWereEnabled;
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
                foreach (ProductPart p in container.Parts)
                    contents.Add(p);
                foreach (IMovable content in contents)
                {
                    if (!IsAlive(content))
                        continue;
                    Rigidbody body = MainBodyOf(content);
                    Transform tr = body != null ? body.transform : content.gameObject.transform;
                    if (seen.Add(tr))
                    {
                        AddPinned(item, tr, body);
                        item.ContentCount++;
                    }
                }
            }
        }

        private static void AddPinned(RemoteHeldItem item, Transform transform, Rigidbody body)
        {
            Quaternion inverse = Quaternion.Inverse(item.Main.rotation);
            item.Pinned.Add(new PinnedPart
            {
                Transform = transform,
                Body = body,
                WasKinematic = body != null && body.isKinematic,
                LocalPosition = inverse * (transform.position - item.Main.position),
                LocalRotation = inverse * transform.rotation,
                UnderMain = transform.IsChildOf(item.Main),
            });
            if (body != null)
                body.isKinematic = true;
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
