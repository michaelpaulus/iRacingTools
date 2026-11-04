using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Stage = Pipeline.Stage;
using State = Pipeline.State;

/// <summary>
/// The window: pick a replay, see which stages are done, make the highlights video (finished stages are skipped), redo
/// a stage, and in paste mode copy the narration prompt for Claude and paste the reply back.
/// </summary>
public sealed class MainForm : Form
{
    static readonly (Stage Stage, string Title, string Help)[] StageInfo =
    {
        (Stage.Scan, "1  Scan", "Plays the race at 16× in iRacing to find the story. Needs the replay open in iRacing."),
        (Stage.Plan, "2  Plan", "Picks the highlights and their camera shots. Quick, no iRacing needed."),
        (Stage.Commentary, "3  Narration", "Claude's voiceover script, voiced."),
        (Stage.Shoot, "4  Record", "Plays each highlight in iRacing and records it. iRacing goes in front; don't touch the PC."),
        (Stage.Edit, "5  Edit", "Clips, graphics, narration and sound into the finished mp4."),
    };

    static readonly (string Focus, string Label)[] Focuses =
    {
        (HighlightsOf.Mixed, "The race, plus my car"),
        (HighlightsOf.Mine, "Mostly my race"),
        (HighlightsOf.Race, "The race only"),
    };

    static readonly (string Mode, string Label)[] Modes =
    {
        ("paste", "Paste into Claude yourself"),
        ("claude", "Claude Code (your subscription)"),
        ("simple", "Simple, built in"),
    };

    static readonly (string Engine, string Label)[] Engines =
    {
        ("kokoro", "Kokoro (natural, am_michael)"),
        ("chatterbox", "Chatterbox (expressive, uses the GPU)"),
    };

    readonly BroadcastConfig _cfg;
    readonly string _cfgPath;
    readonly string _replayDir;
    Pipeline? _p;
    CancellationTokenSource? _cts;
    bool _loading;
    DateTime _infoStamp;

    readonly ComboBox _replays = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Label _info = new() { AutoSize = true, Padding = new Padding(0, 2, 0, 6) };
    readonly ComboBox _featured = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly ComboBox _engine = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    readonly ComboBox _focus = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    readonly Dictionary<Stage, (Label Status, Button Run)> _rows = new();
    readonly GroupBox _pasteBox = new() { Text = "Narration from Claude", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
    readonly Button _copyPrompt = new() { Text = "1. Copy the prompt", AutoSize = true };
    readonly Button _openPrompt = new() { Text = "Open prompt file", AutoSize = true };
    // MaxLength 0 = no limit; the default (32,767 characters) silently cuts a long reply short.
    readonly TextBox _reply = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 90, Font = new Font("Consolas", 9f), MaxLength = 0 };
    readonly Button _useReply = new() { Text = "2. Use this reply", AutoSize = true };
    readonly Label _pasteStatus = new() { AutoSize = true, Padding = new Padding(6, 6, 0, 0), MaximumSize = new Size(700, 0) };
    readonly Button _make = new() { Text = "Make video", AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Padding = new Padding(12, 4, 12, 4) };
    readonly Button _stop = new() { Text = "Stop", AutoSize = true, Enabled = false, Padding = new Padding(8, 4, 8, 4) };
    readonly Button _openVideo = new() { Text = "Open video", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    readonly Button _openFolder = new() { Text = "Show in folder", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window };
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };

    public MainForm(BroadcastConfig cfg, string cfgPath, string? rpy)
    {
        _cfg = cfg;
        _cfgPath = cfgPath;
        _replayDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "replay");
        Text = "iRacing Broadcast";
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 820);
        MinimumSize = new Size(820, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Build();
        _ = Handle; // the log can take lines from the start
        Console.SetOut(new LogWriter(this, _log));
        LoadReplays(rpy);
        _refresh.Tick += (_, _) => RefreshState();
        _refresh.Start();
    }

    void Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var replayRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        replayRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        replayRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        replayRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new Button { Text = "Browse…", AutoSize = true };
        replayRow.Controls.Add(new Label { Text = "Replay", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        replayRow.Controls.Add(_replays, 1, 0);
        replayRow.Controls.Add(browse, 2, 0);
        root.Controls.Add(replayRow);
        root.Controls.Add(_info);

        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
        options.Controls.Add(new Label { Text = "Follow", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        options.Controls.Add(_featured);
        options.Controls.Add(new Label { Text = "    Highlights of", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        options.Controls.Add(_focus);
        options.SetFlowBreak(_focus, true);
        options.Controls.Add(new Label { Text = "Narration", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        options.Controls.Add(_mode);
        options.Controls.Add(new Label { Text = "    Voice", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        options.Controls.Add(_engine);
        root.Controls.Add(options);

        var stages = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true, CellBorderStyle = TableLayoutPanelCellBorderStyle.None };
        stages.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        stages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stages.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        stages.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int r = 0;
        foreach (var (stage, title, help) in StageInfo)
        {
            var status = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
            var run = new Button { Text = "Run", AutoSize = true, Anchor = AnchorStyles.Right };
            run.Click += (_, _) => RunStage(stage);
            stages.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 0, 12, 0) }, 0, r);
            stages.Controls.Add(new Label { Text = help, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText }, 1, r);
            stages.Controls.Add(status, 2, r);
            stages.Controls.Add(run, 3, r);
            _rows[stage] = (status, run);
            r++;
        }
        var stageBox = new GroupBox { Text = "Stages (finished ones are kept: closing the app and coming back carries on)", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        stageBox.Controls.Add(stages);
        root.Controls.Add(stageBox);

        var paste = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        var pasteTop = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        pasteTop.Controls.Add(_copyPrompt);
        pasteTop.Controls.Add(_openPrompt);
        pasteTop.Controls.Add(new Label { Text = "then paste it into a new Claude chat (claude.ai or Claude Code), and paste Claude's whole reply here:", AutoSize = true, Padding = new Padding(4, 7, 0, 0) });
        paste.Controls.Add(pasteTop);
        paste.Controls.Add(_reply);
        var pasteBottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        pasteBottom.Controls.Add(_useReply);
        pasteBottom.Controls.Add(_pasteStatus);
        paste.Controls.Add(pasteBottom);
        _pasteBox.Controls.Add(paste);
        root.Controls.Add(_pasteBox);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 6) };
        buttons.Controls.AddRange([_make, _stop, _openVideo, _openFolder]);
        root.Controls.Add(buttons);

        root.Controls.Add(_log);
        for (int i = 0; i < root.Controls.Count - 1; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        foreach (var (_, label) in Modes) _mode.Items.Add(label);
        _loading = true;
        _mode.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Mode == _cfg.Commentary.ToLowerInvariant()));
        foreach (var (_, label) in Engines) _engine.Items.Add(label);
        _engine.SelectedIndex = Math.Max(0, Array.FindIndex(Engines, e => e.Engine == _cfg.VoiceEngine.ToLowerInvariant()));
        foreach (var (_, label) in Focuses) _focus.Items.Add(label);
        _focus.SelectedIndex = 0;
        _loading = false;

        browse.Click += (_, _) => Browse();
        _replays.SelectedIndexChanged += (_, _) => { if (!_loading) SelectReplay(((ReplayItem)_replays.SelectedItem!).Path); };
        _featured.SelectedIndexChanged += (_, _) => FeaturedChanged();
        _mode.SelectedIndexChanged += (_, _) => ModeChanged();
        _engine.SelectedIndexChanged += (_, _) => EngineChanged();
        _focus.SelectedIndexChanged += (_, _) => FocusChanged();
        _copyPrompt.Click += (_, _) => CopyPrompt();
        _openPrompt.Click += (_, _) => { if (EnsurePrompt()) Shell(_p!.PromptPath); };
        _useReply.Click += (_, _) => UseReply();
        _make.Click += (_, _) => Start((p, ct) => p.RunAll(ct), "Make video");
        _stop.Click += (_, _) => { _cts?.Cancel(); Console.WriteLine("Stopping..."); };
        _openVideo.Click += (_, _) => { if (_p != null && File.Exists(_p.Output)) Shell(_p.Output); };
        _openFolder.Click += (_, _) =>
        {
            if (_p == null) return;
            if (File.Exists(_p.Output)) Process.Start("explorer.exe", $"/select,\"{_p.Output}\"");
            else if (Directory.Exists(_p.Work)) Shell(_p.Work);
        };
    }

    sealed record ReplayItem(string Path, string Label)
    {
        public override string ToString() => Label;
    }

    void LoadReplays(string? select)
    {
        _loading = true;
        _replays.Items.Clear();
        var files = Directory.Exists(_replayDir)
            ? new DirectoryInfo(_replayDir).GetFiles("*.rpy").OrderByDescending(f => f.LastWriteTime).ToList()
            : new List<FileInfo>();
        if (select != null && File.Exists(select) && !files.Any(f => f.FullName.Equals(Path.GetFullPath(select), StringComparison.OrdinalIgnoreCase)))
            files.Insert(0, new FileInfo(select));
        foreach (var f in files) _replays.Items.Add(new ReplayItem(f.FullName, Describe(f)));
        _loading = false;
        int index = select == null ? 0 : Math.Max(0, files.FindIndex(f => f.FullName.Equals(Path.GetFullPath(select), StringComparison.OrdinalIgnoreCase)));
        if (_replays.Items.Count > 0) _replays.SelectedIndex = index; // selects the replay
        else
        {
            _info.Text = $"No replays in {_replayDir}. Save one in iRacing, or use Browse.";
            RefreshState();
        }
    }

    string Describe(FileInfo f)
    {
        var name = Path.GetFileNameWithoutExtension(f.Name);
        string status = File.Exists(Pipeline.OutputFor(_cfg, name)) ? "    ✓ highlights made"
            : Directory.Exists(Path.Combine(_cfg.WorkDir, name)) ? "    · started" : "";
        return $"{name}     {f.LastWriteTime:ddd d MMM yyyy HH:mm}     {f.Length / 1048576.0:0} MB{status}";
    }

    void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose an iRacing replay",
            Filter = "iRacing replays (*.rpy)|*.rpy",
            InitialDirectory = Directory.Exists(_replayDir) ? _replayDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadReplays(dlg.FileName);
    }

    void SelectReplay(string rpy)
    {
        if (_p?.Rpy.Equals(rpy, StringComparison.OrdinalIgnoreCase) == true) return;
        _p?.Dispose();
        _p = new Pipeline(rpy, _cfg) { Mode = Modes[Math.Max(0, _mode.SelectedIndex)].Mode };
        _p.Changed += () => BeginInvoke(RefreshState);
        _loading = true;
        _focus.SelectedIndex = Math.Max(0, Array.FindIndex(Focuses, f => f.Focus == _p.FocusName));
        _loading = false;
        _infoStamp = default;
        _reply.Clear();
        _pasteStatus.Text = "";
        _log.Clear();
        Console.WriteLine($"{_p.Name}: work folder {_p.Work}");
        RefreshState();
    }

    /// <summary>Everything that depends on the files: the race info, the driver list and the stages.</summary>
    void RefreshState()
    {
        bool busy = _cts != null;
        _replays.Enabled = !busy;
        _make.Enabled = !busy && _p != null && Pipeline.Stages.Any(s => _p.StateOf(s) != State.Done);
        _make.Text = _p != null && Pipeline.Stages.All(s => _p.StateOf(s) == State.Done) ? "Video ready" : "Make video";
        _focus.Enabled = !busy;
        _stop.Enabled = busy;
        _openVideo.Enabled = _p != null && File.Exists(_p.Output);
        _openFolder.Enabled = _p != null;
        _pasteBox.Visible = _p?.Mode == "paste";
        _featured.Enabled = !busy;
        _mode.Enabled = !busy;
        _engine.Enabled = !busy;
        if (_p == null) { foreach (var (status, run) in _rows.Values) { status.Text = ""; run.Enabled = false; } return; }

        if (File.Exists(_p.YamlPath) && File.GetLastWriteTimeUtc(_p.YamlPath) != _infoStamp)
        {
            _infoStamp = File.GetLastWriteTimeUtc(_p.YamlPath);
            ShowInfo(_p.Info!);
        }
        else if (!File.Exists(_p.YamlPath) && _infoStamp != DateTime.MinValue.AddTicks(1))
        {
            _infoStamp = DateTime.MinValue.AddTicks(1);
            _info.Text = "Not scanned yet. Open this replay in iRacing (watch it from the race results, or under Replays), then Make video.";
            _loading = true;
            _featured.Items.Clear();
            _featured.Items.Add("(whoever saved the replay)");
            _featured.SelectedIndex = 0;
            _loading = false;
        }

        foreach (var (stage, _, _) in StageInfo)
        {
            var (status, run) = _rows[stage];
            var state = _p.StateOf(stage);
            if (_p.Current == stage)
            {
                status.Text = stage == Stage.Commentary && _p.Mode == "paste" ? "▶ waiting for your reply" : "▶ running…";
                status.ForeColor = Color.RoyalBlue;
            }
            else
            {
                (status.Text, status.ForeColor) = state switch
                {
                    State.Done => ($"✓ done {_p.DoneAt(stage):d MMM HH:mm}", Color.ForestGreen),
                    State.OutOfDate => ("out of date", Color.DarkOrange),
                    State.Waiting => ("waiting for your reply", Color.DarkOrange),
                    _ => ("not done", SystemColors.GrayText),
                };
            }
            run.Text = state == State.Done ? "Redo" : "Run";
            run.Enabled = !busy && _p.CanRun(stage);
        }
    }

    void ShowInfo(RaceInfo info)
    {
        double minutes = info.RaceLengthSeconds > 0 ? info.RaceLengthSeconds / 60 : 0;
        _info.Text = $"{info.Track}{(info.TrackConfig.Length > 0 ? " – " + info.TrackConfig : "")}   ·   {info.CarsSummary}   ·   " +
                     $"{info.Drivers.Count} drivers   ·   SOF {RaceInfo.Rating(info.Sof)}" + (info.RaceLaps is int laps ? $"   ·   {laps} laps" : minutes > 0 ? $"   ·   {minutes:0} min" : "");
        _loading = true;
        _featured.Items.Clear();
        _featured.Items.Add("(whoever saved the replay)");
        foreach (var d in info.Drivers.OrderBy(d => d.Name)) _featured.Items.Add(d.Name + (d.IsPlayer ? "  (saved the replay)" : ""));
        var name = _p!.FeaturedName;
        int idx = name.Length == 0 ? 0 : _featured.Items.Cast<object>().ToList().FindIndex(o => o.ToString()!.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        _featured.SelectedIndex = Math.Max(0, idx);
        _loading = false;
    }

    void FeaturedChanged()
    {
        if (_loading || _p == null) return;
        var name = _featured.SelectedIndex <= 0 ? "" : _featured.SelectedItem!.ToString()!.Replace("  (saved the replay)", "");
        if (name == _p.FeaturedName) return;
        _p.FeaturedName = name;
        if (File.Exists(_p.ClipsPath)) Console.WriteLine("The plan follows another driver now: it, the narration and the recording will be redone.");
        RefreshState();
    }

    void FocusChanged()
    {
        if (_loading || _p == null) return;
        var focus = Focuses[_focus.SelectedIndex].Focus;
        if (focus == _p.FocusName) return;
        _p.FocusName = focus;
        _cfg.HighlightFocus = focus; // the default for the next replay
        try { _cfg.Save(_cfgPath); } catch (IOException) { }
        if (File.Exists(_p.ClipsPath)) Console.WriteLine("Other highlights now: the plan, the narration and the recording will be redone.");
        RefreshState();
    }

    void ModeChanged()
    {
        if (_loading) return;
        var mode = Modes[_mode.SelectedIndex].Mode;
        _cfg.Commentary = mode;
        try { _cfg.Save(_cfgPath); } catch (IOException) { }
        if (_p != null) _p.Mode = mode;
        if (mode == "claude" && ClaudeCli.Find() == null)
            Console.WriteLine("The Claude Code CLI (claude) isn't installed, so the narration stage will stop. Install it and run \"claude\" once to sign in.");
        RefreshState();
    }

    void EngineChanged()
    {
        if (_loading) return;
        _cfg.VoiceEngine = Engines[_engine.SelectedIndex].Engine;
        try { _cfg.Save(_cfgPath); } catch (IOException) { }
        if (_p != null && File.Exists(_p.ScriptPath))
            Console.WriteLine("The narration will be voiced again with this voice (the script and the recording are kept), then the video re-edited.");
        if (Voices.UsesChatterbox(_cfg) && !File.Exists(Path.Combine(_cfg.ChatterboxDir, "venv", "Scripts", "python.exe")))
            Console.WriteLine($"Chatterbox isn't installed in {_cfg.ChatterboxDir}: see the README.");
        RefreshState();
    }

    bool EnsurePrompt()
    {
        if (_p == null) return false;
        if (_p.StateOf(Stage.Plan) != State.Done) { _pasteStatus.Text = "The prompt is made after the plan stage. Make video (or run the scan and plan) first."; return false; }
        try { _p.WritePrompt(); return true; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            _pasteStatus.Text = "Couldn't make the prompt: " + ex.Message;
            return false;
        }
    }

    void CopyPrompt()
    {
        if (!EnsurePrompt()) return;
        var text = File.ReadAllText(_p!.PromptPath);
        Clipboard.SetText(text);
        _pasteStatus.Text = $"Copied the prompt ({text.Length / 1024} KB). Paste it into a new Claude chat; when Claude has answered, paste the reply above.";
    }

    void UseReply()
    {
        if (_p == null) return;
        var text = _reply.Text.Trim();
        if (text.Length == 0 && Clipboard.ContainsText()) text = Clipboard.GetText().Trim();
        if (text.Length == 0) { _pasteStatus.Text = "Paste Claude's reply into the box first."; return; }
        if (_p.StateOf(Stage.Plan) != State.Done) { _pasteStatus.Text = "The plan stage has to be done first."; return; }
        try
        {
            var script = Commentary.FromJson(text);
            File.WriteAllText(_p.ReplyPath, text);
            _reply.Clear();
            _pasteStatus.Text = $"✓ {script.Lines.Count} lines: “{script.Title}”";
            if (_cts == null) Start((p, ct) => p.Run(Stage.Commentary, ct), "Voicing the narration");
            else _pasteStatus.Text += ". It's voiced before the edit.";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or FormatException or KeyNotFoundException)
        {
            _pasteStatus.Text = $"That doesn't read as Claude's JSON reply ({ex.Message}; {text.Length:N0} characters, ending \"…{text[^Math.Min(40, text.Length)..]}\"). " +
                                "Copy the whole reply and try again.";
        }
    }

    void RunStage(Stage stage)
    {
        if (_p == null) return;
        if (stage is Stage.Scan or Stage.Shoot && _p.StateOf(stage) == State.Done &&
            MessageBox.Show(this, stage == Stage.Shoot ? "Record the highlights again? iRacing goes in front for a few minutes." : "Scan the race again?",
                "iRacing Broadcast", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        Start((p, ct) => p.Run(stage, ct), StageInfo.First(s => s.Stage == stage).Title.Trim());
    }

    async void Start(Func<Pipeline, CancellationToken, Task> work, string what)
    {
        if (_p == null || _cts != null) return;
        var p = _p;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        RefreshState();
        try
        {
            await Task.Run(() => work(p, ct));
            Console.WriteLine($"{what}: done.");
            if (what == "Make video" && File.Exists(p.Output))
            {
                Activate();
                MessageBox.Show(this, $"Your highlights are ready:\n{p.Output}", "iRacing Broadcast");
            }
        }
        catch (OperationCanceledException) { Console.WriteLine($"{what}: stopped. Finished stages are kept."); }
        catch (Exception ex)
        {
            Console.WriteLine($"{what} stopped: {ex.Message}");
            Debug.WriteLine(ex);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            RefreshState();
            int i = _replays.SelectedIndex;
            if (i >= 0 && _replays.Items[i] is ReplayItem item)
            {
                _loading = true;
                _replays.Items[i] = item with { Label = Describe(new FileInfo(item.Path)) };
                _loading = false;
            }
        }
    }

    static void Shell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cts != null && MessageBox.Show(this, "A stage is running. Stop it and close? Finished stages are kept.", "iRacing Broadcast",
                MessageBoxButtons.OKCancel) != DialogResult.OK)
        {
            e.Cancel = true;
            return;
        }
        _cts?.Cancel();
        base.OnFormClosing(e);
    }

    /// <summary>Console output into the log box, a line at a time, from any thread.</summary>
    sealed class LogWriter(Form form, TextBox box) : TextWriter
    {
        readonly StringBuilder _line = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\r') return;
                if (value != '\n') { _line.Append(value); return; }
                var text = _line.ToString();
                _line.Clear();
                Post(text);
            }
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            foreach (var c in value) Write(c);
        }

        void Post(string text)
        {
            if (form.IsDisposed || !form.IsHandleCreated) return;
            form.BeginInvoke(() =>
            {
                if (box.TextLength > 400_000) box.Text = box.Text[^200_000..];
                box.AppendText(text + Environment.NewLine);
            });
        }
    }
}
