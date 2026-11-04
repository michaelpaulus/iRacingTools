/// <summary>A car taking the chequered flag, at SessionTime T.</summary>
public sealed record Finisher(int CarIdx, double T);

/// <summary>
/// Who has taken the chequered flag. Every car running when the flag falls is waited for: it's done when it next crosses
/// the line, leaves the world, or parks (pit stall or stopped on track). Used by the scan to know when to stop, and by
/// the detector so the video runs until the whole field is home, not just the winner.
/// </summary>
public sealed class FinishWatch
{
    const double ParkedSeconds = 30, ParkedPct = 0.0005, JustCrossedPct = 0.03;

    readonly Dictionary<int, int> _lapsAtFlag = new();
    readonly Dictionary<int, (float Pct, double T)> _moved = new();
    readonly HashSet<int> _done = new();

    public double FlagT { get; private set; } = double.NaN;
    /// <summary>When the last car crossed the line; NaN before the flag.</summary>
    public double LastFinishT { get; private set; } = double.NaN;
    public List<Finisher> Finishers { get; } = new();
    public bool AllDone => !double.IsNaN(FlagT) && _done.Count == _lapsAtFlag.Count;

    public void Update(Frame f)
    {
        if (double.IsNaN(FlagT))
        {
            if (f.State < Frame.StateCheckered) return;
            FlagT = LastFinishT = f.T;
            for (int i = 0; i < f.Cars.Length; i++)
            {
                var c = f.Cars[i];
                if (!c.InWorld || c.LapCompleted < 0) continue;
                _lapsAtFlag[i] = c.LapCompleted;
                _moved[i] = (c.Pct, f.T);
                // The winner (and anyone crossing with them) has just taken the flag in this very sample.
                if (c.Pct < JustCrossedPct && !c.OnPitRoad) Finish(i, f.T);
            }
            return;
        }
        foreach (var (i, laps) in _lapsAtFlag)
        {
            if (_done.Contains(i) || i >= f.Cars.Length) continue;
            var c = f.Cars[i];
            if (c.LapCompleted > laps) { Finish(i, f.T); continue; }
            if (Math.Abs(c.Pct - _moved[i].Pct) > ParkedPct) _moved[i] = (c.Pct, f.T);
            if (!c.InWorld || f.T - _moved[i].T > ParkedSeconds) _done.Add(i);
        }
    }

    void Finish(int car, double t)
    {
        _done.Add(car);
        Finishers.Add(new Finisher(car, t));
        LastFinishT = t;
    }
}
