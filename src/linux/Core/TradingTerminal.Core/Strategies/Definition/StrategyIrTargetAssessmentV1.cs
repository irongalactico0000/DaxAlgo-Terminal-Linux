namespace TradingTerminal.Core.Strategies.Definition;

/// <summary>
/// One concrete compiler/execution-host/data/runtime target. Semantic validity and target support are
/// deliberately separate: a valid graph can be unsupported by today's installed target.
/// </summary>
public sealed record StrategyIrTargetProfileV1(
    string ProfileId,
    int ProfileRevision,
    int DefinitionSchemaVersion,
    StrategyOperatorCatalogReferenceV1 OperatorCatalog,
    string CompilerBackendId,
    string CompilerBackendVersion,
    string CompilerArtifactHashSha256,
    string ExecutionHostId,
    string ExecutionHostVersion,
    string ExecutionHostArtifactHashSha256,
    IReadOnlyList<StrategyOperatorKeyV1> SupportedOperators,
    IReadOnlyList<string> SupportedCapabilities,
    IReadOnlyList<StrategyOperatorPlacementV1> SupportedPlacements);

public sealed record StrategyIrTargetAssessmentV1(
    StrategyIrValidationResultV1 SemanticValidation,
    IReadOnlyList<StrategyIrIssueV1> Limitations)
{
    /// <summary>
    /// Set compatibility only. This does not prove that the declared artifact hashes identify the
    /// bytes actually loaded by a compiler or execution host; deployment admission must verify that fact.
    /// </summary>
    public bool IsDeclaredCompatible => SemanticValidation.IsValid && Limitations.Count == 0;
}

public static class StrategyIrTargetAssessorV1
{
    public static StrategyIrTargetAssessmentV1 Assess(
        StrategyIntermediateRepresentationV1 definition,
        IStrategyOperatorRegistryV1 registry,
        StrategyIrTargetProfileV1 profile)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(profile);

        var validation = StrategyIrValidatorV1.Validate(definition, registry);
        var limitations = new List<StrategyIrIssueV1>();
        if (!validation.IsValid)
        {
            limitations.Add(new StrategyIrIssueV1(
                "semantic_validation_failed",
                "$",
                "Target support cannot be assessed until semantic validation passes."));
            return new StrategyIrTargetAssessmentV1(validation, limitations);
        }

        if (profile.DefinitionSchemaVersion != definition.SchemaVersion)
            limitations.Add(new StrategyIrIssueV1("target_schema_unsupported", "schemaVersion",
                $"Target '{profile.ProfileId}' supports schema {profile.DefinitionSchemaVersion}, not {definition.SchemaVersion}."));
        if (profile.OperatorCatalog != definition.OperatorCatalog)
            limitations.Add(new StrategyIrIssueV1("target_catalog_unsupported", "operatorCatalog",
                $"Target '{profile.ProfileId}' is not pinned to the definition's operator catalog."));
        if (profile.ProfileRevision <= 0 ||
            string.IsNullOrWhiteSpace(profile.ProfileId) ||
            string.IsNullOrWhiteSpace(profile.CompilerBackendId) ||
            string.IsNullOrWhiteSpace(profile.CompilerBackendVersion) ||
            string.IsNullOrWhiteSpace(profile.ExecutionHostId) ||
            string.IsNullOrWhiteSpace(profile.ExecutionHostVersion) ||
            !IsSha256(profile.CompilerArtifactHashSha256) ||
            !IsSha256(profile.ExecutionHostArtifactHashSha256) ||
            profile.SupportedOperators is null ||
            profile.SupportedCapabilities is null ||
            profile.SupportedPlacements is null ||
            profile.SupportedOperators.Any(static key => key is null ||
                string.IsNullOrWhiteSpace(key.OperatorId) || key.Version <= 0) ||
            profile.SupportedCapabilities.Any(string.IsNullOrWhiteSpace) ||
            profile.SupportedPlacements.Any(static placement => !Enum.IsDefined(placement)))
            limitations.Add(new StrategyIrIssueV1("target_profile_unverified", "targetProfile",
                "Target declarations require revisioned compiler/execution-host identities and lowercase SHA-256 artifact hashes."));

        var operators = (profile.SupportedOperators ?? [])
            .OfType<StrategyOperatorKeyV1>()
            .Select(static key => (key.OperatorId, key.Version))
            .ToHashSet();
        foreach (var node in validation.Nodes)
        {
            if (!operators.Contains((node.Operator.OperatorId, node.Operator.Version)))
                limitations.Add(new StrategyIrIssueV1(
                    "target_operator_unsupported",
                    $"nodes[{node.NodeId}].operatorId",
                    $"Target '{profile.ProfileId}' does not implement {node.Operator.OperatorId}@{node.Operator.Version}."));
        }

        var capabilities = (profile.SupportedCapabilities ?? [])
            .Where(static capability => !string.IsNullOrWhiteSpace(capability))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var requirement in validation.RequiredCapabilities)
        {
            if (!capabilities.Contains(requirement.CapabilityId))
                limitations.Add(new StrategyIrIssueV1(
                    "target_capability_unsupported",
                    "capabilities",
                    $"Target '{profile.ProfileId}' lacks '{requirement.CapabilityId}': {requirement.Reason}"));
        }

        var placements = (profile.SupportedPlacements ?? [])
            .Where(static placement => Enum.IsDefined(placement))
            .ToHashSet();
        foreach (var node in validation.Nodes)
        {
            if (!placements.Contains(node.Placement))
                limitations.Add(new StrategyIrIssueV1(
                    "target_placement_unsupported",
                    $"nodes[{node.NodeId}]",
                    $"Target '{profile.ProfileId}' cannot host placement '{node.Placement}'."));
        }

        return new StrategyIrTargetAssessmentV1(
            validation,
            limitations
                .OrderBy(static issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Message, StringComparer.Ordinal)
                .ToArray());
    }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
