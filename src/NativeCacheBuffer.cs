using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Game adapter only. The arena owns allocation, budgets, sharing and free;
    // this bounded weak table transfers leases between original consumers.
    internal static partial class NativeCacheBuffer
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> BudgetMiB, IdleMiB, MaxFileMiB;
        internal static long Staged, StagedBytes, Uploaded, Released, Failures;
        private static readonly object Sync = new object();
        private static readonly TextureStagingArena Arena = new TextureStagingArena(BudgetBytes, IdleBytesCap);
        private static readonly List<Stage> Stages = new List<Stage>(256);
        private static Harmony _harmony;
        private static bool _retired;
        private static int _nextSweep;
        private const int ScratchBytes = 128 * 1024;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        [ThreadStatic] private static byte[] _scratch;

        private sealed class Stage
        {
            internal WeakReference image;
            internal TextureStagingArena.Lease lease;
        }

        internal static long LiveMiB { get { return Arena.Snapshot().LiveBytes / 1048576; } }
        internal static long IdleHeldMiB { get { return Arena.Snapshot().IdleBytes / 1048576; } }
        internal static int LiveSlots { get { return Arena.Snapshot().LiveBlocks; } }
        internal static TextureStagingArena.State OwnershipState { get { return Arena.Snapshot(); } }
        private static bool Active() { return Enabled == null || Enabled.Value; }
        private static long BudgetBytes()
        { return (BudgetMiB == null ? 1024L : Math.Max(0L, BudgetMiB.Value)) * 1048576; }
        private static long IdleBytesCap()
        { return Math.Min(64L, IdleMiB == null ? 192L : Math.Max(0L, IdleMiB.Value)) * 1048576; }
        private static long FileCapBytes()
        { return (MaxFileMiB == null ? 512L : Math.Max(1L, MaxFileMiB.Value)) * 1048576; }
        private static int AllocationCapacity(int length, bool raster)
        { return TextureStagingArena.Capacity(length, raster); }
        private static TextureStagingArena.Lease Rent(int length, bool raster = false)
        { return Arena.Rent(length, raster); }
        private static void Drop(TextureStagingArena.Lease lease)
        { if (lease != null) lease.Dispose(); }

        internal static bool TryStage(ImageLoaderThreaded.QueuedImage q, string path)
        {
            if (q == null || !Active() || string.IsNullOrEmpty(path)) return false;
            TextureStagingArena.Lease lease = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                long length = info.Length;
                if (length < TextureCacheByteReuse.MinimumBytes() || length > FileCapBytes() || length > int.MaxValue) return false;
                lease = Rent((int)length);
                if (lease == null) return false;
                if (!Fill(lease, path)) { Interlocked.Increment(ref Failures); return false; }
                lease.SourcePath = path;
                if (!StageIt(q, lease)) return false;
                lease = null; // Transfer, not a second reference-count increment.
                Interlocked.Increment(ref Staged); Interlocked.Add(ref StagedBytes, length);
                return true;
            }
            catch (Exception) { Interlocked.Increment(ref Failures); return false; }
            finally { if (lease != null) lease.Dispose(); }
        }

        internal static void Upload(Texture2D texture, ImageLoaderThreaded.QueuedImage q)
        {
            var lease = Detach(q);
            if (lease == null) { texture.LoadRawTextureData(q.raw); return; }
            using (lease)
            {
                try
                {
                    texture.LoadRawTextureData(lease.Pointer, lease.Length);
                    Interlocked.Increment(ref Uploaded);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref Failures);
                    q.raw = lease.SourcePath != null ? TextureCacheByteReuse.ReadCachedBytes(lease.SourcePath, false) : CopyManaged(lease);
                    texture.LoadRawTextureData(q.raw);
                }
                finally { Interlocked.Increment(ref Released); }
            }
        }

        private static Exception AfterFinish(ImageLoaderThreaded.QueuedImage __instance, Exception __exception)
        { Release(__instance); return __exception; }
        private static Exception AfterProcess(ImageLoaderThreaded.QueuedImage __instance, Exception __exception)
        {
            if (__exception != null || (__instance != null && __instance.hadError)) Release(__instance);
            return __exception;
        }
        internal static void Release(ImageLoaderThreaded.QueuedImage q)
        {
            var lease = Detach(q);
            if (lease == null) return;
            Interlocked.Increment(ref Released); lease.Dispose();
        }
        internal static long StagedLength(ImageLoaderThreaded.QueuedImage q)
        {
            if (q == null) return 0;
            lock (Sync) foreach (Stage stage in Stages)
                if (ReferenceEquals(stage.image.Target, q)) return stage.lease.Length;
            return 0;
        }
        private static bool StageIt(ImageLoaderThreaded.QueuedImage q, TextureStagingArena.Lease lease)
        {
            lock (Sync)
            {
                if (_retired || !Arena.Accepts(lease) || q.cancel || q.finished || Stages.Count >= 256) return false;
                foreach (Stage stage in Stages) if (ReferenceEquals(stage.image.Target, q)) return false;
                Stages.Add(new Stage { image = new WeakReference(q), lease = lease });
                q.raw = null;
                return true;
            }
        }
        private static TextureStagingArena.Lease Detach(ImageLoaderThreaded.QueuedImage q)
        {
            if (q == null) return null;
            lock (Sync) for (int i = 0; i < Stages.Count; i++)
                if (ReferenceEquals(Stages[i].image.Target, q))
                { var lease = Stages[i].lease; Stages.RemoveAt(i); return lease; }
            return null;
        }
        internal static long TrimIdleNow() { return Arena.TrimIdle(true); }
        internal static void SweepIdle()
        {
            int now = Environment.TickCount;
            lock (Sync)
            {
                if (_nextSweep != 0 && unchecked(now - _nextSweep) < 0) return;
                _nextSweep = now + 1000;
                for (int i = Stages.Count - 1; i >= 0; i--)
                {
                    // A public finished/cancel flag is not an end-of-consumer
                    // receipt. Normal retirement is the original finalizer.
                    if (Stages[i].image.Target != null) continue;
                    var lease = Stages[i].lease; Stages.RemoveAt(i); lease.Dispose();
                }
                Arena.TrimIdle(false);
            }
        }
        private static bool Fill(TextureStagingArena.Lease lease, string path)
        {
            byte[] scratch = _scratch;
            if (scratch == null || scratch.Length != ScratchBytes) scratch = _scratch = new byte[ScratchBytes];
            int read = 0, length = lease.Length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ScratchBytes, FileOptions.SequentialScan))
                while (read < length)
                {
                    int n = stream.Read(scratch, 0, Math.Min(scratch.Length, length - read));
                    if (n <= 0) break;
                    Marshal.Copy(scratch, 0, new IntPtr(lease.Pointer.ToInt64() + read), n); read += n;
                }
            return read == length;
        }

        // Finish uploads raw at four sites. Replace only q.raw byte[] calls,
        // in place, so
        // every label, branch and exception boundary stays where it was.
        internal static IEnumerable<CodeInstruction> FinishTranspiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo loadBytes = typeof(Texture2D).GetMethod("LoadRawTextureData",
                new[] { typeof(byte[]) });
            if (loadBytes == null) throw new MissingMethodException("byte[] upload");
            FieldInfo raw = typeof(ImageLoaderThreaded.QueuedImage).GetField("raw", All);
            if (raw == null) throw new MissingFieldException("raw");
            var sites = new List<int>();
            for (int i = 2; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Callvirt || !Equals(code[i].operand, loadBytes)) continue;
                if (code[i - 1].opcode != OpCodes.Ldfld || !Equals(code[i - 1].operand, raw)) continue;
                if (code[i - 2].opcode != OpCodes.Ldarg_0) continue;
                if (code[i].blocks.Count != 0 || code[i - 1].blocks.Count != 0)
                    throw new InvalidOperationException("upload exception boundary changed");
                sites.Add(i);
            }
            // Exactly the preprocessed first/retry, DXT intermediary and plain
            // cold uploads. tex.GetRawTextureData uploads are not q.raw sites.
            if (sites.Count != 4) throw new InvalidOperationException("request upload anchors: " + sites.Count);
            foreach (int site in sites)
            {
                code[site - 1].opcode = OpCodes.Nop;
                code[site - 1].operand = null;
                code[site].opcode = OpCodes.Call;
                code[site].operand = typeof(NativeCacheBuffer).GetMethod("Upload", Static);
            }
            return code;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                MethodInfo finish = typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Finish",
                    All, null, Type.EmptyTypes, null);
                if (finish == null) throw new MissingMethodException("QueuedImage.Finish");
                _harmony = new Harmony("Quest3TriggerUI.native-cache-buffer");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(finish,
                    transpiler: new HarmonyMethod(typeof(NativeCacheBuffer)
                        .GetMethod("FinishTranspiler", Static)),
                    finalizer: new HarmonyMethod(typeof(NativeCacheBuffer)
                        .GetMethod("AfterFinish", Static)));
                _harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Process", All),
                    finalizer: new HarmonyMethod(typeof(NativeCacheBuffer).GetMethod("AfterProcess", Static)));
                lock (Sync) { Arena.Reactivate(); _retired = false; }
                Log("installed; staged cache reads upload by pointer, budget=" +
                    (BudgetBytes() / 1048576) + "MiB reuse=" + (IdleBytesCap() / 1048576) +
                    "MiB filecap=" + (FileCapBytes() / 1048576) + "MiB ownership=consumer-leases reclamation=last-consumer");
            }
            catch (Exception e)
            {
                Shutdown();
                Log("not installed: " + e.Message);
            }
        }

        internal static void Shutdown()
        {
            lock (Sync)
            {
                _retired = true; Arena.Retire();
                // Restore each shared payload once. Filling/uploading consumers
                // keep their private leases; retirement never frees their pages.
                var restored = new Dictionary<IntPtr, byte[]>();
                for (int i = Stages.Count - 1; i >= 0; i--)
                {
                    Stage stage = Stages[i]; var q = stage.image.Target as ImageLoaderThreaded.QueuedImage;
                    if (q != null && !q.hadError && !q.finished && q.raw == null)
                    {
                        byte[] raw;
                        if (!restored.TryGetValue(stage.lease.Pointer, out raw))
                        { raw = CopyManaged(stage.lease); restored.Add(stage.lease.Pointer, raw); }
                        q.raw = raw;
                    }
                    Stages.RemoveAt(i); stage.lease.Dispose();
                }
            }
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
        }
        private static void Log(string message)
        { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[native-cache] " + message); }
    }
}
