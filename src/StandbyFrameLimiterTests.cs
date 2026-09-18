using System;
using System.Diagnostics;

internal static class StandbyFrameLimiterTests
{
    public static int Main()
    {
        Quest3TriggerUI.StandbyFrameLimiter limiter =
            new Quest3TriggerUI.StandbyFrameLimiter(5);
        Stopwatch timer = Stopwatch.StartNew();
        for (int i = 0; i < 6; i++)
            limiter.WaitForNextFrame();
        timer.Stop();

        double seconds = timer.Elapsed.TotalSeconds;
        bool pass = seconds >= 0.85 && seconds <= 1.40;
        Console.WriteLine(
            "STANDBY LIMITER RESULT=" + (pass ? "PASS" : "FAIL") +
            "; TargetFPS=5; SixTicksMilliseconds=" + timer.ElapsedMilliseconds +
            "; MainThreadSleep=True; Exit=" + (pass ? "0" : "1"));
        return pass ? 0 : 1;
    }
}
