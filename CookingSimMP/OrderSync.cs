using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using CS.GUI;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using Recipes;
using UnityEngine;
using WIUtils;

namespace CookingSimMP
{
    // Customer orders (career and other classic kitchens) are kept by the host. The host draws new orders, runs
    // their timers and fails late ones; the client draws none of its own and copies the host's list twice a second.
    // Each order is identified by the game's own Recipe.globalId, which the save keeps, so the orders the client
    // loaded with the host's world already match.
    //
    // Serving: whoever serves scores the dish on their copy as usual (money goes through MoneySync), then tells the
    // other copy, which drops the order without scoring it again and removes the served plate. A client's fame change
    // goes to the host, and the host's fame is copied back to the client.
    //
    // v0.10: the served dish's record (recipe history, the cooking points it earned, its score) goes with that
    // message, and the other copy adds it too. So a dish the client serves counts in the host's save (the day's XP is
    // worked out from the recipe history; daily quests and the recipe-unlock average hear about it), and both players
    // get the achievement progress.
    //
    // Two players serving the same order: the host decides. A client's checkout first asks the host for the order
    // (OrderClaim); the host says yes unless the order is gone or someone else is checking it out. Whoever is
    // refused gets "<name> is serving that order" and their plate back.
    internal sealed class OrderSync
    {
        private const float SendInterval = 0.5f;
        private const float KeepEndedFor = 5f;   // Client: an order it just served may still be in the host's next list.
        private const float ClaimTimeout = 15f;  // Host: a claim with no serve after this long is dropped.
        private const float ClaimWait = 3f;      // Client: serve anyway if the host doesn't answer within this long.

        internal static OrderSync Instance;

        // True while applying the other player's change, so the patches don't send it straight back.
        private static bool _applying;

        private readonly NetSession _session;
        private readonly ItemSync _items;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".orders");

        private readonly Dictionary<int, float> _endedHere = new Dictionary<int, float>();
        private readonly HashSet<int> _warnedMissing = new HashSet<int>();
        private float _sendTimer;
        private float _clock;

        // Host: orders someone is checking out: order -> (peer, or -1 for the host; when).
        private readonly Dictionary<int, KeyValuePair<int, float>> _claims = new Dictionary<int, KeyValuePair<int, float>>();

        // Client: a checkout waiting for the host's answer.
        private sealed class PendingServe
        {
            public int Seq;
            public CheckoutStand Stand;
            public Recipe Recipe;
            public Container Plate;
            public float Since;
        }

        private PendingServe _pendingServe;
        private int _nextClaimSeq = 1;
        private static bool _serveApproved;   // Client: replaying a checkout the host approved.

        // The other copy's score for the order being ended here, given to the game's "order ended" event in place of
        // the empty one an unscored EndRecipe makes.
        internal static RecipeScoreData RemoteScore;

        // What a served dish sends along (v0.10).
        private sealed class ServeRecord
        {
            public RecipeHistory H;
            public int CP;
            public bool D;     // Discarded (not kept in the history), as the game does in the Cooking Challenge.
        }

        // What the host sends for each order: the game's own save record of it, plus its time stage.
        private sealed class OrderState
        {
            public RecipeSave S;
            public RecipeTimeStage T;
        }

        private sealed class OrderList
        {
            public int Fame;
            public List<OrderState> O;
        }

        public OrderSync(NetSession session, ItemSync items, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _items = items;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.Stopped += Reset;
            _session.WorldReset += Reset;

            Patch(typeof(RecipesManager), "Update", prefix: nameof(OrderPatches.UpdatePrefix));
            // Orders the game starts outside Update: the day's booked orders and the critic on a juror day.
            Patch(typeof(RecipesManager), "StartDailyRecipes", prefix: nameof(OrderPatches.HostOnlyPrefix));
            Patch(typeof(RecipesManager), nameof(RecipesManager.ForceDrawCriticRecipe), prefix: nameof(OrderPatches.HostOnlyPrefix));
            // Sandbox: a recipe the client picks on its tablet goes to the host, which starts it for everyone.
            _harmony.Patch(AccessTools.Method(typeof(RecipesManager), nameof(RecipesManager.StartRecipe), new[] { typeof(Recipe), typeof(float), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(OrderPatches), nameof(OrderPatches.StartRecipePrefix)));
            Patch(typeof(RecipesManager), nameof(RecipesManager.EndRecipe), prefix: nameof(OrderPatches.EndRecipePrefix), postfix: nameof(OrderPatches.EndRecipePostfix));
            Patch(typeof(CheckoutStand), nameof(CheckoutStand.BringOutOrder), prefix: nameof(OrderPatches.BringOutOrderPrefix));
            Patch(typeof(RecipeEvents), nameof(RecipeEvents.EndRecipe), prefix: nameof(OrderPatches.RecipeEventsEndPrefix));
            Patch(typeof(StandardTimeManager), nameof(StandardTimeManager.SkipToCleaning), postfix: nameof(OrderPatches.SkipToCleaningPostfix));
        }

        public static bool Handles(MessageType type) =>
            type == MessageType.OrderList || type == MessageType.OrderEnded || type == MessageType.KitchenClosed || type == MessageType.OrderRequest
            || type == MessageType.OrderClaim;

        private static bool IsSandbox => Singleton<GameManager>.Me != null && Singleton<GameManager>.Me.GameState.HasFlag(GameState.Sandbox);

        // Client, sandbox only: instead of starting the order here (the host's next list would drop it), ask the
        // host to start it. Returns true when the request was sent and the local start should be skipped.
        internal bool TrySendOrderRequest(Recipe recipe, float customEstimatedTime)
        {
            if (_applying || !ClientFollows || !IsSandbox || recipe == null)
                return false;
#pragma warning disable CS0618 // The host looks the recipe up with GetRecipeTemplate, which still uses the old ID.
            int recipeId = recipe.Id;
#pragma warning restore CS0618
            NetDataWriter writer = _session.BeginMessage(MessageType.OrderRequest);
            writer.Put(recipeId);
            writer.Put(customEstimatedTime);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"[Orders] Asked the host to start order {recipeId}.");
            return true;
        }

        private void OnOrderRequest(NetPacketReader reader)
        {
            int recipeId = reader.GetInt();
            float customEstimatedTime = reader.GetFloat();
            if (!IsSandbox || Manager == null)
            {
                _log.LogWarning($"[Orders] The client asked for order {recipeId}, but this kitchen isn't a sandbox; ignored.");
                return;
            }
            Recipe recipe = Manager.StartRecipe(recipeId, customEstimatedTime);
            if (recipe == null)
                _log.LogWarning($"[Orders] The client asked for order {recipeId}, which this copy doesn't know.");
            else
                _sendTimer = SendInterval;   // Send the new list on the next frame.
        }

        private static readonly AccessTools.FieldRef<RecipesManager, float> ServeTimeRef =
            AccessTools.FieldRefAccess<RecipesManager, float>("serveTime");

        private void Patch(Type type, string method, string prefix = null, string postfix = null)
        {
            try
            {
                _harmony.Patch(AccessTools.Method(type, method),
                    prefix: prefix != null ? new HarmonyMethod(typeof(OrderPatches), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(OrderPatches), postfix) : null);
            }
            catch (Exception e)
            {
                _log.LogError($"[Orders] Patching {type.Name}.{method} failed; order sync is off: {e}");
            }
        }

        private void Reset()
        {
            _endedHere.Clear();
            _warnedMissing.Clear();
            _sendTimer = 0f;
            _claims.Clear();
            _pendingServe = null;
        }

        // The client follows the host's orders instead of running its own.
        internal bool ClientFollows => _session.IsConnected && !_session.IsHost && WorldSync.InKitchen;

        private static RecipesManager Manager => TemporalSingleton<RecipesManager>.Me;

        public void Tick(float deltaTime)
        {
            _clock += deltaTime;
            // The host never answered: serve anyway rather than leave the plate stuck in the checkout.
            if (_pendingServe != null && _clock - _pendingServe.Since > ClaimWait)
            {
                _log.LogWarning($"[Orders] The host didn't answer about order {_pendingServe.Recipe?.globalId}; serving it anyway.");
                FinishPendingServe(true, null);
            }
            if (!_session.IsConnected || !_session.IsHost || !WorldSync.InKitchen || Manager == null || Game.CurrentClassicGameService == null)
                return;
            _sendTimer += deltaTime;
            if (_sendTimer < SendInterval)
                return;
            _sendTimer = 0f;
            SendList();
        }

        // ---------- Host ----------

        private void SendList()
        {
            var list = new OrderList
            {
                Fame = PlayerStats.Me != null ? PlayerStats.Me.fame : 0,
                O = Manager.currentRecipes.Where(r => r != null)
                    .Select(r => new OrderState { S = new RecipeSave().Set(r), T = r.timeStage }).ToList(),
            };
            _session.BeginMessage(MessageType.OrderList).Put(JsonConvert.SerializeObject(list, SaveUtility.JsonSerializerSettings));
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        // ---------- Client ----------

        // Stands in for RecipesManager.Update on the client: no drawing, failing or closing, only the clocks tick
        // (the same way the game ticks them) so the timers count down smoothly between the host's lists.
        internal void TickClientTimers(RecipesManager manager)
        {
            StandardTimeManager time = Game.CurrentClassicGameService?.StandardTime;
            if (Singleton<GameManager>.Me == null || Game.KitchenServices.Stands == null || time == null || !manager.active
                || !Game.KitchenServices.Stands.SimTick
                || (time.GetCurrentDayTime != DayTimeType.Serve && time.GetCurrentDayTime != DayTimeType.Clean && !FirstPlayTutorial.Active))
                return;
            float tick = Game.KitchenServices.Stands.SimTickTime;
            ServeTimeRef(manager) += tick;
            if (time.IsRecipeTimeConstrained == false)
                return;
            foreach (Recipe recipe in manager.currentRecipes)
                recipe.currentTime += tick;
        }

        // Host: the game closed the kitchen for the day (no more customers), so the client skips to cleaning too.
        internal void OnHostSkippedToCleaning()
        {
            if (_applying || !_session.IsConnected || !_session.IsHost || !WorldSync.InKitchen)
                return;
            _session.BeginMessage(MessageType.KitchenClosed);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo("[Orders] Kitchen closed for the day; told the client.");
        }

        private void ApplyList(string json)
        {
            RecipesManager manager = Manager;
            if (manager == null)
                return;
            OrderList list = JsonConvert.DeserializeObject<OrderList>(json, SaveUtility.JsonSerializerSettings);
            if (list?.O == null)
                return;

            foreach (int id in _endedHere.Where(p => _clock - p.Value > KeepEndedFor).Select(p => p.Key).ToList())
                _endedHere.Remove(id);

            var hostIds = new HashSet<int>(list.O.Select(o => o.S.globalId));
            _applying = true;
            try
            {
                // Orders the host no longer has (served or failed there).
                foreach (Recipe recipe in manager.currentRecipes.ToList())
                {
                    if (!hostIds.Contains(recipe.globalId) && !recipe.duringCheckout)
                        manager.EndRecipe(recipe, RequirementOverrides.None, failed: true, null, -1, ignoreScoresAndDiscard: true, discard: true);
                }

                foreach (OrderState state in list.O)
                {
                    Recipe recipe = manager.currentRecipes.FirstOrDefault(r => r.globalId == state.S.globalId);
                    if (recipe == null)
                    {
                        if (_endedHere.ContainsKey(state.S.globalId))
                            continue;
                        // Same as loading an order from a save; this also shows the "new order" notification.
#pragma warning disable CS0612, CS0618 // The save record still uses the old recipe ID.
                        recipe = manager.StartRecipe(state.S.id, state.S.estimatedTime);
                        if (recipe == null)
                        {
                            if (_warnedMissing.Add(state.S.id))
                                _log.LogWarning($"[Orders] The host has an order for recipe {state.S.id}, which this copy doesn't know.");
                            continue;
                        }
#pragma warning restore CS0612, CS0618
                        recipe.ApplySavePack(state.S);
                    }
                    else
                    {
                        recipe.currentTime = state.S.currentTime;
                        recipe.EstimatedTime = state.S.estimatedTime;
                    }

                    if (recipe.timeStage != state.T)
                    {
                        recipe.timeStage = state.T;
                        if (state.T == RecipeTimeStage.NEAR_END)
                            GUIKitchen.Me?.notifications?.Notify(NotificationType.NearEndOrder, recipe);
                        else if (state.T == RecipeTimeStage.OVERTIME)
                            GUIKitchen.Me?.notifications?.Notify(NotificationType.OvertimeOrder, recipe);
                    }
                }
            }
            finally
            {
                _applying = false;
            }

            if (PlayerStats.Me != null)
                PlayerStats.Me.fame = list.Fame;
        }

        // ---------- Both: an order ended on this copy ----------

        // ---------- Both: checking out an order (claims) ----------

        // CheckoutStand.BringOutOrder prefix. Returns false to hold (client, waiting for the host) or refuse it.
        internal bool AllowBringOut(CheckoutStand stand, Recipe recipe, Container plate)
        {
            if (_applying || _serveApproved || recipe == null || !_session.IsConnected || !WorldSync.InKitchen || Manager == null)
                return true;
            if (_session.IsHost)
            {
                if (_claims.TryGetValue(recipe.globalId, out KeyValuePair<int, float> claim) && claim.Key != -1 && _clock - claim.Value < ClaimTimeout)
                {
                    Refuse(stand, WorldSync.Instance?.NameOf(claim.Key) ?? "The other player");
                    return false;
                }
                _claims[recipe.globalId] = new KeyValuePair<int, float>(-1, _clock);
                return true;
            }
            if (_pendingServe != null)
                return false;
            _pendingServe = new PendingServe { Seq = _nextClaimSeq++, Stand = stand, Recipe = recipe, Plate = plate, Since = _clock };
            NetDataWriter writer = _session.BeginMessage(MessageType.OrderClaim);
            writer.Put(recipe.globalId);
            writer.Put(_pendingServe.Seq);
            writer.Put(false);
            writer.Put("");
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            return false;
        }

        // Puts the plate back in the player's hand with a message saying who has the order.
        private void Refuse(CheckoutStand stand, string who)
        {
            PauseSync.Instance?.Notify($"{who} is serving that order");
            _log.LogInfo($"[Orders] Checkout refused: {who} is serving that order.");
            try
            {
                if (stand != null)
                    stand.ExitCheckout();
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Orders] Leaving the checkout failed: {e.Message}");
            }
        }

        // Host: a client asks to serve an order.
        private void OnClaimRequest(int peerId, int globalId, int seq)
        {
            Recipe recipe = Manager?.currentRecipes.FirstOrDefault(r => r.globalId == globalId);
            string refusedBy = null;
            if (recipe == null)
                refusedBy = "Someone else";
            else if (_claims.TryGetValue(globalId, out KeyValuePair<int, float> claim) && claim.Key != peerId && _clock - claim.Value < ClaimTimeout)
                refusedBy = claim.Key == -1 ? PauseSync.LocalName() : WorldSync.Instance?.NameOf(claim.Key) ?? "The other player";
            if (refusedBy == null)
                _claims[globalId] = new KeyValuePair<int, float>(peerId, _clock);
            NetDataWriter writer = _session.BeginMessage(MessageType.OrderClaim);
            writer.Put(globalId);
            writer.Put(seq);
            writer.Put(refusedBy == null);
            writer.Put(refusedBy ?? "");
            _session.SendTo(peerId, DeliveryMethod.ReliableOrdered);
        }

        // Client: the host's answer.
        private void OnClaimAnswer(int globalId, int seq, bool granted, string refusedBy)
        {
            if (_pendingServe == null || _pendingServe.Seq != seq || _pendingServe.Recipe?.globalId != globalId)
                return;
            FinishPendingServe(granted, refusedBy);
        }

        private void FinishPendingServe(bool serve, string refusedBy)
        {
            PendingServe pending = _pendingServe;
            _pendingServe = null;
            if (pending.Stand == null)
                return;
            if (!serve)
            {
                Refuse(pending.Stand, string.IsNullOrEmpty(refusedBy) ? "The other player" : refusedBy);
                return;
            }
            _serveApproved = true;
            try
            {
                pending.Stand.BringOutOrder(pending.Recipe, pending.Plate);
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Orders] Serving order {pending.Recipe?.globalId} failed: {e.Message}");
            }
            finally
            {
                _serveApproved = false;
            }
        }

        // ---------- Both: an order ended on this copy ----------

        internal void OnLocalOrderEnded(Recipe recipe, bool failed, Container container, int fameDelta, RecipeScoreData score, int cpDelta, bool scored, bool discard)
        {
            _claims.Remove(recipe.globalId);
            if (_applying || !_session.IsConnected || !WorldSync.InKitchen)
                return;
            // A client never fails orders on its own; its day ending or skipping must not cancel the host's orders.
            if (!_session.IsHost && failed)
                return;
            if (!_session.IsHost)
                _endedHere[recipe.globalId] = _clock;

            int plateId = container != null && _items.TryGetNetId(container, out int id) ? id : -1;
            NetDataWriter writer = _session.BeginMessage(MessageType.OrderEnded);
            writer.Put(recipe.globalId);
            writer.Put(failed);
            writer.Put(_session.IsHost ? 0 : fameDelta);
            writer.Put(plateId);
            // The dish's record and score, so the other copy counts it too (v0.10).
            string record = "", scoreJson = "";
            if (scored && !failed && score != null)
            {
                try
                {
                    record = JsonConvert.SerializeObject(new ServeRecord { H = score.GetRecipeHistory(), CP = cpDelta, D = discard }, SaveUtility.JsonSerializerSettings);
                }
                catch (Exception e)
                {
                    _log.LogWarning($"[Orders] Couldn't send the served dish's record: {e.Message}");
                }
                try
                {
                    scoreJson = JsonConvert.SerializeObject(score, SaveUtility.JsonSerializerSettings);
                }
                catch (Exception e)
                {
                    _log.LogInfo($"[Orders] Couldn't send the served dish's score details ({e.Message}); daily quests get the record only.");
                }
            }
            writer.Put(record);
            writer.Put(scoreJson);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        private void OnRemoteOrderEnded(NetPacketReader reader)
        {
            int globalId = reader.GetInt();
            bool failed = reader.GetBool();
            int fameDelta = reader.GetInt();
            int plateId = reader.GetInt();
            string record = reader.GetString();
            string scoreJson = reader.GetString();
            _claims.Remove(globalId);

            // The served plate leaves the kitchen on the copy that served it.
            if (plateId >= 0)
                _items.RemoveServedPlate(plateId);

            if (_session.IsHost && fameDelta != 0 && PlayerStats.Me != null)
                PlayerStats.Me.ChangeFame(fameDelta);

            RecipesManager manager = Manager;
            Recipe recipe = manager?.currentRecipes.FirstOrDefault(r => r.globalId == globalId);
            if (recipe == null)
                return;
            _applying = true;
            try
            {
                if (failed)
                {
                    recipe.timeStage = RecipeTimeStage.FAILURE;
                    GUIKitchen.Me?.notifications?.Notify(NotificationType.CancelledOrder, recipe);
                }
                RemoteScore = ReadScore(scoreJson);
                manager.EndRecipe(recipe, RequirementOverrides.None, failed, null, -1, ignoreScoresAndDiscard: true, discard: true);
            }
            finally
            {
                RemoteScore = null;
                _applying = false;
            }
            if (!failed)
                CountRemoteServe(record);
        }

        private RecipeScoreData ReadScore(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                return JsonConvert.DeserializeObject<RecipeScoreData>(json, SaveUtility.JsonSerializerSettings);
            }
            catch (Exception e)
            {
                _log.LogInfo($"[Orders] Couldn't read the other player's score details: {e.Message}");
                return null;
            }
        }

        // The other copy's served dish counts here too: recipe history (the day's XP comes from it), cooking points
        // and the achievement progress, the same calls RecipesManager.EndRecipe makes when it scores a dish.
        private void CountRemoteServe(string json)
        {
            if (string.IsNullOrEmpty(json) || PlayerStats.Me == null)
                return;
            try
            {
                ServeRecord record = JsonConvert.DeserializeObject<ServeRecord>(json, SaveUtility.JsonSerializerSettings);
                if (record == null)
                    return;
                if (record.CP != 0)
                    PlayerStats.Me.AddCookingPoints(record.CP);
                if (record.H != null && !record.D)
                {
                    record.H.dayNum = PlayerStats.Me.DayNum;
                    PlayerStats.Me.recipesHistory.Add(record.H);
                    Singleton<Achievements.AchievementsManager>.Me?.ExecuteEvent(Achievements.AchievementsManager.Action.RecipeDone, record.H);
                }
                _log.LogInfo($"[Orders] Counted the other player's served dish here: {record.H?.stars} stars, {record.CP} cooking points.");
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Orders] Counting the other player's served dish failed: {e.Message}");
            }
        }

        // ---------- Messages ----------

        private void OnMessage(int peerId, MessageType type, NetPacketReader reader)
        {
            try
            {
                switch (type)
                {
                    case MessageType.OrderList:
                        if (!_session.IsHost && WorldSync.InKitchen)
                            ApplyList(reader.GetString());
                        break;
                    case MessageType.OrderEnded:
                        if (WorldSync.InKitchen)
                            OnRemoteOrderEnded(reader);
                        break;
                    case MessageType.OrderRequest:
                        if (_session.IsHost && WorldSync.InKitchen)
                            OnOrderRequest(reader);
                        break;
                    case MessageType.OrderClaim:
                    {
                        int globalId = reader.GetInt();
                        int seq = reader.GetInt();
                        bool granted = reader.GetBool();
                        string refusedBy = reader.GetString(64);
                        if (_session.IsHost)
                            OnClaimRequest(peerId, globalId, seq);
                        else
                            OnClaimAnswer(globalId, seq, granted, refusedBy);
                        break;
                    }
                    case MessageType.KitchenClosed:
                        if (!_session.IsHost && WorldSync.InKitchen && Game.CurrentClassicGameService?.StandardTime != null)
                        {
                            _applying = true;
                            try { Game.CurrentClassicGameService.StandardTime.SkipToCleaning(); }
                            finally { _applying = false; }
                            _log.LogInfo("[Orders] The host closed the kitchen for the day; skipped to cleaning.");
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Orders] Applying the other player's orders failed: {e.Message}");
            }
        }
    }

    // Harmony entry points. Each forwards to OrderSync and never lets an exception reach the game.
    internal static class OrderPatches
    {
        public static bool BringOutOrderPrefix(CheckoutStand __instance, Recipe selected, Container container)
        {
            try
            {
                return OrderSync.Instance == null || OrderSync.Instance.AllowBringOut(__instance, selected, container);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Orders] checkout patch error: " + e);
                return true;
            }
        }

        // An order the other player served ends here unscored; its "ended" event gets their score instead of an
        // empty one, so daily quests and the recipe-unlock average count it.
        public static void RecipeEventsEndPrefix(ref RecipeScoreData __0)
        {
            if (OrderSync.RemoteScore != null)
                __0 = OrderSync.RemoteScore;
        }

        public static bool UpdatePrefix(RecipesManager __instance)
        {
            OrderSync sync = OrderSync.Instance;
            if (sync == null || !sync.ClientFollows)
                return true;
            try
            {
                sync.TickClientTimers(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Orders] timer patch error: " + e);
            }
            return false;
        }

        public static bool StartRecipePrefix(Recipe recipe, float customEstimatedTime, ref Recipe __result)
        {
            try
            {
                if (OrderSync.Instance != null && OrderSync.Instance.TrySendOrderRequest(recipe, customEstimatedTime))
                {
                    __result = null;
                    return false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Orders] start patch error: " + e);
            }
            return true;
        }

        // Leaves drawing orders to the host.
        public static bool HostOnlyPrefix()
        {
            OrderSync sync = OrderSync.Instance;
            return sync == null || !sync.ClientFollows;
        }

        public static void SkipToCleaningPostfix()
        {
            try
            {
                OrderSync.Instance?.OnHostSkippedToCleaning();
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Orders] cleaning patch error: " + e);
            }
        }

        // What EndRecipe's prefix saw: whether the order was live, and fame and cooking points before scoring.
        public struct EndState
        {
            public bool Live;
            public int Fame;
            public int CookingPoints;
        }

        // Remembers whether the order is live and the fame before scoring, so the postfix can tell what changed.
        public static void EndRecipePrefix(RecipesManager __instance, Recipe recipe, bool removeFromCurrent, out EndState __state)
        {
            __state = default;
            try
            {
                if (removeFromCurrent && recipe != null && __instance.currentRecipes.Contains(recipe))
                    __state = new EndState
                    {
                        Live = true,
                        Fame = PlayerStats.Me != null ? PlayerStats.Me.fame : 0,
                        CookingPoints = PlayerStats.Me != null ? PlayerStats.Me.cookingPoints : 0,
                    };
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Orders] end patch error: " + e);
            }
        }

        public static void EndRecipePostfix(Recipe recipe, bool failed, Container container, bool ignoreScoresAndDiscard, bool discard,
            RecipeScoreData __result, EndState __state)
        {
            if (!__state.Live)
                return;
            try
            {
                int fameDelta = PlayerStats.Me != null ? PlayerStats.Me.fame - __state.Fame : 0;
                int cpDelta = PlayerStats.Me != null ? PlayerStats.Me.cookingPoints - __state.CookingPoints : 0;
                OrderSync.Instance?.OnLocalOrderEnded(recipe, failed, container, fameDelta, __result, cpDelta, !ignoreScoresAndDiscard, discard);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Orders] end patch error: " + e);
            }
        }
    }
}
