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
    internal static class PresetCleanupCoalescer
    {
        internal static ConfigEntry<bool> Enabled;
        private static Harmony _harmony;
        private static bool _tried;
        private static long _released, _covered = -1;
        private static AsyncOperation _operation;
        private static int _releaseFrame = -1, _coveredFrame = -1;

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

        internal static void NoteReleased() { _released++; _releaseFrame = Time.frameCount; }

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
            if ((Enabled == null || Enabled.Value) && _coveredFrame > _releaseFrame &&
                Covered(_released, _covered, _operation != null))
            {
                Log("reuse covered sweep epoch=" + _released + " completed=" + _operation.isDone);
                return _operation;
            }
            return StartSweep("supplemental");
        }

        private static AsyncOperation StartSweep(string origin)
        {
            long epoch = _released;
            AsyncOperation op = Resources.UnloadUnusedAssets();
            // Publish only after native submission succeeds.
            _operation = op;
            _covered = epoch;
            _coveredFrame = Time.frameCount;
            Log("sweep " + origin + " covers release epoch=" + epoch);
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
            _releaseFrame = _coveredFrame = -1;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[preset-cleanup] " + message);
        }
    }
}
