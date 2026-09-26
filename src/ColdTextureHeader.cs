using System;
using System.IO;
using MVR.FileManagement;

namespace Quest3TriggerUI
{
    // Read bounded metadata only, never decode or retain an image. Unsupported
    // paths and malformed headers keep the original conservative reservation.
    internal static class ColdTextureHeader
    {
        internal static long Estimate(ImageLoaderThreaded.QueuedImage q)
        {
            if (q == null || q.setSize || q.useWebCache || q.isThumbnail ||
                string.IsNullOrEmpty(q.imgPath) || q.imgPath.IndexOf("://", StringComparison.Ordinal) >= 0 ||
                q.imgPath.StartsWith(@"\\", StringComparison.Ordinal) || q.imgPath.StartsWith("//", StringComparison.Ordinal) ||
                q.imgPath.IndexOf(".latest:", StringComparison.Ordinal) >= 0) return 0;
            string ext = Path.GetExtension(q.imgPath);
            if (!string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase)) return 0;
            try
            {
                using (var file = FileManager.OpenStream(q.imgPath, false))
                {
                    int w, h;
                    if (!TryDimensions(file.Stream, out w, out h)) return 0;
                    // Above existing 16/26 factors: covers source/destination GDI,
                    // raw copy and upload/write overlap; doesn't mutate q dimensions.
                    return Math.Max(1048576L, checked((long)Math.Max(4, w) * Math.Max(4, h) * 32));
                }
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
            catch (NotSupportedException) { return 0; }
            catch (ArgumentException) { return 0; }
            catch (OverflowException) { return 0; }
        }

        internal static bool TryDimensions(Stream stream, out int width, out int height)
        {
            width = height = 0;
            var r = new HeaderReader(stream);
            int first = r.Byte(), second = r.Byte();
            if (first == 137 && second == 80)
            {
                int[] signature = {78,71,13,10,26,10,0,0,0,13,73,72,68,82};
                foreach (int b in signature) if (r.Byte() != b) return false;
                long w = r.UInt32(), h = r.UInt32();
                int depth = r.Byte(), color = r.Byte();
                if (!(depth == 1 || depth == 2 || depth == 4 || depth == 8 || depth == 16) ||
                    !(color == 0 || color == 2 || color == 3 || color == 4 || color == 6) ||
                    r.Byte() != 0 || r.Byte() != 0) return false;
                int interlace = r.Byte();
                if (interlace != 0 && interlace != 1) return false;
                if (w <= 0 || h <= 0 || w > int.MaxValue || h > int.MaxValue) return false;
                width = (int)w; height = (int)h; return true;
            }
            if (first != 255 || second != 216) return false;
            while (r.Remaining > 0)
            {
                if (r.Byte() != 255) return false;
                int marker;
                do { marker = r.Byte(); } while (marker == 255);
                if (marker < 0 || marker == 0 || marker == 217 || marker == 218) return false;
                if (marker == 1 || (marker >= 208 && marker <= 215)) continue;
                int length = r.UInt16();
                if (length < 2 || length - 2 > r.Remaining) return false;
                if (marker == 192 || marker == 193 || marker == 194)
                {
                    if (length < 8 || r.Byte() != 8) return false;
                    int h = r.UInt16(), w = r.UInt16(), components = r.Byte();
                    if (w <= 0 || h <= 0 || components < 1 || components > 4 || length != 8 + 3 * components) return false;
                    if (!r.Skip(3 * components)) return false;
                    width = w; height = h; return true;
                }
                if (!r.Skip(length - 2)) return false;
            }
            return false;
        }

        private sealed class HeaderReader
        {
            private readonly Stream _stream;
            private byte[] _skipBuffer;
            internal int Remaining = 65536;
            internal HeaderReader(Stream stream) { _stream = stream; }
            internal int Byte() { return Remaining-- > 0 ? _stream.ReadByte() : -1; }
            internal int UInt16() { int a = Byte(), b = Byte(); return a < 0 || b < 0 ? -1 : (a << 8) | b; }
            internal long UInt32() { int a = UInt16(), b = UInt16(); return a < 0 || b < 0 ? -1 : ((long)a << 16) | (uint)b; }
            internal bool Skip(int n)
            {
                if (n < 0 || n > Remaining) return false;
                if (_skipBuffer == null) _skipBuffer = new byte[512];
                while (n > 0)
                {
                    int read = _stream.Read(_skipBuffer, 0, Math.Min(n, _skipBuffer.Length));
                    if (read <= 0) return false;
                    n -= read; Remaining -= read;
                }
                return true;
            }
        }
    }
}
