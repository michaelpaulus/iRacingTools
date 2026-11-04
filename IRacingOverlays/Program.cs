using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// IRacingOverlays - overlays for iRacing. First one: standings with country, projected iRating/SR, SOF, laps and gaps.
//   IRacingOverlays                              run live alongside iRacing (windowed or borderless, not exclusive fullscreen)
//   IRacingOverlays demo [multiclass] [speed]    a simulated race, to try the overlay without iRacing
//   IRacingOverlays png <out.png> [multiclass]   render the demo standings to a PNG, for checking the layout
//   IRacingOverlays logos-download               fetch the car badges from Wikipedia (also done in the background on start)
//   IRacingOverlays logos <out.png>              every car badge on one image, for checking them

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var baseDir = Environment.CurrentDirectory;
var cfg = OverlaysConfig.Load(baseDir);
string command = args.Length > 0 ? args[0].ToLowerInvariant() : "live";
bool multiclass = args.Any(a => a.Equals("multiclass", StringComparison.OrdinalIgnoreCase));

if (command == "png" && args.Length >= 2)
{
    var demo = new DemoTelemetry(multiclass, speed: 0, startAtSeconds: 450);
    var demoSession = SessionData.Parse(demo.SessionInfo);
    var demoTracker = new RaceTracker();
    for (int i = 0; i < 160 * 30; i++) // 160 s of 30 Hz frames, so the interval timing has a lap of history
    {
        demo.Advance(1.0 / 30);
        demoTracker.Update(CarFrame.Read(demo), demoSession.PlayerCarIdx);
    }
    var snap = Standings.Build(demoSession, CarFrame.Read(demo), cfg.Standings, demoTracker);
    int w = cfg.Standings.Width;
    using var bmp = new Bitmap(w, StandingsPainter.Height(snap, w));
    using (var g = Graphics.FromImage(bmp)) new StandingsPainter().Paint(g, bmp.Size, snap);
    bmp.Save(args[1], ImageFormat.Png);
    Console.WriteLine($"Wrote {args[1]} ({bmp.Width}x{bmp.Height}).");
    return;
}

if (command == "logos-download")
{
    var missing = CarLogos.Missing();
    if (missing.Count == 0) { Console.WriteLine($"All car badges are already in {CarLogos.DownloadDir}."); return; }
    Console.WriteLine($"Downloading {missing.Count} car badges from Wikipedia to {CarLogos.DownloadDir}...");
    int got = await CarLogos.DownloadMissingAsync(Console.WriteLine);
    Console.WriteLine($"Saved {got} of {missing.Count}.");
    return;
}

if (command == "logos" && args.Length >= 2)
{
    // Every known badge on the overlay background, as the standings draw it, for checking new or changed ones.
    var keys = CarLogos.Keys.ToList();
    using var bmp = new Bitmap(8 * 100, (keys.Count + 7) / 8 * 44);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(Color.FromArgb(14, 16, 20));
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        using var font = new Font("Segoe UI", 9f, GraphicsUnit.Pixel);
        for (int i = 0; i < keys.Count; i++)
        {
            float x = i % 8 * 100 + 30, y = i / 8 * 44 + 6;
            if (CarLogos.Logo(keys[i]) is Image img)
            {
                float sc = Math.Min(38f / img.Width, 18f / img.Height);
                g.DrawImage(img, x + (38 - img.Width * sc) / 2, y, img.Width * sc, img.Height * sc);
            }
            g.DrawString(keys[i], font, Brushes.Gray, x - 20, y + 22);
        }
    }
    bmp.Save(args[1], ImageFormat.Png);
    Console.WriteLine($"Wrote {args[1]} ({keys.Count} badges).");
    return;
}

if (command == "dump")
{
    // Saves the live session info and prints what the standings see, for troubleshooting.
    using var live = new LiveTelemetry();
    var until = DateTime.UtcNow.AddSeconds(10);
    while (!live.WaitForFrame(500) && DateTime.UtcNow < until) { }
    if (!live.IsConnected) { Console.WriteLine("iRacing is not running."); return; }
    File.WriteAllText(Path.Combine(baseDir, "session-info.yaml"), live.SessionInfo);
    var sd = SessionData.Parse(live.SessionInfo);
    var fr = CarFrame.Read(live);
    sd.Sessions.TryGetValue(fr.SessionNum, out var cur);
    Console.WriteLine($"SessionNum {fr.SessionNum}: type '{cur?.Type}', name '{cur?.Name}', state {fr.SessionState}");
    Console.WriteLine($"Sessions: {string.Join(", ", sd.Sessions.Values.Select(x => $"{x.Num}={x.Type}"))}");
    var me = sd.Player;
    Console.WriteLine($"Player car {sd.PlayerCarIdx}: {me?.Name} {me?.LicenseLetter} {me?.SafetyRating:0.00}, laps {(me == null ? 0 : fr.LapCompleted[me.CarIdx])}, incidents {fr.PlayerIncidents}, turns/lap {SafetyRatingEstimate.CornersPerLap(sd)}");
    var snapNow = Standings.Build(sd, fr, cfg.Standings);
    Console.WriteLine($"Footer: {snapNow.PlayerSr ?? "(none)"} {snapNow.PlayerSrDelta}");
    foreach (var row in snapNow.Classes.SelectMany(c => c.Rows))
    {
        int i = row.Driver.CarIdx;
        Console.WriteLine($"  P{row.ClassPosition,-3} {row.Driver.Name,-24} laps {fr.LapCompleted[i],3} pct {fr.LapDistPct[i],6:0.000} est {fr.EstTime[i],7:0.0} best {fr.BestLap[i],8:0.000} last {fr.LastLap[i],8:0.000} surf {fr.TrackSurface[i],2}  gap '{row.Gap}'");
    }
    Console.WriteLine("Wrote session-info.yaml.");
    return;
}

double demoSpeed = args.Skip(1).Select(a => double.TryParse(a, out var s) ? s : 0).FirstOrDefault(s => s > 0);
ITelemetrySource src = command == "demo" ? new DemoTelemetry(multiclass, demoSpeed > 0 ? demoSpeed : 1) : new LiveTelemetry();
using var _ = src;

// Car badges aren't in git: fetch any missing ones once, in the background (a few hundred KB; text tags until then).
Task? logoDownload = CarLogos.Missing().Count == 0 ? null : Task.Run(async () =>
    {
        int got = await CarLogos.DownloadMissingAsync(Console.WriteLine);
        if (got > 0) Console.WriteLine($"Downloaded {got} car badges to {CarLogos.DownloadDir}.");
    });

var latest = new SnapshotHolder();
var overlay = OverlayHost.Start(latest, cfg, baseDir);
Console.WriteLine($"Standings overlay running. Scroll Lock / key {cfg.ToggleKey}: hide/show. Pause / key {cfg.LockKey}: lock (click-through) / unlock (drag). Ctrl+C to quit.");

int lastSessionInfo = -1;
SessionData? session = null;
bool waitingShown = false, toggleWasDown = false, lockWasDown = false;
var lastBuild = DateTime.MinValue;
var tracker = new RaceTracker();

while (true)
{
    if (src.WaitForFrame(200))
    {
        waitingShown = false;
        if (src.SessionInfoVersion != lastSessionInfo)
        {
            lastSessionInfo = src.SessionInfoVersion;
            try { session = SessionData.Parse(src.SessionInfo); }
            catch (Exception ex) { Console.WriteLine($"Could not read session info: {ex.Message}"); }
            if (session != null) Console.WriteLine($"Session: {session.TrackName}, {session.Drivers.Count(d => !d.IsSpectator && !d.IsPaceCar)} drivers.");
        }
        // The tracker sees every 60 Hz frame (interval timing needs it); 10 table updates a second is plenty.
        var frame = CarFrame.Read(src);
        latest.InCar = frame.InCar;
        tracker.Update(frame, session?.PlayerCarIdx ?? -1);
        if (session != null && DateTime.UtcNow - lastBuild > TimeSpan.FromMilliseconds(100))
        {
            lastBuild = DateTime.UtcNow;
            latest.Value = Standings.Build(session, frame, cfg.Standings, tracker);
        }
    }
    else if (!src.IsConnected)
    {
        latest.Value = StandingsSnapshot.Waiting;
        latest.InCar = false;
        if (!waitingShown) { Console.WriteLine("Waiting for iRacing..."); waitingShown = true; }
    }

    bool toggle = Hotkeys.IsDown(cfg.ToggleKey), lockKey = Hotkeys.IsDown(cfg.LockKey);
    if (toggle && !toggleWasDown) overlay.ToggleVisible();
    if (lockKey && !lockWasDown) overlay.ToggleLocked();
    toggleWasDown = toggle;
    lockWasDown = lockKey;
}

sealed class SnapshotHolder
{
    volatile StandingsSnapshot _value = StandingsSnapshot.Waiting;
    volatile bool _inCar;
    public StandingsSnapshot Value { get => _value; set => _value = value; }
    /// <summary>In the car (the overlay uses its in-car position) or not (its out-of-car position).</summary>
    public bool InCar { get => _inCar; set => _inCar = value; }
}

static class Hotkeys
{
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    public static bool IsDown(int vk) => vk > 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
}

/// <summary>Runs the overlay windows on their own UI thread.</summary>
sealed class OverlayHost
{
    OverlayWindow? _standings;
    readonly OverlaysConfig _cfg;
    readonly Action _save;

    OverlayHost(OverlaysConfig cfg, Action save) { _cfg = cfg; _save = save; }

    public static OverlayHost Start(SnapshotHolder latest, OverlaysConfig cfg, string baseDir)
    {
        void Save() { try { cfg.Save(baseDir); } catch (IOException) { /* overlays.json busy (e.g. OneDrive sync); keep position for this run */ } }
        var host = new OverlayHost(cfg, Save);
        var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            var sc = cfg.Standings;

            // Separate positions in and out of the car; out of the car falls back to the in-car one until it's moved.
            bool inCar = latest.InCar;
            Rectangle BoundsFor(bool car)
            {
                var pos = sc.X == int.MinValue ? new Point(area.Left + 40, area.Top + 40) : new Point(sc.X, sc.Y);
                int width = sc.Width;
                if (!car && sc.OutOfCarX != int.MinValue) pos = new Point(sc.OutOfCarX, sc.OutOfCarY);
                if (!car && sc.OutOfCarWidth != int.MinValue) width = sc.OutOfCarWidth;
                return new Rectangle(pos, new Size(width, 200));
            }
            void Moved(Rectangle b)
            {
                if (inCar) { sc.X = b.X; sc.Y = b.Y; sc.Width = b.Width; }
                else { sc.OutOfCarX = b.X; sc.OutOfCarY = b.Y; sc.OutOfCarWidth = b.Width; }
                Save();
            }

            var painter = new StandingsPainter();
            var win = new OverlayWindow("IRacingOverlays standings", BoundsFor(inCar), sc.Opacity, cfg.Locked, Moved, minWidth: 300)
            {
                DesiredHeight = width => StandingsPainter.Height(latest.Value, width),
            };
            win.Tick = () =>
            {
                if (latest.InCar == inCar) return;
                inCar = latest.InCar;
                var r = BoundsFor(inCar);
                win.Location = r.Location;
                win.Width = r.Width;
            };
            win.Painter = e => painter.Paint(e.Graphics, win.ClientSize, latest.Value);
            host._standings = win;
            win.Load += (_, _) => ready.Set();
            Application.Run(win);
        }) { IsBackground = true, Name = "overlay" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return host;
    }

    public void ToggleVisible() => _standings?.BeginInvoke(() => _standings.Visible = !_standings.Visible);

    public void ToggleLocked() => _standings?.BeginInvoke(() =>
    {
        _standings.Locked = !_standings.Locked;
        _cfg.Locked = _standings.Locked;
        _save();
        Console.WriteLine(_standings.Locked ? "Overlays locked (clicks pass through)." : "Overlays unlocked (drag to move, edges to resize).");
    });
}
