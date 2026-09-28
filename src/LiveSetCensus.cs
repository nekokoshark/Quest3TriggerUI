using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Route (2) of the memory work: name what the 21-28GB Boehm live set is
    // actually made of, so the parts this side can release become visible.
    //
    // SAFETY (learned the hard way, see 问题与证据索引 11.10): the first version
    // swept every static field of every loaded assembly from a background
    // thread and killed the game inside mono.dll JIT - FieldInfo.GetValue on a
    // foreign static field runs that type's static constructor, i.e. it forces
    // the runtime to compile code the game never executed. So:
    //   * roots are a curated list of already-initialised types only
    //   * nothing runs on a second thread: every slice runs in Update, so the
    //     game never JITs two methods at once
    //   * time-sliced at SliceMs per frame, hard budgets, report flushed as it
    //     goes so a crash still leaves data
    //
    // Trigger: write "<seconds> [maxObjects] [gc] [exp] [gconly]" (10..900
    // seconds, optional object budget, "gc" for the collection contrast,
    // "exp" to also read foreign statics, "gconly" to report the collected
    // live-set floor and stop without walking) to
    //   BepInEx\plugins\Quest3TriggerUI\liveset_census.txt
    // Report: 工作区\liveset_20260928\census_<timestamp>.txt
    //
    // Size model (every number here is an estimate, stated once):
    //   object = 16B header + instance field sizes, base classes included
    //   array  = 32B header + length * element size
    //   string = 32B + 2 * length
    //   reference field = 8B; value type = sum of its fields (8B guard past
    //   depth 8). Deliberately shallow: an object's own bytes only, so the
    //   per-type table sums to the counted total without double counting.
    // The visited set is a reference-identity HashSet: exact, and the only
    // structure that can tell a repeat visit from a first one. It does pin
    // what it has seen for the duration of the run, so every report is a
    // snapshot of a heap the probe is also holding; Teardown() releases it.
    //
    // Attribution: every counted object is charged to the root that enqueued
    // it first, so the report says which named holder owns which bytes. The
    // named statics are drained before the Unity shell is enumerated, so
    // shared data is charged to the root rather than to the scene.
    internal static class LiveSetCensus
    {
        private const string DirectoryPath =
            @"F:\vam1.22.0.12\工作区\liveset_20260928";
        private const string TriggerPath =
            @"F:\vam1.22.0.12\BepInEx\plugins\Quest3TriggerUI\liveset_census.txt";
        private const int SliceMs = 6;
        private const int DefaultMaxObjects = 12000000;
        private const int SampleCap = 2000;
        private const int ContainerCap = 200000;
        // Arrays are the containers that actually hold millions of entries: a
        // FileManager dictionary keeps three parallel 3.6M-slot arrays, and
        // walking only the first 200k of them silently dropped most of the VAR
        // index. Enumerables keep the tighter cap because some of them are
        // custom iterators that cost per-element work.
        private const int ArrayElementCap = 4000000;
        private const long BigObjectBytes = 1048576L;
        private const int MaxBigRows = 60;
        private const int FrontierCap = 20000;
        private const int ExpandTypesPerRound = 400;
        private const int MaxRoundsAllowed = 8;

        private enum Stage { Idle, Statics, StaticDrain, Unity, Drain, Expand, AudioPhase, Done }

        private static Stage _stage;
        private static float _nextCheck;
        private static StreamWriter _w;
        private static string _path;
        private static int _budgetSeconds;
        private static readonly Stopwatch _clock = new Stopwatch();
        private static readonly Stopwatch _slice = new Stopwatch();
        private static List<Row> _holderRows;
        private static List<Seed> _seedRoots;
        private static int _seedIndex;
        private static UnityEngine.Object[] _live;
        private static int _liveIndex;
        private static Walker _walk;
        private static HashSet<Type> _seededTypes;
        private static int _round;
        private static int _maxRounds = MaxRoundsAllowed;
        private static List<string> _attrNames;
        private static readonly Dictionary<Type, int> _unityAttrByType =
            new Dictionary<Type, int>();
        private static string _audioLine;

        private static readonly Dictionary<Type, int> _sizeCache =
            new Dictionary<Type, int>();
        private static readonly Dictionary<Type, TypeMeta> _metaCache =
            new Dictionary<Type, TypeMeta>();
        private static readonly Dictionary<Type, bool> _refCache =
            new Dictionary<Type, bool>();

        private static void Log(string message)
        {
            try
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo("[liveset] " + message);
            }
            catch { }
        }

        internal static void BeginFrame()
        {
            // A finished run re-arms instead of latching: the trigger file is
            // deleted on start, so the next one takes a fresh snapshot without
            // reloading the payload. Latching made a second measurement in the
            // same generation impossible, and a preset-swap retention reading
            // needs two.
            if (_stage == Stage.Done)
            {
                _stage = Stage.Idle;
                _nextCheck = Time.realtimeSinceStartup + 1f;
                return;
            }
            try
            {
                if (_stage == Stage.Idle)
                {
                    float now = Time.realtimeSinceStartup;
                    if (now < _nextCheck) return;
                    _nextCheck = now + 5f;
                if (!File.Exists(TriggerPath)) return;
                // Trigger file: "<seconds> [maxObjects] [gc] [exp]".
                //   gc  = run one full collection before seeding and report used
                //         before/after, which separates live data from garbage.
                //   exp = also read the statics of foreign types an instance was
                //         seen for (off by default: that is the path that crashed
                //         once, see 11.10).
                string trigger;
                try { trigger = File.ReadAllText(TriggerPath).Trim(); }
                catch { return; }
                if (SuperController.singleton == null ||
                    SuperController.singleton.isLoading ||
                    WardrobeJanitor.ImagesBusy()) return;
                string[] tok = trigger.Split(
                    new char[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                int seconds;
                if (tok.Length == 0 || !int.TryParse(tok[0], out seconds) ||
                    seconds < 10 || seconds > 900)
                    throw new InvalidDataException(
                        "LiveSetCensus budget must be 10..900 seconds");
                int maxObjects = DefaultMaxObjects;
                bool gcFirst = false, expand = false, gcOnly = false;
                for (int ti = 1; ti < tok.Length; ti++)
                {
                    if (string.Equals(tok[ti], "gc",
                            StringComparison.OrdinalIgnoreCase))
                        gcFirst = true;
                    else if (string.Equals(tok[ti], "exp",
                            StringComparison.OrdinalIgnoreCase))
                        expand = true;
                    else if (string.Equals(tok[ti], "gconly",
                            StringComparison.OrdinalIgnoreCase))
                        gcOnly = true;
                    else
                    {
                        int m;
                        if (int.TryParse(tok[ti], out m) && m >= 100000)
                            maxObjects = m;
                    }
                }
                File.Delete(TriggerPath);
                Start(seconds, maxObjects, gcFirst, expand, gcOnly);
                return;
                }
                Step();
            }
            catch (Exception e) { Abort("tick failed: " + e.Message); }
        }

        private static void Start(int seconds, int maxObjects, bool gcFirst,
            bool expand, bool gcOnly)
        {
            Directory.CreateDirectory(DirectoryPath);
            _path = Path.Combine(DirectoryPath,
                "census_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            _w = new StreamWriter(_path, false);
            _budgetSeconds = seconds;
            _clock.Reset();
            _clock.Start();
            _walk = new Walker();
            _walk.Seconds = seconds;
            _walk.MaxObjects = maxObjects;
            _seededTypes = new HashSet<Type>();
            _round = 0;
            _holderRows = new List<Row>();
            _seedRoots = new List<Seed>();
            _seedIndex = 0;
            _liveIndex = 0;
            _audioLine = null;
            _attrNames = new List<string>();
            _unityAttrByType.Clear();
            _maxRounds = expand ? MaxRoundsAllowed : 0;
            _live = null;
            _w.WriteLine("LiveSetCensus " + DateTime.Now.ToString("o") +
                " budget=" + seconds + "s maxObjects=" + maxObjects +
                " slice=" + SliceMs + "ms/frame (main thread) expand=" +
                (expand ? "on" : "off") + " gc=" + (gcFirst ? "on" : "off"));
            Head("START");
            if (gcFirst)
            {
                // A heap that has just been fully collected has no reachable-
                // but-uncollected garbage left, so START-POSTGC is what a
                // collection could actually reach and everything above POSTGC
                // is live-or-pinned.
                _w.WriteLine("gc contrast: collecting (this freezes the game)");
                _w.Flush();
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch (Exception e) { _w.WriteLine("gc failed: " + e.Message); }
                Head("POSTGC");
            }
            if (gcOnly)
            {
                // Live-set floor only: one collection, no walk. This is the
                // cheap probe for watching what a preset swap leaves behind -
                // the delta between two POSTGC readings is the retained part,
                // everything below it is the reachable floor.
                double gu = MonoGcProbe.UsedBytes() / 1073741824.0;
                double gh = MonoGcProbe.HeapBytes() / 1073741824.0;
                _w.WriteLine(string.Format(
                    "RETENTION[POSTGC] monoUsed={0:F2}GB monoHeap={1:F2}GB gc={2:F2}GB free={3:F2}GB",
                    gu, gh, GC.GetTotalMemory(false) / 1073741824.0, gh - gu));
                _w.WriteLine("DONE elapsed=" + _clock.ElapsedMilliseconds + "ms");
                _w.Flush();
                _w.Close();
                _w = null;
                Teardown();
                _stage = Stage.Done;
                Log(string.Format(
                    "gc-only probe done: monoUsed={0:F2}GB monoHeap={1:F2}GB report={2}",
                    gu, gh, _path));
                return;
            }
            // Curated roots only. Each entry is a type the game (or this
            // plugin) has already initialised, so reading its statics cannot
            // start a foreign static constructor.
            SeedOwnAssembly();
            SeedByName("vaM.SuperController", "SuperController");
            SeedByName("vaM.ImageLoaderThreaded", "ImageLoaderThreaded");
            SeedByName("vaM.FileManager", "MVR.FileManagement.FileManager");
            SeedByName("vaM.FileManagerSecure",
                "MVR.FileManagementSecure.FileManagerSecure");
            SeedByName("vaM.VarPackage", "MVR.FileManagement.VarPackage");
            SeedByName("vaM.DAZCharacterSelector", "DAZCharacterSelector");
            SeedByName("vaM.AudioClipManager", "AudioClipManager");
            SeedByName("vaM.URLAudioClipManager", "URLAudioClipManager");
            SeedByName("vaM.EmbeddedAudioClipManager",
                "EmbeddedAudioClipManager");
            _w.WriteLine("seeded static roots=" + _seedRoots.Count);
            _w.WriteLine("--- named static collections (direct count, size estimated) ---");
            _w.Flush();
            _stage = Stage.Statics;
            Log("census started budget=" + seconds + "s report=" + _path);
        }

        private static void Step()
        {
            _slice.Reset();
            _slice.Start();
            switch (_stage)
            {
                case Stage.Statics:
                {
                    while (_slice.ElapsedMilliseconds < SliceMs)
                    {
                        if (_seedIndex >= _seedRoots.Count)
                        {
                // Drain the named roots before the Unity shell floods the queue:
                // the queue is FIFO, so whoever enqueues first owns the
                // attribution of everything shared.
                _stage = Stage.StaticDrain;
                break;
                        }
                        Seed seed = _seedRoots[_seedIndex++];
                MeasureAndQueue(seed.Label, seed.Val, seed.Attr);
                    }
                    break;
                }
                case Stage.StaticDrain:
                {
                    // Named roots get half the object budget before the shell is
                    // even enumerated: their attribution is the actionable part,
                    // and the shell has a million members of its own.
                    long share = _walk.MaxObjects / 2L;
                    while (_slice.ElapsedMilliseconds < SliceMs &&
                           _walk.QObj.Count > 0 && !_walk.Expired() &&
                           _walk.Objects < share)
                        _walk.PopOne();
                    if (_walk.QObj.Count == 0 || _walk.Expired() ||
                        _walk.Objects >= share)
                    {
                        _stage = Stage.Unity;
                        try
                        {
                            _live = Resources.FindObjectsOfTypeAll(
                                typeof(UnityEngine.Object));
                            _w.WriteLine("unity objects=" +
                                (_live == null ? 0 : _live.Length) +
                                " staticsVisited=" + _walk.Objects +
                                " staticsQueued=" + _walk.QObj.Count);
                        }
                        catch (Exception e)
                        {
                            _w.WriteLine("unity enumeration failed: " + e.Message);
                        }
                        _w.Flush();
                    }
                    break;
                }
                case Stage.Unity:
                {
                    while (_slice.ElapsedMilliseconds < SliceMs &&
                           _live != null && _liveIndex < _live.Length)
                    {
                        SeedUnityOne();
                        while (_walk.QObj.Count > FrontierCap &&
                               _slice.ElapsedMilliseconds < SliceMs)
                            _walk.PopOne();
                    }
                    if (_live == null || _liveIndex >= _live.Length ||
                        _walk.Expired())
                        _stage = Stage.Drain;
                    break;
                }
                case Stage.Drain:
                {
                    while (_slice.ElapsedMilliseconds < SliceMs &&
                           _walk.QObj.Count > 0 && !_walk.Expired())
                        _walk.PopOne();
                    if (_walk.QObj.Count == 0 || _walk.Expired())
                        _stage = Stage.Expand;
                    break;
                }
                case Stage.Expand:
                {
                    int added = ExpandStatics();
                    if (added > 0 && !_walk.Expired())
                    {
                        _round++;
                        if (_w != null)
                        {
                            _w.WriteLine("  expand round " + _round +
                                ": newly seeded types=" + added + " queued=" +
                                _walk.QObj.Count);
                            _w.Flush();
                        }
                        _stage = Stage.Drain;
                    }
                    else _stage = Stage.AudioPhase;
                    break;
                }                case Stage.AudioPhase:
                {
                    ComputeAudio();
                    WriteStats();
                    _stage = Stage.Done;
                    break;
                }
            }
        }

        private static void Abort(string reason)
        {
            try
            {
                if (_w != null)
                {
                    _w.WriteLine("ABORTED: " + reason);
                    _w.Flush();
                    _w.Close();
                }
            }
            catch { }
            _w = null;
            Teardown();
            _stage = Stage.Done;
            Log("census aborted: " + reason);
        }

        private static void Head(string tag)
        {
            long ws = 0L, commit = 0L;
            try { MemoryProbe.ProcessBytes(out ws, out commit); }
            catch { }
            double gc = GC.GetTotalMemory(false) / 1073741824.0;
            // Profiler.GetMonoHeapSizeLong/GetMonoUsedSizeLong are stubs
            // (0) on this Unity build; read the Boehm heap directly.
            double heap = MonoGcProbe.HeapBytes() / 1073741824.0;
            double used = MonoGcProbe.UsedBytes() / 1073741824.0;
            _w.WriteLine(string.Format(
                "HEAD[{0}] {1:HH:mm:ss} ws={2:F2}GB commit={3:F2}GB gc={4:F2}GB monoHeap={5:F2}GB monoUsed={6:F2}GB monoFree={7:F2}GB",
                tag, DateTime.Now, ws / 1073741824.0, commit / 1073741824.0,
                gc, heap, used, heap - used));
            _w.Flush();
        }

        // ---------------------------------------------------------------
        // Curated static roots.
        // ---------------------------------------------------------------

        private sealed class Seed
        {
            internal string Label;
            internal object Val;
            internal int Attr;
        }

        private sealed class Row
        {
            internal string Label;
            internal string Kind;
            internal int Count;
            internal long Bytes;
            internal bool Partial;
            internal long Avg;
        }

        // Our own assembly only: every type here is code this plugin already
        // runs, so reading its statics cannot start an unfamiliar static
        // constructor (the failure mode that killed the previous version).
        private static void SeedOwnAssembly()
        {
            Type[] ts;
            try { ts = typeof(LiveSetCensus).Assembly.GetTypes(); }
            catch (Exception e)
            {
                _w.WriteLine("  own assembly GetTypes failed: " + e.Message);
                return;
            }
            int n = 0;
            for (int i = 0; i < ts.Length; i++)
            {
                Type t = ts[i];
                if (t == null || t.IsNested || t.IsGenericTypeDefinition) continue;
                if (!t.IsClass) continue;
                SeedType("plugin." + t.Name, t);
                n++;
            }
            _w.WriteLine("  own types seeded=" + n);
        }

        // Resolve by name instead of typeof: VaM core managers are not all
        // public, and Assembly.GetType(name) is a name lookup, not the
        // whole-assembly metadata sweep that GetTypes() would force.
        private static void SeedByName(string label, string typeName)
        {
            Type t = null;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length && t == null; i++)
            {
                try { t = asms[i].GetType(typeName); }
                catch { }
            }
            if (t == null)
            {
                _w.WriteLine("  type not found: " + typeName);
                return;
            }
            SeedType(label, t);
        }
        private static void SeedType(string prefix, Type t)
        {
            if (t != null && _seededTypes != null) _seededTypes.Add(t);
            // One attribution slot per seeded type: the report can then say how
            // many bytes each named root reaches, not just how big it is.
            int attr = AttrIndex(prefix);
            FieldInfo[] fs;
            try
            {
                fs = t.GetFields(BindingFlags.Static | BindingFlags.Public |
                    BindingFlags.NonPublic);
            }
            catch (Exception e)
            {
                _w.WriteLine("  statics unreadable " + prefix + ": " + e.Message);
                return;
            }
            for (int i = 0; i < fs.Length; i++)
            {
                Type ft;
                try { ft = fs[i].FieldType; } catch { continue; }
                if (ft == null || ft.IsPrimitive || ft.IsEnum ||
                    ft.IsPointer || ft == typeof(string)) continue;
                object val;
                try { val = fs[i].GetValue(null); }
                catch (Exception e)
                {
                    _w.WriteLine("  static unreadable " + prefix + "." +
                        fs[i].Name + ": " + e.Message);
                    continue;
                }
                if (val == null || val is string || val is Type ||
                    val is MemberInfo || val is Assembly) continue;
                var seed = new Seed();
                seed.Label = prefix + "." + fs[i].Name;
                seed.Val = val;
                seed.Attr = attr;
                _seedRoots.Add(seed);
            }
        }

        private static int AttrIndex(string name)
        {
            if (_attrNames == null) _attrNames = new List<string>();
            _attrNames.Add(name);
            return _attrNames.Count - 1;
        }

        // Attribution for the Unity shell is per concrete type, so the report
        // says e.g. "unity:SkinnedMeshRenderer" instead of one anonymous
        // bucket holding the whole scene.
        private static int UnityAttr(Type t)
        {
            int a;
            if (_unityAttrByType.TryGetValue(t, out a)) return a;
            a = AttrIndex("unity:" + t.Name);
            _unityAttrByType[t] = a;
            return a;
        }

        private static void MeasureAndQueue(string label, object val,
            int attr)
        {
            int count; long bytes; bool partial;
            MeasureContainer(val, out count, out bytes, out partial);
            var row = new Row();
            row.Label = label;
            row.Kind = KindOf(val);
            row.Count = count;
            row.Bytes = bytes;
            row.Partial = partial;
            row.Avg = count > 0 ? bytes / count : 0L;
            _holderRows.Add(row);
            if (bytes >= 1048576L)
            {
                _w.WriteLine(string.Format("  {0,10:F1}MB  {1,-7} n={2}{3}  {4}",
                    bytes / 1048576.0, row.Kind, count,
                    partial ? "+" : "", label));
                _w.Flush();
            }
            _walk.Add(val, "R:" + label, attr);
        }

        private static string KindOf(object val)
        {
            if (val is IDictionary) return "dict";
            if (val is Array) return "array";
            if (val is IList) return "list";
            if (val is IEnumerable) return "enum";
            return "other";
        }

        private static void MeasureContainer(object val, out int count,
            out long bytes, out bool partial)
        {
            count = 0; bytes = 0L; partial = false;
            IDictionary dict = val as IDictionary;
            if (dict != null)
            {
                int sampled = 0; long sampleBytes = 0L;
                try
                {
                    foreach (DictionaryEntry de in dict)
                    {
                        if (sampled >= SampleCap) break;
                        sampled++;
                        sampleBytes += Atom(de.Key) + Atom(de.Value);
                    }
                }
                catch { partial = true; }
                // .Count is O(1) and exact. Counting by enumeration used to stop
                // at ContainerCap, which under-reported a 3-million-entry
                // dictionary by that full ratio; the sample now only sizes an
                // entry, it never bounds the count.
                try { count = dict.Count; }
                catch { partial = true; }
                if (sampled > 0)
                    bytes = count <= sampled ? sampleBytes
                        : sampleBytes * count / sampled;
                else
                    bytes = (long)count * 64L;
                return;
            }
            Array arr = val as Array;
            if (arr != null)
            {
                bytes = Shallow(arr);
                count = arr.Length;
                return;
            }
            ICollection col = val as ICollection;
            if (col != null)
            {
                count = col.Count;
                bytes = (long)count * 64L;
                return;
            }
            IEnumerable en = val as IEnumerable;
            if (en != null)
            {
                long sum = 0L;
                try
                {
                    foreach (object item in en)
                    {
                        count++;
                        if (count <= SampleCap) sum += Atom(item);
                        if (count >= ContainerCap) { partial = true; break; }
                    }
                }
                catch { partial = true; }
                int s = Math.Min(count, SampleCap);
                bytes = s > 0 ? sum * count / s : (long)count * 64L;
                return;
            }
            bytes = Shallow(val);
            count = 1;
        }

        private static long Atom(object v)
        {
            if (v == null) return 0L;
            string s = v as string;
            if (s != null) return 32L + 2L * s.Length;
            if (v is Array) return Shallow((Array)v);
            return 32L;
        }

        private static void SeedUnityOne()
        {
            UnityEngine.Object o = _live[_liveIndex++];
            if (o == null) return;
            Type ct = o.GetType();
                TypeMeta meta = MetaOf(ct);
                int attr = UnityAttr(ct);
            for (int fi = 0; fi < meta.Fields.Length; fi++)
            {
                Type ft;
                try { ft = meta.Fields[fi].FieldType; } catch { continue; }
                if (ft == null || ft.IsPrimitive || ft.IsEnum ||
                    ft.IsPointer || ft == typeof(string) ||
                    ft == typeof(decimal)) continue;
                if (ft.IsValueType && !HasRefs(ft, 0)) continue;
                object fv;
                try { fv = meta.Fields[fi].GetValue(o); }
                catch { continue; }
                if (fv == null) continue;
                    _walk.Add(fv, "I:" + ct.Name + "." + meta.Fields[fi].Name, attr);
            }
        }

        // ---------------------------------------------------------------
        // Size model.
        // ---------------------------------------------------------------

        private sealed class TypeMeta
        {
            internal int Size;
            internal FieldInfo[] Fields;
            internal string[] Labels;
        }

        private static int SizeOfType(Type t, int guard)
        {
            if (t == null) return 8;
            if (t.IsPointer) return 8;
            if (t.IsEnum)
                return SizeOfType(Enum.GetUnderlyingType(t), guard + 1);
            if (t.IsPrimitive)
            {
                if (t == typeof(bool) || t == typeof(byte) ||
                    t == typeof(sbyte)) return 1;
                if (t == typeof(char) || t == typeof(short) ||
                    t == typeof(ushort)) return 2;
                if (t == typeof(int) || t == typeof(uint) ||
                    t == typeof(float)) return 4;
                return 8;
            }
            if (t == typeof(IntPtr) || t == typeof(UIntPtr)) return 8;
            if (t == typeof(decimal)) return 16;
            if (t.IsValueType)
            {
                if (guard > 8) return 8;
                int sum = 0;
                for (Type cur = t; cur != null && cur != typeof(object);
                     cur = cur.BaseType)
                {
                    FieldInfo[] fs;
                    try
                    {
                        fs = cur.GetFields(BindingFlags.Instance |
                            BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly);
                    }
                    catch { break; }
                    for (int i = 0; i < fs.Length; i++)
                    {
                        if (fs[i].IsStatic || fs[i].IsLiteral) continue;
                        Type ft;
                        try { ft = fs[i].FieldType; } catch { continue; }
                        sum += SizeOfType(ft, guard + 1);
                    }
                }
                return Math.Max(1, sum);
            }
            return 8;
        }

        private static TypeMeta MetaOf(Type t)
        {
            TypeMeta meta;
            if (_metaCache.TryGetValue(t, out meta)) return meta;
            var fields = new List<FieldInfo>();
            for (Type cur = t; cur != null && cur != typeof(object);
                 cur = cur.BaseType)
            {
                FieldInfo[] fs;
                try
                {
                    fs = cur.GetFields(BindingFlags.Instance |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                }
                catch { break; }
                for (int i = 0; i < fs.Length; i++)
                {
                    if (fs[i].IsStatic || fs[i].IsLiteral) continue;
                    fields.Add(fs[i]);
                }
            }
            meta = new TypeMeta();
            meta.Fields = fields.ToArray();
            meta.Labels = new string[meta.Fields.Length];
            int size = 16;
            for (int i = 0; i < meta.Fields.Length; i++)
            {
                Type ft;
                try { ft = meta.Fields[i].FieldType; } catch { ft = null; }
                size += SizeOfType(ft, 0);
                // Shared label strings: the walk must not allocate one per
                // edge, and every object of this type points at the same one.
                meta.Labels[i] = t.Name + "." + meta.Fields[i].Name;
            }
            meta.Size = size;
            _metaCache[t] = meta;
            return meta;
        }

        private static int SizeOf(object o)
        {
            Type t = o.GetType();
            int cached;
            if (_sizeCache.TryGetValue(t, out cached)) return cached;
            int size = MetaOf(t).Size;
            _sizeCache[t] = size;
            return size;
        }

        private static long Shallow(object o)
        {
            string s = o as string;
            if (s != null) return 32L + 2L * s.Length;
            Array arr = o as Array;
            if (arr != null) return Shallow(arr);
            return SizeOf(o);
        }

        private static long Shallow(Array arr)
        {
            Type et = arr.GetType().GetElementType();
            int es = SizeOfType(et, 0);
            if (es <= 0) es = 8;
            return 32L + (long)arr.Length * es;
        }

        // Would enumerating this element type reach any reference at all?
        // Primitive arrays (byte[], float[]) are leaves: skipping them keeps
        // the walk off the biggest arrays instead of touching millions of
        // elements that cannot hold anything.
        private static bool HasRefs(Type t, int guard)
        {
            if (t == null || guard > 6) return false;
            bool cached;
            if (_refCache.TryGetValue(t, out cached)) return cached;
            bool result = false;
            if (!t.IsValueType)
            {
                result = true;
            }
            else if (!t.IsEnum && !t.IsPrimitive)
            {
                for (Type cur = t; cur != null && cur != typeof(object) && !result;
                     cur = cur.BaseType)
                {
                    FieldInfo[] fs;
                    try
                    {
                        fs = cur.GetFields(BindingFlags.Instance |
                            BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly);
                    }
                    catch { break; }
                    for (int i = 0; i < fs.Length; i++)
                    {
                        Type ft;
                        try { ft = fs[i].FieldType; } catch { continue; }
                        if (ft == null) continue;
                        if (ft.IsPointer || !ft.IsValueType)
                        {
                            result = true;
                            break;
                        }
                        if (ft != t && HasRefs(ft, guard + 1))
                        {
                            result = true;
                            break;
                        }
                    }
                }
            }
            _refCache[t] = result;
            return result;
        }

        // ---------------------------------------------------------------
        // Bounded reachability walk (main thread, slice by slice).
        // ---------------------------------------------------------------

        // Open-addressed identity set: 8 bytes a slot instead of the ~27 an
        // object HashSet costs, which is what makes a 60-million-object walk
        // affordable. Membership is decided by ReferenceEquals, so it stays
        // exact whatever RuntimeHelpers.GetHashCode returns for a type.
        private sealed class IdentitySet
        {
            private object[] _slots = new object[1 << 16];
            private int _mask = (1 << 16) - 1;
            private int _used;

            internal bool Add(object o)
            {
                int h = Hash(o) & _mask;
                while (true)
                {
                    object cur = _slots[h];
                    if (cur == null)
                    {
                        _slots[h] = o;
                        if (++_used * 4 > _slots.Length * 3) Grow();
                        return true;
                    }
                    if (ReferenceEquals(cur, o)) return false;
                    h = (h + 1) & _mask;
                }
            }

            private static int Hash(object o)
            {
                int h = System.Runtime.CompilerServices.RuntimeHelpers
                    .GetHashCode(o);
                // Spread: the raw identity hash can be address-aligned and
                // therefore clustered in the low bits of a power-of-two mask.
                return h ^ (h >> 15);
            }

            private void Grow()
            {
                object[] old = _slots;
                int n = old.Length << 1;
                _slots = new object[n];
                _mask = n - 1;
                for (int i = 0; i < old.Length; i++)
                {
                    object o = old[i];
                    if (o == null) continue;
                    int h = Hash(o) & _mask;
                    while (_slots[h] != null) h = (h + 1) & _mask;
                    _slots[h] = o;
                }
            }
        }

        // Expansion round: once the game has been seen holding an instance of
        // a type, that type is initialised, so reading its statics cannot run
        // an unfamiliar static constructor - the failure mode of 11.10.
        // Runtime/BCL assemblies are skipped: their statics are neither large
        // nor ours to act on.
        private static int ExpandStatics()
        {
            if (_walk == null || _seededTypes == null || _round >= _maxRounds)
                return 0;
            if (_walk.Observed.Count == 0) return 0;
            var types = new List<Type>(_walk.Observed);
            int added = 0;
            for (int i = 0; i < types.Count && added < ExpandTypesPerRound; i++)
            {
                Type t = types[i];
                if (t == null || !t.IsClass || t.IsNested ||
                    t.IsGenericTypeDefinition) continue;
                if (_seededTypes.Contains(t)) continue;
                string an;
                try { an = t.Assembly.GetName().Name; } catch { continue; }
                if (an == null || RuntimeAssembly(an)) continue;
                SeedType("obs." + t.Name, t);
                added++;
            }
            return added;
        }

        private static bool RuntimeAssembly(string name)
        {
            string[] skip = { "mscorlib", "System", "Mono", "UnityEngine",
                "Unity.", "BepInEx", "0Harmony", "Harmony", "I18N", "Microsoft",
                "Anonymously", "PresentationCore", "WindowsBase", "IronPython",
                "netstandard", "csc", "SMDiagnostics", "Rewired", "ExCSS" };
            for (int i = 0; i < skip.Length; i++)
                if (name.StartsWith(skip[i])) return true;
            return false;
        }

        private static void WriteHolderRows()
        {
            if (_w == null || _holderRows == null) return;
            _holderRows.Sort(delegate(Row x, Row y)
                { return y.Bytes.CompareTo(x.Bytes); });
            _w.WriteLine("--- named static collections (top 45 of " +
                _holderRows.Count + ") ---");
            for (int i = 0; i < Math.Min(45, _holderRows.Count); i++)
                _w.WriteLine(string.Format(
                "  {0,9:F1}MB  {1,-7} n={2}{3} avg={4}B  {5}",
                _holderRows[i].Bytes / 1048576.0, _holderRows[i].Kind,
                _holderRows[i].Count,
                _holderRows[i].Partial ? "+" : "", _holderRows[i].Avg,
                _holderRows[i].Label));
        }
        private sealed class BigRow
        {
            internal long Bytes;
            internal string Type;
            internal long Length;
            internal string Via;
        }

        private sealed class Walker
        {
            internal int Seconds;
            internal readonly Stopwatch Sw = new Stopwatch();
                internal readonly IdentitySet Visited = new IdentitySet();
            internal readonly HashSet<Type> Observed = new HashSet<Type>();
            internal readonly Dictionary<string, long> TypeBytes =
                new Dictionary<string, long>();
            internal readonly Dictionary<string, int> TypeCount =
                new Dictionary<string, int>();
            internal readonly Dictionary<string, long> ViaBytes =
                new Dictionary<string, long>();
            internal readonly Dictionary<string, int> ViaCount =
                new Dictionary<string, int>();
            internal readonly List<BigRow> Big = new List<BigRow>();
            internal readonly Queue<object> QObj = new Queue<object>();
                internal readonly Queue<string> QVia = new Queue<string>();
                // Attribution travels with the entry, not with the object: whoever
                // enqueued an object first owns it, so seeding order (named roots
                // before the Unity shell) decides the ownership of shared data.
                internal readonly Queue<int> QAttr = new Queue<int>();
                internal readonly Dictionary<int, long> AttrBytes =
                    new Dictionary<int, long>();
                internal readonly Dictionary<int, int> AttrCount =
                    new Dictionary<int, int>();
                internal int MaxObjects = DefaultMaxObjects;
            internal long Total, Objects;
            internal string Stop;

            internal bool Expired()
            {
                if (Stop != null) return true;
                if (Objects >= MaxObjects)
                {
                    Stop = "object budget " + MaxObjects;
                    return true;
                }
                if (_clock.Elapsed.TotalSeconds >= Seconds)
                {
                    Stop = "time budget " + Seconds + "s";
                    return true;
                }
                return false;
            }

                internal void Add(object val, string via, int attr)
            {
                if (val == null) return;
                QObj.Enqueue(val);
                    QVia.Enqueue(via);
                    QAttr.Enqueue(attr);
            }

            internal bool PopOne()
            {
                object val = QObj.Dequeue();
                    string via = QVia.Dequeue();
                    int attr = QAttr.Dequeue();
                    Pop(val, via, attr);
                return QObj.Count > 0;
            }

                private void Pop(object val, string via, int attr)
            {
                if (val == null) return;
                Type t = val.GetType();
                if (t == typeof(Type) || val is MemberInfo ||
                    val is Assembly || val is Delegate ||
                    val is System.Threading.Thread || val is AppDomain ||
                    val is Module ||
                    val is System.Runtime.InteropServices.SafeHandle)
                    return;
                if (!Visited.Add(val)) return;

                long bytes = Shallow(val);
                Total += bytes;
                Objects++;
                string tn = t.FullName;
                if (tn == null) tn = t.Name;
                long tb;
                TypeBytes.TryGetValue(tn, out tb);
                TypeBytes[tn] = tb + bytes;
                int tc;
                TypeCount.TryGetValue(tn, out tc);
                    TypeCount[tn] = tc + 1;
                    Observed.Add(t);
                    long ab;
                    AttrBytes.TryGetValue(attr, out ab);
                    AttrBytes[attr] = ab + bytes;
                    int ac;
                    AttrCount.TryGetValue(attr, out ac);
                    AttrCount[attr] = ac + 1;

                if (bytes >= BigObjectBytes && via != null)
                {
                    long vb;
                    ViaBytes.TryGetValue(via, out vb);
                    ViaBytes[via] = vb + bytes;
                    int vc;
                    ViaCount.TryGetValue(via, out vc);
                    ViaCount[via] = vc + 1;
                    if (Big.Count < MaxBigRows ||
                        bytes > Big[Big.Count - 1].Bytes)
                    {
                        var row = new BigRow();
                        row.Bytes = bytes;
                        row.Type = tn;
                        Array arr0 = val as Array;
                        row.Length = arr0 == null ? 0L : (long)arr0.Length;
                        row.Via = via;
                        Big.Add(row);
                        if (Big.Count > MaxBigRows)
                        {
                            Big.Sort(delegate(BigRow x, BigRow y)
                                { return y.Bytes.CompareTo(x.Bytes); });
                            Big.RemoveAt(Big.Count - 1);
                        }
                    }
                }

                if (val is string) return;

                Array a = val as Array;
                if (a != null)
                {
                    Type et = t.GetElementType();
                    if (et != null && HasRefs(et, 0))
                    {
                        int cap = a.Length;
                    if (cap > ArrayElementCap) cap = ArrayElementCap;
                        for (int i = 0; i < cap; i++)
                        {
                            object item;
                            try { item = a.GetValue(i); }
                            catch { continue; }
                    if (item != null) Add(item, via, attr);
                        }
                    }
                    return;
                }

                TypeMeta meta = MetaOf(t);
                for (int i = 0; i < meta.Fields.Length; i++)
                {
                    Type ft;
                    try { ft = meta.Fields[i].FieldType; } catch { continue; }
                    if (ft == null) continue;
                    if (ft.IsPrimitive || ft.IsEnum || ft.IsPointer ||
                        ft == typeof(string) || ft == typeof(decimal))
                        continue;
                    if (ft.IsValueType && !HasRefs(ft, 0)) continue;
                    object fv;
                    try { fv = meta.Fields[i].GetValue(val); }
                    catch { continue; }
                    if (fv != null) Add(fv, meta.Labels[i], attr);
                }
            }
        }

        // ---------------------------------------------------------------
        // Report.
        // ---------------------------------------------------------------

        private static void ComputeAudio()
        {
            if (_live == null) return;
            var clips = new Dictionary<AudioClip, long>();
            long total = 0L;
            for (int i = 0; i < _live.Length; i++)
            {
                AudioClip c = _live[i] as AudioClip;
                if (c == null) continue;
                long b = (long)c.samples * c.channels * 4;
                clips[c] = b;
                total += b;
            }
            long referenced = 0L;
            for (int i = 0; i < _live.Length; i++)
            {
                AudioSource s = _live[i] as AudioSource;
                if (s == null) continue;
                long b;
                if (s.clip != null && clips.TryGetValue(s.clip, out b))
                    referenced += b;
            }
            _audioLine = string.Format(
                "audio: clips={0} total={1:F2}GB referenced-by-AudioSource={2:F2}GB unreferenced={3:F2}GB",
                clips.Count, total / 1073741824.0, referenced / 1073741824.0,
                (total - referenced) / 1073741824.0);
        }

        private static void WriteStats()
        {
            if (_w == null) return;
            Head("END");
            WriteHolderRows();
            if (_audioLine != null) _w.WriteLine(_audioLine);
            double monoUsed = MonoGcProbe.UsedBytes() / 1073741824.0;
            _w.WriteLine("walk: unityObjects=" + (_live == null ? 0 : _live.Length) +
                " unitySeeded=" + _liveIndex + " queued=" + _walk.QObj.Count +
                " rounds=" + _round + " maxObjects=" + _walk.MaxObjects);
            _w.WriteLine(string.Format(
                "COVERAGE visited={0} objects counted={1:F2}GB monoUsed={2:F2}GB ratio={3:F1}% stop={4} elapsed={5}s",
                _walk.Objects, _walk.Total / 1073741824.0, monoUsed,
                monoUsed > 0
                    ? _walk.Total / 1073741824.0 / monoUsed * 100.0 : 0.0,
                _walk.Stop == null ? "complete" : _walk.Stop,
                _clock.ElapsedMilliseconds / 1000));

            _w.WriteLine("--- estimated bytes by type (top 40) ---");
            var typeRows = new List<KeyValuePair<string, long>>(_walk.TypeBytes);
            typeRows.Sort(delegate(KeyValuePair<string, long> x,
                                   KeyValuePair<string, long> y)
                { return y.Value.CompareTo(x.Value); });
            for (int i = 0; i < Math.Min(40, typeRows.Count); i++)
                _w.WriteLine(string.Format("  {0,10:F2}GB  n={1,-9} {2}",
                    typeRows[i].Value / 1073741824.0,
                    _walk.TypeCount[typeRows[i].Key], typeRows[i].Key));

            _w.WriteLine("--- largest single objects (>=1MB) ---");
            _walk.Big.Sort(delegate(BigRow x, BigRow y)
                { return y.Bytes.CompareTo(x.Bytes); });
            for (int i = 0; i < Math.Min(MaxBigRows, _walk.Big.Count); i++)
                _w.WriteLine(string.Format("  {0,9:F1}MB  {1}{2}  <- {3}",
                    _walk.Big[i].Bytes / 1048576.0, _walk.Big[i].Type,
                    _walk.Big[i].Length > 0
                        ? "[len=" + _walk.Big[i].Length + "]" : "",
                    _walk.Big[i].Via));

            _w.WriteLine("--- big-object totals by holder field (>=1MB objects only) ---");
            var viaRows = new List<KeyValuePair<string, long>>(_walk.ViaBytes);
            viaRows.Sort(delegate(KeyValuePair<string, long> x,
                                  KeyValuePair<string, long> y)
                { return y.Value.CompareTo(x.Value); });
            for (int i = 0; i < Math.Min(40, viaRows.Count); i++)
                _w.WriteLine(string.Format("  {0,10:F1}MB  x{1,-6} {2}",
                    viaRows[i].Value / 1048576.0,
                    _walk.ViaCount[viaRows[i].Key], viaRows[i].Key));

            _w.WriteLine("--- counted bytes by root attribution (top 40) ---");
            var attrRows = new List<KeyValuePair<int, long>>(_walk.AttrBytes);
            attrRows.Sort(delegate(KeyValuePair<int, long> x,
                                   KeyValuePair<int, long> y)
                { return y.Value.CompareTo(x.Value); });
            for (int i = 0; i < Math.Min(40, attrRows.Count); i++)
            {
                string nm = "?";
                int ai = attrRows[i].Key;
                if (_attrNames != null && ai >= 0 && ai < _attrNames.Count)
                    nm = _attrNames[ai];
                long abytes = attrRows[i].Value;
                _w.WriteLine(string.Format(
                    "  {0,9:F3}GB  {1,5:F1}% of counted  n={2,-9} {3}",
                    abytes / 1073741824.0,
                    _walk.Total > 0 ? abytes * 100.0 / _walk.Total : 0.0,
                    _walk.AttrCount[ai], nm));
            }
            _w.WriteLine("DONE elapsed=" + _clock.ElapsedMilliseconds + "ms");
            _w.Flush();
            _w.Close();
            _w = null;
            long visited = _walk.Objects;
            double counted = _walk.Total / 1073741824.0;
            string stop = _walk.Stop;
            long elapsed = _clock.ElapsedMilliseconds / 1000;
            // Read the walk before releasing it: the previous version touched
            // _walk.Objects after nulling it, which aborted the census on its
            // own last line (the "tick failed" seen after a complete report).
            Teardown();
            Log(string.Format(
                "census done: visited={0} counted={1:F2}GB monoUsed={2:F2}GB stop={3} elapsed={4}s report={5}",
                visited, counted, monoUsed, stop == null ? "complete" : stop,
                elapsed, _path));
        }

        // Everything the census holds must go: the visited set alone keeps a
        // reference to every object it saw, so leaving it alive would make
        // the probe a memory leak of its own.
        private static void Teardown()
        {
            _sizeCache.Clear();
            _metaCache.Clear();
            _refCache.Clear();
            _unityAttrByType.Clear();
            _live = null;
            _seededTypes = null;
            _holderRows = null;
            _seedRoots = null;
            _attrNames = null;
            _liveIndex = 0;
            _walk = null;
        }
    }
}
