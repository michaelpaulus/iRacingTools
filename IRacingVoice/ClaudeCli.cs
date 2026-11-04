using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Asks Claude through the Claude Code CLI (claude -p), signed in with your Claude subscription, so there's no API key
/// and nothing billed per request. The API key is taken out of its environment: with it set, the CLI would bill that
/// instead. Claude Code's own tools are off and its system prompt is replaced by ours.
/// </summary>
public static class ClaudeCli
{
    /// <summary>
    /// claude.exe from the PATH (as saved in Windows now, so an install after this app started is found) or where the
    /// installer or winget puts it; null when it isn't installed.
    /// </summary>
    public static string? Find()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new List<string> { Path.Combine(home, ".local", "bin"), Path.Combine(localData, "Microsoft", "WinGet", "Links") };
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(Path.PathSeparator));
        var winget = Path.Combine(localData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget)) dirs.AddRange(Directory.GetDirectories(winget, "Anthropic.ClaudeCode*"));

        foreach (var dir in dirs.Where(d => d.Length > 0))
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    public sealed record Answer(string? Text, string? Error);

    /// <param name="effort">"low" / "medium" / "high"; empty = the CLI's default.</param>
    public static async Task<Answer> AskAsync(string system, string prompt, string model, TimeSpan timeout, string effort = "")
    {
        var exe = Find();
        if (exe == null) return new(null, "The Claude CLI isn't installed");

        using var p = Process.Start(StartInfo(exe, system, model, effort, streaming: false))!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new(null, $"no answer within {timeout.TotalSeconds:0} s");
        }

        var output = await stdout;
        try
        {
            var json = JsonNode.Parse(output);
            var text = json?["result"]?.GetValue<string>() ?? "";
            if (json?["is_error"]?.GetValue<bool>() == true) return new(null, Short(text));
            return new(text.Trim(), null);
        }
        catch (JsonException)
        {
            var err = (await stderr).Trim();
            return new(null, Short(err.Length > 0 ? err : output));
        }
    }

    internal static string Short(string s) => s.Length > 200 ? s[..200] + "..." : s;

    /// <summary>
    /// The CLI with our system prompt and nothing of Claude Code's own: no tools, settings, plugins, MCP servers or
    /// skills, started away from the projects so no CLAUDE.md is picked up. (Not --bare: that only signs in with an
    /// API key, never the subscription.)
    /// </summary>
    internal static ProcessStartInfo StartInfo(string exe, string system, string model, string effort, bool streaming)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
        };
        var args = new List<string> { "-p", "--model", model, "--system-prompt", system, "--tools", "", "--no-session-persistence",
            "--setting-sources", "", "--strict-mcp-config", "--disable-slash-commands" };
        args.AddRange(streaming
            ? ["--input-format", "stream-json", "--output-format", "stream-json", "--verbose"]
            : ["--output-format", "json"]);
        if (effort.Length > 0) args.AddRange(["--effort", effort]);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");
        return psi;
    }
}

/// <summary>
/// One Claude CLI kept running for many questions, so each one skips the CLI's start-up (about 2 seconds). The answer
/// is taken as soon as Claude's message arrives: the CLI then spends another second or so summarising the turn before
/// it reports the result. The CLI remembers the conversation, so after <see cref="MaxTurns"/> questions a fresh one
/// takes over; it's started ahead of time, so nobody waits for it.
/// </summary>
public sealed class ClaudeSession : IDisposable
{
    const int MaxTurns = 12;

    readonly string _system, _model, _effort;
    readonly object _gate = new();
    Live? _live, _spare;

    sealed class Live(Process process)
    {
        public readonly Process Process = process;
        public readonly Queue<TaskCompletionSource<ClaudeCli.Answer>> Waiting = new(); // answered in order
        public int Turns;
    }

    public ClaudeSession(string system, string model, string effort)
    {
        (_system, _model, _effort) = (system, model, effort);
    }

    public string System => _system;

    /// <summary>Starts the CLI now, so the first question doesn't wait for it.</summary>
    public void Warm()
    {
        lock (_gate) _live ??= Start();
    }

    public async Task<ClaudeCli.Answer> AskAsync(string prompt, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<ClaudeCli.Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Live? live;
        lock (_gate)
        {
            if (_live != null && (_live.Process.HasExited || _live.Turns >= MaxTurns)) { Retire(_live); _live = null; }
            if (_live == null) { _live = _spare is { Process.HasExited: false } ? _spare : Start(); _spare = null; }
            live = _live;
            if (live == null) return new(null, "The Claude CLI isn't installed");
            live.Turns++;
            live.Waiting.Enqueue(tcs);
            // Getting near the end of this one: have the next ready.
            if (live.Turns >= MaxTurns && _spare == null) _spare = Start();
        }
        try
        {
            var message = new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt } };
            await live.Process.StandardInput.WriteLineAsync(message.ToJsonString());
            await live.Process.StandardInput.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return new(null, "the Claude CLI stopped");
        }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        if (done == tcs.Task) return tcs.Task.Result;
        // Stuck: start over with a fresh one next time.
        lock (_gate) if (_live == live) { Retire(live); _live = null; }
        return new(null, $"no answer within {timeout.TotalSeconds:0} s");
    }

    Live? Start()
    {
        var exe = ClaudeCli.Find();
        if (exe == null) return null;
        var live = new Live(Process.Start(ClaudeCli.StartInfo(exe, _system, _model, _effort, streaming: true))!);
        _ = live.Process.StandardError.ReadToEndAsync(); // keep its pipe from filling
        new Thread(() => Read(live)) { IsBackground = true, Name = "claude-cli" }.Start();
        return live;
    }

    // Claude's message answers the oldest waiting question; the turn's result only matters when no message came.
    void Read(Live live)
    {
        TaskCompletionSource<ClaudeCli.Answer>? current = null;
        try
        {
            while (live.Process.StandardOutput.ReadLine() is { } line)
            {
                JsonNode? e;
                try { e = JsonNode.Parse(line); } catch (JsonException) { continue; }
                string type = e?["type"]?.GetValue<string>() ?? "";
                lock (_gate) if (current == null && live.Waiting.Count > 0) current = live.Waiting.Dequeue();
                if (current == null) continue;
                if (type == "assistant")
                {
                    var text = string.Join(" ", (e!["message"]?["content"] as JsonArray ?? [])
                        .Where(b => b?["type"]?.GetValue<string>() == "text")
                        .Select(b => b!["text"]?.GetValue<string>() ?? "")).Trim();
                    if (text.Length > 0) current.TrySetResult(new(text, null));
                }
                else if (type == "result")
                {
                    var text = e!["result"]?.GetValue<string>() ?? "";
                    current.TrySetResult(e["is_error"]?.GetValue<bool>() == true ? new(null, ClaudeCli.Short(text)) : new(text.Trim(), null));
                    current = null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        lock (_gate)
        {
            current?.TrySetResult(new(null, "the Claude CLI stopped"));
            while (live.Waiting.Count > 0) live.Waiting.Dequeue().TrySetResult(new(null, "the Claude CLI stopped"));
        }
    }

    static void Retire(Live live) => Task.Run(() =>
    {
        try { live.Process.StandardInput.Close(); } catch { }
        try { if (!live.Process.WaitForExit(2000)) live.Process.Kill(entireProcessTree: true); } catch { }
    });

    public void Dispose()
    {
        lock (_gate)
        {
            if (_live != null) Retire(_live);
            if (_spare != null) Retire(_spare);
            _live = _spare = null;
        }
    }
}
