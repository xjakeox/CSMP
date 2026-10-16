using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace CookingSimMP
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.jake.cookingsimmp";
        public const string PluginName = "CookingSimMP";
        public const string PluginVersion = "0.9.8";

        private ConfigEntry<string> _hostAddress;
        private ConfigEntry<int> _port;
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
        private ConnectWindows _connectWindows;
        private int _marker;

        private void Awake()
        {
            // The game destroys plugin objects on scene load; this keeps Update() running.
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            _hostAddress = Config.Bind("Network", "HostAddress", "127.0.0.1",
                "Address the F11 join window starts with; it saves whatever you last joined. 127.0.0.1 means this PC; use the host's VPN IP to play with a friend. Add :port to use a different port.");
            _port = Config.Bind("Network", "Port", 9050,
                "UDP port the host listens on and the joiner connects to.");

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
            _orderSync = new OrderSync(_session, _itemSync, Logger);
            _timerSync = new TimerSync(_session, _itemSync, Logger);
            _breakSync = new BreakSync(_session, _itemSync, Logger);
            _connectWindows = new ConnectWindows(_session, _hostAddress, _port);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded (process {NetSession.ProcessId}). " +
                           $"F10 = host on port {_port.Value} (from inside a kitchen), F11 = join from the main menu (asks for the address), F12 = disconnect, F9 (client) = cooking sync test, F8 = log marker.");
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
            _worldSync.Tick(Time.unscaledDeltaTime);
            // Unscaled so syncing keeps running while the game is paused.
            _playerSync.Tick(Time.unscaledDeltaTime);
            _itemSync.Tick(Time.unscaledDeltaTime);
            _interactableSync.Tick(Time.unscaledDeltaTime);
            _cookingSync.Tick(Time.unscaledDeltaTime);
            _pauseSync.Tick(Time.unscaledDeltaTime);
            _moneySync.Tick(Time.unscaledDeltaTime);
            _orderSync.Tick(Time.unscaledDeltaTime);
            _timerSync.Tick(Time.unscaledDeltaTime);
            _contentsSync.Tick(Time.unscaledDeltaTime);
            _cleaningSync.Tick(Time.unscaledDeltaTime);
            _mopSync.Tick(Time.unscaledDeltaTime);
        }

        private void OnMarker(int peerId, MessageType type, LiteNetLib.NetPacketReader reader)
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

        private void LateUpdate() => _itemSync.LateTick(Time.unscaledDeltaTime);

        private void OnGUI()
        {
            DrawVersionLabel();
            _pauseSync?.OnGUI();
            _worldSync?.OnGUI();
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
