using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Only the raster and temporary output storage change. Original Process,
    // Finish, callbacks, caching, mip generation and readability remain owners.
    internal static class ColdRasterDecode
    {
        internal static ConfigEntry<bool> Enabled;
        internal static long NativeDecodes, NativeBytes;
        private static Harmony _harmony;
        private static volatile bool _validated;
        private static bool _tried;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static bool Before(ImageLoaderThreaded.QueuedImage __instance, Stream st)
        {
            var q = __instance;
            if (!_validated || !NativeCacheBuffer.RasterReady || (Enabled != null && !Enabled.Value) ||
                q == null || q.raw != null || NativeCacheBuffer.StagedLength(q) != 0 ||
                (q.createNormalFromBump && BumpNormalRowConverter.Enabled != null && !BumpNormalRowConverter.Enabled.Value)) return true;
            using (var pixels = Decode(q, st)) pixels.Publish(q);
            return false;
        }

        internal static NativeCacheBuffer.RasterLease Decode(ImageLoaderThreaded.QueuedImage q, Stream stream)
        {
            NativeCacheBuffer.RasterLease output = null;
            try
            {
                using (var bitmap = new Bitmap(stream))
                {
                    bitmap.RotateFlip(RotateFlipType.Rotate180FlipX);
                    if (!q.setSize)
                    {
                        q.width = bitmap.Width; q.height = bitmap.Height;
                        if (q.compress) { q.width = Math.Max(1, q.width / 4) * 4; q.height = Math.Max(1, q.height / 4) * 4; }
                    }
                    int channels = 3;
                    q.textureFormat = TextureFormat.RGB24;
                    PixelFormat format = PixelFormat.Format24bppRgb;
                    if (q.createAlphaFromGrayscale || q.isNormalMap || q.createNormalFromBump || bitmap.PixelFormat == PixelFormat.Format32bppArgb)
                    { channels = 4; q.textureFormat = TextureFormat.RGBA32; format = PixelFormat.Format32bppArgb; }
                    bool direct = (TextureScratchLifetime.Enabled == null || TextureScratchLifetime.Enabled.Value) && !q.setSize &&
                        bitmap.PixelFormat == PixelFormat.Format24bppRgb && format == PixelFormat.Format24bppRgb &&
                        (q.width & 3) == 0 && q.width == bitmap.Width && q.height == bitmap.Height;
                    Bitmap destination = direct ? bitmap : new Bitmap(q.width, q.height, format);
                    try
                    {
                        Rectangle rect = new Rectangle(0, 0, q.width, q.height);
                        if (!direct)
                            using (var graphics = System.Drawing.Graphics.FromImage(destination))
                            {
                                if (q.setSize)
                                {
                                    if (q.fillBackground) using (var brush = new SolidBrush(System.Drawing.Color.White)) graphics.FillRectangle(brush, rect);
                                    float scale = Mathf.Min((float)q.width / bitmap.Width, (float)q.height / bitmap.Height);
                                    int w = (int)(bitmap.Width * scale), h = (int)(bitmap.Height * scale);
                                    graphics.DrawImage(bitmap, (q.width - w) / 2, (q.height - h) / 2, w, h);
                                }
                                else graphics.DrawImage(bitmap, 0, 0, q.width, q.height);
                            }
                        int baseBytes = checked(q.width * q.height * channels);
                        int nativeLength = Mathf.CeilToInt((float)baseBytes * 1.5f);
                        int length = ColdTextureBufferLayout.Active ? ColdTextureBufferLayout.DecodeLength(nativeLength, q.width, q.height, channels, q.createMipMaps) : nativeLength;
                        long required = TextureUploadReuse.RequiredBytes(q.width, q.height, channels, q.createMipMaps);
                        if (q.createNormalFromBump && (!ColdTextureBufferLayout.Active || length < required)) length = checked(baseBytes * 2);
                        output = NativeCacheBuffer.RasterLease.Allocate(length, false);
                        // Pooled managed fallbacks must have the same zeroed tail
                        // as the current rental path; newly allocated pages are zero.
                        if (output.managed != null) Array.Clear(output.managed, 0, output.managed.Length);
                        var data = destination.LockBits(rect, ImageLockMode.ReadOnly, destination.PixelFormat);
                        try { output.CopyFrom(data.Scan0, baseBytes); }
                        finally { destination.UnlockBits(data); }
                    }
                    finally { if (!direct) destination.Dispose(); }
                }
                int pixels = checked(q.width * q.height);
                int count = q.textureFormat == TextureFormat.RGB24 ? 3 : 4;
                for (int i = 0; i < pixels; i++)
                {
                    int p = i * count;
                    byte r = output.Read(p + 2), g = output.Read(p + 1), b = output.Read(p);
                    byte a = count == 4 ? output.Read(p + 3) : (byte)255;
                    if (q.isNormalMap) a = 255;
                    if (q.invert) { r = (byte)(255 - r); g = (byte)(255 - g); b = (byte)(255 - b); a = (byte)(255 - a); }
                    if (q.createAlphaFromGrayscale) a = (byte)((r + g + b) / 3);
                    output.Write(p, r); output.Write(p + 1, g); output.Write(p + 2, b);
                    if (count == 4) output.Write(p + 3, a);
                }
                if (q.createNormalFromBump) ColdRasterBump.Convert(output, q.width, q.height, q.bumpStrength);
                return output;
            }
            catch { if (output != null) output.Dispose(); throw; }
        }

        private static void ValidatePointerUpload()
        {
            foreach (int channels in new[] { 3, 4 })
                foreach (bool mipmaps in new[] { false, true })
                    foreach (int width in new[] { 8, 7, 1 })
                    {
                        int height = width == 1 ? 9 : 5;
                        int length = (int)TextureUploadReuse.RequiredBytes(width, height, channels, mipmaps);
                        Texture2D texture = null;
                        try
                        {
                            using (var data = NativeCacheBuffer.RasterLease.Allocate(length, true))
                            {
                                for (int i = 0; i < length; i++) data.Write(i, (byte)(i * 37 + 11));
                                texture = new Texture2D(width, height, (TextureFormat)channels, mipmaps, true);
                                data.UploadForValidation(texture);
                                // Match production: temporary pages end before
                                // Apply, not after a readback still using them.
                                data.Dispose();
                                texture.Apply(false, false);
                                byte[] actual = texture.GetRawTextureData();
                                if (actual.Length != length) throw new InvalidOperationException("pointer upload length mismatch");
                                for (int i = 0; i < length; i++)
                                    if (actual[i] != (byte)(i * 37 + 11)) throw new InvalidOperationException("pointer upload byte mismatch");
                                texture.Apply(true, false);
                                actual = texture.GetRawTextureData();
                                for (int i = 0; i < width * height * channels; i++)
                                    if (actual[i] != (byte)(i * 37 + 11)) throw new InvalidOperationException("mip regeneration changed base pixels");
                            }
                        }
                        finally { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
                    }
        }

        internal static void Install()
        {
            if (_tried || (Enabled != null && !Enabled.Value) || !NativeCacheBuffer.RasterReady) return;
            _tried = true;
            try
            {
                if (!NativeCacheBuffer.RasterReady) throw new InvalidOperationException("native upload broker not installed");
                ValidatePointerUpload();
                _harmony = new Harmony("Quest3TriggerUI.cold-native-raster");
                _harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("ProcessFromStream", All),
                    prefix: new HarmonyMethod(typeof(ColdRasterDecode).GetMethod("Before", All)));
                _validated = true;
                Log("installed; pointer selftest PASS RGB24/RGBA32 POT/NPOT/thin mip/no-mip; existing native budget, weak/shared owners, original completion retained");
            }
            catch (Exception error) { Shutdown(); _tried = true; Log("not installed: " + error.Message); }
        }

        internal static void Shutdown()
        {
            _validated = false;
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _tried = false;
        }
        private static void Log(string text)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[cold-native] " + text);
        }
    }
}
