/// <summary>
/// What the standings remember between frames. Feed it every telemetry frame (60 Hz).
/// <list type="bullet">
/// <item>Interval timing: the session time each car last passed each of <see cref="Bins"/> points round the lap, so a gap
/// is "how long ago the car ahead was where the car behind is now", the same way timing screens and the spotter measure
/// it, instead of a prediction from iRacing's generic per-class lap estimate.</item>
/// <item>Memory: each car's laps, position and distance, so the order and the iRating/SR estimates survive cars (you
/// included) leaving the world after the flag, and where you took the chequered flag, so cooldown laps don't count.</item>
/// </list>
/// </summary>
public sealed class RaceTracker
{
    public const int Bins = 1000;
    const int StateCheckered = 5;
    const int N = CarFrame.MaxCars;

    readonly double[,] _passed = new double[N, Bins];
    readonly float[] _lastPct = new float[N];
    readonly double[] _lastTime = new double[N], _lastDist = new double[N], _lastMoved = new double[N];
    readonly int[] _maxLaps = new int[N], _lastPos = new int[N], _lastOverall = new int[N];
    int _session = int.MinValue, _maxIncidents, _playerLapsAtCheckered = -1;
    double _troubleUntil = double.NaN;

    public double Now { get; private set; }
    /// <summary>Laps you had completed when you took the chequered flag; null until then.</summary>
    public int? PlayerFinishedLaps { get; private set; }
    public int Incidents => _maxIncidents;

    public RaceTracker() => Reset(int.MinValue);

    /// <summary>A tracker that has seen just this one frame (tests, still images).</summary>
    public static RaceTracker From(CarFrame f, int playerCarIdx = -1)
    {
        var t = new RaceTracker();
        t.Update(f, playerCarIdx);
        return t;
    }

    void Reset(int session)
    {
        _session = session;
        for (int i = 0; i < N; i++)
        {
            for (int b = 0; b < Bins; b++) _passed[i, b] = double.NaN;
            _lastPct[i] = -1;
            _lastTime[i] = double.NaN;
            _lastDist[i] = double.NaN;
            _lastMoved[i] = double.NaN;
            _maxLaps[i] = -1;
            _lastPos[i] = 0;
            _lastOverall[i] = 0;
        }
        _maxIncidents = 0;
        _playerLapsAtCheckered = -1;
        _troubleUntil = double.NaN;
        PlayerFinishedLaps = null;
    }

    public void Update(CarFrame f, int playerCarIdx)
    {
        // New session (warmup -> race), or a new event where the clock restarts.
        if (f.SessionNum != _session || f.SessionTime < Now - 1) Reset(f.SessionNum);
        Now = f.SessionTime;
        _maxIncidents = Math.Max(_maxIncidents, f.PlayerIncidents);

        for (int i = 0; i < N; i++)
        {
            _maxLaps[i] = Math.Max(_maxLaps[i], f.LapCompleted[i]);
            if (f.ClassPosition[i] > 0) _lastPos[i] = f.ClassPosition[i];
            if (f.Position[i] > 0) _lastOverall[i] = f.Position[i];

            float pct = f.LapDistPct[i];
            if (pct < 0) { _lastPct[i] = -1; continue; } // not in the world: keep the remembered distance
            _lastDist[i] = Math.Max(0, f.LapCompleted[i]) + pct;

            float prev = _lastPct[i];
            double delta = pct - prev;
            if (delta < -0.5) delta += 1; // crossed the line
            if (prev >= 0 && delta > 0 && delta < 0.05 && !double.IsNaN(_lastTime[i]))
            {
                _lastMoved[i] = Now;
                // Stamp every point passed since the last frame, interpolating the time between frames.
                int from = (int)Math.Floor(prev * Bins) + 1, to = (int)Math.Floor((prev + delta) * Bins);
                for (int k = from; k <= to; k++)
                    _passed[i, k % Bins] = _lastTime[i] + (Now - _lastTime[i]) * ((double)k / Bins - prev) / delta;
            }
            else if (prev < 0 || delta > 0.05)
            {
                _passed[i, Bin(pct)] = Now; // first sight, or towed / reset: start over from here
            }
            _lastPct[i] = pct;
            _lastTime[i] = Now;
        }

        if (playerCarIdx >= 0 && playerCarIdx < N)
        {
            int laps = f.LapCompleted[playerCarIdx];
            if (f.SessionState >= StateCheckered)
            {
                if (_playerLapsAtCheckered < 0) _playerLapsAtCheckered = laps;
                else if (PlayerFinishedLaps == null && laps > _playerLapsAtCheckered) PlayerFinishedLaps = laps;
            }
        }
    }

    static int Bin(double pct) => Math.Clamp((int)Math.Floor(pct * Bins), 0, Bins - 1);

    /// <summary>Laps completed, never going backwards when a car leaves the world.</summary>
    public int Laps(int idx) => Math.Max(0, _maxLaps[idx]);

    /// <summary>Last official class position iRacing gave the car (0 = none yet).</summary>
    public int LastPosition(int idx) => _lastPos[idx];

    /// <summary>Last official overall position, all classes together (0 = none yet).</summary>
    public int LastOverallPosition(int idx) => _lastOverall[idx];

    /// <summary>
    /// Seconds the car took to cover the last <paramref name="bins"/> thousandths of a lap (keeps growing while it's
    /// stopped); NaN without that much history.
    /// </summary>
    public double RecentPace(int idx, int bins = 10)
    {
        if (idx < 0 || idx >= N || _lastPct[idx] < 0) return double.NaN;
        double t0 = _passed[idx, (Bin(_lastPct[idx]) - bins + Bins) % Bins];
        return Now - t0 >= 0 ? Now - t0 : double.NaN;
    }

    /// <summary>
    /// You're in trouble (crashed, spun, crawling back on): true from the first frame <paramref name="now"/> is, until
    /// <paramref name="holdSeconds"/> after it last was, so the view doesn't flick back and forth while you recover.
    /// </summary>
    public bool PlayerInTrouble(bool now, double holdSeconds)
    {
        if (now) _troubleUntil = Now + holdSeconds;
        return Now <= _troubleUntil;
    }

    /// <summary>The car moved forward within the last second (not parked on the grid, in the pits or stopped).</summary>
    public bool Moving(int idx) => idx >= 0 && idx < N && Now - _lastMoved[idx] <= 1.0;

    /// <summary>Laps covered: live while on track, otherwise where the car was last seen; NaN if never seen.</summary>
    public double Distance(int idx) => _lastDist[idx];

    /// <summary>
    /// Seconds since <paramref name="lead"/> was at lap fraction <paramref name="pct"/>; NaN when it hasn't been there
    /// recently enough to say (just joined, came out of the garage, or longer ago than <paramref name="maxSeconds"/>).
    /// </summary>
    public double TimeSince(int lead, float pct, double maxSeconds)
    {
        if (pct < 0 || lead < 0 || lead >= N) return double.NaN;
        double at = pct * Bins;
        int k = Math.Clamp((int)Math.Floor(at), 0, Bins - 1);
        double t0 = _passed[lead, k], t1 = _passed[lead, (k + 1) % Bins];
        if (double.IsNaN(t0)) return double.NaN;
        // t1 older than t0 means the car is still between those points: use the one it has passed.
        double when = double.IsNaN(t1) || t1 < t0 ? t0 : t0 + (t1 - t0) * (at - k);
        double gap = Now - when;
        return gap >= 0 && gap <= maxSeconds ? gap : double.NaN;
    }
}
