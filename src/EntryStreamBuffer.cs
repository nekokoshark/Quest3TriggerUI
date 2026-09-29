using System;
using System.IO;

namespace Quest3TriggerUI
{
    // 换人时每个 item 的 .vab 是 BinaryReader 按 4 个字节要一次：SharpZipLib 的
    // ZipFile/PartialInputStream 每次 Read 都要 Seek 一次底层 FileStream，实测
    // 这条路只有 1.43 MB/s（[item-build] 六项落在同一条直线上，见
    // 问题与证据索引 §14.44/§14.45）。这里给它加一层读前缓冲。
    //
    // **故意不用 System.IO.BufferedStream**：游戏内 Mono 的 BufferedStream 会在
    // EOF 附近对底层做 Position/Seek 操作，而 PartialInputStream 的
    // set_Position 在 target >= end_ 时抛 InvalidOperationException(
    // "Cannot seek past end")（IL：工作区\liveset_20260928\il_sharpzip.txt L394-418）。
    // 实测就是它把金瓶儿的衣服/头发加载打断的：BepInEx\LogOutput.log L38370
    // `wrapped4B type=BufferedStream len=16429 total=16429 ms=0 err=InvalidOperationException:Cannot seek past end`
    // —— 数据其实已经全读出来了（ms=0，快得离谱），错在最后那一下越界定位。
    //
    // 这个包装只做三件事，字节语义与原生完全一致：
    //   1. 顺序读前缓冲：每次填充只请求 Length - Position 以内的字节数，不越界；
    //   2. 消费方 Seek/Position 一律转交底层（先丢弃缓冲、并按 Current 语义回退偏移），
    //      所以即便上层真的定位也不会看到假的 EOF；
    //   3. 只读（Write/SetLength 抛 NotSupportedException，与 PartialInputStream 一致）。
    internal sealed class EntryStreamBuffer : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _buffer;
        private int _filled;
        private int _served;
        private bool _eof;

        internal EntryStreamBuffer(Stream inner, int bufferBytes)
        {
            _inner = inner;
            _buffer = new byte[bufferBytes < 4096 ? 4096 : bufferBytes];
        }

        internal Stream Inner
        {
            get { return _inner; }
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return _inner.CanSeek; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override long Length
        {
            get { return _inner.Length; }
        }

        public override long Position
        {
            get { return _inner.Position - (_filled - _served); }
            set { Seek(value, SeekOrigin.Begin); }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] array, int offset, int count)
        {
            if (count <= 0) return 0;
            int pending = _filled - _served;
            if (pending == 0)
            {
                if (!Fill()) return 0;
                pending = _filled - _served;
            }
            if (count <= pending)
            {
                Buffer.BlockCopy(_buffer, _served, array, offset, count);
                _served += count;
                return count;
            }
            Buffer.BlockCopy(_buffer, _served, array, offset, pending);
            _served = _filled;
            int direct = _inner.Read(array, offset + pending, count - pending);
            return direct > 0 ? pending + direct : pending;
        }

        public override int ReadByte()
        {
            if (_served >= _filled && !Fill()) return -1;
            return _buffer[_served++];
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin == SeekOrigin.Current) offset -= _filled - _served;
            _filled = 0;
            _served = 0;
            _eof = false;
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] array, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _filled = 0;
                _served = 0;
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        // 填缓冲：直接向底层要一整块。可以放心这么做的原因是 PartialInputStream.Read
        // 自己就把 count 夹在 end_ - readPos_ 之内（IL：il_sharpzip.txt L273-292），
        // 到尾了就老实返回 0——所以"读 64KiB"永远不会越界，也就永远碰不到 set_Position。
        // 反过来（用 Length-Position 自己算剩余）更危险：曾有流报出 Length=0/Position=0
        // 却仍有数据可读，那样会被误判成 EOF（离线对照里就复现过一整块读不出来的情况）。
        private bool Fill()
        {
            _filled = 0;
            _served = 0;
            if (_eof) return false;
            int n = _inner.Read(_buffer, 0, _buffer.Length);
            if (n <= 0)
            {
                _eof = true;
                return false;
            }
            _filled = n;
            return true;
        }    }
}
