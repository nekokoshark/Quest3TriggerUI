using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Quest3TriggerUI
{
    // Owns native storage, not game objects. Every reader has its own lease;
    // one payload is charged once, including while filling or uploading.
    internal sealed partial class TextureStagingArena
    {
        private readonly object gate = new object();
        private readonly Func<long> budget, idleLimit;
        private readonly List<Storage> idle = new List<Storage>(64);
        private long liveBytes, idleBytes;
        private int liveBlocks, leases, generation;
        private bool retired;
        private const int MaxBlocks = 16, MaxIdleBlock = 16 * 1048576;
        private const int IdleMilliseconds = 10000;

        internal sealed class Storage
        {
            internal IntPtr Pointer;
            internal int Capacity, Length, Holders, Generation, ReturnedAt;
            internal bool Raster;
        }

        internal struct State
        {
            internal long LiveBytes, IdleBytes;
            internal int LiveBlocks, Leases;
        }

        internal TextureStagingArena(Func<long> budgetBytes, Func<long> idleBytesLimit)
        { budget = budgetBytes; idleLimit = idleBytesLimit; }

        internal State Snapshot()
        {
            lock (gate) return new State { LiveBytes = liveBytes, IdleBytes = idleBytes,
                LiveBlocks = liveBlocks, Leases = leases };
        }

        internal bool Accepts(Lease lease)
        {
            lock (gate) return !retired && lease.Owner == this && !lease.Closed &&
                lease.Data.Generation == generation;
        }

        internal static int Capacity(int length, bool raster)
        {
            if (length <= 0 || length > (1 << 30)) return 0;
            if (raster || length > MaxIdleBlock)
                return checked((int)(((long)length + 4095) / 4096 * 4096));
            int capacity = 1048576;
            while (capacity < length) capacity <<= 1;
            return capacity;
        }

        internal Lease Rent(int length, bool raster)
        {
            int capacity = Capacity(length, raster);
            if (capacity == 0) return null;
            lock (gate)
            {
                if (retired || liveBlocks >= MaxBlocks || liveBytes + capacity > budget()) return null;
                int index = -1;
                if (!raster)
                    for (int i = idle.Count - 1; i >= 0; i--)
                        if (idle[i].Capacity == capacity) { index = i; break; }
                var block = index < 0 ? new Storage() : idle[index];
                // Allocate ownership metadata before taking storage out of the
                // pool or calling VirtualAlloc. A metadata failure owns no pages.
                var lease = new Lease(this, block);
                if (index >= 0) { idle.RemoveAt(index); idleBytes -= block.Capacity; }
                TrimLocked(capacity, false);
                if (index < 0)
                {
                    block.Pointer = VirtualAlloc(IntPtr.Zero, new UIntPtr((ulong)capacity), 0x3000, 0x04);
                    if (block.Pointer == IntPtr.Zero) return null;
                }
                block.Capacity = capacity; block.Length = length; block.Raster = raster;
                block.Generation = generation; block.Holders = 1;
                liveBytes += capacity; liveBlocks++; leases++;
                lease.Registered = true;
                return lease;
            }
        }

        private Lease Retain(Lease source)
        {
            lock (gate)
            {
                if (retired || source.Closed || source.Data.Generation != generation) return null;
                var lease = new Lease(this, source.Data) { SourcePath = source.SourcePath };
                source.Data.Holders++; leases++; lease.Registered = true;
                return lease;
            }
        }

        private void Return(Lease lease, bool reusable)
        {
            lock (gate)
            {
                if (!lease.Registered) return;
                leases--;
                Storage block = lease.Data;
                if (--block.Holders != 0) return;
                liveBytes -= block.Capacity; liveBlocks--;
                if (reusable && !retired && block.Generation == generation && !block.Raster &&
                    block.Capacity <= MaxIdleBlock && idle.Count < 64 &&
                    idleBytes + block.Capacity <= idleLimit() &&
                    liveBytes + idleBytes + block.Capacity <= budget())
                {
                    block.Length = 0; block.ReturnedAt = Environment.TickCount;
                    idle.Add(block); idleBytes += block.Capacity;
                }
                else Free(block);
            }
        }

        private static void Free(Storage block)
        {
            IntPtr pointer = block.Pointer;
            block.Pointer = IntPtr.Zero;
            if (pointer != IntPtr.Zero) VirtualFree(pointer, UIntPtr.Zero, 0x8000);
        }

        private void TrimLocked(int upcoming, bool all)
        {
            int now = Environment.TickCount;
            for (int i = idle.Count - 1; i >= 0; i--)
            {
                Storage block = idle[i];
                if (!all && !retired && unchecked(now - block.ReturnedAt) < IdleMilliseconds &&
                    idleBytes <= idleLimit() && liveBytes + idleBytes + upcoming <= budget()) continue;
                idle.RemoveAt(i); idleBytes -= block.Capacity; Free(block);
            }
        }

        internal long TrimIdle(bool all)
        {
            lock (gate) { long before = idleBytes; TrimLocked(0, all); return before - idleBytes; }
        }

        internal void Retire()
        {
            lock (gate) { retired = true; generation++; TrimLocked(0, true); }
            // Live leases retain their storage until their actual consumer exits.
        }

        internal void Reactivate() { lock (gate) retired = false; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
    }
}
