using System;
using HarmonyLib;
using UnityEngine;

namespace CookingSimMP
{
    // v0.11.7: eggs. Cracking an egg (in the hand, on a pan's edge, or by throwing it) destroys it with
    // Object.Destroy and spawns what comes out of it: a fried or peeled egg, a yolk, and two shell halves. None of
    // that went through the paths the mod watches, so on the other screen the egg stayed whole (or vanished), no
    // shells appeared, and the egg that came out of it was missing.
    //
    // Now, on the copy where the egg cracks, the egg is reported removed and everything it spawns is sent as a new
    // item, like a purchase. The other copy never cracks an egg the other player is holding or throwing; it waits
    // for those messages. The egg white is a liquid poured into the pan, so it comes with the pan's contents.
    // v0.11.8: the shells' flight is sent too, so they land in the same place on both screens.
    internal sealed partial class ItemSync
    {
        // Shell halves (Egg.TopShellID, Egg.BotShellID). The game fades these out with the broken pieces.
        private const int TopShellAsset = 163;
        private const int BotShellAsset = 164;

        // Above 0 while an egg on this copy is spawning what came out of it.
        private static int _eggSpawning;

        private void PatchEggs()
        {
            Type eggType = typeof(Egg.EggType);
            PatchEgg(nameof(EggSpawnPrefix), nameof(EggSpawnPostfix), "SpawnYolk", Type.EmptyTypes);
            PatchEgg(nameof(EggShellsPrefix), nameof(EggSpawnPostfix), "SpawnYolkSplitShells", Type.EmptyTypes);
            PatchEgg(nameof(EggShellsPrefix), nameof(EggSpawnPostfix), "SpawnEggshells", new[] { typeof(SpawnPoint), typeof(SpawnPoint) });
            PatchEgg(nameof(EggSpawnPrefix), nameof(EggSpawnPostfix), "SpawnEgg", new[] { eggType });
            PatchEgg(nameof(EggSpawnPrefix), nameof(EggSpawnPostfix), "SpawnEgg", new[] { eggType, typeof(SpawnPoint) });
            PatchEgg(nameof(EggGonePrefix), null, "Explode", Type.EmptyTypes);
            PatchEgg(nameof(EggCollisionPrefix), null, "OnThrowCollision", new[] { typeof(Collision) });
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(SpawnPointThatSpawnsProductPart), nameof(SpawnPointThatSpawnsProductPart.RequestSpawn),
                        new[] { typeof(int), typeof(Vector3), typeof(Quaternion), typeof(Action<ProductPart>), typeof(Action<ProductPart>) }),
                    postfix: new HarmonyMethod(typeof(ItemSync), nameof(RequestSpawnPostfix)));
                _harmony.Patch(AccessTools.Method(typeof(ProductsManager), nameof(ProductsManager.CreateProduct),
                        new[] { typeof(int), typeof(SpawnPoint), typeof(Vector3), typeof(bool), typeof(Action<ProductPart>), typeof(Action<ProductPart>) }),
                    postfix: new HarmonyMethod(typeof(ItemSync), nameof(CreateProductPostfix)));
            }
            catch (Exception e)
            {
                _log.LogError($"[Eggs] Patching the egg spawns failed; what comes out of a cracked egg won't sync: {e}");
            }
        }

        private void PatchEgg(string prefix, string postfix, string method, Type[] args)
        {
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Egg), method, args),
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(ItemSync), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(ItemSync), postfix));
            }
            catch (Exception e)
            {
                _log.LogError($"[Eggs] Patching Egg.{method} failed; that part of cracking eggs won't sync: {e}");
            }
        }

        private static void EggSpawnPrefix() => _eggSpawning++;

        private static void EggSpawnPostfix() => _eggSpawning = Math.Max(0, _eggSpawning - 1);

        // Every way an egg cracks ends by spawning its shells: the egg is gone.
        private static void EggShellsPrefix(Egg __instance)
        {
            _eggSpawning++;
            EggGonePrefix(__instance);
        }

        private static void EggGonePrefix(Egg __instance)
        {
            try
            {
                Instance?.ReportEggGone(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Eggs] patch error: " + e);
            }
        }

        // An egg the other player is holding or throwing only cracks on their copy.
        private static bool EggCollisionPrefix(Egg __instance)
        {
            try
            {
                return Instance == null || !Instance._session.IsConnected || !Instance.IsRemoteHeld(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Eggs] patch error: " + e);
                return true;
            }
        }

        private static void RequestSpawnPostfix(SpawnPointThatSpawnsProductPart __result)
        {
            if (_eggSpawning > 0 && __result != null && Instance != null)
                __result.ProductPartSpawned += Instance.OnEggPieceSpawned;
        }

        private static void CreateProductPostfix(Product __result)
        {
            if (_eggSpawning > 0 && __result != null && Instance != null)
                __result.ProductPartSpawned += Instance.OnEggPieceSpawned;
        }

        private void OnEggPieceSpawned(ProductPart part)
        {
            try
            {
                if (part == null || !_session.IsConnected)
                    return;
                AnnounceNewItem(part);
                // v0.11.8: the game throws the shells (and an egg that cracked on its own) away from where the egg
                // was, so each copy's physics dropped them somewhere different. The cracker's copy sends where they
                // fly until they land, like an item the player let go of.
                if (!ReferenceEquals(Player.Me != null ? Player.Me.ActiveMovable : null, part) && _netIdOf.TryGetValue(part, out int netId))
                    SendUntilRest(netId, part);
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Eggs] Sending what came out of an egg failed: {e.Message}");
            }
        }

        private void ReportEggGone(Egg egg)
        {
            if (!_session.IsConnected || _applyingRemoval || !IsAlive(egg) || !_netIdOf.TryGetValue(egg, out int netId))
                return;
            SendRemoved(netId, "cracked");
        }

        // The other copy's shell halves fade out with the broken pieces there; these copies do the same here.
        private static void FadeIfEggShell(int assetId, IMovable copy)
        {
            if ((assetId == TopShellAsset || assetId == BotShellAsset) && IsAlive(copy))
                Game.KitchenServices.FracturedObjects.AddFracturedObject(copy.gameObject);
        }
    }
}
