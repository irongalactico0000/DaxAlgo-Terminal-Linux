using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>The lifecycle of one conversationally generated strategy candidate.</summary>
public enum StrategyCandidateStatusV1
{
    Draft,
    AwaitingConfirmation,
    Confirmed,
    Rejected,
    Superseded,
}

/// <summary>
/// Stable top-level concerns shared by strategy domains. Domain-specific detail belongs in nested
/// groups and namespaced statement values rather than in an ever-growing root schema.
/// </summary>
public enum StrategyCandidateGroupKindV1
{
    Custom,
    MarketAndUniverse,
    Data,
    SignalAndAlpha,
    PortfolioAndSizing,
    RiskAndExits,
    Execution,
    StateAndTiming,
    Tests,
}

/// <summary>What one candidate statement contributes to the strategy's meaning.</summary>
public enum StrategyCandidateStatementKindV1
{
    Description,
    Rule,
    Constraint,
    Assumption,
    Requirement,
    Question,
    Test,
    Limitation,
}

/// <summary>Who introduced a statement. This is provenance for user review, not factual authority.</summary>
public enum StrategyCandidateStatementSourceV1
{
    User,
    Agent,
    SystemDefault,
    DeterministicSystem,
}

/// <summary>
/// Review state for a statement. Questions use Open/Resolved; other material statements use
/// Proposed/Confirmed/Rejected.
/// </summary>
public enum StrategyCandidateStatementStateV1
{
    Proposed,
    Confirmed,
    Rejected,
    Open,
    Resolved,
}

/// <summary>Plain-language support result shown before executable lowering is attempted.</summary>
public enum StrategyBuildSupportStatusV1
{
    Unknown,
    Supported,
    NeedsUserChoice,
    NeedsImplementation,
    DataUnavailable,
}

public enum StrategyInterpretationConfidenceV1
{
    Low,
    Medium,
    High,
}

/// <summary>
/// An extensible typed semantic value. Core values use ids such as <c>core.duration@1</c> or
/// <c>core.percentage@1</c>; domain packages may add ids such as <c>options.expiry_policy@1</c> without
/// changing the candidate schema. <see cref="CanonicalValue"/> is the invariant, human-readable wire
/// value and <see cref="Unit"/> is present only when the type permits one.
/// </summary>
public sealed record StrategyCandidateValueV1(
    string TypeId,
    string CanonicalValue,
    string? Unit = null);

public sealed record StrategyInterpretationAlternativeV1(
    string AlternativeId,
    string Summary);

/// <summary>The current plain-language reading of the user's original sentence.</summary>
public sealed record StrategyCandidateInterpretationV1(
    string Summary,
    StrategyInterpretationConfidenceV1 Confidence,
    IReadOnlyList<StrategyInterpretationAlternativeV1> Alternatives);

/// <summary>
/// One durable semantic claim. Text remains visible to the user; an optional typed value lets a
/// confirmed statement be lowered without reparsing prose.
/// </summary>
public sealed record StrategyCandidateStatementV1(
    string StatementId,
    StrategyCandidateStatementKindV1 Kind,
    string Text,
    StrategyCandidateStatementSourceV1 Source,
    StrategyCandidateStatementStateV1 State,
    bool IsMaterial,
    StrategyCandidateValueV1? Value = null);

/// <summary>
/// A recursively nestable strategy concern. Agents may propose changes to a group, but the group—not
/// the agent session—is the durable project object.
/// </summary>
public sealed record StrategyCandidateGroupV1(
    string GroupId,
    StrategyCandidateGroupKindV1 Kind,
    string Title,
    string Summary,
    IReadOnlyList<StrategyCandidateStatementV1> Statements,
    IReadOnlyList<StrategyCandidateGroupV1> Children);

/// <summary>
/// Deterministic support assessment for one requirement. Required items must be Supported before the
/// candidate can be lowered; optional items remain visible without blocking an otherwise complete
/// strategy.
/// </summary>
public sealed record StrategyBuildSupportItemV1(
    string SupportId,
    string Description,
    StrategyBuildSupportStatusV1 Status,
    bool RequiredForLowering,
    string Detail,
    IReadOnlyList<string> RelatedStatementIds);

/// <summary>
/// One versioned strategy proposal derived from a user sentence. It is intentionally upstream of
/// <see cref="StrategyIntermediateRepresentationV1"/>: ambiguity, alternatives, assumptions, open
/// questions, and unsupported requirements belong here and must not be smuggled into executable code.
/// </summary>
public sealed record StrategyCandidateV1(
    string SchemaVersion,
    string CandidateId,
    int Revision,
    string? ParentContentHashSha256,
    string RawIntent,
    string Title,
    StrategyCandidateStatusV1 Status,
    StrategyCandidateInterpretationV1 Interpretation,
    IReadOnlyList<StrategyCandidateGroupV1> Groups,
    IReadOnlyList<StrategyBuildSupportItemV1> BuildSupport)
{
    public const string CurrentSchemaVersion = "strategy-candidate/v1";
}

/// <summary>A user workspace containing independent strategy candidates and their revisions.</summary>
public sealed record StrategyGenerationWorkspaceV1(
    string SchemaVersion,
    string WorkspaceId,
    string Name,
    IReadOnlyList<StrategyCandidateV1> Candidates,
    string? ActiveCandidateId,
    int? ActiveCandidateRevision)
{
    public const string CurrentSchemaVersion = "strategy-generation-workspace/v1";
}

/// <summary>Canonical persistence and hashing for candidate revisions.</summary>
public static class StrategyCandidateCanonicalJsonV1
{
    public static string Serialize(StrategyCandidateV1 candidate) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(candidate);

    public static StrategyCandidateV1 Deserialize(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Deserialize<StrategyCandidateV1>(json);

    public static string Hash(StrategyCandidateV1 candidate) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(candidate);

    public static string Canonicalize(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Canonicalize(json);
}
