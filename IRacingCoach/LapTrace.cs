using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>One lap resampled onto fixed distance bins (about 10 m each).</summary>
public sealed class LapTrace
{
    public readonly int N;
    public readonly double[] Speed, Brake, Throttle, Gear, Time;
    readonly int[] _count;
    readonly double[] _sSpeed, _sBrake, _sThr, _sGear;

    public LapTrace(int n)
    {
        N = n;
        Speed = Nan(n); Brake = Nan(n); Throttle = Nan(n); Gear = Nan(n); Time = Nan(n);
        _count = new int[n];
        _sSpeed = new double[n]; _sBrake = new double[n]; _sThr = new double[n]; _sGear = new double[n];
    }

    static double[] Nan(int n) { var a = new double[n]; Array.Fill(a, double.NaN); return a; }

    public int BinOf(double pct) => Math.Clamp((int)(pct * N), 0, N - 1);

    /// <param name="lapTime">seconds since the lap started</param>
    public void Add(double pct, double lapTime, double speed, double brake, double throttle, double gear)
    {
        if (pct < 0 || pct >= 1 || double.IsNaN(speed)) return;
        int b = BinOf(pct);
        if (_count[b] == 0) Time[b] = lapTime;
        int c = ++_count[b];
        _sSpeed[b] += speed; _sBrake[b] += brake; _sThr[b] += throttle; _sGear[b] += gear;
        Speed[b] = _sSpeed[b] / c; Brake[b] = _sBrake[b] / c; Throttle[b] = _sThr[b] / c; Gear[b] = _sGear[b] / c;
    }

    public double Coverage(int s, int e)
    {
        int k = 0;
        for (int b = s; b <= e; b++) if (_count[b] > 0) k++;
        return (double)k / Math.Max(1, e - s + 1);
    }

    /// <summary>Time at the start of bin b, using the nearest recorded bin at or after it.</summary>
    public double TimeAt(int b)
    {
        for (int k = b; k < N; k++) if (!double.IsNaN(Time[k])) return Time[k];
        for (int k = b; k >= 0; k--) if (!double.IsNaN(Time[k])) return Time[k];
        return double.NaN;
    }
}

public record Corner(int Index, int Start, int Apex, int End, double ApexKm)
{
    public string Name { get; set; } = "";
}

public record CornerStats(
    double MinKmh, int Gear,
    double BrakeOnMBeforeApex, double ReleaseMBeforeApex,
    double BrakingDecelG, double RollSeconds, double SegmentSeconds);

public static class CornerMath
{
    /// <summary>Corners = speed minima below 230 km/h with a 20 km/h drop before and 15 km/h rise after.</summary>
    public static List<Corner> FindCorners(LapTrace r, double binM)
    {
        var apexes = new List<int>();
        double runMax = 0, minV = 1e9; int minIdx = -1; bool falling = false;
        for (int b = 0; b < r.N; b++)
        {
            double v = r.Speed[b] * 3.6;
            if (double.IsNaN(v)) continue;
            if (!falling)
            {
                if (v > runMax) runMax = v;
                if (runMax - v > 20) { falling = true; minV = v; minIdx = b; }
            }
            else
            {
                if (v < minV) { minV = v; minIdx = b; }
                if (v - minV > 15)
                {
                    if (minV < 230) apexes.Add(minIdx);
                    falling = false; runMax = v;
                }
            }
        }
        var corners = new List<Corner>();
        for (int i = 0; i < apexes.Count; i++)
        {
            int a = apexes[i];
            int s = i == 0 ? 0 : (apexes[i - 1] + a) / 2;
            int e = i == apexes.Count - 1 ? r.N - 1 : (a + apexes[i + 1]) / 2;
            corners.Add(new Corner(i + 1, s, a, e, a * binM / 1000));
        }
        return corners;
    }

    public static CornerStats? Measure(LapTrace l, Corner c, double binM)
    {
        int w = Math.Max(1, (int)(60 / binM));
        int la = -1; double mv = double.MaxValue;
        for (int b = Math.Max(c.Start, c.Apex - w); b <= Math.Min(c.End, c.Apex + w); b++)
            if (!double.IsNaN(l.Speed[b]) && l.Speed[b] < mv) { mv = l.Speed[b]; la = b; }
        if (la < 0) return null;

        int brakeOn = -1;
        for (int b = c.Start; b <= la; b++) if (l.Brake[b] > 0.1) { brakeOn = b; break; }
        int release = -1;
        for (int b = la; b >= c.Start; b--) if (l.Brake[b] > 0.05) { release = b; break; }

        double decel = double.NaN;
        if (brakeOn >= 0 && release > brakeOn)
        {
            double dt = l.TimeAt(release) - l.TimeAt(brakeOn);
            if (dt > 0.1) decel = (l.Speed[brakeOn] - l.Speed[release]) / dt / 9.81;
        }

        double roll = 0;
        if (release >= 0)
        {
            int on = c.End;
            for (int b = release; b <= c.End; b++) if (l.Throttle[b] > 0.2) { on = b; break; }
            roll = Math.Max(0, l.TimeAt(on) - l.TimeAt(release));
        }

        double gear = l.Gear[la];
        return new CornerStats(
            mv * 3.6,
            double.IsNaN(gear) ? 0 : (int)Math.Round(gear),
            brakeOn < 0 ? double.NaN : (la - brakeOn) * binM,
            release < 0 ? double.NaN : (la - release) * binM,
            decel, roll,
            l.TimeAt(c.End) - l.TimeAt(c.Start));
    }
}

public sealed class ReferenceLap
{
    public required string Name { get; init; }
    public required LapTrace Trace { get; init; }
    public required double LapTime { get; init; }
    public required List<Corner> Corners { get; init; }
    public required List<CornerStats?> Stats { get; init; }

    /// <summary>Loads a Garage 61 CSV export (Speed m/s, LapDistPct, Brake/Throttle 0-1, Gear...).</summary>
    public static ReferenceLap LoadGarage61Csv(string path, string name, int bins, double trackKm)
    {
        var lines = File.ReadAllLines(path);
        var hdr = lines[0].Split(',');
        int Col(string n) => Array.IndexOf(hdr, n) is var i and >= 0 ? i : throw new InvalidDataException($"CSV column '{n}' missing");
        int cSp = Col("Speed"), cD = Col("LapDistPct"), cB = Col("Brake"), cT = Col("Throttle"), cG = Col("Gear");

        // Rows are evenly spaced; derive the interval from the lap time in the file name (mm.ss.fff), else 60 Hz.
        var rows = new List<string[]>();
        double prev = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            var p = lines[i].Split(',');
            if (p.Length < hdr.Length) continue;
            double d = double.Parse(p[cD], CultureInfo.InvariantCulture);
            if (d < prev - 0.5) break; // wrapped into the next lap
            prev = d;
            rows.Add(p);
        }
        double lapTime = rows.Count / 60.0;
        var m = Regex.Match(Path.GetFileName(path), @"(\d{1,2})\.(\d{2})\.(\d{3})");
        if (m.Success)
            lapTime = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[3].Value) / 1000.0;
        double dtRow = lapTime / rows.Count;

        int cLat = Array.IndexOf(hdr, "Lat"), cLon = Array.IndexOf(hdr, "Lon");
        var lat = new double[bins]; var lon = new double[bins]; var gps = new int[bins];

        var trace = new LapTrace(bins);
        for (int i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            double D(int c) => double.Parse(p[c], CultureInfo.InvariantCulture);
            double pct = D(cD);
            trace.Add(pct, i * dtRow, D(cSp), D(cB), D(cT), D(cG));
            if (cLat >= 0 && cLon >= 0)
            {
                int b = trace.BinOf(pct);
                lat[b] += D(cLat); lon[b] += D(cLon); gps[b]++;
            }
        }
        for (int b = 0; b < bins; b++)
        {
            if (gps[b] > 0) { lat[b] /= gps[b]; lon[b] /= gps[b]; }
            else { lat[b] = double.NaN; lon[b] = double.NaN; }
        }

        // Elapsed time at each bin boundary, monotonic, with the full lap time at the end.
        var times = new double[bins + 1];
        for (int b = 0; b < bins; b++) times[b] = trace.TimeAt(b);
        times[0] = 0;
        times[bins] = lapTime;
        for (int b = 1; b <= bins; b++) if (double.IsNaN(times[b]) || times[b] < times[b - 1]) times[b] = times[b - 1];

        double binM = trackKm * 1000 / bins;
        var corners = CornerMath.FindCorners(trace, binM);
        return new ReferenceLap
        {
            Name = name, Trace = trace, LapTime = lapTime, Corners = corners,
            Stats = corners.Select(c => CornerMath.Measure(trace, c, binM)).ToList(),
            Lat = lat, Lon = lon, _times = times,
        };
    }

    /// <summary>GPS position of each distance bin (NaN where the CSV had no GPS).</summary>
    public double[] Lat { get; init; } = Array.Empty<double>();
    public double[] Lon { get; init; } = Array.Empty<double>();
    double[] _times = Array.Empty<double>();

    /// <summary>Reference lap's elapsed time when it reached this lap fraction.</summary>
    public double TimeAtPct(double pct)
    {
        int n = _times.Length - 1;
        double x = Math.Clamp(pct, 0, 1) * n;
        int b = Math.Min((int)x, n - 1);
        return _times[b] + (x - b) * (_times[b + 1] - _times[b]);
    }

    /// <summary>Where the reference car was (lap fraction) after this many seconds of the lap.</summary>
    public double PctAtTime(double t)
    {
        int n = _times.Length - 1;
        if (t <= 0) return 0;
        if (t >= LapTime) return 1;
        int lo = 0, hi = n;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_times[mid] <= t) lo = mid; else hi = mid;
        }
        double span = _times[hi] - _times[lo];
        return (lo + (span > 0 ? (t - _times[lo]) / span : 0)) / n;
    }
}
