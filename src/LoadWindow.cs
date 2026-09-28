using UnityEngine;

namespace Quest3TriggerUI
{
    // One place that answers "is a person restore still in flight?".
    //
    // Every background job in this plugin was designed for idle time: audio
    // cache eviction, the texture orphan sweep, the scene orphan sweep and the
    // BC7 cache conversion. Their guards only ever tested
    // SuperController.isLoading and SceneLoadAccelerator.SceneLoadActive, and
    // both of those describe a *scene* load. A clothing / appearance / person
    // preset restore runs with both of them false, so those jobs kept firing
    // inside exactly the window that dominates the measured long frames.
    //
    // PresetDeltaApply already hooks the restore: Begin opens a scope,
    // BeginReset and the per-item activation inside that scope run for the
    // whole restore. Marking the window there costs one float store per
    // activation and needs no new Harmony patch.
    internal static class LoadWindow
    {
        // How long the window stays open after the last preset activity. The
        // restore itself is ~1-2s; this covers the gap between its end and the
        // texture pipeline reporting busy.
        private const float QuietSeconds = 3f;
        // A session that never goes quiet (a loader stuck re-activating, a
        // paused restore) must not starve the memory jobs forever.
        private const float MaxSeconds = 120f;

        private static bool _presetBusy;
        private static float _busySince;
        private static float _busyUntil;
        private static readonly System.Collections.Generic.HashSet<string> _logged =
            new System.Collections.Generic.HashSet<string>();

        // Called from the preset restore hooks on the main thread.
        internal static void NoteActivity()
        {
            float now = Time.realtimeSinceStartup;
            _busyUntil = now + QuietSeconds;
            if (!_presetBusy)
            {
                _presetBusy = true;
                _busySince = now;
                _logged.Clear();
            }
        }

        // True while a person / clothing / appearance preset restore is running
        // or still settling.
        internal static bool PresetBusy
        {
            get
            {
                if (!_presetBusy) return false;
                float now = Time.realtimeSinceStartup;
                if (now - _busySince > MaxSeconds)
                {
                    _presetBusy = false;
                    return false;
                }
                if (now < _busyUntil) return true;
                _presetBusy = false;
                return false;
            }
        }

        // One line per job per window, so a single swap proves the window both
        // opened and suppressed work.
        internal static void NoteDeferred(string who)
        {
            try
            {
                if (_logged.Add(who))
                    WardrobeJanitor.Log("[加载窗口] " + who + " 在人物预设窗口内让出");
            }
            catch { }
        }

        internal static void Clear()
        {
            _presetBusy = false;
            _busySince = 0f;
            _busyUntil = 0f;
            _logged.Clear();
        }
    }
}
