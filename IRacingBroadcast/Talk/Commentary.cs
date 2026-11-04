using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>One spoken line. T is seconds into the finished video; Audio and Seconds are filled in by the voices.</summary>
public sealed class Line
{
    public double T { get; set; }
    public string Speaker { get; set; } = "pbp";
    public string Text { get; set; } = "";
    /// <summary>1 = must be heard (calls of the action), 2 = normal, 3 = filler that may be dropped.</summary>
    public int Priority { get; set; } = 2;
    public string Audio { get; set; } = "";
    public double Seconds { get; set; }
}

public sealed class Script
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<Line> Lines { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    public static Script Load(string path) => JsonSerializer.Deserialize<Script>(File.ReadAllText(path))!;
}

/// <summary>
/// The narrator's script for the highlights, written by Claude from the story, the lap-by-lap order and the clips.
/// Claude is asked through the Claude Code CLI; with --no-claude a plain rule-based script is used instead.
/// </summary>
public static class Commentary
{
    const double WordsPerSecond = 2.6;

    public static async Task<Script> Write(Story story, RaceInfo info, List<Clip> clips, BroadcastConfig cfg, string focus, bool useClaude)
    {
        if (useClaude)
        {
            // A missing or signed-out CLI (ClaudeCliException) stops here with its message: fix it, or pick paste or simple.
            try
            {
                var s = await AskClaude(story, info, clips, cfg, focus);
                if (s != null && s.Lines.Count > 0) return s;
            }
            catch (Exception ex) when (ex is TimeoutException or JsonException or InvalidOperationException)
            {
                Console.WriteLine($"Claude couldn't write the script ({ex.GetType().Name}: {ex.Message}); using the simple built-in commentary.");
            }
        }
        return Simple(story, info, clips, cfg);
    }

    static string SystemPrompt(BroadcastConfig cfg, string focus) => $"""
        You write the voiceover for a race highlights video of an iRacing race, about {cfg.HighlightMinutes:0} minutes long, in
        the style of Formula 1's official YouTube race highlights. One narrator, {cfg.PlayByPlayName}, voiced by text-to-speech, tells the
        story of the race over a series of clips: the start, the best moments in race order, and the finish.

        The narrator:
        - Calls each clip's moment as it happens on screen, in the present tense, with real energy for the big moments
          ("And Wile goes for the lead into the first chicane... and he's through!").
        - Links the clips with context: what changed since the last clip (positions, gaps, laps, who's closing in), and
          why this moment matters. Between clips the video simply cuts, so bridge the gap in a sentence.
        - Opens over the grid with the scene: the track, the cars, the field and its SOF, who's on pole{(focus != HighlightsOf.Race ? ", and the featured driver" : "")}.
        - Ends over the results table (the last {Director.ResultsHold:0} seconds) with the result and a short sign-off.
        - {focus switch
        {
            HighlightsOf.Mine => "Tells the featured driver's race (the person who made this replay): their start, their battles, their result. The leaders are context.",
            HighlightsOf.Mixed => "Tells the race at the front, and follows the featured driver (the person who made this replay) whenever they're on screen.",
            _ => "Tells the race as a whole, like a TV broadcast.",
        }}
        - Is warm and a little playful, like the best grassroots sim-racing commentary: celebrate the chaos and the brave
          moves, tease, never insult (these are real people). No claims the data can't back (no invented contact, damage
          or penalties).
        - A car that stops on track has had an accident (a crash, a spin into the wall, a big off): call it that, never
          "stops" or "pulls over" as if it were a choice.
        - The data can't see contact. An incident with several cars means they got into trouble at the same moment at
          the same spot: they may have touched, or one may have been avoiding the other. Never say "contact", "hits",
          "taps", "tags", "makes contact" or "punts", and never blame anyone; say they're "caught up in it", "both in
          trouble", or describe what each car does (goes off, loses places, comes to a stop).
        - Only call what's in the clips: the viewer sees only the clips, so a moment outside them can be mentioned in
          passing between clips ("Earlier, Lee had lost places"), but never as if it's on screen.

        How the lines are written:
        - Every line is spoken by a TTS voice. Write plain speech only: no emoji, no stage directions, no markdown, no
          brackets, no "#". Say "P4" or "fourth", "one minute fifty-two point three".
          iRatings and strength of field are given in k, the way sim racers say them: write "1.2k" as "1 point 2 k"
          (never "twelve hundred" or "1,234").
        - Use the drivers' names as people say them: usually the surname, sometimes the first name. Skip odd parts of
          usernames, such as trailing digits.
        - "v" is seconds into the highlights video, from 0 to its end; each clip and its moment are given in v. Put the
          call of a moment at its v, or a second or two before it for a pass you can see coming.
        - Speaking rate is about {WordsPerSecond.ToString(CultureInfo.InvariantCulture)} words per second. Leave each line time to be
          spoken before the next one starts (its words ÷ {WordsPerSecond.ToString(CultureInfo.InvariantCulture)} seconds, plus a short pause).
          Most lines are 6 to 25 words. Calls of the action are short.
        - Talk for roughly 65 to 75% of the video; let the big moments and the engines breathe for a second or two.
        - priority: 1 = the call of a moment (must be heard), 2 = normal, 3 = can be dropped if lines run long.
        - title: a punchy YouTube title ending in "| Race Highlights". description: two or three sentences for YouTube.
        """;

    static string Digest(Story story, RaceInfo info, List<Clip> clips, BroadcastConfig cfg)
    {
        string R(double t) => (t - story.GreenT).ToString("0.0", CultureInfo.InvariantCulture);
        string V(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        string Name(int i) => info.Car(i)?.Name ?? $"car {i}";
        // The lap being driven: the snapshots count the leader's completed laps.
        int LapAt(double t) => (story.Laps.LastOrDefault(l => l.T <= t)?.Lap ?? 0) + 1;
        // Pit-lane starters read as "pit stops" at the green flag: not news.
        bool Worth(RaceEvent e) => !(e.Kind == Kinds.Pit && e.T < story.GreenT + 30);
        var sb = new StringBuilder();
        var featured = info.Car(story.FeaturedCar);
        var offsets = Director.Offsets(clips);
        double length = clips.Sum(c => c.Length);
        sb.AppendLine($"Track:{info.Track}{(info.TrackConfig.Length > 0 ? " - " + info.TrackConfig : "")} ({info.TrackLengthM / 1000:0.00} km)");
        sb.AppendLine($"Cars: {info.CarsSummary}. Category: {info.Category}. {info.Drivers.Count} starters, SOF {RaceInfo.Rating(info.Sof)}.");
        sb.AppendLine(info.RaceLaps is int laps ? $"Race length: {laps} laps." : $"Race length: {info.RaceLengthSeconds / 60:0} minutes.");
        sb.AppendLine($"Featured driver (made this replay): {featured?.Name ?? "none"}.");
        sb.AppendLine($"The highlights video is {V(length)} seconds long (v = 0 to {V(length)}). Race time t is seconds from the green flag.");
        sb.AppendLine();
        sb.AppendLine("The clips, in video order (v range, race time, lap, the moment and when it happens on screen):");
        for (int k = 0; k < clips.Count; k++)
        {
            var c = clips[k];
            double Vof(double t) => offsets[k] + t - c.From;
            sb.AppendLine($"Clip {k + 1}: v={V(offsets[k])} to {V(offsets[k] + c.Length)} (t={R(c.From)} to {R(c.To)}, lap {LapAt(c.At)}). " +
                $"{c.Kind}: {c.Caption}; the moment at v={V(Vof(c.At))}." + (k == clips.Count - 1 ? $" The results table shows from v={V(length - 10)}." : ""));
            foreach (var e in story.Events.Where(e => e.T >= c.From && e.T <= c.To && e.Kind != Kinds.Battle && Worth(e)))
                sb.AppendLine($"  at v={V(Vof(e.T))}: {e.Kind} {e.Detail}");
            sb.AppendLine("  shots: " + string.Join("; ", c.Shots.Select(s => $"v={V(Vof(s.T))} {Name(s.CarIdx)} [{s.GroupName}]")));
        }
        sb.AppendLine();
        sb.AppendLine("Field (car number, name, car, iRating, licence, country, grid, finish, incidents, best lap, status):");
        foreach (var d in info.Drivers.OrderBy(d => info.Grid.GetValueOrDefault(d.CarIdx, 99)))
        {
            var r = info.Results.FirstOrDefault(x => x.CarIdx == d.CarIdx);
            sb.AppendLine($"#{d.Number} {d.Name} | {d.Car} | iR {RaceInfo.Rating(d.IRating)} | {d.License} | {d.Country} | grid P{info.Grid.GetValueOrDefault(d.CarIdx)} | " +
                (r != null ? $"finish P{r.ClassPosition} | {r.Incidents}x | best {Detector.LapTime(r.FastestTime)} | {r.ReasonOut}" : "no result"));
        }
        sb.AppendLine();
        sb.AppendLine("Everything that happened in the race, for the story between the clips (t, lap, kind, importance, what happened):");
        foreach (var e in story.Events.Where(e => Worth(e) && (e.Kind != Kinds.Battle || e.Importance >= 4 || e.Cars.Contains(story.FeaturedCar))))
            sb.AppendLine($"t={R(e.T)} lap {LapAt(e.T)} {e.Kind} [{e.Importance:0.0}] {e.Detail}" + (e.End > e.T + 5 ? $" (until t={R(e.End)})" : ""));
        sb.AppendLine();
        sb.AppendLine("Running order at each lap (position, name, gap to leader in seconds or laps down, PIT if in the pits); top 10 and the featured driver:");
        foreach (var lap in story.Laps)
        {
            var rows = lap.Order.Where(o => o.Position <= 10 || o.CarIdx == story.FeaturedCar)
                .Select(o => $"P{o.Position} {Name(o.CarIdx)} {(o.LapsDown > 0 ? $"+{o.LapsDown}L" : o.Gap >= 0 ? $"+{o.Gap:0.0}" : "")}{(o.InPit ? " PIT" : "")}");
            sb.AppendLine($"Lap {lap.Lap} (t={R(lap.T)}): " + string.Join(", ", rows));
        }
        return sb.ToString();
    }

    /// <summary>The reply format and the race data: the user's turn, after the system prompt.</summary>
    static string Request(Story story, RaceInfo info, List<Clip> clips, BroadcastConfig cfg) => """
        What to send back: only one JSON object, nothing before or after it, in this shape:
        {"title": "...", "description": "...", "lines": [{"v": 1.5, "text": "...", "priority": 1}, ...]}
        - v: seconds into the highlights video, matching the v values of the clips below
        - priority: 1, 2 or 3
        - lines in time order, covering the whole video from the opening to the sign-off

        The race data:

        """ + Digest(story, info, clips, cfg);

    static async Task<Script?> AskClaude(Story story, RaceInfo info, List<Clip> clips, BroadcastConfig cfg, string focus)
    {
        Console.WriteLine($"Asking {cfg.Model} (Claude Code, on your Claude subscription) to write the narration...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reply = await ClaudeCli.Ask(Request(story, info, clips, cfg), SystemPrompt(cfg, focus), cfg.Model, TimeSpan.FromMinutes(20), cfg.Effort.ToLowerInvariant());
        var script = FromJson(reply);
        Console.WriteLine($"  {script.Lines.Count} lines in {sw.Elapsed.TotalSeconds:0}s: \"{script.Title}\"");
        return script;
    }

    /// <summary>The script from Claude's JSON reply. Text around the JSON (a code fence, a greeting) is ignored.</summary>
    public static Script FromJson(string reply)
    {
        int from = reply.IndexOf('{'), to = reply.LastIndexOf('}');
        if (from < 0 || to < from) throw new JsonException("there's no JSON object in it");
        var node = JsonNode.Parse(reply[from..(to + 1)])!;
        var script = new Script
        {
            Title = node["title"]?.GetValue<string>() ?? "",
            Description = node["description"]?.GetValue<string>() ?? "",
        };
        foreach (var l in node["lines"]?.AsArray() ?? throw new JsonException("it has no \"lines\""))
        {
            var text = Clean(l!["text"]!.GetValue<string>());
            if (text.Length == 0) continue;
            script.Lines.Add(new Line
            {
                T = (l["v"] ?? throw new JsonException("a line has no \"v\" (it may be a reply to the old full-race prompt)")).GetValue<double>(),
                Speaker = "pbp", // the narrator
                Text = text,
                Priority = Math.Clamp(l["priority"]?.GetValue<int>() ?? 2, 1, 3),
            });
        }
        if (script.Lines.Count == 0) throw new JsonException("it has no lines");
        script.Lines.Sort((a, b) => a.T.CompareTo(b.T));
        return script;
    }

    /// <summary>Paste mode: everything Claude needs, in one file to paste into a Claude chat.</summary>
    public static void WritePrompt(Story story, RaceInfo info, List<Clip> clips, BroadcastConfig cfg, string focus, string path, string replyPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SystemPrompt(cfg, focus));
        sb.Append(Request(story, info, clips, cfg));
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine("Narration: paste mode.");
        Console.WriteLine($"  1. Paste everything in {path} into Claude (claude.ai, or Claude Code).");
        Console.WriteLine($"  2. Save Claude's reply (the JSON) as {replyPath}");
        Console.WriteLine("  The recording goes ahead meanwhile; the edit waits for the reply.");
    }

    /// <summary>Paste mode: waits until the reply file is there and reads as a script.</summary>
    public static Script WaitForReply(string replyPath, string promptPath, DateTime newerThanUtc, CancellationToken ct = default)
    {
        DateTime tried = DateTime.MinValue;
        bool told = false;
        while (true)
        {
            // A reply from before the current plan was written for other shots.
            if (File.Exists(replyPath) && File.GetLastWriteTimeUtc(replyPath) != tried && File.GetLastWriteTimeUtc(replyPath) > newerThanUtc)
            {
                tried = File.GetLastWriteTimeUtc(replyPath);
                Thread.Sleep(500); // let the save finish
                try
                {
                    var script = FromJson(File.ReadAllText(replyPath));
                    Console.WriteLine($"Commentary from {Path.GetFileName(replyPath)}: {script.Lines.Count} lines, \"{script.Title}\"");
                    return script;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or FormatException)
                {
                    Console.WriteLine($"  {Path.GetFileName(replyPath)} can't be read ({ex.Message}). Fix or replace it; waiting...");
                }
            }
            else if (!told)
            {
                told = true;
                Console.WriteLine($"Waiting for the commentary: paste {promptPath} into Claude and save the reply as {replyPath}");
            }
            ct.WaitHandle.WaitOne(2000);
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Takes out what a TTS voice would read out literally.</summary>
    static string Clean(string s)
    {
        s = s.Replace("#", "number ").Replace("&", " and ").Replace("*", "").Replace("_", " ");
        s = string.Concat(s.Where(c => !char.IsSurrogate(c)));
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Rule-based narration: each clip's moment read out plainly, with the scene set at the start and the result at the end.</summary>
    public static Script Simple(Story story, RaceInfo info, List<Clip> clips, BroadcastConfig cfg)
    {
        string Surname(int i) => info.Car(i)?.Surname ?? "a car";
        var lines = new List<Line>();
        var offsets = Director.Offsets(clips);
        double length = clips.Sum(c => c.Length);
        var pole = info.Grid.OrderBy(g => g.Value).Select(g => g.Key).FirstOrDefault(-1);
        lines.Add(new Line { T = 0.5, Priority = 1, Text = $"Welcome to {info.Track}. {info.Drivers.Count} drivers, strength of field {RaceInfo.SpokenRating(info.Sof)}." });
        if (pole >= 0) lines.Add(new Line { T = 4.5, Priority = 2, Text = $"{Surname(pole)} on pole." });
        for (int k = 0; k < clips.Count; k++)
        {
            var c = clips[k];
            var e = story.Events.FirstOrDefault(e => e.Kind == c.Kind && Math.Abs(e.T - c.At) < 0.01);
            string text = c.Kind switch
            {
                Kinds.Start => "And they're away!",
                Kinds.Finish => $"{Surname(c.Cars[0])} takes the chequered flag and wins it!",
                Kinds.FeaturedFinish => $"{Surname(c.Cars[0])} brings it home.",
                Kinds.Overtake when e != null => $"{Surname(c.Cars[0])} gets past {Surname(c.Cars[1])} for P{e.Position}.",
                Kinds.Incident => c.Cars.Length > 1 ? "Trouble! " + string.Join(" and ", c.Cars.Take(2).Select(Surname)) + " are caught up in it." : $"{Surname(c.Cars[0])} is in trouble!",
                _ => c.Caption + ".",
            };
            double moment = offsets[k] + c.At - c.From;
            lines.Add(new Line { T = Math.Max(offsets[k] + 0.5, moment - (c.Kind == Kinds.Overtake ? 1.5 : 0)), Priority = 1, Text = text });
        }
        if (info.Results.FirstOrDefault() is { } win)
            lines.Add(new Line { T = length - 9, Priority = 2, Text = $"{Surname(win.CarIdx)} the winner at {info.Track}. Thanks for watching!" });
        lines.Sort((a, b) => a.T.CompareTo(b.T));
        return new Script { Title = $"{info.Track} | Race Highlights", Description = $"{info.CarsSummary} at {info.Track}.", Lines = lines };
    }
}
