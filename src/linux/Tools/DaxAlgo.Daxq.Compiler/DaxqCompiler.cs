namespace DaxAlgo.Daxq.Compiler;

using DaxAlgo.Daxq.Contracts;
using DaxAlgo.Daxq.Vm;
using System.Security.Cryptography;

/// <summary>Complete server-side Roslyn-to-signed-DAXQ compiler pipeline.</summary>
public sealed class DaxqCompiler
{
    private readonly DaxqRoslynCompiler _frontEnd = new();

    public DaxqCompilationArtifact Compile(string source, DaxqCompilerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.DiversificationSeed);
        ArgumentNullException.ThrowIfNull(options.Watermark);
        if (options.DiversificationSeed.Length != 32)
        {
            throw new ArgumentException(
                "The per-release diversification seed must contain exactly 32 bytes.",
                nameof(options));
        }
        if (options.Watermark.Length != 32)
            throw new ArgumentException("The DAXQ v1 watermark must contain exactly 32 bytes.", nameof(options));

        var lowering = _frontEnd.CompileAndLower(source, options.SourceFileName);
        var requirements = new HashSet<string>(options.DataRequirements, StringComparer.Ordinal);
        if ((lowering.Program.Entrypoints.Any(entrypoint => entrypoint.Id == DaxqEntrypoint.OnBar) ||
             lowering.Program.ReferencedHostFunctions.Contains(HostFn.Bar) ||
             lowering.Program.ReferencedHostFunctions.Contains(HostFn.Ind)) &&
            !requirements.Contains("bars"))
        {
            throw new ArgumentException(
                "A strategy using OnBar, Bar, or Indicator must declare the 'bars' data requirement.",
                nameof(options));
        }
        if (lowering.Program.Entrypoints.Any(entrypoint => entrypoint.Id == DaxqEntrypoint.OnTick) &&
            !requirements.Contains("ticks"))
        {
            throw new ArgumentException("An OnTick strategy must declare the 'ticks' data requirement.", nameof(options));
        }
        var invalidParameterId = lowering.Program.ReferencedParameterIds
            .Where(id => id >= options.Parameters.Count)
            .DefaultIfEmpty(-1)
            .First();
        if (invalidParameterId >= 0)
        {
            throw new DaxqCompilationException(
            [
                new DaxqCompilerDiagnostic(
                    "DAXQ2030",
                    $"Parameter ID {invalidParameterId} is not declared by the {options.Parameters.Count}-entry manifest."),
            ]);
        }
        DaxqPlaintextBuildResult plaintext;
        try
        {
            plaintext = DaxqPlaintextBuilder.BuildDiversified(
                lowering.Program,
                options.Watermark,
                options.DiversificationSeed);
        }
        catch (InvalidOperationException exception)
        {
            throw new DaxqCompilationException(
            [
                new DaxqCompilerDiagnostic(
                    "DAXQ2034",
                    $"Lowered bytecode failed the DAXQ v1 verifier: {exception.Message}"),
            ]);
        }
        var package = DaxqPackageWriter.Write(new DaxqPackageWriteOptions
        {
            PlaintextBytes = plaintext.DiversifiedPlaintext,
            StrategyId = options.StrategyId,
            Version = options.Version,
            DataRequirements = options.DataRequirements,
            Parameters = options.Parameters,
            ContentKeyId = options.ContentKeyId,
            ContentKey = options.ContentKey,
            Nonce = options.Nonce,
            ReleaseKeyId = options.ReleaseKeyId,
            ReleaseSigningKey = options.ReleaseSigningKey,
        });
        var releaseSeed = options.DiversificationSeed.ToArray();
        return new DaxqCompilationArtifact(
            lowering,
            plaintext,
            package,
            new DaxqReleaseMetadata(
                options.StrategyId,
                options.Version,
                Convert.ToHexStringLower(releaseSeed),
                Convert.ToHexStringLower(SHA256.HashData(releaseSeed)),
                Convert.ToHexStringLower(SHA256.HashData(package.PackageBytes))));
    }
}
