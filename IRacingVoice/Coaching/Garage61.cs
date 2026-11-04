using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// Downloads a comparison lap from Garage 61's public API (garage61.net/developer) when you start a car and track:
/// finds the iRacing car and track there, searches the laps your token can see, picks one and saves its telemetry
/// export into the reference folder. Needs a personal access token (Garage61TokenVariable) and, to see telemetry, a
/// Garage 61 Pro plan. By default the API only shows your own and your teammates' laps.
/// </summary>
public sealed class Garage61(string tokenVariable, string pick)
{
    const string Api = "https://garage61.net/api/v1/";
    const string Prefix = "Garage 61 - ";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    JsonArray? _cars, _tracks;

    string? Token => Environment.GetEnvironmentVariable(tokenVariable)
                     ?? Environment.GetEnvironmentVariable(tokenVariable, EnvironmentVariableTarget.User);

    /// <summary>Downloads a lap unless the folder already has a recent one. True when a new lap was saved.</summary>
    public async Task<bool> Fetch(SessionFacts f, string folder)
    {
        if (Token == null) { Console.WriteLine($"  [garage61] No {tokenVariable} set: not downloading a comparison lap."); return false; }
        if (f.CarId == 0 || f.TrackId == 0 || folder.Length == 0) return false;
        // One download per car and track per week is plenty (and keeps well inside the rate limits).
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, Prefix + "*.csv").Any(p => File.GetLastWriteTimeUtc(p) > DateTime.UtcNow.AddDays(-7)))
            return false;
        try
        {
            _cars ??= (await Get("cars"))?["items"] as JsonArray;
            _tracks ??= (await Get("tracks"))?["items"] as JsonArray;
            int? car = Find(_cars, f.CarId), track = Find(_tracks, f.TrackId);
            if (car == null || track == null) { Console.WriteLine("  [garage61] This car or track isn't on Garage 61."); return false; }

            string query = $"laps?cars={car}&tracks={track}&group=driver&seeTelemetry=true&limit=200";
            var laps = Laps(await Get(query));
            JsonNode? chosen = null;
            if (pick == "near" && f.IRating > 0)
            {
                // Someone a bit quicker: rated from your iRating up to 1000 above it, fastest of those.
                chosen = laps.Where(l => Num(l, "driverRating") is double r && r >= f.IRating - 100 && r <= f.IRating + 1000)
                    .OrderBy(l => Num(l, "lapTime")).FirstOrDefault();
            }
            chosen ??= laps.OrderBy(l => Num(l, "lapTime")).FirstOrDefault();
            if (chosen == null)
            {
                Console.WriteLine("  [garage61] No laps with telemetry you can see for this car and track (needs Pro; by default only your own and your teammates' laps).");
                return false;
            }

            string id = chosen["id"]!.GetValue<string>();
            double time = Num(chosen, "lapTime") ?? 0;
            string csv = await GetText($"laps/{Uri.EscapeDataString(id)}/csv");
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.EnumerateFiles(folder, Prefix + "*.csv")) File.Delete(old); // replaces the last download only
            string who = Regex.Replace(Driver(chosen["driver"]), @"[^\w\- ]+", "").Trim();
            string name = $"{Prefix}{(who.Length > 0 ? who : "lap " + id)}.csv";
            await File.WriteAllTextAsync(Path.Combine(folder, name), csv);
            Console.WriteLine($"  [garage61] Downloaded a comparison lap: {name} ({(int)(time / 60)}:{time % 60:00.000}).");
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Console.WriteLine($"  [garage61] Garage 61 refused the token ({(int)ex.StatusCode!}): check {tokenVariable}, the app's driving_data permission and your Pro plan.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            Console.WriteLine("  [garage61] Rate limited: trying again next session.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"  [garage61] Couldn't download a comparison lap: {ex.Message}");
        }
        return false;
    }

    static int? Find(JsonArray? items, int iracingId) => items?
        .FirstOrDefault(i => (i?["platform"]?.GetValue<string>() ?? "") == "iracing" && i?["platform_id"]?.ToString() == iracingId.ToString(CultureInfo.InvariantCulture))
        ?["id"]?.GetValue<int>();

    static List<JsonNode> Laps(JsonNode? result) =>
        (result?["items"] as JsonArray ?? []).Where(l => l != null && l["canViewTelemetry"]?.GetValue<bool>() != false
            && l["clean"]?.GetValue<bool>() != false && Num(l, "lapTime") > 0).Select(l => l!).ToList();

    static double? Num(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    static string Driver(JsonNode? d)
    {
        if (d == null) return "";
        foreach (var key in new[] { "name", "displayName" }) if (d[key]?.ToString() is { Length: > 0 } s) return s;
        return $"{d["firstName"]} {d["lastName"]}".Trim() is { Length: > 0 } full ? full : d["slug"]?.ToString() ?? "";
    }

    async Task<JsonNode?> Get(string path) => JsonNode.Parse(await GetText(path));

    async Task<string> GetText(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase}", null, res.StatusCode);
        return await res.Content.ReadAsStringAsync();
    }
}
