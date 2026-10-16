using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CookingSimMP
{
    // Shows the other player's mop moving while they mop.
    //
    // The mop isn't picked up like other items. The player picks up an invisible handle (MopGrabberHand), which
    // ItemSync already moves on the other copy. The mop head (Mop) is pulled after a target on the floor by
    // physics, but only on the copy whose player holds the handle, and the stick (MopStick) points from the head
    // to the local player's hand. On the other copy the head stayed put, so the mop never seemed to move.
    //
    // So the mopping copy sends where its mop head is. The other copy freezes its head, slides it there, and points
    // the stick at the handle. The strokes themselves are synced by CleaningSync.
    internal sealed class MopSync
    {
        private const float SendInterval = 0.1f;
        private const float SmoothingSpeed = 15f;
        private const float SettleTime = 1f;   // After letting go, keep sending while it lands or is put back in its holder.

        private static MopSync _instance;

        private readonly NetSession _session;
        private readonly ItemSync _items;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".mop");

        private float _sendTimer;
        private Mop _sending;                  // Our mop while this player mops with it, and shortly after.
        private float _settleTimer;
        private readonly Dictionary<int, Following> _following = new Dictionary<int, Following>();
        private readonly Dictionary<int, float> _stoppedAt = new Dictionary<int, float>();  // A late "still mopping" can trail the final one.

        private sealed class Following
        {
            public Mop Mop;
            public bool WasKinematic;
            public Vector3 Target;
            public GameObject LookAtBefore;
            public bool FollowingGrabberBefore;
        }

        public MopSync(NetSession session, ItemSync items, ManualLogSource log)
        {
            _instance = this;
            _session = session;
            _items = items;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.PeerLeft += _ => Reset();
            _session.Stopped += Reset;
            _session.WorldReset += Reset;
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(Mop), "OnLeftClick", Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(MopSync), nameof(OnLeftClickPrefix)));
            }
            catch (Exception e)
            {
                _log.LogError($"[Mop] Patching Mop.OnLeftClick failed; both players could grab the mop at once: {e}");
            }
        }

        public static bool Handles(MessageType type) => type == MessageType.MopState;

        private bool Active => _session.IsConnected && _items.HasTable;

        private void Reset()
        {
            foreach (Following f in _following.Values)
                StopFollowing(f, null, f.LookAtBefore, null);
            _following.Clear();
            _sending = null;
        }

        // ---------- Our mop: send the head ----------

        public void Tick(float deltaTime)
        {
            if (!Active)
            {
                _sending = null;
                return;
            }

            Mop mine = null;
            if (Mop.IsActive && Player.Me != null && Player.Me.ActiveMovable is MopGrabberHand hand && hand != null && hand.mop != null)
                mine = hand.mop;

            if (mine != null)
            {
                _sending = mine;
                _settleTimer = SettleTime;
            }
            else if (_sending != null)
            {
                _settleTimer -= deltaTime;
                if (_settleTimer <= 0f)
                {
                    // Landed: one last reliable position so both copies leave it in the same spot and pose.
                    Send(_sending, false, DeliveryMethod.ReliableOrdered);
                    _sending = null;
                }
            }
            if (_sending != null)
            {
                _sendTimer += deltaTime;
                if (_sendTimer >= SendInterval)
                {
                    _sendTimer = 0f;
                    Send(_sending, true, DeliveryMethod.Sequenced);
                }
            }

            float t = 1f - Mathf.Exp(-SmoothingSpeed * deltaTime);
            foreach (Following f in _following.Values)
            {
                if (f.Mop != null)
                    f.Mop.transform.position = Vector3.Lerp(f.Mop.transform.position, f.Target, t);
            }
        }

        private void Send(Mop mop, bool mopping, DeliveryMethod method)
        {
            NetDataWriter writer = _session.BeginMessage(MessageType.MopState);
            writer.Put(mop.mopId);
            writer.Put(mopping);
            Vector3 p = mop.transform.position;
            writer.Put(p.x);
            writer.Put(p.y);
            writer.Put(p.z);
            // Put back in its holder (MopSpawnPoint points the stick at the holder) or left lying on the floor.
            writer.Put(mop.mopStick != null && mop.mopStick.lookAtTarget != mop.mopStick.startLookAtTarget);
            _session.SendToAll(method);
        }

        // ---------- Their mop: follow it ----------

        private void OnMessage(int peerId, MessageType type, NetPacketReader reader)
        {
            if (type != MessageType.MopState)
                return;
            int mopId = reader.GetInt();
            bool mopping = reader.GetBool();
            var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            bool parked = reader.GetBool();
            if (!Active)
                return;

            if (!_following.TryGetValue(mopId, out Following f) || f.Mop == null)
            {
                if (!mopping)
                    return;
                if (_stoppedAt.TryGetValue(mopId, out float stopped) && Time.unscaledTime - stopped < 0.5f)
                    return;
                Mop mop = FindMop(mopId);
                if (mop == null)
                    return;
                // We were mopping with it ourselves: the other player's grab wins only if ours already ended.
                if (ReferenceEquals(mop, _sending))
                    return;
                f = StartFollowing(mop);
                _following[mopId] = f;
            }

            f.Target = position;
            if (!mopping)
            {
                StopFollowing(f, position, parked ? HolderTarget(mopId) ?? f.LookAtBefore : f.Mop.mopStick?.startLookAtTarget, parked);
                _following.Remove(mopId);
                _stoppedAt[mopId] = Time.unscaledTime;
            }
        }

        private Mop FindMop(int mopId)
        {
            foreach (Mop mop in Object.FindObjectsOfType<Mop>())
            {
                if (mop != null && mop.mopId == mopId)
                    return mop;
            }
            _log.LogWarning($"[Mop] The other player is mopping with mop {mopId}, which this copy doesn't have.");
            return null;
        }

        private static Following StartFollowing(Mop mop)
        {
            var f = new Following
            {
                Mop = mop,
                WasKinematic = mop.rb != null && mop.rb.isKinematic,
                Target = mop.transform.position,
            };
            if (mop.rb != null)
                mop.rb.isKinematic = true;
            if (mop.mopStick != null && mop.grabberHand != null)
            {
                // Point the stick at the handle, which ItemSync keeps in the other player's hand.
                f.LookAtBefore = mop.mopStick.lookAtTarget;
                f.FollowingGrabberBefore = mop.mopStick.FollowingGrabber;
                mop.mopStick.lookAtTarget = mop.grabberHand.gameObject;
                mop.mopStick.FollowingGrabber = true;
            }
            return f;
        }

        // Ends like the game ends it on the mopping copy: lying on the floor with the stick pointing at its own
        // handle (Mop.OnLeftClick + MopGrabberHand.OnThrow), or back in its holder, frozen, pointing at the holder
        // (MopSpawnPoint's drop). Restoring how it was here before would stand a dropped mop's stick straight up.
        private static void StopFollowing(Following f, Vector3? finalPosition, GameObject lookAt, bool? parked)
        {
            Mop mop = f.Mop;
            if (mop == null)
                return;
            if (finalPosition.HasValue)
                mop.transform.position = finalPosition.Value;
            if (mop.rb != null)
            {
                bool kinematic = parked ?? f.WasKinematic;
                mop.rb.isKinematic = kinematic;
                if (!kinematic)
                {
                    mop.rb.velocity = Vector3.zero;
                    mop.rb.angularVelocity = Vector3.zero;
                }
            }
            if (mop.mopStick != null)
            {
                if (lookAt != null)
                    mop.mopStick.lookAtTarget = lookAt;
                mop.mopStick.FollowingGrabber = parked.HasValue || f.FollowingGrabberBefore;
            }
        }

        // The holder's own aim point for the stick (MopSpawnPoint.LookAtTarget), matched by mop ID.
        private static GameObject HolderTarget(int mopId)
        {
            foreach (MopSpawnPoint holder in Object.FindObjectsOfType<MopSpawnPoint>())
            {
                if (holder != null && Traverse.Create(holder).Field("mopSpawnPointId").GetValue<int>() == mopId)
                    return holder.LookAtTarget;
            }
            return null;
        }

        private bool IsFollowing(Mop mop)
        {
            foreach (Following f in _following.Values)
            {
                if (ReferenceEquals(f.Mop, mop))
                    return true;
            }
            return false;
        }

        // ---------- Harmony ----------

        // Mop.OnLeftClick marks the mop as in use even when picking up its handle is refused, which would leave
        // this copy's mop stuck. So check first: no grabbing a mop the other player is using.
        private static bool OnLeftClickPrefix(Mop __instance)
        {
            try
            {
                if (_instance == null || !_instance.Active || __instance == null)
                    return true;
                if (_instance.IsFollowing(__instance))
                {
                    if (__instance.grabberHand != null && ItemSync.Instance != null)
                        ItemSync.Instance.AllowPick(__instance.grabberHand);  // Shows "... is holding that".
                    return false;
                }
                return __instance.grabberHand == null || ItemSync.Instance == null || ItemSync.Instance.AllowPick(__instance.grabberHand);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Mop] patch error: " + e);
                return true;
            }
        }
    }
}
