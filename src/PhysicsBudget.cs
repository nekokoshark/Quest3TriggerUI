using System.Collections.Generic;
using UnityEngine;

namespace Quest3TriggerUI
{
    /// <summary>
    /// Physics/GPU-sim quality budget. Runtime-only, fully revertible — no
    /// JSON-backed value is ever touched, no freezing, no rate changes
    /// (both measured net-negative: rate drops FPS via render pacing, and
    /// freeze/thaw transitions hitch on view movement).
    ///
    ///   1. solverIterations cap on every atom's PhysicsSimulator (PhysX CPU)
    ///   2. GPUTools hair/cloth iteration scaling via GpuSimBudget (GPU)
    ///
    /// Level 0 disables and restores everything; originals are snapshotted
    /// on first touch and restored on OnDestroy (hot-reload safe).
    /// </summary>
    internal static class PhysicsBudget
    {
        internal static int Level;                        // 0=off 1=balanced 2=aggressive
        internal static int SolverCapOverride = -1;       // >0 cap override, 0=disable cap
        internal static float HairScaleOverride = -1f;    // 0..1 density/detail scale, 0=off
        internal static float HairCurlScaleOverride = -1f; // 0..1 curl frequency scale, -1=level default
        internal static int HairCollisionMode = -1;       // -1=default (aggr only), 0=never, 1=always
        internal static float ClothScaleOverride = -1f;   // 0..1 iteration scale, 0=off
        internal static int ClothOffBelowOverride = -1;   // particles ≤ N → sim off; 0=never
        internal static int HairCollOffAboveOverride = -1; // ≥N particles → collision off; -1=level default, 0=never
        internal static BepInEx.Configuration.ConfigEntry<int> LevelEntry;
        internal static BepInEx.Configuration.ConfigEntry<int> CollEntry;

        private const float ScanSeconds = 1.0f;

        private static float _nextScan;
        private static int _appliedLevel = -1;
        private static bool _collOnlyApplied;
        private static bool _simsLogged;
        private static readonly Dictionary<PhysicsSimulator, int> _origIterations =
            new Dictionary<PhysicsSimulator, int>();

        private static void ParamsFor(int level, out int cap,
            out float hairDensity, out float hairDetail, out float hairCurl,
            out bool hairCollOff, out int hairCollOffAbove,
            out float clothScale, out int clothOffBelow)
        {
            // Solver cap measured harmful on real content (collider chains
            // under-converge → soft-body clothing sags) — off in presets,
            // SolverCap override remains for manual tuning.
            if (level <= 0)
            {
                // Neutral set — only reached when the standalone collision
                // toggle runs the scan with no budget level active.
                cap = 0; hairDensity = 1f; hairDetail = 1f;
                hairCurl = 1f; hairCollOff = false;
                hairCollOffAbove = 0;
                clothScale = 1f; clothOffBelow = 0;
            }
            else if (level >= 2)
            {
                cap = 0; hairDensity = 0.75f; hairDetail = 0.6f;
                hairCurl = 0.6f; hairCollOff = true;
                hairCollOffAbove = 0;
                clothScale = 0.66f; clothOffBelow = 500;
            }
            else
            {
                // Balanced: collision off only for genuinely heavy hair —
                // density×detail ≈ particle count, 20000 ≈ long dense hair.
                cap = 0; hairDensity = 0.85f; hairDetail = 0.75f;
                hairCurl = 0.75f; hairCollOff = false;
                hairCollOffAbove = 20000;
                clothScale = 0.8f; clothOffBelow = 250;
            }
            if (SolverCapOverride >= 0) cap = SolverCapOverride;
            if (HairScaleOverride >= 0f)
            {
                float hs = HairScaleOverride < 0.001f ? 1.0f : HairScaleOverride;
                hairDensity = hs; hairDetail = hs;
            }
            if (HairCurlScaleOverride >= 0f)
                hairCurl = HairCurlScaleOverride < 0.001f
                    ? 1.0f : HairCurlScaleOverride;
            if (HairCollisionMode >= 0) hairCollOff = HairCollisionMode == 1;
            if (HairCollOffAboveOverride >= 0) hairCollOffAbove = HairCollOffAboveOverride;
            // Mode 0 = "never": the toggle's off state must not touch hair
            // collision at all — suppress the level's collision policy too.
            if (HairCollisionMode == 0) { hairCollOff = false; hairCollOffAbove = 0; }
            if (ClothScaleOverride >= 0f)
                clothScale = ClothScaleOverride < 0.001f ? 1.0f : ClothScaleOverride;
            if (ClothOffBelowOverride >= 0) clothOffBelow = ClothOffBelowOverride;
        }

        // 1s cadence; covers atoms added mid-scene.
        internal static void Tick()
        {
            if (Level != _appliedLevel)
            {
                if (Level <= 0) Restore();
                else Apply(Level);
            }
            // The standalone collision toggle keeps the scan alive at
            // level 0; releasing it restores what it had switched off.
            bool collOnly = _appliedLevel <= 0 && HairCollisionMode == 1;
            if (!collOnly && _collOnlyApplied)
            {
                GpuSimBudget.Restore();
                _collOnlyApplied = false;
            }
            if (_appliedLevel <= 0 && !collOnly) return;
            if (collOnly) _collOnlyApplied = true;
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            Scan();
        }

        internal static void SetLevel(int level)
        {
            Level = Mathf.Clamp(level, 0, 2);
        }

        // Quick-action entry: cycle 0→1→2→0 and persist the choice.
        internal static void Cycle()
        {
            SetLevelEntry((Level + 1) % 3);
        }

        // Menu button: toggle collision force-off (1) ↔ never touch (0).
        // Off means hands-off even when a budget level is active.
        internal static void ToggleHairCollision()
        {
            HairCollisionMode = HairCollisionMode == 1 ? 0 : 1;
            if (CollEntry != null) CollEntry.Value = HairCollisionMode;
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo(
                    "Hair collision mode=" + HairCollisionMode);
        }

        internal static void SetLevelEntry(int level)
        {
            SolverCapOverride = -1;
            HairScaleOverride = -1f;
            ClothScaleOverride = -1f;
            ClothOffBelowOverride = -1;
            HairCollOffAboveOverride = -1;
            HairCurlScaleOverride = -1f;
            SetLevel(level);
            if (LevelEntry != null) LevelEntry.Value = Level;
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("Physics budget level=" + Level);
        }

        private static void Apply(int level)
        {
            _appliedLevel = level;
            _nextScan = 0f;
            Scan();
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo(
                    "Physics budget level " + level + " applied.");
        }

        internal static void Restore()
        {
            bool hadState = _origIterations.Count > 0;
            foreach (KeyValuePair<PhysicsSimulator, int> kv in _origIterations)
            {
                try { if (kv.Key != null) kv.Key.solverIterations = kv.Value; }
                catch { }
            }
            _origIterations.Clear();
            GpuSimBudget.Restore();
            _appliedLevel = 0;
            _simsLogged = false;
            if (hadState && Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("Physics budget restored.");
        }

        private static void Scan()
        {
            SuperController sc = SuperController.singleton;
            if (sc == null) return;
            int cap, clothOffBelow, hairCollOffAbove;
            float hairDensity, hairDetail, hairCurl, clothScale;
            bool hairCollOff;
            ParamsFor(_appliedLevel, out cap, out hairDensity, out hairDetail,
                out hairCurl, out hairCollOff,
                out hairCollOffAbove, out clothScale, out clothOffBelow);
            if (cap <= 0 && _origIterations.Count > 0)
            {
                foreach (KeyValuePair<PhysicsSimulator, int> kv in _origIterations)
                {
                    try { if (kv.Key != null) kv.Key.solverIterations = kv.Value; }
                    catch { }
                }
                _origIterations.Clear();
            }
            List<Atom> atoms = sc.GetAtoms();
            if (atoms == null) return;
            var typeNames = _simsLogged
                ? null : new Dictionary<string, int>();
            for (int i = 0; i < atoms.Count; i++)
            {
                Atom atom = atoms[i];
                if (atom == null) continue;
                if (hairDensity < 0.999f || hairDetail < 0.999f ||
                    hairCurl < 0.999f || hairCollOff || hairCollOffAbove > 0 ||
                    clothScale < 0.999f ||
                    clothOffBelow > 0 || GpuSimBudget.HasState)
                    GpuSimBudget.ScanAtom(atom, hairDensity, hairDetail,
                        hairCurl, hairCollOff,
                        hairCollOffAbove, clothScale, clothOffBelow);
                PhysicsSimulator[] sims;
                try { sims = atom.physicsSimulators; }
                catch { continue; }
                if (sims == null) continue;
                for (int s = 0; s < sims.Length; s++)
                {
                    PhysicsSimulator sim = sims[s];
                    if (sim == null) continue;
                    if (typeNames != null)
                    {
                        string key = atom.uid + ":" + sim.GetType().Name;
                        int n;
                        typeNames[key] = typeNames.TryGetValue(key, out n) ? n + 1 : 1;
                    }
                    if (cap > 0) CapIterations(sim, cap);
                }
            }
            if (typeNames != null && Quest3TriggerUIPlugin.Log != null)
            {
                _simsLogged = true;
                var sb = new System.Text.StringBuilder();
                foreach (KeyValuePair<string, int> kv in typeNames)
                    sb.Append(kv.Key).Append('x').Append(kv.Value).Append("; ");
                Quest3TriggerUIPlugin.Log.LogInfo("Physics budget sim census: " + sb);
            }
        }

        private static void CapIterations(PhysicsSimulator sim, int cap)
        {
            try
            {
                if (!_origIterations.ContainsKey(sim))
                    _origIterations[sim] = sim.solverIterations;
                int target = System.Math.Min(_origIterations[sim], cap);
                if (sim.solverIterations != target)
                    sim.solverIterations = target;
            }
            catch { }
        }
    }
}
