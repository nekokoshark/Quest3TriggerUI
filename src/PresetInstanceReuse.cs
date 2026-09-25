using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Scope native neverUnloadOnDisable ONLY across ResetHair/ResetClothing.
    // Keep OnDisable/OnEnable, registration, PostLoadJSONRestore and physics
    // parameter restoration intact; never skip SetActive or storable restore.
    internal static class PresetInstanceReuse
    {
        internal static ConfigEntry<bool> Enabled;
        private static Harmony _harmony;
        private static bool _tried;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo InstanceField = typeof(JSONStorableDynamic).GetField("instance", All);
        private static readonly Dictionary<DAZCharacterSelector, Context> Contexts =
            new Dictionary<DAZCharacterSelector, Context>();

        private sealed class Context
        {
            internal JSONClass json;
            internal DAZCharacter character;
            internal Context previous;
            internal int reused;
        }

        internal static void Install()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                if (InstanceField == null) throw new MissingFieldException("JSONStorableDynamic.instance");
                _harmony = new Harmony("Quest3TriggerUI.preset-instance-reuse");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(typeof(DAZCharacterSelector).GetMethod("RestoreFromJSON", All,
                    null, new[] { typeof(JSONClass), typeof(bool), typeof(bool), typeof(JSONArray), typeof(bool) }, null),
                    prefix: Patch("BeforeRestore"), finalizer: Patch("AfterRestore"));
                foreach (string name in new[] { "ResetClothing", "ResetHair" })
                    _harmony.Patch(typeof(DAZCharacterSelector).GetMethod(name, All),
                        prefix: Patch("BeforeReset"), finalizer: Patch("AfterReset"));
                Log("installed; reuse same-base ready instances through native disable/enable lifecycle");
            }
            catch (Exception e)
            {
                Shutdown(); _tried = true;
                Log("not installed: " + e.Message);
            }
        }

        private static HarmonyMethod Patch(string name)
        {
            return new HarmonyMethod(typeof(PresetInstanceReuse).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
        }

        private static void BeforeRestore(DAZCharacterSelector __instance, JSONClass __0,
            bool __2, out Context __state)
        {
            Context previous;
            Contexts.TryGetValue(__instance, out previous);
            bool eligible = (Enabled == null || Enabled.Value) && __2 && __0 != null && !__instance.mergeRestore &&
                (__instance.containingAtom == null || !__instance.containingAtom.isPreparingToPutBackInPool);
            // An ineligible nested restore must mask, not inherit, the outer scope.
            __state = new Context { json = __0, character = eligible ? __instance.selectedCharacter : null, previous = previous };
            Contexts[__instance] = __state;
        }

        private static Exception AfterRestore(DAZCharacterSelector __instance, Context __state, Exception __exception)
        {
            if (__state != null)
            {
                if (__state.previous == null) Contexts.Remove(__instance);
                else Contexts[__instance] = __state.previous;
                if (__state.reused > 0) Log("preserved instances=" + __state.reused + "; native parameter restore retained");
            }
            return __exception;
        }

        private static void BeforeReset(DAZCharacterSelector __instance, bool __0,
            MethodBase __originalMethod, out List<DAZDynamicItem> __state)
        {
            __state = null;
            Context context;
            if (!__0 || !Contexts.TryGetValue(__instance, out context) || context.character == null ||
                !ReferenceEquals(context.character, __instance.selectedCharacter)) return;
            try
            {
                bool hair = __originalMethod.Name == "ResetHair";
                // Cast rather than LazyCreator.AsArray: never create a missing
                // array in the caller's JSON while inspecting eligibility.
                JSONArray array = context.json[hair ? "hair" : "clothing"] as JSONArray;
                if (array == null) return; // Preserve native legacy/missing/default semantics.
                var choices = new Dictionary<DAZDynamicItem, bool>();
                for (int i = 0; i < array.Count; i++)
                {
                    JSONClass entry = array[i] as JSONClass;
                    if (entry == null) return;
                    string id = entry["id"];
                    if (hair || id != null) id = MVR.FileManagement.FileManager.NormalizeID(id);
                    else id = entry["name"];
                    DAZDynamicItem item = hair ? (DAZDynamicItem)__instance.GetHairItem(id) : __instance.GetClothingItem(id);
                    string fallback = entry["internalId"];
                    if (item == null && fallback != null)
                        item = hair ? (DAZDynamicItem)__instance.GetHairItem(fallback) : __instance.GetClothingItem(fallback);
                    if (item == null) continue;
                    // Duplicate IDs may toggle mid-restore; leave those to native.
                    bool duplicate = choices.ContainsKey(item);
                    choices[item] = !duplicate && entry["enabled"].AsBool;
                }
                foreach (var choice in choices)
                {
                    DAZDynamicItem item = choice.Key;
                    if (!choice.Value || !item.active || !item.ready || item.locked ||
                        !item.enabled || !item.gameObject.activeInHierarchy ||
                        item.neverUnloadOnDisable || !item.unloadOnDisable ||
                        InstanceField.GetValue(item) as Transform == null) continue;
                    if (__state == null) __state = new List<DAZDynamicItem>();
                    __state.Add(item);
                    item.neverUnloadOnDisable = true;
                }
                if (__state != null) context.reused += __state.Count;
            }
            catch (Exception e)
            {
                RestoreFlags(__state); __state = null;
                Log("native reset retained: " + e.Message);
            }
        }

        private static Exception AfterReset(List<DAZDynamicItem> __state, Exception __exception)
        {
            RestoreFlags(__state);
            return __exception;
        }

        private static void RestoreFlags(List<DAZDynamicItem> items)
        {
            if (items == null) return;
            foreach (var item in items) if (item != null) item.neverUnloadOnDisable = false;
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null; _tried = false;
            Contexts.Clear();
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[preset-reuse] " + message);
        }
    }
}
