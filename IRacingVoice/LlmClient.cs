using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

/// <summary>A model service in voice.json: Claude (Anthropic SDK), or any OpenAI-compatible chat endpoint (Groq, Gemini, OpenRouter, local Ollama).</summary>
public sealed class ModelProvider
{
    public string Name { get; set; } = "";
    /// <summary>
    /// "claude-cli" for Claude through the Claude Code CLI on your subscription (no key); "anthropic" for Claude through
    /// the Anthropic SDK and an API key; "openai" for OpenAI-compatible services.
    /// </summary>
    public string Kind { get; set; } = "openai";
    /// <summary>Base URL, e.g. https://api.groq.com/openai/v1 or http://localhost:11434/v1 (Ollama).</summary>
    public string Url { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Environment variable holding the API key (read from your Windows user settings too); empty = no key.</summary>
    public string KeyVariable { get; set; } = "";
    /// <summary>"low" / "medium" / "high": Claude's effort, or the reasoning effort of reasoning models (gpt-oss); empty = not sent.</summary>
    public string ReasoningEffort { get; set; } = "";
    /// <summary>Give up and try the next service after this long.</summary>
    public double TimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// Sends the conversation and the tools to the first service that answers: the cloud model normally, the local one when
/// the internet is down, the key is missing or the free daily limit is used up (that service then rests for a minute).
/// </summary>
public sealed class LlmClient
{
    readonly List<ModelProvider> _providers;
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly Dictionary<string, DateTime> _restUntil = new();

    public LlmClient(List<ModelProvider> providers) => _providers = providers;

    public sealed record Reply(string Provider, string Text, List<(string Name, JsonNode? Arguments)> ToolCalls, long Ms);

    static string? Key(ModelProvider p)
    {
        if (p.KeyVariable.Length == 0) return null;
        return Environment.GetEnvironmentVariable(p.KeyVariable)
               ?? Environment.GetEnvironmentVariable(p.KeyVariable, EnvironmentVariableTarget.User); // set with setx, no restart needed
    }

    /// <summary>Which services are usable right now, for the start-up message.</summary>
    public string Describe() => string.Join(", ", _providers.Select(p =>
        $"{p.Name} ({p.Model}){(p.KeyVariable.Length > 0 && Key(p) == null ? $" - no {p.KeyVariable} set" : "")}" +
        (p.Kind.Equals("claude-cli", StringComparison.OrdinalIgnoreCase) && ClaudeCli.Find() == null ? " - Claude CLI not installed" : "")));

    /// <summary>Starts the Claude CLI now (when it's the service used), so the first command doesn't wait for it.</summary>
    public void Warm(JsonArray messages, JsonArray tools)
    {
        foreach (var p in _providers.Where(p => p.Kind.Equals("claude-cli", StringComparison.OrdinalIgnoreCase)))
            Claude.WarmCli(p, messages, tools);
    }

    public async Task<Reply?> Chat(JsonArray messages, JsonArray tools)
    {
        foreach (var p in _providers)
        {
            if (_restUntil.TryGetValue(p.Name, out var until) && DateTime.UtcNow < until) continue;
            var key = Key(p);
            if (p.KeyVariable.Length > 0 && key == null) continue;
            if (p.Kind.Equals("claude-cli", StringComparison.OrdinalIgnoreCase))
            {
                if (await Claude.ChatCli(p, messages, tools, _restUntil) is { } cliReply) return cliReply;
                continue;
            }
            if (p.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            {
                if (await Claude.Chat(p, key!, messages, tools, _restUntil) is { } claudeReply) return claudeReply;
                continue;
            }

            var body = new JsonObject
            {
                ["model"] = p.Model,
                ["messages"] = messages.DeepClone(),
                ["tools"] = tools.DeepClone(),
                ["tool_choice"] = "auto",
                ["temperature"] = 0,
                ["max_tokens"] = 1024,
            };
            if (p.ReasoningEffort.Length > 0) body["reasoning_effort"] = p.ReasoningEffort;

            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, p.Url.TrimEnd('/') + "/chat/completions") { Content = JsonContent.Create(body) };
                if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(p.TimeoutSeconds));
                using var res = await _http.SendAsync(req, cts.Token);
                var json = await res.Content.ReadAsStringAsync(cts.Token);
                if (!res.IsSuccessStatusCode)
                {
                    Console.WriteLine($"  [model] {p.Name} answered {(int)res.StatusCode}: {Short(json)}");
                    if ((int)res.StatusCode is 429 or 401 or 403) _restUntil[p.Name] = DateTime.UtcNow.AddMinutes(1);
                    continue;
                }
                var msg = JsonNode.Parse(json)?["choices"]?[0]?["message"];
                var calls = (msg?["tool_calls"]?.AsArray() ?? [])
                    .Select(c => (c?["function"]?["name"]?.GetValue<string>() ?? "", c?["function"]?["arguments"]))
                    .Where(c => c.Item1.Length > 0).ToList();
                return new Reply(p.Name, msg?["content"]?.GetValue<string>()?.Trim() ?? "", calls, sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                Console.WriteLine($"  [model] {p.Name} unavailable after {sw.ElapsedMilliseconds} ms ({ex.GetType().Name}); trying the next one.");
            }
        }
        return null;
    }

    static string Short(string s) => s.Length > 200 ? s[..200] + "..." : s;
}
