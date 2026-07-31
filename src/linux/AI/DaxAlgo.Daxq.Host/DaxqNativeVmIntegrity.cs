using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DaxAlgo.Daxq.Vm;

namespace DaxAlgo.Daxq.Host;

internal interface IDaxqAuthenticodeVerifier
{
    bool TryVerify(string path, out string signerCertificateSha256, out string failure);
}

internal interface IDaxqMacCodeSignatureVerifier
{
    bool TryVerify(string path, out string teamIdentifier, out string failure);
}

internal static class DaxqNativeVmIntegrity
{
    private const string HashMetadataName = "DaxqNativeVmSha256";
    private const string PublisherMetadataName = "DaxqNativeVmPublisherSha256";
    private const string MacTeamMetadataName = "DaxqNativeVmMacTeamIdentifier";

    public static string? VerifyAndConfigure(DaxqProtectedStrategyEngineOptions options)
    {
        var isWindows = OperatingSystem.IsWindows();
        var isMacOS = OperatingSystem.IsMacOS();
        if (!isWindows && !isMacOS)
            return "The protected native DAXQ VM is supported only on Windows and macOS.";

        var path = options.NativeLibraryPath ?? Path.Combine(
            AppContext.BaseDirectory,
            isMacOS ? "libdaxq_vm.dylib" : "daxq_vm.dll");
        if (!Path.IsPathFullyQualified(path))
            return "The native DAXQ VM path is not absolute.";
        path = Path.GetFullPath(path);
        if (isWindows && new Uri(path).IsUnc)
            return "The native DAXQ VM cannot be loaded from a remote path.";
        if (isMacOS && !path.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
            return "The macOS native DAXQ VM must be a dylib.";

        var expectedHash = options.ExpectedNativeLibrarySha256 ?? ReadMetadata(HashMetadataName);
        if (!TryNormalizeSha256(expectedHash, out var normalizedHash))
            return "The release omitted the pinned DAXQ VM hash.";

        string? normalizedPublisher = null;
        string? expectedMacTeam = null;
        if (isWindows)
        {
            var expectedPublisher = options.ExpectedNativePublisherCertificateSha256 ??
                                    ReadMetadata(PublisherMetadataName);
            if (!TryNormalizeSha256(expectedPublisher, out normalizedPublisher))
                return "The release omitted the pinned DAXQ VM Authenticode publisher certificate.";
        }
        else
        {
            expectedMacTeam = options.ExpectedNativeMacTeamIdentifier ?? ReadMetadata(MacTeamMetadataName);
            if (!TryNormalizeTeamIdentifier(expectedMacTeam, out expectedMacTeam))
                return "The release omitted the DAXQ VM Apple Developer Team identifier.";
        }
        if (!File.Exists(path))
            return "The signed protected native DAXQ VM is missing from the application directory.";

        try
        {
            // Deny replacement between hashing/signature verification and LoadLibrary.
            using var locked = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(locked));
            if (!FixedTimeHexEquals(normalizedHash, actualHash))
                return "The native DAXQ VM hash does not match the signed release manifest.";

            if (isWindows)
            {
                var authenticode = options.AuthenticodeVerifier ?? WindowsAuthenticodeVerifier.Instance;
                if (!authenticode.TryVerify(path, out var actualPublisher, out var signatureFailure))
                    return $"The native DAXQ VM Authenticode signature is invalid: {signatureFailure}";
                if (!TryNormalizeSha256(actualPublisher, out var normalizedActualPublisher) ||
                    !FixedTimeHexEquals(normalizedPublisher!, normalizedActualPublisher))
                {
                    return "The native DAXQ VM was signed by an unexpected publisher certificate.";
                }
            }
            else
            {
                var codeSignature = options.MacCodeSignatureVerifier ?? MacCodeSignatureVerifier.Instance;
                if (!codeSignature.TryVerify(path, out var actualTeam, out var signatureFailure))
                    return $"The native DAXQ VM Apple code signature is invalid: {signatureFailure}";
                if (!string.Equals(expectedMacTeam, actualTeam, StringComparison.Ordinal))
                    return "The native DAXQ VM was signed by an unexpected Apple Developer Team.";
            }

            var configureFault = DaxqNativeVm.ConfigureNativeLibrary(path);
            return configureFault == DaxqFault.Ok
                ? null
                : $"The verified native DAXQ VM could not be loaded: {configureFault}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          CryptographicException or BadImageFormatException)
        {
            return $"The native DAXQ VM integrity check failed: {exception.Message}";
        }
    }

    private static string? ReadMetadata(string name) =>
        typeof(DaxqNativeVmIntegrity).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, name, StringComparison.Ordinal))
            ?.Value;

    private static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = value?.Replace(":", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant() ?? string.Empty;
        return normalized.Length == 64 &&
               normalized.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    internal static bool TryNormalizeTeamIdentifier(string? value, out string normalized)
    {
        normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length is >= 2 and <= 64 &&
               normalized.All(character =>
                   character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-');
    }

    private static bool FixedTimeHexEquals(string expected, string actual)
    {
        var expectedBytes = Convert.FromHexString(expected);
        var actualBytes = Convert.FromHexString(actual);
        try { return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }
}

internal sealed class MacCodeSignatureVerifier : IDaxqMacCodeSignatureVerifier
{
    public static MacCodeSignatureVerifier Instance { get; } = new();

    public bool TryVerify(string path, out string teamIdentifier, out string failure)
    {
        teamIdentifier = string.Empty;
        failure = string.Empty;
        if (!OperatingSystem.IsMacOS())
        {
            failure = "Apple code-signature verification is available only on macOS.";
            return false;
        }

        var verify = RunCodesign(["--verify", "--strict", "--verbose=2", path]);
        if (verify.ExitCode != 0)
        {
            failure = TrimFailure(verify.StandardError);
            return false;
        }

        var details = RunCodesign(["--display", "--verbose=4", path]);
        if (details.ExitCode != 0)
        {
            failure = TrimFailure(details.StandardError);
            return false;
        }

        const string prefix = "TeamIdentifier=";
        var line = details.StandardError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null ||
            !DaxqNativeVmIntegrity.TryNormalizeTeamIdentifier(
                line[prefix.Length..],
                out teamIdentifier))
        {
            failure = "The code signature has no valid Apple Developer Team identifier.";
            return false;
        }

        return true;
    }

    private static (int ExitCode, string StandardError) RunCodesign(string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/codesign")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("macOS codesign could not be started.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "codesign timed out.");
        }
        Task.WaitAll(error, output);
        return (process.ExitCode, error.Result);
    }

    private static string TrimFailure(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }
}

internal sealed class WindowsAuthenticodeVerifier : IDaxqAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static WindowsAuthenticodeVerifier Instance { get; } = new();

    public bool TryVerify(string path, out string signerCertificateSha256, out string failure)
    {
        signerCertificateSha256 = string.Empty;
        failure = string.Empty;
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = nint.Zero;
        var data = new WinTrustData();
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePath,
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = fileInfoPointer,
                StateAction = 1, // WTD_STATEACTION_VERIFY
                ProviderFlags = 0x00000080 | 0x00001000, // chain excluding root; cached URLs only
            };
            var status = WinVerifyTrust(nint.Zero, GenericVerifyV2, ref data);
            if (status != 0)
            {
                failure = $"WinVerifyTrust returned 0x{status:x8}.";
                return false;
            }

#pragma warning disable SYSLIB0057 // Authenticode signer extraction has no loader replacement API.
            using var legacy = X509Certificate.CreateFromSignedFile(path);
            using var signer = new X509Certificate2(legacy);
#pragma warning restore SYSLIB0057
            signerCertificateSha256 = signer.GetCertHashString(HashAlgorithmName.SHA256);
            return true;
        }
        catch (CryptographicException exception)
        {
            failure = exception.Message;
            return false;
        }
        finally
        {
            if (data.StateData != nint.Zero)
            {
                data.StateAction = 2; // WTD_STATEACTION_CLOSE
                _ = WinVerifyTrust(nint.Zero, GenericVerifyV2, ref data);
            }
            if (fileInfoPointer != nint.Zero)
                Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        nint window,
        [MarshalAs(UnmanagedType.LPStruct)] Guid action,
        ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public nint FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }
}
