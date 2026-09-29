using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Quest3TriggerUI
{
    // One-shot coverage probe for the "point-destroy dead assets, demote UUA to
    // a rare backstop" idea. The two numbers that decide go/no-go:
    //
    //   coverage   = dying subtree assets that are dead-and-exclusive
    //                (safe to Destroy) vs still shared with live renderers
    //                (Destroy would pink them).
    //   debtShare  = how much of a swap's UUA debt sits on renderer-attached
    //                materials/meshes at all - if native teardown already
    //                destroyed them or the debt lives in MaterialOptions/dicts/
    //                VAR residue, the diff set is empty and the idea is dead.
    //
    // Measurement only: captures candidates at UnloadInstance, then two frames
    // later censuses every live scene renderer's sharedMaterials/sharedMesh.
    // Candidates referenced by a live renderer = shared; absent = exclusively
    // dead; Unity-null = native already destroyed them. Renderer-census is a
    // lower bound on "shared": MaterialOptions fields, dictionaries and
    // plugin-held refs are NOT visible here, so reported exclusivity can still
    // over-estimate safety.
    internal static class UnloadCoverageProbe
    {
        internal static ConfigEntry<bool> Once;

        private const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static Harmony _harmony;
        private static bool _tried;
        private static bool _armed;
        private static FieldInfo _instance;
        private static readonly HashSet<UnityEngine.Object> _candidates =
            new HashSet<UnityEngine.Object>();
        // Materials collected during capture, so census can classify their
        // texture references as candidates too (renderer mesh/materials alone
        // are near-zero bytes; the real UUA debt is textures).
        private static readonly HashSet<Material> _candidateMats =
            new HashSet<Material>();
        private static int _censusFrame = -1;
        private static int _capturedItems;
        private static string _firstItem;

        internal static void Install()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                _instance = typeof(JSONStorableDynamic).GetField("instance", All);
                if (_instance == null) throw new MissingFieldException("JSONStorableDynamic.instance");
                _harmony = new Harmony("Quest3TriggerUI.unload-coverage-probe");
                _harmony.Patch(typeof(JSONStorableDynamic).GetMethod("UnloadInstance", All),
                    prefix: new HarmonyMethod(typeof(UnloadCoverageProbe).GetMethod(
                        "BeforeUnload", All)));
                Log("installed; set UnloadCoverageProbeOnce=true to arm");
            }
            catch (Exception e) { Log("not installed: " + e.Message); }
        }

        // Armed by the cfg text watcher (CheckMemorySnapshotFlag): live-editing
        // the file does not reach ConfigEntry.Value in this process.
        internal static void Arm()
        {
            _armed = true;
            Log("armed; next UnloadInstance burst is captured");
        }

        private static void BeforeUnload(JSONStorableDynamic __instance)
        {
            bool armed = _armed;
            // While a census is pending, sibling unloads in the same teardown
            // window still count - one swap's coverage is the evidence unit.
            if (!armed && _censusFrame < 0) return;
            try
            {
                Transform root = _instance.GetValue(__instance) as Transform;
                if (root == null) return;
                if (armed)
                {
                    _armed = false;
                    if (Once != null) Once.Value = false;
                }
                if (_firstItem == null) _firstItem = root.name;
                _capturedItems++;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    foreach (Material m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        _candidates.Add(m);
                        _candidateMats.Add(m);
                    }
                    var smr = r as SkinnedMeshRenderer;
                    if (smr != null && smr.sharedMesh != null) _candidates.Add(smr.sharedMesh);
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) _candidates.Add(mf.sharedMesh);
                }
                // Textures ride on materials: expand candidates to every
                // texture slot so the byte totals reflect the real debt.
                foreach (Material m in _candidateMats)
                    if (m != null) AddTextures(m, _candidates);
                // Slide to two frames after the LAST unload of the burst so
                // the destruction flush has actually run before the census.
                _censusFrame = Time.frameCount + 2;
            }
            catch (Exception e) { Log("capture failed: " + e.Message); }
        }

        internal static void Tick()
        {
            if (_censusFrame < 0 || Time.frameCount < _censusFrame) return;
            _censusFrame = -1;
            RunCensus();
        }

        // Unity 2018 runtime cannot enumerate material texture slots
        // (GetTexturePropertyNames is 2019+; ShaderUtil is editor-only), so
        // probe a fixed slot list covering VaM skin/hair/cloth shaders.
        private static readonly string[] TexSlots = {
            "_MainTex", "_BumpMap", "_SpecTex", "_GlossTex", "_SpecGlossMap",
            "_AlphaTex", "_DecalTex", "_DetailTex", "_DetailNormalMap",
            "_EmissionMap", "_MetallicGlossMap", "_OcclusionMap",
            "_ParallaxMap", "_CutTex", "_AlphaMask", "_DiffuseTex",
            "_SpecularTex", "_GlossinessTex", "_NormalMap", "_MaskTex",
            "_DetailMask", "_BumpTex", "_LipTex", "_Mask"
        };

        private static void AddTextures(Material m, HashSet<UnityEngine.Object> into)
        {
            foreach (string pn in TexSlots)
            {
                try
                {
                    if (!m.HasProperty(pn)) continue;
                    Texture t = m.GetTexture(pn);
                    if (t != null) into.Add(t);
                }
                catch { }
            }
        }

        private static void Collect(Renderer r, HashSet<UnityEngine.Object> into)
        {
            foreach (Material m in r.sharedMaterials)
            {
                if (m == null) continue;
                into.Add(m);
                AddTextures(m, into);
            }
            var smr = r as SkinnedMeshRenderer;
            if (smr != null && smr.sharedMesh != null) into.Add(smr.sharedMesh);
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) into.Add(mf.sharedMesh);
        }

        private static void RunCensus()
        {
            try
            {
                var referenced = new HashSet<UnityEngine.Object>();
                foreach (Renderer r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (r == null) continue;
                    GameObject go = r.gameObject;
                    if (go == null || !go.scene.IsValid()) continue;
                    Collect(r, referenced);
                }
                int dead = 0, exclusive = 0, shared = 0, mats = 0, meshes = 0, texs = 0;
                long exBytes = 0, shBytes = 0;
                var sharedNames = new List<string>();
                var exNames = new List<string>();
                foreach (UnityEngine.Object c in _candidates)
                {
                    bool isMesh = c is Mesh;
                    if (c is Material) mats++; else if (isMesh) meshes++;
                    else if (c is Texture) texs++;
                    if (c == null) { dead++; continue; }
                    long bytes;
                    try { bytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(c); }
                    catch { bytes = 0; }
                    if (referenced.Contains(c))
                    {
                        shared++;
                        shBytes += bytes;
                        if (sharedNames.Count < 8) sharedNames.Add(c.name + "(" + c.GetType().Name + ")");
                    }
                    else
                    {
                        exclusive++;
                        exBytes += bytes;
                        if (exNames.Count < 8) exNames.Add(c.name + "(" + c.GetType().Name + ")");
                    }
                }
                Log("items=" + _capturedItems + " first=" + _firstItem +
                    " candidates=" + _candidates.Count + " (mat=" + mats + " mesh=" + meshes +
                    " tex=" + texs + ") teardownDead=" + dead +
                    " exclusive=" + exclusive + "(~" + exBytes / 1048576 + "MiB)" +
                    " shared=" + shared + "(~" + shBytes / 1048576 + "MiB)" +
                    " sharedSample=[" + string.Join(",", sharedNames.ToArray()) + "]" +
                    " exclSample=[" + string.Join(",", exNames.ToArray()) + "]" +
                    " — renderer census only; MaterialOptions/dict/plugin refs invisible, so 'exclusive' is an upper bound on safety");
            }
            catch (Exception e) { Log("census failed: " + e.Message); }
            finally
            {
                _candidates.Clear();
                _candidateMats.Clear();
                _capturedItems = 0;
                _firstItem = null;
            }
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[unload-coverage] " + message);
        }
    }
}
