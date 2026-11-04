using System.ComponentModel;
using System.Diagnostics;

/// <summary>Getting iRacing to show the replay, and moving around in it.</summary>
public static class Replay
{
    /// <summary>
    /// Connects to iRacing with the replay open, opening the .rpy if the sim isn't running. Refuses a live session:
    /// someone is driving, and driving the cameras (and a heavy capture) is the last thing they need.
    /// </summary>
    public static RaceInfo Open(LiveTelemetry t, string rpy, long? subSessionId = null, CancellationToken ct = default)
    {
        if (Connected(t, 3000) is RaceInfo live)
        {
            if (!live.SimMode.Equals("replay", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("iRacing is in a live session (SimMode: " + live.SimMode + "). Finish racing, then open the replay instead.");
            Console.WriteLine($"iRacing has a replay open: {live.Track}, subsession {live.SubSessionId}.");
            // Otherwise the video would be of one race and named after another.
            if (subSessionId is long want && live.SubSessionId != 0 && live.SubSessionId != want)
                throw new InvalidOperationException($"iRacing has subsession {live.SubSessionId} open, not {want}. Open {Path.GetFileName(rpy)} in iRacing, or pick that replay here.");
            return live;
        }

        // Without a file association the shell would show an "open with" dialog instead, so only try when there is one.
        using var assoc = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(".rpy");
        if (assoc?.GetValue(null) is string { Length: > 0 })
        {
            Console.WriteLine($"Opening {Path.GetFileName(rpy)} in iRacing...");
            try { Process.Start(new ProcessStartInfo(rpy) { UseShellExecute = true }); }
            catch (Win32Exception) { Console.WriteLine("  Windows couldn't open it."); }
        }
        Console.WriteLine($"  Open this replay in iRacing (Replays, or Watch Replay on the race result): {rpy}");
        Console.WriteLine("  Waiting up to 10 minutes...");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(10))
        {
            ct.ThrowIfCancellationRequested();
            if (Connected(t, 2000) is RaceInfo info)
            {
                if (!info.SimMode.Equals("replay", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("iRacing opened a live session, not a replay.");
                Console.WriteLine($"Connected: {info.Track}, subsession {info.SubSessionId}.");
                if (subSessionId is long want && info.SubSessionId != 0 && info.SubSessionId != want)
                    throw new InvalidOperationException($"iRacing opened subsession {info.SubSessionId}, not {want}.");
                return info;
            }
        }
        throw new TimeoutException("iRacing didn't open the replay within 10 minutes.");
    }

    /// <summary>
    /// The replay has stopped moving. At its end that's that (false); anywhere else it was paused (a click in the sim, a
    /// loading hitch), so it's set playing again (true).
    /// </summary>
    public static bool PlayOn(LiveTelemetry t, Stopwatch stalled, int speed, ref int resumes)
    {
        int framesLeft = t.Int("ReplayFrameNumEnd", 0, -1);
        if (framesLeft >= 0 && framesLeft < 120) return false;
        if (++resumes > 20) throw new InvalidOperationException("The replay keeps stopping before its end. Leave iRacing alone while it plays, and try again.");
        Console.WriteLine($"  The replay stopped playing before its end ({framesLeft / 60.0:0} s left); playing on.");
        Sim.ReplaySpeed(speed);
        stalled.Restart();
        return true;
    }

    static RaceInfo? Connected(LiveTelemetry t, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
            if (t.WaitForFrame(500) && t.SessionInfo.Length > 0)
            {
                var info = RaceInfo.Parse(t.SessionInfo);
                if (info.Drivers.Count > 0) return info;
            }
        return null;
    }

    /// <summary>Jumps to a moment of a session and pauses there, waiting until the sim has actually arrived.</summary>
    public static void SeekAndPause(LiveTelemetry t, int sessionNum, double sessionTime, CancellationToken ct = default)
    {
        Sim.ReplaySpeed(0);
        var sw = Stopwatch.StartNew();
        var resend = Stopwatch.StartNew();
        Sim.SeekSessionTime(sessionNum, sessionTime);
        double lastT = double.NaN;
        var steady = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            ct.ThrowIfCancellationRequested();
            if (!t.WaitForFrame(500)) continue;
            double now = t.Get("SessionTime");
            if (now != lastT) { lastT = now; steady.Restart(); }
            bool there = Math.Abs(now - sessionTime) < 1.5;
            // A moment before the replay's first frame lands on its first frame: arrived once it sits still there.
            bool settled = sw.Elapsed > TimeSpan.FromSeconds(4) && steady.Elapsed > TimeSpan.FromSeconds(2);
            if (t.Int("SessionNum") == sessionNum && (there || settled))
            {
                Thread.Sleep(1500); // let the world and the camera settle
                return;
            }
            if (resend.Elapsed > TimeSpan.FromSeconds(8))
            {
                Sim.SeekSessionTime(sessionNum, sessionTime);
                resend.Restart();
            }
        }
        throw new TimeoutException($"The replay didn't reach session {sessionNum} at {sessionTime:0}s.");
    }
}
