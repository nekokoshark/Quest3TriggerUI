using System;
using System.Collections.Generic;
using UnityEngine;
using BepInEx.Configuration;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Post-scene-load orphan sweep. Reloading a scene leaves behind objects
    // that have no home: runtime "(Clone)" materials no renderer references
    // any more, and component/GameObject populations that should have died
    // with the old scene but didn't. Both were measured growing ~2GB/5min
    // across identical-scene reloads (managedMiB +2.0-2.6GB per pass).
    //
    // Scope is deliberately narrow and evidence-gated:
    //   - materials: destroy ONLY "(Clone)"-suffixed materials that zero live
    //     Renderer/Graphic references point at. sharedMaterials is used —
    //     reading .materials would instantiate fresh copies (Unity side
    //     effect) and would defeat the whole point.
    //   - components: observation only — top type counts among GameObjects
    //     that are inactive-in-hierarchy or sit in an invalid scene, logged
    //     so a later targeted fix can pick the real offender without us
    //     guessing which third-party plugin leaks what.
    internal static class SceneOrphanSweep
    {
        private static float _sweepAt = -1f;
        // Per-type live component census taken at each sweep; the next sweep
        // diffs against it to name which types grew during the reload and
        // where a survivor lives (transform path + scene), which answers
        // "is it stranded junk or something still in use" before we destroy
        // anything third-party.
        private static Dictionary<string, int> _lastCompCounts;
        private static bool _censusOff;
        private static readonly Dictionary<string, string> _lastCompPath =
            new Dictionary<string, string>();

        // Inactive "(Clone)" GameObjects suspected of being leak-clones get
        // one grace period: they must still be inactive at the next sweep
        // before destruction, so a temporarily-parked pool row a plugin is
        // about to reuse is never caught mid-flight. The set is rebuilt from
        // scratch each sweep — anything that went active (or was destroyed
        // by its owner) simply fails to appear twice.
        private static HashSet<int> _suspectIds = new HashSet<int>();
        private static readonly System.Reflection.Assembly CurrentAsm =
            typeof(SceneOrphanSweep).Assembly;

        // The sweep used to run every leg inside one frame. On a loaded scene
        // that measured a 10.8s single-frame freeze (four FindObjectsOfTypeAll
        // walks plus a forced double GC). Same work, same order, same
        // per-object rules — only spread across frames under a budget.
        internal static ConfigEntry<float> SliceMs;
        internal static ConfigEntry<bool> MeasureGC;
        // The objects leg walks every live GameObject and groups them by
        // transform path to spot growing "(Clone)" pools. Measured on a loaded
        // scene: objects=16118ms of a 17676ms sweep, and the pass it feeds
        // (clone kills) destroyed nothing across sweeps (cloneGo killed=0).
        // Off by default; re-enable when a clone leak is actually suspected.
        internal static ConfigEntry<bool> SweepObjects;
        // The components leg counts stranded components and builds a
        // per-MonoBehaviour-type histogram. Nothing reads either: the count and
        // the histogram go into the log line and nowhere else, while the leg
        // walks every live MonoBehaviour and resolves Transform.root for each.
        // Measured 6147ms of slice time in the 14.70 sweep (graphics=1033ms for
        // comparison). Off by default; re-enable when the census is the thing
        // being investigated.
        internal static ConfigEntry<bool> Census;

        private const int Batch = 256;
        private static readonly System.Diagnostics.Stopwatch _frameWatch =
            new System.Diagnostics.Stopwatch();
        private static readonly System.Diagnostics.Stopwatch _legWatch =
            new System.Diagnostics.Stopwatch();

        // step: 1 renderers, 2 graphics, 3 materials, 4 components,
        //       5 objects, 6 clone kills, 7 log; 0 = idle.
        private static int _step;
        private static float _sweepStartAt;
        private static UnityEngine.Object[] _objects;
        private static int _index;
        private static string _timings = "";
        private static HashSet<Material> _used;
        private static int _killed;
        private static Dictionary<string, int> _orphans;
        private static int _orphanTotal;
        private static Dictionary<string, int> _counts;
        private static Dictionary<string, string> _paths;
        private static HashSet<int> _cur;
        private static Dictionary<string, int> _groupNow;
        private static Dictionary<string, List<int>> _memberIds;
        private static Dictionary<int, GameObject> _memberObjs;
        private static int _killedGo;
        private static int _zombieGo;
        private static string _killedNames = "";

        private static bool IsSafeEngineAssembly(string an)
        {
            return an == null ||
                an.StartsWith("UnityEngine") || an.StartsWith("Unity.") ||
                an == "Assembly-CSharp" || an.StartsWith("Assembly-") ||
                an == "mscorlib" || an.StartsWith("System") ||
                an.StartsWith("Mono.") || an == "netstandard";
        }

        // True when the subtree hosts any component from a foreign plugin
        // assembly — pooled/managed third-party UI, never ours to destroy.
        private static bool ForeignManaged(GameObject go)
        {
            Component[] comps = null;
            try { comps = go.GetComponentsInChildren<Component>(true); }
            catch { }
            if (comps == null) return false;
            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null) continue;
                string an = null;
                try { an = c.GetType().Assembly.GetName().Name; }
                catch { }
                if (an != null && an.StartsWith("Quest3TriggerUI")) continue;
                if (IsSafeEngineAssembly(an)) continue;
                return true;
            }
            return false;
        }

        // True when the GO itself hosts a script component that is neither
        // ours (any generation) nor Unity built-in — i.e. the object is
        // owned by VaM or another plugin even if it also carries one of our
        // stale components. Such hosts must lose only our components.
        internal static bool HostHasAlienScript(GameObject go)
        {
            Component[] comps = null;
            try { comps = go.GetComponents<Component>(); }
            catch { }
            if (comps == null) return false;
            for (int i = 0; i < comps.Length; i++)
            {
                MonoBehaviour mb = comps[i] as MonoBehaviour;
                if (mb == null) continue;
                string ns = mb.GetType().Namespace;
                if (ns != null && ns.StartsWith("Quest3TriggerUI")) continue;
                string an = null;
                try { an = mb.GetType().Assembly.GetName().Name; }
                catch { }
                if (an != null &&
                    (an.StartsWith("UnityEngine") || an.StartsWith("Unity.")))
                    continue;
                return true;
            }
            return false;
        }

        // Strip only our old-generation components off a foreign host —
        // the host object stays alive for its real owner.
        private static int StripStaleComponents(GameObject go)
        {
            int stripped = 0;
            Component[] comps = null;
            try { comps = go.GetComponents<Component>(); }
            catch { }
            if (comps == null) return 0;
            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null) continue;
                try
                {
                    var asm = c.GetType().Assembly;
                    if (asm == CurrentAsm) continue;
                    string an = asm.GetName().Name;
                    if (an == null || !an.StartsWith("Quest3TriggerUI"))
                        continue;
                    UnityEngine.Object.Destroy(c);
                    stripped++;
                }
                catch { }
            }
            return stripped;
        }
        // Group population per (parentPath|name): a stable pool stays the
        // same size across reloads, a leak grows every pass. Only groups
        // that grew AND already had ≥4 members get trimmed — the victims are
        // last sweep's suspects (the old backlog), never the fresh arrivals.
        private static Dictionary<string, int> _groupCounts =
            new Dictionary<string, int>();
        private const int GroupFloor = 4;

        internal static void OnSceneLoaded()
        {
            // Give VaM and scene plugins a few seconds to run their own
            // teardown first — sweeping early would destroy objects that are
            // mid-migration between the old and new scene. A sweep already in
            // flight is abandoned: its snapshots belong to the old scene.
            AbortSweep();
            _sweepAt = Time.realtimeSinceStartup + 8f;
        }

        private static void AbortSweep()
        {
            _step = 0;
            _objects = null;
            _index = 0;
            _used = null;
            _orphans = null;
            _counts = null;
            _paths = null;
            _cur = null;
            _groupNow = null;
            _memberIds = null;
            _memberObjs = null;
        }

        // Driven once per frame from the main Update. A call runs whole legs
        // while they stay under the per-frame budget, so the total cost is the
        // same as the old single-frame sweep but no frame stalls on it.
        internal static void Tick()
        {
            if (_step == 0)
            {
                if (_sweepAt < 0f) return;
                if (Time.realtimeSinceStartup < _sweepAt) return;
                // Keep the deadline: the sweep starts at the first frame
                // outside the person-preset window instead of inside it.
                if (LoadWindow.PresetBusy) { LoadWindow.NoteDeferred("OrphanSweep"); return; }
                _sweepAt = -1f;
                BeginSweep();
            }
            float budget = (SliceMs == null) ? 3f : SliceMs.Value;
            if (budget < 0.5f) budget = 0.5f;
            _frameWatch.Reset();
            _frameWatch.Start();
            try
            {
                while (_step != 7 &&
                    _frameWatch.Elapsed.TotalMilliseconds < budget)
                {
                    if (!StepOnce()) break;
                }
                if (_step == 7) FinishSweep();
            }
            catch (Exception e)
            {
                WardrobeJanitor.Log("orphan sweep failed: " + e.Message);
                AbortSweep();
            }
            finally { _frameWatch.Stop(); }
        }

        private static void BeginSweep()
        {
            _used = new HashSet<Material>();
            _orphans = new Dictionary<string, int>();
            _counts = new Dictionary<string, int>();
            _paths = new Dictionary<string, string>();
            _killed = 0;
            _orphanTotal = 0;
            _killedGo = 0;
            _zombieGo = 0;
            _killedNames = "";
            _timings = "";
            _censusOff = (Census != null && !Census.Value);
            _objects = null;
            _index = 0;
            _step = 1;
            _sweepStartAt = Time.realtimeSinceStartup;
            _legWatch.Reset();
            _legWatch.Start();
        }

        // Closes the leg being left, charges its ms to the sweep's timing line
        // and opens the next one. The watch is never reset between frames, so
        // a leg spanning many frames still reports its true total.
        private static void Advance(string legName, int nextStep)
        {
            _objects = null;
            _index = 0;
            _legWatch.Stop();
            _timings += " " + legName + "=" +
                _legWatch.Elapsed.TotalMilliseconds.ToString("F0") + "ms";
            _legWatch.Reset();
            _legWatch.Start();
            _step = nextStep;
        }

        private static bool StepOnce()
        {
            switch (_step)
            {
                case 1: StepRenderers(); return true;
                case 2: StepGraphics(); return true;
                case 3: StepMaterials(); return true;
                case 4: StepComponents(); return true;
                case 5: StepObjects(); return true;
                case 6: StepCloneKills(); return true;
                default: return false;
            }
        }

        private static string FullPath(Transform t)
        {
            string p = "";
            while (t != null)
            {
                p = "/" + t.name + p;
                t = t.parent;
            }
            return p.Length > 90 ? "…" + p.Substring(p.Length - 90) : p;
        }

        private static void StepRenderers()
        {
            if (_objects == null)
            {
                _objects = Resources.FindObjectsOfTypeAll<Renderer>();
                _index = 0;
            }
            Renderer[] rs = (Renderer[])_objects;
            int end = _index + Batch;
            if (end > rs.Length) end = rs.Length;
            for (; _index < end; _index++)
            {
                Renderer r = rs[_index];
                if (r == null) continue;
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null) _used.Add(mats[i]);
            }
            if (_index >= rs.Length) Advance("renderers", 2);
        }

        private static void StepGraphics()
        {
            if (_objects == null)
            {
                _objects = Resources.FindObjectsOfTypeAll<Graphic>();
                _index = 0;
            }
            Graphic[] gs = (Graphic[])_objects;
            int end = _index + Batch;
            if (end > gs.Length) end = gs.Length;
            for (; _index < end; _index++)
            {
                Graphic g = gs[_index];
                if (g == null) continue;
                Material m = g.material;
                if (m != null) _used.Add(m);
            }
            if (_index >= gs.Length) Advance("graphics", 3);
        }

        private static void StepMaterials()
        {
            if (_objects == null)
            {
                _objects = Resources.FindObjectsOfTypeAll<Material>();
                _index = 0;
            }
            Material[] ms = (Material[])_objects;
            int end = _index + Batch;
            if (end > ms.Length) end = ms.Length;
            for (; _index < end; _index++)
            {
                Material m = ms[_index];
                if (m == null) continue;
                string n = m.name;
                if (!n.EndsWith("(Clone)")) continue;
                if (_used.Contains(m)) continue;
                UnityEngine.Object.Destroy(m);
                _killed++;
            }
            if (_index >= ms.Length) Advance("materials", 4);
        }

        // Both former MonoBehaviour legs run here: the stranded-component
        // counter and the per-type live census walked the same array twice.
        private static void StepComponents()
        {
            if (_censusOff)
            {
                // Observation-only leg: leave it out and hand the step on.
                bool objectsOn = (SweepObjects == null) || SweepObjects.Value;
                Advance("components", objectsOn ? 5 : 7);
                _timings += " census=off";
                if (!objectsOn)
                {
                    _timings += " objects=off clones=off";
                    _suspectIds = new HashSet<int>();
                }
                return;
            }
            if (_objects == null)
            {
                _objects = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                _index = 0;
            }
            MonoBehaviour[] bs = (MonoBehaviour[])_objects;
            int end = _index + Batch;
            if (end > bs.Length) end = bs.Length;
            for (; _index < end; _index++)
            {
                MonoBehaviour b = bs[_index];
                if (b == null) continue;
                GameObject go = b.gameObject;
                if (go == null) continue;
                // Prefab assets have an invalid scene — legitimate library
                // content, not stranded junk and not part of the live census.
                if (!go.scene.IsValid()) continue;
                Transform root = go.transform.root;
                if (root != null && !root.gameObject.activeInHierarchy)
                {
                    _orphanTotal++;
                    string on = b.GetType().Name;
                    int oc;
                    _orphans[on] = _orphans.TryGetValue(on, out oc) ? oc + 1 : 1;
                }
                string tn = b.GetType().FullName;
                int c;
                _counts[tn] = _counts.TryGetValue(tn, out c) ? c + 1 : 1;
                if (!_paths.ContainsKey(tn))
                    _paths[tn] = "/" + FullPath(go.transform) +
                        (go.activeInHierarchy ? "" : " [inactive]");
            }
            if (_index >= bs.Length)
            {
                bool objects = (SweepObjects == null) || SweepObjects.Value;
                Advance("components", objects ? 5 : 7);
                if (!objects)
                {
                    // Skipping legs 5/6 leaves no census to feed the clone
                    // kill, so clear last sweep's suspects instead of logging
                    // a stale count for a pass that did not run.
                    _timings += " objects=off clones=off";
                    _suspectIds = new HashSet<int>();
                }
            }
        }

        private static void StepObjects()
        {
            if (_objects == null)
            {
                _cur = new HashSet<int>();
                _groupNow = new Dictionary<string, int>();
                _memberIds = new Dictionary<string, List<int>>();
                _memberObjs = new Dictionary<int, GameObject>();
                _objects = Resources.FindObjectsOfTypeAll<GameObject>();
                _index = 0;
            }
            GameObject[] gos = (GameObject[])_objects;
            int end = _index + Batch;
            if (end > gos.Length) end = gos.Length;
            for (; _index < end; _index++)
            {
                GameObject go = gos[_index];
                if (go == null) continue;
                if (!go.scene.IsValid()) continue;
                if (go.activeInHierarchy) continue;
                string nm = go.name;
                bool zombie = false;
                bool ours = false;
                bool foreign = false;
                bool cloneNamed = nm.EndsWith("(Clone)");
                Component[] comps = null;
                try { comps = go.GetComponents<Component>(); }
                catch { }
                if (comps != null)
                {
                    for (int c = 0; c < comps.Length; c++)
                    {
                        Component comp = comps[c];
                        if (comp == null) continue;
                        var asm = comp.GetType().Assembly;
                        if (asm == CurrentAsm) { ours = true; break; }
                        string an = asm.GetName().Name;
                        if (an == null) continue;
                        if (an.StartsWith("Quest3TriggerUI"))
                            zombie = true;
                        else if (!IsSafeEngineAssembly(an))
                            foreign = true;
                    }
                }
                if (zombie)
                {
                    if (!ours)
                    {
                        // A host carrying anyone else's scripts belongs to
                        // that owner — strip only our stale-gen components
                        // off it, never destroy the object.
                        if (foreign || HostHasAlienScript(go) ||
                            ForeignManaged(go))
                            _zombieGo += StripStaleComponents(go);
                        else
                        {
                            try { UnityEngine.Object.Destroy(go); _zombieGo++; }
                            catch { }
                        }
                    }
                    continue;
                }
                if (ours || !cloneNamed) continue;
                // Inactive clones hosting third-party plugin components are
                // owned objects, not leaks — skip them entirely.
                if (foreign || ForeignManaged(go)) continue;
                // Group by (parent path | name): session-plugin panels are
                // inactive clones too, so "inactive clone" alone is not a
                // leak proof — population growth is.
                Transform parent = go.transform.parent;
                string key = (parent == null ? "/" : FullPath(parent)) +
                    "|" + nm;
                int n;
                _groupNow[key] = _groupNow.TryGetValue(key, out n) ? n + 1 : 1;
                int id = go.GetInstanceID();
                List<int> lst;
                if (!_memberIds.TryGetValue(key, out lst))
                    _memberIds[key] = lst = new List<int>();
                lst.Add(id);
                _memberObjs[id] = go;
            }
            if (_index >= gos.Length) Advance("objects", 6);
        }

        private static void StepCloneKills()
        {
            // Second pass over grown groups only: kill members that were
            // already suspects last sweep. A group whose headcount is
            // unchanged is a pool — nobody gets touched.
            foreach (KeyValuePair<string, int> kv in _groupNow)
            {
                int prev;
                if (!_groupCounts.TryGetValue(kv.Key, out prev)) prev = 0;
                if (kv.Value < GroupFloor || kv.Value <= prev) continue;
                List<int> lst;
                if (!_memberIds.TryGetValue(kv.Key, out lst)) continue;
                for (int i = 0; i < lst.Count; i++)
                {
                    int id = lst[i];
                    if (!_suspectIds.Contains(id)) continue;
                    GameObject go = _memberObjs[id];
                    // Re-check at kill time: a suspect pooled by a
                    // plugin since the census must survive.
                    if (go == null || ForeignManaged(go)) { _cur.Remove(id); continue; }
                    try { UnityEngine.Object.Destroy(go); _killedGo++; }
                    catch { }
                    _cur.Remove(id);
                    if (_killedNames.Length < 200)
                        _killedNames += " " + kv.Key.Split('|')[1];
                }
            }
            // Everyone still alive and inactive-clone is a suspect next time.
            foreach (KeyValuePair<string, List<int>> kv in _memberIds)
                for (int i = 0; i < kv.Value.Count; i++)
                    _cur.Add(kv.Value[i]);
            _suspectIds = _cur;
            Advance("clones", 7);
        }

        private static void FinishSweep()
        {
            _legWatch.Stop();
            _frameWatch.Stop();

            // Which clone groups grew this reload — names the leaking root
            // (e.g. a session plugin repopulating a list into the same
            // inactive tree every load) so we can decide whether a targeted
            // unload patch is warranted instead of generic sweeping.
            string grew = "";
            int gshow2 = 0;
            if (_groupNow != null)
            {
                foreach (KeyValuePair<string, int> kv in _groupNow)
                {
                    int prev;
                    if (!_groupCounts.TryGetValue(kv.Key, out prev)) prev = 0;
                    int d = kv.Value - prev;
                    if (d < 4 || gshow2 >= 5) continue;
                    grew += " " + kv.Key + " " + prev + "->" + kv.Value;
                    gshow2++;
                }
                _groupCounts = _groupNow;
            }

            string grown = "";
            int gshown = 0;
            if (_counts != null)
            {
                foreach (KeyValuePair<string, int> kv in _counts)
                {
                    int prev;
                    int delta = (_lastCompCounts != null &&
                        _lastCompCounts.TryGetValue(kv.Key, out prev))
                        ? kv.Value - prev : 0;
                    if (delta < 5 || gshown >= 8) continue;
                    string sample;
                    grown += " " + kv.Key + "+" + delta +
                        (_lastCompPath.TryGetValue(kv.Key, out sample)
                            ? "@" + sample : "");
                    gshown++;
                }
                _lastCompCounts = _counts;
            }
            if (_paths != null)
            {
                foreach (KeyValuePair<string, string> kv in _paths)
                    _lastCompPath[kv.Key] = kv.Value;
            }

            string top = "";
            int shown = 0;
            if (_orphans != null)
            {
                foreach (KeyValuePair<string, int> kv in _orphans)
                {
                    if (kv.Value < 3 || shown >= 6) continue;
                    top += " " + kv.Key + "x" + kv.Value;
                    shown++;
                }
            }

            // Boehm never returns pages to the OS, so heap size is a
            // high-water mark, not live usage. The forced collect that used to
            // separate the two ran inside this sweep and cost seconds; the
            // same numbers already come from PresetSweepGate on its own
            // schedule, so it is opt-in again (OrphanSweepMeasureGC).
            string memLine = "";
            try
            {
                if (MeasureGC != null && MeasureGC.Value)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    long liveB = GC.GetTotalMemory(true);
                    memLine += " liveMiB=" + (liveB / 1048576);
                }
                // Profiler.GetMonoHeapSizeLong/GetMonoUsedSizeLong are stubs
                // that return 0 in this build (monoUsed=0 monoHeap=0 in every
                // sweep), so the numbers came from mono.dll directly.
                long heapB = MonoGcProbe.HeapBytes();
                long usedB = MonoGcProbe.UsedBytes();
                memLine += " monoUsed=" + (usedB < 0L ? "n/a" : (usedB / 1048576).ToString()) +
                    " monoHeap=" + (heapB < 0L ? "n/a" : (heapB / 1048576).ToString()) +
                    " retained≈" + ((heapB < 0L || usedB < 0L) ? "n/a" : ((heapB - usedB) / 1048576).ToString());
            }
            catch { }

            int suspectCount = (_suspectIds == null) ? 0 : _suspectIds.Count;
            int liveRefs = (_used == null) ? 0 : _used.Count;
            WardrobeJanitor.Log("orphan sweep: clone-mat killed=" + _killed +
                " liveRefs=" + liveRefs +
                " cloneGo killed=" + _killedGo + " zombieGo=" + _zombieGo +
                " suspects=" + suspectCount +
                (_killedNames.Length > 0 ? " names:" + _killedNames : "") +
                " strandedComps=" + (_censusOff ? "off" : _orphanTotal.ToString()) + top +
                (grew.Length > 0 ? " grewGroups:" + grew : "") +
                memLine +
                " totalMs=" +
                (long)((Time.realtimeSinceStartup - _sweepStartAt) * 1000f) +
                " timing:" + _timings +
                " grown:" + (grown == "" ? " none" : grown));
            AbortSweep();
        }
    }
}
