using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// IRacingCoach - live corner coaching against a reference lap, plus a push-to-talk race engineer.
//   IRacingCoach                      run live alongside iRacing
//   IRacingCoach bind                 choose the push-to-talk key or wheel button
//   IRacingCoach replay <file.ibt> [speed] [--quiet]   coach a recorded session (speed 0 = as fast as possible)
//   IRacingCoach ask "<question>" [file.ibt]           ask the engineer from the console (optionally after replaying a file)

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var baseDir = Environment.CurrentDirectory;
var cfg = CoachConfig.Load(baseDir);
string command = args.Length > 0 ? args[0].ToLowerInvariant() : "live";

if (command == "bind")
{
    Console.WriteLine("Press and release the key or wheel button you want for push-to-talk (30 s)...");
    Thread.Sleep(300);
    if (PushToTalk.Bind(cfg.PushToTalk, TimeSpan.FromSeconds(30)))
    {
        cfg.Save(baseDir);
        Console.WriteLine($"Push-to-talk set to {new PushToTalk(cfg.PushToTalk).Describe()}. Saved to coach.json.");
    }
    else Console.WriteLine("Nothing pressed - push-to-talk unchanged.");
    return;
}

if (!File.Exists(cfg.ReferenceCsv))
{
    Console.WriteLine($"Reference lap not found: '{cfg.ReferenceCsv}'. Set ReferenceCsv in coach.json to a Garage 61 CSV export.");
    return;
}

var reference = ReferenceLap.LoadGarage61Csv(cfg.ReferenceCsv, cfg.ReferenceName, cfg.DistanceBins, cfg.TrackLengthKm);
CornerNames.Apply(reference, Path.Combine(baseDir, "corners.json"));
var notesPath = Path.Combine(baseDir, "coach_notes.md");
var notes = File.Exists(notesPath) ? File.ReadAllText(notesPath) : "";
Console.WriteLine($"Reference: {reference.Name} {reference.LapTime:F3}s, {reference.Corners.Count} corners.");

var engineer = new Engineer(cfg, notes, reference);

switch (command)
{
    case "replay" when args.Length >= 2:
    {
        double speed = args.Length >= 3 && double.TryParse(args[2], out var s) ? s : 0;
        bool quiet = args.Contains("--quiet");
        using var src = new IbtReplay(args[1], speed);
        var coach = Session.Run(src, cfg, reference, engineer, baseDir, live: false, quiet, showOverlay: speed > 0 && cfg.ShowOverlay);
        Console.WriteLine();
        Console.WriteLine(coach.Summary());
        break;
    }
    case "ask" when args.Length >= 2:
    {
        string snapshot = "Not connected to iRacing.";
        if (args.Length >= 3)
        {
            using var src = new IbtReplay(args[2], 0);
            var coach = Session.Run(src, cfg, reference, engineer, baseDir, live: false, quiet: true, showOverlay: false);
            snapshot = Session.Snapshot(src, coach, trackOk: true);
        }
        Console.WriteLine(await engineer.AskAsync(args[1], snapshot));
        break;
    }
    case "overlay-png" when args.Length >= 5:
    {
        // Renders the overlay as it looks at a given lap and lap fraction of a recorded session, for checking the layout.
        int lap = int.Parse(args[2]);
        double at = double.Parse(args[3], CultureInfo.InvariantCulture);
        using var src = new IbtReplay(args[1], 0);
        var coach = new CornerCoach(reference, cfg);
        var tracker = new GhostTracker(reference, cfg.TrackLengthKm);
        tracker.SetSectors(Session.Sectors(src.SessionInfo));
        while (src.WaitForFrame(0))
        {
            coach.OnFrame(src);
            double pct = src.Get("LapDistPct");
            tracker.Update(pct, src.Get("SessionTime"), src.Get("Brake"), src.Get("Throttle"), src.Get("YawNorth"),
                src.Get("IsOnTrack") > 0, src.Get("OnPitRoad") > 0, src.Get("Speed"));
            if ((int)src.Get("Lap") == lap && pct >= at) break;
        }
        using var bmp = new System.Drawing.Bitmap(cfg.OverlayWidth, cfg.OverlayHeight);
        var overlayPainter = new OverlayPainter(reference);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) overlayPainter.Paint(g, bmp.Size, tracker.State);
        bmp.Save(args[4], System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"Saved {args[4]}: {tracker.State}");
        Console.WriteLine($"Relative: {tracker.Relative}");

        var stem = Path.Combine(Path.GetDirectoryName(args[4])!, Path.GetFileNameWithoutExtension(args[4]));
        foreach (var (pw, ph) in new[] { (cfg.PedalWidth, cfg.PedalHeight), (560, 260) }) // default size and a resized one
        {
            using var pb = new System.Drawing.Bitmap(pw, ph);
            using (var g = System.Drawing.Graphics.FromImage(pb)) overlayPainter.PaintPedalWindow(g, pb.Size, tracker.Pedals);
            pb.Save($"{stem}_pedals_{pw}x{ph}.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        using (var sb = new System.Drawing.Bitmap(cfg.StripWidth, cfg.StripHeight))
        {
            using (var g = System.Drawing.Graphics.FromImage(sb)) new StripPainter(reference.Name).Paint(g, sb.Size, tracker.Relative);
            sb.Save(stem + "_strip.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        // Marker preview at a quarter of 2560x1440, on grey so the box is visible.
        double fovDeg = cfg.MarkerFovDeg > 0 ? cfg.MarkerFovDeg : Overlays.FovFromIRacing(70);
        using (var mb = new System.Drawing.Bitmap(640, 360))
        {
            using var g = System.Drawing.Graphics.FromImage(mb);
            g.Clear(System.Drawing.Color.FromArgb(60, 64, 70));
            var mp = new MarkerPainter(reference.Name, fovDeg, cfg);
            if (mp.Compute(tracker.Relative, mb.Size) is { } layout) mp.Paint(g, layout);
            mb.Save(stem + "_marker.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        Console.WriteLine($"Marker FOV {fovDeg:F1} deg");
        break;
    }
    default:
    {
        using var src = new LiveTelemetry();
        Session.Run(src, cfg, reference, engineer, baseDir, live: true, quiet: false, showOverlay: cfg.ShowOverlay);
        break;
    }
}

static class Session
{
    public static CornerCoach Run(ITelemetrySource src, CoachConfig cfg, ReferenceLap reference, Engineer engineer,
        string baseDir, bool live, bool quiet, bool showOverlay)
    {
        var coach = new CornerCoach(reference, cfg);
        var tracker = new GhostTracker(reference, cfg.TrackLengthKm);
        double fov = cfg.MarkerFovDeg > 0 ? cfg.MarkerFovDeg : Overlays.FovFromIRacing(70);
        if (showOverlay && cfg.ShowWindscreenMarker) Console.WriteLine($"Windscreen marker: {fov:F1} degree field of view.");
        Action? toggleOverlay = showOverlay ? Overlays.Start(tracker, reference, cfg, baseDir, fov) : null;
        bool toggleWasDown = false;
        using var speaker = new Speaker(cfg) { Muted = quiet };
        var lastCallout = DateTime.MinValue;
        coach.Callout += text =>
        {
            if (!cfg.SpokenCallouts) { Console.WriteLine($"  -- {text}"); return; }
            // In replay at high speed, wall-clock spacing is meaningless; print everything.
            if (live && (DateTime.UtcNow - lastCallout).TotalSeconds < cfg.MinSecondsBetweenCallouts) return;
            lastCallout = DateTime.UtcNow;
            speaker.Say(text);
        };

        var ptt = new PushToTalk(cfg.PushToTalk);
        Listener? listener = null;
        if (live)
        {
            try { listener = new Listener(); }
            catch (Exception ex) { Console.WriteLine($"Speech recognition unavailable ({ex.Message}); type questions instead."); }
        }

        var questions = new ConcurrentQueue<string>();
        if (live)
        {
            Console.WriteLine($"Push-to-talk: {ptt.Describe()}. Engineer: {(engineer.Available ? cfg.Model : "OFF (Claude CLI not installed)")}.");
            Console.WriteLine("You can also type a question and press Enter. Ctrl+C to quit.");
            new Thread(() =>
            {
                while (Console.ReadLine() is { } line)
                    if (!string.IsNullOrWhiteSpace(line)) questions.Enqueue(line.Trim());
            }) { IsBackground = true }.Start();
        }

        int lastSessionInfo = -1;
        bool trackOk = false, wasHeld = false, waitingShown = false;

        while (true)
        {
            if (src.WaitForFrame(200))
            {
                waitingShown = false;
                if (src.SessionInfoVersion != lastSessionInfo)
                {
                    lastSessionInfo = src.SessionInfoVersion;
                    var track = Yaml(src.SessionInfo, "TrackName");
                    bool ok = string.Equals(track, cfg.ReferenceTrack, StringComparison.OrdinalIgnoreCase);
                    if (ok != trackOk || lastSessionInfo <= 1)
                    {
                        Console.WriteLine(ok ? $"Track {track}: corner coaching ON." : $"Track {track}: no reference lap for this track, corner coaching OFF.");
                    }
                    trackOk = ok;
                    tracker.SetSectors(Sectors(src.SessionInfo));
                }
                if (trackOk)
                {
                    coach.OnFrame(src);
                    tracker.Update(src.Get("LapDistPct"), src.Get("SessionTime"), src.Get("Brake"), src.Get("Throttle"), src.Get("YawNorth"),
                        src.Get("IsOnTrack") > 0, src.Get("OnPitRoad") > 0, src.Get("Speed"));
                }
            }
            else if (src.IsFinished) break;
            else if (live && !src.IsConnected && !waitingShown)
            {
                Console.WriteLine("Waiting for iRacing...");
                waitingShown = true;
            }

            if (toggleOverlay != null)
            {
                bool down = PushToTalk.IsKeyDown(cfg.OverlayToggleKey);
                if (down && !toggleWasDown) toggleOverlay();
                toggleWasDown = down;
            }

            if (live)
            {
                bool held = ptt.IsHeld();
                if (held && !wasHeld && listener != null)
                {
                    speaker.Pause();
                    listener.Start();
                    Console.WriteLine("  [listening]");
                }
                else if (!held && wasHeld && listener != null)
                {
                    speaker.Resume();
                    var l = listener;
                    Task.Run(() =>
                    {
                        var heard = l.Stop();
                        if (heard.Length > 0) questions.Enqueue(heard);
                        else speaker.Say("Didn't catch that.", important: true);
                    });
                }
                wasHeld = held;
            }

            while (questions.TryDequeue(out var q))
            {
                Console.WriteLine($"  [driver] {q}");
                string snapshot = Snapshot(src, coach, trackOk); // built here: telemetry is read on this thread only
                Task.Run(async () => speaker.Say(await engineer.AskAsync(q, snapshot), important: true));
            }
        }

        // Let queued speech finish when replaying with audio.
        if (!quiet) Thread.Sleep(1500);
        return coach;
    }

    public static string Snapshot(ITelemetrySource t, CornerCoach coach, bool trackOk)
    {
        if (!t.IsConnected) return "Not connected to iRacing.";
        var si = t.SessionInfo;
        var sb = new StringBuilder();
        sb.AppendLine($"Track: {Yaml(si, "TrackDisplayName")} ({Yaml(si, "TrackName")}), {Yaml(si, "TrackLength")}. Car: {PlayerCar(si)}.");
        sb.AppendLine($"Weather: air {Yaml(si, "TrackAirTemp")}, track {Yaml(si, "TrackSurfaceTemp")}, {Yaml(si, "TrackPrecipitation")} precipitation.");

        string V(string name, string fmt = "F1") => t.Has(name) && !double.IsNaN(t.Get(name)) ? t.Get(name).ToString(fmt) : "n/a";
        sb.AppendLine($"Now: lap {V("Lap", "F0")}, {V("LapDistPct", "P0")} around, speed {t.Get("Speed") * 3.6:F0} km/h, on pit road {t.Get("OnPitRoad") > 0}.");
        sb.AppendLine($"Last lap {V("LapLastLapTime", "F3")} s, best {V("LapBestLapTime", "F3")} s, position {V("PlayerCarPosition", "F0")}, session time remaining {V("SessionTimeRemain", "F0")} s.");

        var fuelLaps = coach.Laps.Where(l => l.Valid).ToList();
        string fuelPerLap = "unknown yet";
        if (fuelLaps.Count >= 2)
        {
            var a = fuelLaps[^2]; var b = fuelLaps[^1];
            if (b.Lap == a.Lap + 1 && a.FuelAtStart > b.FuelAtStart) fuelPerLap = $"{a.FuelAtStart - b.FuelAtStart:F2} L";
        }
        sb.AppendLine($"Fuel {V("FuelLevel")} L, using about {fuelPerLap} per lap.");
        sb.AppendLine($"In-car settings: brake bias {V("dcBrakeBias", "F1")}, traction control {V("dcTractionControl", "F0")}, ABS {V("dcABS", "F0")}.");

        if (trackOk) sb.AppendLine(coach.Summary());
        else sb.AppendLine("Corner coaching is off: the reference lap is for a different track.");

        int setup = si.IndexOf("CarSetup:", StringComparison.Ordinal);
        if (setup >= 0)
        {
            var text = si[setup..];
            sb.AppendLine("Current car setup (iRacing garage values):");
            sb.AppendLine(text.Length > 4000 ? text[..4000] : text);
        }
        return sb.ToString();
    }

    /// <summary>iRacing's sector split points (lap fraction) from SplitTimeInfo.</summary>
    public static IEnumerable<double> Sectors(string sessionInfo) =>
        Regex.Matches(sessionInfo, @"SectorStartPct:\s*([\d.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));

    public static string Yaml(string yaml, string key)
    {
        var m = Regex.Match(yaml, $@"^\s*{Regex.Escape(key)}:\s*(.+?)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : "";
    }

    static string PlayerCar(string si)
    {
        var idx = Yaml(si, "DriverCarIdx");
        foreach (var block in si.Split("- CarIdx: "))
            if (block.StartsWith(idx + "\n") || block.StartsWith(idx + "\r"))
                return Yaml(block, "CarScreenName");
        return "unknown";
    }
}

static class CornerNames
{
    // Approximate apex positions on the Nürburgring Combined (Gesamtstrecke VLN) layout.
    static readonly (double Km, string Name)[] Known =
    {
        (0.63, "Turn 1"), (7.08, "Aremberg"), (8.42, "Adenauer Forst"), (10.61, "Wehrseifen"), (14.77, "Karussell"),
    };

    record Entry(int Index, double ApexKm, string Name);

    /// <summary>Names corners from corners.json (editable), creating it on first run.</summary>
    public static void Apply(ReferenceLap r, string path)
    {
        var saved = new List<Entry>();
        if (File.Exists(path))
            saved = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new();

        foreach (var c in r.Corners)
        {
            var s = saved.FirstOrDefault(e => e.Index == c.Index && Math.Abs(e.ApexKm - c.ApexKm) < 0.2);
            var k = Known.FirstOrDefault(x => Math.Abs(x.Km - c.ApexKm) < 0.15);
            c.Name = s?.Name ?? k.Name ?? $"kilometre {c.ApexKm:F1}";
        }
        var entries = r.Corners.Select(c => new Entry(c.Index, Math.Round(c.ApexKm, 2), c.Name)).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}
