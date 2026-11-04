using System.Globalization;

/// <summary>
/// One node of iRacing's session-info YAML: a scalar, a map or a list. Missing keys return an empty node, so lookups chain
/// (<c>root["DriverInfo"]["Drivers"]</c>) without null checks. Same parser as IRacingOverlays\SessionInfo.cs.
/// </summary>
public sealed class YNode
{
    public static readonly YNode Empty = new();

    public string? Value { get; init; }
    public Dictionary<string, YNode>? Map { get; init; }
    public List<YNode>? List { get; init; }

    public YNode this[string key] => Map != null && Map.TryGetValue(key, out var n) ? n : Empty;
    public IEnumerable<YNode> Items => List ?? Enumerable.Empty<YNode>();
    public string Str => Value ?? "";

    public int Int(int fallback = 0) =>
        int.TryParse(Str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Leading number of values like "20.6383 km" or "60.00 kph".</summary>
    public double Num(double fallback = double.NaN)
    {
        var s = Str;
        int end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] is '.' or '-' or '+')) end++;
        return double.TryParse(s[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>Hex colours such as "0xffda59".</summary>
    public int Hex(int fallback = 0xffffff)
    {
        var s = Str.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Str[2..] : Str;
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}

/// <summary>
/// Tolerant parser for the YAML subset iRacing writes. Splits on the first ": " so values containing colons, quotes or
/// other characters a strict YAML parser rejects (team names, setup notes) still come through as plain strings.
/// </summary>
public static class Yaml
{
    readonly record struct Line(int Indent, bool Item, string? Key, string Value);

    public static YNode Parse(string yaml)
    {
        var lines = new List<Line>();
        foreach (var raw in yaml.Split('\n'))
        {
            var text = raw.TrimEnd('\r', ' ', '\t');
            if (text.Length == 0 || text == "---" || text == "...") continue;
            int indent = 0;
            while (indent < text.Length && text[indent] == ' ') indent++;
            bool item = false;
            if (text.Length > indent + 1 && text[indent] == '-' && text[indent + 1] == ' ')
            {
                item = true;
                indent += 2;
                while (indent < text.Length && text[indent] == ' ') indent++;
            }
            var content = text[indent..];
            int colon = content.IndexOf(": ", StringComparison.Ordinal);
            string? key; string value;
            if (colon >= 0) { key = content[..colon]; value = content[(colon + 2)..].Trim(); }
            else if (content.EndsWith(':')) { key = content[..^1]; value = ""; }
            else { key = null; value = content.Trim(); }
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            lines.Add(new Line(indent, item, key, value));
        }
        int i = 0;
        return lines.Count == 0 ? YNode.Empty : ParseMap(lines, ref i, lines[0].Indent, firstIsItem: false);
    }

    static YNode ParseMap(List<Line> lines, ref int i, int indent, bool firstIsItem)
    {
        var map = new Dictionary<string, YNode>(StringComparer.Ordinal);
        bool first = true;
        while (i < lines.Count)
        {
            var l = lines[i];
            if (l.Indent < indent) break;
            if (l.Item && !(first && firstIsItem)) { if (l.Indent <= indent) break; i++; continue; }
            if (l.Indent > indent || l.Key == null) { i++; continue; } // stray line: skip rather than loop forever
            first = false;
            i++;
            YNode child;
            if (l.Value.Length > 0 || i >= lines.Count) child = new YNode { Value = l.Value };
            else if (lines[i].Item && lines[i].Indent > indent) child = ParseList(lines, ref i, lines[i].Indent);
            else if (lines[i].Indent > indent) child = ParseMap(lines, ref i, lines[i].Indent, firstIsItem: false);
            else child = new YNode { Value = "" };
            map[l.Key] = child;
        }
        return new YNode { Map = map };
    }

    static YNode ParseList(List<Line> lines, ref int i, int indent)
    {
        var list = new List<YNode>();
        while (i < lines.Count && lines[i].Item && lines[i].Indent == indent)
        {
            if (lines[i].Key == null) { list.Add(new YNode { Value = lines[i].Value }); i++; continue; }
            list.Add(ParseMap(lines, ref i, indent, firstIsItem: true));
        }
        return new YNode { List = list };
    }
}

public sealed record Driver(
    int CarIdx, string Name, string Number, string Car, string ClassName, int ClassId, int ClassColor,
    int IRating, string License, int LicenseColor, string Country, bool IsPlayer)
{
    /// <summary>"Max Verstappen" → "Verstappen": what commentators and the tower use.</summary>
    public string Surname
    {
        get
        {
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Drop trailing digits iRacing adds to duplicate names ("John Smith2").
            var last = parts.Length > 1 ? parts[^1].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') : Name;
            return last.Length > 0 ? last : Name;
        }
    }
}

public sealed record CameraGroup(int Num, string Name);

public sealed record Result(int CarIdx, int Position, int ClassPosition, int Laps, double FastestTime, int Incidents, string ReasonOut, double Time);

/// <summary>What the broadcast needs from the session info: the field, cameras, track and the race's final results.</summary>
public sealed class RaceInfo
{
    public string Track { get; init; } = "";
    public string TrackConfig { get; init; } = "";
    public double TrackLengthM { get; init; }
    public string Category { get; init; } = "";
    public string SimMode { get; init; } = "";
    public long SubSessionId { get; init; }
    public int PlayerCarIdx { get; init; } = -1;
    public int RaceSessionNum { get; init; } = -1;
    /// <summary>Scheduled race laps; null for a timed race.</summary>
    public int? RaceLaps { get; init; }
    public double RaceLengthSeconds { get; init; }
    public List<Driver> Drivers { get; init; } = new();
    public List<CameraGroup> Cameras { get; init; } = new();
    public Dictionary<int, int> Grid { get; init; } = new();
    public List<Result> Results { get; init; } = new();

    public Driver? Car(int idx) => Drivers.FirstOrDefault(d => d.CarIdx == idx);
    public string CarsSummary => string.Join(", ", Drivers.Select(d => d.Car).Distinct());
    public int Sof => Drivers.Count == 0 ? 0 : (int)Math.Round(Drivers.Average(d => (double)d.IRating));

    /// <summary>iRating as sim racers write it: 1234 → "1.2k".</summary>
    public static string Rating(int ir) => (ir / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k";
    /// <summary>iRating as it's said: 1234 → "1 point 2 k".</summary>
    public static string SpokenRating(int ir) => Rating(ir).Replace(".", " point ").Replace("k", " k");

    public static RaceInfo Parse(string yaml)
    {
        var root = Yaml.Parse(yaml);
        var wi = root["WeekendInfo"];
        var di = root["DriverInfo"];
        int player = di["DriverCarIdx"].Int(-1);

        var drivers = di["Drivers"].Items
            .Where(d => d["CarIsPaceCar"].Int() == 0 && d["IsSpectator"].Int() == 0 && d["CarIdx"].Int(-1) >= 0 && d["UserName"].Str.Length > 0)
            .Select(d => new Driver(
                d["CarIdx"].Int(), d["UserName"].Str, d["CarNumber"].Str, d["CarScreenNameShort"].Str,
                d["CarClassShortName"].Str, d["CarClassID"].Int(), d["CarClassColor"].Hex(),
                d["IRating"].Int(), d["LicString"].Str, d["LicColor"].Hex(0x777777),
                d["FlairName"].Str is { Length: > 0 } f ? f : d["ClubName"].Str, d["CarIdx"].Int() == player))
            .ToList();

        var cams = root["CameraInfo"]["Groups"].Items.Select(g => new CameraGroup(g["GroupNum"].Int(), g["GroupName"].Str)).ToList();

        var race = root["SessionInfo"]["Sessions"].Items.LastOrDefault(s => s["SessionType"].Str == "Race") ?? YNode.Empty;
        var results = race["ResultsPositions"].Items.Select(r => new Result(
            r["CarIdx"].Int(-1), r["Position"].Int(), r["ClassPosition"].Int() + 1, r["LapsComplete"].Int(),
            r["FastestTime"].Num(-1), r["Incidents"].Int(), r["ReasonOutStr"].Str, r["Time"].Num(-1)))
            .Where(r => r.CarIdx >= 0 && r.Position > 0).OrderBy(r => r.Position).ToList();

        var grid = root["QualifyResultsInfo"]["Results"].Items
            .Select(r => (Car: r["CarIdx"].Int(-1), Pos: r["Position"].Int(-1) + 1))
            .Where(r => r.Car >= 0 && r.Pos > 0)
            .GroupBy(r => r.Car).ToDictionary(g => g.Key, g => g.First().Pos);

        return new RaceInfo
        {
            Track = wi["TrackDisplayName"].Str,
            TrackConfig = wi["TrackConfigName"].Str,
            TrackLengthM = wi["TrackLength"].Num(0) * 1000,
            Category = wi["Category"].Str,
            SimMode = wi["SimMode"].Str,
            SubSessionId = long.TryParse(wi["SubSessionID"].Str, out var ss) ? ss : 0,
            PlayerCarIdx = player,
            RaceSessionNum = race == YNode.Empty ? -1 : race["SessionNum"].Int(-1),
            RaceLaps = int.TryParse(race["SessionLaps"].Str, out var laps) ? laps : null,
            RaceLengthSeconds = race["SessionTime"].Num(0),
            Drivers = drivers,
            Cameras = cams,
            Grid = grid,
            Results = results,
        };
    }
}
