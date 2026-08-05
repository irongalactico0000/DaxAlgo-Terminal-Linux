using System.Text.Json.Serialization;

namespace TradingTerminal.Core.Strategies.Definition;

public enum TradeIrDigestAlgorithmV1
{
    Sha256 = 1,
}

/// <summary>A location-independent content identity for source, model, schema, or runtime bytes.</summary>
public sealed record TradeIrContentAddressV1(
    TradeIrDigestAlgorithmV1 Algorithm,
    string Digest);

/// <summary>A typed input exposed by an isolated extension module.</summary>
public sealed record StrategyModuleInputV1(
    string InputId,
    StrategyValueTypeV1 ValueType);

/// <summary>One of the same four inert values that the safe graph may export.</summary>
public sealed record StrategyModuleOutputV1(
    string OutputId,
    StrategyIrOutputKindV1 Kind,
    StrategyValueTypeV1 ValueType);

public enum StrategyModuleDeterminismV1
{
    Deterministic = 1,
    SeededDeterministic = 2,
    ExternallyDetermined = 3,
}

/// <summary>
/// Required extension ABI and confinement policy. This declaration is admission input, not proof
/// of isolation; the selected worker must independently attest that it enforces every restriction.
/// </summary>
public sealed record StrategyModuleRuntimeContractV1(
    string AbiVersion,
    TradeIrContentAddressV1 RuntimeAddress,
    StrategyModuleDeterminismV1 Determinism,
    long? RandomSeed,
    bool RequiresIsolatedProcess,
    bool AllowNetwork,
    bool AllowFileSystem,
    bool AllowCredentials,
    bool AllowInterprocessCommunication,
    bool AllowProcessCreation);

/// <summary>The three module forms supported by the authoring contract.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "moduleKind")]
[JsonDerivedType(typeof(OperatorGraphModuleV1), typeDiscriminator: "operatorGraph")]
[JsonDerivedType(typeof(CSharpModuleV1), typeDiscriminator: "csharp")]
[JsonDerivedType(typeof(ModelArtifactModuleV1), typeDiscriminator: "modelArtifact")]
public abstract record TradeIrModuleV1
{
    public const string CurrentSchemaVersion = "trade-ir/module/v1";

    private protected TradeIrModuleV1(string SchemaVersion, string ModuleId)
    {
        this.SchemaVersion = SchemaVersion;
        this.ModuleId = ModuleId;
    }

    public string SchemaVersion { get; init; }
    public string ModuleId { get; init; }
}

/// <summary>
/// The only safe persisted graph form. It wraps the canonical
/// <see cref="StrategyIntermediateRepresentationV1"/> rather than defining another node schema.
/// </summary>
public sealed record OperatorGraphModuleV1 : TradeIrModuleV1
{
    public OperatorGraphModuleV1(
        string SchemaVersion,
        string ModuleId,
        StrategyIntermediateRepresentationV1 Definition)
        : base(SchemaVersion, ModuleId)
    {
        this.Definition = Definition;
    }

    public StrategyIntermediateRepresentationV1 Definition { get; init; }
}

/// <summary>
/// A content-addressed C# source extension. Identity and a restrictive ABI do not make arbitrary
/// C# safe; admission must select an OS-isolated worker that proves this runtime contract.
/// </summary>
public sealed record CSharpModuleV1 : TradeIrModuleV1
{
    public CSharpModuleV1(
        string SchemaVersion,
        string ModuleId,
        IReadOnlyList<StrategyModuleInputV1> Inputs,
        IReadOnlyList<StrategyModuleOutputV1> Outputs,
        TradeIrContentAddressV1 SourceAddress,
        string LanguageVersion,
        string EntryPoint,
        StrategyModuleRuntimeContractV1 Runtime)
        : base(SchemaVersion, ModuleId)
    {
        this.Inputs = Inputs;
        this.Outputs = Outputs;
        this.SourceAddress = SourceAddress;
        this.LanguageVersion = LanguageVersion;
        this.EntryPoint = EntryPoint;
        this.Runtime = Runtime;
    }

    public IReadOnlyList<StrategyModuleInputV1> Inputs { get; init; }
    public IReadOnlyList<StrategyModuleOutputV1> Outputs { get; init; }
    public TradeIrContentAddressV1 SourceAddress { get; init; }
    public string LanguageVersion { get; init; }
    public string EntryPoint { get; init; }
    public StrategyModuleRuntimeContractV1 Runtime { get; init; }
}

/// <summary>
/// A content-addressed inference artifact. Training and promotion are separate research artifacts;
/// this module pins only immutable inference bytes, feature/output schemas, and runtime behavior.
/// </summary>
public sealed record ModelArtifactModuleV1 : TradeIrModuleV1
{
    public ModelArtifactModuleV1(
        string SchemaVersion,
        string ModuleId,
        IReadOnlyList<StrategyModuleInputV1> Inputs,
        IReadOnlyList<StrategyModuleOutputV1> Outputs,
        TradeIrContentAddressV1 ArtifactAddress,
        string Format,
        string EntryPoint,
        string FeatureSchemaHashSha256,
        string OutputSchemaHashSha256,
        StrategyModuleRuntimeContractV1 Runtime)
        : base(SchemaVersion, ModuleId)
    {
        this.Inputs = Inputs;
        this.Outputs = Outputs;
        this.ArtifactAddress = ArtifactAddress;
        this.Format = Format;
        this.EntryPoint = EntryPoint;
        this.FeatureSchemaHashSha256 = FeatureSchemaHashSha256;
        this.OutputSchemaHashSha256 = OutputSchemaHashSha256;
        this.Runtime = Runtime;
    }

    public IReadOnlyList<StrategyModuleInputV1> Inputs { get; init; }
    public IReadOnlyList<StrategyModuleOutputV1> Outputs { get; init; }
    public TradeIrContentAddressV1 ArtifactAddress { get; init; }
    public string Format { get; init; }
    public string EntryPoint { get; init; }
    public string FeatureSchemaHashSha256 { get; init; }
    public string OutputSchemaHashSha256 { get; init; }
    public StrategyModuleRuntimeContractV1 Runtime { get; init; }
}
