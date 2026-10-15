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
        public const string PluginVersion = "0.4.9";

        private ConfigEntry<string> _hostAddress;
        private ConfigEntry<int> _port;
        private NetSession _session;
        private PlayerSync _playerSync;
        private ItemSync _itemSync;

        private void Awake()
        {
            // The game destroys plugin objects on scene load; this keeps Update() running.
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            _hostAddress = Config.Bind("Network", "HostAddress", "127.0.0.1",
                "IP address F11 joins. 127.0.0.1 means this PC; use the host's VPN IP to play with a friend.");
            _port = Config.Bind("Network", "Port", 9050,
                "UDP port the host listens on and the joiner connects to.");

            _session = new NetSession(Logger);
            _playerSync = new PlayerSync(_session, Logger);
            _itemSync = new ItemSync(_session, Logger);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded (process {NetSession.ProcessId}). " +
                           $"F10 = host on port {_port.Value}, F11 = join {_hostAddress.Value}:{_port.Value}, F12 = disconnect.");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F10))
                _session.StartHost(_port.Value);
            else if (Input.GetKeyDown(KeyCode.F11))
                _session.Join(_hostAddress.Value, _port.Value);
            else if (Input.GetKeyDown(KeyCode.F12))
                _session.Stop();

            _session.Poll();
            // Unscaled so syncing keeps running while the game is paused.
            _playerSync.Tick(Time.unscaledDeltaTime);
            _itemSync.Tick(Time.unscaledDeltaTime);
        }

        private void LateUpdate() => _itemSync.LateTick(Time.unscaledDeltaTime);

        private void OnApplicationQuit() => _session?.Stop();

        private void OnDestroy()
        {
            Logger.LogWarning("Plugin object was destroyed!");
            _session?.Stop();
        }
    }
}
