using System.Collections.Generic;
using UnityEngine;

namespace Quest3TriggerUI
{
    /// <summary>
    /// GPU-side simulation quality budget for GPUTools hair and cloth — the
    /// sims that do NOT live under atom.physicsSimulators (they run in compute
    /// shaders). Per-item policy driven by each item's own authored settings:
    ///
    ///   hair:  FixedDensity/FixedDetail scaled proportionally (already-low
    ///          items are skipped); collision solve off at aggressive level.
    ///   cloth: items with few physics particles (jewelry, straps, small
    ///          accessories — ClothOffBelow) get their sim component disabled
    ///          outright, matching VaM's own sim toggle (clothSettings.enabled,
    ///          same field SyncSimEnabled drives); heavier items keep sim
    ///          running with iterations scaled down — but only when they
    ///          have headroom (>2), since under-converged cloth sags.
    ///
    /// All fields are runtime-only (verified: written only by JSON sync
    /// callbacks, read per-frame by the GPUTools data facades). Originals are
    /// snapshotted on first touch, re-baselined if an external writer (user
    /// slider) changes a live value, and restored on level 0 / OnDestroy.
    /// </summary>
    internal static class GpuSimBudget
    {
        private sealed class HairState
        {
            internal int origDensity, origDetail, lastWDensity, lastWDetail;
            internal float origCurl, lastWCurl;
            internal bool origColl, collSnapshotted;
        }
        private sealed class ClothState
        {
            internal int origIter, origInner, lastWIter, lastWInner;
            internal bool origEnabled, enabledSnapshotted, lastWEnabled;
        }

        private static readonly Dictionary<Atom, List<HairSimControl>> _hairByAtom =
            new Dictionary<Atom, List<HairSimControl>>();
        private static readonly Dictionary<Atom, List<ClothSimControl>> _clothByAtom =
            new Dictionary<Atom, List<ClothSimControl>>();
        private static readonly Dictionary<HairSimControl, HairState> _hair =
            new Dictionary<HairSimControl, HairState>();
        private static readonly Dictionary<ClothSimControl, ClothState> _cloth =
            new Dictionary<ClothSimControl, ClothState>();
        private static bool _censusLogged;

        // True while any item still holds tracked state — callers must keep
        // scanning so scaled values can be returned to original.
        internal static bool HasState
        {
            get { return _hair.Count > 0 || _cloth.Count > 0; }
        }

        // hairCollOffAbove: collision is also switched off for items whose
        // inherent size (origDensity×origDetail ≈ particle count) reaches
        // the threshold — the balanced level uses this so only genuinely
        // heavy long hair loses collision, while aggressive turns it off
        // for everything via hairCollOff.
        internal static void ScanAtom(Atom atom, float hairDensity,
            float hairDetail, float hairCurl, bool hairCollOff,
            int hairCollOffAbove, float clothScale, int clothOffBelow)
        {
            List<HairSimControl> hairs = GetControls(atom, _hairByAtom);
            List<ClothSimControl> cloths = GetControls(atom, _clothByAtom);
            if (!_censusLogged && (hairs.Count > 0 || cloths.Count > 0) &&
                Quest3TriggerUIPlugin.Log != null)
            {
                _censusLogged = true;
                var sb = new System.Text.StringBuilder("GPU sim census ");
                sb.Append(atom.uid).Append(':');
                for (int i = 0; i < hairs.Count; i++)
                {
                    var h = hairs[i];
                    if (h != null && h.hairSettings != null &&
                        h.hairSettings.PhysicsSettings != null &&
                        h.hairSettings.LODSettings != null)
                        sb.Append(" hair[").Append(h.hairSettings.LODSettings.FixedDensity)
                          .Append('/').Append(h.hairSettings.LODSettings.FixedDetail)
                          .Append(" coll=").Append(h.hairSettings.PhysicsSettings.IsCollisionEnabled ? 1 : 0)
                          .Append(']');
                }
                for (int i = 0; i < cloths.Count; i++)
                {
                    var c = cloths[i];
                    if (c != null && c.clothSettings != null)
                        sb.Append(" cloth[").Append(ParticleCount(c))
                          .Append("p ").Append(c.clothSettings.Iterations)
                          .Append('/').Append(c.clothSettings.InnerIterations).Append(']');
                }
                Quest3TriggerUIPlugin.Log.LogInfo(sb.ToString());
            }
            for (int i = 0; i < hairs.Count; i++)
            {
                HairSimControl h = hairs[i];
                if (h == null || h.hairSettings == null) continue;
                var ps = h.hairSettings.PhysicsSettings;
                var lod = h.hairSettings.LODSettings;
                if (ps == null || lod == null) continue;
                HairState st;
                if (!_hair.TryGetValue(h, out st))
                    _hair[h] = st = new HairState {
                        origDensity = lod.FixedDensity, origDetail = lod.FixedDetail,
                        origCurl = h.hairSettings.RenderSettings == null
                            ? -1f : h.hairSettings.RenderSettings.WavinessFrequency,
                        lastWDensity = -1, lastWDetail = -1, lastWCurl = -1f };
                st.lastWDensity = ScaleHairField(lod, true, st, hairDensity, 6);
                st.lastWDetail = ScaleHairField(lod, false, st, hairDetail, 4);
                st.lastWCurl = ScaleHairCurl(h.hairSettings, st, hairCurl);
                bool wantCollOff = hairCollOff ||
                    (hairCollOffAbove > 0 &&
                     (long)st.origDensity * st.origDetail >= hairCollOffAbove);
                if (wantCollOff)
                {
                    if (!st.collSnapshotted)
                    { st.origColl = ps.IsCollisionEnabled; st.collSnapshotted = true; }
                    if (ps.IsCollisionEnabled) ps.IsCollisionEnabled = false;
                }
                else if (st.collSnapshotted && ps.IsCollisionEnabled != st.origColl)
                    ps.IsCollisionEnabled = st.origColl;
            }
            for (int i = 0; i < cloths.Count; i++)
            {
                ClothSimControl c = cloths[i];
                if (c == null || c.clothSettings == null) continue;
                var cs = c.clothSettings;
                ClothState st;
                if (!_cloth.TryGetValue(c, out st))
                    _cloth[c] = st = new ClothState {
                        origIter = cs.Iterations, origInner = cs.InnerIterations,
                        origEnabled = cs.enabled, enabledSnapshotted = true,
                        lastWIter = -1, lastWInner = -1, lastWEnabled = cs.enabled };
                // Small-item policy: below the particle threshold the sim is
                // pure waste — disable the component (VaM's own off switch).
                if (clothOffBelow > 0)
                {
                    bool small = ParticleCount(c) <= clothOffBelow;
                    if (cs.enabled != st.lastWEnabled)
                    {
                        // External toggle (user/UI) — adopt as new baseline.
                        st.origEnabled = cs.enabled;
                    }
                    st.lastWEnabled = small ? false : st.origEnabled;
                    if (cs.enabled != st.lastWEnabled) cs.enabled = st.lastWEnabled;
                    if (small) continue; // disabled — iterations moot
                }
                st.lastWIter = ScaleCloth(cs, true, st, clothScale);
                st.lastWInner = ScaleCloth(cs, false, st, clothScale);
            }
        }

        private static int ParticleCount(ClothSimControl c)
        {
            try
            {
                var gd = c.clothSettings.GeometryData;
                if (gd != null && gd.Particles != null) return gd.Particles.Length;
            }
            catch { }
            return int.MaxValue; // unknown → treat as heavy, never auto-disable
        }

        // Proportional scale with re-baselining: if the live field differs
        // from what we last wrote, an external writer moved it — adopt that
        // as the new original instead of clinging to a stale snapshot.
        // `floor` only applies to content above it: already-minimal items
        // (value <= floor) are never reduced further.
        private static int ScaleHairField(
            GPUTools.Hair.Scripts.Settings.HairLODSettings lod, bool density,
            HairState st, float scale, int floor)
        {
            int orig = density ? st.origDensity : st.origDetail;
            int lastW = density ? st.lastWDensity : st.lastWDetail;
            int live = density ? lod.FixedDensity : lod.FixedDetail;
            if (lastW >= 0 && live != lastW) orig = live;
            if (density) st.origDensity = orig; else st.origDetail = orig;
            int target = (scale >= 0.99f || orig <= floor) ? orig :
                Mathf.Max(floor, Mathf.FloorToInt(orig * scale));
            if (live != target)
            {
                if (density) lod.FixedDensity = target;
                else lod.FixedDetail = target;
            }
            return target;
        }

        // Curl frequency lives in RenderSettings (the 卷曲密度 slider →
        // WavinessFrequency) and needs particlesData.UpdateSettings() to
        // take effect — same re-baselining rule as the int fields, float
        // variant with no floor.
        private static float ScaleHairCurl(
            GPUTools.Hair.Scripts.HairSettings hs, HairState st, float scale)
        {
            var rs = hs.RenderSettings;
            if (rs == null || st.origCurl < 0f) return -1f;
            float live = rs.WavinessFrequency;
            if (st.lastWCurl >= 0f && live != st.lastWCurl)
                st.origCurl = live;
            float target = scale >= 0.99f
                ? st.origCurl : st.origCurl * scale;
            if (live != target)
            {
                rs.WavinessFrequency = target;
                try
                {
                    var cmd = hs.HairBuidCommand;
                    if (cmd != null && cmd.particlesData != null)
                        cmd.particlesData.UpdateSettings();
                }
                catch { }
            }
            return target;
        }

        private static int ScaleCloth(GPUTools.Cloth.Scripts.ClothSettings cs,
            bool outer, ClothState st, float scale)
        {
            int orig = outer ? st.origIter : st.origInner;
            int lastW = outer ? st.lastWIter : st.lastWInner;
            int live = outer ? cs.Iterations : cs.InnerIterations;
            if (lastW >= 0 && live != lastW) orig = live;
            if (outer) st.origIter = orig; else st.origInner = orig;
            // Cloth converges poorly when cut — only trim items that have
            // real headroom (>2 iterations).
            int target = (scale >= 0.99f || orig <= 2) ? orig :
                Mathf.Max(1, Mathf.RoundToInt(orig * scale));
            if (live != target)
            {
                if (outer) cs.Iterations = target; else cs.InnerIterations = target;
            }
            return target;
        }

        private static List<T> GetControls<T>(Atom atom, Dictionary<Atom, List<T>> cache)
            where T : Component
        {
            List<T> list;
            if (!cache.TryGetValue(atom, out list)) cache[atom] = list = new List<T>();
            bool stale = list.Count == 0;
            if (!stale)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i] == null) { stale = true; break; }
            }
            if (stale)
            {
                list.Clear();
                try { atom.GetComponentsInChildren(true, list); }
                catch { }
            }
            return list;
        }

        internal static void Restore()
        {
            foreach (KeyValuePair<HairSimControl, HairState> kv in _hair)
            {
                try
                {
                    var h = kv.Key;
                    if (h == null || h.hairSettings == null) continue;
                    if (h.hairSettings.LODSettings != null)
                    {
                        h.hairSettings.LODSettings.FixedDensity = kv.Value.origDensity;
                        h.hairSettings.LODSettings.FixedDetail = kv.Value.origDetail;
                    }
                    if (kv.Value.origCurl >= 0f &&
                        h.hairSettings.RenderSettings != null)
                    {
                        h.hairSettings.RenderSettings.WavinessFrequency =
                            kv.Value.origCurl;
                        try
                        {
                            var cmd = h.hairSettings.HairBuidCommand;
                            if (cmd != null && cmd.particlesData != null)
                                cmd.particlesData.UpdateSettings();
                        }
                        catch { }
                    }
                    if (kv.Value.collSnapshotted &&
                        h.hairSettings.PhysicsSettings != null)
                        h.hairSettings.PhysicsSettings.IsCollisionEnabled = kv.Value.origColl;
                }
                catch { }
            }
            foreach (KeyValuePair<ClothSimControl, ClothState> kv in _cloth)
            {
                try
                {
                    var c = kv.Key;
                    if (c != null && c.clothSettings != null)
                    {
                        c.clothSettings.Iterations = kv.Value.origIter;
                        c.clothSettings.InnerIterations = kv.Value.origInner;
                        if (kv.Value.enabledSnapshotted)
                            c.clothSettings.enabled = kv.Value.origEnabled;
                    }
                }
                catch { }
            }
            _hair.Clear();
            _cloth.Clear();
            _hairByAtom.Clear();
            _clothByAtom.Clear();
            _censusLogged = false;
        }
    }
}
