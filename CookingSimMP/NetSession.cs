using System;
using System.Collections.Generic;
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
        InteractReady = 10,    // Client -> host: "I'm settled in this kitchen, send me every door and switch."
        InteractSnapshot = 11, // Host -> client: state of every door, drawer, knob and switch (host wins).
        InteractChange = 12,   // "I opened/closed/switched/pressed this interactable."
        CookState = 13,        // Host -> client: doneness of food that changed (4 times a second).
        PauseState = 14,       // Host -> client: "My clock is stopped/running again" (pause menu or other).
        PauseRequest = 15,     // "<name> is asking to pause." Client -> host, then host -> every client.
        MoneyReady = 16,       // Client -> host: "I'm in this kitchen, send me the shared balance."
        MoneyRequest = 17,     // Client -> host: "My cash changed by this much; apply it to the shared wallet."
        MoneyChange = 18,      // Host -> client: "The wallet changed by this much and now holds this balance."
        CutDone = 19,          // "I cut this food item with this plane; the pieces got these IDs."
        ContentsState = 20,    // "This pan's/pot's contents (or this food's seasoning) are now this."
        PaintStrokes = 21,     // "I stained or cleaned these spots on these surfaces."
        HoldDenied = 22,       // Host -> client: "I'm already holding that item; let go of it."
        LogMarker = 23,        // "Write test marker N into your log too" (F8).
        MopState = 24,         // "My mop head is here" (while mopping), or "I put the mop down here".
        ContentRest = 25,      // "The pan/tray I carried came to rest; this food in it ended up exactly here."
        WorldRequest = 26,     // Client -> host: "Versions match; send me your world." Carries the player's name.
        WorldBegin = 27,       // Host -> client: "My world follows": size, kitchen, game mode and content IDs.
        WorldChunk = 28,       // Host -> client: one piece of the world save.
        WorldLoaded = 29,      // Client -> host: "I've loaded your world and I'm in the kitchen."
        WorldRefused = 30,     // Client -> host: "I can't load your world" (missing DLC), with the reason.
        WorldLeaving = 31,     // Host -> client: "I left my kitchen; you'll get the next one when I'm in it."
        OrderList = 32,        // Host -> client: every open customer order with its timer, plus fame (twice a second).
        OrderEnded = 33,       // Either way: "This order was served (or failed) here", with the served plate's ID.
        KitchenClosed = 34,    // Host -> client: "No more customers today; skipping to cleaning."
        OrderRequest = 35,     // Client -> host (sandbox only): "Start this recipe as an order", from the client's tablet.
        TimerState = 36,       // Host -> client: a kitchen timer is idle, counting (with the time left) or ringing.
        TimerRequest = 37,     // Client -> host: "I set, cancelled or silenced this kitchen timer."
        ItemBroken = 38,       // Either way: "This item broke here", with where it was, so the other copy breaks it too.
        ItemSeated = 39,       // Either way: where a just-bought item settled inside the container it was bought into.
        ContentsAdd = 40,      // Client -> host: "I poured this much of this into these pots"; the host merges it (v0.10).
        StackCount = 41,       // Either way: "This stack now holds this many put-back items" (free next pick-up).
        DayClock = 42,         // Host -> client: the day's time and phase, once a second and on every change.
        SkipRequest = 43,      // Client -> host: "<name> is asking to skip ahead" (TV skip buttons).
        OrderClaim = 44,       // Either way: "I started (or stopped) checking out this order."
        ServeScored = 45,      // Client -> host: "I served this order; count it in your save" (recipe history, cooking points).
        StandState = 46,       // Host -> client: condition, broken and repair state of appliances and breakables that changed.
        StandHit = 47,         // Client -> host: "This appliance took this much damage from a knock here."
        RepairStart = 48,      // Either way: "I called a repair for this appliance with this company."
        RepairStop = 49,       // Either way: "I stopped the repair of this appliance."
        PropBroken = 50,       // Either way: "This window, shelf, TV or lamp broke (or cracked, or was fixed) here."
        PieceList = 51,        // Host -> client, after the item table: every broken piece lying around, to rebuild (v0.10.1).
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
        private bool _stopAfterPoll;
        private bool _verified;          // Client: the host answered our hello with the same version.
        private string _joinTarget;      // Client: "address:port" we're connecting to, for error messages.
        private bool _problemShown;      // Client: a clearer reason is already on screen; don't replace it with the disconnect.
        private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();

        public event Action<int, PlayerState> PlayerStateReceived;
        public event Action<int> PeerJoined;
        public event Action<int> PeerLeft;
        // The other copy runs the same mod version (checked in Hello). The client then asks for the world.
        public event Action<int> PeerVerified;
        // A new world is being sent (host) or loaded (client): every sync drops what it knew about the old one.
        public event Action WorldReset;
        // While a peer is loading a new world, its messages about the old one are dropped. Set by WorldSync.
        public Func<int, MessageType, bool> DropMessage;
        public event Action Stopped;
        // Connecting failed, or the connection was lost: (title, explanation, whether "Try again" makes sense).
        // ConnectWindows shows it on screen (v0.10); before, most of these were only in the log.
        public event Action<string, string, bool> ConnectProblem;
        // Every message type NetSession doesn't handle itself goes here.
        public event Action<int, MessageType, NetPacketReader> MessageReceived;

        public bool IsHost => _isHost;

        public bool IsRunning => _net.IsRunning;

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
            // Joining players load the host's world, so there has to be one.
            if (!WorldSync.HostInKitchen)
            {
                _log.LogWarning("Load a kitchen before hosting.");
                PauseSync.Instance?.Notify("Load a kitchen before hosting");
                return;
            }
            if (!_net.Start(port))
            {
                _log.LogError($"Could not open UDP port {port}. Another copy may already be hosting on it.");
                ConnectProblem?.Invoke("Couldn't start hosting",
                    $"UDP port {port} is already in use. Another copy of the game (or another program) may already be hosting on it. Close it, or change the port in BepInEx\\config\\com.jake.cookingsimmp.cfg.", false);
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
                ConnectProblem?.Invoke("Couldn't connect", "This PC couldn't open a network port to connect from. A firewall or security program may be blocking the game.", true);
                return;
            }
            _isHost = false;
            _verified = false;
            _problemShown = false;
            _joinTarget = $"{address}:{port}";
            _log.LogInfo($"Connecting to {address}:{port}...");
            try
            {
                if (_net.Connect(address, port, ConnectionKey) == null)
                    throw new Exception("no connection was created");
            }
            catch (Exception e)
            {
                _log.LogWarning($"Couldn't connect to {address}:{port}: {e.Message}");
                ConnectProblem?.Invoke("Couldn't connect", $"\"{address}\" isn't an address this PC can connect to ({e.Message}). Check the host's IP address and try again.", true);
                _stopAfterPoll = true;
            }
        }

        public void Stop()
        {
            if (!_net.IsRunning)
                return;
            _net.Stop();
            _peers.Clear();
            _log.LogInfo("Disconnected and stopped networking.");
            Stopped?.Invoke();
        }

        // Stop once the current Poll() is done; safe to call from inside a message handler.
        public void StopAfterPoll() => _stopAfterPoll = true;

        public void RaiseWorldReset() => WorldReset?.Invoke();

        // Send the current message to one peer only.
        public void SendTo(int peerId, DeliveryMethod deliveryMethod)
        {
            if (_peers.TryGetValue(peerId, out NetPeer peer))
                peer.Send(_writer, deliveryMethod);
        }

        public void Poll()
        {
            if (_net.IsRunning)
                _net.PollEvents();
            // Stopping from inside an event callback isn't safe, so it waits until polling is done.
            if (_stopAfterPoll)
            {
                _stopAfterPoll = false;
                Stop();
            }
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
            _writer.Put(Plugin.PluginVersion);
            peer.Send(_writer, DeliveryMethod.ReliableOrdered);
            _peers[peer.Id] = peer;
            PeerJoined?.Invoke(peer.Id);
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            _log.LogInfo($"Peer {peer.Id} disconnected: {info.Reason}.");
            _peers.Remove(peer.Id);
            PeerLeft?.Invoke(peer.Id);
            // A client with no host left has nothing to do; stop so F10/F11 work again without F12.
            if (!_isHost)
            {
                if (info.Reason == DisconnectReason.ConnectionFailed)
                    _log.LogWarning("Couldn't reach the host. Is the other copy hosting (F10)? Press F11 to try again.");
                ExplainClientDisconnect(info);
                _stopAfterPoll = true;
            }
        }

        // Shows a connection problem on screen. A client that is about to stop because of it (wrong version, missing
        // DLC) keeps this message instead of the plainer disconnect reason that follows.
        public void ReportProblem(string title, string text, bool canRetry)
        {
            if (!_isHost)
                _problemShown = true;
            ConnectProblem?.Invoke(title, text, canRetry);
        }

        // Client: puts the reason the connection ended on screen. Pressing F12 here (DisconnectPeerCalled) needs none.
        private void ExplainClientDisconnect(DisconnectInfo info)
        {
            string target = _joinTarget ?? "the host";
            bool wasIn = _verified;
            _verified = false;
            if (ConnectProblem == null || info.Reason == DisconnectReason.DisconnectPeerCalled || _problemShown)
                return;
            if (wasIn)
            {
                string why = info.Reason == DisconnectReason.Timeout
                    ? "The host stopped answering (its game may have closed, or the network dropped)."
                    : info.Reason == DisconnectReason.RemoteConnectionClose
                        ? "The host stopped hosting or closed the game."
                        : $"The connection ended ({info.Reason}).";
                ConnectProblem("Lost connection to the host", why + " You can join again with F11 once the host is back.", true);
                return;
            }
            string text;
            switch (info.Reason)
            {
                case DisconnectReason.ConnectionFailed:
                case DisconnectReason.Timeout:
                    text = $"Nobody answered at {target}. Check that the host is in a kitchen and has clicked Host (F10), that the IP address is right, and that both PCs are on the same network or VPN (Tailscale). Over the internet, the host needs UDP port forwarding.";
                    break;
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable:
                    text = $"{target} can't be reached from this PC. Check the IP address and that this PC is online (and on the VPN, if you use one).";
                    break;
                case DisconnectReason.UnknownHost:
                    text = $"\"{target}\" isn't a known address. Type the host's IP address, for example 100.101.102.103.";
                    break;
                case DisconnectReason.ConnectionRejected:
                case DisconnectReason.InvalidProtocol:
                    text = $"The host at {target} refused the connection. It may be running a different multiplayer mod or a very old CSMP version.";
                    break;
                case DisconnectReason.RemoteConnectionClose:
                    text = "The host closed the connection while you were joining. If a message about versions or DLC showed up, fix that first.";
                    break;
                default:
                    text = $"The connection to {target} ended before you could join ({info.Reason}).";
                    break;
            }
            ConnectProblem("Couldn't connect", text, true);
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            var type = (MessageType)reader.GetByte();
            switch (type)
            {
                case MessageType.Hello:
                    OnHello(peer, reader);
                    break;
                case MessageType.PlayerState:
                    PlayerStateReceived?.Invoke(peer.Id, PlayerState.Read(reader));
                    break;
                default:
                    if (DropMessage != null && DropMessage(peer.Id, type))
                        break;
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

        // Both copies must run the same mod version; v0.8.5 and older don't send one.
        private void OnHello(NetPeer peer, NetPacketReader reader)
        {
            _log.LogInfo($"Received from peer {peer.Id}: \"{reader.GetString(256)}\"");
            string theirs = reader.AvailableBytes > 0 ? reader.GetString(32) : "0.8.5 or older";
            if (theirs == Plugin.PluginVersion)
            {
                _verified = true;
                PeerVerified?.Invoke(peer.Id);
                return;
            }
            string text = _isHost
                ? $"A player tried to join with CSMP {theirs}; you have {Plugin.PluginVersion}. Both need the same version."
                : $"The host has CSMP {theirs}; you have {Plugin.PluginVersion}. Both need the same version.";
            _log.LogWarning(text);
            PauseSync.Instance?.Notify(text, 10f);
            if (_isHost)
                peer.Disconnect();
            else
            {
                ReportProblem("Different CSMP versions", text + " Install the same CSMP version on both PCs.", false);
                _stopAfterPoll = true;
            }
        }

        private void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            _log.LogError($"Network error with {endPoint}: {socketError}.");
        }
    }
}
