using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class CandidateRestoreRecoveryTests
{
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
    public void Stale_saved_candidate_batch_keeps_chat_and_code_but_requires_fresh_generation()
    {
        const string strategyId = "saved-strategy";
        const string prompt = "Fade a liquidity sweep at the prior-day low.";
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
            sessionRepository: new MemoryAuthoringSessionRepository(saved));

        viewModel.HasCandidateRestoreWarning.Should().BeTrue();
        viewModel.CandidateRestoreWarning.Should().Contain("chat and code were kept");
        viewModel.CandidateRestoreWarning.Should().Contain("Check & generate");
        viewModel.CandidateRestoreWarning.Should().Contain("original brief is loaded");
        viewModel.Composer.Should().Be(prompt, "recovery should not require copying from the transcript");
        viewModel.WorkbenchTab.Should().Be(3, "the recovery instruction belongs in the Candidate tab");
        viewModel.GeneratedCandidateOptions.Should().BeEmpty("stale validation proofs must not be rebound");
        viewModel.SelectedGeneratedCandidateOption.Should().BeNull();
        viewModel.ChosenGeneratedCandidateHash.Should().BeNull();
        viewModel.Messages.Should().ContainSingle(message => message.Text == prompt);
        viewModel.Files.Should().ContainSingle(file =>
            file.Name == "SavedStrategy.cs" && file.Content == "// saved code");

        viewModel.NewChatCommand.Execute(null);

        viewModel.HasCandidateRestoreWarning.Should().BeFalse();
        viewModel.CandidateRestoreWarning.Should().BeNull();
    }

    private static ParallelStrategyGenerationResultV1 ValidBatchWithDeclarativeCandidate(
        string strategyId,
        string prompt)
    {
        var lane = StrategyGenerationLaneV1.DeclarativeSpec;
        var candidateId = $"{strategyId}/{StrategyGenerationLaneCatalogV1.WireName(lane)}";
        using var document = JsonDocument.Parse($$"""
            {
              "schemaVersion": "declarative-strategy/v1",
              "strategy": {
                "id": {{JsonSerializer.Serialize(strategyId)}},
                "summary": "Causal EMA cross",
                "universe": ["equity/xnas/ALPHA"],
                "clock": "quote"
              },
              "parameters": [
                {"name":"fast_period","type":"integer","default":4},
                {"name":"slow_period","type":"integer","default":12}
              ],
              "dataRequirements": [],
              "indicators": [
                {"id":"fast","kind":"ema","input":"mid","period":"fast_period"},
                {"id":"slow","kind":"ema","input":"mid","period":"slow_period"}
              ],
              "entryRules": [{"when":"fast > slow","target":1}],
              "exitRules": [{"when":"fast <= slow","target":0}],
              "risk": {"sizingRule":"fixed_quantity","maximumAbsoluteTarget":1}
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
                "Next: review the recovered brief in the composer, then press Check & generate.");
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
