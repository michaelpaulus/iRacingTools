using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

static class Palette
{
    public static readonly Color Bg = Color.FromArgb(14, 16, 20);
    public static readonly Color Header = Color.FromArgb(24, 27, 33);
    public static readonly Color RowAlt = Color.FromArgb(20, 23, 28);
    public static readonly Color Player = Color.FromArgb(36, 62, 102);
    public static readonly Color Text = Color.FromArgb(236, 238, 242);
    public static readonly Color Dim = Color.FromArgb(140, 146, 158);
    public static readonly Color Badge = Color.FromArgb(48, 53, 62);
    public static readonly Color Gain = Color.FromArgb(88, 214, 125);
    public static readonly Color Loss = Color.FromArgb(242, 92, 84);
    public static readonly Color Fastest = Color.FromArgb(196, 128, 255);
    public static readonly Color Pit =Color.FromArgb(255, 190, 60);
    public static readonly Color Separator = Color.FromArgb(60, 66, 76);
    public static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);

    /// <summary>Black or white, whichever reads better on <paramref name="bg"/>.</summary>
    public static Color On(Color bg) => bg.R * 0.299 + bg.G * 0.587 + bg.B * 0.114 > 150 ? Color.FromArgb(12, 12, 12) : Color.White;
}

/// <summary>
/// Draws the standings in the style of iRacing's own: a title bar, then per class a row per car with position,
/// car number in the class colour, country, driver, car, tyre compound, licence, iRating, projected iRating change, laps,
/// last lap and gap.
/// Your estimated SR change goes in the footer.
/// Laid out for <see cref="BaseWidth"/> pixels and scaled to the window width.
/// </summary>
public sealed class StandingsPainter
{
    /// <summary>Was 638 before the Car and Tyre columns; saved widths are rescaled when it changes (see OverlaysConfig.Load).</summary>
    public const int BaseWidth = 712;
    public const int OldBaseWidth = 638;
    const float Pad = 8, TitleH = 34, ClassH = 24, ColumnsH = 18, RowH = 24, FooterH = 24;

    // Column widths at BaseWidth; the driver name takes what is left.
    static readonly (string Title, float Width, bool Right)[] Columns =
    {
        ("", 30, true), ("#", 40, false), ("", 34, false), ("Driver", 0, false), ("Car", 44, false), ("Tyre", 30, false),
        ("Lic", 52, false), ("iR", 46, true), ("±iR", 44, true), ("Laps", 38, true), ("Last", 66, true), ("Gap", 62, true),
    };

    static Color TyreColor(string name) =>
        Tyres.IsWet(name) ? (name.StartsWith("Inter", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(70, 190, 90) : Color.FromArgb(50, 130, 235))
        : name.StartsWith("Soft", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(230, 60, 55)
        : name.StartsWith("Med", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(245, 205, 50)
        : name.StartsWith("Hard", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(225, 228, 232)
        : Palette.Badge;

    public static int Height(StandingsSnapshot s, int width)
    {
        float scale = width / (float)BaseWidth;
        float h = TitleH;
        bool multi = s.Classes.Count > 1;
        if (s.Classes.Count > 0) h += ColumnsH;
        foreach (var c in s.Classes) h += (multi ? ClassH : 0) + c.Rows.Count * RowH;
        if (s.PlayerSr != null) h += FooterH;
        return (int)Math.Ceiling((h + Pad / 2) * scale);
    }

    public void Paint(Graphics g, Size size, StandingsSnapshot s)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Palette.Bg);
        float k = size.Width / (float)BaseWidth;
        g.ScaleTransform(k, k);
        float w = BaseWidth;

        // Pixel sizes so the layout is the same at any Windows display scaling; resize the window to make it bigger.
        using var title = new Font("Segoe UI Semibold", 14f, GraphicsUnit.Pixel);
        using var normal = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        using var bold = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 10.5f, GraphicsUnit.Pixel);
        using var smallBold = new Font("Segoe UI Semibold", 10.5f, GraphicsUnit.Pixel);
        using var gapFont = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var positionFont = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);

        // Title bar: track and session on the left, SOF, progress and local time on the right.
        Fill(g, Palette.Header, 0, 0, w, TitleH);
        Text(g, s.Session.Length > 0 ? $"{s.Track}  ·  {s.Session}" : s.Track, title, Palette.Text, Pad, 0, w / 2, TitleH, false);
        var right = new List<string>();
        if (s.Sof > 0) right.Add($"SOF {s.Sof.ToString("N0", CultureInfo.InvariantCulture)}");
        if (s.Progress.Length > 0) right.Add(s.Progress);
        right.Add(DateTime.Now.ToString("t", CultureInfo.CurrentCulture));
        Text(g, string.Join("  ·  ", right), title, Palette.Text, w / 2, 0, w / 2 - Pad, TitleH, true);

        float y = TitleH;
        if (s.Classes.Count == 0) return;

        var x = new float[Columns.Length + 1];
        x[0] = Pad;
        float fixedW = Columns.Sum(c => c.Width);
        for (int i = 0; i < Columns.Length; i++)
            x[i + 1] = x[i] + (Columns[i].Width > 0 ? Columns[i].Width : w - 2 * Pad - fixedW);

        for (int i = 0; i < Columns.Length; i++)
            Text(g, Columns[i].Title, small, Palette.Dim, x[i] + 3, y, x[i + 1] - x[i] - 6, ColumnsH, Columns[i].Right);
        y += ColumnsH;

        bool multi = s.Classes.Count > 1;
        foreach (var c in s.Classes)
        {
            var classColor = Palette.Rgb(c.Color);
            if (multi)
            {
                Fill(g, Palette.Header, 0, y, w, ClassH);
                Fill(g, classColor, 0, y, 4, ClassH);
                Text(g, c.Name, bold, Palette.Text, Pad + 4, y, w / 2, ClassH, false);
                Text(g, $"SOF {c.Sof.ToString("N0", CultureInfo.InvariantCulture)}  ·  {c.Cars} cars", small, Palette.Dim,
                    w / 2, y, w / 2 - Pad, ClassH, true);
                y += ClassH;
            }

            for (int r = 0; r < c.Rows.Count; r++)
            {
                var row = c.Rows[r];
                var d = row.Driver;
                if (row.IsPlayer) Fill(g, Palette.Player, 0, y, w, RowH);
                else if (r % 2 == 1) Fill(g, Palette.RowAlt, 0, y, w, RowH);
                if (row.RowsSkippedBefore)
                    using (var pen = new Pen(Palette.Separator, 1) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, Pad, y, w - Pad, y);

                var font = row.IsPlayer ? bold : normal;
                float cy = y + 3, ch = RowH - 6;

                // Like the gaps: your position stands out, the rest stay grey.
                // Same size for every row so the column stays aligned; yours is bold.
                if (row.ClassPosition > 0)
                    Text(g, row.ClassPosition.ToString(CultureInfo.InvariantCulture), row.IsPlayer ? positionFont : normal,
                    row.IsPlayer ? Palette.Text : Palette.Dim, x[0], y, x[1] - x[0] - 4, RowH, true);

                var carColor = Palette.Rgb(d.ClassColor); // all classes can share one list
                Pill(g, carColor, x[1] + 3, cy, x[2] - x[1] - 6, ch);
                Text(g, d.CarNumber, smallBold, Palette.On(carColor),x[1] + 3, y, x[2] - x[1] - 6, RowH, false, center: true);

                if (Countries.Flag(row.Country) is Image flag)
                    Flag(g, flag, x[2] + 2, cy, x[3] - x[2] - 4, ch);
                else if (row.Country.Length > 0)
                {
                    Pill(g, Palette.Badge, x[2] + 2, cy, x[3] - x[2] - 4, ch);
                    Text(g, row.Country, smallBold, Palette.Text, x[2] + 2, y, x[3] - x[2] - 4, RowH, false, center: true);
                }

                float nameRight = x[4] - 4;
                if (row.InPit)
                {
                    Pill(g, Palette.Pit, nameRight - 28, cy + 2, 28, ch - 4);
                    Text(g, "PIT", smallBold, Color.Black, nameRight - 28, y, 28, RowH, false, center: true);
                    nameRight -= 32;
                }
                Text(g, d.Name, font, Palette.Text, x[3] + 4, y, nameRight - x[3] - 4, RowH, false);

                // The make's badge; its first word as a tag when there's no badge for it.
                if (CarLogos.Logo(CarLogos.Make(d.CarName)) is Image logo)
                    Fit(g, logo, x[4] + 3, cy, x[5] - x[4] - 6, ch);
                else if (d.CarName.Length > 0)
                {
                    Pill(g, Palette.Badge, x[4] + 3, cy, x[5] - x[4] - 6, ch);
                    Text(g, CarLogos.Tag(d.CarName), smallBold, Palette.Text, x[4] + 3, y, x[5] - x[4] - 6, RowH, false, center: true);
                }

                if (row.TyreLabel.Length > 0)
                {
                    var tyreColor = TyreColor(row.Tyre);
                    float cx = (x[5] + x[6]) / 2;
                    using (var b = new SolidBrush(tyreColor)) g.FillEllipse(b, cx - ch / 2, cy, ch, ch);
                    Text(g, row.TyreLabel, smallBold, Palette.On(tyreColor), cx - ch / 2, y, ch, RowH, false, center: true);
                }

                var lic = Palette.Rgb(d.LicenseColor);
                Pill(g, lic, x[6] + 3, cy, x[7] - x[6] - 6, ch);
                Text(g, $"{d.LicenseLetter} {d.SafetyRating:0.00}", smallBold, Palette.On(lic), x[6] + 3, y, x[7] - x[6] - 6, RowH, false, center: true);

                Text(g, d.IRating > 0 ? d.IRating.ToString(CultureInfo.InvariantCulture) : "-", font, Palette.Text, x[7], y, x[8] - x[7] - 4, RowH, true);

                if (row.IRatingDelta is int dir)
                    Text(g, dir.ToString("+0;-0;0", CultureInfo.InvariantCulture), font, dir > 0 ? Palette.Gain : dir < 0 ? Palette.Loss : Palette.Dim,
                        x[8], y, x[9] - x[8] - 4, RowH, true);

                Text(g, row.Laps.ToString(CultureInfo.InvariantCulture), font, Palette.Dim, x[9], y, x[10] - x[9] - 4, RowH, true);
                var lastColor = row.LastLapMark switch { LapMark.ClassBest => Palette.Fastest, LapMark.PersonalBest => Palette.Gain, _ => Palette.Text };
                Text(g, row.LastLap, font, lastColor, x[10], y, x[11] - x[10] - 4, RowH, true);
                // The cars directly ahead and behind are the gaps that matter: bright and bold; the rest stay grey.
                Text(g, row.Gap, row.GapHighlight ? gapFont : normal, row.GapHighlight ? Palette.Text : Palette.Dim,
                    x[11], y, x[12] - x[11] - 4, RowH, true);
                y += RowH;
            }
        }

        if (s.PlayerSr != null)
        {
            Fill(g, Palette.Header, 0, y, w, FooterH);
            string sr = $"SR estimate  {s.PlayerSr}";
            Text(g, sr, normal, Palette.Text, Pad, y, w / 2 + 60, FooterH, false);
            if (s.PlayerSrDelta is double dsr)
            {
                float after = Pad + g.MeasureString(sr, normal, PointF.Empty, Left).Width + 6;
                Text(g, $"({dsr.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)})", bold,
                    dsr > 0.005 ? Palette.Gain : dsr < -0.005 ? Palette.Loss : Palette.Dim, after, y, w / 2, FooterH, false);
            }
            Text(g, $"{s.PlayerIncidents}x", bold, s.PlayerIncidents > 0 ? Palette.Loss : Palette.Dim, w / 2, y, w / 2 - Pad, FooterH, true);
        }
    }

    static void Fill(Graphics g, Color c, float x, float y, float w, float h)
    {
        using var b = new SolidBrush(c);
        g.FillRectangle(b, x, y, w, h);
    }

    static void Pill(Graphics g, Color c, float x, float y, float w, float h)
    {
        float r = Math.Min(4, h / 2);
        using var path = new GraphicsPath();
        path.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        path.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        path.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    /// <summary>Flag fitted inside the box at its own aspect ratio (Switzerland is square), with a thin outline.</summary>
    static void Flag(Graphics g, Image img, float x, float y, float w, float h)
    {
        float scale = Math.Min(w / img.Width, h / img.Height);
        float fw = img.Width * scale, fh = img.Height * scale;
        var rect = new RectangleF(x + (w - fw) / 2, y + (h - fh) / 2, fw, fh);
        var mode = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, rect);
        g.InterpolationMode = mode;
        using var pen = new Pen(Color.FromArgb(90, 0, 0, 0), 1);
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>Image fitted inside the box at its own aspect ratio, centred, no outline.</summary>
    static void Fit(Graphics g, Image img, float x, float y, float w, float h)
    {
        float scale = Math.Min(w / img.Width, h / img.Height);
        float fw = img.Width * scale, fh = img.Height * scale;
        var mode = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, new RectangleF(x + (w - fw) / 2, y + (h - fh) / 2, fw, fh));
        g.InterpolationMode = mode;
    }

    static readonly StringFormat Left = Format(StringAlignment.Near), Right = Format(StringAlignment.Far), Center = Format(StringAlignment.Center);

    // Typographic: no built-in padding, which differs between bold and regular fonts and would push your bold row's
    // right-aligned numbers a few pixels out of line with the others.
    static StringFormat Format(StringAlignment a) => new(StringFormat.GenericTypographic)
    {
        Alignment = a, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
    };

    static void Text(Graphics g, string text, Font f, Color c, float x, float y, float w, float h, bool right, bool center = false)
    {
        if (string.IsNullOrEmpty(text) || w <= 0) return;
        using var b = new SolidBrush(c);
        g.DrawString(text, f, b, new RectangleF(x, y, w, h), center ? Center : right ? Right : Left);
    }
}
