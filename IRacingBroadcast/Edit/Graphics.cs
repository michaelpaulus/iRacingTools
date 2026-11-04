using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
/// Draws the broadcast graphics, one transparent frame at a time, from the shoot's timeline: the standings tower, a name
/// tag on every cut, event banners, the intro card, a wipe between clips and the results. Laid out for 1920×1080 and
/// scaled to the output.
/// </summary>
public sealed class BroadcastGraphics : IDisposable
{
    const float BaseW = 1920, BaseH = 1080;

    static readonly Color Panel = Color.FromArgb(225, 14, 16, 22);
    static readonly Color PanelLight = Color.FromArgb(235, 28, 31, 40);
    static readonly Color Accent = Color.FromArgb(232, 36, 64);
    static readonly Color Text = Color.FromArgb(244, 246, 250);
    static readonly Color Dim = Color.FromArgb(150, 158, 172);
    static readonly Color Gold = Color.FromArgb(255, 196, 46);
    static readonly Color PitColor = Color.FromArgb(255, 190, 60);

    readonly RaceInfo _info;
    readonly Story _story;
    readonly List<Frame> _frames;
    readonly BroadcastConfig _cfg;
    readonly Func<double, double> _toVideo;
    readonly FieldTracker _tracker;
    readonly Bitmap _bmp;
    readonly Graphics _g;
    readonly byte[] _buffer;
    readonly List<(double At, RaceEvent E)> _banners;
    readonly double _resultsAt;
    readonly double[] _cuts;
    readonly string _family, _condensed;
    readonly Dictionary<(string, float, FontStyle), Font> _fonts = new();
    int _fi, _camCar = -1;
    double _camSince = double.NegativeInfinity;

    public int Width { get; }
    public int Height { get; }

    /// <param name="toVideo">SessionTime → seconds into the video (for moments inside the clips).</param>
    public BroadcastGraphics(RaceInfo info, Story story, List<Clip> clips, List<Frame> timeline, BroadcastConfig cfg, Func<double, double> toVideo)
    {
        _info = info; _story = story; _frames = timeline; _cfg = cfg; _toVideo = toVideo;
        _tracker = new FieldTracker(info);
        Width = cfg.Width; Height = cfg.Height;
        _bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        _g = Graphics.FromImage(_bmp);
        _buffer = new byte[Width * Height * 4];
        using var fonts = new InstalledFontCollection();
        bool has(string n) => fonts.Families.Any(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        _family = has("Bahnschrift") ? "Bahnschrift" : "Segoe UI";
        _condensed = has("Bahnschrift SemiBold SemiConden") ? "Bahnschrift SemiBold SemiConden" : _family;

        var bannerKinds = new[] { Kinds.Overtake, Kinds.Incident, Kinds.Pit, Kinds.FastestLap, Kinds.LeadChange, Kinds.LastLap, Kinds.Retire, Kinds.Battle };
        // Banners only for what's on screen: moments inside a clip, a few seconds clear of its end.
        _banners = story.Events
            .Where(e => bannerKinds.Contains(e.Kind) && (e.Importance >= 3 || e.Kind is Kinds.LastLap or Kinds.FastestLap || e.Cars.Contains(story.FeaturedCar)))
            .Where(e => e.Kind != Kinds.Pit || e.Position <= 3 || e.Cars.Contains(story.FeaturedCar))
            .Where(e => clips.Any(c => e.T >= c.From && e.T <= c.To - 2))
            .Select(e => (toVideo(e.T), e)).OrderBy(x => x.Item1).ToList();
        var offsets = Director.Offsets(clips);
        _cuts = offsets.Skip(1).ToArray();
        // The results table over the last clip's closing seconds.
        _resultsAt = clips.Sum(c => c.Length) - 10;
    }

    /// <summary>The graphics at <paramref name="v"/> seconds into the capture, as BGRA bytes (straight alpha).</summary>
    public byte[] Render(double v)
    {
        while (_fi < _frames.Count && _frames[_fi].V <= v) _tracker.Update(_frames[_fi++]);
        var f = _tracker.Current;

        _g.ResetTransform();
        _g.Clear(Color.Transparent);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _g.CompositingQuality = CompositingQuality.AssumeLinear; // gamma-corrected blending washes out translucent panels
        _g.ScaleTransform(Width / BaseW, Height / BaseH);

        if (f != null)
        {
            if (f.CamCar != _camCar) { _camCar = f.CamCar; _camSince = v; }
            bool results = v >= _resultsAt;
            Bug();
            if (!results) Tower(f);
            if (v < IntroSeconds) Intro(v);
            else if (!results && v - _camSince < 6.5 && _camCar >= 0) NameTag(_camCar, f, v - _camSince);
            if (!results) Banner(v);
            if (results) Results(v - _resultsAt);
        }
        // The wipe hides each cut from one moment of the race to the next.
        foreach (var cut in _cuts)
            if (Math.Abs(v - cut) < WipeHalf) Wipe(v - cut);

        var data = _bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < Height; y++)
            Marshal.Copy(data.Scan0 + y * data.Stride, _buffer, y * Width * 4, Width * 4);
        _bmp.UnlockBits(data);
        return _buffer;
    }

    // ---- elements ----

    void Bug()
    {
        var font = F(_condensed, 20, FontStyle.Regular);
        var size = _g.MeasureString(_cfg.ShowName.ToUpperInvariant(), font);
        float w = size.Width + 40, x = BaseW - w - 40, y = 40;
        Box(Panel, x, y, w, 40, 6);
        Box(Accent, x, y, 6, 40, 0);
        Str(_cfg.ShowName.ToUpperInvariant(), font, Text, x + 18, y, w - 24, 40);
    }

    void Tower(Frame f)
    {
        float x = 40, y = 40, w = 330, rowH = 31;
        var order = _tracker.Order();
        var leader = order.Count > 0 ? f.Car(order[0]) : null;
        int lap = leader is { } l ? Math.Max(1, l.LapCompleted + 1) : 0;
        string header = f.State >= Frame.StateCheckered ? "FINISH"
            : f.T < _story.GreenT ? "GRID"
            : _info.RaceLaps is int total ? $"LAP {Math.Min(lap, total)} / {total}"
            : $"LAP {lap}  ·  {Remaining(f)}";

        Box(Accent, x, y, w, 40, 6, top: true);
        Str(header, F(_condensed, 21, FontStyle.Regular), Color.White, x + 14, y, w - 28, 40);
        if (f.State >= Frame.StateCheckered) Checkers(x + w - 58, y + 8, 44, 24);
        y += 40;

        // Top ten, then the car on camera and the featured driver if they're further back.
        var rows = order.Take(10).ToList();
        var extra = new[] { _camCar, _story.FeaturedCar }.Where(c => c >= 0 && order.Contains(c) && !rows.Contains(c)).Distinct().OrderBy(order.IndexOf).ToList();
        int n = rows.Count + extra.Count;
        Box(Panel, x, y, w, n * rowH + (extra.Count > 0 ? 8 : 0) + 6, 6, bottom: true);
        y += 3;
        foreach (var (car, i) in rows.Select((c, i) => (c, i)).Concat(extra.Select(c => (c, -1))))
        {
            if (i == -1 && car == extra[0])
            {
                using var pen = new Pen(Color.FromArgb(90, Dim), 1) { DashStyle = DashStyle.Dash };
                _g.DrawLine(pen, x + 10, y + 4, x + w - 10, y + 4);
                y += 8;
            }
            TowerRow(car, order, f, x, y, w, rowH);
            y += rowH;
        }
    }

    void TowerRow(int car, List<int> order, Frame f, float x, float y, float w, float h)
    {
        var d = _info.Car(car);
        if (d == null) return;
        var state = f.Car(car);
        bool onCam = car == _camCar, featured = car == _story.FeaturedCar;
        int pos = order.IndexOf(car) + 1;
        if (onCam) Box(Color.FromArgb(240, 244, 246, 250), x + 4, y + 1, w - 8, h - 2, 4);
        var fg = onCam ? Color.FromArgb(14, 16, 22) : Text;

        Str(pos.ToString(CultureInfo.InvariantCulture), F(_condensed, 19, FontStyle.Regular), onCam ? fg : Text, x + 6, y, 30, h, StringAlignment.Far);
        var cls = Rgb(d.ClassColor);
        Box(cls, x + 44, y + 6, 4, h - 12, 0);
        Box(onCam ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(60, 255, 255, 255), x + 52, y + 5, 40, h - 10, 4);
        Str(d.Number, F(_condensed, 15, FontStyle.Regular), fg, x + 52, y, 40, h, StringAlignment.Center);
        if (featured) Box(Accent, x + w - 10, y + 5, 4, h - 10, 0);
        Str(d.Surname.ToUpperInvariant(), F(_condensed, 18, FontStyle.Regular), fg, x + 100, y, 150, h);

        string gap; Color gc = onCam ? fg : Dim;
        if (state is { } s && s.OnPitRoad) { gap = "PIT"; gc = PitColor; }
        else if (state is { } o && !o.InWorld && f.State == Frame.StateRacing) { gap = "OUT"; gc = Color.FromArgb(242, 92, 84); }
        else if (pos == 1) gap = f.State >= Frame.StateCheckered ? "WIN" : "LEADER";
        else
        {
            int ahead = order[pos - 2];
            double laps = _tracker.Distance(ahead) - _tracker.Distance(car);
            double g = _tracker.Gap(car, ahead);
            gap = laps >= 1 ? $"+{Math.Floor(laps):0}L" : double.IsFinite(g) ? $"+{g:0.0}" : "";
            if (double.IsFinite(g) && g < 1.0 && !onCam) gc = Text;
        }
        if (onCam && gap == "PIT") gc = Color.FromArgb(200, 120, 0);
        Str(gap, F(_condensed, 16, FontStyle.Regular), gc, x + w - 90, y, 72, h, StringAlignment.Far);
    }

    void NameTag(int car, Frame f, double age)
    {
        var d = _info.Car(car);
        if (d == null) return;
        float slide = (float)Math.Clamp(age / 0.35, 0, 1), fade = (float)Math.Clamp((6.5 - age) / 0.4, 0, 1);
        float ease = 1 - (1 - slide) * (1 - slide) * (1 - slide);
        float x = 60 - (1 - ease) * 120, y = BaseH - 190, w = 760, h = 112;
        int pos = _tracker.Rank(car);

        Box(Fade(Panel, fade), x, y, w, h, 8);
        Box(Fade(Accent, fade), x, y, 112, h, 8, left: true);
        Str(pos > 0 ? $"P{pos}" : "", F(_condensed, 46, FontStyle.Regular), Fade(Color.White, fade), x, y, 112, h, StringAlignment.Center);
        var cls = Rgb(d.ClassColor);
        Box(Fade(cls, fade), x + 130, y + 18, 58, 34, 5);
        Str(d.Number, F(_condensed, 22, FontStyle.Regular), Fade(On(cls), fade), x + 130, y + 18, 58, 34, StringAlignment.Center);
        Str(d.Name.ToUpperInvariant(), F(_condensed, 34, FontStyle.Regular), Fade(Text, fade), x + 202, y + 12, w - 220, 46);
        string lic = d.License.Length > 0 ? d.License : "";
        string sub = string.Join("   ·   ", new[] { d.Car, d.IRating > 0 ? $"iR {RaceInfo.Rating(d.IRating)}" : "", lic, d.Country }.Where(s => s.Length > 0 && s != "None"));
        Str(sub, F(_family, 19, FontStyle.Regular), Fade(Dim, fade), x + 132, y + 62, w - 150, 36);

        // Who's around them.
        var order = _tracker.Order(d.ClassId);
        int k = order.IndexOf(car);
        var bits = new List<string>();
        if (k > 0 && _tracker.Gap(car, order[k - 1]) is var ga && double.IsFinite(ga) && _tracker.Distance(order[k - 1]) - _tracker.Distance(car) < 1) bits.Add($"AHEAD {ga:0.0}s");
        if (k >= 0 && k + 1 < order.Count && _tracker.Gap(order[k + 1], car) is var gb && double.IsFinite(gb) && _tracker.Distance(car) - _tracker.Distance(order[k + 1]) < 1) bits.Add($"BEHIND {gb:0.0}s");
        var cam = _info.Cameras.FirstOrDefault(c => c.Num == f.CamGroup)?.Name ?? "";
        if (cam is "Cockpit" or "Roll Bar" or "Gyro" or "Nose" or "Gearbox" or "LF Susp" or "RF Susp" or "LR Susp" or "RR Susp") bits.Insert(0, "ONBOARD");
        if (bits.Count > 0)
        {
            var text = string.Join("   ", bits);
            var font = F(_condensed, 18, FontStyle.Regular);
            float tw = _g.MeasureString(text, font).Width + 28;
            Box(Fade(PanelLight, fade), x + w - tw, y - 36, tw, 32, 6);
            Str(text, font, Fade(Text, fade), x + w - tw, y - 36, tw, 32, StringAlignment.Center);
        }
    }

    void Banner(double v)
    {
        var active = _banners.Where(b => v >= b.At && v < b.At + 4.5).OrderByDescending(b => b.E.Importance).Select(b => ((double At, RaceEvent E)?)b).FirstOrDefault();
        if (active is not { } a) return;
        double age = v - a.At;
        float fade = (float)Math.Clamp(Math.Min(age / 0.25, (4.5 - age) / 0.4), 0, 1);
        var (label, color) = a.E.Kind switch
        {
            Kinds.Overtake => ("OVERTAKE", Color.FromArgb(46, 184, 92)),
            Kinds.Incident => ("INCIDENT", Color.FromArgb(226, 56, 48)),
            Kinds.Pit => ("PIT STOP", PitColor),
            Kinds.FastestLap => ("FASTEST LAP", Color.FromArgb(160, 90, 255)),
            Kinds.LeadChange => ("NEW LEADER", Gold),
            Kinds.LastLap => ("FINAL LAP", Color.White),
            Kinds.Retire => ("OUT", Color.FromArgb(120, 126, 140)),
            Kinds.Battle => ("BATTLE", Color.FromArgb(60, 140, 255)),
            _ => (a.E.Kind.ToUpperInvariant(), Accent),
        };
        string text = BannerText(a.E);
        var font = F(_condensed, 24, FontStyle.Regular);
        var lf = F(_condensed, 22, FontStyle.Regular);
        float lw = _g.MeasureString(label, lf).Width + 34;
        float tw = Math.Min(900, _g.MeasureString(text, font).Width + 40);
        float w = lw + tw, x = (BaseW - w) / 2, y = 40 - (1 - fade) * 20;
        Box(Fade(Panel, fade), x, y, w, 48, 6);
        Box(Fade(color, fade), x, y, lw, 48, 6, left: true);
        Str(label, lf, Fade(On(color), fade), x, y, lw, 48, StringAlignment.Center);
        Str(text, font, Fade(Text, fade), x + lw + 18, y, tw - 24, 48);
    }

    string BannerText(RaceEvent e)
    {
        string S(int i) => (_info.Car(i)?.Surname ?? "").ToUpperInvariant();
        return e.Kind switch
        {
            Kinds.Overtake => $"{S(e.Cars[0])} PASSES {S(e.Cars[1])}  ·  P{e.Position}",
            Kinds.Battle => $"{S(e.Cars[1])} CHASING {S(e.Cars[0])}  ·  P{e.Position}",
            Kinds.Incident => string.Join(", ", e.Cars.Select(S)),
            Kinds.Pit => $"{S(e.Cars[0])}  ·  FROM P{e.Position}",
            Kinds.FastestLap => $"{S(e.Cars[0])}  ·  {e.Detail.Split(": ").LastOrDefault()}",
            Kinds.LeadChange => S(e.Cars[0]),
            Kinds.LastLap => $"{S(e.Cars[0])} LEADS",
            Kinds.Retire => S(e.Cars[0]),
            _ => e.Detail.ToUpperInvariant(),
        };
    }

    const double IntroSeconds = 8, WipeHalf = 0.3;

    /// <summary>
    /// A slanted band in the show's colour sweeps across the screen, covering it fully at the cut (<paramref name="dv"/> = 0),
    /// with the show's name on it.
    /// </summary>
    void Wipe(double dv)
    {
        float p = (float)((dv + WipeHalf) / (2 * WipeHalf)); // 0 → 1 across the wipe
        const float band = 2700, slant = 360;
        float x0 = -band - slant + p * (BaseW + band + 2 * slant);
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { new PointF(x0 + slant, 0), new PointF(x0 + band + slant, 0), new PointF(x0 + band, BaseH), new PointF(x0, BaseH) });
        using (var b = new SolidBrush(Accent)) _g.FillPath(b, path);
        using (var edge = new Pen(Color.White, 14)) _g.DrawLine(edge, x0 + band + slant, 0, x0 + band, BaseH);
        float fade = (float)Math.Clamp(1 - Math.Abs(dv) / (WipeHalf * 0.6), 0, 1);
        Str(_cfg.ShowName.ToUpperInvariant(), F(_condensed, 64, FontStyle.Regular), Fade(Color.White, fade), 0, 0, BaseW, BaseH, StringAlignment.Center);
    }

    void Intro(double age)
    {
        float fade = (float)Math.Clamp(Math.Min((age - 0.5) / 0.5, (IntroSeconds - age) / 0.6), 0, 1);
        if (fade <= 0) return;
        float w = 1100, h = 190, x = (BaseW - w) / 2, y = BaseH - h - 90;
        Box(Fade(Panel, fade), x, y, w, h, 10);
        Box(Fade(Accent, fade), x, y, w, 8, 10, top: true);
        Str(_cfg.ShowName.ToUpperInvariant(), F(_condensed, 22, FontStyle.Regular), Fade(Accent, fade), x + 40, y + 20, w - 80, 34);
        Str(_info.Track.ToUpperInvariant(), F(_condensed, 50, FontStyle.Regular), Fade(Text, fade), x + 40, y + 52, w - 80, 66);
        var facts = new List<string> { _info.CarsSummary, $"{_info.Drivers.Count} DRIVERS", $"SOF {RaceInfo.Rating(_info.Sof)}" };
        facts.Add(_info.RaceLaps is int laps ? $"{laps} LAPS" : $"{_info.RaceLengthSeconds / 60:0} MINUTES");
        Str(string.Join("   ·   ", facts), F(_family, 22, FontStyle.Regular), Fade(Dim, fade), x + 40, y + 124, w - 80, 40);
    }

    void Results(double age)
    {
        float fade = (float)Math.Clamp(age / 0.6, 0, 1);
        var results = _info.Results;
        if (results.Count == 0) return;
        var winner = results[0];
        var rows = results.Take(10).ToList();
        if (results.FirstOrDefault(r => r.CarIdx == _story.FeaturedCar) is { } mine && !rows.Contains(mine)) rows.Add(mine);
        float rowH = 44, w = 1000, h = 110 + rows.Count * rowH + 20, x = (BaseW - w) / 2, y = (BaseH - h) / 2;
        Box(Fade(Color.FromArgb(235, 12, 14, 19), fade), x, y, w, h, 10);
        Box(Fade(Accent, fade), x, y, w, 70, 10, top: true);
        Str("RACE RESULT", F(_condensed, 34, FontStyle.Regular), Fade(Color.White, fade), x + 32, y, 500, 70);
        Str(_info.Track.ToUpperInvariant(), F(_condensed, 22, FontStyle.Regular), Fade(Color.White, fade), x + w - 532, y, 500, 70, StringAlignment.Far);
        y += 84;
        var cols = F(_family, 15, FontStyle.Regular);
        Str("POS", cols, Fade(Dim, fade), x + 24, y, 60, 22);
        Str("DRIVER", cols, Fade(Dim, fade), x + 170, y, 300, 22);
        Str("INC", cols, Fade(Dim, fade), x + w - 330, y, 60, 22, StringAlignment.Far);
        Str("BEST", cols, Fade(Dim, fade), x + w - 240, y, 100, 22, StringAlignment.Far);
        Str("GAP", cols, Fade(Dim, fade), x + w - 130, y, 100, 22, StringAlignment.Far);
        y += 26;
        double best = results.Where(r => r.FastestTime > 0).Select(r => r.FastestTime).DefaultIfEmpty(0).Min();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var d = _info.Car(r.CarIdx);
            if (d == null) continue;
            // Rows appear one by one.
            float rf = (float)Math.Clamp((age - 0.3 - i * 0.12) / 0.3, 0, 1) * fade;
            bool me = r.CarIdx == _story.FeaturedCar;
            if (me) Box(Fade(Color.FromArgb(70, Accent), rf), x + 12, y, w - 24, rowH - 4, 5);
            else if (i % 2 == 1) Box(Fade(Color.FromArgb(18, 255, 255, 255), rf), x + 12, y, w - 24, rowH - 4, 5);
            var big = F(_condensed, 24, FontStyle.Regular);
            Str(r.Position.ToString(CultureInfo.InvariantCulture), big, Fade(r.Position <= 3 ? Gold : Text, rf), x + 24, y, 50, rowH - 4);
            var cls = Rgb(d.ClassColor);
            Box(Fade(cls, rf), x + 90, y + 7, 56, rowH - 18, 5);
            Str(d.Number, F(_condensed, 18, FontStyle.Regular), Fade(On(cls), rf), x + 90, y + 7, 56, rowH - 18, StringAlignment.Center);
            Str(d.Name.ToUpperInvariant(), big, Fade(Text, rf), x + 170, y, w - 520, rowH - 4);
            var small = F(_condensed, 20, FontStyle.Regular);
            Str($"{r.Incidents}x", small, Fade(r.Incidents > 0 ? Dim : Text, rf), x + w - 330, y, 60, rowH - 4, StringAlignment.Far);
            Str(Detector.LapTime(r.FastestTime), small, Fade(r.FastestTime > 0 && Math.Abs(r.FastestTime - best) < 0.0005 ? Color.FromArgb(180, 120, 255) : Dim, rf),
                x + w - 240, y, 100, rowH - 4, StringAlignment.Far);
            string gap = r.Position == 1 ? "WINNER"
                : r.Laps < winner.Laps ? $"+{winner.Laps - r.Laps} LAP{(winner.Laps - r.Laps == 1 ? "" : "S")}"
                : r.Time > 0 && winner.Time > 0 ? $"+{r.Time - winner.Time:0.000}" : r.ReasonOut;
            Str(gap, small, Fade(Text, rf), x + w - 160, y, 130, rowH - 4, StringAlignment.Far);
            y += rowH;
        }
    }

    string Remaining(Frame f)
    {
        double left = _info.RaceLengthSeconds - (f.T - _story.GreenT);
        return left <= 0 ? "FINAL LAP" : TimeSpan.FromSeconds(left).ToString(left >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    // ---- drawing helpers ----

    Font F(string family, float px, FontStyle style)
    {
        if (!_fonts.TryGetValue((family, px, style), out var f))
            _fonts[(family, px, style)] = f = new Font(family, px, family == "Bahnschrift" ? style | FontStyle.Bold : style, GraphicsUnit.Pixel);
        return f;
    }

    static readonly StringFormat Near = Format(StringAlignment.Near), Far = Format(StringAlignment.Far), Mid = Format(StringAlignment.Center);
    static StringFormat Format(StringAlignment a) => new(StringFormat.GenericTypographic)
    {
        Alignment = a, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
    };

    void Str(string s, Font f, Color c, float x, float y, float w, float h, StringAlignment a = StringAlignment.Near)
    {
        if (string.IsNullOrEmpty(s) || w <= 0 || c.A == 0) return;
        using var b = new SolidBrush(c);
        _g.DrawString(s, f, b, new RectangleF(x, y, w, h), a == StringAlignment.Far ? Far : a == StringAlignment.Center ? Mid : Near);
    }

    void Box(Color c, float x, float y, float w, float h, float r, bool top = false, bool bottom = false, bool left = false)
    {
        if (c.A == 0 || w <= 0 || h <= 0) return;
        using var b = new SolidBrush(c);
        if (r <= 0) { _g.FillRectangle(b, x, y, w, h); return; }
        // Rounded on the given side only (or all round).
        bool all = !top && !bottom && !left;
        bool tl = all || top || left, tr = all || top, br = all || bottom, bl = all || bottom || left;
        using var p = new GraphicsPath();
        float d = r * 2;
        if (tl) p.AddArc(x, y, d, d, 180, 90); else p.AddLine(x, y, x, y);
        if (tr) p.AddArc(x + w - d, y, d, d, 270, 90); else p.AddLine(x + w, y, x + w, y);
        if (br) p.AddArc(x + w - d, y + h - d, d, d, 0, 90); else p.AddLine(x + w, y + h, x + w, y + h);
        if (bl) p.AddArc(x, y + h - d, d, d, 90, 90); else p.AddLine(x, y + h, x, y + h);
        p.CloseFigure();
        _g.FillPath(b, p);
    }

    void Checkers(float x, float y, float w, float h)
    {
        int cols = 6, rows = 3;
        float cw = w / cols, ch = h / rows;
        using var white = new SolidBrush(Color.White);
        using var black = new SolidBrush(Color.FromArgb(20, 20, 20));
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                _g.FillRectangle((r + c) % 2 == 0 ? white : black, x + c * cw, y + r * ch, cw, ch);
    }

    static Color Fade(Color c, float a) => Color.FromArgb((int)(c.A * Math.Clamp(a, 0, 1)), c.R, c.G, c.B);
    static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
    static Color On(Color bg) => bg.R * 0.299 + bg.G * 0.587 + bg.B * 0.114 > 150 ? Color.FromArgb(12, 12, 12) : Color.White;

    public void Dispose()
    {
        foreach (var f in _fonts.Values) f.Dispose();
        _g.Dispose();
        _bmp.Dispose();
    }
}
