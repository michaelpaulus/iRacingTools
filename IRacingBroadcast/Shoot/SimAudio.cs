using System.Diagnostics;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

/// <summary>
/// Which sound device the sim plays on, to record it. That's often not the Windows default: iRacing can be set to a
/// specific device or to the default communications device (a headset, a USB DAC).
/// </summary>
static class SimAudio
{
    public static MMDevice Device(BroadcastConfig cfg)
    {
        var en = new MMDeviceEnumerator();
        var active = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

        if (cfg.GameAudioDevice.Length > 0)
            return active.FirstOrDefault(d => d.FriendlyName.Contains(cfg.GameAudioDevice, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"No sound device called \"{cfg.GameAudioDevice}\" (GameAudioDevice). There are: {string.Join(", ", active.Select(d => d.FriendlyName))}.");

        // Best: the device the sim has a sound session open on right now.
        foreach (var d in active)
        {
            var sessions = d.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var name = Process.GetProcessById((int)sessions[i].GetProcessID).ProcessName;
                    if (name.StartsWith("iRacingSim", StringComparison.OrdinalIgnoreCase)) return Found(d, "iRacing is playing on it");
                }
                catch (ArgumentException) { } // the process has gone
            }
        }

        // Else iRacing's own setting (app.ini, [Audio] devSpeakerDefault / devSpeakerId).
        var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "app.ini");
        if (File.Exists(ini))
        {
            var text = File.ReadAllText(ini);
            string? Value(string key) => Regex.Match(text, $@"^{key}=([^\s;]*)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
            switch (Value("devSpeakerDefault"))
            {
                case "1": return Found(en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), "iRacing uses the default device");
                case "2": return Found(en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications), "iRacing uses the default communications device");
                case "0" when Value("devSpeakerId") is { Length: > 0 } id:
                    var dev = active.FirstOrDefault(d => d.ID.Contains(id, StringComparison.OrdinalIgnoreCase));
                    if (dev != null) return Found(dev, "iRacing's chosen device");
                    break;
            }
        }
        return Found(en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), "the Windows default");
    }

    static MMDevice Found(MMDevice d, string why)
    {
        Console.WriteLine($"  Recording the sim's sound from {d.FriendlyName} ({why}).");
        return d;
    }
}
