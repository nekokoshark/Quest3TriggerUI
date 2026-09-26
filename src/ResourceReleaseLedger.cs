using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class ResourceReleaseLedger
    {
        private static readonly FieldInfo Counts = typeof(ImageLoaderThreaded).GetField("textureUseCount", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo Cache = typeof(ImageLoaderThreaded).GetField("textureCache", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly List<int> Keys = new List<int>();
        private static int _offset;
        private static long _revision = -1;
        private static float _next;
        // This Unity has no GetTexturePropertyNames, so the slot list must be
        // explicit — extend it with every texture property name present in
        // VaM's shaders (dumped from Assembly-CSharp). The original 9 missed
        // eye/lash/skin-variant slots; NormaEyesNM etc. then landed as
        // unknown-external-owners despite being referenced by live materials.
        private static readonly string[] MaterialSlots = { "_MainTex", "_BumpMap", "_SpecTex", "_GlossTex", "_AlphaTex", "_DecalTex", "_DetailMap", "_DetailNormalMap", "_TessTex",
            "_BaseTex", "_NormalMap", "_RoughnessMap", "_SubdermisTex", "_TranslucencyTex", "_AlphaMask", "_AlphaMask2",
            "_OcclusionTexture", "_OcclusionTexture1", "_OcclusionTexture2", "_PrimarySpecular", "_SecondarySpecular",
            "_ScratchTex", "_GrabTexture", "_ReflectionTex0", "_ReflectionTex1", "_LeftReflectionTex", "_RightReflectionTex", "_RefractionTex" };
        private static readonly Dictionary<int, int> MaterialRefCounts = new Dictionary<int, int>();
        private static readonly HashSet<int> CachedTextureIds = new HashSet<int>();
        internal static void Tick(ResourceLedgerIndex index, bool settled)
        {
            // A parked proof needs verdicts now — a deferred UUA decision is
            // the only consumer, so run the audit while one is pending even
            // when the ledger is still churning. Verdicts produced under
            // unsettled conditions are provisional (ReleaseSettled=false).
            if ((!settled && !PresetSweepGate.ProofPending) || Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.25f;
            // Keep scan progress across churn: appends bump RetiredRevision
            // every tick during an unload, and resetting _offset to zero
            // starved every entry past the first batch — fresh
            // pending-reconciliation assets were never audited.
            if (_revision != index.RetiredRevision)
            { Keys.Clear(); Keys.AddRange(index.Retired.Keys); _offset = Math.Min(_offset, Keys.Count); _revision = index.RetiredRevision; }
            var loader = ImageLoaderThreaded.singleton;
            var counts = loader == null || Counts == null ? null : Counts.GetValue(loader) as Dictionary<Texture2D, int>;
            var cache = loader == null || Cache == null ? null : Cache.GetValue(loader) as Dictionary<string, Texture2D>;
            MaterialRefCounts.Clear();
            CachedTextureIds.Clear();
            foreach (var pair in index.Assets)
            {
                var material = pair.Value.Target.Target as Material;
                if (material == null) continue;
                foreach (string slot in MaterialSlots)
                {
                    if (!material.HasProperty(slot)) continue;
                    var texture = material.GetTexture(slot) as Texture2D;
                    if (texture == null) continue;
                    int refs;
                    MaterialRefCounts.TryGetValue(texture.GetInstanceID(), out refs);
                    MaterialRefCounts[texture.GetInstanceID()] = refs + 1;
                }
            }
            if (cache != null) foreach (var entry in cache)
                if (entry.Value != null) CachedTextureIds.Add(entry.Value.GetInstanceID());
            // Current observed consumer counts are not the same as owner counts.
            foreach (var asset in index.Assets.Values)
            {
                var texture = asset.Target.Target as Texture2D;
                int count = -1;
                if (texture != null && counts != null && !counts.TryGetValue(texture, out count)) count = -1;
                if (asset.NativeRefs != count) { asset.NativeRefs = count; index.Revision++; }
            }
            // 1700+ retired entries at 32/tick ≈ 12s — past the proof deadline.
            // While a proof is parked, drain much faster; reads are cheap.
            int batch = PresetSweepGate.ProofPending ? 256 : 32;
            for (int n = 0; n < Math.Min(batch, Keys.Count); n++)
            {
                if (_offset >= Keys.Count) _offset = 0;
                int id = Keys[_offset++]; ResourceLedgerIndex.Asset asset;
                index.Work++;
                if (!index.Retired.TryGetValue(id, out asset)) continue;
                // Terminal verdicts stay in Retired — removing a confirmed-dead
                // asset turned the BEST-covered release into a proof-fatal
                // "left ledger without native receipt".
                if (asset.ReleaseState == "native-destroyed" || asset.ReleaseState == "weak-lost" ||
                    asset.ReleaseState == "sweep-survivor") continue;
                // Terminal verdicts are monotone — a dead wrapper or destroyed
                // native object can never be un-dead, so they do not wait for
                // a settled pass and never hold a proof hostage.
                // RetiredTarget pins the wrapper from retirement onward, so
                // weak-lost can now only mean the wrapper was already dead
                // before the asset retired.
                object raw = asset.RetiredTarget;
                if (raw == null)
                { index.UncertainCollected++; asset.ReleaseState = "weak-lost"; asset.ReleaseSettled = true; index.RetiredRevision++; continue; }
                var target = raw as UnityEngine.Object;
                if (target == null)
                { index.NativeGone++; asset.ReleaseState = "native-destroyed"; asset.ReleaseSettled = true; asset.RetiredTarget = null; index.RetiredRevision++; continue; }
                var texture = target as Texture2D;
                int count = -1;
                if (texture != null && counts != null && !counts.TryGetValue(texture, out count)) count = -1;
                int materialRefs = -1;
                if (texture != null) MaterialRefCounts.TryGetValue(texture.GetInstanceID(), out materialRefs);
                bool cached = texture != null && CachedTextureIds.Contains(texture.GetInstanceID());
                string status = texture == null ? "unknown-nontexture-owners" : count > 0 ? "native-still-used" :
                    materialRefs > 0 ? "known-current-material-owner" :
                    cached ? "image-loader-cache-retained" : "unknown-external-owners";
                if (asset.NativeRefs != count || asset.ReleaseState != status || asset.ReleaseSettled != settled)
                { asset.NativeRefs = count; asset.ReleaseState = status; asset.ReleaseSettled = settled; index.RetiredRevision++; }
            }
            // No Destroy, UnloadAsset or UUA bypass. A zero native texture count
            // does not enumerate third-party/native engine references.
        }

        // A completed UUA mark-sweep is the strongest reference evidence there
        // is: an asset it could have collected but did not has a reference we
        // simply cannot see (eye textures on persistent skin tables, engine
        // internals). Retired entries that predate the sweep's watermark and
        // are still alive graduate to the terminal "sweep-survivor" verdict;
        // ones the sweep actually killed settle as "native-destroyed" and
        // release their pin. Entries retired after the watermark were never
        // in the mark set and keep whatever evidence they had.
        internal static void SweepAudit(ResourceLedgerIndex index, int watermark, out int survivors, out int destroyed)
        {
            survivors = 0; destroyed = 0;
            foreach (var pair in index.Retired)
            {
                ResourceLedgerIndex.Asset asset = pair.Value;
                index.Work++;
                if (asset.RetiredSeq > watermark) continue;
                if (asset.ReleaseState == "native-destroyed" || asset.ReleaseState == "weak-lost" ||
                    asset.ReleaseState == "sweep-survivor") continue;
                object raw = asset.RetiredTarget;
                var target = raw as UnityEngine.Object;
                if (raw == null)
                { index.UncertainCollected++; asset.ReleaseState = "weak-lost"; asset.ReleaseSettled = true; index.RetiredRevision++; continue; }
                if (target == null)
                { index.NativeGone++; asset.ReleaseState = "native-destroyed"; asset.ReleaseSettled = true; asset.RetiredTarget = null; destroyed++; index.RetiredRevision++; continue; }
                asset.ReleaseState = "sweep-survivor"; asset.ReleaseSettled = true;
                asset.RetiredTarget = null; survivors++; index.RetiredRevision++;
            }
        }

        internal static void Reset() { Keys.Clear(); _offset = 0; _revision = -1; _next = 0; MaterialRefCounts.Clear(); CachedTextureIds.Clear(); }
    }
}
