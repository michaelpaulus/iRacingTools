using System.Drawing;

/// <summary>
/// iRacing flair (country) names to ISO 3166 two-letter codes (three letters for the UK home nations).
/// The code picks the flag image in flags\; it is also drawn as a text badge when there is no image.
/// </summary>
public static class Countries
{
    static readonly Dictionary<string, Image?> FlagCache = new();

    /// <summary>
    /// The flag for a code from <see cref="Code"/>, loaded from flags\ next to the exe (flagcdn.com 40 px PNGs),
    /// or null when there is none. Call from one thread only: GDI+ images are not thread-safe.
    /// </summary>
    public static Image? Flag(string code)
    {
        if (code.Length == 0) return null;
        if (FlagCache.TryGetValue(code, out var cached)) return cached;
        string name = code switch { "ENG" => "gb-eng", "SCO" => "gb-sct", "WAL" => "gb-wls", "NIR" => "gb-nir", _ => code.ToLowerInvariant() };
        var path = Path.Combine(AppContext.BaseDirectory, "flags", name + ".png");
        Image? img = null;
        try { if (File.Exists(path)) img = Image.FromFile(path); } catch (OutOfMemoryException) { /* not a valid image: fall back to the badge */ }
        return FlagCache[code] = img;
    }

    static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Argentina"] = "AR", ["Australia"] = "AU", ["Austria"] = "AT", ["Belgium"] = "BE", ["Bolivia"] = "BO",
        ["Brazil"] = "BR", ["Bulgaria"] = "BG", ["Canada"] = "CA", ["Chile"] = "CL", ["China"] = "CN",
        ["Colombia"] = "CO", ["Costa Rica"] = "CR", ["Croatia"] = "HR", ["Cyprus"] = "CY", ["Czech Republic"] = "CZ",
        ["Czechia"] = "CZ", ["Denmark"] = "DK", ["Dominican Republic"] = "DO", ["Ecuador"] = "EC", ["Egypt"] = "EG",
        ["El Salvador"] = "SV", ["England"] = "ENG", ["Estonia"] = "EE", ["Finland"] = "FI", ["France"] = "FR",
        ["Germany"] = "DE", ["Greece"] = "GR", ["Guatemala"] = "GT", ["Honduras"] = "HN", ["Hong Kong"] = "HK",
        ["Hungary"] = "HU", ["Iceland"] = "IS", ["India"] = "IN", ["Indonesia"] = "ID", ["Ireland"] = "IE",
        ["Israel"] = "IL", ["Italy"] = "IT", ["Jamaica"] = "JM", ["Japan"] = "JP", ["Kazakhstan"] = "KZ",
        ["Korea"] = "KR", ["South Korea"] = "KR", ["Latvia"] = "LV", ["Lithuania"] = "LT", ["Luxembourg"] = "LU",
        ["Malaysia"] = "MY", ["Malta"] = "MT", ["Mexico"] = "MX", ["Monaco"] = "MC", ["Morocco"] = "MA",
        ["Netherlands"] = "NL", ["New Zealand"] = "NZ", ["Nicaragua"] = "NI", ["Northern Ireland"] = "NIR",
        ["Norway"] = "NO", ["Panama"] = "PA", ["Paraguay"] = "PY", ["Peru"] = "PE", ["Philippines"] = "PH",
        ["Poland"] = "PL", ["Portugal"] = "PT", ["Puerto Rico"] = "PR", ["Romania"] = "RO", ["Russia"] = "RU",
        ["Saudi Arabia"] = "SA", ["Scotland"] = "SCO", ["Serbia"] = "RS", ["Singapore"] = "SG", ["Slovakia"] = "SK",
        ["Slovenia"] = "SI", ["South Africa"] = "ZA", ["Spain"] = "ES", ["Sweden"] = "SE", ["Switzerland"] = "CH",
        ["Taiwan"] = "TW", ["Thailand"] = "TH", ["Turkey"] = "TR", ["Türkiye"] = "TR", ["Ukraine"] = "UA",
        ["United Arab Emirates"] = "AE", ["United Kingdom"] = "GB", ["United States"] = "US", ["Uruguay"] = "UY",
        ["Venezuela"] = "VE", ["Vietnam"] = "VN", ["Wales"] = "WAL",
    };

    /// <summary>Two-letter code, "" for no flair / "Global", or the first three letters of an unknown name.</summary>
    public static string Code(string flair)
    {
        if (string.IsNullOrWhiteSpace(flair) || flair is "-none-" or "None" or "Global") return "";
        if (Codes.TryGetValue(flair.Trim(), out var code)) return code;
        var letters = new string(flair.Where(char.IsLetter).Take(3).ToArray());
        return letters.ToUpperInvariant();
    }
}
