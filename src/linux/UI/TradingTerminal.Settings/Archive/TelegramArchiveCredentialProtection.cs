using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Protects Telegram archive credentials with the current user's platform secret store. Windows
/// builds retain the existing DPAPI wire format; macOS stores a random AES-256 key in the login
/// Keychain and persists only AES-GCM authenticated ciphertext. Unsupported platforms fail closed.
/// </summary>
public static class TelegramArchiveCredentialProtection
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

    private static readonly byte[] FormatMagic = "DAXTG"u8.ToArray();
    private static readonly byte[] AssociatedData =
        "DaxAlgo.Terminal.TelegramArchive.v1"u8.ToArray();
    private static readonly byte[] KeychainService =
        Encoding.UTF8.GetBytes("com.daxalgo.terminal.telegram-archive");
    private static readonly byte[] KeychainAccount =
        Encoding.UTF8.GetBytes("credentials-encryption-key-v1");

    public static string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
#endif

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Persistent Telegram credential protection requires DPAPI or the macOS Keychain.");
        }

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var key = GetOrCreateMacKey();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSize];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, AssociatedData);

                var headerSize = FormatMagic.Length + 1 + NonceSize + TagSize;
                var protectedBytes = new byte[headerSize + ciphertext.Length];
                FormatMagic.CopyTo(protectedBytes, 0);
                protectedBytes[FormatMagic.Length] = FormatVersion;
                nonce.CopyTo(protectedBytes, FormatMagic.Length + 1);
                tag.CopyTo(protectedBytes, FormatMagic.Length + 1 + NonceSize);
                ciphertext.CopyTo(protectedBytes, headerSize);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(tag);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static string? Decrypt(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return null;

        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = Convert.FromBase64String(cipherBase64);

#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                var plain = ProtectedData.Unprotect(
                    protectedBytes,
                    optionalEntropy: null,
                    DataProtectionScope.CurrentUser);
                try
                {
                    return Encoding.UTF8.GetString(plain);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
#endif

            if (!OperatingSystem.IsMacOS()) return null;
            return DecryptMac(protectedBytes);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static string DecryptMac(byte[] protectedBytes)
    {
        var headerSize = FormatMagic.Length + 1 + NonceSize + TagSize;
        if (protectedBytes.Length < headerSize ||
            !protectedBytes.AsSpan(0, FormatMagic.Length).SequenceEqual(FormatMagic) ||
            protectedBytes[FormatMagic.Length] != FormatVersion)
        {
            throw new CryptographicException("The protected Telegram credential payload is invalid.");
        }

        var key = GetOrCreateMacKey(createWhenMissing: false);
        try
        {
            var plaintext = new byte[protectedBytes.Length - headerSize];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(
                    protectedBytes.AsSpan(FormatMagic.Length + 1, NonceSize),
                    protectedBytes.AsSpan(headerSize),
                    protectedBytes.AsSpan(FormatMagic.Length + 1 + NonceSize, TagSize),
                    plaintext,
                    AssociatedData);
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] GetOrCreateMacKey(bool createWhenMissing = true)
    {
        var existing = FindMacKey();
        if (existing is not null) return existing;
        if (!createWhenMissing)
            throw new CryptographicException("The macOS Keychain Telegram credential key is missing.");

        var created = RandomNumberGenerator.GetBytes(KeySize);
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecKeychainAddGenericPassword(
                IntPtr.Zero,
                (uint)KeychainService.Length,
                KeychainService,
                (uint)KeychainAccount.Length,
                KeychainAccount,
                (uint)created.Length,
                created,
                out item);
            if (status == Success) return created;
            if (status == DuplicateItem)
            {
                CryptographicOperations.ZeroMemory(created);
                return FindMacKey() ?? throw KeychainFailure(status);
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

    private static byte[]? FindMacKey()
    {
        IntPtr passwordData = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)KeychainService.Length,
            KeychainService,
            (uint)KeychainAccount.Length,
            KeychainAccount,
            out var passwordLength,
            out passwordData,
            out item);
        try
        {
            if (status == ItemNotFound) return null;
            if (status != Success) throw KeychainFailure(status);
            if (passwordData == IntPtr.Zero || passwordLength != KeySize)
                throw new CryptographicException("The macOS Keychain Telegram credential key is invalid.");

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
        new($"macOS Keychain could not access the Telegram credential key (status {status}).");

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
