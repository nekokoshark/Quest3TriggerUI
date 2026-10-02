using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // QueuedImage.Process reads a completed texture cache file with
    // FileManager.ReadAllBytes and stores it in q.raw; Finish uploads it and
    // then nulls it. That array is request-private and short-lived exactly like
    // the decode output ProcessFromStream allocates, but it is allocated inside
    // the native reader, so the decode-path transpiler never saw it: a preset
    // switch served entirely from the texture cache still allocated 0.4-1.4GB
    // of throwaway managed bytes per cycle (texture-budget managedDecodeMiB,
    // 2026-09-28 08:47, poolHit=0/0). Route those two reads through the
    // exact-length pool. Any deviation from the native read - missing file,
    // small file, short read, exception - falls back to the native reader so
    // the caller always sees the same bytes.
    internal static class TextureCacheByteReuse
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> MinBytesKB;
        // Diagnostics only; read from the texture-budget cycle line.
        internal static long Hits, Bytes, Fallbacks;
        private static Harmony _harmony;
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;

        private static bool Active()
        {
            return (Enabled == null || Enabled.Value) &&
                (DecodedBufferPool.Enabled == null || DecodedBufferPool.Enabled.Value);
        }

        internal static int MinimumBytes()
        {
            return (MinBytesKB == null ? 256 : Math.Max(0, MinBytesKB.Value)) * 1024;
        }

        // Stands in for the two cache reads of QueuedImage.Process. The native
        // module stages the payload off the managed heap when it can; anything
        // it refuses keeps the pooled managed read that was there before.
        private static void Acquire(ImageLoaderThreaded.QueuedImage q, string path, bool onlySystem)
        {
            // A second read for the same request replaces the first payload,
            // including when the second read must use the managed path.
            NativeCacheBuffer.Release(q);
            if (q != null && NativeCacheBuffer.TryStage(q, path))
            {
                return;
            }
            q.raw = ReadCachedBytes(path, onlySystem);
        }

        // Stands in for MVR.FileManagement.FileManager.ReadAllBytes(string, bool)
        // at the two cache-read sites of QueuedImage.Process. The pool returns
        // an exact-length recycled array which is then overwritten in full.
        internal static byte[] ReadCachedBytes(string path, bool onlySystem)
        {
            if (!Active() || string.IsNullOrEmpty(path))
                return MVR.FileManagement.FileManager.ReadAllBytes(path, onlySystem);
            byte[] buffer = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return MVR.FileManagement.FileManager.ReadAllBytes(path, onlySystem);
                long length = info.Length;
                if (length < MinimumBytes() || length > int.MaxValue)
                {
                    Fallbacks++;
                    return MVR.FileManagement.FileManager.ReadAllBytes(path, onlySystem);
                }
                buffer = DecodedBufferPool.RentByteArray((int)length);
                if (buffer == null || buffer.Length != (int)length)
                {
                    if (buffer != null) { DecodedBufferPool.ReturnArray(buffer); buffer = null; }
                    Fallbacks++;
                    return MVR.FileManagement.FileManager.ReadAllBytes(path, onlySystem);
                }
                int read = 0;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 65536, FileOptions.SequentialScan))
                {
                    while (read < buffer.Length)
                    {
                        int n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                if (read != buffer.Length)
                {
                    // The file changed under us: hand the buffer back and let the
                    // native reader produce a complete result.
                    Fallbacks++;
                    DecodedBufferPool.ReturnArray(buffer);
                    buffer = null;
                    return MVR.FileManagement.FileManager.ReadAllBytes(path, onlySystem);
                }
                Hits++;
                Bytes += buffer.Length;
                byte[] result = buffer;
                buffer = null; // The request owns the loan now.
                return result;
            }
            catch (Exception)
            {
                Fallbacks++;
                if (buffer != null) { DecodedBufferPool.ReturnArray(buffer); buffer = null; }
                return MVR.FileManagement.FileManager.ReadAllBytes(path, onlySystem);
            }
            finally { if (buffer != null) DecodedBufferPool.ReturnArray(buffer); }
        }

        // Process() has exactly two cache reads: the web-cache path and the
        // disk-cache path. Both are real files under the cache directory, both
        // feed q.raw, and neither result may be reused by the pool while
        // another request still holds it.
        internal static IEnumerable<CodeInstruction> ProcessTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo swap = typeof(TextureCacheByteReuse).GetMethod("Acquire",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (swap == null) throw new MissingMethodException("cache byte reader");
            FieldInfo raw = typeof(ImageLoaderThreaded.QueuedImage).GetField("raw", All);
            if (raw == null) throw new MissingFieldException("QueuedImage.raw");
            var code = new List<CodeInstruction>(instructions);
            int matches = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Call) continue;
                var m = code[i].operand as MethodInfo;
                if (m == null || m.DeclaringType != typeof(MVR.FileManagement.FileManager) ||
                    m.Name != "ReadAllBytes") continue;
                var ps = m.GetParameters();
                if (ps.Length != 2 || ps[0].ParameterType != typeof(string) ||
                    ps[1].ParameterType != typeof(bool)) continue;
                // The reader keeps the q.raw store inside the replacement: a
                // staged native block means the request has no managed payload,
                // and a fallback writes the pooled array itself.
                if (i + 1 >= code.Count || code[i + 1].opcode != OpCodes.Stfld ||
                    !Equals(code[i + 1].operand, raw))
                    throw new InvalidOperationException("cache read result store changed");
                if (code[i].blocks.Count != 0 || code[i + 1].blocks.Count != 0)
                    throw new InvalidOperationException("cache read exception boundary changed");
                code[i].operand = swap;
                code[i].labels.AddRange(code[i + 1].labels);
                code.RemoveAt(i + 1);
                matches++;
            }
            if (matches != 2)
                throw new InvalidOperationException("cache read anchors: " + matches);
            return code;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            bool managed = Enabled == null || Enabled.Value;
            bool native = NativeCacheBuffer.Enabled == null || NativeCacheBuffer.Enabled.Value;
            if (!managed && !native) return;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.texture-cache-byte-reuse");
                _harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Process", All),
                    transpiler: new HarmonyMethod(typeof(TextureCacheByteReuse)
                        .GetMethod("ProcessTranspiler", All)));
                Log("installed; cached texture bytes come from the decode pool");
            }
            catch (Exception e)
            {
                Shutdown();
                Log("not installed: " + e.Message);
            }
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            Hits = 0; Bytes = 0; Fallbacks = 0;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[texture-cache-bytes] " + message);
        }
    }
}
