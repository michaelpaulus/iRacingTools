using System.Diagnostics;
using System.Windows.Automation;

namespace IRacingCommandCenter;

/// <summary>
/// After a driver swap, presses the session's Join button in the iRacing app ("Join", "Join Race", "Join Practice"),
/// so the incoming driver only has to click Drive in the sim.
///
/// It finds the button through Windows UI Automation (the accessibility interface screen readers use) by its exact
/// name, and presses it the way a screen reader would. The app fills in its accessibility tree only after it is first
/// asked, and the page takes a while to load after the swap, so it keeps looking until the button appears.
/// </summary>
public static class AutoJoin
{
    /// <summary>Waits for a Join button and presses it. Returns the button's name, or null if none showed up in time.</summary>
    public static Task<string?> PressAsync(IReadOnlyCollection<string> names, TimeSpan timeout, CancellationToken cancel = default) =>
        Task.Run(async () =>
        {
            var deadline = DateTime.Now + timeout;
            while (DateTime.Now < deadline && !cancel.IsCancellationRequested)
            {
                try
                {
                    if (Find(names) is { } button)
                    {
                        var name = Clean(button.Current.Name);
                        Press(button);
                        Log.Write($"Driver swap: pressed \"{name}\" in the iRacing app");
                        return name;
                    }
                }
                catch (ElementNotAvailableException) { } // the page changed under us: look again
                catch (Exception ex) { Log.Write($"Auto-join: {ex.GetType().Name}: {ex.Message}"); }
                await Task.Delay(1000, cancel).ContinueWith(_ => { });
            }
            return null;
        }, cancel);

    static AutomationElement? Find(IReadOnlyCollection<string> names)
    {
        var pids = Process.GetProcessesByName("iRacingUI").Select(p => p.Id).ToHashSet();
        if (pids.Count == 0 || names.Count == 0) return null;
        var window = AutomationElement.RootElement
            .FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "iRacing"))
            .Cast<AutomationElement>()
            .FirstOrDefault(w => pids.Contains(w.Current.ProcessId));
        if (window == null) return null;

        // Exact names only, as a button or a link styled as one, and visible and enabled: never "Join a league" and the like.
        // The app's names start with an icon-font glyph ("Join Practice"), so those are stripped before comparing.
        var kind = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink));
        return window.FindAll(TreeScope.Descendants, kind)
            .Cast<AutomationElement>()
            .FirstOrDefault(e => names.Contains(Clean(e.Current.Name), StringComparer.OrdinalIgnoreCase)
                && e.Current.IsEnabled && !e.Current.IsOffscreen);
    }

    /// <summary>The visible text of a name: icon-font glyphs (private-use characters) and extra spaces removed.</summary>
    public static string Clean(string name) =>
        string.Join(' ', new string(name.Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.PrivateUse).ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static void Press(AutomationElement button)
    {
        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var p) && p is InvokePattern invoke)
        {
            invoke.Invoke();
            return;
        }
        // no Invoke support: focus it and press Enter, which follows a link or presses a button
        button.SetFocus();
        SendKeys.SendWait("{ENTER}");
    }
}
