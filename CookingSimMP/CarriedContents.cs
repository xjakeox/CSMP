using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CookingSimMP
{
    // v0.11.4: food that gets into or out of a container while the player carries it, e.g. scooping food from a
    // pan or pot onto the plate in your hand.
    //
    // The other copy carries a held container's contents along with it, but only what was in it when it was picked
    // up (PinParts). Food scooped in later sat still in the pan there until the plate was put down, and if the plate
    // was served straight from the hand it never moved at all: the plate went, the food stayed behind.
    //
    // Now the carrier looks at the held container's contents with every pose it sends. Food that joined is sent as
    // ItemSeated (where it sits in the container), and the other copy starts carrying it too. Food that left (fell
    // off) starts sending its own poses as usual, and the other copy stops carrying it with the container.
    internal sealed partial class ItemSync
    {
        // Containers in the local player's hand: the content IDs the other copy already knows are in them.
        private readonly Dictionary<int, HashSet<int>> _carriedContents = new Dictionary<int, HashSet<int>>();
        private readonly HashSet<int> _scratchContent = new HashSet<int>();

        // Every item in a container that has an ID, nested containers included.
        private void ContentIds(Container container, HashSet<int> result)
        {
            result.Clear();
            foreach (IMovable m in container.MovablesRecursive)
            {
                if (IsAlive(m) && !ReferenceEquals(m, container) && _netIdOf.TryGetValue(m, out int id))
                    result.Add(id);
            }
            foreach (ProductPart p in container.GetPartsRecursive())
            {
                if (IsAlive(p) && _netIdOf.TryGetValue(p, out int id))
                    result.Add(id);
            }
        }

        // The food on a plate as this copy sees it, for the served message.
        internal List<int> FoodIdsOn(Container plate)
        {
            var ids = new HashSet<int>();
            if (IsAlive(plate))
                ContentIds(plate, ids);
            return new List<int>(ids);
        }

        // Called with every pose sent for a container in the local player's hand.
        private void SendCarriedContentChanges(int containerId, Container container)
        {
            if (!_carriedContents.TryGetValue(containerId, out HashSet<int> known))
            {
                // Just picked up: the other copy pins what it sees in it now.
                known = new HashSet<int>();
                ContentIds(container, known);
                _carriedContents[containerId] = known;
                return;
            }
            ContentIds(container, _scratchContent);
            foreach (int id in _scratchContent)
            {
                if (!known.Add(id) || !_byNetId.TryGetValue(id, out IMovable m))
                    continue;
                // The container carries it from now on; its own poses would fight the container's.
                if (_localTracks.TryGetValue(id, out LocalTrack own) && !own.InHand)
                    _localTracks.Remove(id);
                SendSeat(id, containerId, container, m);
                // It may still be sliding into place; send where it settled too.
                _pendingSeats.Add(new PendingSeat { NetId = id, HolderId = containerId, Due = _seatClock + SeatDelays[0], SendsLeft = SeatDelays.Length });
                _log.LogInfo($"{Describe(id, m)} landed in {Describe(containerId, container)} in the hand; the other player's copy carries it too.");
            }
            // Fell off: forget it, so it's sent again if it lands back in.
            known.RemoveWhere(id => !_scratchContent.Contains(id));
        }

        private void SendSeat(int netId, int holderId, IMovable holder, IMovable m)
        {
            Transform ht = holder.gameObject.transform;
            Transform t = m.gameObject.transform;
            NetDataWriter writer = _session.BeginMessage(MessageType.ItemSeated);
            writer.Put(netId);
            writer.Put(holderId);
            PutVector(writer, ht.InverseTransformPoint(t.position));
            PutQuaternion(writer, Quaternion.Inverse(ht.rotation) * t.rotation);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        // Receiving side of ItemSeated: an item the other player's carried container doesn't carry yet here.
        private void StartCarrying(int netId, IMovable m, int holderId, RemoteHeldItem carried)
        {
            // It was following its own poses (scooped up while sliding): the container carries it now.
            if (_remoteHeld.TryGetValue(netId, out RemoteHeldItem own))
            {
                ReleaseRemoteHeld(own, snap: false);
                _remoteHeld.Remove(netId);
            }
            _localTracks.Remove(netId);
            UnpinFromCarriers(m, except: carried);
            var seen = new HashSet<Transform> { carried.Main };
            foreach (PinnedPart pinned in carried.Pinned)
            {
                if (pinned.Transform != null)
                    seen.Add(pinned.Transform);
            }
            PinContent(carried, m, seen);
            _log.LogInfo($"{Describe(netId, m)} landed in the other player's {Describe(holderId, carried.Movable)}; carrying it with it here.");
        }

        // True when a container the other player carries already moves this item here.
        private static bool IsPinnedIn(RemoteHeldItem carried, IMovable m)
        {
            Transform root = m.gameObject.transform;
            foreach (PinnedPart pinned in carried.Pinned)
            {
                if (pinned.Transform != null && (pinned.Transform == root || pinned.Transform.IsChildOf(root)))
                    return true;
            }
            return false;
        }

        // The item left a container the other player carries (it fell off, or they picked it out): stop moving it
        // with that container here.
        private void UnpinFromCarriers(IMovable m, RemoteHeldItem except = null)
        {
            Transform root = m.gameObject.transform;
            var bodies = new HashSet<Transform> { root };
            Rigidbody main = MainBodyOf(m);
            if (main != null)
                bodies.Add(main.transform);
            foreach (Rigidbody bone in SecondaryBodiesOf(m))
            {
                if (bone != null)
                    bodies.Add(bone.transform);
            }
            foreach (RemoteHeldItem carried in _remoteHeld.Values)
            {
                if (ReferenceEquals(carried, except) || ReferenceEquals(carried.Movable, m))
                    continue;
                for (int i = carried.Pinned.Count - 1; i >= 0; i--)
                {
                    PinnedPart pinned = carried.Pinned[i];
                    if (pinned.Transform == null || !(bodies.Contains(pinned.Transform) || pinned.Transform.IsChildOf(root)))
                        continue;
                    Thaw(pinned.Body, null);
                    carried.Pinned.RemoveAt(i);
                }
            }
        }
    }
}
