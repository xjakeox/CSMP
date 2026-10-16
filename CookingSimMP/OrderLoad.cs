using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CookingSimMP
{
    // v0.11: more orders when more people cook. The game draws a new order whenever the open orders' total workload
    // (RecipesManager.CurrentDishIntensity) is under the fame level's minimum, and only picks orders that keep it
    // under the maximum. While the host's RecipesManager.Update runs, that total is divided by 1 + 0.5 per client,
    // so the kitchen takes 1.5x the work at once with two players and orders keep coming at that rate.
    //
    // Host only (the client draws no orders), not in sandbox (orders there are picked by hand), and only while a
    // client is connected. The host can turn it off from its pause menu; the setting is saved in the config file.
    internal sealed partial class OrderSync
    {
        private const float ExtraPerClient = 0.5f;

        private readonly ConfigEntry<bool> _moreOrders;
        private static float _loadDivisor = 1f;   // Above 1 only during the host's RecipesManager.Update.
        private GUIStyle _toggleStyle;

        private void PatchMoreOrders()
        {
            Patch(typeof(RecipesManager), "Update", prefix: nameof(OrderPatches.MoreOrdersUpdatePrefix), postfix: nameof(OrderPatches.MoreOrdersUpdatePostfix));
            try
            {
                _harmony.Patch(AccessTools.PropertyGetter(typeof(RecipesManager), nameof(RecipesManager.CurrentDishIntensity)),
                    postfix: new HarmonyMethod(typeof(OrderPatches), nameof(OrderPatches.CurrentDishIntensityPostfix)));
            }
            catch (Exception e)
            {
                _log.LogError($"[Orders] Patching CurrentDishIntensity failed; the extra orders for more players are off: {e}");
            }
        }

        // How much more work the kitchen takes at once right now (1 = the game's own amount).
        internal float OrderMultiplier
        {
            get
            {
                if (_moreOrders == null || !_moreOrders.Value || !_session.IsHost || !_session.IsConnected || IsSandbox)
                    return 1f;
                return 1f + ExtraPerClient * _session.PeerCount;
            }
        }

        internal static void BeginOrderDraw()
        {
            _loadDivisor = Instance != null ? Instance.OrderMultiplier : 1f;
        }

        internal static void EndOrderDraw() => _loadDivisor = 1f;

        internal static float ScaleLoad(float load) => _loadDivisor > 1f ? load / _loadDivisor : load;

        // Host pause menu, under RESYNC: turns the extra orders on or off.
        public void OnGUI()
        {
            if (_moreOrders == null || !_session.IsHost || !_session.IsRunning || !WorldSync.HostPauseMenuOpen())
                return;
            if (_toggleStyle == null)
            {
                _toggleStyle = new GUIStyle(GUI.skin.button) { fontSize = 16, fontStyle = FontStyle.Bold, wordWrap = true };
                _toggleStyle.normal.textColor = Color.white;
                _toggleStyle.hover.textColor = new Color(1f, 0.85f, 0.4f);
            }
            int players = 1 + _session.PeerCount;
            string label = _moreOrders.Value
                ? $"More orders: ON ({1f + ExtraPerClient * Math.Max(1, players - 1):0.#}x with {Math.Max(2, players)} players)"
                : "More orders: OFF (single-player amount)";
            var rect = new Rect(Screen.width - 380f, 80f, 360f, 44f);
            if (GUI.Button(rect, label, _toggleStyle))
            {
                _moreOrders.Value = !_moreOrders.Value;
                _log.LogInfo($"[Orders] Host turned the extra orders for more players {(_moreOrders.Value ? "on" : "off")}.");
                PauseSync.Instance?.Notify(_moreOrders.Value ? "More orders with more players: on" : "More orders with more players: off");
            }
        }
    }

    internal static partial class OrderPatches
    {
        public static void MoreOrdersUpdatePrefix()
        {
            try
            {
                OrderSync.BeginOrderDraw();
            }
            catch (Exception e)
            {
                OrderSync.EndOrderDraw();
                Debug.LogError("[CookingSimMP] [Orders] more-orders patch error: " + e);
            }
        }

        public static void MoreOrdersUpdatePostfix() => OrderSync.EndOrderDraw();

        public static void CurrentDishIntensityPostfix(ref float __result) => __result = OrderSync.ScaleLoad(__result);
    }
}
