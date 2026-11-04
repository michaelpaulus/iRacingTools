using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

/// <summary>
/// One request to Claude through the Anthropic SDK: the voice agent's system prompt, the driver's words and the tools
/// (converted from the OpenAI-style definitions the agent builds). Low effort keeps answers quick; on Opus/Sonnet the
/// server-side refusal fallback is on, so a declined request is re-served instead of coming back empty.
/// </summary>
public static class Claude
{
    static readonly Dictionary<string, AnthropicClient> Clients = new();

    public static async Task<LlmClient.Reply?> Chat(ModelProvider p, string key, JsonArray messages, JsonArray tools, Dictionary<string, DateTime> restUntil)
    {
        if (!Clients.TryGetValue(p.Name + key, out var client))
            Clients[p.Name + key] = client = new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromSeconds(p.TimeoutSeconds), MaxRetries = 0 };

        string system = string.Join("\n\n", messages.Where(m => m?["role"]?.GetValue<string>() == "system").Select(m => m!["content"]!.GetValue<string>()));
        string user = string.Join("\n\n", messages.Where(m => m?["role"]?.GetValue<string>() == "user").Select(m => m!["content"]!.GetValue<string>()));
        bool fallbacks = p.Model.StartsWith("claude-opus") || p.Model.StartsWith("claude-sonnet") || p.Model.StartsWith("claude-fable");

        var request = new MessageCreateParams
        {
            Model = p.Model,
            MaxTokens = 4000,
            System = new List<BetaTextBlockParam> { new() { Text = system, CacheControl = new BetaCacheControlEphemeral() } },
            Tools = tools.Select(ToTool).ToList(),
            Messages = [new BetaUserMessageParam { Content = user }],
            OutputConfig = new BetaOutputConfig { Effort = Effort(p.ReasoningEffort) },
        };
        if (fallbacks)
        {
            request = request with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await client.Beta.Messages.Create(request);
            if (response.StopReason == "refusal")
            {
                Console.WriteLine("  [model] Claude declined the request.");
                return new LlmClient.Reply(p.Name, "Sorry, I can't do that one.", [], sw.ElapsedMilliseconds);
            }
            var calls = new List<(string Name, JsonNode? Arguments)>();
            var text = new List<string>();
            foreach (var block in response.Content)
            {
                if (block.TryPickToolUse(out var tool)) calls.Add((tool.Name, JsonSerializer.SerializeToNode(tool.Input)));
                else if (block.TryPickText(out var t) && !string.IsNullOrWhiteSpace(t.Text)) text.Add(t.Text.Trim());
            }
            return new LlmClient.Reply(p.Name, string.Join(" ", text), calls, sw.ElapsedMilliseconds);
        }
        catch (AnthropicRateLimitException)
        {
            Console.WriteLine($"  [model] {p.Name} is rate limited; resting a minute.");
            restUntil[p.Name] = DateTime.UtcNow.AddMinutes(1);
        }
        catch (AnthropicApiException ex)
        {
            Console.WriteLine($"  [model] {p.Name} error: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Console.WriteLine($"  [model] {p.Name} unavailable after {sw.ElapsedMilliseconds} ms ({ex.GetType().Name}).");
        }
        return null;
    }

    /// <summary>
    /// The same request through the Claude CLI on your subscription, kept open between commands (see
    /// <see cref="ClaudeSession"/>). The CLI can't take our tools, so they're listed in the system prompt and Claude
    /// answers with one JSON object: what to say, and the tools to call.
    /// </summary>
    public static async Task<LlmClient.Reply?> ChatCli(ModelProvider p, JsonArray messages, JsonArray tools, Dictionary<string, DateTime> restUntil)
    {
        var session = CliSession(p, messages, tools);
        string user = string.Join("\n\n", messages.Where(m => m?["role"]?.GetValue<string>() == "user").Select(m => m!["content"]!.GetValue<string>()));

        var sw = Stopwatch.StartNew();
        var answer = await session.AskAsync(user, TimeSpan.FromSeconds(p.TimeoutSeconds));
        if (answer.Text == null)
        {
            Console.WriteLine($"  [model] {p.Name} unavailable after {sw.ElapsedMilliseconds} ms: {answer.Error}");
            if (answer.Error?.Contains("limit", StringComparison.OrdinalIgnoreCase) == true) restUntil[p.Name] = DateTime.UtcNow.AddMinutes(1);
            return null;
        }

        var text = answer.Text;
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        try
        {
            var reply = JsonNode.Parse(text[start..(end + 1)])!;
            var calls = (reply["calls"] as JsonArray ?? [])
                .Select(c => (c?["name"]?.GetValue<string>() ?? "", c?["arguments"]?.DeepClone()))
                .Where(c => c.Item1.Length > 0).ToList();
            return new LlmClient.Reply(p.Name, reply["say"]?.GetValue<string>()?.Trim() ?? "", calls, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            // Not JSON after all: say it as it came.
            return new LlmClient.Reply(p.Name, text, [], sw.ElapsedMilliseconds);
        }
    }

    static readonly Dictionary<string, ClaudeSession> Sessions = new();

    /// <summary>Starts the CLI for this provider ahead of the first command.</summary>
    public static void WarmCli(ModelProvider p, JsonArray messages, JsonArray tools) => CliSession(p, messages, tools).Warm();

    // One kept-open CLI per provider; a new one if the system prompt or tools change.
    static ClaudeSession CliSession(ModelProvider p, JsonArray messages, JsonArray tools)
    {
        string system = CliSystem(messages, tools);
        lock (Sessions)
        {
            if (Sessions.TryGetValue(p.Name, out var s) && s.System == system) return s;
            s?.Dispose();
            return Sessions[p.Name] = new ClaudeSession(system, p.Model, p.ReasoningEffort);
        }
    }

    static string CliSystem(JsonArray messages, JsonArray tools)
    {
        string system = string.Join("\n\n", messages.Where(m => m?["role"]?.GetValue<string>() == "system").Select(m => m!["content"]!.GetValue<string>()));
        var toolList = new JsonArray(tools.Select(t => (JsonNode?)new JsonObject
        {
            ["name"] = t!["function"]!["name"]!.DeepClone(),
            ["description"] = t["function"]!["description"]?.DeepClone(),
            ["parameters"] = t["function"]!["parameters"]!.DeepClone(),
        }).ToArray());
        system += $$$"""


            You have these tools (JSON Schema for each one's arguments):
            {{{toolList.ToJsonString()}}}

            Reply with exactly one JSON object and nothing else, no code fence:
            {"say": "what to say to the driver, or empty", "calls": [{"name": "tool_name", "arguments": {...}}]}
            Use "calls": [] when no tool is needed.

            Each message is a new thing the driver said: act on and answer only the latest one. Earlier messages in
            this conversation have already been carried out.
            """;
        return system;
    }

    /// <summary>{"type":"function","function":{"name","description","parameters"}} to a Claude tool.</summary>
    static BetaToolUnion ToTool(JsonNode? node)
    {
        var f = node!["function"]!;
        var parameters = f["parameters"]!;
        var properties = (parameters["properties"] as JsonObject ?? new JsonObject())
            .ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
        var required = (parameters["required"] as JsonArray ?? new JsonArray()).Select(r => r!.GetValue<string>()).ToList();
        return new BetaTool
        {
            Name = f["name"]!.GetValue<string>(),
            Description = f["description"]?.GetValue<string>() ?? "",
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    static Effort Effort(string s) => s.ToLowerInvariant() switch
    {
        "high" => Anthropic.Models.Beta.Messages.Effort.High,
        "medium" => Anthropic.Models.Beta.Messages.Effort.Medium,
        _ => Anthropic.Models.Beta.Messages.Effort.Low,
    };
}
