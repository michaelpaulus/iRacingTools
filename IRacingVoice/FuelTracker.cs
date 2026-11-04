/// <summary>
/// Fuel used per lap, from the fuel level each time a clean lap is completed (average of the last three), so the
/// agent can answer "how many laps until empty?" and act on "add three laps of fuel". Feed it every telemetry frame.
/// </summary>
public sealed class FuelTracker
{
    readonly object _gate = new();
    readonly List<double> _perLap = new();
    double _lastPct = -1, _fuelAtLine = double.NaN;
    bool _lapClean;
    int _session = int.MinValue;

    public double FuelNow { get; private set; } = double.NaN;
    public double LapsRemaining { get; private set; } = double.NaN; // race laps to go, if the session counts laps
    public double TimeRemaining { get; private set; } = double.NaN;

    public void OnFrame(ITelemetrySource t)
    {
        lock (_gate)
        {
            int session = (int)t.Get("SessionNum");
            if (session != _session) { _session = session; _perLap.Clear(); _lastPct = -1; _fuelAtLine = double.NaN; }
            double pct = t.Get("LapDistPct"), fuel = t.Get("FuelLevel");
            if (double.IsNaN(pct) || pct < 0 || double.IsNaN(fuel)) return;
            FuelNow = fuel;
            double laps = t.Has("SessionLapsRemainEx") ? t.Get("SessionLapsRemainEx") : double.NaN;
            LapsRemaining = laps >= 0 && laps < 10000 ? laps : double.NaN;
            double time = t.Get("SessionTimeRemain");
            TimeRemaining = time > 0 && time < 86400 ? time : double.NaN;

            bool clean = t.Get("IsOnTrack") > 0 && t.Get("OnPitRoad") <= 0;
            if (_lastPct > 0.9 && pct < 0.1)
            {
                // A lap that refuelled or went through the pits doesn't count.
                if (_lapClean && !double.IsNaN(_fuelAtLine) && _fuelAtLine - fuel > 0.05)
                {
                    _perLap.Add(_fuelAtLine - fuel);
                    if (_perLap.Count > 3) _perLap.RemoveAt(0);
                }
                _fuelAtLine = fuel;
                _lapClean = clean;
            }
            else if (_lastPct >= 0 && Math.Abs(pct - _lastPct) > 0.02) _lapClean = false; // reset / tow
            if (!clean) _lapClean = false;
            _lastPct = pct;
        }
    }

    /// <summary>Litres per lap (average of the last three clean laps), or NaN before the first one.</summary>
    public double PerLap { get { lock (_gate) return _perLap.Count == 0 ? double.NaN : _perLap.Average(); } }

    /// <summary>The fuel answer, in the driver's units: in the car, per lap, laps it lasts, and what's needed to finish.</summary>
    public string Status(bool metric)
    {
        if (double.IsNaN(FuelNow)) return "I can't see the car right now.";
        string answer = $"You've got {IRacing.Fuel(FuelNow, metric)}.";
        double per = PerLap;
        if (double.IsNaN(per)) return answer + " I'll know your fuel per lap after one clean lap.";
        answer += $" That's about {FuelNow / per:0.0} laps at {IRacing.Fuel(per, metric)} a lap.";
        if (!double.IsNaN(LapsRemaining))
        {
            double short_ = LapsRemaining * per - FuelNow;
            answer += short_ > 0 ? $" To finish you need {IRacing.Fuel(short_, metric)} more." : " Enough to finish.";
        }
        return answer;
    }

    /// <summary>Litres for this many laps (with a tenth of a lap spare), or NaN when the fuel per lap isn't known yet.</summary>
    public double LitresForLaps(double laps) => double.IsNaN(PerLap) ? double.NaN : (laps + 0.1) * PerLap;

    /// <summary>Litres still needed to reach the finish (with half a lap spare), or NaN when it can't be worked out.</summary>
    public double LitresToFinish() =>
        double.IsNaN(PerLap) || double.IsNaN(LapsRemaining) ? double.NaN : Math.Max(0, (LapsRemaining + 0.5) * PerLap - FuelNow);
}
