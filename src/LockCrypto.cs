using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileLocker;

enum ItemKind : byte { File = 0, Folder = 1 }

sealed class WrongPasscodeException : Exception
{
    public WrongPasscodeException() : base("Wrong passcode.") { }
}

sealed class CorruptLockedFileException : Exception
{
    public CorruptLockedFileException(string message) : base(message) { }
}

/// <summary>
/// Encrypted container format (".locked"):
///   header: "FLCK" | version(1) | kind(1) | pbkdf2 iterations(4) | salt(16) | nonce prefix(8) | passcode verifier(16)
///   chunks: plaintext length(4) | final flag(1) | ciphertext | GCM tag(16)
/// Each chunk is AES-256-GCM with nonce = prefix + chunk counter, and the header, counter and
/// final flag are authenticated, so chunks can't be reordered, dropped or truncated.
/// </summary>
static class LockCrypto
{
    public const int Iterations = 300_000;
    const byte Version = 1;
    const int SaltLen = 16, NoncePrefixLen = 8, VerifierLen = 16, TagLen = 16, KeyLen = 32, NonceLen = 12;
    const int ChunkSize = 1 << 20;
    const int HeaderLen = 4 + 1 + 1 + 4 + SaltLen + NoncePrefixLen + VerifierLen;
    static ReadOnlySpan<byte> Magic => "FLCK"u8;

    sealed record Header(byte[] Raw, ItemKind Kind, int Iterations, byte[] Salt, byte[] NoncePrefix, byte[] Verifier);

    public static byte[] HashPasscode(string passcode, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passcode, salt, iterations, HashAlgorithmName.SHA256, 32);

    static void DeriveKeys(string passcode, byte[] salt, int iterations, out byte[] key, out byte[] verifier)
    {
        byte[] password = Encoding.UTF8.GetBytes(passcode);
        byte[] material = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyLen * 2);
        key = material[..KeyLen];
        verifier = HMACSHA256.HashData(material.AsSpan(KeyLen), "FileLocker passcode check"u8)[..VerifierLen];
        CryptographicOperations.ZeroMemory(material);
        CryptographicOperations.ZeroMemory(password);
    }

    public static void Encrypt(Stream input, Stream output, string passcode, ItemKind kind)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLen);
        byte[] prefix = RandomNumberGenerator.GetBytes(NoncePrefixLen);
        DeriveKeys(passcode, salt, Iterations, out byte[] key, out byte[] verifier);

        byte[] header = new byte[HeaderLen];
        Magic.CopyTo(header);
        header[4] = Version;
        header[5] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(6), Iterations);
        salt.CopyTo(header, 10);
        prefix.CopyTo(header, 26);
        verifier.CopyTo(header, 34);
        output.Write(header);

        byte[] cur = new byte[ChunkSize], next = new byte[ChunkSize], cipher = new byte[ChunkSize];
        try
        {
            using var gcm = new AesGcm(key, TagLen);
            byte[] tag = new byte[TagLen], nonce = new byte[NonceLen], aad = new byte[HeaderLen + 5], chunkHead = new byte[5];
            header.CopyTo(aad, 0);

            int curLen = ReadFull(input, cur);
            for (uint counter = 0; ; counter++)
            {
                int nextLen = curLen == ChunkSize ? ReadFull(input, next) : 0;
                bool final = nextLen == 0;
                SetChunkParams(prefix, counter, final, nonce, aad);
                gcm.Encrypt(nonce, cur.AsSpan(0, curLen), cipher.AsSpan(0, curLen), tag, aad);

                BinaryPrimitives.WriteInt32LittleEndian(chunkHead, curLen);
                chunkHead[4] = final ? (byte)1 : (byte)0;
                output.Write(chunkHead);
                output.Write(cipher, 0, curLen);
                output.Write(tag);
                if (final) break;

                (cur, next) = (next, cur);
                curLen = nextLen;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(cur);
            CryptographicOperations.ZeroMemory(next);
        }
    }

    public static ItemKind Decrypt(Stream input, Stream output, string passcode)
    {
        Header h = ReadHeader(input);
        DeriveKeys(passcode, h.Salt, h.Iterations, out byte[] key, out byte[] verifier);
        byte[] plain = new byte[ChunkSize];
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(verifier, h.Verifier))
                throw new WrongPasscodeException();

            using var gcm = new AesGcm(key, TagLen);
            byte[] cipher = new byte[ChunkSize], tag = new byte[TagLen], nonce = new byte[NonceLen];
            byte[] aad = new byte[HeaderLen + 5], chunkHead = new byte[5];
            h.Raw.CopyTo(aad, 0);

            for (uint counter = 0; ; counter++)
            {
                if (ReadFull(input, chunkHead) != chunkHead.Length) throw Damaged();
                int len = BinaryPrimitives.ReadInt32LittleEndian(chunkHead);
                if (len < 0 || len > ChunkSize || chunkHead[4] > 1) throw Damaged();
                bool final = chunkHead[4] == 1;
                if (ReadFull(input, cipher.AsSpan(0, len)) != len || ReadFull(input, tag) != TagLen) throw Damaged();

                SetChunkParams(h.NoncePrefix, counter, final, nonce, aad);
                try { gcm.Decrypt(nonce, cipher.AsSpan(0, len), tag, plain.AsSpan(0, len), aad); }
                catch (CryptographicException) { throw Damaged(); }
                output.Write(plain, 0, len);

                if (final)
                {
                    if (input.ReadByte() != -1) throw Damaged();
                    break;
                }
            }
            return h.Kind;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static ItemKind ReadKind(string lockedPath)
    {
        using var fs = OpenForRead(lockedPath);
        return ReadHeader(fs).Kind;
    }

    public static bool CheckPasscode(string lockedPath, string passcode)
    {
        Header h;
        using (var fs = OpenForRead(lockedPath)) h = ReadHeader(fs);
        DeriveKeys(passcode, h.Salt, h.Iterations, out byte[] key, out byte[] verifier);
        CryptographicOperations.ZeroMemory(key);
        return CryptographicOperations.FixedTimeEquals(verifier, h.Verifier);
    }

    static FileStream OpenForRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static Header ReadHeader(Stream input)
    {
        byte[] raw = new byte[HeaderLen];
        if (ReadFull(input, raw) != HeaderLen || !raw.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CorruptLockedFileException("This isn't a File Locker file.");
        if (raw[4] != Version)
            throw new CorruptLockedFileException("This file was locked by a newer version of File Locker.");
        if (raw[5] > (byte)ItemKind.Folder)
            throw new CorruptLockedFileException("This locked file is damaged.");
        int iterations = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(6));
        if (iterations < 10_000 || iterations > 10_000_000)
            throw new CorruptLockedFileException("This locked file is damaged.");
        return new Header(raw, (ItemKind)raw[5], iterations, raw[10..26], raw[26..34], raw[34..50]);
    }

    static void SetChunkParams(byte[] prefix, uint counter, bool final, byte[] nonce, byte[] aad)
    {
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixLen), counter);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(HeaderLen), counter);
        aad[HeaderLen + 4] = final ? (byte)1 : (byte)0;
    }

    static CorruptLockedFileException Damaged() => new("This locked file is damaged or incomplete.");

    static int ReadFull(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
