using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CookingSimMP
{
    // Breakable parts of the kitchen that aren't items (v0.10): windows and glass doors, shelves, the TV, lamps,
    // lights, the fan, the power box and studio accessories. Each type has its own "break" method and a Broken
    // flag, and each one is identified by its BreakableID, which the game's save uses too.
    //
    // When one breaks on this copy (Broken goes from false to true inside its break method), the other copy is told
    // and breaks the same one. When one is fixed (Broken set back to false, which swaps in the working model), the
    // other copy fixes it too. The TV also has a half-way state: the first hard knock cracks its screen (IsBruised)
    // and the next one breaks it, so a crack is sent too (v0.10.1).
    internal sealed class PropSync
    {
        internal static PropSync Instance;

        // True while breaking or fixing a prop because the other copy did, so it isn't sent back.
        private static bool _applying;

        // What a message says happened.
        private const byte StateFixed = 0;
        private const byte StateBroken = 1;
        private const byte StateBruised = 2;

        // The prefixes' __state: what the prop was before the call.
        private const int WasBroken = 1;
        private const int WasBruised = 2;

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly Harmony _harmony = new Harmony(Plugin.PluginGuid + ".props");

        // Each type's break method: how to call it again here. "flag" is Set(true), "none" is Destruct(),
        // "power" is PowerDestruction.Set(true, group).
        private sealed class PropType
        {
            public Type Type;
            public MethodInfo Break;
            public string Kind;
            public PropertyInfo Broken;
            public PropertyInfo Id;
            public FieldInfo Bruised;   // Only types with a half-broken state (the TV); null otherwise.
        }

        private static readonly Dictionary<string, PropType> Types = new Dictionary<string, PropType>();

        public PropSync(NetSession session, ManualLogSource log)
        {
            Instance = this;
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;

            Register(typeof(ShelfDestruction), "Set", new[] { typeof(bool) }, "flag");
            Register(typeof(TVDestruction), "Set", new[] { typeof(bool) }, "flag");
            Register(typeof(SimpleDestruction), "Set", new[] { typeof(bool) }, "flag");
            Register(typeof(DoorDestruction), "Set", new[] { typeof(bool) }, "flag");
            Register(typeof(FanDestruction), "Destruct", Type.EmptyTypes, "none");
            Register(typeof(LightDestruction), "Destruct", Type.EmptyTypes, "none");
            Register(typeof(PowerDestruction), "Set", new[] { typeof(bool), typeof(int) }, "power");
            Register(typeof(StudioAccessoriesDestructable), "Destruct", Type.EmptyTypes, "none");
        }

        public static bool Handles(MessageType type) => type == MessageType.PropBroken;

        private bool Active => _session.IsConnected && WorldSync.InKitchen;

        private void Register(Type type, string method, Type[] args, string kind)
        {
            try
            {
                var prop = new PropType
                {
                    Type = type,
                    Break = AccessTools.Method(type, method, args),
                    Kind = kind,
                    Broken = AccessTools.Property(type, "Broken"),
                    Id = AccessTools.Property(type, "BreakableID"),
                    Bruised = type == typeof(TVDestruction) ? AccessTools.Field(type, "IsBruised") : null,
                };
                if (prop.Break == null || prop.Broken == null || prop.Id == null)
                {
                    _log.LogWarning($"[Props] Couldn't find {type.Name}.{method}, Broken or BreakableID; {type.Name} breaking isn't synced.");
                    return;
                }
                Types[type.Name] = prop;
                _harmony.Patch(prop.Break,
                    prefix: new HarmonyMethod(typeof(PropSync), nameof(BeforeChange)),
                    postfix: new HarmonyMethod(typeof(PropSync), nameof(AfterBreak)));
                MethodInfo setter = prop.Broken.GetSetMethod(true);
                if (setter != null)
                    _harmony.Patch(setter,
                        prefix: new HarmonyMethod(typeof(PropSync), nameof(BeforeChange)),
                        postfix: new HarmonyMethod(typeof(PropSync), nameof(AfterSetBroken)));
            }
            catch (Exception e)
            {
                _log.LogError($"[Props] Patching {type.Name} failed; its breaking isn't synced: {e}");
            }
        }

        private static bool IsBroken(PropType prop, object instance)
        {
            try
            {
                return (bool)prop.Broken.GetValue(instance, null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsBruised(PropType prop, object instance)
        {
            try
            {
                return prop.Bruised != null && (bool)prop.Bruised.GetValue(instance);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Describe(byte state) =>
            state == StateBroken ? "broke" : state == StateBruised ? "was cracked" : "was fixed";

        // ---------- Local change ----------

        private void SendChange(object instance, byte state, int group)
        {
            if (_applying || !Active || !(instance is Component component) || component == null)
                return;
            if (!Types.TryGetValue(instance.GetType().Name, out PropType prop) && !TryFindBaseType(instance.GetType(), out prop))
                return;
            int id = (int)prop.Id.GetValue(instance, null);
            Vector3 p = component.transform.position;
            NetDataWriter writer = _session.BeginMessage(MessageType.PropBroken);
            writer.Put(prop.Type.Name);
            writer.Put(id);
            writer.Put(state);
            writer.Put(group);
            writer.Put(p.x); writer.Put(p.y); writer.Put(p.z);
            _session.SendToAll(DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"[Props] {prop.Type.Name} {id} {Describe(state)} here; told the other player.");
        }

        private static bool TryFindBaseType(Type type, out PropType prop)
        {
            for (Type t = type.BaseType; t != null; t = t.BaseType)
            {
                if (Types.TryGetValue(t.Name, out prop))
                    return true;
            }
            prop = null;
            return false;
        }

        // ---------- Other player's change ----------

        private void OnMessage(int peerId, MessageType type, NetDataReader reader)
        {
            if (type != MessageType.PropBroken || !Active)
                return;
            string typeName = reader.GetString(128);
            int id = reader.GetInt();
            byte state = reader.GetByte();
            int group = reader.GetInt();
            var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            if (!Types.TryGetValue(typeName, out PropType prop))
                return;

            // The working model swapped in by a repair can share the old one's ID for a frame; pick one that can
            // still change that way, nearest to where it was on the other copy.
            Func<Component, bool> canChange;
            if (state == StateBroken)
                canChange = c => !IsBroken(prop, c);
            else if (state == StateBruised)
                canChange = c => prop.Bruised != null && !IsBroken(prop, c) && !IsBruised(prop, c);
            else
                canChange = c => IsBroken(prop, c) || IsBruised(prop, c);
            Component target = Object.FindObjectsOfType(prop.Type)
                .OfType<Component>()
                .Where(c => c != null && (int)prop.Id.GetValue(c, null) == id && canChange(c))
                .OrderBy(c => (c.transform.position - position).sqrMagnitude)
                .FirstOrDefault();
            if (target == null)
                return;

            _applying = true;
            try
            {
                if (state == StateFixed)
                {
                    // The TV's own repair end puts back its screen, picture and texture; setting Broken only flips the flag.
                    if (target is TVDestruction tv)
                    {
                        tv.OnRepairStart();
                        tv.OnRepairEnd();
                    }
                    else
                    {
                        prop.Broken.SetValue(target, false, null);
                    }
                }
                else if (state == StateBruised)
                    prop.Break.Invoke(target, new object[] { false });   // TVDestruction.Set(false) cracks an uncracked TV.
                else if (prop.Kind == "flag")
                    prop.Break.Invoke(target, new object[] { true });
                else if (prop.Kind == "power")
                    prop.Break.Invoke(target, new object[] { true, group });
                else
                    prop.Break.Invoke(target, null);
                _log.LogInfo($"[Props] {typeName} {id} {Describe(state)} on the other player's copy; did the same here.");
            }
            catch (Exception e)
            {
                _log.LogWarning($"[Props] Changing {typeName} {id} like the other player ({Describe(state)}) failed: {e.InnerException?.Message ?? e.Message}");
            }
            finally
            {
                _applying = false;
            }
        }

        // ---------- Harmony ----------

        private static void BeforeChange(object __instance, out int __state)
        {
            __state = 0;
            try
            {
                if (Types.TryGetValue(__instance.GetType().Name, out PropType prop) || TryFindBaseType(__instance.GetType(), out prop))
                    __state = (IsBroken(prop, __instance) ? WasBroken : 0) | (IsBruised(prop, __instance) ? WasBruised : 0);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Props] patch error: " + e);
            }
        }

        // A break method ran: did it break (or crack) the prop just now?
        private static void AfterBreak(object __instance, object[] __args, int __state)
        {
            try
            {
                if ((__state & WasBroken) != 0 || Instance == null)
                    return;
                if (!(Types.TryGetValue(__instance.GetType().Name, out PropType prop) || TryFindBaseType(__instance.GetType(), out prop)))
                    return;
                if (IsBroken(prop, __instance))
                {
                    int group = prop.Kind == "power" && __args != null && __args.Length > 1 ? (int)__args[1] : 0;
                    Instance.SendChange(__instance, StateBroken, group);
                }
                else if ((__state & WasBruised) == 0 && IsBruised(prop, __instance))
                {
                    Instance.SendChange(__instance, StateBruised, 0);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Props] patch error: " + e);
            }
        }

        // Broken set to false: the prop was fixed. (Setting it to true happens inside the break methods.)
        private static void AfterSetBroken(object __instance, bool value, int __state)
        {
            try
            {
                if ((__state & WasBroken) != 0 && !value)
                    Instance?.SendChange(__instance, StateFixed, 0);
            }
            catch (Exception e)
            {
                Debug.LogError("[CookingSimMP] [Props] patch error: " + e);
            }
        }
    }
}
