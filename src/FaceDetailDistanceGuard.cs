using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace Quest3TriggerUI
{
    // The VAMSOY scenes reuse one template woman, and her head carries two
    // things that fall apart with distance while nothing else does.
    //
    // 1. A set of razor-thin facial overlays - CMA_EYESSHADOW F/UP/UP 2,
    //    CMA_IRIS REFLEX, VL_13:Lacrimal_gland_v2, AnalogueBob:Realistic Eyes,
    //    paledriver:Eyes upper/side shadow, Qing:EYE2. Their textures are
    //    alpha ~0 for nearly every texel, so a distant mip averages the
    //    transparent RGB back in and paints a grey film over the whole face;
    //    the same overlays at mip 0 are invisible, which is why walking closer
    //    clears it.
    //
    // 2. The lashes, VL_13:Lashes_upper / Lashes_lower. In VaM these are hair
    //    (DAZHairGroup), so they go through the GPUTools hair pipeline, which
    //    does have distance LOD (HairLODSettings / IsPhysicsEnabledLOD). The
    //    simplification collapses fine strands into long triangles, which read
    //    as spikes around the eyes.
    //
    // VaM has no person-mesh distance LOD of its own (only the hair-side
    // settings above), so nothing degrades these politely. This guard takes
    // them out of the renderer instead, but only beyond a tuned distance where
    // they cannot be read anyway. Preset state is untouched: item.active and
    // item.enabled are never written. Only the worn instance's Renderers are
    // switched off, each switch is recorded, and every switch is undone the
    // moment the camera closes in, the item unloads, the atom is turned off,
    // the scene changes or the plugin shuts down.
    //
    // Distance is measured from the camera to the item's own world bounds, so
    // a kneeling or reclining pose is measured against the face rather than
    // the atom origin. Hide and show use separate thresholds so a viewer
    // parked on the boundary does not flicker.
    internal static class FaceDetailDistanceGuard
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> HideDistance;

        private const BindingFlags All =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo InstanceField =
            typeof(JSONStorableDynamic).GetField("instance", All);

        // Substring match, case-insensitive, against uid + displayName + tags.
        // The region classifier table cannot see the VAMSOY names: it matches
        // whole tokens, so "CMA_EYESSHADOW" splits into "cma"/"eyesshadow" and
        // the table's "eye"/"mask" both miss, and "Qing:EYE2" splits into
        // "qing"/"eye2". Substrings catch every name measured in those scenes.
        // Deliberately short and eye-specific: no bare "lash" ("splash"), no
        // "brow" ("brown"), no "face" ("surface"), no bare "shadow".
        private static readonly string[] Terms = {
            "eye", "iris", "pupil", "lashes", "eyelash", "lacrimal", "sclera",
            "cornea", "eyelid", "tears", "睫毛", "瞳孔", "虹膜", "眼影", "眼"
        };

        private sealed class Entry
        {
            internal Transform Root;
            internal Renderer[] Renderers;
            internal readonly List<Renderer> SwitchedOff = new List<Renderer>(4);
            internal bool Hidden;
            internal long LastSeen;
        }

        private static readonly Dictionary<int, Entry> Tracked =
            new Dictionary<int, Entry>();
        private static readonly HashSet<int> Cleared = new HashSet<int>();
        private static readonly HashSet<string> LoggedItems = new HashSet<string>();
        private static readonly List<int> Stale = new List<int>();

        private const float ScanSeconds = 0.25f;
        private const float ShowRatio = 0.85f;

        private static long _scan;
        private static float _nextScan = -1f;
        private static long _hideEvents;
        private static long _showEvents;
        private static bool _loggedNoField;

        internal static void Tick()
        {
            if (Enabled == null || !Enabled.Value) return;
            SuperController sc = SuperController.singleton;
            if (sc == null || sc.isLoading) return;
            if (InstanceField == null)
            {
                if (!_loggedNoField)
                {
                    _loggedNoField = true;
                    Log("no JSONStorableDynamic.instance field; guard idle");
                }
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now < _nextScan) return;
            _nextScan = now + ScanSeconds;

            float hide = HideDistance != null ? HideDistance.Value : 3f;
            if (hide < 0.2f) hide = 0.2f;
            float show = hide * ShowRatio;
            Vector3 eye = EyePoint(sc);

            List<Atom> atoms;
            try { atoms = sc.GetAtoms(); } catch { return; }
            if (atoms == null) return;

            _scan++;
            for (int a = 0; a < atoms.Count; a++)
            {
                Atom atom = atoms[a];
                if (atom == null || atom.type != "Person") continue;
                DAZCharacterSelector sel = null;
                try { sel = atom.GetStorableByID("geometry") as DAZCharacterSelector; }
                catch { sel = null; }
                if (sel == null) continue;
                ScanItems(sel.clothingItems, eye, hide, show);
                ScanItems(sel.hairItems, eye, hide, show);
            }

            // Nothing seen this round is still worn on an active person:
            // unloaded, swapped away, or the atom was turned off. Put the
            // renderers back and drop the entry, so the guard never keeps a
            // reference to a dead instance alive.
            Stale.Clear();
            foreach (KeyValuePair<int, Entry> pair in Tracked)
                if (pair.Value.LastSeen != _scan) Stale.Add(pair.Key);
            for (int i = 0; i < Stale.Count; i++)
            {
                Entry entry = Tracked[Stale[i]];
                if (entry.Hidden) { Restore(entry); _showEvents++; }
                Tracked.Remove(Stale[i]);
            }
        }

        private static void ScanItems(DAZDynamicItem[] items, Vector3 eye,
            float hide, float show)
        {
            if (items == null) return;
            for (int i = 0; i < items.Length; i++)
            {
                DAZDynamicItem item = items[i];
                if (item == null || !item.active || !item.ready) continue;
                if (!FaceDetail(item)) continue;

                Transform root;
                try { root = InstanceField.GetValue(item) as Transform; }
                catch { root = null; }
                if (root == null) continue;

                int id = item.GetInstanceID();
                Entry entry;
                if (!Tracked.TryGetValue(id, out entry) || entry.Root != root)
                {
                    if (entry != null)
                    {
                        if (entry.Hidden) { Restore(entry); _showEvents++; }
                        Tracked.Remove(id);
                    }
                    Renderer[] renderers;
                    try { renderers = root.GetComponentsInChildren<Renderer>(true); }
                    catch { renderers = null; }
                    if (renderers == null || renderers.Length == 0) continue;
                    entry = new Entry();
                    entry.Root = root;
                    entry.Renderers = renderers;
                    Tracked[id] = entry;
                }
                entry.LastSeen = _scan;

                float distance = Distance(entry.Renderers, eye);
                if (distance < 0f) continue;

                bool wantHidden = entry.Hidden ? distance > show : distance >= hide;
                if (wantHidden)
                {
                    if (!entry.Hidden)
                    {
                        entry.Hidden = true;
                        _hideEvents++;
                        LogItem(item, distance);
                    }
                    SwitchOff(entry);
                }
                else if (entry.Hidden)
                {
                    Restore(entry);
                    _showEvents++;
                }
            }
        }

        private static bool FaceDetail(DAZDynamicItem item)
        {
            int id = item.GetInstanceID();
            if (Cleared.Contains(id)) return false;
            if (Hit(item.uid) || Hit(item.displayName)) return true;
            string[] tags = item.tagsArray;
            if (tags != null)
                for (int i = 0; i < tags.Length; i++)
                    if (Hit(tags[i])) return true;
            if (Cleared.Count < 4096) Cleared.Add(id);
            return false;
        }

        private static bool Hit(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < Terms.Length; i++)
                if (value.IndexOf(Terms[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static float Distance(Renderer[] renderers, Vector3 eye)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                Bounds bounds = renderer.bounds;
                Vector3 center = bounds.center;
                Vector3 extents = bounds.extents;
                min = Vector3.Min(min, center - extents);
                max = Vector3.Max(max, center + extents);
                any = true;
            }
            if (!any) return -1f;
            return Vector3.Distance(eye, (min + max) * 0.5f);
        }

        // Idempotent: only renderers that are currently on are switched off, so
        // a native skin/hair refresh that re-enables one is corrected on the
        // next scan instead of being recorded twice.
        private static void SwitchOff(Entry entry)
        {
            for (int i = 0; i < entry.Renderers.Length; i++)
            {
                Renderer renderer = entry.Renderers[i];
                if (renderer == null || !renderer.enabled) continue;
                renderer.enabled = false;
                entry.SwitchedOff.Add(renderer);
            }
        }

        // Only the renderers this guard switched off are switched back on; a
        // renderer something else had disabled stays disabled.
        private static void Restore(Entry entry)
        {
            for (int i = 0; i < entry.SwitchedOff.Count; i++)
            {
                Renderer renderer = entry.SwitchedOff[i];
                if (renderer != null) renderer.enabled = true;
            }
            entry.SwitchedOff.Clear();
            entry.Hidden = false;
        }

        private static Vector3 EyePoint(SuperController sc)
        {
            if (sc.lookCamera != null) return sc.lookCamera.transform.position;
            if (sc.centerCameraTarget != null)
                return sc.centerCameraTarget.transform.position;
            return sc.transform.position;
        }

        private static void LogItem(DAZDynamicItem item, float distance)
        {
            string label = item.uid;
            if (string.IsNullOrEmpty(label)) label = item.displayName;
            if (string.IsNullOrEmpty(label) ||
                LoggedItems.Count >= 24 || LoggedItems.Contains(label)) return;
            LoggedItems.Add(label);
            Log("hiding face detail at " + distance.ToString("0.00") +
                "m: " + label);
        }

        // Logged once per runtime generation so the effective thresholds are
        // never a guess: config entries are only re-read when the payload
        // reloads.
        internal static void Report()
        {
            Log("enabled=" + (Enabled != null && Enabled.Value) +
                " hideDistance=" +
                (HideDistance != null ? HideDistance.Value.ToString("0.00") : "?") +
                "m showDistance=" +
                (HideDistance != null
                    ? (HideDistance.Value * ShowRatio).ToString("0.00") : "?") +
                "m tracked=" + Tracked.Count +
                " hidden=" + HiddenCount() +
                " hideEvents=" + _hideEvents +
                " showEvents=" + _showEvents +
                (Enabled != null && Enabled.Value
                    ? "; facial overlays and lashes are switched off past that distance"
                    : "; every renderer left alone"));
        }

        private static int HiddenCount()
        {
            int count = 0;
            foreach (KeyValuePair<int, Entry> pair in Tracked)
                if (pair.Value.Hidden) count++;
            return count;
        }

        internal static void Shutdown()
        {
            foreach (KeyValuePair<int, Entry> pair in Tracked)
                if (pair.Value.Hidden) Restore(pair.Value);
            Tracked.Clear();
            Cleared.Clear();
            LoggedItems.Clear();
            Stale.Clear();
            _scan = 0;
            _nextScan = -1f;
            _hideEvents = 0;
            _showEvents = 0;
            _loggedNoField = false;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[face-distance] " + message);
        }
    }
}
