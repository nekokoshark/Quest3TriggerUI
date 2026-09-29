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
    // Default off. Turning this on changes behaviour on the swap path, so it is
    // a separate decision from installing the probe.
    internal static class UuaGate
    {
        internal static ConfigEntry<bool> Enabled;

        private static AsyncOperation _cached;
        private static long _skipped;
        private static long _passed;
        private static long _unrecognisedLogged;
        private static float _lastSkipAt = -1f;

        internal static long SkippedCount { get { return _skipped; } }
        internal static long PassedCount { get { return _passed; } }
        internal static float LastSkipAt { get { return _lastSkipAt; } }

        // Called from the census prefix - the same patch invocation that would
        // have measured this sweep - so Harmony patch order cannot make the
        // decision read a stale caller.
        internal static bool ShouldSkip(string who, string chain)
        {
            if (Enabled == null || !Enabled.Value) return false;
            if (chain == null || chain.IndexOf("PresetSweepGate.SubmitSweep",
                    StringComparison.Ordinal) < 0)
            {
                _passed++;
                if (_unrecognisedLogged < 4)
                {
                    _unrecognisedLogged++;
                    Log("pass through (not the preset path): who=" + who + " chain=" + chain);
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
            if (_cached == null)
            {
                Log("no completed operation cached yet; running this one so there is " +
                    "something to hand back next time");
                return false;
            }
            return true;
        }

        internal static AsyncOperation OnSkipped(string who)
        {
            _skipped++;
            _lastSkipAt = Time.realtimeSinceStartup;
            Log("skipped #" + _skipped + " who=" + who +
                " passed=" + _passed + " (returned a completed operation instead)");
            return _cached;
        }

        // Only a real sweep can mint the token handed back while skipping.
        internal static void NoteSweep(AsyncOperation operation)
        {
            if (operation != null) _cached = operation;
        }

        // Logged once per runtime generation so the effective setting is never
        // a guess: the config entry is only re-read when the payload reloads.
        internal static void Report()
        {
            Log("enabled=" + (Enabled != null && Enabled.Value) +
                " passed=" + _passed + " skipped=" + _skipped +
                (Enabled != null && Enabled.Value
                    ? "; preset-path sweeps will be demoted once one real sweep has run"
                    : "; passing every sweep through"));
        }

        internal static void Shutdown()
        {
            _cached = null;
            _skipped = 0;
            _passed = 0;
            _unrecognisedLogged = 0;
            _lastSkipAt = -1f;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[uua-gate] " + message);
        }
    }
}