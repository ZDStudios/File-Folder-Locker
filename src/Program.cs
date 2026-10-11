namespace FileLocker;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length >= 2 && args[0].Equals("--open", StringComparison.OrdinalIgnoreCase))
                return Viewer.Run(args[1], args.Contains("--passcode-stdin"));
            return RunManager();
        }
        catch (Exception ex)
        {
            Ui.Error(null, Ui.Describe(ex));
            return 1;
        }
    }

    static int RunManager()
    {
        using var instance = new Mutex(true, @"Local\FileLocker.Main", out bool firstInstance);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FileLocker.ShowMain");
        if (!firstInstance)
        {
            showSignal.Set();
            return 0;
        }

        try { Registration.Register(); }
        catch (Exception ex) { Ui.Error(null, $"Couldn't register .locked files with File Explorer.\n\n{Ui.Describe(ex)}"); }

        ViewSession.CleanupOrphans();
        Locker.CleanupScratch();

        var settings = AppSettings.Load();
        string? passcode = settings.HasPasscode
            ? PasscodeForm.Ask(null, "File Locker", "Enter your passcode to manage locked items.", settings.VerifyPasscode)
            : PasscodeForm.Create(null, "Welcome to File Locker",
                "Create a passcode. You'll use it to unlock your files and folders.\n\n" +
                "There's no way to recover it if you forget it, so pick one you'll remember.");
        if (passcode == null) return 0;

        if (!settings.HasPasscode)
        {
            settings.SetPasscode(passcode);
            settings.Save();
        }

        using var form = new MainForm(settings, passcode);
        using var registeredWait = new RegisteredWaitHolder(ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) =>
        {
            try { form.BeginInvoke(form.BringToFrontFromOtherInstance); } catch { }
        }, null, -1, false));
        Application.Run(form);
        return 0;
    }

    sealed class RegisteredWaitHolder(RegisteredWaitHandle handle) : IDisposable
    {
        public void Dispose() => handle.Unregister(null);
    }
}
