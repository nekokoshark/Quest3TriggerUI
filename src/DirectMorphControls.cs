using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Five preselected library UIDs (two columns). No expression/fold heuristics or library scans in Tick.
    internal sealed class DirectMorphControls : IDisposable
    {
        internal sealed class Slot
        {
            internal Atom Target;
            internal DAZCharacterSelector Selector;
            internal DAZMorphBank Bank1, Bank2, Bank3;
            internal DAZMorph Morph;
            internal float Raw, Value, Min, Max;
            internal bool Enabled;
        }
        internal const int SlotCount = 5;
        internal const float RangeScale = 3f;
        private readonly Slot[] _slots = { new Slot(), new Slot(), new Slot(), new Slot(), new Slot() };
        private static DirectMorphControls _active;
        private Harmony _harmony;
        private bool _applying;
        internal const string FoldUid = "vecterror._MorphCollection_.26:/Custom/Atom/Person/Morphs/female/vecterror/Nose Cheek Flat SKB.vmi";
        internal const string CheekSmoothUid = "vecterror._MorphCollection_.26:/Custom/Atom/Person/Morphs/female/vecterror/Cheek Smoother.vmi";
        internal const string FoldLineUid = "vecterror._MorphCollection_.26:/Custom/Atom/Person/Morphs/female/vecterror/Nasolabial Folds.vmi";
        internal const string CheekCreaseUid = "vecterror._MorphCollection_.26:/Custom/Atom/Person/Morphs/female/vecterror/Mouth Side Crease.vmi";
        internal const string MouthCornerUid = "Mouth Corner Depth";
        internal static readonly string[] Uids = { FoldUid, CheekSmoothUid, FoldLineUid, CheekCreaseUid, MouthCornerUid };
        internal static readonly string[] Labels = { "鼻旁平整", "面颊柔化", "法令纹线", "颊部沟纹", "嘴角深度" };
        internal static readonly string[] SourceNames = { "Nose Cheek Flat SKB", "Cheek Smoother", "Nasolabial Folds", "Mouth Side Crease", "Mouth Corner Depth" };
        // Declared (unscaled) range per morph, captured before this plugin ever writes past it.
        private static readonly Dictionary<DAZMorph, Vector2> Declared = new Dictionary<DAZMorph, Vector2>();
        internal Slot Get(int index) {
            Slot slot = _slots[index];
            if (Valid(slot) && !slot.Enabled) slot.Value = Mathf.Clamp(slot.Morph.morphValue, slot.Min, slot.Max);
            return slot;
        }
        internal void Prepare(Atom target) {
            if (SuperController.singleton == null || SuperController.singleton.isLoading) target = null;
            for (int i = 0; i < _slots.Length; i++) {
                Slot slot = _slots[i];
                if (slot.Target == target && Valid(slot)) continue;
                Clear(i);
                if (target == null) continue;
                string message;
                Bind(i,target,Uids[i],out message);
            }
        }

        internal bool Bind(int index, Atom target, string uid, out string message)
        {
            var selector = target == null ? null : target.GetStorableByID("geometry") as DAZCharacterSelector;
            if (selector == null || selector.morphsControlUI == null)
            { message = "角色形变库尚未就绪。"; return false; }
            DAZMorph morph = selector.morphsControlUI.GetMorphByUid(uid);
            if (morph == null || morph.max <= morph.min)
            { message = "所选形变已卸载或没有可调范围。"; return false; }
            for (int i = 0; i < _slots.Length; i++)
                if (i != index && _slots[i].Morph == morph)
                { message = "该形变已绑定另一行，请选择其他形变。"; return false; }
            if (_harmony == null)
            {
                _harmony = new Harmony("Quest3TriggerUI.direct-morph." + GetType().Namespace);
                try
                {
                    _active = this;
                    _harmony.Patch(AccessTools.PropertySetter(typeof(JSONStorableFloat), "val"),
                        prefix: new HarmonyMethod(typeof(DirectMorphControls), "Filter"));
                }
                catch (Exception e)
                {
                    _harmony.UnpatchAll(_harmony.Id); _harmony = null; _active = null;
                    message = "形变控制初始化失败：" + e.Message; return false;
                }
            }
            Clear(index);
            Slot slot = _slots[index];
            slot.Target = target; slot.Selector = selector;
            slot.Bank1 = selector.morphBank1; slot.Bank2 = selector.morphBank2; slot.Bank3 = selector.morphBank3;
            Vector2 declared;
            if (!Declared.TryGetValue(morph, out declared)) {
                declared = new Vector2(morph.min, morph.max);
                Declared[morph] = declared;
            }
            slot.Morph = morph; slot.Raw = morph.morphValue;
            slot.Min = declared.x * RangeScale; slot.Max = declared.y * RangeScale;
            slot.Value = Mathf.Clamp(slot.Raw, slot.Min, slot.Max);
            message = "已绑定 " + target.uid + "：" + morph.resolvedDisplayName + "（" + uid + "）";
            return true;
        }

        internal bool Set(int index, float value, out string message)
        {
            Slot slot = _slots[index];
            if (!Valid(slot)) { Clear(index); message = "角色形变尚未就绪，请重新打开键盘。"; return false; }
            if (!slot.Enabled) { slot.Raw = slot.Morph.morphValue; slot.Enabled = true; }
            slot.Value = Mathf.Clamp(value, slot.Min, slot.Max);
            Write(slot, slot.Value);
            message = slot.Morph.resolvedDisplayName + " = " + slot.Value.ToString("F3");
            return true;
        }

        private static void Filter(JSONStorableFloat __instance, ref float __0)
        {
            var owner = _active;
            if (owner == null || owner._applying || SuperController.singleton == null ||
                SuperController.singleton.isLoading) return;
            for (int i = 0; i < owner._slots.Length; i++)
            {
                Slot slot = owner._slots[i];
                if (slot.Enabled && slot.Morph != null && slot.Morph.jsonFloat == __instance && owner.Valid(slot))
                { slot.Raw = __0; __0 = slot.Value; return; }
            }
        }

        private bool Valid(Slot slot)
        {
            return slot.Target != null && slot.Selector != null && slot.Morph != null &&
                SuperController.singleton != null && !SuperController.singleton.isLoading &&
                slot.Selector.morphBank1 == slot.Bank1 && slot.Selector.morphBank2 == slot.Bank2 &&
                slot.Selector.morphBank3 == slot.Bank3;
        }

        private void Write(Slot slot, float value)
        {
            _applying = true;
            try { slot.Morph.morphValue = value; }
            finally { _applying = false; }
        }

        internal void Clear(int index)
        {
            Slot slot = _slots[index];
            if (slot.Enabled && slot.Target != null && slot.Morph != null) Write(slot, slot.Raw);
            _slots[index] = new Slot();
        }

        internal void Tick()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                if (slot.Morph == null) continue;
                if (!Valid(slot)) { Clear(i); continue; }
                if (!slot.Enabled) continue;
                // Cover direct/formula writes that bypass JSON parameters as well.
                float current = slot.Morph.morphValue;
                if (current != slot.Value) { slot.Raw = current; Write(slot, slot.Value); }
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _slots.Length; i++) Clear(i);
            if (_active == this) _active = null;
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
        }
    }
}
