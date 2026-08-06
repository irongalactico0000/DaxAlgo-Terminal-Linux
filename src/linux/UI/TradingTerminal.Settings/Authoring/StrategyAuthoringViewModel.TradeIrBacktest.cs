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
        _tradeIrSimulatedBacktestRunner is not null &&
        !IsGenerating &&
        !IsRunningTradeIrBacktest &&
        TryResolveActiveTradeIr(out _, out _, out _, out _);

    public string BacktestActionText => IsRunningTradeIrBacktest
        ? "Running synthetic test…"
        : TradeIrBacktestResult is not null
            ? "Run synthetic test again"
            : "Run synthetic smoke test";

    public string CandidateBacktestAvailabilityText
    {
        get
        {
            if (_tradeIrSimulatedBacktestRunner is null)
                return "Backtest unavailable · the TradeIR smoke runner is not registered in this app build.";
            if (TryResolveActiveTradeIr(out _, out _, out var label, out _))
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

                return $"Graph valid · {label} is active. Smoke compatibility is not proven; " +
                       "the installed runner supports the QuoteL1 EMA smoke profile only and checks admission on run.";
            }

            if (SelectedGeneratedCandidateOption is { Result.Lane: StrategyGenerationLaneV1.TypedGraph } graph)
            {
                if (!graph.Result.Selectable)
                {
                    var state = graph.Result.Readiness switch
                    {
                        StrategyGenerationReadinessV1.Invalid => "invalid",
                        StrategyGenerationReadinessV1.Unsupported => "unsupported",
                        StrategyGenerationReadinessV1.Failed => "generation failed",
                        _ => "not package-valid",
                    };
                    return $"Graph {state} · {graph.FirstIssueCode}: {graph.FirstIssueMessage} " +
                           "It cannot enter the smoke runner. Load the QuoteL1 EMA smoke starter or regenerate.";
                }

                return "Graph valid · preview only. Smoke compatibility is separate; the installed runner supports " +
                       "the QuoteL1 EMA smoke profile only. Use selected in editor, then run admission.";
            }

            var sourceReview = SelectedGeneratedCandidateOption is { } previewed
                ? previewed.Result.Lane switch
                {
                    StrategyGenerationLaneV1.VibePython =>
                        "Vibe · Python is an inert source-review draft; no Python importer or runtime is registered. ",
                    StrategyGenerationLaneV1.DeclarativeSpec =>
                        "Spec · Rules is a source-review draft; no deterministic Rules-to-TradeIR lowerer is registered. ",
                    StrategyGenerationLaneV1.CspPython =>
                        "CSP · Events is an inert source-review draft; no CSP host or importer is registered. ",
                    _ => string.Empty,
                }
                : string.Empty;
            var batchGraph = GeneratedCandidateOptions.FirstOrDefault(static option =>
                option.Result.Lane == StrategyGenerationLaneV1.TypedGraph);
            if (batchGraph is { Result.Selectable: false })
            {
                var state = batchGraph.Result.Readiness switch
                {
                    StrategyGenerationReadinessV1.Invalid => "invalid",
                    StrategyGenerationReadinessV1.Unsupported => "unsupported",
                    StrategyGenerationReadinessV1.Failed => "generation failed",
                    _ => "not package-valid",
                };
                return $"{sourceReview}Graph {state} · {batchGraph.FirstIssueCode}: " +
                       $"{batchGraph.FirstIssueMessage} This batch cannot enter the smoke runner. " +
                       "Load the QuoteL1 EMA smoke starter or regenerate.";
            }

            if (batchGraph is { Result.Selectable: true })
                return $"{sourceReview}A package-valid Graph candidate exists, but smoke compatibility is separate. " +
                       "Preview and load Graph · Typed to request admission to the QuoteL1 EMA runner.";

            if (ChosenGeneratedCandidateOption is { Result.Lane: not StrategyGenerationLaneV1.TypedGraph })
                return $"{sourceReview}This batch has no package-valid Graph · Typed artifact for the QuoteL1 EMA runner.";
            return "No runnable Graph · Typed artifact is active. Invalid or unsupported graphs cannot run; " +
                   "the installed runner supports the QuoteL1 EMA smoke profile only.";
        }
    }

    public string BacktestReadinessTitle
    {
        get
        {
            if (TryResolveActiveTradeIr(out _, out _, out var label, out _))
                return $"Synthetic test readiness · {label}";
            return ChosenGeneratedCandidateOption is { } chosen
                ? $"Backtest readiness · {chosen.LaneName}"
                : "Backtest readiness";
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
        if (_tradeIrSimulatedBacktestRunner is null ||
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
