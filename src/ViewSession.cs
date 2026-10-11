using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace FileLocker;

/// <summary>
/// One temporarily-unlocked item. Holds a named mutex for as long as it's open (so other File Locker
/// processes can see it's open) and listens on a named event for "lock now" requests from the main app.
/// </summary>
sealed class ViewSession : IDisposable
{
    public string LockedPath { get; }
    public ItemKind Kind { get; }
    public string Name { get; }
    public string PlainPath { get; }

    readonly string sessionDir;
    readonly Mutex mutex;
    readonly EventWaitHandle lockRequest;
    string snapshot = "";
    List<string> sampleFiles = new();
    DateTime sampledAt;
    bool disposed;

    ViewSession(string lockedPath, ItemKind kind, Mutex mutex, EventWaitHandle lockRequest)
    {
        LockedPath = lockedPath;
        Kind = kind;
        Name = Locker.OriginalNameOf(lockedPath);
        sessionDir = Path.Combine(Locker.OpenRoot, IdFor(lockedPath));
        PlainPath = Path.Combine(sessionDir, Name);
        this.mutex = mutex;
        this.lockRequest = lockRequest;
    }

    // ---- cross-process coordination ----

    static string IdFor(string lockedPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(lockedPath).ToUpperInvariant())))[..16];

    static string MutexName(string id) => @"Local\FileLocker.Open." + id;
    static string EventName(string id) => @"Local\FileLocker.Lock." + id;

    public static bool IsOpen(string lockedPath)
    {
        if (!Mutex.TryOpenExisting(MutexName(IdFor(lockedPath)), out var m)) return false;
        m.Dispose();
        return true;
    }

    public static bool RequestLock(string lockedPath)
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(IdFor(lockedPath)), out var ev)) return false;
        using (ev) ev.Set();
        return true;
    }

    /// <summary>Starts a session, or returns null if this item is already open in another process.</summary>
    public static ViewSession? TryBegin(string lockedPath, ItemKind kind)
    {
        string id = IdFor(lockedPath);
        var m = new Mutex(true, MutexName(id), out bool createdNew);
        if (!createdNew)
        {
            m.Dispose();
            return null;
        }
        var ev = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(id));
        return new ViewSession(lockedPath, kind, m, ev);
    }

    /// <summary>Deletes unlocked copies left behind if File Locker was killed or crashed.</summary>
    public static void CleanupOrphans()
    {
        if (!Directory.Exists(Locker.OpenRoot)) return;
        foreach (string dir in Directory.GetDirectories(Locker.OpenRoot))
        {
            if (Mutex.TryOpenExisting(MutexName(Path.GetFileName(dir)), out var m))
            {
                m.Dispose();
                continue;
            }
            Locker.TryDeleteDirectory(dir);
        }
    }

    public bool LockRequested => lockRequest.WaitOne(0);

    // ---- unlocking & viewing ----

    public void Extract(string passcode)
    {
        Locker.TryDeleteDirectory(sessionDir);
        Directory.CreateDirectory(sessionDir);
        Locker.ExtractTo(LockedPath, PlainPath, passcode);
        snapshot = TakeSnapshot();
    }

    /// <summary>Shows an already-unlocked item (used when it's double-clicked while open).</summary>
    public static void ShowExisting(string lockedPath, ItemKind kind) =>
        Show(Path.Combine(Locker.OpenRoot, IdFor(lockedPath), Locker.OriginalNameOf(lockedPath)), kind, lockedPath);

    public void Show() => Show(PlainPath, Kind, LockedPath);

    static void Show(string plainPath, ItemKind kind, string lockedPath)
    {
        if (kind == ItemKind.Folder)
        {
            // Open it in the same Explorer window you double-clicked from, so "Back" leaves (and locks) it.
            string parent = Path.GetDirectoryName(lockedPath)!;
            if (!ExplorerWindows.NavigateFrom(parent, plainPath))
                Process.Start("explorer.exe", $"\"{plainPath}\"");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(plainPath) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {plainPath}");
        }
    }

    // ---- watching ----

    /// <summary>True while any program or Explorer window still appears to be showing the item.</summary>
    public bool IsInUse()
    {
        if (Kind == ItemKind.File)
        {
            if (Native.AnyProcessUsing(new[] { PlainPath })) return true;
            return Native.GetTopWindows().Any(w => !IsExplorer(w) && TitleMentions(w.Title, Name));
        }

        if (ExplorerWindows.AnyShowing(PlainPath)) return true;
        var files = SampleFiles();
        if (Native.AnyProcessUsing(files)) return true;
        var names = files.Select(Path.GetFileName).OfType<string>().ToList();
        return Native.GetTopWindows().Any(w => !IsExplorer(w) && names.Any(n => TitleMentions(w.Title, n)));
    }

    /// <summary>Asks the programs showing this item to close (used by "Lock now").</summary>
    public void CloseViewers()
    {
        if (Kind == ItemKind.Folder)
            ExplorerWindows.LeaveFolder(PlainPath, Path.GetDirectoryName(LockedPath)!);

        var names = Kind == ItemKind.File
            ? new List<string> { Name }
            : SampleFiles().Select(Path.GetFileName).OfType<string>().ToList();
        foreach (var w in Native.GetTopWindows())
            if (!IsExplorer(w) && names.Any(n => TitleMentions(w.Title, n)))
                Native.CloseWindow(w.Handle);
    }

    static bool IsExplorer(Native.TopWindow w) => w.ClassName is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW";

    static bool TitleMentions(string title, string fileName)
    {
        if (title.Contains(fileName, StringComparison.OrdinalIgnoreCase)) return true;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Length >= 4 && stem != fileName && title.Contains(stem, StringComparison.OrdinalIgnoreCase);
    }

    List<string> SampleFiles()
    {
        if (DateTime.UtcNow - sampledAt < TimeSpan.FromSeconds(5)) return sampleFiles;
        sampledAt = DateTime.UtcNow;
        try
        {
            sampleFiles = Directory.Exists(PlainPath)
                ? Directory.EnumerateFiles(PlainPath, "*", SearchOption.AllDirectories).Take(300).ToList()
                : new List<string>();
        }
        catch
        {
            sampleFiles = new List<string>();
        }
        return sampleFiles;
    }

    // ---- locking again ----

    /// <summary>Encrypts the unlocked copy back into the .locked file if anything changed. Returns true if it saved changes.</summary>
    public bool Reseal(string passcode)
    {
        string now = TakeSnapshot();
        if (now == snapshot || now == "missing") return false;
        Locker.Reseal(PlainPath, Kind, LockedPath, passcode);
        snapshot = now;
        return true;
    }

    public bool TryDeletePlain() => Locker.TryDeleteDirectory(sessionDir);

    string TakeSnapshot()
    {
        if (Kind == ItemKind.File)
        {
            var fi = new FileInfo(PlainPath);
            return fi.Exists ? $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : "missing";
        }
        if (!Directory.Exists(PlainPath)) return "missing";
        var sb = new StringBuilder();
        foreach (var e in new DirectoryInfo(PlainPath).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                     .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(e.FullName).Append('|');
            if (e is FileInfo f) sb.Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks);
            sb.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
        lockRequest.Dispose();
    }
}
