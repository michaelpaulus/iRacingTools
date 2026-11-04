using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NAudio.Wave;

// IRacingVoice - hold push-to-talk, say what you want ("no tyres, 20 litres", "turn Discord down", "mute the race channel").
//   IRacingVoice                 run alongside iRacing
//   IRacingVoice bind            choose the push-to-talk key or wheel button
//   IRacingVoice test "<words>" [--dry]  run a command as if spoken (no microphone); --dry shows the actions without doing them
//   IRacingVoice say <out.wav> "<words>" / hear <file.wav>   make a synthesized clip / transcribe one (checks speech recognition)
//   IRacingVoice speak "<words>"  say the words with the confirmation voice
// Speech is recognised locally by Whisper; Claude (through the Claude CLI by default, voice.json: Models) works out what
// you meant; answers are spoken with the Kokoro voice.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
// Speech recognition runs in short bursts; never let it take CPU from iRacing.
Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

var baseDir = Environment.CurrentDirectory;
var cfg = VoiceConfig.Load(baseDir);
// And off the performance cores iRacing runs on, where the CPU has both kinds.
if (cfg.EfficiencyCoresOnly) Console.WriteLine($"Running on {Cores.UseEfficiencyCores()}.");
string command = args.Length > 0 ? args[0].ToLowerInvariant() : "live";

if (command == "bind")
{
    Console.WriteLine("Press and release the key or wheel button you want for push-to-talk (30 s)...");
    Thread.Sleep(300);
    if (PushToTalk.Bind(cfg.PushToTalk, TimeSpan.FromSeconds(30)))
    {
        cfg.Save(baseDir);
        Console.WriteLine($"Push-to-talk set to {new PushToTalk(cfg.PushToTalk).Describe()}. Saved to voice.json.");
    }
    else Console.WriteLine("Nothing pressed - push-to-talk unchanged.");
    return;
}

if (command == "say" && args.Length >= 3)
{
    // Writes a synthesized voice clip (16 kHz mono, what Whisper takes), for testing "hear" without a microphone.
    using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
    synth.SetOutputToWaveFile(args[1], new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
    synth.Speak(string.Join(" ", args.Skip(2)));
    Console.WriteLine($"Wrote {args[1]}.");
    return;
}

if (command == "voice-samples" && args.Length >= 2)
{
    // The same sentence in several Kokoro voices, as WAVs to compare, with how long each took to make:
    // voice-samples <folder> [voice names... | threads] ("threads" times the configured voice with 1 to 8 threads).
    Directory.CreateDirectory(args[1]);
    const string line = "Box this lap. No tyres, twenty litres. Car twelve is a second and a half behind and closing.";
    var kokoro = await NeuralVoice.Ensure(cfg.KokoroModel, baseDir);
    if (kokoro == null) return;
    var voices = new List<(string Name, Func<NeuralVoice> Make)>();
    if (args.Skip(2).FirstOrDefault() == "threads")
        foreach (int t in new[] { 1, 2, 4, 6, 8 }) voices.Add(($"kokoro-{cfg.KokoroVoice}-{t}threads", () => NeuralVoice.Kokoro(kokoro, cfg.KokoroVoice, cfg.KokoroSpeed, t)));
    else
        foreach (var n in args.Length > 2 ? args.Skip(2) : [cfg.KokoroVoice])
            voices.Add(("kokoro-" + n, () => NeuralVoice.Kokoro(kokoro, n, cfg.KokoroSpeed, cfg.KokoroThreads)));
    foreach (var (name, make) in voices)
    {
        using var v = make();
        v.SaveWav("Warm up.", Path.Combine(Path.GetTempPath(), "warmup.wav"));
        // The first sentence is when it starts talking; the rest is made while that plays.
        var sw = Stopwatch.StartNew();
        v.SaveWav(line[..(line.IndexOf('.') + 1)], Path.Combine(Path.GetTempPath(), "first.wav"));
        long firstMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var path = Path.Combine(args[1], name + ".wav");
        v.SaveWav(line, path);
        Console.WriteLine($"{name,-40} starts talking after {firstMs,5} ms, whole line {sw.ElapsedMilliseconds,5} ms  {path}");
    }
    return;
}

if (command == "answers" && args.Length >= 2)
{
    // The race answers (position, cars around, laps, weather, car) at points through a recorded session (.ibt).
    using var ibt = new IbtReplay(args[1], 0);
    var points = args.Skip(2).Select(a => double.Parse(a, CultureInfo.InvariantCulture)).DefaultIfEmpty(0.5).ToList();
    int total = 0;
    while (ibt.WaitForFrame(0)) total++;
    using var again = new IbtReplay(args[1], 0);
    int at = 0;
    foreach (var frac in points.Order())
    {
        int target = (int)(frac * (total - 1));
        while (at <= target && again.WaitForFrame(0)) at++;
        Console.WriteLine($"==== {frac:P0} through ({at}/{total} frames, session time {again.Get("SessionTime"):0} s)");
        Console.WriteLine($"position:  {RaceInfo.Position(again)}");
        Console.WriteLine($"nearby:    {RaceInfo.Nearby(again)}");
        Console.WriteLine($"laps:      {RaceInfo.LapTimes(again)}");
        Console.WriteLine($"weather:   {RaceInfo.Weather(again)}");
        Console.WriteLine($"car:       {RaceInfo.Car(again)}");
    }
    Console.WriteLine($"compounds: {string.Join(", ", SessionYaml.TireCompounds(again.SessionInfo).Select(c => $"{c.Index} {c.Type}"))}");
    Console.WriteLine($"cameras:   {string.Join(", ", SessionYaml.CameraGroups(again.SessionInfo).Select(g => $"{g.Num} {g.Name}"))}");
    var drivers = SessionYaml.Cars(again.SessionInfo);
    Console.WriteLine($"drivers:   {drivers.Count}, e.g. {string.Join("; ", drivers.Take(4).Select(c => $"#{c.Number} {c.Name} ({c.Class}{(c.IsPaceCar ? ", pace car" : "")})"))}");
    return;
}

if (command == "coach-replay" && args.Length >= 2)
{
    // Runs a recorded session (.ibt) through the coach and prints what it would say after every corner.
    using var ibt = new IbtReplay(args[1], 0);
    // A fresh folder each run, so one replay's saved bests don't become the next replay's "best".
    var replayDir = Path.Combine(Path.GetTempPath(), "iracing-coach-replay-" + Guid.NewGuid().ToString("N"));
    var replayCoach = new Coach(replayDir, Path.Combine(baseDir, "reference"));
    int corners = 0;
    replayCoach.PassMeasured += (name, p, best, cx) =>
    {
        corners++;
        var issues = Diagnosis.Find(p, best, cx);
        Console.WriteLine($"lap {p.Lap,2} {name,-8} {p.Seconds,6:0.00}s min {p.MinKmh,5:0} km/h | {Diagnosis.Report(name, p, best, issues)}");
    };
    replayCoach.LapDone += (lap, time, n) => Console.WriteLine($"==== lap {lap} clean: {(int)(time / 60)}:{time % 60:00.000}, {n} corners known");
    var issueCounts = new Dictionary<string, int>();
    replayCoach.PassMeasured += (_, p, best, cx) =>
    {
        var top = Diagnosis.Find(p, best, cx).FirstOrDefault()?.Key ?? "clean";
        issueCounts[top] = issueCounts.GetValueOrDefault(top) + 1;
    };
    // --ask <lap> <turn>: ask "how was that corner?" right after that corner, to check the reminder and follow-up next lap.
    int askAt = Array.IndexOf(args, "--ask");
    int askLap = askAt > 0 ? int.Parse(args[askAt + 1]) : -1, askTurn = askAt > 0 ? int.Parse(args[askAt + 2]) : -1;
    bool askNow = false;
    replayCoach.PassMeasured += (name, p, _, _) => askNow |= p.Lap == askLap &&
        System.Text.RegularExpressions.Regex.IsMatch(name, $@"^Turns? {askTurn}\b");
    replayCoach.Say += s => Console.WriteLine($"     >> COACH: {s}");
    while (ibt.WaitForFrame(0))
    {
        replayCoach.OnFrame(ibt);
        if (askNow) { askNow = false; Console.WriteLine($"     >> DRIVER: how was that corner?\n     >> COACH: {replayCoach.LastCorner()}"); }
    }
    if (args.Contains("--habit")) Console.WriteLine($"     >> DRIVER: what should I work on?\n     >> COACH: {replayCoach.Habit()}");
    try { Directory.Delete(replayDir, recursive: true); } catch (IOException) { }
    Console.WriteLine($"{corners} corner passes. Top issue per pass: " + string.Join(", ", issueCounts.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")));
    return;
}

if (command == "reference-check" && args.Length >= 3)
{
    // reference-check <lap.csv|.ibt> <session.ibt> [--to-csv out.csv]: reads a reference lap the way the coach will
    // (the session recording gives the track), prints its time and coverage; --to-csv writes it back out as a CSV.
    var facts = SessionFacts.Parse(ReferenceLap.IbtSessionInfo(args[2]));
    int bins = (int)Math.Round(facts.TrackM / 5);
    double binM = facts.TrackM / bins;
    var lap = args[1].EndsWith(".ibt", StringComparison.OrdinalIgnoreCase)
        ? ReferenceLap.FromIbt(args[1], bins, binM) : ReferenceLap.FromCsv(args[1], bins, binM, facts.TrackM);
    if (lap == null) { Console.WriteLine("No complete lap found."); return; }
    var tr = lap.Trace;
    Console.WriteLine($"{facts.Key}: {lap.Name}, {(int)(lap.LapTime / 60)}:{lap.LapTime % 60:00.000}, coverage {tr.Coverage(0, tr.N - 1):P0}, " +
                      $"top speed {tr.Speed.Where(v => !double.IsNaN(v)).Max() * 3.6:0} km/h, peak lat {tr.LatG.Where(v => !double.IsNaN(v)).Max():0.0} g");
    int csvAt = Array.IndexOf(args, "--to-csv");
    if (csvAt > 0)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var lines = new List<string> { "LapDistPct,Speed (km/h),Brake (%),Throttle (%),Gear,RPM,SteeringWheelAngle (deg),LatAccel (g),LongAccel (g),YawRate (deg/s),BrakeABSactive" };
        for (int b = 0; b < tr.N; b++)
            if (tr.Has(b))
                lines.Add(string.Join(",", new double[] { (b + 0.5) / tr.N, tr.Speed[b] * 3.6, tr.Brake[b] * 100, tr.Throttle[b] * 100, tr.Gear[b], tr.Rpm[b],
                    tr.Steer[b] * 57.2958, tr.LatG[b], tr.LongG[b], tr.Yaw[b], tr.Abs[b] }.Select(v => v.ToString("0.####", inv))));
        File.WriteAllLines(args[csvAt + 1], lines);
        Console.WriteLine($"Wrote {args[csvAt + 1]}.");
    }
    return;
}

if (command == "hear" && args.Length >= 2)
{
    // Transcribes a 16 kHz mono .wav file: checks speech recognition (and its speed) without a microphone.
    using var t = await Transcriber.Load(cfg, baseDir);
    using var reader = new NAudio.Wave.WaveFileReader(args[1]);
    var provider = reader.ToSampleProvider();
    var samples = new List<float>();
    var buffer = new float[16000];
    for (int n; (n = provider.Read(buffer.AsSpan())) > 0;) samples.AddRange(buffer.Take(n));
    var sw = Stopwatch.StartNew();
    var heard = await t.Transcribe(samples.ToArray());
    Console.WriteLine($"Heard: \"{heard}\" ({sw.ElapsedMilliseconds} ms for {samples.Count / 16000.0:0.0} s of audio, {cfg.WhisperThreads} threads)");
    return;
}

// First run: borrow the coach's push-to-talk button so the same wheel button works.
if (cfg.PushToTalk.Type == "None" && TryCoachPushToTalk(baseDir) is { } coachPtt)
{
    cfg.PushToTalk = coachPtt;
    cfg.Save(baseDir);
    Console.WriteLine("Using the coach's push-to-talk button (change it with: run.cmd bind).");
}

using var telemetry = new LiveTelemetry();
var coach = new Coach(Path.Combine(baseDir, "coach"), Path.Combine(baseDir, "reference")); // corners and best passes, per car and track
if (cfg.Garage61Download)
{
    var garage61 = new Garage61(cfg.Garage61TokenVariable, cfg.Garage61Pick);
    coach.NewTrack += (facts, folder) => Task.Run(async () => { if (await garage61.Fetch(facts, folder)) coach.ReloadReference(); });
}
var fuel = new FuelTracker();
new Thread(() =>
{
    while (true)
        try { if (telemetry.WaitForFrame(500)) { coach.OnFrame(telemetry); fuel.OnFrame(telemetry); } }
        catch (Exception) { Thread.Sleep(500); } // closed while reading (exit, or iRacing quitting)
}) { IsBackground = true, Name = "telemetry" }.Start();
var llm = new LlmClient(cfg.Models);
var agent = new Agent(cfg, telemetry, llm) { Coach = coach, Fuel = fuel };
agent.Warm();
string? kokoroDir = cfg.KokoroVoice.Length > 0 ? await NeuralVoice.Ensure(cfg.KokoroModel, baseDir) : null;
using var speaker = new Speaker(cfg, kokoroDir);
coach.Say += speaker.Say; // the reminder before the corner and how it went after
agent.Voice = speaker;

if (command == "speak" && args.Length >= 2)
{
    // Says the words with the configured voice (to hear what the confirmations sound like).
    var sw = Stopwatch.StartNew();
    speaker.Say(string.Join(" ", args.Skip(1)));
    Console.WriteLine($"  (ready in {sw.ElapsedMilliseconds} ms)");
    Thread.Sleep(4000);
    return;
}

if (command == "test" && args.Length >= 2)
{
    Thread.Sleep(600); // let the telemetry thread connect if iRacing is running
    agent.DryRun = args.Contains("--dry");
    if (args.Contains("--gallons")) IRacing.ForceMetric = false;
    if (args.Contains("--litres")) IRacing.ForceMetric = true;
    // Several commands in a row with "|" between them, as in a session (the model is already warm after the first).
    foreach (var said in string.Join(" ", args.Skip(1).Where(a => !a.StartsWith("--"))).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        Console.WriteLine($"  [driver] {said}");
        var sw = Stopwatch.StartNew();
        string reply = await agent.Handle(said);
        Console.WriteLine($"  ({sw.ElapsedMilliseconds} ms)");
        speaker.Say(reply);
        Thread.Sleep(2500); // let it finish speaking
    }
    return;
}

var ptt = new PushToTalk(cfg.PushToTalk);
using var recorder = new Recorder();
Console.WriteLine("Loading speech recognition...");
using var transcriber = await Transcriber.Load(cfg, baseDir);
Console.WriteLine($"Voice agent ready. Push-to-talk: {ptt.Describe()}. Model: {llm.Describe()}.");
Console.WriteLine("You can also type a command and press Enter. Ctrl+C to quit.");

var commands = new ConcurrentQueue<string>();
new Thread(() =>
{
    while (Console.ReadLine() is { } line)
        if (!string.IsNullOrWhiteSpace(line)) commands.Enqueue(line.Trim());
}) { IsBackground = true }.Start();

// Pressing push-to-talk stops the answer being spoken and drops one still being worked out; a quick tap does only that.
bool wasHeld = false, busy = false;
int asked = 0; // bumped on every press: an answer for an older press isn't spoken
var pressedAt = DateTime.UtcNow;
while (true)
{
    bool held = ptt.IsHeld();
    if (held && !wasHeld)
    {
        speaker.Cancel();
        Interlocked.Increment(ref asked);
        pressedAt = DateTime.UtcNow;
        recorder.Start();
    }
    else if (!held && wasHeld)
    {
        var audio = recorder.Stop();
        bool tap = DateTime.UtcNow - pressedAt < TimeSpan.FromSeconds(0.5);
        if (tap) Console.WriteLine("  [cancelled]");
        else if (audio.Length > 0 && !busy)
        {
            busy = true;
            int mine = asked;
            if (cfg.Cues) speaker.Cue(thinking: false); // heard you
            _ = Task.Run(async () =>
            {
                // Still working on it: a soft blip every two seconds until the answer (or a cancel).
                using var thinking = cfg.Cues
                    ? new Timer(_ => { if (mine == Volatile.Read(ref asked)) speaker.Cue(thinking: true); }, null, 2000, 2000)
                    : null;
                try
                {
                    var sw = Stopwatch.StartNew();
                    string said = await transcriber.Transcribe(audio);
                    long heardMs = sw.ElapsedMilliseconds;
                    if (said.Length == 0) { thinking?.Dispose(); speaker.Say("Didn't catch that."); return; }
                    Console.WriteLine($"  [driver] {said}  (heard in {heardMs} ms)");
                    string reply = await agent.Handle(said);
                    thinking?.Dispose();
                    Console.WriteLine($"  (total {sw.ElapsedMilliseconds} ms)");
                    if (mine != Volatile.Read(ref asked)) { Console.WriteLine("  [cancelled before it was spoken]"); return; }
                    speaker.Say(reply);
                }
                catch (Exception ex) { thinking?.Dispose(); Console.WriteLine($"  [error] {ex.Message}"); speaker.Say("Something went wrong."); }
                finally { busy = false; }
            });
        }
    }
    wasHeld = held;

    while (commands.TryDequeue(out var typed))
    {
        Console.WriteLine($"  [driver] {typed}");
        speaker.Say(await agent.Handle(typed));
    }
    Thread.Sleep(15);
}

static PushToTalkConfig? TryCoachPushToTalk(string baseDir)
{
    try
    {
        var path = Path.Combine(baseDir, "..", "IRacingCoach", "coach.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("PushToTalk", out var p)) return null;
        var ptt = p.Deserialize<PushToTalkConfig>();
        return ptt is { Type: "Keyboard" or "Joystick" } ? ptt : null;
    }
    catch { return null; }
}
