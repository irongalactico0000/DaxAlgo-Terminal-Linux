namespace TradingTerminal.Core.Strategies.Generation;

public sealed record StrategyCandidateConfirmationIssueV1(
    string Code,
    string Path,
    string Message);

/// <summary>
/// Result of confirming the exact candidate revision the user reviewed. Confirmation accepts the
/// candidate's proposed non-question statements as written; it never invents answers to open
/// questions and it never upgrades build-support claims.
/// </summary>
public sealed record StrategyCandidateConfirmationResultV1(
    StrategyCandidateV1? Candidate,
    StrategyCandidateAssessmentV1? Assessment,
    IReadOnlyList<StrategyCandidateConfirmationIssueV1> Issues)
{
    public bool Success => Candidate is not null && Assessment is not null && Issues.Count == 0;
}

/// <summary>
/// Hash-bound user confirmation. The expected hash prevents a stale UI action from confirming a
/// revision that changed after it was displayed. Proposed statements become Confirmed because the
/// user is accepting that exact text; open material questions and required user choices still stop
/// confirmation. Build support remains independent, so an understood candidate can be confirmed
/// while honestly remaining unable to lower.
/// </summary>
public static class StrategyCandidateConfirmationV1
{
    public static StrategyCandidateConfirmationResultV1 Confirm(
        StrategyCandidateV1? candidate,
        string? expectedContentHashSha256)
    {
        var issues = new List<StrategyCandidateConfirmationIssueV1>();
        if (candidate is null)
        {
            issues.Add(Issue("CONFIRM_CANDIDATE_REQUIRED", "$", "A strategy candidate is required."));
            return Failed(issues);
        }

        var assessment = StrategyCandidateValidatorV1.Assess(candidate);
        foreach (var issue in assessment.Issues.Where(static issue =>
                     issue.Scope == StrategyCandidateIssueScopeV1.Structure))
        {
            issues.Add(Issue("CONFIRM_CANDIDATE_INVALID", issue.Path, issue.Message));
        }

        if (candidate.Status is StrategyCandidateStatusV1.Confirmed or
            StrategyCandidateStatusV1.Rejected or StrategyCandidateStatusV1.Superseded)
        {
            issues.Add(Issue("CONFIRM_STATUS_INVALID", "status",
                $"Candidate status '{candidate.Status}' cannot be confirmed."));
        }
        if (candidate.Revision == int.MaxValue)
        {
            issues.Add(Issue("CONFIRM_REVISION_EXHAUSTED", "revision",
                "The candidate revision cannot be incremented."));
        }

        var actualHash = StrategyCandidateCanonicalJsonV1.Hash(candidate);
        if (!IsSha256(expectedContentHashSha256) ||
            !string.Equals(actualHash, expectedContentHashSha256, StringComparison.Ordinal))
        {
            issues.Add(Issue("CONFIRM_HASH_MISMATCH", "expectedContentHashSha256",
                "The candidate changed after it was reviewed. Review the current revision before confirming."));
        }

        // Proposed statements are what the user is accepting with this action. All other
        // confirmation problems (open questions, rejected material statements, required choices)
        // must be resolved explicitly rather than being silently promoted.
        foreach (var issue in assessment.Issues.Where(static issue =>
                     issue.Scope == StrategyCandidateIssueScopeV1.Confirmation &&
                     issue.Code != "CANDIDATE_STATEMENT_UNCONFIRMED"))
        {
            issues.Add(Issue("CONFIRM_DECISION_REQUIRED", issue.Path, issue.Message));
        }

        if (issues.Count > 0) return Failed(issues);

        var confirmed = candidate with
        {
            Revision = checked(candidate.Revision + 1),
            ParentContentHashSha256 = actualHash,
            Status = StrategyCandidateStatusV1.Confirmed,
            Groups = ConfirmGroups(candidate.Groups),
        };
        var confirmedAssessment = StrategyCandidateValidatorV1.Assess(confirmed);
        foreach (var issue in confirmedAssessment.Issues.Where(static issue =>
                     issue.Scope is StrategyCandidateIssueScopeV1.Structure or
                         StrategyCandidateIssueScopeV1.Confirmation))
        {
            issues.Add(Issue("CONFIRM_RESULT_INVALID", issue.Path, issue.Message));
        }

        return issues.Count == 0
            ? new StrategyCandidateConfirmationResultV1(confirmed, confirmedAssessment, [])
            : Failed(issues);
    }

    private static IReadOnlyList<StrategyCandidateGroupV1> ConfirmGroups(
        IReadOnlyList<StrategyCandidateGroupV1> groups) =>
        groups.Select(ConfirmGroup).ToArray();

    private static StrategyCandidateGroupV1 ConfirmGroup(StrategyCandidateGroupV1 group) => group with
    {
        Statements = group.Statements
            .Select(static statement => statement.Kind != StrategyCandidateStatementKindV1.Question &&
                                        statement.State == StrategyCandidateStatementStateV1.Proposed
                ? statement with { State = StrategyCandidateStatementStateV1.Confirmed }
                : statement)
            .ToArray(),
        Children = group.Children.Select(ConfirmGroup).ToArray(),
    };

    private static StrategyCandidateConfirmationResultV1 Failed(
        IReadOnlyCollection<StrategyCandidateConfirmationIssueV1> issues) =>
        new(null, null, issues.ToArray());

    private static StrategyCandidateConfirmationIssueV1 Issue(string code, string path, string message) =>
        new(code, path, message);

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
