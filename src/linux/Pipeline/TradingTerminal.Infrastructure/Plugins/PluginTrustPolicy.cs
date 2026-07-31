using System.Collections.Generic;
using System.Linq;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// Decides whether a plugin is allowed to load. This is the curated-marketplace gate: production
/// pins the stable publisher identities it trusts and requires valid artifact authentication, while the
/// open-core dev build is <see cref="Permissive"/> (loads unsigned local plugins). Thumbprint
/// PINNING — not merely "any valid signature" — is the curation control: only Windows DLLs signed by
/// a known publisher or macOS packages attested by the signed marketplace feed load, because an
/// in-process strategy gets the user's broker session and can't be sandboxed.
/// </summary>
public sealed record PluginTrustPolicy(
    bool RequireSignature,
    bool RequireManifest,
    IReadOnlySet<string> TrustedPublisherThumbprints,
    string FeedPublicKey = "")
{
    /// <summary>Dev / open-core default: load anything (the existing app + CLI flow). No signature or
    /// manifest required; signatures aren't even inspected.</summary>
    public static PluginTrustPolicy Permissive { get; } =
        new(RequireSignature: false, RequireManifest: false, new HashSet<string>());

    /// <summary>Curated marketplace policy: require a manifest and valid artifact authentication whose
    /// publisher identity is one of <paramref name="trustedThumbprints"/>.</summary>
    public static PluginTrustPolicy Curated(
        IEnumerable<string> trustedThumbprints,
        string? feedPublicKey = null) =>
        new(RequireSignature: true,
            RequireManifest: true,
            trustedThumbprints.Select(Normalize).Where(t => t.Length > 0).ToHashSet(),
            feedPublicKey?.Trim() ?? string.Empty);

    /// <summary>The policy the <c>Plugins</c> configuration section asks for — the shells' single
    /// entry point, so the mapping isn't triplicated across the three shell copies.</summary>
    public static PluginTrustPolicy From(PluginsOptions options) => options.TrustPolicy switch
    {
        PluginTrustMode.Curated => Curated(options.TrustedThumbprints, options.FeedPublicKey),
        _ => Permissive,
    };

    /// <summary>True when a plugin with the given signature and manifest presence may load. On
    /// rejection, <paramref name="reason"/> explains why (for logging).</summary>
    public bool Allows(PluginSignature signature, bool hasManifest, out string? reason)
    {
        if (RequireManifest && !hasManifest)
        {
            reason = "a plugin manifest (plugin.json) is required but is missing";
            return false;
        }

        if (!RequireSignature)
        {
            reason = null;
            return true;
        }

        if (!signature.IsSigned || !signature.IsValid)
        {
            reason = "valid publisher authentication is required, but the artifact is unsigned, unattested, or invalid";
            return false;
        }

        var thumb = Normalize(signature.Thumbprint);
        if (thumb.Length == 0 || !TrustedPublisherThumbprints.Contains(thumb))
        {
            reason = $"publisher identity '{signature.Thumbprint}' is not trusted";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Publisher identities are compared case-insensitively with spaces stripped (Windows
    /// certificate UIs add spaces; some sources lower-case).</summary>
    private static string Normalize(string? thumbprint) =>
        thumbprint?.Replace(" ", string.Empty).ToUpperInvariant() ?? string.Empty;
}
