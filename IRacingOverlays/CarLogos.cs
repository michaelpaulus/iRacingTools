using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>
/// The make of a car from its iRacing name ("Ferrari 296 GT3" -> "ferrari"), and that make's badge from logos\.
/// The badges aren't in git (they're trademarks): <see cref="DownloadMissingAsync"/> fetches them from Wikipedia /
/// Wikimedia Commons the first time. Without a badge the overlay shows a short text tag instead.
/// </summary>
public static class CarLogos
{
    // First match wins, so model names that give the make away come before makes they contain.
    static readonly (string Key, string[] Words)[] Makes =
    {
        ("astonmartin", new[] { "aston martin", "amr " }),
        ("alfaromeo", new[] { "alfa romeo" }),
        ("mercedes", new[] { "mercedes", "amg" }),
        ("volkswagen", new[] { "volkswagen", "vw " }),
        ("chevrolet", new[] { "chevrolet", "chevy", "corvette", "camaro" }),
        ("ford", new[] { "ford", "mustang" }),
        ("toyota", new[] { "toyota", "supra", "gr86" }),
        ("mazda", new[] { "mazda", "mx-5" }),
        ("superformula", new[] { "super formula" }),
        ("ferrari", new[] { "ferrari" }), ("porsche", new[] { "porsche" }), ("bmw", new[] { "bmw" }),
        ("mclaren", new[] { "mclaren" }), ("audi", new[] { "audi" }), ("lamborghini", new[] { "lamborghini" }),
        ("acura", new[] { "acura" }), ("honda", new[] { "honda" }), ("cadillac", new[] { "cadillac" }),
        ("hyundai", new[] { "hyundai" }), ("kia", new[] { "kia " }), ("nissan", new[] { "nissan" }),
        ("lotus", new[] { "lotus" }), ("ligier", new[] { "ligier" }), ("dallara", new[] { "dallara" }),
        ("radical", new[] { "radical" }), ("renault", new[] { "renault" }), ("pontiac", new[] { "pontiac" }),
        ("mitsubishi", new[] { "mitsubishi" }), ("subaru", new[] { "subaru" }), ("genesis", new[] { "genesis" }),
        ("dodge", new[] { "dodge" }), ("williams", new[] { "williams" }),
    };

    /// <summary>
    /// Where each badge comes from: a file on Wikipedia / Commons, and how to cut the emblem out of it ("top": the
    /// emblem above the brand name, "left": beside it, "": the whole image).
    /// </summary>
    static readonly Dictionary<string, (string File, string Crop)> Sources = new()
    {
        ["acura"] = ("Acura logo.svg", "top"), ["alfaromeo"] = ("Alfa Romeo logo.png", ""),
        ["astonmartin"] = ("Aston Martin Lagonda brand logo.svg", ""), ["audi"] = ("Audi logo detail.svg", "top"),
        ["bmw"] = ("BMW logo (gray).svg", ""), ["cadillac"] = ("Cadillac (logo).svg", "top"),
        ["chevrolet"] = ("Chevrolet (logo).svg", "top"), ["dallara"] = ("Dallara logo.svg", ""),
        ["dodge"] = ("Dodge logo.svg", ""), ["ferrari"] = ("Scuderia Ferrari HP logo 24.svg", "left"),
        ["ford"] = ("Ford logo flat.svg", ""), ["genesis"] = ("Genesis division emblem.svg", "top"),
        ["honda"] = ("Honda logo.svg", ""), ["hyundai"] = ("Hyundai Motor Company logo.svg", "left"),
        ["kia"] = ("KIA logo3.svg", ""), ["lamborghini"] = ("Lamborghini Logo.svg", ""),
        ["ligier"] = ("Ligier2017.png", ""), ["lotus"] = ("Lotus Cars logo.svg", ""),
        ["mazda"] = ("Mazda logo.svg", ""), ["mclaren"] = ("McLaren logo (2026).svg", ""),
        ["mercedes"] = ("Mercedes-Benz free logo.svg", ""), ["mitsubishi"] = ("Mitsubishi logo.svg", ""),
        ["nissan"] = ("Nissan 2020 logo.svg", ""), ["pontiac"] = ("Pontiac dart emblem.svg", ""),
        ["porsche"] = ("Porsche Logo 2024.png", ""), ["radical"] = ("Radical Sportscars Logo.png", ""),
        ["renault"] = ("Renault logo.svg", "top"), ["subaru"] = ("Subaru logo (transparent).svg", "top"),
        ["superformula"] = ("Super Formula logo.svg", ""), ["toyota"] = ("Toyota carlogo.svg", "left"),
        ["volkswagen"] = ("Volkswagen logo 2019.svg", ""),
    };

    /// <summary>Logo key for a car name, or "" when the make isn't known.</summary>
    public static string Make(string carName)
    {
        var name = carName.ToLowerInvariant() + " ";
        foreach (var (key, words) in Makes)
            if (words.Any(w => name.Contains(w, StringComparison.Ordinal))) return key;
        return "";
    }

    /// <summary>Short text for when there's no badge: the first word of the car name, cut to four letters.</summary>
    public static string Tag(string carName)
    {
        var first = carName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length <= 4 ? first : first[..4];
    }

    /// <summary>
    /// logos\ in the folder the overlays run from (the project folder, with run.cmd or the command center), where
    /// downloads go; then next to the exe.
    /// </summary>
    public static string DownloadDir => Path.Combine(Environment.CurrentDirectory, "logos");
    static IEnumerable<string> Dirs => new[] { DownloadDir, Path.Combine(AppContext.BaseDirectory, "logos") }.Distinct(StringComparer.OrdinalIgnoreCase);

    static readonly Dictionary<string, Image?> Cache = new();
    static volatile int _downloaded; // bumped after a download, so badges cached as missing are looked for again
    static int _cachedAt;

    /// <summary>The badge for a key from <see cref="Make"/>, or null. Call from one thread only: GDI+ images are not thread-safe.</summary>
    public static Image? Logo(string key)
    {
        if (key.Length == 0) return null;
        if (_cachedAt != _downloaded) { Cache.Clear(); _cachedAt = _downloaded; }
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Image? img = null;
        var path = Dirs.Select(d => Path.Combine(d, key + ".png")).FirstOrDefault(File.Exists);
        try { if (path != null) using (var file = Image.FromFile(path)) img = LightenIfDark(new Bitmap(file)); }
        catch (OutOfMemoryException) { /* not a valid image: use the tag */ }
        return Cache[key] = img;
    }

    /// <summary>Every make with a badge source.</summary>
    public static IEnumerable<string> Keys => Sources.Keys.Order();

    /// <summary>Makes with a known source but no badge on disk yet.</summary>
    public static List<string> Missing() =>
        Sources.Keys.Where(k => !Dirs.Any(d => File.Exists(Path.Combine(d, k + ".png")))).Order().ToList();

    /// <summary>
    /// Fetches the badges that aren't on disk: one batched lookup on Wikipedia's API for the thumbnail URLs, then the
    /// images about one a second (Wikipedia rate-limits), each cut down to the emblem and saved to <see cref="DownloadDir"/>.
    /// Returns how many were saved. Failures are reported and skipped: the overlay works without them.
    /// </summary>
    public static async Task<int> DownloadMissingAsync(Action<string> log, CancellationToken ct = default)
    {
        var missing = Missing();
        if (missing.Count == 0) return 0;
        Directory.CreateDirectory(DownloadDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Wikimedia asks automated clients to say who they are.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("iRacingTools-Overlays/1.0 (+https://github.com/michaelpaulus/iRacingTools)");

        var titles = string.Join('|', missing.Select(k => "File:" + Sources[k].File));
        var api = "https://en.wikipedia.org/w/api.php?action=query&format=json&prop=imageinfo&iiprop=url&iiurlheight=96&titles=" +
                  Uri.EscapeDataString(titles);
        var urls = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(api, ct));
            var q = doc.RootElement.GetProperty("query");
            // The API reports titles in its own spelling ("File:BMW logo (gray).svg" stays, "_" becomes " ", ...).
            var asked = q.TryGetProperty("normalized", out var norm)
                ? norm.EnumerateArray().ToDictionary(n => n.GetProperty("to").GetString()!, n => n.GetProperty("from").GetString()!)
                : new Dictionary<string, string>();
            foreach (var page in q.GetProperty("pages").EnumerateObject())
            {
                if (!page.Value.TryGetProperty("imageinfo", out var info)) continue;
                var title = page.Value.GetProperty("title").GetString()!;
                var original = asked.TryGetValue(title, out var from) ? from : title;
                var key = missing.FirstOrDefault(k => "File:" + Sources[k].File == original);
                if (key != null) urls[key] = info[0].GetProperty("thumburl").GetString()!;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            log($"Couldn't look up the car badges on Wikipedia ({ex.Message}); text tags are shown instead.");
            return 0;
        }

        int saved = 0;
        foreach (var key in missing)
        {
            if (!urls.TryGetValue(key, out var url)) { log($"  {key}: not found on Wikipedia ({Sources[key].File})"); continue; }
            try
            {
                if (saved > 0) await Task.Delay(1200, ct);
                using var stream = new MemoryStream(await http.GetByteArrayAsync(url, ct));
                using var src = new Bitmap(stream);
                using var badge = Emblem(src, Sources[key].Crop);
                badge.Save(Path.Combine(DownloadDir, key + ".png"), ImageFormat.Png);
                saved++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or ExternalException)
            {
                log($"  {key}: couldn't download ({ex.Message})");
            }
        }
        if (saved > 0) _downloaded++;
        return saved;
    }

    /// <summary>The emblem cut out of a logo image, trimmed to what's drawn and at most 96 x 48 px.</summary>
    internal static Bitmap Emblem(Bitmap src, string crop)
    {
        var r = Bounds(src, new Rectangle(0, 0, src.Width, src.Height));
        if (r.IsEmpty) r = new Rectangle(0, 0, src.Width, src.Height);
        else if (crop == "top") r = Bounds(src, FirstBlock(src, r, vertical: true));
        else if (crop == "left") r = Bounds(src, FirstBlock(src, r, vertical: false));
        if (r.Width <= 0 || r.Height <= 0) r = new Rectangle(0, 0, src.Width, src.Height);

        double scale = Math.Min(1, Math.Min(96.0 / r.Width, 48.0 / r.Height));
        var dst = new Bitmap(Math.Max(1, (int)(r.Width * scale)), Math.Max(1, (int)(r.Height * scale)), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height), r, GraphicsUnit.Pixel);
        return dst;
    }

    static bool Opaque(Bitmap b, int x, int y) => b.GetPixel(x, y).A > 40;

    /// <summary>The smallest rectangle inside <paramref name="area"/> holding everything drawn (empty if nothing is).</summary>
    static Rectangle Bounds(Bitmap b, Rectangle area)
    {
        int minX = area.Right, minY = area.Bottom, maxX = area.Left - 1, maxY = area.Top - 1;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (Opaque(b, x, y))
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        return maxX < minX ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>The first part of the drawing, up to a gap of two empty rows (or columns): the emblem, not the name.</summary>
    static Rectangle FirstBlock(Bitmap b, Rectangle r, bool vertical)
    {
        int start = vertical ? r.Top : r.Left, end = vertical ? r.Bottom : r.Right, gap = 0;
        bool seen = false;
        for (int i = start; i < end; i++)
        {
            bool any = false;
            for (int j = vertical ? r.Left : r.Top; j < (vertical ? r.Right : r.Bottom) && !any; j++)
                any = vertical ? Opaque(b, j, i) : Opaque(b, i, j);
            if (any) { seen = true; gap = 0; }
            else if (seen && ++gap >= 2)
                return vertical ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, i - 1) : Rectangle.FromLTRB(r.Left, r.Top, i - 1, r.Bottom);
        }
        return r;
    }

    /// <summary>
    /// Black and dark-grey badges (Acura, Audi, Mercedes, VW, ...) vanish on the dark overlay: redraw those in light grey,
    /// keeping their shape and shading. Coloured badges are left as they are.
    /// </summary>
    static Bitmap LightenIfDark(Bitmap bmp)
    {
        double lum = 0, sat = 0;
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A < 128) continue;
                lum += Luma(c);
                sat += Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
                n++;
            }
        if (n == 0 || lum / n > 110 || sat / n > 40) return bmp;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A == 0) continue;
                int v = (int)Math.Clamp(255 - Luma(c) * 0.5, 0, 255) - 20; // dark -> light grey, light details stay a little darker
                bmp.SetPixel(x, y, Color.FromArgb(c.A, v, v, Math.Min(255, v + 4)));
            }
        return bmp;
    }

    static double Luma(Color c) => c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
}
