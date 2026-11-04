using System.Text.RegularExpressions;

/// <summary>
/// Questions ("how much fuel do I have?") are answered from iRacing's live data and never change anything, so a
/// question can't be mistaken for an order (it once filled the tank). No model call, so the answer is instant.
/// </summary>
public static class Questions
{
    // "do" is a question only as "do I / we / you ..." ("do the rears" is an order).
    static readonly Regex Question = new(
        @"^\s*(how|what|what's|whats|when|where|which|who|why|is|are|am|does|did|should|do (i|we|you)\b)\b|\?\s*$",
        RegexOptions.IgnoreCase);
    // "Can you fill it up?" is a request, not a question.
    static readonly Regex Request = new(@"^\s*(can|could|would|will) you\b|^\s*please\b", RegexOptions.IgnoreCase);

    public static bool IsQuestion(string said) => Question.IsMatch(said) && !Request.IsMatch(said);

    /// <summary>The spoken answer, or a short "can't check that" for questions it doesn't know.</summary>
    public static string Answer(string said, LiveTelemetry t)
    {
        string s = said.ToLowerInvariant();
        if (!t.IsConnected || !t.Has("FuelLevel")) return "I can't see the car right now.";
        bool metric = IRacing.Metric(t);

        if (Regex.IsMatch(s, @"\b(fuel|gas|tank)\b"))
        {
            string answer = $"You've got {IRacing.Fuel(t.Get("FuelLevel"), metric)}.";
            int flags = t.Has("PitSvFlags") ? (int)t.Get("PitSvFlags") : 0;
            if ((flags & 0x10) != 0) answer += $" The pit stop adds {IRacing.Fuel(t.Get("PitSvFuel"), metric)}.";
            return answer;
        }
        if (Regex.IsMatch(s, @"\blaps?\b") || Regex.IsMatch(s, @"\b(time|minutes) (left|remaining)\b|\bhow long\b"))
        {
            double laps = t.Has("SessionLapsRemainEx") ? t.Get("SessionLapsRemainEx") : double.NaN;
            double time = t.Get("SessionTimeRemain");
            if (!double.IsNaN(laps) && laps >= 0 && laps < 10000) return laps == 1 ? "Last lap." : $"{laps:0} laps to go.";
            if (!double.IsNaN(time) && time > 0 && time < 86400)
                return time >= 120 ? $"{time / 60:0} minutes left." : $"{time:0} seconds left.";
            return "I can't tell how long is left.";
        }
        if (Regex.IsMatch(s, @"\bpit\b|\bstop\b|\btyres?\b|\btires?\b|\btear|\brepair"))
            return IRacing.PitSummary(t);
        if (Regex.IsMatch(s, @"\bincidents?\b|\bx\b|\bpoints\b"))
        {
            int inc = (int)t.Get("PlayerCarMyIncidentCount");
            return inc == 0 ? "No incidents." : $"{inc} incident {(inc == 1 ? "point" : "points")}.";
        }
        return "I can't check that.";
    }
}
