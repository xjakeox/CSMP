using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BC.GamingPlatforms;
using BepInEx;
using BepInEx.Logging;
using CS.GUI;
using HarmonyLib;
using Ionic.Zip;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using UnityEngine;
using WIUtils;

namespace CookingSimMP
{
    // A joining player gets the host's world: the host builds a save of its kitchen in memory (the same data the
    // game writes to a .cssave file) and sends it; the client loads it with the game's own LoadGame. Both copies
    // then hold the same items with the same saved GlobalIds, cut food, pan contents, cash, doors and time.
    //
    // The host's clock is stopped from the moment the save is built until the client has loaded it, so nothing
    // changes in between. Whenever the host loads another kitchen, every client follows.
    //
    // While a client plays the host's world, it never saves (its own quicksave would be replaced by the host's world).
    //
    // RESYNC (v0.10): a button in the top right corner of the host's pause menu sends the world again to every
    // client, which reloads it in place, the same way it follows the host into another kitchen.
    internal sealed class WorldSync
    {
        private const int ChunkSize = 32 * 1024;
        private const float JoinTimeout = 120f;
        private const string JoinFileName = "csmp_join";

        internal static WorldSync Instance;

        // Client: a world from the host is downloading or loading; every other sync waits.
        internal static bool Loading;

        // Client: this copy is in (or loading) the host's world, so saving is off.
        internal static bool PlayingHostWorld;

        // A kitchen is loaded and it's the shared one: on a client, only once the host's world has loaded.
        internal static bool InKitchen =>
            Player.Me != null && !Loading && (Instance == null || !Instance.IsClient || PlayingHostWorld);

        private bool IsClient => _session.IsRunning && !_session.IsHost;

        internal static bool HostInKitchen => GameManager.DuringGame && Player.Me != null;

        private static readonly MethodInfo GetSaveMethod =
            AccessTools.Method(typeof(SaveLoadManager), "GetSave")?.MakeGenericMethod(typeof(CookingSave));

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".world");

        // Host side.
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        private readonly HashSet<int> _waiting = new HashSet<int>();         // Asked for the world; sent when the host is in a kitchen.
        private readonly Dictionary<int, string> _joining = new Dictionary<int, string>();  // Sent the world; loading it.
        private bool _wasInKitchen;
        private bool _wePaused;
        private float _joinTimer;
        private int _transferId;

        // Client side.
        private byte[] _incoming;
        private int _received;
        private int _incomingId;
        private string _incomingKitchen;
        private bool _loadPending;
        private bool _ourLoad;            // The load in progress is the host's world, started by us.
        private float _loadTimer;
        private GameManager _subscribedGame;
        private SaveLoadManager _subscribedSaves;
        private float _skipNoticeCooldown;

        private bool _resyncing;          // Host: the world being sent is a RESYNC, not a join.
        private bool _toldWaitForDay;     // Host: already said that a joining player waits for the next day.

        private GUIStyle _style;
        private GUIStyle _resyncStyle;

        public WorldSync(NetSession session, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.PeerVerified += OnPeerVerified;
            _session.PeerLeft += OnPeerLeft;
            _session.Stopped += OnStopped;
            _session.DropMessage = ShouldDrop;

            Patch(typeof(SaveLoadManager), nameof(SaveLoadManager.Save), nameof(WorldPatches.SavePrefix));
            Patch(typeof(SaveLoadManager), nameof(SaveLoadManager.QuickSave), nameof(WorldPatches.SavePrefix));
            Patch(typeof(GameManager), nameof(GameManager.OpenMainMenu), nameof(WorldPatches.OpenMainMenuPrefix));
            if (GetSaveMethod == null)
                _log.LogError("[World] SaveLoadManager.GetSave not found; hosting can't send the world.");
        }

        public static bool Handles(MessageType type) =>
            type >= MessageType.WorldRequest && type <= MessageType.WorldLeaving;

        private void Patch(Type type, string method, string prefix)
        {
            try
            {
                _harmony.Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(typeof(WorldPatches), prefix));
            }
            catch (Exception e)
            {
                _log.LogError($"[World] Patching {type.Name}.{method} failed: {e}");
            }
        }

        public void Tick(float deltaTime)
        {
            SubscribeToGame();
            if (_skipNoticeCooldown > 0f)
                _skipNoticeCooldown -= deltaTime;
            if (!_session.IsRunning)
                return;
            if (_session.IsHost)
                TickHost(deltaTime);
            else
                TickClient(deltaTime);
        }

        // The game's managers can be recreated, so keep the event subscriptions on the current ones.
        private void SubscribeToGame()
        {
            GameManager game = Singleton<GameManager>.Me;
            if (game != _subscribedGame)
            {
                if (_subscribedGame != null)
                    _subscribedGame.OnLoadSceneComplete -= OnLoadSceneComplete;
                _subscribedGame = game;
                if (game != null)
                    game.OnLoadSceneComplete += OnLoadSceneComplete;
            }
            var saves = Game.SaveLoadManager as SaveLoadManager;
            if (saves != _subscribedSaves)
            {
                if (_subscribedSaves != null)
                    _subscribedSaves.BeforeLoad -= OnBeforeLoad;
                _subscribedSaves = saves;
                if (saves != null)
                    saves.BeforeLoad += OnBeforeLoad;
            }
        }

        // ---------- Host ----------

        private void OnPeerVerified(int peerId)
        {
            // The client asks for the world once it knows the versions match.
            if (_session.IsHost)
                return;
            _session.BeginMessage(MessageType.WorldRequest).Put(PauseSync.LocalName());
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        private void TickHost(float deltaTime)
        {
            bool inKitchen = HostInKitchen;
            if (_wasInKitchen && !inKitchen && _session.IsConnected)
            {
                // Loading another kitchen (or going to the menu): everyone gets the new one once it's ready.
                foreach (int peerId in _names.Keys)
                    _waiting.Add(peerId);
                _joining.Clear();
                _session.BeginMessage(MessageType.WorldLeaving);
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
                _log.LogInfo("[World] Left the kitchen; players will get the next one.");
            }
            _wasInKitchen = inKitchen;

            // A player who asks while the host is ending the day gets the next day once it has started (v0.11).
            bool endingDay = CareerSync.HostEndingDay;
            if (endingDay && _waiting.Count > 0 && !_toldWaitForDay)
            {
                _toldWaitForDay = true;
                _log.LogInfo($"[World] {string.Join(", ", NamesOf(_waiting))} will get the world once the next day has started.");
                PauseSync.Instance?.Notify($"{string.Join(", ", NamesOf(_waiting))} will join when the next day starts");
            }
            if (inKitchen && _waiting.Count > 0 && !endingDay)
            {
                _toldWaitForDay = false;
                var peers = new List<int>(_waiting);
                _waiting.Clear();
                AddPlayersAlreadyIn(peers);
                SendWorld(peers);
            }

            if (_joining.Count > 0)
            {
                _joinTimer += deltaTime;
                if (_joinTimer > JoinTimeout)
                {
                    _log.LogWarning($"[World] {string.Join(", ", _joining.Values)} didn't finish loading; resuming.");
                    _joining.Clear();
                }
            }
            if (_joining.Count == 0)
            {
                _resyncing = false;
                ResumeHost();
            }
        }

        private void SendWorld(List<int> peers)
        {
            byte[] bytes;
            GameGeneralSaveData general;
            try
            {
                bytes = BuildSave(out general);
            }
            catch (Exception e)
            {
                _log.LogError($"[World] Couldn't build the world save: {e}");
                return;
            }

            // Everything the syncs knew was about the old world; the client is about to replace it.
            _session.RaiseWorldReset();
            PauseHost();
            _transferId++;
            _joinTimer = 0f;
            string[] contentIds = general?.contentIDCollection ?? new string[0];
            foreach (int peerId in peers)
            {
                _joining[peerId] = NameOf(peerId);
                NetDataWriter writer = _session.BeginMessage(MessageType.WorldBegin);
                writer.Put(_transferId);
                writer.Put(bytes.Length);
                writer.Put(general?.kitchenId ?? "");
                writer.Put(general != null ? general.gameState.ToString() : "");
                writer.Put(contentIds.Length);
                foreach (string id in contentIds)
                    writer.Put(id ?? "");
                _session.SendTo(peerId, DeliveryMethod.ReliableOrdered);

                for (int offset = 0; offset < bytes.Length; offset += ChunkSize)
                {
                    int count = Math.Min(ChunkSize, bytes.Length - offset);
                    writer = _session.BeginMessage(MessageType.WorldChunk);
                    writer.Put(_transferId);
                    writer.Put(offset);
                    writer.PutBytesWithLength(bytes, offset, (ushort)count);
                    _session.SendTo(peerId, DeliveryMethod.ReliableOrdered);
                }
            }
            _log.LogInfo($"[World] Sent the world ({bytes.Length / 1024} KB, kitchen {general?.kitchenId}, {general?.gameState}) to {string.Join(", ", _joining.Values)}.");
        }

        // v0.12.2: a player joins while others are already in. Sending the world makes the host number every item
        // again (ItemSync), and the players already in kept their old numbers, so they reload the world with the new
        // player, the way RESYNC does. Before, they stayed on the old numbers and their items no longer matched.
        private void AddPlayersAlreadyIn(List<int> peers)
        {
            if (_resyncing)
                return;
            var joiners = new List<string>(NamesOf(peers));
            foreach (int peerId in new List<int>(_names.Keys))
            {
                if (peers.Contains(peerId) || _joining.ContainsKey(peerId))
                    continue;
                peers.Add(peerId);
                NetDataWriter writer = _session.BeginMessage(MessageType.WorldLeaving);
                writer.Put(true);    // A reload of this kitchen...
                writer.Put(false);   // ...not because the next day started...
                writer.Put(string.Join(", ", joiners));   // ...but because these players are joining.
                _session.SendTo(peerId, DeliveryMethod.ReliableOrdered);
            }
            if (peers.Count > joiners.Count)
                _log.LogInfo($"[World] {string.Join(", ", joiners)} joining; the players already in reload the world with them.");
        }

        // The same save the game writes to disk (SaveLoadManager.Save), but kept in memory: no file, screenshot or save list entry.
        private static byte[] BuildSave(out GameGeneralSaveData general)
        {
            var manager = (SaveLoadManager)Game.SaveLoadManager;
            var save = (CookingSave)GetSaveMethod.Invoke(manager, new object[] { false });
            general = save.FindSaveData<GameGeneralSaveData>();

            // An item in the host's hand would load into the client's hand; leave it where it is instead.
            // The host's hold sync moves it once the client is in.
            int handId = Player.Me != null && Player.Me.HandSpawnPoint != null ? Player.Me.HandSpawnPoint.id : int.MinValue;
            foreach (ISavePack pack in save.ToSave)
            {
                if (pack is MovableSave movable && movable.spawnPointId == handId)
                    movable.spawnPointId = -1;
            }

            string json = JsonConvert.SerializeObject(save, Formatting.None, SaveUtility.JsonSerializerSettings);
            using (var zip = new ZipFile())
            using (var stream = new MemoryStream())
            {
                zip.ParallelDeflateThreshold = -1L;
                zip.AddEntry("JSON", json);
                foreach (ISavePack pack in save.ToSave)
                {
                    if (pack != null && pack.BinaryData(out int id, out byte[] data))
                        zip.AddEntry(id.ToString(), data);
                }
                zip.Save(stream);
                return stream.ToArray();
            }
        }

        private void PauseHost()
        {
            GameManager game = Singleton<GameManager>.Me;
            if (_wePaused || game == null || GameManager.Paused || Time.timeScale == 0f)
                return;
            game.PauseTime();
            _wePaused = true;
        }

        private void ResumeHost()
        {
            if (!_wePaused)
                return;
            _wePaused = false;
            GameManager game = Singleton<GameManager>.Me;
            if (game != null && GameManager.DuringGame && GameManager.Paused)
                game.UnpauseTime();
        }

        // Host: RESYNC button. Every connected player reloads the host's world. The pause menu closes first so the
        // host's clock can be held (as for a join) until everyone is back in.
        private void Resync()
        {
            if (!_session.IsHost || !_session.IsConnected || _names.Count == 0 || !HostInKitchen)
            {
                PauseSync.Instance?.Notify("Nobody is connected to resync");
                return;
            }
            try
            {
                GUIKitchen.Me?.pauseMenu?.BackToGame();
            }
            catch (Exception e)
            {
                _log.LogWarning($"[World] Closing the pause menu for RESYNC failed: {e.Message}");
            }
            _resyncing = true;
            foreach (int peerId in _names.Keys)
                _waiting.Add(peerId);
            NetDataWriter writer = _session.BeginMessage(MessageType.WorldLeaving);
            writer.Put(true);   // A resync, not another kitchen.
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"[World] RESYNC: sending the world again to {string.Join(", ", _names.Values)}.");
        }

        // Host, v0.11: the end of day is over and the next day has started; every client reloads the world, which
        // brings them the new day (level, perks, recipes, quests, clock) the same way RESYNC does.
        internal void ResyncForNextDay()
        {
            if (!_session.IsHost || !_session.IsConnected || _names.Count == 0 || !HostInKitchen)
                return;
            _resyncing = true;
            foreach (int peerId in _names.Keys)
            {
                if (!_joining.ContainsKey(peerId))
                    _waiting.Add(peerId);
            }
            NetDataWriter writer = _session.BeginMessage(MessageType.WorldLeaving);
            writer.Put(true);   // A reload of this kitchen...
            writer.Put(true);   // ...because the next day started.
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"[World] Next day: sending the world again to {string.Join(", ", _names.Values)}.");
        }

        private IEnumerable<string> NamesOf(IEnumerable<int> peers)
        {
            foreach (int peerId in peers)
                yield return NameOf(peerId);
        }

        internal static bool HostPauseMenuOpen()
        {
            GUIPauseMenu menu = GUIKitchen.Me != null ? GUIKitchen.Me.pauseMenu : null;
            return menu != null && menu.gameObject.activeInHierarchy;
        }

        internal string NameOf(int peerId) => _names.TryGetValue(peerId, out string name) ? name : "A player";

        private void OnPeerLeft(int peerId)
        {
            _names.Remove(peerId);
            _waiting.Remove(peerId);
            _joining.Remove(peerId);
            if (!_session.IsHost)
                ClearClientTransfer();
        }

        private void OnStopped()
        {
            _toldWaitForDay = false;
            _names.Clear();
            _waiting.Clear();
            _joining.Clear();
            ResumeHost();
            ClearClientTransfer();
        }

        // ---------- Client ----------

        private void TickClient(float deltaTime)
        {
            if (_loadPending)
            {
                _loadPending = false;
                StartLoad();
            }
            // LoadGame sends the player to the main menu if it can't load the kitchen; don't wait forever.
            if (_ourLoad)
            {
                _loadTimer += deltaTime;
                if (_loadTimer > JoinTimeout)
                {
                    _log.LogWarning("[World] The host's world never finished loading.");
                    _ourLoad = false;
                    Loading = false;
                }
            }
        }

        private void OnWorldBegin(NetDataReader reader)
        {
            int transferId = reader.GetInt();
            int size = reader.GetInt();
            string kitchenId = reader.GetString(128);
            string gameState = reader.GetString(128);
            int contentCount = reader.GetInt();
            var contentIds = new List<string>();
            for (int i = 0; i < contentCount; i++)
                contentIds.Add(reader.GetString(128));

            if (!CanLoad(kitchenId, contentIds, out string reason))
            {
                _log.LogWarning($"[World] Can't load the host's world: {reason}");
                _session.ReportProblem("Can't join this kitchen", reason, false);
                _session.BeginMessage(MessageType.WorldRefused).Put(reason);
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
                _session.StopAfterPoll();
                return;
            }

            // From here until the kitchen is shown, the other syncs leave this copy alone.
            Loading = true;
            PlayingHostWorld = true;
            _session.RaiseWorldReset();
            _incomingId = transferId;
            _incoming = new byte[size];
            _received = 0;
            _incomingKitchen = kitchenId;
            _log.LogInfo($"[World] Receiving the host's world ({size / 1024} KB, kitchen {kitchenId}, {gameState}).");
        }

        private void OnWorldChunk(NetDataReader reader)
        {
            int transferId = reader.GetInt();
            int offset = reader.GetInt();
            byte[] data = reader.GetBytesWithLength();
            if (_incoming == null || transferId != _incomingId || offset + data.Length > _incoming.Length)
                return;
            Buffer.BlockCopy(data, 0, _incoming, offset, data.Length);
            _received += data.Length;
            if (_received >= _incoming.Length)
                _loadPending = true;
        }

        private void StartLoad()
        {
            string path = Path.Combine(Paths.CachePath, JoinFileName);
            try
            {
                File.WriteAllBytes(path + Game.SaveLoadManager.SaveFileExtension, _incoming);
            }
            catch (Exception e)
            {
                _log.LogError($"[World] Couldn't write the host's world to {path}: {e.Message}");
                Loading = false;
                return;
            }
            _incoming = null;
            _ourLoad = true;
            _loadTimer = 0f;
            Singleton<GameManager>.Me.LoadGame(path, false);
        }

        private void OnLoadSceneComplete()
        {
            if (!_ourLoad)
                return;
            _ourLoad = false;
            Loading = false;
            if (_session.IsConnected && !_session.IsHost)
            {
                _session.BeginMessage(MessageType.WorldLoaded);
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
                _log.LogInfo($"[World] Loaded the host's world ({_incomingKitchen}).");
            }
        }

        // Loading anything other than the host's world (a save, a new game) means this copy has its own world again.
        private void OnBeforeLoad()
        {
            if (!_ourLoad)
                PlayingHostWorld = false;
        }

        internal void OnOpenMainMenu()
        {
            if (!_ourLoad)
                PlayingHostWorld = false;
        }

        private void ClearClientTransfer()
        {
            _incoming = null;
            _loadPending = false;
            // A load already running finishes on its own; this copy keeps the host's world but stops waiting.
            if (!_ourLoad)
                Loading = false;
        }

        // Can this copy load the host's kitchen? LoadGame itself doesn't check DLC ownership.
        private static bool CanLoad(string kitchenId, List<string> contentIds, out string reason)
        {
            reason = null;
            if (Game.Configurations.Common.Kitchens.GetKitchenData(kitchenId) == null)
            {
                reason = $"this copy doesn't have the host's kitchen ({kitchenId}).";
                return false;
            }
            var missing = new List<string>();
            var dlcs = new DLCService();
            foreach (string bundle in Game.Configurations.AssetBundles.GetAssetBundleNamesForContentIDCollection(contentIds))
            {
                if (!dlcs.TryGetAssetBundleDLCName(bundle, out string dlc) || missing.Contains(dlc))
                    continue;
                bool owned = GamingPlatformManager.InAppPurchases.IsDlcPurchased(dlc, out bool purchased) & purchased;
                if (!owned)
                    missing.Add(dlc);
            }
            if (missing.Count > 0)
            {
                reason = $"the host's kitchen needs DLC this copy doesn't own ({string.Join(", ", missing)}).";
                return false;
            }
            return true;
        }

        // ---------- Saving ----------

        // True when the game's save or quicksave must be skipped on this copy.
        internal bool BlockSave()
        {
            bool block = PlayingHostWorld || (_session.IsRunning && !_session.IsHost);
            if (block && _skipNoticeCooldown <= 0f)
            {
                _skipNoticeCooldown = 5f;
                _log.LogInfo("[World] Skipped saving: this is the host's world.");
                PauseSync.Instance?.Notify("Saving is off while playing the host's world");
            }
            return block;
        }

        // ---------- Messages ----------

        // Messages about the old world are dropped while the client loads the new one (both sides).
        private bool ShouldDrop(int peerId, MessageType type)
        {
            if (Handles(type) || type == MessageType.PauseState || type == MessageType.PauseRequest || type == MessageType.LogMarker)
                return false;
            return _session.IsHost ? _joining.ContainsKey(peerId) || _waiting.Contains(peerId) : Loading;
        }

        private void OnMessage(int peerId, MessageType type, NetDataReader reader)
        {
            switch (type)
            {
                case MessageType.WorldRequest:
                    if (!_session.IsHost)
                        break;
                    _names[peerId] = reader.GetString(64);
                    _waiting.Add(peerId);
                    _log.LogInfo($"[World] {NameOf(peerId)} asked for the world.");
                    break;
                case MessageType.WorldLoaded:
                    if (!_session.IsHost || !_joining.Remove(peerId))
                        break;
                    _log.LogInfo($"[World] {NameOf(peerId)} has loaded the world.");
                    PauseSync.Instance?.Notify($"{NameOf(peerId)} joined");
                    break;
                case MessageType.WorldRefused:
                {
                    if (!_session.IsHost)
                        break;
                    string reason = reader.GetString(256);
                    _joining.Remove(peerId);
                    _log.LogWarning($"[World] {NameOf(peerId)} can't join: {reason}");
                    PauseSync.Instance?.Notify($"{NameOf(peerId)} can't join: {reason}", 10f);
                    break;
                }
                case MessageType.WorldBegin:
                    if (!_session.IsHost)
                        OnWorldBegin(reader);
                    break;
                case MessageType.WorldChunk:
                    if (!_session.IsHost)
                        OnWorldChunk(reader);
                    break;
                case MessageType.WorldLeaving:
                    if (_session.IsHost)
                        break;
                    bool resync = reader.AvailableBytes > 0 && reader.GetBool();
                    bool nextDay = reader.AvailableBytes > 0 && reader.GetBool();
                    string joining = reader.AvailableBytes > 0 ? reader.GetString(256) : null;
                    PauseSync.Instance?.Notify(!string.IsNullOrEmpty(joining) ? $"{joining} is joining; reloading the host's world"
                        : nextDay ? "The host started the next day; loading it"
                        : resync ? "The host is resyncing; reloading the host's world" : "The host is loading another kitchen; you'll follow", 6f);
                    break;
            }
        }

        // ---------- Overlay ----------

        // Host, pause menu open, someone connected: RESYNC in the top right corner.
        private void DrawResyncButton()
        {
            if (!_session.IsHost || !_session.IsConnected || _names.Count == 0 || _joining.Count > 0 || !HostPauseMenuOpen())
                return;
            if (_resyncStyle == null)
            {
                _resyncStyle = new GUIStyle(GUI.skin.button) { fontSize = 22, fontStyle = FontStyle.Bold };
                _resyncStyle.normal.textColor = Color.white;
                _resyncStyle.hover.textColor = new Color(1f, 0.85f, 0.4f);
            }
            var rect = new Rect(Screen.width - 200f, 20f, 180f, 50f);
            if (GUI.Button(rect, "RESYNC", _resyncStyle))
                Resync();
        }

        public void OnGUI()
        {
            DrawResyncButton();
            string text = null;
            if (_session.IsHost && _joining.Count > 0 && _resyncing)
                text = $"Resyncing {string.Join(", ", _joining.Values)}\n\nYour game is paused until they've reloaded your world.";
            else if (_session.IsHost && _joining.Count > 0)
                text = $"{string.Join(", ", _joining.Values)} is joining\n\nYour game is paused until they've loaded in.";
            else if (!_session.IsHost && _incoming != null)
                text = $"Downloading the host's world... {100L * _received / Math.Max(1, _incoming.Length)}%";
            if (text == null)
                return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 24, wordWrap = true };
                _style.normal.textColor = Color.white;
            }
            var rect = new Rect(Screen.width / 2f - 300f, Screen.height / 2f - 70f, 600f, 140f);
            GUI.Box(rect, GUIContent.none, _style);
            GUI.Box(rect, text, _style);
        }
    }

    // Harmony entry points. Each forwards to WorldSync and never lets an exception reach the game.
    internal static class WorldPatches
    {
        public static bool SavePrefix()
        {
            try
            {
                return WorldSync.Instance == null || !WorldSync.Instance.BlockSave();
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [World] save patch error: " + e);
                return true;
            }
        }

        public static void OpenMainMenuPrefix()
        {
            try
            {
                WorldSync.Instance?.OnOpenMainMenu();
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [World] menu patch error: " + e);
            }
        }
    }
}
