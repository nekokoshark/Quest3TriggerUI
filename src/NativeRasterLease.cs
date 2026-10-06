using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Quest3TriggerUI
{
    internal static partial class NativeCacheBuffer
    {
        internal static bool RasterReady { get { lock (Sync) return _harmony != null && !_retired; } }

        // A private build consumer transfers its lease only after publishing.
        // Dispose after transfer no longer owns the published request's pages.
        internal sealed unsafe class RasterLease : IDisposable
        {
            private TextureStagingArena.Lease _lease;
            private readonly IntPtr _pointer;
            internal byte[] managed;
            internal readonly int length;
            // One private worker scope: 0=building, 1=transferred, 2=disposed.
            // Pixel loops retain this scope through Publish/Dispose; no atomic
            // lease accounting or GC calls belong in each pixel operation.
            private int _state;
            private RasterLease(TextureStagingArena.Lease lease, byte[] bytes, int size)
            { _lease = lease; _pointer = lease == null ? IntPtr.Zero : lease.Pointer; managed = bytes; length = size; }

            internal static RasterLease Allocate(int size, bool validation)
            {
                TextureStagingArena.Lease lease = null;
                if (RasterReady && Active() && (validation || size >= TextureCacheByteReuse.MinimumBytes()) && size <= FileCapBytes())
                    lease = Rent(size, true);
                if (lease != null)
                {
                    return new RasterLease(lease, null, size);
                }
                return new RasterLease(null, DecodedBufferPool.RentByteArray(size), size);
            }

            internal byte Read(int index)
            {
                if (_state != 0) throw new ObjectDisposedException("RasterLease");
                if ((uint)index >= (uint)length) throw new IndexOutOfRangeException();
                byte value = managed != null ? managed[index] : ((byte*)_pointer)[index];
                return value;
            }
            internal void Write(int index, byte value)
            {
                if (_state != 0) throw new ObjectDisposedException("RasterLease");
                if ((uint)index >= (uint)length) throw new IndexOutOfRangeException();
                if (managed != null) managed[index] = value; else ((byte*)_pointer)[index] = value;
            }
            internal void CopyFrom(IntPtr source, int count)
            {
                if (_state != 0) throw new ObjectDisposedException("RasterLease");
                if (count < 0 || count > length || source == IntPtr.Zero) throw new ArgumentException("raster copy range");
                if (managed != null) Marshal.Copy(source, managed, 0, count);
                else MoveMemory(_pointer, source, new UIntPtr((uint)count));
                GC.KeepAlive(_lease);
            }
            internal void Publish(ImageLoaderThreaded.QueuedImage q)
            {
                if (_state != 0) throw new ObjectDisposedException("RasterLease");
                if (_lease != null && StageIt(q, _lease))
                {
                    _lease = null;
                    Interlocked.Increment(ref ColdRasterDecode.NativeDecodes);
                    Interlocked.Add(ref ColdRasterDecode.NativeBytes, length);
                }
                else
                {
                    // A cancelled request can still execute original Finish.
                    // A retired filling worker owns its block until this copy.
                    q.raw = managed ?? CopyManaged(_lease);
                    managed = null;
                }
                _state = 1;
            }
            internal void UploadForValidation(UnityEngine.Texture2D texture)
            {
                if (_state != 0) throw new ObjectDisposedException("RasterLease");
                if (_lease == null) throw new InvalidOperationException("native validation allocation rejected");
                texture.LoadRawTextureData(_pointer, length);
                GC.KeepAlive(_lease);
            }
            public void Dispose()
            {
                if (_state == 2) return;
                _state = 2;
                if (_lease != null) { _lease.Dispose(); _lease = null; }
                if (managed != null) { DecodedBufferPool.ReturnArray(managed); managed = null; }
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void MoveMemory(IntPtr destination, IntPtr source, UIntPtr count);

        private static byte[] CopyManaged(TextureStagingArena.Lease lease)
        {
            byte[] bytes = new byte[lease.Length];
            Marshal.Copy(lease.Pointer, bytes, 0, bytes.Length);
            GC.KeepAlive(lease);
            return bytes;
        }

        internal static WeakReference ShareOwner(ImageLoaderThreaded.QueuedImage q)
        {
            lock (Sync)
            {
                if (_retired) return null;
                foreach (Stage stage in Stages)
                    if (ReferenceEquals(stage.image.Target, q)) return new WeakReference(q);
                return null;
            }
        }

        internal static bool TryShare(WeakReference source, ImageLoaderThreaded.QueuedImage q)
        {
            if (source == null || q == null) return false;
            lock (Sync)
            {
                if (_retired || !Active() || Stages.Count >= 256 || q.cancel || q.finished || q.hadError || q.raw != null)
                    return false;
                var owner = source.Target as ImageLoaderThreaded.QueuedImage;
                if (owner == null || owner.finished || owner.hadError) return false;
                foreach (Stage stage in Stages)
                    if (ReferenceEquals(stage.image.Target, q)) return false;
                for (int i = 0; i < Stages.Count; i++)
                {
                    Stage stage = Stages[i];
                    if (!ReferenceEquals(stage.image.Target, owner)) continue;
                    var lease = stage.lease.Share();
                    if (lease == null) return false;
                    try { Stages.Add(new Stage { image = new WeakReference(q), lease = lease }); }
                    catch { lease.Dispose(); throw; }
                    // This publication must be atomic with Shutdown's raw refill.
                    q.raw = null;
                    return true;
                }
                return false;
            }
        }
    }
}
