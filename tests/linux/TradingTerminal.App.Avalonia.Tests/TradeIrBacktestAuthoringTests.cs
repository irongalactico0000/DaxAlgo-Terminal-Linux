using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Backtest.Engine.TradeIr;
using TradingTerminal.Core.Backtest;
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
        viewModel.ChooseGeneratedCandidateCommand.Execute(null);

        viewModel.WorkbenchTab.Should().Be(3,
            "the newly unlocked smoke action must remain visible in Candidate");
        viewModel.ChosenGeneratedCandidateHash.Should().Be(candidateHash);
        viewModel.CanPrepareGeneratedCandidateForBacktest.Should().BeTrue();
        viewModel.BacktestReadinessText.Should().Contain("submitted to the closed in-process target");
        viewModel.BacktestReadinessStages.Single(stage => stage.Step == "4").Status
            .Should().Be("CHECK ON RUN");

        await viewModel.RunTradeIrSimulatedBacktestCommand.ExecuteAsync(null);

        runner.Request.Should().NotBeNull();
        runner.Request!.SourceCandidateHashSha256.Should().Be(candidateHash);
        runner.Request.ExpectedModuleHashSha256.Should().Be(OperatorGraphModuleCanonicalJsonV1.Hash(module));
        OperatorGraphModuleCanonicalJsonV1.Hash(runner.Request.Module)
            .Should().Be(OperatorGraphModuleCanonicalJsonV1.Hash(module));
        viewModel.TradeIrBacktestStatusText.Should().Be("ADMISSION BLOCKED");
        viewModel.TradeIrBacktestIssueText.Should().Contain("TEST_BLOCKED · test.target");

        viewModel.SelectedFile!.Content += Environment.NewLine;

        viewModel.CanPrepareGeneratedCandidateForBacktest.Should().BeFalse(
            "editing the active bytes must clear the exact generated-hash proof");
        viewModel.TradeIrBacktestResult.Should().BeNull(
            "a result for the pre-edit hash must not remain attached to edited bytes");
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
                [new TradeIrSimulatedBacktestIssueV1("TEST_BLOCKED", "test.target", "Deliberate fixture rejection.")]));
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
