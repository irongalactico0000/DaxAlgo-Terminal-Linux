using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class StrategyAuthoringFreshSessionTests
{
    [Fact]
    public void Generation_lane_row_exposes_real_phases_and_rejects_late_regression()
    {
        var row = new StrategyGenerationLaneProgressRow(StrategyGenerationLaneV1.TypedGraph);

        row.Apply(new StrategyGenerationLaneProgressV1(
            row.Lane,
            StrategyGenerationLaneProgressStateV1.PreparingRequest));
        row.StateLabel.Should().Be("PREPARING");
        row.PipelineText.Should().Contain("● PREPARE");

        row.Apply(new StrategyGenerationLaneProgressV1(
            row.Lane,
            StrategyGenerationLaneProgressStateV1.WaitingForModel));
        row.StateLabel.Should().Be("WAITING FOR MODEL");
        row.StateDetail.Should().Contain("waiting for the model response");

        row.Apply(new StrategyGenerationLaneProgressV1(
            row.Lane,
            StrategyGenerationLaneProgressStateV1.ValidatingArtifact));
        row.StateDetail.Should().Contain("installed package check");

        row.Apply(new StrategyGenerationLaneProgressV1(
            row.Lane,
            StrategyGenerationLaneProgressStateV1.Completed,
            "Installed package validation passed; nothing was tested or run."));
        row.StateLabel.Should().Be("READY");
        row.PipelineText.Should().Be("✓ PREPARE   ✓ MODEL   ✓ PARSE   ✓ CHECK");

        row.Apply(new StrategyGenerationLaneProgressV1(
            row.Lane,
            StrategyGenerationLaneProgressStateV1.ParsingResponse,
            "late callback"));
        row.State.Should().Be(StrategyGenerationLaneProgressStateV1.Completed);
        row.StateDetail.Should().Be("Installed package validation passed; nothing was tested or run.");
    }

    [Fact]
    public async Task Stop_marks_active_four_lane_rows_canceled_and_rejects_late_generator_output()
    {
        var provider = new StubCodegenClient();
        var generator = new NonCooperativeParallelGenerator();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(provider),
            parallelCandidateGenerator: generator,
            sessionRepository: new MemoryAuthoringSessionRepository());
        viewModel.Composer = "Generate a four-lane strategy";

        var send = viewModel.SendCommand.ExecuteAsync(null);
        await generator.Started.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.IsGenerating.Should().BeTrue();
        viewModel.GenerationLaneProgressRows.Should().HaveCount(4);
        viewModel.StopCommand.Execute(null);

        viewModel.IsGenerating.Should().BeFalse();
        viewModel.GenerationLaneProgressRows.Should().OnlyContain(row =>
            row.State == StrategyGenerationLaneProgressStateV1.Canceled);
        viewModel.GenerationProgressSummary.Should().Be("4/4 lanes finished");
        viewModel.AiStatus.Should().Be("Stopped. Late provider output will be ignored.");

        generator.CompleteWithLateResult();
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.GenerationLaneProgressRows.Should().OnlyContain(row =>
            row.State == StrategyGenerationLaneProgressStateV1.Canceled,
            "progress callbacks and completion from the invalidated epoch must be ignored");
        viewModel.GeneratedCandidateOptions.Should().BeEmpty();
        viewModel.InputTokens.Should().Be(0);
        viewModel.OutputTokens.Should().Be(0);
        viewModel.CachedTokens.Should().Be(0);
        viewModel.AiStatus.Should().Be("Stopped. Late provider output will be ignored.");
    }

    [Fact]
    public void New_strategy_clears_session_transients_without_reselecting_the_saved_chat()
    {
        var saved = new AuthoringSessionSnapshot(
            StrategyId: "saved-strategy",
            DisplayName: "Saved strategy",
            Chat:
            [
                new AuthoringChatEntry(
                    AuthoringChatEntry.User,
                    "Keep this saved conversation",
                    DateTime.Now),
            ],
            Thread: [],
            Files: [new StrategyFile("Saved.cs", "// saved")],
            InputTokens: 120,
            OutputTokens: 30,
            GenerateCandidateFirst: false,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var sessions = new MemoryAuthoringSessionRepository(saved);
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: sessions);

        viewModel.SelectedSavedSession.Should().NotBeNull();
        viewModel.Composer = "unsent follow-up";
        viewModel.StarterSearchText = "futures";
        viewModel.SelectedStarterFamily = viewModel.StarterFamilyOptions[1];
        viewModel.SelectedStarterHorizon = viewModel.StarterHorizonOptions[1];
        viewModel.SelectedStarterData = viewModel.StarterDataOptions[1];
        viewModel.InputTokens = 321;
        viewModel.OutputTokens = 123;
        viewModel.CachedTokens = 99;
        viewModel.WorkbenchTab = 3;
        viewModel.Activity.Add("old activity");
        viewModel.Tasks.Add(new BuildTask("old task"));
        viewModel.AiStatus = "old provider status";
        viewModel.ElapsedText = "2m elapsed";
        viewModel.ElapsedCompact = "2:00";
        viewModel.WorkingVerb = "Generating…";
        viewModel.StepText = "step 3 of 4";
        viewModel.CompiledOk = true;
        viewModel.IsRegistered = true;
        viewModel.AwaitingAnswer = true;
        viewModel.ReviewOpen = true;

        viewModel.NewChatCommand.Execute(null);

        sessions.SaveCalls.Should().Be(1, "the outgoing saved conversation must remain available");
        viewModel.SavedSessions.Should().ContainSingle(session => session.StrategyId == "saved-strategy");
        viewModel.SelectedSavedSession.Should().BeNull("a new strategy is not a restored session");
        viewModel.StrategyId.Should().Be("myStrategy");
        viewModel.DisplayName.Should().Be("My custom strategy");
        viewModel.GenerateCandidateFirst.Should().BeTrue();
        viewModel.Composer.Should().BeEmpty();
        viewModel.StarterSearchText.Should().BeEmpty();
        viewModel.SelectedStarterFamily.Should().Be(viewModel.StarterFamilyOptions[0]);
        viewModel.SelectedStarterHorizon.Should().Be(viewModel.StarterHorizonOptions[0]);
        viewModel.SelectedStarterData.Should().Be(viewModel.StarterDataOptions[0]);
        viewModel.VisibleStarterBriefs.Should().HaveCount(viewModel.AllStarterBriefs.Count);
        viewModel.InputTokens.Should().Be(0);
        viewModel.OutputTokens.Should().Be(0);
        viewModel.CachedTokens.Should().Be(0);
        viewModel.WorkbenchTab.Should().Be(0);
        viewModel.Messages.Should().BeEmpty();
        viewModel.Activity.Should().BeEmpty();
        viewModel.Tasks.Should().BeEmpty();
        viewModel.Diagnostics.Should().BeEmpty();
        viewModel.AiStatus.Should().BeNull();
        viewModel.ElapsedText.Should().BeNull();
        viewModel.ElapsedCompact.Should().BeNull();
        viewModel.WorkingVerb.Should().BeNull();
        viewModel.StepText.Should().BeNull();
        viewModel.CompiledOk.Should().BeFalse();
        viewModel.IsRegistered.Should().BeFalse();
        viewModel.AwaitingAnswer.Should().BeFalse();
        viewModel.ReviewOpen.Should().BeFalse();
        viewModel.Files.Should().ContainSingle(file => file.Name == StrategyFile.DefaultName);
        viewModel.SelectedFile.Should().BeSameAs(viewModel.Files[0]);
    }

    private sealed class MemoryAuthoringSessionRepository(params AuthoringSessionSnapshot[] sessions)
        : IAuthoringSessionRepository
    {
        private readonly List<AuthoringSessionSnapshot> _sessions = [.. sessions];

        public int SaveCalls { get; private set; }

        public IReadOnlyList<AuthoringSessionSnapshot> List() =>
            [.. _sessions.OrderByDescending(static session => session.UpdatedUtc)];

        public bool Save(AuthoringSessionSnapshot session)
        {
            SaveCalls++;
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

    private sealed class NonCooperativeParallelGenerator : IParallelStrategyCandidateGeneratorV1
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ParallelStrategyGenerationResultV1> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task<ParallelStrategyGenerationResultV1> GenerateAsync(
            IStrategyCodegenClient provider,
            ParallelStrategyGenerationRequestV1 request,
            CancellationToken ct = default,
            IProgress<StrategyGenerationLaneProgressV1>? progress = null)
        {
            foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered)
            {
                progress?.Report(new StrategyGenerationLaneProgressV1(
                    lane,
                    StrategyGenerationLaneProgressStateV1.Queued));
                progress?.Report(new StrategyGenerationLaneProgressV1(
                    lane,
                    StrategyGenerationLaneProgressStateV1.WaitingForModel));
            }

            _started.TrySetResult();
            return _completion.Task;
        }

        public void CompleteWithLateResult() => _completion.TrySetResult(
            new ParallelStrategyGenerationResultV1(
                "late-strategy",
                "late prompt",
                "late hash",
                [],
                new CodegenUsage(700, 300, 200)));
    }

    private sealed class StubAiStrategyBuilder(IStrategyCodegenClient provider) : IAiStrategyBuilder
    {
        public IReadOnlyList<IStrategyCodegenClient> Providers => [provider];
        public IStrategyCodegenClient DefaultProvider => provider;
        public IStrategyCodegenClient WithSettings(string providerId, string? model, CodegenEffort effort) => provider;
        public IReadOnlyList<string> ModelsFor(string providerId) => [];
        public IReadOnlyList<AiModelChoice> AllModels() => [];

        public StrategyBuildSession StartSession(
            IStrategyCodegenClient selectedProvider,
            string strategyId,
            string displayName,
            IReadOnlyList<CodegenMessage>? history = null,
            CodegenUsage? priorUsage = null,
            StrategyBuildProfile? profile = null) =>
            throw new NotSupportedException("The parallel-candidate test never starts an expert-code session.");

        public Task<StrategyBuildLoopResult> BuildAsync(
            IStrategyCodegenClient selectedProvider,
            string instruction,
            string strategyId,
            string displayName,
            CancellationToken ct = default) =>
            Task.FromException<StrategyBuildLoopResult>(
                new NotSupportedException("The parallel-candidate test never starts a build loop."));
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
                new NotSupportedException("The parallel generator owns this test call."));
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
