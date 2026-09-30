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
    // A completed .vamcache hit is read into a native block instead of a
    // managed byte[]: the block leaves the process the moment the upload has
    // consumed it, so those pages never join Boehm's free lists for the rest
    // of the session. Staging is budget-bounded, and every deviation - small
    // file, short read, budget, exception - falls back to the pooled managed
    // read, so the request always uploads the same bytes.
    internal static class NativeCacheBuffer
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> BudgetMiB;
        internal static ConfigEntry<int> IdleMiB;
        internal static ConfigEntry<int> MaxFileMiB;

        // Diagnostics, read from the texture-budget cycle line.
        internal static long Staged, StagedBytes, Uploaded, Released, Failures;

        // Bytes currently staged for an in-flight request, and bytes held
        // committed for reuse. Both are bounded by config, not by traffic.
        internal static long LiveMiB { get { return Interlocked.Read(ref _liveBytes) / 1048576L; } }
        internal static long IdleHeldMiB { get { return Interlocked.Read(ref _idleBytes) / 1048576L; } }

        private static Harmony _harmony;
        private static readonly object Sync = new object();
        // Weak keys plus counted blocks: staging can never root the request it
        // describes, and a retired hot-reload generation keeps at most the
        // blocks it is still walking (its own finalizers release those).
        private static readonly List<Stage> Stages = new List<Stage>(8);
        private static readonly Dictionary<int, Stack<Block>> Idle =
            new Dictionary<int, Stack<Block>>();
        private static long _liveBytes, _idleBytes;
        private const int MaxStages = 16;
        private const int MinShift = 20;
        private const int MaxShift = 30;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;
        private const int ScratchBytes = 128 * 1024;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size,
            uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);

        [ThreadStatic] private static byte[] _scratch;

        private sealed class Block
        {
            internal IntPtr ptr;
            internal int capacity;
            internal int length;
            internal string path;
            ~Block()
            {
                // Safety net only: a staged block is released on the main
                // thread, an idle block is owned by the reuse list. This runs
                // for a block whose generation was retired with it still open.
                IntPtr handle = ptr;
                ptr = IntPtr.Zero;
                if (handle != IntPtr.Zero) VirtualFree(handle, UIntPtr.Zero, MEM_RELEASE);
            }
        }

        private sealed class Stage
        {
            internal WeakReference image;
            internal Block block;
        }

        private static bool Active()
        {
            return Enabled == null || Enabled.Value;
        }

        private static long BudgetBytes()
        {
            long mib = BudgetMiB == null ? 1024L : Math.Max(0L, BudgetMiB.Value);
            return mib * 1048576L;
        }

        private static long IdleBytesCap()
        {
            long mib = IdleMiB == null ? 192L : Math.Max(0L, IdleMiB.Value);
            return mib * 1048576L;
        }

        private static long FileCapBytes()
        {
            long mib = MaxFileMiB == null ? 512L : Math.Max(1L, MaxFileMiB.Value);
            return mib * 1048576L;
        }

        // Called at both cache reads of QueuedImage.Process. True means a
        // native block now owns the payload and q.raw must stay null.
        internal static bool TryStage(ImageLoaderThreaded.QueuedImage q, string path)
        {
            if (q == null || !Active() || string.IsNullOrEmpty(path)) return false;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                long length = info.Length;
                if (length < TextureCacheByteReuse.MinimumBytes() || length > FileCapBytes() ||
                    length > int.MaxValue) return false;
                int size = (int)length;
                if (Interlocked.Read(ref _liveBytes) + size > BudgetBytes()) return false;
                Block block = Rent(size);
                if (block == null) return false;
                int read;
                if (!Fill(block, path, size, out read) || read != size)
                {
                    Interlocked.Increment(ref Failures);
                    Return(block);
                    return false;
                }
                block.length = size;
                block.path = path;
                Interlocked.Add(ref _liveBytes, block.capacity);
                StageIt(q, block);
                Interlocked.Increment(ref Staged);
                Interlocked.Add(ref StagedBytes, size);
                return true;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref Failures);
                return false;
            }
        }

        // Stands in for tex.LoadRawTextureData(q.raw) at the preprocessed
        // upload of QueuedImage.Finish. A staged block is uploaded by pointer
        // and released immediately; every other request - decode, inflight
        // share, web cache - keeps the exact byte[] upload it had.
        internal static void Upload(Texture2D texture, ImageLoaderThreaded.QueuedImage q)
        {
            Block block = Detach(q);
            if (block == null)
            {
                texture.LoadRawTextureData(q.raw);
                return;
            }
            try
            {
                texture.LoadRawTextureData(block.ptr, block.length);
                Interlocked.Increment(ref Uploaded);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref Failures);
                // The native upload can only fail on a pathological geometry
                // change; re-read the staged file so the native byte[] path
                // still completes the request.
                try { q.raw = TextureCacheByteReuse.ReadCachedBytes(block.path, false); }
                catch (Exception) { }
                Drop(block);
                texture.LoadRawTextureData(q.raw);
                return;
            }
            Interlocked.Increment(ref Released);
            Drop(block);
        }

        // Finish can return before the upload (native cache recheck, stale
        // request, upload failure). Whatever is still staged for the request
        // is released here, so no block outlives its transaction.
        private static void AfterFinish(ImageLoaderThreaded.QueuedImage __instance)
        {
            Release(__instance);
        }

        internal static void Release(ImageLoaderThreaded.QueuedImage q)
        {
            Block block = Detach(q);
            if (block == null) return;
            Interlocked.Increment(ref Released);
            Drop(block);
        }

        internal static long StagedLength(ImageLoaderThreaded.QueuedImage q)
        {
            if (q == null) return 0L;
            lock (Sync)
                for (int i = 0; i < Stages.Count; i++)
                    if (ReferenceEquals(Stages[i].image.Target, q)) return Stages[i].block.length;
            return 0L;
        }

        private static void StageIt(ImageLoaderThreaded.QueuedImage q, Block block)
        {
            var evicted = new List<Block>();
            lock (Sync)
            {
                for (int i = Stages.Count - 1; i >= 0; i--)
                {
                    if (Stages[i].image.IsAlive && !ReferenceEquals(Stages[i].image.Target, q))
                        continue;
                    evicted.Add(Stages[i].block);
                    Stages.RemoveAt(i);
                }
                if (Stages.Count >= MaxStages)
                {
                    evicted.Add(Stages[0].block);
                    Stages.RemoveAt(0);
                }
                Stages.Add(new Stage { image = new WeakReference(q), block = block });
            }
            for (int i = 0; i < evicted.Count; i++) Drop(evicted[i]);
        }

        private static Block Detach(ImageLoaderThreaded.QueuedImage q)
        {
            if (q == null) return null;
            lock (Sync)
                for (int i = 0; i < Stages.Count; i++)
                    if (ReferenceEquals(Stages[i].image.Target, q))
                    {
                        Block block = Stages[i].block;
                        Stages.RemoveAt(i);
                        return block;
                    }
            return null;
        }

        private static void Drop(Block block)
        {
            if (block == null) return;
            Interlocked.Add(ref _liveBytes, -block.capacity);
            Return(block);
        }

        private static int Capacity(int length)
        {
            if (length <= 0 || length > (1 << MaxShift)) return 0;
            int shift = MinShift;
            while ((1 << shift) < length && shift < MaxShift) shift++;
            return 1 << shift;
        }

        private static Block Rent(int length)
        {
            int capacity = Capacity(length);
            if (capacity == 0) return null;
            lock (Sync)
            {
                Stack<Block> idle;
                if (Idle.TryGetValue(capacity, out idle) && idle.Count > 0)
                {
                    Block reused = idle.Pop();
                    _idleBytes -= reused.capacity;
                    if (reused.ptr != IntPtr.Zero)
                    {
                        reused.length = 0;
                        reused.path = null;
                        return reused;
                    }
                }
            }
            IntPtr ptr = VirtualAlloc(IntPtr.Zero, new UIntPtr((ulong)capacity),
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (ptr == IntPtr.Zero) return null;
            return new Block { ptr = ptr, capacity = capacity };
        }

        private static void Return(Block block)
        {
            if (block == null || block.ptr == IntPtr.Zero) return;
            lock (Sync)
            {
                if (_idleBytes + block.capacity <= IdleBytesCap())
                {
                    Stack<Block> idle;
                    if (!Idle.TryGetValue(block.capacity, out idle))
                    {
                        idle = new Stack<Block>();
                        Idle[block.capacity] = idle;
                    }
                    idle.Push(block);
                    _idleBytes += block.capacity;
                    return;
                }
            }
            // Above the reuse cap the pages go straight back to the OS instead
            // of waiting for the next allocation of this size class.
            IntPtr ptr = block.ptr;
            block.ptr = IntPtr.Zero;
            VirtualFree(ptr, UIntPtr.Zero, MEM_RELEASE);
        }

        private static bool Fill(Block block, string path, int length, out int read)
        {
            read = 0;
            byte[] scratch = _scratch;
            if (scratch == null || scratch.Length != ScratchBytes)
                scratch = _scratch = new byte[ScratchBytes];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, ScratchBytes, FileOptions.SequentialScan))
            {
                while (read < length)
                {
                    int want = Math.Min(scratch.Length, length - read);
                    int n = stream.Read(scratch, 0, want);
                    if (n <= 0) break;
                    Marshal.Copy(scratch, 0, Offset(block.ptr, read), n);
                    read += n;
                }
            }
            return read == length;
        }

        private static IntPtr Offset(IntPtr address, int offset)
        {
            return new IntPtr(address.ToInt64() + offset);
        }

        // Finish uploads the preprocessed cache payload with the first byte[]
        // overload. Only that site is replaced, and it is replaced in place so
        // every label, branch and exception boundary stays where it was.
        internal static IEnumerable<CodeInstruction> FinishTranspiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo loadBytes = typeof(Texture2D).GetMethod("LoadRawTextureData",
                new[] { typeof(byte[]) });
            if (loadBytes == null) throw new MissingMethodException("byte[] upload");
            FieldInfo raw = typeof(ImageLoaderThreaded.QueuedImage).GetField("raw", All);
            FieldInfo preprocessed = typeof(ImageLoaderThreaded.QueuedImage).GetField("preprocessed", All);
            if (raw == null || preprocessed == null) throw new MissingFieldException("raw/preprocessed");
            int anchor = -1, seen = 0;
            for (int i = 2; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Callvirt || !Equals(code[i].operand, loadBytes)) continue;
                if (code[i - 1].opcode != OpCodes.Ldfld || !Equals(code[i - 1].operand, raw)) continue;
                if (code[i - 2].opcode != OpCodes.Ldarg_0) continue;
                seen++;
                bool guarded = false;
                for (int k = Math.Max(0, i - 8); k < i - 1; k++)
                    if (code[k].opcode == OpCodes.Ldfld && Equals(code[k].operand, preprocessed))
                        guarded = true;
                if (!guarded) continue;
                if (anchor >= 0) throw new InvalidOperationException("multiple preprocessed uploads");
                if (code[i].blocks.Count != 0 || code[i - 1].blocks.Count != 0)
                    throw new InvalidOperationException("upload exception boundary changed");
                anchor = i;
            }
            if (anchor < 0 || seen == 0)
                throw new InvalidOperationException("preprocessed upload anchor: " + seen);
            // tex is already on the stack here; the helper takes the request
            // from the field load it replaces, so the stack shape is unchanged.
            code[anchor - 1].opcode = OpCodes.Nop;
            code[anchor - 1].operand = null;
            code[anchor].opcode = OpCodes.Call;
            code[anchor].operand = typeof(NativeCacheBuffer).GetMethod("Upload", Static);
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
                    postfix: new HarmonyMethod(typeof(NativeCacheBuffer)
                        .GetMethod("AfterFinish", Static)));
                Log("installed; staged cache reads upload by pointer, budget=" +
                    (BudgetBytes() / 1048576) + "MiB reuse=" + (IdleBytesCap() / 1048576) +
                    "MiB filecap=" + (FileCapBytes() / 1048576) + "MiB");
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
            var blocks = new List<Block>();
            lock (Sync)
            {
                for (int i = 0; i < Stages.Count; i++) blocks.Add(Stages[i].block);
                Stages.Clear();
                foreach (var pair in Idle)
                    while (pair.Value.Count > 0) blocks.Add(pair.Value.Pop());
                Idle.Clear();
                _idleBytes = 0;
            }
            for (int i = 0; i < blocks.Count; i++)
            {
                Interlocked.Add(ref _liveBytes, -blocks[i].capacity);
                IntPtr ptr = blocks[i].ptr;
                blocks[i].ptr = IntPtr.Zero;
                if (ptr != IntPtr.Zero) VirtualFree(ptr, UIntPtr.Zero, MEM_RELEASE);
            }
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[native-cache] " + message);
        }
    }
}
