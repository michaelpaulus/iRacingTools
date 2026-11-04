using System.Globalization;
using System.Text;
/// <summary>
/// Answers spoken questions over the "radio" using Claude, grounded in the live session data. Claude is asked through
/// the Claude CLI, signed in with your Claude subscription (no API key).
/// </summary>
public sealed class Engineer
{
    readonly CoachConfig _cfg;
    readonly string _system;
    readonly List<(string Q, string A)> _radio = new();

    public bool Available => ClaudeCli.Find() != null;

    public Engineer(CoachConfig cfg, string coachingNotes, ReferenceLap reference)
    {
        _cfg = cfg;
        _system = BuildSystemPrompt(coachingNotes, reference);
    }

    static string BuildSystemPrompt(string notes, ReferenceLap r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            You are the driver's race engineer and driving coach, talking to them over team radio while they drive in iRacing.
            Your reply is read aloud by a text-to-speech voice while they are driving at speed, so:
            - Answer in one to three short sentences. Lead with the answer.
            - Plain spoken English only: no markdown, lists, symbols, or abbreviations like km/h (say "kilometres per hour" or just the number).
            - Round numbers the way an engineer would on the radio ("about four tenths", "two laps of fuel").
            - Base what you say on the session data provided. If the data doesn't cover the question, say so briefly rather than guessing.
            - The question comes from speech recognition and may have misheard words; interpret it charitably in a racing context.
            - Coaching should be one concrete thing to try, tied to a named corner where possible.
            """);
        sb.AppendLine();
        sb.AppendLine("Coaching notes from earlier analysis of this driver's telemetry:");
        sb.AppendLine(notes.Trim());
        sb.AppendLine();
        sb.AppendLine($"Reference lap: {r.Name}, {r.LapTime:F3} s. Corners are detected from the reference lap; km is distance from the start/finish line.");
        sb.AppendLine("corner | apex km | ref apex km/h | ref gear | ref braking g | ref brake released m before apex | ref roll s");
        for (int i = 0; i < r.Corners.Count; i++)
        {
            var c = r.Corners[i];
            var s = r.Stats[i];
            if (s == null) continue;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} | {1:F2} | {2:F0} | {3} | {4:F2} | {5:F0} | {6:F2}",
                c.Name, c.ApexKm, s.MinKmh, s.Gear, s.BrakingDecelG, s.ReleaseMBeforeApex, s.RollSeconds));
        }
        sb.AppendLine("""

            Column meanings in the session tables: "seconds lost" is time lost to the reference through that corner segment (negative = faster);
            "braking g" is average deceleration while on the brakes; "brake released m before apex" is how early the driver came fully off the brake;
            "roll s" is the time between releasing the brake and picking up the throttle.
            """);
        return sb.ToString();
    }

    public async Task<string> AskAsync(string question, string snapshot)
    {
        if (!Available) return "I can't answer questions yet. Install the Claude command line and sign in with your Claude account.";

        var user = new StringBuilder();
        if (_radio.Count > 0)
        {
            user.AppendLine("Earlier on the radio this session:");
            foreach (var (q, a) in _radio.TakeLast(4)) user.AppendLine($"Driver: {q}\nEngineer: {a}");
            user.AppendLine();
        }
        user.AppendLine("Live session data right now:");
        user.AppendLine(snapshot);
        user.AppendLine();
        user.AppendLine($"Driver asks: {question}");

        var answer = await ClaudeCli.AskAsync(_system, user.ToString(), _cfg.Model, TimeSpan.FromSeconds(60), _cfg.Effort);
        if (answer.Text == null)
        {
            Console.WriteLine($"  [engineer] {answer.Error}");
            return "The engineer had an error, check the console.";
        }
        if (answer.Text.Length == 0) return "No answer came back, try again.";
        _radio.Add((question, answer.Text));
        return answer.Text;
    }
}
