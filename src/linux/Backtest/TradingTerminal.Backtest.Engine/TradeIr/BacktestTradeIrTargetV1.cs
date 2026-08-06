using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Backtest.Engine.TradeIr;

/// <summary>An immutable content identity for one installed target artifact.</summary>
public sealed record BacktestTradeIrArtifactIdentityV1(
    string ArtifactId,
    string ArtifactVersion,
    string ArtifactHashSha256);

/// <summary>
/// The three independent artifact identities required before the closed TradeIR backtest target
/// may compile. Keeping them separate prevents a compiler hash from standing in for runtime or
/// execution-host provenance.
/// </summary>
public sealed record BacktestTradeIrArtifactSetV1(
    BacktestTradeIrArtifactIdentityV1 Compiler,
    BacktestTradeIrArtifactIdentityV1 Runtime,
    BacktestTradeIrArtifactIdentityV1 ExecutionHost);

public static class BacktestTradeIrAdmissionIssueCodesV1
{
    public const string CompilerIdentityMismatch = "BACKTEST_TRADEIR_COMPILER_IDENTITY_MISMATCH";
    public const string RuntimeIdentityMismatch = "BACKTEST_TRADEIR_RUNTIME_IDENTITY_MISMATCH";
    public const string ExecutionHostIdentityMismatch = "BACKTEST_TRADEIR_EXECUTION_HOST_IDENTITY_MISMATCH";
}

/// <summary>
/// Engine-owned closed target for the minimum quote/EMA TradeIR slice. This surface accepts only
/// the typed operator graph; arbitrary C# and model modules have no admission overload here.
/// </summary>
public sealed class BacktestTradeIrTargetV1
{
    public const string ProfileId = "backtest.tradeir.quote-ema-v1";
    public const int ProfileRevision = 1;
    public const string CompilerArtifactId = "daxalgo.tradeir.plan-compiler";
    public const string RuntimeArtifactId = "daxalgo.tradeir.pure-evaluator";
    public const string ExecutionHostArtifactId = "daxalgo.backtest-engine.tradeir-host";
    public const string ArtifactVersion = "1.0.0";

    private static readonly IReadOnlyList<StrategyOperatorKeyV1> Operators = Array.AsReadOnly(
    [
        new StrategyOperatorKeyV1("execution.market", 1),
        new StrategyOperatorKeyV1("feature.ema", 1),
        new StrategyOperatorKeyV1("logic.greater_than", 1),
        new StrategyOperatorKeyV1("market.quote.mid", 1),
        new StrategyOperatorKeyV1("portfolio.fixed_quantity", 1),
        new StrategyOperatorKeyV1("risk.trailing_fraction", 1),
    ]);

    private static readonly IReadOnlyList<string> Capabilities = Array.AsReadOnly(
    [
        "data.quote_l1",
        "execution.market",
        "lifecycle.flatten_on_end",
        "portfolio.fixed_quantity",
        "risk.trailing_fraction",
        "state.recursive",
    ]);

    private static readonly IReadOnlyList<StrategyOperatorPlacementV1> Placements = Array.AsReadOnly(
    [
        StrategyOperatorPlacementV1.RestrictedCompute,
        StrategyOperatorPlacementV1.HostPortfolio,
        StrategyOperatorPlacementV1.HostRisk,
        StrategyOperatorPlacementV1.HostExecutionIntent,
    ]);

    private readonly StrategyOperatorRegistryV1 _registry;

    private BacktestTradeIrTargetV1(
        StrategyOperatorRegistryV1 registry,
        BacktestTradeIrArtifactSetV1 pinnedArtifacts)
    {
        _registry = registry;
        PinnedArtifacts = pinnedArtifacts;
        Profile = new StrategyIrTargetProfileV1(
            ProfileId,
            ProfileRevision,
            StrategyIntermediateRepresentationV1.CurrentSchemaVersion,
            registry.Catalog,
            pinnedArtifacts.Compiler.ArtifactId,
            pinnedArtifacts.Compiler.ArtifactVersion,
            pinnedArtifacts.Compiler.ArtifactHashSha256,
            pinnedArtifacts.ExecutionHost.ArtifactId,
            pinnedArtifacts.ExecutionHost.ArtifactVersion,
            pinnedArtifacts.ExecutionHost.ArtifactHashSha256,
            Operators,
            Capabilities,
            Placements);
    }

    /// <summary>The product registry pinned by the target; callers cannot substitute a catalog.</summary>
    public IStrategyOperatorRegistryV1 Registry => _registry;

    /// <summary>
    /// Set-compatibility profile for <see cref="StrategyCompilationAdmissionV1"/>. Deployment code
    /// must use <see cref="Assess"/> as well so the separately pinned runtime identity is checked.
    /// </summary>
    public StrategyIrTargetProfileV1 Profile { get; }

    public BacktestTradeIrArtifactSetV1 PinnedArtifacts { get; }

    public static BacktestTradeIrTargetV1 Create(BacktestTradeIrArtifactSetV1 pinnedArtifacts)
    {
        ArgumentNullException.ThrowIfNull(pinnedArtifacts);
        ValidatePinnedIdentity(
            pinnedArtifacts.Compiler,
            CompilerArtifactId,
            nameof(pinnedArtifacts.Compiler));
        ValidatePinnedIdentity(
            pinnedArtifacts.Runtime,
            RuntimeArtifactId,
            nameof(pinnedArtifacts.Runtime));
        ValidatePinnedIdentity(
            pinnedArtifacts.ExecutionHost,
            ExecutionHostArtifactId,
            nameof(pinnedArtifacts.ExecutionHost));

        return new BacktestTradeIrTargetV1(
            StrategyOperatorRegistryV1.CreateDefault(),
            pinnedArtifacts);
    }

    /// <summary>
    /// Runs semantic, closed-target, exact data-binding, and installed-artifact gates. No compiler
    /// or evaluator is invoked by this method.
    /// </summary>
    public StrategyCompilationAdmissionResultV1 Assess(
        StrategyIntermediateRepresentationV1 definition,
        BacktestTradeIrArtifactSetV1 loadedArtifacts,
        IReadOnlyList<DataSourceCapabilityV1> capabilities,
        IReadOnlyList<DataBindingManifestV1> bindings)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(loadedArtifacts);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(bindings);

        var result = StrategyCompilationAdmissionV1.Assess(
            definition,
            _registry,
            Profile,
            capabilities,
            bindings);
        return AddArtifactIdentityIssues(result, loadedArtifacts);
    }

    /// <summary>
    /// Freezes every caller-owned compilation input and verifies the three independently pinned
    /// target artifacts. A compiler must lower only the definition read back from the returned
    /// manifest; the original mutable DTO is no longer an authorized handoff.
    /// </summary>
    public StrategyCompilationAdmissionOutcomeV1 AssessAndFreeze(
        StrategyIntermediateRepresentationV1 definition,
        BacktestTradeIrArtifactSetV1 loadedArtifacts,
        IReadOnlyList<DataSourceCapabilityV1> capabilities,
        IReadOnlyList<DataBindingManifestV1> bindings)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(loadedArtifacts);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(bindings);

        var outcome = StrategyCompilationAdmissionV1.AssessAndFreeze(
            definition,
            _registry,
            Profile,
            capabilities,
            bindings);
        var assessment = AddArtifactIdentityIssues(outcome.Assessment, loadedArtifacts);
        return assessment.CanCompile && outcome.Manifest is not null
            ? new StrategyCompilationAdmissionOutcomeV1(assessment, outcome.Manifest)
            : new StrategyCompilationAdmissionOutcomeV1(assessment, Manifest: null);
    }

    private StrategyCompilationAdmissionResultV1 AddArtifactIdentityIssues(
        StrategyCompilationAdmissionResultV1 result,
        BacktestTradeIrArtifactSetV1 loadedArtifacts)
    {
        var identityIssues = new List<StrategyCompilationAdmissionIssueV1>();
        AddIdentityMismatch(
            PinnedArtifacts.Compiler,
            loadedArtifacts.Compiler,
            BacktestTradeIrAdmissionIssueCodesV1.CompilerIdentityMismatch,
            "targetArtifacts.compiler",
            identityIssues);
        AddIdentityMismatch(
            PinnedArtifacts.Runtime,
            loadedArtifacts.Runtime,
            BacktestTradeIrAdmissionIssueCodesV1.RuntimeIdentityMismatch,
            "targetArtifacts.runtime",
            identityIssues);
        AddIdentityMismatch(
            PinnedArtifacts.ExecutionHost,
            loadedArtifacts.ExecutionHost,
            BacktestTradeIrAdmissionIssueCodesV1.ExecutionHostIdentityMismatch,
            "targetArtifacts.executionHost",
            identityIssues);

        if (identityIssues.Count == 0) return result;
        return result with
        {
            Issues = result.Issues
                .Concat(identityIssues)
                .OrderBy(static issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Message, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static void ValidatePinnedIdentity(
        BacktestTradeIrArtifactIdentityV1? identity,
        string expectedId,
        string parameterName)
    {
        if (identity is null ||
            !StringComparer.Ordinal.Equals(identity.ArtifactId, expectedId) ||
            !StringComparer.Ordinal.Equals(identity.ArtifactVersion, ArtifactVersion) ||
            !IsSha256(identity.ArtifactHashSha256))
            throw new ArgumentException(
                $"Expected {expectedId}@{ArtifactVersion} with a lowercase SHA-256 artifact hash.",
                parameterName);
    }

    private static void AddIdentityMismatch(
        BacktestTradeIrArtifactIdentityV1 expected,
        BacktestTradeIrArtifactIdentityV1? actual,
        string code,
        string path,
        ICollection<StrategyCompilationAdmissionIssueV1> issues)
    {
        if (expected == actual) return;
        issues.Add(new StrategyCompilationAdmissionIssueV1(
            code,
            path,
            $"Loaded artifact identity '{Describe(actual)}' does not match pinned identity '{Describe(expected)}'."));
    }

    private static string Describe(BacktestTradeIrArtifactIdentityV1? identity) => identity is null
        ? "<null>"
        : $"{identity.ArtifactId}@{identity.ArtifactVersion}#{identity.ArtifactHashSha256}";

    private static bool IsSha256(string value) =>
        value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
