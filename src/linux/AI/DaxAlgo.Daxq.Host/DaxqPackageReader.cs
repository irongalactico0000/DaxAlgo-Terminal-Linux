using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaxAlgo.Daxq.Contracts;

namespace DaxAlgo.Daxq.Host;

internal sealed record LoadedDaxqPackage(
    DaxqManifest Manifest,
    byte[] Ciphertext,
    string ReleaseSigningKeyId);

/// <summary>Strict official-installer reader for the frozen DAXQ v1 development package.</summary>
internal static class DaxqPackageReader
{
    internal const string DevelopmentReleaseKeyId = "daxq-local-dev-p256-v1";

    private const int MaximumPackageBytes = 10 * 1024 * 1024;
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumIndexBytes = 16 * 1024;
    private const int MaximumSignatureBytes = 4 * 1024;
    private const int MaximumCiphertextBytes = 8 * 1024 * 1024;
    private const int MaximumEntryNameLength = 64;
    private const long MaximumCompressionRatio = 100;

    private static readonly string[] ExpectedEntries =
    [
        DaxqFormat.ManifestEntryName,
        DaxqFormat.CiphertextEntryName,
        DaxqFormat.PackageIndexEntryName,
        DaxqFormat.SignatureEntryName,
    ];

    public static LoadedDaxqPackage Read(string daxqPath, DaxqEs256PublicKeyRing releaseTrust)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(daxqPath);
        ArgumentNullException.ThrowIfNull(releaseTrust);
        if (!string.Equals(Path.GetExtension(daxqPath), DaxqFormat.PackageExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Protected strategies must use the .daxq extension.");
        }

        var fullPath = Path.GetFullPath(daxqPath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
            throw new FileNotFoundException("The protected strategy package was not found.", fullPath);
        if (file.Length is <= 0 or > MaximumPackageBytes)
            throw new InvalidDataException($"The DAXQ package exceeds the {MaximumPackageBytes}-byte limit.");

        var entries = ReadExactEntries(fullPath);
        var manifestBytes = entries[DaxqFormat.ManifestEntryName];
        var ciphertext = entries[DaxqFormat.CiphertextEntryName];
        var packageIndexBytes = entries[DaxqFormat.PackageIndexEntryName];
        var signatureBytes = entries[DaxqFormat.SignatureEntryName];

        var manifest = ReadAndValidateManifest(manifestBytes);
        ValidateIntegrityIndex(packageIndexBytes, manifestBytes, ciphertext, manifest);
        var releaseSigningKeyId = VerifySignature(
            signatureBytes,
            manifestBytes,
            packageIndexBytes,
            releaseTrust);
        return new LoadedDaxqPackage(manifest, ciphertext, releaseSigningKeyId);
    }

    private static Dictionary<string, byte[]> ReadExactEntries(string fullPath)
    {
        using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        if (archive.Entries.Count != ExpectedEntries.Length)
            throw new InvalidDataException("A DAXQ v1 package must contain exactly four root entries.");

        var result = new Dictionary<string, byte[]>(ExpectedEntries.Length, StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Length is 0 or > MaximumEntryNameLength ||
                name.Contains('/') || name.Contains('\\') ||
                !string.Equals(name, entry.Name, StringComparison.Ordinal) ||
                !ExpectedEntries.Contains(name, StringComparer.Ordinal) ||
                IsLink(entry))
            {
                throw new InvalidDataException($"Unexpected DAXQ ZIP entry '{name}'.");
            }

            var limit = name switch
            {
                DaxqFormat.ManifestEntryName => MaximumManifestBytes,
                DaxqFormat.PackageIndexEntryName => MaximumIndexBytes,
                DaxqFormat.SignatureEntryName => MaximumSignatureBytes,
                DaxqFormat.CiphertextEntryName => MaximumCiphertextBytes,
                _ => 0,
            };
            if (entry.Length is <= 0 or > int.MaxValue || entry.Length > limit)
                throw new InvalidDataException($"DAXQ entry '{name}' has an invalid size.");
            if (entry.CompressedLength < 0 ||
                (entry.Length > 4096 &&
                 (entry.CompressedLength == 0 || entry.Length / entry.CompressedLength > MaximumCompressionRatio)))
            {
                throw new InvalidDataException($"DAXQ entry '{name}' exceeds the compression-ratio limit.");
            }
            if (!result.TryAdd(name, ReadEntry(entry, checked((int)entry.Length))))
                throw new InvalidDataException($"Duplicate DAXQ ZIP entry '{name}'.");
        }

        if (result.Count != ExpectedEntries.Length || ExpectedEntries.Any(name => !result.ContainsKey(name)))
            throw new InvalidDataException("A DAXQ v1 package omitted a required root entry.");
        return result;
    }

    private static bool IsLink(ZipArchiveEntry entry)
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixFileType == 0xA000 ||
               (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int length)
    {
        var bytes = new byte[length];
        using var input = entry.Open();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = input.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new InvalidDataException($"DAXQ entry '{entry.FullName}' ended early.");
            offset += read;
        }
        if (input.ReadByte() != -1)
            throw new InvalidDataException($"DAXQ entry '{entry.FullName}' exceeded its declared size.");
        return bytes;
    }

    private static DaxqManifest ReadAndValidateManifest(byte[] manifestBytes)
    {
        using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        RequireExactProperties(root,
        [
            "formatVersion", "kind", "strategyId", "version", "sdkAbiVersion",
            "executionClass", "dataRequirements", "params", "protection", "watermark",
            "vmMin", "files",
        ]);
        RequireExactProperties(root.GetProperty("protection"),
            ["alg", "contentKeyId", "nonce", "cipherSha256"]);
        RequireExactProperties(root.GetProperty("watermark"), ["scheme", "slot"]);
        RequireExactProperties(root.GetProperty("files"),
            [DaxqFormat.ManifestEntryName, DaxqFormat.CiphertextEntryName]);

        foreach (var parameter in root.GetProperty("params").EnumerateArray())
            RequireExactProperties(parameter, ["id", "type", "default"], ["min", "max"]);

        var manifest = JsonSerializer.Deserialize<DaxqManifest>(manifestBytes)
            ?? throw new InvalidDataException("manifest.json was empty.");
        ValidateManifestSemantics(manifest);
        return manifest;
    }

    private static void ValidateManifestSemantics(DaxqManifest manifest)
    {
        if (manifest.FormatVersion != DaxqFormat.FormatVersion ||
            manifest.Kind != DaxqFormat.Kind ||
            manifest.SdkAbiVersion != DaxqFormat.SdkAbiVersion ||
            manifest.VmMin != DaxqFormat.VmAbiVersion ||
            manifest.ExecutionClass != ExecutionClass.SealedBytecode)
        {
            throw new InvalidDataException("manifest.json does not describe a supported sealed DAXQ v1 program.");
        }
        if (!ValidStrategyId(manifest.StrategyId) || !ValidVersion(manifest.Version))
            throw new InvalidDataException("manifest.json contains an invalid strategy identity.");
        if (manifest.DataRequirements is not { Length: >= 1 and <= 2 } ||
            manifest.DataRequirements.Any(value => value is not ("bars" or "ticks")) ||
            manifest.DataRequirements.Distinct(StringComparer.Ordinal).Count() != manifest.DataRequirements.Length ||
            !manifest.DataRequirements.SequenceEqual(
                manifest.DataRequirements.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException("manifest.json has invalid or non-canonical dataRequirements.");
        }
        if (manifest.Parameters is null || manifest.Parameters.Length > 256)
            throw new InvalidDataException("manifest.json exceeds the DAXQ parameter limit.");
        var previousId = string.Empty;
        foreach (var parameter in manifest.Parameters)
        {
            ValidateParameter(parameter);
            if (string.CompareOrdinal(previousId, parameter.Id) >= 0)
                throw new InvalidDataException("manifest.json parameters must be unique and ordinal-sorted.");
            previousId = parameter.Id;
        }

        if (manifest.Protection is null ||
            manifest.Protection.Algorithm != DaxqFormat.CipherAlgorithm ||
            !ValidKeyId(manifest.Protection.ContentKeyId) ||
            !IsLowerHexSha256(manifest.Protection.CipherSha256) ||
            DecodeBase64Url(manifest.Protection.Nonce, DaxqFormat.NonceSizeBytes).Length != DaxqFormat.NonceSizeBytes)
        {
            throw new InvalidDataException("manifest.json has unsupported protection metadata.");
        }
        if (manifest.Watermark is null || manifest.Watermark.Scheme != "per-buyer-v1" ||
            manifest.Watermark.Slot != "wm")
        {
            throw new InvalidDataException("manifest.json has unsupported watermark metadata.");
        }
        if (manifest.Files is null || manifest.Files.Count != 2 ||
            !manifest.Files.TryGetValue(DaxqFormat.ManifestEntryName, out var self) || self != "self" ||
            !manifest.Files.TryGetValue(DaxqFormat.CiphertextEntryName, out var cipherHash) ||
            cipherHash != manifest.Protection.CipherSha256)
        {
            throw new InvalidDataException("manifest.json has an invalid files index.");
        }
    }

    private static void ValidateParameter(DaxqParameterManifest parameter)
    {
        if (parameter is null || !ValidParameterId(parameter.Id))
            throw new InvalidDataException("manifest.json contains an invalid parameter id.");
        switch (parameter.Type)
        {
            case "int":
            {
                var value = ReadInteger(parameter.Default, parameter.Id);
                var min = parameter.Min is { } minimum ? ReadInteger(minimum, parameter.Id) : (long?)null;
                var max = parameter.Max is { } maximum ? ReadInteger(maximum, parameter.Id) : (long?)null;
                if (min > max || value < min || value > max)
                    throw new InvalidDataException($"Parameter '{parameter.Id}' has invalid integer bounds.");
                break;
            }
            case "float":
            {
                var value = ReadFiniteDouble(parameter.Default, parameter.Id);
                var min = parameter.Min is { } minimum ? ReadFiniteDouble(minimum, parameter.Id) : (double?)null;
                var max = parameter.Max is { } maximum ? ReadFiniteDouble(maximum, parameter.Id) : (double?)null;
                if (min > max || value < min || value > max)
                    throw new InvalidDataException($"Parameter '{parameter.Id}' has invalid numeric bounds.");
                break;
            }
            case "bool" when parameter.Min is null && parameter.Max is null &&
                                  parameter.Default.ValueKind is JsonValueKind.True or JsonValueKind.False:
                break;
            default:
                throw new InvalidDataException($"Parameter '{parameter.Id}' uses an unsupported DAXQ type.");
        }
    }

    private static long ReadInteger(JsonElement element, string id)
    {
        const long maxExact = 9_007_199_254_740_991L;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value) ||
            value is < -maxExact or > maxExact)
            throw new InvalidDataException($"Parameter '{id}' is not an exact DAXQ integer.");
        return value;
    }

    private static double ReadFiniteDouble(JsonElement element, string id)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) ||
            !double.IsFinite(value))
            throw new InvalidDataException($"Parameter '{id}' is not a finite DAXQ number.");
        return value;
    }

    private static void ValidateIntegrityIndex(
        byte[] indexBytes,
        byte[] manifestBytes,
        byte[] ciphertext,
        DaxqManifest manifest)
    {
        using var document = JsonDocument.Parse(indexBytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        RequireExactProperties(root, ["formatVersion", "files"]);
        if (root.GetProperty("formatVersion").GetInt32() != DaxqFormat.FormatVersion)
            throw new InvalidDataException("package.json uses an unsupported format version.");
        var files = root.GetProperty("files");
        RequireExactProperties(files, [DaxqFormat.ManifestEntryName, DaxqFormat.CiphertextEntryName]);
        AssertSha256(manifestBytes, files.GetProperty(DaxqFormat.ManifestEntryName).GetString());
        AssertSha256(ciphertext, files.GetProperty(DaxqFormat.CiphertextEntryName).GetString());
        AssertSha256(ciphertext, manifest.Protection.CipherSha256);
    }

    private static string VerifySignature(
        byte[] signatureEnvelopeBytes,
        byte[] manifestBytes,
        byte[] packageIndexBytes,
        DaxqEs256PublicKeyRing releaseTrust)
    {
        using var document = JsonDocument.Parse(signatureEnvelopeBytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        RequireExactProperties(root, ["alg", "keyId", "sig"]);
        var keyId = root.GetProperty("keyId").GetString();
        if (root.GetProperty("alg").GetString() != DaxqFormat.SignatureAlgorithm ||
            !ValidKeyId(keyId))
        {
            throw new CryptographicException("The DAXQ package release-signing metadata is invalid.");
        }

        var signature = DecodeBase64Url(
            root.GetProperty("sig").GetString(), DaxqFormat.SignatureSizeBytes);
        var input = BuildSignatureInput(keyId!, manifestBytes, packageIndexBytes);
        if (!releaseTrust.Verify(keyId!, input, signature))
        {
            throw new CryptographicException("The DAXQ release signature is invalid or untrusted.");
        }
        return keyId!;
    }

    private static byte[] BuildSignatureInput(string keyId, byte[] manifest, byte[] index)
    {
        var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
        using var stream = new MemoryStream(
            12 + keyIdBytes.Length + manifest.Length + index.Length);
        stream.Write("DAXQ-SIG-V1"u8);
        stream.WriteByte(0);
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(integer[..2], checked((ushort)keyIdBytes.Length));
        stream.Write(integer[..2]);
        stream.Write(keyIdBytes);
        BinaryPrimitives.WriteUInt32BigEndian(integer, checked((uint)manifest.Length));
        stream.Write(integer);
        stream.Write(manifest);
        BinaryPrimitives.WriteUInt32BigEndian(integer, checked((uint)index.Length));
        stream.Write(integer);
        stream.Write(index);
        return stream.ToArray();
    }

    private static void AssertSha256(byte[] content, string? expected)
    {
        if (!IsLowerHexSha256(expected))
            throw new InvalidDataException("A DAXQ integrity hash is not canonical lowercase SHA-256.");
        var expectedBytes = Convert.FromHexString(expected!);
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(content, actual);
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedBytes))
            throw new InvalidDataException("A DAXQ package integrity hash did not match.");
    }

    private static byte[] DecodeBase64Url(string? value, int expectedLength)
    {
        if (string.IsNullOrEmpty(value) || value.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw new InvalidDataException("A DAXQ base64url value is malformed.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => throw new InvalidDataException("A DAXQ base64url value has invalid length."),
        };
        byte[] decoded;
        try { decoded = Convert.FromBase64String(padded); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("A DAXQ base64url value is malformed.", exception);
        }
        if (decoded.Length != expectedLength || Base64Url(decoded) != value)
            throw new InvalidDataException("A DAXQ base64url value is non-canonical or has the wrong size.");
        return decoded;
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void RequireExactProperties(
        JsonElement value,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string>? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A DAXQ JSON member must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name) ||
                (!required.Contains(property.Name, StringComparer.Ordinal) &&
                 !(optional?.Contains(property.Name, StringComparer.Ordinal) ?? false)))
            {
                throw new InvalidDataException($"Unexpected or duplicate DAXQ JSON property '{property.Name}'.");
            }
        }
        if (required.Any(name => !seen.Contains(name)))
            throw new InvalidDataException("A DAXQ JSON object omitted a required property.");
    }

    private static bool IsLowerHexSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidKeyId(string? value) =>
        value is { Length: >= 1 and <= 256 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-');

    private static bool ValidStrategyId(string value) =>
        value.Length is >= 1 and <= 128 && LowerAlphaNumeric(value[0]) && LowerAlphaNumeric(value[^1]) &&
        value.All(character => LowerAlphaNumeric(character) || character is '.' or '-');

    private static bool ValidParameterId(string value) =>
        value.Length is >= 1 and <= 64 && value[0] is >= 'a' and <= 'z' &&
        value.All(character => LowerAlphaNumeric(character) || character == '_');

    private static bool ValidVersion(string value) =>
        value.Length is >= 1 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-');

    private static bool LowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
