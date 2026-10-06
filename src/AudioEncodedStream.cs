using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using MVR.FileManagement;

namespace Quest3TriggerUI
{
    // Only the encoded VAR-to-native copy changes. The existing importer owns
    // a successfully delivered HGlobal, exactly as it did before this patch.
    internal static class AudioEncodedStream
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private const int BufferBytes = 32768;
        private static readonly MethodInfo Target = typeof(URLAudioClipManager).GetMethod("LoadFileIntoByteArray", All);
        private static readonly Action<Stream, Stream, byte[]> Copy = (Action<Stream, Stream, byte[]>)Delegate.CreateDelegate(
            typeof(Action<Stream, Stream, byte[]>), Type.GetType("ICSharpCode.SharpZipLib.Core.StreamUtils, ICSharpCode.SharpZipLib", true)
                .GetMethod("Copy", new[] { typeof(Stream), typeof(Stream), typeof(byte[]) }));
        private static Harmony harmony;

        private static bool Selected()
        {
            var patches = Harmony.GetPatchInfo(Target);
            if (patches == null) return false;
            MethodInfo newest = null;
            foreach (var patch in patches.Prefixes)
            {
                var method = patch.PatchMethod;
                if (method.Name == "BeforeRead" && method.DeclaringType != null && method.DeclaringType.Name == "AudioEncodedStream") newest = method;
            }
            return newest == typeof(AudioEncodedStream).GetMethod("BeforeRead", All);
        }

        private static bool BeforeRead(FileEntry fe, ref IntPtr byteArray)
        {
            // Harmony 2.0 calls every bool prefix even after an earlier false.
            // Only the last installed generation opens/copies this file.
            if (!Selected() || fe == null) return true;
            long size = fe.Size;
            if (size < 0 || size > int.MaxValue) return true;
            IntPtr pending = IntPtr.Zero;
            try
            {
                var buffer = new byte[BufferBytes];
                using (var source = FileManager.OpenStream(fe))
                {
                    pending = Marshal.AllocHGlobal((int)size);
                    using (var destination = new NativeDestination(pending, (int)size))
                    {
                        Copy(source.Stream, destination, buffer);
                        // The original fixed MemoryStream wraps a zeroed array.
                        // A truncated entry must therefore have the same zero tail.
                        destination.ZeroTail(buffer);
                    }
                }
                // Dispose is part of the read transaction. No pointer escapes
                // a failed open/read/copy/zero-fill/dispose operation.
                byteArray = pending;
                pending = IntPtr.Zero;
                return false;
            }
            finally
            {
                if (pending != IntPtr.Zero) Marshal.FreeHGlobal(pending);
            }
        }

        private sealed class NativeDestination : Stream
        {
            private readonly IntPtr address;
            private readonly int capacity;
            private int written;

            internal NativeDestination(IntPtr address, int capacity) { this.address = address; this.capacity = capacity; }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return capacity; } }
            public override long Position { get { return written; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0) throw new ArgumentOutOfRangeException("offset");
                if (count < 0) throw new ArgumentOutOfRangeException("count");
                if (offset > buffer.Length - count) throw new ArgumentException("Offset and length were out of bounds for the array.");
                // Preserve the fixed, non-expandable destination's failure.
                // Reject the entire oversized write before copying any bytes.
                if (count > capacity - written) throw new NotSupportedException("Cannot expand this MemoryStream");
                if (count != 0) Marshal.Copy(buffer, offset, new IntPtr(address.ToInt64() + written), count);
                written += count;
            }

            internal void ZeroTail(byte[] buffer)
            {
                if (written == capacity) return;
                Array.Clear(buffer, 0, buffer.Length);
                while (written < capacity) Write(buffer, 0, Math.Min(buffer.Length, capacity - written));
            }
        }

        internal static void Install()
        {
            if (harmony != null) return;
            if (Target == null || Target.ReturnType != typeof(void)) throw new MissingMethodException("VAR audio reader changed");
            var parameters = Target.GetParameters();
            if (parameters.Length != 2 || parameters[0].ParameterType != typeof(FileEntry)
                || parameters[1].ParameterType != typeof(IntPtr).MakeByRefType()) throw new MissingMethodException("VAR audio reader signature changed");
            harmony = new Harmony("quest3triggerui.audioencodedstream." + typeof(AudioEncodedStream).Namespace);
            try
            {
                harmony.Patch(Target, prefix: new HarmonyMethod(typeof(AudioEncodedStream), "BeforeRead"));
                if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[audio-encoded-stream] installed bufferBytes="
                    + BufferBytes + " encodedWholeManagedArray=False shortTail=zero nativeOwner=originalImporter decoderPCM=unchanged");
            }
            catch { harmony.UnpatchAll(harmony.Id); harmony = null; throw; }
        }

        internal static void Shutdown()
        {
            if (harmony != null) harmony.UnpatchAll(harmony.Id);
            harmony = null;
            // Calls already executing own only local state. No transferred
            // HGlobal, stream or worker is retained or freed by hot shutdown.
        }
    }
}
