using System;
using System.Runtime.InteropServices;

using UnityEngine;

namespace Quest3TriggerUI
{
    // Pins VaM's audio output to the Windows default render device.
    //
    // Unity binds its audio session once at engine init and never follows
    // OS-level default-device changes, so switching headset/speakers mid-run
    // leaves VaM on the old endpoint. There is no managed "pick device" API;
    // the only lever is AudioSettings.Reset, which tears the audio engine
    // down and rebuilds it against the CURRENT default endpoint.
    //
    // Design: poll the default endpoint ID every few seconds and reset the
    // audio engine once when it actually changes. The MMDevice COM API is
    // reached via raw P/Invoke + manual vtable slots instead of ComImport
    // RCWs — Unity's Mono does not implement COM interop and hard-crashes
    // (native AV, uncatchable) on ComImport activation. Reset interrupts
    // all playing sounds — the inherent cost of rebinding, paid only on a
    // real switch.
    internal static class AudioDeviceFollower
    {
        internal static bool Enabled = true;
        private const float PollInterval = 3f;

        private const int EDataFlowRender = 0;
        private const int ERoleConsole = 0;
        private const int ERoleMultimedia = 1;
        private const uint ClsctxInprocServer = 1;

        private static readonly Guid ClsidMmDeviceEnumerator =
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IidImmDeviceEnumerator =
            new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid,
            IntPtr outer, uint clsctx, ref Guid iid, out IntPtr obj);
        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr ptr);

        // IUnknown occupies vtable slots 0..2 (QueryInterface, AddRef,
        // Release); interface methods follow in declaration order:
        //   IMMDeviceEnumerator: EnumAudioEndpoints=3, GetDefaultAudioEndpoint=4
        //   IMMDevice:           Activate=3, OpenPropertyStore=4, GetId=5
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDefaultAudioEndpointFn(IntPtr self,
            int flow, int role, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetIdFn(IntPtr self, out IntPtr id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseFn(IntPtr self);

        private static IntPtr _enumerator;
        private static string _lastKey;
        private static float _nextPoll;
        private static bool _failed;

        internal static void Tick()
        {
            if (!Enabled || _failed) return;
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollInterval;
            string key = CurrentDefaultKey();
            if (key == null) return;
            if (_lastKey == null)
            {
                _lastKey = key;
                Log("音频跟随已启动，当前默认输出 " + key);
                return;
            }
            if (key == _lastKey) return;
            _lastKey = key;
            try
            {
                AudioSettings.Reset(AudioSettings.GetConfiguration());
                Log("Windows 默认输出已变更 → 音频引擎重绑 " + key);
            }
            catch (Exception e)
            {
                Log("音频引擎重绑失败：" + e.Message);
            }
        }

        // Both default roles are watched: Unity/FMOD renders through the
        // "Default Device" which may be reported under console or
        // multimedia role depending on the driver.
        private static string CurrentDefaultKey()
        {
            try
            {
                if (_enumerator == IntPtr.Zero)
                {
                    Guid clsid = ClsidMmDeviceEnumerator;
                    Guid iid = IidImmDeviceEnumerator;
                    int hr = CoCreateInstance(ref clsid, IntPtr.Zero,
                        ClsctxInprocServer, ref iid, out _enumerator);
                    if (hr != 0 || _enumerator == IntPtr.Zero)
                    {
                        _failed = true;
                        Log("音频跟随初始化失败：CoCreateInstance hr=" + hr);
                        return null;
                    }
                }
                return EndpointId(ERoleConsole) + "|" +
                       EndpointId(ERoleMultimedia);
            }
            catch (Exception e)
            {
                _failed = true;
                Log("音频跟随初始化失败：" + e.Message);
                return null;
            }
        }

        private static string EndpointId(int role)
        {
            IntPtr dev = IntPtr.Zero;
            IntPtr idPtr = IntPtr.Zero;
            try
            {
                GetDefaultAudioEndpointFn getDefault =
                    (GetDefaultAudioEndpointFn)
                        Marshal.GetDelegateForFunctionPointer(
                            Vtable(_enumerator, 4),
                            typeof(GetDefaultAudioEndpointFn));
                int hr = getDefault(_enumerator, EDataFlowRender, role,
                    out dev);
                if (hr != 0 || dev == IntPtr.Zero) return "";
                GetIdFn getId = (GetIdFn)
                    Marshal.GetDelegateForFunctionPointer(
                        Vtable(dev, 5), typeof(GetIdFn));
                hr = getId(dev, out idPtr);
                if (hr != 0 || idPtr == IntPtr.Zero) return "";
                return Marshal.PtrToStringUni(idPtr) ?? "";
            }
            catch { return ""; }
            finally
            {
                if (idPtr != IntPtr.Zero) CoTaskMemFree(idPtr);
                if (dev != IntPtr.Zero)
                    ((ReleaseFn)Marshal.GetDelegateForFunctionPointer(
                        Vtable(dev, 2), typeof(ReleaseFn)))(dev);
            }
        }

        private static IntPtr Vtable(IntPtr obj, int slot)
        {
            return Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),
                slot * IntPtr.Size);
        }

        private static void Log(string message)
        {
            try { Quest3TriggerUIPlugin.Log.LogInfo("[音频跟随] " + message); }
            catch { }
        }
    }
}
