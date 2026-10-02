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
    // managed byte[]: large blocks leave the process once the upload has
    // consumed them; small blocks have a short, bounded reuse window. These
    // pages never enter Boehm's free lists. Staging is budget-bounded, and every deviation - small
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
        private static int _liveSlots, _generation, _nextSweep;
        private static bool _retired;
        private const int MaxIdleBlock = 16 * 1048576;
        private const long MaxIdleBytes = 64L * 1048576;
        private const int IdleMilliseconds = 10000;
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
            internal int generation, returnedAt;
            internal bool charged;
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
            return Math.Min(MaxIdleBytes, mib * 1048576L);
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
            Block block = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                long length = info.Length;
                if (length < TextureCacheByteReuse.MinimumBytes() || length > FileCapBytes() ||
                    length > int.MaxValue) return false;
                int size = (int)length;
                block = Rent(size); // Reserves rounded capacity and a slot before I/O.
                if (block == null) return false;
                int read;
                if (!Fill(block, path, size, out read) || read != size)
                {
                    Interlocked.Increment(ref Failures);
                    return false;
                }
                block.length = size;
                block.path = path;
                if (!StageIt(q, block)) return false;
                block = null; // The stage now owns the reservation.
                Interlocked.Increment(ref Staged);
                Interlocked.Add(ref StagedBytes, size);
                return true;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref Failures);
                return false;
            }
            finally { if (block != null) Drop(block); }
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
                try
                {
                    texture.LoadRawTextureData(block.ptr, block.length);
                    Interlocked.Increment(ref Uploaded);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref Failures);
                    q.raw = TextureCacheByteReuse.ReadCachedBytes(block.path, false);
                    texture.LoadRawTextureData(q.raw);
                }
            }
            finally
            {
                Interlocked.Increment(ref Released);
                Drop(block);
            }
        }

        // Runs even when Finish throws; preserve the original exception.
        private static Exception AfterFinish(ImageLoaderThreaded.QueuedImage __instance,
            Exception __exception)
        {
            Release(__instance);
            return __exception;
        }

        private static Exception AfterProcess(ImageLoaderThreaded.QueuedImage __instance,
            Exception __exception)
        {
            // Native Finish guards hadError/finished, not cancel. A cancel flag
            // alone is not permission to discard a payload it may still upload.
            if (__exception != null || (__instance != null && __instance.hadError))
                Release(__instance);
            return __exception;
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

        private static bool StageIt(ImageLoaderThreaded.QueuedImage q, Block block)
        {
            lock (Sync)
            {
                if (_retired || block.generation != _generation || q.cancel || q.finished)
                    return false;
                for (int i = 0; i < Stages.Count; i++)
                    if (ReferenceEquals(Stages[i].image.Target, q)) return false;
                // Rent counts filling, staged and uploading blocks. Never evict a live request.
                Stages.Add(new Stage { image = new WeakReference(q), block = block });
                // Publish pointer ownership and detach raw under the same lock
                // used by Shutdown. The caller must not clear raw afterwards.
                q.raw = null;
                return true;
            }
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
            lock (Sync)
            {
                if (!block.charged) return;
                block.charged = false;
                _liveBytes -= block.capacity;
                _liveSlots--;
                if (!_retired && block.generation == _generation && block.ptr != IntPtr.Zero &&
                    block.capacity <= MaxIdleBlock && _idleBytes + block.capacity <= IdleBytesCap())
                {
                    Stack<Block> idle;
                    if (!Idle.TryGetValue(block.capacity, out idle))
                        Idle[block.capacity] = idle = new Stack<Block>();
                    block.length = 0;
                    block.path = null;
                    block.returnedAt = Environment.TickCount;
                    idle.Push(block);
                    _idleBytes += block.capacity;
                    return;
                }
            }
            Free(block);
        }

        private static void Free(Block block)
        {
            IntPtr ptr = block.ptr;
            block.ptr = IntPtr.Zero;
            if (ptr != IntPtr.Zero) VirtualFree(ptr, UIntPtr.Zero, MEM_RELEASE);
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
                if (_retired || _liveSlots >= MaxStages || _liveBytes + capacity > BudgetBytes())
                    return null;
                Block block = null;
                Stack<Block> idle;
                if (Idle.TryGetValue(capacity, out idle) && idle.Count > 0)
                {
                    block = idle.Pop();
                    _idleBytes -= block.capacity;
                    if (idle.Count == 0) Idle.Remove(capacity);
                }
                // Idle committed pages are included in the allocation budget too.
                TrimIdleLocked(capacity);
                if (block == null)
                {
                    IntPtr ptr = VirtualAlloc(IntPtr.Zero, new UIntPtr((ulong)capacity),
                        MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    if (ptr == IntPtr.Zero) return null;
                    block = new Block { ptr = ptr, capacity = capacity };
                }
                block.charged = true;
                block.generation = _generation;
                _liveBytes += capacity;
                _liveSlots++;
                return block;
            }
        }

        // Caller holds Sync. Small bounded idle pool; no Unity object enumeration.
        private static void TrimIdleLocked(int upcoming)
        {
            int now = Environment.TickCount;
            var empty = new List<int>();
            foreach (var pair in Idle)
            {
                Block[] blocks = pair.Value.ToArray();
                pair.Value.Clear();
                foreach (Block block in blocks)
                {
                    if (_retired || unchecked(now - block.returnedAt) >= IdleMilliseconds ||
                        _idleBytes > IdleBytesCap() || _liveBytes + _idleBytes + upcoming > BudgetBytes())
                    {
                        _idleBytes -= block.capacity;
                        Free(block);
                    }
                    else pair.Value.Push(block);
                }
                if (pair.Value.Count == 0) empty.Add(pair.Key);
            }
            foreach (int key in empty) Idle.Remove(key);
        }

        internal static void SweepIdle()
        {
            int now = Environment.TickCount;
            lock (Sync)
            {
                if (_nextSweep != 0 && unchecked(now - _nextSweep) < 0) return;
                _nextSweep = now + 1000;
                for (int i = Stages.Count - 1; i >= 0; i--)
                {
                    var q = Stages[i].image.Target as ImageLoaderThreaded.QueuedImage;
                    if (q != null && !q.finished) continue;
                    Block block = Stages[i].block;
                    Stages.RemoveAt(i);
                    Drop(block);
                }
                TrimIdleLocked(0);
            }
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
                    finalizer: new HarmonyMethod(typeof(NativeCacheBuffer)
                        .GetMethod("AfterFinish", Static)));
                _harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Process", All),
                    finalizer: new HarmonyMethod(typeof(NativeCacheBuffer).GetMethod("AfterProcess", Static)));
                lock (Sync) { _retired = false; }
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
            lock (Sync)
            {
                _retired = true;
                _generation++;
                // Future Finish calls are unpatched. Preserve live staged payloads
                // before releasing pointers; filling workers own their blocks until finally.
                for (int i = Stages.Count - 1; i >= 0; i--)
                {
                    Stage stage = Stages[i];
                    var q = stage.image.Target as ImageLoaderThreaded.QueuedImage;
                    if (q != null && !q.hadError && !q.finished && q.raw == null)
                    {
                        var raw = new byte[stage.block.length];
                        Marshal.Copy(stage.block.ptr, raw, 0, raw.Length);
                        q.raw = raw;
                    }
                    Stages.RemoveAt(i);
                    Drop(stage.block);
                }
                TrimIdleLocked(0); // Idle blocks are not live reservations.
            }
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[native-cache] " + message);
        }
    }
}
