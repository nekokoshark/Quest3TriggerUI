using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace Quest3TriggerUI
{
    // A synchronous preset scope, NOT a cache of previously loaded presets.
    // Keep identical, live items registered. All target parameters still pass
    // through native PostLoadJSONRestore, including missing/default parameters.
    internal static class PresetDeltaApply
    {
        internal static ConfigEntry<bool> Enabled;
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Instance = typeof(JSONStorableDynamic).GetField("instance", All);
        private static readonly FieldInfo Registered = typeof(JSONStorableDynamic).GetField("isRegistered", All);
        private static readonly Dictionary<DAZCharacterSelector, Scope> Scopes = new Dictionary<DAZCharacterSelector, Scope>();
        private static Harmony _harmony;

        private sealed class Scope
        {
            internal Scope previous;
            internal JSONClass json;
            internal DAZCharacter character;
            internal HashSet<DAZDynamicItem> resetting;
            internal readonly HashSet<DAZDynamicItem> retained = new HashSet<DAZDynamicItem>();
            internal int restored;
        }

        private sealed class ResetScope
        {
            internal Scope owner;
            internal HashSet<DAZDynamicItem> previous;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                if (Instance == null || Registered == null) throw new MissingFieldException("dynamic instance/registration");
                _harmony = new Harmony("Quest3TriggerUI.preset-delta");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(typeof(DAZCharacterSelector).GetMethod("RestoreFromJSON", All),
                    prefix: Hook("Begin"), finalizer: Hook("End"));
                foreach (string name in new[] { "ResetHair", "ResetClothing" })
                    _harmony.Patch(typeof(DAZCharacterSelector).GetMethod(name, All),
                        prefix: Hook("BeginReset"), finalizer: Hook("EndReset"), transpiler: Hook("RouteReset"));
                foreach (Type type in new[] { typeof(DAZHairGroup), typeof(DAZClothingItem) })
                    _harmony.Patch(typeof(DAZCharacterSelector).GetMethod(type == typeof(DAZHairGroup) ?
                        "SetActiveHairItem" : "SetActiveClothingItem", All, null,
                        new[] { type, typeof(bool), typeof(bool) }, null), postfix: Hook("AfterActivate"));
                Log("installed; same-base live hair/clothing delta, native parameter restore retained");
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e.Message); }
        }

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(PresetDeltaApply).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void Begin(DAZCharacterSelector __instance, JSONClass __0, bool __2, out Scope __state)
        {
            Scope previous;
            Scopes.TryGetValue(__instance, out previous);
            bool eligible = (Enabled == null || Enabled.Value) && __2 && __0 != null &&
                __instance.isPresetRestore && !__instance.mergeRestore && __instance.containingAtom != null &&
                !__instance.containingAtom.isPreparingToPutBackInPool;
            __state = new Scope { previous = previous, json = __0,
                character = eligible ? __instance.selectedCharacter : null };
            Scopes[__instance] = __state; // Ineligible nested restores mask outer scopes.
        }

        private static Exception End(DAZCharacterSelector __instance, Scope __state, Exception __exception)
        {
            if (__state != null)
            {
                if (__state.previous == null) Scopes.Remove(__instance);
                else Scopes[__instance] = __state.previous;
                if (__state.retained.Count > 0) Log("atom=" + __instance.containingAtom.uid +
                    " retained=" + __state.retained.Count + " restored=" + __state.restored +
                    " completed=" + (__exception == null));
            }
            return __exception;
        }

        private static void BeginReset(DAZCharacterSelector __instance, bool __0, MethodBase __originalMethod,
            out ResetScope __state)
        {
            __state = null;
            Scope scope;
            if (!Scopes.TryGetValue(__instance, out scope)) return;
            __state = new ResetScope { owner = scope, previous = scope.resetting };
            scope.resetting = null;
            if (!__0 || scope.character == null || !scope.character.ready ||
                !ReferenceEquals(scope.character, __instance.selectedCharacter)) return;
            try
            {
                bool hair = __originalMethod.Name == "ResetHair";
                var array = scope.json[hair ? "hair" : "clothing"] as JSONArray;
                if (array == null) return;
                var unique = new HashSet<DAZDynamicItem>();
                var duplicate = new HashSet<DAZDynamicItem>();
                var candidates = new HashSet<DAZDynamicItem>();
                for (int i = 0; i < array.Count; i++)
                {
                    var entry = array[i] as JSONClass;
                    if (entry == null) return;
                    string id = entry["id"];
                    if (hair || id != null) id = MVR.FileManagement.FileManager.NormalizeID(id);
                    else id = entry["name"];
                    DAZDynamicItem item = hair ? (DAZDynamicItem)__instance.GetHairItem(id) : __instance.GetClothingItem(id);
                    string fallback = entry["internalId"];
                    if (item == null && fallback != null)
                        item = hair ? (DAZDynamicItem)__instance.GetHairItem(fallback) : __instance.GetClothingItem(fallback);
                    if (item == null) continue;
                    if (!unique.Add(item)) duplicate.Add(item);
                    if (entry["enabled"].AsBool && CanRetain(item)) candidates.Add(item);
                }
                candidates.ExceptWith(duplicate);
                scope.resetting = candidates;
            }
            catch (Exception e) { Log("native reset retained: " + e.Message); }
        }

        private static bool CanRetain(DAZDynamicItem item)
        {
            if (!item.active || !item.ready || item.locked || !item.enabled ||
                !item.gameObject.activeInHierarchy || !(bool)Registered.GetValue(item) ||
                Instance.GetValue(item) as Transform == null) return false;
            var clothing = item as DAZClothingItem;
            // Exclusive garments and controller-driving garments need their full
            // disable/enable side effects. Do not reorder those transitions.
            return clothing == null || ((int)clothing.exclusiveRegion == 0 &&
                clothing.driveXAngleTargetController1 == null && clothing.driveXAngleTargetController2 == null &&
                clothing.drive2XAngleTargetController1 == null && clothing.drive2XAngleTargetController2 == null);
        }

        private static Exception EndReset(ResetScope __state, Exception __exception)
        {
            if (__state != null) __state.owner.resetting = __state.previous;
            return __exception;
        }

        private static IEnumerable<CodeInstruction> RouteReset(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                var method = instruction.operand as MethodInfo;
                if (method != null && method.DeclaringType == typeof(DAZCharacterSelector) &&
                    (method.Name == "SetActiveClothingItem" || method.Name == "SetActiveHairItem"))
                {
                    Type itemType = method.GetParameters()[0].ParameterType;
                    if (itemType != typeof(DAZClothingItem) && itemType != typeof(DAZHairGroup))
                        throw new InvalidOperationException("Unexpected native reset overload");
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = typeof(PresetDeltaApply).GetMethod(itemType == typeof(DAZHairGroup) ?
                        "ResetHairItem" : "ResetClothingItem", BindingFlags.Static | BindingFlags.NonPublic);
                    replaced++;
                }
                yield return instruction; // Preserve labels and exception blocks.
            }
            if (replaced != 4) throw new InvalidOperationException("Native reset anchors changed: " + replaced);
        }

        private static bool Keep(DAZCharacterSelector owner, DAZDynamicItem item, bool active)
        {
            Scope scope;
            if (active || item == null || !item.active || !item.ready || !Scopes.TryGetValue(owner, out scope) || scope.resetting == null ||
                !scope.resetting.Contains(item)) return false;
            scope.retained.Add(item);
            return true;
        }

        private static void ResetClothingItem(DAZCharacterSelector owner, DAZClothingItem item, bool active, bool restore)
        {
            if (!Keep(owner, item, active)) owner.SetActiveClothingItem(item, active, restore);
        }

        private static void ResetHairItem(DAZCharacterSelector owner, DAZHairGroup item, bool active, bool restore)
        {
            if (!Keep(owner, item, active)) owner.SetActiveHairItem(item, active, restore);
        }

        private static void AfterActivate(DAZCharacterSelector __instance, DAZDynamicItem __0, bool __1, bool __2)
        {
            Scope scope;
            if (!__1 || !__2 || __0 == null || !__0.active || !__0.ready ||
                !__0.needsPostLoadJSONRestore || !Scopes.TryGetValue(__instance, out scope) ||
                !scope.retained.Contains(__0)) return;
            // SetActive(true) on an already active GameObject does not run
            // OnEnable. Retain its required restore + physics-reset completion.
            __0.PostLoadJSONRestore();
            __0.ResetPhysics();
            scope.restored++;
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            Scopes.Clear();
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[preset-delta] " + message);
        }
    }
}
