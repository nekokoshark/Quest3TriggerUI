using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Native cleanup destroys resources but leaves its ownership list rooted.
    // Retire only that list after successful play-mode cleanup, not owner data.
    internal static class AllocatedObjectRetirement
    {
        private static Harmony _harmony;
        private static long _slots;

        private static void BeforeDestroy(out bool __state)
        {
            __state = Application.isPlaying;
        }

        private static void AfterDestroy(bool __state,
            List<UnityEngine.Object> ___allocatedObjects, MethodBase __originalMethod)
        {
            if (!__state || !Application.isPlaying || ___allocatedObjects == null) return;
            int count = ___allocatedObjects.Count;
            if (count == 0) return;
            // List identity and capacity are retained for subclasses/aliases.
            // No extra Destroy, and no clearing if native cleanup threw.
            ___allocatedObjects.Clear();
            _slots += count;
            Log("retired type=" + __originalMethod.DeclaringType.Name + " referenceSlots=" + count +
                " totalReferenceSlots=" + _slots + " nativeDestroy=unchanged ownerFields=preserved countsNotBytes=True");
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            var types = new[] { typeof(MVR.ObjectAllocator), typeof(DAZSkinV2),
                typeof(DAZSkinWrap), typeof(CubicBezierCurve) };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var methods = new List<MethodInfo>();
            foreach (var type in types)
            {
                var field = type.GetField("allocatedObjects", flags);
                var method = type.GetMethod("DestroyAllocatedObjects", flags);
                if (field == null || field.FieldType != typeof(List<UnityEngine.Object>) ||
                    method == null || method.ReturnType != typeof(void) || method.GetParameters().Length != 0)
                    throw new MissingMemberException(type.Name + ".DestroyAllocatedObjects ownership changed");
                methods.Add(method);
            }
            _harmony = new Harmony("quest3triggerui.allocatedobjects." + typeof(AllocatedObjectRetirement).Namespace);
            try
            {
                foreach (var method in methods)
                    _harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(AllocatedObjectRetirement), "BeforeDestroy"),
                        postfix: new HarmonyMethod(typeof(AllocatedObjectRetirement), "AfterDestroy"));
                Log("installed cleanupMethods=4 activeResources=preserved noExtraDestroy=True gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            Log("shutdown referenceSlots=" + _slots + " countsNotBytes=True");
            _slots = 0;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[allocated-object-ret] " + message);
        }
    }
}
