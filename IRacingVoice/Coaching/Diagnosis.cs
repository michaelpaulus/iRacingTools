using System.Text.RegularExpressions;

/// <summary>
/// One thing to work on in a corner: what happened (said now), what to try (said now), the short version said as a
/// reminder before the corner next lap, and a measure to tell afterwards whether it got better.
/// </summary>
public sealed record Issue(string Key, double Score, string What, string Tip, string Reminder, Func<Pass, double> Metric, bool LowerIsBetter, Func<double, string> Words);

/// <summary>
/// Judges a corner by good racing technique, not only by the clock, largely following Suellio Almeida (Almeida Racing
/// Academy): firm braking just short of the ABS; no gap in grip between braking and turning (turn as the brake comes
/// off, or you braked too early); the brake used as a range, not a switch, with the last of it there to rotate the car;
/// no turning while deep in the ABS; maximum rotation at the slowest point; no cornering while accelerating; quick to
/// 50% throttle then committed to 100 (a lift means rotation was missing); plus coasting, revs, and the driver's own best
/// pass through the corner as evidence. Every tip says why, so it carries over to other corners.
/// </summary>
public static class Diagnosis
{
    public sealed record Context(bool Metric, double MaxBrake, double MaxRpm);

    public static List<Issue> Find(Pass p, Pass? best, Context cx)
    {
        var issues = new List<Issue>();
        double drop = p.EntryKmh - p.MinKmh; // how big a braking zone this is
        string Dist(double metres) => Units.Distance(metres, cx.Metric);
        string Speed(double kmh) => Units.Speed(kmh, cx.Metric);

        if (p.CoastSeconds >= 0.5)
            issues.Add(new("coast", p.CoastSeconds,
                $"You coasted for {p.CoastSeconds:0.0} seconds between the brake and the throttle.",
                "Stay on a pedal: carry the brake a little deeper into the turn, then go straight to the throttle.",
                "less coasting, brake to the turn-in then throttle",
                x => x.CoastSeconds, true, v => $"coasting {v:0.0} seconds"));

        // Light braking is right where little slowing is needed: only a big stop braked long and soft (or slow to bite) counts.
        if (p.Braked && drop > 50)
        {
            bool soft = p.PeakBrake < 0.7 * cx.MaxBrake && p.BrakeSeconds > 1.2;
            bool slow = !double.IsNaN(p.BiteSeconds) && p.BiteSeconds > 0.4;
            if (soft || slow)
                issues.Add(new("bite", 0.4 + (soft ? 0.2 : 0),
                    soft ? $"You only used {p.PeakBrake * 100:0} percent brake, when you'll go to {cx.MaxBrake * 100:0} elsewhere."
                         : $"The brakes took {p.BiteSeconds:0.0} seconds to come in.",
                    "Brake firmly right at the marker, up to just short of the ABS, then trail it off as you turn. A short, hard stop leaves more time to turn the car.",
                    "firm on the brakes, just short of the ABS",
                    x => x.PeakBrake, false, v => $"{v * 100:0} percent peak brake"));
        }

        // The grip gap (Almeida's "the tyre sound disappears"): released the brake before turning, so for a moment the
        // tyres did nothing. Usually means braking too early, or steering and brake release not overlapping.
        if (p.GripGap < 0.6)
            issues.Add(new("gap", 0.5 + (0.6 - p.GripGap) * 2,
                "There was a gap between your braking and your turning: for a moment the tyres weren't doing anything.",
                "Start turning as you start releasing the brake, so one takes over as the other fades. If there's nothing to turn into yet when you release, you braked too early.",
                "turn in as the brake comes off",
                x => x.GripGap, false, v => $"{v * 100:0} percent of the grip through the turn-in"));

        if (p.Braked && p.BrakeAtApex >= 0.15)
            issues.Add(new("trail", 0.3 + p.BrakeAtApex * 2,
                $"You were still on {p.BrakeAtApex * 100:0} percent brake at the apex, which stops the front from turning.",
                "Trail the brake into the turn-in, but have it fully off by the apex so the car can rotate.",
                "trail it in, off by the apex",
                x => x.BrakeAtApex, true, v => $"{v * 100:0} percent brake at the apex"));
        // Brakes used like a switch (Almeida's "biggest trail braking mistake"): full pressure, then straight off.
        else if (p.Braked && drop > 40 && p.PeakBrake > 0.85 && !double.IsNaN(p.ReleaseSeconds) && p.ReleaseSeconds < 0.2)
            issues.Add(new("switch", 0.45,
                "You used the brake like a switch: full pressure, then straight off.",
                "Use the whole range: firm to just short of the ABS, then let it off gradually as the steering comes in. The last bit of brake is there to rotate the car, not slow it.",
                "ease off the brake as you turn",
                x => x.ReleaseSeconds, false, v => $"a {v:0.0} second release"));
        // Trail braking (a smooth release as the steering goes in) matters most in slow and medium corners.
        else if (p.Braked && drop > 40 && p.MinKmh < 150 && p.PeakBrake > 0.5 && !double.IsNaN(p.ReleaseSeconds) && p.ReleaseSeconds < 0.15)
            issues.Add(new("abrupt", 0.3,
                "You came off the brake all at once.",
                "Make the release as long as the steering going in, so the nose stays loaded while the car turns.",
                "ease off the brake as you turn",
                x => x.ReleaseSeconds, false, v => $"a {v:0.0} second release"));

        // Turning while the ABS is still working pushes the front; coming off it with steering in is what turns the car.
        if (p.Braked && p.SteerInAbs > 25 && p.AbsShare > 0.2)
            issues.Add(new("absturn", 0.4,
                "You were already turning while the ABS was still working, which makes the front push.",
                "Ease the pressure as the steering comes in. Coming off the ABS with some steering in is what makes the car turn.",
                "ease off the ABS as you turn",
                x => x.SteerInAbs, true, v => $"{v:0} degrees of steering in the ABS"));
        // Braking at the ABS threshold is normal in a GT car; living in it overheats the tyres.
        else if (p.Braked && p.AbsShare > 0.6 && p.BrakeSeconds > 0.8)
            issues.Add(new("abs", p.AbsShare * 0.5,
                $"You were in the ABS for {p.AbsShare * 100:0} percent of the braking.",
                "Back the peak pressure off a touch; living in the ABS overheats the tyres and costs you grip in the middle of the corner.",
                "a little less brake pressure",
                x => x.AbsShare, true, v => $"ABS for {v * 100:0} percent of the braking"));

        // Doing the corner while accelerating gives understeer: speed still falling as you turn is what rotates the car.
        if (!double.IsNaN(p.ThrottleOnM) && p.ThrottleOnM > 25 && drop > 20)
            issues.Add(new("earlythrottle", 0.35 + p.ThrottleOnM / 200,
                "You were back on the throttle before the car had turned, so you did the corner while accelerating.",
                "Stay off the power until the car has done about half the turning. Speed still falling as you turn is what rotates the car.",
                "off the power until it's pointed",
                x => x.ThrottleOnM, true, v => v >= 0 ? $"throttle {Dist(v)} before the apex" : $"throttle {Dist(-v)} after the apex"));

        if (p.ThrottleLift > 0.25)
            issues.Add(new("lift", 0.3 + p.ThrottleLift,
                "You got on the throttle, then had to lift.",
                "A lift on exit usually means the car wasn't rotated enough before the throttle. Get it turned at the slowest point, then commit.",
                "rotate first, then commit to the throttle",
                x => x.ThrottleLift, true, v => v < 0.05 ? "no lift" : $"a {v * 100:0} percent lift"));
        else if (p.HoverSeconds > 0.8 || p.ThrottleTo50Seconds > 0.7)
        {
            bool hovered = p.HoverSeconds > 0.8;
            issues.Add(new("timid", 0.3 + Math.Max(p.HoverSeconds, p.ThrottleTo50Seconds) / 3,
                hovered ? $"You hovered short of full throttle for {p.HoverSeconds:0.0} seconds." : $"You took {p.ThrottleTo50Seconds:0.0} seconds to get to half throttle.",
                "Quick to 50 percent, then to 100 as soon as the car allows. The earlier you're flat, the faster the exit.",
                "quick to 50, then commit to 100",
                hovered ? x => x.HoverSeconds : x => x.ThrottleTo50Seconds, true,
                hovered ? v => $"{v:0.0} seconds short of flat" : v => $"{v:0.0} seconds to half throttle"));
        }
        else if (!double.IsNaN(p.ThrottleOnM) && p.ThrottleOnM < -30)
            issues.Add(new("latethrottle", 0.3 + -p.ThrottleOnM / 100,
                $"The throttle came {Dist(-p.ThrottleOnM)} after the apex.",
                "Get back to the throttle at the apex, even gently, to settle the car.",
                "throttle at the apex",
                x => x.ThrottleOnM, false, v => v >= 0 ? $"throttle {Dist(v)} before the apex" : $"throttle {Dist(-v)} after the apex"));

        if (best != null && best != p)
        {
            if (p.Braked && best.Braked && p.BrakeOnM > best.BrakeOnM + 15 && p.MinKmh <= best.MinKmh + 2)
                issues.Add(new("early", (p.BrakeOnM - best.BrakeOnM) / 60,
                    $"You braked {Dist(p.BrakeOnM - best.BrakeOnM)} earlier than on your best pass.",
                    "Brake at your best marker; you've shown the car can do it.",
                    "brake at your best marker",
                    x => x.BrakeOnM, true, v => $"braking {Dist(v)} before the apex"));
            if (p.MinKmh < best.MinKmh - 4)
                issues.Add(new("slow", (best.MinKmh - p.MinKmh) / 12,
                    $"You carried {Speed(best.MinKmh - p.MinKmh)} less through the apex than your best.",
                    "Brake a little less and trust the grip; the car carries more speed than that here.",
                    "carry more speed to the apex",
                    x => x.MinKmh, false, v => $"{Speed(v)} at the apex"));
            if (!double.IsNaN(p.FullThrottleM) && !double.IsNaN(best.FullThrottleM) && p.FullThrottleM < best.FullThrottleM - 25)
                issues.Add(new("lateflat", (best.FullThrottleM - p.FullThrottleM) / 80,
                    $"You went flat {Dist(best.FullThrottleM - p.FullThrottleM)} later than on your best.",
                    "Open the steering earlier on exit so you can go flat sooner.",
                    "open the steering and go flat earlier",
                    x => x.FullThrottleM, false, v => v >= 0 ? $"flat {Dist(v)} before the apex" : $"flat {Dist(-v)} after the apex"));
            // Rotation at the slowest point (Almeida: "this is where the car has to turn the most... that's where the lap time is").
            if (best.TurnRate > 0 && p.YawAtApex > 3 && p.TurnRate < best.TurnRate * 0.85)
            {
                double bestRate = best.TurnRate;
                issues.Add(new("rotation", 0.4 + (1 - p.TurnRate / bestRate),
                    $"The car rotated less at the slowest point than on your best pass, about {p.TurnRate / bestRate * 100:0} percent of it.",
                    "The slowest point is where the car should turn the most. Keep a little brake as you turn in so the nose stays loaded, and only go to the throttle once the car is pointed.",
                    "get it rotated before the throttle",
                    x => x.TurnRate, false, v => $"{v / bestRate * 100:0} percent of your best rotation"));
            }
            if (p.SteerAtApex > best.SteerAtApex * 1.25 && p.LatGAtApex <= best.LatGAtApex + 0.05 && p.SteerAtApex > 0.05)
                issues.Add(new("push", 0.45,
                    "You needed more steering than usual for the same grip, so the car was pushing.",
                    "Slow the entry slightly and have the brake off before you turn in fully.",
                    "brake off before full steering",
                    x => x.SteerAtApex, true, v => $"{v * 57.3:0} degrees of steering"));
        }

        if (cx.MaxRpm > 0 && p.RpmAtApex > 0 && p.RpmAtApex < 0.5 * cx.MaxRpm && p.MinKmh < 160)
            issues.Add(new("revs", 0.3,
                $"Revs were low at the apex in gear {p.Gear}.",
                "Try one gear lower so the car pulls out of the corner.",
                $"try gear {p.Gear - 1}",
                x => x.RpmAtApex, false, v => $"{v:0} rpm at the apex"));

        return issues.OrderByDescending(i => i.Score).ToList();
    }

    /// <summary>"Turn 12: two tenths slower than your best. [what] [tip]"</summary>
    public static string Report(string name, Pass p, Pass? best, List<Issue> issues)
    {
        string time = best == null || best == p ? "" : " " + Units.Delta(p.Seconds - best.Seconds) + ".";
        if (best == p && best != null) time = " Your best through there yet.";
        if (issues.Count == 0) return $"{name}:{time} Clean, nothing to fix there.".Replace(": Clean", ": clean");
        var top = issues[0];
        string more = issues.Count > 1 ? $" Also: {Lower(issues[1].What)}" : "";
        return $"{name}:{time} {top.What} {top.Tip}{more}";
    }

    /// <summary>After the reminder: how the corner went on the issue that was worked on.</summary>
    public static string FollowUp(string name, Pass before, Pass now, Issue issue)
    {
        double a = issue.Metric(before), b = issue.Metric(now);
        bool better = issue.LowerIsBetter ? b < a : b > a;
        bool same = Math.Abs(b - a) <= Math.Max(Math.Abs(a), 1) * 0.08;
        string time = Units.Delta(now.Seconds - before.Seconds, "than last time");
        string verdict = same ? "About the same." : better ? "Better." : "Not this time.";
        return $"{name}: {verdict} {Capital(time)}, {issue.Words(b)}, against {OnlyTheDifference(issue.Words(a), issue.Words(b))}.";
    }

    /// <summary>"142 kilometres an hour at the apex" next to "156 kilometres an hour at the apex" -> "142": drop the words both share.</summary>
    static string OnlyTheDifference(string before, string now)
    {
        var a = before.Split(' ').ToList();
        var b = now.Split(' ');
        int suffix = 0;
        while (suffix < a.Count - 1 && suffix < b.Length - 1 && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        a.RemoveRange(a.Count - suffix, suffix);
        int prefix = 0;
        while (prefix < a.Count - 1 && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        a.RemoveRange(0, prefix);
        return string.Join(' ', a);
    }

    /// <summary>
    /// Against the reference lap: the time, then the one or two biggest differences in how it was driven, as things
    /// to copy ("The reference is 3 tenths quicker here: it brakes 20 metres later and carries 6 kilometres an hour more").
    /// </summary>
    public static string VsReference(Pass me, Pass r, Context cx)
    {
        double d = me.Seconds - r.Seconds;
        if (d < -0.05) return $"You're {Units.Delta(-d, "").Replace(" slower", "").Trim()} quicker than the reference lap here.";
        var diffs = new List<(double Score, string Words)>();
        if (me.Braked && r.Braked && !double.IsNaN(me.BrakeOnM) && !double.IsNaN(r.BrakeOnM) && me.BrakeOnM - r.BrakeOnM > 8)
            diffs.Add(((me.BrakeOnM - r.BrakeOnM) / 15, $"it brakes {Units.Distance(me.BrakeOnM - r.BrakeOnM, cx.Metric)} later"));
        if (me.Braked && !r.Braked) diffs.Add((2, "it doesn't brake at all, just lifts"));
        if (me.Braked && r.Braked && r.PeakBrake - me.PeakBrake > 0.1)
            diffs.Add(((r.PeakBrake - me.PeakBrake) * 10, $"it brakes harder, {r.PeakBrake * 100:0} percent against your {me.PeakBrake * 100:0}"));
        if (r.MinKmh - me.MinKmh > 2.5)
            diffs.Add(((r.MinKmh - me.MinKmh) / 3, $"it carries {Units.Speed(r.MinKmh - me.MinKmh, cx.Metric)} more through the apex"));
        if (!double.IsNaN(r.ThrottleOnM) && !double.IsNaN(me.ThrottleOnM) && r.ThrottleOnM - me.ThrottleOnM > 10)
            diffs.Add(((r.ThrottleOnM - me.ThrottleOnM) / 15, $"it's back on the throttle {Units.Distance(r.ThrottleOnM - me.ThrottleOnM, cx.Metric)} earlier"));
        if (!double.IsNaN(r.FullThrottleM) && !double.IsNaN(me.FullThrottleM) && r.FullThrottleM - me.FullThrottleM > 15)
            diffs.Add(((r.FullThrottleM - me.FullThrottleM) / 20, $"it's flat {Units.Distance(r.FullThrottleM - me.FullThrottleM, cx.Metric)} sooner"));
        if (r.ExitKmh - me.ExitKmh > 3)
            diffs.Add(((r.ExitKmh - me.ExitKmh) / 4, $"it leaves {Units.Speed(r.ExitKmh - me.ExitKmh, cx.Metric)} faster"));
        string time = d < 0.05 ? "You matched the reference lap here" : $"The reference lap is {Units.Delta(d, "").Replace(" slower", "").Trim()} quicker here";
        var top = diffs.OrderByDescending(x => x.Score).Take(2).Select(x => x.Words).ToList();
        return top.Count == 0 ? time + "." : $"{time}: {string.Join(" and ", top)}.";
    }

    static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;
    static string Capital(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}

/// <summary>Spoken numbers in the driver's units (iRacing's DisplayUnits).</summary>
public static class Units
{
    public static string Speed(double kmh, bool metric) =>
        metric ? $"{kmh:0} kilometres an hour" : $"{kmh / 1.609:0} miles an hour";

    public static string Distance(double metres, bool metric) =>
        metric ? $"{metres:0} metres" : $"{metres * 3.281:0} feet";

    /// <summary>"two tenths slower than your best" / "a tenth quicker" / "the same as your best".</summary>
    public static string Delta(double seconds, string than = "than your best")
    {
        double s = Math.Abs(seconds);
        if (s < 0.05) return than == "than your best" ? "the same as your best" : than == "than last time" ? "same time as last time" : $"the same {Regex.Replace(than, "^than ", "as ")}".Trim();
        string amount = s >= 0.95 ? $"{s:0.0} seconds" : (int)Math.Round(s * 10) is var t && t <= 1 ? "a tenth" : $"{(int)Math.Round(s * 10)} tenths";
        return $"{amount} {(seconds > 0 ? "slower" : "quicker")} {than}";
    }
}
