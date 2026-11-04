using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

/// <summary>Something's wrong with the Claude Code CLI itself (not installed, not signed in), so asking again won't help.</summary>
public sealed class ClaudeCliException(string message) : Exception(message);

/// <summary>
/// Claude through the Claude Code CLI in headless mode (claude -p), on the user's Claude subscription. API keys are
/// taken out of its environment, or the CLI would bill the key instead.
/// </summary>
public static class ClaudeCli
{
    const string Install = "Install Claude Code (https://claude.com/claude-code), run \"claude\" once to sign in, then try again.";

    /// <summary>
    /// claude.exe or claude.cmd: where the installers put it, or on PATH. The User and Machine PATH are read too, since
    /// an app started before the install has the old PATH.
    /// </summary>
    public static string? Find()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = new List<string> { Path.Combine(home, ".local", "bin"), Path.Combine(localData, "Microsoft", "WinGet", "Links"), Path.Combine(appData, "npm") };
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(Path.PathSeparator, StringSplitOptions.TrimEntries));
        var winget = Path.Combine(localData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget)) dirs.AddRange(Directory.GetDirectories(winget, "Anthropic.ClaudeCode*"));

        foreach (var dir in dirs.Where(d => d.Length > 0))
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    /// <summary>Claude's reply to <paramref name="prompt"/>, with Claude Code's own tools off.</summary>
    /// <param name="effort">"low", "medium" or "high"; empty = the CLI's default.</param>
    public static async Task<string> Ask(string prompt, string systemPrompt, string model, TimeSpan timeout, string effort = "")
    {
        var exe = Find() ?? throw new ClaudeCliException("The Claude Code CLI (claude) isn't installed. " + Install);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Somewhere with no CLAUDE.md for it to pick up.
            WorkingDirectory = Path.GetTempPath(),
        };
        // Not --bare: that only signs in with an API key, never the subscription.
        foreach (var a in new[] { "-p", "--output-format", "json", "--model", model, "--system-prompt", systemPrompt, "--tools", "", "--no-session-persistence" })
            psi.ArgumentList.Add(a);
        if (effort.Length > 0) { psi.ArgumentList.Add("--effort"); psi.ArgumentList.Add(effort); }
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Win32Exception ex) { throw new ClaudeCliException($"Couldn't start {exe} ({ex.Message}). " + Install); }
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();

        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"claude didn't answer within {timeout.TotalMinutes:0} minutes.");
        }
        string output = await stdout, errors = (await stderr).Trim();

        JsonElement result;
        try { result = JsonDocument.Parse(output).RootElement; }
        catch (JsonException)
        {
            var said = (errors.Length > 0 ? errors : output).Trim();
            throw Failure(said.Length > 0 ? said : $"exit code {p.ExitCode}");
        }
        var text = result.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
        bool isError = result.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        if (isError || p.ExitCode != 0) throw Failure(text.Length > 0 ? text : errors.Length > 0 ? errors : $"exit code {p.ExitCode}");
        return text;
    }

    static Exception Failure(string said)
    {
        if (said.Contains("login", StringComparison.OrdinalIgnoreCase) || said.Contains("log in", StringComparison.OrdinalIgnoreCase)
            || said.Contains("not logged", StringComparison.OrdinalIgnoreCase) || said.Contains("authenticat", StringComparison.OrdinalIgnoreCase)
            || said.Contains("API key", StringComparison.OrdinalIgnoreCase))
            return new ClaudeCliException($"Claude Code isn't signed in ({said}). Run \"claude\" in a terminal, sign in with your Claude account, then try again.");
        return new InvalidOperationException("claude: " + said);
    }
}
