using BepInEx;
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace CookingSimMP
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "cookingsimmp";
        public const string PluginName = "CookingSimMP";
        public const string PluginVersion = "0.12.2";

        private ConfigEntry<string> _hostAddress;
        private ConfigEntry<int> _port;
        private ConfigEntry<TransportKind> _connection;
        private ConfigEntry<string> _hostSteamId;
        private ConfigEntry<bool> _moreOrders;
        private NetSession _session;
        private PlayerSync _playerSync;
        private ItemSync _itemSync;
        private InteractableSync _interactableSync;
        private CookingSync _cookingSync;
        private PauseSync _pauseSync;
        private MoneySync _moneySync;
        private CutSync _cutSync;
        private ContentsSync _contentsSync;
        private CleaningSync _cleaningSync;
        private MopSync _mopSync;
        private WorldSync _worldSync;
        private OrderSync _orderSync;
        private TimerSync _timerSync;
        private BreakSync _breakSync;
        private StackSync _stackSync;
        private PropSync _propSync;
        private StandSync _standSync;
        private ClockSync _clockSync;
        private CareerSync _careerSync;
        private KeepRunning _keepRunning;
        private ConnectWindows _connectWindows;
        private SteamInvites _steamInvites;
        private int _marker;

        private void Awake()
        {
            // The game destroys plugin objects on scene load; this keeps Update() running.
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            _connection = Config.Bind("Network", "Connection", TransportKind.Steam,
                "How players connect (v0.12). Steam: through Steam, using the host's Steam ID; no VPN or port forwarding. Direct: by IP address and UDP port (LAN, Tailscale, port forwarding, or two copies on one PC). The F10/F11 windows switch it and save the choice.");
            _hostSteamId = Config.Bind("Network", "HostSteamId", "",
                "Steam ID the F11 join window starts with on the Steam tab; it saves whatever you last joined.");
            _hostAddress = Config.Bind("Network", "HostAddress", "127.0.0.1",
                "Direct IP only: address the F11 join window starts with; it saves whatever you last joined. 127.0.0.1 means this PC; use the host's VPN IP to play with a friend. Add :port to use a different port.");
            _port = Config.Bind("Network", "Port", 9050,
                "Direct IP only: UDP port the host listens on and the joiner connects to. Steam connections don't use it.");
            _moreOrders = Config.Bind("Gameplay", "MoreOrdersWithMorePlayers", true,
                "Host only: with a second player, the kitchen takes 1.5x the orders at once (another 0.5x per extra player). Also a button in the host's pause menu.");

            _session = new NetSession(Logger);
            _session.MessageReceived += OnMarker;
            _playerSync = new PlayerSync(_session, Logger);
            _itemSync = new ItemSync(_session, Logger);
            _interactableSync = new InteractableSync(_session, Logger);
            _cookingSync = new CookingSync(_session, _itemSync, Logger);
            _pauseSync = new PauseSync(_session, _cookingSync, _interactableSync, Logger);
            _moneySync = new MoneySync(_session, Logger);
            _cutSync = new CutSync(_session, _itemSync, Logger);
            _contentsSync = new ContentsSync(_session, _itemSync, Logger);
            _cleaningSync = new CleaningSync(_session, _itemSync, Logger);
            _mopSync = new MopSync(_session, _itemSync, Logger);
            _worldSync = new WorldSync(_session, Logger);
            _orderSync = new OrderSync(_session, _itemSync, Logger, _moreOrders);
            _timerSync = new TimerSync(_session, _itemSync, Logger);
            _breakSync = new BreakSync(_session, _itemSync, Logger);
            _stackSync = new StackSync(_session, Logger);
            _propSync = new PropSync(_session, Logger);
            _standSync = new StandSync(_session, Logger);
            _clockSync = new ClockSync(_session, Logger);
            _careerSync = new CareerSync(_session, Logger);
            _keepRunning = new KeepRunning(_session, Logger);
            _connectWindows = new ConnectWindows(_session, _hostAddress, _port, _connection, _hostSteamId);
            _steamInvites = new SteamInvites(_session, Logger, _connection, _hostSteamId);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded (process {NetSession.ProcessId}). " +
                           $"F10 = host through Steam or Direct IP (from inside a kitchen), F11 = join from the main menu (Steam ID or address), F12 = disconnect, F9 (client) = cooking sync test, F8 = log marker.");
        }

        private void Update()
        {
            // F10 and F11 open the host and join windows.
            _connectWindows.Tick();
            if (Input.GetKeyDown(KeyCode.F12))
                _session.Stop();
            // F8 writes a numbered marker into both players' logs, so test steps are easy to find.
            if (Input.GetKeyDown(KeyCode.F8))
            {
                _marker++;
                Logger.LogInfo($"========== F8 MARKER {_marker} ==========");
                if (_session.IsConnected)
                {
                    _session.BeginMessage(MessageType.LogMarker).Put(_marker);
                    _session.SendToAll(LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
            }

            _session.Poll();
            Run("Steam invites", _steamInvites.Tick, Time.unscaledDeltaTime);
            // Unscaled so syncing keeps running while the game is paused.
            float dt = Time.unscaledDeltaTime;
            Run("World", _worldSync.Tick, dt);
            Run("Players", _playerSync.Tick, dt);
            Run("Items", _itemSync.Tick, dt);
            Run("Interactables", _interactableSync.Tick, dt);
            Run("Cooking", _cookingSync.Tick, dt);
            Run("Pause", _pauseSync.Tick, dt);
            Run("Money", _moneySync.Tick, dt);
            Run("Orders", _orderSync.Tick, dt);
            Run("Timers", _timerSync.Tick, dt);
            Run("Contents", _contentsSync.Tick, dt);
            Run("Cleaning", _cleaningSync.Tick, dt);
            Run("Mop", _mopSync.Tick, dt);
            Run("Stacks", _stackSync.Tick, dt);
            Run("Stands", _standSync.Tick, dt);
            Run("Clock", _clockSync.Tick, dt);
            Run("Career", _careerSync.Tick, dt);
        }

        // v0.11: one sync failing mustn't stop the rest for the frame, and the error has to reach LogOutput.log
        // (Unity's exception log isn't written there). The same error is logged again at most every 10 seconds.
        private readonly Dictionary<string, float> _lastTickError = new Dictionary<string, float>();

        private void Run(string name, Action<float> tick, float dt)
        {
            try
            {
                tick(dt);
            }
            catch (Exception e)
            {
                string key = name + e.GetType().Name + e.Message;
                if (_lastTickError.TryGetValue(key, out float at) && Time.realtimeSinceStartup - at < 10f)
                    return;
                _lastTickError[key] = Time.realtimeSinceStartup;
                Logger.LogError($"[{name}] Update failed: {e}");
            }
        }

        private void OnMarker(int peerId, MessageType type, LiteNetLib.Utils.NetDataReader reader)
        {
            if (type != MessageType.LogMarker)
                return;
            _marker = reader.GetInt();
            Logger.LogInfo($"========== F8 MARKER {_marker} (pressed by the other player) ==========");
        }

        private GUIStyle _versionStyle;
        private GUIStyle _versionShadow;

        // Main menu only: shows that the mod loaded, and which version, in the top right corner.
        private void DrawVersionLabel()
        {
            if (GameManager.DuringGame || Player.Me != null || WorldSync.Loading)
                return;
            if (_versionStyle == null)
            {
                _versionStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight, fontSize = 18, fontStyle = FontStyle.Bold };
                _versionStyle.normal.textColor = Color.white;
                _versionShadow = new GUIStyle(_versionStyle);
                _versionShadow.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            }
            string text = $"Cooking Simulator Multiplayer (CSMP) Active V{PluginVersion}";
            var rect = new Rect(Screen.width - 620f, 12f, 600f, 30f);
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, _versionShadow);
            GUI.Label(rect, text, _versionStyle);
        }

        private void LateUpdate() => Run("Items (late)", _itemSync.LateTick, Time.unscaledDeltaTime);

        private void OnGUI()
        {
            DrawVersionLabel();
            _playerSync?.OnGUI();
            _pauseSync?.OnGUI();
            _worldSync?.OnGUI();
            _orderSync?.OnGUI();
            _careerSync?.OnGUI();
            _connectWindows?.OnGUI();
        }

        private void OnApplicationQuit() => _session?.Stop();

        private void OnDestroy()
        {
            Logger.LogWarning("Plugin object was destroyed!");
            _session?.Stop();
        }
    }
}
