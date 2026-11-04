using Xunit;

public class SessionYamlTests
{
    const string Sample = """
        ---
        WeekendInfo:
         TrackID: 249
         TrackLength: 20.6383 km
         TrackDisplayShortName: Nordschleife
         TrackNumTurns: 154
         Category: SportsCar
        SessionInfo:
         Sessions:
         - SessionNum: 0
           SessionLaps: unlimited
           SessionType: Warmup
           ResultsPositions:
         - SessionNum: 1
           SessionLaps: 3
           SessionType: Race
           SessionName: RACE
           ResultsPositions:
           ResultsFastestLap:
           - CarIdx: 255
             FastestTime: -1.0000
        QualifyResultsInfo:
         Results:
         - Position: 0
           CarIdx: 1
         - Position: 1
           CarIdx: 0
        DriverInfo:
         DriverCarIdx: 1
         Drivers:
         - CarIdx: 0
           UserName: Sam: The Rocket
           UserID: 100001
           CarNumber: "19"
           CarClassID: 2708
           CarClassShortName: GT3 Class
           CarClassColor: 0xffda59
           CarClassEstLapTime: 386.0929
           IRating: 1854
           LicSubLevel: 306
           LicString: B 3.06
           LicColor: 0x00c702
           IsSpectator: 0
           FlairName: United States
         - CarIdx: 1
           UserName: Inês Carvalho
           UserID: 100002
           CarNumber: "8"
           CarClassID: 2708
           IRating: 1889
           LicSubLevel: 297
           LicString: B 2.97
           FlairName: Brazil
         - CarIdx: 64
           UserName: Pace Car
           UserID: -1
           CarIsPaceCar: 1
        """;

    [Fact]
    public void ParsesDriversSessionsAndGrid()
    {
        var s = SessionData.Parse(Sample);
        Assert.Equal(1, s.PlayerCarIdx);
        Assert.Equal("Nordschleife", s.TrackName);
        Assert.Equal(20.6383, s.TrackLengthKm, 4);
        Assert.Equal(154, s.TrackTurns);
        Assert.Equal(3, s.Drivers.Count);

        var sam = s.Drivers[0];
        Assert.Equal("Sam: The Rocket", sam.Name); // colon inside a value
        Assert.Equal("19", sam.CarNumber);
        Assert.Equal(0xffda59, sam.ClassColor);
        Assert.Equal("B", sam.LicenseLetter);
        Assert.Equal(3.06, sam.SafetyRating, 2);
        Assert.Equal("United States", sam.Flair);
        Assert.True(s.Drivers[2].IsPaceCar);

        Assert.Equal("Race", s.Sessions[1].Type);
        Assert.Equal(3, s.Sessions[1].Laps);
        Assert.Null(s.Sessions[0].Laps);
        Assert.Equal(1, s.Grid[1]);
        Assert.Equal(2, s.Grid[0]);
    }

    /// <summary>Set IRACING_SESSION_YAML to a dumped session-info file to check the parser against a real session.</summary>
    [Fact]
    public void ParsesRealSessionIfProvided()
    {
        var path = Environment.GetEnvironmentVariable("IRACING_SESSION_YAML");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var s = SessionData.Parse(File.ReadAllText(path, System.Text.Encoding.Latin1));
        Assert.NotEmpty(s.Drivers);
        Assert.Contains(s.Drivers, d => d.CarIdx == s.PlayerCarIdx);
        Assert.All(s.Drivers.Where(d => !d.IsPaceCar), d => Assert.False(string.IsNullOrEmpty(d.Name)));
        Assert.NotEmpty(s.Sessions);
    }
}

public class RatingTests
{
    [Fact]
    public void SofOfEqualFieldIsThatRating() =>
        Assert.Equal(2000, IRatingMath.StrengthOfField(Enumerable.Repeat(2000, 20)));

    [Fact]
    public void EqualFieldGainsDecreaseWithPosition()
    {
        int n = 12;
        var changes = IRatingMath.Changes(Enumerable.Repeat(2000, n).ToList(), Enumerable.Range(1, n).ToList(), Enumerable.Repeat(true, n).ToList());
        Assert.True(changes[0] > 0);
        Assert.True(changes[^1] < 0);
        for (int i = 1; i < n; i++) Assert.True(changes[i] < changes[i - 1]);
    }

    [Fact]
    public void UpsetWinIsWorthMoreThanExpectedWin()
    {
        var started = new[] { true, true };
        double favouriteWins = IRatingMath.Changes(new[] { 3000, 1500 }, new[] { 1, 2 }, started)[0];
        double underdogWins = IRatingMath.Changes(new[] { 3000, 1500 }, new[] { 2, 1 }, started)[1];
        Assert.True(underdogWins > favouriteWins);
        Assert.True(favouriteWins > 0);
    }

    [Fact]
    public void NonStarterLosesRating()
    {
        var changes = IRatingMath.Changes(new[] { 2000, 2000, 2000, 2000 }, new[] { 1, 2, 3, 4 }, new[] { true, true, true, false });
        Assert.True(changes[3] < 0);
    }

    [Fact]
    public void SafetyRatingEstimateMatchesTheCalibrationRace()
    {
        // Clean 3-lap Nordschleife races: real +0.33 at A 1.38, and 1.71 -> 2.47 (+0.36 and the whole-number bump).
        Assert.Equal(0.35, global::SafetyRatingEstimate.Change(462, 0, 1.38, "A", 1, new StandingsConfig()), 2);
        Assert.Equal(2.46, 1.71 + global::SafetyRatingEstimate.Change(462, 0, 1.71, "A", 1, new StandingsConfig()), 2);
        // ...and a 3-lap race with 2x at A 2.47: real +0.14.
        Assert.Equal(0.14, global::SafetyRatingEstimate.Change(462, 2, 2.47, "A", 1, new StandingsConfig()), 2);
    }

    [Fact]
    public void IncidentsCostMoreAtHigherLicences()
    {
        var cfg = new StandingsConfig();
        double atD = global::SafetyRatingEstimate.Change(200, 4, 2.5, "D", 1, cfg);
        double atA = global::SafetyRatingEstimate.Change(200, 4, 2.5, "A", 1, cfg);
        Assert.True(atD > 0);   // 200 corners against a D's 18-corner bar: 4x is fine
        Assert.True(atA < 0);   // the same race at A (135-corner bar) loses SR
        Assert.Equal(0.5 * 0.00075 * 200, global::SafetyRatingEstimate.Change(200, 0, 2.5, "A", 0.5, cfg), 4); // practice: half
    }

    [Fact]
    public void CrossingAWholeNumberAddsTheBump()
    {
        Assert.Equal(3.42, global::SafetyRatingEstimate.Apply(2.98, 0.04, 0.40), 2);  // 2.98 -> 3.02 -> 3.42
        Assert.Equal(2.58, global::SafetyRatingEstimate.Apply(3.02, -0.04, 0.40), 2); // 3.02 -> 2.98 -> 2.58
        Assert.Equal(1.71, global::SafetyRatingEstimate.Apply(1.38, 0.33, 0.40), 2);  // no crossing
        Assert.Equal(4.99, global::SafetyRatingEstimate.Apply(4.90, 0.20, 0.40), 2);  // capped, no bump past 4.99
    }

    [Fact]
    public void CountryCodes()
    {
        Assert.Equal("US", Countries.Code("United States"));
        Assert.Equal("", Countries.Code("-none-"));
        Assert.Equal("", Countries.Code("Global"));
        Assert.Equal("ATL", Countries.Code("Atlantis"));
    }

    [Fact]
    public void FlagsLoadWithFallback()
    {
        Assert.NotNull(Countries.Flag(Countries.Code("United States")));
        Assert.NotNull(Countries.Flag(Countries.Code("Scotland")));
        Assert.Null(Countries.Flag(Countries.Code("Atlantis")));
        Assert.Null(Countries.Flag(""));
    }
}

public class StandingsTests
{
    static SessionData Session(int cars, string type = "Race") => new()
    {
        PlayerCarIdx = 2,
        TrackTurns = 10,
        Official = true,
        Sessions = new Dictionary<int, SessionDef> { [0] = new(0, type, type.ToUpperInvariant(), 10) },
        Drivers = Enumerable.Range(0, cars).Select(i => new DriverEntry(i, $"Driver {i}", 1000 + i, $"{i}", "Car", 1, "GT3", 0xffffff, 100,
            3000 - i * 100, "B", 3.0, 0x00c702, "United States", false, false)).ToList(),
    };

    static CarFrame Frame(params (int Pos, int Laps, float Pct)[] cars)
    {
        var f = new CarFrame { SessionState = CarFrame.StateRacing };
        for (int i = 0; i < CarFrame.MaxCars; i++) { f.LapDistPct[i] = -1; f.TrackSurface[i] = CarFrame.SurfaceNotInWorld; }
        for (int i = 0; i < cars.Length; i++)
        {
            f.ClassPosition[i] = cars[i].Pos;
            f.Position[i] = cars[i].Pos;
            f.LapCompleted[i] = cars[i].Laps;
            f.LapDistPct[i] = cars[i].Pct;
            f.EstTime[i] = cars[i].Pct * 100;
            f.TrackSurface[i] = 3;
        }
        return f;
    }

    [Fact]
    public void GapsToPlayerIncludingAcrossTheLine()
    {
        // Car 0 leads by a lap+; car 1 is just past the line ahead of you; you (car 2) are just before it; car 3 is behind.
        var f = Frame((1, 6, 0.50f), (2, 5, 0.02f), (3, 4, 0.98f), (4, 4, 0.90f));
        var snap = Standings.Build(Session(4), f, new StandingsConfig());
        var rows = snap.Classes.Single().Rows;
        Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Select(r => r.Driver.CarIdx));
        Assert.Equal("-1L", rows[0].Gap);
        Assert.Equal("-4.0", rows[1].Gap); // 0.04 laps of a 100 s lap, wrapping past the line
        Assert.Equal("", rows[2].Gap);
        Assert.Equal("+8.0", rows[3].Gap);
        Assert.Equal(5, rows[1].Laps);
        Assert.Equal("US", rows[0].Country);
        Assert.All(rows, r => Assert.NotNull(r.IRatingDelta));
        Assert.NotNull(snap.PlayerSrDelta);
    }

    [Fact]
    public void LastLapIsFormattedAndMarked()
    {
        var f = Frame((1, 3, 0.5f), (2, 3, 0.4f), (3, 3, 0.3f));
        f.BestLap[0] = 98.5f; f.LastLap[0] = 98.5f;   // fastest in class
        f.BestLap[1] = 99.1f; f.LastLap[1] = 99.1f;   // own best
        f.BestLap[2] = 99.0f; f.LastLap[2] = 100.25f; // neither
        var rows = Standings.Build(Session(3), f, new StandingsConfig()).Classes.Single().Rows;
        Assert.Equal("1:38.500", rows[0].LastLap);
        Assert.Equal(LapMark.ClassBest, rows[0].LastLapMark);
        Assert.Equal(LapMark.PersonalBest, rows[1].LastLapMark);
        Assert.Equal(LapMark.None, rows[2].LastLapMark);
        Assert.Equal("1:40.250", rows[2].LastLap);
    }

    [Fact]
    public void SideBySideCarsWithNoisyEstimatesAreNotALapApart()
    {
        // Seen live on the Nordschleife grid: the car just behind you had a slightly larger EstTime than yours.
        var f = Frame((1, 0, 0.8680f), (2, 0, 0.8679f), (3, 0, 0.8670f));
        f.EstTime[0] = 341.5f; f.EstTime[1] = 341.4f; f.EstTime[2] = 343.4f;
        var s = Session(3);
        s = new SessionData { PlayerCarIdx = 0, TrackTurns = 10, Sessions = s.Sessions,
            Drivers = s.Drivers.Select(d => d with { ClassEstLapTime = 386 }).ToList() };
        var rows = Standings.Build(s, f, new StandingsConfig()).Classes.Single().Rows;
        Assert.Equal("+0.0", rows[1].Gap); // 0.0001 laps behind: ~0.04 s
        Assert.Equal("+0.0", rows[2].Gap); // was +383.9

        f.SessionState = 3; // formation lap: plain gap on track
        rows = Standings.Build(s, f, new StandingsConfig()).Classes.Single().Rows;
        Assert.Equal("+0.4", rows[2].Gap); // 0.001 laps behind, not the 1.9 s "up the road" the noisy estimate says
    }

    /// <summary>
    /// Four cars on a 100 s lap at steady speed: leader, car 1 two seconds ahead of you (car 2), car 3 1.5 s behind.
    /// EstTime is deliberately 30% off (like iRacing's generic class estimate) to prove the gap comes from timing.
    /// </summary>
    static (RaceTracker, CarFrame) Race(double seconds, Func<int, double, double>? offsetOverride = null, int state = CarFrame.StateRacing)
    {
        double[] offsets = { 10, 2, 0, -1.5 };
        var t = new RaceTracker();
        CarFrame f = null!;
        for (double now = 0; now <= seconds; now += 1 / 60.0)
        {
            f = new CarFrame { SessionState = state, SessionTime = now };
            for (int i = 0; i < CarFrame.MaxCars; i++) { f.LapDistPct[i] = -1; f.TrackSurface[i] = CarFrame.SurfaceNotInWorld; }
            for (int i = 0; i < offsets.Length; i++)
            {
                double dist = (now + 20 + (offsetOverride?.Invoke(i, now) ?? offsets[i])) / 100;
                f.LapCompleted[i] = (int)Math.Floor(dist);
                f.LapDistPct[i] = (float)(dist - Math.Floor(dist));
                f.EstTime[i] = f.LapDistPct[i] * 130;
                f.TrackSurface[i] = 3;
                f.ClassPosition[i] = i + 1;
            }
            t.Update(f, 2);
        }
        return (t, f);
    }

    [Fact]
    public void GapsComeFromIntervalTiming()
    {
        var (t, f) = Race(130);
        var rows = Standings.Build(Session(4), f, new StandingsConfig(), t).Classes.Single().Rows;
        Assert.Equal("-2.0", rows[1].Gap);
        Assert.Equal("+1.5", rows[3].Gap);
        Assert.True(rows[1].GapHighlight);
        Assert.True(rows[3].GapHighlight);
        Assert.False(rows[0].GapHighlight);
    }

    [Fact]
    public void PositionsAreLiveWhileRacing()
    {
        // Car 3 has just passed you, but iRacing's official order hasn't caught up yet.
        var (t, f) = Race(30, (i, now) => i == 3 ? 0.5 : new double[] { 10, 2, 0, 0 }[i]);
        var rows = Standings.Build(Session(4), f, new StandingsConfig(), t).Classes.Single().Rows;
        Assert.Equal(new[] { 0, 1, 3, 2 }, rows.Select(r => r.Driver.CarIdx));
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.Select(r => r.ClassPosition));
    }

    [Fact]
    public void EstimatesSurviveGoingBackToThePits()
    {
        var (t, f) = Race(130);
        f.PlayerIncidents = 2;
        t.Update(f, 2);
        var before = Standings.Build(Session(4), f, new StandingsConfig(), t);
        int beforeIr = before.Classes.Single().Rows.Single(r => r.IsPlayer).IRatingDelta!.Value;

        // Chequered flag, you cross the line, then leave the car: iRacing clears your laps, position and incidents.
        f.SessionState = 5; f.SessionTime += 1; t.Update(f, 2);
        f.LapCompleted[2] += 1; f.LapDistPct[2] = 0.001f; f.SessionTime += 1; t.Update(f, 2);
        var finished = Standings.Build(Session(4), f, new StandingsConfig(), t);
        f.LapCompleted[2] = -1; f.LapDistPct[2] = -1; f.TrackSurface[2] = CarFrame.SurfaceNotInWorld;
        f.ClassPosition[2] = 0; f.PlayerIncidents = 0; f.SessionTime += 60; t.Update(f, 2);

        var after = Standings.Build(Session(4), f, new StandingsConfig(), t);
        var me = after.Classes.Single().Rows.Single(r => r.IsPlayer);
        Assert.Equal(3, me.ClassPosition);
        Assert.Equal(beforeIr, me.IRatingDelta);
        Assert.Equal(finished.PlayerSrDelta, after.PlayerSrDelta);
        Assert.Equal(2, after.PlayerIncidents);
        Assert.True(me.Laps > 0);
    }

    [Fact]
    public void FormationLapOrderFollowsTheTrackAcrossTheLine()
    {
        // Formation lap, grid order 0-1-2-3. The polesitter has crossed the line (its lap counter ticked over), the rest
        // haven't, and car 3 has got ahead of you (car 2) on track.
        var f = Frame((1, 1, 0.020f), (2, 0, 0.990f), (3, 0, 0.985f), (4, 0, 0.988f));
        f.SessionState = CarFrame.StateParadeLaps;
        var rows = Standings.Build(Session(4), f, new StandingsConfig()).Classes.Single().Rows;
        Assert.Equal(new[] { 0, 1, 3, 2 }, rows.Select(r => r.Driver.CarIdx));
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.Select(r => r.ClassPosition));
        Assert.StartsWith("-", rows[2].Gap); // car 3 is up the road from you
    }

    [Fact]
    public void FormationLapGapsComeFromIntervalTiming()
    {
        // Same steady field as the race test, but before the green: gaps are timed live, not modelled from race pace.
        var (t, f) = Race(130, state: CarFrame.StateParadeLaps);
        var rows = Standings.Build(Session(4), f, new StandingsConfig(), t).Classes.Single().Rows;
        Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Select(r => r.Driver.CarIdx));
        Assert.Equal("-2.0", rows[1].Gap);
        Assert.Equal("+1.5", rows[3].Gap);
    }

    [Fact]
    public void OffTheTrackShowsTheCarsAroundYou()
    {
        // Same field as GapsToPlayerIncludingAcrossTheLine, but you've gone off: the list becomes the cars around you on
        // track (the leader, half a lap round, is just behind you on the road).
        var f = Frame((1, 6, 0.50f), (2, 5, 0.02f), (3, 4, 0.98f), (4, 4, 0.90f));
        f.TrackSurface[2] = CarFrame.SurfaceOffTrack;
        var rows = Standings.Build(Session(4), f, new StandingsConfig()).Classes.Single().Rows;
        Assert.Equal(new[] { 1, 2, 3, 0 }, rows.Select(r => r.Driver.CarIdx));

        var off = Standings.Build(Session(4), f, new StandingsConfig { RelativeWhenInTrouble = false }).Classes.Single().Rows;
        Assert.Equal(new[] { 0, 1, 2, 3 }, off.Select(r => r.Driver.CarIdx));
    }

    [Fact]
    public void CrashedGapCountsDownAsACarComesUp()
    {
        // You stop dead at 100 s (on the track surface, so only the pace says you're in trouble); car 3 is coming up
        // from 20 s behind. The gap must shrink as it closes, not grow with the time you're losing.
        static double Off(int i, double now) => i switch { 2 => now > 100 ? 100 - now : 0, 3 => -20, _ => new double[] { 10, 2 }[i] };
        string GapAt(double seconds)
        {
            var (t, f) = Race(seconds, Off);
            var rows = Standings.Build(Session(4), f, new StandingsConfig(), t).Classes.Single().Rows;
            return rows.Single(r => r.Driver.CarIdx == 3).Gap;
        }
        double at105 = double.Parse(GapAt(105)), at110 = double.Parse(GapAt(110));
        Assert.True(at110 < at105, $"gap went {at105} -> {at110}");
        Assert.InRange(at110, 5, 15); // 10 s away at pace (the test's estimate is skewed 30%)
    }

    [Fact]
    public void GridGapDoesNotGrowWhileStopped()
    {
        // Gridded: everyone stopped for two minutes. The gap must stay what the positions say.
        var (t, f) = Race(60);
        double now = f.SessionTime;
        f.SessionState = 3; // parade / gridding, before the green
        string Gap() => Standings.Build(Session(4), f, new StandingsConfig(), t).Classes.Single().Rows.Single(r => r.Driver.CarIdx == 3).Gap;
        // Settled on the grid (stopped for a couple of seconds; while still rolling in, the gap is timed).
        for (int i = 0; i < 20; i++) { f.SessionTime = now + i / 10.0; t.Update(f, 2); }
        now = f.SessionTime;
        string atStart = Gap();
        for (int i = 0; i < 120 * 10; i++) { f.SessionTime = now + i / 10.0; t.Update(f, 2); }
        Assert.Equal(atStart, Gap());
        Assert.True(double.Parse(atStart) < 3); // ~1.5 s behind (the test's estimate is skewed 30%), not minutes
    }

    [Fact]
    public void PracticeShowsTheCarsAroundYouOnTrack()
    {
        // 100 s lap. You (car 2) at 0.50. Up the road: car 0 at 0.60 (laps ahead) and car 1 at 0.55, laps down;
        // car 3 at 0.40 behind; car 4 at 0.02 is 48 s behind.
        var f = Frame((1, 5, 0.60f), (4, 1, 0.55f), (2, 3, 0.50f), (3, 3, 0.40f), (5, 3, 0.02f));
        var rows = Standings.Build(Session(5, "Practice"), f, new StandingsConfig()).Classes.Single().Rows;
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, rows.Select(r => r.Driver.CarIdx));
        Assert.Equal(new[] { 1, 4, 2, 3, 5 }, rows.Select(r => r.ClassPosition)); // iRacing's positions, not the row
        Assert.Equal(new[] { "-10.0", "-5.0", "", "+10.0", "+48.0" }, rows.Select(r => r.Gap));
        Assert.True(rows[1].GapHighlight && rows[3].GapHighlight);

        var race = Standings.Build(Session(5, "Practice"), f, new StandingsConfig { OnTrackOrder = "Never" }).Classes.Single().Rows;
        Assert.Equal(new[] { 0, 2, 3, 4, 1 }, race.Select(r => r.Driver.CarIdx)); // by laps and distance, like a race
    }

    [Fact]
    public void ClassesShareOneListUnlessSplit()
    {
        // Car 1 is in another class, running between car 0 and you (car 2).
        var s = Session(4);
        s = new SessionData
        {
            PlayerCarIdx = s.PlayerCarIdx, TrackTurns = s.TrackTurns, Official = s.Official, Sessions = s.Sessions,
            Drivers = s.Drivers.Select(d => d.CarIdx == 1 ? d with { ClassId = 2, ClassName = "GT4", ClassColor = 0x33ceff } : d).ToList(),
        };
        var f = Frame((1, 3, 0.6f), (1, 3, 0.5f), (2, 3, 0.4f), (3, 3, 0.3f));

        var rows = Standings.Build(s, f, new StandingsConfig()).Classes.Single().Rows;
        Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Select(r => r.Driver.CarIdx));
        Assert.Equal(new[] { 1, 1, 2, 3 }, rows.Select(r => r.ClassPosition)); // in each car's own class
        Assert.Equal("-10.0", rows[1].Gap); // the other-class car just ahead is timed to you too
        Assert.True(rows[1].GapHighlight);
        Assert.Null(rows[1].IRatingDelta); // alone in its class

        var split = Standings.Build(s, f, new StandingsConfig { ByClass = true }).Classes;
        Assert.Equal(2, split.Count);
        Assert.Equal(new[] { 0, 2, 3 }, split[0].Rows.Select(r => r.Driver.CarIdx));
        Assert.Equal(new[] { 1, 2, 3 }, split[0].Rows.Select(r => r.ClassPosition));
    }

    [Fact]
    public void PracticeHasNoIRatingProjection()
    {
        var f = Frame((1, 3, 0.5f), (2, 3, 0.4f), (3, 3, 0.3f));
        var snap = Standings.Build(Session(3, "Practice"), f, new StandingsConfig());
        Assert.All(snap.Classes.Single().Rows, r => Assert.Null(r.IRatingDelta));
        Assert.NotNull(snap.PlayerSrDelta); // practice counts for SR, at reduced weight

        var hosted = Standings.Build(new SessionData { PlayerCarIdx = 2, Sessions = Session(3).Sessions, Drivers = Session(3).Drivers }, f, new StandingsConfig());
        Assert.Equal("B 3.00  ·  unofficial session doesn't count", hosted.PlayerSr);
        Assert.Null(hosted.PlayerSrDelta);
    }

    [Fact]
    public void WindowKeepsLeadersAndCarsAroundYou()
    {
        var rows = Enumerable.Range(0, 30).Select(i => new StandingsRow(
            new DriverEntry(i, "", 0, "", "", 1, "", 0, 0, 0, "", 0, 0, "", false, false), i + 1, "", 0, "", null, i == 20, false, false)).ToList();
        var shown = Standings.Window(rows, 10, 3);
        Assert.Equal(10, shown.Count);
        Assert.Equal(new[] { 1, 2, 3 }, shown.Take(3).Select(r => r.ClassPosition));
        Assert.Contains(shown, r => r.IsPlayer);
        Assert.True(shown[3].RowsSkippedBefore);
    }
}
