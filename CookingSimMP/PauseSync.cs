using System;
using BepInEx.Logging;
using CS.GUI;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using WIUtils;

namespace CookingSimMP
{
    // The host owns game time. Whenever the host's clock stops (pause menu, tutorial or anything else),
    // every client's clock stops too, behind a "paused by host" overlay, until the host's clock runs again.
    //
    // Clients can't open the pause menu while connected (its Save, Load and Restart buttons would break the
    // session). Pressing Esc instead asks the host to pause, which shows a short banner on every screen.
    // Esc still closes other windows (recipe book, slicing window), since those use the game's own cancel key.
    internal sealed class PauseSync
    {
        private const float RequestCooldown = 5f;
        private const float BannerTime = 4f;

        private enum Reason : byte
        {
            Menu = 1,   // Host opened the pause menu.
            Busy = 2,   // Host's clock stopped for something else (tutorial, popup).
        }

        internal static PauseSync Instance;

        private readonly NetSession _session;
        private readonly CookingSync _cooking;
        private readonly InteractableSync _interactables;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".pause");

        // Host side.
        private bool _sentPaused;
        private Reason _sentReason;

        // Client side.
        private bool _hostPaused;       // What the host last said.
        private Reason _hostReason;
        private bool _frozen;           // This copy's clock is stopped because of the host.
        private bool _weStoppedClock;   // The clock was running when we froze it, so we restart it on resume.
        private bool _unpauseBlocked;   // Something on the client tried to restart its clock while frozen.
        private float _requestCooldown;

        private string _banner;
        private float _bannerTimer;
        private GUIStyle _overlayStyle;
        private GUIStyle _bannerStyle;

        // While frozen, the client's own attempts to restart its clock are held back until the host resumes.
        internal static bool HoldUnpause()
        {
            if (Instance == null || !Instance._frozen)
                return false;
            Instance._unpauseBlocked = true;
            return true;
        }
        internal static bool IsClient => Instance != null && Instance._session.IsConnected && !Instance._session.IsHost;

        public PauseSync(NetSession session, CookingSync cooking, InteractableSync interactables, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _cooking = cooking;
            _interactables = interactables;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.PeerJoined += _ => OnPeerJoined();
            _session.Stopped += OnStopped;

            Patch(typeof(KitchenInputService), nameof(KitchenInputService.TryOpenPauseMenu), nameof(PausePatches.TryOpenPauseMenuPrefix));
            Patch(typeof(GUIPauseMenu), nameof(GUIPauseMenu.OpenWindow), nameof(PausePatches.OpenPauseMenuPrefix));
            Patch(typeof(GameManager), nameof(GameManager.UnpauseTime), nameof(PausePatches.UnpausePrefix));
            Patch(typeof(GameManager), nameof(GameManager.UnPauseTimeScale), nameof(PausePatches.UnpausePrefix));
        }

        public static bool Handles(MessageType type) => type == MessageType.PauseState || type == MessageType.PauseRequest;

        private void Patch(Type type, string method, string prefix)
        {
            try
            {
                _harmony.Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(typeof(PausePatches), prefix));
            }
            catch (Exception e)
            {
                _log.LogError($"[Pause] Patching {type.Name}.{method} failed; pause sync may not work: {e}");
            }
        }

        public void Tick(float deltaTime)
        {
            if (_bannerTimer > 0f)
                _bannerTimer -= deltaTime;
            if (_requestCooldown > 0f)
                _requestCooldown -= deltaTime;

            if (!_session.IsConnected)
                return;
            if (_session.IsHost)
                TickHost();
            else
                TickClient();
        }

        // ---------- Host ----------

        private static bool HostClockStopped(out Reason reason)
        {
            GUIPauseMenu menu = GUIKitchen.Me != null ? GUIKitchen.Me.pauseMenu : null;
            reason = menu != null && menu.gameObject.activeInHierarchy ? Reason.Menu : Reason.Busy;
            return GameManager.DuringGame && (GameManager.Paused || Time.timeScale == 0f);
        }

        private void TickHost()
        {
            bool paused = HostClockStopped(out Reason reason);
            if (paused == _sentPaused && (!paused || reason == _sentReason))
                return;
            bool resumed = _sentPaused && !paused;
            SendState(paused, reason);
            if (resumed)
            {
                // Both clocks were stopped, so nothing should have drifted, but resend everything to be sure.
                _cooking.SendAllOnNextTick();
                _interactables.ResendSnapshot();
            }
        }

        private void SendState(bool paused, Reason reason)
        {
            _sentPaused = paused;
            _sentReason = reason;
            NetDataWriter writer = _session.BeginMessage(MessageType.PauseState);
            writer.Put(paused);
            writer.Put((byte)reason);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        // A player who joins mid-pause starts paused.
        private void OnPeerJoined()
        {
            if (_session.IsHost)
                SendState(_sentPaused, _sentReason);
        }

        // ---------- Client ----------

        private void TickClient()
        {
            // The client may have had the pause menu open when it connected; it can't be used in a session.
            GUIPauseMenu menu = GUIKitchen.Me != null ? GUIKitchen.Me.pauseMenu : null;
            if (menu != null && menu.gameObject.activeInHierarchy)
                menu.BackToGame();

            bool shouldFreeze = _hostPaused && GameManager.DuringGame && Singleton<GameManager>.Me != null;
            if (shouldFreeze && !_frozen)
                Freeze();
            else if (!shouldFreeze && _frozen)
                Unfreeze();
        }

        private void Freeze()
        {
            _weStoppedClock = Time.timeScale != 0f;
            _unpauseBlocked = false;
            Singleton<GameManager>.Me.PauseTime();
            _frozen = true;
        }

        private void Unfreeze()
        {
            _frozen = false;
            if ((_weStoppedClock || _unpauseBlocked) && Singleton<GameManager>.Me != null)
                Singleton<GameManager>.Me.UnpauseTime();
            _weStoppedClock = false;
            _unpauseBlocked = false;
        }

        // Called from the TryOpenPauseMenu patch when the client presses the pause key.
        internal void OnClientPauseKey()
        {
            if (_frozen || _requestCooldown > 0f)
                return;
            _requestCooldown = RequestCooldown;
            string name = LocalName();
            ShowBanner($"{name} is asking to pause");
            _session.BeginMessage(MessageType.PauseRequest).Put(name);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        private void OnStopped()
        {
            bool wasFrozen = _frozen;
            if (_frozen)
                Unfreeze();
            _hostPaused = false;
            _sentPaused = false;
            if (wasFrozen)
                ShowBanner("Host disconnected");
        }

        // ---------- Messages ----------

        private void OnMessage(int peerId, MessageType type, NetPacketReader reader)
        {
            switch (type)
            {
                case MessageType.PauseState:
                    if (_session.IsHost)
                        break;
                    _hostPaused = reader.GetBool();
                    _hostReason = (Reason)reader.GetByte();
                    TickClient();
                    break;
                case MessageType.PauseRequest:
                    string name = reader.GetString(64);
                    ShowBanner($"{name} is asking to pause");
                    // The host passes the request on so every client sees it.
                    if (_session.IsHost)
                    {
                        _session.BeginMessage(MessageType.PauseRequest).Put(name);
                        _session.SendToAll(DeliveryMethod.ReliableOrdered);
                    }
                    break;
            }
        }

        internal static string LocalName() => SteamName() ?? "A player";

        // This player's Steam name, or null when Steam isn't running for this copy.
        internal static string SteamName()
        {
            try
            {
                string name = Steamworks.SteamFriends.GetPersonaName();
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            catch (Exception)
            {
                // Steam isn't running for this copy.
            }
            return null;
        }

        // ---------- Overlay ----------

        // A short line at the top of the screen, e.g. "Jake is holding that".
        internal void Notify(string text, float seconds = BannerTime) => ShowBanner(text, seconds);

        private void ShowBanner(string text, float seconds = BannerTime)
        {
            _banner = text;
            _bannerTimer = seconds;
        }

        public void OnGUI()
        {
            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 28,
                    wordWrap = true,
                };
                _overlayStyle.normal.textColor = Color.white;
                _bannerStyle = new GUIStyle(_overlayStyle) { fontSize = 20 };
            }

            if (_frozen)
            {
                string text = _hostReason == Reason.Menu
                    ? "Game paused by host\n\nIt will resume when the host resumes."
                    : "The host's game is paused\n\nIt will resume when the host is back.";
                var rect = new Rect(Screen.width / 2f - 300f, Screen.height / 2f - 90f, 600f, 180f);
                // Drawing the box twice makes IMGUI's translucent box dark enough to read over the kitchen.
                GUI.Box(rect, GUIContent.none, _overlayStyle);
                GUI.Box(rect, text, _overlayStyle);
            }

            if (_bannerTimer > 0f && !string.IsNullOrEmpty(_banner))
            {
                // Long messages (a refused join) wrap onto more lines.
                float height = Mathf.Max(50f, _bannerStyle.CalcHeight(new GUIContent(_banner), 520f) + 16f);
                var rect = new Rect(Screen.width / 2f - 260f, 40f, 520f, height);
                GUI.Box(rect, GUIContent.none, _bannerStyle);
                GUI.Box(rect, _banner, _bannerStyle);
            }
        }
    }

    // Harmony entry points. Each forwards to PauseSync and never lets an exception reach the game.
    internal static class PausePatches
    {
        // Clients never open the pause menu; the pause key sends a request to the host instead.
        // Same conditions as the original, so Esc in photo mode or with another window open behaves as before.
        public static bool TryOpenPauseMenuPrefix()
        {
            if (!PauseSync.IsClient)
                return true;
            try
            {
                if (Game.Input.Menu.GetButtonDown() && !Singleton<PhotoModeEffects>.Me.PhotoModeEnabled &&
                    !GUIGenericWindow.AnyWindowActive && !Singleton<ContextStrategyController>.Me.IsUseContextControll &&
                    !CameraLocking.LockedOrLocking)
                    PauseSync.Instance.OnClientPauseKey();
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Pause] patch error: " + e);
            }
            return false;
        }

        // Catches every other way the menu opens (tutorial windows reopen it when they close, for example).
        public static bool OpenPauseMenuPrefix() => !PauseSync.IsClient;

        // While the host is paused, nothing on the client may restart its clock.
        public static bool UnpausePrefix() => !PauseSync.HoldUnpause();
    }
}
