using System.Collections.Generic;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CookingSimMP
{
    // Milestone 3: syncs containers (plates, bowls, pots...) while a player holds them.
    //
    // Matching: the game's own GlobalId is random per copy and never saved (SaveLoadManager.GetGlobalId),
    // so it can't identify the same plate on both machines. Instead the host numbers every container and
    // sends (number, asset ID, position); the client pairs each with its own nearest container of the same
    // asset ID. This only works when both copies loaded the same save and nothing has moved yet.
    //
    // Authority: whoever holds an item sends its pose. The other copy makes it kinematic and follows.
    internal sealed class ItemSync
    {
        private const float SendInterval = 0.05f;
        private const float SettleTime = 2f;          // Keep sending after a drop so the landing syncs too.
        private const float MatchDistance = 0.5f;     // Max metres between host and client copies of an item.
        private const float SmoothingSpeed = 20f;

        private readonly NetSession _session;
        private readonly ManualLogSource _log;

        private readonly Dictionary<int, Container> _byNetId = new Dictionary<int, Container>();
        private readonly Dictionary<Container, int> _netIdOf = new Dictionary<Container, int>();
        private readonly Dictionary<int, RemoteHeldItem> _remoteHeld = new Dictionary<int, RemoteHeldItem>();

        private string _registryScene;       // Scene the current ID table belongs to.
        private string _readySentForScene;   // Client: scene we already asked the host about.
        private string _pendingReadyScene;   // Host: client is waiting for a table for this scene.

        private PlayerHandSpawnPoint _subscribedHand;
        private int _heldId = NoId;
        private int _settlingId = NoId;
        private float _settleTimer;
        private float _sendTimer;

        private const int NoId = -1;

        private sealed class RemoteHeldItem
        {
            public Container Container;
            public Rigidbody Body;
            public bool WasKinematic;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation;
        }

        public ItemSync(NetSession session, ManualLogSource log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.PeerLeft += _ => Reset();
            _session.Stopped += Reset;
        }

        public void Tick(float deltaTime)
        {
            string scene = SceneManager.GetActiveScene().name;
            bool inKitchen = Player.Me != null;

            // A new scene destroys every container, so the old ID table is useless.
            if (_registryScene != null && _registryScene != scene)
                ClearTable();

            SubscribeToHand();

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

            SendLocalPoses(deltaTime);
            FollowRemotePoses(deltaTime);
        }

        // ---------- ID table ----------

        private void BuildAndSendRegistry(string scene)
        {
            ClearTable();
            _registryScene = scene;

            NetDataWriter writer = _session.BeginMessage(MessageType.ItemRegistry);
            writer.Put(scene);
            Container[] containers = UnityEngine.Object.FindObjectsOfType<Container>();
            writer.Put(containers.Length);
            for (int i = 0; i < containers.Length; i++)
            {
                Container c = containers[i];
                _byNetId[i] = c;
                _netIdOf[c] = i;
                writer.Put(i);
                writer.Put(c.AssetId);
                PutVector(writer, c.transform.position);
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"Sent item table: {containers.Length} containers in {scene}.");
        }

        private void ApplyRegistry(NetPacketReader reader)
        {
            string scene = reader.GetString(128);
            int count = reader.GetInt();
            string localScene = SceneManager.GetActiveScene().name;
            if (scene != localScene)
            {
                _log.LogWarning($"Ignoring item table for {scene}; this copy is in {localScene}.");
                return;
            }

            ClearTable();
            _registryScene = scene;

            var unclaimed = new List<Container>(UnityEngine.Object.FindObjectsOfType<Container>());
            int matched = 0;
            for (int i = 0; i < count; i++)
            {
                int netId = reader.GetInt();
                int assetId = reader.GetInt();
                Vector3 position = GetVector(reader);

                Container best = null;
                float bestDistance = MatchDistance;
                foreach (Container c in unclaimed)
                {
                    if (c.AssetId != assetId)
                        continue;
                    float d = Vector3.Distance(c.transform.position, position);
                    if (d <= bestDistance)
                    {
                        best = c;
                        bestDistance = d;
                    }
                }
                if (best == null)
                    continue;

                unclaimed.Remove(best);
                _byNetId[netId] = best;
                _netIdOf[best] = netId;
                matched++;
            }
            _log.LogInfo($"Matched {matched} of {count} host containers ({unclaimed.Count} local containers unmatched).");
        }

        private void ClearTable()
        {
            ReleaseAllRemoteHeld();
            _byNetId.Clear();
            _netIdOf.Clear();
            _registryScene = null;
            _heldId = NoId;
            _settlingId = NoId;
        }

        private void Reset()
        {
            ClearTable();
            _readySentForScene = null;
            _pendingReadyScene = null;
        }

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
            if (!(movable is Container container) || !_netIdOf.TryGetValue(container, out int netId))
                return;
            if (_remoteHeld.ContainsKey(netId))
            {
                _log.LogWarning($"Picked up container {netId} while the other player holds it. Expect a desync.");
                return;
            }

            if (!unregister)
            {
                if (_settlingId != NoId && _settlingId != netId)
                    FinishSettling();
                _settlingId = NoId;
                _heldId = netId;
                _session.BeginMessage(MessageType.HoldStart).Put(netId);
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
                _log.LogInfo($"Picked up container {netId} (asset {container.AssetId}).");
            }
            else if (_heldId == netId)
            {
                _heldId = NoId;
                _settlingId = netId;
                _settleTimer = SettleTime;
                _log.LogInfo($"Let go of container {netId}; syncing where it lands.");
            }
        }

        private void SendLocalPoses(float deltaTime)
        {
            if (_settlingId != NoId)
            {
                _settleTimer -= deltaTime;
                if (_settleTimer <= 0f)
                    FinishSettling();
            }

            _sendTimer += deltaTime;
            if (_sendTimer < SendInterval || !_session.IsConnected)
                return;
            _sendTimer = 0f;

            SendPose(_heldId, MessageType.HoldPose, DeliveryMethod.Sequenced);
            SendPose(_settlingId, MessageType.HoldPose, DeliveryMethod.Sequenced);
        }

        private void FinishSettling()
        {
            SendPose(_settlingId, MessageType.HoldEnd, DeliveryMethod.ReliableOrdered);
            _settlingId = NoId;
        }

        private void SendPose(int netId, MessageType type, DeliveryMethod deliveryMethod)
        {
            if (netId == NoId || !_byNetId.TryGetValue(netId, out Container c) || c == null)
                return;
            NetDataWriter writer = _session.BeginMessage(type);
            writer.Put(netId);
            PutVector(writer, c.transform.position);
            PutQuaternion(writer, c.transform.rotation);
            _session.SendToAll(deliveryMethod);
        }

        // ---------- Remote player holding something ----------

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
                default:
                    _log.LogWarning($"Unknown message type {(byte)type} from peer {peerId}.");
                    break;
            }
        }

        private void OnRemoteHoldStart(int netId)
        {
            if (!_byNetId.TryGetValue(netId, out Container c) || c == null)
            {
                _log.LogWarning($"Other player picked up container {netId}, which this copy couldn't match.");
                return;
            }
            if (_remoteHeld.ContainsKey(netId))
                return;

            Rigidbody body = c.PhysicsController != null ? c.PhysicsController.MoveRigidbody : null;
            var item = new RemoteHeldItem
            {
                Container = c,
                Body = body,
                WasKinematic = body != null && body.isKinematic,
                TargetPosition = c.transform.position,
                TargetRotation = c.transform.rotation,
            };
            // Kinematic: physics stops pushing it around, so it only moves where we put it.
            if (body != null)
                body.isKinematic = true;
            _remoteHeld[netId] = item;
            _log.LogInfo($"Other player picked up container {netId} (asset {c.AssetId}).");
        }

        private void OnRemotePose(NetPacketReader reader, bool end)
        {
            int netId = reader.GetInt();
            Vector3 position = GetVector(reader);
            Quaternion rotation = GetQuaternion(reader);

            // A late pose can arrive after the drop; ignore it.
            if (!_remoteHeld.TryGetValue(netId, out RemoteHeldItem item))
                return;

            item.TargetPosition = position;
            item.TargetRotation = rotation;
            if (end)
            {
                ReleaseRemoteHeld(item, snap: true);
                _remoteHeld.Remove(netId);
                _log.LogInfo($"Other player let go of container {netId}.");
            }
        }

        private void FollowRemotePoses(float deltaTime)
        {
            if (_remoteHeld.Count == 0)
                return;
            float t = 1f - Mathf.Exp(-SmoothingSpeed * deltaTime);
            foreach (RemoteHeldItem item in _remoteHeld.Values)
            {
                if (item.Container == null)
                    continue;
                Transform tr = item.Container.transform;
                tr.position = Vector3.Lerp(tr.position, item.TargetPosition, t);
                tr.rotation = Quaternion.Slerp(tr.rotation, item.TargetRotation, t);
            }
        }

        private void ReleaseAllRemoteHeld()
        {
            foreach (KeyValuePair<int, RemoteHeldItem> pair in _remoteHeld)
                ReleaseRemoteHeld(pair.Value, snap: false);
            _remoteHeld.Clear();
        }

        private static void ReleaseRemoteHeld(RemoteHeldItem item, bool snap)
        {
            if (item.Container == null)
                return;
            if (snap)
                item.Container.transform.SetPositionAndRotation(item.TargetPosition, item.TargetRotation);
            if (item.Body != null)
            {
                item.Body.isKinematic = item.WasKinematic;
                if (!item.WasKinematic)
                {
                    item.Body.velocity = Vector3.zero;
                    item.Body.angularVelocity = Vector3.zero;
                }
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
