using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Infrastructure.Strategies.Authoring;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Review-bound bridge from the four independently generated authoring drafts to one new canonical
/// TradeIR candidate. The synthesis receipt is deliberately separate from the original four-lane
/// batch: it is a fifth artifact, produced by one additional provider call, with exact source hashes.
/// </summary>
public sealed partial class StrategyAuthoringViewModel
{
    private readonly ITradeIrCandidateSynthesizerV1? _tradeIrCandidateSynthesizer;
    private string? _loadedCombinedTradeIrCandidateHash;

    [ObservableProperty]
    private TradeIrCandidateSynthesisResultV1? _combinedTradeIrSynthesis;

    [ObservableProperty]
    private bool _isSynthesizingTradeIr;

    public bool HasCombinedTradeIrSynthesis => CombinedTradeIrSynthesis is not null;

    public bool HasCurrentPackageValidCombinedTradeIr =>
        CombinedTradeIrSynthesis is { } result &&
        _parallelCandidateBatch is { } batch &&
        TradeIrCandidateSynthesisValidationV1.Validate(result, batch).Count == 0;

    public bool HasLoadedCombinedTradeIr =>
        HasCurrentPackageValidCombinedTradeIr &&
        string.Equals(
            _loadedCombinedTradeIrCandidateHash,
            CombinedTradeIrSynthesis?.Output.CandidateHashSha256,
            StringComparison.Ordinal);

    public bool CanSynthesizeTradeIr =>
        _tradeIrCandidateSynthesizer is not null &&
        !HasPendingFourLanePrompt &&
        _parallelCandidateBatch is not null &&
        SelectableGeneratedCandidateCount > 0 &&
        SelectedAiProvider is { IsAvailable: true } &&
        !IsGenerating;

    public bool CanUseCombinedTradeIr =>
        !HasPendingFourLanePrompt &&
        HasCurrentPackageValidCombinedTradeIr &&
        !HasLoadedCombinedTradeIr &&
        !IsGenerating;

    public string CombinedTradeIrStatusText => CombinedTradeIrSynthesis switch
    {
        null => "NOT SYNTHESIZED",
        { } when HasCurrentPackageValidCombinedTradeIr => "PACKAGE VALID · READY TO LOAD",
        { Output.Readiness: StrategyGenerationReadinessV1.Invalid } => "SYNTHESIS INVALID",
        { Output.Readiness: StrategyGenerationReadinessV1.Failed } => "SYNTHESIS FAILED",
        _ => "SYNTHESIS BLOCKED",
    };

    public string CombinedTradeIrActionText => HasLoadedCombinedTradeIr
        ? "Combined TradeIR is active"
        : "Use combined TradeIR in editor";

    public string CombinedTradeIrSourceSummary
    {
        get
        {
            var sources = CombinedTradeIrSynthesis?.Receipt?.Sources;
            if (sources is null || sources.Count == 0)
                return $"Will use all {SelectableGeneratedCandidateCount} selectable drafts in lane order.";
            return $"{sources.Count} source draft(s): " + string.Join(" · ", sources.Select(source =>
                StrategyGenerationLaneCatalogV1.DisplayName(source.Lane)));
        }
    }

    public string CombinedTradeIrTargetHash =>
        CombinedTradeIrSynthesis?.Output.CandidateHashSha256 ?? "not available";

    public string CombinedTradeIrReceiptHash =>
        CombinedTradeIrSynthesis?.ReceiptHashSha256 ?? "not available";

    public string CombinedTradeIrIssueText => CombinedTradeIrSynthesis is null
        ? string.Empty
        : string.Join(Environment.NewLine, CombinedTradeIrSynthesis.Output.Issues.Select(issue =>
            $"{issue.Code} · {issue.Path}: {issue.Message}"));

    partial void OnCombinedTradeIrSynthesisChanged(TradeIrCandidateSynthesisResultV1? value) =>
        NotifyTradeIrSynthesisStateChanged();

    partial void OnIsSynthesizingTradeIrChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGeneratingCandidates));
        NotifyTradeIrSynthesisStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSynthesizeTradeIrAction))]
    private async Task SynthesizeTradeIrAsync()
    {
        if (!CanSynthesizeTradeIr ||
            _tradeIrCandidateSynthesizer is null ||
            _parallelCandidateBatch is not { } batch ||
            SelectedAiProvider is not { } choice)
            return;

        var sourceHashes = batch.Lanes
            .Where(static lane => lane.Selectable && lane.CandidateHashSha256 is not null)
            .Select(static lane => lane.CandidateHashSha256!)
            .ToArray();
        if (sourceHashes.Length == 0) return;

        var turnStrategyId = StrategyId.Trim();
        var turnEpoch = Interlocked.Increment(ref _generationContextEpoch);
        SetLoadedCombinedTradeIrCandidateHash(null);
        CombinedTradeIrSynthesis = null;
        IsSynthesizingTradeIr = true;
        IsGenerating = true;
        WorkbenchTab = 3;
        WorkingVerb = "Synthesizing canonical TradeIR…";
        StepText = $"Binding {sourceHashes.Length} selectable source draft(s) by exact hash";
        AiStatus = "A fifth AI call is reconciling the selectable drafts into one new canonical TradeIR candidate…";

        _generateCts?.Cancel();
        _generateCts?.Dispose();
        var turnCts = new CancellationTokenSource();
        _generateCts = turnCts;
        var ticking = TickElapsedAsync(turnCts.Token);

        try
        {
            var provider = ResolveClient(choice) ?? choice.Client;
            var result = await _tradeIrCandidateSynthesizer.SynthesizeAsync(
                provider,
                new TradeIrCandidateSynthesisRequestV1(batch, sourceHashes),
                turnCts.Token);
            if (!IsGenerationContextCurrent(turnEpoch, turnStrategyId) ||
                !ReferenceEquals(batch, _parallelCandidateBatch))
                return;

            InputTokens += result.Output.AgentRun.Usage.InputTokens;
            OutputTokens += result.Output.AgentRun.Usage.OutputTokens;
            CachedTokens += result.Output.AgentRun.Usage.CachedInputTokens;
            CombinedTradeIrSynthesis = result;

            var validation = TradeIrCandidateSynthesisValidationV1.Validate(result, batch);
            if (validation.Count == 0)
            {
                var targetHash = result.Output.CandidateHashSha256!;
                var receiptHash = result.ReceiptHashSha256!;
                AiStatus = $"Combined TradeIR is package-valid at {targetHash[..12]}…. Review it, then explicitly load it into the editor. No backtest ran.";
                Status = "Synthesis produced a fifth, canonical TradeIR artifact. Load it to unlock exact data and target admission for the synthetic smoke test.";
                Append(AuthoringMessage.Tool(
                    "Ok",
                    $"Synthesized TradeIR from {result.Receipt!.Sources.Count} source draft(s)",
                    $"target {targetHash[..12]}… · receipt {receiptHash[..12]}… · package valid · ready to load for smoke admission"));
            }
            else
            {
                var detail = string.Join(Environment.NewLine, validation.Select(issue =>
                    $"{issue.Code} · {issue.Path}: {issue.Message}"));
                AiStatus = "The synthesis result was preserved for inspection, but no package-valid combined TradeIR artifact was produced.";
                Status = "Combined TradeIR synthesis is blocked. Inspect the synthesis diagnostics; no source draft was overwritten.";
                Append(AuthoringMessage.Tool("Fail", "TradeIR synthesis blocked", detail));
            }
        }
        catch (OperationCanceledException)
        {
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
                AiStatus = "TradeIR synthesis stopped. The four source drafts were not changed.";
        }
        catch (Exception exception)
        {
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
            {
                _logger.LogError(exception, "TradeIR candidate synthesis threw for {Id}", turnStrategyId);
                AiStatus = $"TradeIR synthesis error: {exception.Message}";
                Append(AuthoringMessage.System(AiStatus));
            }
        }
        finally
        {
            turnCts.Cancel();
            await ticking;
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
            {
                IsGenerating = false;
                WorkingVerb = null;
                StepText = null;
                ElapsedText = null;
                ElapsedCompact = null;
                Save();
            }
            if (ReferenceEquals(_generateCts, turnCts)) _generateCts = null;
            turnCts.Dispose();
        }
    }

    private bool CanSynthesizeTradeIrAction() => CanSynthesizeTradeIr;

    [RelayCommand(CanExecute = nameof(CanUseCombinedTradeIrAction))]
    private void UseCombinedTradeIr()
    {
        if (_parallelCandidateBatch is not { } batch ||
            CombinedTradeIrSynthesis is not { } result ||
            TradeIrCandidateSynthesisValidationV1.Validate(result, batch).Count != 0 ||
            result.Output.Candidate is not { } candidate ||
            result.Output.CandidateHashSha256 is not { } candidateHash)
            return;

        InvalidateDerivedArtifactState(markUnregistered: true);
        SetFiles([new StrategyFile(candidate.Artifact.FileName, EditableArtifactContent(candidate.Artifact))]);
        _filesEditedByUser = false;
        SetLoadedCombinedTradeIrCandidateHash(candidateHash);
        WorkbenchTab = 3;
        AiStatus = $"Combined TradeIR loaded at exact synthesized hash {candidateHash[..12]}…. The smoke admission action is now visible below; compatibility is not proven until run, and editing the artifact clears this receipt proof.";
        Status = "Canonical TradeIR is in the editor. Choose Run synthetic smoke test to perform exact-hash data, target, and runtime admission.";
        Append(AuthoringMessage.Tool(
            "Ok",
            "Loaded combined TradeIR",
            $"{candidate.Artifact.FileName} · target {candidateHash[..12]}… · receipt {result.ReceiptHashSha256![..12]}… · smoke admission available"));
        Save();
    }

    private bool CanUseCombinedTradeIrAction() => CanUseCombinedTradeIr;

    private void ClearTradeIrSynthesis()
    {
        SetLoadedCombinedTradeIrCandidateHash(null);
        CombinedTradeIrSynthesis = null;
    }

    private void InvalidateLoadedTradeIrSynthesisProof()
    {
        if (_loadedCombinedTradeIrCandidateHash is null) return;
        SetLoadedCombinedTradeIrCandidateHash(null);
        Status = "The combined TradeIR editor content changed; its synthesis receipt no longer proves the edited artifact hash.";
    }

    private void SetLoadedCombinedTradeIrCandidateHash(string? hash)
    {
        if (string.Equals(_loadedCombinedTradeIrCandidateHash, hash, StringComparison.Ordinal)) return;
        _loadedCombinedTradeIrCandidateHash = hash;
        NotifyTradeIrSynthesisStateChanged();
        NotifyTradeIrBacktestStateChanged(clearStaleResult: true);
    }

    private void NotifyTradeIrSynthesisStateChanged()
    {
        OnPropertyChanged(nameof(HasCombinedTradeIrSynthesis));
        OnPropertyChanged(nameof(HasCurrentPackageValidCombinedTradeIr));
        OnPropertyChanged(nameof(HasLoadedCombinedTradeIr));
        OnPropertyChanged(nameof(CanSynthesizeTradeIr));
        OnPropertyChanged(nameof(CanUseCombinedTradeIr));
        OnPropertyChanged(nameof(CombinedTradeIrStatusText));
        OnPropertyChanged(nameof(CombinedTradeIrActionText));
        OnPropertyChanged(nameof(CombinedTradeIrSourceSummary));
        OnPropertyChanged(nameof(CombinedTradeIrTargetHash));
        OnPropertyChanged(nameof(CombinedTradeIrReceiptHash));
        OnPropertyChanged(nameof(CombinedTradeIrIssueText));
        SynthesizeTradeIrCommand.NotifyCanExecuteChanged();
        UseCombinedTradeIrCommand.NotifyCanExecuteChanged();
    }
}
