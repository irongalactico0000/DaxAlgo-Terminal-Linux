# DaxAlgo.Codegen — public API surface (macOS/Avalonia)

Generated from source fingerprint `cb463a404ff1`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Tools/DaxAlgo.Codegen/AgentCliCodegenClient.cs
```cs
   10: public sealed record AgentCliAdapter(
   27: public static AgentCliAdapter ClaudeCode { get; } =
   40: public static AgentCliAdapter Codex { get; } =
   45: public static IReadOnlyList<AgentCliAdapter> All { get; } = [ClaudeCode, Codex];
   50: public IReadOnlyList<string>? StreamFlags { get; init; }
   54: public IReadOnlyList<string> ArgumentsFor(
   93: public sealed class AgentCliCodegenClient : IStrategyCodegenClient
  102: public AgentCliCodegenClient(
  116: public string ProviderId => _adapter.ProviderId;
  117: public string DisplayName => _adapter.DisplayName;
  118: public bool IsAvailable => _resolveOnPath(_adapter.Executable) is not null;
  121: public string Model => _model ?? string.Empty;
  122: public CodegenEffort Effort => _effort;
  123: public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);
  131: public async IAsyncEnumerable<CodegenEvent> StreamAsync(
  267: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
```

## src/linux/Tools/DaxAlgo.Codegen/AiModelCatalog.cs
```cs
   16: public static class AiModelCatalog
   29: public static IReadOnlyList<string> For(string providerId) => providerId.ToLowerInvariant() switch
   40: public static IReadOnlyList<string> Offer(string providerId, string? configuredModel)
   55: public static bool SupportsEffort(string providerId) => providerId.ToLowerInvariant() switch
```

## src/linux/Tools/DaxAlgo.Codegen/AiStrategyBuilder.cs
```cs
   13: public interface IAiStrategyBuilder
   17:     IReadOnlyList<IStrategyCodegenClient> Providers { get; }
   20:     IStrategyCodegenClient? DefaultProvider { get; }
   25:     IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort);
   29:     IReadOnlyList<string> ModelsFor(string providerId);
   37:     IReadOnlyList<AiModelChoice> AllModels();
   45:     StrategyBuildSession StartSession(
   46:     IStrategyCodegenClient provider, string strategyId, string displayName,
   47:     IReadOnlyList<CodegenMessage>? history = null, CodegenUsage? priorUsage = null,
   48:     StrategyBuildProfile? profile = null);
   53:     Task<StrategyBuildLoopResult> BuildAsync(
   54:     IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
   55:     CancellationToken ct = default);
   58: public sealed class AiStrategyBuilder(
   64: public IReadOnlyList<IStrategyCodegenClient> Providers => factory.BuildAll();
   66: public IStrategyCodegenClient? DefaultProvider => factory.SelectDefault();
   68: public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) =>
   71: public IReadOnlyList<string> ModelsFor(string providerId) => factory.ModelsFor(providerId);
   73: public IReadOnlyList<AiModelChoice> AllModels()
   95: public StrategyBuildSession StartSession(
  102: public Task<StrategyBuildLoopResult> BuildAsync(
```

## src/linux/Tools/DaxAlgo.Codegen/AnthropicCodegenClient.cs
```cs
   14: public sealed class AnthropicCodegenClient : IStrategyCodegenClient
   25: public AnthropicCodegenClient(
   36: public string ProviderId => "anthropic";
   37: public string DisplayName => "Anthropic (API key)";
   38: public bool IsAvailable => !string.IsNullOrWhiteSpace(_model) && !string.IsNullOrWhiteSpace(_apiKey);
   39: public string Model => _model;
   40: public CodegenEffort Effort => _effort;
   41: public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);
   45: public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
   72: public async IAsyncEnumerable<CodegenEvent> StreamAsync(
  145: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
  258: public WireCacheControl? CacheControl { get; init; }
  263: public static WireCacheControl Ephemeral { get; } = new("ephemeral");
```

## src/linux/Tools/DaxAlgo.Codegen/AnthropicStreamParser.cs
```cs
   21: public string Text => _text.ToString();
   23: public CodegenUsage Usage => new(_input, _output, _cached);
   29: public IEnumerable<CodegenEvent> Consume(JsonElement evt)
   81: public static async IAsyncEnumerable<JsonElement> ReadAsync(
```

## src/linux/Tools/DaxAlgo.Codegen/CliWorkspaceLauncher.cs
```cs
   11: public sealed record CliLaunchResult(bool Success, string Message, string WorkspacePath);
   20: public interface ICliWorkspaceLauncher
   24:     IReadOnlyList<AgentCliAdapter> AvailableClis();
   28:     CliLaunchResult Launch(AgentCliAdapter adapter, string strategyId, string displayName, StrategyBuildEffort effort);
   37: public sealed class CliWorkspaceLauncher(
   42: public IReadOnlyList<AgentCliAdapter> AvailableClis() =>
   45: public CliLaunchResult Launch(AgentCliAdapter adapter, string strategyId, string displayName, StrategyBuildEffort effort)
  332: public sealed class MyStrategy : IBacktestStrategy
  334: public static StrategyParameterSchema Schema { get; } = new(
  338: public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
  345: public MyStrategy(Contract contract) : this(contract, 20, 1.5) { }
  347: public MyStrategy(Contract contract, int lookback, double threshold)
  354: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
  357: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
  365: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  367: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/linux/Tools/DaxAlgo.Codegen/CodegenCodeExtractor.cs
```cs
   15: public static partial class CodegenCodeExtractor
   31: public static string Extract(string? reply)
   44: public static string StripCode(string? reply)
   66: public static IReadOnlyList<StrategyFile> ExtractFiles(string? reply)
```

## src/linux/Tools/DaxAlgo.Codegen/FakeCodegenClient.cs
```cs
   11: public sealed class FakeCodegenClient : IStrategyCodegenClient
   19: public FakeCodegenClient(params string[] replies)
   24: public string ProviderId => "fake";
   25: public string DisplayName => "Fake (deterministic)";
   26: public bool IsAvailable => true;
   29: public int CallCount { get; private set; }
   32: public CodegenUsage Usage { get; init; } = new(100, 50);
   36: public StrategyCodegenRequest? LastRequest { get; private set; }
   38: public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
   58: public const string DefaultKernel = """
   60: public sealed class GeneratedStrategy(Contract contract) : IBacktestStrategy
   66: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   68: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   77: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   78: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
```

## src/linux/Tools/DaxAlgo.Codegen/OpenAiCompatibleCodegenClient.cs
```cs
   15: public sealed class OpenAiCompatibleCodegenClient : IStrategyCodegenClient
   28: public OpenAiCompatibleCodegenClient(
   45: public string ProviderId { get; }
   46: public string DisplayName { get; }
   48: public bool IsAvailable =>
   52: public string Model => _model;
   53: public CodegenEffort Effort => _effort;
   54: public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);
   58: public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
   86: public async IAsyncEnumerable<CodegenEvent> StreamAsync(
  206: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
```

## src/linux/Tools/DaxAlgo.Codegen/ParallelStrategyCandidateGeneratorV1.cs
```cs
   11: public sealed class StrategyGenerationLaneAgentV1(StrategyGenerationLaneV1 lane) : IStrategyGenerationLaneAgentV1
   13: public StrategyGenerationLaneV1 Lane { get; } = lane;
   15: public async Task<StrategyGenerationLaneResultV1> GenerateAsync(
  208: public sealed class ParallelStrategyCandidateGeneratorV1 : IParallelStrategyCandidateGeneratorV1
  210: public const int MaxUserPromptCharacters = 100_000;
  215: public ParallelStrategyCandidateGeneratorV1(
  240: public async Task<ParallelStrategyGenerationResultV1> GenerateAsync(
```

## src/linux/Tools/DaxAlgo.Codegen/ParallelStrategyGenerationContractsV1.cs
```cs
    9: public enum StrategyGenerationLaneV1
   18: public enum StrategyGenerationLaneProgressStateV1
   30: public sealed record StrategyGenerationLaneProgressV1(
   35: public enum StrategyGenerationArtifactKindV1
   47: public enum StrategyGenerationReadinessV1
   62: public enum StrategyGenerationSemanticRoleV1
   69: public enum StrategyGenerationLoweringModeV1
   79: public enum StrategyGenerationExternalCompatibilityV1
   91: public sealed record StrategyGenerationContractAuthorityV1(
  104: public sealed record StrategyGenerationPackageBindingV1(
  115: public enum StrategyVariationAxisKindV1
  128: public sealed record StrategyGenerationParameterV1(
  144: public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
  163: public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
  168: public sealed record StrategyVariationAxisV1(
  179: public sealed record StrategyGenerationArtifactV1(
  190: public sealed record StrategyGenerationCandidateV1(
  206: public const string CurrentSchemaVersion = "strategy-generation-candidate/v2";
  209: public sealed record ParallelStrategyGenerationRequestV1(
  213: public sealed record StrategyGenerationLaneResultV1(
  221: public bool Generated => Candidate is not null && CandidateHashSha256 is not null &&
  229: public bool PackageValidationAvailable =>
  237: public bool Selectable
  277: public bool PackageValid
  304: public sealed record ParallelStrategyGenerationResultV1(
  311: public bool HasPackageValidCandidate =>
  316: public interface IStrategyGenerationLaneAgentV1
  318:     StrategyGenerationLaneV1 Lane { get; }
  320:     Task<StrategyGenerationLaneResultV1> GenerateAsync(
  321:     IStrategyCodegenClient provider,
  322:     ParallelStrategyGenerationRequestV1 request,
  323:     string expectedCandidateId,
  324:     CancellationToken ct = default,
  325:     IProgress<StrategyGenerationLaneProgressV1>? progress = null);
  328: public interface IParallelStrategyCandidateGeneratorV1
  330:     Task<ParallelStrategyGenerationResultV1> GenerateAsync(
  331:     IStrategyCodegenClient provider,
  332:     ParallelStrategyGenerationRequestV1 request,
  333:     CancellationToken ct = default,
  334:     IProgress<StrategyGenerationLaneProgressV1>? progress = null);
  337: public static class StrategyGenerationCandidateCanonicalJsonV1
  339: public static string Serialize(StrategyGenerationCandidateV1 candidate) =>
  342: public static StrategyGenerationCandidateV1 Deserialize(string json) =>
  345: public static string Hash(StrategyGenerationCandidateV1 candidate) =>
  368: public static string SerializeBatch(ParallelStrategyGenerationResultV1 result) =>
  371: public static ParallelStrategyGenerationResultV1 DeserializeBatch(string json) =>
  374: public static string PromptHash(string strategyId, string userPrompt) =>
  377: public static string RequestHash(
  427: public sealed record StrategyGenerationSelectionResultV1(
  432: public bool Success => Candidate is not null && CandidateHashSha256 is not null &&
  437: public sealed record StrategyGenerationRevalidationResultV1(
  442: public bool Applied => Batch is not null && LaneResult is not null && Issues is not null &&
  448: public static class StrategyGenerationBatchValidationV1
  450: public static IReadOnlyList<StrategyCandidateGenerationIssueV1> Validate(
  611: public static StrategyGenerationSelectionResultV1 Select(
  638: public static StrategyGenerationRevalidationResultV1 RevalidateArtifact(
  727: public static class StrategyGenerationLaneCatalogV1
  729: public static IReadOnlyList<StrategyGenerationLaneV1> Ordered { get; } = Array.AsReadOnly(
  738: public static string WireName(StrategyGenerationLaneV1 lane) => lane switch
  747: public static string DisplayName(StrategyGenerationLaneV1 lane) => lane switch
  756: public static StrategyGenerationArtifactKindV1 ArtifactKind(StrategyGenerationLaneV1 lane) => lane switch
```

## src/linux/Tools/DaxAlgo.Codegen/ParallelStrategyGenerationPromptV1.cs
```cs
    8: public static string AgentId(StrategyGenerationLaneV1 lane) => lane switch
   17: public static string SystemContext(StrategyGenerationLaneV1 lane)
   25: public static string UserMessage(
  128: public const int MaxArtifactCharacters = 750_000;
  130: public static IReadOnlyList<StrategyCandidateGenerationIssueV1> Validate(
```

## src/linux/Tools/DaxAlgo.Codegen/StrategyBacktestSmoke.cs
```cs
   20: public static class StrategyBacktestSmoke
   35: public static async Task<string?> RunAsync(BacktestStrategyOption option, CancellationToken ct = default)
   91: public DateTime UtcNow { get; private set; } = start;
   92: public void Advance(TimeSpan by) => UtcNow += by;
   99: public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
  102: public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default) => Task.CompletedTask;
  104: public IObservable<OrderEvent> OrderEvents { get; } = new NeverObservable();
  108: public IDisposable Subscribe(IObserver<OrderEvent> observer) => new Nothing();
```

## src/linux/Tools/DaxAlgo.Codegen/StrategyBuildSession.cs
```cs
    8: public enum BuildTurnKind
   34: public sealed record StrategyBuildTurn(
   43: public bool Success => Kind == BuildTurnKind.Compiled;
   62: public sealed class StrategyBuildSession
  109: public IStrategyCodegenClient Provider { get; }
  112: public string BasePack { get; }
  117: public string SystemContext { get; private set; }
  120: public IReadOnlyList<StrategySkill> LoadedSkills { get; private set; } = [];
  121: public string StrategyId { get; }
  122: public string DisplayName { get; }
  123: public int MaxFixAttempts { get; }
  126: public StrategyBuildProfile? Profile { get; }
  129: public IReadOnlyList<CodegenMessage> Transcript => _messages;
  132: public IReadOnlyList<StrategyFile> Files { get; private set; } = [];
  135: public CodegenUsage TotalUsage { get; private set; } = CodegenUsage.None;
  147: public async Task<StrategyBuildTurn> SendAsync(
  257: public void SyncEditedFiles(IReadOnlyList<StrategyFile> files) => Files = files;
```

## src/linux/Tools/DaxAlgo.Codegen/StrategyCandidateGenerationOrchestratorV1.cs
```cs
    9: public enum StrategyCandidateGenerationIssueSeverityV1
   15: public sealed record StrategyCandidateGenerationIssueV1(
   21: public sealed record StrategyGenerationAgentRunV1(
   34: public sealed record StrategyCandidateGenerationRequestV1(
   40: public sealed record StrategyCandidateGenerationResultV1(
   48: public bool Success => Candidate is not null && Assessment is not null &&
   52: public interface IStrategyCandidateGeneratorV1
   54:     Task<StrategyCandidateGenerationResultV1> GenerateAsync(
   55:     IStrategyCodegenClient provider,
   56:     StrategyCandidateGenerationRequestV1 request,
   57:     CancellationToken ct = default);
   65: public interface IStrategyGenerationAgentRouterV1
   67:     IStrategyCodegenClient ResolveSpecialist(
   68:     StrategySpecialistRequestV1 request,
   69:     IStrategyCodegenClient selectedProvider);
   72: public sealed class SameProviderStrategyGenerationAgentRouterV1 : IStrategyGenerationAgentRouterV1
   74: public IStrategyCodegenClient ResolveSpecialist(
   85: public sealed class StrategyCandidateGenerationOrchestratorV1(
   89: public const int MaxSpecialists = 4;
   90: public const int MaxUserInputCharacters = 100_000;
   91: public const int MaxModelResponseCharacters = 1_000_000;
   97: public async Task<StrategyCandidateGenerationResultV1> GenerateAsync(
  557: public static bool TryDeserialize<T>(
```

## src/linux/Tools/DaxAlgo.Codegen/StrategyCandidateGenerationPromptV1.cs
```cs
    8: public const string IntakeAgentId = "strategy.intake@1";
   10: public static string IntakeSystemContext => """
   97: public static string CreateIntakeUserMessage(
  113: public static string SpecialistSystemContext() => """
  143: public static string CreateSpecialistUserMessage(
```

## src/linux/Tools/DaxAlgo.Codegen/StrategyCodegenClientFactory.cs
```cs
   19: public sealed class StrategyCodegenClientFactory
   28: public StrategyCodegenClientFactory(Func<HttpClient> httpFactory, AiCodegenOptions options, Func<string, string?> keyResolver)
   38: public IReadOnlyList<IStrategyCodegenClient> BuildAll()
   67: public IStrategyCodegenClient? Build(string providerId, string? model, CodegenEffort effort = CodegenEffort.Default)
   87: public IReadOnlyList<string> ModelsFor(string providerId) =>
   92: public IStrategyCodegenClient? SelectDefault()
```

## src/linux/Tools/DaxAlgo.Codegen/StrategyCodegenOrchestrator.cs
```cs
   10: public sealed record StrategyBuildLoopResult(
   32: public sealed class StrategyCodegenOrchestrator(
   46: public StrategyBuildSession CreateSession(
   58: public async Task<StrategyBuildLoopResult> BuildAsync(
```

## src/linux/Tools/DaxAlgo.Codegen/StrategyCodegenServiceCollectionExtensions.cs
```cs
   11: public static class StrategyCodegenServiceCollectionExtensions
   22: public static IServiceCollection AddStrategyCodegen(this IServiceCollection services, IConfiguration configuration)
```

## src/linux/Tools/DaxAlgo.Codegen/StrategyContextPack.cs
```cs
   11: public sealed class StrategyContextPack
   16: public string SystemPrompt { get; }
   22: public static StrategyContextPack Load()
```

## src/linux/Tools/DaxAlgo.Codegen/StrategyGenerationPackageCatalogV1.cs
```cs
   91: public static bool IsSupported(StrategyGenerationLaneV1 lane) =>
   97: public static bool PackageValidationAvailable(StrategyGenerationLaneV1 lane) =>
  100: public static StrategyGenerationPackageBindingV1 RequireBinding(StrategyGenerationLaneV1 lane) => lane switch
  109: public static string ArtifactFileName(StrategyGenerationLaneV1 lane) => lane switch
  118: public static string ArtifactLanguage(StrategyGenerationLaneV1 lane) => lane switch
  126: public static string UnsupportedReason(StrategyGenerationLaneV1 lane) => lane switch
  139: public static string PromptContract(StrategyGenerationLaneV1 lane)
  378: public static IReadOnlyList<StrategyCandidateGenerationIssueV1> ValidatePackage(
```

## src/linux/Tools/DaxAlgo.Codegen/StrategyGenerationSessionV1.cs
```cs
   11: public sealed class StrategyGenerationSessionV1
   18: public StrategyGenerationSessionV1(
   51: public string WorkspaceId { get; }
   52: public string WorkspaceName { get; }
   53: public string CandidateId { get; }
   54: public StrategyCandidateV1? CurrentCandidate { get; private set; }
   55: public IReadOnlyList<StrategyCandidateV1> Revisions => _revisions;
   57: public StrategyGenerationWorkspaceV1 Workspace => new(
   65: public async Task<StrategyCandidateGenerationResultV1> SendAsync(
   87: public StrategyCandidateConfirmationResultV1 Confirm(string expectedContentHashSha256)
```

## src/linux/Tools/DaxAlgo.Codegen/StrategySkillLibrary.cs
```cs
   11: public sealed record StrategySkill(string Id, string Name, IReadOnlyList<string> Triggers, string Body)
   15: public int Score(string text)
   41: public sealed class StrategySkillLibrary
   46: public const int MaxSkillsPerSession = 3;
   47: public const int MaxCharacters = 12_000;
   53: public IReadOnlyList<StrategySkill> All => _skills;
   57: public static StrategySkillLibrary Load()
   77: public IReadOnlyList<StrategySkill> SelectFor(string? brief) => SelectFor(brief, MaxSkillsPerSession);
   82: public IReadOnlyList<StrategySkill> SelectFor(string? brief, int maxSkills)
  109: public static string Compose(string basePack, IReadOnlyList<StrategySkill> skills)
```

## src/linux/Tools/DaxAlgo.Codegen/TradeIrCandidateSynthesisV1.cs
```cs
    6: public sealed record StrategySynthesisSourceV1(
   13: public sealed record TradeIrCandidateSynthesisRequestV1(
   17: public sealed record TradeIrSynthesisReceiptV1(
   30: public const string CurrentSchemaVersion = "trade-ir-candidate-synthesis-receipt/v1";
   33: public sealed record TradeIrCandidateSynthesisResultV1(
   38: public bool Success => TradeIrCandidateSynthesisValidationV1.Validate(this).Count == 0;
   41: public interface ITradeIrCandidateSynthesizerV1
   43:     Task<TradeIrCandidateSynthesisResultV1> SynthesizeAsync(
   44:     IStrategyCodegenClient provider,
   45:     TradeIrCandidateSynthesisRequestV1 request,
   46:     CancellationToken ct = default);
   49: public static class TradeIrCandidateSynthesisCanonicalJsonV1
   51: public const string AgentId = "strategy.tradeir_synthesis@1";
   53: public static string ReceiptHash(TradeIrSynthesisReceiptV1 receipt) =>
   56: public static string RequestHash(
   75: public static string SynthesisId(string strategyId) => $"{strategyId.Trim()}/tradeir-synthesis/v1";
   90: public static class TradeIrCandidateSynthesisValidationV1
   92: public static IReadOnlyList<StrategyCandidateGenerationIssueV1> Validate(
  298: public sealed class TradeIrCandidateSynthesizerV1 : ITradeIrCandidateSynthesizerV1
  300: public const int MaxSourcePayloadCharacters = 1_000_000;
  302: public async Task<TradeIrCandidateSynthesisResultV1> SynthesizeAsync(
  524: public static string SystemContext { get; } = """
  536: public static string UserMessage(
```

## src/linux/Tools/DaxAlgo.Codegen/VibeQuantDeclarativeRulesContractV1.cs
```cs
   13: public const string SchemaVersion = "vibe-quant/declarative-rules/v1";
   16: public static IReadOnlyList<StrategyCandidateGenerationIssueV1> Validate(
```
