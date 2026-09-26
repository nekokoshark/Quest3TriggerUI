using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Explicit, time-bounded observations only: no GC, cache eviction or scene action.
    // Scope deltas are GLOBAL net managed bytes, not a thread allocation profiler.
    internal static class LoadAttributionProbe
    {
        private const string DirectoryPath = @"F:\vam1.22.0.12\工作区\load_attribution_20260926";
        private const string TriggerPath = @"F:\vam1.22.0.12\BepInEx\plugins\Quest3TriggerUI\load_attribution_probe.txt";
        private static StreamWriter _writer;
        private static float _nextCheck, _stop, _nextReport;
        private static bool _sample;
        private static int _frames, _stage, _gc, _probeRows, _ownerIndex;
        private static int _sensorErrors, _poseErrors, _otherErrors;
        private static long _before, _ticks;
        private static readonly long[] Net = new long[10], Ticks = new long[10];
        private static readonly int[] Calls = new int[10], CrossGc = new int[10];
        private static readonly string[] Labels = { "uiassist", "scene-browser", "warmup-tools", "diagnostics-audio", "wardrobe", "preset-gc", "input-physics", "keyboard", "radial-modes", "gestures" };
        private static Component[] _owners;
        private static FieldInfo[] _ownerCountFields;
        private static readonly Dictionary<int, int> Owned = new Dictionary<int, int>();
        private static Dictionary<string, Texture2D> _cache;
        private static Dictionary<Texture2D, int> _counts;
        private static double Ms(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }
        private static string Clean(string text) { return (text ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }

        internal static void BeginFrame()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_writer == null)
                {
                    if (now < _nextCheck) return;
                    _nextCheck = now + 2f;
                    if (!File.Exists(TriggerPath)) return;
                    int seconds;
                    if (!int.TryParse(File.ReadAllText(TriggerPath).Trim(), out seconds) || seconds < 10 || seconds > 900)
                        throw new InvalidDataException("Probe duration must be 10..900 seconds");
                    if (SuperController.singleton == null || SuperController.singleton.isLoading || WardrobeJanitor.ImagesBusy()) return;
                    File.Delete(TriggerPath);
                    Directory.CreateDirectory(DirectoryPath);
                    _writer = new StreamWriter(Path.Combine(DirectoryPath, "probe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".tsv"), false);
                    _stop = now + seconds; _nextReport = now + 10f;
                    _frames = _probeRows = 0;
                    _sensorErrors = _poseErrors = _otherErrors = 0;
                    Application.logMessageReceivedThreaded += ObserveLog;
                    Array.Clear(Net, 0, Net.Length); Array.Clear(Ticks, 0, Ticks.Length);
                    Array.Clear(Calls, 0, Calls.Length); Array.Clear(CrossGc, 0, CrossGc.Length);
                    _writer.WriteLine("START\t" + DateTime.Now.ToString("o") + "\tduration=" + seconds + "\tScope bytes are global net deltas; GC-crossing scopes excluded; every frame. Census covers native material receivers, not all possible third-party owners.");
                    TextureCacheEstimate.Probe = TraceProbe;
                    StartCensus();
                }
                if (now >= _stop) { Shutdown(); return; }
                CensusSlice();
                if (now >= _nextReport)
                {
                    Report(); _nextReport = now + 10f;
                }
                ++_frames; _sample = true;
                _stage = 0;
                if (_sample) { _gc = GC.CollectionCount(0); _before = GC.GetTotalMemory(false); _ticks = Stopwatch.GetTimestamp(); }
            }
            catch (Exception e) { Fail(e); }
        }

        internal static void Mark(int next)
        {
            if (!_sample) return;
            long now = Stopwatch.GetTimestamp(), bytes = GC.GetTotalMemory(false);
            int gc = GC.CollectionCount(0);
            if (_gc == gc) { Net[_stage] += bytes - _before; Ticks[_stage] += now - _ticks; Calls[_stage]++; }
            else CrossGc[_stage]++;
            _stage = next; _before = bytes; _ticks = now; _gc = gc;
        }
        internal static void EndFrame() { Mark(0); _sample = false; }

        private static void TraceProbe(int phase, long ticks, int gc, string path)
        {
            if (_writer == null || _probeRows >= 4096) return;
            try
            {
                _writer.WriteLine("CACHE_PROBE\t" + phase + "\t" + Ms(ticks).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "\tgc=" + gc + "\t" + Clean(path));
                _probeRows++;
            }
            catch (Exception e) { Fail(e); }
        }
        private static void Report()
        {
            _writer.WriteLine("ERROR_COUNTS\tSensor=" + System.Threading.Interlocked.Exchange(ref _sensorErrors, 0) +
                "\tPose=" + System.Threading.Interlocked.Exchange(ref _poseErrors, 0) +
                "\tOther=" + System.Threading.Interlocked.Exchange(ref _otherErrors, 0));
            _writer.WriteLine("SAMPLE\t" + DateTime.Now.ToString("o") + "\tused=" + GC.GetTotalMemory(false) + "\tgc=" + GC.CollectionCount(0) + "\timagesBusy=" + WardrobeJanitor.ImagesBusy() + "\tpendingWrite=" + TextureCacheWriteBudget.PendingBytes());
            for (int i = 0; i < Labels.Length; i++)
            {
                _writer.WriteLine("SCOPE\t" + Labels[i] + "\tcalls=" + Calls[i] + "\tnetBytes=" + Net[i] + "\tms=" + Ms(Ticks[i]).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "\tgcExcluded=" + CrossGc[i]);
                Net[i] = Ticks[i] = 0; Calls[i] = CrossGc[i] = 0;
            }
            _writer.Flush();
        }
        private static void StartCensus()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var loader = ImageLoaderThreaded.singleton;
            _cache = (Dictionary<string, Texture2D>)typeof(ImageLoaderThreaded).GetField("textureCache", flags).GetValue(loader);
            _counts = (Dictionary<Texture2D, int>)typeof(ImageLoaderThreaded).GetField("textureUseCount", flags).GetValue(loader);
            foreach (var pair in _cache)
            {
                var t = pair.Value; if (t == null) continue;
                int count; _counts.TryGetValue(t, out count);
                _writer.WriteLine("TEXTURE\t" + t.GetInstanceID() + "\trefs=" + count + "\t" + t.width + "x" + t.height + "\t" + t.format + "\tmips=" + t.mipmapCount + "\t" + Clean(pair.Key));
            }
            var skin = Resources.FindObjectsOfTypeAll<DAZCharacterTextureControl>();
            var materials = Resources.FindObjectsOfTypeAll<MaterialOptions>();
            _owners = new Component[skin.Length + materials.Length];
            Array.Copy(skin, 0, _owners, 0, skin.Length); Array.Copy(materials, 0, _owners, skin.Length, materials.Length);
            _ownerCountFields = new[] { typeof(DAZCharacterTextureControl).GetField("textureUseCount", flags), typeof(MaterialOptions).GetField("textureUseCount", flags) };
            _ownerIndex = 0; Owned.Clear();
            _writer.WriteLine("CENSUS_BEGIN\t" + _owners.Length + "\tIncludes inactive/pooled native receivers; labels include hierarchy active state.");
        }
        private static void CensusSlice()
        {
            if (_owners == null) return;
            long begin = Stopwatch.GetTimestamp();
            for (int n = 0; n < 64 && _ownerIndex < _owners.Length; n++)
            {
                Component owner = _owners[_ownerIndex]; _owners[_ownerIndex++] = null;
                if (owner == null) continue;
                var field = _ownerCountFields[owner is DAZCharacterTextureControl ? 0 : 1];
                var counts = field.GetValue(owner) as Dictionary<Texture2D, int>;
                if (counts == null || counts.Count == 0) continue;
                Atom atom = owner.GetComponentInParent<Atom>();
                string label = (atom == null ? "(no atom)" : atom.uid) + "/" + owner.name + "/" + owner.GetType().Name;
                foreach (var pair in counts)
                {
                    if (pair.Key == null) continue;
                    int id = pair.Key.GetInstanceID(), sum;
                    Owned.TryGetValue(id, out sum); Owned[id] = sum + pair.Value;
                    _writer.WriteLine("OWNER\t" + id + "\trefs=" + pair.Value + "\tactive=" + owner.gameObject.activeInHierarchy + "\tcomponent=" + owner.GetInstanceID() + "\t" + Clean(label));
                }
                if (Stopwatch.GetTimestamp() - begin >= Stopwatch.Frequency / 500) break;
            }
            if (_ownerIndex < _owners.Length) return;
            foreach (var pair in _cache)
            {
                if (pair.Value == null) continue;
                int id = pair.Value.GetInstanceID(), observed, native;
                Owned.TryGetValue(id, out observed); _counts.TryGetValue(pair.Value, out native);
                _writer.WriteLine("OWNERSHIP_SUM\t" + id + "\tnative=" + native + "\tobserved=" + observed + "\t" + Clean(pair.Key));
            }
            _writer.WriteLine("CENSUS_END\tNot atomic across frames; unmatched count is a lead, not proof of orphan.");
            ClearCensus();
        }
        private static void ClearCensus() { _owners = null; _ownerCountFields = null; _cache = null; _counts = null; Owned.Clear(); }
        private static void ObserveLog(string message, string stack, LogType type)
        {
            // Only counters; no retained message/stack, formatting, or logging recursion.
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (stack != null && stack.IndexOf("PrettyFrank.Sensor", StringComparison.Ordinal) >= 0)
                System.Threading.Interlocked.Increment(ref _sensorErrors);
            else if (message != null && message.IndexOf("GetPoseActionData", StringComparison.Ordinal) >= 0)
                System.Threading.Interlocked.Increment(ref _poseErrors);
            else System.Threading.Interlocked.Increment(ref _otherErrors);
        }
        internal static void Shutdown()
        {
            Application.logMessageReceivedThreaded -= ObserveLog;
            _sample = false; TextureCacheEstimate.Probe = null; ClearCensus();
            if (_writer == null) return;
            try { Report(); _writer.WriteLine("END\t" + DateTime.Now.ToString("o")); }
            finally { _writer.Dispose(); _writer = null; }
        }
        private static void Fail(Exception e)
        {
            Application.logMessageReceivedThreaded -= ObserveLog;
            _sample = false; TextureCacheEstimate.Probe = null; ClearCensus();
            if (_writer != null) { _writer.Dispose(); _writer = null; }
            _nextCheck = Time.realtimeSinceStartup + 10f;
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogWarning("[load-attribution] stopped: " + e.Message);
        }
    }
}
