using System;
using System.Reflection;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Patch is invoked only when installing hooks, never in frame/input loops.
    // Harmony 2.0 returns DynamicMethod; newer Harmony/HarmonyX return MethodInfo.
    // Do not encode either return type in a MemberRef.
    internal static class HarmonyCompat
    {
        private static readonly MethodInfo PatchMethod = ResolvePatch();

        private static MethodInfo ResolvePatch()
        {
            Type[] signature = { typeof(MethodBase), typeof(HarmonyMethod),
                typeof(HarmonyMethod), typeof(HarmonyMethod), typeof(HarmonyMethod) };
            MethodInfo method = typeof(Harmony).GetMethod("Patch",
                BindingFlags.Public | BindingFlags.Instance, null, signature, null);
            if (method == null)
                throw new MissingMethodException("Unsupported Harmony Patch signature: " +
                    typeof(Harmony).Assembly.FullName);
            return method;
        }

        internal static void Patch(Harmony harmony, MethodBase original,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
        {
            if (harmony == null) throw new ArgumentNullException("harmony");
            try
            {
                PatchMethod.Invoke(harmony, new object[] {
                    original, prefix, postfix, transpiler, finalizer });
            }
            catch (TargetInvocationException error)
            {
                if (error.InnerException == null) throw;
                // VaM's Mono profile predates ExceptionDispatchInfo.
                // Preserve the actual exception type, not the reflection wrapper.
                throw error.InnerException;
            }
        }
    }
}
