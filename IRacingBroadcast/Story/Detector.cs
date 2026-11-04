/// <summary>
/// Finds the story in the scan: overtakes, battles, incidents, pit stops, lead changes, fastest laps, the start and the
/// finish. Pure function of the recorded frames, so it can be re-run and tested without iRacing.
/// </summary>
public static class Detector
{
    const double BattleGap = 1.0, BattleMinSeconds = 20, PassConfirmSeconds = 3;

    sealed class PendingPass { public int Passer, Passed; public double T; public int Position; public bool Lap1; }
    sealed class OpenBattle { public int Ahead, Behind; public double Start, LastClose, MinGap = 99; public int Position; }
    sealed class OpenIncident { public double Start, LastBad; public int RankBefore; public bool OffTrack, Slow, Stopped; public double StoppedSince = double.NaN; public float Pct; }

    /// <summary>How close on track (metres) and in time (s) two cars' incidents must be to count as one incident.</summary>
    const double SameSpotM = 60, SameMomentS = 2.5;

    public static Story Detect(List<Frame> frames, RaceInfo info, int featured)
    {
        var story = new Story { FeaturedCar = featured };
        if (frames.Count == 0) return story;
        story.FirstT = frames[0].T;
        story.EndT = frames[^1].T;
        var greenFrame = frames.FirstOrDefault(f => f.State >= Frame.StateRacing);
        story.GreenT = greenFrame?.T ?? frames[0].T;
        story.CheckeredT = frames.FirstOrDefault(f => f.State >= Frame.StateCheckered)?.T ?? story.EndT;
        var finish = new FinishWatch();
        foreach (var f in frames) finish.Update(f);
        story.LastFinishT = double.IsNaN(finish.LastFinishT) ? story.CheckeredT : finish.LastFinishT;
        story.Finishers = finish.Finishers;

        var tracker = new FieldTracker(info);
        var events = story.Events;
        var classes = info.Drivers.Select(d => d.ClassId).Distinct().ToList();
        bool multiClass = classes.Count > 1;

        var prevOrder = new Dictionary<int, List<int>>();
        var pending = new List<PendingPass>();
        var battles = new Dictionary<(int, int), OpenBattle>();
        var incidents = new Dictionary<int, OpenIncident>();
        var refSpeed = new Dictionary<int, double>();
        var pitIn = new Dictionary<int, (double T, int Rank)>();
        var lastInWorld = new Dictionary<int, double>();
        var classBest = classes.ToDictionary(c => c, _ => double.MaxValue);
        var lastLapSeen = new Dictionary<int, float>();
        var leader = classes.ToDictionary(c => c, _ => -1);
        int leaderLap = -1;
        bool lastLapCalled = false, featuredFinished = false;
        var recentIncident = new Dictionary<int, double>();
        var incidentAt = new Dictionary<RaceEvent, float>(ReferenceEqualityComparer.Instance); // where on the lap each began
        var pits = new Dictionary<int, List<(double Start, double End)>>();
        int featuredLapsAtFlag = featured >= 0 ? frames.FirstOrDefault(x => x.T >= story.CheckeredT)?.Car(featured)?.LapCompleted ?? int.MaxValue : int.MaxValue;
        double lastT = double.NaN;

        double W(int pos) => 1 + Math.Max(0, 10 - pos) / 3.0;
        double Featured(params int[] cars) => cars.Contains(featured) ? 4 : 0;
        string Name(int idx) => info.Car(idx)?.Name ?? $"car {idx}";
        // Some series leave the class name empty; the car says it then.
        string ClassLabel(int idx) => info.Car(idx) is { } d ? (d.ClassName.Length > 0 ? d.ClassName : d.Car) : "its class";

        foreach (var f in frames)
        {
            tracker.Update(f);
            if (f.T < story.GreenT) continue;
            bool racing = f.State == Frame.StateRacing;
            double dt = double.IsNaN(lastT) ? 0 : f.T - lastT;
            lastT = f.T;

            foreach (var cls in classes)
            {
                var order = tracker.Order(cls);
                var rank = new Dictionary<int, int>();
                for (int k = 0; k < order.Count; k++) rank[order[k]] = k + 1;

                // --- Overtakes: a car now ahead of one that was ahead of it a frame ago, both on track.
                if (racing && prevOrder.TryGetValue(cls, out var prev))
                {
                    var prevRank = new Dictionary<int, int>();
                    for (int k = 0; k < prev.Count; k++) prevRank[prev[k]] = k + 1;
                    foreach (var x in order)
                    {
                        if (!prevRank.TryGetValue(x, out int px) || rank[x] >= px) continue;
                        foreach (var y in prev.Take(px - 1).Skip(Math.Max(0, rank[x] - 1)))
                        {
                            if (!rank.TryGetValue(y, out int ry) || ry < rank[x]) continue;
                            var cx = f.Car(x)!.Value; var cy = f.Car(y)!.Value;
                            if (!cx.InWorld || !cy.InWorld || cx.OnPitRoad || cy.OnPitRoad) continue;
                            if (Math.Abs(tracker.Distance(x) - tracker.Distance(y)) > 0.05) continue;
                            pending.RemoveAll(p => p.Passer == y && p.Passed == x); // re-pass while side by side
                            pending.Add(new PendingPass { Passer = x, Passed = y, T = f.T, Position = rank[x], Lap1 = f.T - story.GreenT < 60 });
                        }
                    }
                }
                prevOrder[cls] = order;

                // --- Lead changes (by any means: pass, pit stop, incident).
                if (racing && order.Count > 0 && order[0] != leader[cls])
                {
                    if (leader[cls] >= 0 && f.T - story.GreenT > 10)
                        events.Add(new RaceEvent(Kinds.LeadChange, f.T, f.T + 5, [order[0], leader[cls]], 1, 6 + Featured(order[0], leader[cls]),
                            $"{Name(order[0])} takes the {(multiClass ? info.Car(order[0])!.ClassName + " " : "")}lead from {Name(leader[cls])}"));
                    leader[cls] = order[0];
                }

                // --- Battles between neighbours (not on the opening lap, where the whole field is nose to tail).
                if (racing && f.T - story.GreenT > 45)
                {
                    for (int k = 1; k < order.Count; k++)
                    {
                        int a = order[k - 1], b = order[k];
                        var key = (Math.Min(a, b), Math.Max(a, b));
                        double gap = tracker.Gap(b, a);
                        bool close = gap < BattleGap && tracker.InWorld(a) && tracker.InWorld(b);
                        if (!battles.TryGetValue(key, out var bt))
                        {
                            if (close) battles[key] = new OpenBattle { Ahead = a, Behind = b, Start = f.T, LastClose = f.T, MinGap = gap, Position = k };
                        }
                        else if (close)
                        {
                            bt.LastClose = f.T;
                            bt.MinGap = Math.Min(bt.MinGap, gap);
                        }
                    }
                }
            }

            // Confirm passes that held.
            foreach (var p in pending.Where(p => f.T - p.T >= PassConfirmSeconds).ToList())
            {
                pending.Remove(p);
                if (tracker.Distance(p.Passer) <= tracker.Distance(p.Passed)) continue; // swapped back
                if (InPitWindow(p.Passer, p.T) || InPitWindow(p.Passed, p.T)) continue;
                bool afterIncident = recentIncident.TryGetValue(p.Passed, out var it) && p.T - it < 8;
                double imp = (p.Lap1 ? 1.0 : 2.0) * W(p.Position) + Featured(p.Passer, p.Passed) + (p.Position == 1 ? 3 : 0) - (afterIncident ? 1.5 : 0);
                events.Add(new RaceEvent(Kinds.Overtake, p.T, p.T + 3, [p.Passer, p.Passed], p.Position, imp,
                    $"{Name(p.Passer)} passes {Name(p.Passed)} for P{p.Position}{(afterIncident ? " after their incident" : "")}{(p.Lap1 ? " (lap 1)" : "")}"));
            }

            // Close battles that ended.
            foreach (var (key, bt) in battles.Where(kv => f.T - kv.Value.LastClose > 4 || !racing).ToList())
            {
                battles.Remove(key);
                CloseBattle(bt);
            }

            foreach (var d in info.Drivers)
            {
                int i = d.CarIdx;
                var c = f.Car(i);
                if (c == null) continue;
                var car = c.Value;

                // --- Pit stops.
                if (car.OnPitRoad && !pitIn.ContainsKey(i) && racing) pitIn[i] = (f.T, tracker.Rank(i));
                else if (!car.OnPitRoad && pitIn.TryGetValue(i, out var pin) && car.InWorld)
                {
                    pitIn.Remove(i);
                    int after = tracker.Rank(i);
                    double length = f.T - pin.T;
                    if (length > 5)
                        events.Add(new RaceEvent(Kinds.Pit, pin.T, f.T, [i], pin.Rank, 1 + W(pin.Rank) / 2 + Featured(i),
                            $"{Name(i)} pits from P{pin.Rank}, {length:0}s in the pit lane, rejoins P{after}"));
                }
                if (car.OnPitRoad)
                {
                    if (!pits.TryGetValue(i, out var visits)) pits[i] = visits = new();
                    if (visits.Count == 0 || f.T - visits[^1].End > 2) visits.Add((f.T, f.T));
                    else visits[^1] = (visits[^1].Start, f.T);
                }

                // --- Retirements: gone from the world for a minute before the flag.
                if (car.InWorld) lastInWorld[i] = f.T;
                else if (racing && lastInWorld.TryGetValue(i, out var seen) && f.T - seen > 60 && seen > 0)
                {
                    lastInWorld[i] = -1; // once
                    var res = info.Results.FirstOrDefault(r => r.CarIdx == i);
                    events.Add(new RaceEvent(Kinds.Retire, seen, seen + 5, [i], tracker.Rank(i), 2 + Featured(i),
                        $"{Name(i)} is out of the race{(res != null && res.ReasonOut.Length > 0 && res.ReasonOut != "Running" ? $" ({res.ReasonOut})" : "")}"));
                }

                // --- Fastest laps.
                if (car.LastLap > 0 && (!lastLapSeen.TryGetValue(i, out var ll) || Math.Abs(ll - car.LastLap) > 0.0005))
                {
                    lastLapSeen[i] = car.LastLap;
                    if (car.LastLap < classBest[d.ClassId] && f.T - story.GreenT > 30)
                    {
                        bool first = classBest[d.ClassId] == double.MaxValue;
                        classBest[d.ClassId] = car.LastLap;
                        if (!first && racing)
                            events.Add(new RaceEvent(Kinds.FastestLap, f.T, f.T + 4, [i], tracker.Rank(i), 1.5 + Featured(i),
                                $"Fastest lap for {Name(i)}: {LapTime(car.LastLap)}"));
                    }
                }

                // --- Incidents: off track, a sudden loss of speed away from the pits, or stopped on track.
                if (!racing || !car.InWorld || car.OnPitRoad || car.Surface is 1 or 2 || f.Caution || f.T - story.GreenT < 5) { incidents.Remove(i); continue; }
                double v = tracker.Speed(i);
                if (double.IsNaN(v)) continue;
                refSpeed.TryGetValue(i, out var vr);
                if (v > 15) refSpeed[i] = vr = vr <= 0 ? v : vr + (v - vr) * Math.Min(1, dt / 15);
                bool off = car.Surface == 0;
                bool slow = vr > 20 && v < 0.35 * vr;
                bool bad = off || slow;
                if (incidents.TryGetValue(i, out var inc))
                {
                    if (bad) inc.LastBad = f.T;
                    inc.OffTrack |= off; inc.Slow |= slow;
                    if (v < 2) { if (double.IsNaN(inc.StoppedSince)) inc.StoppedSince = f.T; else if (f.T - inc.StoppedSince > 5) inc.Stopped = true; }
                    else inc.StoppedSince = double.NaN;
                    if (f.T - inc.LastBad > 3)
                    {
                        incidents.Remove(i);
                        int lost = Math.Max(0, tracker.Rank(i) - inc.RankBefore);
                        // A brief wheel off the track that cost nothing isn't a story.
                        if (inc.Slow || inc.Stopped || lost > 0 || inc.LastBad - inc.Start > 2)
                        {
                            // Nobody parks on a racing line on purpose: a car stopped on track has crashed.
                            string what = inc.Stopped ? "crashes and comes to a stop" : inc.Slow && inc.OffTrack ? "goes off and loses time" : inc.Slow ? "has a moment" : "runs wide off the track";
                            var ev = new RaceEvent(Kinds.Incident, inc.Start, inc.LastBad, [i], inc.RankBefore,
                                2 + W(inc.RankBefore) + lost * 0.5 + (inc.Stopped ? 1 : 0) + Featured(i),
                                $"{Name(i)} {what} from P{inc.RankBefore}{(lost > 0 ? $", loses {lost} place{(lost == 1 ? "" : "s")}" : "")}");
                            events.Add(ev);
                            incidentAt[ev] = inc.Pct;
                            recentIncident[i] = inc.Start;
                        }
                    }
                }
                else if (bad)
                {
                    incidents[i] = new OpenIncident { Start = f.T, LastBad = f.T, RankBefore = tracker.Rank(i), OffTrack = off, Slow = slow, Pct = car.Pct };
                    recentIncident[i] = f.T;
                }
            }

            // --- Lap snapshots, last lap and the finish.
            var overall = tracker.Order();
            if (overall.Count > 0)
            {
                int lap = f.Car(overall[0])!.Value.LapCompleted;
                if (lap > leaderLap && racing)
                {
                    leaderLap = lap;
                    story.Laps.Add(Snapshot(lap, f, tracker, info));
                    if (!lastLapCalled && info.RaceLaps is int total && lap == total - 1)
                    {
                        lastLapCalled = true;
                        events.Add(new RaceEvent(Kinds.LastLap, f.T, f.T + 5, [overall[0]], 1, 5, $"Last lap. {Name(overall[0])} leads"));
                    }
                }
                if (!lastLapCalled && (f.Flags & Frame.FlagWhite) != 0 && racing)
                {
                    lastLapCalled = true;
                    events.Add(new RaceEvent(Kinds.LastLap, f.T, f.T + 5, [overall[0]], 1, 5, $"White flag: last lap. {Name(overall[0])} leads"));
                }
            }
            if (f.T >= story.CheckeredT && featured >= 0 && !featuredFinished && f.State >= Frame.StateCheckered &&
                f.Car(featured) is { } fc && fc.LapCompleted > featuredLapsAtFlag)
            {
                featuredFinished = true;
                var pos = info.Results.FirstOrDefault(r => r.CarIdx == featured)?.ClassPosition ?? tracker.Rank(featured);
                events.Add(new RaceEvent(Kinds.FeaturedFinish, f.T, f.T + 6, [featured], pos, 8, $"{Name(featured)} takes the flag in P{pos}"));
            }
        }
        foreach (var bt in battles.Values) CloseBattle(bt);

        // --- The start: positions gained and lost on the opening lap.
        var lap1 = story.Laps.FirstOrDefault(l => l.Lap >= 1);
        var startDetail = new List<string>();
        if (lap1 != null && info.Grid.Count > 0)
        {
            var moves = lap1.Order.Where(r => info.Grid.ContainsKey(r.CarIdx))
                .Select(r => (r.CarIdx, Delta: info.Grid[r.CarIdx] - r.Position)).ToList();
            foreach (var m in moves.Where(m => m.Delta >= 2).OrderByDescending(m => m.Delta).Take(3))
                startDetail.Add($"{Name(m.CarIdx)} gains {m.Delta} on lap 1 (P{info.Grid[m.CarIdx]} to P{info.Grid[m.CarIdx] - m.Delta})");
            foreach (var m in moves.Where(m => m.Delta <= -2).OrderBy(m => m.Delta).Take(3))
                startDetail.Add($"{Name(m.CarIdx)} loses {-m.Delta} on lap 1");
        }
        var pole = info.Grid.OrderBy(g => g.Value).Select(g => g.Key).FirstOrDefault(-1);
        events.Add(new RaceEvent(Kinds.Start, story.GreenT, story.GreenT + 30, pole >= 0 ? [pole] : [], 1, 10,
            "Green flag" + (pole >= 0 ? $", {Name(pole)} on pole" : "") + (startDetail.Count > 0 ? ". " + string.Join("; ", startDetail) : "")));

        var winner = info.Results.FirstOrDefault(r => r.Position == 1)?.CarIdx ?? (story.Laps.LastOrDefault()?.Order.FirstOrDefault()?.CarIdx ?? -1);
        if (winner >= 0)
            events.Add(new RaceEvent(Kinds.Finish, story.CheckeredT, story.CheckeredT + 8, [winner], 1, 10, $"Chequered flag: {Name(winner)} wins"));
        // In a multiclass race every class winner gets their moment as they cross the line.
        if (multiClass)
            foreach (var r in info.Results.Where(r => r.ClassPosition == 1 && r.CarIdx != winner))
                if (story.Finishers.FirstOrDefault(x => x.CarIdx == r.CarIdx) is { } fin)
                    events.Add(new RaceEvent(Kinds.ClassWin, fin.T, fin.T + 6, [r.CarIdx], 1, 8, $"{Name(r.CarIdx)} wins in {ClassLabel(r.CarIdx)}"));
        if (story.LastFinishT > story.CheckeredT + 5 && story.Finishers.Count > 1)
            events.Add(new RaceEvent(Kinds.FieldHome, story.LastFinishT, story.LastFinishT + 5, [story.Finishers[^1].CarIdx], 0, 3,
                $"{Name(story.Finishers[^1].CarIdx)} is the last car home; {story.Finishers.Count} cars took the flag"));

        MergeSameSpot(events, incidentAt, info.TrackLengthM);
        events.Sort((a, b) => a.T.CompareTo(b.T));
        return story;

        // Real passes, not pit-cycle swaps.
        bool InPitWindow(int car, double t) =>
            pits.TryGetValue(car, out var l) && l.Any(p => t >= p.Start - 5 && t <= p.End + 15);

        void CloseBattle(OpenBattle bt)
        {
            double length = bt.LastClose - bt.Start;
            if (length < BattleMinSeconds) return;
            int passes = events.Count(e => e.Kind == Kinds.Overtake && e.T >= bt.Start - 1 && e.T <= bt.LastClose + 4 && e.Cars.Contains(bt.Ahead) && e.Cars.Contains(bt.Behind));
            events.Add(new RaceEvent(Kinds.Battle, bt.Start, bt.LastClose, [bt.Ahead, bt.Behind], bt.Position,
                W(bt.Position) * Math.Min(3, length / 30) + Featured(bt.Ahead, bt.Behind) + passes,
                $"{Name(bt.Behind)} hunts {Name(bt.Ahead)} for P{bt.Position}: {length:0}s within a second, closest {bt.MinGap:0.0}s" +
                (passes > 0 ? $", {passes} pass{(passes == 1 ? "" : "es")}" : "")));
        }
    }

    /// <summary>
    /// Cars in trouble at the same moment and the same spot on track are one incident. That may be contact, or one car
    /// avoiding another: the SDK can't tell, so it's never called contact. The worst-hit car comes first (the camera's).
    /// </summary>
    static void MergeSameSpot(List<RaceEvent> events, Dictionary<RaceEvent, float> at, double trackM)
    {
        double Apart(RaceEvent a, RaceEvent b)
        {
            if (!at.TryGetValue(a, out var pa) || !at.TryGetValue(b, out var pb)) return double.MaxValue;
            double d = Math.Abs(pa - pb);
            return Math.Min(d, 1 - d) * (trackM > 0 ? trackM : 5000);
        }
        // How hard a car was hit, from what it did: not who it is (the featured driver mustn't take the camera off a crash).
        static double Severity(RaceEvent e)
        {
            double s = e.Detail.Contains("crashes") ? 30 : e.Detail.Contains("goes off") ? 20 : e.Detail.Contains("runs wide") ? 10 : 0;
            var m = System.Text.RegularExpressions.Regex.Match(e.Detail, @"loses (\d+) place");
            return s + (m.Success ? int.Parse(m.Groups[1].Value) : 0);
        }
        var inc = events.Where(e => e.Kind == Kinds.Incident).OrderBy(e => e.T).ToList();
        var used = new HashSet<RaceEvent>(ReferenceEqualityComparer.Instance);
        foreach (var first in inc)
        {
            if (used.Contains(first)) continue;
            var all = inc.Where(e => !used.Contains(e) && e.T >= first.T && e.T - first.T < SameMomentS && (e == first || Apart(first, e) < SameSpotM))
                .OrderByDescending(Severity).ThenByDescending(e => e.Importance).ToList();
            if (all.Count < 2) continue;
            foreach (var e in all) { used.Add(e); events.Remove(e); }
            events.Add(new RaceEvent(Kinds.Incident, all.Min(e => e.T), all.Max(e => e.End), all.SelectMany(e => e.Cars).Distinct().ToArray(),
                all.Min(e => e.Position), all.Max(e => e.Importance) + all.Count,
                "At the same spot (contact unknown): " + string.Join("; ", all.Select(e => e.Detail))));
        }
    }

    static LapSnapshot Snapshot(int lap, Frame f, FieldTracker t, RaceInfo info)
    {
        var order = t.Order();
        var rows = new List<Running>();
        if (order.Count == 0) return new LapSnapshot(lap, f.T, rows);
        int lead = order[0];
        for (int k = 0; k < order.Count; k++)
        {
            int i = order[k];
            int down = (int)Math.Floor(t.Distance(lead) - t.Distance(i));
            double gap = k == 0 ? 0 : down >= 1 ? double.NaN : t.Gap(i, lead);
            rows.Add(new Running(i, t.Rank(i), double.IsFinite(gap) ? Math.Round(gap, 1) : -1, Math.Max(0, down), f.Car(i)?.OnPitRoad ?? false));
        }
        return new LapSnapshot(lap, f.T, rows);
    }

    public static string LapTime(double s) => s <= 0 ? "-" : s >= 60 ? $"{(int)(s / 60)}:{s % 60:00.000}" : $"{s:0.000}";
}
