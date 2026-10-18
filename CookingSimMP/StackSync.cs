using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CookingSimMP
{
    // Plates (and other containers) put back on their stack. The game counts them in Stack.countOfPutoff, and the
    // next one taken from that stack is free. Both copies watch every stack's count; when it changes here (a plate
    // put back, or a free one taken), the change is sent as +1/-1 and the other copy applies it too, so the next
    // plate is free for whoever takes it.
    //
    // Stacks are matched by the item they create (Stack.idToCreate), the same way the game's save matches them,
    // with position to tell apart two stacks of the same item.
    internal sealed class StackSync
    {
        private const float ScanInterval = 0.25f;

        private readonly NetSession _session;
        private readonly ManualLogSource _log;

        private readonly Dictionary<Stack, int> _lastCount = new Dictionary<Stack, int>();
        private List<Stack> _stacks = new List<Stack>();
        private string _scene;
        private float _timer;

        public StackSync(NetSession session, ManualLogSource log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
            _session.Stopped += Reset;
            _session.WorldReset += Reset;
        }

        private bool Active => _session.IsConnected && WorldSync.InKitchen;

        private void Reset()
        {
            _lastCount.Clear();
            _stacks.Clear();
            _scene = null;
        }

        private void RefreshStacks()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene == _scene && _stacks.All(s => s != null))
                return;
            _scene = scene;
            _stacks = Object.FindObjectsOfType<Stack>().ToList();
            _lastCount.Clear();
            foreach (Stack stack in _stacks)
                _lastCount[stack] = stack.countOfPutoff;
        }

        public void Tick(float deltaTime)
        {
            if (!Active)
            {
                if (_scene != null)
                    Reset();
                return;
            }
            _timer += deltaTime;
            if (_timer < ScanInterval)
                return;
            _timer = 0f;
            RefreshStacks();

            foreach (Stack stack in _stacks)
            {
                if (stack == null)
                    continue;
                int count = stack.countOfPutoff;
                if (!_lastCount.TryGetValue(stack, out int last))
                {
                    _lastCount[stack] = count;
                    continue;
                }
                if (count == last)
                    continue;
                _lastCount[stack] = count;
                Vector3 p = stack.transform.position;
                NetDataWriter writer = _session.BeginMessage(MessageType.StackCount);
                writer.Put(stack.idToCreate);
                writer.Put(p.x); writer.Put(p.y); writer.Put(p.z);
                writer.Put(count - last);
                _session.SendToAll(DeliveryMethod.ReliableOrdered);
                _log.LogInfo($"[Stacks] Stack of {stack.idToCreate} changed by {count - last} here (now {count} free); told the other player.");
            }
        }

        private void OnMessage(int peerId, MessageType type, NetDataReader reader)
        {
            if (type != MessageType.StackCount || !Active)
                return;
            int id = reader.GetInt();
            var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            int delta = reader.GetInt();
            RefreshStacks();
            Stack stack = _stacks.Where(s => s != null && s.idToCreate == id)
                .OrderBy(s => (s.transform.position - position).sqrMagnitude)
                .FirstOrDefault();
            if (stack == null)
            {
                _log.LogWarning($"[Stacks] The other player's stack of {id} isn't in this kitchen.");
                return;
            }
            stack.countOfPutoff = Mathf.Max(0, stack.countOfPutoff + delta);
            _lastCount[stack] = stack.countOfPutoff;
            _log.LogInfo($"[Stacks] The other player's stack of {id} changed by {delta} (now {stack.countOfPutoff} free).");
        }
    }
}
