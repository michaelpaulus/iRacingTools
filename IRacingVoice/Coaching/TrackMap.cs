using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// The track's corners from Crew Chief's track data (trackLandmarksData.json in its install folder, 470 iRacing tracks):
/// official turn numbers, corner names and where each runs in metres from the line. Lets the coach work from the first
/// lap and use the names drivers know ("Turn 14, Forrests Elbow") instead of counting speed dips.
/// </summary>
public sealed class TrackMap
{
    sealed record Landmark(double StartM, double EndM, List<int> Turns, string Name);

    readonly List<Landmark> _landmarks;

    TrackMap(List<Landmark> landmarks) => _landmarks = landmarks;

    static readonly string[] Files =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Britton IT Ltd", "CrewChiefV4", "trackLandmarksData.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Britton IT Ltd", "CrewChiefV4", "trackLandmarksData.json"),
    ];

    /// <summary>The map for iRacing's TrackName (e.g. "spa up"), or null when Crew Chief isn't installed or doesn't know the layout.</summary>
    public static TrackMap? For(string trackName, double trackM)
    {
        var file = Files.FirstOrDefault(File.Exists);
        if (file == null || trackName.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var t in doc.RootElement.GetProperty("trackLandmarksData").EnumerateArray())
            {
                if (!t.TryGetProperty("irTrackName", out var ir) || !string.Equals(ir.GetString(), trackName, StringComparison.OrdinalIgnoreCase)) continue;
                // A different length is a different layout (or a rebuilt track): its distances wouldn't line up.
                if (t.TryGetProperty("approximateTrackLength", out var len) && Math.Abs(len.GetDouble() - trackM) > 0.02 * trackM) continue;
                var list = new List<Landmark>();
                foreach (var l in t.GetProperty("trackLandmarks").EnumerateArray())
                {
                    if (l.TryGetProperty("isStraight", out var st) && st.ValueKind == JsonValueKind.True) continue;
                    double s = l.GetProperty("distanceRoundLapStart").GetDouble(), e = l.GetProperty("distanceRoundLapEnd").GetDouble();
                    if (e <= s) continue; // runs over the line
                    var names = l.GetProperty("landmarkNames").EnumerateArray().Select(n => n.GetString() ?? "").Where(n => n.Length > 0).ToList();
                    var turns = names.Select(n => Regex.Match(n, @"^turns?(\d+)$")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).OrderBy(n => n).ToList();
                    string name = names.Where(n => !Regex.IsMatch(n, @"^(turns?|t)\d+$")).Select(Pretty).FirstOrDefault() ?? "";
                    if (turns.Count == 0 && name.Length == 0) continue;
                    list.Add(new Landmark(s, e, turns, name));
                }
                if (list.Count > 0) return new TrackMap(list.OrderBy(l => l.StartM).ToList());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { /* unreadable: learn the corners from driving */ }
        return null;
    }

    static string Pretty(string id) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(id.Replace('_', ' '));

    /// <summary>
    /// Corners straight from the map, for before the first clean lap: one per landmark, the apex searched across the whole
    /// landmark, the reminder given before its start.
    /// </summary>
    public List<Corner> Corners(int bins, double binM)
    {
        int Bin(double m) => Math.Clamp((int)(m / binM), 0, bins - 1);
        var list = new List<Corner>();
        for (int i = 0; i < _landmarks.Count; i++)
        {
            var l = _landmarks[i];
            int s = Bin(l.StartM), e = Bin(l.EndM);
            int start = i == 0 ? 0 : (Bin(_landmarks[i - 1].EndM) + s) / 2;
            int end = i == _landmarks.Count - 1 ? bins - 1 : (e + Bin(_landmarks[i + 1].StartM)) / 2;
            var (label, turns) = Label(l, 0, 1);
            list.Add(new Corner(i + 1, start, (s + e) / 2, end, s, label, turns, (e - s) / 2 + (int)(30 / binM)));
        }
        return list;
    }

    /// <summary>
    /// Names corners found from your driving: each takes the turn number and name of the landmark it falls in. Where a
    /// landmark holds several (a sequence of kinks), they're parts of it; one outside every landmark is "after" the one before.
    /// </summary>
    public List<Corner> Label(List<Corner> corners, double binM)
    {
        var owner = corners.Select(c =>
        {
            double m = c.Apex * binM;
            int best = -1; double bestD = 30;
            for (int i = 0; i < _landmarks.Count; i++)
            {
                double d = m < _landmarks[i].StartM ? _landmarks[i].StartM - m : m > _landmarks[i].EndM ? m - _landmarks[i].EndM : 0;
                if (d <= bestD) { bestD = d; best = i; }
            }
            return best;
        }).ToList();

        var result = new List<Corner>();
        string previous = "";
        for (int k = 0; k < corners.Count; k++)
        {
            var c = corners[k];
            if (owner[k] < 0)
            {
                result.Add(c with { Label = previous.Length > 0 ? $"the corner after {previous}" : "the first corner", Turns = [] });
                continue;
            }
            var group = Enumerable.Range(0, corners.Count).Where(j => owner[j] == owner[k]).ToList();
            var (label, turns) = Label(_landmarks[owner[k]], group.IndexOf(k), group.Count);
            result.Add(c with { Label = label, Turns = turns });
            previous = label.Split(',')[0];
        }
        return result;
    }

    /// <summary>Piece <paramref name="i"/> of <paramref name="of"/> corners in one landmark.</summary>
    static (string Label, int[] Turns) Label(Landmark l, int i, int of)
    {
        var t = l.Turns;
        if (t.Count >= of && t.Count > 0)
        {
            // Enough turn numbers: one each, the last piece takes the rest ("Turns 3 and 4").
            var mine = i < of - 1 ? t.GetRange(i, 1) : t.GetRange(i, t.Count - i);
            string label = Turns(mine) + (i == 0 && l.Name.Length > 0 ? ", " + l.Name : "");
            return (label, mine.ToArray());
        }
        string whole = l.Name.Length > 0 ? l.Name : Turns(t);
        return (of == 1 ? (t.Count > 0 && l.Name.Length > 0 ? $"{Turns(t)}, {l.Name}" : whole) : $"{whole}, part {i + 1}", t.ToArray());
    }

    static string Turns(List<int> t) => t.Count switch
    {
        1 => $"Turn {t[0]}",
        2 => $"Turns {t[0]} and {t[1]}",
        _ => $"Turns {t[0]} to {t[^1]}",
    };

    /// <summary>Corner names in what the driver said ("how was Eau Rouge?"), matched to the labels.</summary>
    public static Corner? Named(IEnumerable<Corner> corners, string said)
    {
        static string Plain(string x) => Regex.Replace(x.ToLowerInvariant(), @"[^a-z0-9 ]", "").Trim();
        string s = " " + Plain(said) + " ";
        return corners.FirstOrDefault(c => c.Label.Split(',').Select(Plain)
            .Any(n => n.Length > 2 && !Regex.IsMatch(n, @"^(turns? |part |the corner after|the first)") && s.Contains(" " + n + " ")));
    }
}
