using System.Security.Cryptography;

namespace DaxAlgo.Daxq.Host.Tests;

public sealed class DaxqHardeningTests
{
    [DesktopSecurityFact]
    public void Native_vm_hash_mismatch_is_rejected_before_signature_verification_or_load()
    {
        using var file = new TemporaryFile([1, 2, 3, 4]);
        var authenticode = new StubAuthenticode(new string('a', 64));
        var codeSignature = new StubMacCodeSignature("TEAM-A");
        var options = Options(
            file.Path,
            new string('0', 64),
            new string('a', 64),
            authenticode,
            "TEAM-A",
            codeSignature);

        var failure = DaxqNativeVmIntegrity.VerifyAndConfigure(options);

        Assert.Contains("hash", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, authenticode.Calls);
        Assert.Equal(0, codeSignature.Calls);
    }

    [WindowsFact]
    public void Native_vm_wrong_authenticode_publisher_is_rejected_before_load()
    {
        using var file = new TemporaryFile([5, 6, 7, 8]);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.Path)));
        var authenticode = new StubAuthenticode(new string('b', 64));
        var options = Options(
            file.Path,
            hash,
            new string('a', 64),
            authenticode,
            "TEAM-A",
            new StubMacCodeSignature("TEAM-A"));

        var failure = DaxqNativeVmIntegrity.VerifyAndConfigure(options);

        Assert.Contains("unexpected publisher", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, authenticode.Calls);
    }

    [MacOSFact]
    public void Native_vm_wrong_Apple_team_is_rejected_before_load()
    {
        using var file = new TemporaryFile([5, 6, 7, 8]);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.Path)));
        var codeSignature = new StubMacCodeSignature("TEAM-B");
        var options = Options(
            file.Path,
            hash,
            new string('a', 64),
            new StubAuthenticode(new string('a', 64)),
            "TEAM-A",
            codeSignature);

        var failure = DaxqNativeVmIntegrity.VerifyAndConfigure(options);

        Assert.Contains("unexpected Apple Developer Team", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, codeSignature.Calls);
    }

    private static DaxqProtectedStrategyEngineOptions Options(
        string path,
        string hash,
        string publisher,
        IDaxqAuthenticodeVerifier authenticode,
        string macTeam,
        IDaxqMacCodeSignatureVerifier codeSignature) => new()
        {
            NativeLibraryPath = path,
            ExpectedNativeLibrarySha256 = hash,
            ExpectedNativePublisherCertificateSha256 = publisher,
            AuthenticodeVerifier = authenticode,
            ExpectedNativeMacTeamIdentifier = macTeam,
            MacCodeSignatureVerifier = codeSignature,
        };

    private sealed class StubAuthenticode(string publisher) : IDaxqAuthenticodeVerifier
    {
        public int Calls { get; private set; }

        public bool TryVerify(string path, out string signerCertificateSha256, out string failure)
        {
            Calls++;
            signerCertificateSha256 = publisher;
            failure = string.Empty;
            return true;
        }
    }

    private sealed class StubMacCodeSignature(string team) : IDaxqMacCodeSignatureVerifier
    {
        public int Calls { get; private set; }

        public bool TryVerify(string path, out string teamIdentifier, out string failure)
        {
            Calls++;
            teamIdentifier = team;
            failure = string.Empty;
            return true;
        }
    }

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(byte[] content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"daxq-hardening-{Guid.NewGuid():N}{(OperatingSystem.IsMacOS() ? ".dylib" : ".bin")}");
            File.WriteAllBytes(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
