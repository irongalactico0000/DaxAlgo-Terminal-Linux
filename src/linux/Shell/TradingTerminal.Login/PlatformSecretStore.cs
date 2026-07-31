using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TradingTerminal.App.Login;

/// <summary>
/// Stores secrets in the current macOS user's login Keychain. The serialized value is only an
/// opaque Keychain reference. Non-macOS test and development hosts use AES-GCM with a per-user,
/// owner-readable key file; secret plaintext is never written to the credential JSON files.
/// </summary>
internal static class PlatformSecretStore
{
    private const string KeychainPrefix = "keychain-v1:";
    private const string FallbackPrefix = "aesgcm-v1:";
    private const string KeychainService = "com.daxalgo.terminal";
    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;
    private static readonly byte[] KeychainServiceBytes = Encoding.UTF8.GetBytes(KeychainService);
    private static readonly object Gate = new();

    private static readonly string FallbackKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal",
        ".credential-key");

    public static string? Protect(string purpose, string? value, string? existingReference = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            Delete(existingReference);
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            var account = NormalizePurpose(purpose);
            PutKeychain(account, value);
            return KeychainPrefix + account;
        }

        return FallbackPrefix + EncryptFallback(value);
    }

    public static string? Unprotect(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        try
        {
            if (reference.StartsWith(KeychainPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsMacOS()) return null;
                return GetKeychain(reference[KeychainPrefix.Length..]);
            }

            if (reference.StartsWith(FallbackPrefix, StringComparison.Ordinal))
                return DecryptFallback(reference[FallbackPrefix.Length..]);

            // Legacy Windows DPAPI blobs are intentionally not migrated or exposed on macOS.
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static void Delete(string? reference)
    {
        if (!OperatingSystem.IsMacOS() ||
            string.IsNullOrWhiteSpace(reference) ||
            !reference.StartsWith(KeychainPrefix, StringComparison.Ordinal))
            return;

        var account = reference[KeychainPrefix.Length..];
        lock (Gate)
        {
            var accountBytes = Encoding.UTF8.GetBytes(account);
            IntPtr passwordData = IntPtr.Zero;
            IntPtr item = IntPtr.Zero;
            try
            {
                var status = FindKeychainItem(accountBytes, out _, out passwordData, out item);
                if (status == ItemNotFound) return;
                if (status != Success) throw KeychainFailure(status);

                status = SecKeychainItemDelete(item);
                if (status != Success && status != ItemNotFound) throw KeychainFailure(status);
            }
            finally
            {
                FreeKeychainResult(passwordData, item);
                CryptographicOperations.ZeroMemory(accountBytes);
            }
        }
    }

    private static void PutKeychain(string account, string value)
    {
        lock (Gate)
        {
            var accountBytes = Encoding.UTF8.GetBytes(account);
            var passwordBytes = Encoding.UTF8.GetBytes(value);
            IntPtr passwordData = IntPtr.Zero;
            IntPtr item = IntPtr.Zero;
            try
            {
                var status = FindKeychainItem(accountBytes, out _, out passwordData, out item);
                if (status == Success)
                {
                    status = SecKeychainItemModifyAttributesAndData(
                        item,
                        IntPtr.Zero,
                        (uint)passwordBytes.Length,
                        passwordBytes);
                    if (status != Success) throw KeychainFailure(status);
                    return;
                }

                if (status != ItemNotFound) throw KeychainFailure(status);
                status = SecKeychainAddGenericPassword(
                    IntPtr.Zero,
                    (uint)KeychainServiceBytes.Length,
                    KeychainServiceBytes,
                    (uint)accountBytes.Length,
                    accountBytes,
                    (uint)passwordBytes.Length,
                    passwordBytes,
                    out var addedItem);
                try
                {
                    if (status == DuplicateItem)
                    {
                        status = UpdateExistingKeychainItem(accountBytes, passwordBytes);
                    }
                    if (status != Success) throw KeychainFailure(status);
                }
                finally
                {
                    if (addedItem != IntPtr.Zero) CFRelease(addedItem);
                }
            }
            finally
            {
                FreeKeychainResult(passwordData, item);
                CryptographicOperations.ZeroMemory(passwordBytes);
                CryptographicOperations.ZeroMemory(accountBytes);
            }
        }
    }

    private static string? GetKeychain(string account)
    {
        lock (Gate)
        {
            var accountBytes = Encoding.UTF8.GetBytes(account);
            IntPtr passwordData = IntPtr.Zero;
            IntPtr item = IntPtr.Zero;
            byte[]? passwordBytes = null;
            try
            {
                var status = FindKeychainItem(accountBytes, out var passwordLength, out passwordData, out item);
                if (status == ItemNotFound) return null;
                if (status != Success) throw KeychainFailure(status);
                if (passwordData == IntPtr.Zero) return string.Empty;

                passwordBytes = new byte[passwordLength];
                Marshal.Copy(passwordData, passwordBytes, 0, passwordBytes.Length);
                return Encoding.UTF8.GetString(passwordBytes);
            }
            finally
            {
                if (passwordBytes is not null) CryptographicOperations.ZeroMemory(passwordBytes);
                FreeKeychainResult(passwordData, item);
                CryptographicOperations.ZeroMemory(accountBytes);
            }
        }
    }

    private static int UpdateExistingKeychainItem(byte[] accountBytes, byte[] passwordBytes)
    {
        IntPtr passwordData = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = FindKeychainItem(accountBytes, out _, out passwordData, out item);
            return status == Success
                ? SecKeychainItemModifyAttributesAndData(
                    item,
                    IntPtr.Zero,
                    (uint)passwordBytes.Length,
                    passwordBytes)
                : status;
        }
        finally
        {
            FreeKeychainResult(passwordData, item);
        }
    }

    private static int FindKeychainItem(
        byte[] accountBytes,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr item) =>
        SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)KeychainServiceBytes.Length,
            KeychainServiceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            out passwordLength,
            out passwordData,
            out item);

    private static void FreeKeychainResult(IntPtr passwordData, IntPtr item)
    {
        if (passwordData != IntPtr.Zero)
            _ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
        if (item != IntPtr.Zero) CFRelease(item);
    }

    private static CryptographicException KeychainFailure(int status) =>
        new($"macOS Keychain could not access terminal credentials (status {status}).");

    private static string EncryptFallback(string value)
    {
        var key = LoadOrCreateFallbackKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);
        CryptographicOperations.ZeroMemory(plaintext);
        return Convert.ToBase64String(payload);
    }

    private static string DecryptFallback(string encoded)
    {
        var payload = Convert.FromBase64String(encoded);
        if (payload.Length < 29) throw new CryptographicException("Invalid encrypted secret payload.");

        var nonce = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(12, 16);
        var ciphertext = payload.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(LoadOrCreateFallbackKey(), tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] LoadOrCreateFallbackKey()
    {
        lock (Gate)
        {
            if (File.Exists(FallbackKeyPath))
            {
                var existing = File.ReadAllBytes(FallbackKeyPath);
                if (existing.Length == 32) return existing;
                throw new CryptographicException("Invalid local credential-protection key.");
            }

            var directory = Path.GetDirectoryName(FallbackKeyPath)!;
            Directory.CreateDirectory(directory);
            var key = RandomNumberGenerator.GetBytes(32);
            using (var stream = new FileStream(FallbackKeyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(key);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    FallbackKeyPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            else
            {
                File.SetAttributes(FallbackKeyPath, File.GetAttributes(FallbackKeyPath) | FileAttributes.Hidden);
            }

            return key;
        }
    }

    private static string NormalizePurpose(string purpose)
    {
        var builder = new StringBuilder(purpose.Length);
        foreach (var c in purpose.Trim().ToLowerInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-');
        return builder.ToString();
    }

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
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef,
        IntPtr attributeList,
        uint length,
        byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attributeList, IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr value);
}
