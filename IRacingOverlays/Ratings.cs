/// <summary>
/// iRating maths. iRacing does not publish its formula; this is the community reverse-engineered version
/// (expected score from pairwise win chances on a 1600/ln 2 scale) that third-party calculators use, so treat the
/// result as a close estimate rather than the official number.
/// </summary>
public static class IRatingMath
{
    static readonly double Br1 = 1600 / Math.Log(2);

    /// <summary>Unrated / missing ratings count as a new member's 1350.</summary>
    public static int Normalize(int ir) => ir > 0 ? ir : 1350;

    public static int StrengthOfField(IEnumerable<int> ratings)
    {
        var r = ratings.Select(Normalize).ToList();
        if (r.Count == 0) return 0;
        return (int)Math.Round(Br1 * Math.Log(r.Count / r.Sum(x => Math.Exp(-x / Br1))));
    }

    /// <summary>Chance that a driver rated <paramref name="a"/> finishes ahead of one rated <paramref name="b"/>.</summary>
    static double Chance(double a, double b)
    {
        double ea = Math.Exp(-a / Br1), eb = Math.Exp(-b / Br1);
        return (1 - ea) * eb / ((1 - eb) * ea + (1 - ea) * eb);
    }

    /// <summary>
    /// Projected iRating change for each driver of one class.
    /// <paramref name="positions"/> are 1-based finishing positions within the class; drivers who did not start
    /// (<paramref name="started"/> false) should be placed last.
    /// </summary>
    public static double[] Changes(IReadOnlyList<int> ratings, IReadOnlyList<int> positions, IReadOnlyList<bool> started)
    {
        int n = ratings.Count;
        var result = new double[n];
        if (n < 2) return result;

        var ir = ratings.Select(r => (double)Normalize(r)).ToArray();
        var expected = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = 0; j < n; j++) sum += Chance(ir[i], ir[j]);
            expected[i] = sum - 0.5; // drop the 0.5 a driver scores against themselves
        }

        int nonStarters = started.Count(s => !s), starters = n - nonStarters;
        if (starters == 0) return result;

        double starterSum = 0;
        for (int i = 0; i < n; i++)
        {
            if (!started[i]) continue;
            double fudge = ((n - nonStarters / 2.0) / 2 - positions[i]) / 100;
            result[i] = (n - positions[i] - expected[i] - fudge) * 200 / starters;
            starterSum += result[i];
        }

        if (nonStarters > 0)
        {
            double avgExpected = Enumerable.Range(0, n).Where(i => !started[i]).Average(i => expected[i]);
            for (int i = 0; i < n; i++)
                if (!started[i]) result[i] = -starterSum / nonStarters * expected[i] / avgExpected;
        }
        return result;
    }
}

/// <summary>
/// Safety-rating estimate. iRacing keeps the formula private; what is known and used here:
/// <list type="bullet">
/// <item>SR follows corners per incident (CPI) against a bar that rises with licence class (community estimates:
/// ~15-20 corners per incident holds a D steady, ~70-80 an A). So each corner earns a little and each incident point
/// costs as much as that class's target CPI in corners.</item>
/// <item>Races count fully, other sessions with reduced weight (iRacing's help article; the figures are third-party).</item>
/// <item>Crossing a whole number adds 0.40, crossing one downward takes 0.40 (iRacing's help article).</item>
/// </list>
/// Not modelled: the real system weighs this session against your recent history (a rolling window of corners), which
/// the SDK doesn't expose. The rates live in overlays.json for tuning against your own results.
/// </summary>
public static class SafetyRatingEstimate
{
    /// <summary>Share of the SR effect a session type carries; 0 = doesn't change SR.</summary>
    public static double SessionWeight(string type)
    {
        if (type.Equals("Race", StringComparison.OrdinalIgnoreCase) || type.Contains("Heat", StringComparison.OrdinalIgnoreCase)) return 1;
        if (type.Contains("Lone", StringComparison.OrdinalIgnoreCase) || type.Contains("Time Trial", StringComparison.OrdinalIgnoreCase)) return 0.35;
        if (type.Contains("Qualify", StringComparison.OrdinalIgnoreCase) || type.Contains("Warmup", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Practice", StringComparison.OrdinalIgnoreCase)) return 0.5;
        return 0; // offline testing and anything unknown
    }

    /// <summary>Estimated SR change, including the whole-number bump.</summary>
    public static double Change(double corners, int incidents, double currentSr, string license, double weight, StandingsConfig cfg)
    {
        double targetCpi = cfg.SrTargetCpi.TryGetValue(license, out var cpi) ? cpi : cfg.SrTargetCpi.GetValueOrDefault("A", 75);
        double raw = weight * cfg.SrGainPerCorner * (Math.Max(0, corners) - Math.Max(0, incidents) * targetCpi);
        return Apply(currentSr, raw, cfg.SrWholeNumberBump) - currentSr;
    }

    /// <summary>SR after a raw change, with the bump for each whole number crossed, kept within 0.00-4.99.</summary>
    public static double Apply(double sr, double raw, double bump)
    {
        double after = sr + raw;
        int crossed = (int)Math.Floor(Math.Min(after, 4.99)) - (int)Math.Floor(sr);
        return Math.Clamp(after + crossed * bump, 0, 4.99);
    }

    /// <summary>Corners per lap: iRacing's turn count for the track, or a guess from the track type and length.</summary>
    public static int CornersPerLap(SessionData s)
    {
        if (s.TrackTurns > 0) return s.TrackTurns;
        if (s.Category.Contains("Oval", StringComparison.OrdinalIgnoreCase)) return 4;
        return Math.Max(4, (int)Math.Round(s.TrackLengthKm * 2.7));
    }
}
