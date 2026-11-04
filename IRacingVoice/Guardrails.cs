using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Corrects the small local model's tool calls where the driver's own words are unambiguous, and drops calls for
/// things the driver never mentioned ("no tyres" must not also switch off fuel). Everything stays inside iRacing:
/// a mute is voice chat, a radio channel ("channel") or one driver ("that driver") - never the game's sound.
/// </summary>
public static class Guardrails
{
    // "left" / "right" alone are everyday words ("laps left", "that's right"): only plurals or "left side" etc. count.
    const string TyreWords = @"\b(tyres?|tires?|lefts|rights|left side|right side|fronts?|rears?|all four|four)\b";
    const string AutoFuelWords = @"\b(auto[- ]?(matic )?fuel(ing)?|fuel (automatically|itself))\b";
    const string AutoFuelOff = @"\b(off|no|disable|cancel|manual|stop)\b";
    // "Left front plus two" (pressure), "wets" (compound), "bias to the front" (car setting): tyre words, but not a tyre change.
    const string NotATyreChange = @"\b(pressures?|psi|kpa|plus|minus|up|down|compound|wets?|softs?|hards?|mediums?|slicks?|bias|traction|abs)\b";

    /// <summary>The corrected call, or null to drop it.</summary>
    public static (string Name, Dictionary<string, JsonElement> Input)? Fix(string said, string name, Dictionary<string, JsonElement> input)
    {
        string s = said.ToLowerInvariant();
        bool unmute = Regex.IsMatch(s, @"\bun-?mute\b");
        bool mute = !unmute && Regex.IsMatch(s, @"\b(mute|silence)\b|\bturn off the (chat|voice)");

        // Mutes, in order of what was said: a channel, one driver, otherwise all of voice chat. Only for mute commands,
        // so "mute chat and no tyres" still changes the tyres.
        if ((mute || unmute) && name is "mute_driver" or "toggle_voice_chat" or "radio_channel")
        {
            if (s.Contains("channel"))
            {
                var m = Regex.Match(s, @"(\w+)\s+channel");
                string channel = m.Success && m.Groups[1].Value is not ("the" or "a" or "this" or "that") ? m.Groups[1].Value
                    : input.TryGetValue("channel", out var c) ? c.ToString() : "race";
                return ("radio_channel", Make(new { action = unmute ? "unmute" : "mute", channel }));
            }
            if (mute && Regex.IsMatch(s, @"\b(driver|guy|him|her|that person|whoever)\b")) return ("mute_driver", new());
            return ("toggle_voice_chat", new());
        }

        // Drop calls about things the driver didn't mention.
        bool mentioned = name switch
        {
            "set_tires" or "toggle_tires" => Regex.IsMatch(s, TyreWords) && !Regex.IsMatch(s, NotATyreChange),
            // "Auto fuel 2 laps" is the auto-fuel margin, not 2 litres.
            "set_fuel" or "set_fuel_laps" or "set_fuel_to_finish" => Regex.IsMatch(s, @"\b(fuel|litres?|liters?|gallons?|gas|fill|tank)\b") && !Regex.IsMatch(s, AutoFuelWords),
            "set_auto_fuel" => Regex.IsMatch(s, AutoFuelWords),
            "set_voice_volume" => Regex.IsMatch(s, @"\b(voice|louder|quieter|volume|speak|talk)\b"),
            "set_tearoff" => Regex.IsMatch(s, @"\b(tear|windshield|windscreen|visor)"),
            "set_fast_repair" => Regex.IsMatch(s, @"\brepair"),
            "clear_pit_service" => Regex.IsMatch(s, @"\b(clear|reset)\b"),
            "set_force_feedback" => Regex.IsMatch(s, @"\b(force|feedback|ffb|wheel)\b"),
            "send_chat" => Regex.IsMatch(s, @"\b(tell|send|say|message|chat)\b"),
            "radio_channel" => s.Contains("channel"),
            "mute_driver" or "toggle_voice_chat" => mute || unmute, // handled above; here only when no mute word was said
            "toggle_overlay" => Regex.IsMatch(s, @"\b(overlay|standings|hud)\b"),
            _ => true,
        };
        if (!mentioned) return null;

        // Tyre groups named in the sentence win over the model's list.
        if (name == "set_tires")
        {
            var tires = new HashSet<string>();
            if (Regex.IsMatch(s, @"\b(rears?|back)\b")) tires.UnionWith(["lr", "rr"]);
            if (Regex.IsMatch(s, @"\b(fronts?)\b")) tires.UnionWith(["lf", "rf"]);
            if (Regex.IsMatch(s, @"\b(lefts|left side|left[- ]hand|left (tyres|tires))\b")) tires.UnionWith(["lf", "lr"]);
            if (Regex.IsMatch(s, @"\b(rights|right side|right[- ]hand|right (tyres|tires))\b")) tires.UnionWith(["rf", "rr"]);
            if (Regex.IsMatch(s, @"\b(all four|four tyres|four tires|all tyres|all tires|tyres on|tires on)\b")) tires.UnionWith(["lf", "rf", "lr", "rr"]);
            if (Regex.IsMatch(s, @"\bno (tyres|tires)\b|\b(tyres|tires) off\b")) tires.Clear();
            else if (tires.Count == 0) return (name, input);
            input["tires"] = Element(tires.ToArray());
        }

        // Chat: drop the "send ... to everyone" wrapping the model sometimes copies into the message.
        if (name == "send_chat" && input.TryGetValue("message", out var msg) && msg.ValueKind == JsonValueKind.String)
        {
            var m = Regex.Match(msg.GetString()!, @"^(?:send|say|tell everyone)\s+(.+?)(?:\s+to\s+(?:everyone|all|everybody))?$", RegexOptions.IgnoreCase);
            if (m.Success) input["message"] = Element(m.Groups[1].Value);
        }
        return (name, input);
    }

    /// <summary>
    /// The pit basics read straight from the words (tyres, fuel, tear-off, fast repair), to fill in what the model
    /// missed. Empty if none of them was mentioned.
    /// </summary>
    public static List<(string Name, Dictionary<string, JsonElement> Input)> FromWords(string said)
    {
        string s = WordsToDigits(said.ToLowerInvariant());
        var calls = new List<(string, Dictionary<string, JsonElement>)>();

        if (Regex.IsMatch(s, TyreWords) && !Regex.IsMatch(s, NotATyreChange)
            && Fix(said, "set_tires", new()) is { } tires && tires.Input.ContainsKey("tires"))
            calls.Add(tires);

        var fuel = Regex.Match(s, @"(\d+(?:\.\d+)?)\s*(litres?|liters?|l\b|gallons?|gal\b)");
        if (Regex.IsMatch(s, AutoFuelWords)) calls.Add(("set_auto_fuel", Make(new { on = !Regex.IsMatch(s, AutoFuelOff) })));
        else if (Regex.IsMatch(s, @"\bno (fuel|gas)\b")) calls.Add(("set_fuel", Make(new { amount = 0, unit = "liters" })));
        else if (fuel.Success)
            calls.Add(("set_fuel", Make(new { amount = double.Parse(fuel.Groups[1].Value), unit = fuel.Groups[2].Value.StartsWith('g') ? "gallons" : "liters" })));
        else if (Regex.Match(s, @"\bfuel\s+(\d+(?:\.\d+)?)\b|\b(\d+(?:\.\d+)?)\s+(?:of\s+)?fuel\b") is { Success: true } bare)
            calls.Add(("set_fuel", Make(new { amount = double.Parse(bare.Groups[1].Success ? bare.Groups[1].Value : bare.Groups[2].Value) })));
        else if (Regex.IsMatch(s, @"\bfill (it |her )?up\b")) calls.Add(("set_fuel", Make(new { amount = 200, unit = "liters" })));

        if (Regex.IsMatch(s, @"\btear[- ]?offs?\b"))
            calls.Add(("set_tearoff", Make(new { on = !Regex.IsMatch(s, @"\bno tear|tear[- ]?offs? off\b") })));
        if (Regex.IsMatch(s, @"\bfast repair"))
            calls.Add(("set_fast_repair", Make(new { on = !Regex.IsMatch(s, @"\bno fast repair|fast repair off\b") })));
        return calls;
    }

    static readonly Dictionary<string, int> Ones = new()
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };
    static readonly Dictionary<string, int> Tens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>"forty five litres" -> "45 litres" (Whisper usually writes digits, but not always). "all four" is left alone.</summary>
    static string WordsToDigits(string s) =>
        Regex.Replace(s, @"\b(twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:[- ](one|two|three|four|five|six|seven|eight|nine))?\b|\b(one|two|three|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen)\b(?=\s*(litres?|liters?|gallons?|(of\s+)?fuel))|(?<=\bfuel\s)(one|two|three|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen)\b",
            m => m.Groups[1].Success ? (Tens[m.Groups[1].Value] + (m.Groups[2].Success ? Ones[m.Groups[2].Value] : 0)).ToString()
               : Ones[m.Groups[3].Success ? m.Groups[3].Value : m.Groups[6].Value].ToString());

    static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    static Dictionary<string, JsonElement> Make(object value) =>
        JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
