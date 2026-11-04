using System.IO.Compression;
using System.Text.Json;

/// <summary>
/// One SDK sample of the whole field, as recorded by the scan (16×) and the shoot (1×) passes. Compact on purpose:
/// a race is tens of thousands of these.
/// </summary>
public sealed class Frame
{
    /// <summary>SessionTime of the race session, seconds.</summary>
    public double T { get; set; }
    /// <summary>Seconds into the captured video (shoot pass only; NaN in the scan).</summary>
    public double V { get; set; } = double.NaN;
    public int State { get; set; }
    public int Flags { get; set; }
    public int CamCar { get; set; } = -1;
    public int CamGroup { get; set; }
    /// <summary>The car whose driver is talking on voice chat (RadioTransmitCarIdx), -1 = nobody.</summary>
    public int Radio { get; set; } = -1;
    public CarState[] Cars { get; set; } = Array.Empty<CarState>();

    public CarState? Car(int idx) => idx >= 0 && idx < Cars.Length ? Cars[idx] : null;

    public const int StateRacing = 4, StateCheckered = 5, StateCoolDown = 6;
    public const int FlagCheckered = 0x1, FlagWhite = 0x2, FlagGreen = 0x4, FlagYellow = 0x8, FlagCaution = 0x4000, FlagCautionWaving = 0x8000;
    public bool Caution => (Flags & (FlagCaution | FlagCautionWaving)) != 0;
}

/// <summary>A car at one sample. Surface: -1 not in world, 0 off track, 1 pit stall, 2 approaching pits, 3 on track.</summary>
public struct CarState
{
    public int Lap { get; set; }
    public int LapCompleted { get; set; }
    public float Pct { get; set; }
    public int Position { get; set; }
    public int ClassPosition { get; set; }
    public bool OnPitRoad { get; set; }
    public int Surface { get; set; }
    public float LastLap { get; set; }
    public float BestLap { get; set; }

    public readonly bool InWorld => Surface >= 0 && Pct >= 0;

    public static CarState Read(LiveTelemetry t, int i) => new()
    {
        Lap = t.Int("CarIdxLap", i, -1),
        LapCompleted = t.Int("CarIdxLapCompleted", i, -1),
        Pct = (float)t.Get("CarIdxLapDistPct", i),
        Position = t.Int("CarIdxPosition", i),
        ClassPosition = t.Int("CarIdxClassPosition", i),
        OnPitRoad = t.Get("CarIdxOnPitRoad", i) > 0,
        Surface = t.Int("CarIdxTrackSurface", i, -1),
        LastLap = (float)t.Get("CarIdxLastLapTime", i),
        BestLap = (float)t.Get("CarIdxBestLapTime", i),
    };
}

/// <summary>Frames on disk: one JSON array per line, gzip-compressed (a race is a few MB instead of a hundred).</summary>
public static class FrameFile
{
    public const int MaxCars = 64;

    public static void Write(string path, IEnumerable<Frame> frames)
    {
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Fastest);
        using var w = new StreamWriter(gz);
        foreach (var f in frames) w.WriteLine(Line(f));
    }

    /// <summary>Appends frames as they come (the shoot pass), so a crash still leaves what was recorded.</summary>
    public sealed class Writer : IDisposable
    {
        readonly FileStream _file;
        readonly GZipStream _gz;
        readonly StreamWriter _w;
        public Writer(string path)
        {
            _file = File.Create(path);
            _gz = new GZipStream(_file, CompressionLevel.Fastest);
            _w = new StreamWriter(_gz);
        }
        public void Add(Frame f) => _w.WriteLine(Line(f));
        public void Dispose() { _w.Dispose(); _gz.Dispose(); _file.Dispose(); }
    }

    static string Line(Frame f)
    {
        // [T, V, State, Flags, CamCar, CamGroup, [lap, lapCompleted, pct, pos, classPos, pit, surface, last, best] x cars, Radio]
        var cars = f.Cars.Select(c => new object[]
        {
            c.Lap, c.LapCompleted, Num(c.Pct, 5), c.Position, c.ClassPosition, c.OnPitRoad ? 1 : 0, c.Surface,
            Num(c.LastLap, 3), Num(c.BestLap, 3),
        });
        return JsonSerializer.Serialize(new object[]
        {
            Num(f.T, 3), Num(f.V, 4), f.State, f.Flags, f.CamCar, f.CamGroup, cars, f.Radio,
        });
    }

    // JSON has no NaN: missing values are written as -1.
    static double Num(double v, int digits) => double.IsFinite(v) ? Math.Round(v, digits) : -1;

    public static List<Frame> Read(string path)
    {
        var frames = new List<Frame>();
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { break; } // cut short by a crash
            using var _ = doc;
            var a = doc.RootElement;
            var cars = a[6].EnumerateArray().Select(c => new CarState
            {
                Lap = c[0].GetInt32(), LapCompleted = c[1].GetInt32(), Pct = c[2].GetSingle(), Position = c[3].GetInt32(),
                ClassPosition = c[4].GetInt32(), OnPitRoad = c[5].GetInt32() != 0, Surface = c[6].GetInt32(),
                LastLap = c[7].GetSingle(), BestLap = c[8].GetSingle(),
            }).ToArray();
            double v = a[1].GetDouble();
            frames.Add(new Frame
            {
                T = a[0].GetDouble(), V = v < 0 ? double.NaN : v, State = a[2].GetInt32(), Flags = a[3].GetInt32(),
                CamCar = a[4].GetInt32(), CamGroup = a[5].GetInt32(), Cars = cars,
                Radio = a.GetArrayLength() > 7 ? a[7].GetInt32() : -1, // older files have no radio
            });
        }
        return frames;
    }

    /// <summary>The current sample from the SDK.</summary>
    public static Frame Capture(LiveTelemetry t, int cars)
    {
        var f = new Frame
        {
            T = t.Get("SessionTime"), State = t.Int("SessionState"), Flags = t.Int("SessionFlags"),
            CamCar = t.Int("CamCarIdx", 0, -1), CamGroup = t.Int("CamGroupNumber"),
            Radio = t.Int("RadioTransmitCarIdx", 0, -1),
            Cars = new CarState[cars],
        };
        for (int i = 0; i < cars; i++) f.Cars[i] = CarState.Read(t, i);
        return f;
    }
}
