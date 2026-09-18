using System.Diagnostics;
using System.Threading;

namespace Quest3TriggerUI
{
    internal sealed class StandbyFrameLimiter
    {
        private readonly int _framesPerSecond;
        private long _nextFrameTicks;

        internal StandbyFrameLimiter(int framesPerSecond)
        {
            _framesPerSecond = framesPerSecond;
        }

        internal void Reset()
        {
            _nextFrameTicks = 0;
        }

        internal void WaitForNextFrame()
        {
            long now = Stopwatch.GetTimestamp();
            long interval = Stopwatch.Frequency / _framesPerSecond;
            if (_nextFrameTicks == 0)
            {
                _nextFrameTicks = now + interval;
                return;
            }

            long remaining = _nextFrameTicks - now;
            if (remaining > 0)
            {
                int milliseconds = (int)(remaining * 1000L / Stopwatch.Frequency);
                if (milliseconds > 0)
                    Thread.Sleep(milliseconds);
            }
            _nextFrameTicks = Stopwatch.GetTimestamp() + interval;
        }
    }
}
