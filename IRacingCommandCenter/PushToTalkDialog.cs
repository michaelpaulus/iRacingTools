using System.Text.Json;
using System.Text.Json.Nodes;

namespace IRacingCommandCenter;

/// <summary>The "PushToTalk" block in the voice agent's voice.json and the coach's coach.json.</summary>
public sealed class PushToTalkConfig
{
    public string Type { get; set; } = "None";
    public int Key { get; set; }
    public int JoystickMid { get; set; }
    public int JoystickPid { get; set; }
    public string JoystickName { get; set; } = "";
    public int Button { get; set; }

    /// <summary>The block in an app's settings file, or null when the file has none (the app has no push-to-talk).</summary>
    public static PushToTalkConfig? Read(string? settingsPath)
    {
        try
        {
            if (settingsPath == null || !File.Exists(settingsPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(settingsPath))?["PushToTalk"];
            return node?.Deserialize<PushToTalkConfig>();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Replaces the block in the settings file, leaving everything else in it as it was.</summary>
    public void Write(string settingsPath)
    {
        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject();
        root["PushToTalk"] = JsonSerializer.SerializeToNode(this);
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Waits for a wheel button or key press and saves it as an app's push-to-talk.</summary>
public class PushToTalkDialog : Form
{
    readonly Label _prompt, _result;
    readonly Button _save, _again;
    readonly PushToTalkConfig _captured = new();
    int _attempt;

    public PushToTalkConfig? Chosen { get; private set; }

    public PushToTalkDialog(string appName, PushToTalkConfig? current)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"Push-to-talk: {appName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(400, 170);

        var now = new Label
        {
            Left = 15, Top = 12, Width = 370, ForeColor = Color.DimGray,
            Text = $"Now: {(current == null ? "not set" : new PushToTalk(current).Describe())}",
        };
        _prompt = new Label { Left = 15, Top = 40, Width = 370, Height = 40, Font = new Font("Segoe UI Semibold", 10.5F) };
        _result = new Label { Left = 15, Top = 85, Width = 370, ForeColor = Color.ForestGreen };
        _save = new Button { Left = 130, Top = 125, Width = 80, Height = 28, Text = "Save", Enabled = false, DialogResult = DialogResult.OK };
        _again = new Button { Left = 215, Top = 125, Width = 80, Height = 28, Text = "Try again", Enabled = false };
        var cancel = new Button { Left = 300, Top = 125, Width = 80, Height = 28, Text = "Cancel", DialogResult = DialogResult.Cancel };
        _save.Click += (_, _) => Chosen = _captured;
        _again.Click += (_, _) => Listen();
        Controls.AddRange([now, _prompt, _result, _save, _again, cancel]);
        CancelButton = cancel;
        Shown += (_, _) => Listen();
    }

    void Listen()
    {
        int attempt = ++_attempt;
        _prompt.Text = "Press and release the wheel button or key you want to hold while talking...";
        _result.Text = "";
        _save.Enabled = _again.Enabled = false;
        Task.Run(() => PushToTalk.Bind(_captured, TimeSpan.FromSeconds(30))).ContinueWith(t =>
        {
            if (IsDisposed || attempt != _attempt) return;
            BeginInvoke(() =>
            {
                _again.Enabled = true;
                if (!t.Result) { _prompt.Text = "Nothing pressed in 30 seconds."; return; }
                _prompt.Text = "Got it:";
                _result.Text = new PushToTalk(_captured).Describe();
                _save.Enabled = true;
                _save.Focus();
            });
        });
    }
}
