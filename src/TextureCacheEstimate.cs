using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Quest3TriggerUI
{
    // Read only the tiny native metadata file, never the image/VAR contents.
    // Native GetDiskCachePath keys by source size, timestamp and conversion flags.
    internal static class TextureCacheEstimate
    {
        // Optional bounded observer. Null outside an explicitly requested run.
        internal static Action<int, long, int, string> Probe;
        internal delegate void MetadataObserver(ImageLoaderThreaded.QueuedImage q, string path, string text,
            long metaStamp, long dataStamp, long bytes);
        internal static MetadataObserver MetadataObserved;
        private static readonly MethodInfo CachePath = typeof(ImageLoaderThreaded.QueuedImage)
            .GetMethod("GetDiskCachePath", BindingFlags.Instance | BindingFlags.NonPublic);
        // Accept exactly the native metadata shape. Unknown formats/layouts retain
        // the old reservation rather than making assumptions about partial JSON.
        private static readonly Regex Meta = new Regex(
            "\\A\\s*\\{\\s*\"type\"\\s*:\\s*\"image\"\\s*,\\s*" +
            "\"width\"\\s*:\\s*\"([0-9]{1,10})\"\\s*,\\s*" +
            "\"height\"\\s*:\\s*\"([0-9]{1,10})\"\\s*,\\s*" +
            "\"format\"\\s*:\\s*\"([A-Za-z0-9]+)\"\\s*\\}\\s*\\z",
            RegexOptions.CultureInvariant);

        internal static bool TryDimensions(string text, out int width, out int height)
        {
            width = height = 0;
            if (text == null || text.Length > 4096) return false;
            Match m = Meta.Match(text);
            return m.Success && int.TryParse(m.Groups[1].Value, out width) &&
                int.TryParse(m.Groups[2].Value, out height) && width > 0 && height > 0;
        }

        internal static bool TryEstimate(ImageLoaderThreaded.QueuedImage q, out long bytes)
        {
            bytes = 0;
            if (CachePath == null || q == null || q.setSize || q.forceReload ||
                q.useWebCache || string.IsNullOrEmpty(q.imgPath) ||
                q.imgPath.IndexOf("://", StringComparison.Ordinal) >= 0 ||
                q.imgPath.IndexOf(".latest:", StringComparison.Ordinal) >= 0 ||
                !MVR.FileManagement.CacheManager.CachingEnabled) return false;
            var observer = Probe;
            long stamp = observer == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            int generation = observer == null ? 0 : GC.CollectionCount(0), phase = 0;
            string observedPath = q.imgPath;
            try
            {
                string path = CachePath.Invoke(q, null) as string;
                ProbeStage(observer, ref stamp, ref generation, phase++, observedPath);
                if (string.IsNullOrEmpty(path)) return false;
                // No network probe on the Unity thread. Native cache locations
                // can be relative; GetFullPath resolves those without file I/O.
                path = Path.GetFullPath(path);
                if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                var data = new FileInfo(path);
                if (!data.Exists || data.Length <= 0) return false;
                long rawBytes = data.Length;
                long dataStamp = data.LastWriteTimeUtc.Ticks;
                long metaStamp = File.GetLastWriteTimeUtc(path + "meta").Ticks;
                ProbeStage(observer, ref stamp, ref generation, phase++, observedPath);
                string text;
                using (var stream = new FileStream(path + "meta", FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length <= 0 || stream.Length > 4096) return false;
                    // Bound the read even if another process grows the file.
                    using (var reader = new StreamReader(stream))
                    {
                        var chars = new char[4097];
                        int count = reader.ReadBlock(chars, 0, chars.Length);
                        if (count > 4096) return false;
                        text = new string(chars, 0, count);
                    }
                }
                ProbeStage(observer, ref stamp, ref generation, phase++, observedPath);
                int width, height;
                if (!TryDimensions(text, out width, out height)) return false;
                var remember = MetadataObserved;
                if (remember != null) remember(q, path, text, metaStamp, dataStamp, rawBytes);
                // Only complete, power-of-two DXT caches bypass GDI and compression.
                // Unknown/base-only/malformed layouts retain the original estimate.
                if (!q.createNormalFromBump && TryDxtBytes(width, height,
                    Meta.Match(text).Groups[3].Value, rawBytes, out bytes)) return true;
                long rawCredit = rawBytes > long.MaxValue / 2 ? long.MaxValue : rawBytes * 2;
                bytes = Math.Max(rawCredit,
                    TextureDecodeBudget.EstimateBytes(width, height, true, q.createNormalFromBump));
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (TargetInvocationException) { return false; }
            finally { ProbeStage(observer, ref stamp, ref generation, phase, observedPath); }
        }

        internal static bool TryDxtBytes(int width, int height, string format, long rawBytes, out long bytes)
        {
            bytes = 0;
            if (width < 4 || height < 4 || width > 16384 || height > 16384 ||
                (width & (width - 1)) != 0 || (height & (height - 1)) != 0 ||
                (format != "DXT1" && format != "DXT5")) return false;
            int block = format == "DXT1" ? 8 : 16;
            long full = 0;
            for (int w = width, h = height; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
            {
                full += (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;
                if (w == 1 && h == 1) break;
            }
            if (rawBytes != full) return false;
            // Raw managed array + Unity CPU storage + upload staging + spare copy.
            // Retain 64KiB overhead / 1MiB floor; global RAM/commit caps are unchanged.
            bytes = Math.Max(1024L * 1024, full * 4 + 65536);
            return true;
        }

        private static void ProbeStage(Action<int, long, int, string> observer, ref long stamp, ref int generation, int phase, string path)
        {
            if (observer == null) return;
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            int gc = GC.CollectionCount(0);
            try { observer(phase, end - stamp, gc - generation, path); }
            catch { /* A diagnostic observer must not change native image loading. */ }
            stamp = System.Diagnostics.Stopwatch.GetTimestamp(); generation = GC.CollectionCount(0);
        }
    }
}
