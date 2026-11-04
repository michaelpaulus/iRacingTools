using System.Globalization;

/// <summary>The per-car telemetry the standings need, copied out of one SDK frame.</summary>
public sealed class CarFrame
{
    public const int MaxCars = 64;
    public int SessionNum, SessionState;
    public double SessionTime, SessionTimeRemain = double.NaN;
    public int SessionLapsRemain = -1, PlayerIncidents;
    /// <summary>You're in the car with physics running (not in the garage, spectating or watching a replay).</summary>
    public bool InCar;
    public readonly int[] Position = new int[MaxCars], ClassPosition = new int[MaxCars], LapCompleted = new int[MaxCars], TrackSurface = new int[MaxCars];
    /// <summary>Index into DriverTires of the compound each car is on; -1 when iRacing doesn't know.</summary>
    public readonly int[] TireCompound = new int[MaxCars];
    public readonly float[] LapDistPct = new float[MaxCars], EstTime = new float[MaxCars], BestLap = new float[MaxCars], LastLap = new float[MaxCars];
    public readonly bool[] OnPitRoad = new bool[MaxCars];

    public const int StateParadeLaps = 3, StateRacing = 4, SurfaceNotInWorld = -1, SurfaceOffTrack = 0;

    public static CarFrame Read(ITelemetrySource t)
    {
        var f = new CarFrame
        {
            SessionNum = (int)t.Get("SessionNum"),
            SessionState = (int)t.Get("SessionState"),
            SessionTime = t.Get("SessionTime"),
            SessionTimeRemain = t.Get("SessionTimeRemain"),
            SessionLapsRemain = t.Has("SessionLapsRemainEx") ? (int)t.Get("SessionLapsRemainEx") : -1,
            PlayerIncidents = (int)t.Get("PlayerCarMyIncidentCount"),
            InCar = t.Get("IsOnTrack") > 0,
        };
        bool hasTires = t.Has("CarIdxTireCompound");
        for (int i = 0; i < MaxCars; i++)
        {
            f.TireCompound[i] = hasTires ? (int)t.Get("CarIdxTireCompound", i) : -1;
            f.Position[i] = (int)t.Get("CarIdxPosition", i);
            f.ClassPosition[i] = (int)t.Get("CarIdxClassPosition", i);
            f.LapCompleted[i] = (int)t.Get("CarIdxLapCompleted", i);
            f.TrackSurface[i] = (int)t.Get("CarIdxTrackSurface", i);
            f.LapDistPct[i] = (float)t.Get("CarIdxLapDistPct", i);
            f.EstTime[i] = (float)t.Get("CarIdxEstTime", i);
            f.BestLap[i] = (float)t.Get("CarIdxBestLapTime", i);
            f.LastLap[i] = (float)t.Get("CarIdxLastLapTime", i);
            f.OnPitRoad[i] = t.Get("CarIdxOnPitRoad", i) > 0;
        }
        return f;
    }
}

/// <summary>How a last lap compares: fastest in the class this session (purple), the driver's own best (green), or neither.</summary>
public enum LapMark { None, PersonalBest, ClassBest }

/// <param name="GapHighlight">The car directly ahead of or behind you: its gap is drawn brighter than the rest.</param>
public sealed record StandingsRow(
    DriverEntry Driver, int ClassPosition, string Country, int Laps, string Gap,
    int? IRatingDelta, bool IsPlayer, bool InPit, bool RowsSkippedBefore,
    string LastLap = "", LapMark LastLapMark = LapMark.None, bool GapHighlight = false,
    string Tyre = "", string TyreLabel = "");

/// <summary>How a tyre compound is shown: a short label, and the compound's name for its colour.</summary>
public static class Tyres
{
    public static (string Name, string Label) Describe(IReadOnlyDictionary<int, string> compounds, int index)
    {
        if (index < 0) return ("", "");
        if (!compounds.TryGetValue(index, out var name)) return ("", index.ToString(CultureInfo.InvariantCulture));
        // One dry compound (GT3, GT4, ...): D / W; with several, the compound's letter (S / M / H, I / W).
        int dry = compounds.Values.Count(c => !IsWet(c));
        if (IsWet(name)) return (name, name.StartsWith("Inter", StringComparison.OrdinalIgnoreCase) ? "I" : "W");
        return (name, dry <= 1 ? "D" : name[..1].ToUpperInvariant());
    }

    public static bool IsWet(string name) =>
        name.Contains("Wet", StringComparison.OrdinalIgnoreCase) || name.Contains("Rain", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Inter", StringComparison.OrdinalIgnoreCase);
}

public sealed record ClassBlock(string Name, int Color, int Sof, int Cars, IReadOnlyList<StandingsRow> Rows);

/// <summary>Everything the standings overlay draws for one frame. Immutable, so the UI thread can read it at any time.</summary>
public sealed record StandingsSnapshot(
    string Track, string Session, string Progress, int Sof, IReadOnlyList<ClassBlock> Classes,
    string? PlayerSr, double? PlayerSrDelta, int PlayerIncidents)
{
    public static readonly StandingsSnapshot Waiting =
        new("Waiting for iRacing", "", "", 0, Array.Empty<ClassBlock>(), null, null, 0);
}

public static class Standings
{
    /// <param name="t">Tracker fed every frame; without one, gaps fall back to iRacing's lap estimates.</param>
    public static StandingsSnapshot Build(SessionData s, CarFrame f, StandingsConfig cfg, RaceTracker? t = null)
    {
        t ??= RaceTracker.From(f, s.PlayerCarIdx);
        s.Sessions.TryGetValue(f.SessionNum, out var session);
        string type = session?.Type ?? "";
        bool isRace = type.Equals("Race", StringComparison.OrdinalIgnoreCase);
        double srWeight = s.Official ? SafetyRatingEstimate.SessionWeight(type) : 0;
        bool raceUnderway = isRace && f.SessionState >= CarFrame.StateRacing;
        // While racing, order by where cars are right now; from the chequered flag on, iRacing's finishing order.
        // Practice and warmup work the same way, so passing a car on track moves you up (qualifying keeps the times).
        bool practice = type.Contains("Practice", StringComparison.OrdinalIgnoreCase) || type.Contains("Warmup", StringComparison.OrdinalIgnoreCase);
        bool liveOrder = (isRace || practice) && f.SessionState == CarFrame.StateRacing;
        // Gridding and the formation lap: order and gaps follow the cars on track too, like the race.
        bool formation = isRace && f.SessionState == CarFrame.StateParadeLaps;

        var racers = s.Drivers.Where(d => !d.IsSpectator && !d.IsPaceCar && d.CarIdx < CarFrame.MaxCars && d.UserId != -1).ToList();
        var player = racers.FirstOrDefault(d => d.CarIdx == s.PlayerCarIdx);

        var byClass = racers.GroupBy(d => d.ClassId)
            .OrderBy(g => g.Key == player?.ClassId ? 0 : 1) // your class first, then the fastest classes
            .ThenBy(g => g.Select(d => d.ClassEstLapTime).Where(t => t > 0).DefaultIfEmpty(double.MaxValue).Min());

        string? srText = null;
        double? srDelta = null;
        if (player != null && srWeight <= 0)
        {
            // Hosted / offline sessions don't change SR: still show where you stand.
            srText = $"{player.LicenseLetter} {player.SafetyRating:0.00}  ·  {(s.Official ? type.ToLowerInvariant() : "unofficial session")} doesn't count";
        }
        else if (player != null)
        {
            // Counted up to the chequered flag, and kept once you're back in the pits.
            int idx = player.CarIdx;
            double laps = t.PlayerFinishedLaps ?? t.Laps(idx) + Math.Max(0, f.LapDistPct[idx]);
            double corners = laps * SafetyRatingEstimate.CornersPerLap(s);
            srDelta = SafetyRatingEstimate.Change(corners, t.Incidents, player.SafetyRating, player.LicenseLetter, srWeight, cfg);
            srText = $"{player.LicenseLetter} {player.SafetyRating:0.00} → {player.SafetyRating + srDelta.Value:0.00}";
        }

        // Racing: furthest round first (cars that left the world keep where they were last seen).
        // Otherwise iRacing's last official position, then the grid, then iRating.
        double Distance(DriverEntry d) => double.IsNaN(t.Distance(d.CarIdx)) ? double.MinValue : t.Distance(d.CarIdx);
        int Official(DriverEntry d) => t.LastPosition(d.CarIdx) > 0 ? t.LastPosition(d.CarIdx) : int.MaxValue;
        int Overall(DriverEntry d) => t.LastOverallPosition(d.CarIdx) > 0 ? t.LastOverallPosition(d.CarIdx) : int.MaxValue;
        int Grid(DriverEntry d) => isRace && s.Grid.TryGetValue(d.CarIdx, out var g) ? g : int.MaxValue;

        // On the formation lap the lap counters tick over at different moments as cars cross the line, so order by where
        // each car is relative to the front of the grid, the short way round (the field is well inside half a lap).
        bool OffTrack(DriverEntry d) => f.LapDistPct[d.CarIdx] < 0 || f.OnPitRoad[d.CarIdx];
        int front = formation
            ? racers.Where(d => !OffTrack(d)).OrderBy(Grid).ThenBy(Official).Select(d => d.CarIdx).DefaultIfEmpty(-1).First()
            : -1;
        double FormationUp(DriverEntry d)
        {
            if (front < 0 || OffTrack(d)) return double.MinValue;
            double up = f.LapDistPct[d.CarIdx] - f.LapDistPct[front];
            return up - Math.Round(up);
        }
        IOrderedEnumerable<DriverEntry> FormationOrder(IEnumerable<DriverEntry> cars, Func<DriverEntry, int> tie) =>
            cars.OrderBy(OffTrack).ThenByDescending(FormationUp).ThenBy(tie);

        // Positions, iRating changes and fastest laps are always per class (that's how iRacing scores a multiclass race).
        var inClass = new Dictionary<int, (int Position, int? Delta, float Best)>();
        var classOrders = new List<(IGrouping<int, DriverEntry> Group, List<DriverEntry> Cars)>();
        foreach (var group in byClass)
        {
            var ordered = (formation ? FormationOrder(group, Official)
                    : liveOrder ? group.OrderByDescending(Distance).ThenBy(Official) : group.OrderBy(Official))
                .ThenBy(Grid)
                .ThenByDescending(d => d.IRating)
                .ToList();

            int[]? deltas = null;
            if (isRace && ordered.Count >= 2)
            {
                var started = ordered.Select(d => !(raceUnderway && t.Laps(d.CarIdx) <= 0 && f.TrackSurface[d.CarIdx] == CarFrame.SurfaceNotInWorld)).ToList();
                // Non-starters go to the back, keeping everyone else's order.
                var order = Enumerable.Range(0, ordered.Count).OrderBy(i => started[i] ? 0 : 1).ThenBy(i => i).ToList();
                var positions = new int[ordered.Count];
                for (int p = 0; p < order.Count; p++) positions[order[p]] = p + 1;
                var changes = IRatingMath.Changes(ordered.Select(d => d.IRating).ToList(), positions, started);
                deltas = changes.Select(c => (int)Math.Round(c)).ToArray();
            }

            float best = ordered.Select(d => f.BestLap[d.CarIdx]).Where(t => t > 0).DefaultIfEmpty(0).Min();
            for (int i = 0; i < ordered.Count; i++)
            {
                var d = ordered[i];
                inClass[d.CarIdx] = (liveOrder || formation || t.LastPosition(d.CarIdx) <= 0 ? i + 1 : t.LastPosition(d.CarIdx), deltas?[i], best);
            }
            classOrders.Add((group, ordered));
        }

        // Around you on track, like iRacing's relative: the cars physically ahead above you (lapped or not), those
        // behind below. Laps mean nothing in practice, so there it's the default.
        // In a race also while you're in trouble: crashed, spun or off the track, the standings gap (time you're losing)
        // isn't what you need; how soon each car reaches you is.
        bool trouble = player != null && isRace && f.SessionState == CarFrame.StateRacing && cfg.RelativeWhenInTrouble &&
            t.PlayerInTrouble(InTrouble(player.CarIdx), holdSeconds: 3);
        bool aroundMe = player != null && f.LapDistPct[player.CarIdx] >= 0 && f.SessionState == CarFrame.StateRacing &&
            (trouble || cfg.OnTrackOrder.Equals("Always", StringComparison.OrdinalIgnoreCase) ||
             practice && cfg.OnTrackOrder.Equals("Practice", StringComparison.OrdinalIgnoreCase));

        bool InTrouble(int me)
        {
            if (f.LapDistPct[me] < 0 || f.OnPitRoad[me]) return false;
            if (f.TrackSurface[me] == CarFrame.SurfaceOffTrack) return true;
            // Far slower than the cars around you (a standing start or a safety car slows everyone, so that doesn't count).
            var paces = racers.Where(d => d.CarIdx != me && f.LapDistPct[d.CarIdx] >= 0 && !f.OnPitRoad[d.CarIdx])
                .Select(d => t.RecentPace(d.CarIdx)).Where(p => !double.IsNaN(p)).Order().ToList();
            if (paces.Count == 0) return false;
            double field = paces[paces.Count / 2], mine = t.RecentPace(me);
            return double.IsNaN(mine) ? !t.Moving(me) : mine > 2.5 * field;
        }
        double Up(DriverEntry d)
        {
            double up = f.LapDistPct[d.CarIdx] - f.LapDistPct[player!.CarIdx];
            return up - Math.Round(up); // > 0: up the road, the short way round
        }
        List<DriverEntry> OnTrack(IEnumerable<DriverEntry> cars) =>
            cars.Where(d => f.LapDistPct[d.CarIdx] >= 0).OrderByDescending(Up).ThenBy(Overall).ToList();

        // One list of every car in the order they run on track, whatever the class, or a block per class.
        var blocks = aroundMe
            // In trouble every class matters: a faster class bearing down on you most of all.
            ? [(Cars: OnTrack(cfg.ByClass && !trouble ? racers.Where(d => d.ClassId == player!.ClassId) : racers), Mine: true,
                Name: cfg.ByClass && !trouble ? player!.ClassName : "", Color: player!.ClassColor,
                Sof: IRatingMath.StrengthOfField(racers.Where(d => d.ClassId == player.ClassId).Select(d => d.IRating)))]
            : cfg.ByClass
            ? classOrders.Select(c => (Cars: c.Cars, Mine: player == null || c.Group.Key == player.ClassId,
                Name: c.Group.First().ClassName, Color: c.Group.First().ClassColor, Sof: IRatingMath.StrengthOfField(c.Group.Select(d => d.IRating)))).ToList()
            : [(Cars: (formation ? FormationOrder(racers, Overall)
                    : liveOrder ? racers.OrderByDescending(Distance).ThenBy(Overall) : racers.OrderBy(Overall))
                    .ThenBy(d => inClass[d.CarIdx].Position).ThenBy(Grid).ThenByDescending(d => d.IRating).ToList(),
                Mine: true, Name: "", Color: player?.ClassColor ?? 0,
                Sof: IRatingMath.StrengthOfField(racers.Where(d => player == null || d.ClassId == player.ClassId).Select(d => d.IRating)))];

        var classes = new List<ClassBlock>();
        foreach (var block in blocks)
        {
            var ordered = block.Cars;
            if (ordered.Count == 0) continue;
            // Other classes always measure to their own leader: a gap to you means little across classes.
            bool myClass = block.Mine;
            bool toLeader = !aroundMe && (cfg.GapTo.Equals("Leader", StringComparison.OrdinalIgnoreCase) || player == null || !myClass);
            var reference = toLeader ? ordered[0] : player!;
            double refLapTime = reference.ClassEstLapTime > 0 ? reference.ClassEstLapTime : f.BestLap[reference.CarIdx];

            string Gap(DriverEntry d)
            {
                if (d.CarIdx == reference.CarIdx) return "";
                // On the grid / formation lap before the green, in qualifying, and when ordered around you, the live gap
                // on track is what matters; otherwise practice is timed like the race, to match its order.
                if (aroundMe || !raceUnderway && !(practice && liveOrder)) return OnTrackGap(d.CarIdx);
                double behind = t.Distance(reference.CarIdx) - t.Distance(d.CarIdx); // laps; positive = behind the reference
                if (double.IsNaN(behind)) return "";
                if (Math.Abs(behind) >= 1)
                {
                    int laps = (int)Math.Truncate(behind);
                    return (laps > 0 ? "+" : "-") + Math.Abs(laps) + "L";
                }
                if (refLapTime <= 0) return "";

                // Interval timing: how long ago the car in front was where the car behind is now.
                float pctRef = f.LapDistPct[reference.CarIdx], pctCar = f.LapDistPct[d.CarIdx];
                double interval = behind >= 0
                    ? t.TimeSince(reference.CarIdx, pctCar, refLapTime * 1.2)
                    : t.TimeSince(d.CarIdx, pctRef, refLapTime * 1.2);
                if (!double.IsNaN(interval)) return (behind >= 0 ? interval : -interval).ToString("+0.0;-0.0", CultureInfo.InvariantCulture);

                // Not enough history yet (first lap after joining): estimate from iRacing's per-class lap model.
                double seconds = behind * refLapTime;
                float estRef = f.EstTime[reference.CarIdx], estCar = f.EstTime[d.CarIdx];
                if (estRef > 0 && estCar > 0)
                {
                    // EstTime (expected time from the line to each car) is more accurate than distance x lap time, but jumps
                    // by a lap at the line and is slightly noisy: take whichever lap-shift of it lands nearest the distance
                    // estimate, so two cars side by side never read as a lap apart.
                    double est = estRef - estCar;
                    est += Math.Round((seconds - est) / refLapTime) * refLapTime;
                    seconds = Math.Sign(est) == Math.Sign(behind) ? est : 0;
                }
                return seconds.ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
            }

            // Seconds apart on track the short way round, like iRacing's relative: negative = up the road from the reference.
            string OnTrackGap(int idx)
            {
                float pctRef = f.LapDistPct[reference.CarIdx], pctCar = f.LapDistPct[idx];
                if (pctRef < 0 || pctCar < 0 || refLapTime <= 0) return "";
                double up = pctCar - pctRef;
                up -= Math.Round(up); // > 0: the car is up the road
                var car = racers.FirstOrDefault(d => d.CarIdx == idx);
                bool sameClass = car == null || car.ClassId == reference.ClassId;

                // Moving: interval timing, measured not modelled, so it's right across classes and at formation-lap
                // speed: how long ago the car in front was where the one behind is now. Not while either is stopped (on
                // the grid, in the pits), where that would just keep counting up, nor when the car in front is crawling
                // (you, crashed, with a car coming up behind): that says how much time it's losing, not how soon the
                // car behind gets there.
                int lead = up > 0 ? idx : reference.CarIdx, follow = up > 0 ? reference.CarIdx : idx;
                double leadPace = t.RecentPace(lead), followPace = t.RecentPace(follow);
                bool comparable = double.IsNaN(leadPace) || double.IsNaN(followPace) || leadPace <= 1.5 * followPace;
                if ((aroundMe && f.SessionState == CarFrame.StateRacing || formation) && !f.OnPitRoad[idx] && !f.OnPitRoad[reference.CarIdx] &&
                    t.Moving(idx) && t.Moving(reference.CarIdx) && comparable)
                {
                    double interval = up > 0 ? t.TimeSince(idx, pctRef, refLapTime * 0.6) : t.TimeSince(reference.CarIdx, pctCar, refLapTime * 0.6);
                    if (!double.IsNaN(interval)) return (up > 0 ? -interval : interval).ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
                }

                // Otherwise from where the cars are, at the following car's own pace.
                double followLap = up > 0 ? refLapTime : car?.ClassEstLapTime > 0 ? car.ClassEstLapTime : refLapTime;
                double ahead = up * followLap;
                float estRef = f.EstTime[reference.CarIdx], estCar = f.EstTime[idx];
                // iRacing's estimate uses each car's own class model, so it only compares within a class.
                if (sameClass && estRef > 0 && estCar > 0)
                {
                    // iRacing's time estimate follows the track's slow and fast parts, but it's noisy for cars side by
                    // side (it can even put the car behind ahead): only use it when it agrees on who's in front.
                    double est = estCar - estRef;
                    est -= Math.Round(est / refLapTime) * refLapTime; // wrap into ±half a lap
                    if (Math.Sign(est) == Math.Sign(up)) ahead = est;
                }
                return (-ahead).ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
            }

            int me = ordered.FindIndex(d => d.CarIdx == s.PlayerCarIdx);
            var rows = ordered.Select((d, i) =>
            {
                var tyre = Tyres.Describe(s.TireCompounds, f.TireCompound[d.CarIdx]);
                return new StandingsRow(
                    // Always the position in the car's own class, even with every class in one list. In a race it's live
                    // (iRacing's own only changes at the line); in practice around you it's iRacing's (fastest lap), blank
                    // until it has one.
                    d, aroundMe && !isRace ? t.LastPosition(d.CarIdx) : inClass[d.CarIdx].Position,
                    Countries.Code(d.Flair), t.Laps(d.CarIdx), Gap(d), inClass[d.CarIdx].Delta,
                    d.CarIdx == s.PlayerCarIdx, f.OnPitRoad[d.CarIdx], false,
                    FormatLap(f.LastLap[d.CarIdx]), Mark(f.LastLap[d.CarIdx], f.BestLap[d.CarIdx], inClass[d.CarIdx].Best),
                    GapHighlight: me >= 0 && Math.Abs(i - me) == 1, Tyre: tyre.Name, TyreLabel: tyre.Label);
            }).ToList();

            rows = Window(rows, myClass ? cfg.MaxRows : cfg.OtherClassRows, aroundMe ? 0 : myClass ? cfg.LeaderRows : cfg.OtherClassRows);
            classes.Add(new ClassBlock(block.Name, block.Color, block.Sof, ordered.Count, rows));
        }

        var playerClass = classes.FirstOrDefault();
        return new StandingsSnapshot(s.TrackName, session?.Name is { Length: > 0 } n ? n : type,
            Progress(session, f, t, racers), playerClass?.Sof ?? 0, classes, srText, srDelta, t.Incidents);
    }

    static string Progress(SessionDef? session, CarFrame f, RaceTracker t, List<DriverEntry> racers)
    {
        if (session?.Laps is int total && total > 0)
        {
            int leaderLaps = racers.Count == 0 ? 0 : racers.Max(d => t.Laps(d.CarIdx));
            return $"Lap {Math.Clamp(leaderLaps + 1, 1, total)}/{total}";
        }
        if (!double.IsNaN(f.SessionTimeRemain) && f.SessionTimeRemain > 0 && f.SessionTimeRemain < 86400)
        {
            var left = TimeSpan.FromSeconds(f.SessionTimeRemain);
            return left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left:mm\\:ss} left" : $"{left:mm\\:ss} left";
        }
        return "";
    }

    static LapMark Mark(float last, float best, float classBest)
    {
        if (last <= 0 || best <= 0) return LapMark.None;
        const float same = 0.0005f; // the SDK reports both as floats
        if (classBest > 0 && Math.Abs(last - classBest) < same) return LapMark.ClassBest;
        return Math.Abs(last - best) < same ? LapMark.PersonalBest : LapMark.None;
    }

    public static string FormatLap(double seconds) =>
        seconds <= 0 ? "" : TimeSpan.FromSeconds(seconds).ToString(seconds >= 60 ? @"m\:ss\.fff" : @"ss\.fff", CultureInfo.InvariantCulture);

    /// <summary>At most <paramref name="max"/> rows: the first <paramref name="leaders"/>, then a run centred on you.</summary>
    internal static List<StandingsRow> Window(List<StandingsRow> rows, int max, int leaders)
    {
        if (rows.Count <= max) return rows;
        int me = rows.FindIndex(r => r.IsPlayer);
        if (me < max) return rows.Take(max).ToList();
        leaders = Math.Clamp(leaders, 0, max - 1);
        int rest = max - leaders;
        int start = Math.Clamp(me - rest / 2, leaders, rows.Count - rest);
        var result = rows.Take(leaders).ToList();
        result.AddRange(rows.Skip(start).Take(rest).Select((r, i) => i == 0 && start > leaders ? r with { RowsSkippedBefore = true } : r));
        return result;
    }
}
