using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CookingSimMP
{
    // First byte of every packet says what the rest of the packet holds.
    internal enum MessageType : byte
    {
        Hello = 1,
        PlayerState = 2,
        ClientReady = 3,   // Client -> host: "I'm in this kitchen, send me the item list."
        ItemRegistry = 4,  // Host -> client: network ID, asset ID and position of every container.
        HoldStart = 5,     // "I picked up this item."
        HoldPose = 6,      // "This item is here now." (sent 20 times per second while held)
        HoldEnd = 7,       // "I let go; here is where it ended up."
        ItemSpawned = 8,   // "I bought this item; make a free copy."
        ItemRemoved = 9,   // "This item is gone (e.g. put back on its stack)."
    }

    // Owns the LiteNetLib connection. Events only fire inside Poll(), which runs on Unity's main thread.
    internal sealed class NetSession
    {
        private const string ConnectionKey = "CSMP-v1";

        public static readonly int ProcessId = Process.GetCurrentProcess().Id;

        private readonly ManualLogSource _log;
        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly NetManager _net;
        private readonly NetDataWriter _writer = new NetDataWriter();
        private bool _isHost;

        public event Action<int, PlayerState> PlayerStateReceived;
        public event Action<int> PeerLeft;
        public event Action Stopped;
        // Every message type NetSession doesn't handle itself goes here.
        public event Action<int, MessageType, NetPacketReader> MessageReceived;

        public bool IsHost => _isHost;

        public bool IsConnected => _net.IsRunning && _net.ConnectedPeersCount > 0;

        public NetSession(ManualLogSource log)
        {
            _log = log;
            _net = new NetManager(_listener);
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
            _listener.NetworkErrorEvent += OnNetworkError;
        }

        private string Role => _isHost ? "host" : "client";

        public void StartHost(int port)
        {
            if (_net.IsRunning)
            {
                _log.LogWarning($"Already running as {Role}. Press F12 to disconnect first.");
                return;
            }
            if (!_net.Start(port))
            {
                _log.LogError($"Could not open UDP port {port}. Another copy may already be hosting on it.");
                return;
            }
            _isHost = true;
            _log.LogInfo($"Hosting on UDP port {port}. Waiting for a player to join...");
        }

        public void Join(string address, int port)
        {
            if (_net.IsRunning)
            {
                _log.LogWarning($"Already running as {Role}. Press F12 to disconnect first.");
                return;
            }
            if (!_net.Start())
            {
                _log.LogError("Could not open a local UDP port to connect from.");
                return;
            }
            _isHost = false;
            _log.LogInfo($"Connecting to {address}:{port}...");
            _net.Connect(address, port, ConnectionKey);
        }

        public void Stop()
        {
            if (!_net.IsRunning)
                return;
            _net.Stop();
            _log.LogInfo("Disconnected and stopped networking.");
            Stopped?.Invoke();
        }

        public void Poll()
        {
            if (_net.IsRunning)
                _net.PollEvents();
        }

        // Sequenced: may drop packets, but never delivers an older position after a newer one.
        public void SendPlayerState(PlayerState state)
        {
            _writer.Reset();
            _writer.Put((byte)MessageType.PlayerState);
            state.Write(_writer);
            _net.SendToAll(_writer, DeliveryMethod.Sequenced);
        }

        // Start a message: write its fields into the returned writer, then call SendToAll.
        public NetDataWriter BeginMessage(MessageType type)
        {
            _writer.Reset();
            _writer.Put((byte)type);
            return _writer;
        }

        public void SendToAll(DeliveryMethod deliveryMethod)
        {
            _net.SendToAll(_writer, deliveryMethod);
        }

        private void OnConnectionRequest(ConnectionRequest request)
        {
            _log.LogInfo($"Connection request from {request.RemoteEndPoint}.");
            request.AcceptIfKey(ConnectionKey);
        }

        private void OnPeerConnected(NetPeer peer)
        {
            _log.LogInfo($"Connected to peer {peer.Id} as {Role}. Sending hello.");
            _writer.Reset();
            _writer.Put((byte)MessageType.Hello);
            _writer.Put($"hello from {Role} (process {ProcessId})");
            peer.Send(_writer, DeliveryMethod.ReliableOrdered);
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            _log.LogInfo($"Peer {peer.Id} disconnected: {info.Reason}.");
            PeerLeft?.Invoke(peer.Id);
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            var type = (MessageType)reader.GetByte();
            switch (type)
            {
                case MessageType.Hello:
                    _log.LogInfo($"Received from peer {peer.Id}: \"{reader.GetString(256)}\"");
                    break;
                case MessageType.PlayerState:
                    PlayerStateReceived?.Invoke(peer.Id, PlayerState.Read(reader));
                    break;
                default:
                    if (MessageReceived != null)
                    {
                        MessageReceived(peer.Id, type, reader);
                        break;
                    }
                    _log.LogWarning($"Unknown message type {(byte)type} from peer {peer.Id}.");
                    break;
            }
            reader.Recycle();
        }

        private void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            _log.LogError($"Network error with {endPoint}: {socketError}.");
        }
    }
}
