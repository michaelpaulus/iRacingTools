namespace IRacingCommandCenter;

/// <summary>
/// Sets up driver swaps: names whoever is signed in to the iRacing app now, and adds another driver (the app opens on
/// an empty profile for them to sign in once).
/// </summary>
public class DriversDialog : Form
{
    public DriversDialog(TrayApp app)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Drivers";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(520, 300);

        var current = app.Drivers.Current;
        var intro = new Label
        {
            Left = 15, Top = 12, Width = 490, Height = 64,
            Text = "Each driver keeps their own iRacing sign-in on this PC. To swap at a pit stop: leave the car (ESC in your " +
                   "stall), close the sim, then press Swap. The iRacing app restarts signed in as the other driver: they " +
                   "click Join, then Drive. No passwords are stored.",
        };

        var nowLabel = new Label { Left = 15, Top = 86, Width = 490, Text = "Who is signed in to the iRacing app right now:" };
        var nowName = new TextBox { Left = 15, Top = 108, Width = 250, Text = current ?? "" };
        var nowSave = new Button { Left = 275, Top = 106, Width = 100, Height = 28, Text = current is null ? "Save" : "Rename" };
        nowSave.Click += (_, _) => Try(() =>
        {
            if (DriverSwap.CheckName(nowName.Text) is { } bad) throw new InvalidOperationException(bad);
            app.Drivers.NameCurrent(nowName.Text.Trim());
            Close();
        });

        var addLabel = new Label { Left = 15, Top = 150, Width = 490, Text = "Add another driver (closes the iRacing app and opens it for them to sign in):" };
        var addName = new TextBox { Left = 15, Top = 172, Width = 250, Enabled = current != null };
        var add = new Button { Left = 275, Top = 170, Width = 100, Height = 28, Text = "Add", Enabled = current != null };
        add.Click += async (_, _) =>
        {
            if (DriverSwap.CheckName(addName.Text) is { } bad) { MessageBox.Show(this, bad, Text); return; }
            if (DriverSwap.SimRunning) { MessageBox.Show(this, "Close the sim first.", Text); return; }
            Enabled = false;
            bool ok = await app.AddDriverAsync(addName.Text.Trim());
            Enabled = true;
            if (ok) Close();
        };
        var hint = new Label
        {
            Left = 15, Top = 206, Width = 490, Height = 40, ForeColor = Color.DimGray,
            Text = current is null ? "Name the driver who is signed in now first." : "Saved drivers: " + string.Join(", ", app.Drivers.Drivers),
        };

        var close = new Button { Left = 405, Top = 258, Width = 100, Height = 28, Text = "Close", DialogResult = DialogResult.Cancel };
        Controls.AddRange([intro, nowLabel, nowName, nowSave, addLabel, addName, add, hint, close]);
        CancelButton = close;
    }

    void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
    }
}
