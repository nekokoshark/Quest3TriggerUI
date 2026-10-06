using System;
using System.Threading;

namespace Quest3TriggerUI
{
    internal sealed partial class TextureStagingArena
    {
        internal sealed class Lease : IDisposable
        {
            internal readonly TextureStagingArena Owner;
            internal readonly Storage Data;
            internal bool Registered;
            internal string SourcePath;
            private int closed;
            internal Lease(TextureStagingArena owner, Storage data) { Owner = owner; Data = data; }
            internal bool Closed { get { return Interlocked.CompareExchange(ref closed, 0, 0) != 0; } }
            internal IntPtr Pointer { get { EnsureOpen(); return Data.Pointer; } }
            internal int Length { get { EnsureOpen(); return Data.Length; } }
            internal Lease Share() { return Owner.Retain(this); }
            private void EnsureOpen()
            { if (Closed || !Registered) throw new ObjectDisposedException("TextureStagingLease"); }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref closed, 1) == 0) Owner.Return(this, true);
                GC.SuppressFinalize(this);
            }
            ~Lease()
            {
                // An abandoned private consumer has no pool-return proof.
                // This is only a safety net; normal paths dispose in finally.
                if (Interlocked.Exchange(ref closed, 1) == 0) Owner.Return(this, false);
            }
        }
    }
}
