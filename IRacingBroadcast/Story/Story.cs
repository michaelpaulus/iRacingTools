using System.Text.Json;

/// <summary>Something worth showing or saying. Times are race-session SessionTime seconds.</summary>
public sealed record RaceEvent(string Kind, double T, double End, int[] Cars, int Position, double Importance, string Detail);

public static class Kinds
{
    public const string Start = "start", Overtake = "overtake", Battle = "battle", Incident = "incident", Pit = "pit",
        LeadChange = "lead-change", FastestLap = "fastest-lap", LastLap = "last-lap", Finish = "finish",
        FeaturedFinish = "featured-finish", Retire = "retire", ClassWin = "class-win", FieldHome = "field-home";
}

public sealed record Running(int CarIdx, int Position, double Gap, int LapsDown, bool InPit);

/// <summary>The running order each time the leader starts a new lap.</summary>
public sealed record LapSnapshot(int Lap, double T, List<Running> Order);

/// <summary>The story of the race found by the scan: what happened, when and to whom.</summary>
public sealed class Story
{
    public double FirstT { get; set; }
    public double GreenT { get; set; }
    public double CheckeredT { get; set; }
    /// <summary>When the last car still running took the flag.</summary>
    public double LastFinishT { get; set; }
    public double EndT { get; set; }
    public int FeaturedCar { get; set; } = -1;
    public List<RaceEvent> Events { get; set; } = new();
    public List<LapSnapshot> Laps { get; set; } = new();
    public List<Finisher> Finishers { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    public static Story Load(string path) => JsonSerializer.Deserialize<Story>(File.ReadAllText(path))!;
}
