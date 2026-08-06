namespace TradingTerminal.TradeIr.Runtime;

/// <summary>
/// Deterministically evaluates one admitted, host-compiled graph. This type owns numeric and
/// pending-intent state only; current filled position is supplied afresh by the host on every
/// frame, and the only output is an inert <see cref="TradeIrOrderIntentV1"/>.
/// </summary>
public sealed class TradeIrEvaluatorV1
{
    private readonly CompiledTradeIrPlanV1 _plan;
    private readonly TradeIrInstructionV1[] _instructions;
    private readonly RuntimeSlotValue[] _values;
    private readonly Dictionary<int, EmaState> _emaStates = [];
    private readonly Dictionary<int, TrailingState> _trailingStates = [];
    private readonly MarketIntentInstructionV1 _marketInstruction;
    private bool _hasTimelineValue;
    private long _lastEventSequence;
    private long _lastEventTimeUnixMicroseconds;
    private long? _lastQuoteEventSequence;
    private long _nextIntentSequence;
    private PendingIntent? _pendingIntent;

    public TradeIrEvaluatorV1(CompiledTradeIrPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plan = plan;
        _instructions = plan.Instructions.ToArray();
        _marketInstruction = ValidatePlan(plan, _instructions);
        _values = new RuntimeSlotValue[_instructions.Length];

        foreach (var instruction in _instructions)
        {
            if (instruction is EmaInstructionV1 ema)
                _emaStates.Add(ema.Slot, new EmaState(ema.Period));
            else if (instruction is TrailingFractionInstructionV1 trailing)
                _trailingStates.Add(trailing.Slot, new TrailingState());
        }
    }

    public TradeIrOrderIntentV1? EvaluateQuote(TradeIrQuoteFrameV1 frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!StringComparer.Ordinal.Equals(
                frame.AdmissionManifestSha256,
                _plan.AdmissionManifestSha256))
            throw new InvalidOperationException(
                "Quote frame admission-manifest identity does not match the compiled plan.");
        AdvanceTimeline(frame.InstrumentKey, frame.EventSequence, frame.EventTimeUnixMicroseconds);
        _lastQuoteEventSequence = frame.EventSequence;
        Array.Clear(_values);

        TradeIrOrderIntentV1? emitted = null;
        foreach (var instruction in _instructions)
        {
            switch (instruction)
            {
                case QuoteMidInstructionV1 quote:
                    _values[quote.Slot] = RuntimeSlotValue.FromNumber((frame.Bid * 0.5d) + (frame.Ask * 0.5d));
                    break;

                case EmaInstructionV1 ema:
                {
                    var input = _values[ema.ValueSlot];
                    if (!input.IsReady) break;
                    var state = _emaStates[ema.Slot];
                    state.Push(input.Number);
                    if (state.IsReady)
                        _values[ema.Slot] = RuntimeSlotValue.FromNumber(state.Value);
                    break;
                }

                case GreaterThanInstructionV1 greaterThan:
                {
                    var left = _values[greaterThan.LeftSlot];
                    var right = _values[greaterThan.RightSlot];
                    if (left.IsReady && right.IsReady)
                        _values[greaterThan.Slot] = RuntimeSlotValue.FromBoolean(left.Number > right.Number);
                    break;
                }

                case FixedQuantityInstructionV1 fixedQuantity:
                {
                    var decision = _values[fixedQuantity.DecisionSlot];
                    if (decision.IsReady)
                    {
                        _values[fixedQuantity.Slot] = RuntimeSlotValue.FromTarget(
                            decision.Boolean ? fixedQuantity.WhenTrue : fixedQuantity.WhenFalse);
                    }
                    break;
                }

                case TrailingFractionInstructionV1 trailing:
                {
                    var price = _values[trailing.PriceSlot];
                    var target = _values[trailing.TargetSlot];
                    if (price.IsReady && target.IsReady)
                    {
                        var state = _trailingStates[trailing.Slot];
                        var requestsExit = state.Evaluate(
                            price.Number,
                            frame.CurrentPositionQuantity,
                            trailing.Fraction);
                        if (state.IsReady)
                            _values[trailing.Slot] = RuntimeSlotValue.FromExit(requestsExit);
                    }
                    break;
                }

                case MarketIntentInstructionV1 market:
                {
                    var target = _values[market.TargetSlot];
                    if (!target.IsReady) break;

                    var requestsExit = false;
                    if (market.ExitSlot is { } exitSlot)
                    {
                        var exit = _values[exitSlot];
                        if (!exit.IsReady) break;
                        requestsExit = exit.Exit;
                    }

                    _values[market.Slot] = RuntimeSlotValue.IntentReady;
                    var desiredTarget = requestsExit ? 0L : target.Target;
                    emitted = TryCreateIntent(
                        desiredTarget,
                        frame.CurrentPositionQuantity,
                        market.TimeInForce,
                        frame.EventSequence,
                        frame.EventTimeUnixMicroseconds);
                    break;
                }

                default:
                    throw new InvalidOperationException(
                        $"Unsupported runtime instruction '{instruction.GetType().FullName}'.");
            }
        }

        return emitted;
    }

    public void ApplyOrderFeedback(TradeIrOrderFeedbackV1 feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (_pendingIntent is not { } pending)
            throw new InvalidOperationException("Order feedback arrived when no inert intent is pending.");
        if (feedback.IntentSequence != pending.Sequence)
            throw new InvalidOperationException(
                $"Feedback intent {feedback.IntentSequence} does not match pending intent {pending.Sequence}.");
        if (feedback.CumulativeFilledQuantity < pending.CumulativeFilledQuantity ||
            feedback.CumulativeFilledQuantity > pending.Quantity)
        {
            throw new InvalidOperationException(
                "Cumulative filled quantity must be monotonic and cannot exceed the inert intent quantity.");
        }

        var terminal = false;
        switch (feedback.Status)
        {
            case TradeIrOrderFeedbackStatusV1.Working:
                if (feedback.CumulativeFilledQuantity != 0)
                    throw new InvalidOperationException("A working acknowledgement cannot report a fill.");
                break;

            case TradeIrOrderFeedbackStatusV1.PartiallyFilled:
                if (feedback.CumulativeFilledQuantity is <= 0 ||
                    feedback.CumulativeFilledQuantity >= pending.Quantity)
                    throw new InvalidOperationException("A partial fill must be positive and less than intent quantity.");
                break;

            case TradeIrOrderFeedbackStatusV1.Filled:
                if (feedback.CumulativeFilledQuantity != pending.Quantity)
                    throw new InvalidOperationException("A filled acknowledgement must report the complete intent quantity.");
                terminal = true;
                break;

            case TradeIrOrderFeedbackStatusV1.Cancelled:
                terminal = true;
                break;

            case TradeIrOrderFeedbackStatusV1.Rejected:
            case TradeIrOrderFeedbackStatusV1.Denied:
                if (feedback.CumulativeFilledQuantity != 0)
                    throw new InvalidOperationException("Rejected or denied intent feedback cannot report a fill.");
                terminal = true;
                break;

            default:
                throw new InvalidOperationException($"Unsupported feedback status '{feedback.Status}'.");
        }

        _pendingIntent = terminal
            ? null
            : pending with { CumulativeFilledQuantity = feedback.CumulativeFilledQuantity };
    }

    public TradeIrOrderIntentV1? End(TradeIrPortfolioFrameV1 portfolio)
    {
        ArgumentNullException.ThrowIfNull(portfolio);
        AdvanceTimeline(
            portfolio.InstrumentKey,
            portfolio.EventSequence,
            portfolio.EventTimeUnixMicroseconds);

        if (!_plan.FlattenOnEnd || portfolio.CurrentPositionQuantity == 0 || _pendingIntent is not null)
            return null;

        return TryCreateIntent(
            desiredTarget: 0,
            portfolio.CurrentPositionQuantity,
            _marketInstruction.TimeInForce,
            _lastQuoteEventSequence ?? portfolio.EventSequence,
            portfolio.EventTimeUnixMicroseconds);
    }

    private TradeIrOrderIntentV1? TryCreateIntent(
        long desiredTarget,
        long currentPosition,
        TradeIrTimeInForceV1 timeInForce,
        long sourceEventSequence,
        long eventTimeUnixMicroseconds)
    {
        if (_pendingIntent is not null) return null;

        long delta;
        long quantity;
        try
        {
            delta = checked(desiredTarget - currentPosition);
            if (delta == 0) return null;
            quantity = delta > 0 ? delta : checked(-delta);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "Target delta cannot be represented as a positive 64-bit quantity.",
                exception);
        }

        var sequence = checked(++_nextIntentSequence);
        var intent = new TradeIrOrderIntentV1(
            _plan.DefinitionSha256,
            _plan.AdmissionManifestSha256,
            sequence,
            sourceEventSequence,
            _plan.OrderIntentOutputId,
            _plan.OrderIntentNodeId,
            _plan.InstrumentKey,
            delta > 0 ? TradeIrOrderSideV1.Buy : TradeIrOrderSideV1.Sell,
            quantity,
            desiredTarget,
            timeInForce,
            IsNonCrossingReduction(currentPosition, desiredTarget),
            eventTimeUnixMicroseconds);
        _pendingIntent = new PendingIntent(sequence, quantity, CumulativeFilledQuantity: 0);
        return intent;
    }

    private void AdvanceTimeline(string instrumentKey, long sequence, long eventTimeUnixMicroseconds)
    {
        if (!StringComparer.Ordinal.Equals(instrumentKey, _plan.InstrumentKey))
            throw new InvalidOperationException(
                $"Frame instrument '{instrumentKey}' does not match plan instrument '{_plan.InstrumentKey}'.");
        if (_hasTimelineValue && sequence <= _lastEventSequence)
            throw new InvalidOperationException("Event sequence must increase strictly.");
        if (_hasTimelineValue && eventTimeUnixMicroseconds < _lastEventTimeUnixMicroseconds)
            throw new InvalidOperationException("Event time cannot move backwards.");

        _hasTimelineValue = true;
        _lastEventSequence = sequence;
        _lastEventTimeUnixMicroseconds = eventTimeUnixMicroseconds;
    }

    private static bool IsNonCrossingReduction(long currentPosition, long desiredTarget) =>
        currentPosition switch
        {
            > 0 => desiredTarget >= 0 && desiredTarget < currentPosition,
            < 0 => desiredTarget <= 0 && desiredTarget > currentPosition,
            _ => false,
        };

    private static MarketIntentInstructionV1 ValidatePlan(
        CompiledTradeIrPlanV1 plan,
        IReadOnlyList<TradeIrInstructionV1> instructions)
    {
        if (!StringComparer.Ordinal.Equals(plan.RuntimeSemanticsVersion, TradeIrRuntimeSemanticsV1.Version))
        {
            throw new ArgumentException(
                $"Runtime semantics '{plan.RuntimeSemanticsVersion}' are unsupported; expected '{TradeIrRuntimeSemanticsV1.Version}'.",
                nameof(plan));
        }
        if (instructions.Count is 0 or > TradeIrRuntimeLimitsV1.MaximumInstructionCount)
            throw new ArgumentException(
                $"A runtime plan must contain between 1 and {TradeIrRuntimeLimitsV1.MaximumInstructionCount} instructions.",
                nameof(plan));

        var kinds = new RuntimeSlotKind[instructions.Count];
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var quoteCount = 0;
        MarketIntentInstructionV1? marketInstruction = null;

        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index]
                ?? throw new ArgumentException("Runtime instructions cannot contain null entries.", nameof(plan));
            if (instruction.Slot != index)
                throw new ArgumentException("Runtime instruction slots must be contiguous and in slot order.", nameof(plan));
            if (!nodeIds.Add(instruction.NodeId))
                throw new ArgumentException($"Runtime node id '{instruction.NodeId}' is duplicated.", nameof(plan));

            switch (instruction)
            {
                case QuoteMidInstructionV1:
                    quoteCount++;
                    kinds[index] = RuntimeSlotKind.Number;
                    break;

                case EmaInstructionV1 ema:
                    RequireInput(ema.ValueSlot, index, RuntimeSlotKind.Number, kinds, "EMA value", plan);
                    kinds[index] = RuntimeSlotKind.Number;
                    break;

                case GreaterThanInstructionV1 greaterThan:
                    RequireInput(greaterThan.LeftSlot, index, RuntimeSlotKind.Number, kinds, "greater-than left", plan);
                    RequireInput(greaterThan.RightSlot, index, RuntimeSlotKind.Number, kinds, "greater-than right", plan);
                    kinds[index] = RuntimeSlotKind.Boolean;
                    break;

                case FixedQuantityInstructionV1 fixedQuantity:
                    RequireInput(
                        fixedQuantity.DecisionSlot,
                        index,
                        RuntimeSlotKind.Boolean,
                        kinds,
                        "fixed-quantity decision",
                        plan);
                    kinds[index] = RuntimeSlotKind.Target;
                    break;

                case TrailingFractionInstructionV1 trailing:
                    RequireInput(trailing.PriceSlot, index, RuntimeSlotKind.Number, kinds, "trailing price", plan);
                    RequireInput(trailing.TargetSlot, index, RuntimeSlotKind.Target, kinds, "trailing target", plan);
                    kinds[index] = RuntimeSlotKind.Exit;
                    break;

                case MarketIntentInstructionV1 market:
                    if (marketInstruction is not null)
                        throw new ArgumentException("A runtime plan must contain exactly one market-intent instruction.", nameof(plan));
                    RequireInput(market.TargetSlot, index, RuntimeSlotKind.Target, kinds, "market target", plan);
                    if (market.ExitSlot is { } exitSlot)
                        RequireInput(exitSlot, index, RuntimeSlotKind.Exit, kinds, "market exit", plan);
                    kinds[index] = RuntimeSlotKind.Intent;
                    marketInstruction = market;
                    break;

                default:
                    throw new ArgumentException(
                        $"Instruction type '{instruction.GetType().FullName}' is not in the closed runtime vocabulary.",
                        nameof(plan));
            }
        }

        if (quoteCount != 1)
            throw new ArgumentException("A runtime plan must contain exactly one quote-mid instruction.", nameof(plan));
        if (marketInstruction is null)
            throw new ArgumentException("A runtime plan must contain exactly one market-intent instruction.", nameof(plan));
        if (!StringComparer.Ordinal.Equals(marketInstruction.NodeId, plan.OrderIntentNodeId))
            throw new ArgumentException("The exported order-intent node must be the market-intent instruction.", nameof(plan));
        if (marketInstruction.Slot != instructions.Count - 1)
            throw new ArgumentException("The exported market-intent instruction must be the final plan slot.", nameof(plan));

        RequireAllInstructionsReachMarket(marketInstruction, instructions, plan);
        return marketInstruction;
    }

    private static void RequireInput(
        int inputSlot,
        int currentSlot,
        RuntimeSlotKind expectedKind,
        IReadOnlyList<RuntimeSlotKind> kinds,
        string label,
        CompiledTradeIrPlanV1 plan)
    {
        if (inputSlot >= currentSlot)
            throw new ArgumentException($"{label} input must refer to an earlier slot.", nameof(plan));
        if (kinds[inputSlot] != expectedKind)
        {
            throw new ArgumentException(
                $"{label} input has kind '{kinds[inputSlot]}', expected '{expectedKind}'.",
                nameof(plan));
        }
    }

    private static void RequireAllInstructionsReachMarket(
        MarketIntentInstructionV1 market,
        IReadOnlyList<TradeIrInstructionV1> instructions,
        CompiledTradeIrPlanV1 plan)
    {
        var reachable = new bool[instructions.Count];
        var pending = new Stack<int>();
        pending.Push(market.Slot);
        while (pending.TryPop(out var slot))
        {
            if (reachable[slot]) continue;
            reachable[slot] = true;
            foreach (var input in InputsOf(instructions[slot]))
                pending.Push(input);
        }

        if (reachable.Any(static value => !value))
            throw new ArgumentException("Every runtime instruction must be on the exported market-intent path.", nameof(plan));
    }

    private static IEnumerable<int> InputsOf(TradeIrInstructionV1 instruction)
    {
        switch (instruction)
        {
            case QuoteMidInstructionV1:
                yield break;
            case EmaInstructionV1 ema:
                yield return ema.ValueSlot;
                yield break;
            case GreaterThanInstructionV1 greaterThan:
                yield return greaterThan.LeftSlot;
                yield return greaterThan.RightSlot;
                yield break;
            case FixedQuantityInstructionV1 fixedQuantity:
                yield return fixedQuantity.DecisionSlot;
                yield break;
            case TrailingFractionInstructionV1 trailing:
                yield return trailing.PriceSlot;
                yield return trailing.TargetSlot;
                yield break;
            case MarketIntentInstructionV1 market:
                yield return market.TargetSlot;
                if (market.ExitSlot is { } exitSlot) yield return exitSlot;
                yield break;
        }
    }

    private enum RuntimeSlotKind
    {
        None,
        Number,
        Boolean,
        Target,
        Exit,
        Intent,
    }

    private struct RuntimeSlotValue
    {
        public bool IsReady;
        public double Number;
        public bool Boolean;
        public long Target;
        public bool Exit;

        public static RuntimeSlotValue FromNumber(double value) => new() { IsReady = true, Number = value };
        public static RuntimeSlotValue FromBoolean(bool value) => new() { IsReady = true, Boolean = value };
        public static RuntimeSlotValue FromTarget(long value) => new() { IsReady = true, Target = value };
        public static RuntimeSlotValue FromExit(bool value) => new() { IsReady = true, Exit = value };
        public static RuntimeSlotValue IntentReady => new() { IsReady = true };
    }

    private sealed class EmaState
    {
        private readonly double _alpha;
        private long _sampleCount;

        public EmaState(int period)
        {
            Period = period;
            _alpha = 2d / (period + 1d);
        }

        public int Period { get; }
        public double Value { get; private set; }
        public bool IsReady => _sampleCount >= Period;

        public void Push(double value)
        {
            Value = _sampleCount == 0
                ? value
                : (_alpha * value) + ((1d - _alpha) * Value);
            _sampleCount = checked(_sampleCount + 1);
        }
    }

    private sealed class TrailingState
    {
        private long _readyInputObservations;
        private int _positionSign;
        private double _favorableExtreme;
        private bool _hasExtreme;

        // The trusted binder declares minimumWarmup=1 for risk.trailing_fraction. Its first
        // target-ready observation initializes state; the exported exit value is ready from the
        // following observation onward.
        public bool IsReady => _readyInputObservations > 1;

        public bool Evaluate(double price, long currentPosition, double fraction)
        {
            _readyInputObservations = checked(_readyInputObservations + 1);
            var sign = Math.Sign(currentPosition);
            if (sign == 0)
            {
                _positionSign = 0;
                _favorableExtreme = 0d;
                _hasExtreme = false;
                return false;
            }

            if (!_hasExtreme || sign != _positionSign)
            {
                _positionSign = sign;
                _favorableExtreme = price;
                _hasExtreme = true;
                return false;
            }

            if (sign > 0)
            {
                _favorableExtreme = Math.Max(_favorableExtreme, price);
                return price <= _favorableExtreme * (1d - fraction);
            }

            _favorableExtreme = Math.Min(_favorableExtreme, price);
            return price >= _favorableExtreme * (1d + fraction);
        }
    }

    private readonly record struct PendingIntent(
        long Sequence,
        long Quantity,
        long CumulativeFilledQuantity);
}
