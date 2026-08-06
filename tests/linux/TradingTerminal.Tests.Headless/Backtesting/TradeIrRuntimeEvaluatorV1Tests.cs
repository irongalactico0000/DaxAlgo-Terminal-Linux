using System.Reflection;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Definition;
using TradingTerminal.TradeIr.Runtime;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

public sealed class TradeIrRuntimeEvaluatorV1Tests
{
    [Fact]
    public void First_value_seed_readiness_and_trailing_exit_match_the_runtime_contract()
    {
        var evaluator = new TradeIrEvaluatorV1(CreatePlan(flattenOnEnd: true));

        evaluator.EvaluateQuote(Frame(sequence: 1, mid: 100d, position: 0)).Should().BeNull();
        evaluator.EvaluateQuote(Frame(sequence: 2, mid: 80d, position: 0)).Should().BeNull();

        // At sample three the first-value-seeded fast EMA is 92.222..., while the slow EMA is
        // 92.5. The trailing operator consumes its declared one-observation warmup here, so the
        // market output remains unavailable until sample four.
        evaluator.EvaluateQuote(Frame(sequence: 3, mid: 95d, position: 0)).Should().BeNull();

        // A mid of 90 keeps first-value seeding short but would make an SMA-seeded graph long;
        // the emitted side therefore pins both EMA semantics and the extra trailing warmup.
        var entry = evaluator.EvaluateQuote(Frame(sequence: 4, mid: 90d, position: 0));
        entry.Should().NotBeNull();
        entry!.IntentSequence.Should().Be(1);
        entry.SourceEventSequence.Should().Be(4);
        entry.AdmissionManifestSha256.Should().Be(AdmissionManifestHash);
        entry.Side.Should().Be(TradeIrOrderSideV1.Sell);
        entry.Quantity.Should().Be(5);
        entry.TargetQuantity.Should().Be(-5);
        entry.ReduceOnly.Should().BeFalse();

        evaluator.ApplyOrderFeedback(new TradeIrOrderFeedbackV1(
            entry.IntentSequence,
            TradeIrOrderFeedbackStatusV1.Filled,
            cumulativeFilledQuantity: 5));

        // Establish the short position's favorable extreme, then cross its five-percent trail.
        evaluator.EvaluateQuote(Frame(sequence: 5, mid: 80d, position: -5)).Should().BeNull();
        var exit = evaluator.EvaluateQuote(Frame(sequence: 6, mid: 85d, position: -5));
        exit.Should().NotBeNull();
        exit!.IntentSequence.Should().Be(2);
        exit.SourceEventSequence.Should().Be(6);
        exit.Side.Should().Be(TradeIrOrderSideV1.Buy);
        exit.Quantity.Should().Be(5);
        exit.TargetQuantity.Should().Be(0);
        exit.ReduceOnly.Should().BeTrue();

        // Indicator/trailing state continues to advance, but no duplicate intent crosses the host
        // boundary until terminal feedback clears the pending intent.
        evaluator.EvaluateQuote(Frame(sequence: 7, mid: 90d, position: -5)).Should().BeNull();
        evaluator.ApplyOrderFeedback(new TradeIrOrderFeedbackV1(
            exit.IntentSequence,
            TradeIrOrderFeedbackStatusV1.Denied,
            cumulativeFilledQuantity: 0));

        var retriedExit = evaluator.EvaluateQuote(Frame(sequence: 8, mid: 90d, position: -5));
        retriedExit.Should().NotBeNull();
        retriedExit!.IntentSequence.Should().Be(3);
        retriedExit.ReduceOnly.Should().BeTrue();
    }

    [Fact]
    public void Same_plan_frames_and_feedback_produce_equal_intents()
    {
        var first = new TradeIrEvaluatorV1(CreatePlan(flattenOnEnd: true));
        var second = new TradeIrEvaluatorV1(CreatePlan(flattenOnEnd: true));

        for (var sequence = 1; sequence <= 4; sequence++)
        {
            var mid = sequence switch { 1 => 100d, 2 => 80d, 3 => 95d, _ => 90d };
            first.EvaluateQuote(Frame(sequence, mid, position: 0))
                .Should().Be(second.EvaluateQuote(Frame(sequence, mid, position: 0)));
        }

        var feedback = new TradeIrOrderFeedbackV1(
            intentSequence: 1,
            TradeIrOrderFeedbackStatusV1.Filled,
            cumulativeFilledQuantity: 5);
        first.ApplyOrderFeedback(feedback);
        second.ApplyOrderFeedback(feedback);

        first.EvaluateQuote(Frame(sequence: 5, mid: 80d, position: -5))
            .Should().Be(second.EvaluateQuote(Frame(sequence: 5, mid: 80d, position: -5)));
        first.EvaluateQuote(Frame(sequence: 6, mid: 85d, position: -5))
            .Should().Be(second.EvaluateQuote(Frame(sequence: 6, mid: 85d, position: -5)));
    }

    [Fact]
    public void End_emits_only_a_non_crossing_flatten_intent_when_requested_and_not_pending()
    {
        var flattening = new TradeIrEvaluatorV1(CreatePlan(flattenOnEnd: true));
        var intent = flattening.End(new TradeIrPortfolioFrameV1(
            InstrumentKey,
            eventSequence: 0,
            eventTimeUnixMicroseconds: 1_000_000,
            currentPositionQuantity: 3));

        intent.Should().NotBeNull();
        intent!.SourceEventSequence.Should().Be(0);
        intent.Side.Should().Be(TradeIrOrderSideV1.Sell);
        intent.Quantity.Should().Be(3);
        intent.TargetQuantity.Should().Be(0);
        intent.ReduceOnly.Should().BeTrue();
        intent.TimeInForce.Should().Be(TradeIrTimeInForceV1.Day);

        flattening.End(new TradeIrPortfolioFrameV1(
            InstrumentKey,
            eventSequence: 1,
            eventTimeUnixMicroseconds: 1_000_001,
            currentPositionQuantity: 3)).Should().BeNull("the first flatten intent is still pending");

        var nonFlattening = new TradeIrEvaluatorV1(CreatePlan(flattenOnEnd: false));
        nonFlattening.End(new TradeIrPortfolioFrameV1(
            InstrumentKey,
            eventSequence: 0,
            eventTimeUnixMicroseconds: 1_000_000,
            currentPositionQuantity: -3)).Should().BeNull();
    }

    [Fact]
    public void Plan_is_snapshotted_and_hostile_semantics_topology_and_types_fail_closed()
    {
        var mutableInstructions = StandardInstructions().ToList();
        var snapshotted = CreatePlan(flattenOnEnd: true, mutableInstructions);
        mutableInstructions.Clear();

        snapshotted.Instructions.Should().HaveCount(7);
        new TradeIrEvaluatorV1(snapshotted).Should().NotBeNull();

        var wrongSemantics = new CompiledTradeIrPlanV1(
            DefinitionHash,
            AdmissionManifestHash,
            "daxalgo.tradeir.runtime/v2",
            InstrumentKey,
            StandardInstructions(),
            OutputId,
            MarketNodeId,
            flattenOnEnd: true);
        var constructWrongSemantics = () => new TradeIrEvaluatorV1(wrongSemantics);
        constructWrongSemantics.Should().Throw<ArgumentException>().WithMessage("*semantics*unsupported*");

        var forwardReference = StandardInstructions().ToArray();
        forwardReference[1] = new EmaInstructionV1(slot: 1, "fast", valueSlot: 1, period: 2);
        var constructForwardReference = () => new TradeIrEvaluatorV1(CreatePlan(true, forwardReference));
        constructForwardReference.Should().Throw<ArgumentException>().WithMessage("*earlier slot*");

        TradeIrInstructionV1[] wrongTypeInstructions =
        [
            new QuoteMidInstructionV1(0, "price", "quotes"),
            new GreaterThanInstructionV1(1, "decision", 0, 0),
            new EmaInstructionV1(2, "ema-of-boolean", 1, 2),
            new FixedQuantityInstructionV1(3, "target", 1, -5, 5),
            new TrailingFractionInstructionV1(4, "exit", 0, 3, 0.05),
            new MarketIntentInstructionV1(5, MarketNodeId, 3, 4, TradeIrTimeInForceV1.Day),
        ];
        var constructWrongType = () => new TradeIrEvaluatorV1(CreatePlan(true, wrongTypeInstructions));
        constructWrongType.Should().Throw<ArgumentException>().WithMessage("*kind*expected*");
    }

    [Fact]
    public void Feedback_identity_quantity_and_timeline_are_strictly_causal()
    {
        var evaluator = new TradeIrEvaluatorV1(CreatePlan(flattenOnEnd: true));
        var wrongAdmission = () => evaluator.EvaluateQuote(new TradeIrQuoteFrameV1(
            InstrumentKey,
            new string('c', 64),
            eventSequence: 1,
            eventTimeUnixMicroseconds: 1_000_001,
            bid: 100d,
            ask: 100d,
            currentPositionQuantity: 0));
        wrongAdmission.Should().Throw<InvalidOperationException>().WithMessage("*admission-manifest identity*");

        evaluator.EvaluateQuote(Frame(1, 100d, 0));
        evaluator.EvaluateQuote(Frame(2, 80d, 0));
        evaluator.EvaluateQuote(Frame(3, 95d, 0));
        var intent = evaluator.EvaluateQuote(Frame(4, 90d, 0))!;

        var wrongIdentity = () => evaluator.ApplyOrderFeedback(new TradeIrOrderFeedbackV1(
            intent.IntentSequence + 1,
            TradeIrOrderFeedbackStatusV1.Working,
            0));
        wrongIdentity.Should().Throw<InvalidOperationException>().WithMessage("*does not match*");

        var impossiblePartial = () => evaluator.ApplyOrderFeedback(new TradeIrOrderFeedbackV1(
            intent.IntentSequence,
            TradeIrOrderFeedbackStatusV1.PartiallyFilled,
            intent.Quantity));
        impossiblePartial.Should().Throw<InvalidOperationException>().WithMessage("*partial fill*");

        var duplicateSequence = () => evaluator.EvaluateQuote(Frame(4, 96d, 0));
        duplicateSequence.Should().Throw<InvalidOperationException>().WithMessage("*increase strictly*");
    }

    private const string DefinitionHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AdmissionManifestHash =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string InstrumentKey = "equity/xnas/ALPHA";
    private const string OutputId = "order-intent";
    private const string MarketNodeId = "execution";

    private static CompiledTradeIrPlanV1 CreatePlan(
        bool flattenOnEnd,
        IReadOnlyList<TradeIrInstructionV1>? instructions = null) =>
        new(
            DefinitionHash,
            AdmissionManifestHash,
            TradeIrRuntimeSemanticsV1.Version,
            InstrumentKey,
            instructions ?? StandardInstructions(),
            OutputId,
            MarketNodeId,
            flattenOnEnd);

    private static IReadOnlyList<TradeIrInstructionV1> StandardInstructions() =>
    [
        new QuoteMidInstructionV1(0, "price", "quotes"),
        new EmaInstructionV1(1, "fast", 0, period: 2),
        new EmaInstructionV1(2, "slow", 0, period: 3),
        new GreaterThanInstructionV1(3, "decision", 1, 2),
        new FixedQuantityInstructionV1(4, "target", 3, whenFalse: -5, whenTrue: 5),
        new TrailingFractionInstructionV1(5, "exit", 0, 4, fraction: 0.05),
        new MarketIntentInstructionV1(6, MarketNodeId, 4, 5, TradeIrTimeInForceV1.Day),
    ];

    private static TradeIrQuoteFrameV1 Frame(long sequence, double mid, long position) =>
        new(
            InstrumentKey,
            AdmissionManifestHash,
            sequence,
            eventTimeUnixMicroseconds: 1_000_000 + sequence,
            bid: mid,
            ask: mid,
            currentPositionQuantity: position);
}

public sealed class TradeIrRuntimeAuthorityTests
{
    [Fact]
    public void Runtime_semantic_contract_hashes_match_the_current_trusted_catalog()
    {
        var registry = StrategyOperatorRegistryV1.CreateDefault();
        var contracts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["market.quote.mid"] = TradeIrRuntimeSemanticsV1.QuoteMidContract,
            ["feature.ema"] = TradeIrRuntimeSemanticsV1.EmaContract,
            ["logic.greater_than"] = TradeIrRuntimeSemanticsV1.GreaterThanContract,
            ["portfolio.fixed_quantity"] = TradeIrRuntimeSemanticsV1.FixedQuantityContract,
            ["risk.trailing_fraction"] = TradeIrRuntimeSemanticsV1.TrailingFractionContract,
            ["execution.market"] = TradeIrRuntimeSemanticsV1.MarketIntentContract,
        };

        foreach (var (operatorId, contract) in contracts)
        {
            registry.TryResolve(operatorId, 1, out var descriptor).Should().BeTrue();
            var expectedHash = ExecutableStrategyDefinitionCanonicalJson.Hash(
                new SemanticContractHashInput(contract));
            descriptor.SemanticContractHashSha256.Should().Be(expectedHash, operatorId);
        }
    }

    [Fact]
    public void Runtime_assembly_has_no_product_or_host_references()
    {
        var assembly = typeof(TradeIrEvaluatorV1).Assembly;
        var references = assembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();

        references.Should().NotContain(reference =>
            reference.StartsWith("TradingTerminal.Core", StringComparison.Ordinal) ||
            reference.StartsWith("TradingTerminal.Backtest.Engine", StringComparison.Ordinal) ||
            reference.StartsWith("TradingTerminal.Infrastructure", StringComparison.Ordinal) ||
            reference.StartsWith("DaxAlgo", StringComparison.Ordinal));
        references.Should().OnlyContain(reference =>
            reference == "netstandard" || reference.StartsWith("System", StringComparison.Ordinal),
            "the runtime project has no project or package dependencies");
    }

    [Fact]
    public void Public_surface_has_no_authority_or_delegate_types_and_instruction_union_is_closed()
    {
        var assembly = typeof(TradeIrEvaluatorV1).Assembly;
        var exportedTypes = assembly.GetExportedTypes();
        var surfaceTypes = PublicSurfaceTypes(exportedTypes).ToArray();
        var forbiddenTokens = new[]
        {
            "TradingTerminal.Core",
            "TradingTerminal.Backtest.Engine",
            "TradingTerminal.Infrastructure",
            "IOrderRouter",
            "Router",
            "ExecutionCommand",
            "Command",
            "RiskDecision",
            "Risk",
            "Broker",
            "Credential",
            "Adapter",
            "Dispatch",
            "Submission",
            "Handle",
        };

        foreach (var type in surfaceTypes)
        {
            typeof(Delegate).IsAssignableFrom(type).Should().BeFalse(
                $"public runtime surface must not expose delegate type {type}");
            foreach (var token in forbiddenTokens)
            {
                (type.FullName ?? type.Name).Should().NotContainEquivalentOf(
                    token,
                    $"public runtime surface must not expose authority token '{token}'");
            }
        }

        var instructionTypes = exportedTypes
            .Where(type => type != typeof(TradeIrInstructionV1) &&
                typeof(TradeIrInstructionV1).IsAssignableFrom(type))
            .OrderBy(static type => type.Name, StringComparer.Ordinal)
            .ToArray();
        instructionTypes.Should().Equal(
            typeof(EmaInstructionV1),
            typeof(FixedQuantityInstructionV1),
            typeof(GreaterThanInstructionV1),
            typeof(MarketIntentInstructionV1),
            typeof(QuoteMidInstructionV1),
            typeof(TrailingFractionInstructionV1));

        typeof(TradeIrInstructionV1)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().OnlyContain(static constructor => constructor.IsFamilyAndAssembly);
    }

    private static IEnumerable<Type> PublicSurfaceTypes(IReadOnlyList<Type> exportedTypes)
    {
        var discovered = new HashSet<Type>();
        var pending = new Stack<Type>(exportedTypes);
        while (pending.TryPop(out var type))
        {
            AddType(type, discovered, pending);
            foreach (var interfaceType in type.GetInterfaces()) AddType(interfaceType, discovered, pending);
            if (type.BaseType is { } baseType) AddType(baseType, discovered, pending);

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance |
                                       BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var constructor in type.GetConstructors(flags))
                foreach (var parameter in constructor.GetParameters()) AddType(parameter.ParameterType, discovered, pending);
            foreach (var method in type.GetMethods(flags))
            {
                AddType(method.ReturnType, discovered, pending);
                foreach (var parameter in method.GetParameters()) AddType(parameter.ParameterType, discovered, pending);
            }
            foreach (var property in type.GetProperties(flags)) AddType(property.PropertyType, discovered, pending);
            foreach (var field in type.GetFields(flags)) AddType(field.FieldType, discovered, pending);
            foreach (var eventInfo in type.GetEvents(flags))
                if (eventInfo.EventHandlerType is { } eventType) AddType(eventType, discovered, pending);
        }

        return discovered;
    }

    private static void AddType(Type type, ISet<Type> discovered, Stack<Type> pending)
    {
        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            AddType(type.GetElementType()!, discovered, pending);
            return;
        }
        if (!discovered.Add(type)) return;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments()) pending.Push(argument);
    }

    private sealed record SemanticContractHashInput(string Contract);
}
