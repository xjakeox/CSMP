using System.Net;
using System.Net.Sockets;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CookingSimMP
{
    // The original connection (v0.1 to v0.11): LiteNetLib over UDP to an IP address and port. Kept in v0.12 as
    // "Direct IP" for LAN, Tailscale and two copies on one PC, which Steam can't do on a single account.
    internal sealed class DirectTransport : ITransport
    {
        private const string ConnectionKey = "CSMP-v1";

        private readonly ManualLogSource _log;
        private readonly ITransportSink _sink;
        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly NetManager _net;

        public DirectTransport(ManualLogSource log, ITransportSink sink)
        {
            _log = log;
            _sink = sink;
            _net = new NetManager(_listener);
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += peer => _sink.OnPeerConnected(peer.Id);
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
            _listener.NetworkErrorEvent += OnNetworkError;
        }

        public TransportKind Kind => TransportKind.Direct;

        public bool IsRunning => _net.IsRunning;

        // False if the UDP port is already taken.
        public bool StartHost(int port) => _net.Start(port);

        // False if no local UDP port could be opened. Throws if the address can't be used.
        public bool Join(string address, int port)
        {
            if (!_net.Start())
                return false;
            if (_net.Connect(address, port, ConnectionKey) == null)
                throw new System.Exception("no connection was created");
            return true;
        }

        public void Send(int peerId, NetDataWriter writer, DeliveryMethod method)
        {
            NetPeer peer = _net.GetPeerById(peerId);
            if (peer != null)
                peer.Send(writer, method);
        }

        public void SendToAll(NetDataWriter writer, DeliveryMethod method) => _net.SendToAll(writer, method);

        public void Disconnect(int peerId) => _net.GetPeerById(peerId)?.Disconnect();

        public void Poll()
        {
            if (_net.IsRunning)
                _net.PollEvents();
        }

        public void Stop()
        {
            if (_net.IsRunning)
                _net.Stop();
        }

        private void OnConnectionRequest(ConnectionRequest request)
        {
            _log.LogInfo($"Connection request from {request.RemoteEndPoint}.");
            request.AcceptIfKey(ConnectionKey);
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            PeerLeftReason reason;
            switch (info.Reason)
            {
                case DisconnectReason.DisconnectPeerCalled: reason = PeerLeftReason.LocalStop; break;
                case DisconnectReason.Timeout: reason = PeerLeftReason.Timeout; break;
                case DisconnectReason.RemoteConnectionClose: reason = PeerLeftReason.RemoteClosed; break;
                case DisconnectReason.ConnectionFailed: reason = PeerLeftReason.ConnectFailed; break;
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable: reason = PeerLeftReason.Unreachable; break;
                case DisconnectReason.UnknownHost: reason = PeerLeftReason.UnknownHost; break;
                case DisconnectReason.ConnectionRejected:
                case DisconnectReason.InvalidProtocol: reason = PeerLeftReason.Rejected; break;
                default: reason = PeerLeftReason.Other; break;
            }
            _sink.OnPeerDisconnected(peer.Id, reason, info.Reason.ToString());
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            _sink.OnReceive(peer.Id, reader);
            reader.Recycle();
        }

        private void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            _log.LogError($"Network error with {endPoint}: {socketError}.");
        }
    }
}
