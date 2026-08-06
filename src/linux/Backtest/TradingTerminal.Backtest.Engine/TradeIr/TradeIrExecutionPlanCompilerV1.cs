using TradingTerminal.Core.Strategies.Definition;
using TradingTerminal.TradeIr.Runtime;

namespace TradingTerminal.Backtest.Engine.TradeIr;

public static class TradeIrExecutionPlanIssueCodesV1
{
    public const string DataRequirementCount = "TRADEIR_PLAN_DATA_REQUIREMENT_COUNT";
    public const string DataRequirementKind = "TRADEIR_PLAN_DATA_REQUIREMENT_KIND";
    public const string PortableInstrumentCount = "TRADEIR_PLAN_PORTABLE_INSTRUMENT_COUNT";
    public const string PortableInstrumentKeyInvalid = "TRADEIR_PLAN_PORTABLE_INSTRUMENT_KEY_INVALID";
    public const string OrderIntentOutputCount = "TRADEIR_PLAN_ORDER_INTENT_OUTPUT_COUNT";
    public const string OperatorUnsupported = "TRADEIR_PLAN_OPERATOR_UNSUPPORTED";
    public const string QuoteMidCount = "TRADEIR_PLAN_QUOTE_MID_COUNT";
    public const string MarketIntentCount = "TRADEIR_PLAN_MARKET_INTENT_COUNT";
    public const string NodeOutsideOrderPath = "TRADEIR_PLAN_NODE_OUTSIDE_ORDER_PATH";
    public const string InstructionCount = "TRADEIR_PLAN_INSTRUCTION_COUNT";
    public const string GraphInvalid = "TRADEIR_PLAN_GRAPH_INVALID";
    public const string ParameterInvalid = "TRADEIR_PLAN_PARAMETER_INVALID";
    public const string FixedQuantityNonIntegral = "TRADEIR_PLAN_FIXED_QUANTITY_NON_INTEGRAL";
    public const string FixedQuantityOutOfRange = "TRADEIR_PLAN_FIXED_QUANTITY_OUT_OF_RANGE";
}

public sealed record TradeIrExecutionPlanIssueV1(string Code, string Path, string Message);

public sealed record TradeIrExecutionPlanCompilationResultV1(
    StrategyCompilationAdmissionResultV1 Admission,
    StrategyCompilationAdmissionManifestV1? AdmissionManifest,
    CompiledTradeIrPlanV1? Plan,
    IReadOnlyList<TradeIrExecutionPlanIssueV1> Issues)
{
    public bool Succeeded =>
        Admission.CanCompile && AdmissionManifest is not null && Plan is not null && Issues.Count == 0;
}

/// <summary>
/// Lowers the Engine-owned quote/EMA target into the dependency-free runtime plan. Target,
/// data-binding, and loaded-artifact admission always complete before any authored node is lowered.
/// </summary>
public static class TradeIrExecutionPlanCompilerV1
{
    private static readonly IReadOnlySet<string> SupportedOperatorIds = new HashSet<string>(
    [
        "execution.market",
        "feature.ema",
        "logic.greater_than",
        "market.quote.mid",
        "portfolio.fixed_quantity",
        "risk.trailing_fraction",
    ],
        StringComparer.Ordinal);

    private const double LongMinimumAsDouble = -9_223_372_036_854_775_808d;
    private const double LongExclusiveMaximumAsDouble = 9_223_372_036_854_775_808d;

    public static TradeIrExecutionPlanCompilationResultV1 Compile(
        StrategyIntermediateRepresentationV1 definition,
        BacktestTradeIrTargetV1 target,
        BacktestTradeIrArtifactSetV1 loadedArtifacts,
        IReadOnlyList<DataSourceCapabilityV1> capabilities,
        IReadOnlyList<DataBindingManifestV1> bindings)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(loadedArtifacts);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(bindings);

        var admissionOutcome = target.AssessAndFreeze(
            definition,
            loadedArtifacts,
            capabilities,
            bindings);
        var admission = admissionOutcome.Assessment;
        if (!admissionOutcome.CanCompile)
            return Failed(admission, Manifest: null, AdmissionIssues(admission));

        var manifest = admissionOutcome.Manifest!;
        var frozenDefinition = manifest.ReadDefinitionForCompilation();

        var issues = new List<TradeIrExecutionPlanIssueV1>();
        if (frozenDefinition.Nodes.Count > TradeIrRuntimeLimitsV1.MaximumInstructionCount)
        {
            issues.Add(new(
                TradeIrExecutionPlanIssueCodesV1.InstructionCount,
                "nodes",
                $"The closed runtime accepts at most {TradeIrRuntimeLimitsV1.MaximumInstructionCount} instructions; found {frozenDefinition.Nodes.Count}."));
        }
        var requirements = frozenDefinition.DataRequirements.ToArray();
        DataRequirementV1? requirement = null;
        SourceIndependentInstrumentRef? instrument = null;
        if (requirements.Length != 1)
        {
            issues.Add(new(
                TradeIrExecutionPlanIssueCodesV1.DataRequirementCount,
                "dataRequirements",
                $"The closed backtest plan requires exactly one data requirement; found {requirements.Length}."));
        }
        else
        {
            requirement = requirements[0];
            if (requirement.DataKind != TradeIrDataKindV1.QuoteL1)
            {
                issues.Add(new(
                    TradeIrExecutionPlanIssueCodesV1.DataRequirementKind,
                    $"dataRequirements[{requirement.RequirementId}].dataKind",
                    $"The closed backtest plan requires QuoteL1, not '{requirement.DataKind}'."));
            }

            var references = requirement.InstrumentSelector.References;
            if (references.Count != 1)
            {
                issues.Add(new(
                    TradeIrExecutionPlanIssueCodesV1.PortableInstrumentCount,
                    $"dataRequirements[{requirement.RequirementId}].instrumentSelector.references",
                    $"The closed backtest plan requires exactly one portable instrument; found {references.Count}."));
            }
            else
            {
                instrument = references[0];
                if (!IsRuntimeText(instrument.InstrumentKey))
                {
                    issues.Add(new(
                        TradeIrExecutionPlanIssueCodesV1.PortableInstrumentKeyInvalid,
                        $"dataRequirements[{requirement.RequirementId}].instrumentSelector.references[0].instrumentKey",
                        "The portable instrument key must be non-empty, trimmed, and free of control characters."));
                }
            }
        }

        var orderIntentOutputs = frozenDefinition.Outputs
            .Where(static output => output.Kind == StrategyIrOutputKindV1.OrderIntent)
            .ToArray();
        StrategyIrOutputBindingV1? orderIntentOutput = null;
        if (orderIntentOutputs.Length != 1)
        {
            issues.Add(new(
                TradeIrExecutionPlanIssueCodesV1.OrderIntentOutputCount,
                "outputs",
                $"The closed backtest plan requires exactly one OrderIntent output; found {orderIntentOutputs.Length}."));
        }
        else
        {
            orderIntentOutput = orderIntentOutputs[0];
        }

        foreach (var node in frozenDefinition.Nodes)
        {
            if (node.OperatorVersion != 1 || !SupportedOperatorIds.Contains(node.OperatorId))
            {
                issues.Add(new(
                    TradeIrExecutionPlanIssueCodesV1.OperatorUnsupported,
                    $"nodes[{node.NodeId}].operatorId",
                    $"The closed backtest plan does not lower '{node.OperatorId}@{node.OperatorVersion}'."));
            }
        }

        var quoteMidNodes = frozenDefinition.Nodes
            .Where(static node => node.OperatorId == "market.quote.mid" && node.OperatorVersion == 1)
            .ToArray();
        if (quoteMidNodes.Length != 1)
        {
            issues.Add(new(
                TradeIrExecutionPlanIssueCodesV1.QuoteMidCount,
                "nodes",
                $"The runtime plan requires exactly one market.quote.mid@1 node; found {quoteMidNodes.Length}."));
        }

        var marketIntentNodes = frozenDefinition.Nodes
            .Where(static node => node.OperatorId == "execution.market" && node.OperatorVersion == 1)
            .ToArray();
        if (marketIntentNodes.Length != 1)
        {
            issues.Add(new(
                TradeIrExecutionPlanIssueCodesV1.MarketIntentCount,
                "nodes",
                $"The runtime plan requires exactly one execution.market@1 node; found {marketIntentNodes.Length}."));
        }

        if (orderIntentOutput is not null)
        {
            var nodeById = frozenDefinition.Nodes.ToDictionary(static node => node.NodeId, StringComparer.Ordinal);
            var orderPath = AncestorsOf(orderIntentOutput.NodeId, nodeById);
            foreach (var nodeId in nodeById.Keys.Except(orderPath, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                issues.Add(new(
                    TradeIrExecutionPlanIssueCodesV1.NodeOutsideOrderPath,
                    $"nodes[{nodeId}]",
                    "Every runtime instruction must contribute to the exported OrderIntent path."));
            }
        }

        if (issues.Count > 0 || requirement is null || instrument is null || orderIntentOutput is null)
            return Failed(admission, manifest, issues);

        var orderedNodes = TopologicalOrder(frozenDefinition.Nodes, issues);
        if (issues.Count > 0)
            return Failed(admission, manifest, issues);

        var slotByNodeId = orderedNodes
            .Select(static (node, slot) => (node.NodeId, Slot: slot))
            .ToDictionary(static pair => pair.NodeId, static pair => pair.Slot, StringComparer.Ordinal);
        var instructions = new List<TradeIrInstructionV1>(orderedNodes.Count);
        for (var slot = 0; slot < orderedNodes.Count; slot++)
        {
            var node = orderedNodes[slot];
            var instruction = Lower(node, slot, slotByNodeId, issues);
            if (instruction is not null) instructions.Add(instruction);
        }

        if (issues.Count > 0)
            return Failed(admission, manifest, issues);

        var plan = new CompiledTradeIrPlanV1(
            StrategyIrCanonicalJsonV1.Hash(frozenDefinition),
            manifest.ManifestHashSha256,
            TradeIrRuntimeSemanticsV1.Version,
            instrument.InstrumentKey,
            instructions,
            orderIntentOutput.OutputId,
            orderIntentOutput.NodeId,
            frozenDefinition.FlattenOnEnd);
        return new TradeIrExecutionPlanCompilationResultV1(admission, manifest, plan, []);
    }

    private static IReadOnlyList<TradeIrExecutionPlanIssueV1> AdmissionIssues(
        StrategyCompilationAdmissionResultV1 admission) => admission.SemanticValidation.Issues
        .Select(static issue => new TradeIrExecutionPlanIssueV1(issue.Code, issue.Path, issue.Message))
        .Concat(admission.Issues.Select(static issue =>
            new TradeIrExecutionPlanIssueV1(issue.Code, issue.Path, issue.Message)))
        .OrderBy(static issue => issue.Path, StringComparer.Ordinal)
        .ThenBy(static issue => issue.Code, StringComparer.Ordinal)
        .ThenBy(static issue => issue.Message, StringComparer.Ordinal)
        .ToArray();

    private static TradeIrExecutionPlanCompilationResultV1 Failed(
        StrategyCompilationAdmissionResultV1 admission,
        StrategyCompilationAdmissionManifestV1? Manifest,
        IEnumerable<TradeIrExecutionPlanIssueV1> issues) => new(
            admission,
            Manifest,
            Plan: null,
            issues.OrderBy(static issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Message, StringComparer.Ordinal)
                .ToArray());

    private static bool IsRuntimeText(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        StringComparer.Ordinal.Equals(value, value.Trim()) &&
        !value.Any(char.IsControl);

    private static IReadOnlyList<StrategyIrNodeV1> TopologicalOrder(
        IReadOnlyList<StrategyIrNodeV1> nodes,
        ICollection<TradeIrExecutionPlanIssueV1> issues)
    {
        var nodeById = nodes.ToDictionary(static node => node.NodeId, StringComparer.Ordinal);
        var indegree = nodeById.Keys.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        var outgoing = nodeById.Keys.ToDictionary(
            static id => id,
            static _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var node in nodeById.Values)
        {
            foreach (var source in node.InputBindings.Values)
            {
                indegree[node.NodeId]++;
                outgoing[source].Add(node.NodeId);
            }
        }

        var ready = new SortedSet<string>(
            indegree.Where(static pair => pair.Value == 0).Select(static pair => pair.Key),
            StringComparer.Ordinal);
        var result = new List<StrategyIrNodeV1>(nodes.Count);
        while (ready.Count > 0)
        {
            var nodeId = ready.Min!;
            ready.Remove(nodeId);
            result.Add(nodeById[nodeId]);
            foreach (var targetId in outgoing[nodeId])
            {
                indegree[targetId]--;
                if (indegree[targetId] == 0) ready.Add(targetId);
            }
        }

        if (result.Count != nodes.Count)
        {
            issues.Add(new(
                TradeIrExecutionPlanIssueCodesV1.GraphInvalid,
                "nodes",
                "The admitted graph could not be ordered as an acyclic runtime plan."));
        }
        return result;
    }

    private static TradeIrInstructionV1? Lower(
        StrategyIrNodeV1 node,
        int slot,
        IReadOnlyDictionary<string, int> slotByNodeId,
        ICollection<TradeIrExecutionPlanIssueV1> issues)
    {
        switch (node.OperatorId)
        {
            case "market.quote.mid":
                return TryText(node, "requirement_id", issues, out var requirementId)
                    ? new QuoteMidInstructionV1(slot, node.NodeId, requirementId)
                    : null;

            case "feature.ema":
                return TryInteger(node, "period", issues, out var period) &&
                       TryInputSlot(node, "value", slotByNodeId, issues, out var valueSlot)
                    ? new EmaInstructionV1(slot, node.NodeId, valueSlot, checked((int)period))
                    : null;

            case "logic.greater_than":
                return TryInputSlot(node, "left", slotByNodeId, issues, out var leftSlot) &&
                       TryInputSlot(node, "right", slotByNodeId, issues, out var rightSlot)
                    ? new GreaterThanInstructionV1(slot, node.NodeId, leftSlot, rightSlot)
                    : null;

            case "portfolio.fixed_quantity":
            {
                var valid = TryInputSlot(node, "decision", slotByNodeId, issues, out var decisionSlot);
                valid &= TryQuantity(node, "when_false", issues, out var whenFalse);
                valid &= TryQuantity(node, "when_true", issues, out var whenTrue);
                return valid
                    ? new FixedQuantityInstructionV1(slot, node.NodeId, decisionSlot, whenFalse, whenTrue)
                    : null;
            }

            case "risk.trailing_fraction":
            {
                return TryInputSlot(node, "price", slotByNodeId, issues, out var priceSlot) &&
                       TryInputSlot(node, "target", slotByNodeId, issues, out var targetSlot) &&
                       TryNumber(node, "fraction", issues, out var fraction)
                    ? new TrailingFractionInstructionV1(
                        slot,
                        node.NodeId,
                        priceSlot,
                        targetSlot,
                        fraction)
                    : null;
            }

            case "execution.market":
            {
                var valid = TryInputSlot(node, "target", slotByNodeId, issues, out var targetSlot);
                int? exitSlot = null;
                if (node.InputBindings.ContainsKey("exit"))
                {
                    valid &= TryInputSlot(node, "exit", slotByNodeId, issues, out var resolvedExitSlot);
                    exitSlot = resolvedExitSlot;
                }
                valid &= TryTimeInForce(node, issues, out var timeInForce);
                return valid
                    ? new MarketIntentInstructionV1(
                        slot,
                        node.NodeId,
                        targetSlot,
                        exitSlot,
                        timeInForce)
                    : null;
            }

            default:
                issues.Add(new(
                    TradeIrExecutionPlanIssueCodesV1.OperatorUnsupported,
                    $"nodes[{node.NodeId}].operatorId",
                    $"The closed backtest plan does not lower '{node.OperatorId}@{node.OperatorVersion}'."));
                return null;
        }
    }

    private static bool TryInputSlot(
        StrategyIrNodeV1 node,
        string port,
        IReadOnlyDictionary<string, int> slotByNodeId,
        ICollection<TradeIrExecutionPlanIssueV1> issues,
        out int slot)
    {
        slot = -1;
        if (node.InputBindings.TryGetValue(port, out var sourceId) &&
            slotByNodeId.TryGetValue(sourceId, out slot)) return true;
        issues.Add(ParameterIssue(
            node,
            $"inputBindings.{port}",
            $"The admitted input port '{port}' could not be resolved to a runtime slot."));
        return false;
    }

    private static bool TryText(
        StrategyIrNodeV1 node,
        string name,
        ICollection<TradeIrExecutionPlanIssueV1> issues,
        out string value)
    {
        value = string.Empty;
        if (node.Parameters.TryGetValue(name, out var literal) &&
            literal.Kind == StrategyLiteralKindV1.Text &&
            literal.TextValue is { } text)
        {
            value = text;
            return true;
        }
        issues.Add(ParameterIssue(node, $"parameters.{name}", "Expected a text literal."));
        return false;
    }

    private static bool TryInteger(
        StrategyIrNodeV1 node,
        string name,
        ICollection<TradeIrExecutionPlanIssueV1> issues,
        out long value)
    {
        value = 0;
        if (node.Parameters.TryGetValue(name, out var literal) &&
            literal.Kind == StrategyLiteralKindV1.Integer &&
            literal.IntegerValue is { } integer)
        {
            value = integer;
            return true;
        }
        issues.Add(ParameterIssue(node, $"parameters.{name}", "Expected an integer literal."));
        return false;
    }

    private static bool TryNumber(
        StrategyIrNodeV1 node,
        string name,
        ICollection<TradeIrExecutionPlanIssueV1> issues,
        out double value)
    {
        value = 0d;
        if (node.Parameters.TryGetValue(name, out var literal))
        {
            if (literal.Kind == StrategyLiteralKindV1.Integer && literal.IntegerValue is { } integer)
            {
                value = integer;
                return true;
            }
            if (literal.Kind == StrategyLiteralKindV1.Number &&
                literal.NumberValue is { } number &&
                double.IsFinite(number))
            {
                value = number;
                return true;
            }
        }
        issues.Add(ParameterIssue(node, $"parameters.{name}", "Expected a finite numeric literal."));
        return false;
    }

    private static bool TryQuantity(
        StrategyIrNodeV1 node,
        string name,
        ICollection<TradeIrExecutionPlanIssueV1> issues,
        out long quantity)
    {
        quantity = 0;
        if (!node.Parameters.TryGetValue(name, out var literal))
        {
            issues.Add(ParameterIssue(node, $"parameters.{name}", "Expected a numeric quantity literal."));
            return false;
        }
        if (literal.Kind == StrategyLiteralKindV1.Integer && literal.IntegerValue is { } integer)
        {
            if (!TradeIrRuntimeLimitsV1.IsSupportedPositionQuantity(integer))
            {
                issues.Add(QuantityOutOfRangeIssue(node, name));
                return false;
            }
            quantity = integer;
            return true;
        }
        if (literal.Kind != StrategyLiteralKindV1.Number ||
            literal.NumberValue is not { } number ||
            !double.IsFinite(number))
        {
            issues.Add(ParameterIssue(node, $"parameters.{name}", "Expected a finite numeric quantity literal."));
            return false;
        }
        if (Math.Truncate(number) != number)
        {
            issues.Add(new(
                TradeIrExecutionPlanIssueCodesV1.FixedQuantityNonIntegral,
                $"nodes[{node.NodeId}].parameters.{name}",
                "A fixed position quantity must be an integral value."));
            return false;
        }
        if (number < LongMinimumAsDouble || number >= LongExclusiveMaximumAsDouble)
        {
            issues.Add(new(
                TradeIrExecutionPlanIssueCodesV1.FixedQuantityOutOfRange,
                $"nodes[{node.NodeId}].parameters.{name}",
                "A fixed position quantity must fit in a signed 64-bit integer."));
            return false;
        }
        quantity = (long)number;
        if (!TradeIrRuntimeLimitsV1.IsSupportedPositionQuantity(quantity))
        {
            issues.Add(QuantityOutOfRangeIssue(node, name));
            quantity = 0;
            return false;
        }
        return true;
    }

    private static bool TryTimeInForce(
        StrategyIrNodeV1 node,
        ICollection<TradeIrExecutionPlanIssueV1> issues,
        out TradeIrTimeInForceV1 timeInForce)
    {
        timeInForce = default;
        if (!node.Parameters.TryGetValue("time_in_force", out var literal) ||
            literal.Kind != StrategyLiteralKindV1.Text)
        {
            issues.Add(ParameterIssue(
                node,
                "parameters.time_in_force",
                "Expected a time-in-force text literal."));
            return false;
        }

        timeInForce = literal.TextValue switch
        {
            "day" => TradeIrTimeInForceV1.Day,
            "good_til_cancelled" => TradeIrTimeInForceV1.GoodTilCancelled,
            "immediate_or_cancel" => TradeIrTimeInForceV1.ImmediateOrCancel,
            _ => default,
        };
        if (timeInForce != default) return true;
        issues.Add(ParameterIssue(
            node,
            "parameters.time_in_force",
            $"Unsupported time-in-force literal '{literal.TextValue}'."));
        return false;
    }

    private static TradeIrExecutionPlanIssueV1 ParameterIssue(
        StrategyIrNodeV1 node,
        string suffix,
        string message) => new(
            TradeIrExecutionPlanIssueCodesV1.ParameterInvalid,
            $"nodes[{node.NodeId}].{suffix}",
            message);

    private static TradeIrExecutionPlanIssueV1 QuantityOutOfRangeIssue(
        StrategyIrNodeV1 node,
        string name) => new(
            TradeIrExecutionPlanIssueCodesV1.FixedQuantityOutOfRange,
            $"nodes[{node.NodeId}].parameters.{name}",
            $"A fixed position quantity must be between {-TradeIrRuntimeLimitsV1.MaximumAbsolutePositionQuantity} and {TradeIrRuntimeLimitsV1.MaximumAbsolutePositionQuantity} so every runtime delta remains representable.");

    private static HashSet<string> AncestorsOf(
        string root,
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!nodes.ContainsKey(root)) return result;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var nodeId))
        {
            if (!result.Add(nodeId)) continue;
            foreach (var sourceId in nodes[nodeId].InputBindings.Values)
                pending.Push(sourceId);
        }
        return result;
    }
}
