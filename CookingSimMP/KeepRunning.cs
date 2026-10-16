using System;
using BepInEx.Logging;
using CS.GUI;
using CS.GUI.ScreenUI;
using HarmonyLib;
using Recipes;
using UnityEngine;

namespace CookingSimMP
{
    // v0.11.2: in single player, a few in-kitchen screens stop the game's clock while they're open: the score window
    // after serving (GUIFinalWindow), the dish analyzer (GUIDishAnalyzer) and the kitchen computer (ScreenGUI, which
    // uses PauseTimeScale). In a session the host's stop froze everyone ("paused by host"), so while connected and in
    // the kitchen those screens leave the clock running, the same way TimerSync does for the timer dial.
    // The pause menu is unchanged: it still pauses everyone.
    internal sealed class KeepRunning
    {
        internal static KeepRunning Instance;

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".keeprunning");

        // Inside the score window's or dish analyzer's OpenWindow, or a method of theirs that unpauses (CloseWindow, OK).
        private static int _inWindowOpen;
        private static int _inWindowClose;
        // A window's pause was skipped, so its unpause must be skipped too (it would reset the clock speed).
        private static int _skippedPauses;
        // The computer's PauseTimeScale was skipped; its UnPauseTimeScale is skipped too.
        private static bool _skippedScreenPause;

        public KeepRunning(NetSession session, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _log = log;
            _session.Stopped += Reset;
            _session.WorldReset += Reset;

            Patch(typeof(GUIFinalWindow), nameof(GUIFinalWindow.OpenWindow), nameof(OpenPrefix), nameof(OpenPostfix), new[] { typeof(RecipeScoreData), typeof(Recipe) });
            Patch(typeof(GUIFinalWindow), nameof(GUIFinalWindow.CloseWindow), nameof(ClosePrefix), nameof(ClosePostfix), Type.EmptyTypes);
            Patch(typeof(GUIFinalWindow), nameof(GUIFinalWindow.OK), nameof(ClosePrefix), nameof(ClosePostfix), Type.EmptyTypes);
            Patch(typeof(GUIDishAnalyzer), nameof(GUIDishAnalyzer.OpenWindow), nameof(OpenPrefix), nameof(OpenPostfix), new[] { typeof(Recipe) });
            Patch(typeof(GUIDishAnalyzer), nameof(GUIDishAnalyzer.CloseWindow), nameof(ClosePrefix), nameof(ClosePostfix), Type.EmptyTypes);
            Patch(typeof(GameManager), nameof(GameManager.PauseTime), nameof(PauseTimePrefix), null, Type.EmptyTypes);
            Patch(typeof(GameManager), nameof(GameManager.UnpauseTime), nameof(UnpauseTimePrefix), null, Type.EmptyTypes);
            Patch(typeof(GameManager), nameof(GameManager.PauseTimeScale), nameof(PauseTimeScalePrefix), null, Type.EmptyTypes);
            Patch(typeof(GameManager), nameof(GameManager.UnPauseTimeScale), nameof(UnPauseTimeScalePrefix), null, Type.EmptyTypes);
        }

        private void Patch(Type type, string method, string prefix, string postfix, Type[] args)
        {
            try
            {
                _harmony.Patch(AccessTools.Method(type, method, args),
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(KeepRunning), prefix) { priority = Priority.First },
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(KeepRunning), postfix));
            }
            catch (Exception e)
            {
                _log.LogError($"[KeepRunning] Patching {type.Name}.{method} failed; that screen still stops the clock: {e}");
            }
        }

        private static void Reset()
        {
            _skippedPauses = 0;
            _skippedScreenPause = false;
        }

        private bool Active => _session.IsConnected && WorldSync.InKitchen;

        private static bool ShouldKeepRunning => Instance != null && Instance.Active;

        // ---------- Harmony ----------

        private static void OpenPrefix() => _inWindowOpen++;

        private static void OpenPostfix() => _inWindowOpen = Math.Max(0, _inWindowOpen - 1);

        private static void ClosePrefix() => _inWindowClose++;

        private static void ClosePostfix() => _inWindowClose = Math.Max(0, _inWindowClose - 1);

        private static bool PauseTimePrefix()
        {
            if (_inWindowOpen == 0 || !ShouldKeepRunning)
                return true;
            _skippedPauses++;
            Instance._log.LogInfo("[KeepRunning] A score or analyzer window opened; the clock keeps running for everyone.");
            return false;
        }

        private static bool UnpauseTimePrefix()
        {
            if (_inWindowClose == 0 || _skippedPauses == 0)
                return true;
            _skippedPauses--;
            return false;
        }

        private static bool PauseTimeScalePrefix()
        {
            // Only the kitchen computer; other screens that stop the clock this way keep doing so.
            if (!ShouldKeepRunning || ScreenGUI.Me == null || !ScreenGUI.Me.IsActive)
                return true;
            _skippedScreenPause = true;
            Instance._log.LogInfo("[KeepRunning] The computer opened; the clock keeps running for everyone.");
            return false;
        }

        private static bool UnPauseTimeScalePrefix()
        {
            if (!_skippedScreenPause)
                return true;
            _skippedScreenPause = false;
            return false;
        }
    }
}
