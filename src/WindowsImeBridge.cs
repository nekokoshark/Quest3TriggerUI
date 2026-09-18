using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal sealed class ImeCandidateSnapshot
    {
        internal readonly List<string> Candidates = new List<string>();
        internal int Selection;
        internal int PageStart;
        internal int PageSize;
        internal string Composition = string.Empty;
        // True while the IME's own conversion mode is Chinese (the state a
        // desktop Shift-tap toggles). Drives the 中/英 indicator.
        internal bool ChineseMode;
        // Doubao bridge: candidate list arrives as a captured image of the
        // desktop IME's real candidate window (its text is unreachable).
        internal bool ImageMode;
        internal Texture2D Image;
        internal int ImageW, ImageH;
        internal int ImageStamp;   // bumped per capture so UI knows pixels changed
    }

    internal static class WindowsImeBridge
    {
        private const uint GcsCompStr = 0x0008;
        private const uint NiSelectCandidateStr = 0x0012;
        private const uint ImeCmodeNative = 0x0001;
        private const int MaxCandidateBytes = 1024 * 1024;
        // Feature gate (config-level). Engaged state is _attached — driven
        // by whichever keyboard layout the DESKTOP thread is on.
        private static bool _enabled = true;
        private static bool _attached;
        private static IntPtr _chineseLayout;
        private static IntPtr _lastNonChineseLayout;
        private static ImeCandidateSnapshot _snapshot = new ImeCandidateSnapshot();

        // Enabled = the bridge is live AND the desktop is currently on a
        // Chinese IME layout — the state callers should gate key routing on.
        internal static bool Enabled
        {
            get { return _enabled && _attached; }
        }
        internal static ImeCandidateSnapshot Snapshot { get { return _snapshot; } }

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint threadId);

        [DllImport("user32.dll")]
        private static extern int GetKeyboardLayoutList(int count, IntPtr[] layouts);

        [DllImport("user32.dll")]
        private static extern IntPtr ActivateKeyboardLayout(IntPtr layout, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window, out uint processId);

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr window);

        [DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr window, IntPtr context);

        [DllImport("imm32.dll")]
        private static extern bool ImmSetOpenStatus(IntPtr context, bool open);

        [DllImport("imm32.dll")]
        private static extern uint ImmGetCandidateListW(
            IntPtr context, uint index, IntPtr candidateList, uint bufferLength);

        [DllImport("imm32.dll")]
        private static extern uint ImmGetCandidateListCountW(
            IntPtr context, out uint listCount);

        [DllImport("imm32.dll")]
        private static extern int ImmGetCompositionStringW(
            IntPtr context, uint index, IntPtr buffer, uint bufferLength);

        [DllImport("imm32.dll")]
        private static extern bool ImmNotifyIME(
            IntPtr context, uint action, uint index, uint value);

        [DllImport("imm32.dll")]
        private static extern bool ImmGetConversionStatus(
            IntPtr context, out uint conversion, out uint sentence);

        // Feature gate only — engagement itself follows the desktop layout
        // (see Tick). Callers toggle the actual desktop layout via
        // SwitchToChinese/SwitchToNonChinese, not this flag.
        internal static void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
                Detach();
        }

        // The "desktop input locale" the user sees in the taskbar is the
        // FOREGROUND window's thread layout — per-thread state, not global.
        // Our own thread's layout can disagree (e.g. the user switched to
        // Chinese while a different app was foreground), so we must read the
        // foreground thread, then mirror that layout onto OUR thread so keys
        // delivered to our window compose the same way.
        internal static IntPtr DesktopLayout()
        {
            IntPtr window = GetForegroundWindow();
            uint pid;
            uint thread = window == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(window, out pid);
            return GetKeyboardLayout(thread);
        }

        // Engage only when the desktop is on a Chinese IME layout — we follow
        // the user's choice instead of forcing it, so Win+Space / taskbar
        // switches carry over automatically.
        internal static void Attach()
        {
            if (!_enabled || _attached) return;
            IntPtr layout = DesktopLayout();
            if (!IsChineseLayout(layout)) return;
            // Mirror the desktop layout onto our own thread — the HIMC and
            // key processing for our window run under OUR thread's active
            // layout, which may still be the old English one.
            if (GetKeyboardLayout(0) != layout)
                ActivateKeyboardLayout(layout, 0);
            _attached = true;
            Input.imeCompositionMode = IMECompositionMode.On;
            WithContext(delegate(IntPtr context) { ImmSetOpenStatus(context, true); });
            Log("attach layout=0x" + layout.ToString("X") +
                " own=0x" + GetKeyboardLayout(0).ToString("X"));
        }

        internal static void Detach()
        {
            // Idempotent: Tick reaches this every frame while no text field is
            // selected, so a fully clean state must not reallocate or touch IME.
            if (!_attached &&
                _snapshot.Candidates.Count == 0 &&
                _snapshot.Composition.Length == 0)
                return;
            _attached = false;
            _snapshot = new ImeCandidateSnapshot();
            Input.imeCompositionMode = IMECompositionMode.Auto;
            // The layout itself is never ours to restore — the desktop owns
            // it; we only close the composition engine on this window.
            WithContext(delegate(IntPtr context) { ImmSetOpenStatus(context, false); });
            Log("detach");
        }

        internal static IntPtr CurrentLayout
        {
            get { return DesktopLayout(); }
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("Q3 IME " + message);
        }

        internal static bool IsChineseLayout(IntPtr layout)
        {
            // HKL low word = LANGID; primary language id 0x04 = Chinese.
            return ((int)(layout.ToInt64() & 0xFFFF) & 0x03FF) == 0x0004;
        }

        // Keyboard-button equivalent of the desktop Win+Space switch.
        internal static void SwitchToChinese()
        {
            IntPtr chinese = FindChineseLayout();
            if (chinese != IntPtr.Zero && chinese != GetKeyboardLayout(0))
                ActivateKeyboardLayout(chinese, 0);
        }

        internal static void SwitchToNonChinese()
        {
            IntPtr target = _lastNonChineseLayout;
            if (target == IntPtr.Zero || IsChineseLayout(target))
            {
                int count = GetKeyboardLayoutList(0, null);
                if (count > 0 && count <= 128)
                {
                    IntPtr[] layouts = new IntPtr[count];
                    count = GetKeyboardLayoutList(layouts.Length, layouts);
                    for (int i = 0; i < count; i++)
                    {
                        if (!IsChineseLayout(layouts[i]))
                        {
                            target = layouts[i];
                            break;
                        }
                    }
                }
            }
            if (target != IntPtr.Zero && target != GetKeyboardLayout(0))
                ActivateKeyboardLayout(target, 0);
        }

        internal static bool AcceptsKey(ushort key)
        {
            return (key >= 0x30 && key <= 0x5A) ||
                   (key >= 0x60 && key <= 0x69) ||
                   key == 0x08 || key == 0x0D || key == 0x1B || key == 0x20 ||
                   (key >= 0x21 && key <= 0x28) || key == 0x2E ||
                   (key >= 0xBA && key <= 0xC0) || (key >= 0xDB && key <= 0xDE);
        }

        internal static void SendKey(ushort key, bool extended, bool shift)
        {
            Attach();
            if (shift) WindowsKeyboard.SendGlobal(0xA0, false, false);
            WindowsKeyboard.SendGlobal(key, false, extended);
            WindowsKeyboard.SendGlobal(key, true, extended);
            if (shift) WindowsKeyboard.SendGlobal(0xA0, true, false);
        }

        internal static void SelectCandidate(int pageIndex, int absoluteIndex)
        {
            // Number selection follows the active desktop IME's normal path and
            // commits exactly as if the user pressed the displayed number.
            if (pageIndex >= 0 && pageIndex < 9)
            {
                SendKey((ushort)(0x31 + pageIndex), false, false);
                return;
            }
            if (pageIndex == 9)
            {
                SendKey(0x30, false, false);
                return;
            }

            bool selected = false;
            WithContext(delegate(IntPtr context) {
                selected = ImmNotifyIME(context, NiSelectCandidateStr, 0,
                    unchecked((uint)absoluteIndex));
            });
            if (selected) SendKey(0x0D, false, false);
        }

        internal static void Tick()
        {
            // Follow the desktop input locale every frame: Chinese layout →
            // attach; anything else → detach. The keyboard's 中/英 button
            // and external Win+Space switches both land here.
            if (_enabled)
            {
                IntPtr layout = DesktopLayout();
                if (IsChineseLayout(layout))
                    Attach();
                else
                {
                    if (_attached)
                        Detach();
                    _lastNonChineseLayout = layout;
                }
            }
            if (!_enabled || !_attached)
            {
                if (_snapshot.Candidates.Count != 0 ||
                    _snapshot.Composition.Length != 0)
                    _snapshot = new ImeCandidateSnapshot();
                return;
            }

            ImeCandidateSnapshot next = null;
            WithContext(delegate(IntPtr context) {
                next = ReadSnapshot(context);
            });
            _snapshot = next ?? new ImeCandidateSnapshot();

            // Throttled heartbeat while engaged: shows exactly what the IME
            // reports (composition bytes, candidate bytes, conversion mode)
            // so a silent failure is diagnosable from the log alone.
            if (Time.unscaledTime >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.unscaledTime + 1.5f;
                Log("hb ctx=" + (DebugContextOk ? 1 : 0) +
                    " conv=0x" + DebugConversion.ToString("X") +
                    " compBytes=" + DebugCompBytes +
                    " comp=\"" + _snapshot.Composition + "\"" +
                    " candBytes=" + DebugCandBytes +
                    " candIdx=" + DebugCandIndex +
                    " candLists=" + DebugCandListCount +
                    " cands=" + _snapshot.Candidates.Count +
                    " layout=0x" + DesktopLayout().ToString("X"));
            }
        }

        private static float _nextHeartbeat;

        // Raw values from the last read — dumped by the heartbeat log so we
        // can see exactly which stage of the IMM32 read chain is failing.
        internal static int DebugCompBytes;
        internal static uint DebugCandBytes;
        internal static uint DebugCandIndex;
        internal static uint DebugCandListCount;
        internal static uint DebugConversion;
        internal static bool DebugContextOk;

        private static ImeCandidateSnapshot ReadSnapshot(IntPtr context)
        {
            ImeCandidateSnapshot result = new ImeCandidateSnapshot();
            uint conv, sent;
            result.ChineseMode =
                ImmGetConversionStatus(context, out conv, out sent) &&
                (conv & ImeCmodeNative) != 0;
            DebugConversion = conv;
            DebugCompBytes = ImmGetCompositionStringW(context, GcsCompStr, IntPtr.Zero, 0);
            if (DebugCompBytes > 0 && DebugCompBytes <= 65536)
            {
                IntPtr composition = Marshal.AllocHGlobal(DebugCompBytes + 2);
                try
                {
                    int copied = ImmGetCompositionStringW(
                        context, GcsCompStr, composition, (uint)DebugCompBytes);
                    if (copied > 0)
                        result.Composition = Marshal.PtrToStringUni(composition, copied / 2) ?? string.Empty;
                }
                finally { Marshal.FreeHGlobal(composition); }
            }

            uint listCount;
            DebugCandListCount = ImmGetCandidateListCountW(context, out listCount);
            // CUAS/IME variants park the visible candidate list under
            // different dwIndex slots — scan a handful instead of only 0.
            uint bytes = 0;
            uint index = 0;
            for (uint i = 0; i < 8; i++)
            {
                uint size = ImmGetCandidateListW(context, i, IntPtr.Zero, 0);
                if (size >= 24 && size <= MaxCandidateBytes)
                {
                    bytes = size;
                    index = i;
                    break;
                }
                if (i == 0) bytes = size; // keep index-0 size for the log
            }
            DebugCandBytes = bytes;
            DebugCandIndex = index;
            if (bytes < 24) return result;
            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                uint copied = ImmGetCandidateListW(context, index, buffer, bytes);
                if (copied < 24 || copied > bytes) return result;
                byte[] raw = new byte[copied];
                Marshal.Copy(buffer, raw, 0, raw.Length);
                ImeCandidateSnapshot candidates = ParseCandidateList(raw);
                candidates.Composition = result.Composition;
                candidates.ChineseMode = result.ChineseMode;
                return candidates;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal static ImeCandidateSnapshot ParseCandidateList(byte[] data)
        {
            ImeCandidateSnapshot result = new ImeCandidateSnapshot();
            if (data == null || data.Length < 24) return result;
            uint size = ReadUInt32(data, 0);
            uint count = ReadUInt32(data, 8);
            result.Selection = SafeInt(ReadUInt32(data, 12));
            result.PageStart = SafeInt(ReadUInt32(data, 16));
            result.PageSize = SafeInt(ReadUInt32(data, 20));
            if (size > data.Length || count > 2048 || 24L + count * 4L > data.Length)
                return new ImeCandidateSnapshot();
            for (int i = 0; i < (int)count; i++)
            {
                uint offset = ReadUInt32(data, 24 + i * 4);
                if (offset >= size || offset >= data.Length) { result.Candidates.Add(string.Empty); continue; }
                int end = (int)offset;
                while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end += 2;
                int length = Math.Max(0, end - (int)offset);
                result.Candidates.Add(Encoding.Unicode.GetString(data, (int)offset, length));
            }
            return result;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | data[offset + 1] << 8 |
                data[offset + 2] << 16 | data[offset + 3] << 24);
        }

        private static int SafeInt(uint value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static IntPtr FindChineseLayout()
        {
            if (_chineseLayout != IntPtr.Zero) return _chineseLayout;
            int count = GetKeyboardLayoutList(0, null);
            if (count <= 0 || count > 128) return IntPtr.Zero;
            IntPtr[] layouts = new IntPtr[count];
            count = GetKeyboardLayoutList(layouts.Length, layouts);
            for (int i = 0; i < count; i++)
            {
                int languageId = unchecked((int)layouts[i].ToInt64()) & 0xFFFF;
                if ((languageId & 0x03FF) == 0x0004)
                {
                    _chineseLayout = layouts[i];
                    break;
                }
            }
            return _chineseLayout;
        }

        private static void WithContext(Action<IntPtr> action)
        {
            IntPtr window = WindowsKeyboard.GetGameWindowHandle();
            if (window == IntPtr.Zero) { DebugContextOk = false; return; }
            IntPtr context = ImmGetContext(window);
            if (context == IntPtr.Zero) { DebugContextOk = false; return; }
            DebugContextOk = true;
            try { action(context); }
            finally { ImmReleaseContext(window, context); }
        }
    }
}
