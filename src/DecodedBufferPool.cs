using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Quest3TriggerUI
{
    // Exact-length byte[] pool for QueuedImage.raw decode buffers. IL shows
    // every raw write site holds a request-private array (newarr decode
    // output or FileManager.ReadAllBytes result — never rawImageToLoad),
    // Only tracked loans may return to the small idle pool. Decode-path
    // allocation is diverted via transpiler to RentByteArray, and the cached
    // texture read (QueuedImage.Process reading a completed .vamcache) goes
    // through the same pool, so a cache-served preset switch stops allocating
    // 0.4-1.4GB of throwaway managed bytes per cycle. Arrays join the pool
    // only when returned after a successful Finish has detached raw. Exact-length
    // buckets are mandatory: Finish passes raw straight to
    // LoadRawTextureData, so an oversized pooled array would corrupt upload.
    // Idle expiry removes pool ownership. OS page return is runtime-dependent;
    // dropping references does not promise immediate working-set reduction.
    internal static class DecodedBufferPool
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> BudgetMiB;

        private const int PerBucketCap = 8;
        private const int IdleSeconds = 30;
        private const long MaxIdleBytes = 64L * 1048576;
        private const int MaxIdleArray = 16 * 1048576;
        private static readonly List<WeakReference> Loans = new List<WeakReference>();
        private const int MaxLoans = 256;
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
        // Tracking must not own the decoded array: Finish can be cancelled or
        // unpatched during hot reload. QueuedImage.raw owns the actual data.
        private sealed class SharedRef { internal WeakReference A; internal int N; }
        private static readonly List<SharedRef> Shared = new List<SharedRef>();

        // Weak loan tags never retain cancelled requests' arrays.
        private static bool RemoveLoan(byte[] a)
        {
            bool found = false;
            for (int i = Loans.Count - 1; i >= 0; i--)
            {
                object target = Loans[i].Target;
                if (target == null || ReferenceEquals(target, a))
                {
                    found |= target != null;
                    Loans.RemoveAt(i);
                }
            }
            return found;
        }
        private static void TrackLoan(byte[] a)
        {
            RemoveLoan(a);
            if (Loans.Count < MaxLoans) Loans.Add(new WeakReference(a));
        }

        // Caller holds Gate. A strong local keeps a found array alive while
        // checking identity; expired bookkeeping never accumulates past MaxShared.
        private static SharedRef FindShared(byte[] a)
        {
            SharedRef found = null;
            for (int i = Shared.Count - 1; i >= 0; i--)
            {
                object target = Shared[i].A.Target;
                if (target == null) Shared.RemoveAt(i);
                else if (ReferenceEquals(target, a)) found = Shared[i];
            }
            return found;
        }
        private static readonly Dictionary<int, Stack<Pooled>> Buckets =
            new Dictionary<int, Stack<Pooled>>();
        private static long _pooledBytes;
        internal static long Rents, Hits, Returns, Drops, Evicted;
        internal static long Reserves, Claims, Held, Refused;
        private static int _lastRent;
        private static bool _borrowed;
        // Set when this payload generation is shutting down. Late worker
        // callbacks may still return a shared buffer, but an old generation
        // must never put it back into a pool that can outlive the callback.
        private static bool _retired;

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
                    var f = p.GetType().GetField("A", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var arr = f == null ? null : f.GetValue(p) as byte[];
                    if (arr == null) continue;
                    long dropsBefore = Drops;
                    lock (Gate) { TrackLoan(arr); }
                    ReturnArray(arr);
                    if (Drops == dropsBefore) adopted++;
                }
            }
            // Drop rejected old arrays as well as accepted ones.
            dict.Clear();
            if (adopted > 0)
            {
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
                FindShared(null);
                if (_retired || Shared.Count >= MaxShared) { Refused++; return false; }
                if (FindShared(a) != null) { Refused++; return false; }
                Shared.Add(new SharedRef { A = new WeakReference(a), N = 1 });
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
                if (_retired) { Refused++; return false; }
                SharedRef r = FindShared(a);
                if (r == null) { Refused++; return false; }
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
                if (_retired) return new byte[length];
                Rents++;
                _borrowed = true;
                _lastRent = Environment.TickCount;
                byte[] a;
                Stack<Pooled> s;
                if (Buckets.TryGetValue(length, out s) && s.Count > 0)
                {
                    Hits++;
                    Pooled p = s.Pop();
                    _pooledBytes -= p.A.LongLength;
                    a = p.A;
                    if (s.Count == 0) Buckets.Remove(length);
                }
                else a = new byte[length];
                TrackLoan(a);
                return a;
            }
        }

        // Called on the main thread after Finish; raw has been nulled by then.
        internal static void ReturnArray(byte[] a)
        {
            if (a == null) return;
            long cap = Math.Min(MaxIdleBytes, (BudgetMiB == null ? 1536 : Math.Max(0, BudgetMiB.Value)) * 1048576L);
            int now = Environment.TickCount;
            lock (Gate)
            {
                Returns++;
                SharedRef shared = FindShared(a);
                if (shared != null)
                {
                    // Another request is still uploading this very buffer; keep
                    // it out of the pool until the last holder returns it.
                    if (--shared.N > 0) { Held++; return; }
                    Shared.Remove(shared);
                }
                if (!RemoveLoan(a)) { Drops++; return; }
                if (a.Length > MaxIdleArray) { Drops++; return; }
                // A retired generation has no safe pool owner. The last
                // shared holder was accounted above; dropping here lets the
                // array become collectable without reintroducing a stale pool.
                if (_retired || (Enabled != null && !Enabled.Value)) { Drops++; return; }
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

        internal static void AbandonArray(byte[] a)
        {
            if (a == null) return;
            lock (Gate)
            {
                RemoveLoan(a);
                SharedRef shared = FindShared(a);
                if (shared != null && --shared.N <= 0) Shared.Remove(shared);
                Drops++;
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
            if (_nextSweep != 0 && unchecked(now - _nextSweep) < 0) return;
            _nextSweep = now + 1000;
            int maxAge = IdleSeconds * 1000;
            lock (Gate)
            {
                List<int> dead = null;
                foreach (KeyValuePair<int, Stack<Pooled>> kv in Buckets)
                {
                    bool expired = false;
                    foreach (Pooled p in kv.Value)
                        if (unchecked(now - p.Tick) >= maxAge) { expired = true; break; }
                    if (!expired) continue;
                    // Mutate this stack, not Buckets while its enumerator is live.
                    // Snapshot only buckets containing expired entries.
                    Pooled[] entries = kv.Value.ToArray();
                    kv.Value.Clear();
                    for (int i = entries.Length - 1; i >= 0; i--)
                    {
                        Pooled p = entries[i];
                        if (unchecked(now - p.Tick) < maxAge) kv.Value.Push(p);
                        else { _pooledBytes -= p.A.LongLength; Evicted++; }
                    }
                    if (kv.Value.Count == 0)
                    {
                        if (dead == null) dead = new List<int>();
                        dead.Add(kv.Key);
                    }
                }
                if (dead != null)
                    foreach (int k in dead) Buckets.Remove(k);
            }
        }

        internal static long PooledBytes
        {
            get { lock (Gate) return _pooledBytes; }
        }

        // Retire is used only by payload shutdown. It deliberately keeps the
        // Shared refcounts until late Finish callbacks arrive; unlike clearing
        // Shared immediately, this cannot let one sharer race an owner's return
        // and accidentally reuse the same array.
        internal static void Retire()
        {
            lock (Gate)
            {
                _retired = true;
                Loans.Clear();
                Buckets.Clear();
                _pooledBytes = 0;
                _borrowed = false;
            }
        }

        // Install also runs when the loader restores this generation after a failed load.
        // Preserve outstanding shared holders; retirement already emptied idle buckets.
        internal static void Reactivate()
        {
            lock (Gate) { _retired = false; }
        }

        internal static int SharedEntries
        {
            get { lock (Gate) { FindShared(null); return Shared.Count; } }
        }

        internal static long SharedBytes
        {
            get
            {
                lock (Gate)
                {
                    long total = 0;
                    FindShared(null);
                    foreach (SharedRef entry in Shared)
                    {
                        byte[] a = entry.A.Target as byte[];
                        if (a != null) total += a.LongLength;
                    }
                    return total;
                }
            }
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
