using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Receipts from native allocation/destruction, not an alternative owner count.
    // No resource is destroyed because of a receipt or a missing weak wrapper.
    internal static class NativeAllocationLedger
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const int Capacity = 8192;
        private static readonly Type[] Allocators = { typeof(MVR.ObjectAllocator), typeof(DAZSkinV2), typeof(DAZSkinWrap), typeof(MaterialOptions) };
        private static readonly Dictionary<Type, FieldInfo> Lists = new Dictionary<Type, FieldInfo>();
        private sealed class Entry
        {
            internal int Id;
            internal WeakReference Resource;
            internal int AllocatorId;
            internal string Source;
            internal bool Requested;
        }
        private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
        private static readonly HashSet<int> Confirmed = new HashSet<int>();
        private static readonly Queue<Entry> Pending = new Queue<Entry>();
        private static Harmony _harmony;
        private static float _next;
        private static long _registered, _seeded, _requested, _destroyed, _weakLost, _overflow, _failures;
        internal static long Revision;

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                // Validate the exact native fields/signatures before patching.
                foreach (Type type in Allocators)
                {
                    var list = type.GetField("allocatedObjects", All | BindingFlags.DeclaredOnly);
                    if (list == null || list.FieldType != typeof(List<UnityEngine.Object>))
                        throw new MissingFieldException(type.FullName, "allocatedObjects");
                    Lists[type] = list;
                    if (type.GetMethod("RegisterAllocatedObject", All | BindingFlags.DeclaredOnly, null, new[] { typeof(UnityEngine.Object) }, null) == null ||
                        type.GetMethod("DestroyAllocatedObjects", All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) == null)
                        throw new MissingMethodException(type.FullName, "native allocation lifecycle");
                }
                _harmony = new Harmony("Quest3TriggerUI.native-allocation-ledger");
                _harmony.UnpatchAll(_harmony.Id);
                foreach (Type type in Allocators)
                {
                    _harmony.Patch(type.GetMethod("RegisterAllocatedObject", All | BindingFlags.DeclaredOnly),
                        postfix: new HarmonyMethod(typeof(NativeAllocationLedger).GetMethod("Registered", All)));
                    _harmony.Patch(type.GetMethod("DestroyAllocatedObjects", All | BindingFlags.DeclaredOnly),
                        transpiler: new HarmonyMethod(typeof(NativeAllocationLedger).GetMethod("ObserveDestroy", All)));
                }
                Log("installed; 4 native allocator families; weak allocation/destroy receipts; original Destroy preserved; no UUA authority");
            }
            catch (Exception e) { Shutdown(); Fault(e); }
        }

        private static bool Supported(UnityEngine.Object value)
        { return value is Texture || value is Material || value is Mesh; }

        private static bool PersonScope(Component owner)
        {
            if (owner == null) return false;
            // Hair is a separate dynamic item and remains outside this ledger.
            for (Transform node = owner.transform; node != null; node = node.parent)
            {
                var dynamicItem = node.GetComponent<DAZDynamicItem>();
                if (dynamicItem != null && !(dynamicItem is DAZClothingItem)) return false;
                var atom = node.GetComponent<Atom>();
                if (atom != null) return atom.type == "Person";
            }
            return false;
        }

        private static void Registered(Component __instance, UnityEngine.Object __0)
        {
            if (!Application.isPlaying || !ResourceLedger.NativeObservationEnabled) return;
            try { if (PersonScope(__instance)) Record(__0, __instance, false); }
            catch (Exception e) { Fault(e); }
        }

        private static void Record(UnityEngine.Object value, Component owner, bool seeded)
        {
            if (value == null || !Supported(value)) return;
            int id = value.GetInstanceID(); Entry entry;
            if (Entries.TryGetValue(id, out entry))
            {
                if (ReferenceEquals(entry.Resource.Target, value)) return;
                // Recycled ID does not transfer a destruction receipt to its new object.
                Entries.Remove(id); _weakLost++; Revision++;
            }
            if (Entries.Count >= Capacity || Pending.Count >= Capacity * 2)
            { _overflow++; Revision++; return; }
            entry = new Entry { Id = id, Resource = new WeakReference(value), Source = owner.GetType().FullName, AllocatorId = owner.GetInstanceID() };
            Entries.Add(id, entry); Pending.Enqueue(entry);
            if (seeded) _seeded++; else _registered++;
            Revision++;
        }

        // Called only during the existing bounded person/clothing graph walk.
        // This seeds allocations made before hot reload without scanning the scene.
        internal static void Capture(Component owner, Dictionary<int, ResourceLedgerIndex.Asset> assets)
        {
            if (_harmony == null || owner == null) return;
            for (Type type = owner.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field;
                if (!Lists.TryGetValue(type, out field)) continue;
                var list = field.GetValue(owner) as List<UnityEngine.Object>;
                if (list == null) continue;
                foreach (var value in list)
                {
                    if (value == null || !Supported(value)) continue;
                    Record(value, owner, true);
                    // Reuse graph insertion so material texture edges are not lost.
                    ResourceLedger.Add(assets, value);
                }
            }
        }

        private static IEnumerable<CodeInstruction> ObserveDestroy(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo original = typeof(UnityEngine.Object).GetMethod("Destroy", All, null, new[] { typeof(UnityEngine.Object) }, null);
            int found = 0;
            foreach (var instruction in code)
                if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, original))
                {
                    // Identical signature: preserve branches, exception blocks and native order.
                    instruction.operand = typeof(NativeAllocationLedger).GetMethod("NativeDestroy", All);
                    found++;
                }
            if (found != 1) throw new InvalidOperationException("native allocation Destroy anchor count=" + found);
            return code;
        }

        private static void NativeDestroy(UnityEngine.Object value)
        {
            // Invoke exactly once, including null and untracked resources. Native exceptions propagate.
            UnityEngine.Object.Destroy(value);
            try
            {
                if (ReferenceEquals(value, null)) return;
                Entry entry;
                if (!Entries.TryGetValue(value.GetInstanceID(), out entry) ||
                    !ReferenceEquals(entry.Resource.Target, value) || entry.Requested) return;
                entry.Requested = true; _requested++; Revision++;
            }
            catch (Exception e) { Fault(e); }
        }

        internal static void Tick()
        {
            if (_harmony == null || Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.25f;
            int count = Math.Min(64, Pending.Count);
            for (int n = 0; n < count; n++)
            {
                Entry entry = Pending.Dequeue(), current;
                if (!Entries.TryGetValue(entry.Id, out current) || !ReferenceEquals(current, entry)) continue;
                var value = entry.Resource.Target as UnityEngine.Object;
                if (ReferenceEquals(value, null))
                { Entries.Remove(entry.Id); _weakLost++; Revision++; }
                else if (value == null)
                { Entries.Remove(entry.Id); Confirmed.Add(entry.Id); _destroyed++; Revision++; }
                else Pending.Enqueue(entry);
            }
        }

        internal static string Describe(UnityEngine.Object value)
        {
            if (ReferenceEquals(value, null)) return "unknown";
            Entry entry;
            if (!Entries.TryGetValue(value.GetInstanceID(), out entry) || !ReferenceEquals(entry.Resource.Target, value))
                return Confirmed.Contains(value.GetInstanceID()) ? "native-destroy-confirmed" : "unknown";
            return (entry.Requested ? "native-destroy-requested:" : "native-allocated:") + entry.Source + "#" + entry.AllocatorId;
        }
        internal static string Summary()
        {
            return "nativeAllocLive=" + Entries.Count + " nativeRegistered=" + _registered + " nativeSeeded=" + _seeded +
                " nativeDestroyRequests=" + _requested + " nativeConfirmedGone=" + _destroyed +
                " nativeConfirmedHistory=" + Confirmed.Count + " nativeWeakLost=" + _weakLost + " nativeAllocOverflow=" + _overflow + " nativeAllocFailures=" + _failures;
        }
        internal static void Clear()
        {
            Entries.Clear(); Pending.Clear(); Confirmed.Clear(); _next = 0;
            _registered = _seeded = _requested = _destroyed = _weakLost = _overflow = _failures = 0;
            Revision++;
        }
        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null; Lists.Clear(); Clear();
        }
        private static void Fault(Exception e)
        { _failures++; Revision++; if (_failures <= 3) Log("receipt observation failed: " + e.Message); }
        private static void Log(string value)
        { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[native-allocation] " + value); }
    }
}
