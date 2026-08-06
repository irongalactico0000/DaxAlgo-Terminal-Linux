using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class CandidateRestoreRecoveryTests
{
    [Fact]
    public async Task Replacement_generation_keeps_last_completed_batch_through_stop_and_restart()
    {
        const string strategyId = "saved-strategy";
        const string oldPrompt = "Compare two causal moving averages.";
        var batch = ValidBatchWithDeclarativeCandidate(strategyId, oldPrompt);
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "Saved strategy",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, oldPrompt, DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile("Strategy.cs", "// unchanged editor")],
            GenerateCandidateFirst: true,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(batch),
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var sessions = new MemoryAuthoringSessionRepository(saved);
        var provider = new StubCodegenClient();
        var generator = new CancelableParallelGenerator();

        using (var viewModel = new StrategyAuthoringViewModel(
                   new StubCompiler(),
                   new StubRegistry(),
                   NullLogger<StrategyAuthoringViewModel>.Instance,
                   ai: new StubAiStrategyBuilder(provider),
                   parallelCandidateGenerator: generator,
                   sessionRepository: sessions))
        {
            viewModel.GeneratedCandidateOptions.Should().HaveCount(4);
            viewModel.Composer = "Replace the slow EMA with a 20-period EMA.";

            var send = viewModel.SendCommand.ExecuteAsync(null);
            await generator.Started.WaitAsync(TimeSpan.FromSeconds(5));

            viewModel.HasRetainedCandidateBatchDuringGeneration.Should().BeTrue();
            viewModel.GeneratedCandidateOptions.Should().HaveCount(4,
                "an in-flight replacement must not erase the last committed batch");

            viewModel.StopCommand.Execute(null);
            await send.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.GeneratedCandidateOptions.Should().HaveCount(4);
        }

        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: sessions);
        restored.GeneratedCandidateOptions.Should().HaveCount(4,
            "the preserved completed batch must survive process restart");
        restored.HasCandidateRestoreWarning.Should().BeFalse();
    }

    [Fact]
    public void Valid_saved_batch_survives_a_malformed_editor_proof_without_rebinding_it()
    {
        const string strategyId = "saved-strategy";
        const string prompt = "Compare two causal moving averages.";
        var batch = ValidBatchWithDeclarativeCandidate(strategyId, prompt);
        var spec = batch.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.DeclarativeSpec);
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "Saved strategy",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, prompt, DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile("strategy.spec.json", string.Empty)],
            GenerateCandidateFirst: true,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(batch),
            SelectedParallelCandidateHash: spec.CandidateHashSha256,
            EditorBaseParallelCandidateHash: spec.CandidateHashSha256,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);

        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository(saved));

        viewModel.GeneratedCandidateOptions.Should().HaveCount(4,
            "the valid batch was applied before editor-proof comparison");
        viewModel.HasCandidateRestoreWarning.Should().BeFalse(
            "a malformed editor copy invalidates only its proof, not a separately valid batch");
        viewModel.ChosenGeneratedCandidateHash.Should().BeNull();
        viewModel.Status.Should().Contain("proof did not match");
    }

    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public async Task Stale_saved_candidate_batch_keeps_chat_and_code_but_requires_explicit_fresh_generation()
    {
        const string strategyId = "saved-strategy";
        const string prompt = "Fade a liquidity sweep at the prior-day low.";
        var provider = new StubCodegenClient();
        var generator = new RecordingParallelGenerator();
        var staleBatch = new ParallelStrategyGenerationResultV1(
            strategyId,
            prompt,
            StrategyGenerationCandidateCanonicalJsonV1.PromptHash(strategyId, prompt),
            [],
            CodegenUsage.None);
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "Saved strategy",
            Chat:
            [
                new AuthoringChatEntry(AuthoringChatEntry.User, prompt, DateTime.Now),
            ],
            Thread: [],
            Files: [new StrategyFile("SavedStrategy.cs", "// saved code")],
            GenerateCandidateFirst: true,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(staleBatch),
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);

        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(provider),
            parallelCandidateGenerator: generator,
            sessionRepository: new MemoryAuthoringSessionRepository(saved));

        generator.CallCount.Should().Be(0, "restoring a stale batch must never send an AI request");
        viewModel.HasCandidateRestoreWarning.Should().BeTrue();
        viewModel.CandidateRestoreWarning.Should().Contain("chat and code were kept");
        viewModel.CandidateRestoreWarning.Should().Contain("Regenerate 4 candidates");
        viewModel.CandidateRestoreWarning.Should().Contain("original brief is loaded");
        viewModel.Composer.Should().Be(prompt, "recovery should not require copying from the transcript");
        viewModel.WorkbenchTab.Should().Be(3, "the recovery instruction belongs in the Candidate tab");
        viewModel.GeneratedCandidateOptions.Should().BeEmpty("stale validation proofs must not be rebound");
        viewModel.SelectedGeneratedCandidateOption.Should().BeNull();
        viewModel.ChosenGeneratedCandidateHash.Should().BeNull();
        viewModel.Messages.Should().ContainSingle(message => message.Text == prompt);
        viewModel.Files.Should().ContainSingle(file =>
            file.Name == "SavedStrategy.cs" && file.Content == "// saved code");
        viewModel.RegenerateRecoveredCandidatesCommand.CanExecute(null).Should().BeTrue();

        await viewModel.RegenerateRecoveredCandidatesCommand.ExecuteAsync(null);

        generator.CallCount.Should().Be(1, "only the explicit recovery action may replay the brief");
        generator.LastRequest.Should().Be(new ParallelStrategyGenerationRequestV1(strategyId, prompt));
        viewModel.HasCandidateRestoreWarning.Should().BeFalse();
        viewModel.GeneratedCandidateOptions.Should().HaveCount(4);

        viewModel.NewChatCommand.Execute(null);

        viewModel.HasCandidateRestoreWarning.Should().BeFalse();
        viewModel.CandidateRestoreWarning.Should().BeNull();
    }

    [Fact]
    public void Retired_batch_shape_recovers_its_raw_prompt_before_typed_deserialization_fails()
    {
        const string strategyId = "saved-strategy";
        const string prompt = "Trade a causal opening-range rejection.";
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "Saved strategy",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, "Later follow-up", DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile("SavedStrategy.cs", "// saved code")],
            GenerateCandidateFirst: true,
            ParallelCandidateBatchJson: $$"""
                {
                  "strategyId": {{JsonSerializer.Serialize(strategyId)}},
                  "userPrompt": {{JsonSerializer.Serialize(prompt)}},
                  "promptHashSha256": "retired",
                  "lanes": "retired-contract-shape",
                  "usage": {}
                }
                """,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);

        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository(saved));

        viewModel.Composer.Should().Be(prompt,
            "prompt recovery must not depend on successfully deserializing the retired batch type");
        viewModel.HasCandidateRestoreWarning.Should().BeTrue();
        viewModel.GeneratedCandidateOptions.Should().BeEmpty();
    }

    private static ParallelStrategyGenerationResultV1 ValidBatchWithDeclarativeCandidate(
        string strategyId,
        string prompt)
    {
        var lane = StrategyGenerationLaneV1.DeclarativeSpec;
        var candidateId = $"{strategyId}/{StrategyGenerationLaneCatalogV1.WireName(lane)}";
        using var document = JsonDocument.Parse($$"""
            {
              "schemaVersion": "vibe-quant/declarative-rules/v1",
              "strategy": {
                "id": {{JsonSerializer.Serialize(strategyId)}},
                "version": "1.0.0",
                "displayName": "Causal EMA cross",
                "summary": "Causal EMA cross"
              },
              "clock": {
                "basis": "eventTime",
                "timezone": "UTC",
                "sessionCalendar": "24x7",
                "decisionTiming": "onEvent",
                "interval": null
              },
              "operatorCatalog": {
                "catalogId": "review-required",
                "catalogVersion": "1",
                "catalogHashSha256": "0000000000000000000000000000000000000000000000000000000000000000"
              },
              "parameters": [],
              "dataRequirements": [
                {
                  "id": "quotes",
                  "dataKind": "quoteL1",
                  "instrumentSelector": {
                    "mode": "references",
                    "references": [{
                      "instrumentKey": "review-required",
                      "assetClass": "future",
                      "symbol": "ES",
                      "venue": "CME",
                      "currency": "USD"
                    }],
                    "universeId": null
                  },
                  "eventSchema": {
                    "schemaId": "quote-l1",
                    "schemaVersion": 1,
                    "schemaHashSha256": "0000000000000000000000000000000000000000000000000000000000000000",
                    "payloadFields": ["bid", "ask"]
                  },
                  "temporalSemantics": {
                    "eventTimeBasis": "occurredAtUtc",
                    "interval": null,
                    "requireAuthoritativeEventTime": true,
                    "requirePointInTimeAvailability": true
                  },
                  "normalizationPolicy": "rawUnadjusted",
                  "missingDataPolicy": "reject",
                  "revisionPolicy": "firstPublishedOnly",
                  "requiredSnapshotHashSha256": null
                }
              ],
              "indicators": [],
              "entryRules": [
                {
                  "id": "enter",
                  "direction": "long",
                  "condition": {"kind": "literal", "value": true},
                  "quantity": {"kind": "literal", "value": 1},
                  "order": {"type": "market", "timeInForce": "day", "limitPrice": null, "stopPrice": null},
                  "tags": []
                }
              ],
              "exitRules": [
                {
                  "id": "exit",
                  "appliesTo": "long",
                  "condition": {"kind": "literal", "value": true},
                  "action": "closePosition",
                  "quantity": null,
                  "order": {"type": "market", "timeInForce": "day", "limitPrice": null, "stopPrice": null},
                  "tags": []
                }
              ],
              "risk": {
                "maxConcurrentPositions": 1,
                "maxOrdersPerSession": null,
                "maxGrossExposure": null,
                "stopLoss": null,
                "takeProfit": null,
                "flattenAtSessionEnd": true
              },
              "outputs": [
                {
                  "id": "orders",
                  "kind": "orderIntent",
                  "source": {"kind": "entryRule", "id": "enter"}
                }
              ]
            }
            """);
        var candidate = new StrategyGenerationCandidateV1(
            StrategyGenerationCandidateV1.CurrentSchemaVersion,
            candidateId,
            lane,
            StrategyGenerationCandidateCanonicalJsonV1.RequestHash(strategyId, prompt, lane),
            StrategyGenerationPackageCatalogV1.RequireBinding(lane),
            "EMA cross",
            "Compare two causal moving averages.",
            [],
            [],
            [],
            [],
            new StrategyGenerationArtifactV1(
                StrategyGenerationArtifactKindV1.DeclarativeStrategyJson,
                "strategy.spec.json",
                "json",
                null,
                document.RootElement.Clone()),
            "Review before use.",
            ["Check the cross transitions."]);
        var issues = StrategyGenerationCandidateValidatorV1.Validate(
            candidate,
            lane,
            candidateId,
            candidate.RequestHashSha256);
        var candidateHash = StrategyGenerationCandidateCanonicalJsonV1.Hash(candidate);

        return new ParallelStrategyGenerationResultV1(
            strategyId,
            prompt,
            StrategyGenerationCandidateCanonicalJsonV1.PromptHash(strategyId, prompt),
            StrategyGenerationLaneCatalogV1.Ordered.Select(candidateLane =>
                candidateLane == lane
                    ? new StrategyGenerationLaneResultV1(
                        lane,
                        StrategyGenerationReadinessV1.Generated,
                        candidate,
                        candidateHash,
                        issues,
                        Run(lane, success: true))
                    : new StrategyGenerationLaneResultV1(
                        candidateLane,
                        StrategyGenerationReadinessV1.Failed,
                        null,
                        null,
                        [new StrategyCandidateGenerationIssueV1(
                            StrategyCandidateGenerationIssueSeverityV1.Error,
                            "PROVIDER_FAILED",
                            "agentRun",
                            "Fixture provider failure.")],
                        Run(candidateLane, success: false))).ToArray(),
            CodegenUsage.None);
    }

    private static StrategyGenerationAgentRunV1 Run(StrategyGenerationLaneV1 lane, bool success) =>
        new(
            $"test.{StrategyGenerationLaneCatalogV1.WireName(lane)}@1",
            "test",
            null,
            success,
            success ? null : "Fixture provider failure.",
            null,
            CodegenUsage.None);

    [Fact]
    public void Empty_candidate_tab_explains_stale_restore_without_presenting_old_proofs()
    {
        var root = XDocument.Load(Fixture("StrategyAuthoringWindow.axaml")).Root
            ?? throw new InvalidOperationException("The strategy authoring fixture has no root element.");
        var recovery = root.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "Saved candidate recovery"));

        recovery.Attribute("IsVisible")!.Value.Should().Be("{Binding HasCandidateRestoreWarning}");
        recovery.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding CandidateRestoreWarning}");
        recovery.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "Nothing was sent automatically. Review the recovered brief, then regenerate when ready.");
        var regenerate = recovery.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Regenerate four candidates from recovered brief");
        regenerate.Attribute("Content")!.Value.Should().Be("Regenerate 4 candidates");
        regenerate.Attribute("Command")!.Value.Should().Be("{Binding RegenerateRecoveredCandidatesCommand}");
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class MemoryAuthoringSessionRepository(params AuthoringSessionSnapshot[] sessions)
        : IAuthoringSessionRepository
    {
        private readonly List<AuthoringSessionSnapshot> _sessions = [.. sessions];

        public IReadOnlyList<AuthoringSessionSnapshot> List() =>
            [.. _sessions.OrderByDescending(static session => session.UpdatedUtc)];

        public bool Save(AuthoringSessionSnapshot session)
        {
            _sessions.RemoveAll(existing => existing.StrategyId == session.StrategyId);
            _sessions.Add(session with { UpdatedUtc = DateTime.UtcNow });
            return true;
        }

        public void Delete(string strategyId) =>
            _sessions.RemoveAll(session => session.StrategyId == strategyId);
    }

    private sealed class StubCompiler : IStrategyCompiler
    {
        public StrategyCompileResult Compile(StrategyScript script) => StrategyCompileResult.Failed([]);
    }

    private sealed class RecordingParallelGenerator : IParallelStrategyCandidateGeneratorV1
    {
        public int CallCount { get; private set; }
        public ParallelStrategyGenerationRequestV1? LastRequest { get; private set; }

        public Task<ParallelStrategyGenerationResultV1> GenerateAsync(
            IStrategyCodegenClient provider,
            ParallelStrategyGenerationRequestV1 request,
            CancellationToken ct = default,
            IProgress<StrategyGenerationLaneProgressV1>? progress = null)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(ValidBatchWithDeclarativeCandidate(request.StrategyId, request.UserPrompt));
        }
    }

    private sealed class CancelableParallelGenerator : IParallelStrategyCandidateGeneratorV1
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task<ParallelStrategyGenerationResultV1> GenerateAsync(
            IStrategyCodegenClient provider,
            ParallelStrategyGenerationRequestV1 request,
            CancellationToken ct = default,
            IProgress<StrategyGenerationLaneProgressV1>? progress = null)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The canceled fixture must never complete.");
        }
    }

    private sealed class StubAiStrategyBuilder(IStrategyCodegenClient provider) : IAiStrategyBuilder
    {
        public IReadOnlyList<IStrategyCodegenClient> Providers => [provider];
        public IStrategyCodegenClient? DefaultProvider => provider;
        public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) => provider;
        public IReadOnlyList<string> ModelsFor(string providerId) => [];
        public IReadOnlyList<AiModelChoice> AllModels() => [];

        public StrategyBuildSession StartSession(
            IStrategyCodegenClient selectedProvider,
            string strategyId,
            string displayName,
            IReadOnlyList<CodegenMessage>? history = null,
            CodegenUsage? priorUsage = null,
            StrategyBuildProfile? profile = null) =>
            throw new NotSupportedException("Recovery generation uses the parallel generator.");

        public Task<StrategyBuildLoopResult> BuildAsync(
            IStrategyCodegenClient selectedProvider,
            string instruction,
            string strategyId,
            string displayName,
            CancellationToken ct = default) =>
            Task.FromException<StrategyBuildLoopResult>(
                new NotSupportedException("Recovery generation uses the parallel generator."));
    }

    private sealed class StubCodegenClient : IStrategyCodegenClient
    {
        public string ProviderId => "stub";
        public string DisplayName => "Stub provider";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request,
            CancellationToken ct = default) =>
            Task.FromException<StrategyCodegenResponse>(
                new NotSupportedException("Recovery generation uses the parallel generator."));
    }

    private sealed class StubRegistry : IBacktestStrategyRegistry
    {
        public IReadOnlyList<BacktestStrategyOption> All => [];
        public BacktestStrategyOption? Find(string id) => null;
        public void Register(BacktestStrategyOption option)
        {
        }

        public bool Remove(string id) => false;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
