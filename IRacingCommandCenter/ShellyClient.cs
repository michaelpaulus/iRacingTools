using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace IRacingCommandCenter;

public record Outlet(int Port, string Label, bool On, double Watts);

/// <summary>
/// Talks to a Shelly plug (Gen2 or later) over its local RPC API. Each switch is an outlet, numbered from 1
/// (switch:0 is outlet 1).
/// </summary>
public class ShellyClient(Config config)
{
    /// <summary>Plug uptime as of the last request, so a power cut (the plug comes back off) can be spotted.</summary>
    public TimeSpan? Uptime { get; private set; }

    public Task SetAsync(IReadOnlyCollection<int> ports, bool on) => WithClientAsync(async http =>
    {
        foreach (var port in ports)
        {
            using var doc = await RpcAsync(http, $"Switch.Set?id={port - 1}&on={(on ? "true" : "false")}");
            if (!doc.RootElement.TryGetProperty("was_on", out _))
                throw new InvalidOperationException($"Outlet {port} switch failed: {doc.RootElement}");
        }
        return true;
    });

    public Task<List<Outlet>> GetAsync() => WithClientAsync(async http =>
    {
        using var status = await RpcAsync(http, "Shelly.GetStatus");
        using var settings = await RpcAsync(http, "Shelly.GetConfig");
        Uptime = status.RootElement.TryGetProperty("sys", out var sys) && sys.TryGetProperty("uptime", out var up)
            ? TimeSpan.FromSeconds(up.GetDouble()) : null;
        var deviceName = settings.RootElement.TryGetProperty("sys", out var sc) && sc.GetProperty("device").TryGetProperty("name", out var dn)
            && dn.ValueKind == JsonValueKind.String ? dn.GetString()! : "";

        return status.RootElement.EnumerateObject()
            .Where(p => p.Name.StartsWith("switch:"))
            .Select(p =>
            {
                int id = int.Parse(p.Name["switch:".Length..]);
                var name = settings.RootElement.TryGetProperty(p.Name, out var c) && c.TryGetProperty("name", out var n)
                    && n.ValueKind == JsonValueKind.String ? n.GetString()! : deviceName;
                return new Outlet(id + 1, name, p.Value.GetProperty("output").GetBoolean(),
                    p.Value.TryGetProperty("apower", out var w) ? Math.Round(w.GetDouble(), 1) : 0);
            })
            .OrderBy(o => o.Port)
            .ToList();
    });

    static async Task<JsonDocument> RpcAsync(HttpClient http, string call)
    {
        var response = await http.GetAsync($"rpc/{call}");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("The Shelly plug has a password set; turn it off in the Shelly app");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Shelly {call} failed: {body}");
        return JsonDocument.Parse(body);
    }

    async Task<T> WithClientAsync<T>(Func<HttpClient, Task<T>> body)
    {
        var ip = await ResolveIpAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://{ip}/"), Timeout = TimeSpan.FromSeconds(10) };
        return await body(http);
    }

    // The plug gets its address from DHCP; if the saved IP stops answering, find it again by MAC.
    async Task<string> ResolveIpAsync()
    {
        if (config.Ip.Length == 0 || config.Mac.Length == 0) return await DiscoverAsync();
        using (var ping = new Ping())
        {
            try { if ((await ping.SendPingAsync(config.Ip, 1000)).Status == IPStatus.Success) return config.Ip; }
            catch (PingException) { }
        }

        var prefix = string.Join('.', config.Ip.Split('.').Take(3));
        await Task.WhenAll(Enumerable.Range(1, 254).Select(async i =>
        {
            using var p = new Ping();
            try { await p.SendPingAsync($"{prefix}.{i}", 400); } catch (PingException) { }
        }));

        var psi = new ProcessStartInfo("arp", "-a") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var arp = Process.Start(psi)!;
        var table = await arp.StandardOutput.ReadToEndAsync();
        await arp.WaitForExitAsync();

        var mac = config.Mac.Replace(':', '-').ToLowerInvariant();
        var line = table.Split('\n').FirstOrDefault(l => l.ToLowerInvariant().Contains(mac))
            ?? throw new InvalidOperationException($"Shelly plug ({config.Mac}) not found on the network");
        var ip = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (ip != config.Ip)
        {
            Log.Write($"Plug moved to {ip}; updating config");
            config.Ip = ip;
            config.Save();
        }
        return ip;
    }

    static DateTime _lastScan = DateTime.MinValue;

    /// <summary>
    /// No plug set up yet: ask every address on this PC's local networks for a Shelly (Gen2 or later answers
    /// /shelly with its MAC and model) and remember the one found. With several, none is picked: switching the wrong
    /// one could cut power to something else, so the error lists them to choose from in config.json.
    /// </summary>
    async Task<string> DiscoverAsync()
    {
        if (DateTime.Now - _lastScan < TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("No Shelly plug set up yet; looking again shortly");
        _lastScan = DateTime.Now;

        var prefixes = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."))
            .Select(a => string.Join('.', a.ToString().Split('.').Take(3)))
            .Distinct().ToList();

        var found = new System.Collections.Concurrent.ConcurrentBag<(string Ip, string Mac, string Model)>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var gate = new SemaphoreSlim(64);
        await Task.WhenAll(prefixes.SelectMany(p => Enumerable.Range(1, 254).Select(i => $"{p}.{i}")).Select(async ip =>
        {
            await gate.WaitAsync();
            try
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync($"http://{ip}/shelly"));
                var r = doc.RootElement;
                if (r.TryGetProperty("gen", out var gen) && gen.GetInt32() >= 2 && r.TryGetProperty("mac", out var mac))
                    found.Add((ip, mac.GetString() ?? "", r.TryGetProperty("model", out var m) ? m.GetString() ?? "" : ""));
            }
            catch { /* nothing there, or not a Shelly */ }
            finally { gate.Release(); }
        }));

        static string Dashed(string mac) => string.Join('-', mac.Replace(":", "").Replace("-", "").ToUpperInvariant().Chunk(2).Select(c => new string(c)));
        var candidates = found.Where(f => config.Mac.Length == 0 || Dashed(f.Mac) == Dashed(config.Mac)).OrderBy(f => f.Ip).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("No Shelly plug found on the network: check it's on the same network as this PC");
        if (candidates.Count > 1)
            throw new InvalidOperationException("Several Shelly devices found (" + string.Join(", ", candidates.Select(c => $"{c.Model} {Dashed(c.Mac)} at {c.Ip}")) +
                "): put the plug's ip and mac in config.json");

        var plug = candidates[0];
        config.Ip = plug.Ip;
        config.Mac = Dashed(plug.Mac);
        config.Save();
        Log.Write($"Found the Shelly plug: {plug.Model} {config.Mac} at {plug.Ip}");
        return plug.Ip;
    }
}
