using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using UnityEngine;

namespace CookingSimMP
{
    // v0.12: players connect through Steam (SteamNetworkingSockets P2P over Steam's relay network), so nobody needs a
    // VPN, port forwarding or the host's IP. The client connects to the host's SteamID64. Uses the game's own
    // Steamworks.NET and Steam connection (app 641320); both players need Steam running and online.
    //
    // Steam only has reliable and unreliable messages. Every message starts with one byte saying which: 0 = reliable
    // (arrives once, in order), 1 = sequenced (unreliable, with a 4-byte number so an older one arriving after a newer
    // one is dropped, which is what LiteNetLib's Sequenced did for player positions).
    internal sealed class SteamTransport : ITransport
    {
        private const int VirtualPort = 0;
        private const int SendUnreliable = 0;    // k_nSteamNetworkingSend_Unreliable
        private const int SendNoNagle = 1;       // k_nSteamNetworkingSend_NoNagle
        private const int SendReliable = 8;      // k_nSteamNetworkingSend_Reliable
        private const int MaxMessageSize = 512 * 1024;   // k_cbMaxSteamNetworkingSocketsMessageSizeSend
        private const int SendBufferSize = 4 * 1024 * 1024;
        private const float ConnectTimeout = 30f;        // Seconds a client waits for the host before giving up.
        private const int AppClosed = 1000;              // k_ESteamNetConnectionEnd_App_Generic: CSMP closed it on purpose.
        private const byte FrameReliable = 0;
        private const byte FrameSequenced = 1;

        private sealed class Peer
        {
            public int Id;
            public HSteamNetConnection Handle;
            public ulong SteamId;
            public bool Connected;
            public uint SendSeq;
            public uint LastSeq;
            public bool HaveSeq;
            public readonly Queue<byte[]> Outbox = new Queue<byte[]>();   // Reliable messages Steam had no room for yet.
        }

        private readonly ManualLogSource _log;
        private readonly ITransportSink _sink;
        private readonly Dictionary<int, Peer> _peers = new Dictionary<int, Peer>();
        private readonly Dictionary<uint, Peer> _byHandle = new Dictionary<uint, Peer>();
        private readonly List<KeyValuePair<int, PeerLeftReason>> _localDisconnects = new List<KeyValuePair<int, PeerLeftReason>>();
        private readonly IntPtr[] _received = new IntPtr[64];
        // Status changes wait here until Poll(), so NetSession's events still only fire from Poll() even if the game
        // pumps Steam callbacks itself at another point in the frame.
        private readonly Queue<SteamNetConnectionStatusChangedCallback_t> _statusQueue = new Queue<SteamNetConnectionStatusChangedCallback_t>();
        private readonly NetDataReader _reader = new NetDataReader();
        private Callback<SteamNetConnectionStatusChangedCallback_t> _statusChanged;
        private HSteamListenSocket _listen = HSteamListenSocket.Invalid;
        private HSteamNetPollGroup _pollGroup = HSteamNetPollGroup.Invalid;
        private bool _running;
        private bool _isHost;
        private int _nextId;
        private float _connectStarted;
        private ESteamNetworkingAvailability _relayStatus = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Unknown;
        private IntPtr _sendBuffer = IntPtr.Zero;
        private int _sendCapacity;

        public SteamTransport(ManualLogSource log, ITransportSink sink)
        {
            _log = log;
            _sink = sink;
        }

        public TransportKind Kind => TransportKind.Steam;

        public bool IsRunning => _running;

        // ---------- Steam itself ----------

        // This player's SteamID64, or 0 when Steam isn't running for this copy.
        public static ulong LocalSteamId()
        {
            try
            {
                if (SteamAPI.IsSteamRunning())
                    return SteamUser.GetSteamID().m_SteamID;
            }
            catch (Exception)
            {
                // Steam isn't initialised in this copy (not started from Steam).
            }
            return 0;
        }

        // Null when Steam networking can be used, otherwise why not, in words for the player.
        public static string Unavailable()
        {
            try
            {
                if (!SteamAPI.IsSteamRunning())
                    return "Steam isn't running. Start Cooking Simulator from Steam, or use Direct IP.";
                if (SteamUser.GetSteamID().m_SteamID == 0)
                    return "This copy of the game isn't connected to Steam. Start it from Steam, or use Direct IP.";
                if (!SteamUser.BLoggedOn())
                    return "Steam is offline. Go online in Steam (Steam menu > Go Online), or use Direct IP.";
            }
            catch (Exception e)
            {
                return $"Steam isn't available in this copy of the game ({e.GetType().Name}). Start it from Steam, or use Direct IP.";
            }
            return null;
        }

        internal static string NameOf(ulong steamId)
        {
            try
            {
                string name = SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
                if (!string.IsNullOrEmpty(name) && name != "[unknown]")
                    return $"{name} ({steamId})";
            }
            catch (Exception)
            {
                // Name lookup is only for the log.
            }
            return steamId.ToString();
        }

        // ---------- Starting and stopping ----------

        private void Begin(bool host)
        {
            if (_statusChanged == null)
                _statusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
            CallbackCheck.Install(_log);
            // Fetching the relay network config takes a few seconds the first time; start it right away.
            SteamNetworkingUtils.InitRelayNetworkAccess();
            // Steam's default send buffer is 512 KB, less than one world send; raise it so most worlds go out in one
            // frame. Anything beyond it waits in the peer's outbox (FlushOutboxes).
            try
            {
                IntPtr value = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    Marshal.WriteInt32(value, SendBufferSize);
                    SteamNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                        ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                        ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, value);
                }
                finally
                {
                    Marshal.FreeHGlobal(value);
                }
            }
            catch (Exception e)
            {
                _log.LogWarning($"Couldn't raise Steam's send buffer: {e.Message}");
            }
            _relayStatus = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Unknown;
            _pollGroup = SteamNetworkingSockets.CreatePollGroup();
            _isHost = host;
            _nextId = 0;
            _running = true;
        }

        // False if Steam wouldn't open a listen socket.
        public bool StartHost()
        {
            Begin(host: true);
            _listen = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
            if (_listen == HSteamListenSocket.Invalid)
            {
                Stop();
                return false;
            }
            _log.LogInfo($"Hosting through Steam as {NameOf(LocalSteamId())}.");
            return true;
        }

        // False if Steam wouldn't create the connection.
        public bool Join(ulong hostSteamId)
        {
            Begin(host: false);
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(hostSteamId));
            HSteamNetConnection handle = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
            if (handle == HSteamNetConnection.Invalid)
            {
                Stop();
                return false;
            }
            Add(handle, hostSteamId);
            _connectStarted = Time.realtimeSinceStartup;
            _log.LogInfo($"Connecting through Steam to {NameOf(hostSteamId)}...");
            return true;
        }

        public void Stop()
        {
            if (!_running)
                return;
            foreach (Peer peer in _peers.Values)
                SteamNetworkingSockets.CloseConnection(peer.Handle, AppClosed, "CSMP stopped", true);
            _peers.Clear();
            _byHandle.Clear();
            _localDisconnects.Clear();
            _statusQueue.Clear();
            if (_listen != HSteamListenSocket.Invalid)
                SteamNetworkingSockets.CloseListenSocket(_listen);
            _listen = HSteamListenSocket.Invalid;
            if (_pollGroup != HSteamNetPollGroup.Invalid)
                SteamNetworkingSockets.DestroyPollGroup(_pollGroup);
            _pollGroup = HSteamNetPollGroup.Invalid;
            _running = false;
        }

        // Closes one connection. Like LiteNetLib, the "peer left" event follows in the next Poll().
        public void Disconnect(int peerId)
        {
            if (!_peers.TryGetValue(peerId, out Peer peer))
                return;
            // Linger so the last reliable messages (such as the version mismatch hello) still arrive.
            SteamNetworkingSockets.CloseConnection(peer.Handle, AppClosed, "CSMP disconnect", true);
            Remove(peer);
            _localDisconnects.Add(new KeyValuePair<int, PeerLeftReason>(peerId, PeerLeftReason.LocalStop));
        }

        private Peer Add(HSteamNetConnection handle, ulong steamId)
        {
            var peer = new Peer { Id = _nextId++, Handle = handle, SteamId = steamId };
            _peers[peer.Id] = peer;
            _byHandle[handle.m_HSteamNetConnection] = peer;
            SteamNetworkingSockets.SetConnectionPollGroup(handle, _pollGroup);
            return peer;
        }

        private void Remove(Peer peer)
        {
            _peers.Remove(peer.Id);
            _byHandle.Remove(peer.Handle.m_HSteamNetConnection);
        }

        // ---------- Connection state ----------

        private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t change)
        {
            if (_running)
                _statusQueue.Enqueue(change);
        }

        private void HandleStatus(SteamNetConnectionStatusChangedCallback_t change)
        {
            HSteamNetConnection handle = change.m_hConn;
            SteamNetConnectionInfo_t info = change.m_info;
            _byHandle.TryGetValue(handle.m_HSteamNetConnection, out Peer peer);
            switch (info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    // Host: a player is knocking on our listen socket.
                    if (peer != null || !_isHost || info.m_hListenSocket != _listen)
                        return;
                    ulong steamId = info.m_identityRemote.GetSteamID().m_SteamID;
                    _log.LogInfo($"Connection request from {NameOf(steamId)} through Steam.");
                    EResult result = SteamNetworkingSockets.AcceptConnection(handle);
                    if (result != EResult.k_EResultOK)
                    {
                        _log.LogWarning($"Couldn't accept the connection from {steamId}: {result}.");
                        SteamNetworkingSockets.CloseConnection(handle, AppClosed, "accept failed", false);
                        return;
                    }
                    Add(handle, steamId);
                    return;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (peer == null || peer.Connected)
                        return;
                    peer.Connected = true;
                    _log.LogInfo($"Steam connection to {NameOf(peer.SteamId)} is up ({Describe(handle)}).");
                    _sink.OnPeerConnected(peer.Id);
                    return;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    // Steam keeps the handle until we close it too, whether or not we knew it.
                    SteamNetworkingSockets.CloseConnection(handle, 0, null, false);
                    if (peer == null)
                        return;
                    Remove(peer);
                    bool byPeer = info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;
                    PeerLeftReason reason = Classify(byPeer, info.m_eEndReason, peer.Connected);
                    string detail = $"{(byPeer ? "closed by the other side" : "problem detected here")}, Steam end reason {info.m_eEndReason}: {info.m_szEndDebug}";
                    // The host never told anyone about a player who didn't finish connecting.
                    if (peer.Connected || !_isHost)
                        _sink.OnPeerDisconnected(peer.Id, reason, detail);
                    else
                        _log.LogInfo($"A Steam connection from {peer.SteamId} ended before it was up ({detail}).");
                    return;
            }
        }

        // Turns a Steam end reason (ESteamNetConnectionEnd) into what NetSession explains on screen.
        private static PeerLeftReason Classify(bool byPeer, int endReason, bool wasConnected)
        {
            if (endReason >= 1000 && endReason < 3000)     // App_* and AppException_*: the other copy closed it.
                return byPeer ? (wasConnected ? PeerLeftReason.RemoteClosed : PeerLeftReason.Rejected) : PeerLeftReason.LocalStop;
            switch (endReason)
            {
                case 4001:   // Remote_Timeout
                case 5003:   // Misc_Timeout
                    return wasConnected ? PeerLeftReason.Timeout : PeerLeftReason.ConnectFailed;
                case 5010:   // Misc_PeerSentNoConnection: nobody listening there (not hosting).
                    return PeerLeftReason.ConnectFailed;
                case 3001:   // Local_OfflineMode
                case 3002:   // Local_ManyRelayConnectivity
                case 3003:   // Local_HostedServerPrimaryRelay
                case 3004:   // Local_NetworkConfig
                case 3005:   // Local_Rights
                case 5004:   // Misc_SteamConnectivity
                case 5005:   // Misc_NoRelaySessionsToClient
                    return PeerLeftReason.SteamProblem;
                case 3006:   // Local_P2P_ICE_NoPublicAddresses
                case 4007:   // Remote_P2P_ICE_NoPublicAddresses
                case 5008:   // Misc_P2P_Rendezvous
                case 5009:   // Misc_P2P_NAT_Firewall
                    return PeerLeftReason.Unreachable;
                case 4002:   // Remote_BadCrypt
                case 4003:   // Remote_BadCert
                case 4006:   // Remote_BadProtocolVersion
                    return PeerLeftReason.Rejected;
            }
            if (byPeer)
                return wasConnected ? PeerLeftReason.RemoteClosed : PeerLeftReason.Rejected;
            return wasConnected ? PeerLeftReason.Timeout : PeerLeftReason.ConnectFailed;
        }

        // "relayed" or "direct", and the ping, for the log.
        private static string Describe(HSteamNetConnection handle)
        {
            try
            {
                if (SteamNetworkingSockets.GetConnectionInfo(handle, out SteamNetConnectionInfo_t info))
                {
                    bool relayed = (info.m_nFlags & 4) != 0;   // k_nSteamNetworkConnectionInfoFlags_Relayed
                    return relayed ? "through Steam's relay" : "direct";
                }
            }
            catch (Exception)
            {
                // Only for the log.
            }
            return "route unknown";
        }

        // ---------- Sending ----------

        public void Send(int peerId, NetDataWriter writer, DeliveryMethod method)
        {
            if (_peers.TryGetValue(peerId, out Peer peer) && peer.Connected)
                SendOne(peer, writer, method);
        }

        public void SendToAll(NetDataWriter writer, DeliveryMethod method)
        {
            foreach (Peer peer in _peers.Values)
            {
                if (peer.Connected)
                    SendOne(peer, writer, method);
            }
        }

        private void SendOne(Peer peer, NetDataWriter writer, DeliveryMethod method)
        {
            bool sequenced = method == DeliveryMethod.Sequenced || method == DeliveryMethod.Unreliable;
            int header = sequenced ? 5 : 1;
            int size = header + writer.Length;
            if (size > MaxMessageSize)
            {
                byte type = writer.Length > 0 ? writer.Data[0] : (byte)0;
                _log.LogError($"Message {(MessageType)type} is {size} bytes, more than Steam's {MaxMessageSize}-byte limit; it wasn't sent.");
                return;
            }
            EnsureSendBuffer(size);
            Marshal.WriteByte(_sendBuffer, 0, sequenced ? FrameSequenced : FrameReliable);
            if (sequenced)
            {
                Marshal.WriteInt32(_sendBuffer, 1, unchecked((int)++peer.SendSeq));
                Marshal.Copy(writer.Data, 0, _sendBuffer + header, writer.Length);
                // Unreliable sends can fail while the route is still being set up; the next position follows anyway.
                SteamNetworkingSockets.SendMessageToConnection(peer.Handle, _sendBuffer, (uint)size, SendUnreliable | SendNoNagle, out long _);
                return;
            }
            // Reliable messages wait behind any already queued, so they still arrive in order.
            if (peer.Outbox.Count > 0)
            {
                peer.Outbox.Enqueue(Frame(writer));
                return;
            }
            Marshal.Copy(writer.Data, 0, _sendBuffer + header, writer.Length);
            EResult result = SteamNetworkingSockets.SendMessageToConnection(peer.Handle, _sendBuffer, (uint)size, SendReliable, out long _);
            if (result == EResult.k_EResultLimitExceeded)
            {
                // Steam's send buffer is full (a world send is several 32 KB chunks in one frame). Keep the message
                // and send it from Poll() once Steam has room, instead of dropping it (v0.12.0's stuck download).
                _log.LogInfo($"Steam's send buffer to peer {peer.Id} is full; queueing messages until it has room.");
                peer.Outbox.Enqueue(Frame(writer));
            }
            else if (result != EResult.k_EResultOK)
                _log.LogWarning($"Steam didn't send a message to peer {peer.Id}: {result}.");
        }

        // A reliable message with its header, copied so it can wait in a peer's outbox.
        private static byte[] Frame(NetDataWriter writer)
        {
            var framed = new byte[1 + writer.Length];
            framed[0] = FrameReliable;
            Buffer.BlockCopy(writer.Data, 0, framed, 1, writer.Length);
            return framed;
        }

        private void EnsureSendBuffer(int size)
        {
            if (size <= _sendCapacity)
                return;
            if (_sendBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_sendBuffer);
            _sendCapacity = Math.Max(size, 4096);
            _sendBuffer = Marshal.AllocHGlobal(_sendCapacity);
        }

        // Sends what each peer's outbox holds, in order, until Steam's buffer is full again.
        private void FlushOutboxes()
        {
            foreach (Peer peer in _peers.Values)
            {
                if (peer.Outbox.Count == 0 || !peer.Connected)
                    continue;
                while (peer.Outbox.Count > 0)
                {
                    byte[] framed = peer.Outbox.Peek();
                    EnsureSendBuffer(framed.Length);
                    Marshal.Copy(framed, 0, _sendBuffer, framed.Length);
                    EResult result = SteamNetworkingSockets.SendMessageToConnection(peer.Handle, _sendBuffer, (uint)framed.Length, SendReliable, out long _);
                    if (result == EResult.k_EResultLimitExceeded)
                        break;
                    peer.Outbox.Dequeue();
                    if (result != EResult.k_EResultOK)
                        _log.LogWarning($"Steam didn't send a queued message to peer {peer.Id}: {result}.");
                }
                if (peer.Outbox.Count == 0)
                    _log.LogInfo($"Sent everything queued for peer {peer.Id}.");
            }
        }

        // ---------- Receiving ----------

        public void Poll()
        {
            if (!_running)
                return;
            // Accepting and closing connections are Steam callbacks, which only fire when callbacks are pumped.
            // Running them a second time in a frame when the game also does is harmless.
            CallbackCheck.RunOurs();
            while (_running && _statusQueue.Count > 0)
            {
                try
                {
                    HandleStatus(_statusQueue.Dequeue());
                }
                catch (Exception e)
                {
                    _log.LogError($"Steam connection update failed: {e}");
                }
            }
            if (!_running)
                return;
            LogRelayStatus();

            if (!_isHost && Time.realtimeSinceStartup - _connectStarted > ConnectTimeout)
            {
                foreach (Peer peer in new List<Peer>(_peers.Values))
                {
                    if (peer.Connected)
                        continue;
                    SteamNetworkingSockets.CloseConnection(peer.Handle, AppClosed, "timed out", false);
                    Remove(peer);
                    _sink.OnPeerDisconnected(peer.Id, PeerLeftReason.ConnectFailed, $"no answer within {ConnectTimeout:0} seconds");
                }
            }

            while (_running && _pollGroup != HSteamNetPollGroup.Invalid)
            {
                int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, _received, _received.Length);
                if (count <= 0)
                    break;
                for (int i = 0; i < count; i++)
                {
                    IntPtr pointer = _received[i];
                    try
                    {
                        if (_running)
                            Deliver(SteamNetworkingMessage_t.FromIntPtr(pointer));
                    }
                    catch (Exception e)
                    {
                        _log.LogError($"Reading a Steam message failed: {e}");
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(pointer);
                    }
                }
            }

            FlushOutboxes();

            if (_localDisconnects.Count > 0)
            {
                var left = new List<KeyValuePair<int, PeerLeftReason>>(_localDisconnects);
                _localDisconnects.Clear();
                foreach (KeyValuePair<int, PeerLeftReason> entry in left)
                    _sink.OnPeerDisconnected(entry.Key, entry.Value, "disconnected here");
            }
        }

        private void Deliver(SteamNetworkingMessage_t message)
        {
            if (!_byHandle.TryGetValue(message.m_conn.m_HSteamNetConnection, out Peer peer) || !peer.Connected)
                return;
            int size = message.m_cbSize;
            if (size < 1)
                return;
            var data = new byte[size];
            Marshal.Copy(message.m_pData, data, 0, size);
            int offset = 1;
            if (data[0] == FrameSequenced)
            {
                if (size < 5)
                    return;
                uint seq = BitConverter.ToUInt32(data, 1);
                // Drop anything older than what we already showed (wrap-safe comparison).
                if (peer.HaveSeq && unchecked((int)(seq - peer.LastSeq)) <= 0)
                    return;
                peer.LastSeq = seq;
                peer.HaveSeq = true;
                offset = 5;
            }
            _reader.SetSource(data, offset, size);
            _sink.OnReceive(peer.Id, _reader);
        }

        private void LogRelayStatus()
        {
            ESteamNetworkingAvailability status;
            try
            {
                status = SteamNetworkingUtils.GetRelayNetworkStatus(out SteamRelayNetworkStatus_t _);
            }
            catch (Exception)
            {
                return;
            }
            if (status == _relayStatus)
                return;
            _relayStatus = status;
            _log.LogInfo($"Steam relay network: {status.ToString().Replace("k_ESteamNetworkingAvailability_", "")}.");
        }

        // ---------- Callback pumping ----------

        // For Steam features outside a connection (the friends-list join request in SteamInvites).
        internal static void PumpCallbacks(ManualLogSource log)
        {
            CallbackCheck.Install(log);
            CallbackCheck.RunOurs();
        }

        // The feasibility study couldn't tell whether the game runs SteamAPI.RunCallbacks() itself. CSMP pumps them
        // while Steam networking is on either way, and logs once whether the game was doing it too, so we know.
        private static class CallbackCheck
        {
            private static bool _installed;
            private static bool _inOurs;
            private static int _gameCalls;
            private static int _ourCalls;
            private static bool _reported;

            public static void Install(ManualLogSource log)
            {
                _log = log;
                if (_installed)
                    return;
                _installed = true;
                try
                {
                    new Harmony(Plugin.PluginGuid + ".steamcallbacks").Patch(
                        AccessTools.Method(typeof(SteamAPI), nameof(SteamAPI.RunCallbacks)),
                        prefix: new HarmonyMethod(typeof(CallbackCheck), nameof(CountPrefix)));
                }
                catch (Exception e)
                {
                    log.LogWarning($"Couldn't watch SteamAPI.RunCallbacks (only used for a log line): {e.Message}");
                }
            }

            private static ManualLogSource _log;

            private static void CountPrefix()
            {
                if (!_inOurs)
                    _gameCalls++;
            }

            public static void RunOurs()
            {
                _inOurs = true;
                try
                {
                    SteamAPI.RunCallbacks();
                }
                finally
                {
                    _inOurs = false;
                }
                if (_reported || ++_ourCalls < 300)
                    return;
                _reported = true;
                _log?.LogInfo(_gameCalls > 0
                    ? $"The game pumps Steam callbacks itself ({_gameCalls} calls seen); CSMP pumping them too is harmless."
                    : "The game doesn't pump Steam callbacks itself; CSMP does it while Steam networking is on.");
            }
        }
    }
}
