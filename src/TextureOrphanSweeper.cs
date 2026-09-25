using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Only native character/material callback results are eligible. Unknown
    // consumers, UI and preload requests are not inferred to be orphans.
    // We drop cache ownership, never Destroy a texture behind a consumer.
    internal static class TextureOrphanSweeper
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> AgeSeconds;
        internal static ConfigEntry<int> MaxPerSweep;
        internal static ConfigEntry<int> UnusedBudgetMiB;
        private const float SweepEvery = 10f, MinimumGrace = 15f;
        private static float _nextSweep;
        private static bool _fieldsTried, _installTried;
        private static FieldInfo _cacheF, _countF, _trackedF;
        private static Harmony _harmony;
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private sealed class Candidate
        {
            internal WeakReference texture;
            internal float since = -1;
            internal long bytes;
            internal int id;
            internal bool protectedConsumer;
        }
        // Weak payloads and numeric keys: observer state cannot pin a texture.
        private static readonly Dictionary<int, Candidate> Candidates = new Dictionary<int, Candidate>();
        private static readonly HashSet<int> Seen = new HashSet<int>();
        private static readonly HashSet<int> Evict = new HashSet<int>();
        private static readonly List<int> Prune = new List<int>();
        private static readonly List<Candidate> Eligible = new List<Candidate>();
        private static readonly List<string> Keys = new List<string>();

        internal static void Install()
        {
            if (_installTried) return;
            _installTried = true;
            try
            {
                ResolveFields();
                if (_cacheF == null || _countF == null || _trackedF == null)
                    throw new MissingFieldException("native texture cache ownership fields");
                MethodInfo callback = typeof(ImageLoaderThreaded.QueuedImage).GetMethod("DoCallback", All);
                if (callback == null) throw new MissingMethodException("QueuedImage.DoCallback");
                _harmony = new Harmony("Quest3TriggerUI.texture-cache-ownership");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(callback,
                    prefix: new HarmonyMethod(typeof(TextureOrphanSweeper).GetMethod("BeforeCallback", BindingFlags.NonPublic | BindingFlags.Static)),
                    finalizer: new HarmonyMethod(typeof(TextureOrphanSweeper).GetMethod("AfterCallback", BindingFlags.NonPublic | BindingFlags.Static)));
                WardrobeJanitor.Log("cache ownership installed: weak native provenance; unused budget; no texture Destroy");
            }
            catch (Exception e)
            {
                if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
                _harmony = null;
                WardrobeJanitor.Log("cache ownership not installed: " + e.Message);
            }
        }

        private static void BeforeCallback(ImageLoaderThreaded.QueuedImage __instance, out bool __state)
        {
            __state = IsNativeRequest(__instance);
        }

        private static bool IsNativeRequest(ImageLoaderThreaded.QueuedImage q)
        {
            if (q == null || q.isThumbnail || q.isPreload || q.rawImageToLoad != null ||
                q.hadError || q.cancel || q.callback == null) return false;
            foreach (Delegate cb in q.callback.GetInvocationList())
            {
                Type type = cb.Method.DeclaringType;
                string name = cb.Method.Name;
                if (type == typeof(DAZCharacterTextureControl) && name == "OnImageLoaded") continue;
                if (type == typeof(MaterialOptions) &&
                    (name == "OnTexture1Loaded" || name == "OnTexture2Loaded" ||
                     name == "OnTexture3Loaded" || name == "OnTexture4Loaded" ||
                     name == "OnTexture5Loaded" || name == "OnTexture6Loaded")) continue;
                return false;
            }
            return true;
        }

        private static Exception AfterCallback(ImageLoaderThreaded.QueuedImage __instance, bool __state, Exception __exception)
        {
            // Native DoCallback clears its delegate on success. Classify before
            // that happens, then record only the texture identity after return.
            try
            {
                Texture2D t = __instance == null ? null : __instance.tex;
                if (t == null) return __exception;
                int id = t.GetInstanceID();
                if (!__state || __exception != null || (Enabled != null && !Enabled.Value))
                {
                    if (Enabled == null || Enabled.Value)
                        Candidates[id] = new Candidate { texture = new WeakReference(t), id = id, protectedConsumer = true };
                    else Candidates.Remove(id);
                    return __exception;
                }
                Candidate entry;
                if (!Candidates.TryGetValue(id, out entry) || !ReferenceEquals(entry.texture.Target, t))
                    Candidates[id] = new Candidate { texture = new WeakReference(t), id = id };
                // Reuse is activity even before a delayed receiver registers.
                else entry.since = -1;
            }
            catch (Exception e) { WardrobeJanitor.Log("cache ownership observation failed: " + e.Message); }
            return __exception;
        }

        internal static void ClearRuntime()
        {
            Candidates.Clear();
            ClearScratch();
            _nextSweep = 0;
        }

        private static void ClearScratch()
        {
            Seen.Clear(); Evict.Clear(); Prune.Clear(); Eligible.Clear(); Keys.Clear();
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _installTried = false;
            ClearRuntime();
        }

        internal static void Tick()
        {
            if (Enabled != null && !Enabled.Value) { ClearRuntime(); return; }
            float now = Time.realtimeSinceStartup;
            if (now < _nextSweep) return;
            _nextSweep = now + SweepEvery;
            if (WardrobeJanitor.ImagesBusy()) return;
            ResolveFields();
            var loader = ImageLoaderThreaded.singleton;
            if (loader == null || _cacheF == null || _countF == null || _trackedF == null) return;
            var cache = _cacheF.GetValue(loader) as Dictionary<string, Texture2D>;
            var counts = _countF.GetValue(loader) as Dictionary<Texture2D, int>;
            var tracked = _trackedF.GetValue(loader) as Dictionary<Texture2D, bool>;
            // Missing refcounts must NEVER be interpreted as zero owners.
            if (cache == null || counts == null || tracked == null) return;
            int released = 0;
            long releasedBytes = 0, unusedBytes = 0;
            try
            {
                foreach (var kv in cache)
                {
                    Texture2D t = kv.Value;
                    if (t == null) continue;
                    int id = t.GetInstanceID();
                    if (!Seen.Add(id)) continue;
                    Candidate entry;
                    if (!Candidates.TryGetValue(id, out entry) || !ReferenceEquals(entry.texture.Target, t) || entry.protectedConsumer) continue;
                    int count;
                    if (counts.TryGetValue(t, out count) && count > 0) { entry.since = -1; continue; }
                    if (entry.since < 0) entry.since = now;
                    entry.bytes = MemoryProbe.TexBytes(t.width, t.height, MemoryProbe.FormatBpp(t.format), t.mipmapCount);
                    unusedBytes += entry.bytes;
                    if (now - entry.since >= MinimumGrace) Eligible.Add(entry);
                }
                foreach (var kv in Candidates)
                    if (!Seen.Contains(kv.Key) || !kv.Value.texture.IsAlive) Prune.Add(kv.Key);
                foreach (int id in Prune) Candidates.Remove(id);
                float age = Mathf.Max(MinimumGrace, AgeSeconds != null ? AgeSeconds.Value : 120f);
                long limit = Math.Max(0, UnusedBudgetMiB == null ? 512 : UnusedBudgetMiB.Value) * 1048576L;
                int remaining = MaxPerSweep == null ? 16 : Mathf.Max(1, MaxPerSweep.Value);
                Eligible.Sort(delegate(Candidate a, Candidate b) { return a.since.CompareTo(b.since); });
                foreach (var entry in Eligible)
                {
                    if (remaining == 0) break;
                    if (unusedBytes <= limit && now - entry.since < age) continue;
                    Evict.Add(entry.id);
                    unusedBytes -= entry.bytes;
                    releasedBytes += entry.bytes;
                    remaining--;
                }
                // One cache walk removes every alias. No O(entries * evictions).
                foreach (var kv in cache)
                    if (kv.Value != null && Evict.Contains(kv.Value.GetInstanceID())) Keys.Add(kv.Key);
                foreach (string key in Keys)
                {
                    Texture2D t = cache[key];
                    cache.Remove(key);
                    tracked.Remove(t);
                    counts.Remove(t); // only zero/absent counts can reach Evict
                }
                foreach (int id in Evict) Candidates.Remove(id);
                released = Evict.Count;
            }
            finally { ClearScratch(); }
            if (released > 0)
            {
                WardrobeJanitor.Log("cache ownership released=" + released + " estimatedMiB=" +
                    (releasedBytes / 1048576) + " unusedEligibleMiB=" + (unusedBytes / 1048576));
                // No observer strong references survive into Unity's sweep.
                WardrobeJanitor.KickUnusedAssets();
            }
        }

        private static void ResolveFields()
        {
            if (_fieldsTried) return;
            _fieldsTried = true;
            Type t = typeof(ImageLoaderThreaded);
            _cacheF = t.GetField("textureCache", All);
            _countF = t.GetField("textureUseCount", All);
            _trackedF = t.GetField("textureTrackedCache", All);
        }
    }
}
