using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Measurement only. UUA's price is a full-heap mark over every loaded
    // object (8-21s on this machine, unloaded=0..5), so the question that
    // decides whether the swap path can stop calling it is not how long it
    // takes but WHAT it reclaims. UnloadCoverageProbe answered that for a
    // dying subtree's renderer-attached materials/meshes (near zero) and the
    // route was shelved; this probe measures the sweep itself: one census
    // immediately before Resources.UnloadUnusedAssets, one right after its
    // AsyncOperation reports done, then the delta.
    //
    // Byte columns: this player build has no profiler, so
    // Profiler.GetRuntimeMemorySizeLong / GetMonoUsedSizeLong /
    // GetAllocatedMemoryForGraphicsDriver all return 0 (the first version of
    // this probe logged zero byte sums because of it). Bytes are therefore
    // derived from each object's own geometry - GpuResourceProbe.TexBytes
    // (width/height/format/mips) and RtBytes - and VRAM from
    // MemoryProbe.GfxDedicatedBytes (PDH GPU Process Memory), which is what
    // tells us whether a sweep that frees almost no objects is nevertheless
    // the thing releasing video memory.
    //
    // GameObject/Component stay behind UuaTypeCensusHeavy: SceneOrphanSweep
    // measured those legs at 4.7s/16.1s including per-object analysis, and
    // censusMs reports what the bare count actually costs here.
    internal static class UuaTypeCensus
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Heavy;

        private const float TimeoutSeconds = 120f;
        // A sweep landing within seconds of the one just measured would pay a
        // second pair of censuses (and a second PDH read) for the same object
        // population; the swap path settles ~5-9s per preset, so a floor this
        // size still measures every swap and only skips loop-issued sweeps.
        private const float MinIntervalSeconds = 6f;
        private const BindingFlags All =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Harmony _harmony;
        private static bool _tried;

        private struct Reading
        {
            internal long Alloc, Reserved, Vram, Managed;
            internal int Mat, Mesh, Tex, Rt, Go, Cmp;
            internal long MeshV, TexB, RtB;
            internal long Ms;
        }

        private static Reading _before;
        private static float _callAt = -1f;
        private static int _callFrame = -1;
        private static AsyncOperation _op;
        private static bool _pending;
        private static long _calls;
        private static long _skipped;
        private static float _lastAt = -1f;
        private static string _who = "-";
        private static string _chain = "";
        // Identity maps for the reconciliation below: filled by Read() in the
        // same loops it already walks, kept only for the duration of one sweep.
        private static Dictionary<int, InstanceAssetLedger.AssetRef> _ids;
        private static Dictionary<int, InstanceAssetLedger.AssetRef> _beforeIds;
        private static Dictionary<int, InstanceAssetLedger.AssetRef> _afterIds;

        internal static void Install()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.uua-type-census");
                MethodInfo target = typeof(Resources).GetMethod("UnloadUnusedAssets", new Type[0]);
                if (target == null) throw new MissingMethodException("Resources.UnloadUnusedAssets");
                _harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(UuaTypeCensus).GetMethod("BeforeSweep", All)),
                    postfix: new HarmonyMethod(typeof(UuaTypeCensus).GetMethod("AfterSweep", All)));
                Log("installed on Resources.UnloadUnusedAssets; measurement only");
            }
            catch (Exception e)
            {
                if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
                _harmony = null;
                Log("not installed: " + e.Message);
            }
        }

        private static bool BeforeSweep(ref AsyncOperation __result)
        {
            if (Enabled == null || !Enabled.Value) return true;
            // A second sweep landing inside an unsettled one would turn the
            // delta into two sweeps minus one census; count it and keep the
            // first pair clean.
            if (_pending) { _skipped++; return true; }
            Classify();
            // The gate is consulted before the census interval, and a demoted
            // sweep no longer stamps _lastAt: the gate is about whether the
            // engine may spend a full mark at all, while the interval only
            // decides whether this call earns a clean measurement pair. In the
            // other order a demoted preset sweep armed the six-second window
            // and the janitor's sweep two seconds later slipped through
            // unmeasured and un-demoted.
            if (UuaGate.ShouldSkip(_who, _chain))
            {
                __result = UuaGate.OnSkipped(_who);
                return false;
            }
            float now = Time.realtimeSinceStartup;
            if (_lastAt >= 0f && now - _lastAt < MinIntervalSeconds) { _skipped++; return true; }
            _lastAt = now;
            _before = Read();
            _beforeIds = _ids;
            _callAt = Time.realtimeSinceStartup;
            _callFrame = Time.frameCount;
            _op = null;
            _pending = true;
            return true;
        }

        private static void AfterSweep(AsyncOperation __result)
        {
            // Only a real sweep can mint the operation the gate hands back.
            UuaGate.NoteSweep(__result);
            if (!_pending) return;
            _op = __result;
        }

        internal static void Tick()
        {
            if (!_pending) return;
            if (_callFrame < 0 || Time.frameCount < _callFrame + 2) return;
            bool done = _op == null || _op.isDone;
            float waited = Time.realtimeSinceStartup - _callAt;
            if (!done && waited < TimeoutSeconds) return;
            _pending = false;
            _calls++;
            Reading after;
            try { after = Read(); _afterIds = _ids; }
            catch (Exception e)
            {
                Log("call#" + _calls + " after census failed: " + e.Message);
                return;
            }
            Log("call#" + _calls + " waitMs=" + (long)(waited * 1000f) +
                " frames=" + (Time.frameCount - _callFrame) +
                (done ? "" : " TIMEOUT") +
                " censusMs=" + _before.Ms + "/" + after.Ms +
                " skipped=" + _skipped + " gateSkipped=" + UuaGate.SkippedCount +
                " who=" + _who + " chain=" + _chain +
                " alloc=" + MiB(_before.Alloc) + "->" + MiB(after.Alloc) + Delta(after.Alloc - _before.Alloc) +
                "MiB reserved=" + MiB(_before.Reserved) + "->" + MiB(after.Reserved) + Delta(after.Reserved - _before.Reserved) +
                "MiB vram=" + Vram(_before.Vram) + "->" + Vram(after.Vram) +
                (_before.Vram >= 0 && after.Vram >= 0 ? Delta(after.Vram - _before.Vram) + "MiB" : "") +
                " managed=" + MiB(_before.Managed) + "->" + MiB(after.Managed) + Delta(after.Managed - _before.Managed) +
                "MiB" +
                " mat=" + _before.Mat + "->" + after.Mat + "(" + (after.Mat - _before.Mat) + ")" +
                " mesh=" + _before.Mesh + "->" + after.Mesh + "(" + (after.Mesh - _before.Mesh) + "," +
                _before.MeshV / 1000 + "->" + after.MeshV / 1000 + "k verts)" +
                " tex2d=" + _before.Tex + "->" + after.Tex + "(" + (after.Tex - _before.Tex) + "," +
                MiB(_before.TexB) + "->" + MiB(after.TexB) + Delta(after.TexB - _before.TexB) + "MiB)" +
                " rt=" + _before.Rt + "->" + after.Rt + "(" + (after.Rt - _before.Rt) + "," +
                MiB(_before.RtB) + "->" + MiB(after.RtB) + Delta(after.RtB - _before.RtB) + "MiB)" +
                (Heavy != null && Heavy.Value
                    ? " go=" + _before.Go + "->" + after.Go + "(" + (after.Go - _before.Go) + ")" +
                      " comp=" + _before.Cmp + "->" + after.Cmp + "(" + (after.Cmp - _before.Cmp) + ")"
                    : " go/comp=off"));
            // What the sweep actually took, by identity, and how much of it the
            // shadow ledger had already seen leave the scene with a dying item.
            try { InstanceAssetLedger.NoteSweepDiff(_beforeIds, _afterIds); }
            catch (Exception e) { Log("reconcile failed: " + e.Message); }
            _beforeIds = null;
            _afterIds = null;
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _tried = false;
            _pending = false;
            _op = null;
            _callFrame = -1;
            _lastAt = -1f;
            _calls = 0;
            _skipped = 0;
            UuaGate.Shutdown();
        }

        // One snapshot of every cheap class plus the allocator totals. Every
        // per-object read is an icall or a struct read, so the whole thing is
        // timed and the cost is reported next to the delta it produced.
        private static Reading Read()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new Reading();
            try { r.Alloc = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(); }
            catch { }
            try { r.Reserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(); }
            catch { }
            try { r.Managed = GC.GetTotalMemory(false); }
            catch { }
            try { r.Vram = MemoryProbe.GfxDedicatedBytes(); }
            catch { r.Vram = -1; }

            var ids = new Dictionary<int, InstanceAssetLedger.AssetRef>(4096);
            _ids = ids;
            Material[] mats = Resources.FindObjectsOfTypeAll<Material>();
            r.Mat = mats == null ? 0 : mats.Length;
            if (mats != null)
                for (int i = 0; i < mats.Length; i++)
                {
                    Material material = mats[i];
                    if (material == null) continue;
                    // This build's Profiler.GetRuntimeMemorySizeLong returns 0
                    // (see the header), so a material carries no byte estimate.
                    ids[material.GetInstanceID()] = Ref(material.GetInstanceID(),
                        material.name, 0L, 0);
                }

            Mesh[] meshes = Resources.FindObjectsOfTypeAll<Mesh>();
            if (meshes != null)
            {
                r.Mesh = meshes.Length;
                for (int i = 0; i < meshes.Length; i++)
                {
                    Mesh mesh = meshes[i];
                    if (mesh == null) continue;
                    int verts = 0;
                    try { verts = mesh.vertexCount; } catch { }
                    r.MeshV += verts;
                    // Geometry-derived, like every other byte column here: 28B
                    // is the position-normal-uv scale of one vertex, so this is
                    // a ranking estimate, not an allocation size.
                    ids[mesh.GetInstanceID()] = Ref(mesh.GetInstanceID(),
                        mesh.name, verts * 28L, 1);
                }
            }

            Texture2D[] textures = Resources.FindObjectsOfTypeAll<Texture2D>();
            if (textures != null)
            {
                r.Tex = textures.Length;
                for (int i = 0; i < textures.Length; i++)
                {
                    if (textures[i] == null) continue;
                    long texB = 0L;
                    try { texB = GpuResourceProbe.TexBytes(textures[i]); }
                    catch { }
                    r.TexB += texB;
                    ids[textures[i].GetInstanceID()] = Ref(
                        textures[i].GetInstanceID(), textures[i].name, texB, 2);
                }
            }

            RenderTexture[] targets = Resources.FindObjectsOfTypeAll<RenderTexture>();
            if (targets != null)
            {
                r.Rt = targets.Length;
                for (int i = 0; i < targets.Length; i++)
                {
                    if (targets[i] == null) continue;
                    long rtB = 0L;
                    try { rtB = GpuResourceProbe.RtBytes(targets[i]); }
                    catch { }
                    r.RtB += rtB;
                    ids[targets[i].GetInstanceID()] = Ref(
                        targets[i].GetInstanceID(), targets[i].name, rtB, 3);
                }
            }

            if (Heavy != null && Heavy.Value)
            {
                GameObject[] objects = Resources.FindObjectsOfTypeAll<GameObject>();
                r.Go = objects == null ? 0 : objects.Length;
                Component[] components = Resources.FindObjectsOfTypeAll<Component>();
                r.Cmp = components == null ? 0 : components.Length;
            }

            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        private static InstanceAssetLedger.AssetRef Ref(int id, string name,
            long bytes, int kind)
        {
            var a = new InstanceAssetLedger.AssetRef();
            a.Id = id;
            a.Name = name == null ? "?" : name;
            a.Bytes = bytes;
            a.Kind = kind;
            return a;
        }

        // Who issued this sweep? PresetSweepGate exposes no in-window state
        // and the module must not touch its source, so read the caller off
        // the stack instead. One UUA per swap, so the walk is paid once per
        // swap, not per frame. Frames are filtered to this assembly and VaM's;
        // Harmony/system frames carry no useful identity.
        private static void Classify()
        {
            _who = "other";
            _chain = "";
            try
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                var chain = new System.Text.StringBuilder();
                int taken = 0;
                for (int i = 0; i < st.FrameCount && taken < 4; i++)
                {
                    System.Diagnostics.StackFrame frame = st.GetFrame(i);
                    if (frame == null) continue;
                    System.Reflection.MethodBase method = frame.GetMethod();
                    if (method == null) continue;
                    Type type = method.DeclaringType;
                    if (type == null) continue;
                    string full = type.FullName;
                    if (full == null) continue;
                    if (full.StartsWith("HarmonyLib", StringComparison.Ordinal) ||
                        full.StartsWith("System.", StringComparison.Ordinal) ||
                        full.StartsWith("Mono.", StringComparison.Ordinal) ||
                        type.Name == "UuaTypeCensus")
                        continue;
                    // Harmony's detours show up as DMD<...> wrappers whose
                    // declaring type is the patched type; they name no caller,
                    // so skipping them is what makes who= read as a real frame.
                    if (method.Name.StartsWith("DMD<", StringComparison.Ordinal) ||
                        method.Name.IndexOf("<DMD<", StringComparison.Ordinal) >= 0 ||
                        type.Name.IndexOf("DynamicMethodDefinition", StringComparison.Ordinal) >= 0)
                        continue;
                    string name = type.Name + "." + method.Name;
                    if (name.Length > 44) name = name.Substring(0, 44);
                    if (taken == 0) _who = name;
                    if (chain.Length > 0) chain.Append('>');
                    chain.Append(name);
                    taken++;
                }
                _chain = chain.ToString();
            }
            catch (Exception e)
            {
                _who = "stack failed";
                _chain = e.Message;
            }
        }

        private static long MiB(long bytes) { return bytes / 1048576L; }

        private static string Vram(long bytes)
        {
            return bytes < 0 ? "n/a" : MiB(bytes).ToString();
        }

        private static string Delta(long bytes)
        {
            return "(" + (bytes >= 0 ? "+" : "") + (bytes / 1048576L) + ")";
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[uua-census] " + message);
        }
    }
}