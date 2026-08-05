namespace TradingTerminal.Core.Strategies.Definition;

/// <summary>
/// Provider-neutral, side-effect-free strategy meaning produced by the conversational builder.
/// Operators are referenced by stable id and version; trusted registry code derives their types,
/// state, capabilities, and runtime placement.
/// </summary>
public sealed record StrategyIntermediateRepresentationV1(
    int SchemaVersion,
    string StrategyId,
    string StrategyVersion,
    StrategyOperatorCatalogReferenceV1 OperatorCatalog,
    StrategyClockKindV1 Clock,
    IReadOnlyList<DataRequirementV1> DataRequirements,
    IReadOnlyList<StrategyIrNodeV1> Nodes,
    IReadOnlyList<StrategyIrOutputBindingV1> Outputs,
    bool FlattenOnEnd)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Pins the exact trusted operator vocabulary used to interpret the graph. A changed catalog
/// identity never silently reinterprets an existing definition.
/// </summary>
public sealed record StrategyOperatorCatalogReferenceV1(
    string CatalogId,
    string CatalogVersion,
    string CatalogHashSha256);

public enum StrategyClockKindV1
{
    EventTime = 1,
}

/// <summary>
/// The only serialized graph node shape. Adding an operator extends the trusted registry rather
/// than the schema or this CLR type.
/// </summary>
public sealed record StrategyIrNodeV1(
    string NodeId,
    string OperatorId,
    int OperatorVersion,
    IReadOnlyDictionary<string, string> InputBindings,
    IReadOnlyDictionary<string, StrategyLiteralV1> Parameters);

/// <summary>
/// Closed literal boundary for v1. Arbitrary source code, callbacks, object graphs, and host
/// handles cannot be embedded in a strategy definition.
/// </summary>
public sealed record StrategyLiteralV1(
    StrategyLiteralKindV1 Kind,
    bool? BooleanValue,
    long? IntegerValue,
    double? NumberValue,
    string? TextValue)
{
    public static StrategyLiteralV1 FromBoolean(bool value) =>
        new(StrategyLiteralKindV1.Boolean, value, null, null, null);

    public static StrategyLiteralV1 FromInteger(long value) =>
        new(StrategyLiteralKindV1.Integer, null, value, null, null);

    public static StrategyLiteralV1 FromNumber(double value) =>
        new(StrategyLiteralKindV1.Number, null, null, value, null);

    public static StrategyLiteralV1 FromText(string value) =>
        new(StrategyLiteralKindV1.Text, null, null, null, value);
}

public enum StrategyLiteralKindV1
{
    Boolean = 1,
    Integer = 2,
    Number = 3,
    Text = 4,
}

/// <summary>The only values a strategy definition may export across the host boundary.</summary>
public enum StrategyIrOutputKindV1
{
    Signal = 1,
    Target = 2,
    QuoteIntent = 3,
    OrderIntent = 4,
}

/// <summary>
/// A typed, inert graph export. An intent is not a risk decision or execution command; only the
/// host may bind account authority, reserve exposure, mutate OMS state, or dispatch to an adapter.
/// </summary>
public sealed record StrategyIrOutputBindingV1(
    string OutputId,
    StrategyIrOutputKindV1 Kind,
    string NodeId);

/// <summary>
/// One keyed semantic dimension. DomainId prevents values from unrelated universes or time grids
/// from becoming type-compatible merely because both have an axis called "instrument" or "time".
/// Cardinality is null only when it is not statically finite (normally the time axis).
/// </summary>
public sealed record StrategyAxisV1(
    string AxisId,
    string DomainId,
    int? Cardinality);

/// <summary>
/// Registry-derived type. Axes are keyed semantic dimensions (for example time, instrument, item),
/// not hard-coded Matrix/Vector subclasses. UnitTag is intentionally opaque in v1 but must match
/// exactly where an operator requires compatible units.
/// </summary>
public sealed record StrategyValueTypeV1(
    string TypeId,
    IReadOnlyList<StrategyAxisV1> Axes,
    string UnitTag,
    StrategyValueAvailabilityV1 Availability,
    bool Nullable);

public enum StrategyValueAvailabilityV1
{
    Ready = 1,
    Warmup = 2,
    MaybeMissing = 3,
}

public enum StrategyOperatorStateKindV1
{
    Stateless = 1,
    BoundedWindow = 2,
    Recursive = 3,
}

/// <summary>
/// Placement is derived from trusted operator metadata. No placement grants adapter access: the
/// final three values mean that the host owns interpretation of the resulting intent.
/// </summary>
public enum StrategyOperatorPlacementV1
{
    RestrictedCompute = 1,
    HostPortfolio = 2,
    HostRisk = 3,
    HostExecutionIntent = 4,
}

public sealed record StrategyOperatorKeyV1(string OperatorId, int Version);

public sealed record StrategyCapabilityRequirementV1(string CapabilityId, string Reason);

public sealed record StrategyIrIssueV1(string Code, string Path, string Message);

public sealed record StrategyIrNodeAnalysisV1(
    string NodeId,
    StrategyOperatorKeyV1 Operator,
    StrategyValueTypeV1 OutputType,
    StrategyOperatorStateKindV1 StateKind,
    StrategyOperatorPlacementV1 Placement,
    int LocalWarmupObservations,
    int MinimumWarmupObservations,
    IReadOnlyList<StrategyCapabilityRequirementV1> Capabilities);

public sealed record StrategyIrValidationResultV1(
    IReadOnlyList<StrategyIrIssueV1> Issues,
    IReadOnlyList<StrategyIrNodeAnalysisV1> Nodes,
    IReadOnlyList<StrategyCapabilityRequirementV1> DefinitionCapabilities)
{
    public bool IsValid => Issues.Count == 0;

    public IReadOnlyList<StrategyCapabilityRequirementV1> RequiredCapabilities => Nodes
        .SelectMany(static node => node.Capabilities)
        .Concat(DefinitionCapabilities)
        .GroupBy(static requirement => requirement.CapabilityId, StringComparer.Ordinal)
        .Select(static group => group.First())
        .OrderBy(static requirement => requirement.CapabilityId, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>Strict RFC-8785/JCS identity for the new IR schema.</summary>
public static class StrategyIrCanonicalJsonV1
{
    public const string AlgorithmVersion = ExecutableStrategyDefinitionCanonicalJson.AlgorithmVersion;

    public static string Serialize(StrategyIntermediateRepresentationV1 definition) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(definition);

    public static StrategyIntermediateRepresentationV1 Deserialize(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Deserialize<StrategyIntermediateRepresentationV1>(json);

    public static string Hash(StrategyIntermediateRepresentationV1 definition) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(definition);

    public static string Canonicalize(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Canonicalize(json);
}
