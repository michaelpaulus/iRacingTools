using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// IRacingBroadcast: an iRacing replay in, a commentated race video out.
///   scan        play the race at 16x, record the whole field, find the story
///   plan        the director's shot list
///   commentary  Claude's script (through the Claude CLI, or pasted in by you) and the Kokoro voices
///   shoot       play at 1x with the UI hidden, cut the cameras, record the screen and sound
///   edit        graphics, commentary and sound mixed into an mp4 with ffmpeg
/// Every stage saves its work, and finished stages are skipped next time. With no options it opens the window; options
/// (or --cli) run it in the console.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); // physical pixels, as the screen capture sees them
        var cfgPath = Path.Combine(AppContext.BaseDirectory, "broadcast.json");
        var cfg = BroadcastConfig.Load(cfgPath);
        if (!args.Any(a => a.StartsWith("--")))
        {
            HideOwnConsole();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(cfg, cfgPath, args.FirstOrDefault()));
            return 0;
        }
        try { return Run(args, cfg).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or TimeoutException or IOException or OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Stopped: " + ex.Message);
            return 1;
        }
    }

    static async Task<int> Run(string[] args, BroadcastConfig cfg)
    {
        string? rpy = null, output = null, featuredName = null, from = null, mode = null, only = null;
        string? focus = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": output = args[++i]; break;
                case "--featured": featuredName = args[++i]; break;
                case "--from": from = args[++i].ToLowerInvariant(); break;
                case "--only": only = args[++i].ToLowerInvariant(); break;
                case "--no-claude": mode = "simple"; break;
                case "--paste": mode = "paste"; break;
                case "--claude": mode = "claude"; break;
                case "--focus": focus = args[++i].ToLowerInvariant(); break;
                case "--cli": break;
                case "--demo": return await Demo.Run(cfg, useClaude: args.Contains("--claude"));
                case "--test-mute": return TestMute(cfg);
                case "-h" or "--help" or "/?": Usage(); return 0;
                default:
                    if (args[i].StartsWith("--")) { Console.WriteLine($"Unknown option {args[i]}"); Usage(); return 2; }
                    rpy = args[i];
                    break;
            }
        }
        if (rpy == null) { Usage(); return 2; }
        if (!File.Exists(rpy)) throw new FileNotFoundException($"No such replay: {rpy}");
        Pipeline.Stage? redoFrom = null;
        if (from != null)
        {
            if (!Enum.TryParse<Pipeline.Stage>(from, true, out var s)) { Console.WriteLine("--from takes one of: scan, plan, commentary, shoot, edit"); return 2; }
            redoFrom = s;
        }

        if (focus != null && !HighlightsOf.All.Contains(focus)) { Console.WriteLine("--focus takes one of: mixed, mine, race"); return 2; }
        using var p = new Pipeline(rpy, cfg);
        if (mode != null) p.Mode = mode;
        if (focus != null && focus != p.FocusName) p.FocusName = focus;
        if (output != null) p.Output = Path.GetFullPath(output);
        if (featuredName != null && featuredName != p.FeaturedName) p.FeaturedName = featuredName;
        Console.WriteLine($"IRacingBroadcast: {Path.GetFileName(p.Rpy)}");
        Console.WriteLine($"  work folder {p.Work}");
        foreach (var s in Pipeline.Stages) Console.WriteLine($"  {s,-11} {p.StateOf(s)}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var total = System.Diagnostics.Stopwatch.StartNew();
        if (only != null)
        {
            if (!Enum.TryParse<Pipeline.Stage>(only, true, out var s)) { Console.WriteLine("--only takes one of: scan, plan, commentary, shoot, edit"); return 2; }
            if (!p.CanRun(s)) { Console.WriteLine($"{s} needs the stages before it done first."); return 1; }
            await p.Run(s, cts.Token);
            Console.WriteLine($"  {s}: {p.StateOf(s)}");
            return 0;
        }
        await p.RunAll(cts.Token, redoFrom);
        Console.WriteLine($"Finished in {total.Elapsed:h\\:mm\\:ss}.");
        return 0;
    }

    /// <summary>The window doesn't need the console it was started with, unless that's a terminal someone is using.</summary>
    static void HideOwnConsole()
    {
        var console = GetConsoleWindow();
        if (console != IntPtr.Zero && GetConsoleProcessList(new uint[2], 2) <= 1) ShowWindow(console, 0); // SW_HIDE
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] list, uint count);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);

    /// <summary>
    /// Brings iRacing to the front and presses the Mute Voice Chat keys as scan codes, then 8 s later as virtual keys,
    /// so you can hear which one iRacing takes. Play a bit of replay where someone talks.
    /// </summary>
    static int TestMute(BroadcastConfig cfg)
    {
        if (Sim.FindProcess() == null) { Console.WriteLine("iRacing isn't running."); return 1; }
        Console.WriteLine($"Testing {cfg.MuteVoiceChatKeys}. Listen to iRacing:");
        foreach (var (scan, name) in new[] { (true, "1: scan codes"), (false, "2: virtual keys") })
        {
            Sim.BringToFront();
            Thread.Sleep(1000);
            Console.WriteLine(Sim.PressKeys(cfg.MuteVoiceChatKeys, scan) ? $"  Pressed ({name}) at {DateTime.Now:HH:mm:ss}." : $"  Not pressed ({name}): iRacing didn't get focus.");
            Thread.Sleep(8000);
        }
        Console.WriteLine("""
            Talking stopped after 1 and came back after 2: both work.   Stopped after 1 for good: only scan codes work.
            Talking went on, then stopped after 2: only virtual keys work.   Never stopped: neither gets through.
            Press the keys yourself afterwards if voice chat was left muted.
            """);
        return 0;
    }

    static void Usage()
    {
        Console.WriteLine("""
            IRacingBroadcast                            opens the window
            IRacingBroadcast <replay.rpy>               opens the window with that replay
            IRacingBroadcast <replay.rpy> --cli [options]   runs in the console
              --out <file.mp4>       where the video goes (default: Videos\iRacing Broadcasts)
              --featured "<name>"    driver to follow (default: whoever saved the replay)
              --from scan|plan|commentary|shoot|edit   redo from this stage (finished stages are otherwise skipped)
              --only <stage>         run just that stage
              --paste                write a prompt to paste into Claude yourself; the edit waits for the reply
              --claude               Claude writes it through the Claude Code CLI (claude -p, your subscription)
              --no-claude            built-in commentary instead of Claude
              --focus mixed|mine|race   the race plus your car (default), mostly your race, or the race only
              --demo                 offline test with a made-up race
              --test-mute            presses the Mute Voice Chat keys in iRacing two ways, to find which one it takes
            Settings: broadcast.json next to the exe.
            """);
    }
}
