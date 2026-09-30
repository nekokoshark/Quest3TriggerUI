using System;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace Quest3TriggerUI
{
    // No retained requests or additional pool. Cold output uses the exact
    // upload layout; on unsupported geometry/layout the native length remains.
    internal static class ColdTextureBufferLayout
    {
        internal static ConfigEntry<bool> Enabled;
        private static bool _uploadValidated;
        private static long _savedBytes, _compactArrays, _inPlaceBumps;
        internal static bool Active { get { return _uploadValidated && (Enabled == null || Enabled.Value); } }
        internal static long SavedBytes { get { return Interlocked.Read(ref _savedBytes); } }
        internal static long CompactArrays { get { return Interlocked.Read(ref _compactArrays); } }
        internal static long InPlaceBumps { get { return Interlocked.Read(ref _inPlaceBumps); } }

        internal static int DecodeLength(int nativeLength, int width, int height, int format, bool mipmaps)
        {
            if (format != 3 && format != 4) return nativeLength;
            long required = TextureUploadReuse.RequiredBytes(width, height, format, mipmaps);
            // Never enlarge an allocation or alter an unknown/native error path.
            if (required <= 0 || required > nativeLength || required > int.MaxValue) return nativeLength;
            return (int)required;
        }

        internal static byte[] RentDecoded(int nativeLength, ImageLoaderThreaded.QueuedImage image)
        {
            int length = Active && image != null ? DecodeLength(nativeLength, image.width,
                image.height, (int)image.textureFormat, image.createMipMaps) : nativeLength;
            byte[] buffer = DecodedBufferPool.RentByteArray(length);
            if (length < nativeLength)
            {
                Interlocked.Add(ref _savedBytes, (long)nativeLength - length);
                Interlocked.Increment(ref _compactArrays);
            }
            return buffer;
        }

        internal static bool CanConvertInPlace(byte[] raw, int width, int height, bool mipmaps)
        {
            long required = TextureUploadReuse.RequiredBytes(width, height, 4, mipmaps);
            return Active && raw != null && required > 0 && raw.LongLength >= required;
        }

        internal static void RecordInPlace(long nativeOutputBytes)
        {
            Interlocked.Add(ref _savedBytes, nativeOutputBytes);
            Interlocked.Increment(ref _inPlaceBumps);
        }

        internal static void ValidateUpload()
        {
            _uploadValidated = false;
            if (Enabled != null && !Enabled.Value) return;
            try
            {
                // Validate against THIS Unity runtime, not a newer API assumption.
                int[] widths = { 8, 7, 1, 1 }, heights = { 8, 5, 8, 1 };
                foreach (int format in new[] { 3, 4 })
                    for (int k = 0; k < widths.Length; k++)
                        for (int mip = 0; mip < 2; mip++)
                        {
                            Texture2D texture = null;
                            try
                            {
                                texture = new Texture2D(widths[k], heights[k], (TextureFormat)format, mip != 0, true);
                                byte[] bytes = new byte[(int)TextureUploadReuse.RequiredBytes(widths[k], heights[k], format, mip != 0)];
                                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 37 + 11);
                                texture.LoadRawTextureData(bytes);
                                texture.Apply(false, false);
                                byte[] readback = texture.GetRawTextureData();
                                if (readback.Length != bytes.Length) throw new InvalidOperationException("upload length mismatch");
                                for (int i = 0; i < bytes.Length; i++)
                                    if (readback[i] != bytes[i]) throw new InvalidOperationException("upload byte mismatch");
                            }
                            finally { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
                        }
                _uploadValidated = true;
                Log("upload selftest PASS: RGB24/RGBA32, POT/NPOT/thin/1px, mip/no-mip; compact cold buffers and in-place bump enabled");
            }
            catch (Exception e) { Log("upload selftest failed; native lengths/out-of-place bump retained: " + e.Message); }
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[cold-buffer] " + message);
        }
    }
}
