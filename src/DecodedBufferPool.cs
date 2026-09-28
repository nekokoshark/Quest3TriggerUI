using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Quest3TriggerUI
{
    // Exact-length byte[] pool for QueuedImage.raw decode buffers. IL shows
    // every raw write site holds a request-private array (newarr decode
    // output or FileManager.ReadAllBytes result — never rawImageToLoad),
    // so any array present at Finish entry is safe to harvest. Decode-path
    // allocation is diverted via transpiler to RentByteArray, and the cached
    // texture read (QueuedImage.Process reading a completed .vamcache) goes
    // through the same pool, so a cache-served preset switch stops allocating
    // 0.4-1.4GB of throwaway managed bytes per cycle. Arrays join the pool
    // only when returned after Finish. Exact-length
    // buckets are mandatory: Finish passes raw straight to
    // LoadRawTextureData, so an oversized pooled array would corrupt upload.
    // Entries age out after IdleSeconds without a rent — large arrays are
    // standalone Boehm blocks, so evicting them actually returns pages
    // instead of sitting at the pool's high-water mark forever.
    internal static class DecodedBufferPool
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> BudgetMiB;

        private const int PerBucketCap = 8;
        private const int IdleSeconds = 120;
        // Ceiling on live multi-request reservations. At the limit sharing is
        // refused and the sharer decodes natively instead of letting the
        // registry grow without bound; a full registry means torn-down
        // requests that never reached Finish.
        private const int MaxShared = 256;
        private static readonly object Gate = new object();
        private sealed class Pooled { internal byte[] A; internal int Tick; }
        // Buffers TextureInFlight handed to more than one request. The
        // reservation is taken before the buffer becomes visible to a sharer
        // and released by the last holder's Finish, so a shared buffer cannot
        // enter Buckets while another request is still uploading it.
        private sealed class SharedRef { internal int N; }
        private sealed class ReferenceComparer : IEqualityComparer<byte[]>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(byte[] a, byte[] b) { return ReferenceEquals(a, b); }
            public int GetHashCode(byte[] a)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a);
            }
        }
        private static readonly Dictionary<byte[], SharedRef> Shared =
            new Dictionary<byte[], SharedRef>(ReferenceComparer.Instance);
        private static readonly Dictionary<int, Stack<Pooled>> Buckets =
            new Dictionary<int, Stack<Pooled>>();
        private static long _pooledBytes;
        internal static long Rents, Hits, Returns, Drops, Evicted;
        internal static long Reserves, Claims, Held, Refused;
        private static int _lastRent;
        private static bool _borrowed;

        // Cross-generation handoff: publish the live Buckets dictionary so the
        // next payload generation can harvest its byte[]s instead of refilling
        // 1.5GB of pool from scratch. The old gen's Pooled type differs, so
        // adoption unwraps via reflection — the arrays themselves are shared.
        internal static void Publish()
        {
            GenBridge.Publish("pool.buckets", Buckets);
        }

        internal static void Adopt()
        {
            object prev = GenBridge.Take("pool.buckets");
            var dict = prev as System.Collections.IDictionary;
            if (dict == null) return;
            // Adoption is the one return path that is not driven by a live
            // rent; open the recency window so the harvested arrays are
            // accepted, then let the idle sweep drain them if this generation
            // turns out to be cache-served.
            _lastRent = Environment.TickCount;
            _borrowed = true;
            int adopted = 0;
            foreach (System.Collections.DictionaryEntry de in dict)
            {
                var stack = de.Value as System.Collections.IEnumerable;
                if (stack == null) continue;
                foreach (object p in stack)
                {
                    if (p == null) continue;
                    var f = p.GetType().GetField("A");
                    var arr = f == null ? null : f.GetValue(p) as byte[];
                    if (arr == null) continue;
                    long dropsBefore = Drops;
                    ReturnArray(arr);
                    if (Drops == dropsBefore) adopted++;
                }
            }
            if (adopted > 0)
            {
                // The old gen's stacks are now empty shells — clear its dict
                // so the shells do not pin the pool bookkeeping alive.
                try { dict.Clear(); } catch { }
                WardrobeJanitor.Log("decoded pool adopted " + adopted +
                    " buffers / " + (PooledBytes / 1048576) + "MiB from previous generation");
            }
        }

        // Owner side of a shared buffer. Called under the sharing entry's lock
        // before the buffer becomes visible to a second request; a refusal
        // means this buffer is not shared and the sharer decodes natively.
        internal static bool Reserve(byte[] a)
        {
            if (a == null || (Enabled != null && !Enabled.Value)) return false;
            lock (Gate)
            {
                if (Shared.Count >= MaxShared) { Refused++; return false; }
                Shared[a] = new SharedRef { N = 1 };
                Reserves++;
            }
            return true;
        }

        // Sharer side. Atomic with the owner's return: either the reservation
        // is still live and this request joins it, or the buffer may already be
        // back in service and the caller must decode natively.
        internal static bool ClaimForShare(byte[] a)
        {
            if (a == null) return false;
            lock (Gate)
            {
                SharedRef r;
                if (!Shared.TryGetValue(a, out r)) { Refused++; return false; }
                r.N++;
                Claims++;
            }
            return true;
        }

        // Called from transpiled ProcessFromStream on decoder worker threads.
        internal static byte[] RentByteArray(int length)
        {
            if (Enabled != null && !Enabled.Value) return new byte[length];
            lock (Gate)
            {
                Rents++;
                _borrowed = true;
                _lastRent = Environment.TickCount;
                Stack<Pooled> s;
                if (Buckets.TryGetValue(length, out s) && s.Count > 0)
                {
                    Hits++;
                    Pooled p = s.Pop();
                    _pooledBytes -= p.A.LongLength;
                    return p.A;
                }
            }
            return new byte[length];
        }

        // Called on the main thread after Finish; raw has been nulled by then.
        internal static void ReturnArray(byte[] a)
        {
            if (a == null || (Enabled != null && !Enabled.Value)) return;
            long cap = (BudgetMiB == null ? 1536 : Math.Max(64, BudgetMiB.Value)) * 1048576L;
            int now = Environment.TickCount;
            lock (Gate)
            {
                Returns++;
                SharedRef shared;
                if (Shared.TryGetValue(a, out shared))
                {
                    // Another request is still uploading this very buffer; keep
                    // it out of the pool until the last holder returns it.
                    if (--shared.N > 0) { Held++; return; }
                    Shared.Remove(a);
                }
                // Arrays this pool neither rented nor adopted have no borrower
                // to reuse them, so parking one leaves dead weight in a
                // cache-served session while every rent comes back empty.
                // Accept a return only while something is actually borrowing.
                if (!_borrowed || unchecked(now - _lastRent) > 120000) { Drops++; return; }
                if (_pooledBytes + a.LongLength > cap) { Drops++; return; }
                Stack<Pooled> s;
                if (!Buckets.TryGetValue(a.Length, out s))
                    Buckets[a.Length] = s = new Stack<Pooled>();
                if (s.Count >= PerBucketCap) { Drops++; return; }
                s.Push(new Pooled { A = a, Tick = Environment.TickCount });
                _pooledBytes += a.LongLength;
            }
        }

        // Drop entries not rented for IdleSeconds; runs at most every 30s so
        // the per-frame Tick caller stays cheap. An idle pool shrinks back
        // toward zero instead of holding the whole burst footprint until the
        // next Clear.
        private static int _nextSweep;

        internal static void SweepIdle()
        {
            int now = Environment.TickCount;
            if (now - _nextSweep < 0) return;
            _nextSweep = now + 30000;
            int maxAge = IdleSeconds * 1000;
            lock (Gate)
            {
                List<int> dead = null;
                foreach (KeyValuePair<int, Stack<Pooled>> kv in Buckets)
                {
                    Stack<Pooled> keep = new Stack<Pooled>();
                    int dropped = 0;
                    foreach (Pooled p in kv.Value)
                    {
                        if (now - p.Tick < maxAge) keep.Push(p);
                        else { _pooledBytes -= p.A.LongLength; dropped++; }
                    }
                    if (dropped == 0) continue;
                    Evicted += dropped;
                    if (keep.Count == 0)
                    {
                        if (dead == null) dead = new List<int>();
                        dead.Add(kv.Key);
                    }
                    else Buckets[kv.Key] = keep;
                }
                if (dead != null)
                    foreach (int k in dead) Buckets.Remove(k);
            }
        }

        internal static long PooledBytes
        {
            get { lock (Gate) return _pooledBytes; }
        }

        internal static void Clear()
        {
            lock (Gate)
            {
                Buckets.Clear();
                _pooledBytes = 0;
            }
        }
    }
}
