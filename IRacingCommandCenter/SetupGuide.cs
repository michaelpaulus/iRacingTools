using System.Text.Json.Nodes;

namespace IRacingCommandCenter;

/// <summary>
/// Step-by-step setup for the helpers, with a tick for everything that can be checked from here (push-to-talk, the
/// Claude CLI, the speech models, start with Windows) and plain instructions for what has to be done in iRacing.
/// "Check again" re-runs the checks.
/// </summary>
public class SetupGuide : Form
{
    readonly TrayApp _app;
    readonly FlowLayoutPanel _steps;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public SetupGuide(TrayApp app)
    {
        _app = app;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Setup guide";
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(640, 640);
        MinimizeBox = false;

        _steps = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12),
        };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var close = new Button { Width = 90, Height = 28, Text = "Close", DialogResult = DialogResult.Cancel };
        var recheck = new Button { Width = 110, Height = 28, Text = "Check again" };
        recheck.Click += async (_, _) => await Build();
        bottom.Controls.AddRange([close, recheck]);
        Controls.AddRange([_steps, bottom]);
        CancelButton = close;
        Shown += async (_, _) => await Build();
    }

    HelperApp? App(string settingsFile) =>
        _app.Config.Apps.FirstOrDefault(a => a.SettingsFile.Equals(settingsFile, StringComparison.OrdinalIgnoreCase));

    async Task Build()
    {
        UseWaitCursor = true;
        _steps.SuspendLayout();
        _steps.Controls.Clear();
        int n = 0;
        string Num() => $"{++n}. ";

        var voice = App("voice.json");
        var voicePath = voice == null ? null : HelperManager.SettingsPath(voice);
        var voiceJson = Read(voicePath);
        var voiceDir = voice?.WorkingDir ?? "";

        // 1. iRacing display mode
        Step(Num() + "iRacing in windowed or borderless mode", null,
            "Overlays can only draw over iRacing when it isn't in exclusive fullscreen. In the sim: Options > Graphics, " +
            "untick Fullscreen (or choose Borderless window), then restart the sim.");

        // 2. Push-to-talk
        var ptt = PushToTalkConfig.Read(voicePath);
        bool pttSet = ptt is { Type: "Keyboard" or "Joystick" };
        Step(Num() + "Push-to-talk button for the voice agent", pttSet,
            pttSet ? $"Set to {new PushToTalk(ptt!).Describe()}. Hold it while you talk, let go when you're done."
                   : "Pick the wheel button (or key) you'll hold while talking.",
            voice == null || voicePath == null ? null : ("Set button...", () => { _app.ShowPushToTalk(voice); _ = Build(); }));

        // 3. The model that works out what you said: Claude through the Claude CLI unless voice.json lists others
        var first = voiceJson?["Models"]?.AsArray().FirstOrDefault();
        string kind = first?["Kind"]?.GetValue<string>() ?? "claude-cli";
        if (kind.Equals("claude-cli", StringComparison.OrdinalIgnoreCase))
        {
            bool hasCli = FindClaudeCli() != null;
            Step(Num() + "Claude CLI (works out what you said)", hasCli,
                hasCli ? "Installed. If the voice agent says it can't reach Claude, run claude in a terminal and sign in with /login."
                       : "Install it in PowerShell with: irm https://claude.ai/install.ps1 | iex — then run claude and sign in with /login " +
                         "(your Claude subscription; no API key).",
                hasCli ? null : ("claude.ai/download", () => Open("https://claude.ai/download")));
        }
        else
        {
            string name = first?["Name"]?.GetValue<string>() ?? kind;
            string keyVar = first?["KeyVariable"]?.GetValue<string>() ?? "";
            bool hasKey = keyVar.Length == 0 || Environment.GetEnvironmentVariable(keyVar, EnvironmentVariableTarget.User) != null
                          || Environment.GetEnvironmentVariable(keyVar) != null;
            Step(Num() + $"{name} (works out what you said)", hasKey,
                hasKey ? $"Set up in the voice agent's settings ({kind})." : $"Set the {keyVar} environment variable to your key, then restart the voice agent.");
        }

        // 4. Speech models: Whisper hears you, Kokoro speaks
        string whisper = voiceJson?["WhisperModel"]?.GetValue<string>() ?? "ggml-base.en.bin";
        string kokoroVoice = voiceJson?["KokoroVoice"]?.GetValue<string>() ?? "am_michael";
        string kokoroModel = voiceJson?["KokoroModel"]?.GetValue<string>() ?? "kokoro-multi-lang-v1_0";
        bool hasWhisper = File.Exists(Path.Combine(voiceDir, whisper));
        bool hasVoice = kokoroVoice.Length == 0 || Directory.Exists(Path.Combine(voiceDir, "voices", kokoroModel));
        Step(Num() + "Speech models", hasWhisper && hasVoice,
            hasWhisper && hasVoice ? $"Speech recognition ({whisper}) and the voice ({(kokoroVoice.Length > 0 ? "Kokoro " + kokoroVoice : "Windows")}) are downloaded."
                                   : "Downloaded automatically the first time the voice agent starts (about 150 MB for speech recognition, 330 MB for the Kokoro voice). Start it once below, or let it start with iRacing.",
            voice == null || (hasWhisper && hasVoice) ? null : ("Start voice agent", () => { _app.StartApp(voice); }));

        // 5. iRacing voice chat keys
        string chatKeys = voiceJson?["MuteVoiceChatKeys"]?.GetValue<string>() ?? "Shift+Ctrl+Alt+M";
        string driverKeys = voiceJson?["MuteDriverKeys"]?.GetValue<string>() ?? "Shift+Ctrl+Alt+D";
        Step(Num() + "iRacing keys for \"mute chat\" and \"mute that driver\"", null,
            "The voice agent presses iRacing's own controls for these, so they need keys in iRacing. In the iRacing launcher: " +
            "Settings > Controls, scroll to Voice Chat Controls, then:\n" +
            $"  • click the box next to Mute Voice Chat and press {chatKeys}\n" +
            $"  • click the box next to Mute a Driver and press {driverKeys}\n" +
            "The boxes then read Shft-Ctrl-Alt-M / Shft-Ctrl-Alt-D. (To use other keys, change MuteVoiceChatKeys / " +
            "MuteDriverKeys in the voice agent's settings to match.)");

        // 6. Start with iRacing / Windows
        var auto = _app.Config.Apps.Where(a => a.StartWithIRacing).Select(a => a.Name).ToList();
        Step(Num() + "Start automatically", Startup.IsEnabled && auto.Count > 0,
            $"Command center starts with Windows: {(Startup.IsEnabled ? "yes" : "no")}. " +
            $"Started when the sim loads: {(auto.Count > 0 ? string.Join(", ", auto) : "nothing")} (the \"w/ iRacing\" boxes in the main window).",
            Startup.IsEnabled ? null : ("Start with Windows", () => { Startup.Set(true); _ = Build(); }));

        // 7. Try it
        Step(Num() + "Try it", null,
            "In the car, hold push-to-talk and say \"no tyres, twenty litres\" - you should hear \"No tyres. Fuel, 20 litres.\" " +
            "Other things to say: \"lefts only\", \"tear-off on\", \"fast repair off\", \"mute chat\", \"mute that driver\", " +
            "\"mute the race channel\", \"tell car 12 sorry\". If nothing happens, open the voice agent's Log in the main window.");

        _steps.ResumeLayout();
        UseWaitCursor = false;
    }

    /// <summary>One step: a title with a tick (done), cross (to do) or nothing (manual step), the explanation, an optional button.</summary>
    void Step(string title, bool? done, string text, (string Label, Action Click)? action = null)
    {
        int width = _steps.ClientSize.Width - 40;
        var panel = new Panel { Width = width, Margin = new Padding(0, 0, 0, 14) };
        var head = new Label
        {
            AutoSize = false, Width = width, Height = 22, Font = new Font("Segoe UI Semibold", 10F),
            Text = (done switch { true => "✔  ", false => "✖  ", null => "○  " }) + title,
            ForeColor = done switch { true => Color.ForestGreen, false => Color.Firebrick, null => SystemColors.ControlText },
        };
        var body = new Label { Left = 22, Top = 24, MaximumSize = new Size(width - 22, 0), AutoSize = true, Text = text, ForeColor = Color.DimGray };
        panel.Controls.AddRange([head, body]);
        int h = body.Top + body.PreferredHeight;
        if (action is { } a)
        {
            var b = new Button { Left = 22, Top = h + 6, AutoSize = true, Height = 28, Text = a.Label };
            b.Click += (_, _) => a.Click();
            panel.Controls.Add(b);
            h = b.Top + 30;
        }
        panel.Height = h + 2;
        _steps.Controls.Add(panel);
    }

    /// <summary>claude.exe where the voice agent looks for it (IRacingVoice\ClaudeCli.cs): its installer's folder, winget's, the PATH.</summary>
    static string? FindClaudeCli()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new List<string> { Path.Combine(home, ".local", "bin"), Path.Combine(localData, "Microsoft", "WinGet", "Links") };
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(Path.PathSeparator));
        var winget = Path.Combine(localData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget)) dirs.AddRange(Directory.GetDirectories(winget, "Anthropic.ClaudeCode*"));
        return dirs.Where(d => d.Length > 0)
            .SelectMany(d => new[] { Path.Combine(d, "claude.exe"), Path.Combine(d, "claude.cmd") })
            .FirstOrDefault(File.Exists);
    }

    static JsonNode? Read(string? path)
    {
        try { return path != null && File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; }
        catch (Exception) { return null; }
    }

    static void Open(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
