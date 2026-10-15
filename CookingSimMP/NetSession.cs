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
        }

        public void Poll()
        {
            if (_net.IsRunning)
                _net.PollEvents();
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
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            var type = (MessageType)reader.GetByte();
            switch (type)
            {
                case MessageType.Hello:
                    _log.LogInfo($"Received from peer {peer.Id}: \"{reader.GetString(256)}\"");
                    break;
                default:
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
