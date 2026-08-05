namespace TradingTerminal.Core.Strategies.Specification;

/// <summary>
/// Atomic runtime, data, and execution semantics that a strategy may require. These capabilities
/// are intentionally separate from the strategy taxonomy: momentum is a hypothesis, while
/// replaying a depth event or coordinating two legs is an engine capability.
/// </summary>
public enum StrategyRuntimeCapability
{
    DeterministicReplay,
    QuoteEvents,
    TradeEvents,
    FinalizedBarEvents,
    DepthEvents,
    OrderLifecycleEvents,
    ScheduledCallbacks,
    StructuredExternalEvents,
    NewsEvents,
    ContractLifecycleEvents,
    FundamentalData,
    MacroData,
    CorporateEventData,
    AlternativeData,
    ImpliedVolatilitySurface,
    SingleInstrument,
    MultiInstrument,
    AtomicMultiLegOrders,
    MultiVenueRouting,
    OptionsLifecycle,
    Greeks,
    MarketOrders,
    LimitOrders,
    StopOrders,
    PartialFills,
    QueuePosition,
    LatencyModel,
    ContinuousQuoting,
    ManagedModelArtifacts,
    OnlineLearning,
}

/// <summary>One runtime semantic required for faithful execution, with an audit-friendly reason.</summary>
public sealed record StrategyCapabilityRequirement(
    StrategyRuntimeCapability Capability,
    string Reason);

/// <summary>A named capability envelope for an engine, worker, builder, or deployment target.</summary>
public sealed record StrategyCapabilityProfile(
    string Id,
    IReadOnlyList<StrategyRuntimeCapability> Supported)
{
    /// <summary>Returns the exact strategy requirements this profile cannot satisfy.</summary>
    public StrategyCapabilityAssessment Assess(StrategySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var issues = StrategySpecValidator.Validate(spec);
        if (issues.Count > 0)
            return new StrategyCapabilityAssessment(Id, spec.Id, [], issues);

        var supported = new HashSet<StrategyRuntimeCapability>(Supported ?? []);
        var missing = (spec.Requirements ?? [])
            .Where(requirement => !supported.Contains(requirement.Capability))
            .DistinctBy(requirement => requirement.Capability)
            .ToArray();

        return new StrategyCapabilityAssessment(Id, spec.Id, missing, []);
    }
}

/// <summary>Derives host semantics from a strategy's classification dimensions.</summary>
public static class StrategyCapabilityInference
{
    public static IReadOnlyList<StrategyCapabilityRequirement> Infer(StrategySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var requirements = new Dictionary<StrategyRuntimeCapability, StrategyCapabilityRequirement>();

        void Add(StrategyRuntimeCapability capability, string reason) =>
            requirements.TryAdd(capability, new StrategyCapabilityRequirement(capability, reason));

        foreach (var information in spec.Context?.Information ?? [])
        {
            switch (information)
            {
                case StrategyInformationKind.Quote:
                    Add(StrategyRuntimeCapability.QuoteEvents, "The strategy consumes quote data.");
                    break;
                case StrategyInformationKind.Trade:
                    Add(StrategyRuntimeCapability.TradeEvents, "The strategy consumes trade prints.");
                    break;
                case StrategyInformationKind.Bar:
                    Add(StrategyRuntimeCapability.FinalizedBarEvents, "The strategy consumes finalized bars at their decision timestamp.");
                    break;
                case StrategyInformationKind.Depth:
                    Add(StrategyRuntimeCapability.DepthEvents, "The strategy consumes order-book depth.");
                    break;
                case StrategyInformationKind.Fundamental:
                    Add(StrategyRuntimeCapability.FundamentalData, "The strategy consumes point-in-time fundamentals.");
                    break;
                case StrategyInformationKind.Macro:
                    Add(StrategyRuntimeCapability.MacroData, "The strategy consumes macroeconomic data.");
                    break;
                case StrategyInformationKind.CorporateEvent:
                    Add(StrategyRuntimeCapability.CorporateEventData, "The strategy consumes corporate events.");
                    break;
                case StrategyInformationKind.NewsText:
                    Add(StrategyRuntimeCapability.NewsEvents, "The strategy consumes timestamped news or text.");
                    break;
                case StrategyInformationKind.Alternative:
                    Add(StrategyRuntimeCapability.AlternativeData, "The strategy consumes alternative data.");
                    break;
                case StrategyInformationKind.ImpliedVolatilitySurface:
                    Add(StrategyRuntimeCapability.ImpliedVolatilitySurface, "The strategy consumes an implied-volatility surface.");
                    Add(StrategyRuntimeCapability.OptionsLifecycle, "Volatility-surface strategies require option lifecycle semantics.");
                    Add(StrategyRuntimeCapability.Greeks, "Volatility-surface strategies require Greek calculations.");
                    break;
            }
        }

        if (spec.Context is { } context)
        {
            if (context.Topology == MarketTopologyKind.SingleInstrument)
                Add(StrategyRuntimeCapability.SingleInstrument, "The strategy trades one instrument.");
            else
                Add(StrategyRuntimeCapability.MultiInstrument, $"The strategy uses {context.Topology} topology.");

            if ((context.AssetClasses ?? []).Contains(TradingTerminal.Core.Domain.AssetClass.Option))
                Add(StrategyRuntimeCapability.OptionsLifecycle, "Option strategies require option contract lifecycle semantics.");

            if (context.Exposure is ExposureGeometryKind.DeltaNeutral or ExposureGeometryKind.VolatilityExposure)
                Add(StrategyRuntimeCapability.Greeks, $"{context.Exposure} exposure requires Greek calculations.");
        }

        foreach (var trigger in spec.Signal?.Triggers ?? [])
        {
            switch (trigger)
            {
                case StrategyTriggerKind.Quote:
                    Add(StrategyRuntimeCapability.QuoteEvents, "Strategy evaluation is quote-triggered.");
                    break;
                case StrategyTriggerKind.Trade:
                    Add(StrategyRuntimeCapability.TradeEvents, "Strategy evaluation is trade-triggered.");
                    break;
                case StrategyTriggerKind.Bar:
                    Add(StrategyRuntimeCapability.FinalizedBarEvents, "Strategy evaluation occurs when a bar is finalized.");
                    break;
                case StrategyTriggerKind.Depth:
                    Add(StrategyRuntimeCapability.DepthEvents, "Strategy evaluation is depth-triggered.");
                    break;
                case StrategyTriggerKind.OrderEvent:
                    Add(StrategyRuntimeCapability.OrderLifecycleEvents, "The strategy reacts to order acknowledgements, fills, cancellations, or rejections.");
                    break;
                case StrategyTriggerKind.Schedule:
                    Add(StrategyRuntimeCapability.ScheduledCallbacks, "Strategy evaluation is schedule-triggered.");
                    break;
                case StrategyTriggerKind.StructuredExternalEvent:
                    Add(StrategyRuntimeCapability.StructuredExternalEvents, "The strategy reacts to structured external events.");
                    break;
                case StrategyTriggerKind.NewsEvent:
                    Add(StrategyRuntimeCapability.NewsEvents, "The strategy reacts to news events.");
                    break;
                case StrategyTriggerKind.ContractLifecycle:
                    Add(StrategyRuntimeCapability.ContractLifecycleEvents, "The strategy reacts to contract lifecycle events.");
                    break;
            }
        }

        foreach (var policy in spec.Execution?.Policies ?? [])
        {
            switch (policy)
            {
                case StrategyExecutionPolicyKind.Market:
                case StrategyExecutionPolicyKind.Aggressive:
                    Add(StrategyRuntimeCapability.MarketOrders, "Execution requires marketable orders.");
                    break;
                case StrategyExecutionPolicyKind.Limit:
                case StrategyExecutionPolicyKind.Passive:
                    Add(StrategyRuntimeCapability.LimitOrders, "Execution requires limit orders.");
                    break;
                case StrategyExecutionPolicyKind.Stop:
                    Add(StrategyRuntimeCapability.StopOrders, "Execution requires stop orders.");
                    break;
                case StrategyExecutionPolicyKind.Twap:
                    Add(StrategyRuntimeCapability.ScheduledCallbacks, "TWAP execution requires deterministic time scheduling.");
                    break;
                case StrategyExecutionPolicyKind.Vwap:
                    Add(StrategyRuntimeCapability.ScheduledCallbacks, "VWAP execution requires deterministic time scheduling.");
                    Add(StrategyRuntimeCapability.TradeEvents, "VWAP execution requires point-in-time market volume observations.");
                    break;
                case StrategyExecutionPolicyKind.Pov:
                    Add(StrategyRuntimeCapability.TradeEvents, "POV execution requires point-in-time market volume observations.");
                    Add(StrategyRuntimeCapability.PartialFills, "Faithful POV replay requires partial-fill semantics.");
                    break;
                case StrategyExecutionPolicyKind.SmartRouting:
                    Add(StrategyRuntimeCapability.MultiVenueRouting, "Execution requires venue selection and routing.");
                    break;
                case StrategyExecutionPolicyKind.CoordinatedLegs:
                    Add(StrategyRuntimeCapability.AtomicMultiLegOrders, "Execution requires coordinated multi-leg orders.");
                    break;
                case StrategyExecutionPolicyKind.ContinuousQuoting:
                    Add(StrategyRuntimeCapability.LimitOrders, "Continuous quoting requires limit orders.");
                    Add(StrategyRuntimeCapability.ContinuousQuoting, "Execution continuously maintains quotes.");
                    Add(StrategyRuntimeCapability.QueuePosition, "Faithful quoting replay requires queue position.");
                    Add(StrategyRuntimeCapability.PartialFills, "Faithful quoting replay requires partial fills.");
                    break;
            }
        }

        foreach (var model in spec.Signal?.Models ?? [])
        {
            switch (model)
            {
                case SignalModelKind.SupervisedMachineLearning:
                    Add(StrategyRuntimeCapability.ManagedModelArtifacts, "The signal depends on a versioned model artifact.");
                    break;
                case SignalModelKind.OnlineLearning:
                case SignalModelKind.ReinforcementLearning:
                    Add(StrategyRuntimeCapability.ManagedModelArtifacts, "The policy depends on a versioned model artifact.");
                    Add(StrategyRuntimeCapability.OnlineLearning, "The model updates during operation.");
                    break;
            }
        }

        if (spec.Risk?.Rules?.Contains(StrategyRiskExitKind.GreekCap) == true)
            Add(StrategyRuntimeCapability.Greeks, "Greek-constrained risk requires Greek calculations.");
        if (spec.Risk?.Rules?.Contains(StrategyRiskExitKind.TimeExit) == true)
            Add(StrategyRuntimeCapability.ScheduledCallbacks, "A faithful time exit requires deterministic scheduling.");

        if (spec.State is { } state)
        {
            switch (state.Adaptation)
            {
                case StrategyAdaptationKind.PeriodicRecalibration:
                case StrategyAdaptationKind.RollingRefit:
                    Add(StrategyRuntimeCapability.ScheduledCallbacks, $"{state.Adaptation} requires deterministic scheduling.");
                    break;
                case StrategyAdaptationKind.ScheduledRetraining:
                    Add(StrategyRuntimeCapability.ScheduledCallbacks, "Scheduled retraining requires deterministic scheduling.");
                    Add(StrategyRuntimeCapability.ManagedModelArtifacts, "Scheduled retraining requires versioned model artifacts.");
                    break;
                case StrategyAdaptationKind.OnlineLearning:
                case StrategyAdaptationKind.ReinforcementLearning:
                    Add(StrategyRuntimeCapability.ManagedModelArtifacts, "Adaptive model state requires versioned model artifacts.");
                    Add(StrategyRuntimeCapability.OnlineLearning, $"{state.Adaptation} updates model state during operation.");
                    break;
            }
        }

        foreach (var additional in spec.AdditionalRequirements ?? [])
            if (additional is not null)
                requirements[additional.Capability] = additional;

        return requirements.Values.OrderBy(x => x.Capability).ToArray();
    }
}

/// <summary>The auditable result of checking one strategy against one capability envelope.</summary>
public sealed record StrategyCapabilityAssessment(
    string ProfileId,
    string StrategyId,
    IReadOnlyList<StrategyCapabilityRequirement> Missing,
    IReadOnlyList<StrategySpecIssue> Issues)
{
    public bool IsSupported => Issues.Count == 0 && Missing.Count == 0;
}
