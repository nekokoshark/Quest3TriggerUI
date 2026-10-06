using System;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Local snapshots only; no source Texture or decoded array survives a blend.
    internal sealed class SkinBlendByteInputs
    {
        private const long MaximumInputBytes = 256L * 1024 * 1024;
        private static ulong formats;
        private static bool tried;
        private static float[][] components;
        private readonly Color32[] torso, gen, mask;
        internal static long Blends, InputBytes;

        private SkinBlendByteInputs(Color32[] t, Color32[] g, Color32[] m)
        { torso = t; gen = g; mask = m; }

        internal static SkinBlendByteInputs TryRead(Texture2D t, Texture2D g, Texture2D m, int width, int height)
        {
            if (components == null) return null;
            bool readT = Supported(t.format), readG = Supported(g.format), readM = Supported(m.format);
            if (!readT && !readG && !readM) return null;
            long pixels = (long)width * height;
            int unique = (readT ? 1 : 0) + (readG && !ReferenceEquals(g, t) ? 1 : 0)
                + (readM && !ReferenceEquals(m, t) && !ReferenceEquals(m, g) ? 1 : 0);
            if (pixels <= 0 || pixels * 4 * unique > MaximumInputBytes) return null;
            // Keep original input order and propagate native read errors into the
            // caller's existing UnityException handling, never publish a partial blend.
            Color32[] a = readT ? t.GetPixels32(0) : null;
            Color32[] b = !readG ? null : ReferenceEquals(g, t) ? a : g.GetPixels32(0);
            Color32[] c = !readM ? null : ReferenceEquals(m, t) ? a : ReferenceEquals(m, g) ? b : m.GetPixels32(0);
            if ((a != null && a.Length != pixels) || (b != null && b.Length != pixels) || (c != null && c.Length != pixels))
                throw new UnityException("Skin blend byte input length changed");
            Blends++; InputBytes += pixels * 4 * unique;
            return new SkinBlendByteInputs(a, b, c);
        }

        private static bool Supported(TextureFormat format)
        { int n = (int)format; return n >= 0 && n < 64 && (formats & (1UL << n)) != 0; }

        private static Color Expand(Color32 pixel)
        { return new Color(components[0][pixel.r], components[1][pixel.g], components[2][pixel.b], components[3][pixel.a]); }

        internal Color Torso(int index) { return Expand(torso[index]); }
        internal Color Gen(int index) { return Expand(gen[index]); }
        internal float Weight(int index) { return components[0][mask[index].r]; }
        internal bool HasTorso { get { return torso != null; } }
        internal bool HasGen { get { return gen != null; } }
        internal bool HasMask { get { return mask != null; } }
        internal int Sources { get { return (HasTorso ? 1 : 0) + (HasGen ? 1 : 0) + (HasMask ? 1 : 0); } }

        private static unsafe bool Equal(Color a, Color b)
        {
            uint* x = (uint*)&a, y = (uint*)&b;
            return x[0] == y[0] && x[1] == y[1] && x[2] == y[2] && x[3] == y[3];
        }

        private static byte[] Pattern(int width, int height, int channels, bool mips)
        {
            int length = 0, w = width, h = height;
            do { length += w * h * channels; if (!mips || (w == 1 && h == 1)) break; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); } while (true);
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 73 + i / 7 + 19);
            return bytes;
        }

        private static void Calibrate()
        {
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(256, 1, TextureFormat.RGBA32, false, true);
                var data = new byte[1024];
                for (int i = 0; i < 256; i++)
                { data[4 * i] = (byte)i; data[4 * i + 1] = (byte)(255 - i); data[4 * i + 2] = (byte)(i ^ 85); data[4 * i + 3] = (byte)(i ^ 170); }
                texture.LoadRawTextureData(data); texture.Apply(false, false);
                var pixels = texture.GetPixels(0);
                if (pixels.Length != 256) throw new InvalidOperationException("Skin calibration length changed");
                var table = new[] { new float[256], new float[256], new float[256], new float[256] };
                for (int i = 0; i < 256; i++)
                { table[0][i] = pixels[i].r; table[1][255 - i] = pixels[i].g; table[2][i ^ 85] = pixels[i].b; table[3][i ^ 170] = pixels[i].a; }
                components = table;
            }
            finally { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
        }

        private static byte[] Bc7Pattern(int width, int height, bool mips)
        {
            int bytes = 0, w = width, h = height;
            do { bytes += ((w + 3) / 4) * ((h + 3) / 4) * 16; if (!mips || (w == 1 && h == 1)) break; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); } while (true);
            var data = new byte[bytes];
            for (int i = 0; i < bytes; i++) data[i] = (byte)(i * 41 + i / 11 + 7);
            // Every block selects valid one-subset mode 6; endpoint/index bits vary.
            for (int i = 0; i < bytes; i += 16) data[i] = (byte)((data[i] & 128) | 64);
            return data;
        }

        private static void Validate(TextureFormat format)
        {
            foreach (bool linear in new[] { false, true })
                foreach (bool mips in new[] { false, true })
                {
                    Texture2D texture = null;
                    try
                    {
                        bool dxt = format == TextureFormat.DXT1 || format == TextureFormat.DXT5;
                        var source = dxt ? (format == TextureFormat.DXT1 ? TextureFormat.RGB24 : TextureFormat.RGBA32) : format;
                        texture = new Texture2D(32, 16, source, mips, linear);
                        texture.LoadRawTextureData(format == TextureFormat.BC7 ? Bc7Pattern(32, 16, mips) : Pattern(32, 16, source == TextureFormat.RGB24 ? 3 : 4, mips));
                        texture.Apply(false, false);
                        if (dxt) texture.Compress(true);
                        if (texture.format != format) throw new InvalidOperationException("Skin test format changed");
                        var reference = texture.GetPixels(0); var packed = texture.GetPixels32(0);
                        if (reference.Length != packed.Length || packed.Length != 512) throw new InvalidOperationException("Skin test length changed");
                        for (int i = 0; i < reference.Length; i++)
                            if (!Equal(reference[i], Expand(packed[i]))) throw new InvalidOperationException("Skin byte float-bit mismatch at " + i);
                    }
                    finally { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
                }
        }

        internal static void Initialize()
        {
            if (tried) return;
            tried = true;
            try { Calibrate(); }
            catch (Exception error) { components = null; Log("calibration skipped: " + error.Message); return; }
            foreach (var format in new[] { TextureFormat.RGB24, TextureFormat.RGBA32, TextureFormat.ARGB32, TextureFormat.BGRA32, TextureFormat.DXT1, TextureFormat.DXT5, TextureFormat.BC7 })
            {
                try { Validate(format); formats |= 1UL << (int)format; Log("float-bit selftest PASS format=" + format + " linear/sRGB mip/no-mip"); }
                catch (Exception error) { Log("kept float tiles format=" + format + " reason=" + error.Message); }
            }
            Log("ready inputLimitMiB=256 formats=" + formats + " sourceReadability=preserved outputFloatMath=original");
        }

        internal static void Shutdown()
        { formats = 0; components = null; tried = false; Blends = 0; InputBytes = 0; }

        private static void Log(string text)
        { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[skin-byte-inputs] " + text); }
    }
}
