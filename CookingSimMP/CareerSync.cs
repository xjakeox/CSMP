using System;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using WIUtils;

namespace CookingSimMP
{
    // Career pieces from careersync.md (v0.11):
    //
    // End of day (section 6): the host runs the end of day on its own. The client never ends its day: it shows
    // "The host is wrapping up the day" and ignores the host's clock until the host's next day has started, then the
    // host sends its world again (the same reload as RESYNC), which brings the new day's level, perks, recipes,
    // quests and clock in one step. Players who ask to join while the host is ending the day get the world once the
    // next day has started.
    //
    // Host leaves (section 9): a client in the host's world goes back to the main menu, since that kitchen is
    // nobody's save. Pressing F12 yourself keeps the old behaviour (you stay where you are).
    //
    // Perks during the day (section 3): using a once-per-day perk uses it up on both copies. Perks that change orders
    // (reroll, reset dish time, extra time) run on the host when the client uses them, since the host owns the
    // orders; the result comes back with the next order list. The dish analyzer only opens a window, so it runs
    // where it was used.
    //
    // Bankrupt (section 8): only the host decides; the client plays the same bankrupt screen when told.
    internal sealed class CareerSync
    {
        private const float WaitTimeout = 600f;   // Client: stop waiting for the host's next day after this long.

        internal static CareerSync Instance;

        private static bool _applying;   // Applying the other copy's perk or bankrupt; don't send it back.

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".career");

        private static readonly AccessTools.FieldRef<MultiplePerDayPerk, int> UsageCounterRef = UsageField();

        // Host side.
        private bool _hostWasEndingDay;

        // Client side.
        private bool _waitingForDay;
        private float _waitedFor;
        private bool _goToMenu;
        // v0.11.4: after going back to the menu because the host left, keep the cursor free for a moment. The game
        // only expects the menu to be opened from the pause menu, where the cursor is already free, so it stayed
        // locked and hidden in the menu.
        private bool _freeCursorInMenu;
        private float _menuCursorFor;
        private const float MenuCursorTime = 10f;   // Covers the loading screen too.
        private GUIStyle _style;

        private static AccessTools.FieldRef<MultiplePerDayPerk, int> UsageField()
        {
            try
            {
                return AccessTools.FieldRefAccess<MultiplePerDayPerk, int>("usageCounter");
            }
            catch (Exception)
            {
                return null;
            }
        }

        public CareerSync(NetSession session, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.HostLost += OnHostLost;
            _session.Stopped += Reset;
            _session.WorldReset += Reset;

            Patch(typeof(StandardDayFlowManager), nameof(StandardDayFlowManager.EndDay), nameof(EndDayPrefix));
            Patch(typeof(OncePerDayPerk), "PerkUsed", null, nameof(OncePerkUsedPostfix));
            Patch(typeof(MultiplePerDayPerk), nameof(MultiplePerDayPerk.Use), null, nameof(MultiplePerkUsePostfix));
            Patch(typeof(RerollPerk), nameof(RerollPerk.Execute), nameof(RecipePerkPrefix));
            Patch(typeof(ResetDishTimePerk), nameof(ResetDishTimePerk.Execute), nameof(RecipePerkPrefix));
            Patch(typeof(TimeEnhancerPerk), nameof(TimeEnhancerPerk.Execute), nameof(TimePerkPrefix));
            Patch(typeof(BankruptManager), nameof(BankruptManager.CheckConditionsAndExecute), nameof(BankruptCheckPrefix));
            Patch(typeof(BankruptManager), nameof(BankruptManager.ExecuteBankrupt), null, nameof(ExecuteBankruptPostfix));
            if (UsageCounterRef == null)
                _log.LogWarning("[Career] MultiplePerDayPerk.usageCounter not found; perks with several uses a day aren't shared.");
        }

        private void Patch(Type type, string method, string prefix, string postfix = null)
        {
            try
            {
                _harmony.Patch(AccessTools.Method(type, method),
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(CareerSync), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(CareerSync), postfix));
            }
            catch (Exception e)
            {
                _log.LogError($"[Career] Patching {type.Name}.{method} failed: {e}");
            }
        }

        private void Reset()
        {
            _waitingForDay = false;
            _waitedFor = 0f;
            _hostWasEndingDay = false;
        }

        private static bool IsSandbox => Singleton<GameManager>.Me != null && Singleton<GameManager>.Me.GameState.HasFlag(GameState.Sandbox);

        private bool Active => _session.IsConnected && WorldSync.InKitchen && !IsSandbox;

        private bool ClientFollows => Active && !_session.IsHost;

        // The host is in the end-of-day sequence (career only; sandbox never finishes it).
        internal static bool HostEndingDay =>
            !IsSandbox && Game.CurrentGameService?.DayFlow != null && Game.CurrentGameService.DayFlow.DuringNextDayCoroutine;

        // Client: the host is ending the day; the clock holds until its next day arrives with the world.
        internal static bool ClientWaitingForDay => Instance != null && Instance._waitingForDay;

        public void Tick(float deltaTime)
        {
            if (_goToMenu)
                GoToMenu();
            FreeCursorInMenu(deltaTime);
            if (!_session.IsRunning)
                return;
            if (_session.IsHost)
            {
                TickHost();
                return;
            }
            if (_waitingForDay)
            {
                _waitedFor += deltaTime;
                if (WorldSync.Loading || _waitedFor > WaitTimeout)
                    _waitingForDay = false;
            }
        }

        // ---------- End of day ----------

        private void TickHost()
        {
            bool ending = Active && HostEndingDay;
            if (_hostWasEndingDay && !ending && _session.IsConnected && WorldSync.HostInKitchen && !IsSandbox)
            {
                _log.LogInfo("[Career] The next day has started; sending it to the other players.");
                WorldSync.Instance?.ResyncForNextDay();
            }
            _hostWasEndingDay = ending;
        }

        // Client: the clock reached the end of the day. Only the host ends it; this copy waits for the next day.
        private bool AllowEndDay()
        {
            if (_applying || !ClientFollows)
                return true;
            if (!_waitingForDay)
            {
                _waitingForDay = true;
                _waitedFor = 0f;
                _log.LogInfo("[Career] The day is over; waiting for the host to start the next one.");
            }
            return false;
        }

        // ---------- Host leaves ----------

        private void OnHostLost()
        {
            if (!WorldSync.PlayingHostWorld)
                return;
            // Not from inside the network callback: next frame, once networking has stopped.
            _goToMenu = true;
            _log.LogInfo("[Career] The host left; going back to the main menu.");
        }

        private void GoToMenu()
        {
            if (WorldSync.Loading)
                return;
            _goToMenu = false;
            _waitingForDay = false;
            GameManager game = Singleton<GameManager>.Me;
            if (game == null || !GameManager.DuringGame)
                return;
            try
            {
                if (GameManager.Paused)
                    game.UnpauseTime();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _freeCursorInMenu = true;
                _menuCursorFor = 0f;
                game.OpenMainMenu();
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Career] Going back to the main menu failed: {e.Message}");
            }
        }

        private void FreeCursorInMenu(float deltaTime)
        {
            if (!_freeCursorInMenu || GameManager.DuringGame || WorldSync.Loading)
                return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _menuCursorFor += deltaTime;
            if (_menuCursorFor >= MenuCursorTime)
                _freeCursorInMenu = false;
        }

        // ---------- Perks ----------

        private void OnLocalPerkUsed(BasicPerk perk, int uses)
        {
            if (_applying || !Active || perk == null)
                return;
            NetDataWriter writer = _session.BeginMessage(MessageType.PerkUsed);
            writer.Put(perk.ID);
            writer.Put(uses);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"[Career] Used perk {perk.GetType().Name} ({perk.ID}); told the other player.");
        }

        private void OnRemotePerkUsed(int perkId, int uses)
        {
            if (!SkillSystem.TryGetPerkStatic<BasicPerk>(perkId, out BasicPerk perk))
            {
                _log.LogWarning($"[Career] The other player used perk {perkId}, which this copy doesn't have.");
                return;
            }
            _applying = true;
            try
            {
                if (perk is OncePerDayPerk once)
                {
                    if (!once.WasUsed)
                        once.ForceSetWasUSed(true);
                }
                else if (perk is MultiplePerDayPerk multiple && UsageCounterRef != null && uses >= 0)
                {
                    UsageCounterRef(multiple) = uses;
                    multiple.PerkUsed?.Invoke(multiple.CanBeUse);
                }
            }
            finally
            {
                _applying = false;
            }
            _log.LogInfo($"[Career] The other player used perk {perk.GetType().Name} ({perkId}).");
        }

        // Client: a perk that changes orders asks the host to run it, and is used up here at once.
        private bool AllowRecipePerk(OncePerDayPerk perk, Recipe recipe)
        {
            if (_applying || !ClientFollows || perk == null || !perk.IsUnlock)
                return true;
            NetDataWriter writer = _session.BeginMessage(MessageType.PerkRequest);
            writer.Put(perk.ID);
            writer.Put(recipe != null ? recipe.globalId : -1);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _applying = true;
            try
            {
                perk.ForceSetWasUSed(true);
            }
            finally
            {
                _applying = false;
            }
            _log.LogInfo($"[Career] Asked the host to use perk {perk.GetType().Name} ({perk.ID}){(recipe != null ? $" on order {recipe.globalId}" : "")}.");
            return false;
        }

        private void OnPerkRequest(int peerId, int perkId, int globalId)
        {
            if (!_session.IsHost)
                return;
            string who = WorldSync.Instance?.NameOf(peerId) ?? "The other player";
            if (!SkillSystem.TryGetPerkStatic<OncePerDayPerk>(perkId, out OncePerDayPerk perk) || !perk.IsUnlock)
            {
                _log.LogWarning($"[Career] {who} used perk {perkId}, which isn't unlocked here.");
                return;
            }
            if (perk.WasUsed)
            {
                _log.LogInfo($"[Career] {who} used perk {perk.GetType().Name}, but it was already used today.");
                PauseSync.Instance?.Notify("That perk was already used today");
                return;
            }
            Recipe recipe = null;
            RecipesManager manager = TemporalSingleton<RecipesManager>.Me;
            if (globalId >= 0 && manager != null)
                recipe = manager.currentRecipes.Find(r => r != null && r.globalId == globalId);
            try
            {
                switch (perk)
                {
                    case RerollPerk reroll:
                        if (recipe == null)
                            break;
                        reroll.Execute(recipe);
                        break;
                    case ResetDishTimePerk reset:
                        if (recipe == null)
                            break;
                        reset.Execute(recipe);
                        break;
                    case TimeEnhancerPerk time:
                        time.Execute();
                        break;
                    default:
                        _log.LogWarning($"[Career] {who} asked to use perk {perk.GetType().Name}, which isn't run for others.");
                        return;
                }
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Career] Running perk {perk.GetType().Name} for {who} failed: {e.Message}");
                return;
            }
            if (recipe == null && !(perk is TimeEnhancerPerk))
            {
                _log.LogWarning($"[Career] {who} used {perk.GetType().Name} on order {globalId}, which is no longer open here.");
                return;
            }
            PauseSync.Instance?.Notify($"{who} used a perk");
            _log.LogInfo($"[Career] Used perk {perk.GetType().Name} for {who}{(recipe != null ? $" on order {globalId}" : "")}.");
        }

        // ---------- Bankrupt ----------

        private void OnLocalBankrupt()
        {
            if (_applying || !_session.IsHost || !Active)
                return;
            _session.BeginMessage(MessageType.Bankrupt);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo("[Career] Went bankrupt; told the other player.");
        }

        private void OnRemoteBankrupt()
        {
            BankruptManager manager = TemporalSingleton<BankruptManager>.Me;
            if (manager == null)
                return;
            _applying = true;
            try
            {
                manager.ExecuteBankrupt();
            }
            finally
            {
                _applying = false;
            }
            _log.LogInfo("[Career] The host went bankrupt.");
        }

        // ---------- Messages ----------

        private void OnMessage(int peerId, MessageType type, NetDataReader reader)
        {
            switch (type)
            {
                case MessageType.PerkUsed:
                {
                    int perkId = reader.GetInt();
                    int uses = reader.GetInt();
                    if (Active)
                        OnRemotePerkUsed(perkId, uses);
                    break;
                }
                case MessageType.PerkRequest:
                {
                    int perkId = reader.GetInt();
                    int globalId = reader.GetInt();
                    if (Active)
                        OnPerkRequest(peerId, perkId, globalId);
                    break;
                }
                case MessageType.Bankrupt:
                    if (ClientFollows)
                        OnRemoteBankrupt();
                    break;
            }
        }

        // ---------- Overlay ----------

        public void OnGUI()
        {
            if (!_waitingForDay || !_session.IsConnected)
                return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 24, wordWrap = true };
                _style.normal.textColor = Color.white;
            }
            var rect = new Rect(Screen.width / 2f - 300f, Screen.height / 2f - 70f, 600f, 140f);
            GUI.Box(rect, GUIContent.none, _style);
            GUI.Box(rect, "The host is wrapping up the day\n\nYou'll load into the next day when they're done.", _style);
        }

        // ---------- Harmony ----------

        private static bool EndDayPrefix()
        {
            try
            {
                return Instance == null || Instance.AllowEndDay();
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Career] end-of-day patch error: " + e);
                return true;
            }
        }

        private static void OncePerkUsedPostfix(OncePerDayPerk __instance)
        {
            try
            {
                Instance?.OnLocalPerkUsed(__instance, -1);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Career] perk patch error: " + e);
            }
        }

        private static void MultiplePerkUsePostfix(MultiplePerDayPerk __instance, bool __result)
        {
            if (!__result)
                return;
            try
            {
                Instance?.OnLocalPerkUsed(__instance, __instance.UsageCounter);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Career] perk patch error: " + e);
            }
        }

        private static bool RecipePerkPrefix(OncePerDayPerk __instance, Recipe __0)
        {
            try
            {
                return Instance == null || Instance.AllowRecipePerk(__instance, __0);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Career] perk patch error: " + e);
                return true;
            }
        }

        private static bool TimePerkPrefix(OncePerDayPerk __instance)
        {
            try
            {
                return Instance == null || Instance.AllowRecipePerk(__instance, null);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Career] perk patch error: " + e);
                return true;
            }
        }

        private static bool BankruptCheckPrefix()
        {
            CareerSync sync = Instance;
            return _applying || sync == null || !sync.ClientFollows;
        }

        private static void ExecuteBankruptPostfix()
        {
            try
            {
                Instance?.OnLocalBankrupt();
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Career] bankrupt patch error: " + e);
            }
        }
    }
}
