using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// One format-specific authoring request and deterministic validation pass. Every lane calls the
/// selected model once; this layer never compiles, runs, imports, packages, or tests an artifact.
/// </summary>
public sealed class StrategyGenerationLaneAgentV1(StrategyGenerationLaneV1 lane) : IStrategyGenerationLaneAgentV1
{
    public StrategyGenerationLaneV1 Lane { get; } = lane;

    public async Task<StrategyGenerationLaneResultV1> GenerateAsync(
        IStrategyCodegenClient provider,
        ParallelStrategyGenerationRequestV1 request,
        string expectedCandidateId,
        CancellationToken ct = default,
        IProgress<StrategyGenerationLaneProgressV1>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCandidateId);

        var systemContext = ParallelStrategyGenerationPromptV1.SystemContext(Lane);
        var originalUserMessage = ParallelStrategyGenerationPromptV1.UserMessage(
            Lane,
            request,
            expectedCandidateId);
        var codegenRequest = new StrategyCodegenRequest(
            systemContext,
            [new CodegenMessage(CodegenRole.User, originalUserMessage)])
        {
            OutputContract = StrategyCodegenOutputContract.RawJsonObject,
        };

        progress?.Report(new StrategyGenerationLaneProgressV1(
            Lane,
            StrategyGenerationLaneProgressStateV1.WaitingForModel));

        StrategyCodegenResponse response;
        try
        {
            response = await provider.GenerateAsync(codegenRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(
                provider,
                "LANE_PROVIDER_EXCEPTION",
                exception.Message,
                null,
                CodegenUsage.None);
        }

        var usage = response.Usage ?? CodegenUsage.None;
        var raw = response.RawText ?? response.Code;
        if (!response.Success)
        {
            return Failed(
                provider,
                "LANE_PROVIDER_FAILED",
                response.Error ?? "The provider returned no result.",
                raw,
                usage);
        }

        var firstResult = ParseAndValidate(
            provider,
            request,
            expectedCandidateId,
            raw,
            usage,
            progress,
            "LANE_JSON_INVALID");
        if (firstResult.Readiness != StrategyGenerationReadinessV1.Invalid)
            return firstResult;

        progress?.Report(new StrategyGenerationLaneProgressV1(
            Lane,
            StrategyGenerationLaneProgressStateV1.RepairingResponse,
            "The first output was invalid; requesting one contract-aware repair."));

        StrategyCodegenResponse repairResponse;
        try
        {
            repairResponse = await provider.GenerateAsync(
                new StrategyCodegenRequest(
                    systemContext,
                    [
                        new CodegenMessage(CodegenRole.User, originalUserMessage),
                        new CodegenMessage(CodegenRole.Assistant, raw ?? string.Empty),
                        new CodegenMessage(
                            CodegenRole.User,
                            ParallelStrategyGenerationPromptV1.RepairMessage(
                                Lane,
                                expectedCandidateId,
                                StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                                    request.StrategyId,
                                    request.UserPrompt,
                                    Lane),
                                firstResult.Issues)),
                    ])
                {
                    OutputContract = StrategyCodegenOutputContract.RawJsonObject,
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Invalid(
                provider,
                "LANE_REPAIR_PROVIDER_EXCEPTION",
                $"The first output was invalid and the one repair request failed: {exception.Message}",
                raw,
                usage);
        }

        var repairUsage = repairResponse.Usage ?? CodegenUsage.None;
        var totalUsage = usage.Add(repairUsage);
        var repairRaw = repairResponse.RawText ?? repairResponse.Code;
        if (!repairResponse.Success)
        {
            return Invalid(
                provider,
                "LANE_REPAIR_PROVIDER_FAILED",
                $"The first output was invalid and the one repair request failed: " +
                (repairResponse.Error ?? "The provider returned no repair result."),
                repairRaw ?? raw,
                totalUsage);
        }

        return ParseAndValidate(
            provider,
            request,
            expectedCandidateId,
            repairRaw,
            totalUsage,
            progress,
            "LANE_JSON_INVALID_AFTER_REPAIR");
    }

    private StrategyGenerationLaneResultV1 ParseAndValidate(
        IStrategyCodegenClient provider,
        ParallelStrategyGenerationRequestV1 request,
        string expectedCandidateId,
        string? raw,
        CodegenUsage usage,
        IProgress<StrategyGenerationLaneProgressV1>? progress,
        string parseIssueCode)
    {
        progress?.Report(new StrategyGenerationLaneProgressV1(
            Lane,
            StrategyGenerationLaneProgressStateV1.ParsingResponse));

        if (!TryDeserializeCandidate(raw, out var candidate, out var parseError))
            return Invalid(provider, parseIssueCode, parseError, raw, usage);

        if (candidate is null)
            return Invalid(
                provider,
                "LANE_CANDIDATE_REQUIRED",
                "The model returned a null candidate.",
                raw,
                usage);

        progress?.Report(new StrategyGenerationLaneProgressV1(
            Lane,
            StrategyGenerationLaneProgressStateV1.ValidatingArtifact));

        IReadOnlyList<StrategyCandidateGenerationIssueV1> issues;
        try
        {
            issues = StrategyGenerationCandidateValidatorV1.Validate(
                candidate,
                Lane,
                expectedCandidateId,
                StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                    request.StrategyId,
                    request.UserPrompt,
                    Lane));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or
            FormatException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            return Invalid(
                provider,
                "LANE_ARTIFACT_VALIDATION_FAILED",
                $"The generated artifact cannot be structurally validated: {exception.Message}",
                raw,
                usage);
        }
        if (!StrategyGenerationCandidateCanonicalJsonV1.TryHash(candidate, out var candidateHash, out var hashError))
        {
            return Invalid(
                provider,
                "LANE_CANONICAL_JSON_INVALID",
                $"The generated candidate cannot be canonically hashed: {hashError}",
                raw,
                usage);
        }
        var valid = issues.All(static issue =>
            issue.Severity != StrategyCandidateGenerationIssueSeverityV1.Error);
        var run = new StrategyGenerationAgentRunV1(
            ParallelStrategyGenerationPromptV1.AgentId(Lane),
            provider.ProviderId,
            null,
            true,
            null,
            raw,
            usage);

        return new StrategyGenerationLaneResultV1(
            Lane,
            valid
                ? StrategyGenerationPackageCatalogV1.PackageValidationAvailable(Lane)
                    ? StrategyGenerationReadinessV1.PackageValid
                    : StrategyGenerationReadinessV1.Generated
                : StrategyGenerationReadinessV1.Invalid,
            candidate,
            candidateHash,
            issues,
            run);
    }

    private static bool TryDeserializeCandidate(
        string? raw,
        out StrategyGenerationCandidateV1? candidate,
        out string error)
    {
        if (StrategyModelJsonV1.TryDeserialize(
                raw,
                StrategyCandidateGenerationOrchestratorV1.MaxModelResponseCharacters,
                out candidate,
                out error))
            return true;

        if (string.IsNullOrWhiteSpace(raw) ||
            raw.Length > StrategyCandidateGenerationOrchestratorV1.MaxModelResponseCharacters ||
            !TryExtractSingleJsonObject(raw, out var extracted))
            return false;

        return StrategyModelJsonV1.TryDeserialize(
            extracted,
            StrategyCandidateGenerationOrchestratorV1.MaxModelResponseCharacters,
            out candidate,
            out error);
    }

    /// <summary>
    /// Recovers one syntactically complete JSON object from harmless model prose or markdown. It
    /// deliberately rejects a second parseable object so recovery cannot silently choose between
    /// competing candidates. Contract deserialization, host bindings, validation, and hashing still
    /// run after extraction.
    /// </summary>
    private static bool TryExtractSingleJsonObject(string raw, out string json)
    {
        json = string.Empty;
        string? recovered = null;
        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (depth == 0)
            {
                if (character != '{')
                    continue;

                start = index;
                depth = 1;
                inString = false;
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inString = false;
                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }
            if (character == '{')
            {
                depth++;
                continue;
            }
            if (character != '}')
                continue;

            depth--;
            if (depth != 0)
                continue;

            var candidate = raw[start..(index + 1)];
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    continue;
                if (recovered is not null)
                    return false;
                recovered = candidate;
            }
            catch (JsonException)
            {
                // This balanced brace span was prose, not JSON. Continue looking conservatively.
            }
        }

        if (recovered is null)
            return false;

        json = recovered;
        return true;
    }

    private StrategyGenerationLaneResultV1 Invalid(
        IStrategyCodegenClient provider,
        string code,
        string message,
        string? raw,
        CodegenUsage usage,
        StrategyGenerationCandidateV1? candidate = null) =>
        new(
            Lane,
            StrategyGenerationReadinessV1.Invalid,
            candidate,
            candidate is null ? null : StrategyGenerationCandidateCanonicalJsonV1.Hash(candidate),
            [new StrategyCandidateGenerationIssueV1(
                StrategyCandidateGenerationIssueSeverityV1.Error,
                code,
                StrategyGenerationLaneCatalogV1.WireName(Lane),
                message)],
            new StrategyGenerationAgentRunV1(
                ParallelStrategyGenerationPromptV1.AgentId(Lane),
                provider.ProviderId,
                null,
                true,
                null,
                raw,
                usage));

    private StrategyGenerationLaneResultV1 Failed(
        IStrategyCodegenClient provider,
        string code,
        string message,
        string? raw,
        CodegenUsage usage) =>
        new(
            Lane,
            StrategyGenerationReadinessV1.Failed,
            null,
            null,
            [new StrategyCandidateGenerationIssueV1(
                StrategyCandidateGenerationIssueSeverityV1.Error,
                code,
                StrategyGenerationLaneCatalogV1.WireName(Lane),
                message)],
            new StrategyGenerationAgentRunV1(
                ParallelStrategyGenerationPromptV1.AgentId(Lane),
                provider.ProviderId,
                null,
                false,
                message,
                raw,
                usage));
}

/// <summary>
/// Fans one user brief out to all four fixed representation agents. Each model call remains
/// failure-isolated, and results return in stable Vibe, Spec, Graph, CSP display order regardless of
/// completion order.
/// </summary>
public sealed class ParallelStrategyCandidateGeneratorV1 : IParallelStrategyCandidateGeneratorV1
{
    public const int MaxUserPromptCharacters = 100_000;

    private readonly IReadOnlyDictionary<StrategyGenerationLaneV1, IStrategyGenerationLaneAgentV1> _agents;
    private readonly ILogger? _logger;

    public ParallelStrategyCandidateGeneratorV1(
        IEnumerable<IStrategyGenerationLaneAgentV1> agents,
        ILogger<ParallelStrategyCandidateGeneratorV1>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(agents);
        _logger = logger;

        var indexed = new Dictionary<StrategyGenerationLaneV1, IStrategyGenerationLaneAgentV1>();
        foreach (var agent in agents)
        {
            ArgumentNullException.ThrowIfNull(agent);
            if (!indexed.TryAdd(agent.Lane, agent))
                throw new ArgumentException($"Strategy generation lane '{agent.Lane}' is registered more than once.", nameof(agents));
        }

        var missing = StrategyGenerationLaneCatalogV1.Ordered.Where(lane => !indexed.ContainsKey(lane)).ToArray();
        var unexpected = indexed.Keys.Where(lane => !StrategyGenerationLaneCatalogV1.Ordered.Contains(lane)).ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
            throw new ArgumentException(
                $"Strategy generation requires exactly the four known lanes. " +
                $"Missing: {string.Join(", ", missing)}. Unexpected: {string.Join(", ", unexpected)}.",
                nameof(agents));
        _agents = indexed;
    }

    public async Task<ParallelStrategyGenerationResultV1> GenerateAsync(
        IStrategyCodegenClient provider,
        ParallelStrategyGenerationRequestV1 request,
        CancellationToken ct = default,
        IProgress<StrategyGenerationLaneProgressV1>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StrategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);
        if (request.UserPrompt.Length > MaxUserPromptCharacters)
            throw new ArgumentException(
                $"The strategy prompt exceeds {MaxUserPromptCharacters:N0} characters.",
                nameof(request));
        ct.ThrowIfCancellationRequested();

        var normalizedRequest = request with { StrategyId = request.StrategyId.Trim() };
        var orderedAgents = StrategyGenerationLaneCatalogV1.Ordered.Select(lane => _agents[lane]).ToArray();
        foreach (var agent in orderedAgents)
            progress?.Report(new StrategyGenerationLaneProgressV1(
                agent.Lane,
                StrategyGenerationLaneProgressStateV1.Queued));
        var tasks = orderedAgents.Select(agent =>
            InvokeLaneWithProgressAsync(provider, normalizedRequest, agent, ct, progress)).ToArray();
        var orderedResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var usage = orderedResults.Aggregate(CodegenUsage.None,
            static (total, result) => total.Add(result.AgentRun?.Usage));
        var promptHash = StrategyGenerationCandidateCanonicalJsonV1.PromptHash(
            normalizedRequest.StrategyId,
            normalizedRequest.UserPrompt);

        _logger?.LogInformation(
            "Parallel strategy generation for {StrategyId}: {Selectable}/4 lane(s) selectable, {PackageValid} package-valid",
            normalizedRequest.StrategyId,
            orderedResults.Count(static result => result.Selectable),
            orderedResults.Count(static result => result.PackageValid));

        return new ParallelStrategyGenerationResultV1(
            normalizedRequest.StrategyId,
            normalizedRequest.UserPrompt,
            promptHash,
            Array.AsReadOnly(orderedResults),
            usage);
    }

    private static async Task<StrategyGenerationLaneResultV1> InvokeLaneWithProgressAsync(
        IStrategyCodegenClient provider,
        ParallelStrategyGenerationRequestV1 request,
        IStrategyGenerationLaneAgentV1 agent,
        CancellationToken ct,
        IProgress<StrategyGenerationLaneProgressV1>? progress)
    {
        progress?.Report(new StrategyGenerationLaneProgressV1(
            agent.Lane,
            StrategyGenerationLaneProgressStateV1.PreparingRequest));
        try
        {
            var result = await InvokeLaneAsync(provider, request, agent, ct, progress).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var state = result.Readiness is StrategyGenerationReadinessV1.Failed
                or StrategyGenerationReadinessV1.Unsupported
                or StrategyGenerationReadinessV1.Invalid
                    ? StrategyGenerationLaneProgressStateV1.Failed
                    : StrategyGenerationLaneProgressStateV1.Completed;
            var detail = state == StrategyGenerationLaneProgressStateV1.Completed
                ? result.Readiness == StrategyGenerationReadinessV1.PackageValid
                    ? "Installed package validation passed; nothing was tested or run."
                    : "Authoring contract check passed; no package validator is registered."
                : result.Issues.FirstOrDefault(static issue =>
                        issue.Severity == StrategyCandidateGenerationIssueSeverityV1.Error)?.Code
                    ?? result.Issues.FirstOrDefault()?.Code
                    ?? result.AgentRun?.Error
                    ?? "This lane was blocked.";
            // This result has passed the coordinator's lane identity/coherence checks. Publishing it
            // with terminal progress lets the UI inspect one finished artifact while sibling model
            // calls continue, without constructing or persisting an invalid partial batch.
            progress?.Report(new StrategyGenerationLaneProgressV1(
                agent.Lane,
                state,
                detail,
                result));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            progress?.Report(new StrategyGenerationLaneProgressV1(
                agent.Lane,
                StrategyGenerationLaneProgressStateV1.Canceled));
            throw;
        }
    }

    private static async Task<StrategyGenerationLaneResultV1> InvokeLaneAsync(
        IStrategyCodegenClient provider,
        ParallelStrategyGenerationRequestV1 request,
        IStrategyGenerationLaneAgentV1 agent,
        CancellationToken ct,
        IProgress<StrategyGenerationLaneProgressV1>? progress)
    {
        var candidateId = $"{request.StrategyId.Trim()}/{StrategyGenerationLaneCatalogV1.WireName(agent.Lane)}";
        try
        {
            var result = await agent.GenerateAsync(provider, request, candidateId, ct, progress).ConfigureAwait(false);
            if (result is null)
                return AgentFailure(provider, agent.Lane, "LANE_AGENT_NULL_RESULT",
                    $"The {StrategyGenerationLaneCatalogV1.DisplayName(agent.Lane)} lane returned no result.");
            if (result.Lane != agent.Lane)
                return AgentFailure(provider, agent.Lane, "LANE_AGENT_IDENTITY_CHANGED",
                    $"The {StrategyGenerationLaneCatalogV1.DisplayName(agent.Lane)} agent returned lane '{result.Lane}'.");
            if (!IsCoherentLaneResult(
                    result,
                    agent.Lane,
                    candidateId,
                    StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                        request.StrategyId,
                        request.UserPrompt,
                        agent.Lane),
                    out var reason))
                return AgentFailure(provider, agent.Lane, "LANE_AGENT_RESULT_INVALID",
                    $"The {StrategyGenerationLaneCatalogV1.DisplayName(agent.Lane)} lane returned an invalid result: {reason}");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"The {StrategyGenerationLaneCatalogV1.DisplayName(agent.Lane)} lane failed: {exception.Message}";
            return AgentFailure(provider, agent.Lane, "LANE_AGENT_EXCEPTION", message, exception.Message);
        }
    }

    private static StrategyGenerationLaneResultV1 AgentFailure(
        IStrategyCodegenClient provider,
        StrategyGenerationLaneV1 lane,
        string code,
        string message,
        string? runError = null)
    {
        return new StrategyGenerationLaneResultV1(
            lane,
            StrategyGenerationReadinessV1.Failed,
            null,
            null,
            [new StrategyCandidateGenerationIssueV1(
                StrategyCandidateGenerationIssueSeverityV1.Error,
                code,
                StrategyGenerationLaneCatalogV1.WireName(lane),
                message)],
            new StrategyGenerationAgentRunV1(
                ParallelStrategyGenerationPromptV1.AgentId(lane),
                provider.ProviderId,
                null,
                false,
                runError ?? message,
                null,
                CodegenUsage.None));
    }

    private static bool IsCoherentLaneResult(
        StrategyGenerationLaneResultV1 result,
        StrategyGenerationLaneV1 expectedLane,
        string expectedCandidateId,
        string expectedRequestHashSha256,
        out string reason)
    {
        if (result.AgentRun is null || result.AgentRun.Usage is null)
        {
            reason = "agent-run metadata and usage are required";
            return false;
        }
        if (result.Issues is null || result.Issues.Any(static issue => issue is null))
        {
            reason = "the issues array cannot be null or contain null entries";
            return false;
        }

        var hasBlockingIssue = result.Issues.Any(static issue =>
            issue.Severity == StrategyCandidateGenerationIssueSeverityV1.Error);
        if (result.Readiness == StrategyGenerationReadinessV1.Failed)
        {
            if (result.Candidate is not null || result.CandidateHashSha256 is not null)
            {
                reason = "a provider-failed lane cannot expose an artifact or hash";
                return false;
            }
            if (result.AgentRun.Success || !hasBlockingIssue)
            {
                reason = "a provider-failed lane requires a failed agent run and one blocking issue";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        if (result.Readiness == StrategyGenerationReadinessV1.Unsupported)
        {
            reason = "all four known lanes have generation-authoring contracts and cannot report unsupported";
            return false;
        }

        if (result.Readiness == StrategyGenerationReadinessV1.TestPassed)
        {
            reason = "the generation layer cannot claim package tests passed without package-owned test evidence";
            return false;
        }

        if (result.Readiness == StrategyGenerationReadinessV1.Invalid &&
            result.Candidate is null && result.CandidateHashSha256 is null)
        {
            if (!result.AgentRun.Success || !hasBlockingIssue)
            {
                reason = "an unparsable generated artifact requires a successful provider run and one blocking issue";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        if (result.Candidate is null || result.CandidateHashSha256 is null || !result.AgentRun.Success)
        {
            reason = "a generated lane requires an artifact, its exact hash, and a successful agent run";
            return false;
        }

        var candidateIssues = StrategyGenerationCandidateValidatorV1.Validate(
            result.Candidate,
            expectedLane,
            expectedCandidateId,
            expectedRequestHashSha256);
        var candidateValid = candidateIssues.All(static issue =>
            issue.Severity != StrategyCandidateGenerationIssueSeverityV1.Error);
        if (!result.Issues.SequenceEqual(candidateIssues))
        {
            reason = "the returned issues do not match deterministic candidate validation";
            return false;
        }

        var expectedReadiness = candidateValid
            ? StrategyGenerationPackageCatalogV1.PackageValidationAvailable(expectedLane)
                ? StrategyGenerationReadinessV1.PackageValid
                : StrategyGenerationReadinessV1.Generated
            : StrategyGenerationReadinessV1.Invalid;
        if (result.Readiness != expectedReadiness)
        {
            reason = $"deterministic validation requires readiness '{expectedReadiness}'";
            return false;
        }
        if (candidateValid && hasBlockingIssue)
        {
            reason = "a valid generated lane cannot retain a blocking issue";
            return false;
        }
        if (!candidateValid && !hasBlockingIssue)
        {
            reason = "an invalid generated lane must preserve its deterministic validation error";
            return false;
        }
        if (!StrategyGenerationCandidateCanonicalJsonV1.TryHash(
                result.Candidate,
                out var actualHash,
                out var hashError))
        {
            reason = $"the candidate cannot be canonically hashed: {hashError}";
            return false;
        }
        if (!string.Equals(result.CandidateHashSha256, actualHash, StringComparison.Ordinal))
        {
            reason = "the candidate hash does not match the returned artifact";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
