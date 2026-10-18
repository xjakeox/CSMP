using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CookingSimMP
{
    // F10 opens a "start hosting?" window listing the addresses others can join with; F11 opens a window
    // to type the host's address. Both free the mouse and block the game's clicks while open.
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

        private Window _open;
        private string _joinText = "";
        private string _error;
        private List<string> _addresses = new List<string>();
        private bool _focusField;
        private string _errorTitle;
        private string _errorText;
        private bool _errorCanRetry;

        // Game input state saved while a window is open.
        private bool _savedLocked;
        private bool _savedCursorLocked;
        private bool _savedCursorVisible;
        private string _savedScene;           // Scene the input state above belongs to.

        private GUIStyle _windowStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _fieldStyle;
        private GUIStyle _buttonStyle;

        internal static bool AnyOpen;

        public ConnectWindows(NetSession session, ConfigEntry<string> hostAddress, ConfigEntry<int> port)
        {
            _session = session;
            _hostAddress = hostAddress;
            _port = port;
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
                if (window == Window.Host)
                    _session.StartHost(_port.Value);
                else
                    _session.Join(_hostAddress.Value, _port.Value);
                return;
            }
            _open = window;
            _error = null;
            if (window == Window.Host)
                _addresses = LocalAddresses();
            else
            {
                _joinText = _hostAddress.Value;
                _focusField = true;
            }
            AnyOpen = true;
            BlockGameInput();
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

        private void StartHosting()
        {
            Close();
            _session.StartHost(_port.Value);
        }

        private void TryJoin()
        {
            if (!TryParseAddress(_joinText, _port.Value, out string address, out int port, out _error))
                return;
            _hostAddress.Value = _joinText.Trim();
            Close();
            _session.Join(address, port);
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
                    else if (_open == Window.Host && WorldSync.HostInKitchen)
                        StartHosting();
                    else if (_open == Window.Host)
                        Close();
                    else
                        TryJoin();
                    return;
                }
            }

            float height = _open == Window.Host ? 200f + 26f * Math.Max(1, _addresses.Count)
                : _open == Window.Error ? 130f + _labelStyle.CalcHeight(new GUIContent(_errorText ?? ""), Width - 40f)
                : 260f;
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
            GUILayout.Label($"Others can join your game with one of these addresses and port {_port.Value}:", _labelStyle);
            if (_addresses.Count == 0)
                GUILayout.Label("   (no network adapters found)", _labelStyle);
            foreach (string address in _addresses)
                GUILayout.Label("   " + address, _labelStyle);
            GUILayout.Label($"Over the internet, use your public IP. That needs UDP port {_port.Value} forwarded on your router.", _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Host", _buttonStyle, GUILayout.Width(120f)))
                StartHosting();
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
                _error = null;
                _joinText = _hostAddress.Value;
                _focusField = true;
            }
            if (GUILayout.Button("OK", _buttonStyle, GUILayout.Width(120f)))
                Close();
            GUILayout.EndHorizontal();
        }

        private void DrawJoin()
        {
            GUILayout.Label("Join a game", _labelStyle);
            GUILayout.Label($"You'll load into the host's kitchen. Host's IP address (add :port if it isn't {_port.Value}):", _labelStyle);
            GUI.SetNextControlName("csmp_join_ip");
            _joinText = GUILayout.TextField(_joinText, 64, _fieldStyle);
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

        private void InitStyles()
        {
            if (_windowStyle != null)
                return;
            _windowStyle = new GUIStyle(GUI.skin.box);
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
            _labelStyle.normal.textColor = Color.white;
            _fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 20 };
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fixedHeight = 36f };
        }
    }
}
