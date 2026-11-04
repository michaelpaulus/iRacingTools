using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SherpaOnnx;
using Whisper.net;
using Whisper.net.Ggml;

/// <summary>Records the microphone at 16 kHz mono (what Whisper expects) while push-to-talk is held.</summary>
public sealed class Recorder : IDisposable
{
    readonly WaveInEvent _wave = new() { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
    readonly List<float> _samples = new();
    bool _recording;

    public Recorder()
    {
        _wave.DataAvailable += (_, e) =>
        {
            lock (_samples)
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                    _samples.Add(BitConverter.ToInt16(e.Buffer, i) / 32768f);
        };
    }

    public void Start()
    {
        if (_recording) return;
        lock (_samples) _samples.Clear();
        _wave.StartRecording();
        _recording = true;
    }

    /// <summary>Stops and returns the recording (empty if shorter than a quarter second: an accidental tap).</summary>
    public float[] Stop()
    {
        if (!_recording) return Array.Empty<float>();
        Thread.Sleep(150); // the last buffer: people let go of the button as they finish the word
        _wave.StopRecording();
        _recording = false;
        lock (_samples) return _samples.Count < 4000 ? Array.Empty<float>() : _samples.ToArray();
    }

    public void Dispose() => _wave.Dispose();
}

/// <summary>Speech to text with OpenAI's Whisper model, run locally (no audio leaves the PC).</summary>
public sealed class Transcriber : IDisposable
{
    readonly WhisperFactory _factory;
    readonly VoiceConfig _cfg;

    Transcriber(WhisperFactory factory, VoiceConfig cfg) { _factory = factory; _cfg = cfg; }

    /// <summary>Loads the model, downloading it first (from Hugging Face, ~150 MB for base.en) if it isn't there.</summary>
    public static async Task<Transcriber> Load(VoiceConfig cfg, string baseDir)
    {
        var path = Path.IsPathRooted(cfg.WhisperModel) ? cfg.WhisperModel : Path.Combine(baseDir, cfg.WhisperModel);
        if (!File.Exists(path))
        {
            var type = Path.GetFileNameWithoutExtension(path).Replace("ggml-", "") switch
            {
                "tiny.en" => GgmlType.TinyEn,
                "small.en" => GgmlType.SmallEn,
                "medium.en" => GgmlType.MediumEn,
                _ => GgmlType.BaseEn,
            };
            Console.WriteLine($"Downloading the Whisper speech model ({type}) to {path}...");
            var partial = path + ".part";
            await using (var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type))
            await using (var file = File.Create(partial))
                await model.CopyToAsync(file);
            File.Move(partial, path, overwrite: true);
        }
        return new Transcriber(WhisperFactory.FromPath(path), cfg);
    }

    public async Task<string> Transcribe(float[] samples)
    {
        if (samples.Length == 0) return "";
        using var processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithPrompt(_cfg.WhisperHints)
            .WithThreads(Math.Max(1, _cfg.WhisperThreads))
            .Build();
        var parts = new List<string>();
        await foreach (var segment in processor.ProcessAsync(samples)) parts.Add(segment.Text);
        var text = string.Join(" ", parts).Trim();
        // Whisper's stock outputs for silence / noise.
        return text is "[BLANK_AUDIO]" or "(silence)" or "[ Silence ]" or "you" ? "" : text;
    }

    public void Dispose() => _factory.Dispose();
}

/// <summary>Short spoken confirmations ("Tyres off."), one at a time: the Kokoro voice, or the Windows voice as a fallback.</summary>
public sealed class Speaker : IDisposable
{
    readonly SpeechSynthesizer _synth = new();
    readonly NeuralVoice? _voice;

    /// <param name="kokoroDir">Folder of the Kokoro model, when <see cref="VoiceConfig.KokoroVoice"/> is set; null = the Windows voice.</param>
    public Speaker(VoiceConfig cfg, string? kokoroDir)
    {
        if (cfg.Voice.Length > 0) try { _synth.SelectVoice(cfg.Voice); } catch { /* fall back to the default voice */ }
        _synth.Rate = cfg.VoiceRate;
        _synth.SetOutputToDefaultAudioDevice();
        if (kokoroDir != null)
            try { _voice = NeuralVoice.Kokoro(kokoroDir, cfg.KokoroVoice, cfg.KokoroSpeed, cfg.KokoroThreads); }
            catch (Exception ex) { Console.WriteLine($"Kokoro voice unavailable ({ex.Message}); using the Windows voice."); }
    }

    public void Say(string text)
    {
        Console.WriteLine($"  >> {text}");
        Cancel();
        bool spoken = false;
        if (_voice != null)
            try { spoken = _voice.Say(text); }
            catch (Exception ex) { Console.WriteLine($"  [voice] Kokoro failed: {ex.Message}"); }
        if (!spoken) _synth.SpeakAsync(text);
    }

    /// <summary>Volume of the confirmations, 0-100 (for "speak louder").</summary>
    public int Volume
    {
        get => _synth.Volume;
        set
        {
            _synth.Volume = Math.Clamp(value, 0, 100);
            if (_voice != null) _voice.Volume = _synth.Volume / 100f;
        }
    }

    /// <summary>Stop talking (you pressed push-to-talk again).</summary>
    public void Cancel()
    {
        _synth.SpeakAsyncCancelAll();
        _voice?.Stop();
    }

    readonly object _cueGate = new();
    WaveOutEvent? _cueOut;
    static readonly byte[] Heard = Tones((660, 0.07), (0, 0.03), (880, 0.09)), Thinking = Tones((520, 0.06));

    /// <summary>
    /// A short sound instead of words: a rising two-note chirp when the command has been heard, a soft blip while it's
    /// still being worked out. Quieter than the voice, and separate from it.
    /// </summary>
    public void Cue(bool thinking)
    {
        var stream = new RawSourceWaveStream(new MemoryStream(thinking ? Thinking : Heard), WaveFormat.CreateIeeeFloatWaveFormat(CueRate, 1));
        lock (_cueGate)
        {
            _cueOut?.Dispose();
            _cueOut = new WaveOutEvent();
            _cueOut.Init(stream);
            _cueOut.Volume = Math.Clamp(Volume / 100f * (thinking ? 0.25f : 0.4f), 0f, 1f);
            _cueOut.Play();
        }
    }

    const int CueRate = 22050;

    // Sine notes (Hz, seconds; 0 Hz = a gap) with 10 ms fades so they don't click.
    static byte[] Tones(params (double Hz, double Seconds)[] notes)
    {
        var samples = new List<float>();
        foreach (var (hz, seconds) in notes)
        {
            int n = (int)(seconds * CueRate), fade = CueRate / 100;
            for (int i = 0; i < n; i++)
                samples.Add(hz <= 0 ? 0 : (float)(Math.Sin(2 * Math.PI * hz * i / CueRate) * Math.Min(1.0, Math.Min(i, n - i) / (double)fade)));
        }
        var bytes = new byte[samples.Count * sizeof(float)];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        lock (_cueGate) _cueOut?.Dispose();
        _voice?.Dispose();
        _synth.Dispose();
    }
}

/// <summary>
/// A Kokoro neural voice that runs locally on the CPU through sherpa-onnx, played with NAudio.
/// </summary>
public sealed class NeuralVoice : IDisposable
{
    readonly OfflineTts _tts;
    readonly float _speed;
    readonly int _speaker;
    readonly object _gate = new();
    WaveOutEvent? _out;

    public float Volume { get; set; } = 1f;

    NeuralVoice(OfflineTtsConfig config, int speaker, double speed)
    {
        config.Model.Provider = "cpu";
        _tts = new OfflineTts(config);
        _speaker = speaker;
        _speed = (float)Math.Clamp(speed, 0.5, 2.0);
    }

    /// <summary>One of the voices in a Kokoro model folder ("bm_george", "af_heart", ...), British or American English.</summary>
    public static NeuralVoice Kokoro(string dir, string voice, double speed, int threads)
    {
        var model = Directory.GetFiles(dir, "*.onnx").FirstOrDefault() ?? throw new FileNotFoundException($"No .onnx model in {dir}");
        int speaker = Array.IndexOf(KokoroVoices(model), voice);
        if (speaker < 0) throw new ArgumentException($"No Kokoro voice called {voice}");
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = model;
        config.Model.Kokoro.Voices = Path.Combine(dir, "voices.bin");
        config.Model.Kokoro.Tokens = Path.Combine(dir, "tokens.txt");
        config.Model.Kokoro.DataDir = Path.Combine(dir, "espeak-ng-data");
        // British voices (b*) read with the British word list, the rest with the American one.
        string lexicon = Path.Combine(dir, voice.StartsWith('b') ? "lexicon-gb-en.txt" : "lexicon-us-en.txt");
        if (File.Exists(lexicon)) config.Model.Kokoro.Lexicon = lexicon;
        config.Model.NumThreads = Math.Max(1, threads);
        return new NeuralVoice(config, speaker, speed);
    }

    /// <summary>The voice names stored in a Kokoro model's metadata, in speaker-number order.</summary>
    public static string[] KokoroVoices(string model)
    {
        // The metadata sits at the end of the file: "speaker_names", a length byte or two, then "af_alloy,af_aoede,...".
        using var f = File.OpenRead(model);
        var tail = new byte[Math.Min(64 * 1024, f.Length)];
        f.Seek(-tail.Length, SeekOrigin.End);
        f.ReadExactly(tail);
        var text = System.Text.Encoding.Latin1.GetString(tail);
        int at = text.IndexOf("speaker_names", StringComparison.Ordinal);
        if (at < 0) return [];
        var m = System.Text.RegularExpressions.Regex.Match(text[(at + "speaker_names".Length)..], @"[a-z]{2}_[a-z]+(?:,[a-z]{2}_[a-z]+)+");
        return m.Success ? m.Value.Split(',') : [];
    }

    /// <summary>The text as a WAV file, for comparing voices.</summary>
    public void SaveWav(string text, string path)
    {
        var audio = _tts.Generate(text, _speed, _speaker);
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, 1));
        w.WriteSamples(audio.Samples, 0, audio.Samples.Length);
    }

    readonly object _generating = new();
    int _said; // bumped by every Say and Stop: the rest of an older answer isn't made or played

    /// <summary>
    /// Speaks the text; false if no audio came out (the caller falls back to the Windows voice). It starts as soon as
    /// the first sentence is made, and makes the rest while that one plays.
    /// </summary>
    public bool Say(string text)
    {
        var sentences = Regex.Split(text.Trim(), @"(?<=[.!?])\s+").Where(s => s.Length > 0).ToList();
        if (sentences.Count == 0) return false;
        int mine = Interlocked.Increment(ref _said);
        float[] first;
        int rate;
        lock (_generating)
        {
            var audio = _tts.Generate(sentences[0], _speed, _speaker);
            (first, rate) = (audio.Samples, audio.SampleRate);
        }
        if (first.Length == 0 || rate <= 0) return false;

        // Plays silence once it runs out, so a slow next sentence doesn't end the answer early.
        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 1), TimeSpan.FromMinutes(2))
        {
            ReadFully = true, DiscardOnBufferOverflow = true,
        };
        Add(buffer, first);
        lock (_gate)
        {
            if (mine != Volatile.Read(ref _said)) return true; // stopped while the first sentence was being made
            _out?.Dispose();
            _out = new WaveOutEvent();
            _out.Init(buffer);
            _out.Volume = Math.Clamp(Volume, 0f, 1f);
            _out.Play();
        }
        if (sentences.Count > 1)
            Task.Run(() =>
            {
                foreach (var s in sentences.Skip(1))
                {
                    if (mine != Volatile.Read(ref _said)) return;
                    float[] samples;
                    lock (_generating) samples = _tts.Generate(s, _speed, _speaker).Samples;
                    if (mine != Volatile.Read(ref _said)) return;
                    Add(buffer, samples);
                }
            });
        return true;
    }

    static void Add(BufferedWaveProvider buffer, float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Stop()
    {
        Interlocked.Increment(ref _said);
        lock (_gate) _out?.Stop();
    }

    /// <summary>
    /// The Kokoro model's folder, downloading it the first time (sherpa-onnx's packaged models on GitHub, ~330 MB)
    /// and unpacking it with Windows' tar. Null if it can't be had.
    /// </summary>
    public static async Task<string?> Ensure(string voice, string baseDir)
    {
        var root = Path.Combine(baseDir, "voices");
        var dir = Path.Combine(root, voice);
        if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.onnx").Length > 0) return dir;
        try
        {
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, voice + ".tar.bz2");
            var url = $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{voice}.tar.bz2";
            Console.WriteLine($"Downloading the {voice} voice from {url}...");
            using (var http = new HttpClient())
            await using (var src = await http.GetStreamAsync(url))
            await using (var file = File.Create(archive))
                await src.CopyToAsync(file);
            var tar = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("tar", $"-xjf \"{archive}\" -C \"{root}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
            })!;
            await tar.WaitForExitAsync();
            File.Delete(archive);
            return Directory.Exists(dir) ? dir : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Couldn't get the {voice} voice ({ex.Message}); using the Windows voice.");
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate) _out?.Dispose();
        _tts.Dispose();
    }
}
