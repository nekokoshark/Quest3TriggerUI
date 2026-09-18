using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class VirtualKeyboardState
    {
        private sealed class KeyState
        {
            internal bool Held;
            internal int DownFrame = -1;
            internal int UpFrame = -1;
        }

        private static readonly Dictionary<KeyCode, KeyState> Keys =
            new Dictionary<KeyCode, KeyState>();

        internal static void KeyDown(ushort virtualKey, bool extended)
        {
            KeyCode key = ToKeyCode(virtualKey, extended);
            if (key == KeyCode.None)
                return;

            KeyState state = GetOrCreate(key);
            if (state.Held)
                return;

            state.Held = true;
            state.DownFrame = Time.frameCount + 1;
        }

        internal static void KeyUp(ushort virtualKey, bool extended)
        {
            KeyCode key = ToKeyCode(virtualKey, extended);
            if (key == KeyCode.None)
                return;

            KeyState state = GetOrCreate(key);
            if (!state.Held)
                return;

            state.Held = false;
            state.UpFrame = Time.frameCount + 1;
        }

        internal static bool IsHeld(KeyCode key)
        {
            KeyState state;
            return Keys.TryGetValue(key, out state) && state.Held;
        }

        internal static bool IsDown(KeyCode key)
        {
            KeyState state;
            return Keys.TryGetValue(key, out state) && state.DownFrame == Time.frameCount;
        }

        internal static bool IsUp(KeyCode key)
        {
            KeyState state;
            return Keys.TryGetValue(key, out state) && state.UpFrame == Time.frameCount;
        }

        internal static void Clear()
        {
            Keys.Clear();
        }

        private static KeyState GetOrCreate(KeyCode key)
        {
            KeyState state;
            if (!Keys.TryGetValue(key, out state))
            {
                state = new KeyState();
                Keys.Add(key, state);
            }
            return state;
        }

        internal static KeyCode ToKeyCode(ushort virtualKey, bool extended)
        {
            if (virtualKey >= 0x30 && virtualKey <= 0x39)
                return (KeyCode)((int)KeyCode.Alpha0 + virtualKey - 0x30);
            if (virtualKey >= 0x41 && virtualKey <= 0x5A)
                return (KeyCode)((int)KeyCode.A + virtualKey - 0x41);
            if (virtualKey >= 0x60 && virtualKey <= 0x69)
                return (KeyCode)((int)KeyCode.Keypad0 + virtualKey - 0x60);
            if (virtualKey >= 0x70 && virtualKey <= 0x7B)
                return (KeyCode)((int)KeyCode.F1 + virtualKey - 0x70);

            switch (virtualKey)
            {
                case 0x08: return KeyCode.Backspace;
                case 0x09: return KeyCode.Tab;
                case 0x0D: return extended ? KeyCode.KeypadEnter : KeyCode.Return;
                case 0x13: return KeyCode.Pause;
                case 0x14: return KeyCode.CapsLock;
                case 0x1B: return KeyCode.Escape;
                case 0x20: return KeyCode.Space;
                case 0x21: return KeyCode.PageUp;
                case 0x22: return KeyCode.PageDown;
                case 0x23: return KeyCode.End;
                case 0x24: return KeyCode.Home;
                case 0x25: return KeyCode.LeftArrow;
                case 0x26: return KeyCode.UpArrow;
                case 0x27: return KeyCode.RightArrow;
                case 0x28: return KeyCode.DownArrow;
                case 0x2C: return KeyCode.SysReq;
                case 0x2D: return KeyCode.Insert;
                case 0x2E: return KeyCode.Delete;
                case 0x5B: return KeyCode.LeftWindows;
                case 0x5C: return KeyCode.RightWindows;
                case 0x6A: return KeyCode.KeypadMultiply;
                case 0x6B: return KeyCode.KeypadPlus;
                case 0x6D: return KeyCode.KeypadMinus;
                case 0x6E: return KeyCode.KeypadPeriod;
                case 0x6F: return KeyCode.KeypadDivide;
                case 0x90: return KeyCode.Numlock;
                case 0x91: return KeyCode.ScrollLock;
                case 0xA0: return KeyCode.LeftShift;
                case 0xA1: return KeyCode.RightShift;
                case 0xA2: return KeyCode.LeftControl;
                case 0xA3: return KeyCode.RightControl;
                case 0xA4: return KeyCode.LeftAlt;
                case 0xA5: return KeyCode.RightAlt;
                case 0xBA: return KeyCode.Semicolon;
                case 0xBB: return KeyCode.Equals;
                case 0xBC: return KeyCode.Comma;
                case 0xBD: return KeyCode.Minus;
                case 0xBE: return KeyCode.Period;
                case 0xBF: return KeyCode.Slash;
                case 0xC0: return KeyCode.BackQuote;
                case 0xDB: return KeyCode.LeftBracket;
                case 0xDC: return KeyCode.Backslash;
                case 0xDD: return KeyCode.RightBracket;
                case 0xDE: return KeyCode.Quote;
                default: return KeyCode.None;
            }
        }
    }

    [HarmonyPatch(typeof(Input), "GetKey", new Type[] { typeof(KeyCode) })]
    internal static class InputGetKeyPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(KeyCode key, ref bool __result)
        {
            if (!VirtualKeyboardState.IsHeld(key))
                return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), "GetKeyDown", new Type[] { typeof(KeyCode) })]
    internal static class InputGetKeyDownPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(KeyCode key, ref bool __result)
        {
            if (!VirtualKeyboardState.IsDown(key))
                return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), "GetKeyUp", new Type[] { typeof(KeyCode) })]
    internal static class InputGetKeyUpPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(KeyCode key, ref bool __result)
        {
            if (!VirtualKeyboardState.IsUp(key))
                return true;
            __result = true;
            return false;
        }
    }
}
