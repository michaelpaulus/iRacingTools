using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using SherpaOnnx;

/// <summary>
/// Speaks the script into one WAV per line: with local Kokoro voices (sherpa-onnx, on the CPU) or with Chatterbox
/// (expressive, on the GPU through its own Python install).
/// </summary>
public static class Voices
{
    public static bool UsesChatterbox(BroadcastConfig cfg) => cfg.VoiceEngine.Equals("chatterbox", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the narration is voiced with: a change re-voices it.</summary>
    public static string Stamp(BroadcastConfig cfg) =>
        UsesChatterbox(cfg) ? "chatterbox" : $"kokoro {cfg.KokoroVoice} {cfg.KokoroColourVoice}".ToLowerInvariant();

    /// <param name="dir">The voice folder; Chatterbox's lines go next to it in "&lt;dir&gt;-chatterbox".</param>
    public static void Synthesize(Script script, BroadcastConfig cfg, string dir, CancellationToken ct = default)
    {
        if (UsesChatterbox(cfg)) { Chatterbox(script, cfg, dir + "-chatterbox", ct); return; }
        Directory.CreateDirectory(dir);
        (OfflineTts Tts, int Id) pbp = Kokoro(cfg, cfg.KokoroVoice);
        using var _ = pbp.Tts;
        // A narrator-only script doesn't need the second voice.
        (OfflineTts? Tts, int Id) colour = !script.Lines.Any(l => l.Speaker == "colour") ? (null, 0) : Kokoro(cfg, cfg.KokoroColourVoice);
        using var __ = colour.Tts;
        Console.WriteLine($"Voicing {script.Lines.Count} lines with Kokoro ({cfg.KokoroVoice})...");
        for (int i = 0; i < script.Lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var line = script.Lines[i];
            var (tts, id) = line.Speaker == "colour" && colour.Tts != null ? (colour.Tts, colour.Id) : pbp;
            var audio = tts.Generate(line.Text, (float)Math.Clamp(cfg.VoiceSpeed, 0.5, 2.0), id);
            line.Audio = Path.Combine(dir, $"{i:0000}-{line.Speaker}.wav");
            line.Seconds = audio.Samples.Length / (double)Math.Max(1, audio.SampleRate);
            using var w = new WaveFileWriter(line.Audio, WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, 1));
            w.WriteSamples(audio.Samples, 0, audio.Samples.Length);
        }
    }

    /// <summary>
    /// Voices every line with Chatterbox in one Python run (the model loads once). Each line's file name carries a hash
    /// of its text, voice and excitement, so a stopped run, or a script with a few changed lines, only voices what's new.
    /// </summary>
    static void Chatterbox(Script script, BroadcastConfig cfg, string dir, CancellationToken ct)
    {
        var python = Path.Combine(cfg.ChatterboxDir, "venv", "Scripts", "python.exe");
        if (!File.Exists(python))
            throw new FileNotFoundException($"Chatterbox isn't installed ({python} is missing). Install it (see the README) or choose the Kokoro voice.");
        var tool = Path.Combine(AppContext.BaseDirectory, "chatterbox_voice.py");
        Directory.CreateDirectory(dir);
        string pbp = ReferenceClip(cfg, cfg.PlayByPlayClip, cfg.KokoroVoice);
        string colour = script.Lines.Any(l => l.Speaker == "colour") ? ReferenceClip(cfg, cfg.ColourClip, cfg.KokoroColourVoice) : pbp;

        var lines = new JsonArray();
        for (int i = 0; i < script.Lines.Count; i++)
        {
            var line = script.Lines[i];
            string voice = line.Speaker == "colour" ? colour : pbp;
            double excite = Excitement(line, cfg);
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{voice}|{excite:0.00}|{line.Text}")))[..8];
            line.Audio = Path.Combine(dir, $"{i:0000}-{line.Speaker}-{key}.wav");
            // Chatterbox's advice: a lower cfg weight keeps the pace natural when the exaggeration is turned up.
            lines.Add(new JsonObject { ["text"] = line.Text, ["out"] = line.Audio, ["voice"] = voice, ["exaggeration"] = excite, ["cfg"] = excite > 0.6 ? 0.3 : 0.5 });
        }
        var job = Path.Combine(dir, "job.json");
        File.WriteAllText(job, new JsonObject { ["device"] = "cuda", ["lines"] = lines }.ToJsonString());

        Console.WriteLine($"Voicing {script.Lines.Count} lines with Chatterbox on the GPU (an hour-long race takes a while; Stop keeps what's done)...");
        var psi = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(tool);
        psi.ArgumentList.Add(job);
        psi.Environment["HF_HOME"] = Path.Combine(cfg.ChatterboxDir, "hf");
        psi.Environment["PYTHONUTF8"] = "1";
        using var p = Process.Start(psi)!;
        var errors = new Queue<string>();
        var progress = Stopwatch.StartNew();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (!e.Data.StartsWith("line ", StringComparison.Ordinal)) { Console.WriteLine("  " + e.Data); return; }
            bool last = e.Data.EndsWith($"/{script.Lines.Count}", StringComparison.Ordinal);
            if (progress.Elapsed < TimeSpan.FromSeconds(15) && !last) return;
            progress.Restart();
            Console.WriteLine($"  {e.Data[5..]} lines voiced");
        };
        // Its progress bars and warnings go to stderr: kept quiet unless it fails.
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) { errors.Enqueue(e.Data); if (errors.Count > 40) errors.Dequeue(); } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } }))
            p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            lock (errors) foreach (var e in errors) Console.WriteLine("  [chatterbox] " + e);
            throw new InvalidOperationException($"Chatterbox stopped with exit code {p.ExitCode} (its messages are above).");
        }
        foreach (var line in script.Lines)
        {
            using var r = new WaveFileReader(line.Audio);
            line.Seconds = r.TotalTime.TotalSeconds;
        }
    }

    /// <summary>How animated a line is: exclamations (passes, crashes, the finish) get more, the colour voice a bit less.</summary>
    static double Excitement(Line line, BroadcastConfig cfg)
    {
        double e = cfg.Excitement;
        if (line.Text.Contains('!')) e += line.Speaker == "colour" ? 0.15 : 0.25;
        if (line.Priority == 1) e += 0.05;
        return Math.Round(Math.Clamp(e, 0.25, 0.85), 2);
    }

    /// <summary>
    /// The ~10 s clip Chatterbox copies a voice from: the one set in broadcast.json, or else one spoken by the
    /// commentator's Kokoro voice, so the commentators keep their voices but sound more animated.
    /// </summary>
    static string ReferenceClip(BroadcastConfig cfg, string clip, string kokoroVoice)
    {
        if (clip.Equals("builtin", StringComparison.OrdinalIgnoreCase)) return ""; // Chatterbox's own voice
        if (clip.Length > 0)
        {
            var full = Path.IsPathRooted(clip) ? clip : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, clip));
            return File.Exists(full) ? full : throw new FileNotFoundException($"The voice clip {full} doesn't exist.");
        }
        var path = Path.Combine(cfg.ChatterboxDir, "voices", "kokoro-" + kokoroVoice + ".wav");
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var (tts, speaker) = Kokoro(cfg, kokoroVoice);
        using var _ = tts;
        var audio = tts.Generate(
            "And they're side by side into the braking zone. He's had a look down the inside, but the door is firmly shut. " +
            "What a battle this has been, lap after lap, and there's still everything to play for.", 1.0f, speaker);
        using (var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, 1)))
            w.WriteSamples(audio.Samples, 0, audio.Samples.Length);
        return path;
    }

    /// <summary>
    /// The Kokoro model isn't in git: download it the first time, the same package the IRacingVoice app uses
    /// (sherpa-onnx's releases on GitHub, ~330 MB), unpacked with Windows' tar into the shared voices folder.
    /// Folders named otherwise are left to you.
    /// </summary>
    static void EnsureDownloaded(string dir)
    {
        if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.onnx").Length > 0) return;
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (!name.StartsWith("kokoro-", StringComparison.OrdinalIgnoreCase)) return;
        var root = Path.GetDirectoryName(dir.TrimEnd('\\', '/'))!;
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, name + ".tar.bz2");
        var url = $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{name}.tar.bz2";
        Console.WriteLine($"  Downloading the {name} voice model from {url}...");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            using (var src = http.GetStreamAsync(url).GetAwaiter().GetResult())
            using (var file = File.Create(archive))
                src.CopyTo(file);
            using var tar = Process.Start(new ProcessStartInfo("tar", $"-xjf \"{archive}\" -C \"{root}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            tar.WaitForExit();
        }
        catch (HttpRequestException ex) { Console.WriteLine($"  Couldn't download {name}: {ex.Message}"); }
        finally { try { File.Delete(archive); } catch (IOException) { } }
    }

    /// <summary>The Kokoro model with the word list for <paramref name="voice"/>'s accent, and the voice's speaker number.</summary>
    static (OfflineTts, int) Kokoro(BroadcastConfig cfg, string voice)
    {
        var dir = cfg.ResolveVoice(cfg.KokoroModel);
        EnsureDownloaded(dir);
        var model = Path.Combine(dir, "model.onnx");
        if (!File.Exists(model)) model = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.onnx").FirstOrDefault() ?? model : model;
        if (!File.Exists(model)) throw new FileNotFoundException($"No Kokoro model in {dir}. Set KokoroModel / VoicesDir in broadcast.json.");
        var names = KokoroVoices(model);
        int speaker = Array.IndexOf(names, voice);
        if (speaker < 0) throw new ArgumentException($"No Kokoro voice called {voice}. It has: {string.Join(", ", names)}.");
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = model;
        config.Model.Kokoro.Voices = Path.Combine(dir, "voices.bin");
        config.Model.Kokoro.Tokens = Path.Combine(dir, "tokens.txt");
        config.Model.Kokoro.DataDir = Path.Combine(dir, "espeak-ng-data");
        // British voices (b*) read with the British word list, the rest with the American one.
        string lexicon = Path.Combine(dir, voice.StartsWith('b') ? "lexicon-gb-en.txt" : "lexicon-us-en.txt");
        if (File.Exists(lexicon)) config.Model.Kokoro.Lexicon = lexicon;
        // A batch job: it can have most of the CPU.
        config.Model.NumThreads = Math.Max(1, Environment.ProcessorCount * 3 / 4);
        config.Model.Provider = "cpu";
        return (new OfflineTts(config), speaker);
    }

    /// <summary>The voice names stored in a Kokoro model's metadata, in speaker-number order.</summary>
    static string[] KokoroVoices(string model)
    {
        // The metadata sits at the end of the file: "speaker_names", a length byte or two, then "af_alloy,af_aoede,...".
        using var f = File.OpenRead(model);
        var tail = new byte[Math.Min(64 * 1024, f.Length)];
        f.Seek(-tail.Length, SeekOrigin.End);
        f.ReadExactly(tail);
        var text = Encoding.Latin1.GetString(tail);
        int at = text.IndexOf("speaker_names", StringComparison.Ordinal);
        if (at < 0) return [];
        var m = System.Text.RegularExpressions.Regex.Match(text[(at + "speaker_names".Length)..], @"[a-z]{2}_[a-z]+(?:,[a-z]{2}_[a-z]+)+");
        return m.Success ? m.Value.Split(',') : [];
    }
}

/// <summary>
/// Places the voiced lines on the video's timeline: each at its moment, pushed back if the previous one is still
/// talking, and fillers dropped when they'd come too late to make sense. Writes the commentary track and subtitles.
/// </summary>
public static class Mixer
{
    const double Gap = 0.3, MaxLateFiller = 3, MaxLateNormal = 6, MaxLateMust = 12;

    public sealed record Placed(Line Line, double At);

    /// <param name="toVideo">SessionTime → seconds into the final video.</param>
    public static List<Placed> Schedule(Script script, Func<double, double> toVideo, double videoLength)
    {
        var placed = new List<Placed>();
        double free = 0;
        foreach (var line in script.Lines.Where(l => l.Seconds > 0).OrderBy(l => l.T))
        {
            double want = toVideo(line.T);
            if (want < 0 || want > videoLength) continue;
            double at = Math.Max(want, free);
            double late = at - want;
            if (late > (line.Priority switch { 1 => MaxLateMust, 2 => MaxLateNormal, _ => MaxLateFiller })) continue;
            if (at + line.Seconds > videoLength) continue;
            placed.Add(new Placed(line, at));
            free = at + line.Seconds + Gap;
        }
        return placed;
    }

    public static void WriteTrack(List<Placed> placed, double videoLength, string wavPath)
    {
        int rate = 22050;
        var clips = placed.Select(p => (p.At, Samples: Read(p.Line.Audio, out var r), Rate: r)).ToList();
        if (clips.Count > 0) rate = clips[0].Rate;
        var mix = new float[(int)Math.Ceiling(videoLength * rate) + rate];
        foreach (var (at, samples, r) in clips)
        {
            var s = r == rate ? samples : Resample(samples, r, rate);
            int start = (int)(at * rate);
            for (int i = 0; i < s.Length && start + i < mix.Length; i++) mix[start + i] += s[i];
        }
        for (int i = 0; i < mix.Length; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);
        using var w = new WaveFileWriter(wavPath, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
        w.WriteSamples(mix, 0, mix.Length);
    }

    public static void WriteSubtitles(List<Placed> placed, BroadcastConfig cfg, string srtPath)
    {
        static string Ts(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        int n = 1;
        foreach (var p in placed)
        {
            string who = p.Line.Speaker == "colour" ? cfg.ColourName : cfg.PlayByPlayName;
            sb.AppendLine((n++).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{Ts(p.At)} --> {Ts(p.At + p.Line.Seconds)}");
            sb.AppendLine($"{who}: {p.Line.Text}");
            sb.AppendLine();
        }
        File.WriteAllText(srtPath, sb.ToString());
    }

    static float[] Read(string path, out int rate)
    {
        using var r = new WaveFileReader(path);
        rate = r.WaveFormat.SampleRate;
        ISampleProvider samples = r.ToSampleProvider();
        int channels = r.WaveFormat.Channels;
        var all = new List<float>();
        var buf = new float[8192 * channels];
        int n;
        while ((n = samples.Read(buf.AsSpan())) > 0)
            for (int i = 0; i < n; i += channels) all.Add(buf[i]); // mono (or the left channel)
        return all.ToArray();
    }

    static float[] Resample(float[] s, int from, int to)
    {
        var o = new float[(long)s.Length * to / from];
        for (int i = 0; i < o.Length; i++)
        {
            double x = i * (double)from / to;
            int k = (int)x;
            float a = s[Math.Min(k, s.Length - 1)], b = s[Math.Min(k + 1, s.Length - 1)];
            o[i] = (float)(a + (b - a) * (x - k));
        }
        return o;
    }
}
