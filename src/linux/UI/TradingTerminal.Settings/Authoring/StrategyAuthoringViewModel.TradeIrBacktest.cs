using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Definition;
using TradingTerminal.Infrastructure.Strategies.Authoring;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Exact-hash bridge from an active, package-valid TradeIR candidate to the deliberately narrow
/// synthetic QuoteL1 smoke runner. This is not the historical Backtest Studio and it never promotes
/// an in-process smoke result into worker-isolated or historical evidence.
/// </summary>
public sealed partial class StrategyAuthoringViewModel
{
    private readonly ITradeIrSimulatedBacktestRunnerV1? _tradeIrSimulatedBacktestRunner;
    private CancellationTokenSource? _tradeIrBacktestCts;
    private string? _tradeIrBacktestSourceHashSha256;

    [ObservableProperty]
    private bool _isRunningTradeIrBacktest;

    [ObservableProperty]
    private TradeIrSimulatedBacktestResultV1? _tradeIrBacktestResult;

    public bool HasBacktestReadinessContext => HasChosenGeneratedCandidate || HasLoadedCombinedTradeIr;

    public bool HasTradeIrBacktestResult => TradeIrBacktestResult is not null;

    public bool CanPrepareGeneratedCandidateForBacktest =>
        CanEnterFourLaneConformance &&
        _tradeIrSimulatedBacktestRunner is not null &&
        !HasPendingFourLanePrompt &&
        !IsGenerating &&
        !IsRunningTradeIrBacktest &&
        TryResolveActiveTradeIr(out _, out _, out _, out _);

    public string BacktestActionText => IsRunningTradeIrBacktest
        ? "Running exact-hash smoke…"
        : TradeIrBacktestResult is not null
            ? "Run exact-hash smoke again"
            : "Run exact-hash synthetic smoke";

    public string CandidateBacktestAvailabilityText
    {
        get
        {
            if (_tradeIrSimulatedBacktestRunner is null)
                return "TEST DISABLED · the synthetic TradeIR smoke runner is not registered in this app build.";
            if (IsRunningTradeIrBacktest)
                return "TEST RUNNING · the exact active TradeIR hash is executing against deterministic synthetic QuoteL1 data.";
            if (IsGenerating)
                return "TEST DISABLED · candidate generation is in progress. Wait for the replacement batch to finish before testing.";
            if (HasPendingFourLanePrompt)
                return "TEST DISABLED · the visible candidates belong to the previous completed brief. " +
                       "A stopped or failed refinement is restored in the composer; apply it with Check & generate, or discard the pending request, before testing.";

            var selected = SelectedGeneratedCandidateOption;
            var chosen = ChosenGeneratedCandidateOption;
            var selectedIsActive = selected?.CandidateHashSha256 is { } selectedHash &&
                chosen?.CandidateHashSha256 is { } chosenHash &&
                string.Equals(selectedHash, chosenHash, StringComparison.Ordinal);
            var hasActiveTradeIr = TryResolveActiveTradeIr(out _, out _, out var label, out _);

            if (selected is { Result.Lane: not StrategyGenerationLaneV1.TypedGraph } sourceReview)
            {
                var boundary = SourceReviewTestBoundary(sourceReview.Result.Lane);
                if (hasActiveTradeIr)
                    return $"{boundary} TEST ENABLED only for the active {label} exact hash below; " +
                           "the action does not test the previewed source draft.";

                var availableGraph = GeneratedCandidateOptions.FirstOrDefault(static option =>
                    option.Result.Lane == StrategyGenerationLaneV1.TypedGraph);
                if (availableGraph is { Result.PackageValid: true })
                    return $"{boundary} To test: preview Graph · Typed → Use selected in editor → " +
                           "run exact-hash synthetic smoke.";
                if (availableGraph is not null)
                    return $"{boundary} {GraphBlockerText(availableGraph)} This batch has no synthetic smoke target.";
                return $"{boundary} This batch has no Graph · Typed artifact for synthetic smoke.";
            }

            if (hasActiveTradeIr && (selected is null || selectedIsActive || HasLoadedCombinedTradeIr))
            {
                if (TradeIrBacktestResult is { Status: TradeIrSimulatedBacktestStatusV1.Rejected } rejected)
                {
                    var issue = rejected.Issues.FirstOrDefault();
                    var detail = issue is null
                        ? "closed-target admission rejected the exact graph"
                        : $"{issue.Code}: {issue.Message}";
                    return $"Graph valid · smoke-incompatible with the installed QuoteL1 EMA runner · {detail}";
                }

                if (TradeIrBacktestResult is { Succeeded: true })
                    return $"QuoteL1 EMA smoke passed · {label} · deterministic and not historical.";

                return $"TEST ENABLED · {label} is active at its exact package-valid hash. " +
                       "Smoke compatibility is not proven; " +
                       "the installed runner supports the QuoteL1 EMA smoke profile only and checks admission on run.";
            }

            if (selected is { Result.Lane: StrategyGenerationLaneV1.TypedGraph } graph)
            {
                if (!graph.Result.PackageValid)
                    return $"TEST DISABLED · {GraphBlockerText(graph)} It cannot enter the smoke runner. " +
                           "Load the QuoteL1 EMA smoke starter or regenerate.";

                return "TEST DISABLED · Graph · Typed is package-valid but preview-only. " +
                       "Use selected in editor to bind its exact generated hash; then the synthetic smoke action enables. " +
                       "QuoteL1 EMA compatibility is checked only when that action runs.";
            }

            var batchGraph = GeneratedCandidateOptions.FirstOrDefault(static option =>
                option.Result.Lane == StrategyGenerationLaneV1.TypedGraph);
            if (batchGraph is { Result.PackageValid: false })
                return $"TEST DISABLED · {GraphBlockerText(batchGraph)} " +
                       "This batch cannot enter the smoke runner. " +
                       "Load the QuoteL1 EMA smoke starter or regenerate.";

            if (batchGraph is { Result.PackageValid: true })
                return "TEST DISABLED · a package-valid Graph candidate exists but is not active in the editor. " +
                       "Preview Graph · Typed, use it in the editor, then run exact-hash synthetic smoke.";

            if (ChosenGeneratedCandidateOption is { Result.Lane: not StrategyGenerationLaneV1.TypedGraph })
                return "TEST DISABLED · the active source-review lane has no executable runtime target. " +
                       "A package-valid Graph · Typed artifact is required.";
            return "TEST DISABLED · no package-valid Graph · Typed artifact is active. " +
                   "Invalid or unsupported graphs cannot run; " +
                   "the installed runner supports the QuoteL1 EMA smoke profile only.";
        }
    }

    private static string SourceReviewTestBoundary(StrategyGenerationLaneV1 lane) => lane switch
    {
        StrategyGenerationLaneV1.VibePython =>
            "SELECTED LANE NOT TESTABLE · Vibe · Python is source-review only; no Python importer or runtime is registered.",
        StrategyGenerationLaneV1.DeclarativeSpec =>
            "SELECTED LANE NOT TESTABLE · Spec · Rules is source-review only; no deterministic Rules-to-TradeIR lowerer is registered.",
        StrategyGenerationLaneV1.CspPython =>
            "SELECTED LANE NOT TESTABLE · CSP · Events is source-review only; no CSP host or importer is registered.",
        _ => "SELECTED LANE NOT TESTABLE · this format has no registered executable runtime target.",
    };

    private static string GraphBlockerText(StrategyGenerationCandidateOption graph)
    {
        var state = graph.Result.Readiness switch
        {
            StrategyGenerationReadinessV1.Invalid => "Graph invalid",
            StrategyGenerationReadinessV1.Unsupported => "Graph unsupported",
            StrategyGenerationReadinessV1.Failed => "Graph generation failed",
            _ => "Graph not package-valid",
        };
        return $"{state} · {graph.FirstIssueCode}: {graph.FirstIssueMessage}";
    }

    public string BacktestReadinessTitle
    {
        get
        {
            if (TryResolveActiveTradeIr(out _, out _, out var label, out _))
                return $"Synthetic test readiness · {label}";
            return ChosenGeneratedCandidateOption is { } chosen
                ? $"Test availability · {chosen.LaneName}"
                : "Synthetic test availability";
        }
    }

    public string BacktestReadinessText
    {
        get
        {
            if (TryResolveActiveTradeIr(out _, out _, out _, out _))
            {
                if (TradeIrBacktestResult is { Status: TradeIrSimulatedBacktestStatusV1.Rejected } rejected)
                {
                    var issue = rejected.Issues.FirstOrDefault();
                    var detail = issue is null
                        ? "Closed-target admission rejected this exact hash."
                        : $"{issue.Code} at {issue.Path}: {issue.Message}";
                    return "Graph package validation passed, but this graph is smoke-incompatible with the installed " +
                           $"QuoteL1 EMA target. {detail}";
                }

                if (TradeIrBacktestResult is { Succeeded: true })
                    return "Graph package validation and QuoteL1 EMA smoke admission passed for this exact hash. " +
                           "The deterministic synthetic run completed; this is not historical performance or worker isolation.";

                return "Graph package validation passed; smoke compatibility has not. The installed in-process runner " +
                       "supports the QuoteL1 EMA smoke profile only and performs exact data and target admission on run. " +
                       "This is not historical performance or worker isolation.";
            }

            return ChosenGeneratedCandidateOption?.Result.Lane switch
            {
                StrategyGenerationLaneV1.VibePython =>
                    "The ordinary-Python draft has no registered importer or runtime package. It cannot be tested yet.",
                StrategyGenerationLaneV1.DeclarativeSpec =>
                    "The Rules JSON draft has no registered deterministic lowerer to TradeIR. It cannot be tested yet.",
                StrategyGenerationLaneV1.TypedGraph =>
                    "The Graph candidate must be package-valid, active in the editor, and unchanged before this exact-hash test unlocks.",
                StrategyGenerationLaneV1.CspPython =>
                    "The CSP draft has no registered CSP runtime host or importer. It cannot be tested yet.",
                _ => "Choose a candidate to see its exact test requirements.",
            };
        }
    }

    public IReadOnlyList<CandidateReadinessStageRow> BacktestReadinessStages
    {
        get
        {
            var chosen = ChosenGeneratedCandidateOption;
            var hasTradeIr = TryResolveActiveTradeIr(out _, out _, out _, out _);
            if (chosen is null && !HasLoadedCombinedTradeIr) return [];

            var generated = hasTradeIr || chosen?.Result.Generated == true;
            var packageValid = hasTradeIr || chosen?.Result.PackageValid == true;
            if (!hasTradeIr && chosen is { Result.Lane: not StrategyGenerationLaneV1.TypedGraph })
                return SourceReviewReadinessStages(chosen, generated);

            return
            [
                new CandidateReadinessStageRow(
                    "1", "Exact generated hash", generated ? "READY" : "BLOCKED",
                    generated
                        ? "The active editor artifact still matches its generated candidate hash."
                        : "No active generated artifact is bound to the editor."),
                new CandidateReadinessStageRow(
                    "2", "TradeIR package", packageValid ? "PASSED" : "MISSING",
                    packageValid
                        ? "The installed TradeIR validator accepted the exact module."
                        : "Only package-valid Typed Graph artifacts can enter the smoke target."),
                new CandidateReadinessStageRow(
                    "3", "Synthetic data + target", hasTradeIr ? "CHECK ON RUN" : "LOCKED",
                    hasTradeIr
                        ? "The runner will create a hash-bound synthetic QuoteL1 snapshot, then perform data and closed-target admission."
                        : "A package-valid active TradeIR module is required."),
                new CandidateReadinessStageRow(
                    "4", "Runtime smoke", hasTradeIr ? "CHECK ON RUN" : "LOCKED",
                    "Runs the real evaluator, risk gateway, simulated order book, and portfolio in-process. It does not use historical data or an isolated worker."),
            ];
        }
    }

    private static IReadOnlyList<CandidateReadinessStageRow> SourceReviewReadinessStages(
        StrategyGenerationCandidateOption chosen,
        bool generated)
    {
        var (validationTitle, missingBoundary, runtimeBoundary) = chosen.Result.Lane switch
        {
            StrategyGenerationLaneV1.VibePython => (
                "Vibe Python authoring profile",
                "No deterministic Python-to-TradeIR lowerer or registered importer is installed.",
                "No constrained Python runtime package is registered."),
            StrategyGenerationLaneV1.DeclarativeSpec => (
                "Closed Rules v1 schema",
                "No deterministic Rules-to-TradeIR lowerer is installed.",
                "Rules JSON has no independent executable runtime target."),
            StrategyGenerationLaneV1.CspPython => (
                "Inert CSP authoring profile",
                "No CSP-to-TradeIR lowerer or registered importer is installed; Point72 CSP compatibility is unverified.",
                "No CSP runtime host or pinned CSP dependency is registered."),
            _ => throw new ArgumentOutOfRangeException(nameof(chosen)),
        };

        // A committed Generated lane reached this state only after its lane-native validator ran.
        // Do not require canonical selectability here: source-review lanes deliberately stop before
        // a package/importer boundary, which is the next stage this panel must expose.
        var validationPassed = generated &&
            chosen.Result.Readiness == StrategyGenerationReadinessV1.Generated;
        return
        [
            new CandidateReadinessStageRow(
                "1", "Exact generated hash", generated ? "READY" : "BLOCKED",
                generated
                    ? "The active editor artifact still matches its generated candidate hash."
                    : "No active generated artifact is bound to the editor."),
            new CandidateReadinessStageRow(
                "2", validationTitle, validationPassed ? "PASSED" : "BLOCKED",
                validationPassed
                    ? "The lane-native deterministic authoring validator accepted the exact artifact. This is validation evidence, not execution evidence."
                    : $"The lane-native validator stopped at {chosen.FirstIssuePath}: {chosen.FirstIssueCode} — {chosen.FirstIssueMessage}"),
            new CandidateReadinessStageRow(
                "3", "Canonical lowering / import", "MISSING",
                missingBoundary),
            new CandidateReadinessStageRow(
                "4", "Native runtime / test", "LOCKED",
                runtimeBoundary),
        ];
    }

    public string TradeIrBacktestStatusText => TradeIrBacktestResult switch
    {
        null when IsRunningTradeIrBacktest => "RUNNING",
        null => "NOT RUN",
        { Succeeded: true } => "SMOKE PASSED",
        { Status: TradeIrSimulatedBacktestStatusV1.Rejected } => "ADMISSION BLOCKED",
        { Status: TradeIrSimulatedBacktestStatusV1.Cancelled } => "CANCELED",
        _ => "SMOKE FAILED",
    };

    public string TradeIrBacktestSummary
    {
        get
        {
            if (IsRunningTradeIrBacktest)
                return "Admitting the exact module and replaying 512 deterministic synthetic QuoteL1 events…";
            if (TradeIrBacktestResult?.Report is not { } report)
                return TradeIrBacktestResult is null
                    ? "No synthetic test has run for this candidate hash."
                    : "No report was produced. Read the blocker below.";

            return $"{report.Summary.EventsProcessed:N0} events · {report.Trades.Count:N0} round trips · " +
                   $"net {report.Summary.NetProfit:N2} · max drawdown {report.Metrics.MaxDrawdown:P2}";
        }
    }

    public string TradeIrBacktestIssueText => TradeIrBacktestResult is null
        ? string.Empty
        : string.Join(Environment.NewLine, TradeIrBacktestResult.Issues.Select(issue =>
            $"{issue.Code} · {issue.Path}: {issue.Message}"));

    public string TradeIrBacktestBoundaryText =>
        "Scope: deterministic synthetic QuoteL1 smoke, in-process. This is not a historical backtest, optimization, profitability claim, or isolated-worker proof.";

    [RelayCommand(CanExecute = nameof(CanRunTradeIrSimulatedBacktest))]
    private async Task RunTradeIrSimulatedBacktestAsync()
    {
        if (!CanEnterFourLaneConformance ||
            _tradeIrSimulatedBacktestRunner is null ||
            !TryResolveActiveTradeIr(out var module, out var sourceHash, out var sourceLabel, out var moduleHash))
            return;

        _tradeIrBacktestCts?.Cancel();
        _tradeIrBacktestCts?.Dispose();
        var runCts = new CancellationTokenSource();
        _tradeIrBacktestCts = runCts;
        _tradeIrBacktestSourceHashSha256 = sourceHash;
        TradeIrBacktestResult = null;
        IsRunningTradeIrBacktest = true;
        WorkbenchTab = 3;
        AiStatus = $"Running an exact-hash synthetic TradeIR smoke test for {sourceLabel}. No historical data is used.";

        try
        {
            var result = await _tradeIrSimulatedBacktestRunner.RunAsync(
                new TradeIrSimulatedBacktestRequestV1(sourceHash, moduleHash, module),
                runCts.Token);

            if (!TryResolveActiveTradeIr(out _, out var currentHash, out _, out var currentModuleHash) ||
                !string.Equals(currentHash, sourceHash, StringComparison.Ordinal) ||
                !string.Equals(currentModuleHash, moduleHash, StringComparison.Ordinal))
            {
                AiStatus = "The active TradeIR changed while the smoke test was running, so its late result was discarded.";
                return;
            }

            TradeIrBacktestResult = result;
            if (result.Succeeded && result.Report is { } report)
            {
                AiStatus = $"Synthetic smoke test passed for exact candidate hash {sourceHash[..12]}…. This is not historical performance.";
                Status = $"Processed {report.Summary.EventsProcessed:N0} synthetic QuoteL1 events with {report.Trades.Count:N0} round trips; net {report.Summary.NetProfit:N2}.";
                Append(AuthoringMessage.Tool(
                    "Ok",
                    "TradeIR synthetic smoke passed",
                    $"{sourceLabel} · candidate {sourceHash[..12]}… · module {moduleHash[..12]}… · {TradeIrBacktestSummary}"));
            }
            else
            {
                var first = result.Issues.FirstOrDefault();
                AiStatus = first is null
                    ? "The synthetic smoke test did not produce a runnable report."
                    : $"Synthetic smoke blocked at {first.Path}: {first.Message}";
                Status = "The exact artifact was preserved. Fix the reported admission/runtime blocker and run the smoke test again.";
                Append(AuthoringMessage.Tool(
                    "Fail",
                    result.Status == TradeIrSimulatedBacktestStatusV1.Rejected
                        ? "TradeIR smoke admission blocked"
                        : "TradeIR synthetic smoke failed",
                    string.IsNullOrWhiteSpace(TradeIrBacktestIssueText)
                        ? result.Status.ToString()
                        : TradeIrBacktestIssueText));
            }
        }
        catch (OperationCanceledException)
        {
            AiStatus = "Synthetic TradeIR smoke test canceled.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TradeIR simulated backtest failed for {StrategyId}", StrategyId);
            AiStatus = $"Synthetic TradeIR smoke test failed: {exception.Message}";
            Append(AuthoringMessage.Tool("Fail", "TradeIR synthetic smoke failed", exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_tradeIrBacktestCts, runCts)) _tradeIrBacktestCts = null;
            runCts.Dispose();
            IsRunningTradeIrBacktest = false;
            Save();
        }
    }

    private bool CanRunTradeIrSimulatedBacktest() => CanPrepareGeneratedCandidateForBacktest;

    private bool TryResolveActiveTradeIr(
        out OperatorGraphModuleV1 module,
        out string sourceCandidateHashSha256,
        out string sourceLabel,
        out string moduleHashSha256)
    {
        module = null!;
        sourceCandidateHashSha256 = string.Empty;
        sourceLabel = string.Empty;
        moduleHashSha256 = string.Empty;

        StrategyGenerationCandidateV1? candidate = null;
        if (HasLoadedCombinedTradeIr &&
            CombinedTradeIrSynthesis?.Output is { PackageValid: true } combined &&
            combined.Candidate is { } combinedCandidate &&
            combined.CandidateHashSha256 is { } combinedHash &&
            combinedCandidate.Artifact.Kind == StrategyGenerationArtifactKindV1.TradeIrModuleJson)
        {
            candidate = combinedCandidate;
            sourceCandidateHashSha256 = combinedHash;
            sourceLabel = "combined TradeIR";
        }
        else if (_parallelCandidateBatch is { } batch &&
                 ChosenGeneratedCandidateHash is { } chosenHash)
        {
            var selection = StrategyGenerationBatchValidationV1.Select(batch, chosenHash);
            var exactLane = batch.Lanes.Where(lane => string.Equals(
                lane.CandidateHashSha256,
                chosenHash,
                StringComparison.Ordinal)).ToArray();
            if (selection is { Success: true, Candidate: { } chosenCandidate } &&
                exactLane is [{ Lane: StrategyGenerationLaneV1.TypedGraph, PackageValid: true }] &&
                chosenCandidate.Artifact.Kind == StrategyGenerationArtifactKindV1.TradeIrModuleJson &&
                EditorMatchesCandidate(chosenCandidate))
            {
                candidate = chosenCandidate;
                sourceCandidateHashSha256 = chosenHash;
                sourceLabel = "Graph · Typed";
            }
        }

        if (candidate?.Artifact.Document is not { } document) return false;
        try
        {
            module = OperatorGraphModuleCanonicalJsonV1.Deserialize(document.GetRawText());
            moduleHashSha256 = OperatorGraphModuleCanonicalJsonV1.Hash(module);
            return true;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            module = null!;
            sourceCandidateHashSha256 = string.Empty;
            sourceLabel = string.Empty;
            moduleHashSha256 = string.Empty;
            return false;
        }
    }

    partial void OnTradeIrBacktestResultChanged(TradeIrSimulatedBacktestResultV1? value) =>
        NotifyTradeIrBacktestStateChanged();

    partial void OnIsRunningTradeIrBacktestChanged(bool value) =>
        NotifyTradeIrBacktestStateChanged();

    private void NotifyTradeIrBacktestStateChanged(bool clearStaleResult = false)
    {
        if (clearStaleResult && TradeIrBacktestResult is not null)
        {
            var current = TryResolveActiveTradeIr(out _, out var hash, out _, out _)
                ? hash
                : null;
            if (!string.Equals(current, _tradeIrBacktestSourceHashSha256, StringComparison.Ordinal))
            {
                _tradeIrBacktestSourceHashSha256 = null;
                TradeIrBacktestResult = null;
                return;
            }
        }

        OnPropertyChanged(nameof(HasBacktestReadinessContext));
        OnPropertyChanged(nameof(HasTradeIrBacktestResult));
        OnPropertyChanged(nameof(CanPrepareGeneratedCandidateForBacktest));
        OnPropertyChanged(nameof(BacktestActionText));
        OnPropertyChanged(nameof(CandidateBacktestAvailabilityText));
        OnPropertyChanged(nameof(BacktestReadinessTitle));
        OnPropertyChanged(nameof(BacktestReadinessText));
        OnPropertyChanged(nameof(BacktestReadinessStages));
        OnPropertyChanged(nameof(TradeIrBacktestStatusText));
        OnPropertyChanged(nameof(TradeIrBacktestSummary));
        OnPropertyChanged(nameof(TradeIrBacktestIssueText));
        RunTradeIrSimulatedBacktestCommand.NotifyCanExecuteChanged();
    }

    private void DisposeTradeIrBacktest()
    {
        _tradeIrBacktestCts?.Cancel();
        _tradeIrBacktestCts?.Dispose();
        _tradeIrBacktestCts = null;
    }
}

public sealed partial class StrategyGenerationCandidateOption
{
    public string SyntheticTestCapabilityText => Result.Lane switch
    {
        StrategyGenerationLaneV1.VibePython =>
            "Not testable · Python importer/runtime missing",
        StrategyGenerationLaneV1.DeclarativeSpec =>
            "Not testable · Rules→TradeIR lowerer missing",
        StrategyGenerationLaneV1.TypedGraph when Result.PackageValid =>
            "Synthetic eligibility · exact hash + registered runner required",
        StrategyGenerationLaneV1.TypedGraph =>
            "Not testable · package-valid Graph required",
        StrategyGenerationLaneV1.CspPython =>
            "Not testable · CSP host/importer missing",
        _ => "Not testable · no runtime target",
    };
}
