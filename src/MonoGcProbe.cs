using System;
using System.Runtime.InteropServices;

namespace Quest3TriggerUI
{
    // Read-only window into the embedded Boehm GC (mono.dll).
    //
    // Why: the person-change paging spikes need a number for "how much of the
    // Boehm heap is reservation rather than live data". mono_gc_get_heap_size
    // is Boehm's total heap size, mono_gc_get_used_size its allocated bytes;
    // both are exported by mono.dll (verified against the PE export table),
    // while every policy setter (GC_* and the GC_* environment variables) is
    // not. heap - used is the ceiling any mono.dll patch could hand back to
    // the OS, so measure before touching the runtime.
    internal static class MonoGcProbe
    {
        [DllImport("mono.dll", EntryPoint = "mono_gc_get_heap_size",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern long HeapSize64();

        [DllImport("mono.dll", EntryPoint = "mono_gc_get_used_size",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern long UsedSize64();

        // Some Mono builds declare these as 32-bit; a 64-bit read of a 32-bit
        // return leaves garbage in the high half, so verify plausibility and
        // fall back instead of printing nonsense.
        [DllImport("mono.dll", EntryPoint = "mono_gc_get_heap_size",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int HeapSize32();

        [DllImport("mono.dll", EntryPoint = "mono_gc_get_used_size",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int UsedSize32();

        private static bool _failed;
        private static bool _warned;

        private static void Log(string message)
        {
            try { Quest3TriggerUIPlugin.Log.LogInfo("[mono-gc] " + message); }
            catch { }
        }

        private static bool Plausible(long v)
        {
            return v > 1048576L && v < (1L << 44);
        }

        private static long Read(bool heap)
        {
            if (_failed) return -1L;
            try
            {
                long v = heap ? HeapSize64() : UsedSize64();
                if (!Plausible(v)) v = heap ? (long)HeapSize32() : (long)UsedSize32();
                if (!Plausible(v))
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Log("implausible read: heap64=" + HeapSize64() +
                            " used64=" + UsedSize64() +
                            " heap32=" + HeapSize32() + " used32=" + UsedSize32());
                    }
                    return -1L;
                }
                return v;
            }
            catch (Exception e)
            {
                _failed = true;
                Log("probe unavailable: " + e.GetType().Name + ": " + e.Message);
                return -1L;
            }
        }

        // Raw bytes for other probes (same guarded read as Suffix).
        internal static long HeapBytes() { return Read(true); }
        internal static long UsedBytes() { return Read(false); }
        internal static string Suffix()
        {
            long h = Read(true), u = Read(false);
            if (h < 0L && u < 0L) return " monoHeap=n/a monoUsed=n/a";
            double hd = h < 0L ? 0.0 : h / 1073741824.0;
            double ud = u < 0L ? 0.0 : u / 1073741824.0;
            return string.Format(
                " monoHeap={0:F2}GB monoUsed={1:F2}GB monoFree={2:F2}GB",
                hd, ud, (h < 0L || u < 0L) ? 0.0 : hd - ud);
        }
    }
}
