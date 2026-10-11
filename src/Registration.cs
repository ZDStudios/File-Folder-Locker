using Microsoft.Win32;

namespace FileLocker;

/// <summary>Registers ".locked" for the current user so double-clicking one in File Explorer opens File Locker.</summary>
static class Registration
{
    const string ProgId = "FileLocker.LockedItem";

    public static void Register()
    {
        string exe = Environment.ProcessPath!;
        string command = $"\"{exe}\" --open \"%1\"";
        string icon = $"\"{exe}\",0";

        using (var current = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
        {
            if (current?.GetValue("") as string == command && !HasForeignUserChoice()) return;
        }

        using (var ext = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Locker.Extension}"))
            ext.SetValue("", ProgId);

        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", "Locked item");
            using (var di = prog.CreateSubKey("DefaultIcon")) di.SetValue("", icon);
            using (var shell = prog.CreateSubKey(@"shell\open"))
            {
                shell.SetValue("", "Unlock");
                using var cmd = shell.CreateSubKey("command");
                cmd.SetValue("", command);
            }
        }

        // If ".locked" was previously set to open with some other app, clear that choice.
        if (HasForeignUserChoice())
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{Locker.Extension}\UserChoice", false); }
            catch { }
        }

        Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    static bool HasForeignUserChoice()
    {
        using var choice = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{Locker.Extension}\UserChoice");
        return choice?.GetValue("ProgId") is string id && id != ProgId;
    }
}
