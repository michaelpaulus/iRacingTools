using System.Globalization;

/// <summary>
/// One node of iRacing's session-info YAML: a scalar, a map or a list. Missing keys return an empty node, so lookups chain
/// (<c>root["DriverInfo"]["Drivers"]</c>) without null checks.
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
public static class SessionYaml
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
            // " Drivers:" followed by " - CarIdx: 0": the item's content starts right of the key.
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

public sealed record DriverEntry(
    int CarIdx, string Name, int UserId, string CarNumber, string CarName,
    int ClassId, string ClassName, int ClassColor, double ClassEstLapTime,
    int IRating, string LicenseLetter, double SafetyRating, int LicenseColor,
    string Flair, bool IsSpectator, bool IsPaceCar);

public sealed record SessionDef(int Num, string Type, string Name, int? Laps);

/// <summary>The parts of the session-info YAML the overlays use.</summary>
public sealed class SessionData
{
    public int PlayerCarIdx { get; init; } = -1;
    public string TrackName { get; init; } = "";
    public double TrackLengthKm { get; init; }
    public int TrackTurns { get; init; }
    public string Category { get; init; } = "";
    public bool Official { get; init; }
    public IReadOnlyList<DriverEntry> Drivers { get; init; } = Array.Empty<DriverEntry>();
    public IReadOnlyDictionary<int, SessionDef> Sessions { get; init; } = new Dictionary<int, SessionDef>();
    /// <summary>Starting grid (1-based) from QualifyResultsInfo, keyed by CarIdx.</summary>
    public IReadOnlyDictionary<int, int> Grid { get; init; } = new Dictionary<int, int>();
    /// <summary>
    /// Compound names ("Hard", "Wet", ...) by TireIndex, from DriverTires. iRacing only lists them for your own car, so in
    /// a multiclass race another class's index is read against your car's list.
    /// </summary>
    public IReadOnlyDictionary<int, string> TireCompounds { get; init; } = new Dictionary<int, string>();

    public DriverEntry? Player => Drivers.FirstOrDefault(d => d.CarIdx == PlayerCarIdx);

    public static SessionData Parse(string yaml)
    {
        var root = SessionYaml.Parse(yaml);
        var wi = root["WeekendInfo"];
        var di = root["DriverInfo"];

        var drivers = di["Drivers"].Items.Select(d =>
        {
            var lic = d["LicString"].Str; // "B 3.06"
            var letter = lic.Length > 0 ? lic[..1] : "";
            int sub = d["LicSubLevel"].Int(-1);
            double sr = sub >= 0 ? sub / 100.0 : 0;
            return new DriverEntry(
                d["CarIdx"].Int(-1), d["UserName"].Str, d["UserID"].Int(-1), d["CarNumber"].Str, d["CarScreenNameShort"].Str,
                d["CarClassID"].Int(), d["CarClassShortName"].Str, d["CarClassColor"].Hex(), d["CarClassEstLapTime"].Num(0),
                d["IRating"].Int(), letter, sr, d["LicColor"].Hex(0x777777),
                // FlairName replaced clubs in 2024; fall back to the club for older builds / replays.
                d["FlairName"].Str is { Length: > 0 } f ? f : d["ClubName"].Str,
                d["IsSpectator"].Int() != 0, d["CarIsPaceCar"].Int() != 0);
        }).Where(d => d.CarIdx >= 0).ToList();

        var sessions = root["SessionInfo"]["Sessions"].Items
            .Select(s => new SessionDef(s["SessionNum"].Int(), s["SessionType"].Str, s["SessionName"].Str,
                int.TryParse(s["SessionLaps"].Str, out var laps) ? laps : null))
            .GroupBy(s => s.Num).ToDictionary(g => g.Key, g => g.First());

        // QualifyResultsInfo positions are 0-based.
        var grid = root["QualifyResultsInfo"]["Results"].Items
            .Select(r => (Car: r["CarIdx"].Int(-1), Pos: r["Position"].Int(-1) + 1))
            .Where(r => r.Car >= 0 && r.Pos > 0)
            .GroupBy(r => r.Car).ToDictionary(g => g.Key, g => g.First().Pos);

        var tires = di["DriverTires"].Items
            .Select(t => (Index: t["TireIndex"].Int(-1), Type: t["TireCompoundType"].Str))
            .Where(t => t.Index >= 0 && t.Type.Length > 0)
            .GroupBy(t => t.Index).ToDictionary(g => g.Key, g => g.First().Type);

        return new SessionData
        {
            TireCompounds = tires,
            PlayerCarIdx = di["DriverCarIdx"].Int(-1),
            TrackName = wi["TrackDisplayShortName"].Str is { Length: > 0 } t ? t : wi["TrackDisplayName"].Str,
            TrackLengthKm = wi["TrackLength"].Num(0),
            TrackTurns = wi["TrackNumTurns"].Int(),
            Category = wi["Category"].Str,
            Official = wi["Official"].Int() != 0,
            Drivers = drivers,
            Sessions = sessions,
            Grid = grid,
        };
    }
}
