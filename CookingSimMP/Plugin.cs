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
        public const string PluginVersion = "0.7.2";

        private ConfigEntry<string> _hostAddress;
        private ConfigEntry<int> _port;
        private NetSession _session;
        private PlayerSync _playerSync;
        private ItemSync _itemSync;
        private InteractableSync _interactableSync;
        private CookingSync _cookingSync;
        private PauseSync _pauseSync;
        private MoneySync _moneySync;
        private ConnectWindows _connectWindows;

        private void Awake()
        {
            // The game destroys plugin objects on scene load; this keeps Update() running.
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            _hostAddress = Config.Bind("Network", "HostAddress", "127.0.0.1",
                "Address the F11 join window starts with; it saves whatever you last joined. 127.0.0.1 means this PC; use the host's VPN IP to play with a friend. Add :port to use a different port.");
            _port = Config.Bind("Network", "Port", 9050,
                "UDP port the host listens on and the joiner connects to.");

            _session = new NetSession(Logger);
            _playerSync = new PlayerSync(_session, Logger);
            _itemSync = new ItemSync(_session, Logger);
            _interactableSync = new InteractableSync(_session, Logger);
            _cookingSync = new CookingSync(_session, _itemSync, Logger);
            _pauseSync = new PauseSync(_session, _cookingSync, _interactableSync, Logger);
            _moneySync = new MoneySync(_session, Logger);
            _connectWindows = new ConnectWindows(_session, _hostAddress, _port);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded (process {NetSession.ProcessId}). " +
                           $"F10 = host on port {_port.Value}, F11 = join (asks for the address), F12 = disconnect, F9 (client) = cooking sync test.");
        }

        private void Update()
        {
            // F10 and F11 open the host and join windows.
            _connectWindows.Tick();
            if (Input.GetKeyDown(KeyCode.F12))
                _session.Stop();

            _session.Poll();
            // Unscaled so syncing keeps running while the game is paused.
            _playerSync.Tick(Time.unscaledDeltaTime);
            _itemSync.Tick(Time.unscaledDeltaTime);
            _interactableSync.Tick(Time.unscaledDeltaTime);
            _cookingSync.Tick(Time.unscaledDeltaTime);
            _pauseSync.Tick(Time.unscaledDeltaTime);
            _moneySync.Tick(Time.unscaledDeltaTime);
        }

        private void LateUpdate() => _itemSync.LateTick(Time.unscaledDeltaTime);

        private void OnGUI()
        {
            _pauseSync?.OnGUI();
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
