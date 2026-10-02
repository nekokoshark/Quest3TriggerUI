using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class TextureCacheWriteBudget
    {
        internal static ConfigEntry<bool> Enabled;
        private static Harmony _harmony;
        private static FieldInfo _bytesField;
        // Share only primitive accounting across hot-loaded namespaces. Old
        // callbacks may finish after Shutdown; do not zero their outstanding debt.
        private static readonly long[] Totals = SharedTotals();
        private static long _readbacks, _readbackFailures, _writeBytes, _candidateWrites,
            _candidateBytes, _rawWriteMax, _pendingPeak, _readTicks, _shapeFailures;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private static long[] SharedTotals()
        {
            const string key = "Quest3TriggerUI.texture-cache-write-bytes.v1";
            var totals = AppDomain.CurrentDomain.GetData(key) as long[];
            if (totals == null) { totals = new long[1]; AppDomain.CurrentDomain.SetData(key, totals); }
            return totals;
        }
        internal static long PendingBytes() { return Interlocked.Read(ref Totals[0]); }

        private static void Peak(ref long field, long value)
        {
            long old = Interlocked.Read(ref field);
            while (value > old)
            {
                long actual = Interlocked.CompareExchange(ref field, value, old);
                if (actual == old) return;
                old = actual;
            }
        }

        private static long ReleaseUploadedRaw(ImageLoaderThreaded.QueuedImage image)
        {
            long bytes = image.raw == null ? 0 : image.raw.LongLength;
            if (Enabled == null || Enabled.Value) image.raw = null;
            return bytes; // Primitive local only; no additional array/request holder.
        }

        private static byte[] ReadCacheBytes(Texture2D texture,
            ImageLoaderThreaded.QueuedImage image, long decodedBytes)
        {
            if (Enabled != null && !Enabled.Value) return texture.GetRawTextureData();
            Interlocked.Increment(ref _readbacks);
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            byte[] bytes;
            try { bytes = texture.GetRawTextureData(); }
            catch { Interlocked.Increment(ref _readbackFailures); throw; }
            finally { Interlocked.Add(ref _readTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start); }
            if (bytes == null) return bytes;
            Interlocked.Add(ref _writeBytes, bytes.LongLength);
            // This is an observed two-buffer capacity, NOT an actual process/heap peak.
            Peak(ref _rawWriteMax, decodedBytes + bytes.LongLength);
            try
            {
                if (image != null && !image.preprocessed && !image.compress &&
                    !image.createMipMaps && !image.createNormalFromBump && image.width > 0 && image.height > 0 &&
                    texture.width == image.width && texture.height == image.height && texture.mipmapCount == 1)
                {
                    int format = (int)texture.format;
                    long pixels = (long)image.width * image.height;
                    int stride = format == 3 ? 3 : format == 4 ? 4 : 0;
                    if (stride != 0 && pixels <= int.MaxValue / stride &&
                        pixels * stride == bytes.LongLength && decodedBytes == bytes.LongLength)
                    {
                        Interlocked.Increment(ref _candidateWrites);
                        Interlocked.Add(ref _candidateBytes, bytes.LongLength);
                    }
                }
            }
            // Only new diagnostic property reads are contained. Native readback exceptions above propagate.
            catch (Exception) { Interlocked.Increment(ref _shapeFailures); }
            return bytes; // Native bytes, exactly one native call; no pooling/reuse added.
        }

        internal static void ReportCycle()
        {
            if (Enabled != null && !Enabled.Value) return;
            Log("profile cumulative=True readbacks=" + Interlocked.Read(ref _readbacks) +
                " failed=" + Interlocked.Read(ref _readbackFailures) +
                " writeBytes=" + Interlocked.Read(ref _writeBytes) +
                " layoutCandidates=" + Interlocked.Read(ref _candidateWrites) +
                " layoutCandidateBytes=" + Interlocked.Read(ref _candidateBytes) +
                " maxRawPlusWriteBytes=" + Interlocked.Read(ref _rawWriteMax) +
                " pendingBytes=" + PendingBytes() + " peakPendingBytes=" + Interlocked.Read(ref _pendingPeak) +
                " readbackTicks=" + Interlocked.Read(ref _readTicks) +
                " stopwatchFrequency=" + System.Diagnostics.Stopwatch.Frequency +
                " shapeUnknown=" + Interlocked.Read(ref _shapeFailures) +
                " (layout candidates, not pixel proof; capacities, not process peak; pending includes older writers)");
        }

        private sealed class Write
        {
            internal WaitCallback callback;
            internal long bytes;
            private int released;
            internal void Release()
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                {
                    callback = null;
                    Interlocked.Add(ref Totals[0], -bytes);
                }
            }
            internal void Run(object state)
            {
                try { callback(null); }
                finally { Release(); }
            }
        }

        private static bool Queue(WaitCallback callback)
        {
            if (Enabled != null && !Enabled.Value) return ThreadPool.QueueUserWorkItem(callback);
            var raw = _bytesField.GetValue(callback.Target) as byte[];
            if (raw == null) return ThreadPool.QueueUserWorkItem(callback);
            var item = new Write { callback = callback, bytes = raw.LongLength };
            Peak(ref _pendingPeak, Interlocked.Add(ref Totals[0], item.bytes));
            try
            {
                bool queued = ThreadPool.QueueUserWorkItem(item.Run);
                if (!queued) item.Release();
                return queued;
            }
            catch { item.Release(); throw; }
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.texture-cache-write-budget");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Finish",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                    transpiler: new HarmonyMethod(typeof(TextureCacheWriteBudget).GetMethod("Transpile", Static)));
                TextureDecodeBudget.PendingCacheBytes = PendingBytes;
                Log("installed; asynchronous cache arrays stay charged until write completion; no main-thread wait; readback profile=True");
                ReportCycle();
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e.Message); }
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);
            var queue = typeof(ThreadPool).GetMethod("QueueUserWorkItem", new[] { typeof(WaitCallback) });
            int found = 0;
            foreach (var c in code)
            {
                var field = c.operand as FieldInfo;
                if (c.opcode == OpCodes.Stfld && field != null && field.Name == "rawTextureData2" &&
                    field.FieldType == typeof(byte[]) && field.DeclaringType.DeclaringType == typeof(ImageLoaderThreaded.QueuedImage))
                    _bytesField = field;
                if (c.opcode != OpCodes.Call || !Equals(c.operand, queue)) continue;
                c.operand = typeof(TextureCacheWriteBudget).GetMethod("Queue", Static);
                found++;
            }
            if (found != 1 || _bytesField == null) throw new InvalidOperationException("native cache writer anchor changed");
            int uploaded = code.FindIndex(c =>
            {
                var m = c.operand as MethodInfo;
                return c.opcode == OpCodes.Call && m != null && m.Name == "get_CachingEnabled" &&
                    m.DeclaringType.FullName == "MVR.FileManagement.CacheManager";
            });
            var raw = typeof(ImageLoaderThreaded.QueuedImage).GetField("raw");
            if (uploaded < 0 || raw == null) throw new InvalidOperationException("upload completion anchor changed");
            for (int i = uploaded; i < code.Count; i++)
                if ((code[i].opcode == OpCodes.Ldfld || code[i].opcode == OpCodes.Ldflda) && Equals(code[i].operand, raw))
                    throw new InvalidOperationException("raw is still read after upload");
            var readback = typeof(Texture2D).GetMethod("GetRawTextureData", Type.EmptyTypes);
            var tex = typeof(ImageLoaderThreaded.QueuedImage).GetField("tex");
            int writer = -1;
            for (int i = uploaded + 3; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Stfld || !Equals(code[i].operand, _bytesField)) continue;
                if (writer >= 0 || code[i - 1].opcode != OpCodes.Callvirt || !Equals(code[i - 1].operand, readback) ||
                    code[i - 2].opcode != OpCodes.Ldfld || !Equals(code[i - 2].operand, tex) ||
                    code[i - 3].opcode != OpCodes.Ldarg_0 || code[i - 1].blocks.Count != 0)
                    throw new InvalidOperationException("native cache readback anchor changed");
                writer = i - 1;
            }
            if (writer < 0) throw new InvalidOperationException("native cache readback missing");
            LocalBuilder decoded = generator.DeclareLocal(typeof(long));
            var request = new CodeInstruction(OpCodes.Ldarg_0);
            request.labels.AddRange(code[writer].labels); code[writer].labels.Clear();
            code.InsertRange(writer, new[] { request, new CodeInstruction(OpCodes.Ldloc, decoded) });
            code[writer + 2].opcode = OpCodes.Call;
            code[writer + 2].operand = typeof(TextureCacheWriteBudget).GetMethod("ReadCacheBytes", Static);
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(code[uploaded].labels); code[uploaded].labels.Clear();
            first.blocks.AddRange(code[uploaded].blocks); code[uploaded].blocks.Clear();
            code.InsertRange(uploaded, new[] { first, new CodeInstruction(OpCodes.Call,
                typeof(TextureCacheWriteBudget).GetMethod("ReleaseUploadedRaw", Static)), new CodeInstruction(OpCodes.Stloc, decoded) });
            return code;
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _readbacks = _readbackFailures = _writeBytes = _candidateWrites = _candidateBytes = 0;
            _rawWriteMax = _pendingPeak = _readTicks = _shapeFailures = 0;
            // Keep the debt reader while old callbacks drain. It owns only longs,
            // not textures/arrays; a new payload replaces the delegate at Install.
        }
        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[texture-cache-write] " + message);
        }
    }
}
