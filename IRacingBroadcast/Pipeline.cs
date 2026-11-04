using System.Text.RegularExpressions;

/// <summary>
/// One replay's way to a highlights video, as stages that each save their work in the work folder. A stage is done when
/// its output is newer than what it was made from, so a restarted app carries on where it stopped, and redoing a stage
/// makes the stages that depend on it out of date (but keeps the rest: a new script doesn't need a new recording).
/// </summary>
public sealed class Pipeline : IDisposable
{
    public enum Stage { Scan, Plan, Commentary, Shoot, Edit }
    public enum State { NotStarted, Done, OutOfDate, Waiting }

    public static readonly Stage[] Stages = Enum.GetValues<Stage>();

    public string Rpy { get; }
    public string Name { get; }
    public string Work { get; }
    public string Output { get; set; }
    public BroadcastConfig Cfg { get; }
    /// <summary>"claude", "paste" or "simple".</summary>
    public string Mode { get; set; }
    /// <summary>The subsession in the replay's file name, to check iRacing has the right replay open.</summary>
    public long? SubSessionId { get; }
    /// <summary>The stage running now, if any.</summary>
    public Stage? Current { get; private set; }
    public event Action? Changed;

    LiveTelemetry? _telemetry;

    public Pipeline(string rpy, BroadcastConfig cfg)
    {
        Rpy = Path.GetFullPath(rpy);
        Cfg = cfg;
        Mode = cfg.Commentary.ToLowerInvariant();
        Name = Path.GetFileNameWithoutExtension(Rpy);
        Work = Path.Combine(cfg.WorkDir, Name);
        Output = OutputFor(cfg, Name);
        var m = Regex.Match(Name, @"subses(\d+)", RegexOptions.IgnoreCase);
        if (m.Success) SubSessionId = long.Parse(m.Groups[1].Value);
    }

    /// <summary>Where a replay's highlights video goes.</summary>
    public static string OutputFor(BroadcastConfig cfg, string name) => Path.Combine(cfg.OutputDir, name + "-highlights.mp4");

    string P(string file) => Path.Combine(Work, file);
    public string YamlPath => P("session.yaml");
    public string ScanPath => P("scan.jsonl.gz");
    public string StoryPath => P("story.json");
    public string ClipsPath => P("clips.json");
    public string ScriptPath => P("narration.json");
    public string ShootPath => P("takes.json");
    public string TimelinePath => P("takes-timeline.jsonl.gz");
    public string PromptPath => P("narration-prompt.txt");
    public string ReplyPath => P("narration-reply.json");
    /// <summary>The driver to follow, when chosen for this replay (newer than the plan = the plan is out of date).</summary>
    public string FeaturedPath => P("featured.txt");
    /// <summary>What the highlights follow, when chosen for this replay (the same way).</summary>
    public string FocusPath => P("focus.txt");
    /// <summary>The voice engine the script was voiced with.</summary>
    string VoiceEnginePath => P("voice-engine.txt");
    string VoicedWith => File.Exists(VoiceEnginePath) ? File.ReadAllText(VoiceEnginePath).Trim() : "";

    // What marks each stage done, and what it's made from.
    string Marker(Stage s) => s switch
    {
        Stage.Scan => ScanPath, Stage.Plan => ClipsPath, Stage.Commentary => ScriptPath, Stage.Shoot => ShootPath, _ => Output,
    };

    static Stage[] Inputs(Stage s) => s switch
    {
        Stage.Plan => [Stage.Scan], Stage.Commentary or Stage.Shoot => [Stage.Plan], Stage.Edit => [Stage.Commentary, Stage.Shoot], _ => [],
    };

    static DateTime Time(string path) => File.GetLastWriteTimeUtc(path);

    public State StateOf(Stage s)
    {
        var marker = Marker(s);
        if (!File.Exists(marker))
            return s == Stage.Commentary && Mode == "paste" && StateOf(Stage.Plan) == State.Done ? State.Waiting : State.NotStarted;
        foreach (var input in Inputs(s))
            if (StateOf(input) != State.Done || Time(Marker(input)) > Time(marker)) return State.OutOfDate;
        if (s == Stage.Plan && new[] { FeaturedPath, FocusPath }.Any(p => File.Exists(p) && Time(p) > Time(marker))) return State.OutOfDate;
        // A reply pasted after the script was made replaces it.
        if (s == Stage.Commentary && Mode == "paste" && File.Exists(ReplyPath) && Time(ReplyPath) > Time(marker)) return State.OutOfDate;
        // Voiced with the other engine than the one chosen now.
        if (s == Stage.Commentary && VoicedWith != Voices.Stamp(Cfg)) return State.OutOfDate;
        return State.Done;
    }

    public DateTime? DoneAt(Stage s) => File.Exists(Marker(s)) ? File.GetLastWriteTime(Marker(s)) : null;

    public RaceInfo? Info => File.Exists(YamlPath) ? RaceInfo.Parse(File.ReadAllText(YamlPath)) : null;

    public string FeaturedName
    {
        get => File.Exists(FeaturedPath) ? File.ReadAllText(FeaturedPath).Trim() : Cfg.FeaturedDriver;
        set { Directory.CreateDirectory(Work); File.WriteAllText(FeaturedPath, value); }
    }

    /// <summary>"mixed", "mine" or "race" (see <see cref="HighlightsOf"/>).</summary>
    public string FocusName
    {
        get
        {
            var f = (File.Exists(FocusPath) ? File.ReadAllText(FocusPath) : Cfg.HighlightFocus).Trim().ToLowerInvariant();
            return HighlightsOf.All.Contains(f) ? f : HighlightsOf.Mixed;
        }
        set { Directory.CreateDirectory(Work); File.WriteAllText(FocusPath, value); }
    }

    public int Featured(RaceInfo info)
    {
        var name = FeaturedName;
        if (name.Length > 0)
        {
            var d = info.Drivers.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? info.Drivers.FirstOrDefault(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (d != null) return d.CarIdx;
            Console.WriteLine($"No driver called \"{name}\" in this race; featuring the replay's own driver.");
        }
        return info.Drivers.Any(d => d.CarIdx == info.PlayerCarIdx) ? info.PlayerCarIdx : -1;
    }

    /// <summary>
    /// Runs every stage that isn't done (and, with <paramref name="redoFrom"/>, that stage and all after it). In paste
    /// mode the recording goes ahead while the reply is outstanding; the edit waits for it.
    /// </summary>
    public async Task RunAll(CancellationToken ct, Stage? redoFrom = null)
    {
        Ffmpeg.Require(Cfg); // before the recording: it and the edit need it
        foreach (var s in Stages)
        {
            if (s == Stage.Edit && StateOf(Stage.Commentary) != State.Done && Mode == "paste")
            {
                Set(Stage.Commentary);
                WritePrompt();
                Commentary.WaitForReply(ReplyPath, PromptPath, Time(ClipsPath), ct);
                await Run(Stage.Commentary, ct);
            }
            if (StateOf(s) != State.Done || (redoFrom is Stage from && s >= from)) await Run(s, ct);
        }
        Console.WriteLine();
        Console.WriteLine($"Video: {Output}");
        Console.WriteLine($"  Subtitles and the title, description and chapters are next to it (.srt, .txt).");
        Console.WriteLine($"  The clips in {Work} can be deleted once you're happy with the video.");
    }

    /// <summary>Runs one stage, whatever its state.</summary>
    public async Task Run(Stage s, CancellationToken ct)
    {
        Directory.CreateDirectory(Work);
        Set(s);
        try
        {
            Console.WriteLine();
            Console.WriteLine($"=== {s} ===");
            switch (s)
            {
                case Stage.Scan: Scan(ct); break;
                case Stage.Plan: Plan(); break;
                case Stage.Commentary: await WriteCommentary(ct); break;
                case Stage.Shoot: Shoot(ct); break;
                case Stage.Edit: Edit(ct); break;
            }
        }
        finally { Set(null); }
    }

    void Set(Stage? s) { Current = s; Changed?.Invoke(); }

    LiveTelemetry Telemetry => _telemetry ??= new LiveTelemetry();

    void Scan(CancellationToken ct)
    {
        var live = Replay.Open(Telemetry, Rpy, SubSessionId, ct);
        var (scan, _) = Scanner.Run(Telemetry, live, Cfg, ct);
        if (scan.Count < 100) throw new InvalidOperationException("The scan recorded almost nothing: is the race in this replay?");
        File.WriteAllText(YamlPath, Telemetry.SessionInfo); // the final results are in it now
        FrameFile.Write(ScanPath + ".part", scan);
        File.Move(ScanPath + ".part", ScanPath, true);
    }

    void Plan()
    {
        var info = Info ?? throw new InvalidOperationException("Scan first.");
        var scan = FrameFile.Read(ScanPath);
        int featured = Featured(info);
        var story = Detector.Detect(scan, info, featured);
        story.Save(StoryPath);
        Console.WriteLine($"Story: {story.Events.Count} events " +
            $"({string.Join(", ", story.Events.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}"))}); featured: {info.Car(featured)?.Name ?? "none"}.");
        var clips = Director.Plan(story, info, Cfg, FocusName);
        Director.Save(ClipsPath, clips);
        Console.WriteLine($"Highlights ({FocusName}): {clips.Count} clips, {clips.Sum(c => c.Length) / 60:0.0} minutes:");
        foreach (var c in clips) Console.WriteLine($"  {TimeSpan.FromSeconds(Math.Max(0, c.From - story.GreenT)):mm\\:ss} into the race   {c.Length,3:0} s   {c.Caption}");
    }

    async Task WriteCommentary(CancellationToken ct)
    {
        var info = Info!;
        var story = Story.Load(StoryPath);
        var clips = Director.Load(ClipsPath);
        Script script;
        if (Mode == "paste")
        {
            if (!File.Exists(ReplyPath) || Time(ReplyPath) < Time(ClipsPath))
            {
                WritePrompt();
                return; // not done until the reply is in
            }
            script = Commentary.FromJson(File.ReadAllText(ReplyPath));
            Console.WriteLine($"Narration from your reply: {script.Lines.Count} lines, \"{script.Title}\"");
        }
        else script = await Commentary.Write(story, info, clips, Cfg, FocusName, Mode != "simple");
        Voices.Synthesize(script, Cfg, P("voice"), ct);
        File.WriteAllText(VoiceEnginePath, Voices.Stamp(Cfg));
        script.Save(ScriptPath);
    }

    /// <summary>The prompt to paste into Claude, made fresh if the plan changed since.</summary>
    public void WritePrompt()
    {
        if (File.Exists(PromptPath) && Time(PromptPath) > Time(ClipsPath)) { Console.WriteLine($"The narration prompt is ready: {PromptPath}"); return; }
        Commentary.WritePrompt(Story.Load(StoryPath), Info!, Director.Load(ClipsPath), Cfg, FocusName, PromptPath, ReplyPath);
    }

    void Shoot(CancellationToken ct)
    {
        var info = Info!;
        Replay.Open(Telemetry, Rpy, SubSessionId, ct);
        var shoot = Recorder.Run(Telemetry, info, Director.Load(ClipsPath), Cfg, Work, TimelinePath, ct);
        shoot.Save(ShootPath);
    }

    /// <summary>Whether everything this stage is made from is done.</summary>
    public bool CanRun(Stage s) => Inputs(s).All(i => StateOf(i) == State.Done);

    void Edit(CancellationToken ct)
    {
        if (!CanRun(Stage.Edit)) throw new InvalidOperationException("The narration and the recording have to be done before the edit.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Output))!);
        Composer.Run(Info!, Story.Load(StoryPath), Director.Load(ClipsPath), Script.Load(ScriptPath), ShootResult.Load(ShootPath),
            FrameFile.Read(TimelinePath), Cfg, Work, Output, ct);
    }

    public void Dispose() => _telemetry?.Dispose();
}
