using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class PushToTalkConfig
{
    /// <summary>"None", "Keyboard" or "Joystick". Run "IRacingVoice bind" to set it.</summary>
    public string Type { get; set; } = "None";
    /// <summary>Windows virtual-key code when Type is Keyboard.</summary>
    public int Key { get; set; }
    /// <summary>Joystick manufacturer/product IDs, so the device is found again if its slot changes.</summary>
    public int JoystickMid { get; set; }
    public int JoystickPid { get; set; }
    public string JoystickName { get; set; } = "";
    /// <summary>Zero-based button number (Windows exposes the first 32 buttons).</summary>
    public int Button { get; set; }
}

public sealed class VoiceConfig
{
    public PushToTalkConfig PushToTalk { get; set; } = new();

    /// <summary>
    /// The model that works out what you meant: Claude through the Claude CLI by default, signed in with your Claude
    /// subscription (no API key, nothing billed per command; a few seconds slower to start). For the API instead:
    /// Kind "anthropic", KeyVariable "ANTHROPIC_API_KEY" (key from console.anthropic.com; "claude-haiku-5-5" is about $1
    /// a month). OpenAI-compatible services (Groq: Kind "openai", Url https://api.groq.com/openai/v1, Model
    /// openai/gpt-oss-120b, KeyVariable GROQ_API_KEY) also work; list several to fall back in order.
    /// </summary>
    public List<ModelProvider> Models { get; set; } =
    [
        new() { Name = "Claude", Kind = "claude-cli", Model = "claude-haiku-5-5", ReasoningEffort = "low", TimeoutSeconds = 30 },
    ];

    /// <summary>
    /// Run only on the CPU's efficiency cores (Intel's E-cores), leaving the performance cores to iRacing. Speech
    /// recognition and the voice take a little longer there.
    /// </summary>
    public bool EfficiencyCoresOnly { get; set; } = true;

    /// <summary>A short chirp when push-to-talk is released (heard you), and a soft blip every 2 s while it's thinking.</summary>
    public bool Cues { get; set; } = true;

    /// <summary>Whisper speech model file (downloaded on first run if missing). base.en: good accuracy, ~150 MB.</summary>
    public string WhisperModel { get; set; } = "ggml-base.en.bin";
    /// <summary>Words Whisper should expect, so racing terms are heard correctly.</summary>
    /// <summary>CPU threads Whisper may use. Low keeps the ~1 s transcription burst from taking frames from iRacing.</summary>
    public int WhisperThreads { get; set; } = 2;
    public string WhisperHints { get; set; } =
        "iRacing pit stop tires tyres fuel litres gallons auto fuel tear-off windshield fast repair pressures psi compound wets " +
        "voice chat mute unmute radio channel transmit scan driver force feedback " +
        "replay screenshot telemetry logging camera cockpit";

    /// <summary>
    /// The voice, from Kokoro (model downloaded on first run, ~330 MB): British bm_george, bm_lewis, bm_daniel, bm_fable,
    /// bf_emma, bf_isabella, bf_alice, bf_lily; American am_michael, am_adam, am_eric, am_liam, af_heart, af_bella,
    /// af_sarah, af_nicole and more. Compare them with "IRacingVoice voice-samples &lt;folder&gt; am_michael bm_george ...".
    /// Empty = the Windows voice below.
    /// </summary>
    public string KokoroVoice { get; set; } = "am_michael";
    /// <summary>Speaking speed of the Kokoro voice: 1 = normal, higher = faster.</summary>
    public double KokoroSpeed { get; set; } = 1.1;
    /// <summary>The full-size model: on a CPU it's several times faster than the smaller int8 one.</summary>
    public string KokoroModel { get; set; } = "kokoro-multi-lang-v1_0";
    /// <summary>CPU threads Kokoro may use while it speaks: more answers sooner, fewer leaves more for iRacing.</summary>
    public int KokoroThreads { get; set; } = 4;

    /// <summary>Windows text-to-speech voice, used when KokoroVoice is empty or Kokoro can't be loaded; empty = Windows default.</summary>
    public string Voice { get; set; } = "Microsoft Zira Desktop";
    public int VoiceRate { get; set; } = 2;

    /// <summary>
    /// Keys for iRacing's "Mute a Driver" control: bind the same combination in iRacing (Settings > Controls >
    /// Voice Chat Controls). Empty = the voice agent can't mute individual drivers.
    /// </summary>
    public string MuteDriverKeys { get; set; } = "Shift+Ctrl+Alt+D";

    /// <summary>Keys for iRacing's "Mute Voice Chat" control (a toggle), bound the same way. Empty = can't mute voice chat.</summary>
    public string MuteVoiceChatKeys { get; set; } = "Shift+Ctrl+Alt+M";
    /// <summary>
    /// Download a comparison lap from Garage 61 when you start a car and track (set from the command center's
    /// Reference laps window). Needs a personal access token in this environment variable and a Garage 61 Pro plan.
    /// </summary>
    public bool Garage61Download { get; set; }
    /// <summary>"near" = the fastest lap by someone rated up to 1000 iRating above you; "fastest" = the fastest lap you can see.</summary>
    public string Garage61Pick { get; set; } = "near";
    public string Garage61TokenVariable { get; set; } = "GARAGE61_TOKEN";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static string PathFor(string baseDir) => Path.Combine(baseDir, "voice.json");

    public static VoiceConfig Load(string baseDir)
    {
        var path = PathFor(baseDir);
        if (!File.Exists(path))
        {
            var fresh = new VoiceConfig();
            fresh.Save(baseDir);
            return fresh;
        }
        return JsonSerializer.Deserialize<VoiceConfig>(File.ReadAllText(path), Json) ?? new VoiceConfig();
    }

    public void Save(string baseDir) => File.WriteAllText(PathFor(baseDir), JsonSerializer.Serialize(this, Json));
}
