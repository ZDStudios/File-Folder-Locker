using System.Diagnostics;
using System.IO.Compression;

namespace FileLocker;

/// <summary>The item was locked successfully, but some of the original couldn't be removed.</summary>
sealed class LockedWithLeftoversException(string lockedPath, string message) : Exception(message)
{
    public string LockedPath { get; } = lockedPath;
}

/// <summary>File-system operations: turning items into ".locked" files and back.</summary>
static class Locker
{
    public const string Extension = ".locked";

    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileLocker");

    /// <summary>
    /// Where unlocked copies live while they're being viewed (outside OneDrive, never synced).
    /// This is under %TEMP% because Store apps (e.g. the new Notepad and Photos) can't see
    /// folders other programs create directly in AppData\Local.
    /// </summary>
    public static string OpenRoot => Path.Combine(Path.GetTempPath(), "FileLocker", "Open");
    static string ScratchRoot => Path.Combine(AppDataDir, "Scratch");

    public static bool IsLockedPath(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static string OriginalNameOf(string lockedPath)
    {
        string name = Path.GetFileName(lockedPath);
        return IsLockedPath(name) && name.Length > Extension.Length ? name[..^Extension.Length] : name;
    }

    public static string Lock(string path, string passcode)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A whole drive can't be locked.");
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(path);

        if (IsLockedPath(path))
            throw new InvalidOperationException($"\"{name}\" is already locked.");
        if (path.StartsWith(AppDataDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That item belongs to File Locker itself.");

        string target = path + Extension;
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"\"{Path.GetFileName(target)}\" already exists in that folder.");

        if (Directory.Exists(path)) LockFolder(path, target, passcode);
        else if (File.Exists(path)) LockFile(path, target, passcode);
        else throw new FileNotFoundException($"\"{name}\" no longer exists.", path);
        return target;
    }

    static void LockFile(string path, string target, string passcode)
    {
        string partial = NewScratchFile(".partial");
        try
        {
            FileStream input;
            try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (IOException ex) { throw new IOException($"\"{Path.GetFileName(path)}\" is open in another program. Close it first.", ex); }
            using (input)
                EncryptStream(input, partial, passcode, ItemKind.File);
            File.Move(partial, target);
        }
        catch
        {
            TryDeleteFile(partial);
            throw;
        }
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LockedWithLeftoversException(target,
                $"\"{Path.GetFileName(path)}\" was locked, but Windows wouldn't let File Locker delete the original. " +
                $"Its contents are safely inside \"{Path.GetFileName(target)}\", so you can delete the original yourself.");
        }
    }

    static void LockFolder(string dir, string target, string passcode)
    {
        // Everything is built in File Locker's private scratch folder, so nothing extra ever appears next to
        // the item; the finished .locked file is moved into place in one step at the end.
        string zip = NewScratchFile(".zip");
        string partial = NewScratchFile(".partial");
        try
        {
            ZipFolder(dir, zip, refuseFilesInUse: true);
            EncryptFile(zip, partial, passcode, ItemKind.Folder);
            File.Move(partial, target);
        }
        catch
        {
            TryDeleteFile(partial);
            throw;
        }
        finally
        {
            TryDeleteFile(zip);
        }

        // The folder is safely locked at this point; failing to tidy up the original shouldn't count as a failed lock.
        if (!TryDeleteDirectory(dir))
            throw new LockedWithLeftoversException(target,
                $"\"{Path.GetFileName(dir)}\" was locked, but Windows wouldn't let File Locker delete all of the original folder. " +
                $"Everything is safely inside \"{Path.GetFileName(target)}\", so you can delete what's left of \"{Path.GetFileName(dir)}\" yourself.");
    }

    /// <summary>
    /// Zips a folder's contents (including empty subfolders). With <paramref name="refuseFilesInUse"/>, each file is
    /// opened so that it fails if another program is writing to it, and nothing is touched in that case.
    /// </summary>
    static void ZipFolder(string dir, string zipPath, bool refuseFilesInUse)
    {
        using var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(dir, entry).Replace('\\', '/');
            if (Directory.Exists(entry))
            {
                if (!Directory.EnumerateFileSystemEntries(entry).Any())
                    archive.CreateEntry(relative + "/");
                continue;
            }

            FileStream source;
            try
            {
                source = new FileStream(entry, FileMode.Open, FileAccess.Read,
                    refuseFilesInUse ? FileShare.Read : FileShare.ReadWrite, 1 << 16);
            }
            catch (IOException ex) when (refuseFilesInUse)
            {
                throw new IOException($"\"{Path.GetFileName(entry)}\" (inside \"{Path.GetFileName(dir)}\") is open in another program. Close it first.", ex);
            }
            using (source)
            {
                var zipEntry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                try { zipEntry.LastWriteTime = File.GetLastWriteTime(entry); } catch { }
                using var target = zipEntry.Open();
                source.CopyTo(target);
            }
        }
    }

    /// <summary>Decrypts a locked item to <paramref name="dest"/> (a file path, or a folder path for folders).</summary>
    public static ItemKind ExtractTo(string lockedPath, string dest, string passcode)
    {
        ItemKind kind = LockCrypto.ReadKind(lockedPath);
        if (kind == ItemKind.File)
        {
            string partial = NewScratchFile(".partial");
            try
            {
                DecryptFile(lockedPath, partial, passcode);
                File.Move(partial, dest);
            }
            catch
            {
                TryDeleteFile(partial);
                throw;
            }
        }
        else
        {
            string zip = NewScratchFile(".zip");
            try
            {
                DecryptFile(lockedPath, zip, passcode);
                ZipFile.ExtractToDirectory(zip, dest);
            }
            finally
            {
                TryDeleteFile(zip);
            }
        }
        return kind;
    }

    /// <summary>Encrypts the (possibly edited) unlocked copy back over the locked file.</summary>
    public static void Reseal(string plainPath, ItemKind kind, string lockedPath, string passcode)
    {
        string partial = NewScratchFile(".partial");
        string? zip = null;
        try
        {
            if (kind == ItemKind.File)
            {
                using var input = new FileStream(plainPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                EncryptStream(input, partial, passcode, kind);
            }
            else
            {
                zip = NewScratchFile(".zip");
                ZipFolder(plainPath, zip, refuseFilesInUse: false);
                EncryptFile(zip, partial, passcode, kind);
            }
            File.Move(partial, lockedPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(partial);
            throw;
        }
        finally
        {
            if (zip != null) TryDeleteFile(zip);
        }
    }

    /// <summary>Permanently unlocks an item, restoring it next to the locked file.</summary>
    public static string RemoveLock(string lockedPath, string passcode)
    {
        string dest = Path.Combine(Path.GetDirectoryName(lockedPath)!, OriginalNameOf(lockedPath));
        if (dest.Equals(lockedPath, StringComparison.OrdinalIgnoreCase) || File.Exists(dest) || Directory.Exists(dest))
            throw new IOException($"\"{Path.GetFileName(dest)}\" already exists in that folder.");
        if (!LockCrypto.CheckPasscode(lockedPath, passcode)) throw new WrongPasscodeException();
        ExtractTo(lockedPath, dest, passcode);
        File.Delete(lockedPath);
        return dest;
    }

    /// <summary>Re-encrypts a locked item under a new passcode.</summary>
    public static void ChangePasscode(string lockedPath, string oldPasscode, string newPasscode)
    {
        string tmp = NewScratchFile(".bin");
        string partial = NewScratchFile(".partial");
        try
        {
            ItemKind kind = DecryptFile(lockedPath, tmp, oldPasscode);
            EncryptFile(tmp, partial, newPasscode, kind);
            File.Move(partial, lockedPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tmp);
            TryDeleteFile(partial);
        }
    }

    static void EncryptFile(string source, string dest, string passcode, ItemKind kind)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        EncryptStream(input, dest, passcode, kind);
    }

    static void EncryptStream(Stream input, string dest, string passcode, ItemKind kind)
    {
        using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        LockCrypto.Encrypt(input, output, passcode, kind);
    }

    static ItemKind DecryptFile(string source, string dest, string passcode)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        return LockCrypto.Decrypt(input, output, passcode);
    }

    // ---- scratch space & cleanup ----

    static string NewScratchFile(string extension)
    {
        string dir = Path.Combine(ScratchRoot, Environment.ProcessId.ToString());
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
    }

    /// <summary>Removes scratch folders left behind by File Locker processes that are no longer running.</summary>
    public static void CleanupScratch()
    {
        if (!Directory.Exists(ScratchRoot)) return;
        foreach (string dir in Directory.GetDirectories(ScratchRoot))
        {
            if (int.TryParse(Path.GetFileName(dir), out int pid) && IsRunning(pid)) continue;
            TryDeleteDirectory(dir);
        }
    }

    static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch { }
    }

    public static bool TryDeleteDirectory(string dir)
    {
        try
        {
            DeleteDirectory(dir);
            return true;
        }
        catch
        {
            return !Directory.Exists(dir);
        }
    }

    static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // OneDrive often marks folders read-only, and Windows won't delete read-only folders,
                // so clear the flag on folders as well as files.
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
                    ClearReadOnly(entry);
                ClearReadOnly(dir);
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }

    static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }
}
