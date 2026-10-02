using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx.Configuration;

namespace Quest3TriggerUI
{
    // VaM stores an uncompressed RGBA32/RGB24 disk cache whenever the source image has
    // non power-of-two dimensions, and every later load then reads four times the bytes a
    // BC7 entry needs. This module re-encodes such an entry just after the game finished
    // reading it and swaps the pair in afterwards:
    //   * the swap waits until the native texture cache still holds the entry, so no
    //     loader thread can be reading that pair at that moment;
    //   * the new files are staged next to the old ones, so an interrupted swap is
    //     completed or rolled back at the next start instead of leaving a meta/data
    //     mismatch behind (a mismatch would make the image fail to load);
    //   * candidates come from the existing metadata observer, so nothing is scanned.
    internal static class TextureCacheBc7Convert
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> ToolPath;
        internal static ConfigEntry<bool> DiagnoseLayout;
        internal static ConfigEntry<int> IdleSeconds;
        internal static ConfigEntry<bool> Immediate;
        internal static ConfigEntry<bool> ConvertOnExit;

        private const string TempSuffix = ".bc7new";
        private const string PendingName = "pending.txt";
        private const int MaxPending = 4000;
        private const uint MoveReplaceExisting = 0x1;
        private const long MinRawBytes = 256L << 10;
        private const long MaxRawBytes = 256L << 20;
        private const int MaxQueue = 2048;
        private const double JobTimeoutSeconds = 180.0;
        private const double StartSpacingSeconds = 15.0;
        // Immediate mode drains the queue instead of waiting for a long idle
        // window, so the spacing only has to keep the worker from monopolising
        // a core back to back. Encoding still runs below normal priority and
        // the swap still needs the load window to be clear.
        private const double ImmediateSpacingSeconds = 1.0;
        private const int DdsHeader = 148;

        private static readonly object Sync = new object();
        private static readonly Queue<Job> Waiting = new Queue<Job>();
        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex Format = new Regex("\"format\"\\s*:\\s*\"([A-Za-z0-9]+)\"", RegexOptions.CultureInvariant);
        private static readonly System.Reflection.PropertyInfo Signature = typeof(ImageLoaderThreaded.QueuedImage).GetProperty(
            "cacheSignature", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);

        private sealed class Job
        {
            internal string data, meta, key, format;
            internal int width, height, mips;
            internal int targetWidth, targetHeight, targetMips;
            internal long rawBytes, dataStamp, metaStamp, encodedBytes;
            internal volatile bool encoded, swapped;
            internal bool keepNative;
        }

        private static Job _encoding, _ready;
        private static Thread _worker;
        private static Process _tool;
        private static volatile bool _stop;
        private static string _toolPath, _workDir;
        private static bool _installed, _toolMissing, _toolLogged;
        private static int _probeStep;
        private static long _nextStart, _observed, _queued, _converted, _failed, _dropped, _keptNative;
        private static string _pendingPath;
        private static long _lastActivity;
        private static int _activityFrame = -1;
        private static string _lastMouse;
        private static volatile bool _allowEncode;

        private static long Now() { return Stopwatch.GetTimestamp(); }
        private static bool Elapsed(long since, double seconds)
        {
            return Stopwatch.GetTimestamp() - since >= (long)(Stopwatch.Frequency * seconds);
        }

        internal static void Install()
        {
            if (_installed) return;
            // A timed-out shutdown worker must retain its stop signal.
            if (_worker != null && _worker.IsAlive) { Log("previous converter still retiring"); return; }
            _worker = null;
            _stop = false;
            try
            {
                Bc7CacheLoadCompatibility.Install();
                _workDir = Path.Combine(Path.GetTempPath(), "q3bc7");
                _pendingPath = Path.Combine(_workDir, PendingName);
                _lastActivity = Now();
                TextureCacheEstimate.AddObserver(Observe);
                _installed = true;
                RecoverInterrupted();
                Log("installed; tool=" + (Tool() ?? "missing") + "; queue from cache reads only; swap needs a cached texture");
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e.Message); }
        }

        internal static void Shutdown()
        {
            _stop = true;
            _installed = false;
            Bc7CacheLoadCompatibility.Shutdown();
            try { TextureCacheEstimate.RemoveObserver(Observe); } catch { }
            var worker = _worker;
            var tool = _tool;
            if (tool != null) { try { if (!tool.HasExited) tool.Kill(); } catch { } }
            if (worker != null) { try { worker.Join(2000); } catch { } }
            if (worker == null || !worker.IsAlive) { _worker = null; _tool = null; }
            try { Flush(); } catch { }
            lock (Sync) { Waiting.Clear(); Known.Clear(); _encoding = null; _ready = null; }
            _allowEncode = false;
        }

        private static bool Active() { return Enabled == null || Enabled.Value; }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[bc7-cache] " + message);
        }

        private static string Name(string path)
        {
            try { return Path.GetFileName(path); } catch { return path; }
        }

        private static string Tool()
        {
            if (_toolMissing) return null;
            if (_toolPath != null) return _toolPath;
            var candidates = new List<string>();
            var configured = ToolPath == null ? null : ToolPath.Value;
            if (!string.IsNullOrEmpty(configured)) candidates.Add(configured);
            try { candidates.Add(Path.Combine(Path.Combine(BepInEx.Paths.PluginPath, "Quest3TriggerUI"), "texconv.exe")); } catch { }
            candidates.Add(@"C:\Program Files\XnViewMP\plugins\texconv.exe");
            foreach (var candidate in candidates)
            {
                try { if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) { _toolPath = candidate; return _toolPath; } }
                catch { }
            }
            _toolMissing = true;
            if (!_toolLogged)
            {
                _toolLogged = true;
                Log("texconv.exe not found; automatic BC7 conversion is off (set Bc7ToolPath to enable)");
            }
            return null;
        }

        // Runs on the loader thread; keeps only cheap bookkeeping.
        private static void Observe(ImageLoaderThreaded.QueuedImage q, string path, string text,
            long metaStamp, long dataStamp, long bytes)
        {
            if (!_installed || !Active()) return;
            try
            {
                if (q == null || string.IsNullOrEmpty(path) || bytes < MinRawBytes || bytes > MaxRawBytes) return;
                if (q.isNormalMap || q.createNormalFromBump || q.createAlphaFromGrayscale || q.linear ||
                    !Bc7CacheCompatibility.ColourCache(path)) return;
                string format = Format.Match(text ?? string.Empty).Groups[1].Value;
                if (format != "RGBA32" && format != "RGB24") return;
                int width, height;
                if (!TextureCacheEstimate.TryDimensions(text, out width, out height)) return;
                if (Signature == null) return;
                string key = Signature.GetValue(q, null) as string;
                if (string.IsNullOrEmpty(key)) return;
                _observed++;
                lock (Sync)
                {
                    if (Known.Count >= MaxQueue || !Known.Add(path)) return;
                    Waiting.Enqueue(new Job
                    {
                        data = path, meta = path + "meta", key = key, format = format,
                        width = width, height = height, rawBytes = bytes,
                        dataStamp = dataStamp, metaStamp = metaStamp
                    });
                    _queued++;
                }
            }
            catch { }
        }

        internal static void Tick()
        {
            if (!_installed) return;
            if (DiagnoseLayout != null && DiagnoseLayout.Value)
            {
                if (_probeStep >= 0) { RunProbeStep(); return; }
            }
            else _probeStep = -1;
            if (!Active() || Tool() == null) return;
            EnsureWorker();
            SampleActivity();
            _allowEncode = Window() && (ImmediateOn() || IdleEnough()) && !WardrobeJanitor.ImagesBusy();
            var job = _ready;
            if (job == null) return;
            if (!Window()) return;
            if (!Resident(job)) return;
            if (!Fresh(job)) { Drop(job); return; }
            Swap(job);
        }

        private static bool Window()
        {
            var sc = SuperController.singleton;
            bool ok = sc != null && !sc.isLoading && !SceneLoadAccelerator.SceneLoadActive;
            if (ok && LoadWindow.PresetBusy)
            {
                LoadWindow.NoteDeferred("Bc7Conv");
                return false;
            }
            return ok;
        }

        // The native texture cache is the proof that the loader is past this pair: while
        // the texture is resident the file is not read again (a repeated request reuses
        // the instance and only the ref count reaching zero destroys it).
        private static bool Resident(Job job)
        {
            try
            {
                var loader = ImageLoaderThreaded.singleton;
                return loader != null && loader.IsTextureCached(job.key);
            }
            catch { return false; }
        }

        private static bool Fresh(Job job)
        {
            try
            {
                var data = new FileInfo(job.data);
                var meta = new FileInfo(job.meta);
                return data.Exists && meta.Exists && data.Length == job.rawBytes &&
                    data.LastWriteTimeUtc.Ticks == job.dataStamp && meta.LastWriteTimeUtc.Ticks == job.metaStamp;
            }
            catch { return false; }
        }

        private static void EnsureWorker()
        {
            if (_worker != null) return;
            var worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Priority = ThreadPriority.BelowNormal;
            worker.Name = "q3-bc7-cache";
            _worker = worker;
            worker.Start();
        }

        private static void Loop()
        {
            while (!_stop)
            {
                Job job = null;
                lock (Sync)
                {
                    if (_ready == null && _encoding == null && Waiting.Count > 0 && Now() >= _nextStart && _allowEncode)
                        job = Waiting.Dequeue();
                    if (job != null) _encoding = job;
                }
                if (job == null) { Thread.Sleep(250); continue; }
                _nextStart = Now() + (long)(Stopwatch.Frequency * Spacing());
                bool ok = false;
                try { ok = Encode(job); }
                catch (Exception e) { Log("encode failed " + Name(job.data) + ": " + e.Message); }
                job.encoded = ok;
                lock (Sync)
                {
                    if (_encoding == job) _encoding = null;
                    if (_stop) ok = false;
                    if (ok) _ready = job;
                }
                if (!ok) { Finish(job, job.keepNative ? 3 : 1, true); continue; }
                long deadline = Now() + (long)(Stopwatch.Frequency * JobTimeoutSeconds);
                while (!_stop && !job.swapped && Now() < deadline) Thread.Sleep(200);
                lock (Sync) { if (_ready == job) _ready = null; }
                if (!job.swapped) Finish(job, 2, true);
            }
        }

        private static bool Encode(Job job)
        {
            string tool = _toolPath;
            if (tool == null) return false;
            var info = new FileInfo(job.data);
            if (!info.Exists || info.Length != job.rawBytes) return false;
            int bpp = job.format == "RGB24" ? 3 : 4;
            int mips = DetectMips(job.width, job.height, info.Length, bpp);
            if (mips <= 0) { Log("mip chain unknown for " + Name(job.data) + " " + job.width + "x" + job.height + " len=" + info.Length); return false; }
            job.mips = mips;
            long expected;
            if (!Bc7CacheCompatibility.Plan(job.width, job.height, mips, job.rawBytes,
                out job.targetWidth, out job.targetHeight, out job.targetMips, out expected))
            {
                job.keepNative = true;
                Log("compatibility: keep native " + Name(job.data) + " (partial mip chain or POT BC7 saves no space)");
                return false;
            }
            Func<bool> stopped = delegate { return _stop; };
            if (!Bc7CacheCompatibility.Source(job.data, job.width, job.height, mips, bpp, stopped))
            {
                Log("compatibility: keep native " + Name(job.data) + " (not opaque colour)");
                job.keepNative = true;
                return false;
            }
            if (!Directory.Exists(_workDir)) Directory.CreateDirectory(_workDir);
            string inDds = Path.Combine(_workDir, "in.dds");
            string outDir = Path.Combine(_workDir, "out");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            TextureCacheStreamIO.WriteInput(job.data, inDds, job.width, job.height, mips, bpp, stopped);
            if (_stop || !RunTool(tool, inDds, outDir, job.targetMips, "BC7_UNORM", job.targetWidth, job.targetHeight) || _stop) return false;
            string outDds = Path.Combine(outDir, "in.DDS");
            if (!File.Exists(outDds)) { Log("tool produced no output for " + Name(job.data)); return false; }
            string verifyDir = Path.Combine(outDir, "verify");
            Directory.CreateDirectory(verifyDir);
            if (!RunTool(tool, outDds, verifyDir, job.targetMips, "R8G8B8A8_UNORM") || _stop) return false;
            if (!Bc7CacheCompatibility.Decoded(Path.Combine(verifyDir, "in.DDS"), job.targetWidth, job.targetHeight, job.targetMips, stopped))
            {
                Log("compatibility: keep native " + Name(job.data) + " (BC7 changes opaque alpha)");
                job.keepNative = true;
                return false;
            }
            TextureCacheStreamIO.ExtractBc7(outDds, job.data + TempSuffix,
                job.targetWidth, job.targetHeight, job.targetMips, expected, stopped);
            string converted = Bc7CacheCompatibility.Metadata(File.ReadAllText(job.meta), job.targetWidth, job.targetHeight);
            File.WriteAllText(job.meta + TempSuffix, converted, new UTF8Encoding(false));
            job.encodedBytes = expected;
            return true;
        }

        private static bool RunTool(string tool, string inDds, string outDir, int mips, string format = "BC7_UNORM", int width = 0, int height = 0)
        {
            var start = new ProcessStartInfo(tool)
            {
                Arguments = "-nologo -dx10 -f " + format +
                    (format == "BC7_UNORM" ? " -bcmax -aw 1024" : "") +
                    (width > 0 ? " -w " + width + " -h " + height : "") +
                    " -m " + mips + " -o \"" + outDir + "\" \"" + inDds + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null) return false;
                _tool = process;
                try
                {
                    try { process.StandardOutput.ReadToEnd(); } catch { }
                    try { process.StandardError.ReadToEnd(); } catch { }
                    if (!process.WaitForExit(300000)) { try { process.Kill(); } catch { } return false; }
                    return process.ExitCode == 0;
                }
                finally { _tool = null; }
            }
        }

        private static void Swap(Job job)
        {
            bool dataMoved = false;
            try
            {
                if (!MoveFileEx(job.data + TempSuffix, job.data, MoveReplaceExisting))
                    throw new IOException("data swap failed: " + Marshal.GetLastWin32Error());
                dataMoved = true;
                if (!MoveFileEx(job.meta + TempSuffix, job.meta, MoveReplaceExisting))
                    throw new IOException("meta swap failed: " + Marshal.GetLastWin32Error());
                job.swapped = true;
                Finish(job, 0, false);
                Log("converted " + Name(job.data) + " " + job.width + "x" + job.height + " mips=" + job.mips +
                    " -> " + job.targetWidth + "x" + job.targetHeight + " mips=" + job.targetMips +
                    " " + MiB(job.rawBytes) + "->" + MiB(job.encodedBytes) + "MiB");
            }
            catch (Exception e)
            {
                // Keep the staged meta when the data already moved: the next start
                // completes that swap instead of leaving a mismatched pair.
                Log("swap failed " + Name(job.data) + ": " + e.Message +
                    (dataMoved ? " (meta staged for startup repair)" : ""));
                Finish(job, 1, !dataMoved);
            }
        }

        private static void Drop(Job job)
        {
            Finish(job, 2, true);
        }

        private static void Finish(Job job, int outcome, bool deleteTemps)
        {
            lock (Sync)
            {
                // Only a swapped pair leaves the list. Anything else stays queued for the
                // exit converter, which can finish it while nothing is loaded.
                if (outcome == 0) Known.Remove(job.data);
                if (_ready == job) _ready = null;
            }
            if (outcome == 0) _converted++;
            else if (outcome == 1) _failed++;
            else if (outcome == 3) _keptNative++;
            else _dropped++;
            if (deleteTemps)
            {
                TryDelete(job.data + TempSuffix);
                TryDelete(job.meta + TempSuffix);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string MiB(long bytes) { return ((bytes + 524288L) / 1048576L).ToString(); }

        private static long ChainPix(int w, int h, int mips, int bpp)
        {
            long sum = 0;
            for (int level = 0; level < mips; level++)
            {
                sum += (long)w * h * bpp;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            return sum;
        }

        private static long ChainBc(int w, int h, int mips, int block)
        {
            long sum = 0;
            for (int level = 0; level < mips; level++)
            {
                sum += (long)((w + 3) / 4) * ((h + 3) / 4) * block;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            return sum;
        }

        private static long ChainBcFloor(int w, int h, int mips, int block)
        {
            long sum = 0;
            for (int level = 0; level < mips; level++)
            {
                sum += (long)Math.Max(1, w / 4) * Math.Max(1, h / 4) * block;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            return sum;
        }

        private static int DetectMips(int w, int h, long length, int bpp)
        {
            for (int mips = 1; mips <= 20; mips++) if (ChainPix(w, h, mips, bpp) == length) return mips;
            return 0;
        }

        // A staged pair can only be half applied when the process died between the two
        // renames. Finish it when the data already holds the BC7 chain, drop it otherwise.
        private static void RecoverInterrupted()
        {
            string dir = null;
            try { dir = MVR.FileManagement.CacheManager.GetTextureCacheDir(); } catch { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            int finished = 0, removed = 0;
            foreach (var metaTemp in Directory.GetFiles(dir, "*.vamcachemeta" + TempSuffix))
            {
                string meta = metaTemp.Substring(0, metaTemp.Length - TempSuffix.Length);
                string data = meta.Substring(0, meta.Length - "meta".Length) + "cache";
                string dataTemp = data + TempSuffix;
                if (File.Exists(dataTemp)) { TryDelete(dataTemp); TryDelete(metaTemp); removed++; continue; }
                bool completes = false;
                try
                {
                    string text = File.ReadAllText(metaTemp);
                    int w, h;
                    if (Format.Match(text).Groups[1].Value == "BC7" &&
                        TextureCacheEstimate.TryDimensions(text, out w, out h))
                    {
                        long length = new FileInfo(data).Length;
                        for (int mips = 1; mips <= 20 && !completes; mips++)
                            completes = ChainBc(w, h, mips, 16) == length;
                    }
                }
                catch { }
                if (completes && MoveFileEx(metaTemp, meta, MoveReplaceExisting)) finished++;
                else { TryDelete(metaTemp); removed++; }
            }
            if (finished > 0 || removed > 0) Log("startup repair: finished=" + finished + " removed=" + removed);
        }

        internal static void Report()
        {
            Log("stats reads=" + _observed + " queued=" + _queued + " converted=" + _converted +
                " dropped=" + _dropped + " failed=" + _failed + " keptNative=" + _keptNative + " pending=" + Known.Count);
        }

        // Unity's own storage size for a block-compressed chain is the only authority on
        // the non multiple-of-four padding rule; native VaM never compresses such an image
        // (Finish() gates Compress on IsPowerOfTwo), so there is no cached example to read.
        private static readonly int[][] LayoutProbe = new int[][]
        {
            new int[] { 4096, 4096, 13 },
            new int[] { 2048, 2048, 12 },
            new int[] { 3000, 2250, 12 },
            new int[] { 3000, 1125, 12 },
            new int[] { 2400, 1502, 12 },
            new int[] { 1079, 1079, 11 },
            new int[] { 1300, 1298, 11 },
            new int[] { 1050, 1050, 11 },
            new int[] { 1125, 750, 11 },
            new int[] { 478, 1913, 11 },
            new int[] { 799, 799, 10 },
            new int[] { 278, 277, 9 },
            new int[] { 225, 225, 8 },
            new int[] { 17, 68, 7 },
            new int[] { 3, 1, 1 }
        };

        private static void RunProbeStep()
        {
            if (_probeStep >= LayoutProbe.Length) { _probeStep = -1; return; }
            int[] probe = LayoutProbe[_probeStep++];
            int width = probe[0], height = probe[1], mips = probe[2];
            long ceil = ChainBc(width, height, mips, 16);
            long floor = ChainBcFloor(width, height, mips, 16);
            UnityEngine.Texture2D texture = null;
            bool loaded = false;
            string error = null;
            try
            {
                texture = new UnityEngine.Texture2D(width, height, UnityEngine.TextureFormat.BC7, mips > 1, false);
                texture.LoadRawTextureData(new byte[ceil]);
                texture.Apply(false);
                loaded = true;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
            long stored = -1;
            if (texture != null) { try { stored = texture.GetRawTextureData().LongLength; } catch { } }
            if (texture != null) UnityEngine.Object.Destroy(texture);
            Log("layout probe " + width + "x" + height + " mips=" + mips + " ceil=" + ceil + " floor=" + floor +
                " loaded=" + loaded + " stored=" + stored + (error == null ? "" : " error=" + error));
        }

        // Re-encoding costs CPU and GPU time, so it only runs once the scene has settled and
        // nothing has been touched for a while; the exit path covers everything else.
        private static void SampleActivity()
        {
            if (UnityEngine.Time.frameCount == _activityFrame) return;
            _activityFrame = UnityEngine.Time.frameCount;
            bool active = false;
            try
            {
                if (UnityEngine.Input.anyKey) active = true;
                UnityEngine.Vector3 mouse = UnityEngine.Input.mousePosition;
                string position = mouse.x + "," + mouse.y;
                if (_lastMouse != null && _lastMouse != position) active = true;
                _lastMouse = position;
            }
            catch { }
            try
            {
                float trigger, rightGrip, leftGrip, button;
                UnityEngine.Vector2 right, left;
                if (OpenVrInputBridge.TryGetInput(out trigger, out rightGrip, out leftGrip, out button, out right))
                    active |= trigger > 0.25f || rightGrip > 0.5f || leftGrip > 0.5f || button > 0.5f;
                if (OpenVrInputBridge.TryGetSticks(out right, out left))
                    active |= right.sqrMagnitude > 0.09f || left.sqrMagnitude > 0.09f;
            }
            catch { }
            if (active) _lastActivity = Now();
        }

        // Convert as soon as the load window closes rather than after a long
        // idle stretch: the texture this entry belongs to is still resident
        // right after its load, so the swap is far more likely to land, and
        // the encode is on the background worker either way.
        private static bool ImmediateOn()
        {
            return Immediate == null || Immediate.Value;
        }

        private static double Spacing()
        {
            return ImmediateOn() ? ImmediateSpacingSeconds : StartSpacingSeconds;
        }

        private static bool IdleEnough()
        {
            double seconds = IdleSeconds == null ? 0.0 : IdleSeconds.Value;
            if (seconds <= 0.0) return false;
            return _lastActivity != 0 && Elapsed(_lastActivity, seconds);
        }

        // The session list is what the exit converter works from: every entry this session
        // saw uncompressed and did not swap yet, merged with whatever a previous run left.
        private static void Flush()
        {
            if (string.IsNullOrEmpty(_pendingPath)) return;
            var paths = new List<string>();
            lock (Sync) { paths.AddRange(Known); }
            try
            {
                if (File.Exists(_pendingPath))
                    foreach (string line in File.ReadAllLines(_pendingPath))
                    {
                        string kept = line.Trim();
                        if (kept.Length > 0) paths.Add(kept);
                    }
            }
            catch { }
            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (unique.Count >= MaxPending) break;
                if (seen.Add(path)) unique.Add(path);
            }
            if (unique.Count == 0) { TryDelete(_pendingPath); return; }
            if (!Directory.Exists(_workDir)) Directory.CreateDirectory(_workDir);
            File.WriteAllLines(_pendingPath, unique.ToArray(), new UTF8Encoding(false));
            Log("session list: " + unique.Count + " entries at " + _pendingPath);
        }

        internal static void OnExit()
        {
            if (!_installed) return;
            Report();
            try { Flush(); } catch (Exception e) { Log("session list failed: " + e.Message); }
            if (ConvertOnExit == null || !ConvertOnExit.Value) return;
            if (string.IsNullOrEmpty(_pendingPath) || !File.Exists(_pendingPath)) return;
            try { Launch(); } catch (Exception e) { Log("exit converter failed to start: " + e.Message); }
        }

        // Started hidden and unhooked: it waits for this process to disappear, then converts
        // the list one entry at a time. No window, no console, no prompt.
        private static void Launch()
        {
            string script = Script();
            if (script == null)
            {
                Log("converter script is not next to the plugin; " + Name(_pendingPath) + " is kept for the next exit");
                return;
            }
            string tool = Tool();
            var arguments = new StringBuilder();
            arguments.Append("-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"");
            arguments.Append(script).Append("\" -List \"").Append(_pendingPath).Append('"');
            if (tool != null) arguments.Append(" -Tool \"").Append(tool).Append('"');
            var start = new ProcessStartInfo("powershell.exe")
            {
                Arguments = arguments.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var process = Process.Start(start);
            Log("exit converter started pid=" + (process == null ? 0 : process.Id) + " script=" + Name(script));
        }

        private static string Script()
        {
            try
            {
                string path = Path.Combine(Path.Combine(BepInEx.Paths.PluginPath, "Quest3TriggerUI"), "bc7_pending_convert.ps1");
                return File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string destination, uint flags);
    }
}
