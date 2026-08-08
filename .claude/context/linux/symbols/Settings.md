# TradingTerminal.Settings — public API surface (macOS/Avalonia)

Generated from source fingerprint `3026999d8534`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/UI/TradingTerminal.Settings/Archive/ArchiveActivityViewModel.cs
```cs
   15: public sealed partial class ArchiveActivityViewModel : ViewModelBase
   20: public ArchiveActivityViewModel(
   31: public ObservableCollection<ArchiveRow> Rows { get; }
   34: public ObservableCollection<CoverageRow> Coverage { get; }
   43: public bool HasPending => PendingCount > 0;
   47: public async Task RefreshAsync()
   78: public async Task InstantOffloadAsync()
  126: public sealed class ArchiveRow
  128: public required ArchiveManifestEntry Entry { get; init; }
  130: public long Id => Entry.Id;
  131: public string PeriodLabel => Entry.PeriodLabel;
  132: public string Range => $"{Entry.FromUtc:yyyy-MM-dd} → {Entry.ToUtc:yyyy-MM-dd}";
  133: public int Parts => Entry.Parts.Count;
  134: public string TotalBytesPretty => Fmt(Entry.TotalBytes);
  135: public string Target => Entry.Target.IsSavedMessages ? "Saved Messages" : (Entry.Target.ChatRef ?? "(unknown)");
  136: public long RowsQuotes => Entry.RowsQuotes;
  137: public long RowsBars => Entry.RowsBars;
  138: public string Uploaded => Entry.UploadedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
  139: public string LocalDeleted => Entry.DeletedLocal ? "yes" : "no";
  141: public static ArchiveRow From(ArchiveManifestEntry e) => new() { Entry = e };
  152: public sealed class CoverageRow
  155: public CoverageRow(ArchiveCoverageWindow w) => _w = w;
  157: public string PeriodLabel => _w.PeriodLabel;
  158: public string Range => $"{_w.FromUtc:yyyy-MM-dd} → {_w.ToUtc:yyyy-MM-dd}";
  159: public bool Offloaded => _w.Offloaded;
  160: public string Status => _w.Offloaded ? "Offloaded" : "Pending";
  161: public string ArchiveRef => _w.ArchiveId is { } id ? $"#{id}" : "—";
```

## src/linux/UI/TradingTerminal.Settings/Archive/ArchiveSettingsViewModel.cs
```cs
   18: public sealed partial class ArchiveSettingsViewModel : ViewModelBase
   26: public ArchiveSettingsViewModel(
   68: public bool DefaultTargetIsChat => string.Equals(DefaultTargetKind, "chat", StringComparison.OrdinalIgnoreCase);
   76: public bool ManualTargetIsChat => string.Equals(ManualTargetKind, "chat", StringComparison.OrdinalIgnoreCase);
   83: public IReadOnlyList<string> PeriodOptions { get; } = new[] { "Weekly", "Monthly" };
   84: public IReadOnlyList<string> TargetKindOptions { get; } = new[] { "saved", "chat" };
```

## src/linux/UI/TradingTerminal.Settings/Archive/ArchiveUserFile.cs
```cs
   14: public static class ArchiveUserFile
   16: public static string Path { get; } = System.IO.Path.Combine(
   20: public static void Save(ArchiveOptions archive, TelegramArchiveOptions telegram)
```

## src/linux/UI/TradingTerminal.Settings/Archive/TelegramArchiveCredentialProtection.cs
```cs
   12: public static class TelegramArchiveCredentialProtection
   34: public static string? Encrypt(string? plaintext)
   89: public static string? Decrypt(string? cipherBase64)
```

## src/linux/UI/TradingTerminal.Settings/Authoring/AiCodegenUserFile.cs
```cs
   15: public static class AiCodegenUserFile
   19: public static string Path { get; } = System.IO.Path.Combine(
   31: public static void SaveSelection(
```

## src/linux/UI/TradingTerminal.Settings/Authoring/AiProvidersSettingsViewModel.cs
```cs
   16: public sealed partial class AiProvidersSettingsViewModel : ViewModelBase
   20: public AiProvidersSettingsViewModel(IAiStrategyBuilder? builder = null, IAiKeyStore? keys = null)
   33: public ObservableCollection<AiProviderRow> Providers { get; }
   69: public sealed partial class AiProviderRow : ObservableObject
   73: public AiProviderRow(IStrategyCodegenClient client, IAiKeyStore? keys)
   81: public string ProviderId => _client.ProviderId;
   82: public string DisplayName => _client.DisplayName;
   83: public bool IsAvailable => _client.IsAvailable;
   84: public bool NeedsKey { get; }
   91: public string StatusText => IsAvailable
   95: public void MarkStored(bool stored)
```

## src/linux/UI/TradingTerminal.Settings/Authoring/AuthoringSessionStore.cs
```cs
   11: public sealed record AuthoringChatEntry(
   21: public const string User = "user";
   22: public const string Assistant = "assistant";
   23: public const string System = "system";
   31: public sealed record AuthoringSessionSnapshot(
   67: public const int CurrentAuthoringUxVersion = 2;
   70: public bool FourLaneGenerationEnabled =>
   74: public string Age
   86: public string Label => $"{DisplayName} ({StrategyId}) · {Age}";
   92: public interface IAuthoringSessionRepository
   94:     IReadOnlyList<AuthoringSessionSnapshot> List();
   95:     bool Save(AuthoringSessionSnapshot session);
   96:     void Delete(string strategyId);
  101: public static FileAuthoringSessionRepository Instance { get; } = new();
  107: public IReadOnlyList<AuthoringSessionSnapshot> List() => AuthoringSessionStore.List();
  108: public bool Save(AuthoringSessionSnapshot session) => AuthoringSessionStore.Save(session);
  109: public void Delete(string strategyId) => AuthoringSessionStore.Delete(strategyId);
  122: public static class AuthoringSessionStore
  130: public static string Directory { get; } = Path.Combine(
  137: public static bool Save(AuthoringSessionSnapshot session)
  158: public static IReadOnlyList<AuthoringSessionSnapshot> List()
  171: public static AuthoringSessionSnapshot? Load(string strategyId) =>
  174: public static void Delete(string strategyId)
```

## src/linux/UI/TradingTerminal.Settings/Authoring/LineDiff.cs
```cs
    5: public sealed record DiffLine(string Kind, string Text);
   14: public static class LineDiff
   19: public static (int Added, int Removed) Count(string before, string after)
   34: public static IReadOnlyList<DiffLine> Build(string before, string after)
```

## src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.Navigation.cs
```cs
    6: public enum StrategyAuthoringScreen
   12: public sealed partial class StrategyAuthoringViewModel
   20: public bool IsDesignScreen => ActiveScreen == StrategyAuthoringScreen.Design;
   21: public bool IsBuildScreen => ActiveScreen == StrategyAuthoringScreen.Build;
   23: public int WorkbenchGridColumn => IsDesignScreen ? 3 : 1;
   24: public int WorkbenchGridColumnSpan => IsDesignScreen ? 1 : 3;
   25: public bool ShowImplementationTabs => IsBuildScreen || !GenerateCandidateFirst;
   26: public bool ShowScreenNavigation => GenerateCandidateFirst;
   27: public bool ShowDesignRequestHeader => IsDesignScreen && GenerateCandidateFirst;
   28: public bool ShowImplementationHeader => IsBuildScreen || !GenerateCandidateFirst;
   29: public bool CanCompileCurrentSource =>
   34: public bool CanOpenDesignScreen => GenerateCandidateFirst && IsBuildScreen && !IsGenerating;
   35: public bool CanOpenBuildScreen =>
   40: public bool ShowDesignCandidateReview => IsDesignScreen && HasCandidate;
   41: public bool ShowBuildGenerationProgress => IsBuildScreen && IsGeneratingCandidates;
   42: public bool ShowBuildBusyStop => IsBuildScreen && IsGenerating && !IsGeneratingCandidates;
   43: public bool ShowBuildCandidateResults => IsBuildScreen && HasGeneratedCandidates;
   44: public bool ShowCandidateEmptyState => IsDesignScreen
   47: public bool ShowStartImplementationAction =>
   51: public bool ShowCliWorkspaceFooter => IsBuildScreen && AvailableClis.Count > 0;
   53: public string ActiveScreenTitle => !GenerateCandidateFirst
   59: public string ActiveScreenDescription => !GenerateCandidateFirst
   65: public string CandidateTabHeader => IsDesignScreen ? "Request" : "Compare";
   67: public string CandidateEmptyTitle => IsDesignScreen
   71: public string CandidateEmptyText => IsDesignScreen
```

## src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.StrategyIntent.cs
```cs
   11: public sealed partial class StrategyAuthoringViewModel
   39: public ObservableCollection<StrategyIntentProfileOption> StrategyIntentProfiles { get; } =
   44: public IReadOnlyList<StrategyIntentShapeOption> StrategyIntentShapes { get; } =
   60: public ObservableCollection<StrategyIntentRequirementRow> StrategyIntentRequirements { get; } = [];
   61: public ObservableCollection<StrategyResearchEvidenceRow> StrategyResearchEvidenceRows { get; } = [];
   62: public ObservableCollection<StrategyResearchFalsifierRow> StrategyResearchFalsifierRows { get; } = [];
   63: public ObservableCollection<StrategyResearchUnresolvedRow> StrategyResearchUnresolvedRows { get; } = [];
   64: public ObservableCollection<StrategyResearchResolvedRow> StrategyResearchResolvedRows { get; } = [];
   65: public ObservableCollection<StrategyIntentQuestionV1> StrategyIntentQuestions { get; } = [];
   66: public ObservableCollection<StrategyIntentIssueV1> StrategyIntentIssues { get; } = [];
   68: public bool HasStrategyIntentReview => _strategyIntentReviewStarted || StrategyIntentDraft is not null;
   69: public bool HasConfirmedStrategyIntent => ConfirmedStrategyIntent is not null;
   70: public string StrategyIntentFamilyText =>
   79: public bool CanConfirmStrategyIntentReview =>
   92: public bool CanEnterFourLaneConformance =>
  111: public bool CanGenerateStrategyImplementations => CanEnterFourLaneConformance;
  114: public void AddStrategyIntentProfile(StrategyStarterBrief brief)
  125: public void SelectStrategyIntentProfile(StrategyStarterBrief brief)
  137: public void BeginStrategyIntentReview()
  204: public void ReviewStrategyIntent(
 1005: public sealed record StrategyIntentProfileOption(
 1011: public static StrategyIntentProfileOption FromBrief(StrategyStarterBrief brief) =>
 1014: public static StrategyIntentProfileOption FromRestoredClassification(
 1022: public static StrategyIntentProfileOption CreateSignalOnly()
 1050: public override string ToString() => Title;
 1053: public sealed record StrategyIntentShapeOption(
 1059: public override string ToString() => Title;
 1062: public sealed record StrategyIntentApplicabilityOption(
 1066: public override string ToString() => Label;
 1069: public sealed partial class StrategyResearchEvidenceRow : ObservableObject
 1088: public string EvidenceId { get; }
 1089: public bool IsMaterial { get; }
 1090: public string MaterialityLabel => IsMaterial ? "Material evidence" : "Supporting evidence";
 1091: public IReadOnlyList<string> CandidateStatementIds { get; }
 1093: public static StrategyResearchEvidenceRow FromEvidence(
 1097: public ResearchEvidenceRequirementV1 ToEvidence() => new(
 1110: public sealed partial class StrategyResearchFalsifierRow : ObservableObject
 1125: public string FalsifierId { get; }
 1126: public bool IsMaterial { get; }
 1127: public string MaterialityLabel => IsMaterial ? "Material falsifier" : "Supporting falsifier";
 1128: public IReadOnlyList<string> CandidateStatementIds { get; }
 1130: public static StrategyResearchFalsifierRow FromFalsifier(
 1134: public ResearchFalsifierV1 ToFalsifier() => new(
 1143: public sealed partial class StrategyResearchUnresolvedRow : ObservableObject
 1164: public string ItemId { get; }
 1165: public bool IsMaterial { get; }
 1166: public string MaterialityLabel => IsMaterial ? "Material unresolved item — launch remains locked" : "Non-material open item";
 1167: public IReadOnlyList<string> CandidateStatementIds { get; }
 1168: public bool CanResolve => !string.IsNullOrWhiteSpace(Resolution);
 1170: public static StrategyResearchUnresolvedRow FromUnresolvedItem(
 1175: public ResearchUnresolvedItemV1 ToUnresolvedItem() => new(
 1181: public ResearchResolvedItemV1 ToResolvedItem(string resolutionProvenance) => new(
 1201: public sealed record StrategyResearchResolvedRow(
 1209: public string MaterialityLabel => IsMaterial ? "Material research choice resolved" : "Supporting research choice resolved";
 1211: public static StrategyResearchResolvedRow FromResolvedItem(ResearchResolvedItemV1 item) => new(
 1219: public ResearchResolvedItemV1 ToResolvedItem() => new(
 1228: public sealed partial class StrategyIntentRequirementRow : ObservableObject
 1291: public string RequirementId { get; }
 1292: public StrategySemanticStageV1 Stage { get; }
 1293: public string StageLabel { get; }
 1294: public string Question { get; }
 1295: public string Description { get; }
 1296: public bool IsMaterial { get; }
 1297: public string MaterialityLabel => IsMaterial ? "Material requirement" : "Supporting requirement";
 1298: public StrategyRequirementProvenanceV1? Provenance { get; }
 1299: public string ValueTypeId { get; }
 1300: public string? ValueUnit { get; }
 1301: public bool MustBeNotApplicable { get; }
 1302: public bool CanChangeApplicability { get; }
 1303: public IReadOnlyList<StrategyIntentApplicabilityOption> ApplicabilityOptions { get; }
 1304: public string AnswerWatermark => SelectedApplicability.Disposition switch
 1312: public static StrategyIntentRequirementRow FromQuestion(
 1332: public static StrategyIntentRequirementRow FromRequirement(
 1351: public StrategySemanticRequirementV1 ToRequirement(
```

## src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.TradeIrBacktest.cs
```cs
   15: public sealed partial class StrategyAuthoringViewModel
   27: public bool HasBacktestReadinessContext => HasChosenGeneratedCandidate || HasLoadedCombinedTradeIr;
   29: public bool HasTradeIrBacktestResult => TradeIrBacktestResult is not null;
   31: public bool CanPrepareGeneratedCandidateForBacktest =>
   39: public string BacktestActionText => IsRunningTradeIrBacktest
   45: public string CandidateBacktestAvailabilityText
  156: public string BacktestReadinessTitle
  168: public string BacktestReadinessText
  208: public IReadOnlyList<CandidateReadinessStageRow> BacktestReadinessStages
  292: public string TradeIrBacktestStatusText => TradeIrBacktestResult switch
  302: public string TradeIrBacktestSummary
  318: public string TradeIrBacktestIssueText => TradeIrBacktestResult is null
  323: public string TradeIrBacktestBoundaryText =>
  508: public sealed partial class StrategyGenerationCandidateOption
  510: public string SyntheticTestCapabilityText => Result.Lane switch
```

## src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.TradeIrSynthesis.cs
```cs
   15: public sealed partial class StrategyAuthoringViewModel
   31: public bool HasCombinedTradeIrSynthesis => CombinedTradeIrSynthesis is not null;
   33: public bool HasCurrentPackageValidCombinedTradeIr =>
   38: public bool HasLoadedCombinedTradeIr =>
   45: public bool CanSynthesizeTradeIr =>
   54: public bool CanUseCombinedTradeIr =>
   61: public string CombinedTradeIrStatusText => CombinedTradeIrSynthesis switch
   70: public string CombinedTradeIrActionText => HasLoadedCombinedTradeIr
   74: public string CombinedTradeIrSourceSummary
   86: public string CombinedTradeIrTargetHash =>
   89: public string CombinedTradeIrReceiptHash =>
   92: public string CombinedTradeIrIssueText => CombinedTradeIrSynthesis is null
```

## src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.cs
```cs
   38: public sealed partial class StrategyAuthoringViewModel : ViewModelBase, IDisposable
   80: public StrategyAuthoringViewModel(
  173: public bool AiEnabled => _ai is not null;
  174: public bool AiHasProvider => AiProviders.Any(p => p.IsAvailable);
  178: public bool HasConversation => Messages.Count > 0;
  191: public IReadOnlyList<StrategyStarterBrief> AllStarterBriefs { get; }
  192: public ObservableCollection<StrategyStarterBrief> VisibleStarterBriefs { get; }
  193: public IReadOnlyList<string> StarterFamilyOptions { get; }
  194: public IReadOnlyList<string> StarterHorizonOptions { get; }
  195: public IReadOnlyList<string> StarterDataOptions { get; }
  202: public string StarterResultText =>
  282: public ObservableCollection<StrategyCandidateGroupRow> CandidateGroups { get; }
  283: public ObservableCollection<StrategyCandidateStatementV1> CandidateOpenQuestions { get; }
  284: public ObservableCollection<StrategyBuildSupportRow> CandidateBuildSupport { get; }
  285: public ObservableCollection<StrategyCandidateIssueV1> CandidateIssues { get; }
  286: public ObservableCollection<StrategyGenerationCandidateOption> GeneratedCandidateOptions { get; }
  287: public ObservableCollection<StrategyGenerationLaneProgressRow> GenerationLaneProgressRows { get; }
  289: public bool HasCandidate => CurrentCandidate is not null;
  290: public bool HasGeneratedCandidates => GeneratedCandidateOptions.Count > 0;
  291: public bool HasCandidateContent => HasCandidate || HasGeneratedCandidates;
  292: public bool HasCandidateRestoreWarning => !string.IsNullOrWhiteSpace(CandidateRestoreWarning);
  293: public int SelectableGeneratedCandidateCount =>
  295: public int BlockedGeneratedCandidateCount =>
  297: public bool HasBlockedGeneratedCandidates => BlockedGeneratedCandidateCount > 0;
  298: public StrategyGenerationCandidateOption? FirstBlockedGeneratedCandidateOption =>
  300: public string CandidateBatchHeadline
  312: public bool HasSelectedGeneratedCandidate => SelectedGeneratedCandidateOption?.Candidate is not null;
  313: public bool HasChosenGeneratedCandidate => ChosenGeneratedCandidateOption is not null;
  314: public bool HasPendingFourLanePrompt => !string.IsNullOrWhiteSpace(_pendingFourLanePrompt);
  315: public bool IsGeneratingCandidates => IsGenerating && GenerateCandidateFirst && !IsSynthesizingTradeIr;
  316: public bool HasRetainedCandidateBatchDuringGeneration => IsGeneratingCandidates && HasGeneratedCandidates;
  317: public bool CanChooseGeneratedCandidate =>
  329: public bool CanRevalidateGeneratedCandidate =>
  338: public bool CanConfirmCandidate => CurrentCandidate is not null && CandidateContentHash is not null &&
  340: public string GenerationModeLabel => GenerateCandidateFirst ? "STRATEGY RESEARCH" : "EXPERT C#";
  341: public string GenerationModeActionText => GenerateCandidateFirst
  344: public string GenerationLaneText => GenerateCandidateFirst ? "Research, confirm, then implement" : "Expert code";
  345: public string SendButtonText => GenerateCandidateFirst ? "Check strategy  ⌘↵" : "Generate code  ⌘↵";
  346: public string AuthoringBoundaryText => GenerateCandidateFirst
  351: public bool HasExpertCSharpFiles =>
  355: public bool HasNonCSharpExpertArtifact =>
  358: public string CandidateActionText => SelectedGeneratedCandidateOption is { CandidateHashSha256: { } selectedHash } &&
  364: public string ChosenGeneratedCandidateSummary => ChosenGeneratedCandidateOption is { } chosen
  367: public string GenerationProgressSummary
  539: public ObservableCollection<StrategyDiagnostic> Diagnostics { get; }
  554: public ObservableCollection<AuthoredFile> Files { get; }
  587: public ObservableCollection<AiProviderChoice> AiProviders { get; }
  593: public ObservableCollection<string> Models { get; } = [];
  600: public IReadOnlyList<CodegenEffort> Efforts { get; } =
  607: public bool EffortSupported => SelectedAiProvider is { } choice && AiModelCatalog.SupportsEffort(choice.ProviderId);
  643: public string ModelPillText =>
  668: public ObservableCollection<AiModelChoice> AllModels { get; }
  736: public IReadOnlyList<StrategyBuildEffort> BuildEfforts { get; } =
  757: public IReadOnlyList<AgentCliAdapter> AvailableClis => _cliLauncher?.AvailableClis() ?? [];
  826: public ObservableCollection<AuthoringMessage> Messages { get; }
  830: public ObservableCollection<string> Activity { get; }
  839: public ObservableCollection<BuildTask> Tasks { get; }
 1052: public string UsageText => InputTokens + OutputTokens == 0
 1111: public bool CanGenerateFourCandidates =>
 2346: public ObservableCollection<AuthoringSessionSnapshot> SavedSessions { get; } = [];
 2761: public ObservableCollection<ReviewFileEntry> ReviewFiles { get; } = [];
 3355: public void Dispose()
 3378: public sealed class MyStrategy : IBacktestStrategy
 3380: public static StrategyParameterSchema Schema { get; } = new(
 3384: public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
 3391: public MyStrategy(Contract contract) : this(contract, 20, 1.5) { }
 3393: public MyStrategy(Contract contract, int lookback, double threshold)
 3400: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
 3403: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
 3411: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
 3413: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
 3421: public sealed partial class AuthoredFile(string name, string content) : ObservableObject
 3435: public sealed partial class AuthoringMessage : ObservableObject
 3437: public const string KindUser = "User";
 3438: public const string KindAssistant = "Assistant";
 3439: public const string KindNote = "Note";
 3440: public const string KindTool = "Tool";
 3441: public const string KindPlan = "Plan";
 3442: public const string KindPlanText = "PlanText";
 3443: public const string KindFiles = "Files";
 3445: public AuthoringMessage(CodegenRole role, string text)
 3461: public static AuthoringMessage System(string? text) => new(KindNote, text ?? string.Empty);
 3466: public static AuthoringMessage Tool(string state, string title, string detail, string? more = null) =>
 3477: public static AuthoringMessage Plan(IReadOnlyList<BuildTask> tasks) =>
 3481: public static AuthoringMessage PlanText(string text) => new(KindPlanText, text);
 3483: public static AuthoringMessage FilesChanged(IReadOnlyList<FileChangeSummary> changes) =>
 3489: public CodegenRole Role { get; }
 3490: public bool IsSystem { get; }
 3491: public string Kind { get; }
 3492: public bool IsUser => !IsSystem && Role == CodegenRole.User;
 3493: public bool IsAssistant => !IsSystem && Role == CodegenRole.Assistant;
 3495: public string? ToolState { get; private init; }
 3496: public string? ToolTitle { get; private init; }
 3497: public string? ToolDetail { get; private init; }
 3498: public string? ToolMore { get; private init; }
 3499: public bool HasMore => !string.IsNullOrEmpty(ToolMore);
 3501: public IReadOnlyList<BuildTask>? PlanTasks { get; private init; }
 3502: public IReadOnlyList<FileChangeSummary>? FileChanges { get; private init; }
 3505: public string PlanSnapshotText() => PlanTasks is null
 3518: public DateTime TimestampLocal { get; } = DateTime.Now;
 3522: public sealed record StrategyCandidateGroupRow(
 3529: public string Location => Depth == 0 ? Kind : $"{new string('·', Depth)} {Kind}";
 3533: public sealed record StrategyBuildSupportRow(
 3540: public sealed partial class StrategyGenerationCandidateOption : ObservableObject
 3542: public StrategyGenerationCandidateOption(StrategyGenerationLaneResultV1 result) => Result = result;
 3544: public StrategyGenerationLaneResultV1 Result { get; }
 3551: public StrategyGenerationCandidateV1? Candidate => Result.Candidate;
 3552: public string? CandidateHashSha256 => Result.CandidateHashSha256;
 3553: public bool IsGenerated => Result.Generated;
 3554: public bool IsFailed => Result.Readiness is StrategyGenerationReadinessV1.Invalid
 3557: public bool PackageValidationAvailable => Result.PackageValidationAvailable;
 3558: public string LaneName => StrategyGenerationLaneCatalogV1.DisplayName(Result.Lane);
 3559: public string Representation => Result.Lane switch
 3567: public string ContractVersion => Candidate?.PackageBinding.ArtifactContractVersion ?? "no contract";
 3568: public string ContractAuthority => Candidate?.PackageBinding.Authority.AuthorityId ?? "no authority";
 3569: public string ContractRole => Candidate?.PackageBinding.Authority.SemanticRole switch
 3575: public string LoweringBoundary => Candidate?.PackageBinding.Authority.LoweringMode switch
 3583: public string CompatibilityBoundary => Candidate?.PackageBinding.Authority.ExternalCompatibility switch
 3589: public string SpecificationReference =>
 3591: public string StatusText => Result.Readiness switch
 3600: public string FailureHeading => Result.Readiness switch
 3607: public string ArtifactName => Candidate?.Artifact.FileName ?? "no artifact";
 3608: public string Summary => Candidate?.Interpretation ?? ErrorText;
 3609: public StrategyCandidateGenerationIssueV1? FirstIssue =>
 3613: public string FirstIssueCode => FirstIssue?.Code ?? "No issue code reported";
 3614: public string FirstIssuePath => FirstIssue?.Path ?? "No issue path reported";
 3615: public string FirstIssueMessage => FirstIssue?.Message
 3618: public string ErrorText => string.Join(Environment.NewLine, Result.Issues.Select(issue =>
 3620: public string RecoveryText => FirstIssueCode switch
 3632: public string ArtifactPreview => Candidate?.Artifact.Source
 3636: public string InspectablePreview => !string.IsNullOrWhiteSpace(ArtifactPreview)
 3641: public string PreviewHeading => !string.IsNullOrWhiteSpace(ArtifactPreview) && Candidate?.Artifact is { } artifact
 3646: public string PreviewStateText => IsChosen
 3649: public string FlexibilityText => Candidate is null
 3655: public sealed partial class StrategyGenerationLaneProgressRow : ObservableObject
 3657: public StrategyGenerationLaneProgressRow(StrategyGenerationLaneV1 lane) => Lane = lane;
 3659: public StrategyGenerationLaneV1 Lane { get; }
 3660: public string LaneName => StrategyGenerationLaneCatalogV1.DisplayName(Lane);
 3661: public string AgentName => Lane switch
 3669: public string ArtifactName => Lane switch
 3677: public string PurposeText => Lane switch
 3685: public string ValidationPlanText => Lane switch
 3712: public bool HasResult => ResultOption is not null;
 3713: public string InspectablePreview => ResultOption?.InspectablePreview ?? string.Empty;
 3714: public string PreviewHeading => ResultOption?.PreviewHeading ?? $"{ArtifactName} · waiting for result";
 3716: public void Apply(StrategyGenerationLaneProgressV1 progress)
 3747: public string StateLabel => State switch
 3760: public string StateDetail => State switch
 3776: public string PipelineText => State switch
 3817: public sealed record CandidateReadinessStageRow(
 3824: public sealed record FileChangeSummary(string Name, int Added, int Removed)
 3826: public string Counts => Removed > 0 ? $"+{Added} −{Removed}" : $"+{Added}";
 3829: public static string Pack(IReadOnlyList<FileChangeSummary> changes) =>
 3832: public static IReadOnlyList<FileChangeSummary>? Unpack(string? packed)
 3850: public sealed class ReviewFileEntry(string name, IReadOnlyList<DiffLine> lines)
 3852: public string Name { get; } = name;
 3853: public IReadOnlyList<DiffLine> Lines { get; } = lines;
 3854: public int Added { get; } = lines.Count(l => l.Kind == "add");
 3855: public int Removed { get; } = lines.Count(l => l.Kind == "del");
 3856: public string Counts => Removed > 0 ? $"+{Added} −{Removed}" : $"+{Added}";
 3861: public sealed class AiProviderChoice(IStrategyCodegenClient client)
 3863: public IStrategyCodegenClient Client { get; } = client;
 3864: public string ProviderId => Client.ProviderId;
 3865: public string DisplayName => Client.DisplayName;
 3866: public bool IsAvailable => Client.IsAvailable;
 3867: public string Label => IsAvailable ? DisplayName : $"{DisplayName} — not set up";
 3871: public enum BuildTaskState
 3881: public sealed partial class BuildTask(string title) : ObservableObject
 3883: public string Title { get; } = title;
```

## src/linux/UI/TradingTerminal.Settings/Authoring/StrategyStarterCatalog.cs
```cs
   11: public sealed record StrategyStarterBrief(
   20: public IReadOnlyList<string> FamilyLabels =>
   24: public StrategyStarterAxisLabels AxisLabels =>
   29: public static class StrategyStarterFamilies
   31: public const string TrendAndMomentum = "Trend & momentum";
   32: public const string ReversionAndRelativeValue = "Reversion & relative value";
   33: public const string ValueCarryAndQuality = "Value, carry & quality";
   34: public const string OrderFlowAndLiquidity = "Order flow & liquidity";
   35: public const string EventsAndCatalysts = "Events & catalysts";
   36: public const string VolatilityAndDerivatives = "Volatility & derivatives";
   37: public const string AllocationAndHedging = "Allocation & hedging";
   38: public const string Execution = "Execution";
   39: public const string AdaptiveAndMl = "Adaptive & ML";
   41: public static IReadOnlyList<string> All { get; } =
   56: public static class StrategyStarterTaxonomy
   58: public static IReadOnlyList<string> GetFamilyLabels(StrategySpec specification)
  137: public sealed record StrategyStarterAxisLabels(
  154: public static StrategyStarterAxisLabels From(StrategySpec specification)
  180: public static class StrategyStarterLabels
  182: public static string For(AssetClass value) => value switch
  193: public static string For(StrategyObjectiveKind value) => value switch
  204: public static string For(ReturnHypothesisKind value) => value switch
  223: public static string For(StrategyTriggerKind value) => value switch
  237: public static string For(StrategyHorizonKind value) => value switch
  247: public static string For(MarketTopologyKind value) => value switch
  260: public static string For(ExposureGeometryKind value) => value switch
  273: public static string For(StrategyInformationKind value) => value switch
  288: public static string For(SignalModelKind value) => value switch
  302: public static string For(PortfolioConstructionKind value) => value switch
  316: public static string For(StrategyExecutionPolicyKind value) => value switch
  333: public static string For(StrategyStateKind value) => value switch
  345: public static string For(StrategyRiskExitKind value) => value switch
  361: public static string For(StrategyAdaptationKind value) => value switch
  374: public sealed record StrategyStarterCatalogIssue(
  381: public static class StrategyStarterCatalog
  383: public const string QuoteL1EmaSmokePrompt =
  386: public const string LiquiditySweepFadePrompt =
  389: public const string FiveMinuteMomentumBreakoutPrompt =
  392: public const string CumulativeDeltaDivergencePrompt =
  395: public static IReadOnlyList<StrategyStarterBrief> All { get; } =
  855: public static bool MatchesSearch(StrategyStarterBrief brief, string? query)
  869: public static IReadOnlyList<StrategyStarterBrief> Filter(string? query) =>
  873: public static IReadOnlyList<StrategyStarterBrief> Filter(
  882: public static IReadOnlyList<StrategyStarterCatalogIssue> ValidateAll()
```

## src/linux/UI/TradingTerminal.Settings/Notifications/NotificationsSettingsViewModel.cs
```cs
   17: public sealed partial class NotificationsSettingsViewModel : ViewModelBase
   23: public NotificationsSettingsViewModel(
   61: public IReadOnlyList<string> AiAnalystProviders { get; } =
```

## src/linux/UI/TradingTerminal.Settings/Notifications/NotificationsUserFile.cs
```cs
   13: public static class NotificationsUserFile
   19: public static string Path { get; } = System.IO.Path.Combine(
   25: public static void Save(NotificationsOptions options)
```

## src/linux/UI/TradingTerminal.Settings/Research/ResearchSettingsViewModel.cs
```cs
   17: public sealed partial class ResearchSettingsViewModel : ViewModelBase
   23: public ResearchSettingsViewModel(
```

## src/linux/UI/TradingTerminal.Settings/Research/ResearchUserFile.cs
```cs
   14: public static class ResearchUserFile
   18: public static string Path { get; } = System.IO.Path.Combine(
   25: public static void Save(ResearchReproOptions options, bool autoLaunchSidecar, int sidecarPort)
```

## src/linux/UI/TradingTerminal.Settings/Support/SupportInfo.cs
```cs
   15: public const string DeveloperEmail = "dhruvsha.info@gmail.com";
   17: public const string ProductName = "DaxAlgo Terminal";
   19: public const string GitHubUrl = "https://github.com/dhruuvsharma/DaxAlgo-Terminal";
   23: public static string DisplayVersion
```

## src/linux/UI/TradingTerminal.Settings/Support/SupportViewModel.cs
```cs
   19: public sealed partial class SupportViewModel : ViewModelBase
   23: public SupportViewModel(ILogger<SupportViewModel> logger)
   28: public string ProductName => SupportInfo.ProductName;
   30: public string Version => SupportInfo.DisplayVersion;
   32: public string DeveloperEmail => SupportInfo.DeveloperEmail;
   34: public string ThankYouMessage =>
   39: public string DonateMessage =>
   53: public event EventHandler? CloseRequested;
```
