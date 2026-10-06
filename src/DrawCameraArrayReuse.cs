using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Refresh on EVERY draw, not once per frame: cameras can change mid-frame.
    // Only the allocating getter is replaced. Filtering, submission and replay
    // remain in DiorBearDrawMeshHelper, including its no-camera fallback.
    internal static class DrawCameraArrayReuse
    {
        private const string HarmonyId = "Quest3TriggerUI.draw-camera-array-reuse";
        private static Harmony harmony;
        private static Camera[] cameras = new Camera[0];

        internal static void Install()
        {
            if (harmony != null) return;
            try
            {
                harmony = new Harmony(HarmonyId);
                harmony.UnpatchAll(HarmonyId);
                int targets = 0;
                foreach (MethodInfo method in typeof(DiorBearDrawMeshHelper).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "DrawMesh") continue;
                    harmony.Patch(method, transpiler: new HarmonyMethod(typeof(DrawCameraArrayReuse).GetMethod(
                        "ReplaceGetter", BindingFlags.NonPublic | BindingFlags.Static)));
                    targets++;
                }
                if (targets != 2) throw new InvalidOperationException("DrawMesh overload count=" + targets);
                Log("installed; overloads=2; per-draw refresh; submission/replay unchanged");
            }
            catch (Exception e)
            {
                Shutdown();
                Log("not installed: " + e.Message);
            }
        }

        private static IEnumerable<CodeInstruction> ReplaceGetter(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getter = typeof(Camera).GetProperty("allCameras").GetGetMethod();
            MethodInfo replacement = typeof(DrawCameraArrayReuse).GetMethod("GetCameras", BindingFlags.NonPublic | BindingFlags.Static);
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(getter))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("Camera.allCameras anchor count=" + replaced);
        }

        private static Camera[] GetCameras()
        {
            int count = Camera.allCamerasCount;
            // Exact length preserves the original foreach and releases disabled
            // cameras on the next draw. Do not keep a high-water capacity array.
            if (cameras.Length != count) cameras = new Camera[count];
            int actual = Camera.GetAllCameras(cameras);
            if (actual != cameras.Length)
            {
                // Preserve getter semantics if Unity reports a changed count.
                cameras = Camera.allCameras;
            }
            return cameras;
        }

        internal static void Shutdown()
        {
            if (harmony != null) harmony.UnpatchAll(HarmonyId);
            harmony = null;
            cameras = new Camera[0];
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[draw-camera-reuse] " + message);
        }
    }
}
