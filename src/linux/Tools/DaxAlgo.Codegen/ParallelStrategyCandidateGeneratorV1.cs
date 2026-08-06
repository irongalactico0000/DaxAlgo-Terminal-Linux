using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Conservative host-owned gate for the single optional repair call. It prefers stopping with an
/// actionable issue over asking a model to invent facts or approximate unsupported semantics.
/// </summary>
internal static class StrategyGenerationAutomaticRepairClassifierV1
{
    private const string TradeIrNeedsFactsCode = "LANE_TRADEIR_NEEDS_SHARED_FACTS";
    private const string TradeIrUnsupportedSemanticCode = "LANE_TRADEIR_UNSUPPORTED_SEMANTIC";

    public static StrategyGenerationAutomaticRepairDispositionV1 Classify(
        StrategyGenerationLaneResultV1? result)
    {
        if (result?.Readiness != StrategyGenerationReadinessV1.Invalid)
            return StrategyGenerationAutomaticRepairDispositionV1.NotApplicable;

        var errors = result.Issues?
            .Where(static issue => issue is not null &&
                issue.Severity == StrategyCandidateGenerationIssueSeverityV1.Error)
            .ToArray() ?? [];
        if (errors.Length == 0)
            return StrategyGenerationAutomaticRepairDispositionV1.NonRepairable;

        if (errors.Any(IsMissingSharedFact))
            return StrategyGenerationAutomaticRepairDispositionV1.NeedsSharedFacts;
        if (errors.Any(static issue =>
                string.Equals(issue.Code, TradeIrUnsupportedSemanticCode, StringComparison.Ordinal)))
            return StrategyGenerationAutomaticRepairDispositionV1.UnsupportedSemantic;

        return errors.All(IsRepairableModelOutput)
            ? StrategyGenerationAutomaticRepairDispositionV1.RepairableModelOutput
            : StrategyGenerationAutomaticRepairDispositionV1.NonRepairable;
    }

    private static bool IsMissingSharedFact(StrategyCandidateGenerationIssueV1 issue)
    {
        if (string.Equals(issue.Code, TradeIrNeedsFactsCode, StringComparison.Ordinal))
            return true;

        // Declarative Rules v1 has no resolved host catalog/facts artifact yet. Only exact paths
        // whose values must come from AuthoringFacts are classified here. Closed-schema noise such
        // as an unknown property remains a shape error even when nested under one of these objects.
        if (!issue.Code.StartsWith("LANE_SPEC_", StringComparison.Ordinal) &&
            !string.Equals(issue.Code, "LANE_ARTIFACT_SECTION_REQUIRED", StringComparison.Ordinal))
            return false;
        if (string.Equals(issue.Code, "LANE_SPEC_PROPERTY_UNKNOWN", StringComparison.Ordinal))
            return false;

        var path = issue.Path ?? string.Empty;
        if (path is "artifact.document.clock.timezone" or
            "artifact.document.clock.sessionCalendar" or
            "artifact.document.clock.decisionTiming" or
            "artifact.document.clock.interval")
            return true;
        if (string.Equals(path, "artifact.document.dataRequirements", StringComparison.Ordinal))
            return true;
        if (!path.StartsWith("artifact.document.dataRequirements[", StringComparison.Ordinal))
            return false;

        var itemPathEnd = path.IndexOf(']');
        if (itemPathEnd < 0 || itemPathEnd + 1 >= path.Length)
            return false;
        var factPath = path[(itemPathEnd + 1)..];
        return factPath is ".dataKind" or
               ".normalizationPolicy" or
               ".missingDataPolicy" or
               ".revisionPolicy" ||
               factPath.StartsWith(".instrumentSelector", StringComparison.Ordinal) ||
               factPath.StartsWith(".eventSchema", StringComparison.Ordinal) ||
               factPath.StartsWith(".temporalSemantics", StringComparison.Ordinal);
    }

    private static bool IsRepairableModelOutput(StrategyCandidateGenerationIssueV1 issue)
    {
        var code = issue.Code ?? string.Empty;
        if (code is "LANE_JSON_INVALID" or
            "LANE_CANDIDATE_REQUIRED" or
            "LANE_TRADEIR_JSON_INVALID" or
            "LANE_TRADEIR_MODULE_KIND_INVALID" or
            "LANE_TRADEIR_PACKAGE_INVALID")
            return true;

        if (code.StartsWith("LANE_SPEC_", StringComparison.Ordinal) ||
            code.StartsWith("LANE_VIBE_", StringComparison.Ordinal) ||
            code.StartsWith("LANE_CSP_", StringComparison.Ordinal) ||
            code.StartsWith("LANE_PYTHON_", StringComparison.Ordinal))
            return true;

        return code is "LANE_PARAMETERS_REQUIRED" or
            "LANE_TEXT_REQUIRED" or
            "LANE_ARRAY_REQUIRED" or
            "LANE_ARRAY_EMPTY" or
            "LANE_PARAMETER_NULL" or
            "LANE_PARAMETER_DUPLICATE" or
            "LANE_VARIATION_AXES_REQUIRED" or
            "LANE_VARIATION_AXIS_NULL" or
            "LANE_VARIATION_AXIS_KIND_INVALID" or
            "LANE_VARIATION_AXIS_DUPLICATE" or
            "LANE_ARTIFACT_REQUIRED" or
            "LANE_ARTIFACT_SECTION_REQUIRED" or
            "LANE_ARTIFACT_SECTION_TYPE_INVALID" or
            "LANE_ARTIFACT_DOCUMENT_REQUIRED" or
            "LANE_ARTIFACT_SOURCE_REQUIRED" or
            "LANE_ARTIFACT_TOO_LARGE";
    }
}

/// <summary>
/// One format-specific authoring request and deterministic validation pass. Every lane makes one
/// initial model call and at most one bounded repair call for proven response-shape errors. Missing
/// shared facts, unsupported semantics, and runtime-readiness gaps never trigger blind model repair;
/// this layer never compiles, runs, imports, packages, or tests an artifact.
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
        var originalUserMessage = ParallelStrategyGenerationPromptV1.UserMessage(request);
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
        if (firstResult.AutomaticRepairDisposition !=
            StrategyGenerationAutomaticRepairDispositionV1.RepairableModelOutput)
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

        if (!TryDeserializeCandidate(
                raw,
                request,
                expectedCandidateId,
                out var candidate,
                out var parseError))
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

    private bool TryDeserializeCandidate(
        string? raw,
        ParallelStrategyGenerationRequestV1 request,
        string expectedCandidateId,
        out StrategyGenerationCandidateV1? candidate,
        out string error)
    {
        if (TryDeserializeDraftRoot(raw, out var root, out error))
            return TryBindHostCandidate(
                Lane,
                root,
                expectedCandidateId,
                StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                    request.StrategyId,
                    request.UserPrompt,
                    Lane),
                request.StrategyId,
                out candidate,
                out error);

        candidate = null;
        return false;
    }

    private static bool TryDeserializeDraftRoot(
        string? raw,
        out JsonElement root,
        out string error)
    {
        if (StrategyModelJsonV1.TryDeserialize(
                raw,
                StrategyCandidateGenerationOrchestratorV1.MaxModelResponseCharacters,
                out root,
                out error))
            return true;

        if (string.IsNullOrWhiteSpace(raw) ||
            raw.Length > StrategyCandidateGenerationOrchestratorV1.MaxModelResponseCharacters ||
            !TryExtractSingleJsonObject(raw, out var extracted))
        {
            root = default;
            return false;
        }

        return StrategyModelJsonV1.TryDeserialize(
            extracted,
            StrategyCandidateGenerationOrchestratorV1.MaxModelResponseCharacters,
            out root,
            out error);
    }

    /// <summary>
    /// Converts untrusted model-owned draft content into the canonical host-owned candidate. Legacy
    /// full envelopes are accepted during migration, but their identity, provenance, package binding,
    /// and artifact metadata are deliberately never read.
    /// </summary>
    internal static bool TryBindHostCandidate(
        StrategyGenerationLaneV1 lane,
        JsonElement root,
        string expectedCandidateId,
        string expectedRequestHashSha256,
        string expectedStrategyId,
        out StrategyGenerationCandidateV1? candidate,
        out string error)
    {
        candidate = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "The model response must be one lane-draft JSON object.";
            return false;
        }

        try
        {
            var artifactPayload = RequireProperty(root, "artifact");
            candidate = new StrategyGenerationCandidateV1(
                StrategyGenerationCandidateV1.CurrentSchemaVersion,
                expectedCandidateId,
                lane,
                expectedRequestHashSha256,
                StrategyGenerationPackageCatalogV1.RequireBinding(lane),
                DeserializeProperty<string>(root, "title"),
                DeserializeProperty<string>(root, "interpretation"),
                DeserializeProperty<IReadOnlyList<string>>(root, "unresolvedQuestions"),
                DeserializeProperty<IReadOnlyList<string>>(root, "assumptions"),
                DeserializeProperty<IReadOnlyList<StrategyGenerationParameterV1>>(root, "parameters"),
                DeserializeProperty<IReadOnlyList<StrategyVariationAxisV1>>(root, "variationAxes"),
                BindArtifact(lane, artifactPayload, expectedStrategyId),
                DeserializeProperty<string>(root, "explanation"),
                DeserializeProperty<IReadOnlyList<string>>(root, "proposedTests"));
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or
            FormatException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            error = $"The model JSON does not match the lane-draft contract: {exception.Message}";
            return false;
        }
    }

    private static StrategyGenerationArtifactV1 BindArtifact(
        StrategyGenerationLaneV1 lane,
        JsonElement artifactPayload,
        string expectedStrategyId)
    {
        string? source = null;
        JsonElement? document = null;

        if (lane is StrategyGenerationLaneV1.VibePython or StrategyGenerationLaneV1.CspPython)
        {
            var sourceElement = artifactPayload;
            if (artifactPayload.ValueKind == JsonValueKind.Object &&
                artifactPayload.TryGetProperty("source", out var wrappedSource))
                sourceElement = wrappedSource;
            if (sourceElement.ValueKind != JsonValueKind.String)
                throw new JsonException(
                    "The artifact must be a direct Python source string or a legacy object containing string property 'source'.");
            source = sourceElement.GetString();
        }
        else
        {
            var documentElement = artifactPayload;
            if (artifactPayload.ValueKind == JsonValueKind.Object &&
                artifactPayload.TryGetProperty("document", out var wrappedDocument))
                documentElement = wrappedDocument;
            if (documentElement.ValueKind != JsonValueKind.Object)
                throw new JsonException(
                    "The artifact must be a direct JSON document object or a legacy object containing object property 'document'.");
            document = lane switch
            {
                StrategyGenerationLaneV1.TypedGraph =>
                    BindHostOwnedTradeIrMetadata(documentElement, expectedStrategyId),
                StrategyGenerationLaneV1.DeclarativeSpec =>
                    BindHostOwnedDeclarativeIdentity(documentElement, expectedStrategyId),
                _ => documentElement.Clone(),
            };
        }

        return new StrategyGenerationArtifactV1(
            StrategyGenerationLaneCatalogV1.ArtifactKind(lane),
            StrategyGenerationPackageCatalogV1.ArtifactFileName(lane),
            StrategyGenerationPackageCatalogV1.ArtifactLanguage(lane),
            source,
            document);
    }

    private static JsonElement BindHostOwnedTradeIrMetadata(
        JsonElement document,
        string expectedStrategyId)
    {
        var root = JsonNode.Parse(document.GetRawText()) as JsonObject;
        if (root?["definition"] is not JsonObject definition)
            return document.Clone();

        var catalog = StrategyGenerationPackageCatalogV1
            .RequireBinding(StrategyGenerationLaneV1.TypedGraph)
            .OperatorCatalog
            ?? throw new InvalidOperationException("The installed TradeIR binding has no operator catalog.");
        definition["operatorCatalog"] = JsonNode.Parse(
            ExecutableStrategyDefinitionCanonicalJson.Serialize(catalog));
        definition["strategyId"] = expectedStrategyId.Trim();

        using var rebound = JsonDocument.Parse(root.ToJsonString());
        return rebound.RootElement.Clone();
    }

    private static JsonElement BindHostOwnedDeclarativeIdentity(
        JsonElement document,
        string expectedStrategyId)
    {
        var root = JsonNode.Parse(document.GetRawText()) as JsonObject;
        if (root?["strategy"] is not JsonObject strategy)
            return document.Clone();

        strategy["id"] = expectedStrategyId.Trim();
        using var rebound = JsonDocument.Parse(root.ToJsonString());
        return rebound.RootElement.Clone();
    }

    private static T DeserializeProperty<T>(JsonElement root, string propertyName)
    {
        var property = RequireProperty(root, propertyName);
        return ExecutableStrategyDefinitionCanonicalJson.Deserialize<T>(property.GetRawText());
    }

    private static JsonElement RequireProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
            throw new JsonException($"Required model-owned property '{propertyName}' is missing.");
        return property;
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
