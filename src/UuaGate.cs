using System;
using BepInEx.Configuration;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Demotes ONE class of native sweep: the one the preset path submits after
    // a swap settles. Measured over four real swaps (character x2, appearance,
    // clothing) that sweep costs 8.57-9.12s of main-thread stall and reclaims
    // 0-16 MiB of CPU allocation, 0 MiB of VRAM, 3-6 materials, 0-3 textures
    // totalling <=1 MiB and no render targets - while the managed GC in the
    // same swap frees 2.5-3.0GB in ~3.7s. It is pure overhead on this path.
    //
    // Everything else keeps its sweep: standby compression, scene load, the
    // janitor's post-preset cleanup, and any caller this gate does not
    // recognise. The decision is therefore negative-by-default: it skips only
    // when the caller chain names PresetSweepGate.SubmitSweep and does not name
    // any of the exempt paths.
    //
    // Unity offers no way to construct an AsyncOperation, and the callers do
    // use the return value (PresetSweepGate stores it and polls isDone,
    // WardrobeJanitor yields on it), so a skipped call hands back the operation
    // from the last real sweep. That object is already complete, so callers
    // settle immediately instead of blocking - which is the entire point.
    //
    // 14.70 widened the demotable set to the janitor's supplemental sweep as
    // well: both cost 7.6-7.8s for <=43MiB, and the janitor one is by
    // construction a second full mark of the same release epoch.
    //
    // A demoted call is never allowed to reset the backstop (the postfix hands
    // our own operation straight back), and past BackstopSeconds or MaxSkips a
    // qualifying call really sweeps, so a session spent only swapping cannot
    // drift forever. The completed operation is carried across payload
    // reloads through GenBridge, so the first swap after a reload no longer
    // pays for the cold start (static fields do not survive the reload).
    internal static class UuaGate
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> BackstopSeconds;
        internal static ConfigEntry<int> MaxSkips;

        // Carried across payload reloads: statics are wiped by the reload, and
        // AppDomain.SetData is a no-op on this mono (GenBridge header).
        private const string CarriedKey = "uua.completed";

        private static AsyncOperation _cached;
        private static long _skipped;
        private static long _passed;
        private static long _unrecognisedLogged;
        private static float _lastSkipAt = -1f;
        private static float _lastRealAt = -1f;
        private static int _skipsSinceReal;

        internal static long SkippedCount { get { return _skipped; } }
        internal static long PassedCount { get { return _passed; } }
        internal static float LastSkipAt { get { return _lastSkipAt; } }

        // Caller chains this gate may demote. Both are ours: the sweep
        // PresetSweepGate submits after a swap settles, and the janitor's
        // supplemental one, which exists only to coalesce against a sweep that
        // already covered the same release epoch.
        private static bool Demotable(string chain)
        {
            return chain.IndexOf("PresetSweepGate.SubmitSweep",
                    StringComparison.Ordinal) >= 0 ||
                chain.IndexOf("PresetCleanupCoalescer.StartSweep",
                    StringComparison.Ordinal) >= 0;
        }

        // Called from the census prefix - the same patch invocation that would
        // have measured this sweep - so Harmony patch order cannot make the
        // decision read a stale caller.
        internal static bool ShouldSkip(string who, string chain)
        {
            if (Enabled == null || !Enabled.Value) return false;
            if (chain == null || !Demotable(chain))
            {
                _passed++;
                if (_unrecognisedLogged < 4)
                {
                    _unrecognisedLogged++;
                    Log("pass through (not a demotable path): who=" + who + " chain=" + chain);
                }
                return false;
            }
            if (chain.IndexOf("WardrobeJanitor", StringComparison.Ordinal) >= 0 ||
                chain.IndexOf("FastStandbyController", StringComparison.Ordinal) >= 0 ||
                chain.IndexOf("SceneLoadAccelerator", StringComparison.Ordinal) >= 0)
            {
                _passed++;
                Log("pass through (exempt path): who=" + who + " chain=" + chain);
                return false;
            }
            if (_cached == null || !_cached.isDone)
            {
                Log("no completed operation cached yet; running this one so there is " +
                    "something to hand back next time");
                return false;
            }
            float now = Time.realtimeSinceStartup;
            int backstop = (BackstopSeconds != null && BackstopSeconds.Value > 0)
                ? BackstopSeconds.Value : 180;
            int maxSkips = (MaxSkips != null && MaxSkips.Value > 0)
                ? MaxSkips.Value : 8;
            if (_lastRealAt >= 0f &&
                (now - _lastRealAt >= (float)backstop || _skipsSinceReal >= maxSkips))
            {
                Log("backstop: " + _skipsSinceReal + " demotions and " +
                    (long)(now - _lastRealAt) + "s since the last real sweep; " +
                    "letting this one run");
                return false;
            }
            return true;
        }

        internal static AsyncOperation OnSkipped(string who)
        {
            _skipped++;
            _skipsSinceReal++;
            _lastSkipAt = Time.realtimeSinceStartup;
            Log("skipped #" + _skipped + " who=" + who +
                " passed=" + _passed + " (returned a completed operation instead)");
            return _cached;
        }

        // Only a real sweep can mint the token handed back while skipping. A
        // demoted call returns our own operation through this same postfix, so
        // identity - not "was it called" - decides whether a sweep happened.
        internal static void NoteSweep(AsyncOperation operation)
        {
            if (operation == null) return;
            if (object.ReferenceEquals(operation, _cached)) return;
            _cached = operation;
            _lastRealAt = Time.realtimeSinceStartup;
            _skipsSinceReal = 0;
            try { GenBridge.Publish(CarriedKey, operation); } catch { }
        }

        private static void Adopt()
        {
            if (_cached != null) return;
            AsyncOperation carried = null;
            try { carried = GenBridge.Take(CarriedKey) as AsyncOperation; } catch { }
            if (carried == null) return;
            _cached = carried;
            _lastRealAt = Time.realtimeSinceStartup;
            _skipsSinceReal = 0;
            Log("adopted the completed sweep operation carried over from the " +
                "previous generation");
        }

        // Logged once per runtime generation so the effective setting is never
        // a guess: the config entry is only re-read when the payload reloads.
        internal static void Report()
        {
            Adopt();
            Log("enabled=" + (Enabled != null && Enabled.Value) +
                " cached=" + (_cached != null) +
                " passed=" + _passed + " skipped=" + _skipped +
                (Enabled != null && Enabled.Value
                    ? "; demotable sweeps are handed a completed operation once one " +
                      "real sweep has run, until the backstop comes due"
                    : "; passing every sweep through"));
        }

        internal static void Shutdown()
        {
            // The carried copy stays on the GenBridge anchor on purpose: the
            // next generation adopts it instead of paying a cold-start sweep.
            _cached = null;
            _skipped = 0;
            _passed = 0;
            _unrecognisedLogged = 0;
            _lastSkipAt = -1f;
            _lastRealAt = -1f;
            _skipsSinceReal = 0;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[uua-gate] " + message);
        }
    }
}