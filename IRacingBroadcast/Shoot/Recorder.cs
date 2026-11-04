using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;
using NAudio.Wave;

/// <summary>One recorded clip: its screen recording and sim sound, and where the clip starts in them.</summary>
public sealed class Take
{
    public string Capture { get; set; } = "";
    public string GameAudio { get; set; } = "";
    /// <summary>Seconds into the capture where the clip begins (the replay is paused, then playing, before it).</summary>
    public double Start { get; set; }
    /// <summary>The clip's length in the video.</summary>
    public double Length { get; set; }
    /// <summary>Seconds into the capture of the game audio's first sample.</summary>
    public double GameAudioAt { get; set; }
}

/// <summary>What the shoot pass produced: one take per highlight clip, in video order.</summary>
public sealed class ShootResult
{
    public List<Take> Takes { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    public static ShootResult Load(string path) => JsonSerializer.Deserialize<ShootResult>(File.ReadAllText(path))!;
}

/// <summary>
/// Pass 3: for each highlight clip, jumps the replay there, plays it at normal speed with the replay UI hidden, records
/// the screen with ffmpeg (Desktop Duplication, hardware-encoded) and the sim's sound with WASAPI loopback, and cuts the
/// cameras per the clip's shots. Every frame is logged with its moment in the finished video, so the graphics and the
/// narration line up with the picture.
/// </summary>
public static class Recorder
{
    /// <param name="timelinePath">Where the frames go, timed in the finished video.</param>
    public static ShootResult Run(LiveTelemetry t, RaceInfo info, List<Clip> clips, BroadcastConfig cfg, string work, string timelinePath, CancellationToken ct = default)
    {
        var ffmpeg = Ffmpeg.Require(cfg);
        int cars = Math.Min(FrameFile.MaxCars, info.Drivers.Max(d => d.CarIdx) + 1);
        int session = info.RaceSessionNum;
        var number = info.Drivers.ToDictionary(d => d.CarIdx, d => d.Number);
        var offsets = Director.Offsets(clips);
        var result = new ShootResult();
        var timeline = new List<Frame>();
        Console.WriteLine($"Recording {clips.Count} clips, {clips.Sum(c => c.Length) / 60:0.0} minutes of race. Leave iRacing in front and don't touch it.");

        int savedState = t.Int("CamCameraState");
        var source = CaptureSource(cfg);
        var encoder = Ffmpeg.Encoder(ffmpeg, cfg.Encoder);
        Sim.MinimizeApp(true);
        // Before the UI is hidden: the mute key is pressed just as you would press it.
        bool chatMuted = MuteVoiceChat(cfg, mute: true);
        Sim.SetCameraState(Sim.CameraState.UIHidden);
        try
        {
            for (int k = 0; k < clips.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                var clip = clips[k];
                Console.WriteLine($"  Clip {k + 1}/{clips.Count}: {clip.Caption} ({clip.Length:0} s)");
                var take = new Take { Capture = Path.Combine(work, $"clip-{k:00}.mkv"), Length = clip.Length };
                if (cfg.GameAudio) take.GameAudio = Path.Combine(work, $"clip-{k:00}.wav");
                var frames = RecordClip(t, clip, take, number, cars, session, ffmpeg, encoder, source, cfg, ct);

                // The clip starts where the replay reached its first moment; its frames move to the finished video's clock.
                var first = frames.FirstOrDefault(f => f.T >= clip.From) ?? throw new InvalidOperationException($"Clip {k + 1} recorded nothing.");
                take.Start = first.V - (first.T - clip.From);
                foreach (var f in frames)
                {
                    f.V = offsets[k] + f.V - take.Start;
                    if (f.V >= offsets[k] && f.V <= offsets[k] + clip.Length) timeline.Add(f);
                }
                result.Takes.Add(take);
            }
        }
        finally
        {
            Sim.ReplaySpeed(0);
            Sim.SetCameraState((Sim.CameraState)savedState & ~Sim.CameraState.UIHidden);
            Sim.MinimizeApp(false);
            if (chatMuted) MuteVoiceChat(cfg, mute: false); // brings iRacing back in front for the key, then you can carry on
        }
        FrameFile.Write(timelinePath, timeline);
        Console.WriteLine($"Recording done: {result.Takes.Count} clips in {work}");
        return result;
    }

    /// <summary>
    /// Presses iRacing's "Mute Voice Chat" binding. It's a toggle that doesn't report its state, so it's skipped when
    /// iRacing's settings say chat is muted already. True if pressed (the caller presses it again afterwards).
    /// </summary>
    static bool MuteVoiceChat(BroadcastConfig cfg, bool mute)
    {
        if (cfg.MuteVoiceChatKeys.Length == 0) return false;
        if (mute)
        {
            var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "app.ini");
            if (File.Exists(ini) && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(ini), @"^voiceChatMuted=1", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                Console.WriteLine("  Voice chat is muted in iRacing already.");
                return false;
            }
        }
        if (!Sim.FocusForKeys() || !Sim.PressKeys(cfg.MuteVoiceChatKeys))
        {
            Console.WriteLine(mute ? "  WARNING: couldn't mute voice chat (iRacing wasn't the focused window). Radio may be heard in the video."
                                   : $"  WARNING: couldn't turn voice chat back on: press {cfg.MuteVoiceChatKeys} in iRacing.");
            return false;
        }
        Console.WriteLine(mute ? $"  Voice chat muted ({cfg.MuteVoiceChatKeys}); it comes back on when the recording ends."
                               : "  Voice chat back on.");
        Thread.Sleep(200);
        return true;
    }

    /// <summary>
    /// Records one clip: seeks a moment before it, starts the capture on a paused frame, plays to the clip's end and
    /// stops. Returns the frames with V = seconds into this clip's capture.
    /// </summary>
    static List<Frame> RecordClip(LiveTelemetry t, Clip clip, Take take, Dictionary<int, string> number, int cars, int session,
        string ffmpeg, string encoder, string source, BroadcastConfig cfg, CancellationToken ct)
    {
        var shots = clip.Shots;
        Replay.SeekAndPause(t, session, clip.From - 2, ct);
        Sim.Camera(number[shots[0].CarIdx], shots[0].Group);
        Thread.Sleep(1000);

        string download = encoder.Contains("nvenc") ? "" : "-vf hwdownload,format=bgra ";
        // stdin stays open: it's how ffmpeg is told to stop ('q'), which finishes the file properly.
        var args = $"-hide_banner -loglevel warning -stats_period 0.1 -progress pipe:1 -f lavfi -i \"{source}\" " +
                   $"{download}{Ffmpeg.EncoderArgs(encoder, master: true)} -y \"{take.Capture}\"";
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var ff = Process.Start(psi)!;
        var clock = new CaptureClock();
        ff.OutputDataReceived += (_, e) => clock.OnProgress(e.Data);
        ff.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Console.WriteLine("  [ffmpeg] " + e.Data); };
        ff.BeginOutputReadLine();
        ff.BeginErrorReadLine();

        var frames = new List<Frame>();
        GameAudioRecorder? audio = null;
        try
        {
            if (!clock.WaitForStart(TimeSpan.FromSeconds(20), ff))
                throw new InvalidOperationException("ffmpeg didn't start recording (see its messages above).");
            if (take.GameAudio.Length > 0)
            {
                audio = new GameAudioRecorder(take.GameAudio, cfg);
                audio.Start();
                take.GameAudioAt = clock.Now;
            }
            Thread.Sleep(500);

            Sim.ReplaySpeed(1);
            double playAt = clock.Now;
            var sincePlay = Stopwatch.StartNew();
            int next = 1, current = 0, retries = 0, resumes = 0;
            var sinceCut = Stopwatch.StartNew();
            var stalled = Stopwatch.StartNew();
            double lastT = double.NegativeInfinity;
            while (!ct.IsCancellationRequested)
            {
                if (ff.HasExited) throw new InvalidOperationException("ffmpeg stopped recording.");
                if (!t.WaitForFrame(1000)) { if (stalled.Elapsed > TimeSpan.FromSeconds(6) && !Replay.PlayOn(t, stalled, 1, ref resumes)) break; continue; }
                if (t.Int("SessionNum") != session) break;
                var f = FrameFile.Capture(t, cars);
                if (f.T <= lastT) { if (stalled.Elapsed > TimeSpan.FromSeconds(6) && !Replay.PlayOn(t, stalled, 1, ref resumes)) break; continue; }
                stalled.Restart();
                lastT = f.T;
                f.V = playAt + sincePlay.Elapsed.TotalSeconds;

                if (next < shots.Count && f.T >= shots[next].T)
                {
                    current = next++;
                    Sim.Camera(number[shots[current].CarIdx], shots[current].Group);
                    sinceCut.Restart();
                    retries = 0;
                }
                else if (f.CamCar != shots[current].CarIdx && sinceCut.Elapsed > TimeSpan.FromSeconds(1.5) && retries < 3)
                {
                    // iRacing sometimes ignores a switch (e.g. the car was in a loading state): ask again.
                    Sim.Camera(number[shots[current].CarIdx], shots[current].Group);
                    sinceCut.Restart();
                    retries++;
                }
                frames.Add(f);
                if (f.T >= clip.To + 0.5) break;
            }
        }
        finally
        {
            Sim.ReplaySpeed(0);
            try { ff.StandardInput.Write('q'); ff.StandardInput.Flush(); } catch (IOException) { }
            if (!ff.WaitForExit(30000)) ff.Kill();
            audio?.Dispose();
        }
        ct.ThrowIfCancellationRequested(); // a part-recorded shoot isn't kept: it starts over next time
        return frames;
    }

    /// <summary>ddagrab on the monitor iRacing is on, cropped to its window if it isn't full screen.</summary>
    static string CaptureSource(BroadcastConfig cfg)
    {
        // A replay just launched connects before the sim has made its window, so give it a minute.
        var found = Sim.WindowRect(out _);
        for (var sw = Stopwatch.StartNew(); found is not { Width: > 0, Height: > 0 } && sw.Elapsed < TimeSpan.FromMinutes(1); found = Sim.WindowRect(out _))
        {
            if (sw.ElapsedMilliseconds < 1000) Console.WriteLine("  Waiting for the iRacing window...");
            Thread.Sleep(1000);
        }
        if (found is not { Width: > 0, Height: > 0 } rect) throw new InvalidOperationException("Can't find the iRacing window.");
        var screens = Screen.AllScreens;
        var screen = Screen.FromRectangle(rect);
        int idx = DxgiOutputs.IndexOf(screen.DeviceName);
        if (cfg.Monitor >= 0)
        {
            idx = cfg.Monitor;
            screen = screens.FirstOrDefault(s => DxgiOutputs.IndexOf(s.DeviceName) == idx) ?? screen;
        }
        if (idx < 0) throw new InvalidOperationException($"Can't find {screen.DeviceName} among the graphics card's outputs.");
        var b = screen.Bounds;
        var inter = System.Drawing.Rectangle.Intersect(rect, b);
        var src = new List<string> { $"output_idx={idx}", $"framerate={cfg.Fps}", "draw_mouse=0" };
        if (inter.Width > 0 && (inter.Width < b.Width - 2 || inter.Height < b.Height - 2))
        {
            int w = inter.Width & ~1, h = inter.Height & ~1;
            src.Add(FormattableString.Invariant($"video_size={w}x{h}"));
            src.Add(FormattableString.Invariant($"offset_x={inter.X - b.X}"));
            src.Add(FormattableString.Invariant($"offset_y={inter.Y - b.Y}"));
        }
        Console.WriteLine($"  Capturing {screen.DeviceName} (output {idx}, {b.Width}x{b.Height}); iRacing window {rect.Width}x{rect.Height}.");
        return "ddagrab=" + string.Join(":", src);
    }

    /// <summary>The video's clock, from ffmpeg's progress reports (out_time) plus the time since the last report.</summary>
    sealed class CaptureClock
    {
        readonly Stopwatch _wall = Stopwatch.StartNew();
        readonly object _gate = new();
        double _outTime = -1, _at;

        public void OnProgress(string? line)
        {
            if (line == null || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
            if (!long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) || us <= 0) return;
            lock (_gate) { _outTime = us / 1e6; _at = _wall.Elapsed.TotalSeconds; }
        }

        public double Now
        {
            get { lock (_gate) return _outTime < 0 ? 0 : _outTime + (_wall.Elapsed.TotalSeconds - _at); }
        }

        public bool WaitForStart(TimeSpan timeout, Process ff)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout && !ff.HasExited)
            {
                lock (_gate) if (_outTime >= 1.0) return true;
                Thread.Sleep(50);
            }
            return false;
        }
    }

    /// <summary>
    /// The sim's sound via WASAPI loopback. Loopback delivers nothing while the PC is silent, so gaps are filled with
    /// silence by the wall clock to keep the track in step with the video.
    /// </summary>
    sealed class GameAudioRecorder : IDisposable
    {
#pragma warning disable CS0618 // the device's whole sound is what's wanted (the sim may route audio through helper processes)
        readonly WasapiLoopbackCapture _cap;
#pragma warning restore CS0618
        readonly WaveFileWriter _writer;
        readonly Stopwatch _clock = new();
        long _bytes;
        float _peak;

        public GameAudioRecorder(string path, BroadcastConfig cfg)
        {
#pragma warning disable CS0618 // the device's whole sound is what's wanted (the sim may route audio through helper processes)
            _cap = new WasapiLoopbackCapture(SimAudio.Device(cfg));
#pragma warning restore CS0618
            _writer = new WaveFileWriter(path, _cap.WaveFormat);
            bool isFloat = _cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && _cap.WaveFormat.BitsPerSample == 32;
            _cap.DataAvailable += (_, e) =>
            {
                lock (_writer)
                {
                    if (isFloat)
                        foreach (var s in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded)))
                            _peak = Math.Max(_peak, Math.Abs(s));
                    else if (e.BytesRecorded > 0) _peak = 1; // can't tell; assume sound
                    long expected = (long)(_clock.Elapsed.TotalSeconds * _cap.WaveFormat.AverageBytesPerSecond);
                    expected -= expected % _cap.WaveFormat.BlockAlign;
                    long missing = expected - _bytes - e.BytesRecorded;
                    if (missing > _cap.WaveFormat.AverageBytesPerSecond / 10)
                    {
                        var zero = new byte[Math.Min(missing, 1 << 20)];
                        for (long left = missing; left > 0; left -= zero.Length)
                            _writer.Write(zero, 0, (int)Math.Min(zero.Length, left));
                        _bytes += missing;
                    }
                    _writer.Write(e.Buffer, 0, e.BytesRecorded);
                    _bytes += e.BytesRecorded;
                }
            };
        }

        public void Start() { _clock.Start(); _cap.StartRecording(); }

        public void Dispose()
        {
            _cap.StopRecording();
            Thread.Sleep(200);
            lock (_writer) _writer.Dispose();
            _cap.Dispose();
            if (_peak < 1e-4f)
                Console.WriteLine("  WARNING: the sim's sound came out silent. Check iRacing's sound device, or set GameAudioDevice in broadcast.json to the device you hear it on.");
        }
    }
}
