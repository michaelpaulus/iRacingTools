using Microsoft.Win32;

namespace IRacingCommandCenter;

/// <summary>"Start with Windows" via the per-user Run key.</summary>
public static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "iRacingCommandCenter";

    static string Command => $"\"{Environment.ProcessPath}\" --tray";

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
        if (enabled) key.SetValue(Name, Command);
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>
    /// When "start with Windows" is on but names another exe (it was run from somewhere else before, such as an
    /// installed copy), point it at this one, so Windows starts the build you're running.
    /// </summary>
    public static void KeepCurrent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (key?.GetValue(Name) is string current && !current.Equals(Command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(Name, Command);
                Log.Write($"Start with Windows now runs {Environment.ProcessPath}");
            }
        }
        catch (Exception ex) { Log.Write($"Couldn't update start with Windows: {ex.Message}"); }
    }
}
