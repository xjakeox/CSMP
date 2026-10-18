using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CookingSimMP
{
    // F10 opens a "start hosting?" window; F11 opens a window to enter the host. Both free the mouse and block the
    // game's clicks while open.
    //
    // v0.12: both windows have two tabs. Steam (the default) hosts through Steam: the host's window shows their Steam
    // ID with a Copy button, and the friend pastes it. Direct IP is the old way: the host's addresses and port, and the
    // friend types one (LAN, Tailscale, port forwarding, or two copies on one PC).
    //
    // When connecting fails or the connection is lost, an error window says why (v0.10), with Try again (back to
    // the join window) where that makes sense.
    internal sealed class ConnectWindows
    {
        private enum Window { None, Host, Join, Error }

        private const float Width = 520f;

        private readonly NetSession _session;
        private readonly ConfigEntry<string> _hostAddress;
        private readonly ConfigEntry<int> _port;
        private readonly ConfigEntry<TransportKind> _connection;
        private readonly ConfigEntry<string> _hostSteamId;

        private Window _open;
        private string _joinText = "";
        private string _error;
        private List<string> _addresses = new List<string>();
        private bool _focusField;
        private string _errorTitle;
        private string _errorText;
        private bool _errorCanRetry;
        private string _steamProblem;         // Why Steam can't be used right now, or null.
        private ulong _ownSteamId;
        private float _copiedAt = -10f;        // When "Copy Steam ID" was last clicked, for the "Copied" label.

        // Game input state saved while a window is open.
        private bool _savedLocked;
        private bool _savedCursorLocked;
        private bool _savedCursorVisible;
        private string _savedScene;           // Scene the input state above belongs to.

        private GUIStyle _windowStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _fieldStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _idStyle;

        internal static bool AnyOpen;

        public ConnectWindows(NetSession session, ConfigEntry<string> hostAddress, ConfigEntry<int> port,
            ConfigEntry<TransportKind> connection, ConfigEntry<string> hostSteamId)
        {
            _session = session;
            _hostAddress = hostAddress;
            _port = port;
            _connection = connection;
            _hostSteamId = hostSteamId;
            _session.ConnectProblem += ShowError;
        }

        // A connection problem goes on screen; it replaces the host or join window if one is open.
        private void ShowError(string title, string text, bool canRetry)
        {
            _errorTitle = title;
            _errorText = text;
            _errorCanRetry = canRetry;
            if (_open == Window.None)
            {
                AnyOpen = true;
                BlockGameInput();
            }
            _open = Window.Error;
        }

        public void Tick()
        {
            if (_open == Window.None)
            {
                if (Input.GetKeyDown(KeyCode.F10))
                    Open(Window.Host);
                else if (Input.GetKeyDown(KeyCode.F11))
                    Open(Window.Join);
                return;
            }
            // The game re-locks the cursor on its own (focus changes, windows closing); keep it free.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Open(Window window)
        {
            // StartHost and Join already explain why they can't run while connected.
            if (_session.IsRunning)
            {
                // v0.12: F10 while hosting through Steam opens the Steam overlay's invite list.
                if (window == Window.Host && _session.IsHost && _session.Kind == TransportKind.Steam)
                {
                    SteamInvites.Instance?.OpenInviteDialog();
                    return;
                }
                if (window == Window.Host)
                    _session.StartHost(_port.Value);
                else
                    _session.Join(_hostAddress.Value, _port.Value);
                return;
            }
            _open = window;
            _error = null;
            RefreshSteam();
            if (window == Window.Host)
                _addresses = LocalAddresses();
            else
                ResetJoinText();
            AnyOpen = true;
            BlockGameInput();
        }

        private bool Steam => _connection.Value == TransportKind.Steam;

        private void RefreshSteam()
        {
            _steamProblem = SteamTransport.Unavailable();
            _ownSteamId = SteamTransport.LocalSteamId();
        }

        // The join field starts with whatever was joined last on the selected tab.
        private void ResetJoinText()
        {
            _joinText = Steam ? _hostSteamId.Value : _hostAddress.Value;
            _error = null;
            _focusField = true;
        }

        private void SwitchTo(TransportKind kind)
        {
            if (_connection.Value == kind)
                return;
            _connection.Value = kind;
            if (_open == Window.Join)
                ResetJoinText();
        }

        private void Close()
        {
            _open = Window.None;
            AnyOpen = false;
            RestoreGameInput();
        }

        // ---------- Game input ----------

        private void BlockGameInput()
        {
            _savedCursorLocked = Cursor.lockState == CursorLockMode.Locked;
            _savedCursorVisible = Cursor.visible;
            _savedScene = SceneManager.GetActiveScene().name;
            try
            {
                KitchenInputService input = KitchenInput();
                if (input != null)
                {
                    _savedLocked = input.IsLocked;
                    input.IsLocked = true;
                    input.SetCursor(locked: false, visible: true);
                }
            }
            catch (Exception)
            {
                // No kitchen loaded (main menu); the cursor is already free there.
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void RestoreGameInput()
        {
            // v0.11.4: the window outlived its scene (the host left, so this copy went back to the main menu while
            // "Lost connection" was open). The saved kitchen state would lock the cursor in the menu, and the old
            // kitchen's input service may still answer, so leave the cursor free and the new scene's input alone.
            if (SceneManager.GetActiveScene().name != _savedScene)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return;
            }
            try
            {
                KitchenInputService input = KitchenInput();
                if (input != null)
                {
                    input.IsLocked = _savedLocked;
                    input.SetCursor(_savedCursorLocked, _savedCursorVisible);
                    return;
                }
            }
            catch (Exception)
            {
                // Fall through and restore the cursor directly.
            }
            Cursor.lockState = _savedCursorLocked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = _savedCursorVisible;
        }

        private static KitchenInputService KitchenInput() => Game.KitchenServices?.KitchenInput as KitchenInputService;

        // ---------- Actions ----------

        private void StartHosting(bool invite = false)
        {
            Close();
            if (Steam)
            {
                _session.StartSteamHost();
                if (invite)
                    SteamInvites.Instance?.OpenInviteDialog();
            }
            else
                _session.StartHost(_port.Value);
        }

        private void TryJoin()
        {
            if (Steam)
            {
                if (_steamProblem != null)
                {
                    _error = _steamProblem;
                    return;
                }
                if (!TryParseSteamId(_joinText, out ulong steamId, out _error))
                    return;
                _hostSteamId.Value = steamId.ToString();
                Close();
                _session.JoinSteam(steamId);
                return;
            }
            if (!TryParseAddress(_joinText, _port.Value, out string address, out int port, out _error))
                return;
            _hostAddress.Value = _joinText.Trim();
            Close();
            _session.Join(address, port);
        }

        // Accepts a SteamID64 (17 digits, starting 7656), alone or inside a steamcommunity.com/profiles/... link.
        internal static bool TryParseSteamId(string text, out ulong steamId, out string error)
        {
            steamId = 0;
            error = null;
            string trimmed = (text ?? "").Trim();
            Match match = Regex.Match(trimmed, @"(?<!\d)7656\d{13}(?!\d)");
            if (match.Success && ulong.TryParse(match.Value, out steamId))
                return true;
            error = trimmed.IndexOf("steamcommunity.com/id/", StringComparison.OrdinalIgnoreCase) >= 0
                ? "That profile link uses a custom name. Ask the host for the 17-digit Steam ID shown in their F10 window."
                : "Paste the host's Steam ID: the 17-digit number starting with 7656 shown in their F10 window.";
            return false;
        }

        // Accepts "address" (configured port) or "address:port".
        internal static bool TryParseAddress(string text, int defaultPort, out string address, out int port, out string error)
        {
            address = (text ?? "").Trim();
            port = defaultPort;
            error = null;
            int colon = address.LastIndexOf(':');
            if (colon >= 0 && address.IndexOf(':') == colon)
            {
                if (!int.TryParse(address.Substring(colon + 1), out port) || port < 1 || port > 65535)
                {
                    error = "The port after ':' must be a number from 1 to 65535.";
                    return false;
                }
                address = address.Substring(0, colon).Trim();
            }
            if (address.Length == 0 || address.IndexOf(' ') >= 0)
            {
                error = "Type the host's IP address, for example 100.101.102.103 or 192.168.1.20:9050.";
                return false;
            }
            return true;
        }

        // IPv4 addresses of this PC's active network adapters, Tailscale first.
        private static List<string> LocalAddresses()
        {
            var tailscale = new List<string>();
            var lan = new List<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        byte[] b = info.Address.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254)
                            continue;   // Self-assigned; nobody can reach it.
                        // Tailscale hands out addresses in 100.64.0.0/10.
                        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                            tailscale.Add($"{info.Address}  (Tailscale / VPN)");
                        else
                            lan.Add($"{info.Address}  (same network)");
                    }
                }
            }
            catch (Exception)
            {
                // Some adapters throw on query; show what we have.
            }
            tailscale.AddRange(lan);
            return tailscale;
        }

        // ---------- Drawing ----------

        public void OnGUI()
        {
            if (_open == Window.None)
                return;
            InitStyles();

            Event e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    Close();
                    e.Use();
                    return;
                }
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    e.Use();
                    if (_open == Window.Error)
                        Close();
                    else if (_open == Window.Host && WorldSync.HostInKitchen && !(Steam && _steamProblem != null))
                        StartHosting();
                    else if (_open == Window.Host)
                        Close();
                    else
                        TryJoin();
                    return;
                }
            }

            float height = _open == Window.Host ? (Steam ? 330f : 250f + 26f * Math.Max(1, _addresses.Count))
                : _open == Window.Error ? 130f + _labelStyle.CalcHeight(new GUIContent(_errorText ?? ""), Width - 40f)
                : 320f;
            var rect = new Rect(Screen.width / 2f - Width / 2f, Screen.height / 2f - height / 2f, Width, height);
            // Drawn twice so IMGUI's translucent box is dark enough to read over the kitchen.
            GUI.Box(rect, GUIContent.none, _windowStyle);
            GUI.Box(rect, GUIContent.none, _windowStyle);
            GUILayout.BeginArea(new Rect(rect.x + 20f, rect.y + 16f, rect.width - 40f, rect.height - 32f));
            if (_open == Window.Host)
                DrawHost();
            else if (_open == Window.Error)
                DrawError();
            else
                DrawJoin();
            GUILayout.EndArea();
        }

        private void DrawHost()
        {
            // Joining players load the host's world, so hosting starts from inside a kitchen.
            if (!WorldSync.HostInKitchen)
            {
                GUILayout.Label("Load a kitchen first", _labelStyle);
                GUILayout.Label("Hosting shares the kitchen you're playing. Load a save or start a game, then press F10 again.", _labelStyle);
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("OK", _buttonStyle, GUILayout.Width(120f)))
                    Close();
                GUILayout.EndHorizontal();
                return;
            }
            GUILayout.Label("Start hosting?", _labelStyle);
            DrawTabs();
            bool canHost = true;
            if (Steam)
            {
                if (_steamProblem != null)
                {
                    GUILayout.Label(_steamProblem, _labelStyle);
                    canHost = false;
                }
                else
                {
                    GUILayout.Label("Friends join through Steam with your Steam ID. No VPN or port forwarding needed.", _labelStyle);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("   " + _ownSteamId, _idStyle);
                    if (GUILayout.Button(Time.realtimeSinceStartup - _copiedAt < 2f ? "Copied" : "Copy Steam ID", _buttonStyle, GUILayout.Width(170f)))
                    {
                        GUIUtility.systemCopyBuffer = _ownSteamId.ToString();
                        _copiedAt = Time.realtimeSinceStartup;
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Label("Send it to them, or use Host and invite. Steam friends can also click Join Game on you in their friends list.", _labelStyle);
                }
            }
            else
            {
                GUILayout.Label($"Others can join your game with one of these addresses and port {_port.Value}:", _labelStyle);
                if (_addresses.Count == 0)
                    GUILayout.Label("   (no network adapters found)", _labelStyle);
                foreach (string address in _addresses)
                    GUILayout.Label("   " + address, _labelStyle);
                GUILayout.Label($"Over the internet, use your public IP. That needs UDP port {_port.Value} forwarded on your router.", _labelStyle);
            }
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUI.enabled = canHost;
            if (Steam && GUILayout.Button("Host and invite", _buttonStyle, GUILayout.Width(180f)))
                StartHosting(invite: true);
            if (GUILayout.Button("Host", _buttonStyle, GUILayout.Width(120f)))
                StartHosting();
            GUI.enabled = true;
            if (GUILayout.Button("Cancel", _buttonStyle, GUILayout.Width(120f)))
                Close();
            GUILayout.EndHorizontal();
        }

        private void DrawError()
        {
            GUILayout.Label(_errorTitle ?? "Connection problem", _labelStyle);
            GUILayout.Label(_errorText ?? "", _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            // Joining happens from the main menu, so Try again only shows there (and when it isn't hosting).
            if (_errorCanRetry && !_session.IsRunning && !GameManager.DuringGame
                && GUILayout.Button("Try again", _buttonStyle, GUILayout.Width(120f)))
            {
                _open = Window.Join;
                RefreshSteam();
                ResetJoinText();
            }
            if (GUILayout.Button("OK", _buttonStyle, GUILayout.Width(120f)))
                Close();
            GUILayout.EndHorizontal();
        }

        private void DrawJoin()
        {
            GUILayout.Label("Join a game", _labelStyle);
            DrawTabs();
            if (Steam)
                GUILayout.Label(_steamProblem ?? "You'll load into the host's kitchen. Paste the host's Steam ID (shown in their F10 window):", _labelStyle);
            else
                GUILayout.Label($"You'll load into the host's kitchen. Host's IP address (add :port if it isn't {_port.Value}):", _labelStyle);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("csmp_join_ip");
            _joinText = GUILayout.TextField(_joinText, 128, _fieldStyle);
            if (Steam && GUILayout.Button("Paste", _buttonStyle, GUILayout.Width(90f)))
            {
                _joinText = (GUIUtility.systemCopyBuffer ?? "").Trim();
                _error = null;
            }
            GUILayout.EndHorizontal();
            if (_focusField)
            {
                GUI.FocusControl("csmp_join_ip");
                _focusField = false;
            }
            if (!string.IsNullOrEmpty(_error))
                GUILayout.Label(_error, _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Connect", _buttonStyle, GUILayout.Width(120f)))
                TryJoin();
            if (GUILayout.Button("Cancel", _buttonStyle, GUILayout.Width(120f)))
                Close();
            GUILayout.EndHorizontal();
        }

        // Steam | Direct IP. The choice is saved (Connection in the config).
        private void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(Steam, "Steam", _buttonStyle, GUILayout.Width(150f)) && !Steam)
                SwitchTo(TransportKind.Steam);
            if (GUILayout.Toggle(!Steam, "Direct IP", _buttonStyle, GUILayout.Width(150f)) && Steam)
                SwitchTo(TransportKind.Direct);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        private void InitStyles()
        {
            if (_windowStyle != null)
                return;
            _windowStyle = new GUIStyle(GUI.skin.box);
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
            _labelStyle.normal.textColor = Color.white;
            _fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 20 };
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fixedHeight = 36f };
            _idStyle = new GUIStyle(_labelStyle) { fontSize = 24, fontStyle = FontStyle.Bold, wordWrap = false };
        }
    }
}
