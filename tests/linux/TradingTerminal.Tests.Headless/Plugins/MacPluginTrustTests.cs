using System.Security.Cryptography;
using System.Text.Json;
using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Plugins.Feed;
using Xunit;

namespace TradingTerminal.Tests.Headless.Plugins;

public sealed class MacPluginTrustTests
{
    private const string PluginId = "tests.feed-attested";
    private const string Publisher = "DaxAlgo Test Publisher";
    private const string PublisherIdentity = "AA11BB22CC33";

    [Fact]
    public void DefaultNonWindowsInspector_SelectsFeedProofOnlyForMacOS()
    {
        var policy = PluginTrustPolicy.Curated([PublisherIdentity], "test-key");

        PluginLoader.CreateDefaultNonWindowsInspector(policy, isMacOS: true)
            .Should().BeOfType<FeedAttestedPluginSignatureInspector>();
        PluginLoader.CreateDefaultNonWindowsInspector(policy, isMacOS: false)
            .Should().BeOfType<NullSignatureInspector>();
    }

    [Fact]
    public void SignedFeedPackage_VerifiesExactInstalledFolder_AndRejectsChangedDependency()
    {
        using var fixture = FeedPackageFixture.Create();
        var policy = PluginTrustPolicy.Curated([PublisherIdentity], fixture.PublicKeyBase64);

        FeedPackageTrust.TryAuthenticateVersion(
                fixture.VerifiedVersion, policy, out var authenticated, out var authenticationError)
            .Should().BeTrue(authenticationError);
        authenticated.Should().NotBeNull();

        fixture.CopyPayloadToInstalledFolder();
        FeedPackageTrust.TryPersist(
                fixture.PackagePath, fixture.InstalledDirectory, authenticated!, out var persistenceError)
            .Should().BeTrue(persistenceError);

        var inspector = new FeedAttestedPluginSignatureInspector(fixture.PublicKeyBase64);
        var accepted = inspector.Inspect(fixture.InstalledMainAssembly);
        accepted.IsSigned.Should().BeTrue();
        accepted.IsValid.Should().BeTrue();
        accepted.Thumbprint.Should().Be(PublisherIdentity);
        policy.Allows(accepted, hasManifest: true, out _).Should().BeTrue();

        File.AppendAllText(Path.Combine(fixture.InstalledDirectory, "private-dependency.dll"), "tampered");

        var rejected = inspector.Inspect(fixture.InstalledMainAssembly);
        rejected.IsSigned.Should().BeTrue();
        rejected.IsValid.Should().BeFalse();
        policy.Allows(rejected, hasManifest: true, out _).Should().BeFalse();
    }

    [Fact]
    public void FreelyConstructedOrMutatedFeedVersion_CannotCreatePublisherTrust()
    {
        using var fixture = FeedPackageFixture.Create();
        var policy = PluginTrustPolicy.Curated([PublisherIdentity], fixture.PublicKeyBase64);
        var unverified = fixture.VerifiedVersion with { VerifiedFeedProof = null };

        FeedPackageTrust.TryAuthenticateVersion(
                unverified, policy, out var unauthenticated, out var missingProofReason)
            .Should().BeFalse();
        unauthenticated.Should().BeNull();
        missingProofReason.Should().Contain("no verified signed-feed proof");

        var mutated = fixture.VerifiedVersion with { Sha256 = new string('0', 64) };
        FeedPackageTrust.TryAuthenticateVersion(
                mutated, policy, out var changed, out var mutationReason)
            .Should().BeFalse();
        changed.Should().BeNull();
        mutationReason.Should().Contain("not present in the signed marketplace index");
    }

    [Fact]
    public async Task MacCatalogInstall_RejectsUnverifiedMetadataBeforeDownloading()
    {
        var version = new PluginFeedVersion(
            "1.0.0",
            SdkInfo.Version,
            "https://plugins.example/unverified.daxplugin",
            new string('A', 64),
            SignatureThumbprint: PublisherIdentity);
        var policy = PluginTrustPolicy.Curated([PublisherIdentity], "not-a-valid-key");
        using var http = new HttpClient(new RejectNetworkHandler());

        var result = await PluginCatalogInstaller.InstallAsyncForPlatform(
            http,
            version,
            Path.Combine(Path.GetTempPath(), "unused-plugin-root"),
            policy,
            new NullSignatureInspector(),
            useFeedPackageAttestation: true);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not cryptographically authenticated");
    }

    private sealed class FeedPackageFixture : IDisposable
    {
        private FeedPackageFixture(
            string root,
            string payloadDirectory,
            string installedDirectory,
            string packagePath,
            string publicKeyBase64,
            PluginFeedVersion verifiedVersion)
        {
            Root = root;
            PayloadDirectory = payloadDirectory;
            InstalledDirectory = installedDirectory;
            PackagePath = packagePath;
            PublicKeyBase64 = publicKeyBase64;
            VerifiedVersion = verifiedVersion;
        }

        public string Root { get; }
        public string PayloadDirectory { get; }
        public string InstalledDirectory { get; }
        public string PackagePath { get; }
        public string PublicKeyBase64 { get; }
        public PluginFeedVersion VerifiedVersion { get; }
        public string InstalledMainAssembly => Path.Combine(InstalledDirectory, "SamplePlugin.dll");

        public static FeedPackageFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "daxalgo-mac-plugin-trust-" + Guid.NewGuid().ToString("N"));
            var payload = Path.Combine(root, "payload");
            var installed = Path.Combine(root, "installed", "SamplePlugin");
            var package = Path.Combine(root, "SamplePlugin.daxplugin");
            Directory.CreateDirectory(payload);

            File.WriteAllBytes(Path.Combine(payload, "SamplePlugin.dll"), "managed-plugin-bytes"u8.ToArray());
            File.WriteAllBytes(Path.Combine(payload, "private-dependency.dll"), "dependency-bytes"u8.ToArray());
            File.WriteAllText(
                Path.Combine(payload, PluginManifest.FileName),
                JsonSerializer.Serialize(new PluginManifest(
                    PluginId,
                    "Sample Plugin",
                    "1.0.0",
                    SdkInfo.Version,
                    Publisher)));
            DaxPluginPackage.Write(payload, "SamplePlugin.dll", package);

            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo());
            var version = new PluginFeedVersion(
                "1.0.0",
                SdkInfo.Version,
                "https://plugins.example/SamplePlugin.daxplugin",
                PluginIntegrity.Sha256(package),
                SizeBytes: new FileInfo(package).Length,
                SignatureThumbprint: PublisherIdentity);
            var index = new PluginIndex(
                PluginIndex.SupportedFeedVersion,
                [new PluginFeedEntry(
                    PluginId,
                    "Sample Plugin",
                    Publisher,
                    "Test package",
                    version)]);
            var indexBytes = JsonSerializer.SerializeToUtf8Bytes(index);
            var signature = signingKey.SignData(indexBytes, HashAlgorithmName.SHA256);
            var verified = new FeedSignatureVerifier(publicKey).Verify(indexBytes, signature);
            verified.Success.Should().BeTrue(verified.Detail);

            return new FeedPackageFixture(
                root,
                payload,
                installed,
                package,
                publicKey,
                verified.Index!.Plugins.Single().Latest);
        }

        public void CopyPayloadToInstalledFolder()
        {
            Directory.CreateDirectory(InstalledDirectory);
            foreach (var source in Directory.EnumerateFiles(PayloadDirectory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(
                    InstalledDirectory,
                    Path.GetRelativePath(PayloadDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Unverified marketplace metadata reached the network.");
    }
}
