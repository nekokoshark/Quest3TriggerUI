using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Quest3TriggerUI
{
    // One-shot memory composition snapshot. Triggered by setting
    // [Diagnostics] MemorySnapshot=true in the cfg (auto-resets).
    // Note: Profiler.GetRuntimeMemorySizeLong is unreliable on this
    // Unity version (returns ~0 for textures), so asset bytes are
    // estimated from dimensions/format; process counters come from
    // GetProcessMemoryInfo because Process.WorkingSet64 also reads 0.
    internal static class MemoryProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemCounters
        {
            public uint cb;
            // Native DWORD — declaring UIntPtr shifted every later field
            // by 4 bytes and made WorkingSetSize/PagefileUsage read the
            // wrong counters.
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(
            IntPtr hProcess, out MemCounters counters, uint size);

        // Per-process dedicated VRAM. Profiler.GetAllocatedMemoryForGraphicsDriver
        // returns 0 on Unity 2018 release builds; WMI class names would work but
        // Mono's System.Management is unreliable, so we go straight to PDH.
        // PdhAddEnglishCounterW takes the English path on any locale (this box
        // is zh-CN — plain PdhAddCounter would need the localized name).
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhOpenQueryW(
            string ds, IntPtr ud, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhAddEnglishCounterW(
            IntPtr query, string path, IntPtr ud, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(
            IntPtr query);
        [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterArrayW(
            IntPtr counter, int format, ref uint bufSize,
            out uint itemCount, IntPtr buffer);
        [DllImport("pdh.dll")] private static extern int PdhCloseQuery(
            IntPtr query);

        private const int PDH_FMT_LARGE = 0x00000400;  // LONGLONG, not LONG
        private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
        // PDH_FMT_COUNTERVALUE_ITEM on x64: LPWSTR name (8) + DWORD CStatus (4)
        // + pad (4) + union value (8) → item stride 24, CStatus offset 8,
        // value offset 16. Items with CStatus != 0 carry undefined values.
        private const int PdhItemStride = 24, PdhItemStatusOfs = 8,
            PdhItemValueOfs = 16;

        private static long GfxDedicatedBytes()
        {
            IntPtr q = IntPtr.Zero, ctr = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out q) != 0) return -1;
                if (PdhAddEnglishCounterW(q,
                        "\\GPU Process Memory(*)\\Dedicated Usage",
                        IntPtr.Zero, out ctr) != 0)
                    return -1;
                // First collect on a fresh counter often yields junk —
                // collect twice like any rate-counter reader would.
                if (PdhCollectQueryData(q) != 0) return -1;
                System.Threading.Thread.Sleep(50);
                if (PdhCollectQueryData(q) != 0) return -1;
                uint bufSize = 0, count = 0;
                int rc = PdhGetFormattedCounterArrayW(ctr, PDH_FMT_LARGE,
                    ref bufSize, out count, IntPtr.Zero);
                if (rc != PDH_MORE_DATA || bufSize == 0) return -1;
                buf = Marshal.AllocHGlobal((int)bufSize);
                rc = PdhGetFormattedCounterArrayW(ctr, PDH_FMT_LARGE,
                    ref bufSize, out count, buf);
                if (rc != 0) return -1;
                string needle = "pid_" + Process.GetCurrentProcess().Id + "_";
                long sum = 0; bool any = false;
                for (uint i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(buf.ToInt64() + i * PdhItemStride);
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    if (name == null || !name.StartsWith(needle,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Marshal.ReadInt32(item, PdhItemStatusOfs) != 0)
                        continue;
                    sum += Marshal.ReadInt64(item, PdhItemValueOfs);
                    any = true;
                }
                return any ? sum : -1;
            }
            catch { return -1; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (q != IntPtr.Zero) PdhCloseQuery(q);
            }
        }

        private class ObjRow
        {
            internal string Name;
            internal long Bytes;
        }

        internal static void Dump()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                MemCounters mc;
                long workingSet = Environment.WorkingSet;
                long pagefile = 0;
                if (GetProcessMemoryInfo(
                        Process.GetCurrentProcess().Handle,
                        out mc, (uint)Marshal.SizeOf(typeof(MemCounters))))
                {
                    workingSet = (long)mc.WorkingSetSize.ToUInt64();
                    pagefile = (long)mc.PagefileUsage.ToUInt64();
                }
                long managed = GC.GetTotalMemory(false);
                long monoHeap = UnityEngine.Profiling.Profiler
                    .GetMonoHeapSizeLong();
                long monoUsed = UnityEngine.Profiling.Profiler
                    .GetMonoUsedSizeLong();
                long unityReserved = UnityEngine.Profiling.Profiler
                    .GetTotalReservedMemoryLong();
                long unityAllocated = UnityEngine.Profiling.Profiler
                    .GetTotalAllocatedMemoryLong();
                long unityUnusedReserved = UnityEngine.Profiling.Profiler
                    .GetTotalUnusedReservedMemoryLong();

                Log("==== MEMORY SNAPSHOT ====");
                Log(string.Format(
                    "Process: workingSet={0:F2}GB pagefile={1:F2}GB",
                    workingSet / 1073741824.0,
                    pagefile / 1073741824.0));
                Log(string.Format(
                    "Managed: GC={0:F2}GB | mono heap={1:F2}GB used={2:F2}GB free≈{3:F2}GB",
                    managed / 1073741824.0,
                    monoHeap / 1073741824.0,
                    monoUsed / 1073741824.0,
                    (monoHeap - monoUsed) / 1073741824.0));
                Log(string.Format(
                    "Unity native: reserved={0:F2}GB allocated={1:F2}GB unusedReserved={2:F2}GB | non-Unity+outside≈{3:F2}GB",
                    unityReserved / 1073741824.0,
                    unityAllocated / 1073741824.0,
                    unityUnusedReserved / 1073741824.0,
                    (pagefile - monoHeap - unityReserved) / 1073741824.0));

                DumpTex();
                DumpRT();
                DumpMeshes();
                DumpClips();
                DumpCategory<Material>("Material", 0);
                DumpCategory<Shader>("Shader", 0);
                DumpAtoms();
                DumpClipHolders();
                Log(string.Format(
                    "==== snapshot done in {0}ms ====", sw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                Log("MemoryProbe failed: " + ex);
            }
        }

        private static void DumpTex()
        {
            UnityEngine.Object[] objs =
                Resources.FindObjectsOfTypeAll(typeof(Texture2D));
            var rows = new List<ObjRow>(objs.Length);
            long total = 0;
            int uncompressed = 0;
            long uncompressedBytes = 0;
            for (int i = 0; i < objs.Length; i++)
            {
                Texture2D t = objs[i] as Texture2D;
                if (t == null) continue;
                long bytes = TexBytes(t.width, t.height,
                    FormatBpp(t.format), t.mipmapCount);
                total += bytes;
                if (t.format == TextureFormat.RGBA32 ||
                    t.format == TextureFormat.ARGB32 ||
                    t.format == TextureFormat.RGB24)
                {
                    uncompressed++;
                    uncompressedBytes += bytes;
                }
                rows.Add(new ObjRow
                {
                    Name = string.Format("{0} {1}x{2} {3} mip{4}",
                        t.name, t.width, t.height, t.format, t.mipmapCount),
                    Bytes = bytes
                });
            }
            Report("Texture2D", rows, total, 8);
            Log(string.Format(
                "  uncompressed RGBA/ARGB/RGB24: {0} textures, {1:F2}GB (Compress() candidates)",
                uncompressed, uncompressedBytes / 1073741824.0));
        }

        private static void DumpRT()
        {
            UnityEngine.Object[] objs =
                Resources.FindObjectsOfTypeAll(typeof(RenderTexture));
            var rows = new List<ObjRow>(objs.Length);
            long total = 0;
            for (int i = 0; i < objs.Length; i++)
            {
                RenderTexture t = objs[i] as RenderTexture;
                if (t == null) continue;
                long bytes = (long)t.width * t.height *
                    (ColorBpp(t.format) + t.depth / 8);
                total += bytes;
                rows.Add(new ObjRow
                {
                    Name = string.Format("{0} {1}x{2} {3} d{4} aa{5}",
                        t.name, t.width, t.height, t.format,
                        t.depth, t.antiAliasing),
                    Bytes = bytes
                });
            }
            Report("RenderTexture", rows, total, 5);
        }

        private static void DumpMeshes()
        {
            UnityEngine.Object[] objs =
                Resources.FindObjectsOfTypeAll(typeof(Mesh));
            var rows = new List<ObjRow>(objs.Length);
            long total = 0;
            for (int i = 0; i < objs.Length; i++)
            {
                Mesh m = objs[i] as Mesh;
                if (m == null) continue;
                // ~48B/vertex (pos+norm+tan+uv+skin) + 4B/index is a
                // reasonable VaM-person ballpark.
                long bytes = (long)m.vertexCount * 48 +
                    (long)m.triangles.Length * 4 / 3;
                total += bytes;
                rows.Add(new ObjRow
                {
                    Name = string.Format("{0} v{1} tri{2}",
                        m.name, m.vertexCount, m.triangles.Length / 3),
                    Bytes = bytes
                });
            }
            Report("Mesh", rows, total, 5);
        }

        private static void DumpClips()
        {
            UnityEngine.Object[] objs =
                Resources.FindObjectsOfTypeAll(typeof(AudioClip));
            var rows = new List<ObjRow>(objs.Length);
            long total = 0;
            var bytesByClip = new Dictionary<AudioClip, long>();
            for (int i = 0; i < objs.Length; i++)
            {
                AudioClip c = objs[i] as AudioClip;
                if (c == null) continue;
                long bytes = (long)c.samples * c.channels * 4;
                total += bytes;
                bytesByClip[c] = bytes;
                rows.Add(new ObjRow
                {
                    Name = string.Format("{0} {1:F1}s ch{2}",
                        c.name, c.length, c.channels),
                    Bytes = bytes
                });
            }
            Report("AudioClip", rows, total, 3);

            // Ownership audit: map each clip to the atoms whose
            // AudioSources reference it, so big clips are attributable.
            var owners = new Dictionary<AudioClip, string>();
            UnityEngine.Object[] srcs =
                Resources.FindObjectsOfTypeAll(typeof(AudioSource));
            for (int i = 0; i < srcs.Length; i++)
            {
                AudioSource src = srcs[i] as AudioSource;
                if (src == null || src.clip == null) continue;
                if (owners.ContainsKey(src.clip)) continue;
                Atom atom = src.GetComponentInParent<Atom>();
                string owner = atom != null
                    ? atom.name + "/" + src.gameObject.name +
                      (src.isPlaying ? " [playing]" : "")
                    : "(no atom)/" + src.gameObject.name +
                      (src.isPlaying ? " [playing]" : "");
                owners[src.clip] = owner;
            }
            long owned = 0;
            var big = new List<ObjRow>();
            foreach (var kv in owners)
            {
                long b;
                if (!bytesByClip.TryGetValue(kv.Key, out b)) continue;
                owned += b;
                if (b >= 8388608) // >=8MB: worth knowing who owns it
                    big.Add(new ObjRow { Name = kv.Key.name + " <- " + kv.Value, Bytes = b });
            }
            big.Sort(delegate(ObjRow a, ObjRow b2) { return b2.Bytes.CompareTo(a.Bytes); });
            Log(string.Format(
                "AudioClip ownership: {0:F2}GB referenced by AudioSources, {1:F2}GB unreferenced",
                owned / 1073741824.0, (total - owned) / 1073741824.0));
            for (int i = 0; i < Math.Min(12, big.Count); i++)
                Log(string.Format("  [big] {0:F1}MB {1}",
                    big[i].Bytes / 1048576.0, Truncate(big[i].Name, 100)));
        }

        private static void DumpCategory<T>(string label, int topN)
            where T : UnityEngine.Object
        {
            UnityEngine.Object[] objs =
                Resources.FindObjectsOfTypeAll(typeof(T));
            Log(string.Format("{0}: {1} objects", label, objs.Length));
        }

        private static void Report(
            string label, List<ObjRow> rows, long total, int topN)
        {
            rows.Sort(delegate(ObjRow a, ObjRow b)
            {
                return b.Bytes.CompareTo(a.Bytes);
            });
            Log(string.Format("{0}: {1} objects, {2:F2}GB (estimated)",
                label, rows.Count, total / 1073741824.0));
            int shown = Math.Min(topN, rows.Count);
            for (int i = 0; i < shown; i++)
            {
                Log(string.Format("  [{0}] {1:F1}MB {2}",
                    i, rows[i].Bytes / 1048576.0,
                    Truncate(rows[i].Name, 100)));
            }
        }

        internal static int FormatBpp(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.DXT1: return 4;
                case TextureFormat.DXT5: return 8;
                case TextureFormat.Alpha8: return 8;
                case TextureFormat.RGB24: return 24;
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32: return 32;
                case TextureFormat.RGB565: return 16;
                default: return 32;
            }
        }

        private static int ColorBpp(RenderTextureFormat f)
        {
            switch (f)
            {
                case RenderTextureFormat.ARGB32: return 4;
                case RenderTextureFormat.ARGBHalf: return 8;
                case RenderTextureFormat.ARGBFloat: return 16;
                case RenderTextureFormat.R8: return 1;
                case RenderTextureFormat.RG16: return 2;
                case RenderTextureFormat.RFloat: return 4;
                case RenderTextureFormat.Depth: return 0;
                default: return 4;
            }
        }

        internal static long TexBytes(int w, int h, int bpp, int mips)
        {
            long bytes = 0;
            int levels = Math.Max(1, mips);
            for (int i = 0; i < levels; i++)
            {
                int lw = Math.Max(1, w >> i);
                int lh = Math.Max(1, h >> i);
                if (bpp < 8)
                {
                    // block-compressed: 4x4 blocks
                    int blocks = ((lw + 3) / 4) * ((lh + 3) / 4);
                    bytes += blocks * (bpp == 4 ? 8 : 16);
                }
                else
                {
                    bytes += (long)lw * lh * bpp / 8;
                }
            }
            return bytes;
        }

        // Finds who holds the orphaned clips: scan every static field of
        // every loaded type for references to AudioClips (direct, array,
        // or dictionary values). Names the owning type+field so big
        // preloaded audio caches are attributable.
        // The 2GB orphan set was held by neither statics nor Unity-object
        // instance fields — so this walk descends recursively through plain
        // C# objects (cache managers, timeline segments, plugin data) too.
        // Bounds: reference-equality visited set, depth 6, 8192 items per
        // enumerable, 15s global budget, scripting/dynamic assemblies
        // skipped, every reflection call individually guarded.
        private sealed class RefEq : System.Collections.Generic.IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
            }
        }

        private sealed class ClipWalk
        {
            internal Dictionary<AudioClip, long> ClipBytes;
            internal readonly Dictionary<string, long> Results =
                new Dictionary<string, long>();
            internal readonly HashSet<object> Visited =
                new HashSet<object>(new RefEq());
            internal readonly System.Diagnostics.Stopwatch Budget =
                new System.Diagnostics.Stopwatch();
            internal string Where = "?";
            internal bool TimedOut;

            internal bool Expired()
            {
                if (Budget.ElapsedMilliseconds <= 15000) return false;
                TimedOut = true;
                return true;
            }

            internal void ScanValue(object val, string label, int depth)
            {
                if (val == null || depth > 6 || TimedOut) return;
                if (Expired()) return;
                AudioClip clip = val as AudioClip;
                if (clip != null)
                {
                    long b;
                    if (ClipBytes.TryGetValue(clip, out b) && b > 0)
                    {
                        long prev;
                        Results.TryGetValue(label, out prev);
                        Results[label] = prev + b;
                    }
                    return;
                }
                if (val is string || val is Type ||
                    val is System.Reflection.MemberInfo ||
                    val is System.Delegate)
                    return;
                Type vt = val.GetType();
                if (vt.IsValueType || vt.IsPointer) return;
                if (!Visited.Add(val)) return;

                var en = val as System.Collections.IEnumerable;
                if (en != null)
                {
                    System.Collections.IEnumerator e;
                    try { e = en.GetEnumerator(); }
                    catch { return; }
                    int seen = 0;
                    while (seen++ < 8192)
                    {
                        object item;
                        try
                        {
                            if (!e.MoveNext()) break;
                            item = e.Current;
                        }
                        catch { break; }
                        if (item is System.Collections.DictionaryEntry)
                        {
                            var de = (System.Collections.DictionaryEntry)item;
                            ScanValue(de.Key, label, depth + 1);
                            ScanValue(de.Value, label, depth + 1);
                            continue;
                        }
                        if (item != null && item.GetType().IsGenericType &&
                            item.GetType().Name.StartsWith("KeyValuePair"))
                        {
                            try
                            {
                                var pv = item.GetType().GetProperty("Value");
                                if (pv != null)
                                    ScanValue(pv.GetValue(item, null),
                                        label, depth + 1);
                            }
                            catch { }
                            continue;
                        }
                        ScanValue(item, label, depth + 1);
                        if (TimedOut) return;
                    }
                    return;
                }

                // Plain object: walk its instance fields.
                System.Reflection.FieldInfo[] fields;
                try
                {
                    fields = vt.GetFields(
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                }
                catch { return; }
                foreach (var f in fields)
                {
                    Type ft;
                    try { ft = f.FieldType; }
                    catch { continue; }
                    if (ft == null || ft.IsPointer || ft.IsPrimitive ||
                        ft.IsEnum || ft == typeof(string) ||
                        ft == typeof(decimal))
                        continue;
                    object fv;
                    try { fv = f.GetValue(val); }
                    catch { continue; }
                    ScanValue(fv, label, depth + 1);
                    if (TimedOut) return;
                }
            }
        }

        private static bool AsmIsPoison(System.Reflection.Assembly a)
        {
            string n;
            try { n = a.GetName().Name; } catch { return true; }
            if (string.IsNullOrEmpty(n)) return true;
            return n.StartsWith("Microsoft.Scripting") ||
                n.StartsWith("IronPython") || n.StartsWith("Anonymously") ||
                n.IndexOf("Dynamic") >= 0;
        }

        internal static void DumpClipHolders()
        {
            var walk = new ClipWalk();
            walk.ClipBytes = new Dictionary<AudioClip, long>();
            walk.Budget.Start();
            try
            {
                foreach (UnityEngine.Object o in
                    Resources.FindObjectsOfTypeAll(typeof(AudioClip)))
                {
                    AudioClip c = o as AudioClip;
                    if (c != null)
                        walk.ClipBytes[c] = (long)c.samples * c.channels * 4;
                }

                // Roots A: static fields of every non-poison assembly.
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm is System.Reflection.Emit.AssemblyBuilder ||
                        AsmIsPoison(asm)) continue;
                    string asmName;
                    try { asmName = asm.GetName().Name; }
                    catch { continue; }
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }
                    foreach (Type t in types)
                    {
                        if (t == null) continue;
                        System.Reflection.FieldInfo[] fields;
                        try
                        {
                            fields = t.GetFields(
                                System.Reflection.BindingFlags.Static |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic);
                        }
                        catch { continue; }
                        foreach (var f in fields)
                        {
                            Type ft;
                            try { ft = f.FieldType; }
                            catch { continue; }
                            if (ft == null || ft.IsPrimitive || ft.IsEnum ||
                                ft.IsPointer || ft == typeof(string))
                                continue;
                            object val;
                            try { val = f.GetValue(null); }
                            catch { continue; }
                            walk.Where = "static " + t.FullName + "." + f.Name;
                            walk.ScanValue(val,
                                "S:" + t.FullName + "." + f.Name, 0);
                            if (walk.TimedOut) break;
                        }
                        if (walk.TimedOut) break;
                    }
                    if (walk.TimedOut) break;
                }

                // Roots B: instance fields of live Unity objects.
                if (!walk.TimedOut)
                {
                    UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(
                        typeof(UnityEngine.Object));
                    for (int i = 0; i < objs.Length; i++)
                    {
                        UnityEngine.Object o = objs[i];
                        if (o == null) continue;
                        Type t = o.GetType();
                        walk.Where = "instance " + t.FullName;
                        System.Reflection.FieldInfo[] fields;
                        try
                        {
                            fields = t.GetFields(
                                System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic);
                        }
                        catch { continue; }
                        foreach (var f in fields)
                        {
                            Type ft;
                            try { ft = f.FieldType; }
                            catch { continue; }
                            if (ft == null || ft.IsPrimitive || ft.IsEnum ||
                                ft.IsPointer || ft == typeof(string))
                                continue;
                            object val;
                            try { val = f.GetValue(o); }
                            catch { continue; }
                            walk.ScanValue(val,
                                "I:" + t.FullName, 0);
                            if (walk.TimedOut) break;
                        }
                        if (walk.TimedOut) break;
                    }
                }

                var rows = new List<KeyValuePair<string, long>>(walk.Results);
                rows.Sort(delegate(KeyValuePair<string, long> a,
                                   KeyValuePair<string, long> b)
                {
                    return b.Value.CompareTo(a.Value);
                });
                Log("clip holders deep (" + rows.Count + " roots, scan " +
                    walk.Budget.ElapsedMilliseconds + "ms" +
                    (walk.TimedOut ? ", TIMED OUT at " + walk.Where : "") + "):");
                for (int i = 0; i < Math.Min(20, rows.Count); i++)
                    Log(string.Format("  {0:F1}MB {1}",
                        rows[i].Value / 1048576.0, rows[i].Key));
            }
            catch (Exception ex)
            {
                Log("clip holder scan failed at " + walk.Where + ": " + ex);
            }
        }

        private static void DumpAtoms()
        {
            try
            {
                if (SuperController.singleton == null) return;
                List<Atom> atoms = SuperController.singleton.GetAtoms();
                if (atoms == null) return;
                var counts = new Dictionary<string, int>();
                for (int i = 0; i < atoms.Count; i++)
                {
                    if (atoms[i] == null) continue;
                    string type = atoms[i].type ?? "?";
                    int n;
                    counts.TryGetValue(type, out n);
                    counts[type] = n + 1;
                }
                var parts = new List<string>();
                foreach (var kv in counts)
                    parts.Add(kv.Key + "=" + kv.Value);
                Log("Atoms: " + atoms.Count +
                    " (" + string.Join(", ", parts.ToArray()) + ")");
            }
            catch (Exception ex)
            {
                Log("atom count failed: " + ex.Message);
            }
        }

        // Lightweight tagged snapshot — one compact line for tracking
        // growth across repeated operations (person preset loads). The
        // heavyweight Dump() stays opt-in via cfg; this one is cheap
        // enough to run automatically. Includes the ImageLoaderThreaded
        // texture-cache census with refcount buckets: cached textures
        // stay referenced by the dictionary, so UnloadUnusedAssets can
        // never reclaim them — dead-bucket growth is the leak signature.
        internal static void Snapshot(string tag)
        {
            try
            {
                MemCounters mc;
                long ws = Environment.WorkingSet, pf = 0;
                if (GetProcessMemoryInfo(
                        Process.GetCurrentProcess().Handle,
                        out mc, (uint)Marshal.SizeOf(typeof(MemCounters))))
                {
                    ws = (long)mc.WorkingSetSize.ToUInt64();
                    pf = (long)mc.PagefileUsage.ToUInt64();
                }
                long gfx = GfxDedicatedBytes();
                if (gfx < 0)
                    try
                    {
                        gfx = UnityEngine.Profiling.Profiler
                            .GetAllocatedMemoryForGraphicsDriver();
                    }
                    catch { gfx = -1; }
                long heap = GC.GetTotalMemory(false);

                int texN = 0, dead = 0, live = 0, untracked = 0;
                long texB = 0, deadB = 0;
                List<ObjRow> top = null;
                try
                {
                    ImageLoaderThreaded loader = ImageLoaderThreaded.singleton;
                    const BindingFlags BF = BindingFlags.Instance |
                        BindingFlags.NonPublic;
                    Type lt = typeof(ImageLoaderThreaded);
                    FieldInfo cacheF = lt.GetField("textureCache", BF);
                    FieldInfo countF = lt.GetField("textureUseCount", BF);
                    var cacheDict = loader != null && cacheF != null
                        ? cacheF.GetValue(loader)
                            as Dictionary<string, Texture2D> : null;
                    var countDict = loader != null && countF != null
                        ? countF.GetValue(loader)
                            as Dictionary<Texture2D, int> : null;
                    if (cacheDict != null)
                    {
                        top = new List<ObjRow>();
                        foreach (KeyValuePair<string, Texture2D> kv
                            in cacheDict)
                        {
                            Texture2D t = kv.Value;
                            if (t == null) continue;
                            texN++;
                            long b = TexBytes(t.width, t.height,
                                FormatBpp(t.format), t.mipmapCount);
                            texB += b;
                            int c = 0;
                            bool tracked = countDict != null &&
                                countDict.TryGetValue(t, out c);
                            if (!tracked) untracked++;
                            else if (c <= 0) { dead++; deadB += b; }
                            else live++;
                            top.Add(new ObjRow
                            {
                                Name = kv.Key +
                                    (tracked ? " x" + c : " x?"),
                                Bytes = b
                            });
                        }
                    }
                }
                catch (Exception texEx)
                {
                    Log("tex census failed: " + texEx.Message);
                }

                Log(string.Format(
                    "snap[{0}] ws={1:F2}GB pagefile={2:F2}GB heap={3:F2}GB gfx={4} texCache={5}({6:F2}GB live={7} dead={8}({9:F2}GB) untracked={10})",
                    tag, ws / 1073741824.0, pf / 1073741824.0,
                    heap / 1073741824.0,
                    gfx < 0 ? "n/a"
                        : (gfx / 1048576.0).ToString("F0") + "MB",
                    texN, texB / 1073741824.0, live, dead,
                    deadB / 1073741824.0, untracked));
                if (top != null && top.Count > 0)
                {
                    top.Sort(delegate(ObjRow a, ObjRow b)
                    { return b.Bytes.CompareTo(a.Bytes); });
                    var sb = new System.Text.StringBuilder();
                    int n = Math.Min(6, top.Count);
                    for (int i = 0; i < n; i++)
                        sb.Append(Truncate(top[i].Name, 40)).Append('=')
                            .Append((top[i].Bytes / 1048576.0)
                                .ToString("F0")).Append("MB; ");
                    Log("snap[" + tag + "] top: " + sb);
                }
            }
            catch (Exception ex)
            {
                Log("snapshot failed: " + ex.Message);
            }
        }

        internal static System.Collections.IEnumerator SnapshotDelayed(
            MonoBehaviour host, string tag, float seconds)
        {
            if (host == null) yield break;
            yield return new WaitForSecondsRealtime(seconds);
            Snapshot(tag);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(unnamed)";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[MemProbe] " + message);
        }
    }
}
