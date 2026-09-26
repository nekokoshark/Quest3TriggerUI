using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace Quest3TriggerUI
{
    internal static class TextureCompletionBudget
    {
        internal static ConfigEntry<bool> Enabled;
        private static Harmony _harmony;
        private static bool _reported;
        [ThreadStatic] private static long _start, _bytes;
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool Active() { return Enabled == null || Enabled.Value; }
        private static void Begin() { _start = Stopwatch.GetTimestamp(); _bytes = 0; }
        internal static bool Allow(int completed, double milliseconds, long bytes)
        {
            return completed < 4 || (completed < 16 && milliseconds < 2.0 && bytes < 32L * 1024 * 1024);
        }
        private static int Limit(int completed)
        {
            if (!Active()) return 4;
            bool more = Allow(completed, (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency, _bytes);
            if (more && completed == 4 && !_reported)
            { _reported = true; Quest3TriggerUIPlugin.Log.LogInfo("[texture-completion] first native pass allowed beyond four cheap completions"); }
            return more ? 16 : completed;
        }
        private static void Charge(ImageLoaderThreaded.QueuedImage q)
        {
            if (Active() && q != null && q.raw != null) _bytes += q.raw.LongLength;
        }
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int bound = -1, finish = -1;
            for (int i = 1; i + 1 < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Ldc_I4_4 && code[i - 1].opcode == OpCodes.Ldloc_0 &&
                    (code[i + 1].opcode == OpCodes.Blt || code[i + 1].opcode == OpCodes.Blt_S))
                { if (bound >= 0) throw new InvalidOperationException("multiple completion loops"); bound = i; }
                var m = code[i].operand as MethodInfo;
                if (m != null && m.DeclaringType == typeof(ImageLoaderThreaded.QueuedImage) && m.Name == "Finish") finish = i;
            }
            if (bound < 0 || finish < 0 || finish >= bound) throw new InvalidOperationException("native completion anchors changed");
            // Preserve the native loop, cache insertion, callbacks and queue locks.
            code[bound].opcode = OpCodes.Dup; code[bound].operand = null;
            code.Insert(bound + 1, new CodeInstruction(OpCodes.Call, typeof(TextureCompletionBudget).GetMethod("Limit", All)));
            var dup = new CodeInstruction(OpCodes.Dup); dup.labels.AddRange(code[finish].labels); code[finish].labels.Clear();
            code.InsertRange(finish, new[] { dup, new CodeInstruction(OpCodes.Call, typeof(TextureCompletionBudget).GetMethod("Charge", All)) });
            return code;
        }
        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.texture-completion-budget");
                _harmony.Patch(typeof(ImageLoaderThreaded).GetMethod("PostProcessCompletedImages", All),
                    prefix: new HarmonyMethod(typeof(TextureCompletionBudget).GetMethod("Begin", All)),
                    transpiler: new HarmonyMethod(typeof(TextureCompletionBudget).GetMethod("Transpile", All)));
                Quest3TriggerUIPlugin.Log.LogInfo("[texture-completion] installed; native4 retained; extra cheap items up to16 / soft2ms / raw32MiB; callbacks unchanged");
            }
            catch (Exception e) { Shutdown(); Quest3TriggerUIPlugin.Log.LogInfo("[texture-completion] not installed: " + e.Message); }
        }
        internal static void Shutdown() { if (_harmony != null) _harmony.UnpatchAll(_harmony.Id); _harmony = null; }
    }
}
