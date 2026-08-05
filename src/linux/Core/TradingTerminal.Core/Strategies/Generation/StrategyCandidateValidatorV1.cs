namespace TradingTerminal.Core.Strategies.Generation;

public enum StrategyCandidateIssueScopeV1
{
    Structure,
    Confirmation,
    BuildSupport,
}

public sealed record StrategyCandidateIssueV1(
    StrategyCandidateIssueScopeV1 Scope,
    string Code,
    string Path,
    string Message);

/// <summary>
/// Separates a well-formed draft from a confirmable meaning and an executable-ready candidate. This
/// allows "understood, but needs a triangle detector" to be represented honestly rather than treated
/// as either a parser failure or permission to generate arbitrary source.
/// </summary>
public sealed record StrategyCandidateAssessmentV1(
    StrategyCandidateStatusV1 Status,
    IReadOnlyList<StrategyCandidateIssueV1> Issues)
{
    public bool IsStructurallyValid => Issues.All(static issue => issue.Scope != StrategyCandidateIssueScopeV1.Structure);

    public bool CanConfirm => IsStructurallyValid &&
        Issues.All(static issue => issue.Scope != StrategyCandidateIssueScopeV1.Confirmation);

    public bool CanLower => Status == StrategyCandidateStatusV1.Confirmed && CanConfirm &&
        Issues.All(static issue => issue.Scope != StrategyCandidateIssueScopeV1.BuildSupport);
}

public static class StrategyCandidateValidatorV1
{
    public static StrategyCandidateAssessmentV1 Assess(StrategyCandidateV1? candidate)
    {
        if (candidate is null)
        {
            return new StrategyCandidateAssessmentV1(StrategyCandidateStatusV1.Draft, [
                Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_REQUIRED", "$", "A strategy candidate is required."),
            ]);
        }

        var issues = new List<StrategyCandidateIssueV1>();
        Required(candidate.SchemaVersion, "schemaVersion", "CANDIDATE_SCHEMA_REQUIRED", issues);
        if (!string.Equals(candidate.SchemaVersion, StrategyCandidateV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_SCHEMA_UNSUPPORTED", "schemaVersion",
                $"Schema version must be '{StrategyCandidateV1.CurrentSchemaVersion}'."));
        }

        Required(candidate.CandidateId, "candidateId", "CANDIDATE_ID_REQUIRED", issues);
        Required(candidate.RawIntent, "rawIntent", "CANDIDATE_RAW_INTENT_REQUIRED", issues);
        Required(candidate.Title, "title", "CANDIDATE_TITLE_REQUIRED", issues);
        if (candidate.Revision <= 0)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_REVISION_INVALID", "revision",
                "Candidate revision must be positive."));
        }

        ValidateParentHash(candidate, issues);
        ValidEnum(candidate.Status, "status", issues);
        ValidateInterpretation(candidate.Interpretation, issues);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var statementIds = new HashSet<string>(StringComparer.Ordinal);
        if (candidate.Groups is null || candidate.Groups.Count == 0)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_GROUP_REQUIRED", "groups",
                "A candidate must contain at least one strategy group."));
        }
        else
        {
            for (var index = 0; index < candidate.Groups.Count; index++)
                ValidateGroup(candidate.Groups[index], $"groups[{index}]", ids, statementIds, issues);
        }

        if (!HasRule(candidate.Groups))
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Confirmation, "CANDIDATE_RULE_REQUIRED", "groups",
                "At least one proposed or confirmed strategy rule is required before confirmation."));
        }

        ValidateBuildSupport(candidate.BuildSupport, statementIds, ids, issues);
        ValidateBuildSupportCoverage(candidate.Groups, candidate.BuildSupport, issues);
        ValidateConfirmation(candidate, issues);

        return new StrategyCandidateAssessmentV1(candidate.Status, issues.AsReadOnly());
    }

    public static IReadOnlyList<StrategyCandidateIssueV1> ValidateWorkspace(StrategyGenerationWorkspaceV1? workspace)
    {
        if (workspace is null)
            return [Issue(StrategyCandidateIssueScopeV1.Structure, "WORKSPACE_REQUIRED", "$", "A strategy workspace is required.")];

        var issues = new List<StrategyCandidateIssueV1>();
        if (!string.Equals(workspace.SchemaVersion, StrategyGenerationWorkspaceV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "WORKSPACE_SCHEMA_UNSUPPORTED", "schemaVersion",
                $"Schema version must be '{StrategyGenerationWorkspaceV1.CurrentSchemaVersion}'."));
        }
        Required(workspace.WorkspaceId, "workspaceId", "WORKSPACE_ID_REQUIRED", issues);
        Required(workspace.Name, "name", "WORKSPACE_NAME_REQUIRED", issues);

        var candidates = workspace.Candidates;
        if (candidates is null)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "WORKSPACE_CANDIDATES_REQUIRED", "candidates",
                "Workspace candidates are required."));
            return issues.AsReadOnly();
        }

        var keys = new HashSet<(string Id, int Revision)>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate is null)
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "WORKSPACE_CANDIDATE_NULL", $"candidates[{index}]",
                    "Workspace candidates cannot be null."));
                continue;
            }

            if (!keys.Add((candidate.CandidateId, candidate.Revision)))
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "WORKSPACE_CANDIDATE_DUPLICATE", $"candidates[{index}]",
                    $"Candidate '{candidate.CandidateId}' revision {candidate.Revision} appears more than once."));
            }

            foreach (var issue in Assess(candidate).Issues.Where(static issue => issue.Scope == StrategyCandidateIssueScopeV1.Structure))
            {
                issues.Add(issue with { Path = $"candidates[{index}].{issue.Path}" });
            }
        }

        var hasActiveId = !string.IsNullOrWhiteSpace(workspace.ActiveCandidateId);
        var hasActiveRevision = workspace.ActiveCandidateRevision.HasValue;
        if (hasActiveId != hasActiveRevision)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "WORKSPACE_ACTIVE_KEY_INCOMPLETE", "activeCandidateId",
                "Active candidate id and revision must either both be present or both be absent."));
        }
        else if (hasActiveId && !keys.Contains((workspace.ActiveCandidateId!, workspace.ActiveCandidateRevision!.Value)))
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "WORKSPACE_ACTIVE_NOT_FOUND", "activeCandidateId",
                "The active candidate revision is not present in the workspace."));
        }

        return issues.AsReadOnly();
    }

    private static void ValidateParentHash(
        StrategyCandidateV1 candidate,
        ICollection<StrategyCandidateIssueV1> issues)
    {
        if (candidate.Revision == 1 && candidate.ParentContentHashSha256 is not null)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_PARENT_UNEXPECTED", "parentContentHashSha256",
                "The first revision cannot reference a parent candidate hash."));
        }
        else if (candidate.Revision > 1 && !IsSha256(candidate.ParentContentHashSha256))
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_PARENT_REQUIRED", "parentContentHashSha256",
                "A revision after the first must reference its parent's lowercase SHA-256 content hash."));
        }
    }

    private static void ValidateInterpretation(
        StrategyCandidateInterpretationV1? interpretation,
        ICollection<StrategyCandidateIssueV1> issues)
    {
        if (interpretation is null)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_INTERPRETATION_REQUIRED", "interpretation",
                "A plain-language interpretation is required."));
            return;
        }

        Required(interpretation.Summary, "interpretation.summary", "CANDIDATE_INTERPRETATION_SUMMARY_REQUIRED", issues);
        ValidEnum(interpretation.Confidence, "interpretation.confidence", issues);
        if (interpretation.Alternatives is null)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_ALTERNATIVES_REQUIRED", "interpretation.alternatives",
                "Interpretation alternatives must be present, even when empty."));
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < interpretation.Alternatives.Count; index++)
        {
            var alternative = interpretation.Alternatives[index];
            var path = $"interpretation.alternatives[{index}]";
            if (alternative is null)
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_ALTERNATIVE_NULL", path,
                    "Interpretation alternatives cannot be null."));
                continue;
            }
            Required(alternative.AlternativeId, $"{path}.alternativeId", "CANDIDATE_ALTERNATIVE_ID_REQUIRED", issues);
            Required(alternative.Summary, $"{path}.summary", "CANDIDATE_ALTERNATIVE_SUMMARY_REQUIRED", issues);
            if (!string.IsNullOrWhiteSpace(alternative.AlternativeId) && !ids.Add(alternative.AlternativeId))
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_ALTERNATIVE_ID_DUPLICATE", $"{path}.alternativeId",
                    $"Alternative id '{alternative.AlternativeId}' is duplicated."));
            }
        }
    }

    private static void ValidateGroup(
        StrategyCandidateGroupV1? group,
        string path,
        ISet<string> ids,
        ISet<string> statementIds,
        ICollection<StrategyCandidateIssueV1> issues)
    {
        if (group is null)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_GROUP_NULL", path,
                "Strategy groups cannot be null."));
            return;
        }

        Required(group.GroupId, $"{path}.groupId", "CANDIDATE_GROUP_ID_REQUIRED", issues);
        if (!string.IsNullOrWhiteSpace(group.GroupId) && !ids.Add(group.GroupId))
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_ID_DUPLICATE", $"{path}.groupId",
                $"Candidate object id '{group.GroupId}' is duplicated."));
        }
        ValidEnum(group.Kind, $"{path}.kind", issues);
        Required(group.Title, $"{path}.title", "CANDIDATE_GROUP_TITLE_REQUIRED", issues);
        Required(group.Summary, $"{path}.summary", "CANDIDATE_GROUP_SUMMARY_REQUIRED", issues);

        if (group.Statements is null)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_STATEMENTS_REQUIRED", $"{path}.statements",
                "Group statements must be present, even when empty."));
        }
        else
        {
            for (var index = 0; index < group.Statements.Count; index++)
                ValidateStatement(group.Statements[index], $"{path}.statements[{index}]", ids, statementIds, issues);
        }

        if (group.Children is null)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_CHILDREN_REQUIRED", $"{path}.children",
                "Nested groups must be present, even when empty."));
        }
        else
        {
            for (var index = 0; index < group.Children.Count; index++)
                ValidateGroup(group.Children[index], $"{path}.children[{index}]", ids, statementIds, issues);
        }
    }

    private static void ValidateStatement(
        StrategyCandidateStatementV1? statement,
        string path,
        ISet<string> ids,
        ISet<string> statementIds,
        ICollection<StrategyCandidateIssueV1> issues)
    {
        if (statement is null)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_STATEMENT_NULL", path,
                "Strategy statements cannot be null."));
            return;
        }

        Required(statement.StatementId, $"{path}.statementId", "CANDIDATE_STATEMENT_ID_REQUIRED", issues);
        if (!string.IsNullOrWhiteSpace(statement.StatementId))
        {
            if (!ids.Add(statement.StatementId))
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_ID_DUPLICATE", $"{path}.statementId",
                    $"Candidate object id '{statement.StatementId}' is duplicated."));
            }
            statementIds.Add(statement.StatementId);
        }

        ValidEnum(statement.Kind, $"{path}.kind", issues);
        ValidEnum(statement.Source, $"{path}.source", issues);
        ValidEnum(statement.State, $"{path}.state", issues);
        Required(statement.Text, $"{path}.text", "CANDIDATE_STATEMENT_TEXT_REQUIRED", issues);

        var questionStateValid = statement.Kind == StrategyCandidateStatementKindV1.Question
            ? statement.State is StrategyCandidateStatementStateV1.Open or StrategyCandidateStatementStateV1.Resolved
            : statement.State is StrategyCandidateStatementStateV1.Proposed or
                StrategyCandidateStatementStateV1.Confirmed or StrategyCandidateStatementStateV1.Rejected;
        if (!questionStateValid)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_STATEMENT_STATE_INVALID", $"{path}.state",
                "Questions must be Open or Resolved; other statements must be Proposed, Confirmed, or Rejected."));
        }

        if (statement.Value is not null)
        {
            Required(statement.Value.TypeId, $"{path}.value.typeId", "CANDIDATE_VALUE_TYPE_REQUIRED", issues);
            Required(statement.Value.CanonicalValue, $"{path}.value.canonicalValue", "CANDIDATE_VALUE_REQUIRED", issues);
            if (!string.IsNullOrWhiteSpace(statement.Value.TypeId) && !IsNamespacedTypeId(statement.Value.TypeId))
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_VALUE_TYPE_INVALID", $"{path}.value.typeId",
                    "Typed semantic values must use a namespaced, versioned id such as 'core.duration@1'."));
            }
        }
    }

    private static void ValidateBuildSupport(
        IReadOnlyList<StrategyBuildSupportItemV1>? support,
        ISet<string> statementIds,
        ISet<string> ids,
        ICollection<StrategyCandidateIssueV1> issues)
    {
        if (support is null)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_BUILD_SUPPORT_REQUIRED", "buildSupport",
                "Build-support items must be present, even when empty."));
            return;
        }

        for (var index = 0; index < support.Count; index++)
        {
            var item = support[index];
            var path = $"buildSupport[{index}]";
            if (item is null)
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_BUILD_SUPPORT_NULL", path,
                    "Build-support items cannot be null."));
                continue;
            }

            Required(item.SupportId, $"{path}.supportId", "CANDIDATE_BUILD_SUPPORT_ID_REQUIRED", issues);
            if (!string.IsNullOrWhiteSpace(item.SupportId) && !ids.Add(item.SupportId))
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_ID_DUPLICATE", $"{path}.supportId",
                    $"Candidate object id '{item.SupportId}' is duplicated."));
            }
            Required(item.Description, $"{path}.description", "CANDIDATE_BUILD_SUPPORT_DESCRIPTION_REQUIRED", issues);
            Required(item.Detail, $"{path}.detail", "CANDIDATE_BUILD_SUPPORT_DETAIL_REQUIRED", issues);
            ValidEnum(item.Status, $"{path}.status", issues);

            if (item.RelatedStatementIds is null)
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_BUILD_SUPPORT_REFS_REQUIRED",
                    $"{path}.relatedStatementIds", "Related statement ids must be present, even when empty."));
            }
            else
            {
                foreach (var statementId in item.RelatedStatementIds)
                {
                    if (string.IsNullOrWhiteSpace(statementId) || !statementIds.Contains(statementId))
                    {
                        issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_BUILD_SUPPORT_REF_UNKNOWN",
                            $"{path}.relatedStatementIds", $"Build-support item references unknown statement '{statementId}'."));
                    }
                }
            }

            if (item.RequiredForLowering && item.Status != StrategyBuildSupportStatusV1.Supported)
            {
                var scope = item.Status == StrategyBuildSupportStatusV1.NeedsUserChoice
                    ? StrategyCandidateIssueScopeV1.Confirmation
                    : StrategyCandidateIssueScopeV1.BuildSupport;
                issues.Add(Issue(scope, "CANDIDATE_BUILD_SUPPORT_INCOMPLETE", $"{path}.status",
                    $"Required build support '{item.Description}' is {Display(item.Status)}."));
            }
        }
    }

    private static void ValidateBuildSupportCoverage(
        IReadOnlyList<StrategyCandidateGroupV1>? groups,
        IReadOnlyList<StrategyBuildSupportItemV1>? support,
        ICollection<StrategyCandidateIssueV1> issues)
    {
        if (support is null) return;
        if (support.Count == 0)
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.BuildSupport,
                "CANDIDATE_BUILD_SUPPORT_EMPTY",
                "buildSupport",
                "Build support has not been assessed for this strategy."));
        }

        var covered = support
            .Where(static item => item is { RequiredForLowering: true, RelatedStatementIds: not null })
            .SelectMany(static item => item.RelatedStatementIds)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (statement, path) in EnumerateStatements(groups))
        {
            if (!statement.IsMaterial || statement.State == StrategyCandidateStatementStateV1.Rejected ||
                statement.Kind is not (StrategyCandidateStatementKindV1.Rule or
                    StrategyCandidateStatementKindV1.Constraint or
                    StrategyCandidateStatementKindV1.Requirement))
                continue;

            if (!covered.Contains(statement.StatementId))
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.BuildSupport,
                    "CANDIDATE_BUILD_SUPPORT_MISSING",
                    path,
                    $"Required build support has not been assessed for '{statement.Text}'."));
            }
        }
    }

    private static void ValidateConfirmation(
        StrategyCandidateV1 candidate,
        ICollection<StrategyCandidateIssueV1> issues)
    {
        foreach (var (statement, path) in EnumerateStatements(candidate.Groups))
        {
            if (!statement.IsMaterial) continue;

            if (statement.Kind == StrategyCandidateStatementKindV1.Question &&
                statement.State == StrategyCandidateStatementStateV1.Open)
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Confirmation, "CANDIDATE_QUESTION_OPEN", path,
                    $"Material question '{statement.Text}' must be resolved."));
            }
            else if (statement.Kind != StrategyCandidateStatementKindV1.Question &&
                     statement.State == StrategyCandidateStatementStateV1.Proposed)
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Confirmation, "CANDIDATE_STATEMENT_UNCONFIRMED", path,
                    $"Material {statement.Kind.ToString().ToLowerInvariant()} '{statement.Text}' must be confirmed or rejected."));
            }
            else if (statement.State == StrategyCandidateStatementStateV1.Rejected)
            {
                issues.Add(Issue(StrategyCandidateIssueScopeV1.Confirmation, "CANDIDATE_STATEMENT_REJECTED", path,
                    $"Rejected material statement '{statement.Text}' must be replaced or removed."));
            }
        }

        if (candidate.Status == StrategyCandidateStatusV1.Confirmed &&
            issues.Any(static issue => issue.Scope == StrategyCandidateIssueScopeV1.Confirmation))
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_CONFIRMED_WITH_OPEN_DECISIONS", "status",
                "A candidate cannot be marked Confirmed while material decisions remain unresolved."));
        }
    }

    private static IEnumerable<(StrategyCandidateStatementV1 Statement, string Path)> EnumerateStatements(
        IReadOnlyList<StrategyCandidateGroupV1>? groups,
        string path = "groups")
    {
        if (groups is null) yield break;
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            if (group is null) continue;
            var groupPath = $"{path}[{groupIndex}]";
            if (group.Statements is not null)
            {
                for (var statementIndex = 0; statementIndex < group.Statements.Count; statementIndex++)
                {
                    if (group.Statements[statementIndex] is { } statement)
                        yield return (statement, $"{groupPath}.statements[{statementIndex}]");
                }
            }

            foreach (var nested in EnumerateStatements(group.Children, $"{groupPath}.children"))
                yield return nested;
        }
    }

    private static bool HasRule(IReadOnlyList<StrategyCandidateGroupV1>? groups) =>
        EnumerateStatements(groups).Any(static item =>
            item.Statement.Kind == StrategyCandidateStatementKindV1.Rule &&
            item.Statement.State != StrategyCandidateStatementStateV1.Rejected);

    private static void Required(
        string? value,
        string path,
        string code,
        ICollection<StrategyCandidateIssueV1> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, code, path, $"{path} is required."));
    }

    private static void ValidEnum<T>(
        T value,
        string path,
        ICollection<StrategyCandidateIssueV1> issues)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            issues.Add(Issue(StrategyCandidateIssueScopeV1.Structure, "CANDIDATE_ENUM_INVALID", path,
                $"{path} contains unsupported value '{value}'."));
        }
    }

    private static StrategyCandidateIssueV1 Issue(
        StrategyCandidateIssueScopeV1 scope,
        string code,
        string path,
        string message) => new(scope, code, path, message);

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsNamespacedTypeId(string value)
    {
        var at = value.LastIndexOf('@');
        return at > 1 && at < value.Length - 1 && value[..at].Contains(".", StringComparison.Ordinal) &&
               int.TryParse(value[(at + 1)..], out var version) && version > 0;
    }

    private static string Display(StrategyBuildSupportStatusV1 status) => status switch
    {
        StrategyBuildSupportStatusV1.NeedsUserChoice => "waiting for a user choice",
        StrategyBuildSupportStatusV1.NeedsImplementation => "not implemented",
        StrategyBuildSupportStatusV1.DataUnavailable => "missing required data",
        StrategyBuildSupportStatusV1.Unknown => "not yet checked",
        _ => status.ToString(),
    };
}
