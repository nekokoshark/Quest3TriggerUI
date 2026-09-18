using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Both hands use EventSystem.RaycastAll before LookInputModule picks its
    // first hit. Preserve the real raycast data; only promote a keyboard hit.
    [HarmonyPatch(typeof(EventSystem), "RaycastAll")]
    internal static class KeyboardRaycastPriority
    {
        internal static Canvas Canvas;

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(EventSystem __instance, List<RaycastResult> __1)
        {
            if (!Quest3TriggerUIPlugin.InputRuntimeActive || Canvas == null ||
                !Canvas.gameObject.activeInHierarchy || !(__instance.currentInputModule is LookInputModule))
                return;
            Transform root = Canvas.transform;
            for (int i = 0; i < __1.Count; i++)
            {
                GameObject hit = __1[i].gameObject;
                if (hit == null || !hit.transform.IsChildOf(root)) continue;
                Selectable control = hit.GetComponentInParent<Selectable>();
                if (control == null || !control.IsActive() || !control.IsInteractable()) continue;
                // Do not reroute down/up or drag: native pointer capture stays intact.
                if (i > 0)
                {
                    RaycastResult result = __1[i];
                    __1.RemoveAt(i);
                    __1.Insert(0, result);
                }
                return;
            }
        }
    }
}
