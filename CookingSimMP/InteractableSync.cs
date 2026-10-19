using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using CS.SwitchSpace;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CookingSimMP
{
    // Syncs the kitchen's fixed interactables: fridge and cupboard doors, drawers, stove and oven knobs,
    // oven doors, sink taps, light switches, microwave buttons, pizza oven valves (all subclasses of the
    // game's Switch), Cooking Challenge oven knobs (SwitchCustomRotable), machine push buttons (mixer,
    // cutter, sauce machine) and fryer oil buttons.
    //
    // Matching: every Switch has a save ID (Tool.id) the game itself uses to restore its mode
    // (SwitchSave.AfterLoad -> ToolsManager.GetNonZeroTool). Where that ID is non-zero and unique it's the key;
    // otherwise the key is the object's type and hierarchy path, numbered by position when several share it.
    // Both copies build the same keys independently as long as they loaded the same save.
    //
    // Authority: whoever flips something sends it; the other copy replays it. When the client joins, the host
    // sends the state of everything and the client copies it (the host wins).
    internal sealed class InteractableSync
    {
        private const float SettleTime = 3f;        // Seconds in a kitchen before syncing starts (Switch.Start sets modes a frame late, saves load after).
        private const float PendingTimeout = 3f;    // How long to retry a change that arrived while the object was busy.
        private const int MaxStringLength = 4096;

        internal enum Kind : byte
        {
            None = 0,
            Switch = 1,      // Knobs, taps, light switches, oven mode dials... (state = Switch.Mode)
            Door = 2,        // Door and subclasses such as SinkSwitch (state = 1 open, 0 closed)
            Drawer = 3,      // Drawer and subclasses (state = 1 open, 0 closed)
            Rotable = 4,     // CS.SwitchSpace.SwitchCustomRotable (state = rotation index)
            PushButton = 5,  // Mixer on/off, cutter and sauce machines (a press, no state)
            FrierButton = 6, // Fryer oil fill / drain (a press, no state)
            CutterMode = 7,  // Automatic cutter's cut mode, changed with E (state = mode index, its lit diode)
        }

        internal static InteractableSync Instance;

        // True while applying the other player's change, so the patches don't send it straight back.
        internal static bool Applying;

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".interactables");

        private readonly Dictionary<string, Component> _byKey = new Dictionary<string, Component>();
        private readonly Dictionary<Component, string> _keyOf = new Dictionary<Component, string>();
        private readonly List<PendingChange> _pending = new List<PendingChange>();
        private string _tableScene;
        private int _tableBuiltFrame = -1;

        private string _kitchenScene;
        private float _inKitchenFor;

        private string _readySentForScene;   // Client: scene we already asked the host about.
        private string _pendingReadyScene;   // Host: client is waiting for a snapshot of this scene.

        private static readonly MethodInfo DoorDo = AccessTools.Method(typeof(Door), "Do", new[] { typeof(bool) });
        private static readonly FieldInfo DrawerTweener = AccessTools.Field(typeof(Drawer), "_currentTweener");
        private static readonly FieldInfo PushButtonUnderChange = AccessTools.Field(typeof(PushButton), "underChange");
        private static readonly MethodInfo PushButtonIsLocked = AccessTools.PropertyGetter(typeof(PushButton), "IsLocked");
        private static readonly FieldInfo CutterModeField = AccessTools.Field(typeof(Stands.AutomaticCutter), "currentCutMode");
        private static readonly FieldInfo CutterDiodes = AccessTools.Field(typeof(Stands.AutomaticCutter), "modeDiodes");
        private static readonly FieldInfo CutterDiodeOn = AccessTools.Field(typeof(Stands.AutomaticCutter), "diodeOnMaterial");
        private static readonly FieldInfo CutterDiodeOff = AccessTools.Field(typeof(Stands.AutomaticCutter), "diodeOffMaterial");

        private sealed class PendingChange
        {
            public string Key;
            public Kind Kind;
            public int Value;
            public int Previous;
            public bool ViaClick;
            public float Age;
        }

        public InteractableSync(NetSession session, ManualLogSource log)
        {
            _session = session;
            _log = log;
            Instance = this;
            _session.MessageReceived += OnMessage;
            _session.AllPeersLeft += Reset;   // v0.12.2: not when one of several clients leaves.
            _session.Stopped += Reset;
            _session.WorldReset += Reset;
            ApplyPatches();
        }

        public static bool Handles(MessageType type) =>
            type == MessageType.InteractReady || type == MessageType.InteractSnapshot || type == MessageType.InteractChange;

        private bool Settled => WorldSync.InKitchen && _inKitchenFor >= SettleTime;

        public void Tick(float deltaTime)
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != _kitchenScene)
            {
                _kitchenScene = scene;
                _inKitchenFor = 0f;
                ClearTable();
                _pending.Clear();
            }

            bool wasSettled = Settled;
            if (WorldSync.InKitchen)
                _inKitchenFor += deltaTime;
            else
                _inKitchenFor = 0f;

            if (!wasSettled && Settled)
                BuildTable(scene);

            if (_session.IsConnected && Settled)
            {
                if (!_session.IsHost && _readySentForScene != scene)
                {
                    _readySentForScene = scene;
                    _session.BeginMessage(MessageType.InteractReady).Put(scene);
                    _session.SendToAll(DeliveryMethod.ReliableOrdered);
                }
                if (_session.IsHost && _pendingReadyScene == scene)
                {
                    _pendingReadyScene = null;
                    SendSnapshot(scene);
                }
            }

            ProcessPending(deltaTime);
        }

        private void Reset()
        {
            _readySentForScene = null;
            _pendingReadyScene = null;
            _pending.Clear();
        }

        // ---------- Keys ----------

        private static Kind KindOf(Component c)
        {
            switch (c)
            {
                case Door _: return Kind.Door;
                case Drawer _: return Kind.Drawer;
                case Switch _: return Kind.Switch;
                case SwitchCustomRotable _: return Kind.Rotable;
                case PushButton _: return Kind.PushButton;
                case FrierButton _: return Kind.FrierButton;
                case Stands.AutomaticCutter _: return Kind.CutterMode;
                default: return Kind.None;
            }
        }

        private static bool HasState(Kind kind) =>
            kind == Kind.Switch || kind == Kind.Door || kind == Kind.Drawer || kind == Kind.Rotable || kind == Kind.CutterMode;

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent)
                parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Describe(Component c) => $"{c.GetType().Name} '{PathOf(c.transform)}'";

        private static List<Component> FindInteractables()
        {
            var all = new List<Component>();
            all.AddRange(Object.FindObjectsOfType<Switch>());   // Includes Door, Drawer and their subclasses.
            all.AddRange(Object.FindObjectsOfType<SwitchCustomRotable>());
            all.AddRange(Object.FindObjectsOfType<PushButton>());
            all.AddRange(Object.FindObjectsOfType<FrierButton>());
            all.AddRange(Object.FindObjectsOfType<Stands.AutomaticCutter>());
            return all;
        }

        private void ClearTable()
        {
            _byKey.Clear();
            _keyOf.Clear();
            _tableScene = null;
        }

        private void BuildTable(string scene)
        {
            ClearTable();
            _tableScene = scene;
            _tableBuiltFrame = Time.frameCount;

            List<Component> all = FindInteractables();

            // Save IDs are only usable when no other object of the same family shares them.
            var switchIdCount = new Dictionary<int, int>();
            var rotableIdCount = new Dictionary<int, int>();
            foreach (Component c in all)
            {
                if (c is Switch sw && sw.id != 0)
                    switchIdCount[sw.id] = switchIdCount.TryGetValue(sw.id, out int n1) ? n1 + 1 : 1;
                else if (c is SwitchCustomRotable r && r.ID != 0)
                    rotableIdCount[r.ID] = rotableIdCount.TryGetValue(r.ID, out int n2) ? n2 + 1 : 1;
            }

            var groups = new Dictionary<string, List<Component>>();
            int byId = 0;
            foreach (Component c in all)
            {
                string key;
                if (c is Switch sw && sw.id != 0 && switchIdCount[sw.id] == 1)
                {
                    key = "sw:" + sw.id;
                    byId++;
                }
                else if (c is SwitchCustomRotable r && r.ID != 0 && rotableIdCount[r.ID] == 1)
                {
                    key = "rot:" + r.ID;
                    byId++;
                }
                else
                {
                    key = c.GetType().Name + "|" + PathOf(c.transform);
                }
                if (!groups.TryGetValue(key, out List<Component> list))
                    groups[key] = list = new List<Component>();
                list.Add(c);
            }

            int duplicates = 0;
            foreach (KeyValuePair<string, List<Component>> group in groups)
            {
                if (group.Value.Count == 1)
                {
                    _byKey[group.Key] = group.Value[0];
                    _keyOf[group.Value[0]] = group.Key;
                    continue;
                }
                // Same path more than once (e.g. two identical stoves): number them by position, which matches
                // on both copies when they loaded the same save.
                duplicates += group.Value.Count;
                List<Component> sorted = group.Value
                    .OrderBy(c => Mathf.RoundToInt(c.transform.position.x * 100f))
                    .ThenBy(c => Mathf.RoundToInt(c.transform.position.y * 100f))
                    .ThenBy(c => Mathf.RoundToInt(c.transform.position.z * 100f))
                    .ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    string key = group.Key + "@" + i;
                    _byKey[key] = sorted[i];
                    _keyOf[sorted[i]] = key;
                }
            }
        }

        private string KeyOf(Component c)
        {
            string scene = SceneManager.GetActiveScene().name;
            if (_tableScene != scene)
                BuildTable(scene);
            if (_keyOf.TryGetValue(c, out string key))
                return key;
            BuildTable(scene);
            if (_keyOf.TryGetValue(c, out key))
                return key;
            key = c.GetType().Name + "|" + PathOf(c.transform);
            _log.LogWarning($"[Interact] {Describe(c)} still isn't in the table (inactive?); sending path key {key}.");
            return key;
        }

        private Component Find(string key)
        {
            string scene = SceneManager.GetActiveScene().name;
            if (_tableScene != scene)
                BuildTable(scene);
            if (_byKey.TryGetValue(key, out Component c) && c != null)
                return c;
            if (_tableBuiltFrame != Time.frameCount)
            {
                BuildTable(scene);
                if (_byKey.TryGetValue(key, out c) && c != null)
                    return c;
            }
            return null;
        }

        // ---------- State ----------

        private static int ReadState(Component c, Kind kind)
        {
            switch (kind)
            {
                case Kind.Door: return ((Door)c).IsOpen ? 1 : 0;
                case Kind.Drawer: return ((Drawer)c).IsDrawerOpen ? 1 : 0;
                case Kind.Switch: return ((Switch)c).Mode;
                case Kind.Rotable: return ((SwitchCustomRotable)c).CurrentRotationIndex;
                case Kind.CutterMode: return (int)CutterModeField.GetValue(c);
                default: return 0;
            }
        }

        private static string StateText(Kind kind, int value)
        {
            switch (kind)
            {
                case Kind.Door:
                case Kind.Drawer: return value != 0 ? "open" : "closed";
                case Kind.Switch: return "mode " + value;
                case Kind.Rotable: return "setting " + value;
                case Kind.CutterMode: return "cut mode " + value;
                default: return "press";
            }
        }

        private static bool IsDrawerBusy(Drawer d) => DrawerTweener != null && DrawerTweener.GetValue(d) != null;

        private static bool IsButtonBusy(PushButton b) =>
            (PushButtonUnderChange != null && (bool)PushButtonUnderChange.GetValue(b)) ||
            (PushButtonIsLocked != null && (bool)PushButtonIsLocked.Invoke(b, null));

        // ---------- Sending local changes ----------

        internal void OnLocalSwitchMode(Switch sw, int previous, bool viaClick)
        {
            // Doors and drawers repurpose Mode internally; their own patches send open/closed instead.
            if (Applying || sw is Door || sw is Drawer || sw.Mode == previous)
                return;
            SendChange(sw, Kind.Switch, sw.Mode, previous, viaClick);
        }

        internal void OnLocalDoor(Door door, bool open)
        {
            if (Applying)
                return;
            SendChange(door, Kind.Door, open ? 1 : 0, open ? 0 : 1, false);
        }

        internal void OnLocalDrawer(Drawer drawer, bool open)
        {
            if (Applying)
                return;
            SendChange(drawer, Kind.Drawer, open ? 1 : 0, open ? 0 : 1, false);
        }

        internal void OnLocalRotable(SwitchCustomRotable rotable, int previous)
        {
            if (Applying || rotable.CurrentRotationIndex == previous)
                return;
            SendChange(rotable, Kind.Rotable, rotable.CurrentRotationIndex, previous, false);
        }

        internal void OnLocalCutterMode(Stands.AutomaticCutter cutter, int previous)
        {
            int mode = ReadState(cutter, Kind.CutterMode);
            if (Applying || mode == previous)
                return;
            SendChange(cutter, Kind.CutterMode, mode, previous, false);
        }

        // Same as AutomaticCutter.ChangeCutMode, but to a given mode: light that mode's diode only.
        private static void SetCutterMode(Stands.AutomaticCutter cutter, int mode)
        {
            var diodes = (List<MeshRenderer>)CutterDiodes.GetValue(cutter);
            if (diodes == null || mode < 0 || mode >= diodes.Count)
                return;
            var on = (Material)CutterDiodeOn.GetValue(cutter);
            var off = (Material)CutterDiodeOff.GetValue(cutter);
            int current = (int)CutterModeField.GetValue(cutter);
            if (current >= 0 && current < diodes.Count && diodes[current] != null)
                diodes[current].material = off;
            CutterModeField.SetValue(cutter, mode);
            if (diodes[mode] != null)
                diodes[mode].material = on;
        }

        internal void OnLocalPress(Component button, Kind kind)
        {
            if (Applying)
                return;
            SendChange(button, kind, 0, 0, true);
        }

        private void SendChange(Component c, Kind kind, int value, int previous, bool viaClick)
        {
            if (!Settled || !_session.IsConnected)
                return;
            string key = KeyOf(c);
            NetDataWriter writer = _session.BeginMessage(MessageType.InteractChange);
            writer.Put(SceneManager.GetActiveScene().name);
            writer.Put(key);
            writer.Put((byte)kind);
            writer.Put(value);
            writer.Put(previous);
            writer.Put(viaClick);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        // ---------- Receiving ----------

        private void OnMessage(int peerId, MessageType type, NetDataReader reader)
        {
            switch (type)
            {
                case MessageType.InteractReady:
                    if (_session.IsHost)
                        _pendingReadyScene = reader.GetString(MaxStringLength);
                    break;
                case MessageType.InteractSnapshot:
                    ApplySnapshot(reader);
                    break;
                case MessageType.InteractChange:
                    OnRemoteChange(reader);
                    break;
            }
        }

        private void OnRemoteChange(NetDataReader reader)
        {
            string scene = reader.GetString(MaxStringLength);
            string key = reader.GetString(MaxStringLength);
            var kind = (Kind)reader.GetByte();
            int value = reader.GetInt();
            int previous = reader.GetInt();
            bool viaClick = reader.GetBool();

            string localScene = SceneManager.GetActiveScene().name;
            if (scene != localScene)
            {
                _log.LogWarning($"[Interact] Ignored change to [{key}] for {scene}: this copy is in {localScene}.");
                return;
            }
            if (!Settled)
            {
                _log.LogWarning($"[Interact] Ignored change to [{key}]: this copy is still loading the kitchen.");
                return;
            }
            Apply(key, kind, value, previous, viaClick, queueIfBusy: true);
        }

        // Returns false only when the object was busy and the change was queued.
        private bool Apply(string key, Kind kind, int value, int previous, bool viaClick, bool queueIfBusy)
        {
            Component c = Find(key);
            if (c == null)
            {
                _log.LogWarning($"[Interact] No local object has key {key}. The copies may differ (different save, " +
                                "bought equipment, or the key list differs: compare the two interactable tables).");
                return true;
            }
            Kind localKind = KindOf(c);
            if (localKind != kind)
            {
                _log.LogWarning($"[Interact] Key {key} is a {localKind} here ({Describe(c)}) but a {kind} on the other copy; skipped.");
                return true;
            }

            bool busy = (c is Drawer d && IsDrawerBusy(d)) || (c is PushButton b && IsButtonBusy(b));
            if (busy)
            {
                if (queueIfBusy)
                {
                    _pending.Add(new PendingChange { Key = key, Kind = kind, Value = value, Previous = previous, ViaClick = viaClick });
                }
                return false;
            }

            Applying = true;
            try
            {
                switch (kind)
                {
                    case Kind.Door:
                    {
                        var door = (Door)c;
                        bool open = value != 0;
                        if (door.IsOpen == open)
                            return true;
                        DoorDo.Invoke(door, new object[] { open });
                        break;
                    }
                    case Kind.Drawer:
                    {
                        var drawer = (Drawer)c;
                        bool open = value != 0;
                        if (drawer.IsDrawerOpen == open)
                            return true;
                        if (open)
                            drawer.Open(whenLoaded: false);
                        else
                        {
                            drawer.Close();
                            if (!IsDrawerBusy(drawer))
                                _log.LogWarning($"[Interact] {Describe(c)} refused to close here (an item in it doesn't fit on this copy).");
                        }
                        break;
                    }
                    case Kind.Switch:
                    {
                        var sw = (Switch)c;
                        if (sw.Mode == value)
                            return true;
                        if (viaClick && sw.Mode == previous)
                        {
                            // Replaying the click also fires the game's OnClick event and click sound
                            // (e.g. the microwave stop button relies on OnClick).
                            sw.ProcessClick();
                            if (sw.Mode != value)
                            {
                                _log.LogWarning($"[Interact] Replayed click left {Describe(c)} at {StateText(kind, sw.Mode)} " +
                                                $"(locked={sw.LockState}); forcing {StateText(kind, value)}.");
                                sw.SetMode(value, force: true);
                            }
                        }
                        else
                        {
                            sw.SetMode(value, force: true);
                        }
                        break;
                    }
                    case Kind.Rotable:
                    {
                        var rotable = (SwitchCustomRotable)c;
                        if (rotable.CurrentRotationIndex == value)
                            return true;
                        if (value == 0)
                            rotable.TurnOff();
                        else
                            rotable.ChangeMode(true, value);
                        break;
                    }
                    case Kind.PushButton:
                        ((PushButton)c).OnLeftClick();
                        break;
                    case Kind.FrierButton:
                        ((FrierButton)c).OnLeftClick();
                        break;
                    case Kind.CutterMode:
                        SetCutterMode((Stands.AutomaticCutter)c, value);
                        break;
                }
            }
            catch (Exception e)
            {
                _log.LogError($"[Interact] Applying {kind} to {Describe(c)} threw: {e}");
                return true;
            }
            finally
            {
                Applying = false;
            }

            return true;
        }

        private void ProcessPending(float deltaTime)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingChange p = _pending[i];
                p.Age += deltaTime;
                if (Apply(p.Key, p.Kind, p.Value, p.Previous, p.ViaClick, queueIfBusy: false))
                {
                    _pending.RemoveAt(i);
                }
                else if (p.Age > PendingTimeout)
                {
                    _log.LogWarning($"[Interact] Gave up on [{p.Key}] {StateText(p.Kind, p.Value)}: still busy after {PendingTimeout}s.");
                    _pending.RemoveAt(i);
                }
            }
        }

        // ---------- Join snapshot (host wins) ----------

        // Host: send the full snapshot again once this copy is settled (after a pause, for example).
        internal void ResendSnapshot()
        {
            if (_session.IsHost && Settled)
                _pendingReadyScene = _kitchenScene;
        }

        private void SendSnapshot(string scene)
        {
            BuildTable(scene);
            var entries = _byKey.Where(p => p.Value != null && HasState(KindOf(p.Value))).ToList();
            NetDataWriter writer = _session.BeginMessage(MessageType.InteractSnapshot);
            writer.Put(scene);
            writer.Put(entries.Count);
            foreach (KeyValuePair<string, Component> pair in entries)
            {
                Kind kind = KindOf(pair.Value);
                writer.Put(pair.Key);
                writer.Put((byte)kind);
                writer.Put(ReadState(pair.Value, kind));
            }
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
        }

        private void ApplySnapshot(NetDataReader reader)
        {
            string scene = reader.GetString(MaxStringLength);
            string localScene = SceneManager.GetActiveScene().name;
            if (scene != localScene)
            {
                _log.LogWarning($"[Interact] Ignoring interactable snapshot for {scene}; this copy is in {localScene}.");
                return;
            }
            BuildTable(scene);
            var seen = new HashSet<string>();
            int count = reader.GetInt();
            int same = 0, applied = 0, missing = 0;
            for (int i = 0; i < count; i++)
            {
                string key = reader.GetString(MaxStringLength);
                var kind = (Kind)reader.GetByte();
                int value = reader.GetInt();
                seen.Add(key);
                if (!_byKey.TryGetValue(key, out Component c) || c == null)
                {
                    missing++;
                    _log.LogWarning($"[Interact] Host has [{key}] ({kind}, {StateText(kind, value)}) but this copy doesn't.");
                    continue;
                }
                if (KindOf(c) == kind && ReadState(c, kind) == value)
                {
                    same++;
                    continue;
                }
                Apply(key, kind, value, ReadState(c, kind), viaClick: false, queueIfBusy: true);
                applied++;
            }
            var onlyHere = _byKey.Where(p => p.Value != null && HasState(KindOf(p.Value)) && !seen.Contains(p.Key)).Select(p => p.Key).ToList();
            foreach (string key in onlyHere.Take(30))
                _log.LogWarning($"[Interact] This copy has [{key}] but the host doesn't.");
            if (onlyHere.Count > 30)
                _log.LogWarning($"[Interact] ...and {onlyHere.Count - 30} more only on this copy.");
            _log.LogInfo($"[Interact] Snapshot done: {same} already matched, {applied} changed to match the host, " +
                         $"{missing} missing here, {onlyHere.Count} only here.");
        }

        // ---------- Harmony ----------

        private void ApplyPatches()
        {
            Patch(typeof(Switch), "SetMode", new[] { typeof(int), typeof(bool) }, prefix: nameof(InteractPatches.SwitchSetModePrefix), postfix: nameof(InteractPatches.SwitchSetModePostfix));
            Patch(typeof(Switch), "ProcessClick", Type.EmptyTypes, prefix: nameof(InteractPatches.ClickDepthPrefix), finalizer: nameof(InteractPatches.ClickDepthFinalizer));
            Patch(typeof(Door), "Do", new[] { typeof(bool) }, postfix: nameof(InteractPatches.DoorDoPostfix));
            Patch(typeof(Drawer), "Open", new[] { typeof(bool) }, postfix: nameof(InteractPatches.DrawerOpenPostfix));
            Patch(typeof(Drawer), "Close", Type.EmptyTypes, postfix: nameof(InteractPatches.DrawerClosePostfix));
            Patch(typeof(SwitchCustomRotable), "ChangeMode", new[] { typeof(bool), typeof(int) }, prefix: nameof(InteractPatches.RotablePrefix), postfix: nameof(InteractPatches.RotablePostfix));
            Patch(typeof(SwitchCustomRotable), "TurnOff", Type.EmptyTypes, prefix: nameof(InteractPatches.RotablePrefix), postfix: nameof(InteractPatches.RotablePostfix));
            Patch(typeof(SwitchCustomRotable), "TurnOn", Type.EmptyTypes, prefix: nameof(InteractPatches.RotablePrefix), postfix: nameof(InteractPatches.RotablePostfix));
            Patch(typeof(PushButton), "OnLeftClick", Type.EmptyTypes, prefix: nameof(InteractPatches.PushButtonPrefix), postfix: nameof(InteractPatches.PushButtonPostfix));
            Patch(typeof(FrierButton), "OnLeftClick", Type.EmptyTypes, postfix: nameof(InteractPatches.FrierButtonPostfix));
            Patch(typeof(Stands.AutomaticCutter), "ChangeCutMode", Type.EmptyTypes, prefix: nameof(InteractPatches.CutterModePrefix), postfix: nameof(InteractPatches.CutterModePostfix));
            // Opening a door or drawer shoves the local player out of the way if they stand too close.
            // That shouldn't happen because the *other* player opened it.
            Patch(typeof(Switch), "MovePlayerWhenIsTooClose", new[] { typeof(Transform), typeof(float) }, prefix: nameof(InteractPatches.MovePlayerPrefix));
            Patch(typeof(Switch), "MovePlayerWhenIsTooCloseIgnoringYAxis", new[] { typeof(Transform), typeof(float) }, prefix: nameof(InteractPatches.MovePlayerPrefix));
        }

        private static HarmonyMethod Hm(string name) => name == null ? null : new HarmonyMethod(typeof(InteractPatches), name);

        private void Patch(Type type, string method, Type[] args, string prefix = null, string postfix = null, string finalizer = null)
        {
            try
            {
                MethodInfo original = AccessTools.Method(type, method, args);
                if (original == null)
                {
                    _log.LogError($"[Interact] Couldn't find {type.Name}.{method}; that part of interactable syncing is off.");
                    return;
                }
                _harmony.Patch(original, prefix: Hm(prefix), postfix: Hm(postfix), finalizer: Hm(finalizer));
            }
            catch (Exception e)
            {
                _log.LogError($"[Interact] Patching {type.Name}.{method} failed; that part of interactable syncing is off: {e}");
            }
        }

    }

    // Harmony entry points. Each forwards to InteractableSync and never lets an exception reach the game.
    internal static class InteractPatches
    {
        private static int _clickDepth;  // Inside Switch.ProcessClick (a player click).

        private static void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Interact] patch error: " + e);
            }
        }

        public static void SwitchSetModePrefix(Switch __instance, out int __state) => __state = __instance.Mode;

        public static void SwitchSetModePostfix(Switch __instance, int __state) =>
            Guard(() => InteractableSync.Instance?.OnLocalSwitchMode(__instance, __state, _clickDepth > 0));

        public static void ClickDepthPrefix() => _clickDepth++;

        public static Exception ClickDepthFinalizer(Exception __exception)
        {
            _clickDepth--;
            return __exception;
        }

        public static void DoorDoPostfix(Door __instance, bool open) =>
            Guard(() => InteractableSync.Instance?.OnLocalDoor(__instance, open));

        public static void DrawerOpenPostfix(Drawer __instance, bool whenLoaded)
        {
            if (!whenLoaded)
                Guard(() => InteractableSync.Instance?.OnLocalDrawer(__instance, true));
        }

        // Close() returns early without moving when an item in the drawer doesn't fit; then no tween is running.
        public static void DrawerClosePostfix(Drawer __instance) =>
            Guard(() =>
            {
                object tweener = AccessTools.Field(typeof(Drawer), "_currentTweener").GetValue(__instance);
                if (tweener != null)
                    InteractableSync.Instance?.OnLocalDrawer(__instance, false);
            });

        public static void RotablePrefix(SwitchCustomRotable __instance, out int __state) => __state = __instance.CurrentRotationIndex;

        public static void RotablePostfix(SwitchCustomRotable __instance, int __state) =>
            Guard(() => InteractableSync.Instance?.OnLocalRotable(__instance, __state));

        // The press only does something when the button isn't mid-animation or locked.
        public static void PushButtonPrefix(PushButton __instance, out bool __state)
        {
            __state = false;
            try
            {
                bool underChange = (bool)AccessTools.Field(typeof(PushButton), "underChange").GetValue(__instance);
                bool locked = (bool)AccessTools.PropertyGetter(typeof(PushButton), "IsLocked").Invoke(__instance, null);
                __state = !underChange && !locked;
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Interact] patch error: " + e);
            }
        }

        public static void PushButtonPostfix(PushButton __instance, bool __state)
        {
            if (__state)
                Guard(() => InteractableSync.Instance?.OnLocalPress(__instance, InteractableSync.Kind.PushButton));
        }

        public static void FrierButtonPostfix(FrierButton __instance) =>
            Guard(() => InteractableSync.Instance?.OnLocalPress(__instance, InteractableSync.Kind.FrierButton));

        public static void CutterModePrefix(Stands.AutomaticCutter __instance, out int __state)
        {
            __state = -1;
            try
            {
                __state = (int)AccessTools.Field(typeof(Stands.AutomaticCutter), "currentCutMode").GetValue(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Interact] patch error: " + e);
            }
        }

        public static void CutterModePostfix(Stands.AutomaticCutter __instance, int __state) =>
            Guard(() => InteractableSync.Instance?.OnLocalCutterMode(__instance, __state));

        public static bool MovePlayerPrefix()
        {
            return !InteractableSync.Applying;
        }
    }
}
