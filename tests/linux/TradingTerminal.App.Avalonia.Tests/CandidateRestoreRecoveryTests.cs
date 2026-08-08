using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class CandidateRestoreRecoveryTests
{
    [Fact]
    public async Task Confirmed_request_generates_only_on_explicit_command_and_unchanged_request_can_regenerate()
    {
        const string prompt = "Publish a causal momentum signal from completed bars.";
        var semantic = new RecordingSemanticGenerator();
        var parallel = new RecordingParallelGenerator();
        var sessions = new MemoryAuthoringSessionRepository();
        using var viewModel = CreateViewModel(semantic, parallel, sessions, "explicit-generation");

        await PrepareConfirmedRequestAsync(viewModel, prompt, generateImplementations: false);

        semantic.CallCount.Should().Be(1);
        parallel.CallCount.Should().Be(0,
            "chat and local confirmation must not implicitly start implementation agents");
        viewModel.CanGenerateFourCandidates.Should().BeTrue();

        await viewModel.GenerateFourCandidatesCommand.ExecuteAsync(null);

        parallel.CallCount.Should().Be(1);
        viewModel.GeneratedCandidateOptions.Should().HaveCount(4);
        var firstRequest = parallel.LastRequest!;
        firstRequest.ConfirmedIntentHashSha256.Should().Be(viewModel.ConfirmedStrategyIntentHash);
        firstRequest.ConfirmedIntentCanonicalJson.Should().Be(
            StrategyIntentCanonicalJsonV1.Serialize(viewModel.ConfirmedStrategyIntent!));
        viewModel.RegenerateFourCandidatesCommand.CanExecute(null).Should().BeTrue();

        await viewModel.RegenerateFourCandidatesCommand.ExecuteAsync(null);

        parallel.CallCount.Should().Be(2);
        parallel.LastRequest!.UserPrompt.Should().Be(firstRequest.UserPrompt);
        parallel.LastRequest.ConfirmedIntentCanonicalJson.Should().Be(firstRequest.ConfirmedIntentCanonicalJson);
        parallel.LastRequest.ConfirmedIntentHashSha256.Should().Be(firstRequest.ConfirmedIntentHashSha256);
    }

    [Fact]
    public async Task Semantic_change_detaches_old_batch_and_requires_reconfirmation_before_new_generation()
    {
        const string original = "Publish a causal momentum signal from completed bars.";
        const string revision = "Also require volume above its completed twenty-bar average.";
        var semantic = new RecordingSemanticGenerator();
        var parallel = new RecordingParallelGenerator();
        using var viewModel = CreateViewModel(
            semantic,
            parallel,
            new MemoryAuthoringSessionRepository(),
            "semantic-revision");
        await PrepareConfirmedRequestAsync(viewModel, original, generateImplementations: true);
        var originalIntentHash = viewModel.ConfirmedStrategyIntentHash;

        viewModel.Composer = revision;
        await viewModel.SendCommand.ExecuteAsync(null);

        semantic.CallCount.Should().Be(2);
        parallel.CallCount.Should().Be(1,
            "a chat revision changes meaning first and cannot directly invoke implementation lanes");
        viewModel.GeneratedCandidateOptions.Should().BeEmpty(
            "artifacts bound to the previous confirmed meaning must be detached");
        viewModel.HasConfirmedStrategyIntent.Should().BeFalse();
        viewModel.CanGenerateFourCandidates.Should().BeFalse();

        CompleteCurrentIntentReview(viewModel);
        viewModel.ConfirmedStrategyIntentHash.Should().NotBe(originalIntentHash);
        await viewModel.GenerateFourCandidatesCommand.ExecuteAsync(null);

        parallel.CallCount.Should().Be(2);
        parallel.LastRequest!.ConfirmedIntentHashSha256.Should().Be(viewModel.ConfirmedStrategyIntentHash);
        parallel.LastRequest.UserPrompt.Should().Contain("Revised strategy meaning");
        parallel.LastRequest.UserPrompt.Should().Contain(revision);
    }

    [Fact]
    public async Task Edited_selected_lane_restores_its_origin_and_detaches_when_the_strategy_changes()
    {
        const string prompt = "Publish a causal momentum signal from completed bars.";
        var sessions = new MemoryAuthoringSessionRepository();
        var semantic = new RecordingSemanticGenerator();
        var parallel = new RecordingParallelGenerator();
        string editedSource;

        using (var original = CreateViewModel(semantic, parallel, sessions, "edited-lane-origin"))
        {
            await PrepareConfirmedRequestAsync(original, prompt, generateImplementations: true);
            original.ChooseGeneratedCandidateCommand.Execute(null);
            original.ChosenGeneratedCandidateHash.Should().NotBeNull();

            editedSource = original.Files.Single().Content.Replace(
                "\"displayName\": \"Causal signal\"",
                "\"displayName\": \"Edited causal signal\"",
                StringComparison.Ordinal);
            editedSource.Should().NotBe(original.Files.Single().Content,
                "the fixture must make a semantic JSON edit rather than a canonical-equivalent whitespace edit");
            original.Files.Single().Content = editedSource;

            original.ChosenGeneratedCandidateHash.Should().BeNull(
                "editing clears selection proof without erasing the candidate from which the source came");
            original.CanRevalidateGeneratedCandidate.Should().BeTrue();
        }

        var saved = sessions.List().Single();
        saved.EditorBaseParallelCandidateHash.Should().NotBeNullOrWhiteSpace();
        saved.SelectedParallelCandidateHash.Should().BeNull();

        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: sessions);

        restored.Files.Single().Content.Should().Be(editedSource);
        restored.ChosenGeneratedCandidateHash.Should().BeNull();
        restored.CanRevalidateGeneratedCandidate.Should().BeTrue(
            "the durable editor origin permits explicit local revalidation after restart");
        restored.Status.Should().Contain("original lane provenance");

        restored.CurrentCandidate = restored.CurrentCandidate! with
        {
            Title = "Changed strategy meaning",
        };

        restored.HasDetachedImplementationSource.Should().BeTrue();
        restored.Files.Single().Content.Should().Be(editedSource,
            "stale source remains inspectable but must not be treated as the new implementation");
        restored.GeneratedCandidateOptions.Should().BeEmpty();
        restored.IsDesignScreen.Should().BeTrue();
    }

    [Fact]
    public async Task Live_lane_results_are_previewable_but_only_the_bound_full_batch_is_committed()
    {
        const string prompt = "Publish a causal completed-bar signal.";
        var sessions = new MemoryAuthoringSessionRepository();
        var semantic = new RecordingSemanticGenerator();
        var parallel = new ProgressiveParallelGenerator();
        using var viewModel = CreateViewModel(semantic, parallel, sessions, "progressive-generation");
        await PrepareConfirmedRequestAsync(viewModel, prompt, generateImplementations: false);

        var generation = viewModel.GenerateFourCandidatesCommand.ExecuteAsync(null);
        await parallel.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var specRow = viewModel.GenerationLaneProgressRows.Single(row =>
            row.Lane == StrategyGenerationLaneV1.DeclarativeSpec);
        parallel.Publish(StrategyGenerationLaneV1.DeclarativeSpec);
        await WaitUntilAsync(() => specRow.HasResult);

        generation.IsCompleted.Should().BeFalse();
        specRow.InspectablePreview.Should().Contain("vibe-quant/declarative-rules/v1");
        viewModel.GeneratedCandidateOptions.Should().BeEmpty();
        sessions.List().Single().ParallelCandidateBatchJson.Should().BeNull();

        foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered.Where(lane =>
                     lane != StrategyGenerationLaneV1.DeclarativeSpec))
            parallel.Publish(lane);
        await WaitUntilAsync(() => viewModel.GenerationLaneProgressRows.All(row => row.HasResult));
        viewModel.GeneratedCandidateOptions.Should().BeEmpty(
            "terminal progress remains transient until the aggregate returns");

        parallel.Complete();
        await generation.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.GeneratedCandidateOptions.Should().HaveCount(4);
        var persisted = sessions.List().Single();
        persisted.ParallelCandidateBatchJson.Should().NotBeNullOrWhiteSpace();
        var restoredBatch = StrategyGenerationCandidateCanonicalJsonV1.DeserializeBatch(
            persisted.ParallelCandidateBatchJson!);
        StrategyGenerationBatchValidationV1.Validate(restoredBatch).Should().BeEmpty();
        restoredBatch.ConfirmedIntentHashSha256.Should().Be(viewModel.ConfirmedStrategyIntentHash);
        restoredBatch.ConfirmedIntentCanonicalJson.Should().Be(
            StrategyIntentCanonicalJsonV1.Serialize(viewModel.ConfirmedStrategyIntent!));
    }

    [Fact]
    public async Task Stop_rejects_late_replacement_and_preserves_last_bound_batch_across_restart()
    {
        const string prompt = "Publish a causal completed-bar signal.";
        var sessions = new MemoryAuthoringSessionRepository();
        var semantic = new RecordingSemanticGenerator();
        var parallel = new FirstThenNonCooperativeParallelGenerator();

        using (var viewModel = CreateViewModel(semantic, parallel, sessions, "stop-recovery"))
        {
            await PrepareConfirmedRequestAsync(viewModel, prompt, generateImplementations: true);
            var committedJson = sessions.List().Single().ParallelCandidateBatchJson;

            var replacement = viewModel.RegenerateFourCandidatesCommand.ExecuteAsync(null);
            await parallel.ReplacementStarted.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.HasRetainedCandidateBatchDuringGeneration.Should().BeTrue();

            viewModel.StopCommand.Execute(null);
            parallel.CompleteLate();
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));

            viewModel.GenerationLaneProgressRows.Should().OnlyContain(row =>
                row.State == StrategyGenerationLaneProgressStateV1.Canceled);
            viewModel.GeneratedCandidateOptions.Should().HaveCount(4);
            sessions.List().Single().ParallelCandidateBatchJson.Should().Be(committedJson,
                "late output from the invalidated generation epoch must not replace durable evidence");
        }

        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: sessions);
        restored.HasConfirmedStrategyIntent.Should().BeTrue();
        restored.GeneratedCandidateOptions.Should().HaveCount(4);
        restored.HasCandidateRestoreWarning.Should().BeFalse();
    }

    [Fact]
    public async Task Provider_failure_preserves_the_previous_bound_batch_and_confirmation()
    {
        const string prompt = "Publish a causal completed-bar signal.";
        var sessions = new MemoryAuthoringSessionRepository();
        var semantic = new RecordingSemanticGenerator();
        var parallel = new FirstThenThrowingParallelGenerator();
        using var viewModel = CreateViewModel(semantic, parallel, sessions, "provider-failure");
        await PrepareConfirmedRequestAsync(viewModel, prompt, generateImplementations: true);
        var committedJson = sessions.List().Single().ParallelCandidateBatchJson;

        await viewModel.RegenerateFourCandidatesCommand.ExecuteAsync(null);

        parallel.CallCount.Should().Be(2);
        viewModel.AiStatus.Should().Contain("Candidate generation error");
        viewModel.HasConfirmedStrategyIntent.Should().BeTrue();
        viewModel.GeneratedCandidateOptions.Should().HaveCount(4);
        sessions.List().Single().ParallelCandidateBatchJson.Should().Be(committedJson);
    }

    [Fact]
    public async Task Bound_batch_restores_only_with_its_matching_semantic_state()
    {
        var sessions = new MemoryAuthoringSessionRepository();
        using (var original = CreateViewModel(
                   new RecordingSemanticGenerator(),
                   new RecordingParallelGenerator(),
                   sessions,
                   "bound-restore"))
        {
            await PrepareConfirmedRequestAsync(
                original,
                "Publish a causal completed-bar signal.",
                generateImplementations: true);
        }

        var saved = sessions.List().Single();
        saved.CandidateJson.Should().NotBeNullOrWhiteSpace();
        saved.ResearchCaseJson.Should().NotBeNullOrWhiteSpace();
        saved.StrategyClassificationJson.Should().NotBeNullOrWhiteSpace();
        saved.StrategyIntentDraftJson.Should().NotBeNullOrWhiteSpace();
        saved.ConfirmedStrategyIntentJson.Should().NotBeNullOrWhiteSpace();

        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: sessions);

        restored.HasConfirmedStrategyIntent.Should().BeTrue();
        restored.CanEnterFourLaneConformance.Should().BeTrue();
        restored.GeneratedCandidateOptions.Should().HaveCount(4);
        restored.CandidateBatchRestored.Should().BeTrue();
        restored.HasCandidateRestoreWarning.Should().BeFalse();
    }

    [Fact]
    public async Task Tampered_bound_batch_is_quarantined_while_valid_semantic_state_is_preserved()
    {
        var source = new MemoryAuthoringSessionRepository();
        using (var original = CreateViewModel(
                   new RecordingSemanticGenerator(),
                   new RecordingParallelGenerator(),
                   source,
                   "tampered-restore"))
        {
            await PrepareConfirmedRequestAsync(
                original,
                "Publish a causal completed-bar signal.",
                generateImplementations: true);
        }

        var saved = source.List().Single();
        var batch = StrategyGenerationCandidateCanonicalJsonV1.DeserializeBatch(saved.ParallelCandidateBatchJson!);
        var tampered = batch with { ConfirmedIntentHashSha256 = new string('0', 64) };
        var sessions = new MemoryAuthoringSessionRepository(saved with
        {
            ParallelCandidateBatchJson = StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(tampered),
        });

        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: sessions);

        restored.HasConfirmedStrategyIntent.Should().BeTrue(
            "the independently valid semantic review is not erased by a bad downstream artifact");
        restored.GeneratedCandidateOptions.Should().BeEmpty();
        restored.HasCandidateRestoreWarning.Should().BeTrue();
        restored.ChosenGeneratedCandidateHash.Should().BeNull();
    }

    [Fact]
    public async Task Legacy_unbound_batch_is_quarantined_and_recovery_restarts_at_strategy_meaning()
    {
        const string strategyId = "legacy-unbound";
        const string prompt = "Fade a liquidity sweep at the prior-day low.";
        var legacyBatch = new ParallelStrategyGenerationResultV1(
            strategyId,
            prompt,
            StrategyGenerationCandidateCanonicalJsonV1.PromptHash(strategyId, prompt),
            [],
            CodegenUsage.None);
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "Legacy unbound strategy",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, prompt, DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile("SavedStrategy.cs", "// preserved")],
            GenerateCandidateFirst: true,
            ParallelCandidateBatchJson: StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(legacyBatch),
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var sessions = new MemoryAuthoringSessionRepository(saved);
        var semantic = new RecordingSemanticGenerator();
        var parallel = new RecordingParallelGenerator();
        var compiler = new RecordingCompiler();
        using var viewModel = CreateViewModel(semantic, parallel, sessions, strategyId, compiler);

        semantic.CallCount.Should().Be(0);
        parallel.CallCount.Should().Be(0);
        viewModel.GeneratedCandidateOptions.Should().BeEmpty();
        viewModel.HasCandidateRestoreWarning.Should().BeTrue();
        viewModel.HasDetachedImplementationSource.Should().BeTrue(
            "retained source from a rejected legacy batch is not a trusted Expert C# draft");
        viewModel.CanCompileCurrentSource.Should().BeFalse();
        viewModel.CompileCommand.CanExecute(null).Should().BeFalse();
        viewModel.CompileCommand.Execute(null);
        compiler.CallCount.Should().Be(0);
        viewModel.Composer.Should().Be(prompt);
        viewModel.RegenerateRecoveredCandidatesCommand.CanExecute(null).Should().BeTrue();

        await viewModel.RegenerateRecoveredCandidatesCommand.ExecuteAsync(null);

        semantic.CallCount.Should().Be(1,
            "recovery must rebuild and confirm strategy meaning before implementation generation");
        parallel.CallCount.Should().Be(0);
        viewModel.HasCandidate.Should().BeTrue();
        viewModel.HasConfirmedStrategyIntent.Should().BeFalse();
        viewModel.GeneratedCandidateOptions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("go to backtest")]
    [InlineData("gow to backtest")]
    [InlineData("let's go to backtest")]
    [InlineData("can you go to backtest")]
    public async Task Fresh_navigation_never_starts_semantic_or_implementation_generation(string navigation)
    {
        var semantic = new RecordingSemanticGenerator();
        var parallel = new RecordingParallelGenerator();
        using var viewModel = CreateViewModel(
            semantic,
            parallel,
            new MemoryAuthoringSessionRepository(),
            "navigation");
        viewModel.Composer = navigation;

        await viewModel.SendCommand.ExecuteAsync(null);

        semantic.CallCount.Should().Be(0);
        parallel.CallCount.Should().Be(0);
        viewModel.GeneratedCandidateOptions.Should().BeEmpty();
        viewModel.HasPendingFourLanePrompt.Should().BeFalse();
    }

    [Fact]
    public void Expert_compile_eligibility_tracks_file_extension_and_never_sends_python_to_C_sharp_compiler()
    {
        var compiler = new RecordingCompiler();
        using var viewModel = new StrategyAuthoringViewModel(
            compiler,
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.CompileCommand.CanExecute(null).Should().BeFalse(
            "Strategy Builder source must use its confirmed-request admission path");
        viewModel.CompileCommand.Execute(null);
        compiler.CallCount.Should().Be(0);

        viewModel.ToggleGenerationModeCommand.Execute(null);

        viewModel.CompileCommand.CanExecute(null).Should().BeTrue(
            "explicit Expert C# mode remains the direct compiler path");
        viewModel.CompileCommand.Execute(null);
        compiler.CallCount.Should().Be(1);

        viewModel.SelectedFile!.Name = "strategy.py";
        viewModel.CompileCommand.Execute(null);

        compiler.CallCount.Should().Be(1);
        viewModel.HasNonCSharpExpertArtifact.Should().BeTrue();
        viewModel.CompileCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Expert_registration_review_expires_when_returning_to_strategy_builder()
    {
        var registry = new RecordingRegistry();
        using var viewModel = new StrategyAuthoringViewModel(
            new SuccessfulCompiler(),
            registry,
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());
        viewModel.ToggleGenerationModeCommand.Execute(null);

        viewModel.CompileCommand.Execute(null);

        viewModel.ReviewOpen.Should().BeTrue();
        viewModel.ConfirmRegisterCommand.CanExecute(null).Should().BeTrue();

        viewModel.ToggleGenerationModeCommand.Execute(null);

        viewModel.GenerateCandidateFirst.Should().BeTrue();
        viewModel.ReviewOpen.Should().BeFalse();
        viewModel.ConfirmRegisterCommand.CanExecute(null).Should().BeFalse();
        viewModel.ConfirmRegisterCommand.Execute(null);
        registry.Registered.Should().BeEmpty();
        viewModel.IsRegistered.Should().BeFalse();
    }

    [Fact]
    public void Expert_registration_rechecks_exact_current_script_before_registering()
    {
        var registry = new RecordingRegistry();
        using var viewModel = new StrategyAuthoringViewModel(
            new SuccessfulCompiler(),
            registry,
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());
        viewModel.ToggleGenerationModeCommand.Execute(null);
        viewModel.CompileCommand.Execute(null);
        viewModel.ReviewOpen.Should().BeTrue();

        viewModel.Files.Add(new AuthoredFile("Unreviewed.cs", "// added after review"));

        viewModel.ConfirmRegisterCommand.CanExecute(null).Should().BeFalse();
        viewModel.ConfirmRegisterCommand.Execute(null);
        registry.Registered.Should().BeEmpty();
        viewModel.IsRegistered.Should().BeFalse();
    }

    [Fact]
    public void Retired_batch_shape_recovers_prompt_without_treating_it_as_actionable_evidence()
    {
        const string strategyId = "retired-shape";
        const string prompt = "Trade a causal opening-range rejection.";
        var saved = new AuthoringSessionSnapshot(
            StrategyId: strategyId,
            DisplayName: "Retired shape",
            Chat: [new AuthoringChatEntry(AuthoringChatEntry.User, "Later follow-up", DateTime.Now)],
            Thread: [],
            Files: [new StrategyFile("SavedStrategy.cs", "// preserved")],
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

        viewModel.Composer.Should().Be(prompt);
        viewModel.HasCandidateRestoreWarning.Should().BeTrue();
        viewModel.GeneratedCandidateOptions.Should().BeEmpty();
    }

    private static StrategyAuthoringViewModel CreateViewModel(
        IStrategyCandidateGeneratorV1 semantic,
        IParallelStrategyCandidateGeneratorV1 parallel,
        IAuthoringSessionRepository sessions,
        string strategyId,
        IStrategyCompiler? compiler = null)
    {
        var viewModel = new StrategyAuthoringViewModel(
            compiler ?? new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            ai: new StubAiStrategyBuilder(new StubCodegenClient()),
            candidateGenerator: semantic,
            parallelCandidateGenerator: parallel,
            sessionRepository: sessions)
        {
            StrategyId = strategyId,
            DisplayName = strategyId,
        };
        viewModel.SelectedStrategyIntentProfile = viewModel.StrategyIntentProfiles.Single(profile =>
            profile.Id == "signal-publication");
        return viewModel;
    }

    private static async Task PrepareConfirmedRequestAsync(
        StrategyAuthoringViewModel viewModel,
        string prompt,
        bool generateImplementations)
    {
        viewModel.Composer = prompt;
        await viewModel.SendCommand.ExecuteAsync(null);
        CompleteCurrentIntentReview(viewModel);
        if (generateImplementations)
            await viewModel.GenerateFourCandidatesCommand.ExecuteAsync(null);
    }

    private static void CompleteCurrentIntentReview(StrategyAuthoringViewModel viewModel)
    {
        viewModel.ConfirmCandidateCommand.CanExecute(null).Should().BeTrue();
        viewModel.ConfirmCandidateCommand.Execute(null);
        viewModel.HasStrategyIntentReview.Should().BeTrue();

        viewModel.StrategyResearchEvidence = "Completed, point-in-time bars available at publication time.";
        viewModel.StrategyResearchPointInTimeRule = "Use only completed observations timestamped at or before the decision.";
        viewModel.StrategyResearchQualificationRule = "Publish only when the reviewed condition passes; otherwise publish nothing.";
        viewModel.StrategyResearchFalsifier = "Reject the idea if it cannot be reproduced without future data.";
        var interpretation = viewModel.CurrentCandidate!.Interpretation.Summary;
        foreach (var row in viewModel.StrategyIntentRequirements.Where(row => !row.MustBeNotApplicable))
        {
            row.SelectedApplicability = row.ApplicabilityOptions.Single(option =>
                option.Disposition == StrategySemanticDispositionV1.Applicable);
            row.Answer = $"{interpretation}: {row.Question}";
        }

        viewModel.CanConfirmStrategyIntentReview.Should().BeTrue(
            string.Join(Environment.NewLine, viewModel.StrategyIntentIssues.Select(issue => issue.Message)));
        viewModel.ConfirmStrategyIntentReviewCommand.Execute(null);
        viewModel.HasConfirmedStrategyIntent.Should().BeTrue();
        viewModel.CanEnterFourLaneConformance.Should().BeTrue();
    }

    private static ParallelStrategyGenerationResultV1 ValidBoundBatch(
        ParallelStrategyGenerationRequestV1 request)
    {
        var intentJson = request.ConfirmedIntentCanonicalJson
            ?? throw new InvalidOperationException("Fixture requires canonical confirmed intent JSON.");
        var intentHash = request.ConfirmedIntentHashSha256
            ?? throw new InvalidOperationException("Fixture requires a confirmed intent hash.");
        var lane = StrategyGenerationLaneV1.DeclarativeSpec;
        var strategyId = request.StrategyId;
        var prompt = request.UserPrompt;
        var candidateId = $"{strategyId}/{StrategyGenerationLaneCatalogV1.WireName(lane)}";
        using var document = JsonDocument.Parse($$"""
            {
              "schemaVersion": "vibe-quant/declarative-rules/v1",
              "strategy": {
                "id": {{JsonSerializer.Serialize(strategyId)}},
                "version": "1.0.0",
                "displayName": "Causal signal",
                "summary": "Causal signal"
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
            StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                strategyId, prompt, intentJson, intentHash, lane),
            StrategyGenerationPackageCatalogV1.RequireBinding(lane),
            "Causal signal",
            "Implement the confirmed signal.",
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
            ["Replay the shared scenarios."],
            intentHash);
        var issues = StrategyGenerationCandidateValidatorV1.Validate(
            candidate,
            lane,
            candidateId,
            candidate.RequestHashSha256,
            intentHash);
        var candidateHash = StrategyGenerationCandidateCanonicalJsonV1.Hash(candidate);

        return new ParallelStrategyGenerationResultV1(
            strategyId,
            prompt,
            StrategyGenerationCandidateCanonicalJsonV1.PromptHash(
                strategyId, prompt, intentJson, intentHash),
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
            CodegenUsage.None,
            intentJson,
            intentHash);
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

    private sealed class RecordingSemanticGenerator : IStrategyCandidateGeneratorV1
    {
        public int CallCount { get; private set; }

        public Task<StrategyCandidateGenerationResultV1> GenerateAsync(
            IStrategyCodegenClient provider,
            StrategyCandidateGenerationRequestV1 request,
            CancellationToken ct = default)
        {
            CallCount++;
            var revision = checked((request.CurrentCandidate?.Revision ?? 0) + 1);
            var parentHash = request.CurrentCandidate is null
                ? null
                : StrategyCandidateCanonicalJsonV1.Hash(request.CurrentCandidate);
            var summary = request.UserMessage is null
                ? "Publish a causal point-in-time signal without order authority."
                : $"Revised strategy meaning: {request.UserMessage}";
            var candidate = new StrategyCandidateV1(
                StrategyCandidateV1.CurrentSchemaVersion,
                request.CandidateId,
                revision,
                parentHash,
                request.RawIntent,
                "Causal signal",
                StrategyCandidateStatusV1.AwaitingConfirmation,
                new StrategyCandidateInterpretationV1(
                    summary,
                    StrategyInterpretationConfidenceV1.High,
                    []),
                [
                    new StrategyCandidateGroupV1(
                        "signal",
                        StrategyCandidateGroupKindV1.SignalAndAlpha,
                        "Signal",
                        summary,
                        [
                            new StrategyCandidateStatementV1(
                                "rule-signal",
                                StrategyCandidateStatementKindV1.Rule,
                                summary,
                                StrategyCandidateStatementSourceV1.User,
                                StrategyCandidateStatementStateV1.Proposed,
                                true),
                        ],
                        []),
                ],
                [
                    new StrategyBuildSupportItemV1(
                        "support-signal",
                        "Signal publication",
                        StrategyBuildSupportStatusV1.Supported,
                        true,
                        "Fixture support is available.",
                        ["rule-signal"]),
                ]);
            var assessment = StrategyCandidateValidatorV1.Assess(candidate);
            return Task.FromResult(new StrategyCandidateGenerationResultV1(
                candidate,
                assessment,
                [candidate],
                [],
                [new StrategyGenerationAgentRunV1(
                    "semantic-fixture",
                    provider.ProviderId,
                    null,
                    true,
                    null,
                    null,
                    CodegenUsage.None)],
                CodegenUsage.None));
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
            return Task.FromResult(ValidBoundBatch(request));
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
            Batch = ValidBoundBatch(request);
            _progress = progress;
            _started.TrySetResult();
            return await _completion.Task.WaitAsync(ct);
        }

        public void Publish(StrategyGenerationLaneV1 lane)
        {
            var result = Batch?.Lanes.Single(candidate => candidate.Lane == lane)
                ?? throw new InvalidOperationException("Generation has not started.");
            _progress?.Report(new StrategyGenerationLaneProgressV1(
                lane,
                result.Readiness == StrategyGenerationReadinessV1.Failed
                    ? StrategyGenerationLaneProgressStateV1.Failed
                    : StrategyGenerationLaneProgressStateV1.Completed,
                "Terminal fixture result.",
                result));
        }

        public void Complete() => _completion.TrySetResult(
            Batch ?? throw new InvalidOperationException("Generation has not started."));
    }

    private sealed class FirstThenNonCooperativeParallelGenerator : IParallelStrategyCandidateGeneratorV1
    {
        private readonly TaskCompletionSource _replacementStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ParallelStrategyGenerationResultV1> _lateCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ParallelStrategyGenerationRequestV1? _lateRequest;
        private IProgress<StrategyGenerationLaneProgressV1>? _lateProgress;
        private int _calls;

        public Task ReplacementStarted => _replacementStarted.Task;

        public Task<ParallelStrategyGenerationResultV1> GenerateAsync(
            IStrategyCodegenClient provider,
            ParallelStrategyGenerationRequestV1 request,
            CancellationToken ct = default,
            IProgress<StrategyGenerationLaneProgressV1>? progress = null)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return Task.FromResult(ValidBoundBatch(request));

            _lateRequest = request;
            _lateProgress = progress;
            foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered)
                progress?.Report(new StrategyGenerationLaneProgressV1(
                    lane,
                    StrategyGenerationLaneProgressStateV1.WaitingForModel));
            _replacementStarted.TrySetResult();
            return _lateCompletion.Task;
        }

        public void CompleteLate()
        {
            var request = _lateRequest ?? throw new InvalidOperationException("Replacement has not started.");
            var lane = StrategyGenerationLaneV1.TypedGraph;
            _lateProgress?.Report(new StrategyGenerationLaneProgressV1(
                lane,
                StrategyGenerationLaneProgressStateV1.Failed,
                "Late fixture result.",
                ValidBoundBatch(request).Lanes.Single(result => result.Lane == lane)));
            _lateCompletion.TrySetResult(ValidBoundBatch(request));
        }
    }

    private sealed class FirstThenThrowingParallelGenerator : IParallelStrategyCandidateGeneratorV1
    {
        public int CallCount { get; private set; }

        public Task<ParallelStrategyGenerationResultV1> GenerateAsync(
            IStrategyCodegenClient provider,
            ParallelStrategyGenerationRequestV1 request,
            CancellationToken ct = default,
            IProgress<StrategyGenerationLaneProgressV1>? progress = null)
        {
            CallCount++;
            return CallCount == 1
                ? Task.FromResult(ValidBoundBatch(request))
                : Task.FromException<ParallelStrategyGenerationResultV1>(
                    new InvalidOperationException("Fixture provider failure."));
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

    private sealed class RecordingCompiler : IStrategyCompiler
    {
        public int CallCount { get; private set; }

        public StrategyCompileResult Compile(StrategyScript script)
        {
            CallCount++;
            return StrategyCompileResult.Failed([]);
        }
    }

    private sealed class SuccessfulCompiler : IStrategyCompiler
    {
        public StrategyCompileResult Compile(StrategyScript script) =>
            StrategyCompileResult.Succeeded(
                new BacktestStrategyOption(
                    script.Id,
                    script.DisplayName,
                    _ => throw new NotSupportedException("The registration-consent test never builds the strategy.")),
                []);
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
            throw new NotSupportedException("Candidate tests use the semantic and parallel generators.");

        public Task<StrategyBuildLoopResult> BuildAsync(
            IStrategyCodegenClient selectedProvider,
            string instruction,
            string strategyId,
            string displayName,
            CancellationToken ct = default) =>
            Task.FromException<StrategyBuildLoopResult>(
                new NotSupportedException("Candidate tests use the semantic and parallel generators."));
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
                new NotSupportedException("The fixture generators own these calls."));
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

    private sealed class RecordingRegistry : IBacktestStrategyRegistry
    {
        public List<BacktestStrategyOption> Registered { get; } = [];
        public IReadOnlyList<BacktestStrategyOption> All => Registered;
        public BacktestStrategyOption? Find(string id) =>
            Registered.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal));
        public void Register(BacktestStrategyOption option) => Registered.Add(option);
        public bool Remove(string id) => false;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
