namespace TradingTerminal.TradeIr.Runtime;

public enum TradeIrOrderSideV1
{
    Buy = 1,
    Sell = 2,
}

public enum TradeIrTimeInForceV1
{
    Day = 1,
    GoodTilCancelled = 2,
    ImmediateOrCancel = 3,
}

public enum TradeIrOrderKindV1
{
    Market = 1,
}

public enum TradeIrOrderFeedbackStatusV1
{
    Working = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Cancelled = 4,
    Rejected = 5,
    Denied = 6,
}

/// <summary>
/// Closed numeric/resource limits shared by plan construction, compilation, and evaluation.
/// Keeping position values within half of the signed 64-bit domain guarantees that the delta
/// between any two admitted positions is representable as a positive signed 64-bit quantity.
/// </summary>
public static class TradeIrRuntimeLimitsV1
{
    public const int MaximumInstructionCount = 4_096;
    public const long MaximumAbsolutePositionQuantity = long.MaxValue / 2;

    public static bool IsSupportedPositionQuantity(long value) =>
        value >= -MaximumAbsolutePositionQuantity && value <= MaximumAbsolutePositionQuantity;
}

/// <summary>
/// Closed instruction base. Its private-protected constructor prevents extension outside this
/// assembly; admitted plans can select only the six trusted instruction records below.
/// </summary>
public abstract class TradeIrInstructionV1
{
    private protected TradeIrInstructionV1(int slot, string nodeId)
    {
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
        Slot = slot;
        NodeId = TradeIrContractValidation.RequireText(nodeId, nameof(nodeId));
    }

    public int Slot { get; }
    public string NodeId { get; }
}

public sealed class QuoteMidInstructionV1 : TradeIrInstructionV1
{
    public QuoteMidInstructionV1(int slot, string nodeId, string requirementId)
        : base(slot, nodeId) =>
        RequirementId = TradeIrContractValidation.RequireText(requirementId, nameof(requirementId));

    public string RequirementId { get; }
}

public sealed class EmaInstructionV1 : TradeIrInstructionV1
{
    public EmaInstructionV1(int slot, string nodeId, int valueSlot, int period)
        : base(slot, nodeId)
    {
        if (valueSlot < 0) throw new ArgumentOutOfRangeException(nameof(valueSlot));
        if (period is < 2 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(period));
        ValueSlot = valueSlot;
        Period = period;
    }

    public int ValueSlot { get; }
    public int Period { get; }
}

public sealed class GreaterThanInstructionV1 : TradeIrInstructionV1
{
    public GreaterThanInstructionV1(int slot, string nodeId, int leftSlot, int rightSlot)
        : base(slot, nodeId)
    {
        if (leftSlot < 0) throw new ArgumentOutOfRangeException(nameof(leftSlot));
        if (rightSlot < 0) throw new ArgumentOutOfRangeException(nameof(rightSlot));
        LeftSlot = leftSlot;
        RightSlot = rightSlot;
    }

    public int LeftSlot { get; }
    public int RightSlot { get; }
}

public sealed class FixedQuantityInstructionV1 : TradeIrInstructionV1
{
    public FixedQuantityInstructionV1(
        int slot,
        string nodeId,
        int decisionSlot,
        long whenFalse,
        long whenTrue)
        : base(slot, nodeId)
    {
        if (decisionSlot < 0) throw new ArgumentOutOfRangeException(nameof(decisionSlot));
        if (!TradeIrRuntimeLimitsV1.IsSupportedPositionQuantity(whenFalse))
            throw new ArgumentOutOfRangeException(nameof(whenFalse));
        if (!TradeIrRuntimeLimitsV1.IsSupportedPositionQuantity(whenTrue))
            throw new ArgumentOutOfRangeException(nameof(whenTrue));
        DecisionSlot = decisionSlot;
        WhenFalse = whenFalse;
        WhenTrue = whenTrue;
    }

    public int DecisionSlot { get; }
    public long WhenFalse { get; }
    public long WhenTrue { get; }
}

public sealed class TrailingFractionInstructionV1 : TradeIrInstructionV1
{
    public TrailingFractionInstructionV1(
        int slot,
        string nodeId,
        int priceSlot,
        int targetSlot,
        double fraction)
        : base(slot, nodeId)
    {
        if (priceSlot < 0) throw new ArgumentOutOfRangeException(nameof(priceSlot));
        if (targetSlot < 0) throw new ArgumentOutOfRangeException(nameof(targetSlot));
        if (!double.IsFinite(fraction) || fraction is <= 0d or >= 1d)
            throw new ArgumentOutOfRangeException(nameof(fraction));
        PriceSlot = priceSlot;
        TargetSlot = targetSlot;
        Fraction = fraction;
    }

    public int PriceSlot { get; }
    public int TargetSlot { get; }
    public double Fraction { get; }
}

public sealed class MarketIntentInstructionV1 : TradeIrInstructionV1
{
    public MarketIntentInstructionV1(
        int slot,
        string nodeId,
        int targetSlot,
        int? exitSlot,
        TradeIrTimeInForceV1 timeInForce)
        : base(slot, nodeId)
    {
        if (targetSlot < 0) throw new ArgumentOutOfRangeException(nameof(targetSlot));
        if (exitSlot is < 0) throw new ArgumentOutOfRangeException(nameof(exitSlot));
        if (!Enum.IsDefined(timeInForce)) throw new ArgumentOutOfRangeException(nameof(timeInForce));
        TargetSlot = targetSlot;
        ExitSlot = exitSlot;
        TimeInForce = timeInForce;
    }

    public int TargetSlot { get; }
    public int? ExitSlot { get; }
    public TradeIrTimeInForceV1 TimeInForce { get; }
}

/// <summary>
/// Host-compiled, authority-free executable plan. The instruction collection is defensively
/// copied; the evaluator performs the semantic/topological/type checks before accepting it.
/// </summary>
public sealed record CompiledTradeIrPlanV1
{
    internal CompiledTradeIrPlanV1(
        string definitionSha256,
        string admissionManifestSha256,
        string runtimeSemanticsVersion,
        string instrumentKey,
        IReadOnlyList<TradeIrInstructionV1> instructions,
        string orderIntentOutputId,
        string orderIntentNodeId,
        bool flattenOnEnd)
    {
        DefinitionSha256 = TradeIrContractValidation.RequireSha256(definitionSha256, nameof(definitionSha256));
        AdmissionManifestSha256 = TradeIrContractValidation.RequireSha256(
            admissionManifestSha256,
            nameof(admissionManifestSha256));
        RuntimeSemanticsVersion = TradeIrContractValidation.RequireText(
            runtimeSemanticsVersion,
            nameof(runtimeSemanticsVersion));
        InstrumentKey = TradeIrContractValidation.RequireText(instrumentKey, nameof(instrumentKey));
        ArgumentNullException.ThrowIfNull(instructions);
        Instructions = Array.AsReadOnly(instructions.ToArray());
        OrderIntentOutputId = TradeIrContractValidation.RequireText(orderIntentOutputId, nameof(orderIntentOutputId));
        OrderIntentNodeId = TradeIrContractValidation.RequireText(orderIntentNodeId, nameof(orderIntentNodeId));
        FlattenOnEnd = flattenOnEnd;
    }

    public string DefinitionSha256 { get; }
    public string AdmissionManifestSha256 { get; }
    public string RuntimeSemanticsVersion { get; }
    public string InstrumentKey { get; }
    public IReadOnlyList<TradeIrInstructionV1> Instructions { get; }
    public string OrderIntentOutputId { get; }
    public string OrderIntentNodeId { get; }
    public bool FlattenOnEnd { get; }
}

public sealed record TradeIrQuoteFrameV1
{
    public TradeIrQuoteFrameV1(
        string instrumentKey,
        string admissionManifestSha256,
        long eventSequence,
        long eventTimeUnixMicroseconds,
        double bid,
        double ask,
        long currentPositionQuantity)
    {
        InstrumentKey = TradeIrContractValidation.RequireText(instrumentKey, nameof(instrumentKey));
        AdmissionManifestSha256 = TradeIrContractValidation.RequireSha256(
            admissionManifestSha256,
            nameof(admissionManifestSha256));
        if (eventSequence < 0) throw new ArgumentOutOfRangeException(nameof(eventSequence));
        if (eventTimeUnixMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(eventTimeUnixMicroseconds));
        if (!double.IsFinite(bid) || bid <= 0d) throw new ArgumentOutOfRangeException(nameof(bid));
        if (!double.IsFinite(ask) || ask < bid) throw new ArgumentOutOfRangeException(nameof(ask));
        if (!TradeIrRuntimeLimitsV1.IsSupportedPositionQuantity(currentPositionQuantity))
            throw new ArgumentOutOfRangeException(nameof(currentPositionQuantity));
        EventSequence = eventSequence;
        EventTimeUnixMicroseconds = eventTimeUnixMicroseconds;
        Bid = bid;
        Ask = ask;
        CurrentPositionQuantity = currentPositionQuantity;
    }

    public string InstrumentKey { get; }
    public string AdmissionManifestSha256 { get; }
    public long EventSequence { get; }
    public long EventTimeUnixMicroseconds { get; }
    public double Bid { get; }
    public double Ask { get; }
    public long CurrentPositionQuantity { get; }
}

public sealed record TradeIrPortfolioFrameV1
{
    public TradeIrPortfolioFrameV1(
        string instrumentKey,
        long eventSequence,
        long eventTimeUnixMicroseconds,
        long currentPositionQuantity)
    {
        InstrumentKey = TradeIrContractValidation.RequireText(instrumentKey, nameof(instrumentKey));
        if (eventSequence < 0) throw new ArgumentOutOfRangeException(nameof(eventSequence));
        if (eventTimeUnixMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(eventTimeUnixMicroseconds));
        if (!TradeIrRuntimeLimitsV1.IsSupportedPositionQuantity(currentPositionQuantity))
            throw new ArgumentOutOfRangeException(nameof(currentPositionQuantity));
        EventSequence = eventSequence;
        EventTimeUnixMicroseconds = eventTimeUnixMicroseconds;
        CurrentPositionQuantity = currentPositionQuantity;
    }

    public string InstrumentKey { get; }
    public long EventSequence { get; }
    public long EventTimeUnixMicroseconds { get; }
    public long CurrentPositionQuantity { get; }
}

public sealed record TradeIrOrderFeedbackV1
{
    public TradeIrOrderFeedbackV1(
        long intentSequence,
        TradeIrOrderFeedbackStatusV1 status,
        long cumulativeFilledQuantity)
    {
        if (intentSequence <= 0) throw new ArgumentOutOfRangeException(nameof(intentSequence));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (cumulativeFilledQuantity < 0) throw new ArgumentOutOfRangeException(nameof(cumulativeFilledQuantity));
        IntentSequence = intentSequence;
        Status = status;
        CumulativeFilledQuantity = cumulativeFilledQuantity;
    }

    public long IntentSequence { get; }
    public TradeIrOrderFeedbackStatusV1 Status { get; }
    public long CumulativeFilledQuantity { get; }
}

/// <summary>
/// An inert desired market-order shape. It carries no account, venue authority, execution command,
/// host decision, router, adapter, credential, or dispatch handle.
/// </summary>
public sealed record TradeIrOrderIntentV1
{
    internal TradeIrOrderIntentV1(
        string definitionSha256,
        string admissionManifestSha256,
        long intentSequence,
        long sourceEventSequence,
        string outputId,
        string nodeId,
        string instrumentKey,
        TradeIrOrderSideV1 side,
        long quantity,
        long targetQuantity,
        TradeIrTimeInForceV1 timeInForce,
        bool reduceOnly,
        long eventTimeUnixMicroseconds)
    {
        DefinitionSha256 = TradeIrContractValidation.RequireSha256(definitionSha256, nameof(definitionSha256));
        AdmissionManifestSha256 = TradeIrContractValidation.RequireSha256(
            admissionManifestSha256,
            nameof(admissionManifestSha256));
        if (intentSequence <= 0) throw new ArgumentOutOfRangeException(nameof(intentSequence));
        if (sourceEventSequence < 0) throw new ArgumentOutOfRangeException(nameof(sourceEventSequence));
        OutputId = TradeIrContractValidation.RequireText(outputId, nameof(outputId));
        NodeId = TradeIrContractValidation.RequireText(nodeId, nameof(nodeId));
        InstrumentKey = TradeIrContractValidation.RequireText(instrumentKey, nameof(instrumentKey));
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (!TradeIrRuntimeLimitsV1.IsSupportedPositionQuantity(targetQuantity))
            throw new ArgumentOutOfRangeException(nameof(targetQuantity));
        if (!Enum.IsDefined(timeInForce)) throw new ArgumentOutOfRangeException(nameof(timeInForce));
        if (eventTimeUnixMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(eventTimeUnixMicroseconds));
        IntentSequence = intentSequence;
        SourceEventSequence = sourceEventSequence;
        Side = side;
        Quantity = quantity;
        TargetQuantity = targetQuantity;
        TimeInForce = timeInForce;
        ReduceOnly = reduceOnly;
        EventTimeUnixMicroseconds = eventTimeUnixMicroseconds;
    }

    public string DefinitionSha256 { get; }
    public string AdmissionManifestSha256 { get; }
    public long IntentSequence { get; }
    public long SourceEventSequence { get; }
    public string OutputId { get; }
    public string NodeId { get; }
    public string InstrumentKey { get; }
    public TradeIrOrderKindV1 Kind => TradeIrOrderKindV1.Market;
    public TradeIrOrderSideV1 Side { get; }
    public long Quantity { get; }
    public long TargetQuantity { get; }
    public TradeIrTimeInForceV1 TimeInForce { get; }
    public bool ReduceOnly { get; }
    public long EventTimeUnixMicroseconds { get; }
}

internal static class TradeIrContractValidation
{
    public static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Value cannot have leading or trailing whitespace.", parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("Value cannot contain control characters.", parameterName);
        return value;
    }

    public static string RequireSha256(string value, string parameterName)
    {
        RequireText(value, parameterName);
        if (value.Length != 64 || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Value must be a lowercase SHA-256 hexadecimal digest.", parameterName);
        return value;
    }
}
