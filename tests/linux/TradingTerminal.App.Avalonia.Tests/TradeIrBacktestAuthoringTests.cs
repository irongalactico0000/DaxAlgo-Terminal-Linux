using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Backtest.Engine.TradeIr;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Definition;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class TradeIrBacktestAuthoringTests
{
    [Fact]
    public async Task Exact_graph_selection_stays_on_candidate_and_submits_the_same_hashes_to_smoke_runner()
    {
        const string strategyId = "ema-smoke";
        const string prompt = "Build the QuoteL1 EMA smoke-compatible starter.";
        var (batch, module, candidateHash) = ValidGraphBatch(strategyId, prompt);
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "EMA smoke",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, prompt, DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile(StrategyFile.DefaultName, string.Empty)],
            GenerateCandidateFirst: true,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(batch),
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var runner = new RecordingSmokeRunner();

        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository(saved),
            tradeIrSimulatedBacktestRunner: runner);

        viewModel.SelectedGeneratedCandidateOption = viewModel.GeneratedCandidateOptions.Single(option =>
            option.Result.Lane == StrategyGenerationLaneV1.TypedGraph);

        viewModel.CanPrepareGeneratedCandidateForBacktest.Should().BeFalse(
            "previewing a valid graph does not yet bind its exact hash to the editor");
        viewModel.CanChooseGeneratedCandidate.Should().BeTrue();
        viewModel.CandidateActionText.Should().Be("Use selected in editor");
        viewModel.CandidateBacktestAvailabilityText.Should().ContainAll(
            "TEST DISABLED",
            "package-valid but preview-only",
            "Use selected in editor",
            "exact generated hash");
        viewModel.SelectedGeneratedCandidateOption.SyntheticTestCapabilityText.Should().Be(
            "Synthetic eligibility · exact hash + registered runner required");

        viewModel.ChooseGeneratedCandidateCommand.Execute(null);

        viewModel.WorkbenchTab.Should().Be(3,
            "the newly unlocked smoke action must remain visible in Candidate");
        viewModel.ChosenGeneratedCandidateHash.Should().Be(candidateHash);
        viewModel.CanPrepareGeneratedCandidateForBacktest.Should().BeTrue();
        viewModel.CandidateBacktestAvailabilityText.Should().ContainAll(
            "TEST ENABLED",
            "active at its exact package-valid hash",
            "Smoke compatibility is not proven",
            "QuoteL1 EMA smoke profile only");
        viewModel.BacktestReadinessText.Should().ContainAll(
            "Graph package validation passed; smoke compatibility has not",
            "QuoteL1 EMA smoke profile only");
        viewModel.BacktestReadinessStages.Single(stage => stage.Step == "4").Status
            .Should().Be("CHECK ON RUN");

        viewModel.TradeIrBacktestResult = SuccessfulSmokeResult(candidateHash, module);
        viewModel.CandidateBacktestAvailabilityText.Should().Contain("QuoteL1 EMA smoke passed");
        viewModel.BacktestReadinessText.Should().ContainAll(
            "QuoteL1 EMA smoke admission passed for this exact hash",
            "not historical performance or worker isolation");
        viewModel.BacktestReadinessText.Should().NotContain("compatibility has not");
        viewModel.TradeIrBacktestResult = null;

        await viewModel.RunTradeIrSimulatedBacktestCommand.ExecuteAsync(null);

        runner.Request.Should().NotBeNull();
        runner.Request!.SourceCandidateHashSha256.Should().Be(candidateHash);
        runner.Request.ExpectedModuleHashSha256.Should().Be(OperatorGraphModuleCanonicalJsonV1.Hash(module));
        OperatorGraphModuleCanonicalJsonV1.Hash(runner.Request.Module)
            .Should().Be(OperatorGraphModuleCanonicalJsonV1.Hash(module));
        viewModel.TradeIrBacktestStatusText.Should().Be("ADMISSION BLOCKED");
        viewModel.TradeIrBacktestIssueText.Should().Contain(
            "TRADEIR_PLAN_DATA_REQUIREMENT_KIND · definition.dataRequirements[0].dataKind");
        viewModel.CandidateBacktestAvailabilityText.Should().ContainAll(
            "Graph valid",
            "smoke-incompatible",
            "QuoteL1 EMA runner",
            "TRADEIR_PLAN_DATA_REQUIREMENT_KIND");
        viewModel.BacktestReadinessText.Should().ContainAll(
            "Graph package validation passed",
            "smoke-incompatible",
            "requires QuoteL1");

        viewModel.SelectedFile!.Content += Environment.NewLine;

        viewModel.CanPrepareGeneratedCandidateForBacktest.Should().BeFalse(
            "editing the active bytes must clear the exact generated-hash proof");
        viewModel.TradeIrBacktestResult.Should().BeNull(
            "a result for the pre-edit hash must not remain attached to edited bytes");
    }

    [Theory]
    [InlineData(StrategyGenerationReadinessV1.Invalid, "Graph invalid")]
    [InlineData(StrategyGenerationReadinessV1.Unsupported, "Graph unsupported")]
    public void Unavailable_graph_preview_reports_its_real_state_and_loads_the_known_smoke_starter(
        StrategyGenerationReadinessV1 readiness,
        string expectedState)
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository(),
            tradeIrSimulatedBacktestRunner: new RecordingSmokeRunner());
        viewModel.SelectedGeneratedCandidateOption = new StrategyGenerationCandidateOption(
            new StrategyGenerationLaneResultV1(
                StrategyGenerationLaneV1.TypedGraph,
                readiness,
                null,
                null,
                [new StrategyCandidateGenerationIssueV1(
                    StrategyCandidateGenerationIssueSeverityV1.Error,
                    "GRAPH_UNAVAILABLE",
                    "artifact",
                    "The Typed Graph did not pass deterministic validation.")],
                Run(StrategyGenerationLaneV1.TypedGraph, success: true)));

        viewModel.CanChooseGeneratedCandidate.Should().BeFalse();
        viewModel.CanPrepareGeneratedCandidateForBacktest.Should().BeFalse();
        viewModel.CandidateBacktestAvailabilityText.Should().ContainAll(
            "TEST DISABLED",
            expectedState,
            "GRAPH_UNAVAILABLE",
            "cannot enter the smoke runner",
            "QuoteL1 EMA smoke starter");
        viewModel.CandidateBacktestAvailabilityText.Should().NotContain("Run synthetic smoke test");
        viewModel.SelectedGeneratedCandidateOption.SyntheticTestCapabilityText.Should().Be(
            "Not testable · package-valid Graph required");

        viewModel.UseQuoteL1EmaSmokeStarterCommand.Execute(null);

        viewModel.Composer.Should().Be(StrategyStarterCatalog.QuoteL1EmaSmokePrompt);
        viewModel.AiStatus.Should().Contain("known QuoteL1 EMA smoke starter");
    }

    [Fact]
    public void Source_review_preview_still_surfaces_the_batch_graph_blocker()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository(),
            tradeIrSimulatedBacktestRunner: new RecordingSmokeRunner());
        var vibe = new StrategyGenerationCandidateOption(new StrategyGenerationLaneResultV1(
            StrategyGenerationLaneV1.VibePython,
            StrategyGenerationReadinessV1.Generated,
            null,
            null,
            [],
            Run(StrategyGenerationLaneV1.VibePython, success: true)));
        var graph = new StrategyGenerationCandidateOption(new StrategyGenerationLaneResultV1(
            StrategyGenerationLaneV1.TypedGraph,
            StrategyGenerationReadinessV1.Invalid,
            null,
            null,
            [new StrategyCandidateGenerationIssueV1(
                StrategyCandidateGenerationIssueSeverityV1.Error,
                "GRAPH_DATA_REQUIREMENT_REQUIRED",
                "$.definition.dataRequirements",
                "At least one complete data requirement is required.")],
            Run(StrategyGenerationLaneV1.TypedGraph, success: true)));
        viewModel.GeneratedCandidateOptions.Add(vibe);
        viewModel.GeneratedCandidateOptions.Add(graph);
        viewModel.SelectedGeneratedCandidateOption = vibe;

        viewModel.CandidateBacktestAvailabilityText.Should().ContainAll(
            "SELECTED LANE NOT TESTABLE",
            "Vibe · Python is source-review only",
            "no Python importer or runtime is registered",
            "Graph invalid",
            "GRAPH_DATA_REQUIREMENT_REQUIRED",
            "At least one complete data requirement is required",
            "no synthetic smoke target");
        vibe.SyntheticTestCapabilityText.Should().Be(
            "Not testable · Python importer/runtime missing");
    }

    [Theory]
    [InlineData(StrategyGenerationLaneV1.VibePython, "Python importer/runtime missing")]
    [InlineData(StrategyGenerationLaneV1.DeclarativeSpec, "Rules→TradeIR lowerer missing")]
    [InlineData(StrategyGenerationLaneV1.CspPython, "CSP host/importer missing")]
    public void Source_review_cards_name_the_missing_runtime_boundary(
        StrategyGenerationLaneV1 lane,
        string expectedBoundary)
    {
        var option = new StrategyGenerationCandidateOption(new StrategyGenerationLaneResultV1(
            lane,
            StrategyGenerationReadinessV1.Generated,
            null,
            null,
            [],
            Run(lane, success: true)));

        option.SyntheticTestCapabilityText.Should().ContainAll("Not testable", expectedBoundary);
    }

    [Theory]
    [InlineData(StrategyGenerationLaneV1.VibePython, "Vibe Python authoring profile", "Python-to-TradeIR", "Python runtime")]
    [InlineData(StrategyGenerationLaneV1.DeclarativeSpec, "Closed Rules v1 schema", "Rules-to-TradeIR", "no independent executable runtime")]
    [InlineData(StrategyGenerationLaneV1.CspPython, "Inert CSP authoring profile", "CSP-to-TradeIR", "CSP runtime host")]
    public void Source_review_readiness_names_passed_native_validation_and_first_missing_execution_stage(
        StrategyGenerationLaneV1 lane,
        string validationTitle,
        string missingLowerer,
        string missingRuntime)
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository(),
            tradeIrSimulatedBacktestRunner: new RecordingSmokeRunner());
        var option = ValidSourceReviewOption(lane);
        viewModel.GeneratedCandidateOptions.Add(option);
        viewModel.SelectedGeneratedCandidateOption = option;
        viewModel.ChosenGeneratedCandidateHash = option.CandidateHashSha256;

        var stages = viewModel.BacktestReadinessStages;

        stages.Should().HaveCount(4);
        stages[1].Title.Should().Be(validationTitle);
        stages[1].Status.Should().Be("PASSED");
        stages[1].Detail.Should().Contain("validation evidence, not execution evidence");
        stages[2].Status.Should().Be("MISSING");
        stages[2].Detail.Should().Contain(missingLowerer);
        stages[3].Status.Should().Be("LOCKED");
        stages[3].Detail.Should().Contain(missingRuntime);
    }

    [Fact]
    public void Missing_smoke_runner_has_an_explicit_disabled_reason()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.CanPrepareGeneratedCandidateForBacktest.Should().BeFalse();
        viewModel.CandidateBacktestAvailabilityText.Should().Be(
            "TEST DISABLED · the synthetic TradeIR smoke runner is not registered in this app build.");
    }

    [Fact]
    public void Pending_refinement_disables_the_exact_hash_smoke_for_the_retained_graph()
    {
        const string strategyId = "ema-smoke";
        const string prompt = "Build the QuoteL1 EMA smoke-compatible starter.";
        const string pending = "Use a faster EMA and require a wider spread filter.";
        var (batch, _, candidateHash) = ValidGraphBatch(strategyId, prompt);
        var graph = batch.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph).Candidate!;
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "EMA smoke",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, prompt, DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile(graph.Artifact.FileName, graph.Artifact.Document!.Value.GetRawText())],
            GenerateCandidateFirst: true,
            FourLaneStrategyBrief: prompt,
            PendingFourLanePrompt: pending,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(batch),
            SelectedParallelCandidateHash: candidateHash,
            EditorBaseParallelCandidateHash: candidateHash,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);

        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository(saved),
            tradeIrSimulatedBacktestRunner: new RecordingSmokeRunner());

        viewModel.HasChosenGeneratedCandidate.Should().BeTrue();
        viewModel.HasPendingFourLanePrompt.Should().BeTrue();
        viewModel.Composer.Should().Be(pending);
        viewModel.CanPrepareGeneratedCandidateForBacktest.Should().BeFalse();
        viewModel.CandidateBacktestAvailabilityText.Should().ContainAll(
            "TEST DISABLED",
            "previous completed brief",
            "restored in the composer",
            "Check & generate");

        viewModel.DiscardPendingFourLanePromptCommand.Execute(null);

        viewModel.HasPendingFourLanePrompt.Should().BeFalse();
        viewModel.Composer.Should().BeEmpty();
        viewModel.HasChosenGeneratedCandidate.Should().BeTrue();
        viewModel.CanPrepareGeneratedCandidateForBacktest.Should().BeTrue(
            "discarding only the uncommitted request restores the exact retained graph gate");
    }

    private static (ParallelStrategyGenerationResultV1 Batch, OperatorGraphModuleV1 Module, string CandidateHash)
        ValidGraphBatch(string strategyId, string prompt)
    {
        var lane = StrategyGenerationLaneV1.TypedGraph;
        var candidateId = $"{strategyId}/{StrategyGenerationLaneCatalogV1.WireName(lane)}";
        var module = CreateModule(strategyId);
        using var document = JsonDocument.Parse(OperatorGraphModuleCanonicalJsonV1.Serialize(module));
        var candidate = new StrategyGenerationCandidateV1(
            StrategyGenerationCandidateV1.CurrentSchemaVersion,
            candidateId,
            lane,
            StrategyGenerationCandidateCanonicalJsonV1.RequestHash(strategyId, prompt, lane),
            StrategyGenerationPackageCatalogV1.RequireBinding(lane),
            "QuoteL1 EMA smoke",
            "Compare two causal quote-mid EMAs and target a fixed position.",
            [],
            [],
            [],
            [],
            new StrategyGenerationArtifactV1(
                StrategyGenerationArtifactKindV1.TradeIrModuleJson,
                "strategy.tradeir.json",
                "json",
                null,
                document.RootElement.Clone()),
            "Known-supported closed-target fixture.",
            ["Run the exact-hash synthetic QuoteL1 smoke test."]);
        var issues = StrategyGenerationCandidateValidatorV1.Validate(
            candidate,
            lane,
            candidateId,
            candidate.RequestHashSha256);
        issues.Should().BeEmpty();
        var candidateHash = StrategyGenerationCandidateCanonicalJsonV1.Hash(candidate);
        var batch = new ParallelStrategyGenerationResultV1(
            strategyId,
            prompt,
            StrategyGenerationCandidateCanonicalJsonV1.PromptHash(strategyId, prompt),
            StrategyGenerationLaneCatalogV1.Ordered.Select(candidateLane =>
                candidateLane == lane
                    ? new StrategyGenerationLaneResultV1(
                        lane,
                        StrategyGenerationReadinessV1.PackageValid,
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
        StrategyGenerationBatchValidationV1.Validate(batch).Should().BeEmpty();
        return (batch, module, candidateHash);
    }

    private static StrategyGenerationCandidateOption ValidSourceReviewOption(StrategyGenerationLaneV1 lane)
    {
        const string strategyId = "source-review-readiness";
        const string prompt = "Build a deterministic source-review fixture.";
        var candidateId = $"{strategyId}/{StrategyGenerationLaneCatalogV1.WireName(lane)}";
        JsonElement? document = null;
        string? source = "# lane-native source validated before this committed fixture";
        if (lane == StrategyGenerationLaneV1.DeclarativeSpec)
        {
            using var parsed = JsonDocument.Parse("{}");
            document = parsed.RootElement.Clone();
            source = null;
        }

        var candidate = new StrategyGenerationCandidateV1(
            StrategyGenerationCandidateV1.CurrentSchemaVersion,
            candidateId,
            lane,
            StrategyGenerationCandidateCanonicalJsonV1.RequestHash(strategyId, prompt, lane),
            StrategyGenerationPackageCatalogV1.RequireBinding(lane),
            "Source review fixture",
            "Inspect the lane boundary.",
            [],
            [],
            [],
            [],
            new StrategyGenerationArtifactV1(
                StrategyGenerationLaneCatalogV1.ArtifactKind(lane),
                StrategyGenerationPackageCatalogV1.ArtifactFileName(lane),
                StrategyGenerationPackageCatalogV1.ArtifactLanguage(lane),
                source,
                document),
            "Committed validation-state fixture.",
            []);
        return new StrategyGenerationCandidateOption(new StrategyGenerationLaneResultV1(
            lane,
            StrategyGenerationReadinessV1.Generated,
            candidate,
            StrategyGenerationCandidateCanonicalJsonV1.Hash(candidate),
            [],
            Run(lane, success: true)));
    }

    private static OperatorGraphModuleV1 CreateModule(string strategyId)
    {
        var instrument = new SourceIndependentInstrumentRef(
            "equity/xnas/ALPHA",
            AssetClass.Equity,
            "ALPHA",
            "XNAS",
            "USD");
        var requirement = new DataRequirementV1(
            "quotes",
            TradeIrDataKindV1.QuoteL1,
            new SourceIndependentInstrumentSelectorV1([instrument]),
            TradeIrSimulatedBacktestContractV1.CreateEventSchema(),
            new DataTemporalSemanticsV1(
                TradeIrEventTimeBasisV1.OccurredAtUtc,
                TradeIrTimestampPrecisionV1.Microseconds,
                TradeIrEventOrderingV1.EventTimeThenSourceSequence,
                Interval: null,
                RequireAuthoritativeEventTime: true,
                RequirePointInTimeAvailability: true),
            TradeIrNormalizationPolicyV1.RawUnadjusted,
            TradeIrMissingDataPolicyV1.Reject,
            TradeIrRevisionPolicyV1.LatestAvailableAtDecisionTime,
            RequiredSnapshotHashSha256: null);
        var definition = new StrategyIntermediateRepresentationV1(
            StrategyIntermediateRepresentationV1.CurrentSchemaVersion,
            strategyId,
            "1.0.0",
            StrategyOperatorRegistryV1.CreateDefault().Catalog,
            StrategyClockKindV1.EventTime,
            [requirement],
            [
                Node("decision", "logic.greater_than", Bindings(("left", "fast"), ("right", "slow"))),
                Node("execution", "execution.market", Bindings(("target", "target")),
                    Parameters(("time_in_force", StrategyLiteralV1.FromText("day")))),
                Node("fast", "feature.ema", Bindings(("value", "price")),
                    Parameters(("period", StrategyLiteralV1.FromInteger(4)))),
                Node("price", "market.quote.mid", Bindings(),
                    Parameters(("requirement_id", StrategyLiteralV1.FromText("quotes")))),
                Node("slow", "feature.ema", Bindings(("value", "price")),
                    Parameters(("period", StrategyLiteralV1.FromInteger(12)))),
                Node("target", "portfolio.fixed_quantity", Bindings(("decision", "decision")),
                    Parameters(
                        ("when_false", StrategyLiteralV1.FromInteger(-5)),
                        ("when_true", StrategyLiteralV1.FromInteger(5)))),
            ],
            [
                new StrategyIrOutputBindingV1("order-intent", StrategyIrOutputKindV1.OrderIntent, "execution"),
                new StrategyIrOutputBindingV1("target", StrategyIrOutputKindV1.Target, "target"),
            ],
            FlattenOnEnd: true);
        return new OperatorGraphModuleV1(TradeIrModuleV1.CurrentSchemaVersion, "ema-smoke", definition);
    }

    private static StrategyIrNodeV1 Node(
        string id,
        string operatorId,
        IReadOnlyDictionary<string, string> bindings,
        IReadOnlyDictionary<string, StrategyLiteralV1>? parameters = null) =>
        new(id, operatorId, 1, bindings, parameters ?? Parameters());

    private static IReadOnlyDictionary<string, string> Bindings(
        params (string Port, string NodeId)[] values) =>
        values.ToDictionary(static value => value.Port, static value => value.NodeId, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, StrategyLiteralV1> Parameters(
        params (string Name, StrategyLiteralV1 Value)[] values) =>
        values.ToDictionary(static value => value.Name, static value => value.Value, StringComparer.Ordinal);

    private static StrategyGenerationAgentRunV1 Run(StrategyGenerationLaneV1 lane, bool success) => new(
        $"test.{StrategyGenerationLaneCatalogV1.WireName(lane)}@1",
        "test",
        null,
        success,
        success ? null : "Fixture provider failure.",
        null,
        CodegenUsage.None);

    private static TradeIrSimulatedBacktestResultV1 SuccessfulSmokeResult(
        string candidateHash,
        OperatorGraphModuleV1 module)
    {
        var moduleHash = OperatorGraphModuleCanonicalJsonV1.Hash(module);
        var placeholderHash = new string('a', 64);
        return new TradeIrSimulatedBacktestResultV1(
            TradeIrSimulatedBacktestStatusV1.Succeeded,
            new BacktestReport(
                new RunSummary(DateTime.UnixEpoch, DateTime.UnixEpoch.AddMinutes(1), 100_000, 100_000, 512, 1),
                new MetricSet(new Dictionary<string, double>
                {
                    [MetricSet.Keys.MaxDrawdown] = 0,
                }),
                [],
                [],
                []),
            new TradeIrSimulatedBacktestEvidenceV1(
                TradeIrSimulatedBacktestContractV1.ExecutionMode,
                IsWorkerIsolated: false,
                IsHistoricalData: false,
                candidateHash,
                moduleHash,
                placeholderHash,
                placeholderHash,
                placeholderHash,
                placeholderHash,
                placeholderHash,
                placeholderHash,
                placeholderHash,
                EventsProcessed: 512,
                SubmittedOrderCount: 0),
            []);
    }

    private sealed class RecordingSmokeRunner : ITradeIrSimulatedBacktestRunnerV1
    {
        public TradeIrSimulatedBacktestRequestV1? Request { get; private set; }

        public Task<TradeIrSimulatedBacktestResultV1> RunAsync(
            TradeIrSimulatedBacktestRequestV1 request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new TradeIrSimulatedBacktestResultV1(
                TradeIrSimulatedBacktestStatusV1.Rejected,
                Report: null,
                Evidence: null,
                [new TradeIrSimulatedBacktestIssueV1(
                    "TRADEIR_PLAN_DATA_REQUIREMENT_KIND",
                    "definition.dataRequirements[0].dataKind",
                    "The synthetic smoke target requires QuoteL1, not 'BarOhlcv'.")]));
        }
    }

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

    private sealed class StubRegistry : IBacktestStrategyRegistry
    {
        public IReadOnlyList<BacktestStrategyOption> All => [];
        public BacktestStrategyOption? Find(string id) => null;
        public void Register(BacktestStrategyOption option) { }
        public bool Remove(string id) => false;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
