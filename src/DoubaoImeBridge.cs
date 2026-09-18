using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Doubao desktop-IME bridge. When the desktop window is foreground and a
    // Chinese TIP (Doubao IME) owns the layout, every VR key is injected as a
    // real keybd_event so the DESKTOP IME composes — its candidate window is
    // then screen-captured via PrintWindow and shown inside the VR IME panel.
    // Commits land in the InputField through the native WM_CHAR path, so the
    // field updates itself; we only mirror composition/candidate state.
    //
    // Why image capture? Doubao exposes composition text through IMM32, but
    // candidate lists are unreachable (IMM32 candLists=0, no TSF
    // CandidateListUIElement, DirectUI window opaque to UI Automation).
    // PrintWindow on the OimeDirectUIWindow captures the real candidate bar —
    // AI phrases, ordering and all — pixel-perfect.
    internal static class DoubaoImeBridge
    {
        private const uint GcsCompStr = 0x0008;
        private const uint ImeCmodeNative = 0x0001;
        private const int WindowPollMs = 120;
        private const int CaptureMs = 70;
        private const int MaxCaptureW = 1600;
        private const int MaxCaptureH = 400;

        internal static bool ConfigEnabled = true;

        private static bool _active;
        private static bool _layoutWasChinese;
        private static IntPtr _candHwnd;
        private static int _nextWindowScan;
        private static int _nextCapture;
        private static Texture2D _candTexture;
        private static readonly HashSet<int> _imePids = new HashSet<int>();
        private static int _nextPidScan;
        private static readonly ImeCandidateSnapshot _snapshot =
            new ImeCandidateSnapshot();
        private static readonly HashSet<ushort> _forwarded =
            new HashSet<ushort>();
        private static bool _loggedNoWindow;
        private static int _lastCompLen;

        internal static ImeCandidateSnapshot Snapshot { get { return _snapshot; } }

        // Bridged = we own the key path (VaM foreground + Chinese layout +
        // input target). Evaluated per call — cheap.
        internal static bool Active
        {
            get { return _active; }
        }

        internal static void Reset()
        {
            _forwarded.Clear();
            _snapshot.Composition = string.Empty;
            _snapshot.ImageMode = false;
            _candHwnd = IntPtr.Zero;
            _lastCompLen = 0;
        }

        internal static bool KeyDown(ushort vk, bool extended)
        {
            if (!EvalActive()) return false;
            _forwarded.Add(vk);
            Inject(vk, false, extended);
            return true;
        }

        internal static bool KeyUp(ushort vk, bool extended)
        {
            // Key-up must reach the desktop for every key we forwarded, or the
            // key sticks. Bridge even when _active flipped mid-press.
            if (!_forwarded.Contains(vk)) return false;
            _forwarded.Remove(vk);
            Inject(vk, true, extended);
            return true;
        }

        internal static void ReleaseAll()
        {
            foreach (ushort vk in _forwarded)
                Inject(vk, true, false);
            _forwarded.Clear();
        }

        private static bool EvalActive()
        {
            _active = ConfigEnabled &&
                VrTextInputBridge.Active &&
                VrTextInputBridge.ImeEnabled &&
                VaMForeground() &&
                ChineseLayoutOnOurThread();
            return _active;
        }

        private static bool VaMForeground()
        {
            IntPtr game = WindowsKeyboard.GetGameWindowHandle();
            return game != IntPtr.Zero && GetForegroundWindow() == game;
        }

        private static bool ChineseLayoutOnOurThread()
        {
            IntPtr hkl = GetKeyboardLayout(0);
            bool cn = (hkl.ToInt64() & 0xFFFF) == 0x0804;
            if (cn != _layoutWasChinese)
            {
                _layoutWasChinese = cn;
                Log("layout " + (cn ? "CN" : "non-CN") +
                    " hkl=0x" + hkl.ToString("x8"));
            }
            return cn;
        }

        // ---- per-frame state -------------------------------------------

        internal static void Tick()
        {
            EvalActive();
            if (!_active)
            {
                if (_snapshot.ImageMode || _snapshot.Composition.Length > 0)
                {
                    _snapshot.Composition = string.Empty;
                    _snapshot.ImageMode = false;
                    _snapshot.Candidates.Clear();
                    _candHwnd = IntPtr.Zero;
                }
                return;
            }
            ReadComposition();
            TrackCandidateWindow();
            MaybeCapture();
        }

        private static void ReadComposition()
        {
            IntPtr hwnd = WindowsKeyboard.GetGameWindowHandle();
            IntPtr ctx = ImmGetContext(hwnd);
            if (ctx == IntPtr.Zero) return;
            try
            {
                int bytes = ImmGetCompositionStringW(ctx, GcsCompStr, null, 0);
                string comp = string.Empty;
                if (bytes > 0)
                {
                    StringBuilder sb = new StringBuilder(bytes / 2 + 2);
                    ImmGetCompositionStringW(ctx, GcsCompStr, sb, bytes + 2);
                    comp = sb.ToString();
                }
                _snapshot.Composition = comp;
                int conv, sent;
                ImmGetConversionStatus(ctx, out conv, out sent);
                _snapshot.ChineseMode = (conv & ImeCmodeNative) != 0;
                if (comp.Length != _lastCompLen)
                {
                    _lastCompLen = comp.Length;
                    // Comp changed — force a fresh capture next frame.
                    _nextCapture = 0;
                }
            }
            finally { ImmReleaseContext(hwnd, ctx); }
        }

        // The candidate bar is an ImeService-owned DirectUI window. Several
        // "O"-class windows exist (status bar, tips); the candidate one is
        // the widest visible window with a landscape aspect.
        private static void TrackCandidateWindow()
        {
            if (Environment.TickCount < _nextWindowScan) return;
            _nextWindowScan = Environment.TickCount + WindowPollMs;
            if (_candHwnd != IntPtr.Zero && IsWindow(_candHwnd) &&
                IsWindowVisible(_candHwnd)) return;
            _candHwnd = IntPtr.Zero;
            if (_snapshot.Composition.Length == 0) return;
            RefreshImePids();
            IntPtr found = IntPtr.Zero; int bestW = 0;
            EnumWindows(delegate(IntPtr hwnd, IntPtr lp)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (!_imePids.Contains((int)pid)) return true;
                if (!IsWindowVisible(hwnd)) return true;
                RECT r; GetWindowRect(hwnd, out r);
                int w = r.r - r.l, h = r.b - r.t;
                if (w < 400 || h < 30 || w < h * 4) return true;
                if (w > bestW) { bestW = w; found = hwnd; }
                return true;
            }, IntPtr.Zero);
            if (found != _candHwnd)
            {
                _candHwnd = found;
                _loggedNoWindow = false;
                if (found != IntPtr.Zero)
                    Log("candidate window " + found.ToString("x") +
                        " " + bestW + "px");
            }
            if (_candHwnd == IntPtr.Zero && !_loggedNoWindow &&
                _snapshot.Composition.Length > 0)
            {
                _loggedNoWindow = true;
                Log("no candidate window found yet (pids=" +
                    _imePids.Count + ")");
            }
        }

        private static void RefreshImePids()
        {
            if (Environment.TickCount < _nextPidScan) return;
            _nextPidScan = Environment.TickCount + 3000;
            _imePids.Clear();
            foreach (Process p in Process.GetProcesses())
            {
                string n = p.ProcessName;
                if (n.IndexOf("ImeService", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("TextInputHost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("ChsIME", StringComparison.OrdinalIgnoreCase) >= 0)
                    _imePids.Add(p.Id);
            }
        }

        private static void MaybeCapture()
        {
            if (_candHwnd == IntPtr.Zero || !IsWindowVisible(_candHwnd))
            {
                if (_snapshot.ImageMode) { _snapshot.ImageMode = false; }
                return;
            }
            if (Environment.TickCount < _nextCapture) return;
            _nextCapture = Environment.TickCount + CaptureMs;
            RECT r; GetWindowRect(_candHwnd, out r);
            int w = Math.Min(r.r - r.l, MaxCaptureW);
            int h = Math.Min(r.b - r.t, MaxCaptureH);
            if (w <= 0 || h <= 0) return;
            Texture2D tex = CaptureWindow(_candHwnd, w, h);
            if (tex != null)
            {
                _snapshot.Image = tex;
                _snapshot.ImageW = w;
                _snapshot.ImageH = h;
                _snapshot.ImageMode = true;
                _snapshot.ImageStamp++;
            }
        }

        private static Texture2D CaptureWindow(IntPtr hwnd, int w, int h)
        {
            IntPtr sdc = GetDC(IntPtr.Zero);
            IntPtr mdc = CreateCompatibleDC(sdc);
            IntPtr bmp = CreateCompatibleBitmap(sdc, w, h);
            IntPtr old = SelectObject(mdc, bmp);
            bool ok = PrintWindow(hwnd, mdc, 2) || PrintWindow(hwnd, mdc, 0);
            Texture2D tex = null;
            if (ok)
            {
                byte[] px = new byte[w * h * 4];
                BITMAPINFO bmi = new BITMAPINFO();
                bmi.size = 40; bmi.w = w; bmi.h = -h;
                bmi.planes = 1; bmi.bpp = 32;
                int lines = GetDIBits(mdc, bmp, 0, h, px, ref bmi, 0);
                if (lines > 0)
                {
                    // BGRA -> RGBA
                    for (int i = 0; i < px.Length; i += 4)
                    {
                        byte b = px[i]; px[i] = px[i + 2]; px[i + 2] = b;
                        px[i + 3] = 255;
                    }
                    if (_candTexture == null ||
                        _candTexture.width != w || _candTexture.height != h)
                    {
                        _candTexture = new Texture2D(w, h,
                            TextureFormat.RGBA32, false);
                    }
                    tex = _candTexture;
                    tex.LoadRawTextureData(px);
                    tex.Apply(false, false);
                }
            }
            SelectObject(mdc, old);
            DeleteObject(bmp);
            DeleteDC(mdc);
            ReleaseDC(IntPtr.Zero, sdc);
            return tex;
        }

        // Click on the VR image -> forward the click into the real candidate
        // window at the same client position. Doubao selects the row itself.
        internal static bool ClickCandidate(float u, float v)
        {
            if (_candHwnd == IntPtr.Zero) return false;
            RECT cr; GetClientRect(_candHwnd, out cr);
            int x = (int)(u * (cr.r - cr.l));
            int y = (int)((1f - v) * (cr.b - cr.t));
            IntPtr pt = ClientPoint(_candHwnd, x, y);
            PostMessage(_candHwnd, 0x0201, new IntPtr(1), pt); // LBUTTONDOWN
            PostMessage(_candHwnd, 0x0202, IntPtr.Zero, pt);   // LBUTTONUP
            return true;
        }

        private static IntPtr ClientPoint(IntPtr hwnd, int x, int y)
        {
            return new IntPtr((y << 16) | (x & 0xFFFF));
        }

        private static void Inject(ushort vk, bool keyUp, bool extended)
        {
            uint flags = (extended ? 1u : 0u) | (keyUp ? 2u : 0u);
            keybd_event((byte)vk,
                (byte)MapVirtualKey(vk, 0), flags, UIntPtr.Zero);
        }

        private static void Log(string m)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("Q3 doubao " + m);
        }

        // ---- pinvoke -----------------------------------------------------

        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint tid);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lp);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint map);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr h);
        [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr h, IntPtr ctx);
        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        private static extern int ImmGetCompositionStringW(IntPtr ctx, uint idx, StringBuilder sb, int cap);
        [DllImport("imm32.dll")]
        private static extern bool ImmGetConversionStatus(IntPtr ctx, out int conv, out int sent);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, int start, int lines, byte[] buf, ref BITMAPINFO bmi, int usage);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int l, t, r, b; }
        [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO
        {
            public uint size; public int w, h;
            public ushort planes, bpp;
            public uint comp, sizeImg; public int xppm, yppm;
            public uint clrUsed, clrImp;
        }
    }
}
