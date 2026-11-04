/// <summary>One lap on fixed distance bins (about 5 m each): what the driver and the car did at each point.</summary>
public sealed class Trace
{
    public readonly int N;
    public readonly double BinM;
    // Speed m/s, Brake/Throttle 0-1, Steer rad (absolute), LatG in g (absolute), LongG in g (negative = slowing),
    // Yaw deg/s (absolute: how fast the car is rotating), Abs 0-1 (share of samples with ABS active), Time s since lap start.
    public readonly double[] Speed, Brake, Throttle, Gear, Rpm, Steer, LatG, LongG, Yaw, Abs, Time;
    readonly int[] _count;

    public Trace(int bins, double binM)
    {
        N = bins;
        BinM = binM;
        Speed = Nan(); Brake = Nan(); Throttle = Nan(); Gear = Nan(); Rpm = Nan(); Steer = Nan(); LatG = Nan(); LongG = Nan(); Yaw = Nan(); Abs = Nan(); Time = Nan();
        _count = new int[bins];
        double[] Nan() { var a = new double[bins]; Array.Fill(a, double.NaN); return a; }
    }

    public int BinOf(double pct) => Math.Clamp((int)(pct * N), 0, N - 1);

    public void Add(double pct, double lapTime, double speed, double brake, double throttle, double gear, double rpm,
        double steer, double latAccel, double longAccel, double yawRate, bool abs)
    {
        if (pct < 0 || pct >= 1 || double.IsNaN(speed)) return;
        int b = BinOf(pct);
        int c = _count[b]++;
        if (c == 0) { Time[b] = lapTime; Speed[b] = Brake[b] = Throttle[b] = Gear[b] = Rpm[b] = Steer[b] = LatG[b] = LongG[b] = Yaw[b] = Abs[b] = 0; }
        void Mix(double[] a, double v) => a[b] = (a[b] * c + (double.IsNaN(v) ? 0 : v)) / (c + 1);
        Mix(Speed, speed); Mix(Brake, brake); Mix(Throttle, throttle); Mix(Gear, gear); Mix(Rpm, rpm);
        Mix(Steer, Math.Abs(steer)); Mix(LatG, Math.Abs(latAccel) / 9.81); Mix(LongG, longAccel / 9.81);
        Mix(Yaw, Math.Abs(yawRate) * 57.2958); Mix(Abs, abs ? 1 : 0);
    }

    /// <summary>Total grip in use (braking/accelerating and cornering together), in g.</summary>
    public double Grip(int b) => Math.Sqrt(LatG[b] * LatG[b] + LongG[b] * LongG[b]);

    public bool Has(int b) => _count[b] > 0;

    public double Coverage(int s, int e)
    {
        int k = 0;
        for (int b = s; b <= e; b++) if (_count[b] > 0) k++;
        return (double)k / Math.Max(1, e - s + 1);
    }

    /// <summary>Lap time at bin b (nearest recorded bin at or after it).</summary>
    public double TimeAt(int b)
    {
        for (int k = b; k < N; k++) if (!double.IsNaN(Time[k])) return Time[k];
        for (int k = b; k >= 0; k--) if (!double.IsNaN(Time[k])) return Time[k];
        return double.NaN;
    }
}

/// <summary>
/// A corner on the track: where it starts and ends (for measuring) and the usual braking point (for reminders). With a
/// track map: what it's called and its official turn numbers (null = no map, Number is the turn), and how far either
/// side of Apex to look for the slowest point when the apex is only known roughly (bins; 0 = the usual 80 m).
/// </summary>
public sealed record Corner(int Number, int Start, int Apex, int End, int BrakeBin, string Label = "", int[]? Turns = null, int Window = 0);

public static class Corners
{
    /// <summary>
    /// Corners from a clean lap: speed minima with at least a 6 km/h drop before and a 6 km/h rise after (so the
    /// Nordschleife's many quick direction changes count, not just the big stops), merged when closer than 60 m.
    /// Each corner runs from halfway after the previous apex to halfway to the next one.
    /// </summary>
    public static List<Corner> Find(Trace t)
    {
        var apexes = new List<int>();
        double runMax = 0, minV = 1e9;
        int minIdx = -1;
        bool falling = false;
        for (int b = 0; b < t.N; b++)
        {
            double v = t.Speed[b] * 3.6;
            if (double.IsNaN(v)) continue;
            if (!falling)
            {
                if (v > runMax) runMax = v;
                if (runMax - v > 6) { falling = true; minV = v; minIdx = b; }
            }
            else
            {
                if (v < minV) { minV = v; minIdx = b; }
                if (v - minV > 6) { apexes.Add(minIdx); falling = false; runMax = v; }
            }
        }
        // Two minima within 60 m are one corner: keep the slower point.
        int gap = Math.Max(1, (int)(60 / t.BinM));
        for (int i = apexes.Count - 1; i > 0; i--)
            if (apexes[i] - apexes[i - 1] < gap)
            {
                if (t.Speed[apexes[i]] < t.Speed[apexes[i - 1]]) apexes[i - 1] = apexes[i];
                apexes.RemoveAt(i);
            }

        var list = new List<Corner>();
        for (int i = 0; i < apexes.Count; i++)
        {
            int a = apexes[i];
            int s = i == 0 ? 0 : (apexes[i - 1] + a) / 2;
            int e = i == apexes.Count - 1 ? t.N - 1 : (a + apexes[i + 1]) / 2;
            // Usual braking point: first real brake application before the apex; otherwise where the lift begins.
            int brake = -1;
            for (int b = Math.Max(s, a - (int)(Measure.BrakeSearchM / t.BinM)); b <= a; b++) if (t.Brake[b] > 0.1) { brake = b; break; }
            if (brake < 0)
            {
                brake = a;
                for (int b = a; b >= s; b--) if (t.Speed[b] >= t.Speed[a] + 15 / 3.6) { brake = b; break; }
            }
            list.Add(new Corner(i + 1, s, a, e, brake));
        }
        return list;
    }
}

/// <summary>
/// How one corner was driven, in the terms a coach thinks in: the braking (where, how hard, how fast it bit, how it
/// was released), coasting, the apex, the throttle (when, how smoothly, when flat) and the time it took.
/// Distances are metres before the apex (negative = after it).
/// </summary>
public sealed record Pass(
    int Corner, int Lap, double Seconds,
    double EntryKmh, double MinKmh, double ExitKmh, int Gear, double RpmAtApex,
    bool Braked, double BrakeOnM, double PeakBrake, double BiteSeconds, double ReleaseM, double BrakeAtApex, double ReleaseSeconds, double AbsShare, double BrakeSeconds,
    double CoastSeconds, double ThrottleOnM, double FullThrottleM, double ThrottleLift,
    double SteerAtApex, double LatGAtApex,
    // Grip gap at turn-in: the lowest total grip between peak braking and peak cornering, as a share of the smaller
    // of the two (1 = the tyres never stopped working; low = they went quiet between braking and turning).
    double GripGap, double PeakBrakeG, double PeakLatG,
    // Rotation: yaw rate at the apex (deg/s) and per unit of speed (how tightly the car is turning).
    double YawAtApex, double TurnRate,
    // Steering already in while the ABS was working (degrees of wheel), throttle ramp to 50% and time hovering at 75-97%.
    double SteerInAbs, double ThrottleTo50Seconds, double HoverSeconds);

public static class Measure
{
    /// <summary>The braking for a corner starts at most this far before its apex (beyond that it belongs to an earlier corner).</summary>
    public const double BrakeSearchM = 350;

    public static Pass? Corner(Trace t, Corner c, int lap)
    {
        if (t.Coverage(c.Start, c.End) < 0.9) return null;
        double m = t.BinM;

        // Apex: lowest speed within 80 m of where it usually is.
        int w = Math.Max(1, (int)(80 / m)), aw = Math.Max(w, c.Window), apex = -1;
        double minV = double.MaxValue;
        for (int b = Math.Max(c.Start, c.Apex - aw); b <= Math.Min(c.End, c.Apex + aw); b++)
            if (t.Has(b) && t.Speed[b] < minV) { minV = t.Speed[b]; apex = b; }
        if (apex < 0) return null;
        double M(int bin) => (apex - bin) * m;

        // Braking.
        int on = -1, searchFrom = Math.Max(c.Start, apex - (int)(BrakeSearchM / m));
        for (int b = searchFrom; b <= apex; b++) if (t.Has(b) && t.Brake[b] > 0.1) { on = b; break; }
        bool braked = on >= 0;
        double peak = 0; int peakBin = on;
        int release = -1;
        if (braked)
        {
            for (int b = on; b <= Math.Min(c.End, apex + w); b++) if (t.Has(b) && t.Brake[b] > peak) { peak = t.Brake[b]; peakBin = b; }
            for (int b = Math.Min(c.End, apex + w); b >= on; b--) if (t.Has(b) && t.Brake[b] > 0.05) { release = b; break; }
        }
        double bite = double.NaN;
        if (braked)
            for (int b = on; b <= peakBin; b++)
                if (t.Has(b) && t.Brake[b] >= 0.9 * peak) { bite = t.TimeAt(b) - t.TimeAt(on); break; }
        // Release: time from leaving 80% of peak to fully off (short = abrupt, long = trail braking).
        double releaseSec = double.NaN;
        if (braked && release > peakBin)
            for (int b = peakBin; b <= release; b++)
                if (t.Has(b) && t.Brake[b] < 0.8 * peak) { releaseSec = t.TimeAt(release) - t.TimeAt(b); break; }
        double abs = 0; int brakeBins = 0;
        if (braked) for (int b = on; b <= release; b++) if (t.Has(b)) { abs += t.Abs[b]; brakeBins++; }

        // Throttle after the brakes (or after the lift).
        int from = braked ? release : Math.Max(c.Start, apex - w);
        int thrOn = -1, flat = -1;
        for (int b = from; b <= c.End; b++) if (t.Has(b) && t.Throttle[b] > 0.2) { thrOn = b; break; }
        if (thrOn >= 0) for (int b = thrOn; b <= c.End; b++) if (t.Has(b) && t.Throttle[b] > 0.95) { flat = b; break; }
        // Lift: biggest drop in throttle after it was first applied, before going flat.
        double lift = 0, high = 0;
        if (thrOn >= 0)
            for (int b = thrOn; b <= (flat >= 0 ? flat : c.End); b++)
            {
                if (!t.Has(b)) continue;
                high = Math.Max(high, t.Throttle[b]);
                lift = Math.Max(lift, high - t.Throttle[b]);
            }
        // Coasting: neither pedal between the end of braking and the throttle.
        double coast = 0;
        int coastFrom = braked ? release : apex, coastTo = thrOn >= 0 ? thrOn : c.End;
        for (int b = Math.Max(c.Start, coastFrom); b < coastTo; b++)
            if (t.Has(b) && t.Brake[b] < 0.05 && t.Throttle[b] < 0.1 && t.Has(b + 1)) coast += t.TimeAt(b + 1) - t.TimeAt(b);

        double entry = 0;
        for (int b = searchFrom; b <= (braked ? on : apex); b++) if (t.Has(b)) entry = Math.Max(entry, t.Speed[b]);

        // Grip gap: from the hardest braking to the hardest cornering near the apex, total grip should stay up.
        double peakBrakeG = 0, peakLatG = 0, gap = 1;
        int brakeGBin = -1, latBin = -1;
        for (int b = searchFrom; b <= apex; b++) if (t.Has(b) && -t.LongG[b] > peakBrakeG) { peakBrakeG = -t.LongG[b]; brakeGBin = b; }
        for (int b = Math.Max(searchFrom, apex - w); b <= Math.Min(c.End, apex + w); b++) if (t.Has(b) && t.LatG[b] > peakLatG) { peakLatG = t.LatG[b]; latBin = b; }
        if (brakeGBin >= 0 && latBin > brakeGBin && peakBrakeG > 0.5 && peakLatG > 0.5)
        {
            double low = double.MaxValue;
            for (int b = brakeGBin; b <= latBin; b++) if (t.Has(b)) low = Math.Min(low, t.Grip(b));
            gap = low / Math.Min(peakBrakeG, peakLatG);
        }

        // Steering while the ABS was still working.
        double steerInAbs = 0;
        if (braked) for (int b = on; b <= release; b++) if (t.Has(b) && t.Abs[b] > 0.5) steerInAbs = Math.Max(steerInAbs, t.Steer[b] * 57.2958);

        // Throttle: how quickly to 50%, and time hovering short of flat.
        double to50 = double.NaN, hover = 0;
        if (thrOn >= 0)
        {
            for (int b = thrOn; b <= c.End; b++) if (t.Has(b) && t.Throttle[b] >= 0.5) { to50 = t.TimeAt(b) - t.TimeAt(thrOn); break; }
            for (int b = thrOn; b < (flat >= 0 ? flat : c.End); b++)
                if (t.Has(b) && t.Has(b + 1) && t.Throttle[b] >= 0.75 && t.Throttle[b] < 0.97) hover += t.TimeAt(b + 1) - t.TimeAt(b);
        }

        return new Pass(
            c.Number, lap, t.TimeAt(c.End) - t.TimeAt(c.Start),
            entry * 3.6, minV * 3.6, (t.Has(c.End) ? t.Speed[c.End] : double.NaN) * 3.6,
            (int)Math.Round(t.Gear[apex]), t.Rpm[apex],
            braked, braked ? M(on) : double.NaN, peak, bite, release >= 0 ? M(release) : double.NaN,
            t.Brake[apex], releaseSec, brakeBins > 0 ? abs / brakeBins : 0,
            braked && release > on ? t.TimeAt(release) - t.TimeAt(on) : 0,
            coast, thrOn >= 0 ? M(thrOn) : double.NaN, flat >= 0 ? M(flat) : double.NaN, lift,
            t.Steer[apex], t.LatG[apex],
            gap, peakBrakeG, peakLatG,
            t.Yaw[apex], minV > 1 ? t.Yaw[apex] / (minV * 3.6) : 0,
            steerInAbs, to50, hover);
    }
}
