using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// The voice coach. Follows the car (feed it every telemetry frame), learns the corners from the first clean lap, and
/// measures every corner as you leave it. "How was that corner?" gets a technique answer; that corner then becomes
/// the focus: about 5 seconds before its braking point next lap you hear the reminder once, and after it how it went.
/// Corners and your best pass through each are remembered per car and track (coach\*.json).
/// </summary>
public sealed class Coach
{
    const double BinM = 5, ReminderSeconds = 5;
    readonly string _dir;
    readonly object _gate = new();

    string _key = "";
    double _trackM;
    Trace? _trace;
    List<Corner> _corners = new();
    readonly Dictionary<int, Pass> _best = new();
    readonly List<Pass> _passes = new();
    double _bestLap = double.NaN, _lastLapTime = double.NaN, _cornersLapTime = double.MaxValue;

    /// <summary>A clean lap finished: lap number, time, corners known (for replaying recordings).</summary>
    public event Action<int, double, int>? LapDone;
    double _maxBrake = 0.8, _maxRpm;
    bool _metric = true;

    double _lastPct = -1, _lapStart = double.NaN;
    bool _lapClean;
    int _lap, _next;
    int _sessionInfoVersion = -1;

    // Focus: one corner, one reminder, one follow-up.
    (Corner Corner, Pass Before, Issue Issue)? _focus;
    bool _reminded;

    /// <summary>Things the coach says on its own (the reminder and the follow-up).</summary>
    public event Action<string>? Say;

    public Coach(string dataDir, string? referenceDir = null) { _dir = dataDir; _referenceDir = referenceDir; }

    TrackMap? _map;

    static string Name(Corner c) => c.Label.Length > 0 ? c.Label : $"Turn {c.Number}";

    /// <summary>Short name for lists ("Turn 14" rather than "Turn 14, Forrests Elbow").</summary>
    string Short(int number) => _corners.FirstOrDefault(c => c.Number == number) is { } c
        ? (Name(c).Contains(", part") ? Name(c) : Name(c).Split(',')[0])
        : $"turn {number}";

    bool IsTurn(Corner c, int turn) => c.Turns == null ? c.Number == turn : c.Turns.Contains(turn);

    public void OnFrame(ITelemetrySource t)
    {
        lock (_gate)
        {
            if (t.SessionInfoVersion != _sessionInfoVersion) { _sessionInfoVersion = t.SessionInfoVersion; NewSession(t.SessionInfo); }
            if (_trackM <= 0) return;
            _metric = !t.Has("DisplayUnits") || t.Get("DisplayUnits") > 0;

            double pct = t.Get("LapDistPct"), now = t.Get("SessionTime"), speed = t.Get("Speed");
            if (double.IsNaN(pct) || pct < 0 || double.IsNaN(now)) return;
            bool clean = t.Get("IsOnTrack") > 0 && t.Get("OnPitRoad") <= 0;

            if (_lastPct < 0 || _trace == null) StartLap(now, (int)t.Get("Lap"), clean && pct < 0.02);
            else if (_lastPct > 0.9 && pct < 0.1) FinishLap(now, (int)t.Get("Lap"), clean);
            else if (Math.Abs(pct - _lastPct) > 0.02) { _lapClean = false; _trace = NewTrace(); _next = NextCorner(pct); } // reset / tow
            _lastPct = pct;
            if (!clean) _lapClean = false;

            double brake = t.Get("Brake"), rpm = t.Get("RPM");
            if (clean && brake > _maxBrake) _maxBrake = Math.Min(1, brake);
            if (clean && rpm > _maxRpm) _maxRpm = rpm;
            _trace!.Add(pct, now - _lapStart, speed, brake, t.Get("Throttle"), t.Get("Gear"), rpm,
                t.Get("SteeringWheelAngle"), t.Get("LatAccel"), t.Get("LongAccel"), t.Get("YawRate"), t.Get("BrakeABSactive") > 0);

            int bin = _trace.BinOf(pct);
            while (_next < _corners.Count && bin > _corners[_next].End + 1) Measured(_corners[_next++]);
            Remind(bin, speed);
        }
    }

    void NewSession(string sessionInfo)
    {
        var facts = SessionFacts.Parse(sessionInfo);
        if (facts.Key == _key || facts.TrackM <= 0) return;
        _key = facts.Key;
        _trackM = facts.TrackM;
        _corners = new(); _best.Clear(); _passes.Clear(); _bestLap = double.NaN; _cornersLapTime = double.MaxValue; _focus = null; _lastPct = -1;
        Load();
        // With Crew Chief's map the corners are known before the first lap, and carry the real turn numbers and names.
        _map = TrackMap.For(facts.TrackName, _trackM);
        if (_map != null)
        {
            var t = NewTrace();
            _corners = _corners.Count == 0 ? _map.Corners(t.N, t.BinM) : _map.Label(_corners, t.BinM);
        }
        LoadReference();
        NewTrack?.Invoke(facts, ReferenceFolder);
    }

    // ---- reference lap: a faster lap (Garage 61 export or a recording) to compare against

    readonly string? _referenceDir;
    ReferenceLap? _ref;
    readonly Dictionary<int, Pass> _refPass = new();

    /// <summary>A new car or track: its facts and the folder its reference laps go in (to download one).</summary>
    public event Action<SessionFacts, string>? NewTrack;

    string ReferenceFolder => _referenceDir == null ? "" : Path.Combine(_referenceDir, _key);

    /// <summary>Reads the reference laps again (after one is added or downloaded).</summary>
    public void ReloadReference() { lock (_gate) LoadReference(); }

    void LoadReference()
    {
        _ref = null;
        _refPass.Clear();
        if (_referenceDir == null || _trackM <= 0) return;
        ReferenceLap.SortInbox(_referenceDir);
        var t = NewTrace();
        _ref = ReferenceLap.Best(ReferenceFolder, t.N, t.BinM, _trackM);
        if (_ref != null) Console.WriteLine($"  [coach] Reference lap: {_ref.Name} ({(int)(_ref.LapTime / 60)}:{_ref.LapTime % 60:00.000}).");
        MeasureReference();
    }

    /// <summary>The reference lap through each corner (again whenever the corners change).</summary>
    void MeasureReference()
    {
        _refPass.Clear();
        if (_ref == null) return;
        foreach (var c in _corners) if (Measure.Corner(_ref.Trace, c, 0) is { } p) _refPass[c.Number] = p;
    }

    Trace NewTrace() => new((int)Math.Round(_trackM / BinM), _trackM / Math.Round(_trackM / BinM));

    int NextCorner(double pct)
    {
        if (_trace == null) return 0;
        int b = _trace.BinOf(pct);
        int i = _corners.FindIndex(c => c.End >= b);
        return i < 0 ? _corners.Count : i;
    }

    void StartLap(double now, int lap, bool fromLine)
    {
        _trace = NewTrace();
        _lapStart = now;
        _lapClean = fromLine;
        _lap = lap;
        _next = fromLine ? 0 : NextCorner(_lastPct < 0 ? 0 : _lastPct);
    }

    void FinishLap(double now, int lap, bool clean)
    {
        while (_next < _corners.Count) Measured(_corners[_next++]);
        if (_lapClean && clean && _trace != null)
        {
            double time = now - _lapStart;
            _lastLapTime = time;
            // Learn the track from the first clean lap, and again if one is much faster (the first was an out lap or
            // formation lap, so its corners and braking points aren't how the track is really driven).
            // Corners from the map are only rough (no braking points yet), so the first clean lap replaces them too.
            if (_corners.Count == 0 || (time < 0.95 * _cornersLapTime && _focus == null))
            {
                _corners = Corners.Find(_trace);
                if (_map != null) _corners = _map.Label(_corners, _trace.BinM);
                _cornersLapTime = time;
                _best.Clear();
                _passes.Clear();
                foreach (var c in _corners) if (Measure.Corner(_trace, c, _lap) is { } p) Record(p);
                MeasureReference();
            }
            LapDone?.Invoke(_lap, time, _corners.Count);
            if (double.IsNaN(_bestLap) || time < _bestLap) _bestLap = time;
            Save();
        }
        else _lastLapTime = double.NaN;
        StartLap(now, lap, clean);
    }

    void Measured(Corner c)
    {
        if (_trace == null || Measure.Corner(_trace, c, _lap) is not { } p) return;
        PassMeasured?.Invoke(Name(c), p, _best.GetValueOrDefault(c.Number), Cx);
        Record(p);
        if (_focus is { } f && f.Corner.Number == c.Number && _reminded)
        {
            Say?.Invoke(Diagnosis.FollowUp(Name(c), f.Before, p, f.Issue));
            _focus = null;
        }
    }

    /// <summary>Every corner as it's measured, with the best pass before it (for replaying recordings).</summary>
    public event Action<string, Pass, Pass?, Diagnosis.Context>? PassMeasured;

    void Record(Pass p)
    {
        _passes.Add(p);
        if (_passes.Count > 2000) _passes.RemoveRange(0, 500);
        // Only a clean lap sets a best (with a track map, corners are measured on the out lap too, pit lane and all).
        if (_lapClean && (!_best.TryGetValue(p.Corner, out var b) || p.Seconds < b.Seconds)) _best[p.Corner] = p;
    }

    void Remind(int bin, double speed)
    {
        if (_focus is not { } f || _reminded || _trace == null || speed < 5) return;
        int ahead = (f.Corner.BrakeBin - bin + _trace.N) % _trace.N;
        double seconds = ahead * _trace.BinM / speed;
        if (seconds <= ReminderSeconds && ahead < _trace.N / 2)
        {
            _reminded = true;
            Say?.Invoke($"{Name(f.Corner)} coming: {f.Issue.Reminder}.");
        }
    }

    Diagnosis.Context Cx => new(_metric, _maxBrake, _maxRpm);

    // ---- questions

    /// <summary>"How was that corner?" - the corner just driven; it becomes the focus for next lap.</summary>
    public string LastCorner()
    {
        lock (_gate)
        {
            if (_corners.Count == 0) return "I'm still learning the track. Give me one clean lap.";
            var p = _passes.LastOrDefault();
            if (p == null) return "I haven't measured a corner yet.";
            return AboutCorner(p);
        }
    }

    /// <summary>"How was turn 7?"</summary>
    public string Corner(int number)
    {
        lock (_gate)
        {
            var numbers = _corners.Where(c => IsTurn(c, number)).Select(c => c.Number).ToHashSet();
            var p = _passes.LastOrDefault(x => numbers.Contains(x.Corner));
            return p == null ? $"I haven't got turn {number} yet." : AboutCorner(p);
        }
    }

    /// <summary>"How was Eau Rouge?" - a corner by its name on the track map; null when no corner name is in the words.</summary>
    public string? CornerNamed(string said)
    {
        lock (_gate)
        {
            if (TrackMap.Named(_corners, said) is not { } c) return null;
            var p = _passes.LastOrDefault(x => x.Corner == c.Number);
            return p == null ? $"I haven't got {Name(c).Split(',').Last().Trim()} yet." : AboutCorner(p);
        }
    }

    string AboutCorner(Pass p)
    {
        var c = _corners.First(x => x.Number == p.Corner);
        var best = _best.GetValueOrDefault(p.Corner);
        var issues = Diagnosis.Find(p, best, Cx);
        if (issues.Count > 0) { _focus = (c, p, issues[0]); _reminded = false; }
        else _focus = null;
        string vsRef = _refPass.TryGetValue(c.Number, out var r) ? " " + Diagnosis.VsReference(p, r, Cx) : "";
        return Diagnosis.Report(Name(c), p, best, issues) + vsRef;
    }

    /// <summary>"What's the reference lap?"</summary>
    public string ReferenceInfo()
    {
        lock (_gate)
        {
            if (_ref == null) return "There's no reference lap for this car and track. Add one in the command center, under Reference laps.";
            string time = $"{(int)(_ref.LapTime / 60)}:{_ref.LapTime % 60:00.0}";
            string vs = double.IsNaN(_bestLap) ? "" : $" Your best is {Units.Delta(_bestLap - _ref.LapTime, "than the reference").Replace("the same as the reference", "level with it")}.";
            return $"The reference lap is {_ref.Name}, a {time}.{vs}";
        }
    }

    /// <summary>"How was that lap?"</summary>
    public string LastLap()
    {
        lock (_gate)
        {
            if (double.IsNaN(_lastLapTime)) return "That lap wasn't clean, so I didn't time it.";
            string time = $"{(int)(_lastLapTime / 60)}:{_lastLapTime % 60:00.0}";
            string vsBest = Math.Abs(_lastLapTime - _bestLap) < 0.05 ? "your best this session" : Units.Delta(_lastLapTime - _bestLap);
            var worst = Losses(_passes.Where(p => p.Lap == _lap - 1)).Take(2).ToList();
            string vsRef = _ref == null ? "" : $" {Capital(Units.Delta(_lastLapTime - _ref.LapTime, "than the reference lap"))}.";
            return $"{time}, {vsBest}.{vsRef}" + (worst.Count > 0 ? " Most time went at " + string.Join(" and ", worst.Select(w => $"{Short(w.Corner)}, {Units.Delta(w.Lost).Replace(" than your best", "")}")) + "." : "");
        }
    }

    /// <summary>"Where am I losing time?" - the corners furthest off your best over the last three laps.</summary>
    public string WhereLosing()
    {
        lock (_gate)
        {
            if (_passes.Count == 0) return "I haven't got enough laps yet.";
            int lastLap = _passes[^1].Lap;
            var worst = Losses(_passes.Where(p => p.Lap > lastLap - 3)).Take(3).ToList();
            // Against the reference lap: the latest pass through each corner.
            var toRef = _passes.Where(p => p.Lap > lastLap - 3).GroupBy(p => p.Corner).Select(g => g.Last())
                .Where(p => _refPass.ContainsKey(p.Corner)).Select(p => (p.Corner, Lost: p.Seconds - _refPass[p.Corner].Seconds))
                .Where(x => x.Lost > 0.1).OrderByDescending(x => x.Lost).Take(3).ToList();
            string refPart = toRef.Count == 0 ? "" : " Against the reference lap: " +
                string.Join(", ", toRef.Select(x => $"{Short(x.Corner)} {Units.Delta(x.Lost, "").Trim().Replace(" slower", "")}")) + ".";
            if (worst.Count == 0) return "You're matching your best everywhere." + refPart;
            var first = _corners.First(c => c.Number == worst[0].Corner);
            var pass = _passes.Last(p => p.Corner == first.Number);
            var issue = Diagnosis.Find(pass, _best.GetValueOrDefault(first.Number), Cx).FirstOrDefault();
            if (issue != null) { _focus = (first, pass, issue); _reminded = false; }
            return "Most time against your best: " + string.Join(", ", worst.Select(w => $"{Short(w.Corner)} about {Units.Delta(w.Lost).Replace(" slower than your best", "")}")) +
                   "." + refPart + (issue != null ? $" At {Short(first.Number)}: {issue.What} {issue.Tip}" : "");
        }
    }

    /// <summary>
    /// "What should I work on?" - the habit, not a corner (Almeida: drivers repeat the same automatic behaviours in
    /// similar corners). The most common issue over the last lap, where it showed up, how to fix it, and the corner where
    /// it was worst becomes the focus for the reminder.
    /// </summary>
    public string Habit()
    {
        lock (_gate)
        {
            if (_passes.Count == 0) return "I need a lap first.";
            int lastLap = _passes[^1].Lap;
            var recent = _passes.Where(p => p.Lap >= lastLap - 1).GroupBy(p => p.Corner).Select(g => g.Last()).ToList();
            var found = recent.Select(p => (Pass: p, Issues: Diagnosis.Find(p, _best.GetValueOrDefault(p.Corner), Cx)))
                .SelectMany(x => x.Issues.Take(2).Select(i => (x.Pass, Issue: i))).ToList();
            if (found.Count == 0) return "Nothing stands out. Clean driving.";
            var habit = found.GroupBy(f => f.Issue.Key).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Sum(f => f.Issue.Score)).First();
            var worst = habit.OrderByDescending(f => f.Issue.Score).First();
            var corner = _corners.First(c => c.Number == worst.Pass.Corner);
            _focus = (corner, worst.Pass, worst.Issue);
            _reminded = false;
            var where = habit.Select(f => f.Pass.Corner).OrderBy(n => n).Take(4).Select(Short).ToList();
            return $"Your most common habit, in {habit.Count()} of {recent.Count} corners, {string.Join(", ", where)}{(habit.Count() > 4 ? " and more" : "")}: " +
                   $"{Lower(worst.Issue.What)} {worst.Issue.Tip} I'll remind you at {Short(corner.Number)}, where it cost the most.";
        }
    }

    static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;
    static string Capital(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    IEnumerable<(int Corner, double Lost)> Losses(IEnumerable<Pass> passes) =>
        passes.GroupBy(p => p.Corner)
            .Select(g => (Corner: g.Key, Lost: g.Average(p => p.Seconds) - (_best.TryGetValue(g.Key, out var b) ? b.Seconds : g.Min(p => p.Seconds))))
            .Where(x => x.Lost > 0.05)
            .OrderByDescending(x => x.Lost);

    /// <summary>"That's enough" - drop the focus.</summary>
    public string Stop()
    {
        lock (_gate) { _focus = null; return "Okay."; }
    }

    // ---- remembered per car and track

    sealed record Saved(double TrackM, double BestLap, double CornersLapTime, List<Corner> Corners, List<Pass> Best);

    string FilePath => Path.Combine(_dir, _key + ".json");

    void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved(_trackM, _bestLap, _cornersLapTime, _corners, _best.Values.OrderBy(p => p.Corner).ToList()),
                new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        }
        catch (IOException) { /* file busy (OneDrive): next lap saves again */ }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath),
                new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
            if (s == null || Math.Abs(s.TrackM - _trackM) > 1) return;
            _corners = s.Corners;
            _bestLap = s.BestLap;
            _cornersLapTime = s.CornersLapTime > 0 ? s.CornersLapTime : double.MaxValue;
            foreach (var p in s.Best) _best[p.Corner] = p;
        }
        catch (JsonException) { /* unreadable: start learning again */ }
    }
}
