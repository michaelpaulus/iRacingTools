using System.Diagnostics;

/// <summary>Finding ffmpeg and what it can do.</summary>
public static class Ffmpeg
{
    static readonly Dictionary<string, string> EncoderCache = new();

    /// <summary>ffmpeg.exe from the config, PATH or a winget install; null when there is none.</summary>
    public static string? Locate(BroadcastConfig cfg)
    {
        if (Path.IsPathRooted(cfg.Ffmpeg)) return File.Exists(cfg.Ffmpeg) ? cfg.Ffmpeg : null;
        var name = cfg.Ffmpeg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? cfg.Ffmpeg : cfg.Ffmpeg + ".exe";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim('"'), name); if (File.Exists(p)) return p; } catch (ArgumentException) { }
        }
        // winget puts a link here, but a console started before the install doesn't have it on PATH yet.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var link = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(link)) return link;
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
            foreach (var d in Directory.GetDirectories(packages, "*FFmpeg*"))
                if (Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault() is string exe) return exe;
        return null;
    }

    public static string Require(BroadcastConfig cfg) => Locate(cfg) ?? throw new FileNotFoundException(
        "ffmpeg wasn't found. Install it (for example: winget install Gyan.FFmpeg) or set \"Ffmpeg\" in broadcast.json to ffmpeg.exe's full path.");

    /// <summary>The configured encoder if this ffmpeg has it, else the CPU encoder.</summary>
    public static string Encoder(string ffmpeg, string wanted)
    {
        if (!EncoderCache.TryGetValue(ffmpeg, out var list))
        {
            var p = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -encoders") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            list = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            EncoderCache[ffmpeg] = list;
        }
        if (list.Contains(" " + wanted + " ")) return wanted;
        Console.WriteLine($"  ffmpeg has no {wanted} encoder; using libx264 (slower).");
        return "libx264";
    }

    public static bool IsHardware(string encoder) => encoder.Contains("nvenc") || encoder.Contains("amf") || encoder.Contains("qsv");

    /// <summary>Quality settings for a recording-grade (<paramref name="master"/>) or delivery encode.</summary>
    public static string EncoderArgs(string encoder, bool master) => encoder switch
    {
        _ when encoder.Contains("nvenc") => $"-c:v {encoder} -preset p5 -tune hq -rc vbr -cq {(master ? 17 : 20)} -b:v 0 -g 120",
        _ when encoder.Contains("amf") => $"-c:v {encoder} -quality quality -rc cqp -qp_i {(master ? 16 : 20)} -qp_p {(master ? 18 : 22)}",
        _ when encoder.Contains("qsv") => $"-c:v {encoder} -preset slower -global_quality {(master ? 18 : 22)}",
        _ => $"-c:v libx264 -preset {(master ? "veryfast" : "medium")} -crf {(master ? 16 : 19)}",
    };
}
