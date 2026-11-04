using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

/// <summary>What the overlay draws; produced on the telemetry thread, read on the UI thread.</summary>
public sealed record OverlayState(
    bool TimingKnown,
    int Sector, double SectorDelta, double Trend,
    int LastSector, double? LastSectorDelta,
    double CornerDelta, string CornerName,
    double YouPct, double GhostPct);

/// <summary>
/// Your pedals vs the reference's at the same spot on track, plus a short distance trace:
/// <see cref="BackBins"/> bins behind you and the rest ahead (reference only, so you can see what's coming).
/// </summary>
public sealed record PedalState(
    double YouBrake, double YouThrottle, double RefBrake, double RefThrottle,
    double[] YouBrakeTrace, double[] YouThrottleTrace, double[] RefBrakeTrace, double[] RefThrottleTrace,
    int BackBins, double BinMetres);

/// <summary>
/// Tracks the gap to the reference lap. The headline number restarts from zero at each of iRacing's
/// sector splits, so it shows how the current sector is going rather than the whole lap's deficit.
/// </summary>
public sealed class GhostTracker
{
    readonly ReferenceLap _ref;
    readonly Queue<(double T, double Gap)> _history = new();
    double[] _sectorStarts = { 0 };
    int _corner = -1, _sector = -1, _lastSector;
    double _cornerStartGap, _sectorStartGap, _lastPct = -1, _lastGap = double.NaN;
    double? _lastSectorDelta;

    public volatile OverlayState? State;
    public volatile PedalState? Pedals;
    public volatile GhostRelative? Relative;
    readonly TrackPath _path;

    // Your pedal inputs by distance bin; never cleared, so the trace runs smoothly across the line.
    readonly double[] _youBrake, _youThrottle;
    readonly double _binMetres;
    public const double TraceBackMetres = 300, TraceAheadMetres = 200;

    public GhostTracker(ReferenceLap reference, double trackKm)
    {
        _ref = reference;
        int n = reference.Trace.N;
        _youBrake = new double[n]; _youThrottle = new double[n];
        Array.Fill(_youBrake, double.NaN); Array.Fill(_youThrottle, double.NaN);
        _binMetres = trackKm * 1000 / n;
        _path = new TrackPath(reference);
    }

    void UpdatePedals(double pct, double brake, double throttle)
    {
        var r = _ref.Trace;
        int n = r.N, bin = Math.Clamp((int)(pct * n), 0, n - 1);
        _youBrake[bin] = brake; _youThrottle[bin] = throttle;

        int back = (int)Math.Round(TraceBackMetres / _binMetres), ahead = (int)Math.Round(TraceAheadMetres / _binMetres);
        int len = back + ahead + 1;
        var yb = new double[len]; var yt = new double[len]; var rb = new double[len]; var rt = new double[len];
        for (int i = 0; i < len; i++)
        {
            int b = ((bin - back + i) % n + n) % n;
            bool ahead_ = i > back;
            yb[i] = ahead_ ? double.NaN : _youBrake[b];
            yt[i] = ahead_ ? double.NaN : _youThrottle[b];
            rb[i] = r.Brake[b]; rt[i] = r.Throttle[b];
        }
        Pedals = new PedalState(brake, throttle, r.Brake[bin], r.Throttle[bin], yb, yt, rb, rt, back, _binMetres);
    }

    /// <summary>Sector start positions (lap fraction) from the session info's SplitTimeInfo.</summary>
    public void SetSectors(IEnumerable<double> starts)
    {
        var s = starts.Where(x => x >= 0 && x < 1).Append(0).Distinct().Order().ToArray();
        _sectorStarts = s;
    }

    int SectorOf(double pct)
    {
        int i = 0;
        while (i + 1 < _sectorStarts.Length && pct >= _sectorStarts[i + 1]) i++;
        return i;
    }

    void CloseSector(double endGap)
    {
        if (_sector >= 0 && !double.IsNaN(endGap))
        {
            _lastSector = _sector;
            _lastSectorDelta = endGap - _sectorStartGap;
        }
    }

    // The ghost is anchored level with you at a known moment (crossing the line, leaving the pits,
    // rejoining after a reset) and runs on the reference's timing from there.
    double _anchorTime = double.NaN, _anchorRef;

    void Anchor(double pct, double now)
    {
        _anchorTime = now;
        _anchorRef = _ref.TimeAtPct(pct);
        _history.Clear();
        _corner = -1;
        _sector = SectorOf(pct); // a partial sector after the anchor isn't reported as a sector result
        _sectorStartGap = 0;
    }

    void Unanchor()
    {
        _anchorTime = double.NaN;
        _history.Clear(); _corner = -1; _sector = -1; _lastGap = double.NaN;
        Relative = null;
    }

    public void Update(double pct, double sessionTime, double brake, double throttle, double yawNorth, bool onTrack, bool onPitRoad, double speed)
    {
        if (double.IsNaN(pct) || pct < 0 || pct >= 1) return;
        UpdatePedals(pct, brake, throttle);

        bool wrapped = _lastPct >= 0 && pct < _lastPct - 0.5;
        bool jumped = _lastPct >= 0 && !wrapped && Math.Abs(pct - _lastPct) > 0.02;
        _lastPct = pct;

        if (!onTrack || onPitRoad || jumped)
        {
            // In the pits, off track or just reset: wait, then start level again once you're racing.
            Unanchor();
            State = new OverlayState(false, SectorOf(pct) + 1, 0, 0, _lastSector + 1, _lastSectorDelta, 0, "", pct, double.NaN);
            return;
        }
        if (wrapped && !double.IsNaN(_anchorTime))
        {
            CloseSector(_lastGap); // the final sector ends with the lap's closing gap
            Anchor(pct, sessionTime); // and the new lap starts level at the line
        }
        if (double.IsNaN(_anchorTime))
        {
            // Just left the pits or rejoined: start level once you're up to speed (90% of the reference's
            // speed here), not at the pit-limiter or from a standstill, or you'd start seconds behind.
            int b0 = Math.Clamp((int)(pct * _ref.Trace.N), 0, _ref.Trace.N - 1);
            double refSpeed = _ref.Trace.Speed[b0];
            if (!double.IsNaN(refSpeed) && speed < 0.9 * refSpeed)
            {
                State = new OverlayState(false, SectorOf(pct) + 1, 0, 0, _lastSector + 1, _lastSectorDelta, 0, "", pct, double.NaN);
                return;
            }
            Anchor(pct, sessionTime);
        }

        double gap = (sessionTime - _anchorTime) - (_ref.TimeAtPct(pct) - _anchorRef);
        _history.Enqueue((sessionTime, gap));
        while (_history.Count > 1 && sessionTime - _history.Peek().T > 1.0) _history.Dequeue();
        double trend = gap - _history.Peek().Gap; // change over the last second

        int sector = SectorOf(pct);
        if (sector != _sector)
        {
            if (_sector >= 0) CloseSector(gap);
            _sector = sector;
            _sectorStartGap = gap;
        }

        int bin = (int)(pct * _ref.Trace.N);
        int idx = _ref.Corners.FindIndex(c => bin >= c.Start && bin <= c.End);
        if (idx != _corner) { _corner = idx; _cornerStartGap = gap; }
        string name = idx >= 0 ? _ref.Corners[idx].Name : "";

        _lastGap = gap;
        // The ghost restarts level with you at each sector split, then runs on the reference's timing:
        // it's where the reference would be had it entered this sector when you did.
        double sectorGap = gap - _sectorStartGap;
        double ghostPct = _ref.PctAtTime(_anchorRef + (sessionTime - _anchorTime) - _sectorStartGap);
        State = new OverlayState(true, _sector + 1, sectorGap, trend, _lastSector + 1, _lastSectorDelta,
            gap - _cornerStartGap, name, pct, ghostPct);
        UpdateRelative(pct, ghostPct, sectorGap, yawNorth);
    }

    void UpdateRelative(double youPct, double ghostPct, double gap, double yawNorth)
    {
        double ahead = ghostPct - youPct;
        if (ahead > 0.5) ahead -= 1; else if (ahead < -0.5) ahead += 1;
        ahead *= _ref.Trace.N * _binMetres;

        // Both cars are placed on the reference line (your exact line isn't in the live telemetry).
        var you = _path.At(youPct);
        var ghost = _path.At(ghostPct);
        double heading = double.IsNaN(yawNorth) ? _path.HeadingAt(youPct) : yawNorth; // clockwise from north
        double dx = ghost.East - you.East, dy = ghost.North - you.North;
        double forward = dx * Math.Sin(heading) + dy * Math.Cos(heading);
        double right = dx * Math.Cos(heading) - dy * Math.Sin(heading);
        Relative = new GhostRelative(ahead, gap, forward, right);
    }
}

/// <summary>Where the reference car is relative to yours: along the track, and in your car's frame (metres).</summary>
public sealed record GhostRelative(double AheadMetres, double GapSeconds, double Forward, double Right);

/// <summary>The reference lap's GPS line in local metres, looked up by lap fraction.</summary>
public sealed class TrackPath
{
    readonly double[] _e, _n;

    public TrackPath(ReferenceLap r)
    {
        int len = r.Lat.Length;
        _e = new double[len]; _n = new double[len];
        double lat0 = r.Lat.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Average();
        double lon0 = r.Lon.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Average();
        double k = Math.Cos(lat0 * Math.PI / 180);
        int last = -1;
        for (int b = 0; b < len; b++)
        {
            if (double.IsNaN(r.Lat[b])) { _e[b] = double.NaN; continue; }
            _e[b] = (r.Lon[b] - lon0) * 111320 * k; _n[b] = (r.Lat[b] - lat0) * 110540;
            last = b;
        }
        // Fill gaps with the previous known point so lookups never return NaN.
        for (int b = 0; b < len; b++)
            if (double.IsNaN(_e[b])) { int src = b > 0 ? b - 1 : last; _e[b] = src >= 0 ? _e[src] : 0; _n[b] = src >= 0 ? _n[src] : 0; }
    }

    public (double East, double North) At(double pct)
    {
        int len = _e.Length;
        double x = (pct - Math.Floor(pct)) * len;
        int a = (int)x % len, b = (a + 1) % len;
        double t = x - Math.Floor(x);
        return (_e[a] + (_e[b] - _e[a]) * t, _n[a] + (_n[b] - _n[a]) * t);
    }

    public double HeadingAt(double pct)
    {
        double step = 2.0 / _e.Length;
        var p = At(pct - step); var q = At(pct + step);
        return Math.Atan2(q.East - p.East, q.North - p.North);
    }
}

/// <summary>Draws the gap number, the corner gain/loss bar and the track map with both cars.</summary>
public sealed class OverlayPainter
{
    static readonly Color Bg = Color.FromArgb(18, 20, 24);
    static readonly Color Track = Color.FromArgb(90, 96, 106);
    static readonly Color You = Color.FromArgb(64, 170, 255);
    static readonly Color Ghost = Color.FromArgb(255, 160, 40);
    static readonly Color Gain = Color.FromArgb(60, 200, 110);
    static readonly Color Loss = Color.FromArgb(235, 70, 60);
    static readonly Color Dim = Color.FromArgb(150, 156, 166);

    readonly ReferenceLap _ref;
    readonly PointF[] _projected; // track shape in metres-ish units, y down
    readonly RectangleF _extent;

    public OverlayPainter(ReferenceLap reference)
    {
        _ref = reference;
        var pts = new List<PointF>();
        double lat0 = reference.Lat.Where(v => !double.IsNaN(v)).DefaultIfEmpty(50).Average();
        double k = Math.Cos(lat0 * Math.PI / 180);
        for (int b = 0; b < reference.Lat.Length; b++)
        {
            if (double.IsNaN(reference.Lat[b])) { pts.Add(new PointF(float.NaN, float.NaN)); continue; }
            pts.Add(new PointF((float)(reference.Lon[b] * k * 111320), (float)(-reference.Lat[b] * 110540)));
        }
        _projected = pts.ToArray();
        var ok = _projected.Where(p => !float.IsNaN(p.X)).ToList();
        _extent = ok.Count == 0 ? RectangleF.Empty
            : RectangleF.FromLTRB(ok.Min(p => p.X), ok.Min(p => p.Y), ok.Max(p => p.X), ok.Max(p => p.Y));
    }

    public void Paint(Graphics g, Size size, OverlayState? s)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Bg);
        int w = size.Width, pad = 10;

        using var big = new Font("Segoe UI", 26, FontStyle.Bold);
        using var small = new Font("Segoe UI", 9.5f);
        using var smallBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var dim = new SolidBrush(Dim);

        // 1. Gap within the current sector (restarts at each split), coloured by whether it's growing or shrinking right now.
        g.DrawString(s == null ? $"vs {_ref.Name}" : $"Sector {s.Sector} vs {_ref.Name}", small, dim, pad, pad);
        if (s is { TimingKnown: true })
        {
            var color = s.Trend < -0.01 ? Gain : s.Trend > 0.01 ? Loss : Color.White;
            using var brush = new SolidBrush(color);
            g.DrawString(s.SectorDelta.ToString("+0.00;-0.00", CultureInfo.InvariantCulture), big, brush, pad - 4, pad + 14);
        }
        else
        {
            g.DrawString("starts once you're up to speed", small, dim, pad, pad + 26);
        }

        // Result of the sector just finished, top right.
        if (s?.LastSectorDelta is double last)
        {
            using var lastBrush = new SolidBrush(last < 0 ? Gain : Loss);
            using var medium = new Font("Segoe UI", 15, FontStyle.Bold);
            var label = $"last: sector {s.LastSector}";
            var value = last.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
            float lw = g.MeasureString(label, small).Width, vw = g.MeasureString(value, medium).Width;
            g.DrawString(label, small, dim, w - pad - lw, pad);
            g.DrawString(value, medium, lastBrush, w - pad - vw + 4, pad + 18);
        }

        // 2. Corner bar: time gained (green, left) or lost (red, right) since entering this corner. Full scale = 0.5 s.
        int barY = 74, barH = 18, barW = w - 2 * pad, mid = pad + barW / 2;
        using (var barBg = new SolidBrush(Color.FromArgb(40, 44, 52))) g.FillRectangle(barBg, pad, barY, barW, barH);
        if (s is { TimingKnown: true })
        {
            double frac = Math.Clamp(s.CornerDelta / 0.5, -1, 1);
            int len = (int)Math.Round(Math.Abs(frac) * barW / 2);
            using var fill = new SolidBrush(frac < 0 ? Gain : Loss);
            if (frac < 0) g.FillRectangle(fill, mid - len, barY, len, barH);
            else g.FillRectangle(fill, mid, barY, len, barH);
            var label = $"{s.CornerName}  {s.CornerDelta.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}";
            g.DrawString(label, smallBold, Brushes.White, pad, barY + barH + 2);
        }
        using (var midPen = new Pen(Color.White, 1)) g.DrawLine(midPen, mid, barY - 2, mid, barY + barH + 2);

        // 3. Track map with you and the reference ("ghost") car.
        int mapTop = barY + barH + 24;
        var map = new RectangleF(pad, mapTop, w - 2 * pad, size.Height - mapTop - pad);
        if (_extent.IsEmpty || map.Height < 40) return;
        float scale = Math.Min(map.Width / _extent.Width, map.Height / _extent.Height);
        float ox = map.X + (map.Width - _extent.Width * scale) / 2 - _extent.X * scale;
        float oy = map.Y + (map.Height - _extent.Height * scale) / 2 - _extent.Y * scale;
        PointF Map(PointF p) => new(p.X * scale + ox, p.Y * scale + oy);

        using (var pen = new Pen(Track, 3) { LineJoin = LineJoin.Round })
        {
            var seg = new List<PointF>();
            void Flush() { if (seg.Count > 1) g.DrawLines(pen, seg.ToArray()); seg.Clear(); }
            foreach (var pt in _projected)
            {
                if (float.IsNaN(pt.X)) { Flush(); continue; }
                seg.Add(Map(pt));
            }
            Flush();
        }

        // Start/finish marker
        var sf = _projected.FirstOrDefault(p => !float.IsNaN(p.X));
        using (var sfPen = new Pen(Color.White, 2)) { var q = Map(sf); g.DrawLine(sfPen, q.X - 5, q.Y, q.X + 5, q.Y); }

        if (s == null) return;
        if (s.TimingKnown && !double.IsNaN(s.GhostPct)) Dot(g, PosAt(s.GhostPct, Map), Ghost, 6);
        Dot(g, PosAt(s.YouPct, Map), You, 7);

    }

    /// <summary>The pedal window: brake/throttle bars and traces, laid out to fill whatever size the window is.</summary>
    public void PaintPedalWindow(Graphics g, Size size, PedalState? p)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Bg);
        const int pad = 10, top = 28;
        using var small = new Font("Segoe UI", 9.5f);
        if (p == null)
        {
            using var dim = new SolidBrush(Dim);
            g.DrawString("pedals: waiting for telemetry", small, dim, pad, pad);
            return;
        }
        PaintPedals(g, p, pad, top, size.Width - 2 * pad, size.Height - top - pad, small);
    }

    void PaintPedals(Graphics g, PedalState p, int x, int y, int width, int height, Font small)
    {
        using var dim = new SolidBrush(Dim);
        using var trackBg = new SolidBrush(Color.FromArgb(40, 44, 52));
        using var youBrush = new SolidBrush(You);
        using var refBrush = new SolidBrush(Ghost);

        // Legend (identity never by colour alone: the bars are also always you-left, reference-right).
        float lx = x;
        void Key(Brush b, string text)
        {
            g.FillRectangle(b, lx, y - 13, 10, 10);
            g.DrawString(text, small, dim, lx + 12, y - 17);
            lx += 16 + g.MeasureString(text, small).Width;
        }
        Key(youBrush, "you");
        Key(refBrush, _ref.Name);

        // Bars: brake pair and throttle pair, each you | reference at your current position.
        const int barW = 16, gap = 2, groupGap = 14;
        int barTop = y + 2, barBottom = y + height - 16;
        int bx = x;
        void Bar(double v, Brush b)
        {
            g.FillRectangle(trackBg, bx, barTop, barW, barBottom - barTop);
            if (!double.IsNaN(v) && v > 0.005)
            {
                int h = (int)Math.Round(Math.Clamp(v, 0, 1) * (barBottom - barTop));
                using var path = RoundedTop(new RectangleF(bx, barBottom - h, barW, h), Math.Min(4, h / 2f));
                g.FillPath(b, path);
            }
            bx += barW + gap;
        }
        void GroupLabel(string text, int startX)
        {
            float tw = g.MeasureString(text, small).Width;
            g.DrawString(text, small, dim, startX + (2 * barW + gap - tw) / 2, barBottom + 1);
        }
        int g1 = bx; Bar(p.YouBrake, youBrush); Bar(p.RefBrake, refBrush); GroupLabel("Brake", g1);
        bx += groupGap;
        int g2 = bx; Bar(p.YouThrottle, youBrush); Bar(p.RefThrottle, refBrush); GroupLabel("Throttle", g2);

        // Traces: brake on top, throttle below (tagged B / T in the gutter); vertical line marks where you are now.
        int tx = bx + 24, tw2 = x + width - tx;
        int plotH = (barBottom - barTop - 6) / 2;
        var brakeArea = new Rectangle(tx, barTop, tw2, plotH);
        var throttleArea = new Rectangle(tx, barTop + plotH + 6, tw2, plotH);
        g.DrawString("B", small, dim, tx - 13, brakeArea.Y + (plotH - 14) / 2);
        g.DrawString("T", small, dim, tx - 13, throttleArea.Y + (plotH - 14) / 2);
        Trace(g, brakeArea, p.RefBrakeTrace, p.YouBrakeTrace);
        Trace(g, throttleArea, p.RefThrottleTrace, p.YouThrottleTrace);

        float nowX = tx + (float)p.BackBins / (p.RefBrakeTrace.Length - 1) * tw2;
        using (var nowPen = new Pen(Color.FromArgb(200, 255, 255, 255), 1)) g.DrawLine(nowPen, nowX, barTop - 2, nowX, barBottom);
        g.DrawString($"-{GhostTracker.TraceBackMetres:F0} m", small, dim, tx - 2, barBottom + 1);
        g.DrawString("now", small, dim, nowX - g.MeasureString("now", small).Width / 2, barBottom + 1);
        var aheadLabel = $"+{GhostTracker.TraceAheadMetres:F0} m";
        g.DrawString(aheadLabel, small, dim, x + width - g.MeasureString(aheadLabel, small).Width + 2, barBottom + 1);
    }

    static void Trace(Graphics g, Rectangle area, double[] reference, double[] you)
    {
        using (var bg = new SolidBrush(Color.FromArgb(28, 31, 37))) g.FillRectangle(bg, area);
        PointF[]? Line(double[] v)
        {
            var pts = new List<PointF>();
            for (int i = 0; i < v.Length; i++)
            {
                if (double.IsNaN(v[i])) continue;
                float px = area.X + (float)i / (v.Length - 1) * area.Width;
                float py = area.Bottom - 1 - (float)Math.Clamp(v[i], 0, 1) * (area.Height - 2);
                pts.Add(new PointF(px, py));
            }
            return pts.Count > 1 ? pts.ToArray() : null;
        }
        using var refPen = new Pen(Ghost, 2) { LineJoin = LineJoin.Round };
        using var youPen = new Pen(You, 2) { LineJoin = LineJoin.Round };
        if (Line(reference) is { } r) g.DrawLines(refPen, r);
        if (Line(you) is { } y) g.DrawLines(youPen, y);
    }

    static GraphicsPath RoundedTop(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius < 0.5f) { path.AddRectangle(r); return path; }
        float d = radius * 2;
        path.AddLine(r.Left, r.Bottom, r.Left, r.Top + radius);
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddLine(r.Right, r.Top + radius, r.Right, r.Bottom);
        path.CloseFigure();
        return path;
    }

    PointF PosAt(double pct, Func<PointF, PointF> map)
    {
        int n = _projected.Length;
        int b = Math.Clamp((int)(pct * n), 0, n - 1);
        for (int k = 0; k < n; k++) // nearest bin with GPS
        {
            var p = _projected[(b + k) % n];
            if (!float.IsNaN(p.X)) return map(p);
        }
        return PointF.Empty;
    }

    static void Dot(Graphics g, PointF p, Color c, float r)
    {
        using var brush = new SolidBrush(c);
        using var outline = new Pen(Color.Black, 1.5f);
        g.FillEllipse(brush, p.X - r, p.Y - r, 2 * r, 2 * r);
        g.DrawEllipse(outline, p.X - r, p.Y - r, 2 * r, 2 * r);
    }
}

