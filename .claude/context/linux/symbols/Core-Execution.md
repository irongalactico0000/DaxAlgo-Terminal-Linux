# TradingTerminal.Core / Execution — public API surface (macOS/Avalonia)

Generated from source fingerprint `8af92ffea5ea`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/Execution/ExecutionCommands.cs
```cs
    7: public sealed record ExecutionCommandMetadata
    9: public ExecutionCommandMetadata(
   63: public int SchemaVersion { get; }
   64: public CommandId CommandId { get; }
   65: public CorrelationId CorrelationId { get; }
   66: public CausationId? CausationId { get; }
   67: public TradingAccountId TradingAccountId { get; }
   68: public StrategyId StrategyId { get; }
   69: public StrategyVersion StrategyVersion { get; }
   70: public VenueId VenueId { get; }
   71: public InstrumentId InstrumentId { get; }
   72: public ExecutionEnvironment Environment { get; }
   73: public DateTimeOffset CreatedAtUtc { get; }
   74: public long ExpectedOrderSequence { get; }
   75: public DateTimeOffset? ExpiresAtUtc { get; }
   78: public sealed record OrderTerms
   80: public OrderTerms(
  118: public OrderSide Side { get; }
  119: public OrderType Type { get; }
  120: public decimal Quantity { get; }
  121: public decimal? LimitPrice { get; }
  122: public decimal? StopPrice { get; }
  123: public TimeInForce TimeInForce { get; }
  124: public bool ReduceOnly { get; }
  127: public enum ExecutionCommandKind
  135: public enum ExecutionSafetyClassification
  147: public abstract record ExecutionCommand
  149: protected ExecutionCommand(ExecutionCommandMetadata metadata, OrderId orderId)
  157: public ExecutionCommandMetadata Metadata { get; }
  158: public OrderId OrderId { get; }
  160: public abstract ExecutionCommandKind Kind { get; }
  161: public abstract ExecutionSafetyClassification Safety { get; }
  163: public string CanonicalJson => ExecutionCanonicalJson.Serialize<ExecutionCommand>(this);
  165: public string PayloadHashSha256 => ExecutionCanonicalJson.Sha256(CanonicalJson);
  168: public sealed record SubmitOrderCommand : ExecutionCommand
  170: public SubmitOrderCommand(
  185: public ClientOrderId ClientOrderId { get; }
  186: public OrderTerms Terms { get; }
  187: public override ExecutionCommandKind Kind => ExecutionCommandKind.Submit;
  188: public override ExecutionSafetyClassification Safety =>
  192: public sealed record ReplaceOrderCommand : ExecutionCommand
  194: public ReplaceOrderCommand(ExecutionCommandMetadata metadata, OrderId orderId, OrderTerms replacementTerms)
  203: public OrderTerms ReplacementTerms { get; }
  204: public override ExecutionCommandKind Kind => ExecutionCommandKind.Replace;
  205: public override ExecutionSafetyClassification Safety =>
  209: public sealed record CancelOrderCommand : ExecutionCommand
  211: public CancelOrderCommand(ExecutionCommandMetadata metadata, OrderId orderId) : base(metadata, orderId)
  217: public override ExecutionCommandKind Kind => ExecutionCommandKind.Cancel;
  218: public override ExecutionSafetyClassification Safety => ExecutionSafetyClassification.ExposureReducingOrNeutral;
  221: public sealed record QueryOrderCommand : ExecutionCommand
  223: public QueryOrderCommand(ExecutionCommandMetadata metadata, OrderId orderId) : base(metadata, orderId)
  229: public override ExecutionCommandKind Kind => ExecutionCommandKind.Query;
  230: public override ExecutionSafetyClassification Safety => ExecutionSafetyClassification.QueryOnly;
```

## src/linux/Core/TradingTerminal.Core/Execution/ExecutionIdentifiers.cs
```cs
    7: public interface IExecutionIdentifier
    9:     string Value { get; }
   10:     bool IsEmpty { get; }
   14: public interface IExecutionIdentifier<TSelf> : IExecutionIdentifier
   15:     where TSelf : struct, IExecutionIdentifier<TSelf>
   17:     static abstract TSelf Parse(string value);
   24: public static string Validate(string value, string parameterName)
   36: public static void Require<T>(T value, string parameterName)
   45: public sealed class ExecutionIdentifierJsonConverterFactory : JsonConverterFactory
   47: public override bool CanConvert(Type typeToConvert) =>
   51: public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
   63: public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
   78: public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
   88: public readonly record struct OrderId : IExecutionIdentifier<OrderId>
   90: public OrderId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
   91: public string Value { get; }
   92: public bool IsEmpty => string.IsNullOrEmpty(Value);
   93: public static OrderId Parse(string value) => new(value);
   94: public override string ToString() => Value ?? string.Empty;
   98: public readonly record struct CommandId : IExecutionIdentifier<CommandId>
  100: public CommandId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  101: public string Value { get; }
  102: public bool IsEmpty => string.IsNullOrEmpty(Value);
  103: public static CommandId Parse(string value) => new(value);
  104: public override string ToString() => Value ?? string.Empty;
  108: public readonly record struct DispatchAttemptId : IExecutionIdentifier<DispatchAttemptId>
  110: public DispatchAttemptId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  111: public string Value { get; }
  112: public bool IsEmpty => string.IsNullOrEmpty(Value);
  113: public static DispatchAttemptId Parse(string value) => new(value);
  114: public override string ToString() => Value ?? string.Empty;
  118: public readonly record struct ExecutionEventId : IExecutionIdentifier<ExecutionEventId>
  120: public ExecutionEventId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  121: public string Value { get; }
  122: public bool IsEmpty => string.IsNullOrEmpty(Value);
  123: public static ExecutionEventId Parse(string value) => new(value);
  124: public override string ToString() => Value ?? string.Empty;
  128: public readonly record struct TradeId : IExecutionIdentifier<TradeId>
  130: public TradeId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  131: public string Value { get; }
  132: public bool IsEmpty => string.IsNullOrEmpty(Value);
  133: public static TradeId Parse(string value) => new(value);
  134: public override string ToString() => Value ?? string.Empty;
  138: public readonly record struct ClientOrderId : IExecutionIdentifier<ClientOrderId>
  140: public ClientOrderId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  141: public string Value { get; }
  142: public bool IsEmpty => string.IsNullOrEmpty(Value);
  143: public static ClientOrderId Parse(string value) => new(value);
  144: public override string ToString() => Value ?? string.Empty;
  148: public readonly record struct VenueOrderId : IExecutionIdentifier<VenueOrderId>
  150: public VenueOrderId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  151: public string Value { get; }
  152: public bool IsEmpty => string.IsNullOrEmpty(Value);
  153: public static VenueOrderId Parse(string value) => new(value);
  154: public override string ToString() => Value ?? string.Empty;
  158: public readonly record struct VenueId : IExecutionIdentifier<VenueId>
  160: public VenueId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  161: public string Value { get; }
  162: public bool IsEmpty => string.IsNullOrEmpty(Value);
  163: public static VenueId Parse(string value) => new(value);
  164: public override string ToString() => Value ?? string.Empty;
  168: public readonly record struct TradingAccountId : IExecutionIdentifier<TradingAccountId>
  170: public TradingAccountId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  171: public string Value { get; }
  172: public bool IsEmpty => string.IsNullOrEmpty(Value);
  173: public static TradingAccountId Parse(string value) => new(value);
  174: public override string ToString() => Value ?? string.Empty;
  178: public readonly record struct StrategyId : IExecutionIdentifier<StrategyId>
  180: public StrategyId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  181: public string Value { get; }
  182: public bool IsEmpty => string.IsNullOrEmpty(Value);
  183: public static StrategyId Parse(string value) => new(value);
  184: public override string ToString() => Value ?? string.Empty;
  188: public readonly record struct StrategyVersion : IExecutionIdentifier<StrategyVersion>
  190: public StrategyVersion(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  191: public string Value { get; }
  192: public bool IsEmpty => string.IsNullOrEmpty(Value);
  193: public static StrategyVersion Parse(string value) => new(value);
  194: public override string ToString() => Value ?? string.Empty;
  198: public readonly record struct CorrelationId : IExecutionIdentifier<CorrelationId>
  200: public CorrelationId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  201: public string Value { get; }
  202: public bool IsEmpty => string.IsNullOrEmpty(Value);
  203: public static CorrelationId Parse(string value) => new(value);
  204: public override string ToString() => Value ?? string.Empty;
  208: public readonly record struct CausationId : IExecutionIdentifier<CausationId>
  210: public CausationId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  211: public string Value { get; }
  212: public bool IsEmpty => string.IsNullOrEmpty(Value);
  213: public static CausationId Parse(string value) => new(value);
  214: public override string ToString() => Value ?? string.Empty;
  218: public readonly record struct OutboxId : IExecutionIdentifier<OutboxId>
  220: public OutboxId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  221: public string Value { get; }
  222: public bool IsEmpty => string.IsNullOrEmpty(Value);
  223: public static OutboxId Parse(string value) => new(value);
  224: public override string ToString() => Value ?? string.Empty;
  228: public readonly record struct RuntimeInstanceId : IExecutionIdentifier<RuntimeInstanceId>
  230: public RuntimeInstanceId(string value) => Value = ExecutionIdentifier.Validate(value, nameof(value));
  231: public string Value { get; }
  232: public bool IsEmpty => string.IsNullOrEmpty(Value);
  233: public static RuntimeInstanceId Parse(string value) => new(value);
  234: public override string ToString() => Value ?? string.Empty;
```

## src/linux/Core/TradingTerminal.Core/Execution/ExecutionSchema.cs
```cs
    8: public static class ExecutionSchema
   10: public const int CurrentVersion = 1;
   12: public static void RequireSupported(int version, string parameterName = "schemaVersion")
   19: public enum ExecutionEnvironment
   25: public enum ExecutionRuntimeMode
   34: public static class ExecutionCanonicalJson
   38: public static string Serialize<T>(T value)
   44: public static string Canonicalize(string json)
   54: public static T Deserialize<T>(string json)
   61: public static string Sha256(string canonicalJson)
   67: public static string Hash<T>(T value) => Sha256(Serialize(value));
  129: public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
  136: public static string RequireText(string value, string parameterName, int maximumLength = 1024)
  148: public static string RequireSha256(string value, string parameterName)
```

## src/linux/Core/TradingTerminal.Core/Execution/RiskPolicy.cs
```cs
    5: public enum RiskControlMode
   12: public enum RiskDecisionCode
   31: public sealed record RiskDecision(
   38: public static RiskDecision Allow(
   45: public static RiskDecision Deny(
   53: public sealed record RiskLimits
   55: public RiskLimits(
   83: public decimal MaximumOrderQuantity { get; }
   84: public decimal MaximumAbsolutePosition { get; }
   85: public decimal MaximumGrossNotional { get; }
   86: public decimal MinimumBuyingPower { get; }
   87: public decimal MaximumDailyLoss { get; }
   88: public decimal MaximumDrawdown { get; }
   89: public int MaximumExposureCommandsPerWindow { get; }
   90: public TimeSpan RateLimitWindow { get; }
   93: public sealed record RiskEvaluationContext
   95: public RiskEvaluationContext(
  156: public RiskLimits Limits { get; }
  157: public RiskControlMode ControlMode { get; }
  158: public bool KillSwitchActive { get; }
  159: public decimal CurrentPositionQuantity { get; }
  160: public decimal CurrentBuyReservedQuantity { get; }
  161: public decimal CurrentSellReservedQuantity { get; }
  162: public decimal CurrentNetReservedQuantity => CurrentBuyReservedQuantity - CurrentSellReservedQuantity;
  163: public decimal CurrentGrossReservedNotional { get; }
  164: public decimal ExistingOrderSignedReservation { get; }
  165: public decimal ExistingOrderGrossReservation { get; }
  166: public decimal ExistingOrderFilledQuantity { get; }
  167: public decimal AvailableBuyingPower { get; }
  168: public decimal DailyNetRealizedPnl { get; }
  169: public decimal CurrentEquity { get; }
  170: public decimal PeakEquity { get; }
  171: public decimal MarketPrice { get; }
  172: public int ExposureCommandsInWindow { get; }
  173: public DateTimeOffset EvaluatedAtUtc { get; }
  174: public DateTimeOffset TradingDayStartedAtUtc { get; }
  175: public decimal ContractMultiplier { get; }
  176: public string AccountCurrency { get; }
  179: public sealed record RiskPolicyEvidence
  181: public RiskPolicyEvidence(string policyVersion, string limitsHashSha256, RiskEvaluationContext context)
  191: public string PolicyVersion { get; }
  192: public string LimitsHashSha256 { get; }
  193: public RiskEvaluationContext Context { get; }
  195: public static RiskPolicyEvidence Capture(RiskEvaluationContext context) =>
  200: public static class RiskPolicy
  202: public const string PolicyVersion = "daxalgo-risk-policy-v2";
  204: public static RiskDecision Evaluate(ExecutionCommand command, RiskEvaluationContext context)
```
