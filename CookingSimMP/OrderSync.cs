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
    internal sealed class OrderSync
    {
        private const float SendInterval = 0.5f;
        private const float KeepEndedFor = 5f;   // Client: an order it just served may still be in the host's next list.

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
            Patch(typeof(StandardTimeManager), nameof(StandardTimeManager.SkipToCleaning), postfix: nameof(OrderPatches.SkipToCleaningPostfix));
        }

        public static bool Handles(MessageType type) =>
            type == MessageType.OrderList || type == MessageType.OrderEnded || type == MessageType.KitchenClosed || type == MessageType.OrderRequest;

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
        }

        // The client follows the host's orders instead of running its own.
        internal bool ClientFollows => _session.IsConnected && !_session.IsHost && WorldSync.InKitchen;

        private static RecipesManager Manager => TemporalSingleton<RecipesManager>.Me;

        public void Tick(float deltaTime)
        {
            _clock += deltaTime;
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

        internal void OnLocalOrderEnded(Recipe recipe, bool failed, Container container, int fameDelta)
        {
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
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        private void OnRemoteOrderEnded(NetPacketReader reader)
        {
            int globalId = reader.GetInt();
            bool failed = reader.GetBool();
            int fameDelta = reader.GetInt();
            int plateId = reader.GetInt();

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
                manager.EndRecipe(recipe, RequirementOverrides.None, failed, null, -1, ignoreScoresAndDiscard: true, discard: true);
            }
            finally
            {
                _applying = false;
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

        // Remembers whether the order is live and the fame before scoring, so the postfix can tell what changed.
        public static void EndRecipePrefix(RecipesManager __instance, Recipe recipe, bool removeFromCurrent, out int __state)
        {
            __state = int.MinValue;
            try
            {
                if (removeFromCurrent && recipe != null && __instance.currentRecipes.Contains(recipe))
                    __state = PlayerStats.Me != null ? PlayerStats.Me.fame : 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Orders] end patch error: " + e);
            }
        }

        public static void EndRecipePostfix(Recipe recipe, bool failed, Container container, int __state)
        {
            if (__state == int.MinValue)
                return;
            try
            {
                int fameDelta = PlayerStats.Me != null ? PlayerStats.Me.fame - __state : 0;
                OrderSync.Instance?.OnLocalOrderEnded(recipe, failed, container, fameDelta);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Orders] end patch error: " + e);
            }
        }
    }
}
