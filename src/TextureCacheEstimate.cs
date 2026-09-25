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
        private static readonly MethodInfo CachePath = typeof(ImageLoaderThreaded.QueuedImage)
            .GetMethod("GetDiskCachePath", BindingFlags.Instance | BindingFlags.NonPublic);
        // Accept exactly the native metadata shape. Unknown formats/layouts retain
        // the old reservation rather than making assumptions about partial JSON.
        private static readonly Regex Meta = new Regex(
            "\\A\\s*\\{\\s*\"type\"\\s*:\\s*\"image\"\\s*,\\s*" +
            "\"width\"\\s*:\\s*\"([0-9]{1,10})\"\\s*,\\s*" +
            "\"height\"\\s*:\\s*\"([0-9]{1,10})\"\\s*,\\s*" +
            "\"format\"\\s*:\\s*\"[A-Za-z0-9]+\"\\s*\\}\\s*\\z",
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
            try
            {
                string path = CachePath.Invoke(q, null) as string;
                if (string.IsNullOrEmpty(path)) return false;
                // No network probe on the Unity thread. Native cache locations
                // can be relative; GetFullPath resolves those without file I/O.
                path = Path.GetFullPath(path);
                if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                var data = new FileInfo(path);
                if (!data.Exists || data.Length <= 0) return false;
                long rawBytes = data.Length;
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
                int width, height;
                if (!TryDimensions(text, out width, out height)) return false;
                // Keep the full existing decode factor even for already-decoded
                // disk caches. Also cover raw bytes plus a possible upload copy.
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
        }
    }
}
