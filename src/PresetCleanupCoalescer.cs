using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Observe native sweeps; only coalesce our supplemental sweep. Native
    // optimization callbacks, GC and completion delegates are never skipped.
    //
    // Coverage model: every real UnloadUnusedAssets submission registers a
    // coverage point (NoteSweep). The janitor's supplemental sweep may reuse
    // that coverage instead of paying a second full-heap mark, but the reuse
    // is bounded — past ReuseSeconds or DeferMax release events the backstop
    // sweep runs, so coverage never rides forever. Debt is counted in release
    // events, not bytes; keep DeferMax small (a single janitor pass can hold
    // person-scale assets).
    internal static class PresetCleanupCoalescer
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ReuseSeconds;
        internal static ConfigEntry<int> DeferMax;
        private static Harmony _harmony;
        private static bool _tried;
        private static long _released, _covered = -1, _deferred;
        private static AsyncOperation _operation;
        private static float _coveredAt = -1f;
        internal static bool OperationPending { get { return _operation != null && !_operation.isDone; } }

        internal static void Install()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.preset-cleanup-coalesce");
                _harmony.UnpatchAll(_harmony.Id);
                int count = 0;
                foreach (Type t in typeof(MemoryOptimizer).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (!t.Name.StartsWith("<OptimizeMemoryUsage>", StringComparison.Ordinal) &&
                        !t.Name.StartsWith("<OptimizeMemoryUsageDelayed>", StringComparison.Ordinal)) continue;
                    MethodInfo move = t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (move == null) continue;
                    _harmony.Patch(move, transpiler: new HarmonyMethod(typeof(PresetCleanupCoalescer)
                        .GetMethod("ObserveCalls", BindingFlags.NonPublic | BindingFlags.Static)));
                    count++;
                }
                if (count != 2) throw new InvalidOperationException("native sweep state machines: " + count);
                Log("installed; native callbacks/GC unchanged; only covered supplemental sweeps reused");
            }
            catch (Exception e)
            {
                if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
                _harmony = null;
                Log("native sweep observer disabled: " + e.Message);
            }
        }

        private static IEnumerable<CodeInstruction> ObserveCalls(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int count = 0;
            foreach (var i in code)
            {
                MethodInfo method = i.operand as MethodInfo;
                if (i.opcode == OpCodes.Call && method != null && method.DeclaringType == typeof(Resources) &&
                    method.Name == "UnloadUnusedAssets" && method.GetParameters().Length == 0)
                {
                    i.operand = typeof(PresetCleanupCoalescer).GetMethod("NativeSweep",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    count++;
                }
            }
            if (count != 1) throw new InvalidOperationException("native UUA anchor count " + count);
            return code;
        }

        internal static void NoteReleased() { _released++; }

        internal static void NoteSweep(AsyncOperation op)
        {
            if (op == null) return;
            _operation = op;
            _covered = _released;
            _deferred = 0;
            _coveredAt = Time.realtimeSinceStartup;
        }

        internal static bool Covered(long released, long covered, bool hasOperation)
        {
            return hasOperation && covered >= released;
        }

        private static AsyncOperation NativeSweep()
        {
            // Always run a native-requested sweep: its callbacks may have freed
            // other resources unknown to this plugin, even in the same frame.
            return StartSweep("native");
        }

        internal static AsyncOperation UnloadForJanitor()
        {
            if (Enabled == null || !Enabled.Value) return StartSweep("supplemental");
            _deferred = _released > _covered ? _released - _covered : 0;
            float age = _coveredAt >= 0f ? Time.realtimeSinceStartup - _coveredAt : float.MaxValue;
            float reuse = ReuseSeconds != null && ReuseSeconds.Value > 0f ? ReuseSeconds.Value : 80f;
            int deferMax = DeferMax != null && DeferMax.Value > 0 ? DeferMax.Value : 4;
            if (_operation != null && age <= reuse && _deferred <= deferMax)
            {
                Log("reuse covered sweep epoch=" + _released + " age=" + age.ToString("0.#") +
                    "s deferred=" + _deferred + " completed=" + _operation.isDone);
                return _operation;
            }
            return StartSweep("supplemental");
        }

        private static AsyncOperation StartSweep(string origin)
        {
            long epoch = _released;
            long demotedBefore = UuaGate.SkippedCount;
            AsyncOperation op = Resources.UnloadUnusedAssets();
            bool demoted = UuaGate.SkippedCount != demotedBefore;
            // Only a sweep that really ran covers the release debt. A demoted
            // call hands back an older completed operation, so recording it as
            // coverage would make the debt disappear on paper.
            if (!demoted) NoteSweep(op);
            Log("sweep " + origin + " covers release epoch=" + epoch +
                (demoted ? " (demoted by UuaGate: no real sweep ran)" : ""));
            return op;
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _tried = false;
            _operation = null;
            _released = 0;
            _covered = -1;
            _deferred = 0;
            _coveredAt = -1f;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[preset-cleanup] " + message);
        }
    }
}
