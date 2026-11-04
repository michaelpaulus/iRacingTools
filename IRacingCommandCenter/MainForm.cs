namespace IRacingCommandCenter;

/// <summary>
/// The command center window: power outlets (one row each, all on/off, automatic mode) and the helper apps
/// (status, start/stop, log, settings, edit, start with iRacing).
/// </summary>
public class MainForm : Form
{
    readonly TrayApp _app;
    readonly Dictionary<int, (Label Name, Label Status, Button Button)> _rows = [];
    readonly List<(HelperApp App, Label Status, Button Toggle, CheckBox WithIRacing)> _appRows = [];
    readonly CheckBox _auto, _pauseOneDrive, _spanWindows;
    readonly Label _status;
    readonly Panel _appsPanel;
    readonly Button _addApp, _guide, _refLaps;
    readonly int _appsTop;

    /// <summary>Closing the window just hides it to the tray unless the app is exiting.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    public MainForm(TrayApp app)
    {
        _app = app;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "iRacing Command Center";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        int y = 10;
        Controls.Add(Heading("Power", y));
        y += 26;
        foreach (var port in app.Config.Outlets)
        {
            var name = new Label { Left = 15, Top = y + 5, Width = 130, Text = $"Outlet {port}" };
            var status = new Label { Left = 150, Top = y + 5, Width = 110 };
            var button = new Button { Left = 265, Top = y, Width = 85, Height = 28 };
            button.Click += async (_, _) => await Busy(() => _app.ToggleAsync(port));
            Controls.AddRange([name, status, button]);
            _rows[port] = (name, status, button);
            y += 36;
        }

        y += 6;
        var allOn = new Button { Left = 15, Top = y, Width = 105, Height = 28, Text = "All on" };
        var allOff = new Button { Left = 130, Top = y, Width = 105, Height = 28, Text = "All off" };
        var refresh = new Button { Left = 245, Top = y, Width = 105, Height = 28, Text = "Refresh" };
        allOn.Click += async (_, _) => await Busy(() => _app.SwitchAsync(_app.Config.Outlets, true));
        allOff.Click += async (_, _) => await Busy(() => _app.SwitchAsync(_app.Config.Outlets, false));
        refresh.Click += async (_, _) => await Busy(_app.RefreshAsync);

        y += 40;
        _auto = new CheckBox
        {
            Left = 15, Top = y, Width = 500,
            Text = $"Automatic: on with iRacing, AC EVO or AC Rally, off {app.Config.OffDelaySeconds}s after they close",
        };
        _auto.Click += (_, _) => _app.SetAuto(_auto.Checked);

        y += 28;
        _spanWindows = new CheckBox
        {
            Left = 15, Top = y, Width = 500,
            Text = "Stretch iRacing, AC EVO and AC Rally across all 3 screens",
        };
        _spanWindows.Click += (_, _) => _app.SetSpanWindows(_spanWindows.Checked);

        y += 28;
        _pauseOneDrive = new CheckBox
        {
            Left = 15, Top = y, Width = 335,
            Text = "Pause OneDrive while racing (restarts when iRacing closes)",
        };
        _pauseOneDrive.Click += (_, _) => _app.SetPauseOneDrive(_pauseOneDrive.Checked);

        y += 36;
        Controls.Add(Heading("Apps", y));
        y += 26;
        _appsTop = y;
        _appsPanel = new Panel { Left = 0, Top = y, Width = AppsWidth };
        _addApp = new Button { Left = 15, Width = 105, Height = 28, Text = "Add app..." };
        _addApp.Click += (_, _) => EditApp(null);
        _guide = new Button { Left = 130, Width = 120, Height = 28, Text = "Setup guide..." };
        _guide.Click += (_, _) => ShowGuide();
        _refLaps = new Button { Left = 260, Width = 130, Height = 28, Text = "Reference laps..." };
        _refLaps.Click += (_, _) => ShowReferenceLaps();
        _status = new Label { Left = 15, Width = AppsWidth - 30, Height = 36, ForeColor = Color.DimGray };

        Controls.AddRange([allOn, allOff, refresh, _auto, _spanWindows, _pauseOneDrive, _appsPanel, _addApp, _guide, _refLaps, _status]);
        RebuildApps();
    }

    const int AppsWidth = 745, AppRowHeight = 34;
    readonly ToolTip _tips = new();

    public void ShowGuide()
    {
        using var guide = new SetupGuide(_app);
        guide.ShowDialog(this);
        RebuildApps();
    }

    HelperApp? VoiceApp() => _app.Config.Apps.FirstOrDefault(a => a.SettingsFile.Equals("voice.json", StringComparison.OrdinalIgnoreCase));

    /// <summary>The voice coach's reference laps and the Garage 61 download.</summary>
    public void ShowReferenceLaps()
    {
        if (VoiceApp() is not { } voice || HelperManager.SettingsPath(voice) is not { } path || !File.Exists(path)) return;
        using var dialog = new ReferenceLapsDialog(path);
        // Added laps are read when the coach next starts a car and track; the download setting at start: restart for both.
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { dialog.SaveSettings(); }
        catch (Exception ex) { MessageBox.Show(this, $"Couldn't save: {ex.Message}", "iRacing Command Center"); return; }
        if (_app.Helpers.IsRunning(voice)) _app.RestartApp(voice);
    }

    public void SetPushToTalk(HelperApp a)
    {
        var path = HelperManager.SettingsPath(a);
        if (path == null || !File.Exists(path)) return;
        using var dialog = new PushToTalkDialog(a.Name, PushToTalkConfig.Read(path));
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Chosen == null) return;
        try { dialog.Chosen.Write(path); }
        catch (Exception ex) { MessageBox.Show(this, $"Couldn't save: {ex.Message}", "iRacing Command Center"); return; }
        Log.Write($"{a.Name} push-to-talk set to {new PushToTalk(dialog.Chosen).Describe()}");
        // The app reads its settings at start: restart it so the new button works now.
        if (_app.Helpers.IsRunning(a)) _app.RestartApp(a);
        RebuildApps();
    }

    static Label Heading(string text, int y) =>
        new() { Left = 15, Top = y, Width = 300, Height = 22, Text = text, Font = new Font("Segoe UI Semibold", 10.5F) };

    /// <summary>One row per helper app; rebuilt when apps are added, edited or removed.</summary>
    public void RebuildApps()
    {
        _appsPanel.Controls.Clear();
        _appRows.Clear();
        int y = 0;
        foreach (var helper in _app.Config.Apps)
        {
            var a = helper;
            var name = new Label { Left = 15, Top = y + 7, Width = 120, Text = a.Name, AutoEllipsis = true };
            var status = new Label { Left = 140, Top = y + 7, Width = 150, AutoEllipsis = true };
            var toggle = new Button { Left = 295, Top = y, Width = 70, Height = 28 };
            toggle.Click += (_, _) => { _app.ToggleApp(a); UpdateView(); };
            var log = new Button { Left = 370, Top = y, Width = 50, Height = 28, Text = "Log", Enabled = a.HideWindow };
            log.Click += (_, _) => OpenFile(HelperManager.LogPath(a), "No log yet: it's written once the app has run from here.");
            var settings = new Button { Left = 425, Top = y, Width = 70, Height = 28, Text = "Settings", Enabled = a.SettingsFile.Length > 0 };
            settings.Click += (_, _) => OpenFile(HelperManager.SettingsPath(a)!, "The settings file doesn't exist yet: it's created the first time the app runs.");
            // Only apps whose settings file has a push-to-talk block (the voice agent, the coach) get this button.
            var current = PushToTalkConfig.Read(HelperManager.SettingsPath(a));
            var ptt = new Button { Left = 500, Top = y, Width = 95, Height = 28, Text = "Push-to-talk", Enabled = current != null };
            if (current != null) _tips.SetToolTip(ptt, $"Now: {new PushToTalk(current).Describe()}");
            ptt.Click += (_, _) => SetPushToTalk(a);
            var edit = new Button { Left = 600, Top = y, Width = 45, Height = 28, Text = "Edit" };
            edit.Click += (_, _) => EditApp(a);
            var withIRacing = new CheckBox { Left = 652, Top = y + 4, Width = 85, Text = "w/ iRacing", Checked = a.StartWithIRacing };
            withIRacing.Click += (_, _) => { a.StartWithIRacing = withIRacing.Checked; _app.SaveApps(); };
            _appsPanel.Controls.AddRange([name, status, toggle, log, settings, ptt, edit, withIRacing]);
            _appRows.Add((a, status, toggle, withIRacing));
            y += AppRowHeight;
        }
        _appsPanel.Height = Math.Max(y, 1);
        _addApp.Top = _guide.Top = _refLaps.Top = _appsTop + y + 4;
        _refLaps.Enabled = VoiceApp() is { } v && File.Exists(HelperManager.SettingsPath(v));
        _status.Top = _addApp.Bottom + 10;
        ClientSize = new Size(AppsWidth, _status.Bottom + 10);
        UpdateView();
    }

    void EditApp(HelperApp? existing)
    {
        using var dialog = new AppEditor(existing?.Clone() ?? new HelperApp { Name = "New app" }, existing != null);
        var result = dialog.ShowDialog(this);
        if (result == DialogResult.OK)
        {
            if (existing == null) _app.Config.Apps.Add(dialog.App);
            else _app.Config.Apps[_app.Config.Apps.IndexOf(existing)] = dialog.App;
            _app.SaveApps();
        }
        else if (result == DialogResult.Abort && existing != null) // Remove
        {
            _app.StopApp(existing);
            _app.Config.Apps.Remove(existing);
            _app.SaveApps();
        }
    }

    static void OpenFile(string path, string missing)
    {
        if (!File.Exists(path)) { MessageBox.Show(missing, "iRacing Command Center"); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void UpdateView()
    {
        foreach (var (port, row) in _rows)
        {
            var o = _app.Find(port);
            row.Name.Text = _app.NameOf(port);
            row.Status.Text = o is null ? "?" : o.On ? $"ON  ({o.Watts} W)" : "off";
            row.Status.ForeColor = o?.On == true ? Color.ForestGreen : Color.DimGray;
            row.Button.Text = o?.On == true ? "Turn off" : "Turn on";
        }
        _auto.Checked = _app.Config.Auto;
        _pauseOneDrive.Checked = _app.Config.PauseOneDrive;
        _spanWindows.Checked = _app.Config.SpanWindows;
        foreach (var (a, status, toggle, withIRacing) in _appRows)
        {
            bool running = _app.Helpers.IsRunning(a);
            status.Text = _app.Helpers.Status(a);
            status.ForeColor = running ? Color.ForestGreen : Color.DimGray;
            toggle.Text = running ? "Stop" : "Start";
            withIRacing.Checked = a.StartWithIRacing;
        }
        _status.Text = _app.StatusText;
    }

    async Task Busy(Func<Task> action)
    {
        UseWaitCursor = true;
        try { await action(); }
        finally { UseWaitCursor = false; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}

/// <summary>Edits one helper app's definition (path, arguments, folder, when it starts and stops).</summary>
public class AppEditor : Form
{
    public HelperApp App { get; }

    public AppEditor(HelperApp app, bool canRemove)
    {
        App = app;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"App: {app.Name}";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(560, 420);

        var grid = new PropertyGrid
        {
            Dock = DockStyle.Fill, SelectedObject = app, ToolbarVisible = false, PropertySort = PropertySort.NoSort, HelpVisible = true,
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var ok = new Button { Width = 80, Height = 28, Text = "Save", DialogResult = DialogResult.OK };
        var cancel = new Button { Width = 80, Height = 28, Text = "Cancel", DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([cancel, ok]);
        if (canRemove)
        {
            var remove = new Button { Width = 80, Height = 28, Text = "Remove" };
            remove.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Remove {app.Name} from the command center? (The program itself isn't touched.)",
                        "Remove app", MessageBoxButtons.OKCancel) == DialogResult.OK)
                {
                    DialogResult = DialogResult.Abort;
                }
            };
            buttons.Controls.Add(remove);
        }
        Controls.AddRange([grid, buttons]);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
