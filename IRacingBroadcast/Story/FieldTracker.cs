/// <summary>
/// Follows every car through a sequence of frames: race distance in laps (unwrapped across the line, so it never
/// jumps), the running order per class, time gaps measured the timing-screen way ("how long ago the car ahead was
/// where this car is now"), and speed. Used by the story detector, the director and the graphics.
/// </summary>
public sealed class FieldTracker
{
    const int N = FrameFile.MaxCars;
    readonly RaceInfo _info;
    readonly double[] _dist = new double[N], _lastPct = new double[N], _lastSeen = new double[N];
    readonly List<(double T, double D)>[] _history = new List<(double, double)>[N];
    readonly int[] _officialPos = new int[N];

    public double Now { get; private set; } = double.NaN;
    public Frame? Current { get; private set; }

    public FieldTracker(RaceInfo info)
    {
        _info = info;
        for (int i = 0; i < N; i++)
        {
            _dist[i] = double.NaN;
            _lastPct[i] = -1;
            _lastSeen[i] = double.NaN;
            _history[i] = new List<(double, double)>();
        }
    }

    public void Update(Frame f)
    {
        Now = f.T;
        Current = f;
        foreach (var d in _info.Drivers)
        {
            int i = d.CarIdx;
            if (i >= f.Cars.Length) continue;
            var c = f.Cars[i];
            if (c.Position > 0) _officialPos[i] = c.Position;
            if (!c.InWorld) { _lastPct[i] = -1; continue; }
            double fresh = Math.Max(0, c.LapCompleted) + c.Pct;
            if (double.IsNaN(_dist[i]) || _lastPct[i] < 0)
            {
                // First sight, or back in the world: trust the lap counter, but never go backwards by a lap glitch.
                _dist[i] = double.IsNaN(_dist[i]) ? fresh : Math.Abs(fresh - _dist[i]) < 1.5 ? Math.Max(fresh, _dist[i] - 0.05) : fresh;
            }
            else
            {
                double delta = c.Pct - _lastPct[i];
                if (delta < -0.5) delta += 1;
                else if (delta > 0.5) delta -= 1;
                _dist[i] = Math.Abs(delta) < 0.25 ? _dist[i] + delta : fresh; // a big jump is a tow or reset
            }
            _lastPct[i] = c.Pct;
            _lastSeen[i] = f.T;
            var h = _history[i];
            if (h.Count == 0 || _dist[i] > h[^1].D) h.Add((f.T, _dist[i]));
        }
    }

    /// <summary>Race distance in laps; NaN if never seen.</summary>
    public double Distance(int idx) => _dist[idx];

    public bool InWorld(int idx) => Current?.Car(idx) is { } c && c.InWorld;

    /// <summary>
    /// Running order of one class (or all cars: classId null), leader first. After the chequered flag iRacing's own
    /// positions are final, so those are used.
    /// </summary>
    public List<int> Order(int? classId = null)
    {
        var cars = _info.Drivers.Where(d => (classId == null || d.ClassId == classId) && !double.IsNaN(_dist[d.CarIdx])).Select(d => d.CarIdx);
        if (Current != null && Current.State >= Frame.StateCheckered)
            return cars.OrderBy(i => _officialPos[i] > 0 ? _officialPos[i] : 999).ThenByDescending(i => _dist[i]).ToList();
        return cars.OrderByDescending(i => _dist[i]).ToList();
    }

    /// <summary>1-based position in the car's class right now (0 if unknown).</summary>
    public int Rank(int idx)
    {
        var d = _info.Car(idx);
        if (d == null) return 0;
        int r = Order(d.ClassId).IndexOf(idx);
        return r < 0 ? 0 : r + 1;
    }

    /// <summary>Seconds since <paramref name="ahead"/> was where <paramref name="behind"/> is now; NaN if not known.</summary>
    public double Gap(int behind, int ahead)
    {
        double d = _dist[behind];
        var h = _history[ahead];
        if (double.IsNaN(d) || h.Count == 0 || h[^1].D < d) return double.NaN;
        int lo = 0, hi = h.Count - 1;
        if (h[0].D > d) return double.NaN;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (h[mid].D <= d) lo = mid; else hi = mid;
        }
        var (t0, d0) = h[lo];
        var (t1, d1) = h[hi];
        double when = d1 > d0 ? t0 + (t1 - t0) * (d - d0) / (d1 - d0) : t0;
        return Now - when;
    }

    /// <summary>Speed in metres per second, averaged over the last <paramref name="window"/> seconds; NaN if not known.</summary>
    public double Speed(int idx, double window = 1.0)
    {
        var h = _history[idx];
        if (h.Count < 2 || Now - _lastSeen[idx] > 1) return double.NaN;
        var (tNow, dNow) = (Now, _dist[idx]);
        int k = h.Count - 1;
        while (k > 0 && tNow - h[k].T < window) k--;
        double dt = tNow - h[k].T;
        // Not moving: the history only grows when the car moves, so the last entry is old.
        return dt <= 0 ? 0 : (dNow - h[k].D) * _info.TrackLengthM / dt;
    }
}
