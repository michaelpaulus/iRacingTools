using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

static class OverlayColors
{
    public static readonly Color Bg = Color.FromArgb(18, 20, 24);
    public static readonly Color Lane = Color.FromArgb(40, 44, 52);
    public static readonly Color Tick = Color.FromArgb(70, 76, 86);
    public static readonly Color You = Color.FromArgb(64, 170, 255);
    public static readonly Color Ghost = Color.FromArgb(255, 160, 40);
    public static readonly Color Dim = Color.FromArgb(150, 156, 166);
}

/// <summary>
/// Vertical "relative" strip: your car fixed in the middle, the reference car above (ahead) or below (behind)
/// by track distance, ±150 m full scale.
/// </summary>
public sealed class StripPainter
{
    const double RangeMetres = 150;
    readonly string _name;

    public StripPainter(string referenceName) => _name = referenceName;

    public void Paint(Graphics g, Size size, GhostRelative? r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(OverlayColors.Bg);
        int w = size.Width;
        using var small = new Font("Segoe UI", 9f);
        using var medium = new Font("Segoe UI", 14f, FontStyle.Bold);
        using var dim = new SolidBrush(OverlayColors.Dim);

        void Centered(string text, Font f, Brush b, float y) => g.DrawString(text, f, b, (w - g.MeasureString(text, f).Width) / 2, y);

        Centered(_name, small, dim, 6);
        if (r == null)
        {
            Centered("from the line", small, dim, 28);
        }
        else
        {
            Centered(r.GapSeconds.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " s", medium, Brushes.White, 20);
            Centered($"{Math.Abs(r.AheadMetres):F0} m {(r.AheadMetres >= 0 ? "ahead" : "behind")}", small, dim, 46);
        }

        // Lane with a tick every 50 m.
        int top = 70, bottom = size.Height - 10, laneW = 44, laneX = (w - laneW) / 2;
        float mid = (top + bottom) / 2f, half = (bottom - top) / 2f - 20;
        using (var lane = new SolidBrush(OverlayColors.Lane)) g.FillRectangle(lane, laneX, top, laneW, bottom - top);
        using (var tick = new Pen(OverlayColors.Tick, 1))
            for (int m = -150; m <= 150; m += 50)
            {
                float y = mid - (float)(m / RangeMetres) * half;
                g.DrawLine(tick, laneX, y, laneX + laneW, y);
            }

        const float carW = 22, carH = 36;
        using var youBrush = new SolidBrush(OverlayColors.You);
        using var ghostBrush = new SolidBrush(OverlayColors.Ghost);
        using var ghostPen = new Pen(OverlayColors.Ghost, 2);
        using var outline = new Pen(Color.Black, 1.5f);

        g.FillRectangle(youBrush, (w - carW) / 2, mid - carH / 2, carW, carH);
        g.DrawRectangle(outline, (w - carW) / 2, mid - carH / 2, carW, carH);

        if (r == null) return;
        if (Math.Abs(r.AheadMetres) <= RangeMetres)
        {
            float y = mid - (float)(r.AheadMetres / RangeMetres) * half;
            var rect = new RectangleF((w - carW) / 2, y - carH / 2, carW, carH);
            // Ghost is drawn semi-transparent and outlined so it stays visible when overlapping your car.
            using var ghostFill = new SolidBrush(Color.FromArgb(150, OverlayColors.Ghost));
            g.FillRectangle(ghostFill, rect);
            g.DrawRectangle(ghostPen, rect.X, rect.Y, rect.Width, rect.Height);
        }
        else
        {
            // Off the scale: an arrow at the end of the lane.
            bool up = r.AheadMetres > 0;
            float tipY = up ? top + 4 : bottom - 4, baseY = up ? top + 18 : bottom - 18;
            g.FillPolygon(ghostBrush, new[] { new PointF(w / 2f, tipY), new PointF(w / 2f - 10, baseY), new PointF(w / 2f + 10, baseY) });
        }
    }
}

/// <summary>
/// Draws a box roughly where the reference car would appear through your windscreen.
/// Approximate: both cars are assumed to be on the reference line, on flat ground, seen from a fixed eye height.
/// </summary>
public sealed class MarkerPainter
{
    readonly string _name;
    readonly CoachConfig _cfg;
    readonly double _tanHalfFov;

    public MarkerPainter(string referenceName, double horizontalFovDeg, CoachConfig cfg)
    {
        _name = referenceName;
        _cfg = cfg;
        _tanHalfFov = Math.Tan(horizontalFovDeg * Math.PI / 360);
    }

    public sealed record Layout(RectangleF? Car, PointF? EdgeArrow, bool ArrowRight, string Label, PointF LabelAt, Rectangle Area);

    public Layout? Compute(GhostRelative? r, Size size)
    {
        // Within ~8 m the box would cover the screen; the relative strip shows that case instead.
        if (r == null || r.AheadMetres <= 0 || r.AheadMetres > _cfg.MarkerMaxDistanceM || r.Forward < 8) return null;

        double f = size.Width / 2.0 / _tanHalfFov; // focal length in pixels
        double cx = size.Width / 2.0, horizon = size.Height / 2.0 + _cfg.MarkerHorizonOffsetPx;
        double x = cx + f * r.Right / r.Forward;
        double bottomY = horizon + f * _cfg.MarkerEyeHeightM / r.Forward;
        float maxW = size.Width * 0.15f;
        float carW = (float)Math.Clamp(f * 2.0 / r.Forward, 10, maxW), carH = (float)Math.Clamp(f * 1.2 / r.Forward, 6, maxW * 0.6f);
        string label = $"{_name}  {r.AheadMetres:F0} m";

        if (x + carW / 2 < 0 || x - carW / 2 > size.Width)
        {
            bool right = x > size.Width;
            var tip = new PointF(right ? size.Width - 6 : 6, (float)horizon);
            var labelAt = new PointF(right ? size.Width - 130 : 30, (float)horizon - 34);
            var area = Rectangle.Round(RectangleF.Union(new RectangleF(tip.X - 30, tip.Y - 40, 60, 80), new RectangleF(labelAt.X - 4, labelAt.Y - 4, 140, 30)));
            return new Layout(null, tip, right, label, labelAt, area);
        }

        var car = new RectangleF((float)x - carW / 2, (float)bottomY - carH, carW, carH);
        var at = new PointF(car.X, car.Y - 24);
        var all = RectangleF.Union(car, new RectangleF(at.X - 4, at.Y - 4, Math.Max(carW, 140) + 8, 28));
        all.Inflate(4, 4);
        return new Layout(car, null, false, label, at, Rectangle.Round(all));
    }

    public void Paint(Graphics g, Layout l)
    {
        // No anti-aliasing: the window's transparent colour key would leave coloured fringes.
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        using var ghost = new SolidBrush(OverlayColors.Ghost);
        using var outline = new Pen(Color.Black, 2);
        using var font = new Font("Segoe UI", 12, FontStyle.Bold);

        if (l.Car is RectangleF car)
        {
            g.FillRectangle(ghost, car);
            g.DrawRectangle(outline, car.X, car.Y, car.Width, car.Height);
        }
        else if (l.EdgeArrow is PointF tip)
        {
            float dir = l.ArrowRight ? -1 : 1;
            g.FillPolygon(ghost, new[] { tip, new PointF(tip.X + dir * 22, tip.Y - 14), new PointF(tip.X + dir * 22, tip.Y + 14) });
        }
        g.DrawString(l.Label, font, Brushes.Black, l.LabelAt.X + 1, l.LabelAt.Y + 1);
        g.DrawString(l.Label, font, Brushes.White, l.LabelAt);
    }
}

/// <summary>
/// Borderless, always-on-top window that never takes focus from iRacing.
/// Unless click-through: drag the middle to move it, drag an edge or corner to resize it.
/// </summary>
public sealed class OverlayWindow : Form
{
    readonly bool _clickThrough, _adjustable;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };

    public Action<PaintEventArgs>? Painter;
    /// <summary>For large windows: the area that needs repainting this frame (null = whole window).</summary>
    public Func<Rectangle?>? DirtyArea;
    Rectangle _lastDirty = Rectangle.Empty;

    public OverlayWindow(string title, Rectangle bounds, double opacity, bool clickThrough, Color? transparentKey,
        Action<Rectangle>? changed, Size? minimumSize = null)
    {
        _clickThrough = clickThrough;
        _adjustable = !clickThrough && changed != null;
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Opacity = Math.Clamp(opacity, 0.2, 1.0);
        if (minimumSize is Size min) MinimumSize = min;
        Bounds = bounds;
        if (transparentKey is Color key) { BackColor = key; TransparencyKey = key; }
        if (_adjustable) ResizeEnd += (_, _) => changed!(Bounds); // fires after a move or a resize

        _timer.Tick += (_, _) =>
        {
            if (DirtyArea == null) { Invalidate(); return; }
            var now = DirtyArea() ?? Rectangle.Empty;
            var both = Rectangle.Union(_lastDirty, now);
            if (!both.IsEmpty) Invalidate(both);
            _lastDirty = now;
        };
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Tells Windows which part of the window the mouse is over: edges resize, the rest moves it.</summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        base.WndProc(ref m);
        if (m.Msg != WM_NCHITTEST || !_adjustable) return;

        long lp = m.LParam.ToInt64();
        var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
        const int grip = 8;
        bool l = p.X < grip, r = p.X >= ClientSize.Width - grip, t = p.Y < grip, b = p.Y >= ClientSize.Height - grip;
        int hit = t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : 2; // 2 = caption (move)
        m.Result = (IntPtr)hit;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000,
                WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (_clickThrough) cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT; // mouse clicks pass through to iRacing
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e) => Painter?.Invoke(e);
}

public static class Overlays
{
    /// <summary>Starts the main overlay, relative strip and windscreen marker on one UI thread; returns a show/hide toggle.</summary>
    public static Action Start(GhostTracker tracker, ReferenceLap reference, CoachConfig cfg, string baseDir, double markerFovDeg)
    {
        var windows = new List<OverlayWindow>();
        var ready = new ManualResetEventSlim();
        void Save() { try { cfg.Save(baseDir); } catch (IOException) { /* coach.json busy (e.g. OneDrive sync); keep position for this run */ } }

        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            var primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
            var area = primary.WorkingArea;

            var mainPos = cfg.OverlayX == int.MinValue ? new Point(area.Left + 40, area.Top + 40) : new Point(cfg.OverlayX, cfg.OverlayY);
            var main = new OverlayWindow("IRacingCoach overlay", new Rectangle(mainPos, new Size(cfg.OverlayWidth, cfg.OverlayHeight)),
                cfg.OverlayOpacity, false, null,
                b => { cfg.OverlayX = b.X; cfg.OverlayY = b.Y; cfg.OverlayWidth = b.Width; cfg.OverlayHeight = b.Height; Save(); },
                new Size(220, 200));
            var painter = new OverlayPainter(reference);
            main.Painter = e => painter.Paint(e.Graphics, main.ClientSize, tracker.State);
            windows.Add(main);

            if (cfg.ShowPedals)
            {
                var pedalPos = cfg.PedalX == int.MinValue ? new Point(mainPos.X, mainPos.Y + cfg.OverlayHeight + 8) : new Point(cfg.PedalX, cfg.PedalY);
                var pedals = new OverlayWindow("IRacingCoach pedals", new Rectangle(pedalPos, new Size(cfg.PedalWidth, cfg.PedalHeight)),
                    cfg.OverlayOpacity, false, null,
                    b => { cfg.PedalX = b.X; cfg.PedalY = b.Y; cfg.PedalWidth = b.Width; cfg.PedalHeight = b.Height; Save(); },
                    new Size(240, 100));
                pedals.Painter = e => painter.PaintPedalWindow(e.Graphics, pedals.ClientSize, tracker.Pedals);
                windows.Add(pedals);
            }

            if (cfg.ShowStrip)
            {
                var stripPos = cfg.StripX == int.MinValue ? new Point(mainPos.X + cfg.OverlayWidth + 8, mainPos.Y) : new Point(cfg.StripX, cfg.StripY);
                var strip = new OverlayWindow("IRacingCoach relative", new Rectangle(stripPos, new Size(cfg.StripWidth, cfg.StripHeight)),
                    cfg.OverlayOpacity, false, null,
                    b => { cfg.StripX = b.X; cfg.StripY = b.Y; cfg.StripWidth = b.Width; cfg.StripHeight = b.Height; Save(); },
                    new Size(80, 200));
                var sp = new StripPainter(reference.Name);
                strip.Painter = e => sp.Paint(e.Graphics, strip.ClientSize, tracker.Relative);
                windows.Add(strip);
            }

            if (cfg.ShowWindscreenMarker)
            {
                var screens = Screen.AllScreens;
                var target = cfg.MarkerScreen >= 0 && cfg.MarkerScreen < screens.Length ? screens[cfg.MarkerScreen] : primary;
                var marker = new OverlayWindow("IRacingCoach marker", target.Bounds, cfg.MarkerOpacity, true, Color.Magenta, null);
                var mp = new MarkerPainter(reference.Name, markerFovDeg, cfg);
                MarkerPainter.Layout? current = null;
                marker.DirtyArea = () => (current = mp.Compute(tracker.Relative, marker.ClientSize))?.Area;
                marker.Painter = e => { if (current != null) mp.Paint(e.Graphics, current); };
                windows.Add(marker);
            }

            foreach (var w in windows.Skip(1)) w.Show();
            main.Load += (_, _) => ready.Set();
            Application.Run(main);
        }) { IsBackground = true, Name = "overlay" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));

        return () =>
        {
            var main = windows.FirstOrDefault();
            main?.BeginInvoke(() => { bool show = !main.Visible; foreach (var w in windows) w.Visible = show; });
        };
    }

    /// <summary>
    /// Horizontal field of view of the centre screen from iRacing's monitor geometry
    /// (Documents\iRacing\rendererDX11Monitor.ini: ScreenWidth and ViewingDist in mm).
    /// </summary>
    public static double FovFromIRacing(double fallbackDeg)
    {
        var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "rendererDX11Monitor.ini");
        if (!File.Exists(ini)) return fallbackDeg;
        double Read(string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(ini), $@"^{key}=([\d.]+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
        }
        double width = Read("ScreenWidth"), dist = Read("ViewingDist");
        if (double.IsNaN(width) || double.IsNaN(dist) || dist <= 0) return fallbackDeg;
        return 2 * Math.Atan(width / 2 / dist) * 180 / Math.PI;
    }
}
