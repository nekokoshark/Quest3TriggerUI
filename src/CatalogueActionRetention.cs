using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Preserve registered actions/UI bindings, but resolve their native toggle
    // targets by UID at invocation time instead of keeping the first component.
    internal static class CatalogueActionRetention
    {
        private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Clothes = typeof(DAZCharacterSelector).GetField("clothingItemToggleJSONs", Instance);
        private static readonly FieldInfo Hair = typeof(DAZCharacterSelector).GetField("hairItemToggleJSONs", Instance);
        private static readonly MethodInfo ClothesCallback = FindCallback("InitClothingItems", typeof(DAZClothingItem));
        private static readonly MethodInfo HairCallback = FindCallback("InitHairItems", typeof(DAZHairGroup));
        private static readonly FieldInfo Previous = typeof(MulticastDelegate).GetField("prev", Instance);
        private static Harmony _harmony;
        private static long _rewritten, _staleBefore;

        private static MethodInfo FindCallback(string init, Type itemType)
        {
            MethodInfo found = null;
            foreach (var instruction in PatchProcessor.GetOriginalInstructions(
                typeof(DAZCharacterSelector).GetMethod(init), (ILGenerator)null))
            {
                if (instruction.opcode != OpCodes.Ldftn) continue;
                var method = instruction.operand as MethodInfo;
                if (method == null || method.DeclaringType.DeclaringType != typeof(DAZCharacterSelector)) continue;
                var item = method.DeclaringType.GetField("dc", Instance);
                var owner = method.DeclaringType.GetField("<>4__this", Instance);
                if (item == null || item.FieldType != itemType || owner == null || owner.FieldType != typeof(DAZCharacterSelector)) continue;
                if (found != null) throw new InvalidOperationException("Ambiguous catalogue toggle: " + init);
                found = method;
            }
            if (found == null) throw new MissingMethodException("Catalogue toggle anchor missing: " + init);
            return found;
        }

        private sealed class UidToggle
        {
            private readonly DAZCharacterSelector _selector;
            private readonly string _uid;
            private readonly bool _hair;
            internal UidToggle(DAZCharacterSelector selector, string uid, bool hair)
            { _selector = selector; _uid = uid; _hair = hair; }
            internal void Invoke()
            {
                if (_selector == null) return;
                if (_hair)
                {
                    var item = _selector.GetHairItem(_uid);
                    if (item != null) _selector.ToggleHairItem(item);
                }
                else
                {
                    var item = _selector.GetClothingItem(_uid);
                    if (item != null) _selector.ToggleClothingItem(item);
                }
            }
        }

        internal static int Normalize(DAZCharacterSelector selector, bool hair)
        {
            var actions = (List<JSONStorableAction>)(hair ? Hair : Clothes).GetValue(selector);
            if (actions == null) return 0;
            var native = hair ? HairCallback : ClothesCallback;
            var owner = native.DeclaringType.GetField("<>4__this", Instance);
            var itemField = native.DeclaringType.GetField("dc", Instance);
            int replaced = 0;
            foreach (var action in actions)
            {
                if (action == null || action.actionCallback == null) continue;
                // A single already-normalized callback needs no new snapshot.
                if (action.actionCallback.Target is UidToggle &&
                    Previous != null && Previous.GetValue(action.actionCallback) == null) continue;
                var callbacks = DelegateSnapshot.GetInvocationList(action.actionCallback);
                bool changed = false;
                for (int i = 0; i < callbacks.Length; i++)
                {
                    var callback = callbacks[i];
                    if (!callback.Method.Equals(native) || callback.Target == null ||
                        !ReferenceEquals(owner.GetValue(callback.Target), selector)) continue;
                    var item = itemField.GetValue(callback.Target);
                    if (ReferenceEquals(item, null)) continue;
                    string uid = hair ? ((DAZHairGroup)item).uid : ((DAZClothingItem)item).uid;
                    if (uid == null || action.name != "toggle:" + uid) continue;
                    // Compare only an already-built dictionary; never initialize a catalogue here.
                    var mapField = typeof(DAZCharacterSelector).GetField(hair ? "_hairItemById" : "_clothingItemById", Instance);
                    var map = mapField.GetValue(selector) as System.Collections.IDictionary;
                    if (map != null && map.Contains(uid) && !ReferenceEquals(map[uid], item)) _staleBefore++;
                    callbacks[i] = new JSONStorableAction.ActionCallback(new UidToggle(selector, uid, hair).Invoke);
                    changed = true;
                    replaced++;
                }
                if (!changed) continue;
                Delegate combined = null;
                foreach (var callback in callbacks) combined = Delegate.Combine(combined, callback);
                action.actionCallback = (JSONStorableAction.ActionCallback)combined;
            }
            _rewritten += replaced;
            return replaced;
        }

        private static void AfterClothes(DAZCharacterSelector __instance) { Normalize(__instance, false); }
        private static void AfterHair(DAZCharacterSelector __instance) { Normalize(__instance, true); }
        internal static void Install()
        {
            if (_harmony != null) return;
            _harmony = new Harmony("quest3triggerui.catalogueactions." + typeof(CatalogueActionRetention).Namespace);
            try
            {
                _harmony.Patch(typeof(DAZCharacterSelector).GetMethod("InitClothingItems"), postfix: new HarmonyMethod(typeof(CatalogueActionRetention), "AfterClothes"));
                _harmony.Patch(typeof(DAZCharacterSelector).GetMethod("InitHairItems"), postfix: new HarmonyMethod(typeof(CatalogueActionRetention), "AfterHair"));
                int selectors = 0;
                var sc = SuperController.singleton;
                if (sc != null)
                {
                    foreach (var atom in sc.GetAtoms())
                    {
                        if (atom == null) continue;
                        var selector = atom.GetStorableByID("geometry") as DAZCharacterSelector;
                        if (selector == null) continue;
                        Normalize(selector, false);
                        Normalize(selector, true);
                        selectors++;
                    }
                }
                Log("installed initPostfixes=2 selectors=" + selectors + " rewritten=" + _rewritten +
                    " staleBefore=" + _staleBefore + " countsNotBytes=True callbacksNotInvoked=True catalogueNotRebuilt=True");
            }
            catch { Shutdown(); throw; }
        }
        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            // Existing normalized actions remain functional. Do not keep a
            // rollback list of obsolete callbacks that would retain old items.
            Log("shutdown rewritten=" + _rewritten + " existingUidActions=preserved");
            _rewritten = _staleBefore = 0;
        }
        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[catalogue-action-ret] " + message);
        }
    }
}
