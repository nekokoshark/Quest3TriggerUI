using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Quest3TriggerUI
{
    // VaM never evicts decoded audio: every clip any plugin ever loaded
    // stays as float PCM in URLAudioClipManager.singleton /
    // EmbeddedAudioClipManager.singleton (~2GB observed, none referenced
    // by an AudioSource). This janitor drops clips that have had no
    // AudioSource reference for longer than the grace window, so audio
    // effectively loads on demand and is destroyed after scene exit.
    // URL clips re-decode lazily if requested again; embedded clips are
    // opt-in because their source data only exists in the manager.
    internal static class AudioCacheJanitor
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> GraceSeconds;
        internal static ConfigEntry<bool> IncludeEmbedded;
        internal static ConfigEntry<bool> EvictNow;

        private const float SweepEvery = 30f;

        // clips and the two-arg RemoveClip are not public — resolved once
        // via reflection; singleton fields are public and bound directly.
        private static readonly System.Reflection.FieldInfo ClipsField =
            typeof(AudioClipManager).GetField("clips",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.MethodInfo UrlRemove2 =
            typeof(URLAudioClipManager).GetMethod("RemoveClip",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic, null,
                new[] { typeof(NamedAudioClip), typeof(bool) }, null);
        private static readonly System.Reflection.MethodInfo BaseRemove =
            typeof(AudioClipManager).GetMethod("RemoveClip",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic, null,
                new[] { typeof(NamedAudioClip) }, null);
        private static readonly Dictionary<AudioClip, float> UnrefSince =
            new Dictionary<AudioClip, float>();
        // AudioSourceControl keeps a LinkedList<NamedAudioClip> queue for
        // clips queued while another is playing/paused, plus _playingClip.
        // Neither is assigned to audioSource.clip until dequeued, so a
        // clip-list sweep alone would evict music still waiting to play.
        private static readonly System.Reflection.FieldInfo QueueField =
            typeof(AudioSourceControl).GetField("queue",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo PlayingField =
            typeof(AudioSourceControl).GetField("_playingClip",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
        private static float _nextSweep;

        internal static void Tick()
        {
            var sc = SuperController.singleton;
            // Never evict mid-restore: scene load re-links clip references
            // and a sweep here could destroy clips the new scene is wiring.
            if (sc == null || sc.isLoading ||
                SceneLoadAccelerator.SceneLoadActive) return;
            if (EvictNow != null && EvictNow.Value)
            {
                EvictNow.Value = false;
                Sweep(true);
                return;
            }
            if (Enabled == null || !Enabled.Value) return;
            if (Time.unscaledTime < _nextSweep) return;
            _nextSweep = Time.unscaledTime + SweepEvery;
            Sweep(false);
        }

        internal static void SweepNow()
        {
            Sweep(true);
        }

        private static void Sweep(bool immediate)
        {
            try
            {
                var referenced = new HashSet<AudioClip>();
                foreach (UnityEngine.Object o in
                    Resources.FindObjectsOfTypeAll(typeof(AudioSource)))
                {
                    AudioSource s = o as AudioSource;
                    if (s != null && s.clip != null)
                        referenced.Add(s.clip);
                }
                // Queued-but-not-yet-playing clips and the control's
                // current clip are live references a clip-only sweep
                // cannot see.
                foreach (UnityEngine.Object o in
                    Resources.FindObjectsOfTypeAll(typeof(AudioSourceControl)))
                {
                    var q = QueueField != null
                        ? QueueField.GetValue(o)
                            as LinkedList<NamedAudioClip> : null;
                    if (q != null)
                        foreach (var nac in q)
                            if (nac != null && nac.sourceClip != null)
                                referenced.Add(nac.sourceClip);
                    var pc = PlayingField != null
                        ? PlayingField.GetValue(o) as NamedAudioClip : null;
                    if (pc != null && pc.sourceClip != null)
                        referenced.Add(pc.sourceClip);
                }

                float now = Time.unscaledTime;
                float grace = GraceSeconds != null ? GraceSeconds.Value : 180f;
                long freed = 0;
                int removed = 0;
                freed += EvictManager(URLAudioClipManager.singleton,
                    referenced, now, grace, immediate, false, ref removed);
                if (IncludeEmbedded != null && IncludeEmbedded.Value)
                    freed += EvictManager(EmbeddedAudioClipManager.singleton,
                        referenced, now, grace, immediate, true, ref removed);

                // Dead clip objects keep their dictionary slots; purge so the
                // table cannot grow without bound across many evictions.
                var dead = new List<AudioClip>();
                foreach (var kv in UnrefSince)
                    if (kv.Key == null) dead.Add(kv.Key);
                foreach (var c in dead) UnrefSince.Remove(c);

                if (removed > 0)
                {
                    if (SuperController.singleton != null)
                        SuperController.singleton.ValidateAllAtoms();
                    Quest3TriggerUIPlugin.Log.LogInfo(string.Format(
                        "[AudioJanitor] evicted {0} unreferenced clip(s), freed ~{1:F0}MB (referenced {2})",
                        removed, freed / 1048576.0, referenced.Count));
                }
            }
            catch (Exception ex)
            {
                Quest3TriggerUIPlugin.Log.LogError(
                    "[AudioJanitor] sweep failed: " + ex.Message);
            }
        }

        private static long EvictManager(AudioClipManager mgr,
            HashSet<AudioClip> referenced, float now, float grace,
            bool immediate, bool embedded, ref int removed)
        {
            if (mgr == null || ClipsField == null) return 0;
            var live = ClipsField.GetValue(mgr) as List<NamedAudioClip>;
            if (live == null) return 0;
            long freed = 0;
            // Removing mutates mgr.clips — iterate a copy.
            var snapshot = new List<NamedAudioClip>(live);
            for (int i = 0; i < snapshot.Count; i++)
            {
                NamedAudioClip nac = snapshot[i];
                if (nac == null || nac.destroyed) continue;
                AudioClip clip = nac.sourceClip;
                if (clip == null) continue;
                URLAudioClip uac = nac as URLAudioClip;
                if (uac != null && (uac.removed || !uac.ready)) continue;
                if (referenced.Contains(clip))
                {
                    UnrefSince.Remove(clip);
                    continue;
                }
                float since;
                if (!UnrefSince.TryGetValue(clip, out since))
                {
                    UnrefSince[clip] = now;
                    if (!immediate) continue;
                    since = now;
                }
                if (!immediate && now - since < grace) continue;

                long bytes = (long)clip.samples * clip.channels * 4;
                bool ok;
                if (embedded)
                {
                    // Base RemoveClip does not destroy the clip — we do.
                    ok = BaseRemove != null &&
                        (bool)BaseRemove.Invoke(mgr, new object[] { nac });
                    if (ok && nac.sourceClip != null)
                        UnityEngine.Object.Destroy(nac.sourceClip);
                }
                else
                {
                    // bool=false skips per-item ValidateAllAtoms; we validate
                    // once after the loop.
                    ok = UrlRemove2 != null &&
                        (bool)UrlRemove2.Invoke(mgr,
                            new object[] { nac, false });
                }
                if (ok)
                {
                    removed++;
                    freed += bytes;
                    UnrefSince.Remove(clip);
                }
            }
            return freed;
        }
    }
}
