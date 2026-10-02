using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Quest3TriggerUI
{
    // BC7 is a colour optimization, not a lossless replacement for normal maps,
    // scalar masks or transparency. Both conversion entry points use this policy.
    internal static class Bc7CacheCompatibility
    {
        internal static bool PowerOfTwo(int value)
        {
            return value > 0 && (value & (value - 1)) == 0;
        }

        private static int FullMips(int width, int height)
        {
            int levels = 1;
            while (width > 1 || height > 1)
            {
                width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); levels++;
            }
            return levels;
        }

        // GPU readback of the same image: native NPOT and POT BC7 are correct,
        // NPOT BC7 distant mips are not. Plan both entry points before encoding.
        internal static bool Plan(int width, int height, int mips, long rawBytes,
            out int targetWidth, out int targetHeight, out int targetMips, out long encodedBytes)
        {
            targetWidth = targetHeight = targetMips = 0; encodedBytes = 0;
            if (width < 1 || height < 1 || width > 16384 || height > 16384 ||
                (mips != 1 && mips != FullMips(width, height))) return false;
            targetWidth = targetHeight = 1;
            while (targetWidth < width) targetWidth *= 2;
            while (targetHeight < height) targetHeight *= 2;
            targetMips = mips == 1 ? 1 : FullMips(targetWidth, targetHeight);
            int w = targetWidth, h = targetHeight;
            for (int i = 0; i < targetMips; i++)
            {
                encodedBytes += (long)((w + 3) / 4) * ((h + 3) / 4) * 16;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            return encodedBytes < rawBytes;
        }

        internal static string Metadata(string text, int width, int height)
        {
            if (!PowerOfTwo(width) || !PowerOfTwo(height)) throw new InvalidDataException("BC7 requires POT dimensions");
            foreach (string field in new[] { "width", "height" })
            {
                var pattern = new Regex("\"" + field + "\"\\s*:\\s*(?:\"[0-9]+\"|[0-9]+)");
                if (pattern.Matches(text).Count != 1) throw new InvalidDataException("Missing or duplicate " + field);
                text = pattern.Replace(text, "\"" + field + "\" : \"" + (field == "width" ? width : height) + "\"");
            }
            var format = new Regex("\"format\"\\s*:\\s*\"(?:RGBA32|RGB24)\"");
            if (format.Matches(text).Count != 1) throw new InvalidDataException("Missing or duplicate native format");
            return format.Replace(text, "\"format\" : \"BC7\"");
        }

        internal static bool ColourCache(string path)
        {
            return path != null && path.EndsWith("_C.vamcache", StringComparison.OrdinalIgnoreCase);
        }

        private static long Pixels(int width, int height, int mips)
        {
            if (width < 1 || height < 1 || mips < 1 || mips > 32) return -1;
            long sum = 0;
            for (int level = 0; level < mips; level++)
            {
                sum = checked(sum + (long)width * height);
                if (level + 1 < mips && width == 1 && height == 1) return -1;
                width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
            }
            return sum;
        }

        private static bool Opaque(Stream source, Func<bool> stop)
        {
            byte[] buffer = new byte[65536];
            while (source.Position < source.Length)
            {
                if (stop != null && stop()) throw new OperationCanceledException();
                int count = source.Read(buffer, 0, buffer.Length);
                if (count == 0) throw new EndOfStreamException();
                if ((count & 3) != 0) return false;
                for (int i = 3; i < count; i += 4) if (buffer[i] != 255) return false;
            }
            return true;
        }

        internal static bool Source(string path, int width, int height, int mips, int bpp, Func<bool> stop)
        {
            if (!ColourCache(path) || (bpp != 3 && bpp != 4)) return false;
            long pixels = Pixels(width, height, mips);
            if (pixels <= 0) return false;
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length != checked(pixels * bpp)) return false;
                // RGB24 has implicit alpha=1, including every distant mip.
                return bpp == 3 || Opaque(stream, stop);
            }
        }

        internal static bool Decoded(string path, int width, int height, int mips, Func<bool> stop)
        {
            if (!PowerOfTwo(width) || !PowerOfTwo(height)) return false;
            long pixels = Pixels(width, height, mips);
            if (pixels <= 0) return false;
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                // texconv -dx10 -f R8G8B8A8_UNORM: reject a different layout.
                if (stream.Length != checked(148 + pixels * 4)) return false;
                byte[] h = reader.ReadBytes(148);
                if (h.Length != 148 || BitConverter.ToUInt32(h, 0) != 0x20534444u ||
                    BitConverter.ToUInt32(h, 4) != 124u || BitConverter.ToUInt32(h, 12) != (uint)height ||
                    BitConverter.ToUInt32(h, 16) != (uint)width || BitConverter.ToUInt32(h, 28) != (uint)mips ||
                    BitConverter.ToUInt32(h, 84) != 0x30315844u || BitConverter.ToUInt32(h, 128) != 28u ||
                    BitConverter.ToUInt32(h, 132) != 3u || BitConverter.ToUInt32(h, 136) != 0u ||
                    BitConverter.ToUInt32(h, 140) != 1u) return false;
                return Opaque(stream, stop);
            }
        }
    }
}
