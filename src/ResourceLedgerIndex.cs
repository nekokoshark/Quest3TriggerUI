using System;
using System.Collections.Generic;

namespace Quest3TriggerUI
{
    // Observed edges, NOT proof of exclusive ownership or permission to unload.
    // Neither owners nor assets are strongly rooted by this index.
    internal sealed class ResourceLedgerIndex
    {
        internal sealed class Asset
        {
            internal int Id;
            internal WeakReference Target;
            // Strong handle held ONLY while the asset sits in Retired —
            // closing the window where the wrapper dies before the audit
            // can ask whether the native object is gone. Released on a
            // terminal verdict so pinned wrappers do not accumulate.
            internal object RetiredTarget;
            internal string Kind, Name;
            internal string ReleaseState = "unexamined";
            // True only when the ReleaseState verdict was computed while the
            // ledger was settled — a verdict from a mid-churn audit pass is
            // provisional and must not ground a proof rejection.
            internal bool ReleaseSettled;
            internal int RetiredSeq;
            internal int LastOwner, NativeRefs = -1;
            internal int LocalUses, ObservedUses;
            internal readonly HashSet<int> Owners = new HashSet<int>();
            internal Asset(int id, object target, string kind, string name)
            { Id = id; Target = new WeakReference(target); Kind = kind; Name = name; }
        }

        internal readonly Dictionary<int, Asset> Assets = new Dictionary<int, Asset>();
        // Last-observed edges survive removal of an owner. These remain weak
        // candidates, never an implicit instruction to destroy an asset.
        internal readonly Dictionary<int, Asset> Retired = new Dictionary<int, Asset>();
        internal long RetiredRevision;
        // Monotone sequence stamped on each asset at retirement. A UUA sweep
        // captures the watermark at submit time; entries retired afterwards
        // were never in the sweep's mark set and earn no survivor verdict.
        private static int _retiredSeq;
        internal static int RetiredWatermark { get { return _retiredSeq; } }
        internal int NativeGone, Reused, UncertainCollected, Overflow;
        // Monotonic work counter — bumped per unit of ledger work (seed item,
        // walked node, audited asset). Stall detection reads this, NOT
        // revisions: real progress moves it even when nothing changes.
        internal long Work;
        private readonly Dictionary<int, HashSet<int>> _owners = new Dictionary<int, HashSet<int>>();
        private readonly Dictionary<int, Dictionary<int, int>> _uses = new Dictionary<int, Dictionary<int, int>>();
        internal long Revision;
        internal int OwnerCount { get { return _owners.Count; } }

        internal void Replace(int owner, Dictionary<int, Asset> next)
        {
            HashSet<int> old;
            if (!_owners.TryGetValue(owner, out old))
            { old = new HashSet<int>(); _owners.Add(owner, old); _uses[owner] = new Dictionary<int, int>(); Revision++; }
            var removed = new List<int>();
            foreach (int id in old) if (!next.ContainsKey(id)) removed.Add(id);
            foreach (int id in removed) { DropEdge(owner, id); old.Remove(id); _uses[owner].Remove(id); }
            foreach (var pair in next)
            {
                if (Retired.Remove(pair.Key)) { Reused++; RetiredRevision++; }
                Asset asset;
                if (!Assets.TryGetValue(pair.Key, out asset))
                { asset = pair.Value; asset.ObservedUses = 0; Assets.Add(pair.Key, asset); }
                else if (!ReferenceEquals(asset.Target.Target, pair.Value.Target.Target))
                {
                    // Unity can recycle IDs after destruction. Preserve edge IDs,
                    // but replace metadata; no reclamation decision uses this map.
                    asset.Target = pair.Value.Target;
                    asset.Kind = pair.Value.Kind; asset.Name = pair.Value.Name;
                    Revision++;
                }
                if (asset.Owners.Add(owner)) Revision++;
                int previous;
                _uses[owner].TryGetValue(pair.Key, out previous);
                if (previous != pair.Value.LocalUses) Revision++;
                asset.ObservedUses += pair.Value.LocalUses - previous;
                _uses[owner][pair.Key] = pair.Value.LocalUses;
                old.Add(pair.Key);
            }
        }

        private void DropEdge(int owner, int id)
        {
            Asset asset;
            if (!Assets.TryGetValue(id, out asset)) return;
            int uses;
            if (_uses[owner].TryGetValue(id, out uses)) asset.ObservedUses -= uses;
            if (asset.Owners.Remove(owner)) Revision++;
            if (asset.Owners.Count == 0)
            {
                asset.LastOwner = owner;
                asset.ReleaseState = "pending-reconciliation"; asset.NativeRefs = -1;
                asset.ReleaseSettled = false;
                // Pin the wrapper: a GC'd wrapper was the single largest
                // "weak-lost" source — a dead handle cannot testify whether
                // the native object still exists.
                asset.RetiredTarget = asset.Target.Target;
                asset.RetiredSeq = ++_retiredSeq;
                if (Retired.Count < 8192 || Retired.ContainsKey(id)) Retired[id] = asset;
                else if (EvictTerminal()) Retired[id] = asset;
                else Overflow++; // Observation incomplete; no unload permission.
                RetiredRevision++; Assets.Remove(id);
            }
        }

        // Terminal verdicts are only needed while a recent proof might still
        // reference them; under cap pressure they are the first to go.
        private bool EvictTerminal()
        {
            int candidate = 0; bool found = false;
            foreach (var pair in Retired)
            {
                if (pair.Value.ReleaseState != "native-destroyed" && pair.Value.ReleaseState != "weak-lost" &&
                    pair.Value.ReleaseState != "sweep-survivor") continue;
                candidate = pair.Key; found = true; break;
            }
            if (found) { Retired.Remove(candidate); RetiredRevision++; }
            return found;
        }

        internal void Remove(int owner)
        {
            HashSet<int> ids;
            if (!_owners.TryGetValue(owner, out ids)) return;
            foreach (int id in ids) DropEdge(owner, id);
            _owners.Remove(owner); _uses.Remove(owner); Revision++;
        }

        internal void Clear()
        {
            if (_owners.Count != 0 || Assets.Count != 0) Revision++;
            _owners.Clear(); _uses.Clear(); Assets.Clear(); Retired.Clear(); RetiredRevision++;
        }
    }
}
