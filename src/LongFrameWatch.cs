using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Quest3TriggerUI
{
    // 窗口关掉之后还会再卡一次（任务记录里的"结束后 40s 又卡一次"），场景里
    // 那记 33s 的单帧停顿也是同一种东西：它们都落在任何窗口之外，于是既没有
    // 阶段行也没有停顿采样。这里用一条 200ms 的后台线程常驻盯着"主线程还在
    // 推进吗"，一旦"卡住 → 恢复"就报一行，并附上卡住期间主线程最内层停在
    // 哪个区间（由 SceneLoadAccelerator 的区间前缀维护）。
    internal static class LongFrameWatch
    {
        internal static bool Enabled = true;

        private const int SampleMs = 200;
        private const long FreezeMs = 800;      // 主线程多久没推进就算卡住
        private const long ReportMinMs = 1000;  // 短于这个时长不报
        private const int ReportCap = 400;      // 一次会话最多报多少行

        private static Thread _thread;
        private static long _mainTicks;
        private static long _frozenMaxMs;
        private static long _frozenCpuMs;
        private static bool _frozen;
        private static int _reports;
        private static System.Diagnostics.Process _proc;
        private static long _lastCpuTicks;
        private static readonly Dictionary<string, int> _brackets =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly object _lock = new object();

        internal static void Install()
        {
            if (_thread != null) return;
            try
            {
                Thread watch = new Thread(Loop);
                watch.IsBackground = true;
                watch.Name = "Quest3TriggerUI.longframe";
                watch.Start();
                _thread = watch;
            }
            catch { _thread = null; }
        }

        internal static void Shutdown() { Enabled = false; }

        // 每帧一次，只有这里更新"主线程活着"的时间戳。
        internal static void Tick()
        {
            if (!Enabled) return;
            Interlocked.Exchange(ref _mainTicks, DateTime.UtcNow.Ticks);
        }

        private static void Loop()
        {
            while (true)
            {
                try { Thread.Sleep(SampleMs); } catch { }
                if (!Enabled) { _thread = null; return; }
                try { Sample(); } catch { }
            }
        }

        private static void Sample()
        {
            long tick = Interlocked.Read(ref _mainTicks);
            if (tick == 0) return;
            long gapMs = (DateTime.UtcNow.Ticks - tick) / TimeSpan.TicksPerMillisecond;
            if (gapMs >= FreezeMs)
            {
                _frozen = true;
                if (gapMs > _frozenMaxMs) _frozenMaxMs = gapMs;
                _frozenCpuMs += CpuDeltaMs();
                string bracket = SceneLoadAccelerator.CurrentBracket;
                if (!string.IsNullOrEmpty(bracket))
                {
                    lock (_lock)
                    {
                        int seen;
                        _brackets.TryGetValue(bracket, out seen);
                        _brackets[bracket] = seen + 1;
                    }
                }
                return;
            }
            if (!_frozen) return;
            _frozen = false;
            long frozen = _frozenMaxMs;
            long cpu = _frozenCpuMs;
            _frozenMaxMs = 0;
            _frozenCpuMs = 0;
            List<string> ranked = new List<string>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, int> pair in _brackets)
                    ranked.Add(pair.Value.ToString("D6", CultureInfo.InvariantCulture)
                        + " " + pair.Key);
                _brackets.Clear();
            }
            if (frozen < ReportMinMs) return;
            // 进程基本没吃 CPU：那是暂停/待机，不是卡死，不报。
            if (cpu * 1000L < frozen * 50L) return;
            ranked.Sort();
            StringBuilder sb = new StringBuilder();
            sb.Append("[long-frame] freeze=")
                .Append((frozen / 1000f).ToString("F1", CultureInfo.InvariantCulture))
                .Append("s processCpu=")
                .Append((cpu / 1000f).ToString("F1", CultureInfo.InvariantCulture))
                .Append("s inner=");
            if (ranked.Count == 0) sb.Append("（未挂区间的代码里）");
            int shown = 0;
            for (int i = ranked.Count - 1; i >= 0 && shown < 5; i--, shown++)
            {
                int sp = ranked[i].IndexOf(' ');
                if (shown > 0) sb.Append(", ");
                sb.Append(ranked[i].Substring(sp + 1)).Append("x")
                    .Append(int.Parse(ranked[i].Substring(0, sp),
                        CultureInfo.InvariantCulture));
            }
            string line = sb.ToString();
            _reports++;
            if (_reports > ReportCap) return;
            // 只在实际报出这一行时才复位：否则会把正在进行的合法长操作
            // 的嵌套深度抹掉，后面的归因反而更糊。
            SceneLoadAccelerator.ResetBracketState();
            CharacterLoadTrace.Note("longframe", line);
            try { Quest3TriggerUIPlugin.Log.LogInfo("[帧看门狗] " + line); } catch { }
        }

        private static long CpuDeltaMs()
        {
            try
            {
                if (_proc == null)
                {
                    _proc = System.Diagnostics.Process.GetCurrentProcess();
                    _lastCpuTicks = _proc.TotalProcessorTime.Ticks;
                    return 0;
                }
                long now = _proc.TotalProcessorTime.Ticks;
                long delta = now - _lastCpuTicks;
                _lastCpuTicks = now;
                return delta < 0 ? 0 : delta / TimeSpan.TicksPerMillisecond;
            }
            catch { return 0; }
        }
    }
}
