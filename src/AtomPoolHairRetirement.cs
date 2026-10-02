using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Complete only the missing unlocked-hair branch of the native callback.
    // Do not destroy pooled Atoms or change preset-lock/reuse semantics.
    internal static class AtomPoolHairRetirement
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Pool = typeof(SuperController).GetField("typeToAtomPool", InstanceFields);
        private static readonly FieldInfo Storables = typeof(Atom).GetField("_storables", InstanceFields);
        private static readonly FieldInfo FemaleHair = typeof(DAZCharacterSelector).GetField("_femaleHairItems", InstanceFields);
        private static readonly FieldInfo MaleHair = typeof(DAZCharacterSelector).GetField("_maleHairItems", InstanceFields);
        private static Harmony _harmony;
        private static float _nextReport;
        private static long _callbacks, _readyRetired;

        private static bool NeedsUnlockedHairCleanup(DAZCharacterSelector selector)
        {
            var atom = selector.containingAtom;
            return atom != null && atom.inPool && atom.keepParamLocksWhenPuttingBackInPool &&
                !selector.appearanceLocked && !selector.IsCustomAppearanceParamLocked("hair");
        }

        private static int ReadyHair(DAZCharacterSelector selector)
        {
            // Use audited fields, not the public getters: those invoke Init().
            return ReadyItems((DAZHairGroup[])FemaleHair.GetValue(selector)) +
                ReadyItems((DAZHairGroup[])MaleHair.GetValue(selector));
        }

        private static int ReadyItems(DAZHairGroup[] items)
        {
            if (items == null) return 0;
            int count = 0;
            foreach (var item in items)
                if (item != null && item.ready) count++;
            return count;
        }

        private static void AfterOptimizeMemory(DAZCharacterSelector __instance)
        {
            // Native OptimizeMemory has no else for unlocked hair when the
            // pooled Atom keeps locks. All other branches already unload hair.
            if (!NeedsUnlockedHairCleanup(__instance)) return;
            int before = ReadyHair(__instance);
            __instance.UnloadInactiveHairItems();
            int retired = before - ReadyHair(__instance);
            _callbacks++;
            _readyRetired += retired;
            if (retired != 0)
                Log("unlocked-hair readyRetired=" + retired + " callbacks=" + _callbacks + " countsNotBytes=True");
        }

        internal static string PoolSnapshot(SuperController controller)
        {
            var pool = (Dictionary<string, List<Atom>>)Pool.GetValue(controller);
            if (pool == null) return "pool status=not-initialized";
            int entries = 0, idle = 0, dead = 0, persons = 0, idlePersons = 0, poolablePersons = 0;
            int keepLocks = 0, affectedSelectors = 0, affectedReady = 0, idleReady = 0;
            foreach (var pair in pool)
            {
                foreach (var atom in pair.Value)
                {
                    entries++;
                    if (atom == null) { dead++; continue; }
                    if (atom.type == "Person") { persons++; if (atom.isPoolable) poolablePersons++; }
                    if (!atom.inPool) continue;
                    idle++;
                    if (atom.type == "Person") idlePersons++;
                    if (atom.keepParamLocksWhenPuttingBackInPool) keepLocks++;
                    var storables = (List<JSONStorable>)Storables.GetValue(atom);
                    if (storables == null) continue;
                    foreach (var storable in storables)
                    {
                        var selector = storable as DAZCharacterSelector;
                        if (selector == null) continue;
                        int ready = ReadyHair(selector);
                        idleReady += ready;
                        if (NeedsUnlockedHairCleanup(selector)) { affectedSelectors++; affectedReady += ready; }
                    }
                }
            }
            return "pool entries=" + entries + " idle=" + idle + " dead=" + dead +
                " personEntries=" + persons + " poolablePersonEntries=" + poolablePersons + " idlePersons=" + idlePersons +
                " keepLocksIdle=" + keepLocks + " unlockedHairSelectors=" + affectedSelectors +
                " idleReadyHair=" + idleReady + " affectedReadyHair=" + affectedReady +
                " callbacks=" + _callbacks + " readyRetired=" + _readyRetired + " readOnly=True countsNotBytes=True";
        }

        internal static void Tick()
        {
            if (_harmony == null || Time.realtimeSinceStartup < _nextReport) return;
            _nextReport = Time.realtimeSinceStartup + 60f;
            var controller = SuperController.singleton;
            if (controller != null) Log(PoolSnapshot(controller));
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            if (Pool == null || Storables == null || FemaleHair == null || MaleHair == null)
                throw new MissingFieldException("Atom pool/hair inventory fields changed");
            if (Pool.FieldType != typeof(Dictionary<string, List<Atom>>) ||
                Storables.FieldType != typeof(List<JSONStorable>) ||
                FemaleHair.FieldType != typeof(DAZHairGroup[]) || MaleHair.FieldType != typeof(DAZHairGroup[]))
                throw new InvalidOperationException("Atom pool/hair inventory field types changed");
            var method = typeof(DAZCharacterSelector).GetMethod("OptimizeMemory", InstanceFields | BindingFlags.DeclaredOnly);
            if (method == null) throw new MissingMethodException("DAZCharacterSelector.OptimizeMemory");
            _harmony = new Harmony("quest3triggerui.atompoolhair." + typeof(AtomPoolHairRetirement).Namespace);
            try
            {
                _harmony.Patch(method, postfix: new HarmonyMethod(typeof(AtomPoolHairRetirement), "AfterOptimizeMemory"));
                Log("installed callback=1 unlockedPooledHair=completed lockedHair=preserved poolEviction=unchanged gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            Log("shutdown callbacks=" + _callbacks + " readyRetired=" + _readyRetired + " countsNotBytes=True");
            _callbacks = _readyRetired = 0;
            _nextReport = 0f;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[atom-pool-hair] " + message);
        }
    }
}
