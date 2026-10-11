namespace FileLocker;

/// <summary>The little passcode prompt — both for unlocking and for choosing a new passcode.</summary>
sealed class PasscodeForm : Form
{
    readonly TextBox passcodeBox;
    readonly TextBox? confirmBox;
    readonly Label errorLabel;
    readonly Button okButton;
    readonly Func<string, bool>? verify;
    int failures;

    public string? Result { get; private set; }

    PasscodeForm(string heading, string message, Func<string, bool>? verify)
    {
        this.verify = verify;
        bool creating = verify == null;

        Text = "File Locker";
        Icon = AppIcon.Window;
        Font = Ui.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        int textWidth = LogicalToDeviceUnits(300);
        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int iconSize = LogicalToDeviceUnits(48);
        var picture = new PictureBox
        {
            Image = AppIcon.Picture(iconSize),
            Size = new Size(iconSize, iconSize),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 16, 0),
        };
        layout.Controls.Add(picture, 0, 0);
        layout.SetRowSpan(picture, 8);

        var column = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        column.Controls.Add(new Label
        {
            Text = heading,
            AutoSize = true,
            MaximumSize = new Size(textWidth, 0),
            Font = new Font(Ui.Font.FontFamily, 12f, FontStyle.Bold),
        });
        if (message.Length > 0)
            column.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(textWidth, 0), Margin = new Padding(0, 6, 0, 0) });

        column.Controls.Add(new Label { Text = creating ? "New passcode" : "Passcode", AutoSize = true, Margin = new Padding(0, 12, 0, 2) });
        passcodeBox = new TextBox { UseSystemPasswordChar = true, Width = textWidth };
        column.Controls.Add(passcodeBox);

        if (creating)
        {
            column.Controls.Add(new Label { Text = "Confirm passcode", AutoSize = true, Margin = new Padding(0, 8, 0, 2) });
            confirmBox = new TextBox { UseSystemPasswordChar = true, Width = textWidth };
            column.Controls.Add(confirmBox);
        }

        errorLabel = new Label { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(textWidth, 0), Margin = new Padding(0, 6, 0, 0) };
        column.Controls.Add(errorLabel);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = textWidth, Margin = new Padding(0, 10, 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        okButton = new Button { Text = creating ? "Save" : "Unlock", AutoSize = true };
        okButton.Click += (_, _) => Submit();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(okButton);
        column.Controls.Add(buttons);

        layout.Controls.Add(column, 1, 0);
        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancel;

        Shown += (_, _) =>
        {
            Activate();
            passcodeBox.Focus();
        };
    }

    void Submit()
    {
        string passcode = passcodeBox.Text;
        if (confirmBox != null)
        {
            if (passcode.Length < 4) { ShowError("Use at least 4 characters."); return; }
            if (passcode != confirmBox.Text) { ShowError("The passcodes don't match."); return; }
            Result = passcode;
            DialogResult = DialogResult.OK;
            return;
        }

        if (passcode.Length == 0) return;
        bool ok;
        UseWaitCursor = true;
        try { ok = verify!(passcode); }
        catch (Exception ex) { ShowError(Ui.Describe(ex)); return; }
        finally { UseWaitCursor = false; }

        if (ok)
        {
            Result = passcode;
            DialogResult = DialogResult.OK;
            return;
        }

        failures++;
        ShowError("Wrong passcode.");
        passcodeBox.SelectAll();
        passcodeBox.Focus();
        if (failures >= 3)
        {
            // Slow down guessing after a few wrong tries.
            okButton.Enabled = false;
            var timer = new System.Windows.Forms.Timer { Interval = Math.Min(30, failures * 2) * 1000 };
            timer.Tick += (_, _) => { timer.Dispose(); okButton.Enabled = true; };
            timer.Start();
        }
    }

    void ShowError(string text) => errorLabel.Text = text;

    /// <summary>Asks for an existing passcode, checked with <paramref name="verify"/>. Returns null if cancelled.</summary>
    public static string? Ask(IWin32Window? owner, string heading, string message, Func<string, bool> verify)
    {
        using var form = new PasscodeForm(heading, message, verify);
        return form.ShowDialog(owner) == DialogResult.OK ? form.Result : null;
    }

    /// <summary>Asks for a new passcode (entered twice). Returns null if cancelled.</summary>
    public static string? Create(IWin32Window? owner, string heading, string message)
    {
        using var form = new PasscodeForm(heading, message, null);
        return form.ShowDialog(owner) == DialogResult.OK ? form.Result : null;
    }
}
