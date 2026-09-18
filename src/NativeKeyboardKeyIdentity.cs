using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Weelco.VRKeyboard;

namespace Quest3TriggerUI
{
    // Existing keyboards may have been initialized before this hot update.
    [HarmonyPatch(typeof(VRKeyboardFull), "HandleClick")]
    internal static class ExistingNativeKeyboardCaseKey
    {
        [HarmonyPrefix]
        private static void Prefix(ref string __0)
        {
            __0 = NativeKeyboardKeyIdentity.Normalize(__0);
        }
    }
    public sealed class NativeKeyboardKeyIdentity : MonoBehaviour
    {
        internal string Key;
        internal static string Read(Text label)
        {
            NativeKeyboardKeyIdentity identity = label.GetComponentInParent<NativeKeyboardKeyIdentity>();
            return identity != null && identity.Key != null ? identity.Key : label.text;
        }

        internal static string Normalize(string key)
        {
            if (key == "上") return "UP";
            if (key == "低" || key == "下") return "LOW";
            return key;
        }
    }

    [HarmonyPatch(typeof(VRKeyboardButton), "SetKeyText")]
    internal static class CaptureNativeKeyboardKey
    {
        [HarmonyPrefix]
        private static void Prefix(VRKeyboardButton __instance, string __0)
        {
            NativeKeyboardKeyIdentity identity = __instance.GetComponent<NativeKeyboardKeyIdentity>();
            if (identity == null) identity = __instance.gameObject.AddComponent<NativeKeyboardKeyIdentity>();
            identity.Key = __0;
        }
    }

    [HarmonyPatch(typeof(VRKeyboardButton), "HandleClick")]
    internal static class DispatchNativeKeyboardKey
    {
        private static readonly FieldInfo LabelField =
            typeof(VRKeyboardButton).GetField(
                "label", BindingFlags.Instance | BindingFlags.NonPublic);

        [HarmonyPrefix]
        private static bool Prefix(VRKeyboardButton __instance)
        {
            if (LabelField == null)
                return true;
            Text label = LabelField.GetValue(__instance) as Text;
            if (label == null)
                return true;

            string key = NativeKeyboardKeyIdentity.Normalize(
                NativeKeyboardKeyIdentity.Read(label));
            if (__instance.OnVRKeyboardBtnClick != null)
                __instance.OnVRKeyboardBtnClick(key);
            return false;
        }
    }
}

