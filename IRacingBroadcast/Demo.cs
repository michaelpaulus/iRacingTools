using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>
/// --demo: a made-up five-lap race (a pass for the lead, a spin, a pit stop) run through every pass that doesn't need
/// iRacing: story, shots, commentary, voices, graphics previews and, if ffmpeg is there, the edit onto a plain
/// background. For testing the pipeline offline.
/// </summary>
public static class Demo
{
    const double TrackM = 4000, GreenT = 40, Step = 0.25;
    const int Cars = 10, Laps = 5;

    public static async Task<int> Run(BroadcastConfig cfg, bool useClaude)
    {
        var work = Path.Combine(cfg.WorkDir, "_demo");
        Directory.CreateDirectory(work);
        Console.WriteLine($"Demo race in {work}");
        var info = Info();
        var scan = Simulate(Step, null);
        int featured = 4;

        var story = Detector.Detect(scan, info, featured);
        story.Save(Path.Combine(work, "story.json"));
        foreach (var e in story.Events) Console.WriteLine($"  {e.T - story.GreenT,7:0.0}s  {e.Kind,-15} [{e.Importance:0.0}] {e.Detail}");
        var demoCfg = new BroadcastConfig
        {
            ShowName = cfg.ShowName, Ffmpeg = cfg.Ffmpeg, Encoder = cfg.Encoder, Width = cfg.Width, Height = cfg.Height,
            Fps = 30, GraphicsFps = cfg.GraphicsFps, GameAudio = false, PlayByPlayName = cfg.PlayByPlayName, ColourName = cfg.ColourName,
            HighlightMinutes = 1.5, // it's a short race
        };
        var clips = Director.Plan(story, info, demoCfg, HighlightsOf.Mixed);
        Director.Save(Path.Combine(work, "clips.json"), clips);
        Console.WriteLine($"Highlights: {clips.Count} clips, {clips.Sum(c => c.Length):0} s: " + string.Join("; ", clips.Select(c => $"{c.Caption} ({c.Length:0} s)")));

        var script = useClaude ? await Commentary.Write(story, info, clips, cfg, HighlightsOf.Mixed, true) : Commentary.Simple(story, info, clips, cfg);
        Voices.Synthesize(script, cfg, Path.Combine(work, "voice"));
        script.Save(Path.Combine(work, "narration.json"));
        Console.WriteLine($"Script: {script.Lines.Count} lines, {script.Lines.Sum(l => l.Seconds):0} s of speech.");

        // The shoot, simulated: a 30 Hz timeline with the camera following each clip's shots, the clips back to back.
        var offsets = Director.Offsets(clips);
        var race = Simulate(1 / 30.0, clips.SelectMany(c => c.Shots).OrderBy(s => s.T).ToList());
        var timeline = new List<Frame>();
        for (int k = 0; k < clips.Count; k++)
            foreach (var f in race.Where(f => f.T >= clips[k].From && f.T <= clips[k].To))
                timeline.Add(new Frame { T = f.T, V = offsets[k] + f.T - clips[k].From, State = f.State, CamCar = f.CamCar, CamGroup = f.CamGroup, Cars = f.Cars });
        var toVideo = Composer.TimeMap(timeline);
        double length = clips.Sum(c => c.Length);

        using (var gfx = new BroadcastGraphics(info, story, clips, timeline, demoCfg, toVideo))
        {
            var moments = new List<(string Name, double V)> { ("intro", 4) };
            if (clips.Count > 1) moments.Add(("wipe", offsets[1]));
            if (story.Events.FirstOrDefault(e => e.Kind == Kinds.Overtake && clips.Any(c => e.T >= c.From && e.T <= c.To)) is { } pass) moments.Add(("overtake", toVideo(pass.T) + 1));
            moments.Add(("results", length - 3));
            foreach (var (name, v) in moments.OrderBy(m => m.V))
            {
                var path = Path.Combine(work, $"preview-{name}.png");
                SavePreview(gfx.Render(v), gfx.Width, gfx.Height, path);
                Console.WriteLine($"  graphics preview: {path}");
            }
        }

        var ffmpeg = Ffmpeg.Locate(cfg);
        if (ffmpeg == null)
        {
            Console.WriteLine("ffmpeg isn't installed, so the demo stops before the edit.");
            return 0;
        }
        // A plain moving background stands in for the recordings: every clip is a stretch of the same one.
        var capture = Path.Combine(work, "capture.mkv");
        var p = Process.Start(new ProcessStartInfo(ffmpeg,
            $"-hide_banner -loglevel error -y -f lavfi -i \"testsrc2=s=1280x720:r=30\" -t {(length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)} -c:v libx264 -preset ultrafast \"{capture}\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        var shoot = new ShootResult { Takes = clips.Select((c, k) => new Take { Capture = capture, Start = offsets[k], Length = c.Length }).ToList() };
        Composer.Run(info, story, clips, script, shoot, timeline, demoCfg, work, Path.Combine(work, "demo-highlights.mp4"));
        return 0;
    }

    static RaceInfo Info()
    {
        string[] names = { "Lewis Hammond", "Max Verstrappen", "Charles Lecrerc", "Lando Norrish", "Michael Paulus", "Oscar Pastry",
                           "George Rustle", "Carlos Sines", "Fernando Alonsome", "Yuki Tsunodah" };
        var drivers = names.Select((n, i) => new Driver(i, n, (i * 7 + 3).ToString(), "Ferrari 296 GT3", "GT3", 1, 0xffda59,
            2400 - i * 90, $"{"ABBCCBACDB"[i]} {2.1 + i * 0.17:0.00}", 0x0153db, i % 2 == 0 ? "United Kingdom" : "Netherlands", i == 4)).ToList();
        var cams = new[] { "Nose", "Gearbox", "Roll Bar", "LF Susp", "Gyro", "Cockpit", "Scenic", "TV1", "TV2", "TV3", "Pit Lane", "Chase", "Far Chase", "Blimp", "Chopper" }
            .Select((n, i) => new CameraGroup(i + 1, n)).ToList();
        var results = Enumerable.Range(0, Cars).Select(i => new Result(i, 0, 0, Laps, 90 + i * 0.3, i % 3 * 2, "Running", 0)).ToList();
        // Finish order as the simulation plays out: car 2 wins after passing car 0; car 5 drops back after its spin; car 1's long stop drops it to last.
        int[] finish = { 2, 0, 3, 4, 6, 7, 8, 9, 5, 1 };
        results = finish.Select((c, k) => results[c] with { Position = k + 1, ClassPosition = k + 1, Time = 470 + k * 2.4 }).ToList();
        return new RaceInfo
        {
            Track = "Demo Raceway", TrackLengthM = TrackM, Category = "SportsCar", SimMode = "replay", SubSessionId = 1234,
            PlayerCarIdx = 4, RaceSessionNum = 0, RaceLaps = Laps, Drivers = drivers, Cameras = cams,
            Grid = Enumerable.Range(0, Cars).ToDictionary(i => i, i => i + 1), Results = results,
        };
    }

    /// <summary>The race: cars on a 4 km track at slightly different paces, with a few things happening.</summary>
    static List<Frame> Simulate(double step, List<Shot>? shots)
    {
        var frames = new List<Frame>();
        var dist = Enumerable.Range(0, Cars).Select(i => -0.004 * i - 0.001).ToArray(); // laps; the grid is just before the line
        var lapStart = new double[Cars];
        var last = new float[Cars]; var best = new float[Cars];
        double checkered = double.NaN;
        var finished = new bool[Cars];
        int shot = 0;
        for (double t = 0; ; t += step)
        {
            bool racing = t >= GreenT;
            var f = new Frame { T = t, State = racing ? Frame.StateRacing : 3, Cars = new CarState[Cars] };
            var order = Enumerable.Range(0, Cars).OrderByDescending(i => dist[i]).ToList();
            for (int i = 0; i < Cars; i++)
            {
                double race = t - GreenT;
                double v = !racing ? 0 : Math.Min(1, race / 8) * (44 - i * 0.12 + (i == 2 ? 0.5 : 0) + Math.Sin(t * 0.3 + i) * 0.4);
                int surface = 3; bool pit = false;
                double lap = dist[i];
                // Car 5 spins on lap 3: off the track and almost stopped for a few seconds.
                if (i == 5 && lap > 2.40 && lap < 2.41 + 0.0008) { v = 3; surface = 0; }
                // Car 1 makes a pit stop at the start of lap 3.
                if (i == 1 && lap > 2.98 && lap < 3.02) { v = lap > 2.999 && lap < 3.001 ? 0.2 : 16; pit = true; surface = lap > 2.999 && lap < 3.001 ? 1 : 2; }
                // Cars that took the flag cruise.
                if (finished[i]) v = 20;
                double before = dist[i];
                dist[i] += v * step / TrackM;
                if (Math.Floor(dist[i]) > Math.Floor(before) && dist[i] > 0.5)
                {
                    last[i] = (float)(t - lapStart[i]);
                    if (best[i] <= 0 || last[i] < best[i]) best[i] = last[i];
                    lapStart[i] = t;
                    if (!double.IsNaN(checkered)) finished[i] = true;
                    if (Math.Floor(dist[i]) >= Laps && double.IsNaN(checkered)) { checkered = t; finished[i] = true; }
                }
                else if (Math.Floor(dist[i]) > Math.Floor(before)) lapStart[i] = t;
                double d = dist[i];
                f.Cars[i] = new CarState
                {
                    Lap = (int)Math.Floor(d) + 1, LapCompleted = d < 0 ? -1 : (int)Math.Floor(d), Pct = (float)(d - Math.Floor(d)),
                    Position = order.IndexOf(i) + 1, ClassPosition = order.IndexOf(i), OnPitRoad = pit, Surface = surface,
                    LastLap = last[i] > 0 ? last[i] : -1, BestLap = best[i] > 0 ? best[i] : -1,
                };
            }
            if (!double.IsNaN(checkered)) f.State = Frame.StateCheckered;
            if (shots != null)
            {
                while (shot + 1 < shots.Count && shots[shot + 1].T <= t) shot++;
                f.CamCar = shots[shot].CarIdx; f.CamGroup = shots[shot].Group;
            }
            frames.Add(f);
            if (!double.IsNaN(checkered) && t - checkered > 70) break;
        }
        return frames;
    }

    /// <summary>The transparent graphics over a dark backdrop, to look at.</summary>
    static void SavePreview(byte[] bgra, int w, int h, string path)
    {
        using var overlay = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = overlay.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        overlay.UnlockBits(data);
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            using var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(70, 96, 120), Color.FromArgb(40, 60, 46), 75f);
            g.FillRectangle(bg, 0, 0, w, h);
            g.DrawImage(overlay, 0, 0, w, h);
        }
        bmp.Save(path, ImageFormat.Png);
    }
}
