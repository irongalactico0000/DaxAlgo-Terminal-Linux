using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Strategies.Specification;

/// <summary>The primary job the strategy is intended to perform.</summary>
public enum StrategyObjectiveKind
{
    ReturnSeeking,
    Hedging,
    Allocation,
    BenchmarkTracking,
    LiquidityProvision,
    Execution,
}

/// <summary>The economic mechanism asserted to produce the desired return or protection.</summary>
public enum ReturnHypothesisKind
{
    None,
    MarketRiskPremium,
    Momentum,
    Reversal,
    Value,
    Carry,
    Quality,
    Defensive,
    Seasonality,
    Convergence,
    CatalystInformation,
    StructuralFlow,
    LiquidityProvision,
    VolatilityInsurance,
}

/// <summary>What causes strategy logic to be evaluated.</summary>
public enum StrategyTriggerKind
{
    Quote,
    Trade,
    Bar,
    Depth,
    Schedule,
    StructuredExternalEvent,
    NewsEvent,
    OrderEvent,
    ContractLifecycle,
}

/// <summary>Expected economic holding horizon, independent of input-data frequency.</summary>
public enum StrategyHorizonKind
{
    Intraday,
    MultiDay,
    MediumTerm,
    LongTerm,
    Mixed,
}

/// <summary>The relationship among instruments consumed and traded together.</summary>
public enum MarketTopologyKind
{
    SingleInstrument,
    CrossSection,
    Pair,
    Basket,
    CrossAsset,
    MultiVenue,
    UnderlyingAndDerivative,
    MultiLeg,
}

/// <summary>The intended shape of portfolio exposure.</summary>
public enum ExposureGeometryKind
{
    LongOnly,
    DirectionalLongShort,
    CrossSectionalLongShort,
    MarketNeutral,
    Spread,
    Arbitrage,
    DeltaNeutral,
    VolatilityExposure,
}

/// <summary>Point-in-time information the strategy consumes.</summary>
public enum StrategyInformationKind
{
    Quote,
    Trade,
    Bar,
    Depth,
    Fundamental,
    Macro,
    CorporateEvent,
    NewsText,
    Alternative,
    ImpliedVolatilitySurface,
}

/// <summary>How observations become a signal, forecast, rank, or policy action.</summary>
public enum SignalModelKind
{
    DeterministicRule,
    Ranking,
    Statistical,
    Econometric,
    Optimization,
    SupervisedMachineLearning,
    OnlineLearning,
    ReinforcementLearning,
    Ensemble,
}

/// <summary>How signals become target exposure.</summary>
public enum PortfolioConstructionKind
{
    FixedQuantity,
    EqualWeight,
    TopK,
    VolatilityTarget,
    RiskBudget,
    Optimized,
    ExposureNeutral,
    InventoryTarget,
}

/// <summary>How target exposure becomes orders.</summary>
public enum StrategyExecutionPolicyKind
{
    Market,
    Limit,
    Stop,
    Passive,
    Aggressive,
    Twap,
    Vwap,
    Pov,
    SmartRouting,
    CoordinatedLegs,
    ContinuousQuoting,
}

/// <summary>History that changes subsequent decisions.</summary>
public enum StrategyStateKind
{
    Stateless,
    PositionAware,
    EventLifecycle,
    InventoryAware,
    Cooldown,
    FiniteState,
    RegimeAware,
}

/// <summary>Rules that constrain or close exposure.</summary>
public enum StrategyRiskExitKind
{
    SignalReversal,
    StopLoss,
    TakeProfit,
    TrailingStop,
    TimeExit,
    EventResolution,
    ExposureCap,
    GreekCap,
    LiquidityCap,
    DrawdownKillSwitch,
}

/// <summary>How parameters or policy state may change after initial selection.</summary>
public enum StrategyAdaptationKind
{
    Fixed,
    OfflineSelected,
    PeriodicRecalibration,
    RollingRefit,
    ScheduledRetraining,
    OnlineLearning,
    ReinforcementLearning,
}

/// <summary>
/// Separates data frequency, decision cadence, and holding period. Treating all three as one
/// "timeframe" is insufficient for cross-sectional, event, and execution strategies.
/// </summary>
public sealed record StrategyTimeSemantics(
    StrategyHorizonKind Horizon,
    TimeSpan? DecisionCadence = null,
    TimeSpan? ExpectedHoldingPeriod = null);

/// <summary>Tradable universe, information inputs, and temporal scope.</summary>
public sealed record StrategyContextSpec(
    IReadOnlyList<AssetClass> AssetClasses,
    MarketTopologyKind Topology,
    ExposureGeometryKind Exposure,
    IReadOnlyList<StrategyInformationKind> Information,
    StrategyTimeSemantics Time);

/// <summary>Return thesis, activation policy, and signal-estimation method.</summary>
public sealed record StrategySignalSpec(
    IReadOnlyList<ReturnHypothesisKind> Hypotheses,
    IReadOnlyList<StrategyTriggerKind> Triggers,
    IReadOnlyList<SignalModelKind> Models);

/// <summary>Transforms strategy intent into target exposure.</summary>
public sealed record StrategyPortfolioSpec(PortfolioConstructionKind Construction);

/// <summary>Constrains and closes target exposure.</summary>
public sealed record StrategyRiskSpec(IReadOnlyList<StrategyRiskExitKind> Rules);

/// <summary>Transforms target exposure into executable order intent.</summary>
public sealed record StrategyExecutionSpec(IReadOnlyList<StrategyExecutionPolicyKind> Policies);

/// <summary>Path-dependent strategy behavior and permitted adaptation.</summary>
public sealed record StrategyStateSpec(
    IReadOnlyList<StrategyStateKind> Policies,
    StrategyAdaptationKind Adaptation);

/// <summary>
/// A strategy's normalized, implementation-independent classification and operational envelope.
/// Named strategy families are templates over these separable classification dimensions rather than mutually
/// exclusive strategy types. This record intentionally does not encode executable expressions,
/// feature/parameter bindings, universe selection, sizing formulas, or order rules; a future
/// executable-definition layer must reference this classification rather than treating it as a DSL.
/// </summary>
public sealed record StrategySpec(
    string Id,
    string Name,
    StrategyObjectiveKind Objective,
    StrategyContextSpec Context,
    StrategySignalSpec Signal,
    StrategyPortfolioSpec Portfolio,
    StrategyRiskSpec Risk,
    StrategyExecutionSpec Execution,
    StrategyStateSpec State,
    IReadOnlyList<StrategyCapabilityRequirement> AdditionalRequirements)
{
    /// <summary>
    /// Host capabilities implied by the normalized axes, plus any semantics the author declared
    /// explicitly. Callers cannot accidentally omit basic requirements such as depth data or
    /// multi-instrument support when those needs are already present in the specification.
    /// </summary>
    public IReadOnlyList<StrategyCapabilityRequirement> Requirements =>
        StrategyCapabilityInference.Infer(this);
}

/// <summary>A structural problem that prevents a strategy specification from being authoritative.</summary>
public sealed record StrategySpecIssue(string Code, string Path, string Message);

/// <summary>Pure validation suitable for builder forms, imports, and registration gates.</summary>
public static class StrategySpecValidator
{
    public static IReadOnlyList<StrategySpecIssue> Validate(StrategySpec? spec)
    {
        if (spec is null)
            return [new StrategySpecIssue("spec.required", "$", "Strategy specification is required.")];

        var issues = new List<StrategySpecIssue>();
        RequiredText(spec.Id, "id", "spec.id.required", issues);
        RequiredText(spec.Name, "name", "spec.name.required", issues);

        RequiredList(spec.Context?.AssetClasses, "context.asset_classes", "spec.asset_classes.required", issues);
        RequiredList(spec.Context?.Information, "context.information", "spec.information.required", issues);
        RequiredList(spec.Signal?.Hypotheses, "signal.hypotheses", "spec.hypothesis.required", issues);
        RequiredList(spec.Signal?.Triggers, "signal.triggers", "spec.trigger.required", issues);
        RequiredList(spec.Signal?.Models, "signal.models", "spec.signal_model.required", issues);
        RequiredList(spec.Execution?.Policies, "execution.policies", "spec.execution.required", issues);
        RequiredList(spec.State?.Policies, "state.policies", "spec.state.required", issues);
        RequiredList(spec.Risk?.Rules, "risk.rules", "spec.risk_rules.required", issues);

        ValidEnum(spec.Objective, "objective", issues);
        ValidEnumList(spec.Context?.AssetClasses, "context.asset_classes", issues);
        ValidEnumList(spec.Context?.Information, "context.information", issues);
        ValidEnumList(spec.Signal?.Hypotheses, "signal.hypotheses", issues);
        ValidEnumList(spec.Signal?.Triggers, "signal.triggers", issues);
        ValidEnumList(spec.Signal?.Models, "signal.models", issues);
        ValidEnumList(spec.Execution?.Policies, "execution.policies", issues);
        ValidEnumList(spec.State?.Policies, "state.policies", issues);
        ValidEnumList(spec.Risk?.Rules, "risk.rules", issues);

        if (spec.Context is { } context)
        {
            ValidEnum(context.Topology, "context.topology", issues);
            ValidEnum(context.Exposure, "context.exposure", issues);
            if (context.AssetClasses?.Contains(AssetClass.Unknown) == true)
            {
                issues.Add(new StrategySpecIssue(
                    "spec.asset_class.unknown",
                    "context.asset_classes",
                    "Unknown is not an authoritative asset class."));
            }
        }

        if (spec.Context?.Time is null)
            issues.Add(new StrategySpecIssue("spec.time.required", "context.time", "Time semantics are required."));
        else
        {
            ValidEnum(spec.Context.Time.Horizon, "context.time.horizon", issues);
            PositiveDuration(spec.Context.Time.DecisionCadence, "context.time.decision_cadence", issues);
            PositiveDuration(spec.Context.Time.ExpectedHoldingPeriod, "context.time.expected_holding_period", issues);
        }

        if (spec.Portfolio is null)
            issues.Add(new StrategySpecIssue("spec.portfolio.required", "portfolio", "Portfolio construction is required."));
        else
            ValidEnum(spec.Portfolio.Construction, "portfolio.construction", issues);
        if (spec.Risk is null)
            issues.Add(new StrategySpecIssue("spec.risk.required", "risk", "Risk and exit policy is required."));
        if (spec.State is not null)
        {
            ValidEnum(spec.State.Adaptation, "state.adaptation", issues);
            if (spec.State.Policies?.Contains(StrategyStateKind.Stateless) == true &&
                spec.State.Policies.Count > 1)
            {
                issues.Add(new StrategySpecIssue(
                    "spec.state.stateless_conflict",
                    "state.policies",
                    "Stateless cannot be combined with stateful policies."));
            }
        }

        var requirements = (spec.AdditionalRequirements ?? [])
            .Where(static requirement => requirement is not null)
            .ToArray();
        if (requirements.Length != (spec.AdditionalRequirements?.Count ?? 0))
        {
            issues.Add(new StrategySpecIssue(
                "spec.requirement.required",
                "requirements",
                "Capability requirements must not contain null entries."));
        }

        var duplicateRequirement = requirements
            .GroupBy(x => x.Capability)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicateRequirement is not null)
        {
            issues.Add(new StrategySpecIssue(
                "spec.requirement.duplicate",
                "requirements",
                $"Capability '{duplicateRequirement.Key}' is required more than once."));
        }

        foreach (var requirement in requirements)
        {
            ValidEnum(requirement.Capability, "requirements.capability", issues);
            if (string.IsNullOrWhiteSpace(requirement.Reason))
            {
                issues.Add(new StrategySpecIssue(
                    "spec.requirement.reason.required",
                    "requirements.reason",
                    $"Capability '{requirement.Capability}' requires a reason."));
            }
        }

        return issues;
    }

    private static void RequiredText(
        string? value,
        string path,
        string code,
        ICollection<StrategySpecIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(new StrategySpecIssue(code, path, $"{path} is required."));
    }

    private static void RequiredList<T>(
        IReadOnlyList<T>? values,
        string path,
        string code,
        ICollection<StrategySpecIssue> issues)
    {
        if (values is null || values.Count == 0)
            issues.Add(new StrategySpecIssue(code, path, $"{path} must contain at least one value."));
    }

    private static void ValidEnum<T>(T value, string path, ICollection<StrategySpecIssue> issues)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            issues.Add(new StrategySpecIssue(
                "spec.enum.invalid",
                path,
                $"{path} contains undefined {typeof(T).Name} value '{value}'."));
        }
    }

    private static void ValidEnumList<T>(
        IReadOnlyList<T>? values,
        string path,
        ICollection<StrategySpecIssue> issues)
        where T : struct, Enum
    {
        if (values is null)
            return;

        foreach (var value in values)
            ValidEnum(value, path, issues);

        if (values.GroupBy(x => x).Any(group => group.Count() > 1))
        {
            issues.Add(new StrategySpecIssue(
                "spec.enum.duplicate",
                path,
                $"{path} must not contain duplicate values."));
        }
    }

    private static void PositiveDuration(
        TimeSpan? value,
        string path,
        ICollection<StrategySpecIssue> issues)
    {
        if (value is { } duration && duration <= TimeSpan.Zero)
        {
            issues.Add(new StrategySpecIssue(
                "spec.duration.positive",
                path,
                $"{path} must be positive when specified."));
        }
    }
}
