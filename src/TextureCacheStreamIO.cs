using System;
using System.IO;

namespace Quest3TriggerUI
{
    // DDS/cache staging only. Never allocate a complete texture payload.
    internal static class TextureCacheStreamIO
    {
        internal const int HeaderBytes = 148;
        private const int ChunkBytes = 65536;

        private static void CheckStop(Func<bool> stop)
        {
            if (stop != null && stop()) throw new OperationCanceledException();
        }

        private static void ReadExact(Stream source, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = source.Read(buffer, read, count - read);
                if (n == 0) throw new EndOfStreamException("texture cache truncated");
                read += n;
            }
        }

        internal static void CopyExact(Stream source, Stream target, long bytes, Func<bool> stop)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException("bytes");
            byte[] buffer = new byte[ChunkBytes];
            while (bytes > 0)
            {
                CheckStop(stop);
                int count = (int)Math.Min(bytes, buffer.Length);
                ReadExact(source, buffer, count);
                target.Write(buffer, 0, count);
                bytes -= count;
            }
            CheckStop(stop);
        }

        private static long Pixels(int width, int height, int mips)
        {
            if (width <= 0 || height <= 0 || mips <= 0 || mips > 32)
                throw new ArgumentOutOfRangeException("texture geometry");
            long pixels = 0;
            for (int i = 0; i < mips; i++)
            {
                pixels = checked(pixels + (long)width * height);
                if (i + 1 < mips && width == 1 && height == 1)
                    throw new ArgumentOutOfRangeException("mips");
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            return pixels;
        }

        private static void WriteHeader(Stream target, int width, int height, int mips)
        {
            var header = new byte[HeaderBytes];
            using (var stream = new MemoryStream(header))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x20534444u); writer.Write(124u); writer.Write(0xA1007u);
                writer.Write((uint)height); writer.Write((uint)width);
                writer.Write(checked((uint)width * 4u)); writer.Write(0u); writer.Write((uint)mips);
                for (int i = 0; i < 11; i++) writer.Write(0u);
                writer.Write(32u); writer.Write(0x4u); writer.Write(0x30315844u);
                for (int i = 0; i < 5; i++) writer.Write(0u);
                writer.Write(0x1000u | 0x8u | 0x400000u);
                for (int i = 0; i < 4; i++) writer.Write(0u);
                writer.Write(28u); writer.Write(3u); writer.Write(0u); writer.Write(1u); writer.Write(0u);
            }
            target.Write(header, 0, header.Length);
        }

        internal static void WriteInput(string sourcePath, string ddsPath, int width,
            int height, int mips, int bpp, Func<bool> stop)
        {
            if (bpp != 3 && bpp != 4) throw new ArgumentOutOfRangeException("bpp");
            long pixels = Pixels(width, height, mips);
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length != checked(pixels * bpp)) throw new InvalidDataException("raw cache size changed");
                using (var target = new FileStream(ddsPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    WriteHeader(target, width, height, mips);
                    if (bpp == 4) CopyExact(source, target, source.Length, stop);
                    else
                    {
                        const int chunkPixels = ChunkBytes / 4;
                        byte[] rgb = new byte[chunkPixels * 3];
                        byte[] rgba = new byte[chunkPixels * 4];
                        while (pixels > 0)
                        {
                            CheckStop(stop);
                            int count = (int)Math.Min(pixels, chunkPixels);
                            ReadExact(source, rgb, count * 3);
                            for (int i = 0; i < count; i++)
                            {
                                rgba[i * 4] = rgb[i * 3];
                                rgba[i * 4 + 1] = rgb[i * 3 + 1];
                                rgba[i * 4 + 2] = rgb[i * 3 + 2];
                                rgba[i * 4 + 3] = 255;
                            }
                            target.Write(rgba, 0, count * 4);
                            pixels -= count;
                        }
                        CheckStop(stop);
                    }
                }
            }
        }

        internal static void ExtractBc7(string ddsPath, string targetPath, int width,
            int height, int mips, long expectedBytes, Func<bool> stop)
        {
            using (var source = new FileStream(ddsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (expectedBytes <= 0 || source.Length != checked(HeaderBytes + expectedBytes))
                    throw new InvalidDataException("encoded cache size mismatch");
                var h = new byte[HeaderBytes];
                ReadExact(source, h, h.Length);
                if (BitConverter.ToUInt32(h, 0) != 0x20534444u || BitConverter.ToUInt32(h, 4) != 124u ||
                    BitConverter.ToUInt32(h, 12) != (uint)height || BitConverter.ToUInt32(h, 16) != (uint)width ||
                    BitConverter.ToUInt32(h, 28) != (uint)mips || BitConverter.ToUInt32(h, 76) != 32u ||
                    BitConverter.ToUInt32(h, 84) != 0x30315844u || BitConverter.ToUInt32(h, 128) != 98u ||
                    BitConverter.ToUInt32(h, 132) != 3u || BitConverter.ToUInt32(h, 136) != 0u ||
                    BitConverter.ToUInt32(h, 140) != 1u)
                    throw new InvalidDataException("unexpected BC7 DDS header");
                using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    CopyExact(source, target, expectedBytes, stop);
            }
        }
    }
}
