using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Periodic GPU-side census: total Texture VRAM, RenderTexture count,
    // and a ComputeBuffer create/dispose ledger. ComputeBuffers have no
    // enumeration API, so constructors are hooked and tracked through weak
    // references — the probe itself never pins a buffer alive. Buffers freed
    // by GC finalization without an explicit Release still vanish from the
    // ledger, so "live" means managed-alive AND undisposed.
    internal static class GpuResourceProbe
    {
        private const float CensusEvery = 60f;
        private static float _nextCensus;
        private static bool _rootSampleReported;
        private static bool _installTried;
        private static Harmony _harmony;

        private sealed class Buf
        {
            internal WeakReference r;
            internal long bytes;
            internal bool disposed;
        }

        private static readonly List<Buf> Bufs = new List<Buf>();
        private static int _created, _disposed, _gcPurged;

        private const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        internal static void Install()
        {
            if (_installTried) return;
            _installTried = true;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.gpu-resource-probe");
                _harmony.UnpatchAll(_harmony.Id);
                var postfix = new HarmonyMethod(typeof(GpuResourceProbe).GetMethod("AfterCtor", BindingFlags.NonPublic | BindingFlags.Static));
                var release = new HarmonyMethod(typeof(GpuResourceProbe).GetMethod("BeforeDispose", BindingFlags.NonPublic | BindingFlags.Static));
                foreach (ConstructorInfo ctor in typeof(ComputeBuffer).GetConstructors())
                    _harmony.Patch(ctor, postfix: postfix);
                MethodInfo m = typeof(ComputeBuffer).GetMethod("Release", All);
                if (m != null) _harmony.Patch(m, prefix: release);
                m = typeof(ComputeBuffer).GetMethod("Dispose", Type.EmptyTypes);
                if (m != null) _harmony.Patch(m, prefix: release);
                WardrobeJanitor.Log("gpu probe installed: ComputeBuffer ledger + texture/RT census");
            }
            catch (Exception e)
            {
                if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
                _harmony = null;
                WardrobeJanitor.Log("gpu probe not installed: " + e.Message);
            }
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _installTried = false;
            Bufs.Clear();
        }

        private static void AfterCtor(ComputeBuffer __instance)
        {
            try
            {
                _created++;
                Bufs.Add(new Buf { r = new WeakReference(__instance), bytes = (long)__instance.count * __instance.stride });
            }
            catch { }
        }

        private static void BeforeDispose(ComputeBuffer __instance)
        {
            try
            {
                _disposed++;
                for (int i = Bufs.Count - 1; i >= 0; i--)
                {
                    object target = Bufs[i].r.Target;
                    if (target == null) continue;
                    if (ReferenceEquals(target, __instance)) { Bufs[i].disposed = true; return; }
                }
            }
            catch { }
        }

        internal static void Tick()
        {
            Install();
            float now = Time.realtimeSinceStartup;
            if (now < _nextCensus) return;
            _nextCensus = now + CensusEvery;
            try { Census(); } catch (Exception e) { WardrobeJanitor.Log("gpu census failed: " + e.Message); }
        }

        private static void Census()
        {
            int bufLive = 0;
            long bufBytes = 0;
            for (int i = Bufs.Count - 1; i >= 0; i--)
            {
                Buf b = Bufs[i];
                if (!b.r.IsAlive) { Bufs.RemoveAt(i); _gcPurged++; continue; }
                if (b.disposed) continue;
                bufLive++;
                bufBytes += b.bytes;
            }

            int texN = 0, rtN = 0;
            long texBytes = 0, rtBytes = 0;
            // Our own panel thumbnails deliberately share one name and size
            // (Q3PdThumb|512x512), so the dup census below cannot tell "one
            // per slot" from "the same texture blown up N times". Counting
            // them apart keeps the dup line honest about real leaks.
            long uiThumbN = 0, uiThumbBytes = 0;
            var uiThumbMap = new Dictionary<string, int>();
            // Duplicate-name census: reloading the same scene should reuse or
            // replace textures, so identical (name,size) pairs stacking up is
            // the reload-leak signature. Key includes dimensions+format so
            // legitimately different textures sharing a name don't count.
            var dupMap = new Dictionary<string, int>();
            var dupBytes = new Dictionary<string, long>();
            foreach (Texture t in Resources.FindObjectsOfTypeAll<Texture>())
            {
                if (t == null) continue;
                RenderTexture rt = t as RenderTexture;
                if (rt != null) { rtN++; rtBytes += RtBytes(rt); continue; }
                texN++;
                long tb = TexBytes(t);
                texBytes += tb;
                if (t.name != null && t.name.Length > 5 &&
                    t.name.StartsWith("Q3", StringComparison.Ordinal) &&
                    t.name.EndsWith("Thumb", StringComparison.Ordinal))
                {
                    uiThumbN++;
                    uiThumbBytes += tb;
                    string uk = t.name + "|" + t.width + "x" + t.height;
                    int un;
                    uiThumbMap[uk] = uiThumbMap.TryGetValue(uk, out un) ? un + 1 : 1;
                }
                string key = t.name + "|" + t.width + "x" + t.height;
                int n;
                dupMap[key] = dupMap.TryGetValue(key, out n) ? n + 1 : 1;
                if (!dupBytes.ContainsKey(key)) dupBytes[key] = tb;
            }
            int dupGroups = 0, dupExtra = 0;
            long dupExtraBytes = 0;
            string dupTop = "";
            long dupTopBytes = -1;
            foreach (KeyValuePair<string, int> kv in dupMap)
            {
                if (kv.Value < 2) continue;
                dupGroups++;
                dupExtra += kv.Value - 1;
                long eb = dupBytes[kv.Key] * (kv.Value - 1);
                dupExtraBytes += eb;
                if (eb > dupTopBytes)
                {
                    dupTopBytes = eb;
                    dupTop = " \"" + kv.Key + "\"x" + kv.Value;
                }
            }

            // Retained-layer census: Boehm can't walk the heap, so count the
            // Unity-object populations that dominate the post-GC floor. The
            // residual between managedMiB and these estimates is pure-managed
            // retention (JSON graphs, morph deltas, plugin state).
            int meshN = 0, matN = 0, audioN = 0, goN = 0;
            long meshVerts = 0, audioSec = 0;
            foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                if (m == null) continue;
                meshN++;
                try { meshVerts += m.vertexCount; } catch { }
            }
            var matMap = new Dictionary<string, int>();
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null) continue;
                matN++;
                string mk = m.name + "|" + (m.shader != null ? m.shader.name : "?");
                int mn;
                matMap[mk] = matMap.TryGetValue(mk, out mn) ? mn + 1 : 1;
            }
            int matDupG = 0, matDupX = 0;
            string matDupTop = "";
            int matDupTopN = 0;
            foreach (KeyValuePair<string, int> kv in matMap)
            {
                if (kv.Value < 3) continue;
                matDupG++;
                matDupX += kv.Value - 1;
                if (kv.Value > matDupTopN)
                {
                    matDupTopN = kv.Value;
                    matDupTop = " \"" + kv.Key.Split('|')[0] + "\"x" + kv.Value;
                }
            }
            foreach (AudioClip c in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                if (c == null) continue;
                audioN++;
                audioSec += (long)c.length;
            }
            GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            goN = allObjects.Length;
            if (!_rootSampleReported) RootSample(allObjects);

            // Top three of our own thumbnail populations: the cap on decoded
            // dock thumbs is only provable from the size in the name, and the
            // dup line below reports the largest group overall, which after
            // the cap is the browser's.
            string uiTop = "";
            for (int rank = 0; rank < 3; rank++)
            {
                string bestKey = null;
                int bestN = 0;
                foreach (KeyValuePair<string, int> kv in uiThumbMap)
                {
                    if (kv.Value > bestN) { bestN = kv.Value; bestKey = kv.Key; }
                }
                if (bestKey == null || bestN < 2) break;
                uiThumbMap.Remove(bestKey);
                uiTop += " " + bestKey + "x" + bestN;
            }
            WardrobeJanitor.Log("gpu census tex=" + texN + "/" + (texBytes / 1048576) +
                "MiB rt=" + rtN + "/" + (rtBytes / 1048576) +
                "MiB cbuf=" + bufLive + "/" + (bufBytes / 1048576) +
                "MiB(created=" + _created + " disposed=" + _disposed + " gcPurged=" + _gcPurged + ")" +
                " mesh=" + meshN + "/" + (meshVerts / 1000000) + "Mv" +
                " mat=" + matN + "/" + matDupG + "g/+" + matDupX + matDupTop +
                " audio=" + audioN + "/" + audioSec + "s" +
                " dupTex=" + dupGroups + "g/+" + dupExtra + "/" + (dupExtraBytes / 1048576) + "MiB" + dupTop +
                " uiThumb=" + uiThumbN + "/" + (uiThumbBytes / 1048576) + "MiB" + uiTop +
                " go=" + goN + " managedMiB=" + (GC.GetTotalMemory(false) / 1048576) +
                " rssMiB=" + (ProcWorkingSet() / 1048576) +
                " commitMiB=" + (ProcPrivateBytes() / 1048576));
        }

        // Mono's System.Diagnostics.Process is a stub on this runtime —
        // WorkingSet64/PrivateMemorySize64 return 0. Read the real counters
        // straight from psapi instead.
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ProcMem
        {
            internal uint cb;
            internal uint PageFaultCount;
            internal System.UIntPtr PeakWorkingSetSize;
            internal System.UIntPtr WorkingSetSize;
            internal System.UIntPtr QuotaPeakPagedPoolUsage;
            internal System.UIntPtr QuotaPagedPoolUsage;
            internal System.UIntPtr QuotaPeakNonPagedPoolUsage;
            internal System.UIntPtr QuotaNonPagedPoolUsage;
            internal System.UIntPtr PagefileUsage;
            internal System.UIntPtr PeakPagefileUsage;
        }

        [System.Runtime.InteropServices.DllImport("psapi.dll")]
        private static extern bool GetProcessMemoryInfo(
            IntPtr process, out ProcMem counters, uint size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static ProcMem _procMem;
        private static bool _procMemOk;

        private static bool ReadProcMem()
        {
            try
            {
                ProcMem m;
                if (GetProcessMemoryInfo(GetCurrentProcess(), out m,
                        (uint)System.Runtime.InteropServices.Marshal.SizeOf(
                            typeof(ProcMem))))
                {
                    _procMem = m;
                    _procMemOk = true;
                }
            }
            catch { _procMemOk = false; }
            return _procMemOk;
        }

        private static long ProcWorkingSet()
        {
            return ReadProcMem() ? (long)_procMem.WorkingSetSize.ToUInt64() : 0;
        }

        private static long ProcPrivateBytes()
        {
            // PagefileUsage == commit charge (private bytes) for the process.
            return ReadProcMem() ? (long)_procMem.PagefileUsage.ToUInt64() : 0;
        }

        // One bounded attribution sample from the array the existing census
        // already creates. No retained Unity references or hierarchy mutation.
        internal static int RootSampleIndex(int slot, int length)
        {
            int limit = Math.Min(1024, length);
            if (slot < 0 || slot >= limit) return -1;
            return (int)((long)slot * length / limit);
        }

        private static void RootSample(GameObject[] objects)
        {
            _rootSampleReported = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var counts = new Dictionary<int, int>();
            var labels = new Dictionary<int, string>();
            int sampled = 0, sceneObjects = 0, assetObjects = 0, active = 0, errors = 0;
            int limit = Math.Min(1024, objects.Length);
            for (int slot = 0; slot < limit; slot++)
            {
                // Budget checked between native queries, not a hard deadline.
                if (clock.ElapsedMilliseconds >= 25) break;
                GameObject obj = objects[RootSampleIndex(slot, objects.Length)];
                if (obj == null) continue;
                try
                {
                    bool inScene = obj.scene.IsValid();
                    if (inScene) sceneObjects++; else assetObjects++;
                    if (obj.activeInHierarchy) active++;
                    Transform root = obj.transform.root;
                    Transform group = root;
                    string branch = "";
                    if (root.name == "SceneAtoms")
                    {
                        Transform first = null, second = null;
                        Transform node = obj.transform;
                        int depth = 0;
                        while (node != null && node != root && depth++ < 128)
                        {
                            second = first;
                            first = node;
                            node = node.parent;
                        }
                        if (node != root) { errors++; continue; }
                        group = second != null ? second : (first != null ? first : root);
                        if (first != null) branch += "/" + first.name;
                        if (second != null) branch += "/" + second.name;
                    }
                    int id = group.GetInstanceID();
                    int count;
                    counts[id] = counts.TryGetValue(id, out count) ? count + 1 : 1;
                    if (!labels.ContainsKey(id))
                    {
                        string name = (root.name ?? "") + branch;
                        name = name.Replace('\r', ' ').Replace('\n', ' ');
                        if (name.Length > 80) name = name.Substring(0, 80);
                        labels[id] = (inScene ? "scene:" : "asset:") + name;
                    }
                    sampled++;
                }
                catch { errors++; }
            }
            var ranked = new List<KeyValuePair<int, int>>(counts);
            ranked.Sort(delegate(KeyValuePair<int, int> a, KeyValuePair<int, int> b) { return b.Value.CompareTo(a.Value); });
            var top = new System.Text.StringBuilder();
            for (int i = 0; i < ranked.Count && i < 8; i++)
                top.Append(" [").Append(ranked[i].Key).Append(' ').Append(labels[ranked[i].Key]).Append(" sampled=").Append(ranked[i].Value).Append(']');
            WardrobeJanitor.Log("go-root-sample total=" + objects.Length + " sampled=" + sampled +
                " planned=" + limit + " scene=" + sceneObjects + " asset=" + assetObjects + " active=" + active +
                " errors=" + errors + " ms=" + clock.ElapsedMilliseconds + " top=" + top +
                " (evenly spaced sample; not exact root counts/bytes or proof of leaks; no objects destroyed)");
        }

        internal static long TexBytes(Texture t)
        {
            Texture2D t2 = t as Texture2D;
            if (t2 != null)
                return MemoryProbe.TexBytes(t2.width, t2.height, MemoryProbe.FormatBpp(t2.format), t2.mipmapCount);
            Texture3D t3 = t as Texture3D;
            if (t3 != null)
                return MemoryProbe.TexBytes(t3.width, t3.height * t3.depth, MemoryProbe.FormatBpp(t3.format), 1);
            Cubemap cube = t as Cubemap;
            if (cube != null)
                return MemoryProbe.TexBytes(cube.width * 6, cube.height, MemoryProbe.FormatBpp(cube.format), cube.mipmapCount);
            return (long)t.width * t.height * 4;
        }

        internal static long RtBytes(RenderTexture rt)
        {
            int aa = rt.antiAliasing > 0 ? rt.antiAliasing : 1;
            int depth = rt.volumeDepth > 0 ? rt.volumeDepth : 1;
            return (long)rt.width * rt.height * depth * aa * RtBpp(rt.format);
        }

        private static int RtBpp(RenderTextureFormat f)
        {
            switch (f)
            {
                case RenderTextureFormat.R8:
                    return 1;
                case RenderTextureFormat.RG16:
                case RenderTextureFormat.RHalf:
                case RenderTextureFormat.ARGB4444:
                case RenderTextureFormat.ARGB1555:
                case RenderTextureFormat.RGB565:
                    return 2;
                case RenderTextureFormat.ARGB32:
                case RenderTextureFormat.ARGB2101010:
                case RenderTextureFormat.RGB111110Float:
                case RenderTextureFormat.RGHalf:
                case RenderTextureFormat.RFloat:
                case RenderTextureFormat.RInt:
                case RenderTextureFormat.RG32:
                case RenderTextureFormat.Depth:
                case RenderTextureFormat.Shadowmap:
                    return 4;
                case RenderTextureFormat.ARGB64:
                case RenderTextureFormat.ARGBHalf:
                case RenderTextureFormat.RGFloat:
                    return 8;
                case RenderTextureFormat.ARGBFloat:
                    return 16;
                default:
                    return 4;
            }
        }
    }
}
