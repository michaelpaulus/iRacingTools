using System.Text.Json;

/// <summary>Settings in broadcast.json next to the exe (written with the defaults on first run).</summary>
public sealed class BroadcastConfig
{
    /// <summary>The show's name on the graphics.</summary>
    public string ShowName { get; set; } = "iRacing Broadcast";
    /// <summary>Driver to follow (name, or part of it); empty = the driver who saved the replay.</summary>
    public string FeaturedDriver { get; set; } = "";

    /// <summary>
    /// How the script gets written: "claude" (the Claude Code CLI, claude -p, on your Claude subscription), "paste" (the
    /// app writes a prompt for you to paste into Claude yourself and waits for the reply before the edit) or "simple"
    /// (built in, no Claude).
    /// </summary>
    public string Commentary { get; set; } = "claude";
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>claude --effort: "low", "medium" or "high"; empty = the CLI's default.</summary>
    public string Effort { get; set; } = "high";
    /// <summary>The narrator (the play-by-play voice) and, for two-voice scripts, the colour commentator.</summary>
    public string PlayByPlayName { get; set; } = "Alan";
    public string ColourName { get; set; } = "Ryan";
    /// <summary>Where the Kokoro model folder is, relative to the exe; the IRacingVoice app's copy is shared.</summary>
    public string VoicesDir { get; set; } = @"..\..\..\..\IRacingVoice\voices";
    public double VoiceSpeed { get; set; } = 1.08;
    /// <summary>"kokoro" (natural, local on the CPU) or "chatterbox" (more expressive; needs its Python install and a GPU).</summary>
    public string VoiceEngine { get; set; } = "kokoro";
    /// <summary>
    /// Kokoro's voices for the narrator and the colour commentator: American am_michael, am_adam, am_eric, af_heart, ...;
    /// British bm_george, bm_lewis, bm_daniel, bf_emma, ...
    /// </summary>
    public string KokoroVoice { get; set; } = "am_michael";
    public string KokoroColourVoice { get; set; } = "bm_george";
    /// <summary>The Kokoro model folder, in <see cref="VoicesDir"/> (downloaded the first time if missing).</summary>
    public string KokoroModel { get; set; } = "kokoro-multi-lang-v1_0";
    /// <summary>Chatterbox's install: venv\ (Python with chatterbox-tts), hf\ (the model) and voices\ (reference clips).</summary>
    public string ChatterboxDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".iracingbroadcast", "chatterbox");
    /// <summary>
    /// Chatterbox copies each commentator's voice from a ~10 s WAV clip. Empty = a clip spoken by their Kokoro voice above;
    /// "builtin" = Chatterbox's own voice.
    /// Use only voices you have permission to use (your own, a friend's).
    /// </summary>
    public string PlayByPlayClip { get; set; } = "";
    public string ColourClip { get; set; } = "";
    /// <summary>Chatterbox's exaggeration for ordinary lines (0.25–0.85); exclamations get more.</summary>
    public double Excitement { get; set; } = 0.5;

    /// <summary>ffmpeg.exe: a full path, or "ffmpeg" to use the one on PATH.</summary>
    public string Ffmpeg { get; set; } = "ffmpeg";
    /// <summary>h264_nvenc (NVIDIA), hevc_nvenc, h264_amf (AMD), h264_qsv (Intel) or libx264 (CPU).</summary>
    public string Encoder { get; set; } = "h264_nvenc";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    /// <summary>Frames per second of the graphics layer (the video stays at <see cref="Fps"/>).</summary>
    public int GraphicsFps { get; set; } = 30;
    /// <summary>Monitor for the screen capture (DXGI output index, as ffmpeg's ddagrab counts them); -1 = the one iRacing is on.</summary>
    public int Monitor { get; set; } = -1;
    /// <summary>Record the sim's sound (engines) as a bed under the commentary.</summary>
    public bool GameAudio { get; set; } = true;
    public double GameAudioDb { get; set; } = -12;
    /// <summary>The sound device to record the sim from (part of its name). Empty = the one iRacing plays on.</summary>
    public string GameAudioDevice { get; set; } = "";
    /// <summary>
    /// The keys bound to iRacing's "Mute Voice Chat" control (Settings > Controls > Voice Chat Controls): pressed when
    /// the recording starts and again when it ends, so drivers' radio stays out of the video. Empty = don't touch voice chat.
    /// </summary>
    public string MuteVoiceChatKeys { get; set; } = "Ctrl+Shift+Alt+M";

    public int ScanSpeed { get; set; } = 16;
    /// <summary>How long the scan carries on once the last car is home.</summary>
    public double PostRoll { get; set; } = 45;
    /// <summary>How long the highlights video runs, roughly.</summary>
    public double HighlightMinutes { get; set; } = 5;
    /// <summary>What the highlights follow: "mixed" (the race plus the featured driver), "mine" (mostly the featured driver) or "race".</summary>
    public string HighlightFocus { get; set; } = "mixed";

    /// <summary>
    /// Scan, recordings and intermediate files. Several GB per race: keep it off OneDrive. Not under AppData: packaged
    /// apps (such as a Claude session driving this one) get a private copy of AppData, and the work must be shared.
    /// </summary>
    public string WorkDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "iRacing Broadcasts", "work");
    /// <summary>Where the finished videos go.</summary>
    public string OutputDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "iRacing Broadcasts");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static BroadcastConfig Load(string path)
    {
        if (File.Exists(path))
        {
            var loaded = JsonSerializer.Deserialize<BroadcastConfig>(File.ReadAllText(path), Json) ?? new();
            // "api" (the Anthropic API) is gone: Claude now goes through the CLI.
            if (loaded.Commentary.Equals("api", StringComparison.OrdinalIgnoreCase)) loaded.Commentary = "claude";
            // The Piper voices are gone: Kokoro replaced them.
            if (loaded.VoiceEngine.Equals("piper", StringComparison.OrdinalIgnoreCase)) loaded.VoiceEngine = "kokoro";
            return loaded;
        }
        var cfg = new BroadcastConfig();
        try { File.WriteAllText(path, JsonSerializer.Serialize(cfg, Json)); } catch (IOException) { }
        return cfg;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public string ResolveVoice(string voice)
    {
        if (Path.IsPathRooted(voice)) return voice;
        var dir = Path.IsPathRooted(VoicesDir) ? VoicesDir : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, VoicesDir));
        var candidates = new[]
        {
            Path.Combine(dir, voice),
            Path.Combine(AppContext.BaseDirectory, "voices", voice),
            // Next to a published exe in IRacingBroadcast\, the sibling app's folder.
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "IRacingVoice", "voices", voice)),
        };
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }
}
