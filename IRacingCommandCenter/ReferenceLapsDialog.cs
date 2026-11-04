using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IRacingCommandCenter;

/// <summary>
/// The voice coach's reference laps: the laps it compares your driving against, per car and track. Add a Garage 61
/// telemetry export (.csv) or an iRacing recording (.ibt), remove them, and switch on the automatic Garage 61 download.
/// Files live next to the voice agent in reference\&lt;car_track&gt;\ (recordings go to reference\inbox\ and the agent
/// files them under their car and track when it next starts a session).
/// </summary>
public class ReferenceLapsDialog : Form
{
    const string TokenVariable = "GARAGE61_TOKEN";
    readonly string _settingsPath, _referenceDir, _coachDir;
    readonly ListView _list;
    readonly CheckBox _download;
    readonly RadioButton _near, _fastest;
    readonly Label _token;

    public ReferenceLapsDialog(string voiceSettingsPath)
    {
        _settingsPath = voiceSettingsPath;
        var dir = Path.GetDirectoryName(voiceSettingsPath)!;
        _referenceDir = Path.Combine(dir, "reference");
        _coachDir = Path.Combine(dir, "coach");

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Reference laps";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(640, 470);

        var intro = new Label
        {
            Left = 15, Top = 10, Width = 610, Height = 36,
            Text = "The coach compares each corner with the fastest reference lap for the car and track you're driving " +
                   "(\"the reference brakes 20 metres later\"). Add a Garage 61 telemetry export (.csv) or an iRacing recording (.ibt).",
        };
        _list = new ListView { Left = 15, Top = 50, Width = 610, Height = 170, View = View.Details, FullRowSelect = true, MultiSelect = true };
        _list.Columns.Add("Car and track", 270);
        _list.Columns.Add("Lap", 240);
        _list.Columns.Add("Added", 95);

        var add = new Button { Left = 15, Top = 228, Width = 150, Height = 28, Text = "Add CSV or .ibt..." };
        add.Click += (_, _) => AddLap();
        var remove = new Button { Left = 175, Top = 228, Width = 90, Height = 28, Text = "Remove" };
        remove.Click += (_, _) => RemoveSelected();
        var open = new Button { Left = 275, Top = 228, Width = 110, Height = 28, Text = "Open folder" };
        open.Click += (_, _) => { Directory.CreateDirectory(_referenceDir); Process.Start(new ProcessStartInfo(_referenceDir) { UseShellExecute = true }); };

        var group = new GroupBox { Left = 15, Top = 268, Width = 610, Height = 155, Text = "Garage 61" };
        var settings = Read();
        _download = new CheckBox
        {
            Left = 12, Top = 22, Width = 585, Text = "Download a comparison lap automatically when I start a car and track",
            Checked = settings?["Garage61Download"]?.GetValue<bool>() == true,
        };
        bool fastest = settings?["Garage61Pick"]?.GetValue<string>() == "fastest";
        _near = new RadioButton { Left = 30, Top = 47, Width = 560, Text = "A driver a bit quicker than me (iRating up to 1000 above mine)", Checked = !fastest };
        _fastest = new RadioButton { Left = 30, Top = 70, Width = 560, Text = "The fastest lap I can see", Checked = fastest };
        _token = new Label { Left = 12, Top = 96, Width = 585, Height = 20 };
        var help = new LinkLabel
        {
            Left = 12, Top = 118, Width = 585, Height = 32,
            Text = "Needs Garage 61 Pro and a personal access token: garage61.net/developer > My applications. Then run setx " +
                   TokenVariable + " \"your-token\". Without approval from Garage 61 the API only shows your own and your teammates' laps.",
        };
        help.LinkArea = new LinkArea(help.Text.IndexOf("garage61.net/developer"), "garage61.net/developer".Length);
        help.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://garage61.net/developer") { UseShellExecute = true });
        group.Controls.AddRange([_download, _near, _fastest, _token, help]);
        _download.CheckedChanged += (_, _) => _near.Enabled = _fastest.Enabled = _download.Checked;
        _near.Enabled = _fastest.Enabled = _download.Checked;

        var save = new Button { Left = 455, Top = 432, Width = 80, Height = 28, Text = "Save", DialogResult = DialogResult.OK };
        var cancel = new Button { Left = 545, Top = 432, Width = 80, Height = 28, Text = "Cancel", DialogResult = DialogResult.Cancel };
        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([intro, _list, add, remove, open, group, save, cancel]);
        RefreshList();
        ShowToken();
    }

    JsonObject? Read()
    {
        try { return JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject; }
        catch (Exception) { return null; }
    }

    void ShowToken()
    {
        bool set = (Environment.GetEnvironmentVariable(TokenVariable) ?? Environment.GetEnvironmentVariable(TokenVariable, EnvironmentVariableTarget.User)) != null;
        _token.Text = set ? $"Token: {TokenVariable} is set." : $"Token: {TokenVariable} isn't set yet.";
        _token.ForeColor = set ? Color.ForestGreen : Color.DarkOrange;
    }

    /// <summary>Saves the Garage 61 choices into voice.json, leaving everything else as it was.</summary>
    public void SaveSettings()
    {
        var root = Read() ?? new JsonObject();
        root["Garage61Download"] = _download.Checked;
        root["Garage61Pick"] = _fastest.Checked ? "fastest" : "near";
        root["Garage61TokenVariable"] ??= TokenVariable;
        File.WriteAllText(_settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    static string Pretty(string key) => key.Replace('_', ' ');

    void RefreshList()
    {
        _list.Items.Clear();
        if (!Directory.Exists(_referenceDir)) return;
        foreach (var folder in Directory.EnumerateDirectories(_referenceDir).OrderBy(d => d))
        {
            bool inbox = Path.GetFileName(folder) == "inbox";
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ibt", StringComparison.OrdinalIgnoreCase)))
            {
                var item = new ListViewItem([inbox ? "(sorted next session)" : Pretty(Path.GetFileName(folder)), Path.GetFileName(file),
                    File.GetLastWriteTime(file).ToString("d MMM yyyy")]) { Tag = file };
                _list.Items.Add(item);
            }
        }
    }

    /// <summary>Car and track names the coach has seen (its saved files) or that already have reference laps.</summary>
    List<string> KnownTracks()
    {
        var keys = new List<string>();
        if (Directory.Exists(_coachDir)) keys.AddRange(Directory.EnumerateFiles(_coachDir, "*.json").OrderByDescending(File.GetLastWriteTime).Select(Path.GetFileNameWithoutExtension)!);
        if (Directory.Exists(_referenceDir)) keys.AddRange(Directory.EnumerateDirectories(_referenceDir).Select(Path.GetFileName).Where(k => k != "inbox")!);
        return keys.Distinct().ToList();
    }

    void AddLap()
    {
        using var pick = new OpenFileDialog
        {
            Title = "Add a reference lap",
            Filter = "Telemetry (*.csv;*.ibt)|*.csv;*.ibt|Garage 61 export (*.csv)|*.csv|iRacing recording (*.ibt)|*.ibt",
            Multiselect = true,
        };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        foreach (var file in pick.FileNames)
        {
            string dest;
            if (file.EndsWith(".ibt", StringComparison.OrdinalIgnoreCase))
                dest = Path.Combine(_referenceDir, "inbox"); // the recording says which car and track it is
            else if (ChooseTrack(Path.GetFileName(file)) is { } key)
                dest = Path.Combine(_referenceDir, key);
            else continue;
            try
            {
                Directory.CreateDirectory(dest);
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
                Log.Write($"Reference lap added: {Path.GetFileName(file)} ({Path.GetFileName(dest)})");
            }
            catch (IOException ex) { MessageBox.Show(this, $"Couldn't copy {Path.GetFileName(file)}: {ex.Message}", Text); }
        }
        RefreshList();
    }

    /// <summary>A CSV doesn't say which car and track it's from: ask.</summary>
    string? ChooseTrack(string fileName)
    {
        var keys = KnownTracks();
        if (keys.Count == 0)
        {
            MessageBox.Show(this, "The coach doesn't know any cars and tracks yet. Drive the car and track once with the voice agent running " +
                                  "(or add an .ibt recording of it), then add the CSV.", Text);
            return null;
        }
        using var ask = new Form
        {
            Text = "Which car and track?", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(440, 120), Font = Font,
            AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi,
        };
        var label = new Label { Left = 12, Top = 12, Width = 416, Height = 20, Text = $"Which car and track is {fileName} from?", AutoEllipsis = true };
        var combo = new ComboBox { Left = 12, Top = 38, Width = 416, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(keys.Select(Pretty).ToArray<object>());
        combo.SelectedIndex = 0;
        var ok = new Button { Left = 262, Top = 80, Width = 80, Height = 28, Text = "Add", DialogResult = DialogResult.OK };
        var cancel = new Button { Left = 348, Top = 80, Width = 80, Height = 28, Text = "Cancel", DialogResult = DialogResult.Cancel };
        ask.AcceptButton = ok;
        ask.CancelButton = cancel;
        ask.Controls.AddRange([label, combo, ok, cancel]);
        return ask.ShowDialog(this) == DialogResult.OK ? keys[combo.SelectedIndex] : null;
    }

    void RemoveSelected()
    {
        var files = _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
        if (files.Count == 0) return;
        if (MessageBox.Show(this, $"Remove {files.Count} reference lap{(files.Count == 1 ? "" : "s")}? The files are moved to the Recycle Bin.", Text,
                MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        foreach (var f in files)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
        RefreshList();
    }
}
