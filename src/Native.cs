using System.Runtime.InteropServices;
using System.Text;

namespace FileLocker;

static class Native
{
    // ---- Restart Manager: "which processes have these files open?" ----

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint sessionHandle, int flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] fileNames,
        uint appCount, IntPtr apps, uint serviceCount, IntPtr serviceNames);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint sessionHandle, out uint procInfoNeeded, ref uint procInfo,
        IntPtr affectedApps, out uint rebootReasons);

    public static bool AnyProcessUsing(IReadOnlyCollection<string> files)
    {
        if (files.Count == 0) return false;
        if (RmStartSession(out uint session, 0, new StringBuilder(64)) != 0) return false;
        try
        {
            if (RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, IntPtr.Zero, 0, IntPtr.Zero) != 0)
                return false;
            uint count = 0;
            RmGetList(session, out uint needed, ref count, IntPtr.Zero, out _);
            return needed > 0;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    // ---- Top-level windows ----

    public readonly record struct TopWindow(IntPtr Handle, string Title, string ClassName, uint ProcessId);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    const int DWMWA_CLOAKED = 14;
    const uint WM_CLOSE = 0x0010;

    /// <summary>Visible, titled top-level windows belonging to other processes.</summary>
    public static List<TopWindow> GetTopWindows()
    {
        var result = new List<TopWindow>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == self) return true;
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var title = new StringBuilder(len + 1);
            GetWindowText(hWnd, title, title.Capacity);
            var cls = new StringBuilder(256);
            GetClassName(hWnd, cls, cls.Capacity);
            result.Add(new TopWindow(hWnd, title.ToString(), cls.ToString(), pid));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static void CloseWindow(IntPtr hWnd) => PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    // ---- Shell ----

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public const int SHCNE_ASSOCCHANGED = 0x08000000;
}

/// <summary>File Explorer windows, via the Shell.Application COM object. Must be used on an STA thread.</summary>
static class ExplorerWindows
{
    static dynamic? shell;

    static List<(dynamic Window, string Path)> All()
    {
        var list = new List<(dynamic, string)>();
        try
        {
            shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic windows = shell!.Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic? w = windows.Item(i);
                    if (w == null) continue;
                    string? path = null;
                    try { path = (string)w.Document.Folder.Self.Path; } catch { }
                    if (string.IsNullOrEmpty(path))
                    {
                        string url = w.LocationURL;
                        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) path = new Uri(url).LocalPath;
                    }
                    if (!string.IsNullOrEmpty(path)) list.Add((w, path));
                }
                catch { }
            }
        }
        catch
        {
            shell = null;
        }
        return list;
    }

    static bool IsAtOrUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static bool AnyShowing(string root) => All().Any(w => IsAtOrUnder(w.Path, root));

    /// <summary>Navigates an Explorer window currently showing <paramref name="fromDir"/> to <paramref name="toDir"/>.</summary>
    public static bool NavigateFrom(string fromDir, string toDir)
    {
        foreach (var (window, path) in All())
        {
            if (!path.Equals(fromDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                window.Navigate2(toDir);
                return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>Moves any Explorer window inside <paramref name="root"/> back to <paramref name="fallbackDir"/>.</summary>
    public static void LeaveFolder(string root, string fallbackDir)
    {
        foreach (var (window, path) in All())
        {
            if (!IsAtOrUnder(path, root)) continue;
            try
            {
                if (Directory.Exists(fallbackDir)) window.Navigate2(fallbackDir);
                else window.Quit();
            }
            catch { }
        }
    }
}
