using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Specification;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class StrategySpecTests
{
    [Fact]
    public void Orthogonal_axes_can_describe_event_driven_ml_momentum_together()
    {
        var spec = ValidSpec() with
        {
            Signal = new StrategySignalSpec(
                [ReturnHypothesisKind.Momentum, ReturnHypothesisKind.CatalystInformation],
                [StrategyTriggerKind.StructuredExternalEvent],
                [SignalModelKind.SupervisedMachineLearning]),
        };

        StrategySpecValidator.Validate(spec).Should().BeEmpty();
        spec.Signal.Hypotheses.Should().Contain(ReturnHypothesisKind.Momentum);
        spec.Signal.Triggers.Should().Contain(StrategyTriggerKind.StructuredExternalEvent);
        spec.Signal.Models.Should().Contain(SignalModelKind.SupervisedMachineLearning);
    }

    [Fact]
    public void Runtime_requirements_are_inferred_from_normalized_axes()
    {
        var spec = ValidSpec() with
        {
            Context = new StrategyContextSpec(
                [AssetClass.Option],
                MarketTopologyKind.MultiLeg,
                ExposureGeometryKind.VolatilityExposure,
                [StrategyInformationKind.ImpliedVolatilitySurface, StrategyInformationKind.CorporateEvent],
                new StrategyTimeSemantics(StrategyHorizonKind.MultiDay)),
            Signal = new StrategySignalSpec(
                [ReturnHypothesisKind.VolatilityInsurance],
                [StrategyTriggerKind.StructuredExternalEvent],
                [SignalModelKind.SupervisedMachineLearning]),
            Execution = new StrategyExecutionSpec([StrategyExecutionPolicyKind.CoordinatedLegs]),
        };

        spec.Requirements.Select(x => x.Capability).Should().Contain(
        [
            StrategyRuntimeCapability.ImpliedVolatilitySurface,
            StrategyRuntimeCapability.OptionsLifecycle,
            StrategyRuntimeCapability.Greeks,
            StrategyRuntimeCapability.CorporateEventData,
            StrategyRuntimeCapability.StructuredExternalEvents,
            StrategyRuntimeCapability.MultiInstrument,
            StrategyRuntimeCapability.AtomicMultiLegOrders,
            StrategyRuntimeCapability.ManagedModelArtifacts,
        ]);
    }

    [Fact]
    public void Capability_assessment_reports_only_missing_semantics_in_stable_order()
    {
        var spec = ValidSpec() with
        {
            Context = ValidSpec().Context with
            {
                Information = [StrategyInformationKind.Depth],
            },
            Signal = ValidSpec().Signal with
            {
                Triggers = [StrategyTriggerKind.Depth],
            },
            Execution = new StrategyExecutionSpec(
                [StrategyExecutionPolicyKind.Limit, StrategyExecutionPolicyKind.ContinuousQuoting]),
        };
        var profile = new StrategyCapabilityProfile(
            "l1-single-instrument",
            [
                StrategyRuntimeCapability.DeterministicReplay,
                StrategyRuntimeCapability.SingleInstrument,
                StrategyRuntimeCapability.LimitOrders,
            ]);

        var assessment = profile.Assess(spec);

        assessment.IsSupported.Should().BeFalse();
        assessment.Missing.Select(x => x.Capability).Should().Equal(
            StrategyRuntimeCapability.DepthEvents,
            StrategyRuntimeCapability.PartialFills,
            StrategyRuntimeCapability.QueuePosition,
            StrategyRuntimeCapability.ContinuousQuoting);
    }

    [Fact]
    public void Additional_requirements_can_make_strategy_specific_semantics_explicit()
    {
        var spec = ValidSpec() with
        {
            AdditionalRequirements =
            [
                new StrategyCapabilityRequirement(
                    StrategyRuntimeCapability.LatencyModel,
                    "The strategy reacts inside a declared maximum latency budget."),
            ],
        };

        spec.Requirements.Should().ContainSingle(x => x.Capability == StrategyRuntimeCapability.LatencyModel)
            .Which.Reason.Should().Contain("latency budget");
    }

    [Fact]
    public void Execution_and_adaptation_axes_infer_their_operational_semantics()
    {
        var spec = ValidSpec() with
        {
            Signal = ValidSpec().Signal with
            {
                Triggers = [StrategyTriggerKind.OrderEvent],
            },
            Execution = new StrategyExecutionSpec([StrategyExecutionPolicyKind.Vwap]),
            State = ValidSpec().State with
            {
                Adaptation = StrategyAdaptationKind.ScheduledRetraining,
            },
        };

        spec.Requirements.Select(x => x.Capability).Should().Contain(
        [
            StrategyRuntimeCapability.OrderLifecycleEvents,
            StrategyRuntimeCapability.ScheduledCallbacks,
            StrategyRuntimeCapability.TradeEvents,
            StrategyRuntimeCapability.ManagedModelArtifacts,
        ]);
    }

    [Fact]
    public void Malformed_spec_returns_all_structural_issues_without_throwing()
    {
        var malformed = new StrategySpec(
            "",
            " ",
            StrategyObjectiveKind.ReturnSeeking,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        var issues = StrategySpecValidator.Validate(malformed);

        issues.Select(x => x.Path).Should().Contain(
        [
            "id",
            "name",
            "context.information",
            "signal.hypotheses",
            "signal.triggers",
            "signal.models",
            "execution.policies",
            "state.policies",
            "context.time",
            "portfolio",
            "risk",
        ]);
    }

    [Fact]
    public void Nested_invalid_values_fail_closed_before_capability_inference()
    {
        var malformed = ValidSpec() with
        {
            Objective = (StrategyObjectiveKind)999,
            Context = ValidSpec().Context with
            {
                AssetClasses = [AssetClass.Unknown],
                Information = [(StrategyInformationKind)999],
                Time = new StrategyTimeSemantics(StrategyHorizonKind.Intraday, TimeSpan.Zero),
            },
            Risk = new StrategyRiskSpec([]),
            State = new StrategyStateSpec(
                [StrategyStateKind.Stateless, StrategyStateKind.PositionAware],
                StrategyAdaptationKind.Fixed),
            AdditionalRequirements =
            [
                null!,
                new StrategyCapabilityRequirement((StrategyRuntimeCapability)999, ""),
            ],
        };

        var issues = StrategySpecValidator.Validate(malformed);

        issues.Select(x => x.Code).Should().Contain(
        [
            "spec.enum.invalid",
            "spec.asset_class.unknown",
            "spec.duration.positive",
            "spec.risk_rules.required",
            "spec.state.stateless_conflict",
            "spec.requirement.required",
            "spec.requirement.reason.required",
        ]);

        var assessment = new StrategyCapabilityProfile("anything", Enum.GetValues<StrategyRuntimeCapability>())
            .Assess(malformed);
        assessment.IsSupported.Should().BeFalse();
        assessment.Issues.Should().BeEquivalentTo(issues, options => options.WithStrictOrdering());
        assessment.Missing.Should().BeEmpty();
    }

    private static StrategySpec ValidSpec() => new(
        Id: "sentinel.quote-momentum",
        Name: "Quote Momentum Sentinel",
        Objective: StrategyObjectiveKind.ReturnSeeking,
        Context: new StrategyContextSpec(
            [AssetClass.Equity],
            MarketTopologyKind.SingleInstrument,
            ExposureGeometryKind.DirectionalLongShort,
            [StrategyInformationKind.Quote],
            new StrategyTimeSemantics(StrategyHorizonKind.Intraday, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5))),
        Signal: new StrategySignalSpec(
            [ReturnHypothesisKind.Momentum],
            [StrategyTriggerKind.Quote],
            [SignalModelKind.DeterministicRule]),
        Portfolio: new StrategyPortfolioSpec(PortfolioConstructionKind.FixedQuantity),
        Risk: new StrategyRiskSpec([StrategyRiskExitKind.SignalReversal]),
        Execution: new StrategyExecutionSpec([StrategyExecutionPolicyKind.Market]),
        State: new StrategyStateSpec([StrategyStateKind.PositionAware], StrategyAdaptationKind.Fixed),
        AdditionalRequirements:
        [
            new StrategyCapabilityRequirement(
                StrategyRuntimeCapability.DeterministicReplay,
                "The strategy must qualify through deterministic historical replay."),
        ]);
}
