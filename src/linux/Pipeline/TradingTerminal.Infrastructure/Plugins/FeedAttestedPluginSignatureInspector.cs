using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Infrastructure.Plugins.Feed;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// Verifies the production macOS marketplace trust chain for an installed managed plugin:
/// pinned feed key -> detached feed signature -> exact <c>.daxplugin</c> SHA-256 -> package integrity
/// index -> exact installed folder bytes. The stable publisher identity comes from the signed feed and
/// is still checked by <see cref="PluginTrustPolicy"/>.
/// <para>
/// This intentionally does not invoke <c>/usr/bin/codesign</c>. The current plugin code object is a
/// loose managed PE/COFF DLL under LocalApplicationData, not a Mach-O image or signed Apple bundle.
/// Apple code signing therefore cannot authenticate that artifact; treating a failed or inapplicable
/// <c>codesign</c> call as success would create a trust bypass.
/// </para>
/// <para>
/// Every failure returns unsigned/invalid. A missing proof, changed package, changed dependency,
/// changed manifest, malformed feed, wrong feed key, or changed installed file can only make Curated
/// policy reject the plugin.
/// </para>
/// </summary>
public sealed class FeedAttestedPluginSignatureInspector : IPluginSignatureInspector
{
    private readonly string _pinnedFeedPublicKey;

    public FeedAttestedPluginSignatureInspector(string? pinnedFeedPublicKey) =>
        _pinnedFeedPublicKey = pinnedFeedPublicKey?.Trim() ?? string.Empty;

    public PluginSignature Inspect(string assemblyPath) =>
        FeedPackageTrust.InspectInstalled(assemblyPath, _pinnedFeedPublicKey);
}

internal sealed record VerifiedFeedPackage(
    string PluginId,
    string Publisher,
    string PublisherIdentity,
    string PackageSha256,
    byte[] IndexBytes,
    byte[] SignatureBytes)
{
    public PluginSignature Signature { get; } =
        new(IsSigned: true, IsValid: true, Thumbprint: PublisherIdentity, Subject: Publisher);
}

internal static class FeedPackageTrust
{
    internal const string PackageFileName = ".daxalgo-feed-package.daxplugin";
    internal const string AttestationFileName = ".daxalgo-feed-attestation.json";

    private const int CurrentAttestationVersion = 1;
    private const long MaxAttestationBytes = 8L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal static bool TryAuthenticateVersion(
        PluginFeedVersion requested,
        PluginTrustPolicy policy,
        out VerifiedFeedPackage? authenticated,
        out string? reason)
    {
        authenticated = null;
        reason = null;

        if (requested.VerifiedFeedProof is not { } proof)
        {
            reason = "The marketplace entry has no verified signed-feed proof.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(policy.FeedPublicKey))
        {
            reason = "No marketplace feed public key is pinned for macOS package verification.";
            return false;
        }

        var verified = new FeedSignatureVerifier(policy.FeedPublicKey)
            .Verify(proof.IndexBytes, proof.SignatureBytes);
        if (!verified.Success)
        {
            reason = $"The marketplace proof no longer verifies: {verified.Detail}";
            return false;
        }

        if (!TryFindExactVersion(verified.Index!, requested, out var entry, out var version))
        {
            reason = "The requested package metadata is not present in the signed marketplace index.";
            return false;
        }

        var identity = NormalizeIdentity(version!.SignatureThumbprint);
        if (identity.Length == 0)
        {
            reason = "The signed marketplace entry has no stable publisher identity.";
            return false;
        }

        var signature = new PluginSignature(true, true, identity, entry!.Publisher);
        if (!policy.Allows(signature, hasManifest: true, out var policyReason))
        {
            reason = $"The signed marketplace publisher is not trusted: {policyReason}.";
            return false;
        }

        authenticated = new VerifiedFeedPackage(
            entry.Id,
            entry.Publisher,
            identity,
            NormalizeHash(version.Sha256),
            proof.IndexBytes.ToArray(),
            proof.SignatureBytes.ToArray());
        return true;
    }

    internal static IPluginSignatureInspector CreateCatalogInspector(VerifiedFeedPackage authenticated) =>
        new CatalogPackageInspector(authenticated);

    internal static bool TryPersist(
        string packagePath,
        string installedDirectory,
        VerifiedFeedPackage authenticated,
        out string? reason)
    {
        reason = null;
        var packageTarget = Path.Combine(installedDirectory, PackageFileName);
        var attestationTarget = Path.Combine(installedDirectory, AttestationFileName);
        var packageTemp = packageTarget + ".tmp-" + Guid.NewGuid().ToString("N");
        var attestationTemp = attestationTarget + ".tmp-" + Guid.NewGuid().ToString("N");
        string? extracted = null;

        try
        {
            if (!string.Equals(
                    NormalizeHash(PluginIntegrity.Sha256(packagePath)),
                    authenticated.PackageSha256,
                    StringComparison.Ordinal))
            {
                reason = "The downloaded package changed before its signed-feed proof could be saved.";
                return false;
            }

            var extraction = DaxPluginPackage.ExtractAndVerify(packagePath);
            extracted = extraction.ExtractedDir;
            if (!FolderMatchesPackage(installedDirectory, extracted, extraction.MainAssemblyName))
            {
                reason = "The installed plugin folder does not exactly match the feed-authenticated package.";
                return false;
            }

            var dto = new AttestationDto(
                CurrentAttestationVersion,
                authenticated.PluginId,
                authenticated.Publisher,
                authenticated.PublisherIdentity,
                authenticated.PackageSha256,
                Convert.ToBase64String(authenticated.IndexBytes),
                Convert.ToBase64String(authenticated.SignatureBytes));

            File.Copy(packagePath, packageTemp, overwrite: false);
            File.WriteAllText(attestationTemp, JsonSerializer.Serialize(dto, JsonOptions));

            // Publish the marker last. A crash between the two moves leaves either no attestation or
            // an old attestation whose exact-folder comparison fails closed.
            File.Move(packageTemp, packageTarget, overwrite: true);
            File.Move(attestationTemp, attestationTarget, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or JsonException or FormatException)
        {
            reason = $"Could not preserve the signed marketplace proof: {ex.Message}";
            return false;
        }
        finally
        {
            TryDeleteFile(packageTemp);
            TryDeleteFile(attestationTemp);
            if (extracted is not null) TryDeleteDirectory(extracted);
        }
    }

    internal static PluginSignature InspectInstalled(string assemblyPath, string pinnedFeedPublicKey)
    {
        var directory = Path.GetDirectoryName(assemblyPath);
        if (directory is null) return PluginSignature.Unsigned;

        var packagePath = Path.Combine(directory, PackageFileName);
        var attestationPath = Path.Combine(directory, AttestationFileName);
        if (!File.Exists(packagePath) || !File.Exists(attestationPath))
            return PluginSignature.Unsigned;

        AttestationDto? dto = null;
        string? extracted = null;
        try
        {
            var length = new FileInfo(attestationPath).Length;
            if (length <= 0 || length > MaxAttestationBytes)
                return Invalid(dto);

            dto = JsonSerializer.Deserialize<AttestationDto>(File.ReadAllText(attestationPath), JsonOptions);
            if (dto is null
                || dto.FormatVersion != CurrentAttestationVersion
                || string.IsNullOrWhiteSpace(dto.PluginId)
                || string.IsNullOrWhiteSpace(dto.PublisherIdentity)
                || string.IsNullOrWhiteSpace(dto.PackageSha256)
                || string.IsNullOrWhiteSpace(dto.IndexBase64)
                || string.IsNullOrWhiteSpace(dto.SignatureBase64)
                || string.IsNullOrWhiteSpace(pinnedFeedPublicKey))
                return Invalid(dto);

            var indexBytes = Convert.FromBase64String(dto.IndexBase64);
            var signatureBytes = Convert.FromBase64String(dto.SignatureBase64);
            if (indexBytes.Length == 0 || indexBytes.Length > MaxAttestationBytes || signatureBytes.Length == 0)
                return Invalid(dto);

            var verified = new FeedSignatureVerifier(pinnedFeedPublicKey).Verify(indexBytes, signatureBytes);
            if (!verified.Success
                || !TryFindAttestedVersion(
                    verified.Index!, dto.PluginId, dto.PackageSha256, dto.PublisherIdentity, out var entry))
                return Invalid(dto);

            var packageHash = NormalizeHash(PluginIntegrity.Sha256(packagePath));
            if (!string.Equals(packageHash, NormalizeHash(dto.PackageSha256), StringComparison.Ordinal))
                return Invalid(dto);

            var extraction = DaxPluginPackage.ExtractAndVerify(packagePath);
            extracted = extraction.ExtractedDir;
            if (!FolderMatchesPackage(directory, extracted, extraction.MainAssemblyName)
                || !string.Equals(
                    Path.GetFileNameWithoutExtension(assemblyPath),
                    extraction.MainAssemblyName,
                    StringComparison.OrdinalIgnoreCase))
                return Invalid(dto);

            var installedManifest = PluginManifest.TryRead(directory);
            if (installedManifest is null
                || !string.Equals(installedManifest.Id, dto.PluginId, StringComparison.OrdinalIgnoreCase))
                return Invalid(dto);

            return new PluginSignature(
                IsSigned: true,
                IsValid: true,
                NormalizeIdentity(dto.PublisherIdentity),
                entry!.Publisher);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or JsonException or FormatException or CryptographicException)
        {
            return Invalid(dto);
        }
        finally
        {
            if (extracted is not null) TryDeleteDirectory(extracted);
        }
    }

    private static bool TryFindExactVersion(
        PluginIndex index,
        PluginFeedVersion requested,
        out PluginFeedEntry? matchedEntry,
        out PluginFeedVersion? matchedVersion)
    {
        foreach (var entry in index.Plugins)
        {
            foreach (var candidate in Versions(entry))
            {
                if (!SameVersion(candidate, requested)) continue;
                matchedEntry = entry;
                matchedVersion = candidate;
                return true;
            }
        }

        matchedEntry = null;
        matchedVersion = null;
        return false;
    }

    private static bool TryFindAttestedVersion(
        PluginIndex index,
        string pluginId,
        string packageSha256,
        string publisherIdentity,
        out PluginFeedEntry? matchedEntry)
    {
        foreach (var entry in index.Plugins)
        {
            if (!string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase)) continue;
            if (Versions(entry).Any(version =>
                    string.Equals(NormalizeHash(version.Sha256), NormalizeHash(packageSha256), StringComparison.Ordinal)
                    && string.Equals(
                        NormalizeIdentity(version.SignatureThumbprint),
                        NormalizeIdentity(publisherIdentity),
                        StringComparison.Ordinal)))
            {
                matchedEntry = entry;
                return true;
            }
        }

        matchedEntry = null;
        return false;
    }

    private static IEnumerable<PluginFeedVersion> Versions(PluginFeedEntry entry)
    {
        yield return entry.Latest;
        if (entry.Versions is null) yield break;
        foreach (var version in entry.Versions) yield return version;
    }

    private static bool SameVersion(PluginFeedVersion left, PluginFeedVersion right) =>
        string.Equals(left.Version, right.Version, StringComparison.Ordinal)
        && string.Equals(left.SdkVersion, right.SdkVersion, StringComparison.Ordinal)
        && string.Equals(left.Url, right.Url, StringComparison.Ordinal)
        && string.Equals(NormalizeHash(left.Sha256), NormalizeHash(right.Sha256), StringComparison.Ordinal)
        && string.Equals(left.MinAppVersion, right.MinAppVersion, StringComparison.Ordinal)
        && left.SizeBytes == right.SizeBytes
        && string.Equals(
            NormalizeIdentity(left.SignatureThumbprint),
            NormalizeIdentity(right.SignatureThumbprint),
            StringComparison.Ordinal);

    private static bool FolderMatchesPackage(
        string installedDirectory,
        string extractedDirectory,
        string mainAssemblyName)
    {
        var installedMain = Path.Combine(installedDirectory, mainAssemblyName + ".dll");
        if (!File.Exists(installedMain)) return false;

        var expected = HashFiles(extractedDirectory, excludeTrustArtifacts: false);
        var installed = HashFiles(installedDirectory, excludeTrustArtifacts: true);
        if (expected is null || installed is null || expected.Count != installed.Count) return false;

        foreach (var (relativePath, hash) in expected)
        {
            if (!installed.TryGetValue(relativePath, out var installedHash)
                || !string.Equals(hash, installedHash, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static Dictionary<string, string>? HashFiles(string root, bool excludeTrustArtifacts)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (IsDirectoryLink(root)) return null;

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (IsDirectoryLink(child)) return null;
                pending.Push(child);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (excludeTrustArtifacts && IsTrustArtifact(relative)) continue;
                if (IsFileLink(file)) return null;

                var hash = PluginIntegrity.Sha256(file);
                if (hash.Length == 0 || !result.TryAdd(relative, hash)) return null;
            }
        }
        return result;
    }

    private static bool IsTrustArtifact(string relativePath) =>
        string.Equals(relativePath, PackageFileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(relativePath, AttestationFileName, StringComparison.OrdinalIgnoreCase);

    private static bool IsFileLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
                   || new FileInfo(path).LinkTarget is not null;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsDirectoryLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
                   || new DirectoryInfo(path).LinkTarget is not null;
        }
        catch
        {
            return true;
        }
    }

    private static PluginSignature Invalid(AttestationDto? dto) =>
        new(
            IsSigned: true,
            IsValid: false,
            dto is null ? null : NormalizeIdentity(dto.PublisherIdentity),
            dto?.Publisher);

    private static string NormalizeIdentity(string? value) =>
        value?.Replace(" ", string.Empty).ToUpperInvariant() ?? string.Empty;

    private static string NormalizeHash(string? value) =>
        value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed class CatalogPackageInspector(VerifiedFeedPackage authenticated) : IPluginSignatureInspector
    {
        public PluginSignature Inspect(string assemblyPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(assemblyPath);
                if (directory is null
                    || File.Exists(Path.Combine(directory, PackageFileName))
                    || File.Exists(Path.Combine(directory, AttestationFileName)))
                    return authenticated.Signature with { IsValid = false };

                var manifest = PluginManifest.TryRead(directory);
                return manifest is not null
                       && string.Equals(manifest.Id, authenticated.PluginId, StringComparison.OrdinalIgnoreCase)
                    ? authenticated.Signature
                    : authenticated.Signature with { IsValid = false };
            }
            catch
            {
                return authenticated.Signature with { IsValid = false };
            }
        }
    }

    private sealed record AttestationDto(
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("pluginId")] string PluginId,
        [property: JsonPropertyName("publisher")] string Publisher,
        [property: JsonPropertyName("publisherIdentity")] string PublisherIdentity,
        [property: JsonPropertyName("packageSha256")] string PackageSha256,
        [property: JsonPropertyName("indexBase64")] string IndexBase64,
        [property: JsonPropertyName("signatureBase64")] string SignatureBase64);
}
