using System.Diagnostics;

namespace IRacingCommandCenter;

/// <summary>
/// Shuts OneDrive down while the sim runs, so it isn't uploading telemetry (CPU, disk and network) mid-session,
/// and starts it again afterwards. Only restarts OneDrive if it was this app that shut it down.
/// </summary>
public class OneDrivePause
{
    static string ExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe");

    public bool PausedByUs { get; private set; }

    public void Pause()
    {
        if (PausedByUs || Process.GetProcessesByName("OneDrive").Length == 0 || !File.Exists(ExePath)) return;
        try
        {
            Process.Start(ExePath, "/shutdown");
            PausedByUs = true;
            Log.Write("OneDrive shut down while iRacing runs");
        }
        catch (Exception ex) { Log.Write($"Error: couldn't shut down OneDrive: {ex.Message}"); }
    }

    public void Resume()
    {
        if (!PausedByUs) return;
        PausedByUs = false;
        try
        {
            Process.Start(ExePath, "/background");
            Log.Write("OneDrive restarted");
        }
        catch (Exception ex) { Log.Write($"Error: couldn't restart OneDrive: {ex.Message}"); }
    }
}
