using System.Diagnostics;
using System.Text;

namespace FileLocker;

sealed class MainForm : Form
{
    enum Status { Locked, Unlocked, Missing }

    readonly AppSettings settings;
    string passcode;
    readonly ListView list;
    readonly Button openButton, lockNowButton, showButton, removeLockButton, forgetButton, lockAllButton;
    readonly Dictionary<string, ItemKind?> kindCache = new(StringComparer.OrdinalIgnoreCase);

    public MainForm(AppSettings settings, string passcode)
    {
        this.settings = settings;
        this.passcode = passcode;

        Text = "File Locker";
        Icon = AppIcon.Window;
        Font = Ui.Font;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(LogicalToDeviceUnits(1010), LogicalToDeviceUnits(480));
        MinimumSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(320));
        AllowDrop = true;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4) };
        toolbar.Controls.Add(MakeButton("Lock files…", (_, _) => LockFiles()));
        toolbar.Controls.Add(MakeButton("Lock folder…", (_, _) => LockFolder()));
        toolbar.Controls.Add(new Label { Width = LogicalToDeviceUnits(12) });
        toolbar.Controls.Add(openButton = MakeButton("Unlock && open", (_, _) => OpenSelected()));
        toolbar.Controls.Add(lockNowButton = MakeButton("Lock now", (_, _) => LockSelectedNow()));
        toolbar.Controls.Add(lockAllButton = MakeButton("Lock all", (_, _) => LockAllNow()));
        toolbar.Controls.Add(showButton = MakeButton("Show in folder", (_, _) => ShowSelected()));
        toolbar.Controls.Add(new Label { Width = LogicalToDeviceUnits(12) });
        toolbar.Controls.Add(removeLockButton = MakeButton("Remove lock…", (_, _) => RemoveLockSelected()));
        toolbar.Controls.Add(forgetButton = MakeButton("Remove from list", (_, _) => ForgetSelected()));
        toolbar.Controls.Add(MakeButton("Change passcode…", (_, _) => ChangePasscode()));

        list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
        };
        list.Columns.Add("Name", LogicalToDeviceUnits(240));
        list.Columns.Add("Status", LogicalToDeviceUnits(150));
        list.Columns.Add("Type", LogicalToDeviceUnits(80));
        list.Columns.Add("Location", LogicalToDeviceUnits(400));
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        list.DoubleClick += (_, _) => OpenSelected();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) OpenSelected(); };

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = LogicalToDeviceUnits(52),
            Padding = new Padding(10, 6, 10, 6),
            ForeColor = SystemColors.GrayText,
            Text = "Drag files or folders here to lock them. Locked items stay in File Explorer with a padlock icon — " +
                   "double-click one to unlock it, and it locks again automatically when you close it.",
        };

        Controls.Add(list);
        Controls.Add(toolbar);
        Controls.Add(hint);

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) LockPaths(paths);
        };

        var refresh = new System.Windows.Forms.Timer { Interval = 1500 };
        refresh.Tick += (_, _) => UpdateStatuses();
        refresh.Start();

        RebuildList();
    }

    Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(3) };
        b.Click += onClick;
        return b;
    }

    public void BringToFrontFromOtherInstance()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    // ---- list ----

    static Status StatusOf(string lockedPath)
    {
        if (ViewSession.IsOpen(lockedPath)) return Status.Unlocked;
        return File.Exists(lockedPath) ? Status.Locked : Status.Missing;
    }

    ItemKind? KindOf(string lockedPath)
    {
        if (kindCache.TryGetValue(lockedPath, out var k) && k != null) return k;
        try { k = LockCrypto.ReadKind(lockedPath); }
        catch { k = null; }
        kindCache[lockedPath] = k;
        return k;
    }

    void RebuildList()
    {
        var selected = SelectedPaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        list.BeginUpdate();
        list.Items.Clear();
        foreach (string path in settings.Items.OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new ListViewItem(Locker.OriginalNameOf(path)) { Tag = path, UseItemStyleForSubItems = false };
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add(Path.GetDirectoryName(path));
            item.Selected = selected.Contains(path);
            list.Items.Add(item);
        }
        list.EndUpdate();
        UpdateStatuses();
    }

    void UpdateStatuses()
    {
        foreach (ListViewItem item in list.Items)
        {
            string path = (string)item.Tag!;
            var status = StatusOf(path);
            var (text, color) = status switch
            {
                Status.Locked => ("Locked", Color.SeaGreen),
                Status.Unlocked => ("Unlocked (open)", Color.DarkOrange),
                _ => ("Missing", SystemColors.GrayText),
            };
            if (item.SubItems[1].Text != text)
            {
                item.SubItems[1].Text = text;
                item.SubItems[1].ForeColor = color;
            }
            string kind = KindOf(path) switch { ItemKind.Folder => "Folder", ItemKind.File => "File", _ => "—" };
            if (item.SubItems[2].Text != kind) item.SubItems[2].Text = kind;
        }
        UpdateButtons();
    }

    List<string> SelectedPaths() => list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    void UpdateButtons()
    {
        var statuses = SelectedPaths().Select(StatusOf).ToList();
        openButton.Enabled = statuses.Any(s => s != Status.Missing);
        lockNowButton.Enabled = statuses.Any(s => s == Status.Unlocked);
        removeLockButton.Enabled = statuses.Any(s => s == Status.Locked);
        showButton.Enabled = statuses.Count == 1 && statuses[0] != Status.Missing;
        forgetButton.Enabled = statuses.Count > 0;
        lockAllButton.Enabled = settings.Items.Any(ViewSession.IsOpen);
    }

    // ---- actions ----

    void LockFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose files to lock",
            Multiselect = true,
            Filter = "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) LockPaths(dialog.FileNames);
    }

    void LockFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder to lock",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) LockPaths(new[] { dialog.SelectedPath });
    }

    void LockPaths(IReadOnlyList<string> paths)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var locked = new List<string>();

        // Already-locked items just get added to the list.
        var toLock = new List<string>();
        foreach (string p in paths)
        {
            if (Locker.IsLockedPath(p) && File.Exists(p))
            {
                try { LockCrypto.ReadKind(p); locked.Add(Path.GetFullPath(p)); }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(p)}: {Ui.Describe(ex)}"); }
            }
            else toLock.Add(p);
        }

        if (toLock.Count > 0)
        {
            string label = toLock.Count == 1 ? $"Locking \"{Path.GetFileName(toLock[0].TrimEnd('\\'))}\"…" : $"Locking {toLock.Count} items…";
            Busy.Run(this, label, () =>
            {
                foreach (string p in toLock)
                {
                    try { locked.Add(Locker.Lock(p, passcode)); }
                    catch (LockedWithLeftoversException ex) { locked.Add(ex.LockedPath); warnings.Add(ex.Message); }
                    catch (Exception ex) { errors.Add($"{Path.GetFileName(p.TrimEnd('\\'))}: {Ui.Describe(ex)}"); }
                }
            });
        }

        foreach (string p in locked) settings.AddItem(p);
        SaveSettings();
        RebuildList();
        if (errors.Count > 0) Ui.Error(this, "Some items couldn't be locked:\n\n" + string.Join("\n\n", errors));
        if (warnings.Count > 0) Ui.Error(this, string.Join("\n\n", warnings));
    }

    void OpenSelected()
    {
        foreach (string path in SelectedPaths().Where(p => StatusOf(p) != Status.Missing))
        {
            // Hand the passcode to the viewer through a pipe so it doesn't ask again.
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true };
            psi.ArgumentList.Add("--open");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("--passcode-stdin");
            using var process = Process.Start(psi);
            if (process == null) continue;
            process.StandardInput.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(passcode)));
            process.StandardInput.Close();
        }
    }

    void LockSelectedNow()
    {
        foreach (string path in SelectedPaths()) ViewSession.RequestLock(path);
    }

    void LockAllNow()
    {
        foreach (string path in settings.Items) ViewSession.RequestLock(path);
    }

    void ShowSelected()
    {
        var path = SelectedPaths().FirstOrDefault();
        if (path != null && File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    void RemoveLockSelected()
    {
        var targets = SelectedPaths().Where(p => StatusOf(p) == Status.Locked).ToList();
        if (targets.Count == 0) return;
        string what = targets.Count == 1 ? $"\"{Locker.OriginalNameOf(targets[0])}\"" : $"these {targets.Count} items";
        if (MessageBox.Show(this,
                $"Remove the lock from {what}?\n\nThey'll be turned back into normal files and folders that anyone can open.",
                "Remove lock", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var errors = new List<string>();
        var done = new List<string>();
        Busy.Run(this, "Removing lock…", () =>
        {
            foreach (string p in targets)
            {
                try { Locker.RemoveLock(p, passcode); done.Add(p); }
                catch (Exception ex) { errors.Add($"{Locker.OriginalNameOf(p)}: {Ui.Describe(ex)}"); }
            }
        });
        foreach (string p in done) settings.RemoveItem(p);
        SaveSettings();
        RebuildList();
        if (errors.Count > 0) Ui.Error(this, "Some items couldn't be unlocked:\n\n" + string.Join("\n", errors));
    }

    void ForgetSelected()
    {
        var targets = SelectedPaths();
        if (targets.Count == 0) return;
        if (MessageBox.Show(this,
                "Remove the selected items from this list?\n\nThey stay locked — you can still unlock them by double-clicking them in File Explorer.",
                "Remove from list", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        foreach (string p in targets) settings.RemoveItem(p);
        SaveSettings();
        RebuildList();
    }

    void ChangePasscode()
    {
        if (settings.Items.Any(ViewSession.IsOpen))
        {
            Ui.Error(this, "Lock all open items first, then change your passcode.");
            return;
        }
        string? current = PasscodeForm.Ask(this, "Change passcode", "Enter your current passcode.", settings.VerifyPasscode);
        if (current == null) return;
        string? next = PasscodeForm.Create(this, "Choose a new passcode",
            "Every item in this list will be re-locked with it. There's no way to recover a forgotten passcode.");
        if (next == null) return;

        var targets = settings.Items.Where(File.Exists).ToList();
        var errors = new List<string>();
        Busy.Run(this, "Re-locking items with your new passcode…", () =>
        {
            foreach (string p in targets)
            {
                try { Locker.ChangePasscode(p, current, next); }
                catch (Exception ex) { errors.Add($"{Locker.OriginalNameOf(p)}: {Ui.Describe(ex)}"); }
            }
        });

        settings.SetPasscode(next);
        SaveSettings();
        passcode = next;
        if (errors.Count > 0)
            Ui.Error(this, "Your passcode was changed, but these items still use the old one:\n\n" + string.Join("\n", errors));
        else
            MessageBox.Show(this, "Passcode changed.", "File Locker", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void SaveSettings()
    {
        try { settings.Save(); }
        catch (Exception ex) { Ui.Error(this, $"Couldn't save settings.\n\n{Ui.Describe(ex)}"); }
    }
}
