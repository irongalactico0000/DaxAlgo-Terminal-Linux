using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DaxAlgo.Daxq.Host;

/// <summary>
/// Preserves the Windows DPAPI wire format on Windows and uses an AES-256 key held by the current
/// user's macOS Keychain on macOS. No fallback writes unprotected DAXQ licensing material.
/// </summary>
internal static class DaxqPlatformDataProtection
{
    private const byte FormatVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;
    private static readonly byte[] KeychainService =
        Encoding.UTF8.GetBytes("com.daxalgo.terminal.daxq");

    public static byte[] Protect(
        byte[] plaintext,
        byte[] windowsEntropy,
        string keychainAccount)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(plaintext, windowsEntropy, DataProtectionScope.CurrentUser);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Persistent DAXQ protection requires DPAPI or macOS Keychain.");

        var key = GetOrCreateMacKey(keychainAccount);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            var result = new byte[1 + NonceSize + TagSize + ciphertext.Length];
            result[0] = FormatVersion;
            nonce.CopyTo(result, 1);
            tag.CopyTo(result, 1 + NonceSize);
            ciphertext.CopyTo(result, 1 + NonceSize + TagSize);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] Unprotect(
        byte[] ciphertext,
        byte[] windowsEntropy,
        string keychainAccount)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (OperatingSystem.IsWindows())
            return ProtectedData.Unprotect(ciphertext, windowsEntropy, DataProtectionScope.CurrentUser);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Persistent DAXQ protection requires DPAPI or macOS Keychain.");
        if (ciphertext.Length < 1 + NonceSize + TagSize || ciphertext[0] != FormatVersion)
            throw new CryptographicException("The protected DAXQ payload is invalid.");

        var key = GetOrCreateMacKey(keychainAccount, createWhenMissing: false);
        try
        {
            var plaintext = new byte[ciphertext.Length - 1 - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                ciphertext.AsSpan(1, NonceSize),
                ciphertext.AsSpan(1 + NonceSize + TagSize),
                ciphertext.AsSpan(1 + NonceSize, TagSize),
                plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] GetOrCreateMacKey(
        string keychainAccount,
        bool createWhenMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keychainAccount);
        var account = Encoding.UTF8.GetBytes(keychainAccount);
        try
        {
            var existing = FindMacKey(account);
            if (existing is not null) return existing;
            if (!createWhenMissing)
                throw new CryptographicException("The macOS Keychain DAXQ key is missing.");

            var created = RandomNumberGenerator.GetBytes(KeySize);
            IntPtr item = IntPtr.Zero;
            try
            {
                var status = SecKeychainAddGenericPassword(
                    IntPtr.Zero,
                    (uint)KeychainService.Length,
                    KeychainService,
                    (uint)account.Length,
                    account,
                    (uint)created.Length,
                    created,
                    out item);
                if (status == Success) return created;
                if (status == DuplicateItem)
                {
                    CryptographicOperations.ZeroMemory(created);
                    return FindMacKey(account) ?? throw KeychainFailure(status);
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
        finally
        {
            CryptographicOperations.ZeroMemory(account);
        }
    }

    private static byte[]? FindMacKey(byte[] account)
    {
        IntPtr passwordData = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)KeychainService.Length,
            KeychainService,
            (uint)account.Length,
            account,
            out var passwordLength,
            out passwordData,
            out item);
        try
        {
            if (status == ItemNotFound) return null;
            if (status != Success) throw KeychainFailure(status);
            if (passwordData == IntPtr.Zero || passwordLength != KeySize)
                throw new CryptographicException("The macOS Keychain DAXQ key is invalid.");
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
        new($"macOS Keychain could not access DAXQ protected state (status {status}).");

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
    private static extern int SecKeychainItemFreeContent(IntPtr attributeList, IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr value);
}
