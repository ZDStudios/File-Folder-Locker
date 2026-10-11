using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileLocker;

sealed class AppSettings
{
    public string? PasscodeSalt { get; set; }
    public string? PasscodeHash { get; set; }
    public int PasscodeIterations { get; set; } = LockCrypto.Iterations;
    public List<string> Items { get; set; } = new();

    static string FilePath => Path.Combine(Locker.AppDataDir, "settings.json");

    [JsonIgnore]
    public bool HasPasscode => PasscodeSalt != null && PasscodeHash != null;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Locker.AppDataDir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void SetPasscode(string passcode)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        PasscodeIterations = LockCrypto.Iterations;
        PasscodeSalt = Convert.ToBase64String(salt);
        PasscodeHash = Convert.ToBase64String(LockCrypto.HashPasscode(passcode, salt, PasscodeIterations));
    }

    public bool VerifyPasscode(string passcode)
    {
        if (!HasPasscode) return false;
        byte[] actual = LockCrypto.HashPasscode(passcode, Convert.FromBase64String(PasscodeSalt!), PasscodeIterations);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(PasscodeHash!));
    }

    public bool AddItem(string lockedPath)
    {
        if (Items.Any(i => i.Equals(lockedPath, StringComparison.OrdinalIgnoreCase))) return false;
        Items.Add(lockedPath);
        return true;
    }

    public void RemoveItem(string lockedPath) =>
        Items.RemoveAll(i => i.Equals(lockedPath, StringComparison.OrdinalIgnoreCase));
}
