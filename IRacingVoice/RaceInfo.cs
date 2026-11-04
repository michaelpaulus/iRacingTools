using System.Globalization;

/// <summary>
/// Spoken answers about the race from iRacing's live data: position and gaps, who's around you on track, lap times,
/// weather, and the car's temperatures and tyres. Read-only; units follow iRacing's own setting.
/// </summary>
public static class RaceInfo
{
    const string NotInCar = "I can't see the car right now.";

    static bool Ready(ITelemetrySource t) => t.IsConnected && t.Has("PlayerCarIdx");

    static string Secs(double s) => s.ToString(s < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);

    static string Lap(double s) => s <= 0 || double.IsNaN(s) ? "no time"
        : s >= 60 ? $"{(int)(s / 60)}:{(s % 60).ToString("00.000", CultureInfo.InvariantCulture)}" : s.ToString("0.000", CultureInfo.InvariantCulture);

    static string Who(SessionYaml.Car? c) => c == null ? "a car" : $"car {c.Number}, {FirstName(c.Name)}";

    static string FirstName(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;

    /// <summary>Class position (and overall in multiclass), with the gaps to the cars either side in the class.</summary>
    public static string Position(ITelemetrySource t)
    {
        if (!Ready(t)) return NotInCar;
        var cars = SessionYaml.Cars(t.SessionInfo).Where(c => !c.IsPaceCar && !c.IsSpectator).ToList();
        int me = (int)t.Get("PlayerCarIdx");
        var mine = cars.FirstOrDefault(c => c.Idx == me);
        var inClass = cars.Where(c => mine == null || c.Class == mine.Class).ToList();
        bool multiclass = cars.Select(c => c.Class).Distinct().Count() > 1;

        // Racing: by where the cars are now (iRacing's own positions only change at the line). Otherwise iRacing's,
        // which recordings have too (the other cars' only live).
        bool liveRace = t.Get("SessionState") == 4 && t.Has("CarIdxLapDistPct") && mine != null &&
            SessionYaml.SessionType(t.SessionInfo, (int)t.Get("SessionNum")) == "Race";
        double Covered(SessionYaml.Car c) => Math.Max(0, t.Get("CarIdxLapCompleted", c.Idx)) + Math.Max(0, t.Get("CarIdxLapDistPct", c.Idx));
        int LivePos(IEnumerable<SessionYaml.Car> field) => field.Count(c => c.Idx != me && Covered(c) > Covered(mine!)) + 1;

        int pos, overall;
        if (liveRace) { pos = LivePos(inClass); overall = LivePos(cars); }
        else
        {
            double rawPos = t.Get("PlayerCarClassPosition");
            pos = double.IsNaN(rawPos) ? 0 : (int)rawPos;
            overall = (int)t.Get("PlayerCarPosition");
        }
        if (pos <= 0) return "You don't have a position yet.";
        string answer = $"P{pos}" + (multiclass ? $" in class, P{overall} overall." : ".");
        if (!t.Has("CarIdxClassPosition")) return answer;

        // Gaps in the class: iRacing's time behind the leader (race), else the best-lap difference.
        var liveOrder = inClass.OrderByDescending(Covered).ToList();
        SessionYaml.Car? At(int p) => liveRace
            ? p >= 1 && p <= liveOrder.Count ? liveOrder[p - 1] : null
            : inClass.FirstOrDefault(c => (int)t.Get("CarIdxClassPosition", c.Idx) == p);
        bool race = t.Get("SessionState") >= 4 && t.Get("CarIdxF2Time", me) > 0;
        string Gap(SessionYaml.Car c)
        {
            double d = race ? t.Get("CarIdxF2Time", c.Idx) - t.Get("CarIdxF2Time", me)
                            : t.Get("CarIdxBestLapTime", c.Idx) - t.Get("CarIdxBestLapTime", me);
            return double.IsNaN(d) || t.Get("CarIdxBestLapTime", me) <= 0 && !race ? "" : $" {Secs(Math.Abs(d))} seconds";
        }
        if (At(pos - 1) is { } ahead) answer += $" {Who(ahead)} is ahead{Gap(ahead)}.";
        if (At(pos + 1) is { } behind) answer += $" {Who(behind)} is behind{Gap(behind)}.";
        return answer;
    }

    /// <summary>The cars just ahead and behind on track, whatever their class or lap.</summary>
    public static string Nearby(ITelemetrySource t)
    {
        if (!Ready(t)) return NotInCar;
        int me = (int)t.Get("PlayerCarIdx");
        float myPct = (float)t.Get("CarIdxLapDistPct", me);
        if (myPct < 0) return "You're not on track.";
        double lap = t.Get("LapLastLapTime") > 0 ? t.Get("LapLastLapTime") : t.Get("CarIdxEstTime", me) / Math.Max(myPct, 0.01);
        var cars = SessionYaml.Cars(t.SessionInfo).Where(c => !c.IsPaceCar && !c.IsSpectator && c.Idx != me).ToList();
        var mine = SessionYaml.Cars(t.SessionInfo).FirstOrDefault(c => c.Idx == me);
        var around = cars.Select(c => (Car: c, Pct: (float)t.Get("CarIdxLapDistPct", c.Idx)))
            .Where(x => x.Pct >= 0 && t.Get("CarIdxOnPitRoad", x.Car.Idx) == 0)
            .Select(x => { double up = x.Pct - myPct; up -= Math.Round(up); return (x.Car, Up: up); }).ToList();
        string Describe((SessionYaml.Car Car, double Up) x, string where)
        {
            string cls = mine != null && x.Car.Class != mine.Class ? $" in {x.Car.Class}" : "";
            // Laps covered against yours: a car up the road can be laps down, one behind can be about to lap you.
            double laps = t.Get("CarIdxLapCompleted", x.Car.Idx) + t.Get("CarIdxLapDistPct", x.Car.Idx) - (t.Get("CarIdxLapCompleted", me) + myPct);
            int down = (int)Math.Round(laps - x.Up);
            string lapped = down < 0 ? $", {-down} lap{(down == -1 ? "" : "s")} down" : down > 0 ? $", {down} lap{(down == 1 ? "" : "s")} up" : "";
            string seconds = lap > 0 ? $" {Secs(Math.Abs(x.Up) * lap)} seconds" : "";
            return $"{Who(x.Car)}{cls}{lapped}{(lapped.Length > 0 ? "," : "")} is{seconds} {where}";
        }
        var parts = new List<string>();
        if (around.Where(x => x.Up > 0).OrderBy(x => x.Up).FirstOrDefault() is { Car: not null } ahead) parts.Add(Describe(ahead, "ahead"));
        if (around.Where(x => x.Up < 0).OrderByDescending(x => x.Up).FirstOrDefault() is { Car: not null } behind) parts.Add(Describe(behind, "behind"));
        return parts.Count == 0 ? "Nobody near you on track." : string.Join(". ", parts) + ".";
    }

    public static string LapTimes(ITelemetrySource t)
    {
        if (!Ready(t)) return NotInCar;
        double last = t.Get("LapLastLapTime"), best = t.Get("LapBestLapTime");
        var parts = new List<string> { $"Last lap {Lap(last)}", $"best {Lap(best)}" };
        if (t.Get("LapDeltaToBestLap_OK") > 0)
        {
            double d = t.Get("LapDeltaToBestLap");
            parts.Add(Math.Abs(d) < 0.05 ? "level with your best this lap" : $"{Secs(Math.Abs(d))} {(d < 0 ? "up on" : "down on")} your best this lap");
        }
        return string.Join(", ", parts) + ".";
    }

    public static string Weather(ITelemetrySource t)
    {
        if (!Ready(t)) return NotInCar;
        bool metric = IRacing.Metric(t);
        string Temp(double c) => metric ? $"{c:0} degrees" : $"{c * 9 / 5 + 32:0} degrees";
        double track = t.Has("TrackTempCrew") ? t.Get("TrackTempCrew") : t.Get("TrackTemp");
        var parts = new List<string> { $"Track {Temp(track)}", $"air {Temp(t.Get("AirTemp"))}" };
        if (t.Has("TrackWetness"))
        {
            string[] wet = ["", "dry", "mostly dry", "very lightly wet", "lightly wet", "moderately wet", "very wet", "extremely wet"];
            int w = (int)t.Get("TrackWetness");
            if (w > 0 && w < wet.Length) parts.Add($"track {wet[w]}");
        }
        if (t.Has("Precipitation") && t.Get("Precipitation") > 0.01) parts.Add($"rain {t.Get("Precipitation") * 100:0} percent");
        if (t.Has("WindVel"))
        {
            double ms = t.Get("WindVel");
            parts.Add(metric ? $"wind {ms * 3.6:0} kilometres an hour" : $"wind {ms * 2.237:0} miles an hour");
        }
        if (t.Has("Skies"))
        {
            string[] skies = ["clear", "partly cloudy", "mostly cloudy", "overcast"];
            int s = (int)t.Get("Skies");
            if (s >= 0 && s < skies.Length) parts.Add(skies[s]);
        }
        return string.Join(", ", parts) + ".";
    }

    /// <summary>Engine temperatures and warnings, and the tyres as last measured (iRacing only updates those in the pits).</summary>
    public static string Car(ITelemetrySource t)
    {
        if (!Ready(t)) return NotInCar;
        bool metric = IRacing.Metric(t);
        string Temp(double c) => metric ? $"{c:0}" : $"{c * 9 / 5 + 32:0}";
        var parts = new List<string>();
        if (t.Has("WaterTemp")) parts.Add($"Water {Temp(t.Get("WaterTemp"))}, oil {Temp(t.Get("OilTemp"))} degrees");
        int warn = t.Has("EngineWarnings") ? (int)t.Get("EngineWarnings") : 0;
        var warnings = new List<string>();
        if ((warn & 0x01) != 0) warnings.Add("water temperature");
        if ((warn & 0x02) != 0) warnings.Add("fuel pressure");
        if ((warn & 0x04) != 0) warnings.Add("oil pressure");
        if ((warn & 0x40) != 0) warnings.Add("oil temperature");
        parts.Add(warnings.Count == 0 ? "no warnings" : "warning: " + string.Join(", ", warnings));

        if (t.Has("LFwearM"))
        {
            // Wear is the tread left (1 = new); the middle of each tyre.
            var wear = new[] { "LF", "RF", "LR", "RR" }.Select(c => (c, w: t.Get($"{c}wearM"))).ToList();
            var worst = wear.OrderBy(x => x.w).First();
            if (worst.w > 0 && worst.w < 0.995) parts.Add($"tyres at the last stop: most worn {worst.c}, {worst.w * 100:0} percent left");
            var temps = new[] { "LF", "RF", "LR", "RR" }.Select(c => t.Get($"{c}tempCM")).ToList();
            if (temps.All(x => x > 0)) parts.Add($"tyre temps then {string.Join(", ", temps.Select(Temp))}");
        }
        return string.Join(", ", parts) + ".";
    }
}
