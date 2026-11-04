using System.Collections.Concurrent;
using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;

/// <summary>Speaks callouts and answers one at a time; stale corner callouts are dropped.</summary>
public sealed class Speaker : IDisposable
{
    readonly SpeechSynthesizer _synth = new();
    readonly BlockingCollection<(string Text, DateTime Queued, bool Important)> _queue = new();
    readonly CoachConfig _cfg;
    readonly Thread _thread;
    volatile bool _paused;

    public bool Muted { get; set; }

    public Speaker(CoachConfig cfg)
    {
        _cfg = cfg;
        try { _synth.SelectVoice(cfg.Voice); } catch { /* fall back to the default voice */ }
        _synth.Rate = cfg.VoiceRate;
        _synth.SetOutputToDefaultAudioDevice();
        _thread = new Thread(Run) { IsBackground = true, Name = "speaker" };
        _thread.Start();
    }

    /// <param name="important">Engineer answers are never dropped for being old.</param>
    public void Say(string text, bool important = false)
    {
        Console.WriteLine($"  >> {text}");
        if (!Muted) _queue.Add((text, DateTime.UtcNow, important));
    }

    /// <summary>Stop talking while the driver holds push-to-talk.</summary>
    public void Pause()
    {
        _paused = true;
        _synth.SpeakAsyncCancelAll();
    }

    public void Resume() => _paused = false;

    void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            while (_paused) Thread.Sleep(50);
            if (!item.Important && (DateTime.UtcNow - item.Queued).TotalSeconds > _cfg.MaxCalloutAgeSeconds) continue;
            // SpeakAsync (not Speak) so Pause() can cut it off mid-sentence.
            var prompt = _synth.SpeakAsync(item.Text);
            while (!prompt.IsCompleted) Thread.Sleep(20);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _synth.Dispose();
    }
}

/// <summary>Dictation while push-to-talk is held, using the offline Windows speech recognizer.</summary>
public sealed class Listener : IDisposable
{
    readonly SpeechRecognitionEngine _engine;
    readonly List<string> _parts = new();
    readonly ManualResetEventSlim _stopped = new(true);
    bool _listening;

    public Listener()
    {
        _engine = new SpeechRecognitionEngine(new CultureInfo("en-US"));
        _engine.LoadGrammar(new DictationGrammar());
        _engine.SetInputToDefaultAudioDevice();
        _engine.SpeechRecognized += (_, e) => { lock (_parts) _parts.Add(e.Result.Text); };
        _engine.RecognizeCompleted += (_, _) => _stopped.Set();
    }

    public void Start()
    {
        if (_listening) return;
        lock (_parts) _parts.Clear();
        _stopped.Reset();
        _engine.RecognizeAsync(RecognizeMode.Multiple);
        _listening = true;
    }

    /// <summary>Stops listening and returns everything heard since Start.</summary>
    public string Stop()
    {
        if (!_listening) return "";
        _listening = false;
        _engine.RecognizeAsyncStop(); // lets the current phrase finish
        _stopped.Wait(TimeSpan.FromSeconds(3));
        lock (_parts) return string.Join(" ", _parts).Trim();
    }

    public void Dispose() => _engine.Dispose();
}
