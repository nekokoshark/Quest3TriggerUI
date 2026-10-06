using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Main-thread admission only. Never block a worker or touch Unity objects
    // from an image decoder. Reservations cover pending + decoded/waiting Finish.
    internal static class TextureDecodeBudget
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> BudgetMiB;
        internal static ConfigEntry<bool> EarlyDiscard;
        internal static Func<long> PendingCacheBytes;
        internal static Func<ImageLoaderThreaded.QueuedImage, long> ColdEstimate;
        private const long MiB = 1024L * 1024L;
        private static readonly Dictionary<ImageLoaderThreaded.QueuedImage, long> Held =
            new Dictionary<ImageLoaderThreaded.QueuedImage, long>();
        private static Harmony _harmony;
        private static long _reserved, _peak;
        // Main-thread scheduler receipt; worker bytes stay charged until Finish.
        internal static int PendingCount { get { return Held.Count; } }
        private static int _admitted, _deferred, _discarded;
        private static long _managedBytes, _bumpBytes;
        private static int _headerSized;
        private static long _headerTicks;
        private static long _nextMemoryCheck;
        private static ulong _available = ulong.MaxValue;
        private static ulong _commitAvailable = ulong.MaxValue;
        private static ulong _totalPhysical;
        private static bool _batch;
        private static long _heapStart;
        private static long _probeMaxTicks;
        // A probe can be suspended mid-read by a stop-the-world native GC:
        // PresetSweepGate runs a full GC on this very thread, so a probe that
        // happens to be in flight measures the freeze, not the I/O. Evidence
        // 2026-09-28 00:10 (probe_20260928_001028.tsv): one probe reported
        // 6186.9ms while the same window held a 6099ms native GC, and the
        // other 434 probes of that load summed to ~7ms. Classify by duration
        // so cacheProbeMs stays an I/O figure.
        private static readonly long ProbeSuspendTicks =
            50L * System.Diagnostics.Stopwatch.Frequency / 1000L;
        private static long _probeIoTicks;
        private static int _probeSuspended;
        private static string _probeWorstPath;
        private static int _gcStart0;
        private static WeakReference _probeHead;
        private static long _probeEstimate, _probeTicks, _probeDeadline;
        private static bool _probeHit;
        private static int _probesLeft = 4, _cacheSized;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            internal uint length, load;
            internal ulong totalPhysical, availablePhysical, totalPageFile,
                availablePageFile, totalVirtual, availableVirtual, extended;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        internal static void Install()
        {
            if (_harmony != null) return;
            DecodedBufferPool.Reactivate();
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo dispatch = typeof(ImageLoaderThreaded).GetMethod("DispatchPendingImages", flags);
                MethodInfo finish = typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Finish", flags);
                MethodInfo resolve = typeof(ImageLoaderThreaded).GetMethod("ResolveLatestPath", flags);
                MethodInfo decode = typeof(ImageLoaderThreaded.QueuedImage).GetMethod("ProcessFromStream", flags);
                if (dispatch == null || finish == null || resolve == null || decode == null ||
                    dispatch.GetMethodBody().LocalVariables.Count < 2 ||
                    dispatch.GetMethodBody().LocalVariables[1].LocalType != typeof(ImageLoaderThreaded.QueuedImage))
                    throw new MissingMethodException("image dispatch layout");
                _harmony = new Harmony("Quest3TriggerUI.texture-decode-budget");
                _harmony.UnpatchAll(_harmony.Id);
                // Install release before admission. A layout mismatch unpatches both.
                _harmony.Patch(finish, prefix: Patch("BeforeFinish"), finalizer: Patch("AfterFinish"));
                _harmony.Patch(resolve, finalizer: Patch("AfterResolve"));
                _harmony.Patch(dispatch, prefix: Patch("BeforeDispatch"), transpiler: Patch("DispatchTranspiler"), postfix: Patch("AfterDispatch"));
                ColdTextureBufferLayout.ValidateUpload();
                _harmony.Patch(decode, transpiler: Patch("DecodeTranspiler"));
                Log("installed: main-thread admission; estimated bytes held through Finish; native cache metadata sizing; pooled decode buffers; no quality changes");
            }
            catch (Exception e)
            {
                Shutdown();
                Log("not installed; native dispatch retained: " + e.Message);
            }
        }

        private static HarmonyMethod Patch(string name)
        {
            return new HarmonyMethod(typeof(TextureDecodeBudget).GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static IEnumerable<CodeInstruction> DispatchTranspiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);
            int site = -1, matches = 0;
            // Exact native handoff: remove head, ResolveLatestPath(q), then enqueue
            // processed/pending. Gate before removal, outside all monitor regions.
            for (int i = 4; i + 3 < code.Count; i++)
            {
                MethodInfo remove = code[i].operand as MethodInfo;
                MethodInfo resolve = code[i + 3].operand as MethodInfo;
                FieldInfo queue = code[i - 1].operand as FieldInfo;
                if (remove != null && remove.Name == "RemoveFirst" &&
                    remove.DeclaringType == typeof(LinkedList<ImageLoaderThreaded.QueuedImage>) &&
                    resolve != null && resolve.Name == "ResolveLatestPath" &&
                    resolve.DeclaringType == typeof(ImageLoaderThreaded) &&
                    code[i - 3].opcode == OpCodes.Ldarg_0 && code[i - 2].opcode == OpCodes.Volatile &&
                    code[i - 1].opcode == OpCodes.Ldfld && queue != null && queue.Name == "queuedImages" &&
                    code[i + 1].opcode == OpCodes.Ldarg_0 && code[i + 2].opcode == OpCodes.Ldloc_1)
                { site = i - 3; matches++; }
            }
            if (matches != 1 || code[code.Count - 1].opcode != OpCodes.Ret || code[site].blocks.Count != 0)
                throw new InvalidOperationException("native image dispatch anchor mismatch");
            Label exit = generator.DefineLabel();
            code[code.Count - 1].labels.Add(exit);
            var load = new CodeInstruction(OpCodes.Ldloc_1);
            load.labels.AddRange(code[site].labels);
            code[site].labels.Clear();
            code.InsertRange(site, new[] { load,
                new CodeInstruction(OpCodes.Call, typeof(TextureDecodeBudget).GetMethod("Admit",
                    BindingFlags.Static | BindingFlags.NonPublic)),
                new CodeInstruction(OpCodes.Brfalse, exit) });
            return code;
        }

        private static IEnumerable<CodeInstruction> DecodeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            // Only the first allocation stores QueuedImage.raw. Native bump
            // output remains separately pooled if the three-row path is off.
            var code = new List<CodeInstruction>(instructions);
            int matched = 0, compact = 0;
            MethodInfo rent = typeof(DecodedBufferPool).GetMethod("RentByteArray",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo coldRent = typeof(ColdTextureBufferLayout).GetMethod("RentDecoded",
                BindingFlags.Static | BindingFlags.NonPublic);
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Newarr || !Equals(code[i].operand, typeof(byte))) continue;
                var raw = i + 1 < code.Count ? code[i + 1].operand as FieldInfo : null;
                if (i + 1 < code.Count && code[i + 1].opcode == OpCodes.Stfld && raw != null &&
                    raw.Name == "raw" && raw.DeclaringType == typeof(ImageLoaderThreaded.QueuedImage))
                {
                    // Stack: [image for stfld, length] -> [..., length, image].
                    // Keep labels and exception blocks on the first replacement.
                    code[i].opcode = OpCodes.Ldarg_0; code[i].operand = null;
                    code.Insert(++i, new CodeInstruction(OpCodes.Call, coldRent));
                    compact++;
                }
                else { code[i].opcode = OpCodes.Call; code[i].operand = rent; }
                matched++;
            }
            if (compact != 1 || matched != 2)
                throw new InvalidOperationException("native cold allocation anchors changed");
            Log("decode pool transpiler: " + matched + " byte[] sites; exact-layout cold raw=" + compact);
            return code;
        }

        internal static long EstimateBytes(int width, int height, bool setSize, bool bump)
        {
            // Unknown source dimensions: conservative 8K-class reservation. This
            // is scheduling credit, NOT an exact measurement/hard RAM bound.
            if (!setSize || width <= 0 || height <= 0)
                return (bump ? 1536L : 1024L) * MiB;
            // Raw + decoder bitmaps + bump working buffers; cap arithmetic for
            // malformed sizes, not the image itself. Oversize head still proceeds.
            long pixels = (long)width * height;
            long factor = bump ? 26L : 16L;
            if (pixels > long.MaxValue / factor) return long.MaxValue;
            return Math.Max(MiB, pixels * factor);
        }

        internal static bool Fits(long reserved, long estimate, long budget, int count)
        {
            // Permit one oversize request so an 8K/16K image cannot deadlock FIFO.
            return count == 0 || (reserved <= budget && estimate <= budget - reserved);
        }

        internal static bool FitsWithWrites(long reserved, long estimate, long budget, int count, long writes)
        {
            // Do not start an oversize request while a previous cache write still
            // owns its byte array. Writers never wait on admission, so they drain.
            writes = Math.Max(0, writes);
            long held = writes > long.MaxValue - reserved ? long.MaxValue : reserved + writes;
            return Fits(held, estimate, budget, count == 0 && writes == 0 ? 0 : 1);
        }

        private static long CurrentBudget()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now >= _nextMemoryCheck)
            {
                _nextMemoryCheck = now + System.Diagnostics.Stopwatch.Frequency;
                var status = new MemoryStatus();
                status.length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
                if (GlobalMemoryStatusEx(ref status))
                {
                    _available = status.availablePhysical;
                    _commitAvailable = status.availablePageFile;
                    _totalPhysical = status.totalPhysical;
                }
            }
            long limit = Math.Max(256, Math.Min(8192, BudgetMiB == null ? 2048 : BudgetMiB.Value)) * MiB;
            if (_available != ulong.MaxValue)
                limit = Math.Min(limit, Math.Max(256L * MiB, (long)(_available / 4)));
            // availablePageFile is system COMMIT headroom, not disk free space.
            if (_commitAvailable != ulong.MaxValue)
                limit = Math.Min(limit, Math.Max(256L * MiB, (long)(_commitAvailable / 4)));
            return BeforePressureBudget(limit, _totalPhysical, _available);
        }

        internal static long BeforePressureBudget(long limit, ulong total, ulong available)
        {
            if (total == 0 || available > total) return limit;
            // Reserve space before the 75% pressure line; external trimming at
            // 80% otherwise wins before our old 85% GC guard can do anything.
            ulong used = total - available;
            ulong ceiling = total / 100 * 75;
            ulong room = used < ceiling ? ceiling - used : 0;
            return Math.Min(limit, Math.Max(256L * MiB, (long)(room / 2)));
        }

        private static void BeforeDispatch()
        {
            _probesLeft = 4;
            _probeDeadline = System.Diagnostics.Stopwatch.GetTimestamp() +
                System.Diagnostics.Stopwatch.Frequency / 500; // 2 ms between probes, not a hard I/O deadline.
        }

        private static long RequestEstimate(ImageLoaderThreaded.QueuedImage q, out bool cacheSized)
        {
            cacheSized = false;
            long estimate = EstimateBytes(q.width, q.height, q.setSize, q.createNormalFromBump);
            if (q.setSize) return estimate;
            if (_probeHead != null && ReferenceEquals(_probeHead.Target, q))
            {
                cacheSized = _probeHit;
                return _probeEstimate;
            }
            // At most four small probes / dispatch. A budget-deferred head is
            // memoized, not re-opened every frame. No retained textures or buffers.
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_probesLeft <= 0 || (_probeDeadline != 0 && start >= _probeDeadline)) return estimate;
            long known;
            cacheSized = TextureCacheEstimate.TryEstimate(q, out known);
            long probeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            _probeTicks += probeTicks;
            if (probeTicks > _probeMaxTicks)
            {
                _probeMaxTicks = probeTicks;
                _probeWorstPath = q.imgPath;
            }
            if (probeTicks <= ProbeSuspendTicks)
            {
                _probeIoTicks += probeTicks;
                _probesLeft--;
            }
            else
            {
                // Frozen by a stop-the-world native GC: the duration is the
                // freeze, not the read. Refund the probe slot and expire this
                // dispatch's deadline so the remaining images of the batch are
                // not each charged the same freeze (and the cold-header
                // fallback below is skipped, keeping the estimate conservative).
                _probeSuspended++;
                _probeDeadline = start;
            }
            if (cacheSized) estimate = known;
            else if (ColdEstimate != null && System.Diagnostics.Stopwatch.GetTimestamp() < _probeDeadline)
            {
                long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    long cold = ColdEstimate(q);
                    if (cold > 0) { estimate = cold; _headerSized++; }
                }
                catch (Exception e) { Log("cold header retained conservative estimate: " + e.GetType().Name); }
                _headerTicks += System.Diagnostics.Stopwatch.GetTimestamp() - begin;
            }
            _probeHead = new WeakReference(q);
            _probeEstimate = estimate;
            _probeHit = cacheSized;
            return estimate;
        }

        private static bool Admit(ImageLoaderThreaded.QueuedImage q)
        {
            if (q == null || q.processed || q.finished || q.hadError || q.cancel) return true;
            // This callback runs on the main thread, after native cache lookup.
            try
            {
                // ResolveLatestPath runs after this gate. Its path normalization
                // may change the result of the native URL validity comparison.
                if ((EarlyDiscard == null || EarlyDiscard.Value) &&
                    (q.imgPath == null || q.imgPath.IndexOf(".latest:", StringComparison.Ordinal) < 0) &&
                    StaleTextureRequestGuard.CanDiscard(q))
                {
                    q.raw = null;
                    q.processed = true;
                    q.finished = true;
                    q.skipCache = true;
                    // Never cancel/remove manually: native counters, Finish web
                    // disposal and DoCallback must execute exactly once.
                    _discarded++;
                    MarkBatch();
                    return true;
                }
            }
            catch (Exception e) { Log("early classification retained request: " + e.Message); }
            if ((Enabled != null && !Enabled.Value) || q.isThumbnail) return true;
            if (Held.ContainsKey(q)) return true;
            bool cacheSized;
            long estimate = RequestEstimate(q, out cacheSized);
            long budget;
            try { budget = CurrentBudget(); }
            catch { budget = 2048L * MiB; }
            long pendingWriteBytes = PendingCacheBytes == null ? 0 : PendingCacheBytes();
            if (!FitsWithWrites(_reserved, estimate, budget, Held.Count, pendingWriteBytes))
            {
                _deferred++;
                return false; // Leave original head intact; next frame follows Finish.
            }
            Held.Add(q, estimate);
            _probeHead = null;
            if (cacheSized) _cacheSized++;
            _reserved += estimate;
            _peak = Math.Max(_peak, _reserved);
            _admitted++;
            MarkBatch();
            return true;
        }

        private static void MarkBatch()
        {
            if (_batch) return;
            _batch = true;
            // Boehm never returns heap pages to the OS: heap growth across a
            // decode cycle is permanent until a full GC proves otherwise.
            _heapStart = GC.GetTotalMemory(false);
            _gcStart0 = Gc0();
        }

        // Collection counters are cheap; keep the read guarded so an
        // unsupported runtime cannot break the admission path.
        private static int Gc0()
        {
            try { return GC.CollectionCount(0); }
            catch (Exception) { return 0; }
        }

        // Prefix, not finalizer: Finish nulls q.raw after uploading, so the
        // post-call view never sees the decoded array. q.raw at entry is the
        // decoded pixel buffer (Marshal.Copy overwrote the source bytes);
        // bump->normal conversion additionally allocates a Single[][] scratch
        // of ~3 floats per pixel.
        private static void BeforeFinish(ImageLoaderThreaded.QueuedImage __instance, out byte[] __state)
        {
            __state = null;
            if (__instance == null) return;
            byte[] decoded = __instance.raw;
            if (decoded != null) _managedBytes += decoded.LongLength;
            if (__instance.createNormalFromBump)
                _bumpBytes += (long)__instance.width * __instance.height * 12L;
            __state = decoded;
        }

        private static Exception AfterFinish(ImageLoaderThreaded.QueuedImage __instance, byte[] __state, Exception __exception)
        {
            long estimate;
            if (__instance != null && Held.TryGetValue(__instance, out estimate))
            {
                Held.Remove(__instance);
                _reserved -= estimate;
            }
            // Finish has consumed and nulled raw; the request-private array is
            // dead now, so park it for the next decode of the same size class.
            if (__state != null)
            {
                if (__exception == null && __instance != null &&
                    !ReferenceEquals(__instance.raw, __state)) DecodedBufferPool.ReturnArray(__state);
                else DecodedBufferPool.AbandonArray(__state);
            }
            return __exception; // Preserve native exceptions; release even on failure.
        }

        private static Exception AfterResolve(ImageLoaderThreaded.QueuedImage __0, Exception __exception)
        {
            // Native removes the queue head before resolving its path. On an
            // exception it never reaches Finish: do not strand admission credit.
            return __exception == null ? null : AfterFinish(__0, null, __exception);
        }

        private static void AfterDispatch(ImageLoaderThreaded __instance)
        {
            // Native queuedImages is private; a quiet reservation cycle can have
            // more queued work. Only report counters, never infer load completion.
            if (_batch && Held.Count == 0)
            {
                DecodedBufferPool.SweepIdle();
                Log("cycle admitted=" + _admitted + " deferred=" + _deferred +
                    " earlyDiscarded=" + _discarded + " cacheSized=" + _cacheSized +
                    " cacheProbeMs=" + (_probeTicks * 1000 / System.Diagnostics.Stopwatch.Frequency) +
                    " peakReservedMiB=" + (_peak / MiB) +
                    " headerSized=" + _headerSized + " headerMs=" + (_headerTicks * 1000 / System.Diagnostics.Stopwatch.Frequency) +
                    " pendingWriteMiB=" + ((PendingCacheBytes == null ? 0 : PendingCacheBytes()) / MiB) +
                    " directCopies=" + TextureScratchLifetime.DirectCopies +
                    " compactSavedMiB=" + (ColdTextureBufferLayout.SavedBytes / MiB) +
                    " compactArrays=" + ColdTextureBufferLayout.CompactArrays +
                    " inPlaceBumps=" + ColdTextureBufferLayout.InPlaceBumps +
                    " managedDecodeMiB=" + (_managedBytes / MiB) +
                    " bumpScratchMiB=" + (_bumpBytes / MiB) +
                    " heapMiB=" + (_heapStart / MiB) + "->" + (GC.GetTotalMemory(false) / MiB) +
                    " poolMiB=" + (DecodedBufferPool.PooledBytes / MiB) +
                    " poolHit=" + DecodedBufferPool.Hits + "/" + DecodedBufferPool.Rents +
                    " poolEvicted=" + DecodedBufferPool.Evicted +
                    " poolShared=" + DecodedBufferPool.Reserves + "/" + DecodedBufferPool.Claims +
                    "/" + DecodedBufferPool.Held + "/" + DecodedBufferPool.Refused +
                    " sharedLive=" + DecodedBufferPool.SharedEntries +
                    " sharedMiB=" + (DecodedBufferPool.SharedBytes / MiB) +
                    " cacheReuse=" + TextureCacheByteReuse.Hits + "/" + (TextureCacheByteReuse.Bytes / MiB) +
                    "MiB fallback=" + TextureCacheByteReuse.Fallbacks +
                    " nativeStaged=" + NativeCacheBuffer.Staged + "/" + (NativeCacheBuffer.StagedBytes / MiB) +
                    "MiB nativeUploaded=" + NativeCacheBuffer.Uploaded +
                    " nativeLive=" + NativeCacheBuffer.LiveMiB + "MiB" +
                    " nativeHeld=" + NativeCacheBuffer.IdleHeldMiB + "MiB" +
                    " nativeFail=" + NativeCacheBuffer.Failures +
                    " probeMaxMs=" + (_probeMaxTicks * 1000 / System.Diagnostics.Stopwatch.Frequency) +
                    " probeIoMs=" + (_probeIoTicks * 1000 / System.Diagnostics.Stopwatch.Frequency) +
                    " probeSuspended=" + _probeSuspended +
                    (_probeSuspended == 0 ? "" : " probeWorst=" + Tail(_probeWorstPath)) +
                    " gc0InCycle=" + (Gc0() - _gcStart0) +
                    " (estimate, not actual memory; not a preset completion signal)");
                TextureCacheWriteBudget.ReportCycle();
                _batch = false;
                _admitted = _deferred = _discarded = 0;
                _peak = _probeTicks = 0;
                _probeMaxTicks = _probeIoTicks = 0;
                _probeSuspended = 0;
                _managedBytes = _bumpBytes = 0;
                _cacheSized = 0;
                _headerSized = 0; _headerTicks = 0;
            }
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            DecodedBufferPool.Retire();
            Held.Clear();
            _reserved = _peak = _nextMemoryCheck = 0;
            _admitted = _deferred = _discarded = 0;
            _headerSized = 0; _headerTicks = 0; ColdEstimate = null;
            _available = _commitAvailable = ulong.MaxValue;
            _totalPhysical = 0;
            _batch = false;
            _probeHead = null;
            _probeEstimate = _probeTicks = _probeDeadline = 0;
            _probeMaxTicks = _probeIoTicks = 0;
            _probeSuspended = 0;
            _probeWorstPath = null;
            _gcStart0 = 0;
            _probeHit = false;
            _probesLeft = 4;
            _cacheSized = 0;
        }

        // Keep the tail of a path: the file name identifies the probe.
        private static string Tail(string path)
        {
            if (string.IsNullOrEmpty(path)) return "(none)";
            return path.Length <= 56 ? path : "..." + path.Substring(path.Length - 53);
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[texture-budget] " + message);
        }
    }
}
