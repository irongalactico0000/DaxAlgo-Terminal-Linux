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
    public async Task Explicit_regenerate_reuses_the_preserved_brief_without_composer_text()
    {
        const string originalBrief =
            "Buy a causal opening-range breakout and trail a 14-period ATR stop.";
        var generator = new RecordingParallelGenerator();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: generator,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.Composer = originalBrief;
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.Composer.Should().BeEmpty();
        viewModel.RegenerateFourCandidatesCommand.CanExecute(null).Should().BeTrue();
        await viewModel.RegenerateFourCandidatesCommand.ExecuteAsync(null);

        generator.CallCount.Should().Be(2);
        generator.LastRequest.Should().Be(new ParallelStrategyGenerationRequestV1(
            viewModel.StrategyId,
            originalBrief));
        viewModel.Composer.Should().BeEmpty();
    }

    [Fact]
    public async Task Four_lane_refinement_carries_and_persists_the_original_strategy_brief()
    {
        const string originalBrief =
            "Buy the opening-range breakout, use a 14-period ATR stop, and flatten before the close.";
        const string refinement = "Run backtest with a 20 period ATR.";
        var sessions = new MemoryAuthoringSessionRepository();
        var generator = new RecordingParallelGenerator();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: generator,
            sessionRepository: sessions);

        viewModel.Composer = originalBrief;
        await viewModel.SendCommand.ExecuteAsync(null);

        generator.CallCount.Should().Be(1);
        generator.LastRequest!.UserPrompt.Should().Be(originalBrief);
        sessions.List().Should().ContainSingle()
            .Which.FourLaneStrategyBrief.Should().Be(originalBrief);

        viewModel.Composer = refinement;
        await viewModel.SendCommand.ExecuteAsync(null);

        generator.CallCount.Should().Be(2);
        generator.LastRequest!.UserPrompt.Should().ContainAll(
            originalBrief,
            "Follow-up refinement",
            refinement);
        sessions.List().Should().ContainSingle()
            .Which.FourLaneStrategyBrief.Should().Be(generator.LastRequest.UserPrompt);
    }

    [Fact]
    public async Task Backtest_navigation_preserves_the_batch_and_brief_across_restore()
    {
        const string originalBrief =
            "Trade a causal momentum breakout with an ATR stop and flatten before the close.";
        const string backtestNavigation = "gow to backtest";
        const string refinementAfterRestore = "Require volume above its 20-period average.";
        var sessions = new MemoryAuthoringSessionRepository();
        var initialGenerator = new RecordingParallelGenerator();

        using (var viewModel = new StrategyAuthoringViewModel(
                   new StubCompiler(),
                   new StubRegistry(),
                   NullLogger<StrategyAuthoringViewModel>.Instance,
                   ai: new StubAiStrategyBuilder(new StubCodegenClient()),
                   parallelCandidateGenerator: initialGenerator,
                   sessionRepository: sessions))
        {
            viewModel.Composer = originalBrief;
            await viewModel.SendCommand.ExecuteAsync(null);
            var originalBatchJson = sessions.List().Single().ParallelCandidateBatchJson;

            viewModel.Composer = backtestNavigation;
            await viewModel.SendCommand.ExecuteAsync(null);

            initialGenerator.CallCount.Should().Be(1,
                "backtest navigation is not a replacement strategy-generation request");
            viewModel.GeneratedCandidateOptions.Should().HaveCount(4);
            viewModel.WorkbenchTab.Should().Be(3);
            viewModel.AiStatus.Should().StartWith("No replacement candidates were generated");
            var saved = sessions.List().Single();
            saved.FourLaneStrategyBrief.Should().Be(originalBrief);
            saved.ParallelCandidateBatchJson.Should().Be(originalBatchJson);
        }

        var restoredGenerator = new RecordingParallelGenerator();
        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: restoredGenerator,
            sessionRepository: sessions);

        restored.Composer = refinementAfterRestore;
        await restored.SendCommand.ExecuteAsync(null);

        restoredGenerator.CallCount.Should().Be(1);
        restoredGenerator.LastRequest!.UserPrompt.Should().ContainAll(
            originalBrief,
            "Follow-up refinement",
            refinementAfterRestore);
        restoredGenerator.LastRequest.UserPrompt.Should().NotContain(backtestNavigation,
            "navigation chatter must not become strategy logic after restore");
    }

    [Fact]
    public async Task Live_lane_results_are_previewable_but_only_the_full_batch_is_committed()
    {
        const string prompt = "Compare two causal moving averages.";
        var sessions = new MemoryAuthoringSessionRepository();
        var generator = new ProgressiveParallelGenerator();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: generator,
            sessionRepository: sessions);
        viewModel.Composer = prompt;

        var send = viewModel.SendCommand.ExecuteAsync(null);
        await generator.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var specRow = viewModel.GenerationLaneProgressRows.Single(row =>
            row.Lane == StrategyGenerationLaneV1.DeclarativeSpec);
        generator.Publish(StrategyGenerationLaneV1.DeclarativeSpec);
        await WaitUntilAsync(() => specRow.HasResult);

        send.IsCompleted.Should().BeFalse("three lane results are still outstanding");
        specRow.ResultOption!.Result.Should().BeSameAs(
            generator.Batch!.Lanes.Single(lane => lane.Lane == specRow.Lane));
        specRow.InspectablePreview.Should().Be(specRow.ResultOption.ArtifactPreview);
        specRow.InspectablePreview.Should().Contain("vibe-quant/declarative-rules/v1");
        viewModel.GeneratedCandidateOptions.Should().BeEmpty(
            "a transient lane preview is not a committed candidate batch");
        viewModel.SelectedGeneratedCandidateOption.Should().BeNull();
        viewModel.ChosenGeneratedCandidateHash.Should().BeNull();
        viewModel.CanChooseGeneratedCandidate.Should().BeFalse();
        var inFlight = sessions.List().Single();
        inFlight.ParallelCandidateBatchJson.Should().BeNull();
        inFlight.SelectedParallelCandidateHash.Should().BeNull();

        var graphRow = viewModel.GenerationLaneProgressRows.Single(row =>
            row.Lane == StrategyGenerationLaneV1.TypedGraph);
        generator.Publish(StrategyGenerationLaneV1.TypedGraph);
        await WaitUntilAsync(() => graphRow.HasResult);

        graphRow.ResultOption!.IsFailed.Should().BeTrue();
        graphRow.ResultOption.FirstIssueCode.Should().Be("PROVIDER_FAILED");
        graphRow.ResultOption.FirstIssuePath.Should().Be("agentRun");
        graphRow.ResultOption.FirstIssueMessage.Should().Be("Fixture provider failure.");
        graphRow.InspectablePreview.Should().Be(
            "PROVIDER_FAILED · agentRun: Fixture provider failure.");
        viewModel.GeneratedCandidateOptions.Should().BeEmpty(
            "even multiple terminal lanes cannot form a partial committed batch");

        generator.Publish(StrategyGenerationLaneV1.VibePython);
        generator.Publish(StrategyGenerationLaneV1.CspPython);
        await WaitUntilAsync(() => viewModel.GenerationLaneProgressRows.All(row => row.HasResult));
        viewModel.GeneratedCandidateOptions.Should().BeEmpty(
            "terminal callbacks remain staging-only until GenerateAsync returns the validated aggregate");
        generator.Complete();
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.GeneratedCandidateOptions.Should().HaveCount(4);
        viewModel.GeneratedCandidateOptions.Select(option => option.Result.Lane)
            .Should().Equal(StrategyGenerationLaneCatalogV1.Ordered);
        viewModel.ChosenGeneratedCandidateHash.Should().BeNull(
            "previewing a result never chooses it");
        viewModel.CanChooseGeneratedCandidate.Should().BeTrue();

        var committed = sessions.List().Single();
        committed.ParallelCandidateBatchJson.Should().NotBeNullOrWhiteSpace();
        committed.SelectedParallelCandidateHash.Should().BeNull();
        var restoredBatch = StrategyGenerationCandidateCanonicalJsonV1.DeserializeBatch(
            committed.ParallelCandidateBatchJson!);
        StrategyGenerationBatchValidationV1.Validate(restoredBatch).Should().BeEmpty();
        restoredBatch.Lanes.Select(lane => lane.Lane)
            .Should().Equal(StrategyGenerationLaneCatalogV1.Ordered);
        foreach (var lane in restoredBatch.Lanes.Where(static lane => lane.Candidate is not null))
        {
            lane.CandidateHashSha256.Should().Be(
                StrategyGenerationCandidateCanonicalJsonV1.Hash(lane.Candidate!));
        }
    }

    [Fact]
    public async Task Replacement_generation_keeps_last_completed_batch_through_stop_and_restart()
    {
        const string strategyId = "saved-strategy";
        const string oldPrompt = "Compare two causal moving averages.";
        const string pendingRefinement = "Replace the slow EMA with a 20-period EMA.";
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
            viewModel.Composer = pendingRefinement;

            var send = viewModel.SendCommand.ExecuteAsync(null);
            await generator.Started.WaitAsync(TimeSpan.FromSeconds(5));

            viewModel.HasRetainedCandidateBatchDuringGeneration.Should().BeTrue();
            viewModel.GeneratedCandidateOptions.Should().HaveCount(4,
                "an in-flight replacement must not erase the last committed batch");

            viewModel.StopCommand.Execute(null);
            await send.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.GeneratedCandidateOptions.Should().HaveCount(4);
            viewModel.Composer.Should().Be(pendingRefinement,
                "a canceled replacement should remain available for retry");
            viewModel.HasPendingFourLanePrompt.Should().BeTrue();
            viewModel.CanChooseGeneratedCandidate.Should().BeFalse(
                "the retained cards do not include the canceled refinement");
            viewModel.RegenerateFourCandidatesCommand.CanExecute(null).Should().BeFalse(
                "rerunning the committed brief must not discard a pending refinement");
            var stopped = sessions.List().Single();
            stopped.FourLaneStrategyBrief.Should().Be(oldPrompt,
                "the durable brief must remain bound to the retained completed batch");
            stopped.PendingFourLanePrompt.Should().Be(pendingRefinement);
        }

        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: sessions);
        restored.GeneratedCandidateOptions.Should().HaveCount(4,
            "the preserved completed batch must survive process restart");
        restored.HasCandidateRestoreWarning.Should().BeFalse();
        restored.HasPendingFourLanePrompt.Should().BeTrue();
        restored.Composer.Should().Be(pendingRefinement);
        restored.CanChooseGeneratedCandidate.Should().BeFalse();
        restored.RegenerateFourCandidatesCommand.CanExecute(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("go to backtest")]
    [InlineData("gow to backtest")]
    [InlineData("let's go to backtest")]
    [InlineData("let’s go to backtest")]
    [InlineData("can you go to backtest")]
    [InlineData("i said do backtest after make it")]
    [InlineData("make it and backtest those")]
    [InlineData("okay can be backtest")]
    public async Task Fresh_session_backtest_navigation_never_starts_candidate_generation(string navigation)
    {
        var sessions = new MemoryAuthoringSessionRepository();
        var generator = new RecordingParallelGenerator();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: generator,
            sessionRepository: sessions);

        viewModel.Composer = navigation;
        await viewModel.SendCommand.ExecuteAsync(null);

        generator.CallCount.Should().Be(0);
        viewModel.WorkbenchTab.Should().Be(3);
        viewModel.HasPendingFourLanePrompt.Should().BeFalse();
        viewModel.GeneratedCandidateOptions.Should().BeEmpty();
        viewModel.AiStatus.Should().StartWith("No replacement candidates were generated");
    }

    [Fact]
    public async Task Navigation_does_not_hide_an_existing_pending_refinement()
    {
        const string oldPrompt = "Compare two causal moving averages.";
        const string pending = "Require the fast average to rise for three events.";
        var batch = ValidBatchWithDeclarativeCandidate("saved-strategy", oldPrompt);
        var saved = new AuthoringSessionSnapshot(
            StrategyId: "saved-strategy",
            DisplayName: "Saved strategy",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, oldPrompt, DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile("Strategy.cs", "// unchanged editor")],
            GenerateCandidateFirst: true,
            FourLaneStrategyBrief: oldPrompt,
            PendingFourLanePrompt: pending,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(batch),
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var generator = new RecordingParallelGenerator();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: generator,
            sessionRepository: new MemoryAuthoringSessionRepository(saved));

        viewModel.Composer = "can you go to backtest";
        await viewModel.SendCommand.ExecuteAsync(null);

        generator.CallCount.Should().Be(0);
        viewModel.Composer.Should().Be(pending);
        viewModel.HasPendingFourLanePrompt.Should().BeTrue();
    }

    [Fact]
    public void Returning_from_expert_mode_restores_a_saved_pending_four_lane_prompt()
    {
        const string pending = "Require the fast average to rise for three events.";
        var saved = new AuthoringSessionSnapshot(
            StrategyId: "saved-strategy",
            DisplayName: "Saved strategy",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, "Expert C# turn", DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile("Strategy.cs", "// expert editor")],
            GenerateCandidateFirst: false,
            PendingFourLanePrompt: pending,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository(saved));

        viewModel.Composer.Should().BeEmpty();
        var restoredFile = viewModel.Files.Single();
        var restoredContent = restoredFile.Content;
        viewModel.ToggleGenerationModeCommand.Execute(null);

        viewModel.GenerateCandidateFirst.Should().BeTrue();
        viewModel.WorkbenchTab.Should().Be(3,
            "returning to the four-lane workflow must reveal Candidate rather than leave Code selected");
        viewModel.Files.Should().ContainSingle().Which.Should().BeSameAs(restoredFile);
        viewModel.Files.Single().Content.Should().Be(restoredContent,
            "mode navigation must not rewrite or discard the editor artifact");
        viewModel.Composer.Should().Be(pending);
        viewModel.HasPendingFourLanePrompt.Should().BeTrue();
    }

    [Fact]
    public void Expert_compile_eligibility_tracks_file_extensions_and_non_C_sharp_never_reaches_compiler()
    {
        var compiler = new RecordingCompiler();
        using var viewModel = new StrategyAuthoringViewModel(
            compiler,
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.ToggleGenerationModeCommand.Execute(null);
        viewModel.GenerateCandidateFirst.Should().BeFalse();
        viewModel.WorkbenchTab.Should().Be(0);
        viewModel.HasExpertCSharpFiles.Should().BeTrue();
        viewModel.HasNonCSharpExpertArtifact.Should().BeFalse();

        viewModel.SelectedFile!.Name = "strategy.py";

        viewModel.HasExpertCSharpFiles.Should().BeFalse();
        viewModel.HasNonCSharpExpertArtifact.Should().BeTrue();
        viewModel.AuthoringBoundaryText.Should().Be("source review only · no importer/runtime");
        viewModel.CompileCommand.Execute(null);
        compiler.CallCount.Should().Be(0);
        viewModel.Status.Should().Contain("C# expert-code path");

        var sameFile = viewModel.SelectedFile;
        viewModel.ToggleGenerationModeCommand.Execute(null);
        viewModel.WorkbenchTab.Should().Be(3);
        viewModel.SelectedFile.Should().BeSameAs(sameFile);
    }

    [Fact]
    public async Task Provider_failure_persists_the_uncommitted_refinement_for_restart()
    {
        const string strategyId = "saved-strategy";
        const string oldPrompt = "Compare two causal moving averages.";
        const string pendingRefinement = "Require the fast average to rise for three events.";
        var batch = ValidBatchWithDeclarativeCandidate(strategyId, oldPrompt);
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "Saved strategy",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, oldPrompt, DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile("Strategy.cs", "// unchanged editor")],
            GenerateCandidateFirst: true,
            FourLaneStrategyBrief: oldPrompt,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(batch),
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var sessions = new MemoryAuthoringSessionRepository(saved);

        using (var viewModel = new StrategyAuthoringViewModel(
                   new StubCompiler(),
                   new StubRegistry(),
                   NullLogger<StrategyAuthoringViewModel>.Instance,
                   ai: new StubAiStrategyBuilder(new StubCodegenClient()),
                   parallelCandidateGenerator: new ThrowingParallelGenerator(),
                   sessionRepository: sessions))
        {
            viewModel.Composer = pendingRefinement;
            await viewModel.SendCommand.ExecuteAsync(null);

            viewModel.Composer.Should().Be(pendingRefinement);
            viewModel.HasPendingFourLanePrompt.Should().BeTrue();
            viewModel.GeneratedCandidateOptions.Should().HaveCount(4);
            viewModel.CanChooseGeneratedCandidate.Should().BeFalse();
            viewModel.AiStatus.Should().Contain("Candidate generation error");
        }

        var persisted = sessions.List().Single();
        persisted.FourLaneStrategyBrief.Should().Be(oldPrompt);
        persisted.PendingFourLanePrompt.Should().Be(pendingRefinement);

        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: sessions);
        restored.GeneratedCandidateOptions.Should().HaveCount(4);
        restored.Composer.Should().Be(pendingRefinement);
        restored.HasPendingFourLanePrompt.Should().BeTrue();
    }

    [Fact]
    public void Legacy_expert_only_chat_is_not_recovered_as_a_four_lane_strategy_brief()
    {
        var legacy = new AuthoringSessionSnapshot(
            StrategyId: "legacy-expert",
            DisplayName: "Legacy expert",
            Chat:
            [
                new AuthoringChatEntry(
                    AuthoringChatEntry.User,
                    "Write a C# helper class for my registered strategy.",
                    DateTime.Now),
                new AuthoringChatEntry(
                    AuthoringChatEntry.Assistant,
                    "Here is the compiled C# implementation.",
                    DateTime.Now),
            ],
            Thread: [],
            Files: [new StrategyFile("Strategy.cs", "// expert code")],
            GenerateCandidateFirst: null,
            AuthoringUxVersion: 0,
            UpdatedUtc: DateTime.UtcNow);

        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: new RecordingParallelGenerator(),
            sessionRepository: new MemoryAuthoringSessionRepository(legacy));

        viewModel.RegenerateFourCandidatesCommand.CanExecute(null).Should().BeFalse();
        viewModel.Composer.Should().BeEmpty();
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

    [Fact]
    public async Task Restored_batch_is_labeled_until_explicit_fresh_generation_replaces_it()
    {
        const string strategyId = "restored-strategy";
        const string prompt = "Compare two causal moving averages.";
        var batch = ValidBatchWithDeclarativeCandidate(strategyId, prompt);
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "Restored strategy",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, prompt, DateTime.Now)],
            Thread: [],
            Files: [],
            GenerateCandidateFirst: true,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(batch),
            FourLaneStrategyBrief: prompt,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var generator = new RecordingParallelGenerator();

        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: generator,
            sessionRepository: new MemoryAuthoringSessionRepository(saved));

        viewModel.CandidateBatchRestored.Should().BeTrue();
        viewModel.RegenerateFourCandidatesCommand.CanExecute(null).Should().BeTrue();

        await viewModel.RegenerateFourCandidatesCommand.ExecuteAsync(null);

        generator.CallCount.Should().Be(1);
        viewModel.CandidateBatchRestored.Should().BeFalse();
    }

    [Fact]
    public void Live_lane_progress_surfaces_the_bounded_repair_cycle()
    {
        var lane = StrategyGenerationLaneV1.VibePython;
        var row = new StrategyGenerationLaneProgressRow(lane);
        row.Apply(new StrategyGenerationLaneProgressV1(
            lane,
            StrategyGenerationLaneProgressStateV1.ValidatingArtifact));
        row.Apply(new StrategyGenerationLaneProgressV1(
            lane,
            StrategyGenerationLaneProgressStateV1.RepairingResponse,
            "The first output was invalid; requesting one contract-aware repair."));

        row.StateLabel.Should().Be("REPAIRING RESPONSE");
        row.StateDetail.Should().Contain("one contract-aware repair");
        row.PipelineText.Should().Contain("REPAIR");

        row.Apply(new StrategyGenerationLaneProgressV1(
            lane,
            StrategyGenerationLaneProgressStateV1.ParsingResponse));

        row.State.Should().Be(StrategyGenerationLaneProgressStateV1.ParsingResponse);
        row.StateLabel.Should().Be("PARSING RESPONSE");
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
    public async Task Stale_batch_recovery_prefers_the_explicit_durable_brief_over_its_old_prompt()
    {
        const string strategyId = "saved-strategy";
        const string oldBatchPrompt = "Trade a causal momentum breakout.";
        const string durableBrief = """
            Ordered strategy request. Later refinements supersede only directly conflicting earlier clauses.
            Preserve every non-conflicting requirement, and do not implement a superseded clause alongside its replacement.

            Original strategy brief:
            Trade a causal momentum breakout.

            Follow-up refinement:
            Use a 20-period ATR stop.
            """;
        var staleBatch = new ParallelStrategyGenerationResultV1(
            strategyId,
            oldBatchPrompt,
            StrategyGenerationCandidateCanonicalJsonV1.PromptHash(strategyId, oldBatchPrompt),
            [],
            CodegenUsage.None);
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "Saved strategy",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, oldBatchPrompt, DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile("SavedStrategy.cs", "// saved code")],
            GenerateCandidateFirst: true,
            FourLaneStrategyBrief: durableBrief,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(staleBatch),
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var generator = new RecordingParallelGenerator();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: generator,
            sessionRepository: new MemoryAuthoringSessionRepository(saved));

        viewModel.Composer.Should().Be(durableBrief);
        viewModel.HasCandidateRestoreWarning.Should().BeTrue();

        await viewModel.RegenerateRecoveredCandidatesCommand.ExecuteAsync(null);

        generator.LastRequest!.UserPrompt.Should().Be(durableBrief);
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

    [Fact]
    public async Task Legacy_batch_generated_from_backtest_navigation_recovers_the_original_chat_brief()
    {
        const string strategyId = "saved-strategy";
        const string originalBrief =
            "Trade a causal momentum breakout with an ATR stop and flatten before the close.";
        const string backtestNavigation = "gow to backtest";
        var misroutedBatch = ValidBatchWithDeclarativeCandidate(strategyId, backtestNavigation);
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "Saved strategy",
            Chat:
            [
                new AuthoringChatEntry(AuthoringChatEntry.User, originalBrief, DateTime.Now),
                new AuthoringChatEntry(AuthoringChatEntry.User, backtestNavigation, DateTime.Now),
            ],
            Thread: [],
            Files: [new StrategyFile("SavedStrategy.cs", "// saved code")],
            GenerateCandidateFirst: true,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(misroutedBatch),
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var generator = new RecordingParallelGenerator();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: generator,
            sessionRepository: new MemoryAuthoringSessionRepository(saved));

        generator.CallCount.Should().Be(0, "restore must never issue a replacement provider request");
        viewModel.GeneratedCandidateOptions.Should().BeEmpty(
            "a structurally valid batch is still unusable when its prompt was navigation chatter");
        viewModel.HasCandidateRestoreWarning.Should().BeTrue();
        viewModel.Composer.Should().Be(originalBrief);

        await viewModel.RegenerateRecoveredCandidatesCommand.ExecuteAsync(null);

        generator.CallCount.Should().Be(1);
        generator.LastRequest.Should().Be(new ParallelStrategyGenerationRequestV1(strategyId, originalBrief));
    }

    [Fact]
    public async Task Explicit_expert_session_chat_does_not_become_a_four_lane_brief()
    {
        const string expertPrompt = "Write a custom C# strategy with a helper class.";
        const string firstFourLaneBrief = "Build a four-lane causal EMA crossover.";
        var saved = new AuthoringSessionSnapshot(
            StrategyId: "saved-strategy",
            DisplayName: "Saved strategy",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, expertPrompt, DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile("SavedStrategy.cs", "// saved code")],
            GenerateCandidateFirst: false,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var generator = new RecordingParallelGenerator();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            parallelCandidateGenerator: generator,
            sessionRepository: new MemoryAuthoringSessionRepository(saved));

        viewModel.ToggleGenerationModeCommand.Execute(null);
        viewModel.Composer = firstFourLaneBrief;
        await viewModel.SendCommand.ExecuteAsync(null);

        generator.LastRequest!.UserPrompt.Should().Be(firstFourLaneBrief);
        generator.LastRequest.UserPrompt.Should().NotContain(expertPrompt);
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
    }

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

    [Fact]
    public void Candidate_results_distinguish_restored_snapshots_and_offer_failure_regeneration()
    {
        var root = XDocument.Load(Fixture("StrategyAuthoringWindow.axaml")).Root
            ?? throw new InvalidOperationException("The strategy authoring fixture has no root element.");
        var restored = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Restored candidate snapshot notice");
        restored.Attribute("IsVisible")!.Value.Should().Be("{Binding CandidateBatchRestored}");
        restored.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "RESTORED RESULT · NOT A NEW AI RUN");
        restored.Descendants(Avalonia + "Button").Should().ContainSingle(element =>
            (string?)element.Attribute("Command") == "{Binding RegenerateFourCandidatesCommand}");

        var repair = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Regenerate after selected candidate failure");
        repair.Attribute("Content")!.Value.Should().Be("Generate fresh 4 candidates");
        repair.Attribute("Command")!.Value.Should().Be("{Binding RegenerateFourCandidatesCommand}");
        repair.Parent!.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding SelectedGeneratedCandidateOption.RecoveryText}");
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

    private sealed class RecordingCompiler : IStrategyCompiler
    {
        public int CallCount { get; private set; }

        public StrategyCompileResult Compile(StrategyScript script)
        {
            CallCount++;
            return StrategyCompileResult.Failed([]);
        }
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

    private sealed class ProgressiveParallelGenerator : IParallelStrategyCandidateGeneratorV1
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ParallelStrategyGenerationResultV1> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<StrategyGenerationLaneProgressV1>? _progress;

        public Task Started => _started.Task;
        public ParallelStrategyGenerationResultV1? Batch { get; private set; }

        public async Task<ParallelStrategyGenerationResultV1> GenerateAsync(
            IStrategyCodegenClient provider,
            ParallelStrategyGenerationRequestV1 request,
            CancellationToken ct = default,
            IProgress<StrategyGenerationLaneProgressV1>? progress = null)
        {
            Batch = ValidBatchWithDeclarativeCandidate(request.StrategyId, request.UserPrompt);
            _progress = progress;
            _started.TrySetResult();
            return await _completion.Task.WaitAsync(ct);
        }

        public void Publish(StrategyGenerationLaneV1 lane)
        {
            var result = Batch?.Lanes.Single(candidate => candidate.Lane == lane)
                ?? throw new InvalidOperationException("Generation has not started.");
            var state = result.Readiness is StrategyGenerationReadinessV1.Generated or
                StrategyGenerationReadinessV1.PackageValid or StrategyGenerationReadinessV1.TestPassed
                    ? StrategyGenerationLaneProgressStateV1.Completed
                    : StrategyGenerationLaneProgressStateV1.Failed;
            _progress?.Report(new StrategyGenerationLaneProgressV1(
                lane,
                state,
                "Terminal fixture result.",
                result));
        }

        public void Complete() => _completion.TrySetResult(
            Batch ?? throw new InvalidOperationException("Generation has not started."));
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

    private sealed class ThrowingParallelGenerator : IParallelStrategyCandidateGeneratorV1
    {
        public Task<ParallelStrategyGenerationResultV1> GenerateAsync(
            IStrategyCodegenClient provider,
            ParallelStrategyGenerationRequestV1 request,
            CancellationToken ct = default,
            IProgress<StrategyGenerationLaneProgressV1>? progress = null) =>
            Task.FromException<ParallelStrategyGenerationResultV1>(
                new InvalidOperationException("Fixture provider failure."));
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
