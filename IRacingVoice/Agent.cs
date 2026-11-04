using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// Turns what the driver said into actions and answers. One model request per command (Groq by default): the model
/// picks the tools - pit service, voice chat, chat, force feedback, and the fuel, session and coaching answers - we run
/// them and speak the results, so there's no second round trip while you're driving. Safety nets: a question never
/// changes anything, guardrails drop actions about things that weren't mentioned, and the pit basics are read from the
/// words if the model misses them.
/// </summary>
public sealed class Agent
{
    readonly VoiceConfig _cfg;
    readonly LiveTelemetry _telemetry;
    readonly LlmClient _llm;
    readonly List<(string Said, string Done)> _recent = new();

    /// <summary>Report the actions instead of doing them (testing without touching iRacing or the keyboard).</summary>
    public bool DryRun { get; set; }
    /// <summary>The voice coach, when running live.</summary>
    public Coach? Coach { get; set; }
    public FuelTracker? Fuel { get; set; }
    public Speaker? Voice { get; set; }

    public Agent(VoiceConfig cfg, LiveTelemetry telemetry, LlmClient llm)
    {
        _cfg = cfg;
        _telemetry = telemetry;
        _llm = llm;
    }

    // Tools that change something; a question may only use the others (answers).
    static readonly HashSet<string> Actions =
    [
        "set_tires", "toggle_tires", "set_fuel", "set_fuel_laps", "set_fuel_to_finish", "set_auto_fuel", "set_tearoff", "set_fast_repair",
        "clear_pit_service", "set_tire_pressure", "set_tire_compound", "radio_channel", "radio_scan", "mute_driver",
        "toggle_voice_chat", "send_chat", "set_force_feedback", "set_voice_volume",
        "toggle_overlay", "telemetry_logging", "capture", "replay", "camera",
    ];

    const string SystemPrompt = """
        You are the race engineer's voice assistant for a driver racing in iRacing right now. They speak a short command
        or question while driving; you act or answer by calling tools. The text comes from speech recognition and may
        have misheard words: read it charitably in a racing context ("tires of" = "tyres off", "tear of" = "tear-off").

        Rules:
        - Always use tools: actions for commands, the status and coach tools for questions. Several things in one
          sentence = one tool call each.
        - A question never changes anything: "how much fuel do I have" is fuel_status, not set_fuel.
        - Tyres: "no tyres" / "tyres off" = set_tires []. "All four" / "tyres on" = all four. "Lefts" = lf,lr; "rights" =
          rf,rr; "fronts" = lf,rf; "rears" = lr,rr. "Toggle tyres" = toggle_tires.
        - Fuel: "turn off fuel" / "no fuel" = set_fuel amount 0. A plain amount = set_fuel with the number as said (give
          unit only if they said one). "N laps of fuel" = set_fuel_laps. "Enough to finish" = set_fuel_to_finish.
          "Fill it up" = set_fuel amount 200 unit liters. "Auto fuel on/off" = set_auto_fuel; "auto fuel with 2 laps
          margin" = set_auto_fuel on, margin_laps 2 (a number after "auto fuel" is the margin, never set_fuel).
        - Pressures: "left front plus 2" = set_tire_pressure tires [lf] change 2; "all pressures 26" = all four, pressure
          26. Unit only if they said one. "Wets / softs / switch compound" = set_tire_compound.
        - "Mute" means iRacing voice chat (toggle_voice_chat), a radio channel (radio_channel, when they say "channel")
          or one driver (mute_driver). Never the game's sound. "Your voice / speak louder" = set_voice_volume.
          "Talk on / join / leave the X channel" = radio_channel transmit / add / remove; "scan on/off" = radio_scan.
        - Questions: position / gap to the car ahead in the race = position; who's around me on track / who's that
          behind = nearby_cars; last / best lap / am I up on my best = lap_times; track temp / rain / wind = weather;
          water / oil temps / tyre wear / warnings = car_status.
        - "Start / stop logging" = telemetry_logging; "screenshot / start recording" = capture; "show me that again /
          back to live" = replay; "watch car 12 / cockpit cam" = camera.
        - Coaching: "how was that corner / what can I do better / what did I do wrong" = coach_last_corner; a corner
          number or name = coach_corner; "how was that lap" = coach_last_lap; "where am I losing time / slowest corner" =
          coach_where_losing; "what should I work on / my biggest problem" = coach_habit; "stop / that's enough" = coach_stop;
          "what's the reference lap / how far off the reference am I" = coach_reference.
        - send_chat only when they ask to send or tell someone a message; message = just the words to send.
        - Any other question (racing, setups, rules, the track, general knowledge, anything at all): answer it yourself
          with no tool. Short and to the point: lead with the answer, one or two short sentences, at most about 30 words.
          It's read aloud while they drive, so plain spoken English: no lists, markdown or symbols, round the numbers.
          If you don't know, say so in a few words.
        - Anything else that fits no tool: one short spoken sentence and no tool.
        """;

    JsonArray Tools()
    {
        static JsonObject Tool(string name, string description, JsonObject? properties = null, params string[] required) => new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties ?? new JsonObject(),
                    ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
                },
            },
        };
        static JsonObject P(string type, string? description = null, string[]? options = null)
        {
            var o = new JsonObject { ["type"] = type };
            if (description != null) o["description"] = description;
            if (options != null) o["enum"] = new JsonArray(options.Select(e => (JsonNode)e).ToArray());
            return o;
        }
        return
        [
            // Pit service
            Tool("set_tires", "Choose which tyres are changed at the next pit stop. Empty list = no tyre change.",
                new() { ["tires"] = new JsonObject { ["type"] = "array", ["items"] = P("string", options: ["lf", "rf", "lr", "rr"]) } }, "tires"),
            Tool("toggle_tires", "Flip the tyre change: if any tyres are selected, none; if none, all four."),
            Tool("set_fuel", "Set how much fuel is added at the next pit stop. amount 0 = no fuel. unit only if the driver said one.",
                new() { ["amount"] = P("number"), ["unit"] = P("string", options: ["liters", "gallons"]) }, "amount"),
            Tool("set_fuel_laps", "Add enough fuel for this many laps (uses the measured fuel per lap).", new() { ["laps"] = P("number") }, "laps"),
            Tool("set_fuel_to_finish", "Add enough fuel to finish the race."),
            Tool("set_auto_fuel", "iRacing's auto fuel on or off: the crew adds what's needed to finish, plus an optional margin in laps.",
                new() { ["on"] = P("boolean"), ["margin_laps"] = P("number", "Extra laps of fuel, only if the driver gave one.") }, "on"),
            Tool("set_tearoff", "Windshield tear-off at the next pit stop on or off.", new() { ["on"] = P("boolean") }, "on"),
            Tool("set_fast_repair", "Use a fast repair at the next pit stop on or off.", new() { ["on"] = P("boolean") }, "on"),
            Tool("clear_pit_service", "Reset the whole pit stop. Only when the driver says clear or reset the pit stop."),
            Tool("set_tire_pressure", "Cold pressure for tyres changed at the next stop (also selects them): an absolute pressure or a change.",
                new()
                {
                    ["tires"] = new JsonObject { ["type"] = "array", ["items"] = P("string", options: ["lf", "rf", "lr", "rr"]) },
                    ["pressure"] = P("number"), ["change"] = P("number", "e.g. +2 or -1.5"), ["unit"] = P("string", options: ["psi", "kpa"]),
                }, "tires"),
            Tool("set_tire_compound", "Tyre compound for the next stop, as the driver said it (wet, soft, hard, dry...).",
                new() { ["compound"] = P("string") }, "compound"),
            // Voice chat, chat, car, assistant
            Tool("radio_channel", "An iRacing radio channel by name: mute, unmute, transmit on it, add (join) or remove (leave) it.",
                new() { ["action"] = P("string", options: ["mute", "unmute", "transmit", "add", "remove"]), ["channel"] = P("string") }, "action", "channel"),
            Tool("radio_scan", "Listen on all radio channels (scan) or not.", new() { ["on"] = P("boolean") }, "on"),
            Tool("mute_driver", "Mute the driver talking on iRacing voice chat."),
            Tool("toggle_voice_chat", "Mute or unmute all of iRacing's voice chat."),
            Tool("send_chat", "Send a text chat message in iRacing.",
                new() { ["message"] = P("string", "Just the words to send."), ["car_number"] = P("string", "Only for a private message to one car.") }, "message"),
            Tool("set_force_feedback", "Wheel force feedback strength (iRacing max force) in newton-metres.", new() { ["newton_meters"] = P("number") }, "newton_meters"),
            Tool("set_voice_volume", "Volume of this assistant's own voice: level_percent (0-100) or change_percent (e.g. +20).",
                new() { ["level_percent"] = P("number"), ["change_percent"] = P("number") }),
            Tool("toggle_overlay", "Show or hide the standings overlay."),
            Tool("telemetry_logging", "Start or stop iRacing's telemetry recording (.ibt file).", new() { ["on"] = P("boolean") }, "on"),
            Tool("capture", "iRacing's own capture: a screenshot, or start / stop video recording.",
                new() { ["action"] = P("string", options: ["screenshot", "start_video", "stop_video"]) }, "action"),
            Tool("replay", "Replay: watch the last seconds again, pause, play, slow motion, or back to live.",
                new() { ["action"] = P("string", options: ["instant_replay", "pause", "play", "slow_motion", "live"]), ["seconds"] = P("number") }, "action"),
            Tool("camera", "Point iRacing's camera at a car and/or switch the camera view (cockpit, chase, TV, nose...). For spectating and replays.",
                new() { ["car_number"] = P("string", "\"me\" for the driver's own car."), ["view"] = P("string") }),
            // Answers
            Tool("position", "The driver's race position and the gaps to the cars ahead and behind in the class."),
            Tool("nearby_cars", "Who is just ahead and behind on track right now, any class or lap, and how far."),
            Tool("lap_times", "Last lap, best lap and the live delta to the best."),
            Tool("weather", "Track and air temperature, wetness, rain, wind and sky."),
            Tool("car_status", "Water and oil temperatures, engine warnings and tyre wear and temperatures from the last stop."),
            Tool("fuel_status", "Fuel in the car, fuel per lap, laps it lasts and what's needed to finish."),
            Tool("session_status", "Laps or time left in the session."),
            Tool("pit_status", "What the next pit stop is set to."),
            Tool("incidents", "The driver's incident count."),
            Tool("coach_last_corner", "Coaching on the corner just driven: what went wrong and how to fix it."),
            Tool("coach_corner", "Coaching on a corner by turn number or by its name (e.g. Eau Rouge, the Corkscrew).",
                new() { ["number"] = P("integer"), ["name"] = P("string", "The corner's name as the driver said it.") }),
            Tool("coach_last_lap", "How the last lap went against the best."),
            Tool("coach_where_losing", "Where the driver is losing the most time."),
            Tool("coach_habit", "The driver's most common technique habit to work on."),
            Tool("coach_stop", "Stop the coaching reminder."),
            Tool("coach_reference", "Which reference (comparison) lap the coach is using and how the driver's best compares."),
        ];
    }

    /// <summary>Gets the model ready ahead of the first command (starts the Claude CLI).</summary>
    public void Warm() => _llm.Warm(new JsonArray(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt }), Tools());

    /// <summary>Carries out what was said; returns what to say back.</summary>
    public async Task<string> Handle(string said)
    {
        bool question = Questions.IsQuestion(said);
        var user = new StringBuilder();
        if (_recent.Count > 0)
        {
            user.AppendLine("Earlier this session:");
            foreach (var (s, d) in _recent.TakeLast(3)) user.AppendLine($"Driver: {s}\nYou: {d}");
            user.AppendLine();
        }
        user.AppendLine(IRacing.PitState(_telemetry));
        user.AppendLine();
        user.AppendLine($"Driver says: {said}");

        var reply = await _llm.Chat(
            new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = user.ToString() }),
            Tools());
        if (reply != null) Console.WriteLine($"  [model] {reply.Provider} {reply.Ms} ms, {reply.ToolCalls.Count} tool call(s)");

        var calls = new List<(string Name, Dictionary<string, JsonElement> Input)>();
        foreach (var (name, args) in reply?.ToolCalls ?? [])
        {
            var input = Arguments(args);
            if (question && Actions.Contains(name)) continue; // a question never changes anything
            if (!Actions.Contains(name)) { calls.Add((name, input)); continue; }
            if (Guardrails.Fix(said, name, input) is { } fixedCall) calls.Add(fixedCall); // else: something they didn't ask for
        }
        if (!question)
            calls.AddRange(Guardrails.FromWords(said).Where(w => calls.All(c => c.Name != w.Name))); // pit basics the model missed

        var done = new List<string>();
        var seen = new HashSet<string>();
        foreach (var (name, input) in calls)
        {
            if (!seen.Add(name + JsonSerializer.Serialize(input))) continue; // a toggle twice would undo itself
            string result;
            if (DryRun && Actions.Contains(name) && name != "set_fuel") result = $"(would {name})";
            else try { result = Run(name, input, said); }
            catch (Exception ex) { result = $"Couldn't {name.Replace('_', ' ')}: {ex.Message}"; }
            Console.WriteLine($"  [tool] {name} {JsonSerializer.Serialize(input)} -> {result}");
            done.Add(result);
        }

        string text = reply?.Text ?? "";
        if (done.Count == 0 && text.Length == 0)
        {
            // Nothing usable came back: answer from the words where possible.
            if (Coach != null && CoachQuestions.Answer(said, Coach) is { } coaching) text = coaching;
            else if (question) text = Questions.Answer(said, _telemetry);
            else if (reply == null) text = _cfg.Models.Count == 0 ? "No model is set up." : NoModelReason();
        }
        string answer = done.Count > 0 ? string.Join(" ", done) : text.Length > 0 ? text : "Didn't catch that.";
        _recent.Add((said, answer));
        return answer;
    }

    string NoModelReason() =>
        _cfg.Models.Any(m => m.Kind.Equals("claude-cli", StringComparison.OrdinalIgnoreCase)) && ClaudeCli.Find() == null
            ? "The Claude command line isn't installed."
        : _cfg.Models.All(m => m.KeyVariable.Length > 0 && Environment.GetEnvironmentVariable(m.KeyVariable) == null
                                                     && Environment.GetEnvironmentVariable(m.KeyVariable, EnvironmentVariableTarget.User) == null)
            ? $"I need a key: set {_cfg.Models[0].KeyVariable}."
            : "I can't reach the model right now.";

    /// <summary>Tool arguments as a dictionary (some models send them as a JSON string, others as an object).</summary>
    static Dictionary<string, JsonElement> Arguments(JsonNode? node)
    {
        try
        {
            var element = node is JsonValue v && v.TryGetValue(out string? s)
                ? (string.IsNullOrWhiteSpace(s) ? JsonDocument.Parse("{}").RootElement : JsonDocument.Parse(s).RootElement)
                : JsonSerializer.SerializeToElement(node);
            return element.ValueKind == JsonValueKind.Object
                ? element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new();
        }
        catch (JsonException) { return new(); }
    }

    string Run(string name, IReadOnlyDictionary<string, JsonElement> input, string said)
    {
        bool Bool(string key) => input.TryGetValue(key, out var v) && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && v.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true));
        double? Num(string key) => input.TryGetValue(key, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d) ? d : null
            : null;
        string Str(string key) => input.TryGetValue(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString() : "";
        bool metric = IRacing.Metric(_telemetry);

        string SetTires(HashSet<string> tires)
        {
            IRacing.PitCommand(IRacing.Pit.ClearTires);
            foreach (var (key, cmd) in new[] { ("lf", IRacing.Pit.LF), ("rf", IRacing.Pit.RF), ("lr", IRacing.Pit.LR), ("rr", IRacing.Pit.RR) })
                if (tires.Contains(key)) IRacing.PitCommand(cmd);
            return tires.Count switch { 0 => "No tyres.", 4 => "Four tyres.", _ => $"Changing {string.Join(", ", tires.Select(t => t.ToUpperInvariant()))}." };
        }

        string SetFuelLitres(double liters, string? say = null)
        {
            if (liters < 0.5) { IRacing.PitCommand(IRacing.Pit.ClearFuel); return "No fuel."; }
            int sent = (int)Math.Round(liters); // the SDK takes whole litres
            IRacing.PitCommand(IRacing.Pit.Fuel, sent);
            return say ?? $"Fuel, {IRacing.Fuel(sent, metric)}.";
        }

        // Cold pressures go with the tyre change: the SDK's tyre command takes the pressure in kPa (0 = keep it).
        string SetPressures()
        {
            const double KpaPerPsi = 6.89476;
            var tires = input.TryGetValue("tires", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList() : [];
            if (tires.Count == 0) tires = ["lf", "rf", "lr", "rr"];
            bool psi = Str("unit") == "psi" || Regex.IsMatch(said, @"\bpsi\b", RegexOptions.IgnoreCase)
                || (Str("unit") != "kpa" && !Regex.IsMatch(said, @"\b(kpa|kilopascals?)\b", RegexOptions.IgnoreCase) && !metric);
            double perUnit = psi ? KpaPerPsi : 1;
            var set = new List<(string Tire, double Kpa)>();
            foreach (var tire in tires)
            {
                var (cmd, var) = tire.ToLowerInvariant() switch
                {
                    "lf" => (IRacing.Pit.LF, "LF"), "rf" => (IRacing.Pit.RF, "RF"), "lr" => (IRacing.Pit.LR, "LR"), _ => (IRacing.Pit.RR, "RR"),
                };
                double now = _telemetry.Get($"PitSv{var}P") is var p && p > 0 ? p : _telemetry.Get($"{var}coldPressure");
                double? kpa = Num("pressure") is double abs ? abs * perUnit
                    : Num("change") is double ch && now > 0 ? now + ch * perUnit : null;
                IRacing.PitCommand(cmd, kpa is double k ? (int)Math.Round(k) : 0);
                set.Add((var, kpa ?? now));
            }
            string Show(double kpa) => kpa <= 0 ? "?" : psi ? $"{kpa / KpaPerPsi:0.#} psi" : $"{kpa:0} kPa";
            if (Num("pressure") == null && Num("change") == null) return $"Changing {string.Join(", ", set.Select(s => s.Tire))}.";
            return set.Select(s => Show(s.Kpa)).Distinct().Count() == 1
                ? $"{(set.Count == 4 ? "All four" : string.Join(", ", set.Select(s => s.Tire)))} at {Show(set[0].Kpa)}."
                : string.Join(", ", set.Select(s => $"{s.Tire} {Show(s.Kpa)}")) + ".";
        }

        string SetCompound(string wanted)
        {
            var compounds = SessionYaml.TireCompounds(_telemetry.SessionInfo);
            if (compounds.Count <= 1) return "This car only has one compound.";
            string w = wanted.ToLowerInvariant().TrimEnd('s');
            var pick = w is "dry" or "slick"
                ? compounds.FirstOrDefault(c => !c.Type.Contains("wet", StringComparison.OrdinalIgnoreCase))
                : compounds.FirstOrDefault(c => c.Type.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (pick.Type == null) return $"This car has {string.Join(", ", compounds.Select(c => c.Type))}.";
            IRacing.PitCommand(IRacing.Pit.Compound, pick.Index);
            return $"{pick.Type} tyres at the next stop.";
        }

        string Camera(string carNumber, string view)
        {
            string yaml = _telemetry.SessionInfo;
            var cars = SessionYaml.Cars(yaml);
            var groups = SessionYaml.CameraGroups(yaml);
            if (groups.Count == 0) return "I can't see iRacing's cameras right now.";
            // The car: "me", a number, or whoever the camera is on now.
            string num = carNumber.Trim().TrimStart('#');
            if (num.Length == 0 || Regex.IsMatch(num, @"^(me|mine|my car|myself)$", RegexOptions.IgnoreCase))
            {
                int idx = num.Length == 0 && _telemetry.Has("CamCarIdx") ? (int)_telemetry.Get("CamCarIdx") : SessionYaml.PlayerIdx(yaml);
                num = cars.FirstOrDefault(c => c.Idx == idx)?.Number ?? "";
            }
            if (!int.TryParse(Regex.Match(num, @"\d+").Value, out int car)) return "Which car?";
            // The view: a camera group by name, or the current one.
            string v = view.Trim().ToLowerInvariant();
            if (v is "tv" or "television") v = "tv1";
            var group = v.Length == 0 ? groups.FirstOrDefault(g => g.Num == (int)_telemetry.Get("CamGroupNumber"))
                : groups.FirstOrDefault(g => g.Name.Equals(v, StringComparison.OrdinalIgnoreCase))
                  is { Name: not null } exact ? exact : groups.FirstOrDefault(g => g.Name.Contains(v, StringComparison.OrdinalIgnoreCase));
            if (group.Name == null) return $"No {view} camera here.";
            IRacing.Camera(car, group.Num);
            return $"{group.Name} on car {car}.";
        }

        switch (name)
        {
            case "set_tires":
                return SetTires(input.TryGetValue("tires", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>());
            case "toggle_tires":
                return SetTires(IRacing.TyresSelected(_telemetry) ? new() : new() { "lf", "rf", "lr", "rr" });
            case "set_fuel":
            {
                // The unit the driver said, otherwise iRacing's own units setting (the model's guess is ignored).
                double amount = Math.Max(0, Num("amount") ?? Num("liters") ?? 0);
                bool saidGallons = Regex.IsMatch(said, @"\bgal(lon)?s?\b", RegexOptions.IgnoreCase);
                bool saidLitres = Regex.IsMatch(said, @"\b(litres?|liters?)\b", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(said, @"\bfill (it |her )?up\b", RegexOptions.IgnoreCase);
                bool gallons = saidGallons || (!saidLitres && !metric);
                // "Fill it up" asks for more than any tank holds; iRacing caps it at the tank.
                return SetFuelLitres(gallons ? amount * IRacing.LitresPerGallon : amount,
                    Regex.IsMatch(said, @"\bfill\b", RegexOptions.IgnoreCase) && amount > 0 ? "Filling it up." : null);
            }
            case "set_fuel_laps":
            {
                double laps = Math.Max(0, Num("laps") ?? 0);
                double litres = Fuel?.LitresForLaps(laps) ?? double.NaN;
                if (double.IsNaN(litres)) return "I don't know your fuel per lap yet; give me one clean lap.";
                return SetFuelLitres(litres, $"Fuel for {laps:0.#} laps, {IRacing.Fuel(Math.Round(litres), metric)}.");
            }
            case "set_fuel_to_finish":
            {
                double litres = Fuel?.LitresToFinish() ?? double.NaN;
                if (double.IsNaN(litres)) return "I can't work out the fuel to finish yet.";
                return litres < 0.5 ? SetFuelLitres(0, "You've got enough to finish, no fuel.") : SetFuelLitres(litres, $"Fuel to finish, {IRacing.Fuel(Math.Round(litres), metric)}.");
            }
            case "set_auto_fuel":
            {
                // No SDK pit command for this: iRacing's #autofuel chat macro (#-autofuel turns it off).
                if (!Bool("on")) { IRacing.SendChat("#-autofuel"); return "Auto fuel off."; }
                double? margin = Num("margin_laps") is double m && m > 0 ? Math.Round(m, 1) : null;
                IRacing.SendChat(margin is double ml ? FormattableString.Invariant($"#autofuel {ml:0.#}") : "#autofuel");
                return margin is double laps ? $"Auto fuel on, {laps:0.#} laps margin." : "Auto fuel on.";
            }
            case "set_tearoff":
                IRacing.PitCommand(Bool("on") ? IRacing.Pit.Windshield : IRacing.Pit.ClearWindshield);
                return Bool("on") ? "Tear-off on." : "Tear-off off.";
            case "set_fast_repair":
                IRacing.PitCommand(Bool("on") ? IRacing.Pit.FastRepair : IRacing.Pit.ClearFastRepair);
                return Bool("on") ? "Fast repair on." : "Fast repair off.";
            case "clear_pit_service":
                IRacing.PitCommand(IRacing.Pit.Clear);
                return "Pit service cleared.";
            case "radio_channel":
            {
                string action = Str("action") is "unmute" or "transmit" or "add" or "remove" ? Str("action") : "mute";
                string channel = Str("channel").Trim().TrimStart('@').Replace(' ', '_');
                IRacing.SendChat($"@{action} {channel}");
                return action switch
                {
                    "transmit" => $"Transmitting on {channel}.",
                    "add" => $"Joined the {channel} channel.",
                    "remove" => $"Left the {channel} channel.",
                    _ => $"{channel} channel {action}d.",
                };
            }
            case "radio_scan":
                IRacing.SendChat(Bool("on") ? "@scan" : "@noscan");
                return Bool("on") ? "Scanning all channels." : "Scan off.";
            case "set_tire_pressure":
                return SetPressures();
            case "set_tire_compound":
                return SetCompound(Str("compound"));
            case "telemetry_logging":
                IRacing.TelemetryLogging(Bool("on"));
                return Bool("on") ? "Telemetry logging on." : "Telemetry logging off.";
            case "capture":
                switch (Str("action"))
                {
                    case "start_video": IRacing.VideoCapture(IRacing.Video.Start); return "Recording.";
                    case "stop_video": IRacing.VideoCapture(IRacing.Video.Stop); return "Recording stopped.";
                    default: IRacing.VideoCapture(IRacing.Video.Screenshot); return "Screenshot.";
                }
            case "replay":
                switch (Str("action"))
                {
                    case "pause": IRacing.ReplaySpeed(0); return "Paused.";
                    case "play": IRacing.ReplaySpeed(1); return "Playing.";
                    case "slow_motion": IRacing.ReplaySpeed(4, slowMotion: true); return "Slow motion.";
                    case "live": IRacing.BackToLive(); return "Back to live.";
                    default:
                    {
                        double seconds = Math.Clamp(Num("seconds") ?? 10, 3, 120);
                        IRacing.InstantReplay(seconds);
                        return $"Last {seconds:0} seconds.";
                    }
                }
            case "camera":
                return Camera(Str("car_number"), Str("view"));
            case "mute_driver":
                if (_cfg.MuteDriverKeys.Length == 0) return "Muting drivers isn't set up: add MuteDriverKeys in voice.json.";
                if (!IRacing.SimFocused()) return "iRacing needs to be the focused window for that."; // keys go to whatever has focus
                Keyboard.Press(_cfg.MuteDriverKeys);
                return "Driver muted.";
            case "toggle_voice_chat":
                if (_cfg.MuteVoiceChatKeys.Length == 0) return "Muting voice chat isn't set up: add MuteVoiceChatKeys in voice.json.";
                if (!IRacing.SimFocused()) return "iRacing needs to be the focused window for that.";
                Keyboard.Press(_cfg.MuteVoiceChatKeys);
                // iRacing's control is a toggle and doesn't report its state, so say what was asked for.
                return Regex.IsMatch(said, @"\bun-?mute\b", RegexOptions.IgnoreCase) ? "Voice chat unmuted." : "Voice chat muted.";
            case "send_chat":
            {
                string car = Str("car_number").Trim().TrimStart('#');
                IRacing.SendChat(car.Length > 0 ? $"/{car} {Str("message")}" : Str("message"));
                return car.Length > 0 ? $"Sent to car {car}." : "Message sent.";
            }
            case "set_force_feedback":
            {
                double nm = Math.Clamp(Num("newton_meters") ?? 0, 1, 150);
                IRacing.SetMaxForce(nm);
                return $"Force feedback {nm:0} newton metres.";
            }
            case "set_voice_volume":
            {
                if (Voice == null) return "I can't change my voice here.";
                int level = Num("level_percent") is double l ? (int)l : Voice.Volume + (int)(Num("change_percent") ?? 20);
                Voice.Volume = Math.Clamp(level, 10, 100);
                return $"Voice at {Voice.Volume} percent.";
            }
            case "toggle_overlay":
                Keyboard.Press("ScrollLock"); // the overlays' show/hide key (ToggleKey in overlays.json)
                return "Overlay toggled.";

            // Answers
            case "fuel_status": return Fuel?.Status(metric) ?? Questions.Answer("how much fuel", _telemetry);
            case "session_status": return Questions.Answer("how many laps left", _telemetry);
            case "pit_status": return IRacing.PitSummary(_telemetry);
            case "incidents": return Questions.Answer("how many incidents", _telemetry);
            case "position": return RaceInfo.Position(_telemetry);
            case "nearby_cars": return RaceInfo.Nearby(_telemetry);
            case "lap_times": return RaceInfo.LapTimes(_telemetry);
            case "weather": return RaceInfo.Weather(_telemetry);
            case "car_status": return RaceInfo.Car(_telemetry);
            case "coach_last_corner": return Coach?.LastCorner() ?? "The coach isn't running.";
            case "coach_corner":
                if (Coach == null) return "The coach isn't running.";
                if (Str("name").Length > 0 && (Coach.CornerNamed(Str("name")) ?? Coach.CornerNamed(said)) is { } named) return named;
                return Num("number") is double n ? Coach.Corner((int)n) : Coach.CornerNamed(said) ?? "Which turn?";
            case "coach_last_lap": return Coach?.LastLap() ?? "The coach isn't running.";
            case "coach_where_losing": return Coach?.WhereLosing() ?? "The coach isn't running.";
            case "coach_habit": return Coach?.Habit() ?? "The coach isn't running.";
            case "coach_stop": return Coach?.Stop() ?? "Okay.";
            case "coach_reference": return Coach?.ReferenceInfo() ?? "The coach isn't running.";
            default:
                return $"I don't have a tool called {name}.";
        }
    }
}
