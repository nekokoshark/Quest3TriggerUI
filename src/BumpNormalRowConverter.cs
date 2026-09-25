using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class BumpNormalRowConverter
    {
        internal static ConfigEntry<bool> Enabled;
        private static Harmony _harmony;
        private static bool _tried;

        internal static void Install()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.bump-normal-rows");
                _harmony.UnpatchAll(_harmony.Id);
                MethodInfo process = typeof(ImageLoaderThreaded.QueuedImage).GetMethod("ProcessFromStream",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (process == null) throw new MissingMethodException("ProcessFromStream");
                _harmony.Patch(process, transpiler: new HarmonyMethod(typeof(BumpNormalRowConverter)
                    .GetMethod("Transpile", BindingFlags.NonPublic | BindingFlags.Static)));
                Log("installed: native output layout preserved; height scratch uses 3 rows instead of full image");
            }
            catch (Exception e)
            {
                if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
                _harmony = null;
                Log("native conversion retained: " + e.Message);
            }
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int start = -1, end = -1, count = 0;
            Label exit = default(Label);
            for (int i = 0; i + 1 < code.Count; i++)
            {
                FieldInfo field = code[i].operand as FieldInfo;
                if (field == null || field.DeclaringType != typeof(ImageLoaderThreaded.QueuedImage) ||
                    field.Name != "createNormalFromBump" || code[i].opcode != OpCodes.Ldfld ||
                    (code[i + 1].opcode != OpCodes.Brfalse && code[i + 1].opcode != OpCodes.Brfalse_S)) continue;
                Label target = (Label)code[i + 1].operand;
                int stop = code.FindIndex(i + 2, c => c.labels.Contains(target));
                if (stop < 0) continue;
                bool heightMap = false;
                for (int k = i + 2; k < stop; k++)
                    if (code[k].opcode == OpCodes.Newarr && Equals(code[k].operand, typeof(float[]))) heightMap = true;
                // Ignore the earlier pixel-format selection using the same flag.
                if (!heightMap) continue;
                start = i + 2; end = stop; exit = target; count++;
            }
            if (count != 1 || end <= start || code[start].blocks.Count != 0)
                throw new InvalidOperationException("native bump block not uniquely recognized");
            var load = new CodeInstruction(OpCodes.Ldarg_0);
            load.labels.AddRange(code[start].labels);
            code[start].labels.Clear();
            code.InsertRange(start, new[] { load, new CodeInstruction(OpCodes.Call,
                typeof(BumpNormalRowConverter).GetMethod("TryConvert", BindingFlags.NonPublic | BindingFlags.Static)),
                new CodeInstruction(OpCodes.Brtrue, exit) });
            // Keep original code and Dispose tail. Config=false executes native block.
            return code;
        }

        private static bool TryConvert(ImageLoaderThreaded.QueuedImage q)
        {
            if (Enabled != null && !Enabled.Value) return false;
            long pixels = (long)q.width * q.height;
            if (q.width <= 0 || q.height <= 0 || pixels > int.MaxValue / 8 ||
                q.raw == null || q.raw.LongLength < pixels * 4) return false;
            q.raw = Convert(q.raw, q.width, q.height, q.bumpStrength);
            return true;
        }

        internal static byte[] Convert(byte[] raw, int width, int height, float strength)
        {
            // Preserve even the native oversized, zero-padded output length so
            // texture upload and existing disk cache byte layouts stay unchanged.
            var output = new byte[checked(width * height * 8)];
            var rows = new float[checked(width * 3)];
            FillRow(raw, rows, width, 0);
            Vector3 normal = new Vector3();
            for (int y = 0; y < height; y++)
            {
                if (y + 1 < height) FillRow(raw, rows, width, y + 1);
                int current = (y % 3) * width;
                int previous = y > 0 ? ((y - 1) % 3) * width : -1;
                int next = y + 1 < height ? ((y + 1) % 3) * width : -1;
                for (int x = 0; x < width; x++)
                {
                    float bl = Sample(rows, next, x - 1, width);
                    float l = Sample(rows, current, x - 1, width);
                    float tl = Sample(rows, previous, x - 1, width);
                    float down = Sample(rows, next, x, width);
                    float up = Sample(rows, previous, x, width);
                    float br = Sample(rows, next, x + 1, width);
                    float r = Sample(rows, current, x + 1, width);
                    float tr = Sample(rows, previous, x + 1, width);
                    // Match native evaluation order and Vector3.Normalize.
                    normal.x = (br + 2f * r + tr - bl - 2f * l - tl) * strength;
                    normal.y = (tl + 2f * up + tr - bl - 2f * down - br) * strength;
                    normal.z = 1f;
                    normal.Normalize();
                    normal.x = normal.x * 0.5f + 0.5f;
                    normal.y = normal.y * 0.5f + 0.5f;
                    normal.z = normal.z * 0.5f + 0.5f;
                    int index = (y * width + x) * 4;
                    output[index] = (byte)(int)(normal.x * 255f);
                    output[index + 1] = (byte)(int)(normal.y * 255f);
                    output[index + 2] = (byte)(int)(normal.z * 255f);
                    output[index + 3] = 255;
                }
            }
            return output;
        }

        private static void FillRow(byte[] raw, float[] rows, int width, int y)
        {
            int row = (y % 3) * width;
            int source = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int index = source + x * 4;
                rows[row + x] = (raw[index] + raw[index + 1] + raw[index + 2]) / 768f;
            }
        }

        private static float Sample(float[] rows, int row, int x, int width)
        {
            return row < 0 || x < 0 || x >= width ? 0.5f : rows[row + x];
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null; _tried = false;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[bump-rows] " + message);
        }
    }
}
