using System;
using System.IO;
using System.Text;

namespace Quest3TriggerUI
{
    internal sealed class BufferedChars
    {
        internal const int Capacity = 16384;
        private readonly TextReader reader;
        private readonly char[] buffer = new char[Capacity];
        private int offset, length;
        private bool ended;
        internal bool Failed;

        internal BufferedChars(TextReader reader) { this.reader = reader; }

        internal int Next()
        {
            if (ended) return -1;
            if (offset == length)
            {
                try { length = reader.Read(buffer, 0, buffer.Length); }
                catch { Failed = true; throw; }
                offset = 0;
                if (length == 0) { ended = true; return -1; }
            }
            return buffer[offset++];
        }

        internal string UnicodeDigits()
        {
            var chars = new char[4];
            for (int i = 0; i < chars.Length; i++)
            {
                int next = Next();
                // Invoke the original Substring failure for a truncated \u.
                if (next < 0) return string.Empty.Substring(0, 4);
                chars[i] = (char)next;
            }
            return new string(chars);
        }

        internal void Drain()
        {
            if (ended) return;
            offset = length;
            while (reader.Read(buffer, 0, buffer.Length) != 0) { }
            ended = true;
        }
    }

    // The scene loader must finish file IO before its path-replacement stage.
    // A raw UTF-16-code-unit spool preserves lone surrogates as well as ordering;
    // an Encoding-based temporary writer would silently change malformed input.
    internal sealed class Utf16Spool : IDisposable
    {
        private readonly FileStream stream;
        private readonly char[] chars = new char[BufferedChars.Capacity];
        private readonly byte[] bytes = new byte[BufferedChars.Capacity * 2];
        internal long Length { get { return stream.Length / 2; } }
        internal readonly string Path;

        internal Utf16Spool()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vam-json-" + Guid.NewGuid().ToString("N") + ".tmp");
            stream = new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 32768,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        }

        internal void CopyFrom(TextReader reader)
        {
            int count;
            while ((count = reader.Read(chars, 0, chars.Length)) > 0) Write(chars, count);
        }

        private void Write(char[] value, int count)
        {
            Buffer.BlockCopy(value, 0, bytes, 0, count * 2);
            stream.Write(bytes, 0, count * 2);
        }

        internal void Write(string value)
        {
            for (int offset = 0; offset < value.Length;)
            {
                int count = Math.Min(chars.Length, value.Length - offset);
                value.CopyTo(offset, chars, 0, count);
                Write(chars, count);
                offset += count;
            }
        }

        internal TextReader Rewind()
        {
            stream.Position = 0;
            return new RawReader(this);
        }

        public void Dispose() { stream.Dispose(); }

        private sealed class RawReader : TextReader
        {
            private readonly Utf16Spool owner;
            internal RawReader(Utf16Spool owner) { this.owner = owner; }

            public override int Read()
            {
                int low = owner.stream.ReadByte();
                if (low < 0) return -1;
                int high = owner.stream.ReadByte();
                if (high < 0) throw new InvalidDataException("Truncated raw JSON spool");
                return low | (high << 8);
            }

            public override string ReadToEnd()
            {
                var result = new StringBuilder();
                int read;
                while ((read = Read(owner.chars, 0, owner.chars.Length)) > 0) result.Append(owner.chars, 0, read);
                return result.ToString();
            }

            public override int Read(char[] buffer, int index, int count)
            {
                int requested = Math.Min(count, owner.bytes.Length / 2) * 2;
                int read = 0;
                while (read < requested)
                {
                    int take = owner.stream.Read(owner.bytes, read, requested - read);
                    if (take == 0) break;
                    read += take;
                }
                if ((read & 1) != 0) throw new InvalidDataException("Truncated raw JSON spool");
                Buffer.BlockCopy(owner.bytes, 0, buffer, index * 2, read);
                return read / 2;
            }
        }
    }
}
