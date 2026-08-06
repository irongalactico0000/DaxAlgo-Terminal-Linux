# TradingTerminal.Settings — public API surface (macOS/Avalonia)

Generated from source fingerprint `cb463a404ff1`. Declaration lines only;
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
   60: public const int CurrentAuthoringUxVersion = 2;
   63: public bool FourLaneGenerationEnabled =>
   67: public string Age
   79: public string Label => $"{DisplayName} ({StrategyId}) · {Age}";
   85: public interface IAuthoringSessionRepository
   87:     IReadOnlyList<AuthoringSessionSnapshot> List();
   88:     bool Save(AuthoringSessionSnapshot session);
   89:     void Delete(string strategyId);
   94: public static FileAuthoringSessionRepository Instance { get; } = new();
  100: public IReadOnlyList<AuthoringSessionSnapshot> List() => AuthoringSessionStore.List();
  101: public bool Save(AuthoringSessionSnapshot session) => AuthoringSessionStore.Save(session);
  102: public void Delete(string strategyId) => AuthoringSessionStore.Delete(strategyId);
  115: public static class AuthoringSessionStore
  123: public static string Directory { get; } = Path.Combine(
  130: public static bool Save(AuthoringSessionSnapshot session)
  151: public static IReadOnlyList<AuthoringSessionSnapshot> List()
  164: public static AuthoringSessionSnapshot? Load(string strategyId) =>
  167: public static void Delete(string strategyId)
```

## src/linux/UI/TradingTerminal.Settings/Authoring/LineDiff.cs
```cs
    5: public sealed record DiffLine(string Kind, string Text);
   14: public static class LineDiff
   19: public static (int Added, int Removed) Count(string before, string after)
   34: public static IReadOnlyList<DiffLine> Build(string before, string after)
```

## src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.TradeIrBacktest.cs
```cs
   15: public sealed partial class StrategyAuthoringViewModel
   27: public bool HasBacktestReadinessContext => HasChosenGeneratedCandidate || HasLoadedCombinedTradeIr;
   29: public bool HasTradeIrBacktestResult => TradeIrBacktestResult is not null;
   31: public bool CanPrepareGeneratedCandidateForBacktest =>
   38: public string BacktestActionText => IsRunningTradeIrBacktest
   44: public string CandidateBacktestAvailabilityText
  155: public string BacktestReadinessTitle
  167: public string BacktestReadinessText
  207: public IReadOnlyList<CandidateReadinessStageRow> BacktestReadinessStages
  241: public string TradeIrBacktestStatusText => TradeIrBacktestResult switch
  251: public string TradeIrBacktestSummary
  267: public string TradeIrBacktestIssueText => TradeIrBacktestResult is null
  272: public string TradeIrBacktestBoundaryText =>
  456: public sealed partial class StrategyGenerationCandidateOption
  458: public string SyntheticTestCapabilityText => Result.Lane switch
```

## src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.TradeIrSynthesis.cs
```cs
   15: public sealed partial class StrategyAuthoringViewModel
   26: public bool HasCombinedTradeIrSynthesis => CombinedTradeIrSynthesis is not null;
   28: public bool HasCurrentPackageValidCombinedTradeIr =>
   33: public bool HasLoadedCombinedTradeIr =>
   40: public bool CanSynthesizeTradeIr =>
   48: public bool CanUseCombinedTradeIr =>
   54: public string CombinedTradeIrStatusText => CombinedTradeIrSynthesis switch
   63: public string CombinedTradeIrActionText => HasLoadedCombinedTradeIr
   67: public string CombinedTradeIrSourceSummary
   79: public string CombinedTradeIrTargetHash =>
   82: public string CombinedTradeIrReceiptHash =>
   85: public string CombinedTradeIrIssueText => CombinedTradeIrSynthesis is null
```

## src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.cs
```cs
   38: public sealed partial class StrategyAuthoringViewModel : ViewModelBase, IDisposable
   79: public StrategyAuthoringViewModel(
  169: public bool AiEnabled => _ai is not null;
  170: public bool AiHasProvider => AiProviders.Any(p => p.IsAvailable);
  174: public bool HasConversation => Messages.Count > 0;
  187: public IReadOnlyList<StrategyStarterBrief> AllStarterBriefs { get; }
  188: public ObservableCollection<StrategyStarterBrief> VisibleStarterBriefs { get; }
  189: public IReadOnlyList<string> StarterFamilyOptions { get; }
  190: public IReadOnlyList<string> StarterHorizonOptions { get; }
  191: public IReadOnlyList<string> StarterDataOptions { get; }
  198: public string StarterResultText =>
  270: public ObservableCollection<StrategyCandidateGroupRow> CandidateGroups { get; }
  271: public ObservableCollection<StrategyCandidateStatementV1> CandidateOpenQuestions { get; }
  272: public ObservableCollection<StrategyBuildSupportRow> CandidateBuildSupport { get; }
  273: public ObservableCollection<StrategyCandidateIssueV1> CandidateIssues { get; }
  274: public ObservableCollection<StrategyGenerationCandidateOption> GeneratedCandidateOptions { get; }
  275: public ObservableCollection<StrategyGenerationLaneProgressRow> GenerationLaneProgressRows { get; }
  277: public bool HasCandidate => CurrentCandidate is not null;
  278: public bool HasGeneratedCandidates => GeneratedCandidateOptions.Count > 0;
  279: public bool HasCandidateContent => HasCandidate || HasGeneratedCandidates;
  280: public bool HasCandidateRestoreWarning => !string.IsNullOrWhiteSpace(CandidateRestoreWarning);
  281: public int SelectableGeneratedCandidateCount =>
  283: public int BlockedGeneratedCandidateCount =>
  285: public bool HasBlockedGeneratedCandidates => BlockedGeneratedCandidateCount > 0;
  286: public StrategyGenerationCandidateOption? FirstBlockedGeneratedCandidateOption =>
  288: public string CandidateBatchHeadline
  300: public bool HasSelectedGeneratedCandidate => SelectedGeneratedCandidateOption?.Candidate is not null;
  301: public bool HasChosenGeneratedCandidate => ChosenGeneratedCandidateOption is not null;
  302: public bool HasPendingFourLanePrompt => !string.IsNullOrWhiteSpace(_pendingFourLanePrompt);
  303: public bool IsGeneratingCandidates => IsGenerating && GenerateCandidateFirst && !IsSynthesizingTradeIr;
  304: public bool HasRetainedCandidateBatchDuringGeneration => IsGeneratingCandidates && HasGeneratedCandidates;
  305: public bool CanChooseGeneratedCandidate =>
  313: public bool CanRevalidateGeneratedCandidate =>
  321: public bool CanConfirmCandidate => CurrentCandidate is not null && CandidateContentHash is not null &&
  323: public string GenerationModeLabel => GenerateCandidateFirst ? "FOUR AI LANES" : "EXPERT CODE";
  324: public string GenerationModeActionText => GenerateCandidateFirst ? "Use Expert code" : "Back to 4 candidates";
  325: public string GenerationLaneText => GenerateCandidateFirst ? "4 AI strategy lanes" : "Expert code";
  326: public string SendButtonText => GenerateCandidateFirst ? "Check & generate  ⌘↵" : "Generate code  ⌘↵";
  327: public string AuthoringBoundaryText => GenerateCandidateFirst
  330: public string CandidateActionText => SelectedGeneratedCandidateOption is { CandidateHashSha256: { } selectedHash } &&
  336: public string ChosenGeneratedCandidateSummary => ChosenGeneratedCandidateOption is { } chosen
  339: public string GenerationProgressSummary
  472: public ObservableCollection<StrategyDiagnostic> Diagnostics { get; }
  487: public ObservableCollection<AuthoredFile> Files { get; }
  518: public ObservableCollection<AiProviderChoice> AiProviders { get; }
  524: public ObservableCollection<string> Models { get; } = [];
  531: public IReadOnlyList<CodegenEffort> Efforts { get; } =
  538: public bool EffortSupported => SelectedAiProvider is { } choice && AiModelCatalog.SupportsEffort(choice.ProviderId);
  574: public string ModelPillText =>
  599: public ObservableCollection<AiModelChoice> AllModels { get; }
  667: public IReadOnlyList<StrategyBuildEffort> BuildEfforts { get; } =
  688: public IReadOnlyList<AgentCliAdapter> AvailableClis => _cliLauncher?.AvailableClis() ?? [];
  757: public ObservableCollection<AuthoringMessage> Messages { get; }
  761: public ObservableCollection<string> Activity { get; }
  770: public ObservableCollection<BuildTask> Tasks { get; }
  983: public string UsageText => InputTokens + OutputTokens == 0
 2146: public ObservableCollection<AuthoringSessionSnapshot> SavedSessions { get; } = [];
 2520: public ObservableCollection<ReviewFileEntry> ReviewFiles { get; } = [];
 3052: public void Dispose()
 3075: public sealed class MyStrategy : IBacktestStrategy
 3077: public static StrategyParameterSchema Schema { get; } = new(
 3081: public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
 3088: public MyStrategy(Contract contract) : this(contract, 20, 1.5) { }
 3090: public MyStrategy(Contract contract, int lookback, double threshold)
 3097: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
 3100: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
 3108: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
 3110: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
 3118: public sealed partial class AuthoredFile(string name, string content) : ObservableObject
 3132: public sealed partial class AuthoringMessage : ObservableObject
 3134: public const string KindUser = "User";
 3135: public const string KindAssistant = "Assistant";
 3136: public const string KindNote = "Note";
 3137: public const string KindTool = "Tool";
 3138: public const string KindPlan = "Plan";
 3139: public const string KindPlanText = "PlanText";
 3140: public const string KindFiles = "Files";
 3142: public AuthoringMessage(CodegenRole role, string text)
 3158: public static AuthoringMessage System(string? text) => new(KindNote, text ?? string.Empty);
 3163: public static AuthoringMessage Tool(string state, string title, string detail, string? more = null) =>
 3174: public static AuthoringMessage Plan(IReadOnlyList<BuildTask> tasks) =>
 3178: public static AuthoringMessage PlanText(string text) => new(KindPlanText, text);
 3180: public static AuthoringMessage FilesChanged(IReadOnlyList<FileChangeSummary> changes) =>
 3186: public CodegenRole Role { get; }
 3187: public bool IsSystem { get; }
 3188: public string Kind { get; }
 3189: public bool IsUser => !IsSystem && Role == CodegenRole.User;
 3190: public bool IsAssistant => !IsSystem && Role == CodegenRole.Assistant;
 3192: public string? ToolState { get; private init; }
 3193: public string? ToolTitle { get; private init; }
 3194: public string? ToolDetail { get; private init; }
 3195: public string? ToolMore { get; private init; }
 3196: public bool HasMore => !string.IsNullOrEmpty(ToolMore);
 3198: public IReadOnlyList<BuildTask>? PlanTasks { get; private init; }
 3199: public IReadOnlyList<FileChangeSummary>? FileChanges { get; private init; }
 3202: public string PlanSnapshotText() => PlanTasks is null
 3215: public DateTime TimestampLocal { get; } = DateTime.Now;
 3219: public sealed record StrategyCandidateGroupRow(
 3226: public string Location => Depth == 0 ? Kind : $"{new string('·', Depth)} {Kind}";
 3230: public sealed record StrategyBuildSupportRow(
 3237: public sealed partial class StrategyGenerationCandidateOption : ObservableObject
 3239: public StrategyGenerationCandidateOption(StrategyGenerationLaneResultV1 result) => Result = result;
 3241: public StrategyGenerationLaneResultV1 Result { get; }
 3246: public StrategyGenerationCandidateV1? Candidate => Result.Candidate;
 3247: public string? CandidateHashSha256 => Result.CandidateHashSha256;
 3248: public bool IsGenerated => Result.Generated;
 3249: public bool IsFailed => Result.Readiness is StrategyGenerationReadinessV1.Invalid
 3252: public bool PackageValidationAvailable => Result.PackageValidationAvailable;
 3253: public string LaneName => StrategyGenerationLaneCatalogV1.DisplayName(Result.Lane);
 3254: public string Representation => Result.Lane switch
 3262: public string ContractVersion => Candidate?.PackageBinding.ArtifactContractVersion ?? "no contract";
 3263: public string ContractAuthority => Candidate?.PackageBinding.Authority.AuthorityId ?? "no authority";
 3264: public string ContractRole => Candidate?.PackageBinding.Authority.SemanticRole switch
 3270: public string LoweringBoundary => Candidate?.PackageBinding.Authority.LoweringMode switch
 3276: public string CompatibilityBoundary => Candidate?.PackageBinding.Authority.ExternalCompatibility switch
 3282: public string SpecificationReference =>
 3284: public string StatusText => Result.Readiness switch
 3293: public string FailureHeading => Result.Readiness switch
 3300: public string ArtifactName => Candidate?.Artifact.FileName ?? "no artifact";
 3301: public string Summary => Candidate?.Interpretation ?? ErrorText;
 3302: public StrategyCandidateGenerationIssueV1? FirstIssue =>
 3306: public string FirstIssueCode => FirstIssue?.Code ?? "No issue code reported";
 3307: public string FirstIssuePath => FirstIssue?.Path ?? "No issue path reported";
 3308: public string FirstIssueMessage => FirstIssue?.Message
 3311: public string ErrorText => string.Join(Environment.NewLine, Result.Issues.Select(issue =>
 3313: public string ArtifactPreview => Candidate?.Artifact.Source
 3317: public string FlexibilityText => Candidate is null
 3323: public sealed partial class StrategyGenerationLaneProgressRow : ObservableObject
 3325: public StrategyGenerationLaneProgressRow(StrategyGenerationLaneV1 lane) => Lane = lane;
 3327: public StrategyGenerationLaneV1 Lane { get; }
 3328: public string LaneName => StrategyGenerationLaneCatalogV1.DisplayName(Lane);
 3329: public string AgentName => Lane switch
 3337: public string ArtifactName => Lane switch
 3345: public string PurposeText => Lane switch
 3353: public string ValidationPlanText => Lane switch
 3374: public void Apply(StrategyGenerationLaneProgressV1 progress)
 3387: public string StateLabel => State switch
 3399: public string StateDetail => State switch
 3413: public string PipelineText => State switch
 3451: public sealed record CandidateReadinessStageRow(
 3458: public sealed record FileChangeSummary(string Name, int Added, int Removed)
 3460: public string Counts => Removed > 0 ? $"+{Added} −{Removed}" : $"+{Added}";
 3463: public static string Pack(IReadOnlyList<FileChangeSummary> changes) =>
 3466: public static IReadOnlyList<FileChangeSummary>? Unpack(string? packed)
 3484: public sealed class ReviewFileEntry(string name, IReadOnlyList<DiffLine> lines)
 3486: public string Name { get; } = name;
 3487: public IReadOnlyList<DiffLine> Lines { get; } = lines;
 3488: public int Added { get; } = lines.Count(l => l.Kind == "add");
 3489: public int Removed { get; } = lines.Count(l => l.Kind == "del");
 3490: public string Counts => Removed > 0 ? $"+{Added} −{Removed}" : $"+{Added}";
 3495: public sealed class AiProviderChoice(IStrategyCodegenClient client)
 3497: public IStrategyCodegenClient Client { get; } = client;
 3498: public string ProviderId => Client.ProviderId;
 3499: public string DisplayName => Client.DisplayName;
 3500: public bool IsAvailable => Client.IsAvailable;
 3501: public string Label => IsAvailable ? DisplayName : $"{DisplayName} — not set up";
 3505: public enum BuildTaskState
 3515: public sealed partial class BuildTask(string title) : ObservableObject
 3517: public string Title { get; } = title;
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
  315: public static string For(StrategyExecutionPolicyKind value) => value switch
  331: public static string For(StrategyStateKind value) => value switch
  343: public static string For(StrategyRiskExitKind value) => value switch
  358: public static string For(StrategyAdaptationKind value) => value switch
  371: public sealed record StrategyStarterCatalogIssue(
  378: public static class StrategyStarterCatalog
  380: public const string QuoteL1EmaSmokePrompt =
  383: public const string LiquiditySweepFadePrompt =
  386: public const string FiveMinuteMomentumBreakoutPrompt =
  389: public const string CumulativeDeltaDivergencePrompt =
  392: public static IReadOnlyList<StrategyStarterBrief> All { get; } =
  852: public static bool MatchesSearch(StrategyStarterBrief brief, string? query)
  866: public static IReadOnlyList<StrategyStarterBrief> Filter(string? query) =>
  870: public static IReadOnlyList<StrategyStarterBrief> Filter(
  879: public static IReadOnlyList<StrategyStarterCatalogIssue> ValidateAll()
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
