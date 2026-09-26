using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Recheck only an already completed native cache entry. No in-flight request
    // merging, extra texture cache, callback replacement or reference-count edit.
    internal static class TextureUploadReuse
    {
        internal static ConfigEntry<bool> Enabled;
        private static Harmony _harmony;
        private static FieldInfo _cache;
        private static readonly FieldInfo WebRequest = typeof(ImageLoaderThreaded.QueuedImage).GetField("webRequest", All);
        private static int _hits;
        private static long _rawBytes;
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                _cache = typeof(ImageLoaderThreaded).GetField("textureCache", All);
                if (_cache == null || _cache.FieldType != typeof(Dictionary<string, Texture2D>) || WebRequest == null)
                    throw new MissingFieldException("native texture cache");
                _harmony = new Harmony("Quest3TriggerUI.texture-upload-reuse");
                _harmony.UnpatchAll(_harmony.Id);
                var hook = new HarmonyMethod(typeof(TextureUploadReuse).GetMethod("BeforeFinish", BindingFlags.Static | BindingFlags.NonPublic));
                hook.priority = Priority.Low; // Expired-receiver guard runs first.
                _harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Finish", All), prefix: hook);
                Log("installed; completed-cache recheck before upload; native completion preserved");
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e.Message); }
        }

        internal static long RequiredBytes(int width, int height, int format, bool mipmaps)
        {
            if (width <= 0 || height <= 0) return 0;
            int bpp = format == 1 ? 1 : format == 3 ? 3 : (format == 4 || format == 5) ? 4 : 0;
            if (bpp == 0 && format != 10 && format != 12) return 0;
            long total = 0;
            try
            {
                do
                {
                    long level = bpp != 0 ? checked((long)width * height * bpp) :
                        checked(((long)width + 3) / 4 * (((long)height + 3) / 4) * (format == 10 ? 8 : 16));
                    total = checked(total + level);
                    if (!mipmaps || (width == 1 && height == 1)) break;
                    width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
                } while (true);
            }
            catch (OverflowException) { return 0; }
            return total;
        }

        private static bool PowerOfTwo(int n) { return n > 0 && (n & (n - 1)) == 0; }

        internal static bool Compatible(ImageLoaderThreaded.QueuedImage q, Texture2D cached)
        {
            if (cached == null || cached.width != q.width || cached.height != q.height) return false;
            int mipCount = 1, w = q.width, h = q.height;
            while (q.createMipMaps && (w > 1 || h > 1)) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); mipCount++; }
            if (cached.mipmapCount != mipCount) return false;
            int format = (int)q.textureFormat;
            long required = RequiredBytes(q.width, q.height, format, q.createMipMaps);
            if (required == 0 || q.raw == null || q.raw.LongLength < required) return false;
            bool compress = q.compress && (q.preprocessed || !q.createMipMaps || (PowerOfTwo(q.width) && PowerOfTwo(q.height)));
            if (compress && format != 10 && format != 12)
            {
                if (format == 3) format = 10;
                else if (format == 4 || format == 5) format = 12;
                else return false;
            }
            return (int)cached.format == format;
        }

        private static void BeforeFinish(ImageLoaderThreaded.QueuedImage __instance)
        {
            var q = __instance;
            if ((Enabled != null && !Enabled.Value) || q == null || !q.processed || q.finished || q.hadError ||
                q.cancel || q.forceReload || q.skipCache || q.isThumbnail || q.isPreload || q.tex != null ||
                q.rawImageToLoad != null || q.useWebCache || string.IsNullOrEmpty(q.imgPath) || WebRequest.GetValue(q) != null) return;
            var loader = ImageLoaderThreaded.singleton;
            if (loader == null) return;
            var cache = _cache.GetValue(loader) as Dictionary<string, Texture2D>;
            Texture2D texture;
            if (cache == null || !cache.TryGetValue(q.cacheSignature, out texture) || !Compatible(q, texture)) return;
            long bytes = q.raw.LongLength;
            // Native PostProcessCompletedImages already chooses this very cache
            // entry after Finish. Avoid creating/uploading its throwaway duplicate.
            q.tex = texture;
            q.raw = null;
            q.finished = true;
            _rawBytes += bytes;
            _hits++;
            if (_hits == 1 || _hits % 16 == 0)
                Log("hits=" + _hits + " decodedPayloadMiB=" + (_rawBytes / 1048576) +
                    " (upload avoided; decoding already happened; not measured VRAM)");
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null; _cache = null; _hits = 0; _rawBytes = 0;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[texture-reuse] " + message);
        }
    }
}
