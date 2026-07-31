namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>The result of inspecting a plugin artifact's publisher authentication.</summary>
/// <param name="IsSigned">True when the artifact has cryptographically authenticated publisher
/// provenance (an Authenticode signature on Windows, or an exact-package signed-feed attestation on
/// macOS).</param>
/// <param name="IsValid">True when that provenance verifies and still binds the exact installed
/// artifact bytes. False for unsigned, unverifiable, or tampered artifacts.</param>
/// <param name="Thumbprint">The stable publisher identity pinned by the trust policy. On Windows this
/// is the signer-certificate thumbprint; for a signed-feed package it is the feed's publisher identity.</param>
/// <param name="Subject">The publisher description, for diagnostics.</param>
public sealed record PluginSignature(bool IsSigned, bool IsValid, string? Thumbprint, string? Subject)
{
    /// <summary>An unsigned assembly.</summary>
    public static PluginSignature Unsigned { get; } = new(false, false, null, null);
}

/// <summary>
/// Inspects a plugin assembly's code signature. A seam so the trust-policy decision is unit-testable
/// without real signed binaries — a fake inspector returns a chosen <see cref="PluginSignature"/>.
/// Production uses <see cref="AuthenticodeSignatureInspector"/> on Windows and
/// <see cref="FeedAttestedPluginSignatureInspector"/> for signed marketplace packages on macOS.
/// </summary>
public interface IPluginSignatureInspector
{
    PluginSignature Inspect(string assemblyPath);
}

/// <summary>Always reports unsigned. Used where no platform/artifact verifier is available; combined
/// with a require-signature policy it rejects everything.</summary>
public sealed class NullSignatureInspector : IPluginSignatureInspector
{
    public PluginSignature Inspect(string assemblyPath) => PluginSignature.Unsigned;
}
