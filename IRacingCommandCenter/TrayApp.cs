using System.Diagnostics;

namespace IRacingCommandCenter;

/// <summary>
/// Tray icon + iRacing watcher: the command center. Owns the outlet state and the helper apps that the menu and window show.
/// </summary>
public class TrayApp : ApplicationContext
{
    public Config Config { get; } = Config.Load();
    public HelperManager Helpers { get; }
    public OneDrivePause OneDrive { get; } = new();
    public DriverSwap Drivers { get; } = new();
    public bool Swapping { get; private set; }
    public List<Outlet> Outlets { get; private set; } = [];
    public string? LastError { get; private set; }

    readonly ShellyClient _strip;
    readonly NotifyIcon _tray;
    readonly MainForm _form;
    readonly Dictionary<int, ToolStripMenuItem> _outletItems = [];
    readonly ToolStripMenuItem _autoItem, _appsItem, _driverItem, _startupItem, _oneDriveItem, _spanItem;
    readonly System.Windows.Forms.Timer _poll;
    readonly SemaphoreSlim _stripLock = new(1, 1);
    readonly RegisteredWaitHandle _showWait;
    readonly Icon _iconOn = DotIcon(Color.LimeGreen), _iconOff = DotIcon(Color.Gray), _iconError = DotIcon(Color.Firebrick);

    bool? _wasRunning;            // iRacing state on the previous tick; null = not checked yet
    bool? _simWasRunning;         // the sim itself (helper apps follow this)
    DateTime? _stoppedAt;         // when iRacing closed, while waiting out OffDelaySeconds
    DateTime _retryAfter = DateTime.MinValue;
    readonly Dictionary<int, bool> _desired = []; // last state we switched each outlet to
    TimeSpan? _lastUptime;
    bool _restorePending;         // the plug restarted and its outlets aren't back to what we set yet
    int _ticks;
    bool _tickBusy;
    string? _missingWarning;
    readonly HashSet<int> _userOff = []; // outlets you turned off yourself: the keep-on check leaves them off
    bool _failSafeDone;                  // the plug's own fail-safe settings have been checked
    DateTime _holdPowerUntil = DateTime.MinValue; // a driver swap closes the iRacing app briefly: keep the rig powered

    public TrayApp(EventWaitHandle showEvent, bool showWindow)
    {
        _strip = new ShellyClient(Config);
        Helpers = new HelperManager(Config);
        _form = new MainForm(this);
        // The app's own icon on the window and taskbar (the tray keeps the power-status dot).
        if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } appIcon) _form.Icon = appIcon;
        _ = _form.Handle; // create the handle so other threads can BeginInvoke onto it

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var port in Config.Outlets)
        {
            var item = new ToolStripMenuItem($"Outlet {port}", null, async (_, _) => await ToggleAsync(port));
            _outletItems[port] = item;
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("All on", null, async (_, _) => await SwitchAsync(Config.Outlets, true));
        menu.Items.Add("All off", null, async (_, _) => await SwitchAsync(Config.Outlets, false));
        menu.Items.Add(new ToolStripSeparator());
        // Apps submenu: one entry per helper, ticked while it runs; click to start or stop it.
        _appsItem = new ToolStripMenuItem("Apps");
        _appsItem.DropDownOpening += (_, _) =>
        {
            _appsItem.DropDownItems.Clear();
            foreach (var app in Config.Apps)
            {
                var a = app;
                _appsItem.DropDownItems.Add(new ToolStripMenuItem(a.Name, null, (_, _) => ToggleApp(a)) { Checked = Helpers.IsRunning(a) });
            }
        };
        _appsItem.DropDownItems.Add("(loading)");
        menu.Items.Add(_appsItem);
        // Driver submenu: who is signed in to the iRacing app (ticked); click another driver to swap.
        _driverItem = new ToolStripMenuItem("Driver");
        _driverItem.DropDownOpening += (_, _) =>
        {
            _driverItem.DropDownItems.Clear();
            var current = Drivers.Current;
            foreach (var name in Drivers.Drivers)
            {
                var n = name;
                bool isCurrent = n.Equals(current, StringComparison.OrdinalIgnoreCase);
                _driverItem.DropDownItems.Add(new ToolStripMenuItem(isCurrent ? n : $"Swap to {n}", null, async (_, _) => await SwapDriverAsync(n))
                    { Checked = isCurrent, Enabled = !isCurrent && !Swapping });
            }
            if (Drivers.Drivers.Count > 0) _driverItem.DropDownItems.Add(new ToolStripSeparator());
            _driverItem.DropDownItems.Add("Drivers...", null, (_, _) => { ShowWindow(); _form.ShowDrivers(); });
        };
        _driverItem.DropDownItems.Add("(loading)");
        menu.Items.Add(_driverItem);
        menu.Items.Add(new ToolStripSeparator());
        _autoItem = new ToolStripMenuItem("Automatic power (follow iRacing)", null, (_, _) => SetAuto(!Config.Auto));
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => { Startup.Set(!Startup.IsEnabled); UpdateUi(); });
        _oneDriveItem = new ToolStripMenuItem("Pause OneDrive while racing", null, (_, _) => SetPauseOneDrive(!Config.PauseOneDrive));
        _spanItem = new ToolStripMenuItem("Stretch sims across all 3 screens", null, (_, _) => SetSpanWindows(!Config.SpanWindows));
        menu.Items.Add(_autoItem);
        menu.Items.Add(_oneDriveItem);
        menu.Items.Add(_spanItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add("Setup guide...", null, (_, _) => { ShowWindow(); _form.ShowGuide(); });
        menu.Items.Add("Open log", null, (_, _) => Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray = new NotifyIcon { Icon = _iconOff, Text = "iRacing Command Center", ContextMenuStrip = menu, Visible = true };
        _tray.MouseDoubleClick += (_, _) => ShowWindow();

        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => _form.BeginInvoke(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        _poll = new System.Windows.Forms.Timer { Interval = Config.PollSeconds * 1000 };
        _poll.Tick += OnPoll;

        Log.Write($"Started; watching for {string.Join(", ", Config.Processes)} (outlets {string.Join(",", Config.Outlets)})");
        UpdateUi();
        if (showWindow) ShowWindow();
        _ = RefreshAsync();
        _poll.Start();
    }

    // ---- actions used by the menu and window

    /// <param name="byUser">false for the automatic switching; a user's own "off" while racing is respected by the keep-on check.</param>
    public async Task<bool> SwitchAsync(IReadOnlyCollection<int> ports, bool on, bool byUser = true)
    {
        // Skip outlets the plug doesn't have (config.json from an older plug with more outlets): switching one fails,
        // which would fail every switch, including the outlets that do exist.
        if (Outlets.Count > 0 && ports.Where(p => Find(p) == null).ToArray() is { Length: > 0 } missing)
        {
            var warning = $"The plug has no outlet {string.Join(",", missing)}: skipped (remove it from outlets in config.json)";
            if (warning != _missingWarning) Log.Write(warning);
            _missingWarning = warning;
            ports = ports.Except(missing).ToArray();
            if (ports.Count == 0) return true;
        }

        bool ok = true;
        await _stripLock.WaitAsync();
        try
        {
            await _strip.SetAsync(ports, on);
            foreach (var port in ports)
            {
                _desired[port] = on;
                if (byUser && on) _userOff.Remove(port);
                else if (byUser) _userOff.Add(port);
            }
            Log.Write($"Outlets {string.Join(",", ports)} {(on ? "ON" : "OFF")}{(byUser ? " (by you)" : "")}");
        }
        catch (Exception ex)
        {
            ok = false;
            Fail(ex);
            // power on is retried quickly; a failed off can wait
            _retryAfter = DateTime.Now.AddSeconds(on ? 5 : 30);
            _tray.ShowBalloonTip(5000, "iRacing Command Center", ex.Message, ToolTipIcon.Error);
        }
        finally { _stripLock.Release(); }
        await RefreshAsync();
        return ok;
    }

    public Task ToggleAsync(int port) => SwitchAsync([port], !(Find(port)?.On ?? false));

    public async Task RefreshAsync()
    {
        bool ok = false;
        await _stripLock.WaitAsync();
        try
        {
            Outlets = await _strip.GetAsync();
            ok = true;
            if (LastError != null) Log.Write("Plug reachable again");
            LastError = null;
            if (_strip.Uptime is TimeSpan up)
            {
                if (_lastUptime == null) Log.Write($"Plug uptime {up:d\\.hh\\:mm\\:ss}");
                else if (up < _lastUptime) { _restorePending = true; Log.Write($"Plug restarted (up {up:hh\\:mm\\:ss})"); }
                _lastUptime = up;
            }
            if (!_failSafeDone)
            {
                try
                {
                    var mine = Outlets.Select(o => o.Port).Intersect(Config.Outlets).ToList();
                    foreach (var change in await _strip.EnsureFailSafeAsync(mine)) Log.Write(change);
                    _failSafeDone = true;
                }
                catch (Exception ex) { Log.Write($"Couldn't check the plug's fail-safe settings (trying again later): {ex.Message}"); }
            }
        }
        catch (Exception ex) { Fail(ex); }
        finally { _stripLock.Release(); }
        UpdateUi();
        if (ok && _restorePending && !_restoring) await RestoreOutletsAsync();
    }

    bool _restoring;

    // After the plug reboots (a crash or power cut can bring it back off), put back any outlet whose state changed.
    // Retried on every refresh until it works: right after a reboot the plug can answer a status read and still fail a
    // switch, and a single try would leave you without power for the rest of the session.
    async Task RestoreOutletsAsync()
    {
        _restoring = true;
        try
        {
            var changed = _desired
                .Where(kv => Find(kv.Key) is { } o && o.On != kv.Value)
                .Where(kv => kv.Value || _wasRunning != true) // never put power back OFF while a sim is running
                .GroupBy(kv => kv.Value, kv => kv.Key)
                .Select(g => (On: g.Key, Ports: g.ToArray()))
                .ToList();
            bool allOk = true;
            foreach (var (on, ports) in changed)
            {
                Log.Write($"Restoring outlets {string.Join(",", ports)} {(on ? "ON" : "OFF")} after plug restart");
                allOk &= await SwitchAsync(ports, on, byUser: false);
            }
            if (allOk) _restorePending = false;
            else Log.Write("Restore failed; trying again at the next check");
        }
        finally { _restoring = false; }
    }

    /// <summary>Driver swap from the tray or the window. Shows progress and problems as tray balloons.</summary>
    public Task<bool> SwapDriverAsync(string name)
    {
        string? joined = null;
        return RunDriverActionAsync(async p =>
        {
            await Drivers.SwapToAsync(name, p);
            if (!Config.AutoJoin) return;
            p.Report($"Signed in as {name}: waiting for the Join button...");
            joined = await AutoJoin.PressAsync(Config.JoinButtons, TimeSpan.FromSeconds(Config.JoinWaitSeconds));
            if (joined == null) Log.Write($"Driver swap: no {string.Join(" / ", Config.JoinButtons)} button within {Config.JoinWaitSeconds}s");
        }, () => joined != null
            ? $"Signed in as {name} and pressed {joined}: click Drive when the sim loads."
            : Config.AutoJoin
                ? $"Signed in as {name}, but no Join button showed up: click Join, then Drive."
                : $"Signed in as {name}: click Join, then Drive.");
    }

    /// <summary>Adds a driver: opens the iRacing app on an empty profile for them to sign in once.</summary>
    public Task<bool> AddDriverAsync(string name) =>
        RunDriverActionAsync(p => Drivers.AddAsync(name, p), () => $"Sign in as {name} in the iRacing app. That sign-in is kept for swaps.");

    async Task<bool> RunDriverActionAsync(Func<IProgress<string>, Task> action, Func<string> doneMessage)
    {
        if (Swapping) return false;
        Swapping = true;
        _holdPowerUntil = DateTime.Now.AddMinutes(3);
        var progress = new Progress<string>(m => { _form.SetDriverStatus(m); });
        try
        {
            await action(progress);
            var done = doneMessage();
            _form.SetDriverStatus(done);
            _tray.ShowBalloonTip(5000, "Driver swap", done, ToolTipIcon.Info);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Driver swap failed: {ex.Message}");
            _form.SetDriverStatus(ex.Message);
            _tray.ShowBalloonTip(8000, "Driver swap", ex.Message, ToolTipIcon.Warning);
            return false;
        }
        finally
        {
            Swapping = false;
            UpdateUi();
        }
    }

    public void SetAuto(bool on)
    {
        Config.Auto = on;
        Config.Save();
        Log.Write($"Automatic mode {(on ? "ON" : "OFF")}");
        if (!on) _stoppedAt = null;
        UpdateUi();
    }

    public void SetPauseOneDrive(bool on)
    {
        Config.PauseOneDrive = on;
        Config.Save();
        Log.Write($"Pause OneDrive while racing {(on ? "ON" : "OFF")}");
        if (!on) OneDrive.Resume();
        else if (_simWasRunning == true) OneDrive.Pause();
        UpdateUi();
    }

    public void SetSpanWindows(bool on)
    {
        Config.SpanWindows = on;
        Config.Save();
        Log.Write($"Stretch sims across all screens {(on ? "ON" : "OFF")}");
        if (on) TripleSpan.Apply(Config.SpanProcesses);
        UpdateUi();
    }

    // ---- helper apps

    public void StartApp(HelperApp app)
    {
        try { Helpers.Start(app); }
        catch (Exception ex) { _tray.ShowBalloonTip(5000, "iRacing Command Center", ex.Message, ToolTipIcon.Error); }
        UpdateUi();
    }

    public void StopApp(HelperApp app)
    {
        Helpers.Stop(app);
        UpdateUi();
    }

    /// <summary>The push-to-talk dialog for an app (used by the setup guide).</summary>
    public void ShowPushToTalk(HelperApp app) => _form.SetPushToTalk(app);

    public void RestartApp(HelperApp app)
    {
        try { Helpers.Restart(app); }
        catch (Exception ex) { _tray.ShowBalloonTip(5000, "iRacing Command Center", ex.Message, ToolTipIcon.Error); }
        UpdateUi();
    }

    public void ToggleApp(HelperApp app)
    {
        if (Helpers.IsRunning(app)) StopApp(app);
        else StartApp(app);
    }

    /// <summary>Saves an edited app (or a new one) and refreshes the window.</summary>
    public void SaveApps()
    {
        Config.Save();
        Log.Write($"Apps saved: {string.Join(", ", Config.Apps.Select(a => a.Name))}");
        _form.RebuildApps();
        UpdateUi();
    }

    public Outlet? Find(int port) => Outlets.FirstOrDefault(o => o.Port == port);

    public string NameOf(int port) => Find(port) is { Label.Length: > 0 } o ? o.Label : $"Outlet {port}";

    public string StatusText
    {
        get
        {
            var other = Config.OtherSims.FirstOrDefault(p => Process.GetProcessesByName(p).Length > 0);
            var parts = new List<string> { $"iRacing: {(_simWasRunning == true ? "in the sim" : _wasRunning == true ? "launcher open" : "not running")}" };
            if (other != null) parts.Add($"{(other == "acr" ? "AC Rally" : other == "AssettoCorsaEVO" ? "AC EVO" : other)} running");
            if (Config.Auto && _stoppedAt is DateTime t)
                parts.Add($"turning off in {Math.Max(0, (int)(Config.OffDelaySeconds - (DateTime.Now - t).TotalSeconds))}s");
            if (OneDrive.PausedByUs) parts.Add("OneDrive paused");
            if (LastError != null) parts.Add($"Error: {LastError}");
            return string.Join("  ·  ", parts);
        }
    }

    // ---- iRacing watcher. Automatic mode acts only when iRacing starts or stops,
    // so a manual toggle sticks until the next start/stop.

    async void OnPoll(object? sender, EventArgs e)
    {
        if (_tickBusy) return;
        _tickBusy = true;
        try
        {
            bool? running = Config.Processes.Concat(Config.OtherSims).Any(p => Process.GetProcessesByName(p).Length > 0);
            // During a driver swap the iRacing app is closed for a few seconds: that isn't iRacing stopping.
            if (running == false && (Swapping || DateTime.Now < _holdPowerUntil)) running = true;
            else if (running == true && !Swapping) _holdPowerUntil = DateTime.MinValue;
            if (Config.SpanWindows) TripleSpan.Apply(Config.SpanProcesses);
            // Helper apps follow the sim itself (not the launcher).
            bool simRunning = Config.AppProcesses.Any(p => Process.GetProcessesByName(p).Length > 0);
            if (simRunning && _simWasRunning != true)
            {
                Helpers.OnIRacingStarted();
                if (Config.PauseOneDrive) OneDrive.Pause();
            }
            else if (!simRunning && _simWasRunning == true)
            {
                Helpers.OnIRacingStopped();
                OneDrive.Resume();
            }
            _simWasRunning = simRunning;
            // While waiting out a failed switch, leave _wasRunning alone: recording a start or stop here would
            // lose it, and the outlets would never follow it.
            bool waiting = Config.Auto && DateTime.Now < _retryAfter;
            if (Config.Auto && !waiting)
            {
                if (running == true && _wasRunning != true)
                {
                    _stoppedAt = null;
                    _userOff.Clear();
                    if (!await SwitchAsync(Config.Outlets, true, byUser: false)) running = null; // retry next tick
                }
                else if (running == true && Outlets.Count > 0
                    && Config.Outlets.Where(p => !_userOff.Contains(p) && Find(p) is { On: false }).ToArray() is { Length: > 0 } offNow)
                {
                    // Keep power on while racing: something else (the Shelly app, a plug reboot) turned it off.
                    Log.Write($"Outlets {string.Join(",", offNow)} found OFF while a sim is running; turning back on");
                    await SwitchAsync(offNow, true, byUser: false);
                }
                else if (running == false && _wasRunning == null)
                {
                    if (!await SwitchAsync(Config.Outlets, false, byUser: false)) running = null; // sync at startup
                }
                else if (running == false && _wasRunning == true)
                {
                    _stoppedAt = DateTime.Now;
                }

                if (running == false && _stoppedAt is DateTime t && (DateTime.Now - t).TotalSeconds >= Config.OffDelaySeconds)
                {
                    if (await SwitchAsync(Config.Outlets, false, byUser: false)) _stoppedAt = null;
                }
            }
            if (!waiting) _wasRunning = running;

            // re-read the plug every 15s while the window is open or a sim runs (the keep-on check), every minute otherwise
            _ticks++;
            int every = _form.Visible || _wasRunning == true ? 15 : 60;
            if (_ticks * Config.PollSeconds % every < Config.PollSeconds) await RefreshAsync();
            else UpdateUi();
        }
        finally { _tickBusy = false; }
    }

    // ---- UI

    void UpdateUi()
    {
        var mine = Outlets.Where(o => Config.Outlets.Contains(o.Port)).ToList();
        _tray.Icon = LastError != null ? _iconError : mine.Any(o => o.On) ? _iconOn : _iconOff;
        var tip = LastError != null
            ? "Command Center: power error"
            : "Command Center: power " + (mine.Count == 0 ? "?" : string.Join(", ", mine.Select(o => $"{o.Port} {(o.On ? "ON" : "off")}")));
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;

        foreach (var (port, item) in _outletItems)
        {
            item.Text = NameOf(port);
            item.Checked = Find(port)?.On ?? false;
        }
        _autoItem.Checked = Config.Auto;
        _oneDriveItem.Checked = Config.PauseOneDrive;
        _spanItem.Checked = Config.SpanWindows;
        _startupItem.Checked = Startup.IsEnabled;
        _form.UpdateView();
    }

    void ShowWindow()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _ = RefreshAsync();
    }

    void Fail(Exception ex)
    {
        // log only when the error changes, so an outage shows as one line with its start time
        if (ex.Message != LastError) Log.Write($"Error: {ex.Message}");
        LastError = ex.Message;
    }

    void Exit()
    {
        _poll.Stop();
        Helpers.StopAll(); // only what we started and that closes with iRacing; Crew Chief is left running
        OneDrive.Resume(); // never leave OneDrive off
        _showWait.Unregister(null);
        _tray.Visible = false;
        _form.AllowClose = true;
        _form.Close();
        Log.Write("Exited");
        ExitThread();
    }

    static Icon DotIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 14, 14);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
