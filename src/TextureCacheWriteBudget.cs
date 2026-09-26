using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;

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
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private static long[] SharedTotals()
        {
            const string key = "Quest3TriggerUI.texture-cache-write-bytes.v1";
            var totals = AppDomain.CurrentDomain.GetData(key) as long[];
            if (totals == null) { totals = new long[1]; AppDomain.CurrentDomain.SetData(key, totals); }
            return totals;
        }
        internal static long PendingBytes() { return Interlocked.Read(ref Totals[0]); }

        private static void ReleaseUploadedRaw(ImageLoaderThreaded.QueuedImage image)
        {
            if (Enabled == null || Enabled.Value) image.raw = null;
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
            Interlocked.Add(ref Totals[0], item.bytes);
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
                Log("installed; asynchronous cache arrays stay charged until write completion; no main-thread wait");
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e.Message); }
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
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
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(code[uploaded].labels); code[uploaded].labels.Clear();
            first.blocks.AddRange(code[uploaded].blocks); code[uploaded].blocks.Clear();
            code.InsertRange(uploaded, new[] { first, new CodeInstruction(OpCodes.Call,
                typeof(TextureCacheWriteBudget).GetMethod("ReleaseUploadedRaw", Static)) });
            return code;
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            // Keep the debt reader while old callbacks drain. It owns only longs,
            // not textures/arrays; a new payload replaces the delegate at Install.
        }
        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[texture-cache-write] " + message);
        }
    }
}
