using System.Net;
using System.Text.Json;

namespace TradingTerminal.Infrastructure.StrategyAgent;

public sealed record StrategyAgentComponentPin(
    string Component,
    string Version,
    string? SourceRevision = null);

public sealed record StrategyAgentDataFile(
    string Role,
    string Instrument,
    string Venue,
    string Source,
    string Timeframe,
    string RelativePath,
    string Sha256);

public sealed record StrategyAgentRunManifest(
    string RunId,
    string ConfirmedIntentSha256,
    string? ResearchContextSha256,
    DateTimeOffset SelectedStartUtc,
    DateTimeOffset SelectedEndUtc,
    DateTimeOffset AsOfUtc,
    string TimezoneName,
    IReadOnlyList<StrategyAgentDataFile> DataFiles,
    IReadOnlyList<StrategyAgentComponentPin> Components,
    string SchemaVersion = "daxalgo-native-run-manifest/v1");

public sealed record StrategyAgentSessionStatus(
    string SessionId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    string? ConfirmedRunId,
    int MessageCount,
    long LastEventSequence,
    JsonElement Context);

public sealed record StrategyAgentLaneResult(
    string SchemaVersion,
    string RunId,
    string Lane,
    string ManifestSha256,
    string Status,
    string NativeStage,
    string Framework,
    string FrameworkVersion,
    string? SourceRelativePath,
    IReadOnlyList<string> ArtifactRelativePaths,
    IReadOnlyDictionary<string, string> ArtifactSha256,
    JsonElement Observations,
    string? Error);

public sealed record StrategyAgentComparison(
    string RelativePath,
    string Sha256,
    JsonElement Report);

public sealed record StrategyAgentRunStatus(
    string RunId,
    string SessionId,
    string ManifestSha256,
    string Status,
    bool CancelRequested,
    IReadOnlyList<string> FixedLanes,
    IReadOnlyDictionary<string, string> LaneStates,
    IReadOnlyDictionary<string, StrategyAgentLaneResult> Results,
    StrategyAgentComparison? Comparison,
    string? EvidenceStatus,
    long LastEventSequence);

public sealed record StrategyAgentEvent(
    long Sequence,
    string? SessionId,
    string? RunId,
    string Lane,
    string Stage,
    string Status,
    DateTimeOffset OccurredAtUtc,
    string Message,
    JsonElement Details);

public sealed record StrategyAgentEventPage(
    IReadOnlyList<StrategyAgentEvent> Events,
    long NextAfterSeq,
    bool HasMore,
    bool Terminal);

public sealed record StrategyAgentArtifact(
    string RunId,
    string RelativePath,
    string Sha256,
    long SizeBytes,
    string Encoding,
    string Content);

/// <summary>An exact actionable error returned by the native-strategy service.</summary>
public sealed class StrategyAgentApiException : Exception
{
    public StrategyAgentApiException(
        string code,
        string detail,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(detail, innerException)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public HttpStatusCode? StatusCode { get; }
}
