using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal static class VrTextInputBridge
    {
        private static InputField _target;
        private static int _anchor, _focus;
        private static bool _voiceActive;
        private static readonly FieldInfo NativeKeyboard = Field("currentKeyboardTransform");
        private static readonly FieldInfo DefaultKeyboard = Field("keyboardTransform");
        internal static bool Active { get { return _target != null; } }
        internal static bool ImeEnabled { get { return PinyinEngine.Enabled; } }

        private static FieldInfo Field(string name)
        {
            return typeof(LookInputModule).GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        internal static void Select(InputField target)
        {
            if (!Quest3TriggerUIPlugin.InputRuntimeActive || target == null ||
                target.readOnly || !target.IsInteractable()) return;
            _target = target;
            CaptureSelection(target);
            PinyinEngine.Reset();
            PinyinEngine.EnsureLoaded();
            SuppressNativeKeyboard();
            if (Quest3TriggerUIPlugin.Instance != null)
                Quest3TriggerUIPlugin.Instance.ShowTextKeyboard();
        }

        internal static void Deselect(InputField target)
        {
            if (target == _target) CaptureSelection(target);
        }

        internal static void Clear()
        {
            if (_voiceActive) WindowsKeyboard.SendGlobal(0xA5, true, true);
            _voiceActive = false;
            PinyinEngine.Reset();
            DoubaoImeBridge.Reset();
            DoubaoImeBridge.ReleaseAll();
            WindowsImeBridge.Detach();
            _target = null;
        }

        internal static void Shutdown()
        {
            Clear();
            WindowsImeBridge.SetEnabled(false);
        }

        internal static void Tick()
        {
            if (_target == null || !_target.gameObject.activeInHierarchy) { Clear(); return; }
            if (_target.isFocused) CaptureSelection(_target);
            if (Quest3TriggerUIPlugin.KeyboardVisible) SuppressNativeKeyboard();
            PinyinEngine.Tick();
            DoubaoImeBridge.Tick();
        }

        // 中/英 toggles Chinese input. In bridge mode we ask the desktop IME
        // to toggle itself (a clean Shift tap); offline it flips our engine.
        internal static void ToggleIme()
        {
            if (DoubaoImeBridge.Active)
            {
                WindowsKeyboard.SendGlobal(0xA0, false, false);
                WindowsKeyboard.SendGlobal(0xA0, true, false);
                return;
            }
            PinyinEngine.Toggle();
        }

        // Desktop-style Shift tap toggles 中/英. Only a clean tap counts —
        // Shift held as a modifier keeps its normal role.
        private static bool _shiftTapCandidate;

        internal static void NoteShiftDown()
        {
            _shiftTapCandidate = ValidTarget();
        }

        internal static void NoteOtherKey()
        {
            _shiftTapCandidate = false;
        }

        internal static void NoteShiftUp()
        {
            if (!_shiftTapCandidate)
                return;
            _shiftTapCandidate = false;
            // In bridge mode the Shift tap already reached the desktop IME
            // via keybd_event — it toggles its own 中/英 natively.
            if (DoubaoImeBridge.Active) return;
            PinyinEngine.Toggle();
        }

        internal static void SelectImeCandidate(int pageIndex, int absoluteIndex)
        {
            if (!ValidTarget() || !PinyinEngine.Enabled) return;
            string commit;
            PinyinEngine.CommitAt(absoluteIndex, out commit);
            if (!string.IsNullOrEmpty(commit)) Insert(commit);
        }

        internal static void ImePage(bool next)
        {
            if (!PinyinEngine.Enabled) return;
            PinyinEngine.Page(next);
        }

        internal static void SuppressNativeKeyboard()
        {
            LookInputModule module = LookInputModule.singleton;
            if (module == null) return;
            HideTransform(NativeKeyboard, module);
            HideTransform(DefaultKeyboard, module);
        }

        private static void HideTransform(FieldInfo field, LookInputModule module)
        {
            if (field == null) return;
            Transform keyboard = field.GetValue(module) as Transform;
            if (keyboard != null && keyboard.gameObject.activeSelf)
                keyboard.gameObject.SetActive(false);
        }

        internal static bool HandleVoiceKey(ushort key, bool keyUp)
        {
            if (key != 0xA5 || (_target == null && !_voiceActive)) return false;
            if (keyUp)
            {
                if (_voiceActive) WindowsKeyboard.SendGlobal(key, true, true);
                _voiceActive = false;
                if (_target != null) CaptureSelection(_target);
            }
            else if (!_voiceActive)
            {
                // Voice IME commits replace the current selection. Collapse it
                // at the end first so dictated text always appends and never
                // destroys text already present in the field.
                _anchor = _focus = _target.text == null ? 0 : _target.text.Length;
                RestoreFocus();
                _voiceActive = WindowsKeyboard.SendGlobal(key, false, true);
            }
            return true;
        }

        internal static bool Send(ushort key, bool extended, Dictionary<ushort, bool> held)
        {
            if (!ValidTarget()) return false;
            if (key == 0x14 || (key >= 0xA0 && key <= 0xA5) ||
                key == 0x5B || key == 0x5C) return false;
            NormalizeSelection();
            bool shift = held.ContainsKey(0xA0) || held.ContainsKey(0xA1);
            bool ctrl = held.ContainsKey(0xA2) || held.ContainsKey(0xA3);
            if (ctrl && HandleControl(key)) return true;
            if (PinyinEngine.Enabled)
            {
                string commit;
                switch (PinyinEngine.HandleKey(key, shift, out commit))
                {
                    case PinyinEngine.KeyResult.CommitText:
                        Insert(commit);
                        return true;
                    case PinyinEngine.KeyResult.Consumed:
                        return true;
                }
            }
            if (key == 0x1B) { Clear(); return true; }
            if (key == 0x08) { Backspace(); return true; }
            if (key == 0x2E) { Delete(); return true; }
            if (key == 0x25) { MoveCaret(-1, shift); return true; }
            if (key == 0x27) { MoveCaret(1, shift); return true; }
            if (key == 0x24) { SetCaret(0, shift); return true; }
            if (key == 0x23) { SetCaret(_target.text.Length, shift); return true; }
            if (key == 0x0D && _target.lineType != InputField.LineType.MultiLineNewline)
            {
                InputFieldAction action = _target.GetComponent<InputFieldAction>();
                if (action != null) action.Submit(); else _target.onEndEdit.Invoke(_target.text);
                UpdateVisual();
                return true;
            }
            char character = Character(key, shift, held.ContainsKey(0x14));
            if (character != '\0') Insert(character.ToString());
            return true;
        }

        private static bool HandleControl(ushort key)
        {
            int start = Math.Min(_anchor, _focus), end = Math.Max(_anchor, _focus);
            if (key == 0x41) { _anchor = 0; _focus = _target.text.Length; UpdateVisual(); return true; }
            if (key == 0x43) { if (end > start) GUIUtility.systemCopyBuffer = _target.text.Substring(start, end-start); return true; }
            if (key == 0x58)
            {
                if (end > start) { GUIUtility.systemCopyBuffer = _target.text.Substring(start,end-start); ReplaceSelection(string.Empty); }
                return true;
            }
            if (key == 0x56) { Insert(GUIUtility.systemCopyBuffer ?? string.Empty); return true; }
            return false;
        }

        private static void Backspace()
        {
            if (_anchor != _focus) { ReplaceSelection(string.Empty); return; }
            if (_focus <= 0) return;
            int start = _focus - 1;
            if (start > 0 && char.IsLowSurrogate(_target.text[start]) &&
                char.IsHighSurrogate(_target.text[start-1])) start--;
            _anchor = start; ReplaceSelection(string.Empty);
        }

        private static void Delete()
        {
            if (_anchor != _focus) { ReplaceSelection(string.Empty); return; }
            if (_focus >= _target.text.Length) return;
            int end = _focus + 1;
            if (end < _target.text.Length && char.IsHighSurrogate(_target.text[_focus]) &&
                char.IsLowSurrogate(_target.text[end])) end++;
            _anchor = _focus; _focus = end; ReplaceSelection(string.Empty);
        }

        private static void Insert(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            int available = _target.characterLimit <= 0 ? int.MaxValue :
                _target.characterLimit - (_target.text.Length - Math.Abs(_focus-_anchor));
            if (available <= 0) return;
            if (value.Length > available) value = value.Substring(0, available);
            ReplaceSelection(value);
        }

        private static void ReplaceSelection(string value)
        {
            int start=Math.Min(_anchor,_focus), end=Math.Max(_anchor,_focus);
            _target.text=_target.text.Substring(0,start)+value+_target.text.Substring(end);
            _anchor=_focus=start+value.Length;
            UpdateVisual();
        }

        private static void MoveCaret(int delta, bool selecting)
        {
            int next=Mathf.Clamp(_focus+delta,0,_target.text.Length);
            if(!selecting)_anchor=next;
            _focus=next;UpdateVisual();
        }

        private static void SetCaret(int position, bool selecting)
        {
            position=Mathf.Clamp(position,0,_target.text.Length);
            if(!selecting)_anchor=position;
            _focus=position;UpdateVisual();
        }

        private static bool ValidTarget()
        {
            return _target != null && _target.gameObject.activeInHierarchy && !_target.readOnly;
        }

        private static void NormalizeSelection()
        {
            int length=_target.text == null ? 0 : _target.text.Length;
            _anchor=Mathf.Clamp(_anchor,0,length);_focus=Mathf.Clamp(_focus,0,length);
        }

        private static void CaptureSelection(InputField field)
        {
            _anchor=field.selectionAnchorPosition;_focus=field.selectionFocusPosition;
            NormalizeSelection();
        }

        private static void RestoreFocus()
        {
            if(!ValidTarget())return;
            if(EventSystem.current!=null)EventSystem.current.SetSelectedGameObject(_target.gameObject);
            _target.ActivateInputField();
            _target.selectionAnchorPosition=_anchor;_target.selectionFocusPosition=_focus;
            SuppressNativeKeyboard();
        }

        private static void UpdateVisual()
        {
            _target.selectionAnchorPosition=_anchor;_target.selectionFocusPosition=_focus;
            _target.ForceLabelUpdate();
        }

        internal static char Character(ushort key, bool shift, bool caps)
        {
            if(key>=0x41&&key<=0x5A)return(char)(key+((shift^caps)?0:32));
            if(key>=0x30&&key<=0x39)return shift?")!@#$%^&*("[key-0x30]:(char)key;
            if(key>=0x60&&key<=0x69)return(char)('0'+key-0x60);
            switch(key){case 0x20:return ' ';case 0x0D:return '\n';case 0xBA:return shift?':':';';case 0xBB:return shift?'+':'=';case 0xBC:return shift?'<':',';case 0xBD:return shift?'_':'-';case 0xBE:return shift?'>':'.';case 0xBF:return shift?'?':'/';case 0xC0:return shift?'~':'`';case 0xDB:return shift?'{':'[';case 0xDC:return shift?'|':'\\';case 0xDD:return shift?'}':']';case 0xDE:return shift?'"':'\'';case 0x6A:return '*';case 0x6B:return '+';case 0x6D:return '-';case 0x6E:return '.';case 0x6F:return '/';default:return '\0';}
        }
    }

    [HarmonyPatch(typeof(InputField), "OnPointerClick")]
    internal static class SelectVrTextInput
    {
        [HarmonyPostfix] private static void Postfix(InputField __instance){VrTextInputBridge.Select(__instance);}
    }

    [HarmonyPatch(typeof(InputField), "OnDeselect")]
    internal static class PreserveVrTextSelection
    {
        [HarmonyPrefix] private static void Prefix(InputField __instance){VrTextInputBridge.Deselect(__instance);}
    }

    [HarmonyPatch(typeof(LookInputModule), "Select")]
    internal static class SelectVrTextInputFromLook
    {
        [HarmonyPostfix] private static void Postfix(GameObject __0)
        {
            if(__0==null)return;
            InputField field=__0.GetComponent<InputField>();
            if(field!=null)VrTextInputBridge.Select(field);
            VrTextInputBridge.SuppressNativeKeyboard();
        }
    }
}
