namespace TradingTerminal.Core.Strategies.Definition;

public sealed record StrategyCompilationAdmissionIssueV1(string Code, string Path, string Message);

public sealed record StrategyCompilationAdmissionResultV1(
    StrategyIrValidationResultV1 SemanticValidation,
    StrategyIrTargetAssessmentV1 TargetAssessment,
    IReadOnlyList<DataAdmissionResult> DataAdmissions,
    IReadOnlyList<StrategyCompilationAdmissionIssueV1> Issues)
{
    /// <summary>No compiler may run until semantic, target, and exact data-binding gates pass.</summary>
    public bool CanCompile =>
        SemanticValidation.IsValid &&
        TargetAssessment.IsDeclaredCompatible &&
        DataAdmissions.All(static admission => admission.IsAdmitted) &&
        Issues.Count == 0;
}

/// <summary>
/// Deterministically joins the otherwise independent semantic, target, and data gates. It does no
/// discovery and accepts no model-authored capability claims: every compared fact is an explicit,
/// versioned input.
/// </summary>
public static class StrategyCompilationAdmissionV1
{
    /// <summary>
    /// Freezes all caller-owned inputs before validation and returns an immutable content-addressed
    /// compiler handoff only when every graph, target, and data gate passes.
    /// </summary>
    public static StrategyCompilationAdmissionOutcomeV1 AssessAndFreeze(
        StrategyIntermediateRepresentationV1 definition,
        IStrategyOperatorRegistryV1 registry,
        StrategyIrTargetProfileV1 target,
        IReadOnlyList<DataSourceCapabilityV1> capabilities,
        IReadOnlyList<DataBindingManifestV1> bindings)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(bindings);

        var canonicalDefinition = StrategyIrCanonicalJsonV1.Serialize(definition);
        var canonicalTarget = ExecutableStrategyDefinitionCanonicalJson.Serialize(target);
        var canonicalCapabilities = ExecutableStrategyDefinitionCanonicalJson.Serialize(capabilities.ToArray());
        var canonicalBindings = ExecutableStrategyDefinitionCanonicalJson.Serialize(bindings.ToArray());

        var frozenDefinition = StrategyIrCanonicalJsonV1.Deserialize(canonicalDefinition);
        var frozenTarget = ExecutableStrategyDefinitionCanonicalJson.Deserialize<StrategyIrTargetProfileV1>(canonicalTarget);
        var frozenCapabilities = ExecutableStrategyDefinitionCanonicalJson
            .Deserialize<DataSourceCapabilityV1[]>(canonicalCapabilities);
        var frozenBindings = ExecutableStrategyDefinitionCanonicalJson
            .Deserialize<DataBindingManifestV1[]>(canonicalBindings);

        var assessment = Assess(frozenDefinition, registry, frozenTarget, frozenCapabilities, frozenBindings);
        if (!assessment.CanCompile)
            return new StrategyCompilationAdmissionOutcomeV1(assessment, Manifest: null);

        var pins = frozenDefinition.DataRequirements
            .Select(requirement =>
            {
                var binding = frozenBindings.Single(candidate =>
                    StringComparer.Ordinal.Equals(candidate.RequirementId, requirement.RequirementId));
                var capability = frozenCapabilities.Single(candidate =>
                    StringComparer.Ordinal.Equals(candidate.CapabilityId, binding.CapabilityId) &&
                    candidate.Revision == binding.CapabilityRevision);
                return new StrategyCompilationDataPinV1(
                    requirement.RequirementId,
                    binding.BindingId,
                    AddressOf(capability),
                    AddressOf(binding),
                    Sha256Address(binding.SnapshotHashSha256),
                    Sha256Address(binding.AdapterHashSha256),
                    Sha256Address(binding.SchemaHashSha256));
            })
            .OrderBy(static pin => pin.RequirementId, StringComparer.Ordinal)
            .ThenBy(static pin => pin.BindingId, StringComparer.Ordinal)
            .ToArray();
        var document = new StrategyCompilationAdmissionDocumentV1(
            StrategyCompilationAdmissionDocumentV1.CurrentSchemaVersion,
            StrategyIrCanonicalJsonV1.AlgorithmVersion,
            StrategyCompilationAdmissionDocumentV1.CurrentAdmissionRulesVersion,
            Sha256Address(ExecutableStrategyDefinitionCanonicalJson.Sha256(canonicalDefinition)),
            Sha256Address(ExecutableStrategyDefinitionCanonicalJson.Sha256(canonicalTarget)),
            frozenDefinition.OperatorCatalog,
            pins);
        var canonicalManifest = ExecutableStrategyDefinitionCanonicalJson.Serialize(document);
        var manifest = StrategyCompilationAdmissionManifestV1.Read(
            canonicalDefinition,
            canonicalTarget,
            canonicalManifest,
            ExecutableStrategyDefinitionCanonicalJson.Sha256(canonicalManifest));
        return new StrategyCompilationAdmissionOutcomeV1(assessment, manifest);
    }

    public static StrategyCompilationAdmissionResultV1 Assess(
        StrategyIntermediateRepresentationV1 definition,
        IStrategyOperatorRegistryV1 registry,
        StrategyIrTargetProfileV1 target,
        IReadOnlyList<DataSourceCapabilityV1> capabilities,
        IReadOnlyList<DataBindingManifestV1> bindings)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(bindings);

        var semantic = StrategyIrValidatorV1.Validate(definition, registry);
        var targetAssessment = StrategyIrTargetAssessorV1.Assess(definition, registry, target);
        var admissions = new List<DataAdmissionResult>();
        var issues = new List<StrategyCompilationAdmissionIssueV1>();

        foreach (var requirement in definition.DataRequirements ?? [])
        {
            if (requirement is null)
            {
                issues.Add(new(
                    "ADMISSION_DATA_REQUIREMENT_INVALID",
                    "dataRequirements",
                    "Null data requirements cannot be admitted."));
                continue;
            }
            var matchingBindings = bindings
                .Where(binding => binding is not null &&
                    StringComparer.Ordinal.Equals(binding.RequirementId, requirement.RequirementId))
                .OrderBy(static binding => binding.BindingId, StringComparer.Ordinal)
                .ToArray();
            if (matchingBindings.Length != 1)
            {
                issues.Add(new(
                    "ADMISSION_DATA_BINDING_COUNT",
                    $"dataRequirements[{requirement.RequirementId}]",
                    $"Expected exactly one binding, found {matchingBindings.Length}."));
                continue;
            }

            var manifest = matchingBindings[0];
            var matchingCapabilities = capabilities
                .Where(capability => capability is not null &&
                    StringComparer.Ordinal.Equals(capability.CapabilityId, manifest.CapabilityId) &&
                    capability.Revision == manifest.CapabilityRevision)
                .OrderBy(static capability => capability.CapabilityId, StringComparer.Ordinal)
                .ToArray();
            if (matchingCapabilities.Length != 1)
            {
                issues.Add(new(
                    "ADMISSION_DATA_CAPABILITY_COUNT",
                    $"dataRequirements[{requirement.RequirementId}]",
                    $"Expected exactly one capability '{manifest.CapabilityId}' revision {manifest.CapabilityRevision}, found {matchingCapabilities.Length}."));
                continue;
            }

            admissions.Add(DataAdmissionValidator.Assess(requirement, matchingCapabilities[0], manifest));
        }

        foreach (var limitation in targetAssessment.Limitations)
            issues.Add(new($"TARGET.{limitation.Code}", limitation.Path, limitation.Message));
        foreach (var admission in admissions)
            foreach (var issue in admission.Issues)
                issues.Add(new($"DATA.{issue.Code}", issue.Path, issue.Message));

        return new StrategyCompilationAdmissionResultV1(
            semantic,
            targetAssessment,
            admissions.OrderBy(static admission => admission.RequirementId, StringComparer.Ordinal).ToArray(),
            issues.OrderBy(static issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Message, StringComparer.Ordinal)
                .ToArray());
    }

    private static TradeIrContentAddressV1 AddressOf(object value) =>
        Sha256Address(ExecutableStrategyDefinitionCanonicalJson.Hash(value));

    private static TradeIrContentAddressV1 Sha256Address(string digest) =>
        new(TradeIrDigestAlgorithmV1.Sha256, digest);
}
