using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class StandingsConfig
{
    public bool Show { get; set; } = true;
    /// <summary>Screen position while you're in the car; int.MinValue = top-left of the primary screen. Saved when you drag the overlay.</summary>
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    /// <summary>Everything scales with the width; drag the left or right edge to resize. The height follows the rows.</summary>
    public int Width { get; set; } = StandingsPainter.BaseWidth;
    /// <summary>The painter's BaseWidth the widths above were saved for; when the layout grows they're scaled to match.</summary>
    public int LayoutWidth { get; set; }

    /// <summary>
    /// Position and width while you're out of the car (garage, spectating, replays), saved separately when you move
    /// the overlay while out of the car. int.MinValue = same as in the car until you do.
    /// </summary>
    public int OutOfCarX { get; set; } = int.MinValue;
    public int OutOfCarY { get; set; } = int.MinValue;
    public int OutOfCarWidth { get; set; } = int.MinValue;
    public double Opacity { get; set; } = 0.9;

    /// <summary>
    /// false: every car in one list in the order they run, whatever the class (the car number shows the class colour;
    /// iRating changes and purple laps stay per class). true: a block per class, yours first.
    /// </summary>
    public bool ByClass { get; set; } = false;

    /// <summary>
    /// When the list is the cars around you on track (ahead above, behind below, lapped or not, like iRacing's relative)
    /// instead of the running order: "Practice" (default; laps mean nothing there), "Always" (races too) or "Never".
    /// </summary>
    public string OnTrackOrder { get; set; } = "Practice";

    /// <summary>
    /// In a race, when you're off the track or far slower than the cars around you (crashed, spun), switch to the cars
    /// around you on track with the time until each one reaches you, so you can see when it's safe to rejoin.
    /// Back to the standings a few seconds after you're up to speed.
    /// </summary>
    public bool RelativeWhenInTrouble { get; set; } = true;

    /// <summary>Rows shown for your class (or for everyone, when not split by class). With more cars: the leaders, then the cars around you.</summary>
    public int MaxRows { get; set; } = 10;
    public int LeaderRows { get; set; } = 3;
    /// <summary>Rows shown for each other class when split by class.</summary>
    public int OtherClassRows { get; set; } = 2;

    /// <summary>"Player": gap to you (negative = ahead of you). "Leader": gap to the class leader. Other classes always use their leader.</summary>
    public string GapTo { get; set; } = "Player";

    /// <summary>
    /// Safety-rating estimate (iRacing does not publish the formula; tune these against your results).
    /// Gain per clean corner, calibrated on two clean 3-lap Nordschleife races (462 corners each): +0.33 real at A 1.38,
    /// and +0.36 plus the whole-number bump (1.71 -> 2.47) at A 1.71.
    /// </summary>
    public double SrGainPerCorner { get; set; } = 0.00075;
    /// <summary>
    /// Corners per incident point that hold SR steady, by licence letter: one incident point costs this many corners' gain.
    /// A is calibrated on a 3-lap Nordschleife race with 2x at A 2.47 (real +0.14); the others are community estimates.
    /// </summary>
    public Dictionary<string, double> SrTargetCpi { get; set; } = new() { ["R"] = 12, ["D"] = 18, ["C"] = 35, ["B"] = 55, ["A"] = 135 };
    /// <summary>Extra SR added (or taken) when crossing a whole number, per iRacing.</summary>
    public double SrWholeNumberBump { get; set; } = 0.40;
}

public sealed class OverlaysConfig
{
    public StandingsConfig Standings { get; set; } = new();
    /// <summary>Virtual-key code that hides/shows the overlays. 145 = Scroll Lock.</summary>
    public int ToggleKey { get; set; } = 145;
    /// <summary>Virtual-key code that locks the overlays (clicks pass through to iRacing) or unlocks them for dragging. 19 = Pause.</summary>
    public int LockKey { get; set; } = 19;
    public bool Locked { get; set; } = false;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string PathFor(string baseDir) => Path.Combine(baseDir, "overlays.json");

    public static OverlaysConfig Load(string baseDir)
    {
        var path = PathFor(baseDir);
        if (!File.Exists(path))
        {
            var fresh = new OverlaysConfig();
            fresh.Standings.LayoutWidth = StandingsPainter.BaseWidth;
            fresh.Save(baseDir);
            return fresh;
        }
        var cfg = JsonSerializer.Deserialize<OverlaysConfig>(File.ReadAllText(path), Json) ?? new OverlaysConfig();
        var sc = cfg.Standings;
        if (sc.LayoutWidth != StandingsPainter.BaseWidth)
        {
            // New columns widened the layout: grow the saved widths so text stays the size it was.
            double k = StandingsPainter.BaseWidth / (double)(sc.LayoutWidth > 0 ? sc.LayoutWidth : StandingsPainter.OldBaseWidth);
            sc.Width = (int)Math.Round(sc.Width * k);
            if (sc.OutOfCarWidth != int.MinValue) sc.OutOfCarWidth = (int)Math.Round(sc.OutOfCarWidth * k);
            sc.LayoutWidth = StandingsPainter.BaseWidth;
            try { cfg.Save(baseDir); } catch (IOException) { }
        }
        return cfg;
    }

    public void Save(string baseDir) => File.WriteAllText(PathFor(baseDir), JsonSerializer.Serialize(this, Json));
}
