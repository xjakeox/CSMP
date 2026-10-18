using LiteNetLib;
using LiteNetLib.Utils;

namespace CookingSimMP
{
    // How the players are connected (v0.12). Steam goes through Steam's relay network with no setup; Direct is the
    // original LiteNetLib UDP connection by IP address (same network, Tailscale, port forwarding, or two copies on one PC).
    internal enum TransportKind { Steam, Direct }

    // Why a connection ended, in words both transports can map to.
    internal enum PeerLeftReason
    {
        LocalStop,        // This copy disconnected (F12 or Stop); no message needed.
        Timeout,          // The other side stopped answering.
        RemoteClosed,     // The other side closed the connection.
        ConnectFailed,    // Nobody answered while connecting.
        Unreachable,      // The address or player can't be reached from here.
        UnknownHost,      // The address isn't one this PC can resolve.
        Rejected,         // The other side refused the connection.
        SteamProblem,     // Steam itself is offline or couldn't set up the route.
        Other,
    }

    // What a transport reports back to NetSession. Every call happens inside ITransport.Poll(), on Unity's main thread.
    internal interface ITransportSink
    {
        void OnPeerConnected(int peerId);
        // detail: extra words for the log (the transport's own reason code).
        void OnPeerDisconnected(int peerId, PeerLeftReason reason, string detail);
        // The reader is only valid during the call.
        void OnReceive(int peerId, NetDataReader reader);
    }

    // One way of moving messages between the host and its clients. NetSession owns the hello, the version check and
    // everything above that; a transport only connects, sends and receives.
    internal interface ITransport
    {
        TransportKind Kind { get; }
        bool IsRunning { get; }
        // ReliableOrdered (and every other reliable mode) arrive once and in order. Sequenced may be dropped but is
        // never delivered after a newer one.
        void Send(int peerId, NetDataWriter writer, DeliveryMethod method);
        void SendToAll(NetDataWriter writer, DeliveryMethod method);
        void Disconnect(int peerId);
        void Poll();
        void Stop();
    }
}
