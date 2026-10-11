namespace FileLocker;

static class AppIcon
{
    static Icon Load(int size)
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("lock.ico")!;
        return new Icon(s, size, size);
    }

    public static Icon Window { get; } = Load(32);
    public static Icon Tray { get; } = Load(SystemInformation.SmallIconSize.Width);
    public static Bitmap Picture(int size) => Load(size).ToBitmap();
}

static class Ui
{
    public static readonly Font Font = new("Segoe UI", 10f);

    public static void Error(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "File Locker", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Windows denied access. The item may be read-only, protected, or open in another program.",
        _ => ex.Message,
    };
}

/// <summary>A small "please wait" dialog that runs work on a background thread.</summary>
static class Busy
{
    public static Exception? Run(IWin32Window? owner, string text, Action work)
    {
        Exception? error = null;
        bool done = false;
        using var form = new Form
        {
            Text = "File Locker",
            Icon = AppIcon.Window,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ControlBox = false,
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            ShowInTaskbar = owner == null,
            TopMost = owner == null,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20),
            Font = Ui.Font,
        };
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        panel.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(form.LogicalToDeviceUnits(360), 0) });
        panel.Controls.Add(new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            Width = form.LogicalToDeviceUnits(340),
            Height = form.LogicalToDeviceUnits(14),
            Margin = new Padding(0, 12, 0, 0),
        });
        form.Controls.Add(panel);
        form.FormClosing += (_, e) => e.Cancel = !done;
        form.Shown += async (_, _) =>
        {
            try { await Task.Run(work); }
            catch (Exception ex) { error = ex; }
            done = true;
            form.Close();
        };
        form.ShowDialog(owner);
        return error;
    }
}
