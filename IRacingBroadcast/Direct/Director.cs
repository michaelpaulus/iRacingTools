using System.Text.Json;

/// <summary>One camera shot: from T (SessionTime) until the next shot, on a car with a camera group.</summary>
public sealed record Shot(double T, int CarIdx, int Group, string GroupName, string Reason);

/// <summary>
/// One moment of the highlights: a stretch of the race from <see cref="From"/> to <see cref="To"/> (SessionTime), the
/// event at its heart (at <see cref="At"/>) and the camera shots that cover it.
/// </summary>
public sealed record Clip(double From, double To, double At, string Kind, int[] Cars, string Caption, double Score, List<Shot> Shots)
{
    public double Length => To - From;
}

/// <summary>What the highlights follow: "mixed" (the race, plus the featured driver), "mine" (mostly the featured driver) or "race".</summary>
public static class HighlightsOf
{
    public const string Mixed = "mixed", Mine = "mine", Race = "race";
    public static readonly string[] All = { Mixed, Mine, Race };
}

/// <summary>
/// The highlights editor and TV director. Knows the whole story in advance (from the scan), so it picks the race's
/// best moments for a video of about <see cref="BroadcastConfig.HighlightMinutes"/>, in race order, and plans the
/// camera shots for each: on the attacker before the move, on the car in trouble, on the leader at the flag. Rules,
/// not AI: deterministic and quick to re-run.
/// </summary>
public static class Director
{
    const double MinShot = 4, MaxShot = 8, MergeGap = 3, MaxClip = 40;
    /// <summary>The last clip runs on this long after its moment, under the results table.</summary>
    public const double ResultsHold = 12;

    // Camera groups by role, matched by name (numbers differ between tracks). First match wins within a role.
    static readonly string[] Wide = { "Blimp", "Chopper", "TV1", "TV2" };
    static readonly string[] Battle = { "TV1", "TV2", "TV3", "Chase", "Far Chase", "Rear Chase" };
    static readonly string[] Action = { "TV1", "TV2", "TV3", "Chopper", "Far Chase" };
    static readonly string[] Flag = { "TV1", "TV3", "TV2" };

    sealed record Candidate(double From, double To, double At, string Kind, int[] Cars, string Caption, double Score, bool Must)
    {
        public double Length => To - From;
    }

    public static List<Clip> Plan(Story story, RaceInfo info, BroadcastConfig cfg, string focus)
    {
        var cams = info.Cameras;
        if (cams.Count == 0) throw new InvalidOperationException("The session info has no camera groups.");
        int featured = story.FeaturedCar;
        bool follow = featured >= 0 && focus != HighlightsOf.Race;
        double target = Math.Max(1, cfg.HighlightMinutes) * 60;
        string S(int car) => info.Car(car)?.Surname ?? "a car";

        // How much a moment is worth to this video.
        double Weight(RaceEvent e)
        {
            bool mine = e.Cars.Contains(featured);
            return focus switch
            {
                HighlightsOf.Mine => mine ? 4 : e.Kind is Kinds.LeadChange or Kinds.ClassWin ? 0.6 : 0.3,
                HighlightsOf.Mixed => mine ? 1.5 : 1,
                _ => 1,
            };
        }
        // The featured driver's share of the time beyond the must-haves, so the race's own story still gets told.
        double featuredBudget = focus switch { HighlightsOf.Mine => 0.8, HighlightsOf.Mixed => 0.35, _ => 1 } * target;
        double featuredTime = 0;

        var candidates = new List<Candidate>();
        foreach (var e in story.Events)
        {
            double w = Weight(e);
            bool mine = e.Cars.Contains(featured);
            switch (e.Kind)
            {
                case Kinds.Start:
                    candidates.Add(new(e.T - 6, e.T + 24, e.T, e.Kind, e.Cars, "Lights out", 1000, true));
                    break;
                case Kinds.Finish:
                    candidates.Add(new(e.T - 14, e.T + 6, e.T, e.Kind, e.Cars, $"{S(e.Cars[0])} wins", 900, true));
                    break;
                case Kinds.FeaturedFinish when follow:
                    candidates.Add(new(e.T - 8, e.T + 5, e.T, e.Kind, e.Cars, $"{S(e.Cars[0])} home", 800, true));
                    break;
                case Kinds.ClassWin:
                    candidates.Add(new(e.T - 9, e.T + 4, e.T, e.Kind, e.Cars, $"{S(e.Cars[0])} wins the class", 60 * w, false));
                    break;
                case Kinds.LeadChange:
                    // The headline of any race: who's leading.
                    candidates.Add(new(e.T - 8, e.T + 5, e.T, e.Kind, e.Cars, $"{S(e.Cars[0])} takes the lead", e.Importance * 3.5 * w, false));
                    break;
                case Kinds.Overtake:
                    candidates.Add(new(e.T - 9, e.T + 4, e.T, e.Kind, e.Cars, $"{S(e.Cars[0])} passes {S(e.Cars[1])}", e.Importance * 2 * w, false));
                    break;
                case Kinds.Incident:
                    candidates.Add(new(e.T - 4, Math.Min(e.End + 3, e.T + 12), e.T, e.Kind, e.Cars,
                        $"Trouble for {string.Join(" and ", e.Cars.Take(2).Select(S))}", e.Importance * 2.2 * w, false));
                    break;
                case Kinds.Battle when mine && focus == HighlightsOf.Mine:
                    candidates.Add(new(e.T, Math.Min(e.End, e.T + 15), e.T, e.Kind, e.Cars, $"{S(e.Cars[1])} chasing {S(e.Cars[0])}", e.Importance * 0.8 * w, false));
                    break;
            }
        }

        // The must-haves first, then the best moments while there's time left. A moment that overlaps one already in
        // joins it, if the joined clip isn't too long.
        var picked = new List<Candidate>();
        double Total() => picked.Sum(c => c.To - c.From);
        foreach (var c in candidates.OrderByDescending(c => c.Must).ThenByDescending(c => c.Score))
        {
            if (c.From < story.FirstT) continue;
            bool mine = featured >= 0 && c.Cars.Contains(featured);
            if (!c.Must && mine && featuredTime + c.Length > featuredBudget) continue;
            // A back-and-forth between the same two cars is one story: once nearby, twice in the whole video.
            var samePair = c.Cars.Length > 1 ? picked.Where(p => !p.Must && p.Cars.Contains(c.Cars[0]) && p.Cars.Contains(c.Cars[1])).ToList() : [];
            if (!c.Must && (samePair.Count >= 2 || samePair.Any(p => Math.Abs(p.At - c.At) < 150))) continue;
            // Everything it would join, including what the joined clip then reaches.
            double from = c.From, to = c.To;
            var near = new List<Candidate>();
            while (picked.Where(p => !near.Contains(p) && from < p.To + MergeGap && to > p.From - MergeGap).ToList() is { Count: > 0 } more)
            {
                near.AddRange(more);
                from = Math.Min(from, more.Min(p => p.From));
                to = Math.Max(to, more.Max(p => p.To));
            }
            if (near.Count == 0)
            {
                if (!c.Must && Total() + c.Length > target) continue;
                picked.Add(c);
                if (mine && !c.Must) featuredTime += c.Length;
                continue;
            }
            double grows = to - from - near.Sum(p => p.To - p.From);
            if (!c.Must && (to - from > MaxClip || Total() + grows > target)) continue;
            if (mine && !c.Must) featuredTime += grows;
            // The joined clip keeps the bigger moment's caption and focus.
            var lead = near.Append(c).OrderByDescending(p => p.Must).ThenByDescending(p => p.Score).First();
            foreach (var p in near) picked.Remove(p);
            picked.Add(lead with
            {
                From = from, To = to, Must = near.Any(p => p.Must) || c.Must,
                Cars = lead.Cars.Concat(near.Append(c).SelectMany(p => p.Cars)).Distinct().ToArray(),
            });
        }
        picked.Sort((a, b) => a.From.CompareTo(b.From));
        if (picked.Count > 0)
        {
            // The last clip stays on track under the results table.
            var last = picked[^1];
            picked[^1] = last with { To = Math.Min(story.EndT, last.To + ResultsHold) };
        }

        var rng = new Random((int)(info.SubSessionId % int.MaxValue));
        string lastCam = "";
        var clips = new List<Clip>();
        foreach (var c in picked)
        {
            var shots = Shots(c, story, info, follow ? featured : -1, rng, ref lastCam);
            clips.Add(new Clip(c.From, c.To, c.At, c.Kind, c.Cars, c.Caption, c.Score, shots));
        }
        return clips;
    }

    /// <summary>The camera shots for one clip: 4–8 s each, never the same camera twice in a row.</summary>
    static List<Shot> Shots(Candidate c, Story story, RaceInfo info, int featured, Random rng, ref string lastCam)
    {
        var shots = new List<Shot>();
        string last = lastCam;
        void Add(double t, int car, string[] role, string reason)
        {
            var cam = PickCamera(info.Cameras, role, last, shots.Count > 0 && shots[^1].CarIdx == car, rng);
            shots.Add(new Shot(t, car, cam.Num, cam.Name, reason));
            last = cam.Name;
        }
        AddShots(c, story, info, featured, Add);
        lastCam = last;
        return shots;
    }

    static void AddShots(Candidate c, Story story, RaceInfo info, int featured, Action<double, int, string[], string> Add)
    {
        int main = c.Cars.Length > 0 ? c.Cars[0] : info.Drivers[0].CarIdx;
        if (c.Kind == Kinds.Start)
        {
            // The grid from above, the launch on the polesitter, then the field into the first corners.
            int pole = info.Grid.OrderBy(g => g.Value).Select(g => g.Key).FirstOrDefault(main);
            Add(c.From, pole, Wide, "the grid");
            Add(c.At - 1, pole, ["TV1", "TV2"], "lights out");
            double t = c.At + 7;
            if (featured >= 0 && featured != pole) { Add(t, featured, Battle, "featured driver's start"); t += 7; }
            while (t < c.To - MinShot) { Add(t, pole, Action, "the field"); t += 7; }
            return;
        }

        string[] role = c.Kind switch
        {
            Kinds.Incident => Action,
            Kinds.Finish or Kinds.FeaturedFinish or Kinds.ClassWin => Flag,
            _ => Battle,
        };
        // Long clips get several shots; a battle cuts between hunter and hunted.
        int n = Math.Max(1, (int)Math.Round(c.Length / 6.5));
        double len = c.Length / n;
        for (int k = 0; k < n; k++)
        {
            double t = c.From + k * len;
            // The moment itself is on the main car; before and after, now and then the other car in it.
            bool aroundMoment = c.At >= t - 2 && c.At < t + len + 2;
            int car = !aroundMoment && c.Cars.Length > 1 && k % 2 == 1 ? c.Cars[1] : main;
            Add(t, car, role, c.Caption);
        }
    }

    static CameraGroup PickCamera(List<CameraGroup> cams, string[] role, string last, bool sameCar, Random rng)
    {
        var options = role.Select(n => cams.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c != null).Select(c => c!).Distinct().ToList();
        // Never the same camera twice in a row; on the same car, a different kind of shot.
        var fresh = options.Where(c => c.Name != last).ToList();
        if (fresh.Count > 0) options = fresh;
        if (options.Count == 0)
            options = cams.Where(c => !c.Name.Contains("Scenic", StringComparison.OrdinalIgnoreCase) && !c.Name.Contains("Pit", StringComparison.OrdinalIgnoreCase)).ToList();
        if (options.Count == 0) options = cams;
        // Favour the first options (the broadcast staples) without always picking them.
        int k = (int)Math.Floor(Math.Pow(rng.NextDouble(), sameCar ? 1.0 : 1.8) * options.Count);
        return options[Math.Clamp(k, 0, options.Count - 1)];
    }

    /// <summary>Where each clip starts in the finished video (seconds).</summary>
    public static double[] Offsets(List<Clip> clips)
    {
        var o = new double[clips.Count];
        for (int i = 1; i < clips.Count; i++) o[i] = o[i - 1] + clips[i - 1].Length;
        return o;
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static void Save(string path, List<Clip> clips) => File.WriteAllText(path, JsonSerializer.Serialize(clips, Json));
    public static List<Clip> Load(string path) => JsonSerializer.Deserialize<List<Clip>>(File.ReadAllText(path))!;
}
