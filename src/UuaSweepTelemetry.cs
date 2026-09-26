using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Observation only. Neither release counters nor heap deltas measure the
    // number of assets UUA unloaded. Native output_log reports are correlated
    // offline between these markers; missing/ambiguous reports stay unknown.
    internal static class UuaSweepTelemetry
    {
        private static readonly FieldInfo Cache = typeof(ImageLoaderThreaded).GetField(
            "textureCache", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Counts = typeof(ImageLoaderThreaded).GetField(
            "textureUseCount", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly List<Sample> Pending = new List<Sample>();
        private static readonly string Session = Guid.NewGuid().ToString("N").Substring(0, 12);
        private static int _sequence;

        internal sealed class Sample
        {
            internal string id;
            internal Stopwatch clock;
            internal AsyncOperation operation;
            internal long released;
            internal int retiredWatermark;
            internal bool finished;
        }

        // A completed sweep is itself reference evidence: anything retired
        // before submission that is still alive afterwards was provably
        // referenced at mark time. The ledger consumes this to promote
        // stragglers to "sweep-survivor" instead of counting them as debt
        // on every swap.
        internal static Action<Sample> SweepCompleted;

        internal static Sample Begin(string origin, string scope, string reason, long released, long debt)
        {
            try
            {
                if (Pending.Count >= 16) { Emit("overflow; observation omitted; policy unchanged"); return null; }
                var s = new Sample { id = Session + "-" + (++_sequence), released = released,
                    retiredWatermark = ResourceLedgerIndex.RetiredWatermark };
                string before = Snapshot();
                s.clock = Stopwatch.StartNew();
                Emit("begin id=" + s.id + " origin=" + Clean(origin) + " scope=" + Clean(scope) +
                    " reason=" + Clean(reason) + " releases=" + released + " uncoveredReleases=" + debt + " " + before);
                Pending.Add(s);
                return s;
            }
            catch (Exception) { return null; } // Diagnostics must not alter native cleanup.
        }

        internal static void Submitted(Sample s, AsyncOperation operation)
        {
            if (s == null) return;
            s.operation = operation;
            Emit("return id=" + s.id + " callMs=" + s.clock.ElapsedMilliseconds +
                " operation=" + (operation == null ? "null" : "submitted"));
            if (operation == null) Finish(s, "unknown-null", s.released);
        }

        internal static void Failed(Sample s)
        {
            if (s != null) Finish(s, "native-exception", s.released);
        }

        internal static void Tick(long released)
        {
            // No pending request: no census, allocations, logging or native calls.
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                Sample s = Pending[i];
                try
                {
                    if (s.operation != null && s.operation.isDone) Finish(s, "complete", released);
                    else if (s.clock.ElapsedMilliseconds >= 120000) Finish(s, "observation-timeout", released);
                }
                catch (Exception) { Finish(s, "observation-error", released); }
            }
        }

        private static void Finish(Sample s, string status, long released)
        {
            if (s.finished) return;
            s.finished = true;
            s.clock.Stop();
            Pending.Remove(s);
            Emit("end id=" + s.id + " status=" + status + " observedMs=" + s.clock.ElapsedMilliseconds +
                " releaseDelta=" + (released - s.released) + " unloaded=unknown " + Snapshot());
            s.operation = null;
            if (status == "complete" && SweepCompleted != null)
            {
                try { SweepCompleted(s); }
                catch (Exception) { }
            }
        }

        private static string Snapshot()
        {
            try
            {
                var loader = ImageLoaderThreaded.singleton;
                var cache = loader == null || Cache == null ? null : Cache.GetValue(loader) as Dictionary<string, Texture2D>;
                var counts = loader == null || Counts == null ? null : Counts.GetValue(loader) as Dictionary<Texture2D, int>;
                int live = 0, dead = 0, untracked = 0;
                // Bounded existing cache only; never enumerate all Unity objects.
                if (cache == null || counts == null || cache.Count > 4096) return "cache=unknown";
                foreach (var kv in cache)
                {
                    if (kv.Value == null) continue;
                    int count;
                    if (!counts.TryGetValue(kv.Value, out count)) untracked++;
                    else if (count <= 0) dead++;
                    else live++;
                }
                return "cacheLive=" + live + " cacheDead=" + dead + " cacheUntracked=" + untracked +
                    " managedBytes=" + GC.GetTotalMemory(false);
            }
            catch (Exception) { return "cache=unknown"; }
        }

        internal static void Shutdown()
        {
            while (Pending.Count > 0) Finish(Pending[Pending.Count - 1], "shutdown", Pending[Pending.Count - 1].released);
        }

        private static string Clean(string value)
        {
            return (value ?? "unknown").Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '_');
        }

        private static void Emit(string message)
        {
            try { UnityEngine.Debug.Log("[Q3-UUA] " + message); }
            catch (Exception) { }
        }
    }
}
