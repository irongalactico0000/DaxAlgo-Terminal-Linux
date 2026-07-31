using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Accounts;

namespace TradingTerminal.Accounts;

internal interface IDevelopmentAccountSessionStore
{
    AccountSessionSnapshot? Load();

    bool Save(AccountSessionSnapshot session);

    bool Clear();
}

internal interface IAccountSessionProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

internal sealed class DevelopmentAccountSessionStore(
    string filePath,
    IAccountSessionProtector protector)
    : IDevelopmentAccountSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };
    private readonly object _gate = new();

    public static DevelopmentAccountSessionStore CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgoTerminal");
        return new DevelopmentAccountSessionStore(
            Path.Combine(directory, "account-session.dat"),
            OperatingSystem.IsMacOS()
                ? MacKeychainAccountSessionProtector.Instance
                : EphemeralAccountSessionProtector.Instance);
    }

    public AccountSessionSnapshot? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                var ciphertext = File.ReadAllBytes(filePath);
                byte[]? plaintext = null;
                try
                {
                    plaintext = protector.Unprotect(ciphertext);
                    var stored = JsonSerializer.Deserialize<StoredAccountSession>(
                        plaintext,
                        JsonOptions);
                    if (stored is null) return null;
                    return new AccountSessionSnapshot(
                        stored.SessionId,
                        new AccountIdentity(
                            stored.AccountId,
                            stored.DisplayName,
                            stored.EmailAddress),
                        stored.AuthenticatedAtUtc,
                        stored.ExpiresAtUtc);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(ciphertext);
                    if (plaintext is not null)
                        CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch
            {
                TryDelete();
                return null;
            }
        }
    }

    public bool Save(AccountSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            var stored = new StoredAccountSession(
                session.SessionId,
                session.Account.AccountId,
                session.Account.DisplayName,
                session.Account.EmailAddress,
                session.AuthenticatedAtUtc,
                session.ExpiresAtUtc);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(stored, JsonOptions);
            byte[]? ciphertext = null;
            string? temporaryPath = null;
            try
            {
                ciphertext = protector.Protect(plaintext);
                var directory = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(directory)) return false;
                Directory.CreateDirectory(directory);
                temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporaryPath, ciphertext);
                File.Move(temporaryPath, filePath, overwrite: true);
                temporaryPath = null;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext is not null)
                    CryptographicOperations.ZeroMemory(ciphertext);
                if (temporaryPath is not null)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            if (!File.Exists(filePath)) return true;
            return TryDelete();
        }
    }

    private bool TryDelete()
    {
        try
        {
            File.Delete(filePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record StoredAccountSession(
        string SessionId,
        string AccountId,
        string? DisplayName,
        string? EmailAddress,
        DateTimeOffset AuthenticatedAtUtc,
        DateTimeOffset? ExpiresAtUtc);
}

internal abstract class AesGcmAccountSessionProtector : IAccountSessionProtector
{
    private const byte FormatVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    protected abstract byte[] GetKey();

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var key = GetKey();
        try
        {
            ValidateKey(key);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            var protectedBytes = new byte[1 + NonceSize + TagSize + ciphertext.Length];
            protectedBytes[0] = FormatVersion;
            nonce.CopyTo(protectedBytes, 1);
            tag.CopyTo(protectedBytes, 1 + NonceSize);
            ciphertext.CopyTo(protectedBytes, 1 + NonceSize + TagSize);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            return protectedBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (ciphertext.Length < 1 + NonceSize + TagSize ||
            ciphertext[0] != FormatVersion)
        {
            throw new CryptographicException("The account-session payload is invalid.");
        }

        var key = GetKey();
        try
        {
            ValidateKey(key);
            var plaintext = new byte[ciphertext.Length - 1 - NonceSize - TagSize];
            var nonce = ciphertext.AsSpan(1, NonceSize);
            var tag = ciphertext.AsSpan(1 + NonceSize, TagSize);
            var encryptedPayload = ciphertext.AsSpan(1 + NonceSize + TagSize);
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, encryptedPayload, tag, plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != KeySize)
            throw new CryptographicException("The account-session encryption key is invalid.");
    }
}

/// <summary>
/// Uses a process-only key when this macOS project is exercised on a non-macOS development host.
/// Session persistence therefore fails closed across processes instead of writing plaintext.
/// </summary>
internal sealed class EphemeralAccountSessionProtector : AesGcmAccountSessionProtector
{
    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    public static EphemeralAccountSessionProtector Instance { get; } = new();

    protected override byte[] GetKey() => ProcessKey.ToArray();
}

/// <summary>Protects the local development session with a key held by the user's macOS Keychain.</summary>
internal sealed class MacKeychainAccountSessionProtector : AesGcmAccountSessionProtector
{
    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;
    private static readonly byte[] ServiceName =
        Encoding.UTF8.GetBytes("com.daxalgo.terminal.account-gate");
    private static readonly byte[] AccountName =
        Encoding.UTF8.GetBytes("session-encryption-key-v1");

    public static MacKeychainAccountSessionProtector Instance { get; } = new();

    protected override byte[] GetKey()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS Keychain is only available on macOS.");

        var existing = FindKey();
        if (existing is not null) return existing;

        var created = RandomNumberGenerator.GetBytes(32);
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecKeychainAddGenericPassword(
                IntPtr.Zero,
                (uint)ServiceName.Length,
                ServiceName,
                (uint)AccountName.Length,
                AccountName,
                (uint)created.Length,
                created,
                out item);
            if (status == Success) return created;

            if (status == DuplicateItem)
            {
                CryptographicOperations.ZeroMemory(created);
                return FindKey() ?? throw KeychainFailure(status);
            }

            throw KeychainFailure(status);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(created);
            throw;
        }
        finally
        {
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    private static byte[]? FindKey()
    {
        IntPtr passwordData = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)ServiceName.Length,
            ServiceName,
            (uint)AccountName.Length,
            AccountName,
            out var passwordLength,
            out passwordData,
            out item);
        try
        {
            if (status == ItemNotFound) return null;
            if (status != Success) throw KeychainFailure(status);
            if (passwordData == IntPtr.Zero || passwordLength != 32)
                throw new CryptographicException("The Keychain account-session key is invalid.");

            var key = new byte[passwordLength];
            Marshal.Copy(passwordData, key, 0, key.Length);
            return key;
        }
        finally
        {
            if (passwordData != IntPtr.Zero)
                _ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    private static CryptographicException KeychainFailure(int status) =>
        new($"macOS Keychain could not access the account-session key (status {status}).");

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(
        IntPtr attributeList,
        IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr value);
}
