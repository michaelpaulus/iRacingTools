using System.Globalization;
using System.Text;

public record CornerResult(Corner Corner, CornerStats You, CornerStats Ref, double SecondsLost);

public sealed class LapRecord
{
    public int Lap { get; init; }
    public double LapTime { get; set; } = double.NaN;
    public double FuelAtStart { get; init; }
    public bool Valid { get; set; } = true;
    public List<CornerResult> Corners { get; } = new();
}

/// <summary>
/// Follows the car around the lap, measures each corner once it has been passed
/// and compares it with the same corner on the reference lap.
/// </summary>
public sealed class CornerCoach
{
    readonly ReferenceLap _ref;
    readonly CoachConfig _cfg;
    readonly double _binM;

    LapTrace _trace;
    LapRecord _lap;
    int _nextCorner;
    double _lastPct = -1, _lapStartTime = double.NaN;
    bool _lapValid, _timingKnown;
    bool[] _dirty;

    public List<LapRecord> Laps { get; } = new();
    public LapRecord CurrentLap => _lap;
    public event Action<string>? Callout;

    public CornerCoach(ReferenceLap reference, CoachConfig cfg)
    {
        _ref = reference;
        _cfg = cfg;
        _binM = cfg.TrackLengthKm * 1000 / cfg.DistanceBins;
        _trace = new LapTrace(cfg.DistanceBins);
        _dirty = new bool[cfg.DistanceBins];
        _lap = new LapRecord { Lap = 0 };
    }

    public void OnFrame(ITelemetrySource t)
    {
        double pct = t.Get("LapDistPct"), time = t.Get("SessionTime");
        if (double.IsNaN(pct) || pct < 0) return;

        bool onTrack = t.Get("IsOnTrack") > 0;
        bool inPits = t.Get("OnPitRoad") > 0;

        if (_lastPct >= 0)
        {
            if (_lastPct > 0.9 && pct < 0.1)
            {
                FinishLap(time, t);
            }
            else if (Math.Abs(pct - _lastPct) > 0.02)
            {
                // Reset, tow or teleport mid-lap: the lap time is meaningless, but corners driven from here on still count.
                _lapValid = false;
                _timingKnown = false;
                _trace = new LapTrace(_cfg.DistanceBins);
                _dirty = new bool[_cfg.DistanceBins];
                int at = _trace.BinOf(pct);
                _nextCorner = Math.Max(0, _ref.Corners.FindIndex(c => c.End >= at));
            }
        }
        else
        {
            StartLap(time, t, startedMidLap: pct > 0.02);
        }
        _lastPct = pct;

        int bin = _trace.BinOf(pct);
        if (!onTrack || inPits)
        {
            _lapValid = false;
            _dirty[bin] = true; // corners touching pit road or off-track driving aren't coached
        }

        _trace.Add(pct, time, t.Get("Speed"), t.Get("Brake"), t.Get("Throttle"), t.Get("Gear"));

        while (_nextCorner < _ref.Corners.Count && bin > _ref.Corners[_nextCorner].End + 1)
            Evaluate(_nextCorner++);
    }

    void StartLap(double time, ITelemetrySource t, bool startedMidLap)
    {
        _trace = new LapTrace(_cfg.DistanceBins);
        _dirty = new bool[_cfg.DistanceBins];
        _lap = new LapRecord { Lap = (int)t.Get("Lap"), FuelAtStart = t.Get("FuelLevel") };
        _nextCorner = 0;
        _lapStartTime = time;
        _lapValid = !startedMidLap;
        _timingKnown = !startedMidLap;
    }

    /// <summary>Seconds since this lap started at the line, or null if the lap was joined midway or reset.</summary>
    public double? LapElapsed(double sessionTime) => _timingKnown ? sessionTime - _lapStartTime : null;

    void FinishLap(double time, ITelemetrySource t)
    {
        while (_nextCorner < _ref.Corners.Count) Evaluate(_nextCorner++);
        _lap.Valid = _lapValid;
        if (_lapValid && !double.IsNaN(_lapStartTime)) _lap.LapTime = time - _lapStartTime;
        if (_lap.Corners.Count > 0) Laps.Add(_lap);
        StartLap(time, t, startedMidLap: false);
    }

    void Evaluate(int i)
    {
        var c = _ref.Corners[i];
        var refStats = _ref.Stats[i];
        if (refStats == null || _trace.Coverage(c.Start, c.End) < 0.85) return;
        for (int b = c.Start; b <= c.End; b++) if (_dirty[b]) return;
        var you = CornerMath.Measure(_trace, c, _binM);
        if (you == null) return;

        var result = new CornerResult(c, you, refStats, you.SegmentSeconds - refStats.SegmentSeconds);
        _lap.Corners.Add(result);

        var text = Describe(result);
        if (text != null) Callout?.Invoke(text);
    }

    string? Describe(CornerResult r)
    {
        string name = r.Corner.Name;
        if (r.SecondsLost <= -0.1)
            return _cfg.Praise ? $"{name}. Good, {TimeWords(-r.SecondsLost)} up." : null;

        if (r.SecondsLost < _cfg.MinSecondsLostToCall) return null;
        if (r.SecondsLost > 2.0) return null; // a spin, off or traffic, not a technique problem

        var tip = string.Join(", ", Tips(r).Take(2));
        return $"{name}. {TimeWords(r.SecondsLost)} down. {char.ToUpperInvariant(tip[0])}{tip[1..]}.";
    }

    public static List<string> Tips(CornerResult r)
    {
        var tips = new List<string>();
        var (you, rf) = (r.You, r.Ref);
        if (!double.IsNaN(you.BrakingDecelG) && !double.IsNaN(rf.BrakingDecelG) && rf.BrakingDecelG - you.BrakingDecelG >= 0.15)
            tips.Add("brake harder");
        if (!double.IsNaN(you.ReleaseMBeforeApex) && !double.IsNaN(rf.ReleaseMBeforeApex) && rf.ReleaseMBeforeApex - you.ReleaseMBeforeApex >= 15)
            tips.Add("off the brake earlier");
        if (you.Gear > rf.Gear && rf.Gear > 0) tips.Add($"try {Ordinal(rf.Gear)} gear");
        else if (you.Gear < rf.Gear && you.Gear > 0) tips.Add($"try {Ordinal(rf.Gear)} gear");
        double speedDown = rf.MinKmh - you.MinKmh;
        if (tips.Count == 0) tips.Add(speedDown >= 3 ? "carry more speed through the middle" : "lost it on exit");
        return tips;
    }

    static string TimeWords(double s)
    {
        if (s >= 0.95) return s.ToString("0.0", CultureInfo.InvariantCulture) + " seconds";
        int tenths = Math.Max(1, (int)Math.Round(s * 10));
        return tenths == 1 ? "a tenth" : $"{tenths} tenths";
    }

    static string Ordinal(int g) => g switch { 1 => "first", 2 => "second", 3 => "third", 4 => "fourth", 5 => "fifth", 6 => "sixth", _ => $"gear {g}" };

    /// <summary>Plain-text summary of the session for the race engineer.</summary>
    public string Summary(int maxLaps = 3)
    {
        var sb = new StringBuilder();
        var valid = Laps.Where(l => l.Valid && !double.IsNaN(l.LapTime)).ToList();
        if (valid.Count > 0)
        {
            var best = valid.MinBy(l => l.LapTime)!;
            sb.AppendLine($"Completed valid laps: {valid.Count}. Best {Fmt(best.LapTime)} (lap {best.Lap}). Reference {_ref.Name}: {Fmt(_ref.LapTime)}.");
            sb.AppendLine("Recent laps: " + string.Join("; ", valid.TakeLast(maxLaps).Select(l => $"lap {l.Lap} {Fmt(l.LapTime)} fuel at start {l.FuelAtStart:F1} L")));
        }
        else sb.AppendLine("No complete valid laps yet this session.");

        void Table(string title, IEnumerable<CornerResult> rows)
        {
            sb.AppendLine(title);
            sb.AppendLine("corner | seconds lost | apex km/h you/ref | gear you/ref | braking g you/ref | brake released m before apex you/ref | roll s you/ref");
            foreach (var c in rows)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} (km {1:F2}) | {2:+0.00;-0.00} | {3:F0}/{4:F0} | {5}/{6} | {7:F2}/{8:F2} | {9:F0}/{10:F0} | {11:F2}/{12:F2}",
                    c.Corner.Name, c.Corner.ApexKm, c.SecondsLost, c.You.MinKmh, c.Ref.MinKmh, c.You.Gear, c.Ref.Gear,
                    c.You.BrakingDecelG, c.Ref.BrakingDecelG, c.You.ReleaseMBeforeApex, c.Ref.ReleaseMBeforeApex,
                    c.You.RollSeconds, c.Ref.RollSeconds));
        }

        var last = Laps.LastOrDefault();
        if (last != null) Table($"Last measured lap (lap {last.Lap}) vs {_ref.Name}, per corner:", last.Corners);
        if (_lap.Corners.Count > 0) Table($"Current lap so far (lap {_lap.Lap}):", _lap.Corners);

        // Average loss per corner over recent laps: where the time really goes.
        var recent = Laps.TakeLast(maxLaps).SelectMany(l => l.Corners).Concat(_lap.Corners)
            .GroupBy(c => c.Corner.Index)
            .Select(g => (g.First().Corner, Avg: g.Average(c => c.SecondsLost), N: g.Count()))
            .OrderByDescending(x => x.Avg).Take(5).ToList();
        if (recent.Count > 0)
            sb.AppendLine("Biggest average losses recently: " + string.Join("; ",
                recent.Select(x => $"{x.Corner.Name} {x.Avg:+0.00;-0.00}s over {x.N}")));
        return sb.ToString();
    }

    static string Fmt(double s) => double.IsNaN(s) ? "-" : $"{(int)(s / 60)}:{s % 60:00.000}";
}
