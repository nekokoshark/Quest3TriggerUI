using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Quest3TriggerUI
{
    // Read-only retention report, built to answer one question: after a preset
    // swap has been collected, which container is still holding the bytes that
    // make the post-GC floor creep (+173MiB per load, measured 2026-09-30)?
    //
    // Two halves:
    //   * a GC-reason histogram, so the distribution of who asks for the full
    //     mark is dated in the watch line as the session grows;
    //   * a governed walk of static collection fields - first in this plugin
    //     assembly, then across Assembly-CSharp - printed as Type.Field=Count
    //     so two runs can simply be diffed.
    //
    // Nothing here allocates on the game's behalf, mutates state or releases
    // anything: it reads field values and counts entries. Measurement only, and
    // every step is individually guarded so one failure cannot silence the rest.
    internal static class MemoryRetentionReport
    {
        private const BindingFlags Statics = BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const int MaxScanTypes = 40000;
        private const int MinReported = 32;
        private const int MaxReported = 28;

        private static readonly object Sync = new object();
        private static Dictionary<string, int> _reasons;
        private static long _total;
        private static List<FieldInfo> _own;
        private static List<FieldInfo> _game;
        private static bool _gameTried;
        // Flipped live from the plugin's cfg-text scan: reading a static field
        // runs that type's initializer, so the Assembly-CSharp half stays
        // switchable at runtime without a rebuild.
        internal static bool GameScan = true;
        private static string _lastHistogram;

        // Called from PresetSweepGate.RunGc, the single funnel every
        // preset-path collection goes through.
        internal static void NoteGc(string reason)
        {
            try
            {
                string key = string.IsNullOrEmpty(reason) ? "?" :
                    reason.Replace(' ', '_');
                lock (Sync)
                {
                    if (_reasons == null)
                        _reasons = new Dictionary<string, int>(StringComparer.Ordinal);
                    int n;
                    _reasons.TryGetValue(key, out n);
                    _reasons[key] = n + 1;
                    _total++;
                }
            }
            catch { }
        }

        private static string Histogram()
        {
            lock (Sync)
            {
                if (_reasons == null || _reasons.Count == 0) return "gcHist=none";
                var rows = new List<KeyValuePair<string, int>>(_reasons);
                rows.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
                { return b.Value.CompareTo(a.Value); });
                var sb = new System.Text.StringBuilder("gcHist=");
                for (int i = 0; i < rows.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(rows[i].Key).Append(':').Append(rows[i].Value);
                }
                return sb.Append("/total=").Append(_total).ToString();
            }
        }

        // The watch line carries the histogram only when it moved: one line per
        // interval is preserved, but the shift in GC reasons is still dated.
        internal static string GcHistogramIfChanged()
        {
            try
            {
                string now = Histogram();
                lock (Sync)
                {
                    if (now == _lastHistogram) return null;
                    _lastHistogram = now;
                }
                return now;
            }
            catch { return null; }
        }

        private static List<FieldInfo> OwnFields()
        {
            lock (Sync)
            {
                if (_own == null)
                    _own = Scan(typeof(Quest3TriggerUIPlugin).Assembly,
                        "own(" + typeof(Quest3TriggerUIPlugin).Assembly.GetName().Name + ")");
                return _own;
            }
        }

        private static List<FieldInfo> GameFields()
        {
            lock (Sync)
            {
                if (!GameScan) return null;
                if (_game != null) return _game;
                if (_gameTried) return null;
                _gameTried = true;
                try
                {
                    _game = Scan(typeof(SuperController).Assembly,
                        "game(" + typeof(SuperController).Assembly.GetName().Name + ")");
                }
                catch (Exception e)
                {
                    Log("[ret] game scan unavailable: " + e.GetType().Name);
                    _game = new List<FieldInfo>();
                }
                return _game;
            }
        }

        // Only static fields whose declared type can hold a collection are kept,
        // so the per-run walk stays a value read per candidate and never touches
        // anything that could allocate. A static field read does run the type's
        // initializer; this assembly has no static constructors, and the game
        // assembly's caches are already initialized by the time a preset loads.
        private static List<FieldInfo> Scan(Assembly assembly, string label)
        {
            var fields = new List<FieldInfo>();
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            if (types == null) return fields;
            int seen = 0;
            for (int i = 0; i < types.Length && seen < MaxScanTypes; i++)
            {
                Type t = types[i];
                if (t == null) continue;
                seen++;
                FieldInfo[] fs;
                try { fs = t.GetFields(Statics); }
                catch { continue; }
                for (int k = 0; k < fs.Length; k++)
                {
                    Type ft = fs[k].FieldType;
                    if (ft.IsArray || typeof(ICollection).IsAssignableFrom(ft))
                        fields.Add(fs[k]);
                }
            }
            Log("[ret] " + label + " scan: types=" + seen + " collectionFields=" + fields.Count);
            return fields;
        }

        private static string Label(FieldInfo f)
        {
            string owner = f.DeclaringType == null ? "?" : f.DeclaringType.Name;
            return owner + "." + f.Name;
        }

        // Count for collections, byte length (marked B) for byte arrays. -1 when
        // the field is null or not countable.
        private static long Measure(FieldInfo f)
        {
            try
            {
                object v = f.GetValue(null);
                if (v == null || v is string) return -1L;
                var bytes = v as byte[];
                if (bytes != null) return -1L; // reported as bytes, not elements
                var arr = v as Array;
                if (arr != null) return arr.Length;
                var c = v as ICollection;
                return c == null ? -1L : c.Count;
            }
            catch { return -1L; }
        }

        private static long MeasureBytes(FieldInfo f)
        {
            try { var bytes = f.GetValue(null) as byte[]; return bytes == null ? -1L : bytes.LongLength; }
            catch { return -1L; }
        }

        private static void Emit(string tag, string side, List<FieldInfo> fields)
        {
            var rows = new List<KeyValuePair<string, long>>();
            var changes = new System.Text.StringBuilder();
            if (_previousCounts == null) _previousCounts = new Dictionary<string, long>();
            int changed = 0;
            for (int i = 0; i < fields.Count; i++)
            {
                long n = Measure(fields[i]);
                string key = side + ":" + fields[i].DeclaringType.FullName + "." + fields[i].Name;
                long old;
                if (n >= 0 && _previousCounts.TryGetValue(key, out old) && old != n)
                {
                    changed++;
                    if (changed <= MaxReported)
                        changes.Append(key).Append('=').Append(old).Append("->").Append(n).Append(' ');
                }
                if (n >= 0) _previousCounts[key] = n;
                if (n >= MinReported) rows.Add(new KeyValuePair<string, long>(Label(fields[i]), n));
                long b = MeasureBytes(fields[i]);
                if (b >= MinReported)
                    rows.Add(new KeyValuePair<string, long>(Label(fields[i]) + "Bytes", b));
            }
            rows.Sort(delegate (KeyValuePair<string, long> a, KeyValuePair<string, long> b)
            { return b.Value.CompareTo(a.Value); });
            var sb = new System.Text.StringBuilder();
            int shown = Math.Min(rows.Count, MaxReported);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(rows[i].Key).Append('=').Append(rows[i].Value);
            }
            Log("[ret] " + tag + " " + side + " changed=" + changed + " " + changes);
            Log("[ret] " + tag + " " + side + " entries>=" + MinReported + ": " +
                rows.Count + "  " + sb);
        }

        // Called from MemoryProbe.Snapshot for every snapshot tag. The GC-reason
        // histogram is dated here only when it moved, so the watch interval still
        // yields exactly one line; the full inventory dump rides along with the
        // preset-cleanup pair, which is the once-per-load point.
        internal static void SnapshotHook(string tag)
        {
            try
            {
                string h = GcHistogramIfChanged();
                if (h != null) Log("[ret] watch gcHist " + h);
            }
            catch { }
            // Reports are queued by preset completion and GC instead of relying
            // on the optional janitor UUA coroutine, which often never runs.
        }

        private static long _requested, _reported;
        private static float _reportAfter;
        private static string _pendingReason;
        private static Dictionary<string, long> _previousCounts;

        // Notifications only queue a scalar ticket; no census in Restore or GC.
        internal static void Request(string reason)
        {
            _requested++;
            _pendingReason = reason;
            _reportAfter = UnityEngine.Time.realtimeSinceStartup + 2f;
        }

        internal static void Tick()
        {
            if (_reported == _requested || UnityEngine.Time.realtimeSinceStartup < _reportAfter) return;
            var sc = SuperController.singleton;
            if (sc == null || sc.isLoading || SceneLoadAccelerator.SceneLoadActive ||
                LoadWindow.PresetBusy || WardrobeJanitor.ImagesBusy()) return;
            long batch = _requested - _reported;
            _reported = _requested;
            string tag = "settled-" + _reported;
            Log("[ret] " + tag + " notifications=" + batch + " reason=" + _pendingReason);
            Report(tag);
            UiAssistHudLink.ReportPdMemory();
        }

        internal static void Report(string tag)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                MeshOwnerRetentionProbe.Request(tag);
                GpuPhysicsRetentionProbe.Request(tag);
                Emit(tag, "own", OwnFields());
                List<FieldInfo> game = GameFields();
                if (game != null) Emit(tag, "game", game);
                else Log("[ret] " + tag + " game half skipped (GameScan off)");
            }
            catch (Exception e)
            {
                Log("[ret] " + tag + " walk failed: " + e.GetType().Name + ": " + e.Message);
            }
            try
            {
                Log("[ret] " + tag + " " + Histogram() +
                    " heapMiB=" + (GC.GetTotalMemory(false) / 1048576) +
                    " censusMs=" + clock.ElapsedMilliseconds +
                    " (run after load N and after load N+1, then diff the two lines)");
            }
            catch { }
        }

        internal static void DeepReport(string tag)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Log("[ret] " + tag + " deep scan starting: GameObject/Component " +
                    "enumeration can freeze the frame for seconds");
                long go = Count<UnityEngine.GameObject>();
                long tr = Count<UnityEngine.Transform>();
                long co = Count<UnityEngine.Component>();
                long mb = Count<UnityEngine.MonoBehaviour>();
                Log("[ret] " + tag + " deep gameObject=" + go + " transform=" + tr +
                    " component=" + co + " monoBehaviour=" + mb +
                    " heapMiB=" + (GC.GetTotalMemory(false) / 1048576) +
                    " censusMs=" + clock.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                Log("[ret] deep failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static long Count<T>() where T : UnityEngine.Object
        {
            try { return UnityEngine.Resources.FindObjectsOfTypeAll<T>().Length; }
            catch { return -1L; }
        }

        private static void Log(string message)
        {
            try
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo(message);
            }
            catch { }
        }
    }
}
