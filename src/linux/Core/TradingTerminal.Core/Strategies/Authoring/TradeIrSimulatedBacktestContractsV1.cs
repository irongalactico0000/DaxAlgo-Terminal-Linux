using System.Security.Cryptography;
using System.Text;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>Host-owned public facts for the deterministic synthetic QuoteL1 smoke adapter.</summary>
public static class TradeIrSimulatedBacktestContractV1
{
    public const string ExecutionMode = "in_process_synthetic_quote_l1_smoke";
    public const int MaximumEventCount = 100_000;
    public const string SchemaId = "canonical.quote-l1";
    public const int SchemaVersion = 1;
    public const string SchemaSemanticContract =
        "v1;fields=ask,ask_size,bid,bid_size;types=float64,float64,float64,float64;" +
        "event-time=utc-microseconds;ordering=event-time-then-source-sequence;sizes=nonnegative";

    public static IReadOnlyList<string> PayloadFields { get; } = Array.AsReadOnly(
        new[] { "ask", "ask_size", "bid", "bid_size" });

    public static string SchemaHashSha256 { get; } = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(SchemaSemanticContract)))
        .ToLowerInvariant();

    public static CanonicalEventSchemaV1 CreateEventSchema() => new(
        SchemaId,
        SchemaVersion,
        SchemaHashSha256,
        PayloadFields);
}

public static class TradeIrSimulatedBacktestIssueCodesV1
{
    public const string RequestRequired = "TRADEIR_SMOKE_REQUEST_REQUIRED";
    public const string SourceCandidateHashInvalid = "TRADEIR_SMOKE_SOURCE_CANDIDATE_HASH_INVALID";
    public const string ModuleHashInvalid = "TRADEIR_SMOKE_MODULE_HASH_INVALID";
    public const string ModuleHashMismatch = "TRADEIR_SMOKE_MODULE_HASH_MISMATCH";
    public const string ModuleInvalid = "TRADEIR_SMOKE_MODULE_INVALID";
    public const string EventCountInvalid = "TRADEIR_SMOKE_EVENT_COUNT_INVALID";
    public const string DataRequirementInvalid = "TRADEIR_SMOKE_DATA_REQUIREMENT_INVALID";
    public const string ArtifactIdentityUnavailable = "TRADEIR_SMOKE_ARTIFACT_IDENTITY_UNAVAILABLE";
    public const string Cancelled = "TRADEIR_SMOKE_CANCELLED";
    public const string RuntimeFailed = "TRADEIR_SMOKE_RUNTIME_FAILED";
}

/// <summary>
/// Terminal state for the deliberately narrow in-process TradeIR smoke runner. Rejection means a
/// deterministic input, package, target, or data-admission gate failed; failure means execution
/// started but the host could not complete it safely.
/// </summary>
public enum TradeIrSimulatedBacktestStatusV1
{
    Succeeded = 1,
    Rejected = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>One stable, path-addressed reason why a simulated TradeIR smoke run did not succeed.</summary>
public sealed record TradeIrSimulatedBacktestIssueV1(
    string Code,
    string Path,
    string Message);

/// <summary>
/// Exact authoring handoff for one typed graph. The source-candidate hash is lineage already proven
/// by the authoring coordinator; the runner independently recomputes the canonical module hash before
/// it admits any authored content.
/// </summary>
public sealed record TradeIrSimulatedBacktestRequestV1(
    string SourceCandidateHashSha256,
    string ExpectedModuleHashSha256,
    OperatorGraphModuleV1 Module,
    int EventCount = 512,
    int Seed = 17);

/// <summary>
/// Content-addressed evidence from a successful synthetic smoke run. This is explicitly not a
/// historical-data result and not a worker-isolation receipt.
/// </summary>
public sealed record TradeIrSimulatedBacktestEvidenceV1(
    string ExecutionMode,
    bool IsWorkerIsolated,
    bool IsHistoricalData,
    string SourceCandidateHashSha256,
    string ModuleHashSha256,
    string DefinitionHashSha256,
    string AdmissionManifestHashSha256,
    string SyntheticInputHashSha256,
    string CompilerArtifactHashSha256,
    string RuntimeArtifactHashSha256,
    string ExecutionHostArtifactHashSha256,
    string RuntimeReceiptHashSha256,
    long EventsProcessed,
    int SubmittedOrderCount);

/// <summary>Result returned to authoring without throwing for deterministic rejection or host failure.</summary>
public sealed record TradeIrSimulatedBacktestResultV1(
    TradeIrSimulatedBacktestStatusV1 Status,
    BacktestReport? Report,
    TradeIrSimulatedBacktestEvidenceV1? Evidence,
    IReadOnlyList<TradeIrSimulatedBacktestIssueV1> Issues)
{
    public bool Succeeded =>
        Status == TradeIrSimulatedBacktestStatusV1.Succeeded &&
        Report is not null &&
        Evidence is not null &&
        Issues.Count == 0;
}

/// <summary>
/// Runs the closed QuoteL1 TradeIR target against a deterministic in-process synthetic tape. It does
/// not dispatch a worker and does not claim historical-data fidelity.
/// </summary>
public interface ITradeIrSimulatedBacktestRunnerV1
{
    Task<TradeIrSimulatedBacktestResultV1> RunAsync(
        TradeIrSimulatedBacktestRequestV1 request,
        CancellationToken ct = default);
}
