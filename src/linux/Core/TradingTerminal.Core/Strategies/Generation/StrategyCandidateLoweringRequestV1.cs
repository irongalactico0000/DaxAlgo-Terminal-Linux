namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// Immutable handoff from strategy generation to the separately owned executable-definition
/// lowerer. It carries the exact confirmed candidate, not a prompt and not generated source.
/// </summary>
public sealed record StrategyCandidateLoweringRequestV1(
    string SchemaVersion,
    string CandidateId,
    int CandidateRevision,
    string CandidateContentHashSha256,
    string CanonicalCandidateJson)
{
    public const string CurrentSchemaVersion = "strategy-candidate-lowering-request/v1";
}

public sealed record StrategyCandidateLoweringRequestResultV1(
    StrategyCandidateLoweringRequestV1? Request,
    IReadOnlyList<StrategyCandidateIssueV1> Issues)
{
    public bool Success => Request is not null && Issues.Count == 0;
}

/// <summary>
/// Closes the strategy-generation workflow. Only an exact Confirmed candidate whose required support
/// is currently marked Supported can cross this handoff. This is not runtime admission: the downstream
/// deterministic component must independently recompute capabilities before lowering to TradeIR/C#.
/// This component intentionally has no compiler or runtime dependency.
/// </summary>
public static class StrategyCandidateLoweringBoundaryV1
{
    public static StrategyCandidateLoweringRequestResultV1 Create(StrategyCandidateV1? candidate)
    {
        var assessment = StrategyCandidateValidatorV1.Assess(candidate);
        if (!assessment.CanLower || candidate is null)
        {
            var issues = assessment.Issues.ToList();
            if (candidate is not null && candidate.Status != StrategyCandidateStatusV1.Confirmed)
            {
                issues.Add(new StrategyCandidateIssueV1(
                    StrategyCandidateIssueScopeV1.Confirmation,
                    "CANDIDATE_NOT_CONFIRMED",
                    "status",
                    "The exact strategy meaning must be confirmed before executable lowering."));
            }
            return new StrategyCandidateLoweringRequestResultV1(null, issues);
        }

        var canonical = StrategyCandidateCanonicalJsonV1.Serialize(candidate);
        var hash = StrategyCandidateCanonicalJsonV1.Hash(candidate);
        return new StrategyCandidateLoweringRequestResultV1(
            new StrategyCandidateLoweringRequestV1(
                StrategyCandidateLoweringRequestV1.CurrentSchemaVersion,
                candidate.CandidateId,
                candidate.Revision,
                hash,
                canonical),
            []);
    }
}
