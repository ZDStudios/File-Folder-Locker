using System.Text;
using Microsoft.Win32;

namespace FileLocker;

/// <summary>
/// Runs when a .locked item is double-clicked: asks for the passcode, opens an unlocked copy,
/// then watches it and locks it again as soon as it's closed.
/// </summary>
static class Viewer
{
    public static int Run(string path, bool passcodeFromStdin)
    {
        string lockedPath = Path.GetFullPath(path);
        string name = Locker.OriginalNameOf(lockedPath);
        ItemKind kind;
        try
        {
            kind = LockCrypto.ReadKind(lockedPath);
        }
        catch (FileNotFoundException)
        {
            Ui.Error(null, $"\"{Path.GetFileName(lockedPath)}\" no longer exists.");
            return 1;
        }
        catch (Exception ex)
        {
            Ui.Error(null, $"Can't open \"{Path.GetFileName(lockedPath)}\".\n\n{Ui.Describe(ex)}");
            return 1;
        }

        string? passcode = passcodeFromStdin ? ReadPasscodeFromStdin() : null;
        if (passcode != null && !LockCrypto.CheckPasscode(lockedPath, passcode)) passcode = null;
        passcode ??= PasscodeForm.Ask(null, $"Unlock \"{name}\"",
            kind == ItemKind.Folder ? "This folder is locked." : "This file is locked.",
            p => LockCrypto.CheckPasscode(lockedPath, p));
        if (passcode == null) return 0;

        using var session = ViewSession.TryBegin(lockedPath, kind);
        if (session == null)
        {
            // Already unlocked by another File Locker window — just show it again.
            ViewSession.ShowExisting(lockedPath, kind);
            return 0;
        }

        ViewSession.CleanupOrphans();
        Locker.CleanupScratch();

        var error = Busy.Run(null, $"Unlocking \"{name}\"…", () => session.Extract(passcode));
        if (error != null)
        {
            session.TryDeletePlain();
            Ui.Error(null, $"Couldn't unlock \"{name}\".\n\n{Ui.Describe(error)}");
            return 1;
        }

        session.Show();
        using var context = new ViewerContext(session, passcode);
        Application.Run(context);
        return 0;
    }

    static string? ReadPasscodeFromStdin()
    {
        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.ASCII);
            string? line = reader.ReadLine();
            return line == null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(line.Trim()));
        }
        catch
        {
            return null;
        }
    }
}

sealed class ViewerContext : ApplicationContext
{
    enum State { Watching, Closing, Locking, PendingDelete, Done }

    const int GraceSeconds = 20;   // how long to wait for the app to show up before asking for a manual lock
    const int IdleSecondsToLock = 2;

    readonly ViewSession session;
    readonly string passcode;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer;
    State state = State.Watching;
    int ticks, idleTicks, pendingTicks;
    bool seenInUse, warnedNoDetect, warnedResealFailed;
    DateTime closeRequestedAt;

    public ViewerContext(ViewSession session, string passcode)
    {
        this.session = session;
        this.passcode = passcode;

        var menu = new ContextMenuStrip();
        menu.Items.Add($"Show \"{Shorten(session.Name, 40)}\"", null, (_, _) => session.Show());
        menu.Items.Add("Lock now", null, (_, _) => LockNow());

        tray = new NotifyIcon
        {
            Icon = AppIcon.Tray,
            Text = Shorten($"Unlocked: {session.Name}", 63),
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => session.Show();
        tray.ShowBalloonTip(3000, "Unlocked",
            $"\"{session.Name}\" will lock again as soon as you close it.", ToolTipIcon.Info);

        SystemEvents.SessionEnding += OnSessionEnding;

        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += OnTick;
        timer.Start();
    }

    void OnTick(object? sender, EventArgs e)
    {
        switch (state)
        {
            case State.Watching:
                if (session.LockRequested) { LockNow(); return; }
                ticks++;
                bool inUse;
                try { inUse = session.IsInUse(); }
                catch { inUse = true; }

                if (inUse)
                {
                    seenInUse = true;
                    idleTicks = 0;
                }
                else if (seenInUse)
                {
                    if (++idleTicks >= IdleSecondsToLock) BeginLock();
                }
                else if (ticks >= GraceSeconds && !warnedNoDetect)
                {
                    warnedNoDetect = true;
                    tray.ShowBalloonTip(8000, "Still unlocked",
                        $"File Locker can't tell when you're finished with \"{session.Name}\". " +
                        "Right-click the padlock in the taskbar and choose Lock now when you're done.", ToolTipIcon.Warning);
                }
                break;

            case State.Closing:
                if (DateTime.UtcNow - closeRequestedAt >= TimeSpan.FromSeconds(1.5)) BeginLock();
                break;

            case State.PendingDelete:
                if (session.TryDeletePlain()) Finish();
                else if (++pendingTicks == 5)
                    tray.ShowBalloonTip(5000, "Almost locked",
                        $"Close \"{session.Name}\" to finish locking it.", ToolTipIcon.Warning);
                break;
        }
    }

    void LockNow()
    {
        if (state != State.Watching) return;
        state = State.Closing;
        closeRequestedAt = DateTime.UtcNow;
        try { session.CloseViewers(); } catch { }
    }

    async void BeginLock()
    {
        state = State.Locking;
        tray.Text = Shorten($"Locking: {session.Name}", 63);
        try
        {
            await Task.Run(() => session.Reseal(passcode));
        }
        catch (Exception ex)
        {
            // Usually means a program still has the file open exclusively; keep watching and retry.
            if (!warnedResealFailed)
            {
                warnedResealFailed = true;
                tray.ShowBalloonTip(6000, "Couldn't lock yet", $"{Ui.Describe(ex)}\nRetrying…", ToolTipIcon.Warning);
            }
            tray.Text = Shorten($"Unlocked: {session.Name}", 63);
            state = State.Watching;
            seenInUse = true;
            idleTicks = 0;
            return;
        }

        if (session.TryDeletePlain()) Finish();
        else state = State.PendingDelete;
    }

    async void Finish()
    {
        state = State.Done;
        timer.Stop();
        tray.Text = Shorten($"Locked: {session.Name}", 63);
        tray.ShowBalloonTip(2000, "Locked", $"\"{session.Name}\" is locked again.", ToolTipIcon.None);
        await Task.Delay(2500);
        ExitThread();
    }

    void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        // Signing out or shutting down: lock synchronously before Windows kills us.
        if (state is State.Done) return;
        try { session.Reseal(passcode); } catch { }
        session.TryDeletePlain();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnding -= OnSessionEnding;
            timer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            session.Dispose();
        }
        base.Dispose(disposing);
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
