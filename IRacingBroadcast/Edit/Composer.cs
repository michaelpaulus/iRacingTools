using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>
/// Pass 4: the final edit. Joins the clips' recordings into one picture, lays the voiced lines on the video's
/// timeline, draws the graphics frame by frame and has ffmpeg put picture, graphics, narration and the ducked game
/// sound together into a YouTube-ready mp4.
/// </summary>
public static class Composer
{
    public static void Run(RaceInfo info, Story story, List<Clip> clips, Script script, ShootResult shoot, List<Frame> timeline, BroadcastConfig cfg,
        string work, string output, CancellationToken ct = default)
    {
        var ffmpeg = Ffmpeg.Require(cfg);
        if (timeline.Count < 2) throw new InvalidOperationException("The recording's timeline is empty.");
        if (shoot.Takes.Count != clips.Count) throw new InvalidOperationException("The recording doesn't match the plan: record again.");
        double length = shoot.Takes.Sum(k => k.Length);
        var offsets = Director.Offsets(clips);
        Func<double, double> toVideo = TimeMap(timeline);

        // Narration track and subtitles. Lines are timed in video seconds already.
        var placed = Mixer.Schedule(script, v => v, length);
        Console.WriteLine($"Narration: {placed.Count} of {script.Lines.Count} lines fit, {placed.Sum(p => p.Line.Seconds) / Math.Max(1, length) * 100:0}% talk time.");
        var commentary = Path.Combine(work, "commentary.wav");
        Mixer.WriteTrack(placed, length, commentary);
        var srt = Path.ChangeExtension(output, ".srt");
        Mixer.WriteSubtitles(placed, cfg, srt);
        WriteDescription(info, clips, script, length, Path.ChangeExtension(output, ".txt"));

        // ffmpeg inputs: each clip's capture, the graphics on stdin, the narration, each clip's game sound.
        var encoder = Ffmpeg.Encoder(ffmpeg, cfg.Encoder);
        string Inv(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        var args = new StringBuilder("-hide_banner -loglevel warning -stats -y ");
        int n = shoot.Takes.Count;
        foreach (var take in shoot.Takes) args.Append($"-i \"{take.Capture}\" ");
        args.Append($"-f rawvideo -pix_fmt bgra -s {cfg.Width}x{cfg.Height} -r {cfg.GraphicsFps} -i pipe:0 ");
        args.Append($"-i \"{commentary}\" ");
        bool game = cfg.GameAudio && shoot.Takes.All(k => k.GameAudio.Length > 0 && File.Exists(k.GameAudio));
        if (game) foreach (var take in shoot.Takes) args.Append($"-i \"{take.GameAudio}\" ");
        int w = cfg.Width, h = cfg.Height, gfxIn = n, voiceIn = n + 1;
        var filter = new StringBuilder();
        for (int k = 0; k < n; k++)
        {
            var take = shoot.Takes[k];
            filter.Append($"[{k}:v]trim=start={Inv(take.Start)}:duration={Inv(take.Length)},setpts=PTS-STARTPTS,");
            filter.Append($"scale={w}:{h}:force_original_aspect_ratio=decrease:flags=lanczos,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,fps={cfg.Fps},setsar=1[c{k}];");
        }
        filter.Append(string.Concat(Enumerable.Range(0, n).Select(k => $"[c{k}]"))).Append($"concat=n={n}:v=1:a=0[base];");
        filter.Append($"[base][{gfxIn}:v]overlay=0:0:format=auto:eof_action=repeat,format=yuv420p[v];");
        if (game)
        {
            // Each clip's sound from where its picture starts (the sound began a moment before the capture's clip).
            for (int k = 0; k < n; k++)
            {
                var take = shoot.Takes[k];
                double from = take.Start - take.GameAudioAt;
                string lead = from >= 0 ? $"atrim=start={Inv(from)}:duration={Inv(take.Length)}" : $"adelay={Inv(-from * 1000)}:all=1,atrim=duration={Inv(take.Length)}";
                filter.Append($"[{voiceIn + 1 + k}:a]{lead},asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=stereo,apad=whole_dur={Inv(take.Length)}[g{k}];");
            }
            filter.Append(string.Concat(Enumerable.Range(0, n).Select(k => $"[g{k}]"))).Append($"concat=n={n}:v=0:a=1[game];");
            filter.Append($"[{voiceIn}:a]aresample=48000,pan=stereo|c0=c0|c1=c0,asplit=2[voice][key];");
            filter.Append($"[game]volume={Inv(cfg.GameAudioDb)}dB");
            // Drivers' voice chat plays from the replay on the same device as the engines: silence the sim while anyone talks.
            var talk = RadioTalk(timeline);
            if (talk.Count > 0)
            {
                filter.Append(",volume=0:enable=").Append(string.Join("+", talk.Select(r => $"between(t\\,{Inv(r.From)}\\,{Inv(r.To)})")));
                Console.WriteLine($"Voice chat: the sim sound is muted for {talk.Count} radio messages ({talk.Sum(r => r.To - r.From):0} s).");
            }
            filter.Append("[bed];");
            // The engines dip under the voices (sidechain), like a real broadcast mix.
            filter.Append("[bed][key]sidechaincompress=threshold=0.02:ratio=6:attack=20:release=400[ducked];");
            filter.Append("[voice][ducked]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.95[a]");
        }
        else filter.Append($"[{voiceIn}:a]aresample=48000,pan=stereo|c0=c0|c1=c0,alimiter=limit=0.95[a]");
        args.Append($"-filter_complex \"{filter}\" -map \"[v]\" -map \"[a]\" ");
        args.Append(Ffmpeg.EncoderArgs(encoder, master: false)).Append(' ');
        // Into a part file: the video only appears under its name once it's complete (that's what marks the edit done).
        var part = Path.ChangeExtension(output, ".part.mp4");
        args.Append($"-c:a aac -b:a 192k -t {Inv(length)} -movflags +faststart \"{part}\"");

        Console.WriteLine($"Editing {length / 60:0.0} minutes into {output}...");
        Console.WriteLine($"  ffmpeg {args}");
        var psi = new ProcessStartInfo(ffmpeg, args.ToString()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        using var ff = Process.Start(psi)!;
        var errors = new StringBuilder();
        ff.ErrorDataReceived += (_, e) => { if (e.Data != null && !e.Data.StartsWith("frame=")) lock (errors) errors.AppendLine(e.Data); };
        ff.BeginErrorReadLine();

        using var gfx = new BroadcastGraphics(info, story, clips, timeline, cfg, toVideo);
        int frames = (int)Math.Ceiling(length * cfg.GraphicsFps);
        var sw = Stopwatch.StartNew();
        var report = Stopwatch.StartNew();
        var stdin = ff.StandardInput.BaseStream;
        try
        {
            for (int k = 0; k < frames; k++)
            {
                if (ct.IsCancellationRequested)
                {
                    try { ff.Kill(); } catch (InvalidOperationException) { }
                    ff.WaitForExit();
                    File.Delete(part);
                    ct.ThrowIfCancellationRequested();
                }
                stdin.Write(gfx.Render(k / (double)cfg.GraphicsFps));
                if (report.Elapsed > TimeSpan.FromSeconds(10))
                {
                    report.Restart();
                    double done = (k + 1) / (double)frames;
                    Console.WriteLine($"  {done * 100:0}% ({TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / done * (1 - done)):m\\:ss} left)");
                }
            }
            stdin.Close();
        }
        catch (IOException)
        {
            // ffmpeg quit early; its messages say why.
        }
        ff.WaitForExit();
        if (ff.ExitCode != 0 || !File.Exists(part))
            throw new InvalidOperationException($"ffmpeg failed (exit {ff.ExitCode}):\n{errors}");
        File.Move(part, output, true);
        Console.WriteLine($"Done in {sw.Elapsed:m\\:ss}: {output}");
    }

    /// <summary>
    /// When someone is talking on voice chat, in video seconds: padded a little (the SDK flag lags the voice) and merged.
    /// </summary>
    public static List<(double From, double To)> RadioTalk(List<Frame> timeline)
    {
        const double Before = 0.4, After = 0.6;
        var talk = new List<(double From, double To)>();
        double start = double.NaN, last = 0;
        foreach (var f in timeline.Where(f => double.IsFinite(f.V)).OrderBy(f => f.V))
        {
            if (f.Radio >= 0 && double.IsNaN(start)) start = f.V;
            else if (f.Radio < 0 && !double.IsNaN(start)) { Add(start, f.V); start = double.NaN; }
            last = f.V;
        }
        if (!double.IsNaN(start)) Add(start, last);
        return talk;

        void Add(double a, double b)
        {
            a = Math.Max(0, a - Before); b += After;
            if (talk.Count > 0 && a <= talk[^1].To) talk[^1] = (talk[^1].From, Math.Max(b, talk[^1].To));
            else talk.Add((a, b));
        }
    }

    /// <summary>
    /// SessionTime → seconds into the video, interpolated from the timeline (extrapolated at the ends). Only meaningful
    /// for moments inside a clip: between clips the video cuts.
    /// </summary>
    public static Func<double, double> TimeMap(List<Frame> timeline)
    {
        var ts = timeline.Select(f => f.T).ToArray();
        var vs = timeline.Select(f => f.V).ToArray();
        return t =>
        {
            if (t <= ts[0]) return vs[0] - (ts[0] - t);
            if (t >= ts[^1]) return vs[^1] + (t - ts[^1]);
            int i = Array.BinarySearch(ts, t);
            if (i >= 0) return vs[i];
            i = ~i;
            double a = (t - ts[i - 1]) / (ts[i] - ts[i - 1]);
            return vs[i - 1] + (vs[i] - vs[i - 1]) * a;
        };
    }

    /// <summary>Title, description and YouTube chapters, ready to paste.</summary>
    static void WriteDescription(RaceInfo info, List<Clip> clips, Script script, double length, string path)
    {
        // A chapter per clip, where they're at least 10 s apart (YouTube's minimum); the first is at 0:00.
        var chapters = new List<(double At, string Title)>();
        var offsets = Director.Offsets(clips);
        for (int k = 0; k < clips.Count; k++)
        {
            double at = k == 0 ? 0 : offsets[k];
            if (chapters.Count > 0 && (at - chapters[^1].At < 10 || length - at < 10)) continue;
            chapters.Add((at, clips[k].Caption));
        }
        var sb = new StringBuilder();
        sb.AppendLine(script.Title);
        sb.AppendLine();
        sb.AppendLine(script.Description);
        sb.AppendLine();
        foreach (var (at, title) in chapters.OrderBy(c => c.At))
            sb.AppendLine($"{TimeSpan.FromSeconds(at).ToString(at >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)} {title}");
        sb.AppendLine();
        sb.AppendLine($"{info.Track} · {info.CarsSummary} · SOF {RaceInfo.Rating(info.Sof)}");
        sb.AppendLine("Narration written by Claude and voiced with text-to-speech. Produced with IRacingBroadcast.");
        File.WriteAllText(path, sb.ToString());
    }
}
