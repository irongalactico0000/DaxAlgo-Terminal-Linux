namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// A bounded request from the intake agent to one relevant specialist. Specialist ids are namespaced
/// capabilities such as <c>technical.chart_pattern@1</c> or <c>domain.options@1</c>; they are not a
/// permanent hierarchy baked into every strategy.
/// </summary>
public sealed record StrategySpecialistRequestV1(
    string RequestId,
    string SpecialistId,
    string TargetGroupId,
    string Goal,
    bool Required);

/// <summary>The intake result: a user-visible candidate plus only the specialist work it needs.</summary>
public sealed record StrategyCandidateDraftV1(
    StrategyCandidateV1 Candidate,
    IReadOnlyList<StrategySpecialistRequestV1> SpecialistRequests);

/// <summary>
/// A specialist may replace exactly one assigned group and propose build-support facts. It cannot
/// alter the raw user intent, candidate identity, lifecycle status, or any unrelated group.
/// </summary>
public sealed record StrategyCandidateAmendmentV1(
    string RequestId,
    string SpecialistId,
    string TargetGroupId,
    StrategyCandidateGroupV1 ReplacementGroup,
    IReadOnlyList<StrategyBuildSupportItemV1> BuildSupportUpserts);

public sealed record StrategyCandidateCompositionIssueV1(
    string Code,
    string Path,
    string Message);

public sealed record StrategyCandidateCompositionResultV1(
    StrategyCandidateV1? Candidate,
    StrategyCandidateAssessmentV1? Assessment,
    IReadOnlyList<StrategyCandidateCompositionIssueV1> Issues)
{
    public bool Success => Candidate is not null && Assessment is not null && Issues.Count == 0;
}

/// <summary>
/// Deterministically combines parallel specialist proposals. Conflicting ownership is rejected rather
/// than resolved by agent ordering, and every successful composition creates a new candidate revision
/// that still requires user confirmation.
/// </summary>
public static class StrategyCandidateComposerV1
{
    public static StrategyCandidateCompositionResultV1 Compose(
        StrategyCandidateDraftV1? draft,
        IReadOnlyList<StrategyCandidateAmendmentV1>? amendments)
    {
        var issues = new List<StrategyCandidateCompositionIssueV1>();
        if (draft is null)
        {
            issues.Add(Issue("COMPOSE_DRAFT_REQUIRED", "$", "A strategy candidate draft is required."));
            return Failed(issues);
        }
        if (draft.Candidate is null)
        {
            issues.Add(Issue("COMPOSE_CANDIDATE_REQUIRED", "candidate", "The draft candidate is required."));
            return Failed(issues);
        }

        var baselineAssessment = StrategyCandidateValidatorV1.Assess(draft.Candidate);
        if (!baselineAssessment.IsStructurallyValid)
        {
            foreach (var issue in baselineAssessment.Issues.Where(static issue =>
                         issue.Scope == StrategyCandidateIssueScopeV1.Structure))
            {
                issues.Add(Issue("COMPOSE_CANDIDATE_INVALID", $"candidate.{issue.Path}", issue.Message));
            }
            return Failed(issues);
        }
        if (draft.Candidate.Revision == int.MaxValue)
        {
            issues.Add(Issue("COMPOSE_REVISION_EXHAUSTED", "candidate.revision",
                "The candidate revision cannot be incremented."));
            return Failed(issues);
        }

        if (draft.SpecialistRequests is null)
        {
            issues.Add(Issue("COMPOSE_REQUESTS_REQUIRED", "specialistRequests",
                "Specialist requests must be present, even when empty."));
            return Failed(issues);
        }
        if (amendments is null)
        {
            issues.Add(Issue("COMPOSE_AMENDMENTS_REQUIRED", "amendments",
                "Specialist amendments must be present, even when empty."));
            return Failed(issues);
        }

        var requests = IndexRequests(draft.SpecialistRequests, issues);
        var accepted = ValidateAmendments(amendments, requests, issues);

        foreach (var request in requests.Values.Where(static request => request.Required))
        {
            if (!accepted.ContainsKey(request.RequestId))
            {
                issues.Add(Issue("COMPOSE_REQUIRED_SPECIALIST_MISSING", "amendments",
                    $"Required specialist request '{request.RequestId}' has no valid amendment."));
            }
        }

        if (issues.Count > 0) return Failed(issues);

        var groups = draft.Candidate.Groups;
        var support = draft.Candidate.BuildSupport.ToDictionary(static item => item.SupportId, StringComparer.Ordinal);
        foreach (var amendment in accepted.Values.OrderBy(static amendment => amendment.RequestId, StringComparer.Ordinal))
        {
            var replacementStatementIds = CollectStatementIds(amendment.ReplacementGroup);
            var replacements = 0;
            groups = groups
                .Select(group => Replace(group, amendment.TargetGroupId, amendment.ReplacementGroup, ref replacements))
                .ToArray();
            if (replacements != 1)
            {
                issues.Add(Issue("COMPOSE_TARGET_NOT_UNIQUE", $"amendments[{amendment.RequestId}].targetGroupId",
                    $"Target group '{amendment.TargetGroupId}' matched {replacements} groups; exactly one is required."));
                continue;
            }

            foreach (var item in amendment.BuildSupportUpserts)
            {
                if (support.TryGetValue(item.SupportId, out var existing) &&
                    existing.RelatedStatementIds.Any(statementId => !replacementStatementIds.Contains(statementId)))
                {
                    issues.Add(Issue("COMPOSE_SUPPORT_SCOPE_VIOLATION",
                        $"amendments[{amendment.RequestId}].buildSupportUpserts[{item.SupportId}]",
                        $"A specialist cannot overwrite build-support item '{item.SupportId}' because it also belongs to statements outside assigned group '{amendment.TargetGroupId}'."));
                    continue;
                }

                support[item.SupportId] = item;
            }
        }

        if (issues.Count > 0) return Failed(issues);

        var composed = draft.Candidate with
        {
            Revision = checked(draft.Candidate.Revision + 1),
            ParentContentHashSha256 = StrategyCandidateCanonicalJsonV1.Hash(draft.Candidate),
            Status = StrategyCandidateStatusV1.AwaitingConfirmation,
            Groups = groups,
            BuildSupport = support.Values.OrderBy(static item => item.SupportId, StringComparer.Ordinal).ToArray(),
        };
        var assessment = StrategyCandidateValidatorV1.Assess(composed);
        if (!assessment.IsStructurallyValid)
        {
            foreach (var issue in assessment.Issues.Where(static issue => issue.Scope == StrategyCandidateIssueScopeV1.Structure))
            {
                issues.Add(Issue("COMPOSE_RESULT_INVALID", $"candidate.{issue.Path}", issue.Message));
            }
            return Failed(issues);
        }

        return new StrategyCandidateCompositionResultV1(composed, assessment, []);
    }

    private static Dictionary<string, StrategySpecialistRequestV1> IndexRequests(
        IReadOnlyList<StrategySpecialistRequestV1> requests,
        ICollection<StrategyCandidateCompositionIssueV1> issues)
    {
        var indexed = new Dictionary<string, StrategySpecialistRequestV1>(StringComparer.Ordinal);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var path = $"specialistRequests[{index}]";
            if (request is null)
            {
                issues.Add(Issue("COMPOSE_REQUEST_NULL", path, "Specialist requests cannot be null."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(request.RequestId) ||
                string.IsNullOrWhiteSpace(request.SpecialistId) ||
                string.IsNullOrWhiteSpace(request.TargetGroupId) ||
                string.IsNullOrWhiteSpace(request.Goal))
            {
                issues.Add(Issue("COMPOSE_REQUEST_INVALID", path,
                    "A specialist request requires non-empty request, specialist, target-group, and goal fields."));
                continue;
            }
            if (!IsNamespacedId(request.SpecialistId))
            {
                issues.Add(Issue("COMPOSE_SPECIALIST_ID_INVALID", $"{path}.specialistId",
                    "Specialist ids must be namespaced and versioned, for example 'technical.chart_pattern@1'."));
                continue;
            }
            if (!indexed.TryAdd(request.RequestId, request))
            {
                issues.Add(Issue("COMPOSE_REQUEST_DUPLICATE", $"{path}.requestId",
                    $"Specialist request id '{request.RequestId}' is duplicated."));
            }
        }
        return indexed;
    }

    private static Dictionary<string, StrategyCandidateAmendmentV1> ValidateAmendments(
        IReadOnlyList<StrategyCandidateAmendmentV1> amendments,
        IReadOnlyDictionary<string, StrategySpecialistRequestV1> requests,
        ICollection<StrategyCandidateCompositionIssueV1> issues)
    {
        var accepted = new Dictionary<string, StrategyCandidateAmendmentV1>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var supportOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < amendments.Count; index++)
        {
            var amendment = amendments[index];
            var path = $"amendments[{index}]";
            if (amendment is null)
            {
                issues.Add(Issue("COMPOSE_AMENDMENT_NULL", path, "Specialist amendments cannot be null."));
                continue;
            }
            if (!requests.TryGetValue(amendment.RequestId, out var request))
            {
                issues.Add(Issue("COMPOSE_REQUEST_UNKNOWN", $"{path}.requestId",
                    $"Amendment references unknown request '{amendment.RequestId}'."));
                continue;
            }
            if (!string.Equals(request.SpecialistId, amendment.SpecialistId, StringComparison.Ordinal) ||
                !string.Equals(request.TargetGroupId, amendment.TargetGroupId, StringComparison.Ordinal))
            {
                issues.Add(Issue("COMPOSE_ASSIGNMENT_MISMATCH", path,
                    "An amendment must come from the assigned specialist and target only its assigned group."));
                continue;
            }
            if (amendment.ReplacementGroup is null ||
                !string.Equals(amendment.TargetGroupId, amendment.ReplacementGroup.GroupId, StringComparison.Ordinal))
            {
                issues.Add(Issue("COMPOSE_REPLACEMENT_ID_MISMATCH", $"{path}.replacementGroup",
                    "A replacement group must preserve its assigned target group id."));
                continue;
            }
            if (EnumerateStatements(amendment.ReplacementGroup).Any(static statement =>
                    statement.Source == StrategyCandidateStatementSourceV1.DeterministicSystem))
            {
                issues.Add(Issue("COMPOSE_STATEMENT_AUTHORITY_VIOLATION", $"{path}.replacementGroup",
                    "A specialist cannot attribute a statement to the deterministic system."));
                continue;
            }
            if (!accepted.TryAdd(amendment.RequestId, amendment))
            {
                issues.Add(Issue("COMPOSE_AMENDMENT_DUPLICATE", $"{path}.requestId",
                    $"Request '{amendment.RequestId}' has more than one amendment."));
                continue;
            }
            if (!targets.Add(amendment.TargetGroupId))
            {
                issues.Add(Issue("COMPOSE_TARGET_CONFLICT", $"{path}.targetGroupId",
                    $"More than one specialist owns target group '{amendment.TargetGroupId}'."));
            }

            if (amendment.BuildSupportUpserts is null)
            {
                issues.Add(Issue("COMPOSE_SUPPORT_REQUIRED", $"{path}.buildSupportUpserts",
                    "Build-support upserts must be present, even when empty."));
                continue;
            }
            var replacementStatementIds = CollectStatementIds(amendment.ReplacementGroup);
            foreach (var item in amendment.BuildSupportUpserts)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.SupportId))
                {
                    issues.Add(Issue("COMPOSE_SUPPORT_INVALID", $"{path}.buildSupportUpserts",
                        "Every build-support upsert requires a non-empty id."));
                    continue;
                }
                if (item.RelatedStatementIds is null)
                {
                    issues.Add(Issue("COMPOSE_SUPPORT_INVALID", $"{path}.buildSupportUpserts",
                        $"Build-support item '{item.SupportId}' requires related statement ids."));
                    continue;
                }
                var outsideStatementId = item.RelatedStatementIds.FirstOrDefault(statementId =>
                    !replacementStatementIds.Contains(statementId));
                if (outsideStatementId is not null)
                {
                    issues.Add(Issue("COMPOSE_SUPPORT_SCOPE_VIOLATION", $"{path}.buildSupportUpserts",
                        $"Build-support item '{item.SupportId}' references statement '{outsideStatementId}' outside assigned group '{amendment.TargetGroupId}'."));
                    continue;
                }
                if (item.Status == StrategyBuildSupportStatusV1.Supported)
                {
                    issues.Add(Issue("COMPOSE_SUPPORT_AUTHORITY_VIOLATION", $"{path}.buildSupportUpserts",
                        "A specialist cannot mark build support as Supported; only the deterministic capability service may do that."));
                    continue;
                }
                if (supportOwners.TryGetValue(item.SupportId, out var owner) &&
                    !string.Equals(owner, amendment.RequestId, StringComparison.Ordinal))
                {
                    issues.Add(Issue("COMPOSE_SUPPORT_CONFLICT", $"{path}.buildSupportUpserts",
                        $"Build-support item '{item.SupportId}' is proposed by both '{owner}' and '{amendment.RequestId}'."));
                }
                else
                {
                    supportOwners[item.SupportId] = amendment.RequestId;
                }
            }
        }

        return accepted;
    }

    private static HashSet<string> CollectStatementIds(StrategyCandidateGroupV1 group)
    {
        return EnumerateStatements(group)
            .Select(static statement => statement.StatementId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<StrategyCandidateStatementV1> EnumerateStatements(StrategyCandidateGroupV1 group)
    {
        if (group.Statements is not null)
        {
            foreach (var statement in group.Statements)
                if (statement is not null)
                    yield return statement;
        }
        if (group.Children is null) yield break;
        foreach (var child in group.Children)
        {
            if (child is null) continue;
            foreach (var statement in EnumerateStatements(child))
                yield return statement;
        }
    }

    private static StrategyCandidateGroupV1 Replace(
        StrategyCandidateGroupV1 group,
        string targetGroupId,
        StrategyCandidateGroupV1 replacement,
        ref int replacements)
    {
        if (string.Equals(group.GroupId, targetGroupId, StringComparison.Ordinal))
        {
            replacements++;
            return replacement;
        }

        var changed = false;
        var children = new StrategyCandidateGroupV1[group.Children.Count];
        for (var index = 0; index < group.Children.Count; index++)
        {
            children[index] = Replace(group.Children[index], targetGroupId, replacement, ref replacements);
            changed |= !ReferenceEquals(children[index], group.Children[index]);
        }
        return changed ? group with { Children = children } : group;
    }

    private static StrategyCandidateCompositionResultV1 Failed(
        IReadOnlyCollection<StrategyCandidateCompositionIssueV1> issues) =>
        new(null, null, issues.ToArray());

    private static StrategyCandidateCompositionIssueV1 Issue(string code, string path, string message) =>
        new(code, path, message);

    private static bool IsNamespacedId(string value)
    {
        var at = value.LastIndexOf('@');
        return at > 1 && at < value.Length - 1 && value[..at].Contains(".", StringComparison.Ordinal) &&
               int.TryParse(value[(at + 1)..], out var version) && version > 0;
    }
}
