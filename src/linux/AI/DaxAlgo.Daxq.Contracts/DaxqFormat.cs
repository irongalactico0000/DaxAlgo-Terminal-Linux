namespace DaxAlgo.Daxq.Contracts;

/// <summary>Frozen file, format, cryptographic, and VM ABI constants for DAXQ package version 1.</summary>
public static class DaxqFormat
{
    /// <summary>The protected-strategy package extension.</summary>
    public const string PackageExtension = ".daxq";

    /// <summary>The cleartext manifest ZIP entry.</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>The encrypted strategy ZIP entry.</summary>
    public const string CiphertextEntryName = "strategy.dqx";

    /// <summary>The package integrity-index ZIP entry.</summary>
    public const string PackageIndexEntryName = "package.json";

    /// <summary>The detached release-signature ZIP entry.</summary>
    public const string SignatureEntryName = "signature.json";

    /// <summary>The only supported manifest and package format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>The only supported deterministic plaintext-container version.</summary>
    public const int PlaintextContainerVersion = 1;

    /// <summary>The frozen VM ABI version used by DAXQ format v1.</summary>
    public const int VmAbiVersion = 3;

    /// <summary>The SDK ABI required by DAXQ format v1.</summary>
    public const int SdkAbiVersion = 3;

    /// <summary>The required manifest kind.</summary>
    public const string Kind = "daxq";

    /// <summary>The required content-encryption algorithm label.</summary>
    public const string CipherAlgorithm = "AES-256-GCM";

    /// <summary>The required detached-signature algorithm label.</summary>
    public const string SignatureAlgorithm = "ES256";

    /// <summary>The AES-GCM nonce size in bytes.</summary>
    public const int NonceSizeBytes = 12;

    /// <summary>The AES-GCM authentication-tag size in bytes.</summary>
    public const int AuthenticationTagSizeBytes = 16;

    /// <summary>The IEEE-P1363 P-256 signature size in bytes.</summary>
    public const int SignatureSizeBytes = 64;
}
