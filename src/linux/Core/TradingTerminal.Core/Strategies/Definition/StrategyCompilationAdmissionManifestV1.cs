namespace TradingTerminal.Core.Strategies.Definition;

/// <summary>Content pins for the one exact source binding admitted for a data requirement.</summary>
public sealed record StrategyCompilationDataPinV1(
    string RequirementId,
    string BindingId,
    TradeIrContentAddressV1 CapabilityDocument,
    TradeIrContentAddressV1 BindingDocument,
    TradeIrContentAddressV1 Snapshot,
    TradeIrContentAddressV1 AdapterArtifact,
    TradeIrContentAddressV1 EventSchema);

/// <summary>
/// Canonical identity document for the graph-lane compilation handoff. It contains identities,
/// not mutable parsed DTOs and not deployment authorization.
/// </summary>
public sealed record StrategyCompilationAdmissionDocumentV1(
    int SchemaVersion,
    string CanonicalizationAlgorithm,
    string AdmissionRulesVersion,
    TradeIrContentAddressV1 DefinitionDocument,
    TradeIrContentAddressV1 TargetProfileDocument,
    StrategyOperatorCatalogReferenceV1 OperatorCatalog,
    IReadOnlyList<StrategyCompilationDataPinV1> DataBindings)
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentAdmissionRulesVersion = "trade-ir/compilation-admission/v1";
}

/// <summary>
/// Immutable, content-addressed output of successful compilation admission. A compiler must read
/// the definition from this object; accepting the original caller-owned DTO would reopen the
/// validation-to-compilation mutation window.
/// </summary>
public sealed class StrategyCompilationAdmissionManifestV1
{
    private StrategyCompilationAdmissionManifestV1(
        string canonicalDefinitionJson,
        string canonicalTargetProfileJson,
        string canonicalManifestJson,
        string manifestHashSha256)
    {
        CanonicalDefinitionJson = canonicalDefinitionJson;
        CanonicalTargetProfileJson = canonicalTargetProfileJson;
        CanonicalManifestJson = canonicalManifestJson;
        ManifestHashSha256 = manifestHashSha256;
    }

    public string CanonicalDefinitionJson { get; }
    public string CanonicalTargetProfileJson { get; }
    public string CanonicalManifestJson { get; }
    public string ManifestHashSha256 { get; }

    /// <summary>
    /// Internal integrity rehydration. External callers cannot turn self-authored hashes into an
    /// admitted object; only deterministic admission in this assembly may create a manifest.
    /// </summary>
    internal static StrategyCompilationAdmissionManifestV1 Read(
        string canonicalDefinitionJson,
        string canonicalTargetProfileJson,
        string canonicalManifestJson,
        string manifestHashSha256)
    {
        RequireCanonical(canonicalDefinitionJson, nameof(canonicalDefinitionJson));
        RequireCanonical(canonicalTargetProfileJson, nameof(canonicalTargetProfileJson));
        RequireCanonical(canonicalManifestJson, nameof(canonicalManifestJson));
        RequireSha256(manifestHashSha256, nameof(manifestHashSha256));

        var actualManifestHash = ExecutableStrategyDefinitionCanonicalJson.Sha256(canonicalManifestJson);
        if (!StringComparer.Ordinal.Equals(actualManifestHash, manifestHashSha256))
            throw new InvalidDataException(
                $"Compilation-admission manifest hash mismatch: expected '{manifestHashSha256}', found '{actualManifestHash}'.");

        var document = ExecutableStrategyDefinitionCanonicalJson
            .Deserialize<StrategyCompilationAdmissionDocumentV1>(canonicalManifestJson);
        if (document.SchemaVersion != StrategyCompilationAdmissionDocumentV1.CurrentSchemaVersion ||
            document.CanonicalizationAlgorithm != StrategyIrCanonicalJsonV1.AlgorithmVersion ||
            document.AdmissionRulesVersion != StrategyCompilationAdmissionDocumentV1.CurrentAdmissionRulesVersion)
            throw new InvalidDataException("Compilation-admission manifest version or canonicalization contract is unsupported.");

        VerifyAddress(document.DefinitionDocument, canonicalDefinitionJson, "definitionDocument");
        VerifyAddress(document.TargetProfileDocument, canonicalTargetProfileJson, "targetProfileDocument");

        var definition = StrategyIrCanonicalJsonV1.Deserialize(canonicalDefinitionJson);
        var target = ExecutableStrategyDefinitionCanonicalJson
            .Deserialize<StrategyIrTargetProfileV1>(canonicalTargetProfileJson);
        ValidateDataPins(document.DataBindings, definition.DataRequirements);
        if (definition.OperatorCatalog != document.OperatorCatalog)
            throw new InvalidDataException("Compilation-admission manifest catalog does not match the frozen definition.");
        if (target.OperatorCatalog != document.OperatorCatalog)
            throw new InvalidDataException("Compilation-admission manifest catalog does not match the frozen target profile.");

        return new StrategyCompilationAdmissionManifestV1(
            canonicalDefinitionJson,
            canonicalTargetProfileJson,
            canonicalManifestJson,
            manifestHashSha256);
    }

    /// <summary>Reverifies all content identities and returns a fresh parsed definition.</summary>
    public StrategyIntermediateRepresentationV1 ReadDefinitionForCompilation()
    {
        var verified = Read(
            CanonicalDefinitionJson,
            CanonicalTargetProfileJson,
            CanonicalManifestJson,
            ManifestHashSha256);
        return StrategyIrCanonicalJsonV1.Deserialize(verified.CanonicalDefinitionJson);
    }

    public StrategyCompilationAdmissionDocumentV1 ReadDocument()
    {
        _ = ReadDefinitionForCompilation();
        return ExecutableStrategyDefinitionCanonicalJson
            .Deserialize<StrategyCompilationAdmissionDocumentV1>(CanonicalManifestJson);
    }

    private static void VerifyAddress(TradeIrContentAddressV1? address, string canonicalJson, string path)
    {
        ValidateAddress(address, path);
        var actual = ExecutableStrategyDefinitionCanonicalJson.Sha256(canonicalJson);
        if (!StringComparer.Ordinal.Equals(address!.Digest, actual))
            throw new InvalidDataException($"Compilation-admission {path} hash does not match its canonical JSON.");
    }

    private static void ValidateDataPins(
        IReadOnlyList<StrategyCompilationDataPinV1>? pins,
        IReadOnlyList<DataRequirementV1>? requirements)
    {
        if (pins is null)
            throw new InvalidDataException("Compilation-admission data pins are required.");
        if (requirements is null)
            throw new InvalidDataException("Frozen definition data requirements are required.");
        var keys = new HashSet<(string RequirementId, string BindingId)>();
        foreach (var pin in pins)
        {
            if (pin is null || string.IsNullOrWhiteSpace(pin.RequirementId) || string.IsNullOrWhiteSpace(pin.BindingId) ||
                !keys.Add((pin.RequirementId, pin.BindingId)))
                throw new InvalidDataException("Compilation-admission data pins require unique requirement and binding identities.");
            ValidateAddress(pin.CapabilityDocument, "dataBindings.capabilityDocument");
            ValidateAddress(pin.BindingDocument, "dataBindings.bindingDocument");
            ValidateAddress(pin.Snapshot, "dataBindings.snapshot");
            ValidateAddress(pin.AdapterArtifact, "dataBindings.adapterArtifact");
            ValidateAddress(pin.EventSchema, "dataBindings.eventSchema");
        }

        var actualOrder = pins.Select(static pin => (pin.RequirementId, pin.BindingId));
        var canonicalOrder = pins
            .OrderBy(static pin => pin.RequirementId, StringComparer.Ordinal)
            .ThenBy(static pin => pin.BindingId, StringComparer.Ordinal)
            .Select(static pin => (pin.RequirementId, pin.BindingId));
        if (!actualOrder.SequenceEqual(canonicalOrder))
            throw new InvalidDataException("Compilation-admission data pins are not canonically ordered.");

        var requirementById = requirements
            .Where(static requirement => requirement is not null && !string.IsNullOrWhiteSpace(requirement.RequirementId))
            .ToDictionary(static requirement => requirement.RequirementId, StringComparer.Ordinal);
        var pinsByRequirement = pins
            .GroupBy(static pin => pin.RequirementId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        if (requirementById.Count != requirements.Count ||
            pinsByRequirement.Count != requirementById.Count ||
            requirementById.Keys.Except(pinsByRequirement.Keys, StringComparer.Ordinal).Any() ||
            pinsByRequirement.Keys.Except(requirementById.Keys, StringComparer.Ordinal).Any() ||
            pinsByRequirement.Values.Any(static matches => matches.Length != 1))
            throw new InvalidDataException("Compilation-admission manifest requires exactly one data pin per frozen requirement.");

        foreach (var (requirementId, requirement) in requirementById)
        {
            var pin = pinsByRequirement[requirementId][0];
            if (requirement.RequiredSnapshotHashSha256 is { } snapshotHash &&
                !StringComparer.Ordinal.Equals(pin.Snapshot.Digest, snapshotHash))
                throw new InvalidDataException(
                    $"Compilation-admission snapshot pin does not match requirement '{requirementId}'.");
            if (requirement.EventSchema is null ||
                !StringComparer.Ordinal.Equals(pin.EventSchema.Digest, requirement.EventSchema.SchemaHashSha256))
                throw new InvalidDataException(
                    $"Compilation-admission schema pin does not match requirement '{requirementId}'.");
        }
    }

    private static void ValidateAddress(TradeIrContentAddressV1? address, string path)
    {
        if (address is null || address.Algorithm != TradeIrDigestAlgorithmV1.Sha256)
            throw new InvalidDataException($"Compilation-admission {path} requires a SHA-256 content address.");
        RequireSha256(address.Digest, path);
    }

    private static void RequireCanonical(string json, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json, parameterName);
        if (!StringComparer.Ordinal.Equals(ExecutableStrategyDefinitionCanonicalJson.Canonicalize(json), json))
            throw new InvalidDataException($"{parameterName} must already be canonical JSON.");
    }

    private static void RequireSha256(string value, string parameterName)
    {
        if (value is not { Length: 64 } ||
            !value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException($"{parameterName} must be a lowercase SHA-256 digest.");
    }
}

public sealed record StrategyCompilationAdmissionOutcomeV1(
    StrategyCompilationAdmissionResultV1 Assessment,
    StrategyCompilationAdmissionManifestV1? Manifest)
{
    public bool CanCompile => Assessment.CanCompile && Manifest is not null;
}
