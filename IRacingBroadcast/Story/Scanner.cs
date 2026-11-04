using System.Diagnostics;

/// <summary>
/// Pass 1: plays the race at high speed and records the whole field from the live SDK (an .ibt has only your own car),
/// then reads the final results from the session info.
/// </summary>
public static class Scanner
{
    public static (List<Frame> Frames, RaceInfo Info) Run(LiveTelemetry t, RaceInfo info, BroadcastConfig cfg, CancellationToken ct = default)
    {
        if (info.RaceSessionNum < 0) throw new InvalidOperationException("This replay has no race session.");
        int session = info.RaceSessionNum;
        int cars = Math.Min(FrameFile.MaxCars, info.Drivers.Max(d => d.CarIdx) + 1);
        Console.WriteLine($"Scanning the race at {cfg.ScanSpeed}x ({info.Drivers.Count} cars)...");

        Replay.SeekAndPause(t, session, 0, ct);
        Sim.ReplaySpeed(cfg.ScanSpeed);
        using var pause = ct.Register(() => Sim.ReplaySpeed(0));

        var frames = new List<Frame>();
        var finish = new FinishWatch();
        double lastT = double.NegativeInfinity;
        var stalled = Stopwatch.StartNew();
        int resumes = 0;
        var progress = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!t.WaitForFrame(1000))
            {
                if (!t.IsConnected) throw new InvalidOperationException("Lost the connection to iRacing during the scan.");
                if (stalled.Elapsed > TimeSpan.FromSeconds(6) && !Replay.PlayOn(t, stalled, cfg.ScanSpeed, ref resumes)) break;
                continue;
            }
            if (t.Int("SessionNum") != session) { if (frames.Count > 0) break; continue; }
            var f = FrameFile.Capture(t, cars);
            if (f.T <= lastT)
            {
                if (stalled.Elapsed > TimeSpan.FromSeconds(6) && !Replay.PlayOn(t, stalled, cfg.ScanSpeed, ref resumes)) break;
                continue;
            }
            stalled.Restart();
            lastT = f.T;
            frames.Add(f);
            finish.Update(f);
            // Everyone still running has taken the flag (plus the post-roll), or the stragglers have had 15 minutes.
            if (finish.AllDone && f.T - finish.LastFinishT > cfg.PostRoll + 10) break;
            if (!double.IsNaN(finish.FlagT) && f.T - finish.FlagT > 900) break;
            if (progress.Elapsed > TimeSpan.FromSeconds(5))
            {
                progress.Restart();
                int lead = f.Cars.Select((c, i) => (c, i)).Where(x => x.c.Position == 1).Select(x => x.c.LapCompleted).FirstOrDefault();
                Console.WriteLine($"  {TimeSpan.FromSeconds(f.T):h\\:mm\\:ss} race time, leader lap {lead}" +
                    (double.IsNaN(finish.FlagT) ? "" : $", {finish.Finishers.Count} cars have taken the flag"));
            }
        }
        Sim.ReplaySpeed(0);
        if (double.IsNaN(finish.FlagT)) Console.WriteLine("  The replay ends before the chequered flag; the video will end there too.");
        // The session info now carries the final results.
        t.WaitForFrame(1000);
        var final = RaceInfo.Parse(t.SessionInfo);
        if (final.Drivers.Count == 0) final = info;
        Console.WriteLine($"Scan done: {frames.Count} samples, {(frames.Count > 0 ? frames[^1].T - frames[0].T : 0) / 60:0.0} minutes of race.");
        return (frames, final);
    }
}
