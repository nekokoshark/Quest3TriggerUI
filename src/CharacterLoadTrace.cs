using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace Quest3TriggerUI
{
    // The optimisation plan asks for one trace per user-visible character load, covering
    // the four situations (first Person of a session, appearance on an existing Person,
    // another Person with a warm disk cache, the same Person again). Those runs are only
    // comparable when every phase the plugin and the native loader report is attached to
    // the same request, so this subscribes to the log stream and tags the phases it
    // already prints instead of re-timing each module by hand. Rows are appended to
    // Quest3TriggerUI.charactertrace_<stamp>.tsv next to the other diagnostics.
    internal static class CharacterLoadTrace
    {
        internal static ConfigEntry<bool> Enabled;

        private const int RowCap = 6000;
        private const int DetailCap = 320;
        private const int AceRowCap = 24;
        private const double HitchSeconds = 0.04;
        private const double SettleSeconds = 1.0;
        private const double TimeoutSeconds = 300.0;

        private static readonly Regex Restore = new Regex(@"restoreMs=(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CacheProbe = new Regex(@"cacheProbeMs=(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex TexCycle = new Regex(@"admitted=(\d+) deferred=(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CacheSized = new Regex(@"cacheSized=(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Releases = new Regex(@"releases=(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Gc = new Regex(@"native GC ms=(\d+); reason=([^;]*); managedMiB=(\d+)->(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Sweep = new Regex(@"sweepSubmitMs=(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SweepWait = new Regex(@"native sweep settled: waitMs=(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private sealed class Listener : ILogListener
        {
            public void Dispose() { }

            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                try
                {
                    object data = eventArgs == null ? null : eventArgs.Data;
                    if (data != null) Observe(data.ToString());
                }
                catch { }
            }
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, int> TagRows = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly object WriterLock = new object();
        private static StreamWriter _writer;
        private static string _path;
        private static Listener _listener;
        private static bool _installed, _active;
        private static int _id, _frames, _rows, _hitches;
        private static long _begin, _lastRow;
        private static long _managed0, _managedEnd, _peakManaged, _gc0;
        private static long _ws0, _wsEnd;
        private static int _cycles, _admitted, _deferred, _cacheSized, _releases, _sweeps, _skippedSweeps, _gcCount, _restores;
        private static long _restoreLastMs, _cacheProbeMs, _sweepMs, _sweepWaitMs, _gcMs;
        private static string _kind = "-", _preset = "-", _atom = "-";

        private static string MiB(long bytes) { return (bytes / (1024L * 1024L)).ToString(CultureInfo.InvariantCulture); }

        // Process.WorkingSet64 / Environment.WorkingSet 在这台机器的 Mono 上
        // 都返回 0（上一版 wsMiB 列全是 0 的原因），所以直接问内核。
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint cb;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool K32GetProcessMemoryInfo(
            IntPtr process, out ProcessMemoryCounters counters, int size);

        private static long WorkingSet()
        {
            try
            {
                ProcessMemoryCounters counters;
                if (K32GetProcessMemoryInfo(GetCurrentProcess(), out counters,
                        System.Runtime.InteropServices.Marshal.SizeOf(
                            typeof(ProcessMemoryCounters))))
                    return counters.WorkingSetSize.ToInt64();
            }
            catch { }
            try { return Process.GetCurrentProcess().WorkingSet64; } catch { return 0; }
        }

        private static string Short(string path)
        {
            if (string.IsNullOrEmpty(path)) return "-";
            try { return Path.GetFileName(path); } catch { return path; }
        }

        internal static void Install()
        {
            if (_installed) return;
            try
            {
                _path = Path.Combine(BepInEx.Paths.ConfigPath,
                    "Quest3TriggerUI.charactertrace_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".tsv");
                _writer = new StreamWriter(_path, false, new UTF8Encoding(false)) { AutoFlush = true };
                _installed = true;
                var listener = new Listener();
                BepInEx.Logging.Logger.Listeners.Add(listener);
                _listener = listener;
                Note("session", "version=" + Quest3TriggerUIPlugin.PluginVersion + " managedMiB=" + MiB(GC.GetTotalMemory(false)) +
                    " wsMiB=" + MiB(WorkingSet()));
                if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[load-trace] " + _path);
            }
            catch (Exception e) { Stop(); Log("not installed: " + e.Message); }
        }

        internal static void Shutdown() { Stop(); }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[load-trace] " + message);
        }

        private static void Stop()
        {
            _active = false;
            try { if (_listener != null) BepInEx.Logging.Logger.Listeners.Remove(_listener); } catch { }
            _listener = null;
            try { if (_writer != null) _writer.Dispose(); } catch { }
            _writer = null;
            _installed = false;
        }

        private static bool Active() { return Enabled == null || Enabled.Value; }

        // One row per user action that loads a character preset. Superseding an open
        // window is normal when presets are clicked faster than one settles.
        internal static void Begin(string kind, string path, string atom)
        {
            if (!_installed || !Active()) return;
            lock (Sync)
            {
                if (_active) Close("superseded");
                _id++;
                _kind = kind ?? "-";
                _preset = Short(path);
                _atom = string.IsNullOrEmpty(atom) ? "-" : atom;
                _begin = Stopwatch.GetTimestamp();
                _frames = 0; _hitches = 0;
                _cycles = 0; _admitted = 0; _deferred = 0; _cacheSized = 0;
                _releases = 0; _sweeps = 0; _skippedSweeps = 0; _gcCount = 0; _restores = 0;
                _restoreLastMs = 0; _cacheProbeMs = 0; _sweepMs = 0; _sweepWaitMs = 0; _gcMs = 0;
                _gc0 = GC.CollectionCount(0);
                _managed0 = GC.GetTotalMemory(false);
                _managedEnd = _managed0;
                _peakManaged = _managed0;
                _ws0 = WorkingSet();
                _wsEnd = _ws0;
                _active = true;
                Write("BEGIN", "kind=" + _kind + " preset=" + _preset + " atom=" + _atom +
                    " scene=" + SceneName() + " managedMiB=" + MiB(_managed0) + " wsMiB=" + MiB(_ws0) +
                    " gc0=" + _gc0, true);
            }
        }

        internal static void Note(string tag, string detail) { Write(tag, detail, true); }

        internal static void Tick()
        {
            if (!_installed || !_active) return;
            lock (Sync)
            {
                if (!_active) return;
                float frame = Time.unscaledDeltaTime;
                _frames++;
                long managed = GC.GetTotalMemory(false);
                _managedEnd = managed;
                if (managed > _peakManaged) _peakManaged = managed;
                if (frame >= HitchSeconds)
                {
                    _hitches++;
                    Write("hitch", "frameMs=" + (frame * 1000f).ToString("F1", CultureInfo.InvariantCulture) +
                        " imagesBusy=" + WardrobeJanitor.ImagesBusy() +
                        " pendingWriteMiB=" + MiB(TextureCacheWriteBudget.PendingBytes()) +
                        " managedMiB=" + MiB(managed), false);
                }
                long now = Stopwatch.GetTimestamp();
                if (Busy()) _lastRow = now;
                bool quiet = now - _lastRow >= (long)(Stopwatch.Frequency * SettleSeconds);
                if (!quiet) return;
                double elapsed = (now - _begin) / (double)Stopwatch.Frequency;
                Close(elapsed >= TimeoutSeconds ? "timeout" : "settled");
            }
        }

        private static bool Busy()
        {
            try
            {
                var sc = SuperController.singleton;
                if (sc != null && (sc.isLoading || SceneLoadAccelerator.SceneLoadActive)) return true;
            }
            catch { }
            try { return WardrobeJanitor.ImagesBusy(); } catch { return false; }
        }

        private static string SceneName()
        {
            try
            {
                var sc = SuperController.singleton;
                if (sc == null) return "-";
                return string.IsNullOrEmpty(sc.name) ? "-" : Path.GetFileNameWithoutExtension(sc.name);
            }
            catch { return "-"; }
        }

        private static void Close(string reason)
        {
            double elapsed = (Stopwatch.GetTimestamp() - _begin) / (double)Stopwatch.Frequency;
            Write("END", "reason=" + reason +
                " totalMs=" + (elapsed * 1000.0).ToString("F0", CultureInfo.InvariantCulture) +
                " frames=" + _frames +
                " hitches=" + _hitches +
                " restores=" + _restores + " restoreLastMs=" + _restoreLastMs +
                " cycles=" + _cycles + " admitted=" + _admitted + " deferred=" + _deferred + " cacheSized=" + _cacheSized +
                " releases=" + _releases + " sweeps=" + _sweeps + " sweepSkipped=" + _skippedSweeps +
                " sweepSubmitMs=" + _sweepMs + " sweepWaitMs=" + _sweepWaitMs +
                " cacheProbeMs=" + _cacheProbeMs +
                " gcs=" + _gcCount + " gcMs=" + _gcMs +
                " managedMiB=" + MiB(_managed0) + "->" + MiB(_managedEnd) + " peak=" + MiB(_peakManaged) +
                " wsMiB=" + MiB(_ws0) + "->" + MiB(_wsEnd), true);
            _active = false;
        }

        // Runs on whichever thread logs; every row is written immediately so a crash
        // still leaves the window that was already recorded.
        private static void Observe(string line)
        {
            if (string.IsNullOrEmpty(line) || _writer == null) return;
            try
            {
                bool hot = _active;
                if (line.IndexOf("[bc7-cache]", StringComparison.Ordinal) >= 0)
                {
                    Note("bc7", line);
                    return;
                }
                if (line.IndexOf("layout probe", StringComparison.Ordinal) >= 0)
                {
                    Note("probe", line);
                    return;
                }
                if (!hot && !Active()) return;
                if (line.IndexOf("preset candidate:", StringComparison.Ordinal) >= 0)
                {
                    Write("restore-begin", line, hot);
                    return;
                }
                if (line.IndexOf("preset completion:", StringComparison.Ordinal) >= 0)
                {
                    Match restore = Restore.Match(line);
                    if (restore.Success && hot) { _restores++; _restoreLastMs = long.Parse(restore.Groups[1].Value, CultureInfo.InvariantCulture); }
                    Write("restore-end", line, hot);
                    return;
                }
                if (line.IndexOf("native GC ms=", StringComparison.Ordinal) >= 0)
                {
                    Match gc = Gc.Match(line);
                    if (gc.Success && hot)
                    {
                        _gcCount++;
                        _gcMs += long.Parse(gc.Groups[1].Value, CultureInfo.InvariantCulture);
                    }
                    Write("gc", line, hot);
                    return;
                }
                if (line.IndexOf("run native character sweep:", StringComparison.Ordinal) >= 0)
                {
                    Match releases = Releases.Match(line);
                    if (releases.Success && hot) _releases = int.Parse(releases.Groups[1].Value, CultureInfo.InvariantCulture);
                    Match sweep = Sweep.Match(line);
                    if (sweep.Success && hot) _sweepMs += long.Parse(sweep.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (hot) _sweeps++;
                    Write("sweep", line, hot);
                    return;
                }
                if (line.IndexOf("native sweep settled:", StringComparison.Ordinal) >= 0)
                {
                    Match wait = SweepWait.Match(line);
                    if (wait.Success && hot) _sweepWaitMs += long.Parse(wait.Groups[1].Value, CultureInfo.InvariantCulture);
                    Write("sweep-settled", line, hot);
                    return;
                }
                if (line.IndexOf("skip unchanged preset UUA", StringComparison.Ordinal) >= 0)
                {
                    if (hot) _skippedSweeps++;
                    Write("sweep-skip", line, hot);
                    return;
                }
                if (line.IndexOf("[texture-budget]", StringComparison.Ordinal) >= 0)
                {
                    Match cycle = TexCycle.Match(line);
                    if (cycle.Success && hot)
                    {
                        _cycles++;
                        _admitted += int.Parse(cycle.Groups[1].Value, CultureInfo.InvariantCulture);
                        _deferred += int.Parse(cycle.Groups[2].Value, CultureInfo.InvariantCulture);
                    }
                    Match sized = CacheSized.Match(line);
                    if (sized.Success && hot) _cacheSized += int.Parse(sized.Groups[1].Value, CultureInfo.InvariantCulture);
                    Match probe = CacheProbe.Match(line);
                    if (probe.Success && hot) _cacheProbeMs += long.Parse(probe.Groups[1].Value, CultureInfo.InvariantCulture);
                    Write("texcycle", line, hot);
                    return;
                }
                if (line.IndexOf("[long-frame]", StringComparison.Ordinal) >= 0) { Write("longframe", line, hot); return; }
                if (line.IndexOf("[ACE] stats", StringComparison.Ordinal) >= 0) { Write("ace", line, hot); return; }
                if (line.IndexOf("LoadPreset invoked", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("Appearance loaded in one native pass", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("Person preset loaded from exact path", StringComparison.Ordinal) >= 0)
                {
                    Note("action", "scene=" + SceneName() + " " + line);
                }
            }
            catch { }
        }

        private static void Write(string tag, string detail, bool scoped)
        {
            if (_writer == null) return;
            try
            {
                int used;
                TagRows.TryGetValue(tag, out used);
                if (used >= (tag == "ace" ? AceRowCap : RowCap)) return;
                if (scoped && _rows >= RowCap) return;
                TagRows[tag] = used + 1;
                if (detail != null && detail.Length > DetailCap) detail = detail.Substring(0, DetailCap);
                long now = Stopwatch.GetTimestamp();
                double ms = _active ? (now - _begin) * 1000.0 / Stopwatch.Frequency : 0.0;
                var row = new StringBuilder(DetailCap + 64);
                row.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append('\t');
                row.Append(_active ? _id : 0).Append('\t');
                row.Append(ms.ToString("F0", CultureInfo.InvariantCulture)).Append('\t');
                row.Append(Time.frameCount).Append('\t');
                row.Append(tag).Append('\t');
                row.Append(detail);
                lock (WriterLock) _writer.WriteLine(row.ToString());
                _rows++;
                _lastRow = now;
                if (!_active) _wsEnd = WorkingSet();
            }
            catch { }
        }
    }
}
