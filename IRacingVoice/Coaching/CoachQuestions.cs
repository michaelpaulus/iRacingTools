using System.Text.RegularExpressions;

/// <summary>Coaching questions the voice agent hands to the coach: "how was that corner?", "how was turn 7?", "how was that lap?", "where am I losing time?".</summary>
public static class CoachQuestions
{
    static readonly Dictionary<string, int> Numbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40,
    };

    /// <summary>The coach's answer, or null when it isn't a coaching question.</summary>
    public static string? Answer(string said, Coach coach)
    {
        string s = said.ToLowerInvariant();
        if (Regex.IsMatch(s, @"\b(that's enough|thats enough|stop coaching|forget it|never mind|nevermind)\b")) return coach.Stop();
        if (Regex.IsMatch(s, @"\bwhat should i (work on|focus on|practi[cs]e)\b|\b(biggest|main) (problem|issue|habit|weakness)\b|\bwhat am i doing wrong\b|\boverall\b"))
            return coach.Habit();

        if (Regex.IsMatch(s, @"\b(reference|comparison) lap\b")) return coach.ReferenceInfo();

        var turn = Regex.Match(s, @"\b(?:turn|corner)\s+(\d+|[a-z]+(?:[- ][a-z]+)?)\b");
        if (turn.Success && Number(turn.Groups[1].Value) is int n && Regex.IsMatch(s, @"\b(how|what|was|did)\b")) return coach.Corner(n);
        if (Regex.IsMatch(s, @"\b(how|what|was|did)\b") && coach.CornerNamed(s) is { } named) return named;

        if (Regex.IsMatch(s, @"\b(that|the|last|previous|this) (corner|turn|bend)\b|\bhow did i do\b|\bwhat (can|could|should) i (do )?(better|improve)\b"))
            return coach.LastCorner();
        if (Regex.IsMatch(s, @"\b(that|the|last|my) lap\b") && Regex.IsMatch(s, @"\b(how|what)\b")) return coach.LastLap();
        if (Regex.IsMatch(s, @"\b(los(e|ing)|lost|slow(est)?|wors[te])\b.*\b(time|corner|turn|where)\b|\bwhere\b.*\b(los(e|ing)|slow)\b|\b(which|what) (corner|turn)s?\b.*\b(slow(est)?|wors[te]|los(e|ing)|improve)\b"))
            return coach.WhereLosing();
        return null;
    }

    static int? Number(string word)
    {
        if (int.TryParse(word, out var n)) return n;
        var parts = word.Split(' ', '-');
        if (parts.Length == 2 && Numbers.TryGetValue(parts[0], out var tens) && Numbers.TryGetValue(parts[1], out var ones)) return tens + ones;
        return Numbers.TryGetValue(word, out var single) ? single : null;
    }
}
