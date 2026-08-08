using System.Text.Json.Serialization;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Definition;
using TradingTerminal.Core.Strategies.Specification;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>The shape of decision output. Families remain classification lenses over these topologies.</summary>
public enum StrategyIntentKindV1
{
    PositionTarget,
    MultiLegTarget,
    PortfolioTarget,
    QuoteSet,
    ExecutionSchedule,
    SignalOnly,
    Extension,
}

/// <summary>
/// Host-derived interview profile. This selects questions and examples; it never dictates how a
/// downstream implementation target must be written.
/// </summary>
public enum StrategyIntentFamilyV1
{
    Directional,
    PairsOrArbitrage,
    PortfolioOrRebalance,
    MarketMaking,
    ExecutionAlgorithm,
    SignalPublication,
    Hedging,
    OptionsOrVolatility,
    GovernedExtension,
}

/// <summary>The family-neutral semantic lifecycle every reviewed intent must account for.</summary>
public enum StrategySemanticStageV1
{
    ObserveOrTrigger,
    QualifyEvidence,
    DecideIntent,
    SizeOrExposure,
    Execution,
    ManageLifecycle,
    FinishOrUnwind,
}

/// <summary>
/// Applicable means the requirement is resolved and applies. NotApplicable is an explicit, justified
/// disposition. Unresolved and Unsupported are never silently treated as warnings.
/// </summary>
public enum StrategySemanticDispositionV1
{
    Applicable,
    NotApplicable,
    Unresolved,
    Unsupported,
}

public sealed record StrategyIntentModelV1(
    StrategyIntentKindV1 Kind,
    string? ExtensionId = null);

/// <summary>
/// The schema role in which a governed extension identifier is authorized. Ownership is deliberately
/// role-scoped so one identifier cannot gain intent, requirement, and value authority at once.
/// </summary>
public enum StrategyIntentExtensionRoleV1
{
    IntentModel,
    Requirement,
    ValueType,
}

/// <summary>
/// Host authority for namespaced intent, requirement, and value extensions. Core validates the
/// semantic envelope; only an installed host registry may claim an identifier for its exact role.
/// </summary>
public interface IStrategyIntentExtensionRegistryV1
{
    bool Owns(StrategyIntentExtensionRoleV1 role, string extensionId);
}

/// <summary>
/// Value schemas implemented by Core itself. Every other namespaced value type must be owned by an
/// installed <see cref="IStrategyIntentExtensionRegistryV1"/>.
/// </summary>
public static class StrategyIntentValueTypesV1
{
    public const string SemanticClause = "core.semantic_clause@1";
    public const string Percentage = "core.percentage@1";
    public const string Duration = "core.duration@1";
    public const string Timeframe = "core.timeframe@1";

    private static readonly IReadOnlySet<string> BuiltIn = new HashSet<string>(StringComparer.Ordinal)
    {
        SemanticClause,
        Percentage,
        Duration,
        Timeframe,
    };

    public static bool IsBuiltIn(string? typeId) =>
        typeId is not null && BuiltIn.Contains(typeId);
}

/// <summary>Traceability back to reviewed candidate statements and research evidence.</summary>
public sealed record StrategyRequirementProvenanceV1(
    IReadOnlyList<string> CandidateStatementIds,
    IReadOnlyList<string> ResearchEvidenceIds,
    string Explanation);

/// <summary>One typed, reviewable unit of strategy meaning.</summary>
public sealed record StrategySemanticRequirementV1(
    string RequirementId,
    StrategySemanticStageV1 Stage,
    StrategySemanticDispositionV1 Disposition,
    string Description,
    bool IsMaterial,
    StrategyRequirementProvenanceV1 Provenance,
    StrategyCandidateValueV1? Value = null,
    string? DispositionRationale = null);

public sealed record ResearchEvidenceRequirementV1(
    string EvidenceId,
    string Description,
    string PointInTimeRule,
    string QualificationRule,
    bool IsMaterial,
    IReadOnlyList<string> CandidateStatementIds);

public sealed record ResearchFalsifierV1(
    string FalsifierId,
    string Description,
    bool IsMaterial,
    IReadOnlyList<string> CandidateStatementIds);

public sealed record ResearchUnresolvedItemV1(
    string ItemId,
    string Description,
    bool IsMaterial,
    IReadOnlyList<string> CandidateStatementIds);

/// <summary>An open research choice closed by an explicit, provenance-bearing user review.</summary>
public sealed record ResearchResolvedItemV1(
    string ItemId,
    string OriginalDescription,
    string Resolution,
    bool IsMaterial,
    IReadOnlyList<string> CandidateStatementIds,
    string ResolutionProvenance);

/// <summary>
/// Why a confirmed candidate deserves an experiment. It is upstream of implementation and carries
/// no backtest, approval, paper-trading, or live-trading claim.
/// </summary>
public sealed record ResearchCaseV1(
    string SchemaVersion,
    string ResearchCaseId,
    string CandidateId,
    string CandidateContentHashSha256,
    string Objective,
    string Hypothesis,
    IReadOnlyList<ResearchEvidenceRequirementV1> EvidenceRequirements,
    IReadOnlyList<ResearchFalsifierV1> Falsifiers,
    IReadOnlyList<ResearchUnresolvedItemV1> UnresolvedItems,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ResearchResolvedItemV1>? ResolvedItems = null)
{
    public const string CurrentSchemaVersion = "research-case/v1";
}

public sealed record StrategyClassificationBindingV1(
    string StrategySpecId,
    string StrategySpecHashSha256);

/// <summary>The exact review payload. Confirmation never reparses prose or calls a model.</summary>
public sealed record StrategyIntentDraftV1(
    string SchemaVersion,
    string IntentId,
    string CandidateId,
    int CandidateRevision,
    string CandidateContentHashSha256,
    string ResearchCaseHashSha256,
    StrategyClassificationBindingV1 Classification,
    StrategyIntentModelV1 IntentModel,
    string RequirementCatalogVersion,
    IReadOnlyList<StrategySemanticRequirementV1> Requirements)
{
    public const string CurrentSchemaVersion = "strategy-intent-draft/v1";
}

/// <summary>
/// Canonical strategy meaning accepted by the user. It grants no executable, historical, paper, or
/// live authority; later artifacts must bind this exact content hash.
/// </summary>
public sealed record ConfirmedStrategyIntentV1(
    string SchemaVersion,
    string IntentId,
    string CandidateId,
    int CandidateRevision,
    string CandidateContentHashSha256,
    string ResearchCaseHashSha256,
    StrategyClassificationBindingV1 Classification,
    StrategyIntentModelV1 IntentModel,
    string RequirementCatalogVersion,
    IReadOnlyList<StrategySemanticRequirementV1> Requirements,
    string ReviewedDraftContentHashSha256)
{
    public const string CurrentSchemaVersion = "confirmed-strategy-intent/v1";
}

public sealed record StrategyIntentQuestionV1(
    string QuestionId,
    string RequirementId,
    StrategySemanticStageV1 Stage,
    string Prompt,
    string Reason);

public sealed record StrategyIntentIssueV1(
    string Code,
    string Path,
    string Message);

public sealed record StrategyIntentConfirmationResultV1(
    ConfirmedStrategyIntentV1? Intent,
    IReadOnlyList<StrategyIntentQuestionV1> Questions,
    IReadOnlyList<StrategyIntentIssueV1> Issues)
{
    public bool Success => Intent is not null && Questions.Count == 0 && Issues.Count == 0;
}

public static class ResearchCaseCanonicalJsonV1
{
    public static string Serialize(ResearchCaseV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(value);

    public static string Hash(ResearchCaseV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(value);

    public static ResearchCaseV1 Deserialize(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Deserialize<ResearchCaseV1>(json);

    public static string Canonicalize(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Canonicalize(json);
}

public static class StrategySpecCanonicalJsonV1
{
    public static string Serialize(StrategySpec value) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(value);

    public static string Hash(StrategySpec value) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(value);

    public static StrategySpec Deserialize(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Deserialize<StrategySpec>(json);

    public static string Canonicalize(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Canonicalize(json);
}

public static class StrategyIntentCanonicalJsonV1
{
    public static string Serialize(StrategyIntentDraftV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(value);

    public static string Serialize(ConfirmedStrategyIntentV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(value);

    public static string Hash(StrategyIntentDraftV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(value);

    public static string Hash(ConfirmedStrategyIntentV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(value);

    public static StrategyIntentDraftV1 DeserializeDraft(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Deserialize<StrategyIntentDraftV1>(json);

    public static ConfirmedStrategyIntentV1 DeserializeConfirmed(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Deserialize<ConfirmedStrategyIntentV1>(json);

    public static string Canonicalize(string json) =>
        ExecutableStrategyDefinitionCanonicalJson.Canonicalize(json);
}

/// <summary>
/// Host-owned confirmation and completeness gate. Required questions are derived from intent topology
/// and classification axes, never from a single hard-coded buy-then-sell family.
/// </summary>
public static class StrategyIntentConfirmationV1
{
    public static StrategyIntentConfirmationResultV1 Confirm(
        StrategyCandidateV1? candidate,
        ResearchCaseV1? researchCase,
        StrategySpec? classification,
        StrategyIntentDraftV1? draft,
        string? expectedDraftHashSha256,
        IStrategyIntentExtensionRegistryV1? extensionRegistry = null)
    {
        var issues = new List<StrategyIntentIssueV1>();
        if (candidate is null) issues.Add(Issue("INTENT_CANDIDATE_REQUIRED", "candidate", "A confirmed candidate is required."));
        if (researchCase is null) issues.Add(Issue("INTENT_RESEARCH_CASE_REQUIRED", "researchCase", "A research case is required."));
        if (classification is null) issues.Add(Issue("INTENT_CLASSIFICATION_REQUIRED", "classification", "A StrategySpec classification is required."));
        if (draft is null) issues.Add(Issue("INTENT_DRAFT_REQUIRED", "draft", "A strategy-intent draft is required."));
        if (issues.Count > 0) return Failed(issues);

        var exactCandidate = candidate!;
        var exactResearchCase = researchCase!;
        var exactClassification = classification!;
        var exactDraft = draft!;

        ValidateCandidate(exactCandidate, issues);
        ValidateResearchCase(exactResearchCase, exactCandidate, issues);
        ValidateClassification(exactClassification, issues);
        ValidateDraft(
            exactDraft,
            exactCandidate,
            exactResearchCase,
            exactClassification,
            issues,
            extensionRegistry);

        var actualDraftHash = StrategyIntentCanonicalJsonV1.Hash(exactDraft);
        if (!IsSha256(expectedDraftHashSha256) ||
            !string.Equals(actualDraftHash, expectedDraftHashSha256, StringComparison.Ordinal))
        {
            issues.Add(Issue(
                "INTENT_DRAFT_HASH_MISMATCH",
                "expectedDraftHashSha256",
                "The intent draft changed after review. Review the current canonical draft before confirming."));
        }

        var questions = StrategySpecValidator.Validate(exactClassification).Count == 0 &&
                        exactDraft.Requirements is not null &&
                        exactDraft.Requirements.All(static requirement => requirement is not null) &&
                        exactDraft.IntentModel is not null
            ? StrategyIntentCompletenessV1.Questions(exactDraft, exactClassification)
            : [];
        if (issues.Count > 0 || questions.Count > 0)
            return new StrategyIntentConfirmationResultV1(null, questions, issues);

        var confirmed = new ConfirmedStrategyIntentV1(
            ConfirmedStrategyIntentV1.CurrentSchemaVersion,
            exactDraft.IntentId,
            exactDraft.CandidateId,
            exactDraft.CandidateRevision,
            exactDraft.CandidateContentHashSha256,
            exactDraft.ResearchCaseHashSha256,
            exactDraft.Classification,
            exactDraft.IntentModel!,
            exactDraft.RequirementCatalogVersion,
            exactDraft.Requirements!.ToArray(),
            actualDraftHash);
        return new StrategyIntentConfirmationResultV1(confirmed, [], []);
    }

    /// <summary>
    /// Revalidates a persisted confirmed intent against its exact dependencies. Reconstructing the
    /// reviewed draft makes any semantic mutation invalidate the stored review hash.
    /// </summary>
    public static IReadOnlyList<StrategyIntentIssueV1> ValidateConfirmed(
        ConfirmedStrategyIntentV1? intent,
        StrategyCandidateV1? candidate,
        ResearchCaseV1? researchCase,
        StrategySpec? classification,
        StrategyIntentDraftV1? expectedDraft = null,
        IStrategyIntentExtensionRegistryV1? extensionRegistry = null)
    {
        if (intent is null)
            return [Issue("CONFIRMED_INTENT_REQUIRED", "intent", "A confirmed strategy intent is required.")];
        var issues = new List<StrategyIntentIssueV1>();
        Exact(intent.SchemaVersion, ConfirmedStrategyIntentV1.CurrentSchemaVersion,
            "intent.schemaVersion", "CONFIRMED_INTENT_SCHEMA_UNSUPPORTED", issues);
        if (!IsSha256(intent.ReviewedDraftContentHashSha256))
            issues.Add(Issue("CONFIRMED_INTENT_REVIEW_HASH_INVALID", "intent.reviewedDraftContentHashSha256",
                "The reviewed draft content hash is not a canonical SHA-256 value."));
        var reconstructed = new StrategyIntentDraftV1(
            StrategyIntentDraftV1.CurrentSchemaVersion,
            intent.IntentId,
            intent.CandidateId,
            intent.CandidateRevision,
            intent.CandidateContentHashSha256,
            intent.ResearchCaseHashSha256,
            intent.Classification,
            intent.IntentModel,
            intent.RequirementCatalogVersion,
            intent.Requirements);
        if (expectedDraft is not null)
        {
            var expectedDraftJson = StrategyIntentCanonicalJsonV1.Serialize(expectedDraft);
            var reconstructedJson = StrategyIntentCanonicalJsonV1.Serialize(reconstructed);
            var expectedDraftHash = StrategyIntentCanonicalJsonV1.Hash(expectedDraft);
            if (!string.Equals(
                    expectedDraftHash,
                    intent.ReviewedDraftContentHashSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(expectedDraftJson, reconstructedJson, StringComparison.Ordinal))
            {
                issues.Add(Issue(
                    "CONFIRMED_INTENT_DRAFT_MISMATCH",
                    "intent.reviewedDraftContentHashSha256",
                    "The confirmed strategy intent does not exactly match the currently displayed review draft."));
            }
        }
        var result = Confirm(
            candidate,
            researchCase,
            classification,
            reconstructed,
            intent.ReviewedDraftContentHashSha256,
            extensionRegistry);
        issues.AddRange(result.Issues);
        if (result.Questions.Count > 0)
            issues.AddRange(result.Questions.Select(question => Issue(
                "CONFIRMED_INTENT_COVERAGE_INVALID",
                $"intent.requirements[{question.RequirementId}]",
                question.Reason)));
        return issues;
    }

    private static void ValidateCandidate(
        StrategyCandidateV1 candidate,
        ICollection<StrategyIntentIssueV1> issues)
    {
        var assessment = StrategyCandidateValidatorV1.Assess(candidate);
        if (!assessment.IsStructurallyValid)
            issues.Add(Issue("INTENT_CANDIDATE_INVALID", "candidate", "The candidate is not structurally valid."));
        if (candidate.Status != StrategyCandidateStatusV1.Confirmed)
            issues.Add(Issue("INTENT_CANDIDATE_NOT_CONFIRMED", "candidate.status", "Strategy meaning must be confirmed first."));
        if (!assessment.CanConfirm)
            issues.Add(Issue("INTENT_CANDIDATE_DECISIONS_OPEN", "candidate", "The candidate still contains material confirmation issues."));
    }

    private static void ValidateResearchCase(
        ResearchCaseV1 researchCase,
        StrategyCandidateV1 candidate,
        ICollection<StrategyIntentIssueV1> issues)
    {
        Exact(researchCase.SchemaVersion, ResearchCaseV1.CurrentSchemaVersion,
            "researchCase.schemaVersion", "RESEARCH_SCHEMA_UNSUPPORTED", issues);
        Required(researchCase.ResearchCaseId, "researchCase.researchCaseId", "RESEARCH_CASE_ID_REQUIRED", issues);
        Required(researchCase.Objective, "researchCase.objective", "RESEARCH_OBJECTIVE_REQUIRED", issues);
        Required(researchCase.Hypothesis, "researchCase.hypothesis", "RESEARCH_HYPOTHESIS_REQUIRED", issues);

        var candidateHash = StrategyCandidateCanonicalJsonV1.Hash(candidate);
        Exact(researchCase.CandidateId, candidate.CandidateId,
            "researchCase.candidateId", "RESEARCH_CANDIDATE_ID_MISMATCH", issues);
        Exact(researchCase.CandidateContentHashSha256, candidateHash,
            "researchCase.candidateContentHashSha256", "RESEARCH_CANDIDATE_HASH_MISMATCH", issues);

        var statementIds = CandidateStatementIds(candidate);
        if (researchCase.EvidenceRequirements is null || researchCase.EvidenceRequirements.Count == 0)
            issues.Add(Issue("RESEARCH_EVIDENCE_REQUIRED", "researchCase.evidenceRequirements", "At least one point-in-time evidence requirement is required."));
        if (researchCase.Falsifiers is null || researchCase.Falsifiers.Count == 0)
            issues.Add(Issue("RESEARCH_FALSIFIER_REQUIRED", "researchCase.falsifiers", "At least one falsifier or rejection condition is required."));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (evidence, index) in (researchCase.EvidenceRequirements ?? []).Select((value, index) => (value, index)))
        {
            var path = $"researchCase.evidenceRequirements[{index}]";
            if (evidence is null)
            {
                issues.Add(Issue("RESEARCH_EVIDENCE_ENTRY_REQUIRED", path, "Evidence entries cannot be null."));
                continue;
            }
            UniqueRequired(evidence.EvidenceId, $"{path}.evidenceId", "RESEARCH_EVIDENCE_ID", ids, issues);
            Required(evidence.Description, $"{path}.description", "RESEARCH_EVIDENCE_DESCRIPTION_REQUIRED", issues);
            Required(evidence.PointInTimeRule, $"{path}.pointInTimeRule", "RESEARCH_POINT_IN_TIME_RULE_REQUIRED", issues);
            Required(evidence.QualificationRule, $"{path}.qualificationRule", "RESEARCH_QUALIFICATION_RULE_REQUIRED", issues);
            ValidateStatementReferences(evidence.CandidateStatementIds, statementIds, $"{path}.candidateStatementIds", issues);
        }
        foreach (var (falsifier, index) in (researchCase.Falsifiers ?? []).Select((value, index) => (value, index)))
        {
            var path = $"researchCase.falsifiers[{index}]";
            if (falsifier is null)
            {
                issues.Add(Issue("RESEARCH_FALSIFIER_ENTRY_REQUIRED", path, "Falsifier entries cannot be null."));
                continue;
            }
            UniqueRequired(falsifier.FalsifierId, $"{path}.falsifierId", "RESEARCH_FALSIFIER_ID", ids, issues);
            Required(falsifier.Description, $"{path}.description", "RESEARCH_FALSIFIER_DESCRIPTION_REQUIRED", issues);
            ValidateStatementReferences(falsifier.CandidateStatementIds, statementIds, $"{path}.candidateStatementIds", issues);
        }
        if (researchCase.UnresolvedItems is null)
            issues.Add(Issue("RESEARCH_UNRESOLVED_ITEMS_REQUIRED", "researchCase.unresolvedItems",
                "Unresolved items must be present, even when the reviewed collection is empty."));
        foreach (var (item, index) in (researchCase.UnresolvedItems ?? []).Select((value, index) => (value, index)))
        {
            var path = $"researchCase.unresolvedItems[{index}]";
            if (item is null)
            {
                issues.Add(Issue("RESEARCH_UNRESOLVED_ENTRY_REQUIRED", path, "Unresolved-item entries cannot be null."));
                continue;
            }
            UniqueRequired(item.ItemId, $"{path}.itemId", "RESEARCH_UNRESOLVED_ID", ids, issues);
            Required(item.Description, $"{path}.description", "RESEARCH_UNRESOLVED_DESCRIPTION_REQUIRED", issues);
            ValidateStatementReferences(item.CandidateStatementIds, statementIds, $"{path}.candidateStatementIds", issues);
            if (item.IsMaterial)
                issues.Add(Issue("RESEARCH_MATERIAL_ITEM_UNRESOLVED", path, "A material research choice remains unresolved."));
        }
        foreach (var (item, index) in (researchCase.ResolvedItems ?? []).Select((value, index) => (value, index)))
        {
            var path = $"researchCase.resolvedItems[{index}]";
            if (item is null)
            {
                issues.Add(Issue("RESEARCH_RESOLVED_ENTRY_REQUIRED", path, "Resolved-item entries cannot be null."));
                continue;
            }
            UniqueRequired(item.ItemId, $"{path}.itemId", "RESEARCH_RESOLVED_ID", ids, issues);
            Required(item.OriginalDescription, $"{path}.originalDescription",
                "RESEARCH_RESOLVED_DESCRIPTION_REQUIRED", issues);
            Required(item.Resolution, $"{path}.resolution", "RESEARCH_RESOLUTION_REQUIRED", issues);
            Required(item.ResolutionProvenance, $"{path}.resolutionProvenance",
                "RESEARCH_RESOLUTION_PROVENANCE_REQUIRED", issues);
            ValidateStatementReferences(item.CandidateStatementIds, statementIds,
                $"{path}.candidateStatementIds", issues);
        }
    }

    private static void ValidateClassification(
        StrategySpec classification,
        ICollection<StrategyIntentIssueV1> issues)
    {
        foreach (var issue in StrategySpecValidator.Validate(classification))
            issues.Add(Issue("INTENT_CLASSIFICATION_INVALID", $"classification.{issue.Path}", issue.Message));
    }

    private static void ValidateDraft(
        StrategyIntentDraftV1 draft,
        StrategyCandidateV1 candidate,
        ResearchCaseV1 researchCase,
        StrategySpec classification,
        ICollection<StrategyIntentIssueV1> issues,
        IStrategyIntentExtensionRegistryV1? extensionRegistry)
    {
        Exact(draft.SchemaVersion, StrategyIntentDraftV1.CurrentSchemaVersion,
            "draft.schemaVersion", "INTENT_DRAFT_SCHEMA_UNSUPPORTED", issues);
        Required(draft.IntentId, "draft.intentId", "INTENT_ID_REQUIRED", issues);
        Exact(draft.CandidateId, candidate.CandidateId,
            "draft.candidateId", "INTENT_CANDIDATE_ID_MISMATCH", issues);
        if (draft.CandidateRevision != candidate.Revision)
            issues.Add(Issue("INTENT_CANDIDATE_REVISION_MISMATCH", "draft.candidateRevision", "The draft is bound to a different candidate revision."));
        Exact(draft.CandidateContentHashSha256, StrategyCandidateCanonicalJsonV1.Hash(candidate),
            "draft.candidateContentHashSha256", "INTENT_CANDIDATE_HASH_MISMATCH", issues);
        Exact(draft.ResearchCaseHashSha256, ResearchCaseCanonicalJsonV1.Hash(researchCase),
            "draft.researchCaseHashSha256", "INTENT_RESEARCH_CASE_HASH_MISMATCH", issues);
        if (draft.Classification is null)
        {
            issues.Add(Issue("INTENT_CLASSIFICATION_BINDING_REQUIRED", "draft.classification", "The classification binding is required."));
        }
        else
        {
            Exact(draft.Classification.StrategySpecId, classification.Id,
                "draft.classification.strategySpecId", "INTENT_CLASSIFICATION_ID_MISMATCH", issues);
            Exact(draft.Classification.StrategySpecHashSha256, StrategySpecCanonicalJsonV1.Hash(classification),
                "draft.classification.strategySpecHashSha256", "INTENT_CLASSIFICATION_HASH_MISMATCH", issues);
        }

        ValidateIntentModel(draft.IntentModel, issues, extensionRegistry);
        Exact(draft.RequirementCatalogVersion, StrategyIntentCompletenessV1.CatalogVersion,
            "draft.requirementCatalogVersion", "INTENT_REQUIREMENT_CATALOG_UNSUPPORTED", issues);
        var classificationIsValid = StrategySpecValidator.Validate(classification).Count == 0;
        if (classificationIsValid)
            ValidateTopologyCompatibility(draft.IntentModel, classification, issues);

        if (draft.Requirements is null || draft.Requirements.Count == 0)
        {
            issues.Add(Issue("INTENT_REQUIREMENTS_REQUIRED", "draft.requirements", "Semantic requirements are required."));
            return;
        }

        var statementIds = CandidateStatementIds(candidate);
        var evidenceIds = new HashSet<string>(
            (researchCase.EvidenceRequirements ?? [])
                .Where(static evidence => evidence is not null)
                .Select(static evidence => evidence.EvidenceId),
            StringComparer.Ordinal);
        var activeRequirementIds = classificationIsValid
            ? new HashSet<string>(
                StrategyIntentCompletenessV1.ActiveRequirementIds(draft.IntentModel, classification),
                StringComparer.Ordinal)
            : null;
        var requirementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (requirement, index) in draft.Requirements.Select((value, index) => (value, index)))
        {
            var path = $"draft.requirements[{index}]";
            if (requirement is null)
            {
                issues.Add(Issue("INTENT_REQUIREMENT_ENTRY_REQUIRED", path, "Requirement entries cannot be null."));
                continue;
            }
            UniqueRequired(requirement.RequirementId, $"{path}.requirementId", "INTENT_REQUIREMENT_ID", requirementIds, issues);
            if (!StrategyIntentCompletenessV1.IsKnownRequirementId(requirement.RequirementId) &&
                !IsGovernedExtensionId(requirement.RequirementId))
            {
                issues.Add(Issue("INTENT_REQUIREMENT_ID_UNGOVERNED", $"{path}.requirementId",
                    "Custom requirements require a namespaced, versioned identifier."));
            }
            else if (!StrategyIntentCompletenessV1.IsKnownRequirementId(requirement.RequirementId) &&
                     extensionRegistry?.Owns(
                         StrategyIntentExtensionRoleV1.Requirement,
                         requirement.RequirementId) != true)
            {
                issues.Add(Issue("INTENT_REQUIREMENT_EXTENSION_UNSUPPORTED", $"{path}.requirementId",
                    "No installed host registry owns this custom requirement schema."));
            }
            else if (StrategyIntentCompletenessV1.IsKnownRequirementId(requirement.RequirementId) &&
                     activeRequirementIds is not null &&
                     !activeRequirementIds.Contains(requirement.RequirementId))
            {
                issues.Add(Issue("INTENT_REQUIREMENT_INACTIVE_FOR_TOPOLOGY", $"{path}.requirementId",
                    "This built-in requirement does not belong to the selected intent topology and classification."));
            }
            if (!Enum.IsDefined(requirement.Stage))
                issues.Add(Issue("INTENT_STAGE_INVALID", $"{path}.stage", "The semantic stage is invalid."));
            if (!Enum.IsDefined(requirement.Disposition))
                issues.Add(Issue("INTENT_DISPOSITION_INVALID", $"{path}.disposition", "The semantic disposition is invalid."));
            Required(requirement.Description, $"{path}.description", "INTENT_REQUIREMENT_DESCRIPTION_REQUIRED", issues);
            if (requirement.Disposition == StrategySemanticDispositionV1.Applicable)
            {
                if (requirement.Value is null)
                {
                    issues.Add(Issue("INTENT_TYPED_VALUE_REQUIRED", $"{path}.value",
                        "Applicable requirements require the concrete reviewed answer."));
                }
                else
                {
                    if (!IsGovernedExtensionId(requirement.Value.TypeId))
                        issues.Add(Issue("INTENT_VALUE_TYPE_ID_INVALID", $"{path}.value.typeId",
                            "Value type identifiers must be namespaced and versioned."));
                    else if (!StrategyIntentValueTypesV1.IsBuiltIn(requirement.Value.TypeId) &&
                             extensionRegistry?.Owns(
                                 StrategyIntentExtensionRoleV1.ValueType,
                                 requirement.Value.TypeId) != true)
                        issues.Add(Issue("INTENT_VALUE_TYPE_EXTENSION_UNSUPPORTED", $"{path}.value.typeId",
                            "No installed host registry owns this custom value schema."));
                    Required(requirement.Value.CanonicalValue, $"{path}.value.canonicalValue",
                        "INTENT_VALUE_REQUIRED", issues);
                }
                if (requirement.DispositionRationale is not null)
                {
                    issues.Add(Issue(
                        "INTENT_APPLICABLE_RATIONALE_UNEXPECTED",
                        $"{path}.dispositionRationale",
                        "Applicable requirements cannot also carry a disposition rationale."));
                }
            }
            if (requirement.Provenance is null)
            {
                issues.Add(Issue("INTENT_PROVENANCE_REQUIRED", $"{path}.provenance", "Requirement provenance is required."));
            }
            else
            {
                Required(requirement.Provenance.Explanation, $"{path}.provenance.explanation", "INTENT_PROVENANCE_EXPLANATION_REQUIRED", issues);
                ValidateStatementReferences(requirement.Provenance.CandidateStatementIds, statementIds,
                    $"{path}.provenance.candidateStatementIds", issues);
                ValidateEvidenceReferences(requirement.Provenance.ResearchEvidenceIds, evidenceIds,
                    $"{path}.provenance.researchEvidenceIds", issues);
                if (requirement.IsMaterial &&
                    (requirement.Provenance.CandidateStatementIds?.Count ?? 0) == 0)
                {
                    issues.Add(Issue("INTENT_MATERIAL_PROVENANCE_REQUIRED", $"{path}.provenance.candidateStatementIds",
                        "A material requirement must trace to at least one confirmed candidate statement."));
                }
            }

            if (requirement.Disposition is StrategySemanticDispositionV1.NotApplicable or
                StrategySemanticDispositionV1.Unresolved or StrategySemanticDispositionV1.Unsupported)
            {
                if (requirement.Value is not null)
                {
                    issues.Add(Issue(
                        "INTENT_DISPOSITION_VALUE_UNEXPECTED",
                        $"{path}.value",
                        "Only Applicable requirements may carry a concrete value."));
                }
                Required(requirement.DispositionRationale, $"{path}.dispositionRationale",
                    "INTENT_DISPOSITION_RATIONALE_REQUIRED", issues);
            }
            if (requirement.IsMaterial && requirement.Disposition == StrategySemanticDispositionV1.Unresolved)
                issues.Add(Issue("INTENT_MATERIAL_REQUIREMENT_UNRESOLVED", path, "A material semantic requirement remains unresolved."));
            if (requirement.IsMaterial && requirement.Disposition == StrategySemanticDispositionV1.Unsupported)
                issues.Add(Issue("INTENT_MATERIAL_REQUIREMENT_UNSUPPORTED", path, "A material semantic requirement is unsupported."));
        }
    }

    private static void ValidateIntentModel(
        StrategyIntentModelV1? model,
        ICollection<StrategyIntentIssueV1> issues,
        IStrategyIntentExtensionRegistryV1? extensionRegistry)
    {
        if (model is null)
        {
            issues.Add(Issue("INTENT_MODEL_REQUIRED", "draft.intentModel", "An intent model is required."));
            return;
        }
        if (!Enum.IsDefined(model.Kind))
            issues.Add(Issue("INTENT_MODEL_KIND_INVALID", "draft.intentModel.kind", "The intent-model kind is invalid."));
        if (model.Kind == StrategyIntentKindV1.Extension)
        {
            if (!IsGovernedExtensionId(model.ExtensionId))
                issues.Add(Issue("INTENT_EXTENSION_ID_INVALID", "draft.intentModel.extensionId",
                    "An extension requires a namespaced, versioned id such as domain.intent@1."));
            else if (extensionRegistry?.Owns(
                         StrategyIntentExtensionRoleV1.IntentModel,
                         model.ExtensionId!) != true)
                issues.Add(Issue("INTENT_EXTENSION_UNSUPPORTED", "draft.intentModel.extensionId",
                    "No installed host registry owns this intent extension schema."));
        }
        else if (model.ExtensionId is not null)
        {
            issues.Add(Issue("INTENT_EXTENSION_ID_UNEXPECTED", "draft.intentModel.extensionId",
                "Built-in intent models cannot carry an extension id."));
        }
    }

    private static void ValidateTopologyCompatibility(
        StrategyIntentModelV1? model,
        StrategySpec classification,
        ICollection<StrategyIntentIssueV1> issues)
    {
        if (model is null) return;
        var topology = classification.Context.Topology;
        var executionNotApplicable = classification.Execution.Policies is [StrategyExecutionPolicyKind.NotApplicable];
        var riskNotApplicable = classification.Risk.Rules is [StrategyRiskExitKind.NotApplicable];
        var portfolioNotApplicable = classification.Portfolio.Construction == PortfolioConstructionKind.NotApplicable;

        // Compatibility is deliberately bidirectional. The checks below this block prove that a
        // selected intent kind has the axes it needs; this check also prevents an already-specialized
        // classification from being weakened to PositionTarget (and thereby receiving the wrong
        // completeness interview). Extension kinds are owned by their host registry and cannot be
        // judged against the built-in topology table here.
        if (model.Kind != StrategyIntentKindV1.Extension)
            ValidateClassificationRequiredIntentKind(model.Kind, classification, issues);

        if (model.Kind == StrategyIntentKindV1.SignalOnly)
        {
            if (!executionNotApplicable || !riskNotApplicable || !portfolioNotApplicable)
            {
                issues.Add(Issue("INTENT_SIGNAL_ONLY_CLASSIFICATION_MISMATCH", "draft.intentModel.kind",
                    "Signal-only requires NotApplicable portfolio construction, execution policy, and risk/exit classification."));
            }
        }
        else if (model.Kind != StrategyIntentKindV1.Extension &&
                 (executionNotApplicable || riskNotApplicable || portfolioNotApplicable))
        {
            issues.Add(Issue("INTENT_EXECUTABLE_CLASSIFICATION_NOT_APPLICABLE", "classification",
                "An intent that owns exposure or execution cannot use NotApplicable portfolio, execution, or risk axes."));
        }

        if (model.Kind == StrategyIntentKindV1.MultiLegTarget &&
            topology is not (MarketTopologyKind.Pair or MarketTopologyKind.Basket or
                MarketTopologyKind.UnderlyingAndDerivative or MarketTopologyKind.MultiLeg or
                MarketTopologyKind.CrossAsset or MarketTopologyKind.MultiVenue))
        {
            issues.Add(Issue("INTENT_MULTI_LEG_TOPOLOGY_MISMATCH", "draft.intentModel.kind",
                "A multi-leg target requires a multi-instrument classification topology."));
        }
        if (model.Kind == StrategyIntentKindV1.MultiLegTarget &&
            !classification.Execution.Policies.Contains(StrategyExecutionPolicyKind.CoordinatedLegs))
        {
            issues.Add(Issue("INTENT_MULTI_LEG_EXECUTION_MISMATCH", "classification.execution.policies",
                "A multi-leg target requires coordinated-leg execution classification."));
        }
        if (model.Kind == StrategyIntentKindV1.PortfolioTarget &&
            topology is not (MarketTopologyKind.CrossSection or MarketTopologyKind.Basket or
                MarketTopologyKind.CrossAsset or MarketTopologyKind.MultiVenue))
        {
            issues.Add(Issue("INTENT_PORTFOLIO_TOPOLOGY_MISMATCH", "draft.intentModel.kind",
                "A portfolio target requires a cross-sectional, basket, cross-asset, or multi-venue topology."));
        }
        if (model.Kind == StrategyIntentKindV1.QuoteSet &&
            (classification.Objective != StrategyObjectiveKind.LiquidityProvision ||
             !classification.Execution.Policies.Contains(StrategyExecutionPolicyKind.ContinuousQuoting) ||
             classification.Portfolio.Construction != PortfolioConstructionKind.InventoryTarget ||
             !classification.State.Policies.Contains(StrategyStateKind.InventoryAware)))
        {
            issues.Add(Issue("INTENT_QUOTE_CLASSIFICATION_MISMATCH", "draft.intentModel.kind",
                "A quote set requires liquidity provision, continuous quoting, inventory targeting, and inventory-aware state."));
        }
        if (model.Kind == StrategyIntentKindV1.ExecutionSchedule &&
            (classification.Objective != StrategyObjectiveKind.Execution ||
             !classification.Execution.Policies.Any(static policy => policy is
                StrategyExecutionPolicyKind.Twap or StrategyExecutionPolicyKind.Vwap or
                StrategyExecutionPolicyKind.Pov or StrategyExecutionPolicyKind.SmartRouting)))
        {
            issues.Add(Issue("INTENT_SCHEDULE_CLASSIFICATION_MISMATCH", "draft.intentModel.kind",
                "An execution schedule requires an execution objective and an installed schedule or routing policy."));
        }
    }

    private static void ValidateClassificationRequiredIntentKind(
        StrategyIntentKindV1 actualKind,
        StrategySpec classification,
        ICollection<StrategyIntentIssueV1> issues)
    {
        StrategyIntentKindV1[] requiredKinds;
        string code;
        string reason;

        var executionNotApplicable = classification.Execution.Policies is [StrategyExecutionPolicyKind.NotApplicable];
        var riskNotApplicable = classification.Risk.Rules is [StrategyRiskExitKind.NotApplicable];
        var portfolioNotApplicable = classification.Portfolio.Construction == PortfolioConstructionKind.NotApplicable;
        if (classification.Objective == StrategyObjectiveKind.LiquidityProvision ||
                 classification.Execution.Policies.Contains(StrategyExecutionPolicyKind.ContinuousQuoting))
        {
            requiredKinds = [StrategyIntentKindV1.QuoteSet];
            code = "INTENT_CLASSIFICATION_REQUIRES_QUOTE_SET";
            reason = "Liquidity provision or continuous quoting requires bid/ask quote-set intent";
        }
        else if (classification.Objective == StrategyObjectiveKind.Execution)
        {
            requiredKinds = [StrategyIntentKindV1.ExecutionSchedule];
            code = "INTENT_CLASSIFICATION_REQUIRES_EXECUTION_SCHEDULE";
            reason = "An execution objective requires parent-order schedule intent";
        }
        else if (classification.Context.Topology is MarketTopologyKind.Pair or
                     MarketTopologyKind.MultiLeg or MarketTopologyKind.UnderlyingAndDerivative ||
                 classification.Context.Exposure is ExposureGeometryKind.Spread or ExposureGeometryKind.Arbitrage ||
                 classification.Execution.Policies.Contains(StrategyExecutionPolicyKind.CoordinatedLegs))
        {
            requiredKinds = [StrategyIntentKindV1.MultiLegTarget];
            code = "INTENT_CLASSIFICATION_REQUIRES_MULTI_LEG_TARGET";
            reason = "Pair, multi-leg, derivative-leg, spread/arbitrage, or coordinated-leg semantics require multi-leg target intent";
        }
        else if (classification.Context.Topology == MarketTopologyKind.Basket)
        {
            requiredKinds = [StrategyIntentKindV1.MultiLegTarget, StrategyIntentKindV1.PortfolioTarget];
            code = "INTENT_CLASSIFICATION_REQUIRES_BASKET_TARGET";
            reason = "A basket classification requires either coordinated multi-leg targets or complete portfolio targets";
        }
        else if (classification.Objective is StrategyObjectiveKind.Allocation or StrategyObjectiveKind.BenchmarkTracking ||
                 classification.Context.Topology == MarketTopologyKind.CrossSection ||
                 classification.Context.Exposure == ExposureGeometryKind.CrossSectionalLongShort)
        {
            requiredKinds = [StrategyIntentKindV1.PortfolioTarget];
            code = "INTENT_CLASSIFICATION_REQUIRES_PORTFOLIO_TARGET";
            reason = "Allocation, benchmark, cross-sectional, or cross-sectional exposure semantics require portfolio target intent";
        }
        else if (executionNotApplicable && riskNotApplicable && portfolioNotApplicable)
        {
            requiredKinds = [StrategyIntentKindV1.SignalOnly];
            code = "INTENT_CLASSIFICATION_REQUIRES_SIGNAL_ONLY";
            reason = "NotApplicable portfolio construction, execution, and risk/exit axes define a signal-only contract";
        }
        else
        {
            return;
        }

        if (requiredKinds.Contains(actualKind))
            return;

        issues.Add(Issue(code, "draft.intentModel.kind",
            $"{reason}; received {actualKind}. Required: {string.Join(" or ", requiredKinds)}."));
    }

    private static HashSet<string> CandidateStatementIds(StrategyCandidateV1 candidate)
    {
        var statementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in candidate.Groups ?? [])
        {
            if (group is null) continue;
            foreach (var flattened in Flatten(group))
            foreach (var statement in flattened.Statements ?? [])
            {
                if (statement is not null &&
                    statement.State is StrategyCandidateStatementStateV1.Confirmed or
                        StrategyCandidateStatementStateV1.Resolved)
                    statementIds.Add(statement.StatementId);
            }
        }

        return statementIds;
    }

    private static IEnumerable<StrategyCandidateGroupV1> Flatten(StrategyCandidateGroupV1 group)
    {
        yield return group;
        foreach (var child in group.Children ?? [])
        {
            if (child is null) continue;
        foreach (var nested in Flatten(child))
            yield return nested;
        }
    }

    private static void ValidateStatementReferences(
        IReadOnlyList<string>? references,
        IReadOnlySet<string> known,
        string path,
        ICollection<StrategyIntentIssueV1> issues)
    {
        if (references is null)
        {
            issues.Add(Issue("INTENT_STATEMENT_REFERENCES_REQUIRED", path, "Candidate statement references must be present, even when empty."));
            return;
        }
        foreach (var reference in references)
            if (!known.Contains(reference))
                issues.Add(Issue("INTENT_STATEMENT_REFERENCE_UNKNOWN", path, $"Candidate statement '{reference}' does not exist."));
    }

    private static void ValidateEvidenceReferences(
        IReadOnlyList<string>? references,
        IReadOnlySet<string> known,
        string path,
        ICollection<StrategyIntentIssueV1> issues)
    {
        if (references is null)
        {
            issues.Add(Issue("INTENT_EVIDENCE_REFERENCES_REQUIRED", path, "Research evidence references must be present, even when empty."));
            return;
        }
        foreach (var reference in references)
            if (!known.Contains(reference))
                issues.Add(Issue("INTENT_EVIDENCE_REFERENCE_UNKNOWN", path, $"Research evidence '{reference}' does not exist."));
    }

    private static void Required(
        string? value,
        string path,
        string code,
        ICollection<StrategyIntentIssueV1> issues)
    {
        if (string.IsNullOrWhiteSpace(value)) issues.Add(Issue(code, path, "A non-empty value is required."));
    }

    private static void Exact(
        string? actual,
        string? expected,
        string path,
        string code,
        ICollection<StrategyIntentIssueV1> issues)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            issues.Add(Issue(code, path, "The value does not match the exact host-owned binding."));
    }

    private static void UniqueRequired(
        string? value,
        string path,
        string codePrefix,
        ISet<string> ids,
        ICollection<StrategyIntentIssueV1> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(Issue($"{codePrefix}_REQUIRED", path, "A non-empty id is required."));
            return;
        }
        if (!ids.Add(value)) issues.Add(Issue($"{codePrefix}_DUPLICATE", path, $"Id '{value}' is duplicated."));
    }

    private static bool IsGovernedExtensionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var at = value.LastIndexOf('@');
        return at > 0 && at < value.Length - 1 && value[..at].Contains('.') &&
               int.TryParse(value[(at + 1)..], out var version) && version > 0;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static StrategyIntentIssueV1 Issue(string code, string path, string message) =>
        new(code, path, message);

    private static StrategyIntentConfirmationResultV1 Failed(IReadOnlyCollection<StrategyIntentIssueV1> issues) =>
        new(null, [], issues.ToArray());
}

/// <summary>Deterministic question projection from intent topology and StrategySpec axes.</summary>
public static class StrategyIntentCompletenessV1
{
    public const string CatalogVersion = "strategy-intent-requirements/v1";

    private static readonly IReadOnlySet<string> KnownRequirementIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "observe.trigger", "observe.suspected_move", "data.primary_instrument", "data.timeframe",
        "evidence.qualification", "evidence.primary_threshold", "evidence.point_in_time",
        "evidence.cross_instrument", "evidence.comparison_inputs", "evidence.data_freshness",
        "evidence.conflict_no_trade",
        "decision.intent", "decision.direction_set", "decision.long_condition",
        "decision.short_condition", "decision.no_trade_condition", "decision.reversal_condition",
        "decision.position_actions", "decision.validity_window",
        "decision.coordinated_legs", "decision.portfolio_targets", "decision.two_sided_quotes",
        "decision.parent_order", "decision.signal_publication", "decision.option_structure",
        "exposure.position_target", "exposure.sizing_formula", "exposure.maximum_position",
        "exposure.leverage_limit", "exposure.risk_budget", "exposure.early_tranche",
        "exposure.later_tranches", "exposure.leg_targets", "exposure.portfolio_constraints",
        "exposure.turnover_limit",
        "exposure.inventory_limits", "exposure.parent_quantity", "exposure.not_applicable",
        "exposure.hedge_target", "exposure.greeks", "exposure.extension",
        "execution.order_policy", "execution.order_type_selection", "execution.market_policy",
        "execution.limit_policy", "execution.time_in_force", "execution.partial_fill_policy",
        "execution.leg_coordination", "execution.rebalance_policy", "execution.cancel_replace",
        "execution.slice_route", "execution.not_applicable", "execution.extension",
        "lifecycle.position", "lifecycle.fill_driven_state", "lifecycle.scale_policy",
        "lifecycle.leg_rebalance", "lifecycle.rebalance_state", "lifecycle.quote_inventory",
        "lifecycle.quote_refresh",
        "lifecycle.schedule_control", "lifecycle.publication", "lifecycle.hedge_rebalance",
        "lifecycle.fill_handling_not_applicable", "lifecycle.extension",
        "finish.position", "finish.stop", "finish.target", "finish.trailing_stop",
        "finish.time_exit", "finish.reversal",
        "finish.invalidation", "finish.session_boundary", "finish.multi_leg_unwind",
        "finish.portfolio_deactivation", "finish.cancel_all_quotes", "finish.residual_policy",
        "finish.not_applicable", "finish.option_lifecycle", "finish.extension",
    };

    public static bool IsKnownRequirementId(string? requirementId) =>
        requirementId is not null && KnownRequirementIds.Contains(requirementId);

    public static IReadOnlyList<string> ActiveRequirementIds(
        StrategyIntentModelV1? model,
        StrategySpec classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        return Templates(model, classification)
            .Select(static template => template.RequirementId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static requirementId => requirementId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Derives the interview family from the decision topology first, then from the orthogonal
    /// StrategySpec axes. The family is a question profile only: it is not persisted as a second
    /// strategy classification and cannot override the confirmed intent kind.
    /// </summary>
    public static StrategyIntentFamilyV1 ClassifyFamily(
        StrategyIntentModelV1? model,
        StrategySpec classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        var hasOptionSemantics = classification.Context.AssetClasses.Contains(AssetClass.Option) ||
                                 classification.Context.Topology == MarketTopologyKind.UnderlyingAndDerivative ||
                                 classification.Context.Information.Contains(StrategyInformationKind.ImpliedVolatilitySurface);

        return model?.Kind switch
        {
            StrategyIntentKindV1.QuoteSet => StrategyIntentFamilyV1.MarketMaking,
            StrategyIntentKindV1.ExecutionSchedule => StrategyIntentFamilyV1.ExecutionAlgorithm,
            StrategyIntentKindV1.SignalOnly => StrategyIntentFamilyV1.SignalPublication,
            StrategyIntentKindV1.PortfolioTarget when classification.Objective == StrategyObjectiveKind.Hedging =>
                StrategyIntentFamilyV1.Hedging,
            StrategyIntentKindV1.PortfolioTarget => StrategyIntentFamilyV1.PortfolioOrRebalance,
            StrategyIntentKindV1.MultiLegTarget when classification.Objective == StrategyObjectiveKind.Hedging =>
                StrategyIntentFamilyV1.Hedging,
            StrategyIntentKindV1.MultiLegTarget when hasOptionSemantics =>
                StrategyIntentFamilyV1.OptionsOrVolatility,
            StrategyIntentKindV1.MultiLegTarget => StrategyIntentFamilyV1.PairsOrArbitrage,
            StrategyIntentKindV1.PositionTarget when classification.Objective == StrategyObjectiveKind.Hedging =>
                StrategyIntentFamilyV1.Hedging,
            StrategyIntentKindV1.PositionTarget when hasOptionSemantics =>
                StrategyIntentFamilyV1.OptionsOrVolatility,
            StrategyIntentKindV1.PositionTarget => StrategyIntentFamilyV1.Directional,
            _ => StrategyIntentFamilyV1.GovernedExtension,
        };
    }

    public static IReadOnlyList<StrategyIntentQuestionV1> Questions(
        StrategyIntentDraftV1 draft,
        StrategySpec classification)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(classification);
        var requirements = (draft.Requirements ?? [])
            .Where(static requirement => requirement is not null &&
                                         !string.IsNullOrWhiteSpace(requirement.RequirementId))
            .GroupBy(static requirement => requirement.RequirementId,
                StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var questions = new List<StrategyIntentQuestionV1>();
        foreach (var template in Templates(draft.IntentModel, classification)
                     .GroupBy(static template => template.RequirementId, StringComparer.Ordinal)
                     .Select(static group => group.First()))
        {
            if (!requirements.TryGetValue(template.RequirementId, out var requirement))
            {
                questions.Add(Question(template, "Required semantic coverage is missing."));
                continue;
            }
            if (requirement.Stage != template.Stage)
            {
                questions.Add(Question(template, "The requirement is assigned to the wrong lifecycle stage."));
                continue;
            }
            if (!requirement.IsMaterial)
            {
                questions.Add(Question(template, "This required strategy decision must be reviewed as material."));
                continue;
            }
            if (requirement.Disposition is StrategySemanticDispositionV1.Unresolved or
                StrategySemanticDispositionV1.Unsupported)
            {
                questions.Add(Question(template, $"The requirement is {requirement.Disposition.ToString().ToLowerInvariant()}."));
                continue;
            }
            if (template.MustBeNotApplicable && requirement.Disposition != StrategySemanticDispositionV1.NotApplicable)
            {
                questions.Add(Question(template, "This topology must explicitly mark the requirement not applicable."));
                continue;
            }
            if (!template.AllowsNotApplicable && requirement.Disposition == StrategySemanticDispositionV1.NotApplicable)
            {
                questions.Add(Question(template, "This topology requires an applicable answer here."));
                continue;
            }
            if (requirement.Disposition == StrategySemanticDispositionV1.Applicable &&
                (requirement.Value is null || string.IsNullOrWhiteSpace(requirement.Value.CanonicalValue)))
                questions.Add(Question(template, "The concrete reviewed answer is missing."));
        }
        return questions.OrderBy(static question => question.Stage)
            .ThenBy(static question => question.RequirementId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<RequirementTemplate> Templates(
        StrategyIntentModelV1? model,
        StrategySpec classification)
    {
        var family = ClassifyFamily(model, classification);
        yield return T("observe.trigger", StrategySemanticStageV1.ObserveOrTrigger,
            "What observation, schedule, event, or parent-order arrival activates evaluation?");
        yield return T("evidence.qualification", StrategySemanticStageV1.QualifyEvidence,
            "What point-in-time evidence qualifies the decision, and what produces no action?",
            allowsNotApplicable: model?.Kind == StrategyIntentKindV1.ExecutionSchedule);
        yield return T("decision.intent", StrategySemanticStageV1.DecideIntent,
            "What exact decision intent is emitted, including the no-action state?");

        foreach (var template in IntentTemplates(model?.Kind ?? StrategyIntentKindV1.Extension))
            yield return template;

        if (family == StrategyIntentFamilyV1.Directional)
        {
            yield return T("data.primary_instrument", StrategySemanticStageV1.ObserveOrTrigger,
                "Which primary instrument or eligible universe is being evaluated?");
            yield return T("data.timeframe", StrategySemanticStageV1.ObserveOrTrigger,
                "Which bar, quote, event, and decision timeframes drive evaluation?");
            yield return T("observe.suspected_move", StrategySemanticStageV1.ObserveOrTrigger,
                "Is a suspected jump, breakdown, overheat, or similar event part of activation?",
                allowsNotApplicable: true);
            yield return T("evidence.primary_threshold", StrategySemanticStageV1.QualifyEvidence,
                "Which exact primary thresholds distinguish a qualified move from noise?");
            yield return T("evidence.point_in_time", StrategySemanticStageV1.QualifyEvidence,
                "What timestamp, completion, alignment, and staleness rules make every input causal?");
            yield return T("evidence.data_freshness", StrategySemanticStageV1.QualifyEvidence,
                "What maximum age is allowed for every required market and comparison input?");
            yield return T("evidence.conflict_no_trade", StrategySemanticStageV1.QualifyEvidence,
                "What conflicting or missing evidence forces no trade?");
            yield return T("decision.long_condition", StrategySemanticStageV1.DecideIntent,
                "What exact qualified evidence produces a long target?");
            yield return T("decision.short_condition", StrategySemanticStageV1.DecideIntent,
                "What exact qualified evidence produces a short target?");
            yield return T("decision.no_trade_condition", StrategySemanticStageV1.DecideIntent,
                "What exact conditions produce no trade or a flat target?");
            yield return T("decision.reversal_condition", StrategySemanticStageV1.DecideIntent,
                "When may an existing position reverse, and what fresh confirmation is required?");
            yield return T("decision.position_actions", StrategySemanticStageV1.DecideIntent,
                "When are flat, reduce, exit, and reverse actions permitted?");
            yield return T("decision.validity_window", StrategySemanticStageV1.DecideIntent,
                "How long is a qualified decision valid before it must be recomputed?");
            yield return T("exposure.sizing_formula", StrategySemanticStageV1.SizeOrExposure,
                "What exact formula converts account state, price, and risk into total target size?");
            yield return T("exposure.maximum_position", StrategySemanticStageV1.SizeOrExposure,
                "What hard position, notional, and portfolio-concentration limits cap total size?");
            yield return T("exposure.leverage_limit", StrategySemanticStageV1.SizeOrExposure,
                "What gross and net leverage limits apply?");
            yield return T("exposure.early_tranche", StrategySemanticStageV1.SizeOrExposure,
                "Is an early tranche permitted, and what fraction and conditions govern it?",
                allowsNotApplicable: true);
            yield return T("exposure.later_tranches", StrategySemanticStageV1.SizeOrExposure,
                "Are later confirmation tranches permitted, with what cumulative cap?",
                allowsNotApplicable: true);
            yield return T("lifecycle.fill_driven_state", StrategySemanticStageV1.ManageLifecycle,
                "How do fills, partial fills, and current exposure—not emitted orders—drive state?");
            yield return T("lifecycle.scale_policy", StrategySemanticStageV1.ManageLifecycle,
                "What scale-in and scale-out transitions are permitted without duplicating exposure?",
                allowsNotApplicable: true);
            yield return T("execution.order_type_selection", StrategySemanticStageV1.Execution,
                "What conditions select market versus limit behavior?");
            yield return T("execution.time_in_force", StrategySemanticStageV1.Execution,
                "What time-in-force, expiry, and cancel behavior applies?",
                allowsNotApplicable: true);
            yield return T("execution.partial_fill_policy", StrategySemanticStageV1.Execution,
                "What happens to residual quantity after partial fills?",
                allowsNotApplicable: true);
            yield return T("execution.cancel_replace", StrategySemanticStageV1.Execution,
                "When are unfilled or stale orders canceled, replaced, repriced, or abandoned?",
                allowsNotApplicable: true);
            yield return T("finish.invalidation", StrategySemanticStageV1.FinishOrUnwind,
                "What invalidates the thesis independently of stop and target mechanics?");
            yield return T("finish.session_boundary", StrategySemanticStageV1.FinishOrUnwind,
                "What end-of-session or market-boundary behavior applies?",
                allowsNotApplicable: true);
        }
        if (classification.Context.Topology is MarketTopologyKind.CrossSection or MarketTopologyKind.Pair or
            MarketTopologyKind.Basket or MarketTopologyKind.CrossAsset or MarketTopologyKind.MultiVenue or
            MarketTopologyKind.UnderlyingAndDerivative or MarketTopologyKind.MultiLeg)
        {
            yield return T("evidence.comparison_inputs", StrategySemanticStageV1.QualifyEvidence,
                "Which comparison instruments, indices, legs, or universe members are required?");
            yield return T("evidence.cross_instrument", StrategySemanticStageV1.QualifyEvidence,
                "Which corroborating instruments or indices are observed, with what point-in-time thresholds?");
        }
        if (classification.Portfolio.Construction == PortfolioConstructionKind.RiskBudget)
            yield return T("exposure.risk_budget", StrategySemanticStageV1.SizeOrExposure,
                "What risk budget determines total exposure and its limits?");
        if (classification.Execution.Policies.Contains(StrategyExecutionPolicyKind.Market))
            yield return T("execution.market_policy", StrategySemanticStageV1.Execution,
                "When may a market order be used, and what slippage or protection bound applies?");
        if (classification.Execution.Policies.Contains(StrategyExecutionPolicyKind.Limit))
            yield return T("execution.limit_policy", StrategySemanticStageV1.Execution,
                "How is the limit price selected, timed, expired, and handled after partial fills?");
        if (classification.Risk.Rules.Contains(StrategyRiskExitKind.StopLoss))
            yield return T("finish.stop", StrategySemanticStageV1.FinishOrUnwind, "What exact stop condition reduces or exits exposure?");
        if (classification.Risk.Rules.Contains(StrategyRiskExitKind.TakeProfit))
            yield return T("finish.target", StrategySemanticStageV1.FinishOrUnwind, "What exact target condition realizes gains?");
        if (classification.Risk.Rules.Contains(StrategyRiskExitKind.TrailingStop))
            yield return T("finish.trailing_stop", StrategySemanticStageV1.FinishOrUnwind,
                "When does trailing protection activate, how is it updated, and what exits it triggers?");
        if (classification.Risk.Rules.Contains(StrategyRiskExitKind.TimeExit))
            yield return T("finish.time_exit", StrategySemanticStageV1.FinishOrUnwind, "What time or session condition forces completion?");
        if (classification.Risk.Rules.Contains(StrategyRiskExitKind.SignalReversal))
            yield return T("finish.reversal", StrategySemanticStageV1.FinishOrUnwind, "How does reversal unwind and establish the opposite target?");
        if (classification.Objective == StrategyObjectiveKind.Hedging)
        {
            yield return T("exposure.hedge_target", StrategySemanticStageV1.SizeOrExposure,
                "What risk or exposure target determines the hedge ratio?");
            yield return T("lifecycle.hedge_rebalance", StrategySemanticStageV1.ManageLifecycle,
                "When is the hedge added, resized, or removed?");
        }
        if (classification.Context.AssetClasses.Contains(AssetClass.Option) ||
            classification.Context.Topology == MarketTopologyKind.UnderlyingAndDerivative ||
            classification.Context.Information.Contains(StrategyInformationKind.ImpliedVolatilitySurface))
        {
            yield return T("decision.option_structure", StrategySemanticStageV1.DecideIntent,
                "Which option legs, expiries, strikes, and structure constraints define the intent?");
            yield return T("exposure.greeks", StrategySemanticStageV1.SizeOrExposure,
                "Which Greek targets and caps determine exposure?");
            yield return T("finish.option_lifecycle", StrategySemanticStageV1.FinishOrUnwind,
                "How are expiry, exercise, assignment, and multi-leg unwind handled?");
        }
    }

    private static IEnumerable<RequirementTemplate> IntentTemplates(StrategyIntentKindV1 kind) => kind switch
    {
        StrategyIntentKindV1.PositionTarget =>
        [
            T("exposure.position_target", StrategySemanticStageV1.SizeOrExposure, "How is target or delta position size determined?"),
            T("execution.order_policy", StrategySemanticStageV1.Execution, "How does the position target become order intent?"),
            T("lifecycle.position", StrategySemanticStageV1.ManageLifecycle, "How is the position entered, resized, scaled, and managed?"),
            T("finish.position", StrategySemanticStageV1.FinishOrUnwind, "How is the position invalidated, exited, or reversed?"),
        ],
        StrategyIntentKindV1.MultiLegTarget =>
        [
            T("decision.coordinated_legs", StrategySemanticStageV1.DecideIntent, "What complete set of coordinated legs defines the decision?"),
            T("exposure.leg_targets", StrategySemanticStageV1.SizeOrExposure, "What target, ratio, and exposure applies to every leg?"),
            T("execution.leg_coordination", StrategySemanticStageV1.Execution, "How are legging risk, ordering, and partial fills coordinated?"),
            T("lifecycle.leg_rebalance", StrategySemanticStageV1.ManageLifecycle, "When and how are all legs rebalanced?"),
            T("finish.multi_leg_unwind", StrategySemanticStageV1.FinishOrUnwind, "How are all legs unwound or completed together?"),
        ],
        StrategyIntentKindV1.PortfolioTarget =>
        [
            T("decision.portfolio_targets", StrategySemanticStageV1.DecideIntent, "What complete target weights or exposures are produced?"),
            T("exposure.portfolio_constraints", StrategySemanticStageV1.SizeOrExposure, "What portfolio-wide constraints govern targets?"),
            T("exposure.turnover_limit", StrategySemanticStageV1.SizeOrExposure, "What turnover, transaction-cost, and capacity limits constrain a rebalance?"),
            T("execution.rebalance_policy", StrategySemanticStageV1.Execution, "How are current holdings transformed into rebalance orders?"),
            T("lifecycle.rebalance_state", StrategySemanticStageV1.ManageLifecycle, "What state and cadence govern subsequent rebalances?"),
            T("finish.portfolio_deactivation", StrategySemanticStageV1.FinishOrUnwind, "What deactivates or unwinds the portfolio policy?", allowsNotApplicable: true),
        ],
        StrategyIntentKindV1.QuoteSet =>
        [
            T("decision.two_sided_quotes", StrategySemanticStageV1.DecideIntent, "How are bid and ask quote intents formed?"),
            T("exposure.inventory_limits", StrategySemanticStageV1.SizeOrExposure, "What inventory targets and hard limits constrain quoting?"),
            T("execution.cancel_replace", StrategySemanticStageV1.Execution, "What exact cancel/replace behavior keeps quotes current?"),
            T("lifecycle.quote_inventory", StrategySemanticStageV1.ManageLifecycle, "How do fills and inventory change subsequent quotes?"),
            T("lifecycle.quote_refresh", StrategySemanticStageV1.ManageLifecycle, "What price, age, market, and inventory events refresh the quote set?"),
            T("finish.cancel_all_quotes", StrategySemanticStageV1.FinishOrUnwind, "When are all quotes canceled and inventory neutralized or handed off?"),
        ],
        StrategyIntentKindV1.ExecutionSchedule =>
        [
            T("decision.parent_order", StrategySemanticStageV1.DecideIntent, "What parent-order constraints define the schedule?"),
            T("exposure.parent_quantity", StrategySemanticStageV1.SizeOrExposure, "What parent quantity or notional remains to execute?"),
            T("execution.slice_route", StrategySemanticStageV1.Execution, "How are slices timed, sized, priced, and routed?"),
            T("lifecycle.schedule_control", StrategySemanticStageV1.ManageLifecycle, "When does the schedule pause, resume, or adapt?"),
            T("finish.residual_policy", StrategySemanticStageV1.FinishOrUnwind, "How are completion, deadline, cancellation, and residual quantity handled?"),
        ],
        StrategyIntentKindV1.SignalOnly =>
        [
            T("decision.signal_publication", StrategySemanticStageV1.DecideIntent, "What signal value, confidence, audience, and expiry are published?"),
            T("exposure.not_applicable", StrategySemanticStageV1.SizeOrExposure, "Confirm that sizing and exposure are outside this signal-only contract.", true, true),
            T("execution.not_applicable", StrategySemanticStageV1.Execution, "Confirm that order type and execution are outside this signal-only contract.", true, true),
            T("lifecycle.fill_handling_not_applicable", StrategySemanticStageV1.ManageLifecycle, "Confirm that order-fill handling is outside this signal-only contract.", true, true),
            T("lifecycle.publication", StrategySemanticStageV1.ManageLifecycle, "How is the signal published, updated, expired, or withdrawn?"),
            T("finish.not_applicable", StrategySemanticStageV1.FinishOrUnwind, "Confirm that position unwind is outside this signal-only contract.", true, true),
        ],
        _ =>
        [
            T("exposure.extension", StrategySemanticStageV1.SizeOrExposure, "How does the governed extension represent size or exposure?", true),
            T("execution.extension", StrategySemanticStageV1.Execution, "How does the governed extension represent execution applicability?", true),
            T("lifecycle.extension", StrategySemanticStageV1.ManageLifecycle, "How does the governed extension manage lifecycle state?", true),
            T("finish.extension", StrategySemanticStageV1.FinishOrUnwind, "How does the governed extension finish or unwind?", true),
        ],
    };

    private static RequirementTemplate T(
        string id,
        StrategySemanticStageV1 stage,
        string prompt,
        bool allowsNotApplicable = false,
        bool mustBeNotApplicable = false) =>
        new(id, stage, prompt, allowsNotApplicable, mustBeNotApplicable);

    private static StrategyIntentQuestionV1 Question(RequirementTemplate template, string reason) =>
        new($"question/{template.RequirementId}", template.RequirementId, template.Stage, template.Prompt, reason);

    private sealed record RequirementTemplate(
        string RequirementId,
        StrategySemanticStageV1 Stage,
        string Prompt,
        bool AllowsNotApplicable,
        bool MustBeNotApplicable);
}
