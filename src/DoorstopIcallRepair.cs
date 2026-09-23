using System;
using System.Runtime.InteropServices;

namespace Quest3TriggerUI
{
    // VaM ships the Doorstop performance patch (PerformancePatches\SkinMeshPartDLL.dll)
    // paired with a patched Assembly-CSharp. The managed side declares
    // NativeLibrary.Linked::EnableGC / DisableGC as native icalls and calls them in
    // SuperController.LoadCo, MemoryOptimizer, SuperController.GarbageCollect,
    // DAZCharacterSelector.UnloadUnusedAssetsDelayed and MeshVR.PerfMon*.
    //
    // When the native side fails to register those two icalls, every call raises
    // MissingMethodException. Inside LoadCo the exception escapes MoveNext, Unity
    // terminates the coroutine, and the scene load stops right after the Person
    // prefab preload: the three Person atoms are never created, so the load never
    // completes. PerfMonPre/PerfMonCamera additionally throw once per frame, which
    // floods output_log.txt and burns main-thread time.
    //
    // Registering no-op stubs reproduces the behaviour of a VaM install that does not
    // run the performance patch, so both symptoms disappear. If the patch DLL does
    // register the real implementations later, its registration simply wins.
    internal static class DoorstopIcallRepair
    {
        private const string DisableGcIcall = "NativeLibrary.Linked::DisableGC";
        private const string EnableGcIcall = "NativeLibrary.Linked::EnableGC";

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = false)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        private delegate void MonoAddInternalCall(string name, IntPtr method);
        private delegate void NativeStub();

        private static MonoAddInternalCall _addInternalCall;
        private static NativeStub _stub;
        private static IntPtr _stubPointer = IntPtr.Zero;
        private static bool _attempted;

        internal static string Status = "not attempted";

        internal static void TryInstall()
        {
            if (_attempted) return;
            _attempted = true;
            try
            {
                IntPtr mono = GetModuleHandle("mono.dll");
                if (mono == IntPtr.Zero) { Status = "mono.dll module not found"; return; }
                IntPtr addInternalCall = GetProcAddress(mono, "mono_add_internal_call");
                if (addInternalCall == IntPtr.Zero)
                {
                    Status = "mono.dll does not export mono_add_internal_call";
                    return;
                }
                _stub = NoOp;
                _stubPointer = Marshal.GetFunctionPointerForDelegate(_stub);
                _addInternalCall = (MonoAddInternalCall)Marshal.GetDelegateForFunctionPointer(
                    addInternalCall, typeof(MonoAddInternalCall));
                _addInternalCall(DisableGcIcall, _stubPointer);
                _addInternalCall(EnableGcIcall, _stubPointer);
                Status = "registered no-op stubs for DisableGC/EnableGC";
                try
                {
                    NativeLibrary.Linked.DisableGC();
                    NativeLibrary.Linked.EnableGC();
                    Status += " | self-test passed";
                }
                catch (MissingMethodException)
                {
                    Status += " | self-test: icall still unresolved";
                }
                catch (Exception probeError)
                {
                    Status += " | self-test error: " + probeError.GetType().Name;
                }
            }
            catch (Exception e)
            {
                Status = "failed: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static void NoOp() { }
    }
}