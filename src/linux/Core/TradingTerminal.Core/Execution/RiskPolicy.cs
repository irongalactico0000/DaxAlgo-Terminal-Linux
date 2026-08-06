using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Execution;

public enum RiskControlMode
{
    Active = 1,
    Reducing = 2,
    Halted = 3,
}

public enum RiskDecisionCode
{
    Allowed = 1,
    CancelOrQueryAlwaysAllowed = 2,
    ExpiredCommand = 3,
    KillSwitchActive = 4,
    NewExposureHalted = 5,
    ReduceOnlyWouldIncreaseExposure = 6,
    MaximumOrderQuantityExceeded = 7,
    MaximumPositionExceeded = 8,
    MaximumGrossNotionalExceeded = 9,
    InsufficientBuyingPower = 10,
    DailyLossLimitExceeded = 11,
    DrawdownLimitExceeded = 12,
    RateLimitExceeded = 13,
    InvalidMarketPrice = 14,
    ReplacementQuantityBelowFilled = 15,
}

public sealed record RiskDecision(
    bool IsAllowed,
    RiskDecisionCode Code,
    string Reason,
    decimal ProjectedNetQuantity,
    decimal ProjectedGrossNotional)
{
    public static RiskDecision Allow(
        RiskDecisionCode code,
        string reason,
        decimal projectedNetQuantity,
        decimal projectedGrossNotional) =>
        new(true, code, reason, projectedNetQuantity, projectedGrossNotional);

    public static RiskDecision Deny(
        RiskDecisionCode code,
        string reason,
        decimal projectedNetQuantity,
        decimal projectedGrossNotional) =>
        new(false, code, reason, projectedNetQuantity, projectedGrossNotional);
}

public sealed record RiskLimits
{
    public RiskLimits(
        decimal maximumOrderQuantity,
        decimal maximumAbsolutePosition,
        decimal maximumGrossNotional,
        decimal minimumBuyingPower,
        decimal maximumDailyLoss,
        decimal maximumDrawdown,
        int maximumExposureCommandsPerWindow,
        TimeSpan rateLimitWindow)
    {
        if (maximumOrderQuantity <= 0m) throw new ArgumentOutOfRangeException(nameof(maximumOrderQuantity));
        if (maximumAbsolutePosition <= 0m) throw new ArgumentOutOfRangeException(nameof(maximumAbsolutePosition));
        if (maximumGrossNotional <= 0m) throw new ArgumentOutOfRangeException(nameof(maximumGrossNotional));
        if (minimumBuyingPower < 0m) throw new ArgumentOutOfRangeException(nameof(minimumBuyingPower));
        if (maximumDailyLoss <= 0m) throw new ArgumentOutOfRangeException(nameof(maximumDailyLoss));
        if (maximumDrawdown <= 0m) throw new ArgumentOutOfRangeException(nameof(maximumDrawdown));
        if (maximumExposureCommandsPerWindow <= 0) throw new ArgumentOutOfRangeException(nameof(maximumExposureCommandsPerWindow));
        if (rateLimitWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(rateLimitWindow));
        MaximumOrderQuantity = maximumOrderQuantity;
        MaximumAbsolutePosition = maximumAbsolutePosition;
        MaximumGrossNotional = maximumGrossNotional;
        MinimumBuyingPower = minimumBuyingPower;
        MaximumDailyLoss = maximumDailyLoss;
        MaximumDrawdown = maximumDrawdown;
        MaximumExposureCommandsPerWindow = maximumExposureCommandsPerWindow;
        RateLimitWindow = rateLimitWindow;
    }

    public decimal MaximumOrderQuantity { get; }
    public decimal MaximumAbsolutePosition { get; }
    public decimal MaximumGrossNotional { get; }
    public decimal MinimumBuyingPower { get; }
    public decimal MaximumDailyLoss { get; }
    public decimal MaximumDrawdown { get; }
    public int MaximumExposureCommandsPerWindow { get; }
    public TimeSpan RateLimitWindow { get; }
}

public sealed record RiskEvaluationContext
{
    public RiskEvaluationContext(
        RiskLimits limits,
        RiskControlMode controlMode,
        bool killSwitchActive,
        decimal currentPositionQuantity,
        decimal currentBuyReservedQuantity,
        decimal currentSellReservedQuantity,
        decimal currentGrossReservedNotional,
        decimal existingOrderSignedReservation,
        decimal existingOrderGrossReservation,
        decimal existingOrderFilledQuantity,
        decimal availableBuyingPower,
        decimal dailyNetRealizedPnl,
        decimal currentEquity,
        decimal peakEquity,
        decimal marketPrice,
        int exposureCommandsInWindow,
        DateTimeOffset evaluatedAtUtc,
        decimal contractMultiplier = 1m,
        string accountCurrency = "USD")
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (!Enum.IsDefined(controlMode)) throw new ArgumentOutOfRangeException(nameof(controlMode));
        if (currentBuyReservedQuantity < 0m) throw new ArgumentOutOfRangeException(nameof(currentBuyReservedQuantity));
        if (currentSellReservedQuantity < 0m) throw new ArgumentOutOfRangeException(nameof(currentSellReservedQuantity));
        if (currentGrossReservedNotional < 0m) throw new ArgumentOutOfRangeException(nameof(currentGrossReservedNotional));
        if (existingOrderGrossReservation < 0m) throw new ArgumentOutOfRangeException(nameof(existingOrderGrossReservation));
        if (existingOrderFilledQuantity < 0m) throw new ArgumentOutOfRangeException(nameof(existingOrderFilledQuantity));
        if (contractMultiplier <= 0m) throw new ArgumentOutOfRangeException(nameof(contractMultiplier));
        if (availableBuyingPower < 0m) throw new ArgumentOutOfRangeException(nameof(availableBuyingPower));
        if (peakEquity < currentEquity) throw new ArgumentOutOfRangeException(nameof(peakEquity), "Peak equity cannot be less than current equity.");
        if (exposureCommandsInWindow < 0) throw new ArgumentOutOfRangeException(nameof(exposureCommandsInWindow));
        Limits = limits;
        ControlMode = controlMode;
        KillSwitchActive = killSwitchActive;
        CurrentPositionQuantity = currentPositionQuantity;
        CurrentBuyReservedQuantity = currentBuyReservedQuantity;
        CurrentSellReservedQuantity = currentSellReservedQuantity;
        CurrentGrossReservedNotional = currentGrossReservedNotional;
        ExistingOrderSignedReservation = existingOrderSignedReservation;
        ExistingOrderGrossReservation = existingOrderGrossReservation;
        ExistingOrderFilledQuantity = existingOrderFilledQuantity;
        AvailableBuyingPower = availableBuyingPower;
        DailyNetRealizedPnl = dailyNetRealizedPnl;
        CurrentEquity = currentEquity;
        PeakEquity = peakEquity;
        MarketPrice = marketPrice;
        ExposureCommandsInWindow = exposureCommandsInWindow;
        EvaluatedAtUtc = ExecutionValidation.RequireUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));
        TradingDayStartedAtUtc = new DateTimeOffset(
            EvaluatedAtUtc.Year,
            EvaluatedAtUtc.Month,
            EvaluatedAtUtc.Day,
            0,
            0,
            0,
            TimeSpan.Zero);
        ContractMultiplier = contractMultiplier;
        AccountCurrency = ExecutionValidation.RequireText(accountCurrency.ToUpperInvariant(), nameof(accountCurrency), 16);
    }

    public RiskLimits Limits { get; }
    public RiskControlMode ControlMode { get; }
    public bool KillSwitchActive { get; }
    public decimal CurrentPositionQuantity { get; }
    public decimal CurrentBuyReservedQuantity { get; }
    public decimal CurrentSellReservedQuantity { get; }
    public decimal CurrentNetReservedQuantity => CurrentBuyReservedQuantity - CurrentSellReservedQuantity;
    public decimal CurrentGrossReservedNotional { get; }
    public decimal ExistingOrderSignedReservation { get; }
    public decimal ExistingOrderGrossReservation { get; }
    public decimal ExistingOrderFilledQuantity { get; }
    public decimal AvailableBuyingPower { get; }
    public decimal DailyNetRealizedPnl { get; }
    public decimal CurrentEquity { get; }
    public decimal PeakEquity { get; }
    public decimal MarketPrice { get; }
    public int ExposureCommandsInWindow { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
    public DateTimeOffset TradingDayStartedAtUtc { get; }
    public decimal ContractMultiplier { get; }
    public string AccountCurrency { get; }
}

public sealed record RiskPolicyEvidence
{
    public RiskPolicyEvidence(string policyVersion, string limitsHashSha256, RiskEvaluationContext context)
    {
        PolicyVersion = ExecutionValidation.RequireText(policyVersion, nameof(policyVersion), 128);
        LimitsHashSha256 = ExecutionValidation.RequireSha256(limitsHashSha256, nameof(limitsHashSha256));
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(ExecutionCanonicalJson.Hash(context.Limits), LimitsHashSha256, StringComparison.Ordinal))
            throw new ArgumentException("Risk limits hash does not match the captured evaluation context.", nameof(limitsHashSha256));
        Context = context;
    }

    public string PolicyVersion { get; }
    public string LimitsHashSha256 { get; }
    public RiskEvaluationContext Context { get; }

    public static RiskPolicyEvidence Capture(RiskEvaluationContext context) =>
        new(RiskPolicy.PolicyVersion, ExecutionCanonicalJson.Hash(context.Limits), context);
}

/// <summary>Stateless policy; callers persist a RiskObservation before applying the decision.</summary>
public static class RiskPolicy
{
    public const string PolicyVersion = "daxalgo-risk-policy-v2";

    public static RiskDecision Evaluate(ExecutionCommand command, RiskEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var positionProjectionInvalid = false;
        var currentNetReserved = SaturatingSubtract(
            context.CurrentBuyReservedQuantity,
            context.CurrentSellReservedQuantity,
            ref positionProjectionInvalid);
        var currentProjected = SaturatingAdd(
            context.CurrentPositionQuantity,
            currentNetReserved,
            ref positionProjectionInvalid);
        var currentGross = context.CurrentGrossReservedNotional;
        if (command is CancelOrderCommand or QueryOrderCommand)
            return RiskDecision.Allow(RiskDecisionCode.CancelOrQueryAlwaysAllowed, "Cancel and query remain admitted during recovery.", currentProjected, currentGross);

        var terms = command switch
        {
            SubmitOrderCommand submit => submit.Terms,
            ReplaceOrderCommand replace => replace.ReplacementTerms,
            _ => throw new NotSupportedException($"Unsupported risk command {command.GetType().Name}.")
        };
        var price = terms.LimitPrice ?? terms.StopPrice ?? context.MarketPrice;
        if (price <= 0m)
            return RiskDecision.Deny(RiskDecisionCode.InvalidMarketPrice, "A positive reservation price is required.", currentProjected, currentGross);

        var reservationQuantity = command is ReplaceOrderCommand
            ? terms.Quantity - context.ExistingOrderFilledQuantity
            : terms.Quantity;
        if (reservationQuantity < 0m)
        {
            return RiskDecision.Deny(
                RiskDecisionCode.ReplacementQuantityBelowFilled,
                "Replacement total quantity cannot be less than accepted fills.",
                currentProjected,
                currentGross);
        }
        var existingBuy = Math.Max(0m, context.ExistingOrderSignedReservation);
        var existingSell = context.ExistingOrderSignedReservation < 0m
            ? SaturatingNegate(context.ExistingOrderSignedReservation, ref positionProjectionInvalid)
            : 0m;
        var projectedBuy = SaturatingSubtract(
            context.CurrentBuyReservedQuantity,
            existingBuy,
            ref positionProjectionInvalid);
        projectedBuy = SaturatingAdd(
            projectedBuy,
            terms.Side == OrderSide.Buy ? reservationQuantity : 0m,
            ref positionProjectionInvalid);
        var projectedSell = SaturatingSubtract(
            context.CurrentSellReservedQuantity,
            existingSell,
            ref positionProjectionInvalid);
        projectedSell = SaturatingAdd(
            projectedSell,
            terms.Side == OrderSide.Sell ? reservationQuantity : 0m,
            ref positionProjectionInvalid);
        var projectedNet = SaturatingAdd(
            context.CurrentPositionQuantity,
            projectedBuy,
            ref positionProjectionInvalid);
        projectedNet = SaturatingSubtract(projectedNet, projectedSell, ref positionProjectionInvalid);
        var worstCaseLong = SaturatingAdd(
            context.CurrentPositionQuantity,
            projectedBuy,
            ref positionProjectionInvalid);
        var worstCaseShort = SaturatingSubtract(
            context.CurrentPositionQuantity,
            projectedSell,
            ref positionProjectionInvalid);
        var worstCaseAbsolutePosition = Math.Max(
            SaturatingAbs(worstCaseLong, ref positionProjectionInvalid),
            SaturatingAbs(worstCaseShort, ref positionProjectionInvalid));
        positionProjectionInvalid |= projectedBuy < 0m || projectedSell < 0m;

        var grossProjectionInvalid = false;
        decimal orderNotional;
        decimal projectedGross;
        try
        {
            orderNotional = checked(checked(reservationQuantity * price) * context.ContractMultiplier);
            projectedGross = checked(
                context.CurrentGrossReservedNotional -
                context.ExistingOrderGrossReservation +
                orderNotional);
        }
        catch (OverflowException)
        {
            // The policy must return a stable denial, never let valid numeric input escape as an
            // arithmetic exception after an execution intent has been consumed.
            grossProjectionInvalid = true;
            projectedGross = decimal.MaxValue;
        }
        grossProjectionInvalid |= projectedGross < 0m;
        var isNonCrossingReduceOnly = !positionProjectionInvalid &&
            terms.ReduceOnly && IsNonCrossingReduction(
            context.CurrentPositionQuantity,
            terms.Side,
            projectedBuy,
            projectedSell);

        if (command.Metadata.ExpiresAtUtc is { } expiresAt && expiresAt <= context.EvaluatedAtUtc)
            return RiskDecision.Deny(RiskDecisionCode.ExpiredCommand, "Command has expired.", projectedNet, projectedGross);
        if (context.KillSwitchActive)
            return RiskDecision.Deny(RiskDecisionCode.KillSwitchActive, "Kill switch blocks new exposure.", projectedNet, projectedGross);
        if (context.ControlMode == RiskControlMode.Halted)
            return RiskDecision.Deny(RiskDecisionCode.NewExposureHalted, "Risk control is halted.", projectedNet, projectedGross);
        if (context.ControlMode == RiskControlMode.Reducing && !terms.ReduceOnly)
            return RiskDecision.Deny(RiskDecisionCode.NewExposureHalted, "Reducing mode admits only explicitly reduce-only exposure commands.", projectedNet, projectedGross);
        if (positionProjectionInvalid)
            return RiskDecision.Deny(RiskDecisionCode.MaximumPositionExceeded, "Position projection exceeded the supported decimal range.", projectedNet, projectedGross);
        if ((context.ControlMode == RiskControlMode.Reducing || terms.ReduceOnly) &&
            !isNonCrossingReduceOnly)
            return RiskDecision.Deny(RiskDecisionCode.ReduceOnlyWouldIncreaseExposure, "Reduce-only reservations must reduce without crossing through flat.", projectedNet, projectedGross);
        if (terms.Quantity > context.Limits.MaximumOrderQuantity)
            return RiskDecision.Deny(RiskDecisionCode.MaximumOrderQuantityExceeded, "Order quantity exceeds the configured maximum.", projectedNet, projectedGross);
        if (!isNonCrossingReduceOnly &&
            worstCaseAbsolutePosition > context.Limits.MaximumAbsolutePosition)
            return RiskDecision.Deny(RiskDecisionCode.MaximumPositionExceeded, "Worst-case directional fills exceed the configured position maximum.", projectedNet, projectedGross);
        if (grossProjectionInvalid)
            return RiskDecision.Deny(RiskDecisionCode.MaximumGrossNotionalExceeded, "Gross-notional projection exceeded the supported decimal range.", projectedNet, projectedGross);
        if (!isNonCrossingReduceOnly &&
            projectedGross > context.Limits.MaximumGrossNotional)
            return RiskDecision.Deny(RiskDecisionCode.MaximumGrossNotionalExceeded, "Projected working-order notional exceeds the configured maximum.", projectedNet, projectedGross);
        var incrementalGrossInvalid = false;
        var incrementalGrossNotional = Math.Max(
            0m,
            SaturatingSubtract(projectedGross, currentGross, ref incrementalGrossInvalid));
        if (incrementalGrossInvalid)
            return RiskDecision.Deny(RiskDecisionCode.MaximumGrossNotionalExceeded, "Incremental gross-notional projection exceeded the supported decimal range.", projectedNet, projectedGross);
        if (!isNonCrossingReduceOnly &&
            incrementalGrossNotional > 0m &&
            incrementalGrossNotional > Math.Max(0m, context.AvailableBuyingPower - context.Limits.MinimumBuyingPower))
            return RiskDecision.Deny(RiskDecisionCode.InsufficientBuyingPower, "Order would consume protected buying power.", projectedNet, projectedGross);
        if (!isNonCrossingReduceOnly &&
            context.DailyNetRealizedPnl <= -context.Limits.MaximumDailyLoss)
            return RiskDecision.Deny(RiskDecisionCode.DailyLossLimitExceeded, "Daily net realized-loss limit is active.", projectedNet, projectedGross);
        var drawdownInvalid = false;
        var drawdown = SaturatingSubtract(context.PeakEquity, context.CurrentEquity, ref drawdownInvalid);
        if (drawdownInvalid ||
            (!isNonCrossingReduceOnly && drawdown >= context.Limits.MaximumDrawdown))
            return RiskDecision.Deny(RiskDecisionCode.DrawdownLimitExceeded, "Drawdown limit is active.", projectedNet, projectedGross);
        if (context.ExposureCommandsInWindow >= context.Limits.MaximumExposureCommandsPerWindow)
            return RiskDecision.Deny(RiskDecisionCode.RateLimitExceeded, "Exposure command rate limit is active.", projectedNet, projectedGross);

        return RiskDecision.Allow(RiskDecisionCode.Allowed, "Risk checks passed.", projectedNet, projectedGross);
    }

    private static bool IsNonCrossingReduction(
        decimal currentPosition,
        OrderSide side,
        decimal projectedBuy,
        decimal projectedSell) =>
        currentPosition switch
        {
            > 0m => side == OrderSide.Sell && projectedSell <= currentPosition,
            < 0m => side == OrderSide.Buy && projectedBuy <= Math.Abs(currentPosition),
            _ => false,
        };

    private static decimal SaturatingAdd(decimal left, decimal right, ref bool invalid)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            invalid = true;
            return left >= 0m ? decimal.MaxValue : decimal.MinValue;
        }
    }

    private static decimal SaturatingSubtract(decimal left, decimal right, ref bool invalid)
    {
        try
        {
            return checked(left - right);
        }
        catch (OverflowException)
        {
            invalid = true;
            return left >= 0m ? decimal.MaxValue : decimal.MinValue;
        }
    }

    private static decimal SaturatingNegate(decimal value, ref bool invalid)
    {
        try
        {
            return checked(-value);
        }
        catch (OverflowException)
        {
            invalid = true;
            return value < 0m ? decimal.MaxValue : decimal.MinValue;
        }
    }

    private static decimal SaturatingAbs(decimal value, ref bool invalid) =>
        value < 0m ? SaturatingNegate(value, ref invalid) : value;
}
