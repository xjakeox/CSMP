using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using Steamworks;
using UnityEngine;

namespace CookingSimMP
{
    // v0.12: joining from Steam itself. While this copy hosts through Steam, it sets the rich presence "connect" key,
    // so friends see "Join Game" on the host in their Steam friends list, and the host can send invites from the
    // Steam overlay (F10 while hosting, or "Host and invite"). Clicking Join Game or an invite:
    // - with the game running, Steam raises GameRichPresenceJoinRequested_t and this copy joins the host;
    // - with the game closed, Steam starts it with "+csmp_join <host Steam ID>" on the command line, and this copy
    //   joins once the main menu is up.
    // A request that arrives in a kitchen waits until the player is back in the main menu.
    internal sealed class SteamInvites
    {
        private const string ConnectKey = "connect";
        private const string JoinArgument = "+csmp_join";
        private const float MenuSettleTime = 3f;     // Seconds in the main menu before a waiting join starts.

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly ConfigEntry<TransportKind> _connection;
        private readonly ConfigEntry<string> _hostSteamId;
        private Callback<GameRichPresenceJoinRequested_t> _joinRequested;
        private bool _steamReady;
        private float _nextSteamCheck;
        private bool _presenceSet;
        private ulong _pendingHost;                   // A join waiting for the main menu, or 0.
        private float _menuSince = -1f;

        internal static SteamInvites Instance { get; private set; }

        public SteamInvites(NetSession session, ManualLogSource log, ConfigEntry<TransportKind> connection, ConfigEntry<string> hostSteamId)
        {
            _session = session;
            _log = log;
            _connection = connection;
            _hostSteamId = hostSteamId;
            Instance = this;
            ReadCommandLine();
        }

        // "Join Game" with the game closed: Steam starts it with the host's connect string on the command line.
        private void ReadCommandLine()
        {
            try
            {
                string line = string.Join(" ", Environment.GetCommandLineArgs());
                int at = line.IndexOf(JoinArgument, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    return;
                if (ConnectWindows.TryParseSteamId(line.Substring(at + JoinArgument.Length), out ulong host, out _))
                {
                    _pendingHost = host;
                    _log.LogInfo($"Started from Steam to join {host}; joining once the main menu is up.");
                }
            }
            catch (Exception e)
            {
                _log.LogWarning($"Couldn't read the command line for a Steam join: {e.Message}");
            }
        }

        public void Tick(float dt)
        {
            if (Time.realtimeSinceStartup >= _nextSteamCheck)
            {
                _nextSteamCheck = Time.realtimeSinceStartup + 5f;
                _steamReady = SteamTransport.Unavailable() == null;
                if (_steamReady && _joinRequested == null)
                    _joinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnJoinRequested);
            }
            if (!_steamReady)
                return;
            // While not connected through Steam, nobody else pumps callbacks for the join request.
            if (!(_session.IsRunning && _session.Kind == TransportKind.Steam))
                SteamTransport.PumpCallbacks(_log);
            UpdatePresence();
            TryPendingJoin();
        }

        // ---------- Host side ----------

        private bool HostingThroughSteam => _session.IsRunning && _session.IsHost && _session.Kind == TransportKind.Steam;

        private static string ConnectString(ulong host) => $"{JoinArgument} {host}";

        private void UpdatePresence()
        {
            bool want = HostingThroughSteam;
            if (want == _presenceSet)
                return;
            try
            {
                // An empty value removes the key; the game's own rich presence keys are left alone.
                SteamFriends.SetRichPresence(ConnectKey, want ? ConnectString(SteamTransport.LocalSteamId()) : "");
                _presenceSet = want;
                _log.LogInfo(want
                    ? "Friends can now join from their Steam friends list (Join Game)."
                    : "Removed Join Game from the Steam friends list.");
            }
            catch (Exception e)
            {
                _presenceSet = want;
                _log.LogWarning($"Couldn't update Steam rich presence: {e.Message}");
            }
        }

        // Opens the Steam overlay's invite list. Only works while hosting through Steam.
        public void OpenInviteDialog()
        {
            if (!HostingThroughSteam)
                return;
            try
            {
                if (!SteamUtils.IsOverlayEnabled())
                {
                    PauseSync.Instance?.Notify("The Steam overlay is off. Turn it on in Steam (Settings > In Game), or send your Steam ID instead.", 8f);
                    return;
                }
                SteamFriends.ActivateGameOverlayInviteDialogConnectString(ConnectString(SteamTransport.LocalSteamId()));
            }
            catch (Exception e)
            {
                _log.LogWarning($"Couldn't open the Steam invite dialog: {e.Message}");
            }
        }

        // ---------- Joining side ----------

        private void OnJoinRequested(GameRichPresenceJoinRequested_t request)
        {
            try
            {
                string connect = request.m_rgchConnect ?? "";
                ulong host = ConnectWindows.TryParseSteamId(connect, out ulong parsed, out _) ? parsed : request.m_steamIDFriend.m_SteamID;
                _log.LogInfo($"Steam asked to join {SteamTransport.NameOf(host)} (connect string \"{connect}\").");
                if (_session.IsRunning)
                {
                    PauseSync.Instance?.Notify("You're already connected. Press F12 to disconnect, then click Join Game again.", 8f);
                    return;
                }
                _pendingHost = host;
                _menuSince = -1f;
                if (!InMainMenu)
                    PauseSync.Instance?.Notify($"Go back to the main menu to join {SteamTransport.NameOf(host)}.", 8f);
            }
            catch (Exception e)
            {
                _log.LogError($"Handling a Steam join request failed: {e}");
            }
        }

        private static bool InMainMenu => !GameManager.DuringGame && Player.Me == null && !WorldSync.Loading;

        private void TryPendingJoin()
        {
            if (_pendingHost == 0)
                return;
            if (_session.IsRunning || !InMainMenu)
            {
                _menuSince = -1f;
                return;
            }
            // Give the main menu a moment to finish loading before the world download starts.
            if (_menuSince < 0f)
                _menuSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - _menuSince < MenuSettleTime)
                return;
            ulong host = _pendingHost;
            _pendingHost = 0;
            _connection.Value = TransportKind.Steam;
            _hostSteamId.Value = host.ToString();
            _session.JoinSteam(host);
        }
    }
}
