using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Replace only the native yaw rate, after VaM's deadzone and UI/input guards.
    // Movement, height, pitch and the native navigation pivot remain unchanged.
    [HarmonyPatch(typeof(SuperController), "ProcessControllerNavigation")]
    internal static class GlobalVrYawSpeedPatch
    {
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int matches = 0;
            foreach (CodeInstruction code in codes)
            {
                if (code.opcode == OpCodes.Ldc_R4 && code.operand is float &&
                    (float)code.operand == 50f)
                {
                    // Mutate the instruction so labels and exception blocks survive.
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.PropertyGetter(
                        typeof(Quest3TriggerUIPlugin), "YawDegreesPerSecond");
                    matches++;
                }
            }
            if (matches != 1)
                throw new InvalidOperationException("Native yaw rate patch expected exactly one 50f constant; got " + matches);
            return codes;
        }
    }
}
