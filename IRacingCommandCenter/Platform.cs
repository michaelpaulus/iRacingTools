using Microsoft.Win32;

namespace IRacingCommandCenter;

/// <summary>"Start with Windows" via the per-user Run key.</summary>
public static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "iRacingCommandCenter";
    const string OldName = "iRacingHubPower";

    /// <summary>
    /// First run after the rename: carries over HubPower's settings folder, and swaps its "start with Windows" entry
    /// for this exe.
    /// </summary>
    public static void MigrateFromHubPower()
    {
        try
        {
            var oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iRacingHubPower");
            var oldConfig = Path.Combine(oldDir, "config.json");
            if (!File.Exists(Config.FilePath) && File.Exists(oldConfig))
            {
                Directory.CreateDirectory(Config.Dir);
                File.Copy(oldConfig, Config.FilePath);
                Log.Write($"Settings copied from {oldDir}");
            }
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (key.GetValue(OldName) != null)
            {
                key.DeleteValue(OldName, throwOnMissingValue: false);
                key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
                Log.Write("Start with Windows moved from HubPower to the command center");
            }
        }
        catch (Exception ex) { Log.Write($"Migration from HubPower failed: {ex.Message}"); }
    }

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Name) != null;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
