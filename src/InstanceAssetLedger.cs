using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Scope-based ownership for the Unity objects a clothing/hair item leaves
    // behind. Destroying the instance is cheap; reclaiming the materials and
    // meshes it referenced is what Unity's asset cleanup (UUA) charges a full
    // heap mark for, and the janitor never asked for any of it. This records
    // what a dying instance referenced and then releases only what no live
    // consumer references any more.
    //
    // Registration is pure bookkeeping: every read goes through the
    // non-instantiating shared* accessors, so it cannot create the very clones
    // it exists to track. Registration alone changes nothing.
    //
    // Release is deliberately narrow. A material is destroyed only when Unity
    // itself named it "(Clone)" - the runtime-instance convention - and no live
    // renderer, graphic or skinned mesh still references it. Meshes and
    // bundle-loaded assets have no such marker: they are counted and logged,
    // never destroyed, because Destroy() on a package asset is how a character
    // turns pink or loses hair. Turn the destroy switches on only after the log
    // line shows what the candidates actually are.
    internal static class InstanceAssetLedger
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> DestroyMaterials;
        internal static ConfigEntry<bool> DestroyMeshes;
        internal static ConfigEntry<float> SettleSeconds;

        private const BindingFlags All =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo InstanceField =
            typeof(JSONStorableDynamic).GetField("instance", All);

        // Entries wait for the native teardown to finish before anything is
        // read again, and are dropped rather than held if they never come due:
        // a ledger entry is a strong reference, which is exactly what would
        // keep UUA from reclaiming the very objects it describes.
        private static readonly List<Entry> Queued = new List<Entry>(8);
        private const int MaxQueued = 16;
        private const float EntryLifeSeconds = 120f;

        // Liveness census: rebuilt at most once per interval, and only when an
        // entry is actually due. Measured legs on a loaded scene: renderers 1ms,
        // graphics 405ms, materials 97ms - the 4.7s/16.1s legs (MonoBehaviour,
        // GameObject) are deliberately not part of this.
        private static readonly HashSet<int> Live = new HashSet<int>();
        private static float _censusAt = -1f;
        private const float CensusInterval = 10f;

        private static bool _warnedNoField;

        private sealed class Entry
        {
            internal string Item;
            internal float At;
            internal float Due;
            internal readonly HashSet<Material> Materials = new HashSet<Material>();
            internal readonly HashSet<Mesh> Meshes = new HashSet<Mesh>();
            internal Watch Pending;
        }

        // ---- sweep reconciliation (phase B/1) ------------------------------
        // The question this phase exists to answer: when a real
        // Resources.UnloadUnusedAssets finishes, WHAT did it take, and was any
        // of it already known to this ledger? UuaTypeCensus already enumerates
        // Material/Mesh/Texture2D/RenderTexture around every sweep; with an id
        // map built in those same loops the reclaimed set falls out as set
        // arithmetic, so the answer costs no extra heap walk.
        internal sealed class AssetRef
        {
            internal int Id;
            internal string Name;
            internal long Bytes;
            internal int Kind;   // 0 material, 1 mesh, 2 texture2d, 3 rendertexture
        }

        // The watch set is deliberately id-only. An Entry holds strong
        // references (Release needs them) and is dropped ~2s after the unload,
        // i.e. seconds before the sweep - but a strong reference is a root, and
        // a root stops the sweep from reclaiming exactly what is being
        // measured. Ids cost nothing and cannot veto anything.
        private sealed class Watch
        {
            internal const int MaxIds = 600;
            internal string Item;
            internal float At;
            internal readonly List<int> Ids = new List<int>(64);
        }

        private static readonly List<Watch> Watching = new List<Watch>(4);
        private const int MaxWatching = 8;
        private const float WatchLifeSeconds = 60f;
        private static int _sweepNo;
        private static long _sweepTaken, _sweepHit, _sweepMiss, _sweepMissBytes;

        // Called while the instance still exists. Returns null when there is
        // nothing to track, so callers can pass the result straight to
        // Queue/Discard without branching on eligibility.
        internal static object Capture(JSONStorableDynamic item)
        {
            if (Enabled == null || !Enabled.Value || item == null) return null;
            if (InstanceField == null)
            {
                if (!_warnedNoField)
                {
                    _warnedNoField = true;
                    Log("installed without instance field; registration disabled");
                }
                return null;
            }
            try
            {
                Transform root = InstanceField.GetValue(item) as Transform;
                if (root == null) return null;
                var entry = new Entry();
                entry.Item = Label(item, root);
                entry.At = Time.realtimeSinceStartup;
                entry.Due = entry.At +
                    (SettleSeconds != null ? Mathf.Max(0f, SettleSeconds.Value) : 2f);
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers != null)
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        Renderer renderer = renderers[i];
                        if (renderer == null) continue;
                        // sharedMaterials never instantiates; the .material
                        // accessor would create the clone we are tracking.
                        Material[] shared = renderer.sharedMaterials;
                        if (shared != null)
                            for (int m = 0; m < shared.Length; m++) Add(entry.Materials, shared[m]);
                        SkinnedMeshRenderer skin = renderer as SkinnedMeshRenderer;
                        if (skin != null) Add(entry.Meshes, skin.sharedMesh);
                    }
                MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
                if (filters != null)
                    for (int i = 0; i < filters.Length; i++)
                        if (filters[i] != null) Add(entry.Meshes, filters[i].sharedMesh);
                entry.Pending = WatchOf(entry);
                return entry;
            }
            catch (Exception e)
            {
                Log("capture failed: " + e.Message);
                return null;
            }
        }

        private static Watch WatchOf(Entry entry)
        {
            var watch = new Watch();
            watch.Item = entry.Item;
            watch.At = entry.At;
            foreach (Material material in entry.Materials)
            {
                if (watch.Ids.Count >= Watch.MaxIds) break;
                watch.Ids.Add(material.GetInstanceID());
            }
            foreach (Mesh mesh in entry.Meshes)
            {
                if (watch.Ids.Count >= Watch.MaxIds) break;
                watch.Ids.Add(mesh.GetInstanceID());
            }
            return watch;
        }

        internal static void Queue(object handle)
        {
            Entry entry = handle as Entry;
            if (entry == null) return;
            if (Queued.Count >= MaxQueued) Queued.RemoveAt(0);
            Queued.Add(entry);
            if (entry.Pending != null)
            {
                if (Watching.Count >= MaxWatching) Watching.RemoveAt(0);
                Watching.Add(entry.Pending);
                entry.Pending = null;
            }
        }

        private static string Tag(AssetRef a)
        {
            string kind = a.Kind == 0 ? "m" : (a.Kind == 1 ? "M" : (a.Kind == 2 ? "t" : "r"));
            return a.Name + "[" + kind + (a.Bytes > 0 ? "," + (a.Bytes / 1024) + "KB" : "") + "]";
        }

        // Called by UuaTypeCensus once a sweep has reported done, with the id
        // maps it built immediately before and immediately after. "Taken" is
        // every object present before and gone after (the sweep, plus anything
        // else freed in those two frames). "Hit" is the part of that set this
        // ledger had already seen leave the scene with a dying item - the
        // coverage number. The miss list is the point of the phase: it names
        // the assets a targeted release would have to own.
        internal static void NoteSweepDiff(Dictionary<int, AssetRef> before,
            Dictionary<int, AssetRef> after)
        {
            if (before == null || after == null || before.Count == 0) return;
            _sweepNo++;
            float now = Time.realtimeSinceStartup;
            var watchIds = new HashSet<int>();
            int watches = 0;
            for (int w = 0; w < Watching.Count; w++)
            {
                Watch watch = Watching[w];
                if (now - watch.At > WatchLifeSeconds) continue;
                watches++;
                for (int i = 0; i < watch.Ids.Count; i++) watchIds.Add(watch.Ids[i]);
            }
            var taken = new List<AssetRef>(64);
            foreach (KeyValuePair<int, AssetRef> kv in before)
                if (!after.ContainsKey(kv.Key)) taken.Add(kv.Value);
            int created = 0;
            foreach (KeyValuePair<int, AssetRef> kv in after)
                if (!before.ContainsKey(kv.Key)) created++;
            long[] kindIds = new long[4];
            long[] kindBytes = new long[4];
            long[] hitIds = new long[4];
            long[] hitBytes = new long[4];
            var hitNames = new List<string>(6);
            for (int i = 0; i < taken.Count; i++)
            {
                AssetRef a = taken[i];
                int k = a.Kind;
                if (k < 0 || k > 3) k = 0;
                kindIds[k]++;
                kindBytes[k] += a.Bytes;
                if (watchIds.Contains(a.Id))
                {
                    hitIds[k]++;
                    hitBytes[k] += a.Bytes;
                    if (hitNames.Count < 6) hitNames.Add(a.Name);
                }
            }
            long ids = 0, bytes = 0, hits = 0, hitB = 0;
            for (int k = 0; k < 4; k++)
            {
                ids += kindIds[k];
                bytes += kindBytes[k];
                hits += hitIds[k];
                hitB += hitBytes[k];
            }
            var miss = new List<AssetRef>(64);
            for (int i = 0; i < taken.Count; i++)
                if (!watchIds.Contains(taken[i].Id)) miss.Add(taken[i]);
            miss.Sort(delegate(AssetRef x, AssetRef y) { return y.Bytes.CompareTo(x.Bytes); });
            var missNames = new List<string>(6);
            int shown = 0;
            long missBytes = bytes - hitB;
            // Largest first, then whatever is left by count: in this build
            // materials carry no byte estimate at all (Profiler returns 0), and
            // a purely byte-ordered list would hide every one of them behind
            // the meshes. Kind letters: m material, M mesh, t texture2d, r rt.
            for (int i = 0; i < miss.Count && shown < 6; i++)
            {
                if (miss[i].Bytes <= 0) continue;
                shown++;
                missNames.Add(Tag(miss[i]));
            }
            for (int i = 0; i < miss.Count && shown < 6; i++)
            {
                if (miss[i].Bytes > 0) continue;
                shown++;
                missNames.Add(Tag(miss[i]));
            }
            _sweepTaken += ids;
            _sweepHit += hits;
            _sweepMiss += ids - hits;
            _sweepMissBytes += missBytes;
            Log("sweep reconcile #" + _sweepNo + ": taken=" + ids +
                "(~" + (bytes / 1024) + "KB) [mat=" + kindIds[0] + " mesh=" + kindIds[1] +
                "/" + (kindBytes[1] / 1024) + "KB tex=" + kindIds[2] + "/" + (kindBytes[2] / 1024) +
                "KB rt=" + kindIds[3] + "/" + (kindBytes[3] / 1024) + "KB]" +
                " created=" + created +
                " ledgerHit=" + hits + "(~" + (hitB / 1024) + "KB) ledgerMiss=" + (ids - hits) +
                "(~" + (missBytes / 1024) + "KB) watches=" + watches +
                "/" + watchIds.Count + "ids" +
                Samples("hit", hitNames) + Samples("missTop", missNames) +
                " [total: sweeps=" + _sweepNo + " taken=" + _sweepTaken + " hit=" + _sweepHit +
                " (" + (_sweepTaken > 0 ? _sweepHit * 100 / _sweepTaken : 0) + "%) miss=" +
                _sweepMiss + " ~" + (_sweepMissBytes / 1024) + "KB]");
            Watching.Clear();
        }

        internal static void Discard(object handle) { }

        internal static void Tick()
        {
            if (Enabled == null || !Enabled.Value || Queued.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = Queued.Count - 1; i >= 0; i--)
            {
                if (now - Queued[i].At <= EntryLifeSeconds) continue;
                Log("dropped stale entry " + Queued[i].Item +
                    " (instance never came due); nothing held");
                Queued.RemoveAt(i);
            }
            bool due = false;
            for (int i = 0; i < Queued.Count; i++)
                if (now >= Queued[i].Due) { due = true; break; }
            if (!due) return;
            if (now - _censusAt >= CensusInterval)
            {
                try { BuildCensus(); }
                catch (Exception e) { Log("census failed: " + e.Message); }
                _censusAt = now;
            }
            for (int i = Queued.Count - 1; i >= 0; i--)
            {
                if (now < Queued[i].Due) continue;
                Entry entry = Queued[i];
                Queued.RemoveAt(i);
                try { Release(entry); }
                catch (Exception e) { Log("release failed: " + e.Message); }
            }
        }

        private static void Release(Entry entry)
        {
            bool destroyMaterials = DestroyMaterials != null && DestroyMaterials.Value;
            bool destroyMeshes = DestroyMeshes != null && DestroyMeshes.Value;
            int shared = 0, candidates = 0, untouched = 0, killed = 0;
            long bytes = 0;
            // The first run is a measurement: the names are what tell a runtime
            // instance from a package asset, and nothing else can.
            var spareMaterialNames = new List<string>(3);
            var spareMeshNames = new List<string>(3);
            var candidateNames = new List<string>(3);
            foreach (Material material in entry.Materials)
            {
                if (material == null) continue;
                if (Live.Contains(material.GetInstanceID())) { shared++; continue; }
                if (!EndsWith(material.name, "(Clone)"))
                {
                    untouched++;
                    if (spareMaterialNames.Count < 3) spareMaterialNames.Add(material.name);
                    continue;
                }
                candidates++;
                if (candidateNames.Count < 3) candidateNames.Add(material.name);
                if (!destroyMaterials) continue;
                bytes += Size(material);
                UnityEngine.Object.Destroy(material);
                killed++;
            }
            foreach (Mesh mesh in entry.Meshes)
            {
                if (mesh == null) continue;
                if (Live.Contains(mesh.GetInstanceID())) { shared++; continue; }
                // No "(Clone)" convention for meshes and no way to tell a
                // runtime copy from a package asset at this level.
                if (!destroyMeshes)
                {
                    untouched++;
                    if (spareMeshNames.Count < 3) spareMeshNames.Add(mesh.name);
                    continue;
                }
                candidates++;
                bytes += Size(mesh);
                UnityEngine.Object.Destroy(mesh);
                killed++;
            }
            Log(entry.Item +
                " materials=" + entry.Materials.Count + " meshes=" + entry.Meshes.Count +
                " shared=" + shared + " candidates=" + candidates +
                " untouched=" + untouched + " killed=" + killed +
                (killed > 0 ? " freed=" + (bytes / 1024) + "KB" : "") +
                " (destroyMaterials=" + destroyMaterials + " destroyMeshes=" + destroyMeshes + ")" +
                Samples("materials", spareMaterialNames) + Samples("candidates", candidateNames) +
                Samples("meshes", spareMeshNames));
        }

        // Liveness is keyed by instance ID: a dead entry can hold a destroyed
        // wrapper, and UnityEngine.Object equality would compare null-ish.
        private static void BuildCensus()
        {
            Live.Clear();
            Renderer[] renderers = Resources.FindObjectsOfTypeAll<Renderer>();
            if (renderers != null)
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null) continue;
                    Material[] shared = renderer.sharedMaterials;
                    if (shared != null)
                        for (int m = 0; m < shared.Length; m++)
                            if (shared[m] != null) Live.Add(shared[m].GetInstanceID());
                    SkinnedMeshRenderer skin = renderer as SkinnedMeshRenderer;
                    if (skin != null && skin.sharedMesh != null)
                        Live.Add(skin.sharedMesh.GetInstanceID());
                }
            MeshFilter[] filters = Resources.FindObjectsOfTypeAll<MeshFilter>();
            if (filters != null)
                for (int i = 0; i < filters.Length; i++)
                    if (filters[i] != null && filters[i].sharedMesh != null)
                        Live.Add(filters[i].sharedMesh.GetInstanceID());
            // UI graphics hold their material through the same source the
            // existing orphan sweep reads; leaving them out would let a cloned
            // panel material look unreferenced.
            Graphic[] graphics = Resources.FindObjectsOfTypeAll<Graphic>();
            if (graphics != null)
                for (int i = 0; i < graphics.Length; i++)
                {
                    Graphic graphic = graphics[i];
                    if (graphic == null) continue;
                    Material material = graphic.material;
                    if (material != null) Live.Add(material.GetInstanceID());
                }
        }

        internal static void Shutdown()
        {
            Queued.Clear();
            Live.Clear();
            Watching.Clear();
            _censusAt = -1f;
        }

        private static void Add(HashSet<Material> set, Material material)
        {
            if (material != null) set.Add(material);
        }

        private static void Add(HashSet<Mesh> set, Mesh mesh)
        {
            if (mesh != null) set.Add(mesh);
        }

        private static string Samples(string label, List<string> names)
        {
            if (names.Count == 0) return "";
            var text = new System.Text.StringBuilder(" ");
            text.Append(label).Append("=");
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) text.Append('|');
                text.Append(names[i]);
            }
            return text.ToString();
        }

        private static bool EndsWith(string value, string suffix)
        {
            return value != null && value.EndsWith(suffix, StringComparison.Ordinal);
        }

        private static long Size(UnityEngine.Object asset)
        {
            try { return UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(asset); }
            catch { return 0; }
        }

        private static string Label(JSONStorableDynamic item, Transform root)
        {
            try
            {
                Atom atom = item.containingAtom;
                return (atom == null ? "?" : atom.uid) + "/" + root.name;
            }
            catch { return root.name; }
        }

        private static void Log(string message)
        {
            WardrobeJanitor.Log("[asset-ledger] " + message);
        }
    }
}
