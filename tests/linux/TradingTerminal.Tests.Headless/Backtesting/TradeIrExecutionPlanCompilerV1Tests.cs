using FluentAssertions;
using TradingTerminal.Backtest.Engine.TradeIr;
using TradingTerminal.Core.Strategies.Definition;
using TradingTerminal.TradeIr.Runtime;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

public sealed class TradeIrExecutionPlanCompilerV1Tests
{
    [Fact]
    public void Exact_admission_lowers_to_a_deterministic_runtime_valid_plan()
    {
        var fixture = BacktestTradeIrTargetV1Tests.CreateFixture();

        var first = Compile(fixture, fixture.Definition);
        var second = Compile(fixture, fixture.Definition);

        first.Succeeded.Should().BeTrue();
        first.Admission.CanCompile.Should().BeTrue();
        first.AdmissionManifest.Should().NotBeNull();
        first.Issues.Should().BeEmpty();
        first.Plan.Should().NotBeNull();
        var plan = first.Plan!;
        plan.DefinitionSha256.Should().Be(StrategyIrCanonicalJsonV1.Hash(fixture.Definition));
        plan.AdmissionManifestSha256.Should().Be(first.AdmissionManifest!.ManifestHashSha256);
        StrategyIrCanonicalJsonV1.Hash(first.AdmissionManifest!.ReadDefinitionForCompilation())
            .Should().Be(plan.DefinitionSha256);
        plan.RuntimeSemanticsVersion.Should().Be(TradeIrRuntimeSemanticsV1.Version);
        plan.InstrumentKey.Should().Be("equity/xnas/ALPHA");
        plan.OrderIntentOutputId.Should().Be("order-intent");
        plan.OrderIntentNodeId.Should().Be("execution");
        plan.FlattenOnEnd.Should().BeTrue();
        plan.Instructions.Select(static instruction => (instruction.Slot, instruction.NodeId)).Should().Equal(
            (0, "price"),
            (1, "fast"),
            (2, "slow"),
            (3, "decision"),
            (4, "target"),
            (5, "exit"),
            (6, "execution"));

        var quote = plan.Instructions[0].Should().BeOfType<QuoteMidInstructionV1>().Which;
        quote.RequirementId.Should().Be("quotes");
        var fast = plan.Instructions[1].Should().BeOfType<EmaInstructionV1>().Which;
        (fast.ValueSlot, fast.Period).Should().Be((0, 4));
        var slow = plan.Instructions[2].Should().BeOfType<EmaInstructionV1>().Which;
        (slow.ValueSlot, slow.Period).Should().Be((0, 12));
        var decision = plan.Instructions[3].Should().BeOfType<GreaterThanInstructionV1>().Which;
        (decision.LeftSlot, decision.RightSlot).Should().Be((1, 2));
        var target = plan.Instructions[4].Should().BeOfType<FixedQuantityInstructionV1>().Which;
        (target.DecisionSlot, target.WhenFalse, target.WhenTrue).Should().Be((3, -5L, 5L));
        var exit = plan.Instructions[5].Should().BeOfType<TrailingFractionInstructionV1>().Which;
        (exit.PriceSlot, exit.TargetSlot, exit.Fraction).Should().Be((0, 4, 0.025d));
        var market = plan.Instructions[6].Should().BeOfType<MarketIntentInstructionV1>().Which;
        (market.TargetSlot, market.ExitSlot, market.TimeInForce).Should().Be(
            (4, (int?)5, TradeIrTimeInForceV1.Day));

        second.Plan.Should().NotBeNull();
        var secondPlan = second.Plan!;
        secondPlan.DefinitionSha256.Should().Be(plan.DefinitionSha256);
        secondPlan.Instructions.Select(Describe).Should().Equal(plan.Instructions.Select(Describe));
        var constructEvaluator = () => new TradeIrEvaluatorV1(plan);
        constructEvaluator.Should().NotThrow();
    }

    [Theory]
    [InlineData("day", TradeIrTimeInForceV1.Day)]
    [InlineData("good_til_cancelled", TradeIrTimeInForceV1.GoodTilCancelled)]
    [InlineData("immediate_or_cancel", TradeIrTimeInForceV1.ImmediateOrCancel)]
    public void Time_in_force_literals_map_exactly(string authored, TradeIrTimeInForceV1 expected)
    {
        var fixture = BacktestTradeIrTargetV1Tests.CreateFixture();
        var definition = ReplaceNode(fixture.Definition, "execution", node => node with
        {
            Parameters = Parameters(("time_in_force", StrategyLiteralV1.FromText(authored))),
        });

        var result = Compile(fixture, definition);

        result.Succeeded.Should().BeTrue();
        result.Plan!.Instructions[^1].Should().BeOfType<MarketIntentInstructionV1>()
            .Which.TimeInForce.Should().Be(expected);
    }

    [Fact]
    public void Artifact_capability_and_binding_admission_fail_before_lowering()
    {
        var fixture = BacktestTradeIrTargetV1Tests.CreateFixture();
        var runtimeDrift = fixture.Pins with
        {
            Runtime = fixture.Pins.Runtime with { ArtifactHashSha256 = new string('a', 64) },
        };

        var artifactResult = TradeIrExecutionPlanCompilerV1.Compile(
            fixture.Definition,
            fixture.Target,
            runtimeDrift,
            [fixture.Capability],
            [fixture.Binding]);
        var capabilityResult = TradeIrExecutionPlanCompilerV1.Compile(
            fixture.Definition,
            fixture.Target,
            fixture.Pins,
            [],
            [fixture.Binding]);
        var bindingResult = TradeIrExecutionPlanCompilerV1.Compile(
            fixture.Definition,
            fixture.Target,
            fixture.Pins,
            [fixture.Capability],
            []);

        artifactResult.Plan.Should().BeNull();
        artifactResult.Issues.Should().ContainSingle(issue =>
            issue.Code == BacktestTradeIrAdmissionIssueCodesV1.RuntimeIdentityMismatch);
        capabilityResult.Plan.Should().BeNull();
        capabilityResult.Issues.Should().ContainSingle(issue => issue.Code == "ADMISSION_DATA_CAPABILITY_COUNT");
        bindingResult.Plan.Should().BeNull();
        bindingResult.Issues.Should().ContainSingle(issue => issue.Code == "ADMISSION_DATA_BINDING_COUNT");
    }

    [Fact]
    public void Fixed_quantities_must_be_integral_and_fit_in_signed_64_bits()
    {
        var fixture = BacktestTradeIrTargetV1Tests.CreateFixture();
        var definition = ReplaceNode(fixture.Definition, "target", node => node with
        {
            Parameters = Parameters(
                ("when_false", StrategyLiteralV1.FromNumber(1.5d)),
                ("when_true", StrategyLiteralV1.FromNumber(1e20d))),
        });

        var result = Compile(fixture, definition);

        result.Admission.CanCompile.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        result.Plan.Should().BeNull();
        result.Issues.Select(static issue => (issue.Code, issue.Path)).Should().Equal(
            (TradeIrExecutionPlanIssueCodesV1.FixedQuantityNonIntegral,
                "nodes[target].parameters.when_false"),
            (TradeIrExecutionPlanIssueCodesV1.FixedQuantityOutOfRange,
                "nodes[target].parameters.when_true"));
    }

    [Fact]
    public void Runtime_resource_and_delta_bounds_fail_before_a_plan_is_created()
    {
        var fixture = BacktestTradeIrTargetV1Tests.CreateFixture();
        var extremeTarget = ReplaceNode(fixture.Definition, "target", node => node with
        {
            Parameters = Parameters(
                ("when_false", StrategyLiteralV1.FromNumber(long.MinValue)),
                ("when_true", StrategyLiteralV1.FromNumber(5))),
        });

        var extremeResult = Compile(fixture, extremeTarget);

        extremeResult.Succeeded.Should().BeFalse();
        extremeResult.Plan.Should().BeNull();
        extremeResult.Issues.Should().ContainSingle(issue =>
            issue.Code == TradeIrExecutionPlanIssueCodesV1.FixedQuantityOutOfRange &&
            issue.Path == "nodes[target].parameters.when_false");

        var nodes = fixture.Definition.Nodes.ToList();
        var sourceId = "price";
        var paddingCount = TradeIrRuntimeLimitsV1.MaximumInstructionCount - nodes.Count + 1;
        for (var index = 0; index < paddingCount; index++)
        {
            var nodeId = $"padding-{index:D4}";
            nodes.Add(new StrategyIrNodeV1(
                nodeId,
                "feature.ema",
                1,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = sourceId },
                Parameters(("period", StrategyLiteralV1.FromInteger(2)))));
            sourceId = nodeId;
        }
        nodes = nodes
            .Select(node => node.NodeId == "fast"
                ? node with
                {
                    InputBindings = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["value"] = sourceId,
                    },
                }
                : node)
            .OrderBy(static node => node.NodeId, StringComparer.Ordinal)
            .ToList();
        var oversized = fixture.Definition with { Nodes = nodes };

        var oversizedResult = Compile(fixture, oversized);

        oversizedResult.Succeeded.Should().BeFalse();
        oversizedResult.Plan.Should().BeNull();
        oversizedResult.Issues.Should().Contain(issue =>
            issue.Code == TradeIrExecutionPlanIssueCodesV1.InstructionCount &&
            issue.Path == "nodes");

        var invalidPlanInstruction = () => new FixedQuantityInstructionV1(
            0,
            "target",
            0,
            long.MinValue,
            0);
        var invalidFrame = () => new TradeIrQuoteFrameV1(
            "equity/xnas/ALPHA",
            new string('b', 64),
            1,
            1,
            99d,
            101d,
            long.MinValue);
        invalidPlanInstruction.Should().Throw<ArgumentOutOfRangeException>();
        invalidFrame.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Compiler_lowers_only_the_frozen_manifest_after_caller_mutation()
    {
        var fixture = BacktestTradeIrTargetV1Tests.CreateFixture();
        var mutableFastParameters = fixture.Definition.Nodes.Single(node => node.NodeId == "fast")
            .Parameters.ToDictionary(StringComparer.Ordinal);
        var mutableNodes = fixture.Definition.Nodes
            .Select(node => node.NodeId == "fast" ? node with { Parameters = mutableFastParameters } : node)
            .ToList();
        var mutableDefinition = fixture.Definition with { Nodes = mutableNodes };

        var result = Compile(fixture, mutableDefinition);
        var frozenHash = result.Plan!.DefinitionSha256;

        mutableFastParameters["period"] = StrategyLiteralV1.FromInteger(999);
        mutableNodes.Clear();

        result.Succeeded.Should().BeTrue();
        result.AdmissionManifest.Should().NotBeNull();
        StrategyIrCanonicalJsonV1.Hash(result.AdmissionManifest!.ReadDefinitionForCompilation())
            .Should().Be(frozenHash);
        result.Plan.Instructions.OfType<EmaInstructionV1>().First().Period.Should().Be(4);
        StrategyIrCanonicalJsonV1.Hash(mutableDefinition).Should().NotBe(frozenHash);
    }

    [Fact]
    public void Nonportable_instrument_key_fails_closed_without_runtime_constructor_exception()
    {
        var fixture = BacktestTradeIrTargetV1Tests.CreateFixture();
        var requirement = fixture.Definition.DataRequirements.Single();
        var badInstrument = requirement.InstrumentSelector.References.Single() with
        {
            InstrumentKey = " equity/xnas/ALPHA",
        };
        var badRequirement = requirement with
        {
            InstrumentSelector = new SourceIndependentInstrumentSelectorV1([badInstrument]),
        };
        var definition = fixture.Definition with { DataRequirements = [badRequirement] };
        var capability = fixture.Capability with { AvailableInstruments = [badInstrument] };
        var binding = fixture.Binding with { BoundInstruments = [badInstrument] };
        TradeIrExecutionPlanCompilationResultV1? result = null;

        var compile = () => result = TradeIrExecutionPlanCompilerV1.Compile(
            definition,
            fixture.Target,
            fixture.Pins,
            [capability],
            [binding]);

        compile.Should().NotThrow();
        result.Should().NotBeNull();
        result!.Succeeded.Should().BeFalse();
        result.Plan.Should().BeNull();
        result.Issues.Should().Contain(issue =>
            issue.Code == "text_not_portable" &&
            issue.Path.EndsWith(".instrumentKey", StringComparison.Ordinal));
    }

    [Fact]
    public void Execution_identifier_length_fails_before_compilation_or_gateway_construction()
    {
        var fixture = BacktestTradeIrTargetV1Tests.CreateFixture();
        var definition = fixture.Definition with { StrategyId = new string('s', 257) };

        var compile = () => Compile(fixture, definition);

        compile.Should().NotThrow();
        var result = compile();
        result.Succeeded.Should().BeFalse();
        result.Plan.Should().BeNull();
        result.Issues.Should().ContainSingle(issue =>
            issue.Code == "text_too_long" && issue.Path == "strategyId");
    }

    [Fact]
    public void Oversized_currency_fails_admission_before_host_risk_context_construction()
    {
        var fixture = BacktestTradeIrTargetV1Tests.CreateFixture();
        var requirement = fixture.Definition.DataRequirements.Single();
        var badInstrument = requirement.InstrumentSelector.References.Single() with
        {
            Currency = new string('U', 17),
        };
        var badRequirement = requirement with
        {
            InstrumentSelector = new SourceIndependentInstrumentSelectorV1([badInstrument]),
        };
        var definition = fixture.Definition with { DataRequirements = [badRequirement] };
        var capability = fixture.Capability with { AvailableInstruments = [badInstrument] };
        var binding = fixture.Binding with { BoundInstruments = [badInstrument] };
        TradeIrExecutionPlanCompilationResultV1? result = null;

        var compile = () => result = TradeIrExecutionPlanCompilerV1.Compile(
            definition,
            fixture.Target,
            fixture.Pins,
            [capability],
            [binding]);

        compile.Should().NotThrow();
        result.Should().NotBeNull();
        result!.Succeeded.Should().BeFalse();
        result.Plan.Should().BeNull();
        result.Issues.Should().Contain(issue =>
            issue.Code == "text_too_long" &&
            issue.Path.EndsWith(".currency", StringComparison.Ordinal));
    }

    private static TradeIrExecutionPlanCompilationResultV1 Compile(
        BacktestTradeIrTargetV1Tests.Fixture fixture,
        StrategyIntermediateRepresentationV1 definition) => TradeIrExecutionPlanCompilerV1.Compile(
            definition,
            fixture.Target,
            fixture.Pins,
            [fixture.Capability],
            [fixture.Binding]);

    private static StrategyIntermediateRepresentationV1 ReplaceNode(
        StrategyIntermediateRepresentationV1 definition,
        string nodeId,
        Func<StrategyIrNodeV1, StrategyIrNodeV1> change) => definition with
        {
            Nodes = definition.Nodes
                .Select(node => node.NodeId == nodeId ? change(node) : node)
                .OrderBy(static node => node.NodeId, StringComparer.Ordinal)
                .ToArray(),
        };

    private static IReadOnlyDictionary<string, StrategyLiteralV1> Parameters(
        params (string Name, StrategyLiteralV1 Value)[] values) => values.ToDictionary(
            static value => value.Name,
            static value => value.Value,
            StringComparer.Ordinal);

    private static string Describe(TradeIrInstructionV1 instruction) => instruction switch
    {
        QuoteMidInstructionV1 quote => $"{quote.Slot}:{quote.NodeId}:quote:{quote.RequirementId}",
        EmaInstructionV1 ema => $"{ema.Slot}:{ema.NodeId}:ema:{ema.ValueSlot}:{ema.Period}",
        GreaterThanInstructionV1 greater =>
            $"{greater.Slot}:{greater.NodeId}:greater:{greater.LeftSlot}:{greater.RightSlot}",
        FixedQuantityInstructionV1 target =>
            $"{target.Slot}:{target.NodeId}:fixed:{target.DecisionSlot}:{target.WhenFalse}:{target.WhenTrue}",
        TrailingFractionInstructionV1 exit =>
            $"{exit.Slot}:{exit.NodeId}:trailing:{exit.PriceSlot}:{exit.TargetSlot}:{exit.Fraction:R}",
        MarketIntentInstructionV1 market =>
            $"{market.Slot}:{market.NodeId}:market:{market.TargetSlot}:{market.ExitSlot}:{market.TimeInForce}",
        _ => throw new ArgumentOutOfRangeException(nameof(instruction)),
    };
}
