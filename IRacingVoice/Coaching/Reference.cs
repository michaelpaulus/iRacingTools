using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// What the session YAML says about the car, track and driver: the coach's per-car-and-track key, the track length,
/// and the iRacing IDs and iRating that Garage 61 searches use.
/// </summary>
public sealed record SessionFacts(string Key, double TrackM, string TrackName, int TrackId, int CarId, int IRating)
{
    public static SessionFacts Parse(string sessionInfo)
    {
        // [ \t]* not \s*: an empty value ("TrackConfigName: ") must not run on into the next line.
        string Yaml(string key) => Regex.Match(sessionInfo, $@"^[ \t]*{key}:[ \t]*(.*?)[ \t]*\r?$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "";
        double km = double.TryParse(Regex.Match(Yaml("TrackLength"), @"[\d.]+").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) ? k : 0;
        // The player's entry in the driver list: from "- CarIdx: n" to the next entry.
        string me = Regex.Match(sessionInfo, $@"-[ \t]*CarIdx:[ \t]*{Regex.Escape(Yaml("DriverCarIdx"))}[ \t]*\r?\n((?:[ \t]+\w+:.*\n)*)") is { Success: true } dm ? dm.Groups[1].Value : "";
        string Mine(string key) => Regex.Match(me, $@"^[ \t]+{key}:[ \t]*(.*?)[ \t]*\r?$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "";
        string car = Mine("CarPath") is { Length: > 0 } c ? c : "car";
        return new SessionFacts(Safe($"{car}_{Yaml("TrackName")}_{Yaml("TrackConfigName")}"), km * 1000, Yaml("TrackName"),
            int.TryParse(Yaml("TrackID"), out var t) ? t : 0, int.TryParse(Mine("CarID"), out var cid) ? cid : 0,
            int.TryParse(Mine("IRating"), out var ir) ? ir : 0);
    }

    static string Safe(string s) => Regex.Replace(s.Trim('_'), @"[^\w\-]+", "_");
}

/// <summary>
/// A lap to compare against: a Garage 61 telemetry export (.csv) or the fastest complete lap of an iRacing recording
/// (.ibt). Files live in reference\&lt;car_track&gt;\; .ibt files dropped in reference\inbox\ are sorted into place.
/// </summary>
public sealed class ReferenceLap
{
    public required string Name { get; init; }
    public required double LapTime { get; init; }
    public required Trace Trace { get; init; }

    /// <summary>The fastest lap in the folder for this car and track, or null.</summary>
    public static ReferenceLap? Best(string folder, int bins, double binM, double trackM)
    {
        if (!Directory.Exists(folder)) return null;
        ReferenceLap? best = null;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            ReferenceLap? lap = null;
            try
            {
                if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) lap = FromCsv(file, bins, binM, trackM);
                else if (file.EndsWith(".ibt", StringComparison.OrdinalIgnoreCase)) lap = FromIbt(file, bins, binM);
            }
            catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException or ArgumentException)
            {
                Console.WriteLine($"  [coach] Couldn't read reference lap {Path.GetFileName(file)}: {ex.Message}");
            }
            if (lap != null && (best == null || lap.LapTime < best.LapTime)) best = lap;
        }
        return best;
    }

    /// <summary>Moves recordings from reference\inbox\ into the folder for their car and track.</summary>
    public static void SortInbox(string referenceDir)
    {
        var inbox = Path.Combine(referenceDir, "inbox");
        if (!Directory.Exists(inbox)) return;
        foreach (var file in Directory.EnumerateFiles(inbox, "*.ibt"))
        {
            try
            {
                var facts = SessionFacts.Parse(IbtSessionInfo(file));
                var dest = Path.Combine(referenceDir, facts.Key);
                Directory.CreateDirectory(dest);
                File.Move(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            }
            catch (IOException) { /* still being copied: next time */ }
        }
    }

    /// <summary>Just the session YAML of an .ibt, without reading the (large) telemetry.</summary>
    public static string IbtSessionInfo(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[Irsdk.HeaderSize];
        fs.ReadExactly(head);
        int len = BitConverter.ToInt32(head, 16), off = BitConverter.ToInt32(head, 20);
        var buf = new byte[len];
        fs.Position = off;
        fs.ReadExactly(buf);
        return Irsdk.CStr(buf, 0, len);
    }

    /// <summary>The fastest complete, clean lap in a recording.</summary>
    public static ReferenceLap? FromIbt(string path, int bins, double binM)
    {
        using var ibt = new IbtReplay(path, 0);
        Trace? trace = null, best = null;
        double lapStart = 0, lastPct = -1, bestTime = double.MaxValue;
        bool clean = false;
        while (ibt.WaitForFrame(0))
        {
            double pct = ibt.Get("LapDistPct"), now = ibt.Get("SessionTime");
            if (double.IsNaN(pct) || pct < 0) { lastPct = -1; continue; }
            bool ok = ibt.Get("IsOnTrack") > 0 && ibt.Get("OnPitRoad") <= 0;
            if (lastPct > 0.9 && pct < 0.1)
            {
                double time = now - lapStart;
                if (trace != null && clean && trace.Coverage(0, bins - 1) > 0.95 && time < bestTime) { best = trace; bestTime = time; }
                trace = new Trace(bins, binM); lapStart = now; clean = ok;
            }
            else if (lastPct >= 0 && Math.Abs(pct - lastPct) > 0.02) clean = false; // reset or tow
            lastPct = pct;
            if (!ok) clean = false;
            trace?.Add(pct, now - lapStart, ibt.Get("Speed"), ibt.Get("Brake"), ibt.Get("Throttle"), ibt.Get("Gear"), ibt.Get("RPM"),
                ibt.Get("SteeringWheelAngle"), ibt.Get("LatAccel"), ibt.Get("LongAccel"), ibt.Get("YawRate"), ibt.Get("BrakeABSactive") > 0);
        }
        return best == null ? null : new ReferenceLap { Name = Path.GetFileNameWithoutExtension(path), LapTime = bestTime, Trace = best };
    }

    // ---- CSV (Garage 61's export; columns found by name, units worked out from the numbers)

    static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["pct"] = ["lapdistpct", "lapdistancepct", "lapdistpercent", "distpct", "lappct"],
        ["dist"] = ["lapdist", "lapdistance", "distance", "dist"],
        ["time"] = ["lapcurrentlaptime", "laptime", "time", "elapsed", "elapsedtime", "sessiontime", "timestamp"],
        ["speed"] = ["speed", "velocity", "groundspeed"],
        ["brake"] = ["brake", "brakeraw", "brakepedal"],
        ["throttle"] = ["throttle", "throttleraw", "gas"],
        ["gear"] = ["gear"],
        ["rpm"] = ["rpm", "enginerpm"],
        ["steer"] = ["steeringwheelangle", "steering", "steer", "steeringangle"],
        ["lat"] = ["lataccel", "latacc", "lateralacceleration", "glat", "latg"],
        ["long"] = ["longaccel", "longacc", "longitudinalacceleration", "glong", "longg"],
        ["yaw"] = ["yawrate"],
        ["abs"] = ["brakeabsactive", "absactive", "abs"],
    };

    public static ReferenceLap? FromCsv(string path, int bins, double binM, double trackM)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 50) return null;
        char sep = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
        var header = lines[0].Split(sep).Select(h => h.Trim().Trim('"')).ToArray();
        var col = new Dictionary<string, int>();
        var unit = new Dictionary<string, string>();
        foreach (var (field, names) in Aliases)
            for (int i = 0; i < header.Length && !col.ContainsKey(field); i++)
            {
                string plain = Regex.Replace(Regex.Replace(header[i], @"\(.*?\)|\[.*?\]", ""), @"[^A-Za-z0-9]", "").ToLowerInvariant();
                if (names.Contains(plain)) { col[field] = i; unit[field] = header[i].ToLowerInvariant(); }
            }
        if (!col.ContainsKey("speed") || !(col.ContainsKey("pct") || col.ContainsKey("dist")))
            throw new FormatException("needs Speed and LapDistPct (or LapDist) columns; found " + string.Join(", ", header));

        var rows = new List<double[]>();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(sep);
            var row = new double[Aliases.Count];
            int k = 0;
            foreach (var field in Aliases.Keys)
            {
                row[k++] = col.TryGetValue(field, out int i) && i < cells.Length &&
                           double.TryParse(cells[i].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
            }
            rows.Add(row);
        }
        int F(string field) => Aliases.Keys.ToList().IndexOf(field);
        double[] Column(string field) => rows.Select(r => r[F(field)]).ToArray();
        double Max(string field) => Column(field).Where(v => !double.IsNaN(v)).Select(Math.Abs).DefaultIfEmpty(0).Max();

        // Position round the lap, 0-1.
        double[] pct = col.ContainsKey("pct") ? Column("pct") : Column("dist").Select(d => d / trackM).ToArray();
        if (col.ContainsKey("pct") && pct.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max() > 1.5) pct = pct.Select(v => v / 100).ToArray();

        // Speed to m/s: the header's unit if it says, else whichever unit makes the distance driven match the track.
        double[] speed = Column("speed");
        double[]? time = col.ContainsKey("time") ? Column("time") : null;
        string su = unit["speed"];
        double factor = su.Contains("km") || su.Contains("kph") ? 1 / 3.6 : su.Contains("mph") ? 0.44704 : su.Contains("m/s") ? 1 : double.NaN;
        if (double.IsNaN(factor))
        {
            if (time != null)
            {
                double driven = 0;
                for (int i = 1; i < rows.Count; i++)
                    if (!double.IsNaN(time[i]) && !double.IsNaN(time[i - 1]) && time[i] > time[i - 1] && time[i] - time[i - 1] < 1) driven += speed[i] * (time[i] - time[i - 1]);
                factor = new[] { 1, 1 / 3.6, 0.44704 }.MinBy(f => Math.Abs(driven * f - trackM));
            }
            else factor = Max("speed") > 110 ? 1 / 3.6 : 1;
        }
        speed = speed.Select(v => v * factor).ToArray();

        // No time column: integrate it from distance and speed.
        if (time == null)
        {
            time = new double[rows.Count];
            for (int i = 1; i < rows.Count; i++)
            {
                double ds = (pct[i] - pct[i - 1]) * trackM;
                if (ds < 0) ds += trackM;
                double v = Math.Max(1, (speed[i] + speed[i - 1]) / 2);
                time[i] = time[i - 1] + (double.IsNaN(ds) ? 0 : ds / v);
            }
        }

        double pedal(string f) => Max(f) > 1.5 ? 0.01 : 1;
        double brakeK = pedal("brake"), throttleK = pedal("throttle");
        double steerK = Max("steer") > 12 ? Math.PI / 180 : 1;           // degrees to radians
        double latK = Max("lat") is var lm && lm > 0 && lm < 5 ? 9.81 : 1; // g to m/s²
        double longK = Max("long") is var gm && gm > 0 && gm < 5 ? 9.81 : 1;
        double yawK = Max("yaw") > 6 ? Math.PI / 180 : 1;                // deg/s to rad/s

        // Start at the line: skip samples from the end of the previous lap.
        int start = 0;
        while (start < rows.Count - 1 && (double.IsNaN(pct[start]) || pct[start] > 0.5)) start++;
        double t0 = time[start];
        var trace = new Trace(bins, binM);
        double lastPct = -1, end = t0;
        for (int i = start; i < rows.Count; i++)
        {
            if (double.IsNaN(pct[i])) continue;
            if (lastPct > 0.9 && pct[i] < 0.1) break; // into the next lap
            lastPct = pct[i];
            end = time[i];
            var r = rows[i];
            trace.Add(pct[i], time[i] - t0, speed[i], r[F("brake")] * brakeK, r[F("throttle")] * throttleK, r[F("gear")], r[F("rpm")],
                r[F("steer")] * steerK, r[F("lat")] * latK, r[F("long")] * longK, r[F("yaw")] * yawK, r[F("abs")] > 0.5);
        }
        if (trace.Coverage(0, bins - 1) < 0.9) throw new FormatException("doesn't cover a whole lap");
        return new ReferenceLap { Name = Path.GetFileNameWithoutExtension(path), LapTime = end - t0, Trace = trace };
    }
}
