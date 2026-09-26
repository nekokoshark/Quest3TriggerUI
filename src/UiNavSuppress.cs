using System.Collections.Generic;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Shared ownership of SuperController.disableAllNavigation.
    //
    // The flag is a plain bool and several features want it (browser scroll
    // suppression, dock scroll suppression, ...). Save/restore writers
    // corrupt each other: whoever releases last writes back a value captured
    // while the flag was already true — including captures taken during a
    // preset load — and the flag stays stuck on with no owner left.
    //
    // This holder is name-based instead: the flag is exactly "any holder",
    // so a release can never resurrect a stale capture. Writes happen only
    // on acquire/release transitions, not per frame, so a third party (VaM
    // or another plugin) toggling the flag for its own reasons is not
    // continuously fought.
    internal static class UiNavSuppress
    {
        private static readonly List<string> _holders = new List<string>(4);

        internal static bool Held { get { return _holders.Count > 0; } }

        internal static void Acquire(string name)
        {
            if (_holders.Contains(name)) return;
            _holders.Add(name);
            Apply();
        }

        internal static void Release(string name)
        {
            if (!_holders.Remove(name)) return;
            Apply();
        }

        // Plugin-wide teardown: drop every hold — a destroyed feature must
        // not leave nav locked for the rest of the session.
        internal static void ReleaseAll()
        {
            if (_holders.Count == 0) return;
            _holders.Clear();
            Apply();
        }

        private static void Apply()
        {
            SuperController sc = SuperController.singleton;
            if (sc != null) sc.disableAllNavigation = _holders.Count > 0;
        }
    }
}
