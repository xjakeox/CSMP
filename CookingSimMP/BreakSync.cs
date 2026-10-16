using System;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CookingSimMP
{
    // Breaking: plates, glasses, bottles and anything else that shatters (MovablePhysics.Break). The copy where it
    // breaks tells the other one, which moves the item to the same spot and breaks it the same way, so the shards,
    // the sound and the food that falls off happen on both screens. An item the other player is holding or throwing
    // only breaks when their copy says so; this copy just follows it.
    internal sealed class BreakSync
    {
        internal static BreakSync Instance;

        // True while breaking an item because the other player's copy did, so it isn't sent back.
        internal static bool Applying;

        // Break's arguments, kept by the prefix for the MovableBroken event that fires inside it.
        private static string _pendingSound;
        private static float _pendingStrength;

        private readonly NetSession _session;
        private readonly ItemSync _items;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".breaks");

        public BreakSync(NetSession session, ItemSync items, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _items = items;
            _log = log;
            _session.MessageReceived += OnMessage;
            MovablePhysics.MovableBroken += OnLocalBroken;

            try
            {
                _harmony.Patch(AccessTools.Method(typeof(MovablePhysics), nameof(MovablePhysics.Break)),
                    prefix: new HarmonyMethod(typeof(BreakPatches), nameof(BreakPatches.BreakPrefix)));
            }
            catch (Exception e)
            {
                _log.LogError($"[Breaks] Patching MovablePhysics.Break failed; breaking isn't synced: {e}");
            }
        }

        public static bool Handles(MessageType type) => type == MessageType.ItemBroken;

        private bool Active => _session.IsConnected && WorldSync.InKitchen;

        // Called by the prefix. Returns false to stop the break on this copy.
        internal bool AllowBreak(MovablePhysics physics, string breakSound, float strength)
        {
            _pendingSound = breakSound;
            _pendingStrength = strength;
            if (Applying || !Active)
                return true;
            // The other player has it in hand or in flight: their copy decides whether it breaks.
            IMovable m = physics.Movable;
            return m == null || !_items.IsRemoteHeld(m);
        }

        private void OnLocalBroken(IMovable movable)
        {
            try
            {
                if (Applying || !Active || movable == null || !_items.TryGetNetId(movable, out int netId))
                    return;
                Transform t = movable.gameObject.transform;
                NetDataWriter writer = _session.BeginMessage(MessageType.ItemBroken);
                writer.Put(netId);
                writer.Put(t.position.x); writer.Put(t.position.y); writer.Put(t.position.z);
                writer.Put(t.rotation.x); writer.Put(t.rotation.y); writer.Put(t.rotation.z); writer.Put(t.rotation.w);
                writer.Put(_pendingStrength);
                writer.Put(_pendingSound ?? "");
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
                // It's gone here; stop tracking it so the item table doesn't also report it as removed.
                _items.DropItem(netId);
                _log.LogInfo($"[Breaks] Item {netId} broke here; told the other player.");
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Breaks] Sending a break failed: {e.Message}");
            }
        }

        private void OnRemoteBroken(NetPacketReader reader)
        {
            int netId = reader.GetInt();
            var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            var rotation = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            float strength = reader.GetFloat();
            string sound = reader.GetString();

            // Already gone here (it broke on both copies at once, or was removed): nothing to do.
            if (!_items.TryGetItem(netId, out IMovable m) || m.PhysicsController == null)
                return;
            _items.DropItem(netId);
            m.gameObject.transform.SetPositionAndRotation(position, rotation);
            Applying = true;
            try
            {
                m.PhysicsController.Break(string.IsNullOrEmpty(sound) ? null : sound, strength);
            }
            finally
            {
                Applying = false;
            }
            _log.LogInfo($"[Breaks] Other player broke item {netId}; broke it here too.");
        }

        private void OnMessage(int peerId, MessageType type, NetPacketReader reader)
        {
            if (type != MessageType.ItemBroken || !Active)
                return;
            try
            {
                OnRemoteBroken(reader);
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Breaks] Applying the other player's break failed: {e.Message}");
            }
        }
    }

    internal static class BreakPatches
    {
        public static bool BreakPrefix(MovablePhysics __instance, string breakSound, float strength)
        {
            try
            {
                return BreakSync.Instance == null || BreakSync.Instance.AllowBreak(__instance, breakSound, strength);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Breaks] patch error: " + e);
                return true;
            }
        }
    }
}
