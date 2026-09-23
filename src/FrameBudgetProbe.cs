using System.Collections.Generic;
using System.Text;

namespace Quest3TriggerUI
{
    /// <summary>
    /// 只读帧预算归因：把当前帧时间拆到「进程 CPU / 主线程 CPU / 最忙非主线程 CPU」
    /// 以及插件自身 Update 成本上，回答「这些毫秒到底花在哪」。
    ///
    /// 采样期之外的开销：MarkUpdateStart/MarkUpdateEnd 只做一次 bool 判断。
    /// 采样期每秒枚举一次进程线程 CPU 时间（Process.Threads + TotalProcessorTime）。
    /// 不修改任何游戏状态，也不写回任何配置。
    /// </summary>
    internal static class FrameBudgetProbe
    {
        private static readonly Dictionary<int, double> _prev = new Dictionary<int, double>();
        private static readonly Dictionary<int, double> _cur = new Dictionary<int, double>();
        private static System.Diagnostics.Process _proc;
        private static double _procBase;
        private static int _mainTid;
        private static long _freq;
        private static double _updateMs;
        private static int _updateCalls;
        private static bool _on;
        private static bool _warned;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        internal static void BeginRun()
        {
            _on = true;
            _procBase = 0.0;
            _prev.Clear();
            _cur.Clear();
            _updateMs = 0.0;
            _updateCalls = 0;
            if (_freq == 0L) _freq = System.Diagnostics.Stopwatch.Frequency;
            if (_mainTid == 0)
            {
                try { _mainTid = (int)GetCurrentThreadId(); }
                catch { }
            }
            if (_proc == null)
            {
                try { _proc = System.Diagnostics.Process.GetCurrentProcess(); }
                catch { }
            }
        }

        internal static void End()
        {
            _on = false;
        }

        internal static long MarkUpdateStart()
        {
            if (!_on) return 0L;
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        internal static void MarkUpdateEnd(long t0)
        {
            if (!_on || t0 == 0L || _freq == 0L) return;
            long d = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (d < 0L) return;
            double ms = d * 1000.0 / (double)_freq;
            if (ms < 0.0 || ms > 1000.0) return;
            _updateMs += ms;
            _updateCalls++;
        }

        internal static string SampleLine(int frames)
        {
            if (!_on) return "";
            var sb = new StringBuilder(200);
            try
            {
                if (_freq == 0L) _freq = System.Diagnostics.Stopwatch.Frequency;
                if (_mainTid == 0) _mainTid = (int)GetCurrentThreadId();
                if (_proc == null) _proc = System.Diagnostics.Process.GetCurrentProcess();
                _proc.Refresh();
                double procNow = _proc.TotalProcessorTime.TotalMilliseconds;

                _cur.Clear();
                foreach (System.Diagnostics.ProcessThread pt in _proc.Threads)
                {
                    double v;
                    try { v = pt.TotalProcessorTime.TotalMilliseconds; }
                    catch { continue; }
                    _cur[pt.Id] = v;
                }

                sb.Append(" | CPU: ");
                if (_procBase <= 0.0)
                {
                    sb.Append("建立基线");
                }
                else
                {
                    double procMs = procNow - _procBase;
                    double mainMs = -1.0;
                    double bestMs = 0.0;
                    int bestTid = 0;
                    int seen = 0;
                    foreach (KeyValuePair<int, double> kv in _cur)
                    {
                        double pv;
                        if (!_prev.TryGetValue(kv.Key, out pv)) continue;
                        double d = kv.Value - pv;
                        if (d < 0.0) d = 0.0;
                        seen++;
                        if (kv.Key == _mainTid) mainMs = d;
                        else if (d > bestMs) { bestMs = d; bestTid = kv.Key; }
                    }
                    sb.Append("进程=").Append(procMs.ToString("0")).Append("ms/s");
                    if (frames > 0)
                        sb.Append("(").Append((procMs / frames).ToString("0.0")).Append("ms/帧)");
                    sb.Append(" 主线程=");
                    if (mainMs >= 0.0)
                    {
                        sb.Append(mainMs.ToString("0")).Append("ms/s");
                        if (frames > 0)
                            sb.Append("(").Append((mainMs / frames).ToString("0.0")).Append("ms/帧)");
                    }
                    else sb.Append("未匹配");
                    if (bestMs > 0.0)
                    {
                        sb.Append(" 次忙线程=tid").Append(bestTid).Append("(").Append(bestMs.ToString("0")).Append("ms/s)");
                    }
                    sb.Append(" 线程数=").Append(seen);
                }

                double upd = _updateCalls > 0 ? _updateMs / _updateCalls : 0.0;
                if (_updateCalls > 0)
                    sb.Append(" 插件Update=").Append(upd.ToString("0.00")).Append("ms/帧(calls=").Append(_updateCalls).Append(")");
                _updateMs = 0.0;
                _updateCalls = 0;

                _procBase = procNow;
                _prev.Clear();
                foreach (KeyValuePair<int, double> kv in _cur) _prev[kv.Key] = kv.Value;
                return sb.ToString();
            }
            catch (System.Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    if (Quest3TriggerUIPlugin.Log != null)
                        Quest3TriggerUIPlugin.Log.LogWarning("[FrameBudget] 线程CPU采样不可用: " + e.GetType().Name + " " + e.Message);
                }
                return " | CPU: 不可用";
            }
        }
    }
}