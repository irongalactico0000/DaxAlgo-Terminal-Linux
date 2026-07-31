using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaxAlgo.Daxq.Host;

public sealed class DaxqDeviceIdentity : IDisposable
{
    private readonly ECDsa _key;
    private readonly object _signGate = new();
    private int _disposed;

    internal DaxqDeviceIdentity(Guid deviceId, ECDsa key, bool nonExportable)
    {
        if (deviceId == Guid.Empty)
            throw new ArgumentException("A device id is required.", nameof(deviceId));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        var parameters = _key.ExportParameters(false);
        if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new CryptographicException("The DAXQ device key must use P-256.");
        var publicKey = _key.ExportSubjectPublicKeyInfo();
        DeviceId = deviceId;
        NonExportable = nonExportable;
        Registration = new DaxqDeviceRegistration(
            deviceId,
            DaxqCryptography.Base64Url(publicKey),
            DaxqCryptography.Sha256Hex(publicKey));
        CryptographicOperations.ZeroMemory(publicKey);
    }

    public Guid DeviceId { get; }

    public bool NonExportable { get; }

    public DaxqDeviceRegistration Registration { get; }

    public byte[] Sign(ReadOnlySpan<byte> canonicalProof)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_signGate)
        {
            return _key.SignData(
                canonicalProof,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }

    internal byte[] ExportPublicKey() => DaxqCryptography.DecodeBase64Url(Registration.PublicKeySpki);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _key.Dispose();
    }
}

public sealed class DaxqDeviceIdentityProvider : IDaxqDeviceIdentityProvider, IDisposable
{
    private static readonly byte[] DpapiEntropy =
        Encoding.UTF8.GetBytes("DaxAlgoTerminal.Daxq.DeviceKey.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
    };

    private readonly string _filePath;
    private readonly bool _forceSoftwareKey;
    private readonly object _gate = new();
    private DaxqDeviceIdentity? _identity;
    private int _disposed;

    public DaxqDeviceIdentityProvider(string filePath, bool forceSoftwareKey = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _forceSoftwareKey = forceSoftwareKey;
    }

    public static DaxqDeviceIdentityProvider CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgoTerminal");
        return new DaxqDeviceIdentityProvider(Path.Combine(directory, "daxq-device-v1.json"));
    }

    public ValueTask<DaxqDeviceIdentity> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            _identity ??= File.Exists(_filePath) ? Load() : Create();
            return ValueTask.FromResult(_identity);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_gate)
            {
                _identity?.Dispose();
                _identity = null;
            }
        }
    }

    private DaxqDeviceIdentity Load()
    {
        StoredDeviceIdentity stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredDeviceIdentity>(
                         File.ReadAllBytes(_filePath),
                         JsonOptions)
                     ?? throw new InvalidDataException("The DAXQ device identity file is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The DAXQ device identity file is invalid.", exception);
        }
        if (stored.SchemaVersion != 1 || stored.DeviceId == Guid.Empty ||
            stored.FingerprintSha256.Length != 64)
            throw new InvalidDataException("The DAXQ device identity file has an unsupported schema.");

        ECDsa key = stored.Mode switch
        {
            "cng" when OperatingSystem.IsWindows() => OpenCng(
                stored.KeyName ?? throw new InvalidDataException("The CNG key name is missing.")),
            "cng" => throw new PlatformNotSupportedException("CNG device identities require Windows."),
            "dpapi" or "keychain" => OpenProtected(stored.ProtectedPrivateKey ??
                                 throw new InvalidDataException("The protected device key is missing.")),
            _ => throw new InvalidDataException("The DAXQ device identity mode is unsupported."),
        };
        try
        {
            var identity = new DaxqDeviceIdentity(stored.DeviceId, key, stored.Mode == "cng");
            if (!string.Equals(
                    identity.Registration.FingerprintSha256,
                    stored.FingerprintSha256,
                    StringComparison.Ordinal))
            {
                identity.Dispose();
                throw new CryptographicException("The persisted DAXQ device key fingerprint changed.");
            }
            return identity;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private DaxqDeviceIdentity Create()
    {
        var deviceId = Guid.NewGuid();
        if (!_forceSoftwareKey && OperatingSystem.IsWindows())
        {
            try
            {
                return CreateCng(deviceId);
            }
            catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
            {
                // Some Windows editions/profiles do not provide a usable persisted CNG store.
                // The fallback is still device-bound at rest through DPAPI CurrentUser.
            }
        }
        return CreateProtectedSoftware(
            deviceId,
            OperatingSystem.IsMacOS() ? "keychain" : "dpapi");
    }

    [SupportedOSPlatform("windows")]
    private DaxqDeviceIdentity CreateCng(Guid deviceId)
    {
        var keyName = $"DaxAlgoTerminal.Daxq.Device.{deviceId:N}";
        var parameters = new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.None,
            KeyUsage = CngKeyUsages.Signing,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
        };
        using var cng = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, parameters);
        var key = new ECDsaCng(cng);
        var identity = new DaxqDeviceIdentity(deviceId, key, nonExportable: true);
        try
        {
            Save(new StoredDeviceIdentity(
                1,
                deviceId,
                "cng",
                keyName,
                null,
                identity.Registration.FingerprintSha256));
            return identity;
        }
        catch
        {
            identity.Dispose();
            try { CngKey.Open(keyName).Delete(); }
            catch (CryptographicException) { }
            throw;
        }
    }

    private DaxqDeviceIdentity CreateProtectedSoftware(Guid deviceId, string mode)
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = generated.ExportPkcs8PrivateKey();
        byte[]? protectedKey = null;
        ECDsa? runtimeKey = null;
        try
        {
            protectedKey = Protect(privateKey);
            runtimeKey = ECDsa.Create();
            runtimeKey.ImportPkcs8PrivateKey(privateKey, out var read);
            if (read != privateKey.Length)
                throw new CryptographicException("The software device key was not canonical PKCS#8.");
            var identity = new DaxqDeviceIdentity(deviceId, runtimeKey, nonExportable: false);
            runtimeKey = null;
            try
            {
                Save(new StoredDeviceIdentity(
                    1,
                    deviceId,
                    mode,
                    null,
                    Convert.ToBase64String(protectedKey),
                    identity.Registration.FingerprintSha256));
                return identity;
            }
            catch
            {
                identity.Dispose();
                throw;
            }
        }
        finally
        {
            runtimeKey?.Dispose();
            CryptographicOperations.ZeroMemory(privateKey);
            if (protectedKey is not null)
                CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    [SupportedOSPlatform("windows")]
    private static ECDsa OpenCng(string keyName)
    {
        var cng = CngKey.Open(
            keyName,
            CngProvider.MicrosoftSoftwareKeyStorageProvider,
            CngKeyOpenOptions.UserKey);
        return new ECDsaCng(cng);
    }

    private static ECDsa OpenProtected(string protectedPrivateKey)
    {
        byte[] ciphertext;
        try { ciphertext = Convert.FromBase64String(protectedPrivateKey); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The protected DAXQ device key is malformed.", exception);
        }
        byte[]? plaintext = null;
        try
        {
            plaintext = Unprotect(ciphertext);
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(plaintext, out var read);
            if (read != plaintext.Length)
            {
                key.Dispose();
                throw new CryptographicException("The protected DAXQ device key is not canonical PKCS#8.");
            }
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Save(StoredDeviceIdentity stored)
    {
        var directory = Path.GetDirectoryName(_filePath) ??
                        throw new InvalidOperationException("The DAXQ device identity path has no directory.");
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stored, JsonOptions);
        var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _filePath, overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
            }
        }
    }

    private static byte[] Protect(byte[] plaintext)
        => DaxqPlatformDataProtection.Protect(
            plaintext,
            DpapiEntropy,
            "device-key-v1");

    private static byte[] Unprotect(byte[] ciphertext)
        => DaxqPlatformDataProtection.Unprotect(
            ciphertext,
            DpapiEntropy,
            "device-key-v1");

    private sealed record StoredDeviceIdentity(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("device_id")] Guid DeviceId,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("key_name")] string? KeyName,
        [property: JsonPropertyName("protected_private_key")] string? ProtectedPrivateKey,
        [property: JsonPropertyName("fingerprint_sha256")] string FingerprintSha256);
}
