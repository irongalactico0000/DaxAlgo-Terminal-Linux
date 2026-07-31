using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DaxAlgo.Daxq.Host;

public sealed class DaxqEs256PublicKeyRing
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public DaxqEs256PublicKeyRing Add(string keyId, ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var read);
        if (read != subjectPublicKeyInfo.Length || key.ExportParameters(false).Curve.Oid.Value !=
            ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            throw new CryptographicException("DAXQ trust keys must be canonical P-256 SPKI values.");
        }
        _keys[keyId] = subjectPublicKeyInfo.ToArray();
        return this;
    }

    internal bool Verify(
        string keyId,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signature)
    {
        if (!_keys.TryGetValue(keyId, out var spki))
            return false;
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return key.VerifyData(
            data,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    internal byte[] ExportP256PublicKey(string keyId)
    {
        if (!_keys.TryGetValue(keyId, out var spki))
            throw new CryptographicException("The DAXQ licensing key id is not trusted.");
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        var parameters = key.ExportParameters(false);
        if (parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
            throw new CryptographicException("The DAXQ licensing key is not a canonical P-256 key.");
        var raw = new byte[64];
        x.CopyTo(raw, 0);
        y.CopyTo(raw, 32);
        return raw;
    }

    internal static DaxqEs256PublicKeyRing DevelopmentReleaseKey()
    {
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Convert.FromHexString(
                    "6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296"),
                Y = Convert.FromHexString(
                    "4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5"),
            },
        });
        return new DaxqEs256PublicKeyRing().Add(
            DaxqPackageReader.DevelopmentReleaseKeyId,
            key.ExportSubjectPublicKeyInfo());
    }
}

internal static class DaxqCryptography
{
    internal const string Es256 = "ES256";
    internal const string KeyWrapAlgorithm = "A256KW";
    internal const string ContentAlgorithm = "A256GCM";
    internal const string ContentKeyOperation = "content_key";
    internal const string HeartbeatOperation = "heartbeat";
    internal const string SignalSessionOperation = "signal_session";

    public static string Base64Url(ReadOnlySpan<byte> value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    public static byte[] DecodeBase64Url(string? value, int? expectedLength = null)
    {
        if (string.IsNullOrEmpty(value) || value.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw new InvalidDataException("A licensing base64url value is malformed.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => throw new InvalidDataException("A licensing base64url value has invalid length."),
        };
        byte[] decoded;
        try { decoded = Convert.FromBase64String(padded); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("A licensing base64url value is malformed.", exception);
        }
        if ((expectedLength is not null && decoded.Length != expectedLength) || Base64Url(decoded) != value)
            throw new InvalidDataException("A licensing base64url value is non-canonical or has the wrong size.");
        return decoded;
    }

    public static string Sha256Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    public static byte[] BuildDeviceProof(
        string operation,
        DaxqChallengeResponse challenge,
        DaxqDeliveryContext context,
        Guid deviceId,
        string bindingSha256,
        string idempotencyKey)
    {
        using var stream = new MemoryStream(512);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("operation", operation);
            writer.WriteString("challenge_id", challenge.ChallengeId.ToString("D"));
            writer.WriteString("nonce", challenge.Nonce);
            writer.WriteString("license_id", context.LicenseId.ToString("D"));
            writer.WriteString("release_id", context.ReleaseId.ToString("D"));
            writer.WriteString("device_id", deviceId.ToString("D"));
            writer.WriteString("binding_sha256", bindingSha256);
            writer.WriteString("idempotency_key", idempotencyKey);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static byte[] DeriveContentKeyEncryptionKey(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> nonce,
        DaxqDeliveryContext context,
        Guid deviceId,
        string contentKeyId)
    {
        var info = Encoding.UTF8.GetBytes(
            $"DAXQ-CONTENT-KEY-V1|{context.LicenseId:D}|{context.ReleaseId:D}|{deviceId:D}|{contentKeyId}");
        try
        {
            return HkdfSha256(sharedSecret, nonce, info, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(info);
        }
    }

    public static byte[] WrapKey(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length < 16 || plaintext.Length % 8 != 0)
            throw new CryptographicException("RFC 3394 input must contain at least two 64-bit blocks.");
        var n = plaintext.Length / 8;
        var result = new byte[plaintext.Length + 8];
        result.AsSpan(0, 8).Fill(0xA6);
        plaintext.CopyTo(result.AsSpan(8));
        Span<byte> block = stackalloc byte[16];
        Span<byte> encrypted = stackalloc byte[16];
        using var aes = Aes.Create();
        var kekBytes = kek.ToArray();
        try
        {
            aes.Key = kekBytes;
            for (var j = 0; j < 6; j++)
            {
                for (var i = 1; i <= n; i++)
                {
                    result.AsSpan(0, 8).CopyTo(block);
                    result.AsSpan(i * 8, 8).CopyTo(block[8..]);
                    aes.EncryptEcb(block, encrypted, PaddingMode.None);
                    var t = checked((ulong)(n * j + i));
                    var a = BinaryPrimitives.ReadUInt64BigEndian(encrypted[..8]) ^ t;
                    BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(0, 8), a);
                    encrypted[8..].CopyTo(result.AsSpan(i * 8, 8));
                }
            }
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kekBytes);
            CryptographicOperations.ZeroMemory(block);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public static byte[] UnwrapKey(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapped)
    {
        if (wrapped.Length < 24 || wrapped.Length % 8 != 0)
            throw new CryptographicException("RFC 3394 wrapped input has an invalid size.");
        var n = wrapped.Length / 8 - 1;
        var state = wrapped.ToArray();
        Span<byte> block = stackalloc byte[16];
        Span<byte> decrypted = stackalloc byte[16];
        using var aes = Aes.Create();
        var kekBytes = kek.ToArray();
        try
        {
            aes.Key = kekBytes;
            for (var j = 5; j >= 0; j--)
            {
                for (var i = n; i >= 1; i--)
                {
                    var t = checked((ulong)(n * j + i));
                    var a = BinaryPrimitives.ReadUInt64BigEndian(state.AsSpan(0, 8)) ^ t;
                    BinaryPrimitives.WriteUInt64BigEndian(block[..8], a);
                    state.AsSpan(i * 8, 8).CopyTo(block[8..]);
                    aes.DecryptEcb(block, decrypted, PaddingMode.None);
                    decrypted[..8].CopyTo(state.AsSpan(0, 8));
                    decrypted[8..].CopyTo(state.AsSpan(i * 8, 8));
                }
            }
            Span<byte> expected = stackalloc byte[8];
            expected.Fill(0xA6);
            if (!CryptographicOperations.FixedTimeEquals(state.AsSpan(0, 8), expected))
                throw new CryptographicException("RFC 3394 key-wrap integrity check failed.");
            return state.AsSpan(8).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kekBytes);
            CryptographicOperations.ZeroMemory(state);
            CryptographicOperations.ZeroMemory(block);
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    public static T VerifyEnvelope<T>(DaxqSignedEnvelope envelope, DaxqEs256PublicKeyRing trust)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(trust);
        if (envelope.Algorithm != Es256)
            throw new CryptographicException("The licensing envelope does not use ES256.");
        var payload = DecodeBase64Url(envelope.EncodedPayload);
        var signature = DecodeBase64Url(envelope.EncodedSignature, 64);
        try
        {
            if (!trust.Verify(envelope.KeyId, payload, signature))
                throw new CryptographicException("The licensing envelope signature is invalid or untrusted.");
            return JsonSerializer.Deserialize<T>(payload)
                   ?? throw new InvalidDataException("The licensing envelope payload was empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static byte[] HkdfSha256(
        ReadOnlySpan<byte> inputKeyMaterial,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info,
        int length)
    {
        var saltBytes = salt.IsEmpty ? new byte[32] : salt.ToArray();
        var ikm = inputKeyMaterial.ToArray();
        byte[] pseudoRandomKey;
        try
        {
            using var extract = new HMACSHA256(saltBytes);
            pseudoRandomKey = extract.ComputeHash(ikm);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(saltBytes);
            CryptographicOperations.ZeroMemory(ikm);
        }

        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;
        try
        {
            while (offset < length)
            {
                using var expand = new HMACSHA256(pseudoRandomKey);
                var input = new byte[previous.Length + info.Length + 1];
                previous.CopyTo(input, 0);
                info.CopyTo(input.AsSpan(previous.Length));
                input[^1] = counter++;
                var next = expand.ComputeHash(input);
                CryptographicOperations.ZeroMemory(input);
                if (previous.Length != 0)
                    CryptographicOperations.ZeroMemory(previous);
                previous = next;
                var take = Math.Min(previous.Length, length - offset);
                previous.AsSpan(0, take).CopyTo(output.AsSpan(offset));
                offset += take;
            }
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pseudoRandomKey);
            if (previous.Length != 0)
                CryptographicOperations.ZeroMemory(previous);
        }
    }
}
