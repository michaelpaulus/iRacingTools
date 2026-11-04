using System.Globalization;
using System.Text;

/// <summary>
/// A simulated race that looks like the live SDK (session-info YAML plus CarIdx arrays), for trying the overlays
/// without iRacing running. Fictional drivers; you are "Demo Driver".
/// </summary>
public sealed class DemoTelemetry : ITelemetrySource
{
    sealed record Car(int Idx, string Name, string Number, string Flair, int IRating, int LicLevel, int LicSub,
        int ClassId, double Pace, double Phase);

    const int RaceLaps = 12, PlayerIdx = 7, NotStartedIdx = 19, PitIdx = 12;
    const double Gt3Lap = 138.2, Gt4Lap = 151.6;
    readonly List<Car> _cars = new();
    readonly double _speed;
    double _time; // seconds since the green flag
    DateTime _last = DateTime.MinValue;

    static readonly (string Name, string Flair, int IRating, int Lic, int Sub)[] People =
    {
        ("Lukas Brenner", "Germany", 4310, 20, 455), ("Sofia Marques", "Portugal", 3980, 19, 399),
        ("Jake Thornton", "United Kingdom", 3720, 20, 412), ("Mateo Alvarez", "Argentina", 3405, 18, 301),
        ("Emma Lindqvist", "Sweden", 3190, 17, 288), ("Noah Fischer", "Austria", 2950, 16, 349),
        ("Chloe Martin", "France", 2810, 17, 197), ("Demo Driver", "United States", 2645, 15, 306),
        ("Kenji Watanabe", "Japan", 2530, 16, 251), ("Liam O'Connor", "Ireland", 2390, 14, 380),
        ("Isabela Costa", "Brazil", 2255, 15, 222), ("Daan de Vries", "Netherlands", 2140, 13, 289),
        ("Ryan Mitchell", "Canada", 1985, 14, 175), ("Aleksander Nowak", "Poland", 1870, 12, 341),
        ("Tom Becker", "Global", 1755, 11, 263), ("Javier Ruiz", "Spain", 1640, 13, 198),
        ("Hannah Scott", "Australia", 1520, 10, 312), ("Marco Bianchi", "Italy", 1415, 9, 245),
        ("Ethan Clarke", "New Zealand", 1350, 9, 150), ("Pavel Horak", "Czech Republic", 1290, 8, 120),
        // GT4 class (multiclass demo only)
        ("Grace Wilson", "Scotland", 2480, 14, 333), ("Felix Wagner", "Switzerland", 2210, 13, 277),
        ("Ana Petrovic", "Serbia", 1990, 12, 301), ("Oscar Hansen", "Denmark", 1760, 11, 214),
        ("Mia Kowalski", "Poland", 1580, 10, 389), ("Leo Moreau", "Belgium", 1410, 9, 166),
    };

    public DemoTelemetry(bool multiclass, double speed, double startAtSeconds = 300)
    {
        _speed = speed;
        _time = startAtSeconds;
        var rng = new Random(7);
        int count = multiclass ? People.Length : 20;
        for (int i = 0; i < count; i++)
        {
            var p = People[i];
            bool gt4 = i >= 20;
            // Faster drivers (higher iRating) lap a little quicker, with some spread so places change hands.
            double pace = (gt4 ? Gt4Lap : Gt3Lap) * (1 + 0.0009 * (gt4 ? i - 20 : i) + rng.NextDouble() * 0.004);
            _cars.Add(new Car(i, p.Name, (i * 7 % 90 + 2).ToString(CultureInfo.InvariantCulture), p.Flair, p.IRating, p.Lic, p.Sub,
                gt4 ? 2 : 1, pace, rng.NextDouble() * Math.PI * 2));
        }
        SessionInfo = BuildYaml();
    }

    public bool IsConnected => true;
    public bool IsFinished => false;
    public int SessionInfoVersion => 1;
    public string SessionInfo { get; }

    public bool WaitForFrame(int timeoutMs)
    {
        Thread.Sleep(16);
        var now = DateTime.UtcNow;
        if (_last != DateTime.MinValue) _time += (now - _last).TotalSeconds * _speed;
        _last = now;
        return true;
    }

    /// <summary>Moves the simulation on without waiting (for rendering a still image).</summary>
    public void Advance(double seconds) => _time += seconds;

    /// <summary>Laps covered: grid slot offset, then each car's pace with a slow wobble so the order shuffles.</summary>
    double Distance(Car c)
    {
        if (c.Idx == NotStartedIdx) return double.NaN;
        double grid = -0.004 * c.Idx;
        double laps = _time / c.Pace + 0.012 * Math.Sin(_time / 90 + c.Phase);
        return Math.Min(RaceLaps + 0.05, grid + laps);
    }

    static readonly HashSet<string> Vars = new()
    {
        "SessionNum", "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemainEx", "PlayerCarMyIncidentCount",
        "CarIdxPosition", "CarIdxClassPosition", "CarIdxLapCompleted", "CarIdxTrackSurface", "CarIdxLapDistPct", "CarIdxEstTime",
        "CarIdxBestLapTime", "CarIdxLastLapTime", "CarIdxOnPitRoad", "CarIdxTireCompound",
    };

    static readonly string[] Gt3Cars =
        { "Ferrari 296 GT3", "Porsche 911 GT3 R (992)", "BMW M4 GT3 EVO", "McLaren 720S GT3 EVO", "Ford Mustang GT3", "Mercedes GT3 2020" };
    static readonly string[] Gt4Cars = { "Porsche 718 GT4", "BMW M4 GT4", "Mercedes GT4" };

    public bool Has(string name) => Vars.Contains(name);

    public double Get(string name, int index = 0)
    {
        var car = index < _cars.Count ? _cars[index] : null;
        double d = car == null ? double.NaN : Distance(car);
        switch (name)
        {
            case "SessionNum": return 1;
            case "SessionState": return CarFrame.StateRacing;
            case "SessionTime": return _time;
            case "SessionTimeRemain": return 3600 - _time;
            case "SessionLapsRemainEx": return RaceLaps - Math.Floor(_cars.Max(c => double.IsNaN(Distance(c)) ? 0 : Distance(c)));
            case "PlayerCarMyIncidentCount": return _time < 200 ? 0 : _time < 520 ? 2 : 3;
        }
        if (car == null) return name == "CarIdxTrackSurface" ? -1 : name == "CarIdxLapDistPct" ? -1 : 0;
        bool gone = double.IsNaN(d);
        switch (name)
        {
            case "CarIdxLapCompleted": return gone ? 0 : Math.Max(0, Math.Floor(d));
            case "CarIdxLapDistPct": return gone ? -1 : d - Math.Floor(d);
            case "CarIdxTrackSurface": return gone ? -1 : 3;
            case "CarIdxEstTime": return gone ? 0 : (d - Math.Floor(d)) * (car.ClassId == 2 ? Gt4Lap : Gt3Lap);
            case "CarIdxBestLapTime": return gone || d < 1 ? -1 : car.Pace * 0.994;
            case "CarIdxLastLapTime":
            {
                if (gone || d < 1) return -1;
                int lap = (int)Math.Floor(d);
                // Every fourth lap matches the car's best, so the personal-best / fastest-lap colours show up.
                return (lap + car.Idx) % 4 == 0 ? car.Pace * 0.994 : car.Pace * (0.996 + 0.006 * Math.Abs(Math.Sin(lap * 1.7 + car.Phase)));
            }
            case "CarIdxOnPitRoad": return car.Idx == PitIdx && d - Math.Floor(d) > 0.97 ? 1 : 0;
            case "CarIdxTireCompound": return gone ? -1 : car.Idx % 6 == 4 ? 1 : 0; // a few gamblers on wets
            case "CarIdxClassPosition":
                if (gone) return 0;
                return 1 + _cars.Count(o => o.ClassId == car.ClassId && o != car && Distance(o) is var od && !double.IsNaN(od) && od > d);
            case "CarIdxPosition":
                if (gone) return 0;
                return 1 + _cars.Count(o => o != car && Distance(o) is var od && !double.IsNaN(od) && od > d);
        }
        return 0;
    }

    string BuildYaml()
    {
        var sb = new StringBuilder();
        sb.Append("---\nWeekendInfo:\n TrackName: spa 2024 up\n TrackID: 525\n TrackLength: 6.9311 km\n");
        sb.Append(" TrackDisplayName: Circuit de Spa-Francorchamps\n TrackDisplayShortName: Spa\n TrackNumTurns: 20\n");
        sb.Append(" Official: 1\n EventType: Race\n Category: SportsCar\n");
        sb.Append($" NumCarClasses: {_cars.Select(c => c.ClassId).Distinct().Count()}\n");
        sb.Append("SessionInfo:\n CurrentSessionNum: 1\n Sessions:\n");
        sb.Append(" - SessionNum: 0\n   SessionLaps: unlimited\n   SessionType: Lone Qualify\n   SessionName: QUALIFY\n   ResultsPositions:\n");
        sb.Append($" - SessionNum: 1\n   SessionLaps: {RaceLaps}\n   SessionType: Race\n   SessionName: RACE\n");
        sb.Append("QualifyResultsInfo:\n Results:\n");
        foreach (var c in _cars) sb.Append($" - Position: {c.Idx}\n   ClassPosition: {c.Idx}\n   CarIdx: {c.Idx}\n");
        sb.Append($"DriverInfo:\n DriverCarIdx: {PlayerIdx}\n PaceCarIdx: {_cars.Count}\n");
        sb.Append(" DriverTires:\n - TireIndex: 0\n   TireCompoundType: \"Hard\"\n - TireIndex: 1\n   TireCompoundType: \"Wet\"\n Drivers:\n");
        foreach (var c in _cars)
        {
            bool gt4 = c.ClassId == 2;
            string letter = c.LicLevel >= 17 ? "A" : c.LicLevel >= 13 ? "B" : c.LicLevel >= 9 ? "C" : "D";
            string color = letter switch { "A" => "0x0153db", "B" => "0x00c702", "C" => "0xfeec04", _ => "0xfc8a27" };
            sb.Append($" - CarIdx: {c.Idx}\n   UserName: {c.Name}\n   UserID: {100000 + c.Idx}\n   CarNumber: \"{c.Number}\"\n");
            sb.Append($"   CarClassID: {c.ClassId}\n   CarIsPaceCar: 0\n   CarScreenNameShort: {(gt4 ? Gt4Cars[c.Idx % Gt4Cars.Length] : Gt3Cars[c.Idx % Gt3Cars.Length])}\n");
            sb.Append($"   CarClassShortName: {(gt4 ? "GT4" : "GT3")}\n   CarClassColor: {(gt4 ? "0x33ceff" : "0xffda59")}\n");
            sb.Append($"   CarClassEstLapTime: {(gt4 ? Gt4Lap : Gt3Lap).ToString(CultureInfo.InvariantCulture)}\n");
            sb.Append($"   IRating: {c.IRating}\n   LicLevel: {c.LicLevel}\n   LicSubLevel: {c.LicSub}\n");
            sb.Append($"   LicString: {letter} {c.LicSub / 100.0:0.00}\n   LicColor: {color}\n   IsSpectator: 0\n");
            sb.Append($"   ClubName: None\n   FlairName: {c.Flair}\n   CurDriverIncidentCount: -1\n");
        }
        sb.Append($" - CarIdx: {_cars.Count}\n   UserName: Pace Car\n   UserID: -1\n   CarNumber: \"0\"\n   CarClassID: 11\n   CarIsPaceCar: 1\n   IsSpectator: 0\n");
        return sb.ToString();
    }

    public void Dispose() { }
}
