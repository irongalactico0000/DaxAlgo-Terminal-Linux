namespace TradingTerminal.Core.Strategies.Definition;

/// <summary>
/// Deterministic structural, causal, graph, type, and placement validation. It never asks the
/// model whether a definition is valid.
/// </summary>
public static class StrategyIrValidatorV1
{
    public static StrategyIrValidationResultV1 Validate(
        StrategyIntermediateRepresentationV1 definition,
        IStrategyOperatorRegistryV1 registry)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);

        var issues = new List<StrategyIrIssueV1>();
        ValidateEnvelope(definition, registry, issues);
        ValidateData(definition.Clock, definition.DataRequirements, issues);

        var nodes = definition.Nodes ?? [];
        var nodeById = new Dictionary<string, StrategyIrNodeV1>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is null)
            {
                issues.Add(new StrategyIrIssueV1("node_null", "nodes", "Node entries cannot be null."));
                continue;
            }
            ValidateNodeShape(node, issues);
            if (!string.IsNullOrWhiteSpace(node.NodeId) && !nodeById.TryAdd(node.NodeId, node))
                issues.Add(new StrategyIrIssueV1("node_duplicate", $"nodes[{node.NodeId}]", "Node id must be unique."));
        }
        RequireCanonicalOrder(nodes.Select(static node => node?.NodeId), "nodes", issues);

        var descriptorByNode = ResolveDescriptors(nodeById, registry, issues);
        ValidateBindings(nodeById, descriptorByNode, issues);
        var order = TopologicalOrder(nodeById, issues);
        var analyses = BindTypes(order, nodeById, descriptorByNode, definition.DataRequirements, issues);
        ValidateOutputs(definition.Outputs, nodeById, analyses, issues);
        ValidateOutputCausality(definition.Outputs, nodeById, analyses, issues);
        ValidateReachability(definition.Outputs, nodeById, issues);

        IReadOnlyList<StrategyCapabilityRequirementV1> definitionCapabilities = definition.FlattenOnEnd
            ? [new StrategyCapabilityRequirementV1(
                "lifecycle.flatten_on_end",
                "The host must deterministically flatten remaining positions when the run ends.")]
            : [];

        return new StrategyIrValidationResultV1(
            issues
                .OrderBy(static issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Message, StringComparer.Ordinal)
                .ToArray(),
            analyses.Values.OrderBy(static node => node.NodeId, StringComparer.Ordinal).ToArray(),
            definitionCapabilities);
    }

    public static StrategyIrValidationResultV1 ReadAndValidate(
        string json,
        IStrategyOperatorRegistryV1 registry)
    {
        try
        {
            return Validate(StrategyIrCanonicalJsonV1.Deserialize(json), registry);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            return new StrategyIrValidationResultV1(
                [new StrategyIrIssueV1("json_invalid", "$", exception.Message)],
                [],
                DefinitionCapabilities: []);
        }
    }

    private static void ValidateEnvelope(
        StrategyIntermediateRepresentationV1 definition,
        IStrategyOperatorRegistryV1 registry,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (definition.SchemaVersion != StrategyIntermediateRepresentationV1.CurrentSchemaVersion)
            issues.Add(new StrategyIrIssueV1("schema_version_unsupported", "schemaVersion",
                $"Expected schema version {StrategyIntermediateRepresentationV1.CurrentSchemaVersion}."));
        RequireText(definition.StrategyId, "strategyId", issues);
        RequireText(definition.StrategyVersion, "strategyVersion", issues);
        RequireMaximumLength(definition.StrategyId, 256, "strategyId", issues);
        RequireMaximumLength(definition.StrategyVersion, 256, "strategyVersion", issues);

        if (definition.OperatorCatalog is null)
        {
            issues.Add(new StrategyIrIssueV1("catalog_missing", "operatorCatalog", "Operator catalog reference is required."));
        }
        else
        {
            RequireText(definition.OperatorCatalog.CatalogId, "operatorCatalog.catalogId", issues);
            RequireText(definition.OperatorCatalog.CatalogVersion, "operatorCatalog.catalogVersion", issues);
            if (!IsSha256(definition.OperatorCatalog.CatalogHashSha256))
                issues.Add(new StrategyIrIssueV1("catalog_hash_invalid", "operatorCatalog.catalogHashSha256",
                    "Catalog hash must be 64 lowercase hexadecimal characters."));
            if (definition.OperatorCatalog != registry.Catalog)
                issues.Add(new StrategyIrIssueV1("catalog_mismatch", "operatorCatalog",
                    $"Definition pins {Describe(definition.OperatorCatalog)}, but validator loaded {Describe(registry.Catalog)}."));
        }

        if (definition.DataRequirements is null)
            issues.Add(new StrategyIrIssueV1("data_requirements_missing", "dataRequirements", "Data requirements are required."));
        if (definition.Nodes is null)
            issues.Add(new StrategyIrIssueV1("nodes_missing", "nodes", "Node collection is required."));
        if (definition.Outputs is null)
            issues.Add(new StrategyIrIssueV1("outputs_missing", "outputs", "Named outputs are required."));
    }

    private static void ValidateData(
        StrategyClockKindV1 clock,
        IReadOnlyList<DataRequirementV1>? requirements,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (!Enum.IsDefined(clock) || clock != StrategyClockKindV1.EventTime)
            issues.Add(new StrategyIrIssueV1("clock_unsupported", "clock", "V1 requires an event-time clock."));
        if (requirements is null) return;
        if (requirements.Count == 0)
            issues.Add(new StrategyIrIssueV1("data_requirements_empty", "dataRequirements", "At least one data requirement is required."));

        var requirementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            if (requirement is null)
            {
                issues.Add(new StrategyIrIssueV1("data_requirement_null", "dataRequirements", "Data requirement cannot be null."));
                continue;
            }
            var path = $"dataRequirements[{requirement.RequirementId}]";
            RequireIdentifier(requirement.RequirementId, $"{path}.requirementId", issues);
            if (!requirementIds.Add(requirement.RequirementId))
                issues.Add(new StrategyIrIssueV1("data_requirement_duplicate", path, "Data requirement id must be unique."));
            if (!Enum.IsDefined(requirement.DataKind) || requirement.DataKind == TradeIrDataKindV1.Unknown)
                issues.Add(new StrategyIrIssueV1("data_kind_invalid", $"{path}.dataKind", "A supported data kind is required."));
            if (requirement.InstrumentSelector?.References is not { Count: > 0 })
                issues.Add(new StrategyIrIssueV1("instrument_selector_empty", $"{path}.instrumentSelector", "At least one source-independent instrument is required."));
            else
                ValidateInstruments(requirement.InstrumentSelector.References, path, issues);
            if (requirement.EventSchema is null || string.IsNullOrWhiteSpace(requirement.EventSchema.SchemaId) ||
                requirement.EventSchema.SchemaVersion <= 0 || !IsSha256(requirement.EventSchema.SchemaHashSha256))
                issues.Add(new StrategyIrIssueV1("event_schema_invalid", $"{path}.eventSchema", "A versioned, SHA-256-bound event schema is required."));
            else
                ValidateEventSchema(requirement.EventSchema, path, issues);
            if (requirement.TemporalSemantics is null ||
                !Enum.IsDefined(requirement.TemporalSemantics.EventTimeBasis) ||
                requirement.TemporalSemantics.EventTimeBasis == TradeIrEventTimeBasisV1.Unknown ||
                !requirement.TemporalSemantics.RequirePointInTimeAvailability)
                issues.Add(new StrategyIrIssueV1("point_in_time_required", $"{path}.temporalSemantics", "Explicit event-time and point-in-time availability semantics are required."));
            else
                ValidateTemporalSemantics(requirement.DataKind, requirement.TemporalSemantics, path, issues);
            if (!Enum.IsDefined(requirement.NormalizationPolicy) || requirement.NormalizationPolicy == TradeIrNormalizationPolicyV1.Unknown ||
                !Enum.IsDefined(requirement.MissingDataPolicy) || requirement.MissingDataPolicy == TradeIrMissingDataPolicyV1.Unknown ||
                !Enum.IsDefined(requirement.RevisionPolicy) || requirement.RevisionPolicy == TradeIrRevisionPolicyV1.Unknown)
                issues.Add(new StrategyIrIssueV1("data_policy_invalid", path, "Normalization, missing-data, and revision policies must be explicit."));
            if (requirement.RequiredSnapshotHashSha256 is { } snapshotHash && !IsSha256(snapshotHash))
                issues.Add(new StrategyIrIssueV1("snapshot_hash_invalid", $"{path}.requiredSnapshotHashSha256", "Snapshot identity must be a lowercase SHA-256 digest."));
        }
        RequireCanonicalOrder(requirements.Select(static requirement => requirement?.RequirementId), "dataRequirements", issues);
    }

    private static void ValidateInstruments(
        IReadOnlyList<SourceIndependentInstrumentRef> references,
        string requirementPath,
        ICollection<StrategyIrIssueV1> issues)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (reference is null)
            {
                issues.Add(new StrategyIrIssueV1("instrument_null", $"{requirementPath}.instrumentSelector.references", "Instrument references cannot be null."));
                continue;
            }
            var path = $"{requirementPath}.instrumentSelector.references[{reference.InstrumentKey}]";
            RequireText(reference.InstrumentKey, $"{path}.instrumentKey", issues);
            RequireText(reference.Symbol, $"{path}.symbol", issues);
            RequireText(reference.Venue, $"{path}.venue", issues);
            RequireText(reference.Currency, $"{path}.currency", issues);
            RequireMaximumLength(reference.Currency, 16, $"{path}.currency", issues);
            if (!Enum.IsDefined(reference.AssetClass) || reference.AssetClass == TradingTerminal.Core.Domain.AssetClass.Unknown)
                issues.Add(new StrategyIrIssueV1("asset_class_invalid", $"{path}.assetClass", "A broker-neutral asset class is required."));
            if (!string.IsNullOrWhiteSpace(reference.InstrumentKey) && !keys.Add(reference.InstrumentKey))
                issues.Add(new StrategyIrIssueV1("instrument_duplicate", path, "Instrument keys must be unique within a requirement."));
        }
        RequireCanonicalOrder(references.Where(static reference => reference is not null)
            .Select(static reference => reference.InstrumentKey), $"{requirementPath}.instrumentSelector.references", issues);
    }

    private static void ValidateEventSchema(
        CanonicalEventSchemaV1 schema,
        string requirementPath,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (schema.PayloadFields is not { Count: > 0 })
        {
            issues.Add(new StrategyIrIssueV1("event_schema_fields_empty", $"{requirementPath}.eventSchema.payloadFields", "At least one canonical payload field is required."));
            return;
        }
        foreach (var field in schema.PayloadFields)
            RequireIdentifier(field, $"{requirementPath}.eventSchema.payloadFields", issues);
        RequireCanonicalOrder(schema.PayloadFields, $"{requirementPath}.eventSchema.payloadFields", issues);
    }

    private static void ValidateTemporalSemantics(
        TradeIrDataKindV1 dataKind,
        DataTemporalSemanticsV1 temporal,
        string requirementPath,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (!Enum.IsDefined(temporal.TimestampPrecision) || temporal.TimestampPrecision == TradeIrTimestampPrecisionV1.Unknown ||
            !Enum.IsDefined(temporal.Ordering) || temporal.Ordering == TradeIrEventOrderingV1.Unknown)
            issues.Add(new StrategyIrIssueV1("temporal_semantics_invalid", $"{requirementPath}.temporalSemantics", "Timestamp precision and deterministic ordering must be explicit."));
        if (!temporal.RequireAuthoritativeEventTime)
            issues.Add(new StrategyIrIssueV1("authoritative_event_time_required", $"{requirementPath}.temporalSemantics", "V1 executable inputs require authoritative event time."));
        if (temporal.Interval is { } interval && interval <= TimeSpan.Zero)
            issues.Add(new StrategyIrIssueV1("interval_invalid", $"{requirementPath}.temporalSemantics.interval", "Interval must be positive when present."));
        if (dataKind == TradeIrDataKindV1.Bar && temporal.Interval is null)
            issues.Add(new StrategyIrIssueV1("bar_interval_required", $"{requirementPath}.temporalSemantics.interval", "Bar data requires an explicit interval."));
    }

    private static void ValidateNodeShape(StrategyIrNodeV1 node, ICollection<StrategyIrIssueV1> issues)
    {
        var path = $"nodes[{node.NodeId}]";
        RequireIdentifier(node.NodeId, $"{path}.nodeId", issues);
        RequireNamespacedId(node.OperatorId, $"{path}.operatorId", issues);
        if (node.OperatorVersion <= 0)
            issues.Add(new StrategyIrIssueV1("operator_version_invalid", $"{path}.operatorVersion", "Operator version must be positive."));
        if (node.InputBindings is null)
            issues.Add(new StrategyIrIssueV1("input_bindings_missing", $"{path}.inputBindings", "Input bindings are required."));
        else
            foreach (var binding in node.InputBindings)
            {
                RequireIdentifier(binding.Key, $"{path}.inputBindings", issues);
                RequireIdentifier(binding.Value, $"{path}.inputBindings.{binding.Key}", issues);
            }
        if (node.Parameters is null)
        {
            issues.Add(new StrategyIrIssueV1("parameters_missing", $"{path}.parameters", "Parameter map is required."));
            return;
        }
        foreach (var parameter in node.Parameters)
        {
            RequireIdentifier(parameter.Key, $"{path}.parameters", issues);
            ValidateLiteral(parameter.Value, $"{path}.parameters.{parameter.Key}", issues);
        }
    }

    private static Dictionary<string, StrategyOperatorDescriptorV1> ResolveDescriptors(
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes,
        IStrategyOperatorRegistryV1 registry,
        ICollection<StrategyIrIssueV1> issues)
    {
        var result = new Dictionary<string, StrategyOperatorDescriptorV1>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            if (registry.TryResolve(node.OperatorId, node.OperatorVersion, out var descriptor))
                result[node.NodeId] = descriptor;
            else
                issues.Add(new StrategyIrIssueV1("operator_unknown", $"nodes[{node.NodeId}].operatorId",
                    $"Operator '{node.OperatorId}@{node.OperatorVersion}' is not present in catalog {registry.Catalog.CatalogId}@{registry.Catalog.CatalogVersion}."));
        }
        return result;
    }

    private static void ValidateBindings(
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes,
        IReadOnlyDictionary<string, StrategyOperatorDescriptorV1> descriptors,
        ICollection<StrategyIrIssueV1> issues)
    {
        foreach (var node in nodes.Values)
        {
            if (node.InputBindings is null || !descriptors.TryGetValue(node.NodeId, out var descriptor)) continue;
            var allowed = descriptor.RequiredInputPorts.Concat(descriptor.OptionalInputPorts).ToHashSet(StringComparer.Ordinal);
            foreach (var required in descriptor.RequiredInputPorts)
                if (!node.InputBindings.ContainsKey(required))
                    issues.Add(new StrategyIrIssueV1("input_port_missing", $"nodes[{node.NodeId}].inputBindings.{required}",
                        $"Required input port '{required}' is not bound."));
            foreach (var binding in node.InputBindings)
            {
                if (!allowed.Contains(binding.Key))
                    issues.Add(new StrategyIrIssueV1("input_port_unknown", $"nodes[{node.NodeId}].inputBindings.{binding.Key}",
                        $"Operator has no input port '{binding.Key}'."));
                if (!string.IsNullOrWhiteSpace(binding.Value) && !nodes.ContainsKey(binding.Value))
                    issues.Add(new StrategyIrIssueV1("input_node_missing", $"nodes[{node.NodeId}].inputBindings.{binding.Key}",
                        $"Input node '{binding.Value}' does not exist."));
            }
        }
    }

    private static IReadOnlyList<string> TopologicalOrder(
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes,
        ICollection<StrategyIrIssueV1> issues)
    {
        var indegree = nodes.Keys.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        var outgoing = nodes.Keys.ToDictionary(static id => id, static _ => new List<string>(), StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            foreach (var source in node.InputBindings?.Values ?? [])
            {
                if (string.IsNullOrWhiteSpace(source) || !nodes.ContainsKey(source)) continue;
                indegree[node.NodeId]++;
                outgoing[source].Add(node.NodeId);
            }
        }
        var ready = new SortedSet<string>(indegree.Where(static pair => pair.Value == 0).Select(static pair => pair.Key), StringComparer.Ordinal);
        var order = new List<string>(nodes.Count);
        while (ready.Count > 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            order.Add(current);
            foreach (var target in outgoing[current])
                if (--indegree[target] == 0) ready.Add(target);
        }
        if (order.Count != nodes.Count)
        {
            var cycle = indegree.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).Order(StringComparer.Ordinal);
            issues.Add(new StrategyIrIssueV1("graph_cycle", "nodes", $"Graph contains a cycle involving: {string.Join(", ", cycle)}."));
        }
        return order;
    }

    private static Dictionary<string, StrategyIrNodeAnalysisV1> BindTypes(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes,
        IReadOnlyDictionary<string, StrategyOperatorDescriptorV1> descriptors,
        IReadOnlyList<DataRequirementV1>? dataRequirements,
        ICollection<StrategyIrIssueV1> issues)
    {
        var result = new Dictionary<string, StrategyIrNodeAnalysisV1>(StringComparer.Ordinal);
        if (dataRequirements is null) return result;
        var nonNullDataRequirements = dataRequirements
            .OfType<DataRequirementV1>()
            .Where(static requirement =>
                requirement.InstrumentSelector?.References is { Count: > 0 } references &&
                references.All(static reference => reference is not null) &&
                requirement.EventSchema is not null &&
                requirement.TemporalSemantics is not null)
            .ToArray();
        foreach (var nodeId in order)
        {
            var node = nodes[nodeId];
            if (!descriptors.TryGetValue(nodeId, out var descriptor) ||
                node.InputBindings is null ||
                node.Parameters is null ||
                node.Parameters.Any(static parameter => parameter.Value is null))
                continue;
            var inputs = new Dictionary<string, StrategyValueTypeV1>(StringComparer.Ordinal);
            var unresolved = false;
            foreach (var binding in node.InputBindings)
            {
                if (string.IsNullOrWhiteSpace(binding.Value))
                {
                    unresolved = true;
                    continue;
                }
                if (result.TryGetValue(binding.Value, out var source)) inputs[binding.Key] = source.OutputType;
                else if (nodes.ContainsKey(binding.Value)) unresolved = true;
            }
            if (unresolved)
            {
                issues.Add(new StrategyIrIssueV1("input_untyped", $"nodes[{nodeId}].inputBindings",
                    "One or more upstream nodes failed type validation."));
                continue;
            }

            var bindingResult = descriptor.Binder(new StrategyOperatorBindingContextV1(node, inputs, nonNullDataRequirements));
            foreach (var issue in bindingResult.Issues) issues.Add(issue);
            if (!bindingResult.IsValid) continue;
            var typeIssueCount = issues.Count;
            StrategyValueTypeRulesV1.Validate(
                bindingResult.OutputType,
                $"nodes[{nodeId}].outputType",
                (code, path, message) => issues.Add(new StrategyIrIssueV1(
                    "operator_output_type_invalid",
                    path,
                    $"{code}: {message}")));
            if (issues.Count != typeIssueCount) continue;
            var upstreamWarmup = node.InputBindings.Values
                .Where(static source => !string.IsNullOrWhiteSpace(source))
                .Select(source => result.TryGetValue(source, out var sourceAnalysis)
                    ? sourceAnalysis.MinimumWarmupObservations
                    : 0)
                .DefaultIfEmpty(0)
                .Max();
            var cumulativeWarmup = (long)upstreamWarmup + bindingResult.MinimumWarmupObservations;
            if (cumulativeWarmup > int.MaxValue)
            {
                issues.Add(new StrategyIrIssueV1("warmup_overflow", $"nodes[{nodeId}]",
                    "Cumulative warm-up exceeds the supported 32-bit observation count."));
                continue;
            }
            result[nodeId] = new StrategyIrNodeAnalysisV1(
                nodeId,
                descriptor.Key,
                bindingResult.OutputType!,
                descriptor.StateKind,
                descriptor.Placement,
                bindingResult.MinimumWarmupObservations,
                (int)cumulativeWarmup,
                descriptor.Capabilities);
        }
        return result;
    }

    private static void ValidateOutputs(
        IReadOnlyList<StrategyIrOutputBindingV1>? outputs,
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes,
        IReadOnlyDictionary<string, StrategyIrNodeAnalysisV1> analyses,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (outputs is null) return;
        if (outputs.Count == 0)
            issues.Add(new StrategyIrIssueV1("outputs_empty", "outputs", "At least one typed output is required."));
        RequireCanonicalOrder(outputs.Select(static output => output?.OutputId), "outputs", issues);
        var outputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            if (output is null)
            {
                issues.Add(new StrategyIrIssueV1("output_null", "outputs", "Output entries cannot be null."));
                continue;
            }
            var path = $"outputs[{output.OutputId}]";
            RequireIdentifier(output.OutputId, $"{path}.outputId", issues);
            if (!outputIds.Add(output.OutputId))
                issues.Add(new StrategyIrIssueV1("output_duplicate", path, "Output id must be unique."));
            RequireIdentifier(output.NodeId, $"{path}.nodeId", issues);
            if (!Enum.IsDefined(output.Kind))
            {
                issues.Add(new StrategyIrIssueV1("output_kind_invalid", $"{path}.kind", "Unknown output kind."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(output.NodeId)) continue;
            switch (output.Kind)
            {
                case StrategyIrOutputKindV1.Signal:
                    RequireSignalOutput(output.NodeId, $"{path}.nodeId", nodes, analyses, issues);
                    break;
                case StrategyIrOutputKindV1.Target:
                    RequireOutput(output.NodeId, $"{path}.nodeId", StrategyIrTypeIdsV1.PortfolioTarget,
                        StrategyOperatorPlacementV1.HostPortfolio, nodes, analyses, issues);
                    break;
                case StrategyIrOutputKindV1.QuoteIntent:
                    RequireOutput(output.NodeId, $"{path}.nodeId", StrategyIrTypeIdsV1.QuoteIntent,
                        StrategyOperatorPlacementV1.HostExecutionIntent, nodes, analyses, issues);
                    break;
                case StrategyIrOutputKindV1.OrderIntent:
                    RequireOutput(output.NodeId, $"{path}.nodeId", StrategyIrTypeIdsV1.OrderIntent,
                        StrategyOperatorPlacementV1.HostExecutionIntent, nodes, analyses, issues);
                    break;
            }
        }
    }

    private static void RequireSignalOutput(
        string nodeId,
        string path,
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes,
        IReadOnlyDictionary<string, StrategyIrNodeAnalysisV1> analyses,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (!nodes.ContainsKey(nodeId))
        {
            issues.Add(new StrategyIrIssueV1("output_node_missing", path, $"Output node '{nodeId}' does not exist."));
            return;
        }
        if (!analyses.TryGetValue(nodeId, out var analysis)) return;
        if (analysis.OutputType.TypeId is not (StrategyIrTypeIdsV1.Boolean or StrategyIrTypeIdsV1.Number))
            issues.Add(new StrategyIrIssueV1("output_type_mismatch", path,
                $"Signal output must be Boolean or numeric, but node produces '{analysis.OutputType.TypeId}'."));
        if (analysis.Placement != StrategyOperatorPlacementV1.RestrictedCompute)
            issues.Add(new StrategyIrIssueV1("output_placement_mismatch", path,
                $"Signal output must use placement '{StrategyOperatorPlacementV1.RestrictedCompute}'."));
    }

    private static void RequireOutput(
        string nodeId,
        string path,
        string typeId,
        StrategyOperatorPlacementV1 placement,
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes,
        IReadOnlyDictionary<string, StrategyIrNodeAnalysisV1> analyses,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (!nodes.ContainsKey(nodeId))
        {
            issues.Add(new StrategyIrIssueV1("output_node_missing", path, $"Output node '{nodeId}' does not exist."));
            return;
        }
        if (!analyses.TryGetValue(nodeId, out var analysis)) return;
        if (analysis.OutputType.TypeId != typeId)
            issues.Add(new StrategyIrIssueV1("output_type_mismatch", path,
                $"Output requires '{typeId}', but node produces '{analysis.OutputType.TypeId}'."));
        if (analysis.Placement != placement)
            issues.Add(new StrategyIrIssueV1("output_placement_mismatch", path,
                $"Output requires placement '{placement}', but node is '{analysis.Placement}'."));
    }

    private static void ValidateOutputCausality(
        IReadOnlyList<StrategyIrOutputBindingV1>? outputs,
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes,
        IReadOnlyDictionary<string, StrategyIrNodeAnalysisV1> analyses,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (outputs is null) return;
        var valid = outputs
            .Where(output => output is not null &&
                             !string.IsNullOrWhiteSpace(output.OutputId) &&
                             !string.IsNullOrWhiteSpace(output.NodeId) &&
                             nodes.ContainsKey(output.NodeId))
            .GroupBy(static output => output.OutputId, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        var signals = valid.Where(static output => output.Kind == StrategyIrOutputKindV1.Signal).ToArray();
        var targets = valid.Where(static output => output.Kind == StrategyIrOutputKindV1.Target).ToArray();
        var actions = valid.Where(static output =>
            output.Kind is StrategyIrOutputKindV1.OrderIntent or StrategyIrOutputKindV1.QuoteIntent).ToArray();

        var ancestry = actions.ToDictionary(
            static output => output.OutputId,
            output => AncestorsOf(output.NodeId, nodes),
            StringComparer.Ordinal);

        foreach (var action in actions)
        {
            var ancestors = ancestry[action.OutputId];
            var typedTargetNodes = ancestors
                .Where(nodeId => analyses.TryGetValue(nodeId, out var analysis) &&
                                 analysis.OutputType.TypeId == StrategyIrTypeIdsV1.PortfolioTarget)
                .ToHashSet(StringComparer.Ordinal);
            var exportedTargetNodes = targets.Select(static output => output.NodeId).ToHashSet(StringComparer.Ordinal);

            if (action.Kind == StrategyIrOutputKindV1.OrderIntent && typedTargetNodes.Count == 0)
                issues.Add(new StrategyIrIssueV1("output_target_missing", $"outputs[{action.OutputId}]",
                    "An order intent must causally depend on an exported portfolio target."));

            foreach (var hiddenTarget in typedTargetNodes.Except(exportedTargetNodes, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                issues.Add(new StrategyIrIssueV1("output_target_unexported", $"outputs[{action.OutputId}]",
                    $"Action depends on target node '{hiddenTarget}', which is not exported for review."));

            if (action.Kind == StrategyIrOutputKindV1.OrderIntent &&
                !exportedTargetNodes.Any(ancestors.Contains))
                issues.Add(new StrategyIrIssueV1("output_target_decoupled", $"outputs[{action.OutputId}]",
                    "The exported order intent does not consume any exported portfolio target."));

            if (action.Kind == StrategyIrOutputKindV1.QuoteIntent && signals.Length > 0 &&
                !signals.Any(signal => ancestors.Contains(signal.NodeId)))
                issues.Add(new StrategyIrIssueV1("output_signal_decoupled", $"outputs[{action.OutputId}]",
                    "The exported quote intent does not depend on an exported signal."));
        }

        foreach (var target in targets)
        {
            var targetAncestors = AncestorsOf(target.NodeId, nodes);
            if (signals.Length > 0 && !signals.Any(signal => targetAncestors.Contains(signal.NodeId)))
                issues.Add(new StrategyIrIssueV1("output_signal_decoupled", $"outputs[{target.OutputId}]",
                    "The exported target does not depend on an exported signal."));
            if (actions.Length > 0 && !actions.Any(action => ancestry[action.OutputId].Contains(target.NodeId)))
                issues.Add(new StrategyIrIssueV1("output_target_decoupled", $"outputs[{target.OutputId}]",
                    "The exported target is not consumed by any exported action intent."));
        }

        if (targets.Length + actions.Length > 0)
        {
            foreach (var signal in signals)
            {
                var usedByTarget = targets.Any(target => AncestorsOf(target.NodeId, nodes).Contains(signal.NodeId));
                var usedByAction = actions.Any(action => ancestry[action.OutputId].Contains(signal.NodeId));
                if (!usedByTarget && !usedByAction)
                    issues.Add(new StrategyIrIssueV1("output_signal_decoupled", $"outputs[{signal.OutputId}]",
                        "The exported signal is not part of any exported target or action path."));
            }
        }
    }

    private static HashSet<string> AncestorsOf(
        string root,
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes)
    {
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        if (!nodes.ContainsKey(root)) return ancestors;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!ancestors.Add(current)) continue;
            foreach (var source in nodes[current].InputBindings?.Values ?? [])
                if (!string.IsNullOrWhiteSpace(source) && nodes.ContainsKey(source)) pending.Push(source);
        }
        return ancestors;
    }

    private static void ValidateReachability(
        IReadOnlyList<StrategyIrOutputBindingV1>? outputs,
        IReadOnlyDictionary<string, StrategyIrNodeV1> nodes,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (outputs is null) return;
        var roots = outputs
            .Where(static output => output is not null && !string.IsNullOrWhiteSpace(output.NodeId))
            .Select(static output => output.NodeId);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots.Where(nodes.ContainsKey));
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!reachable.Add(id)) continue;
            foreach (var source in nodes[id].InputBindings?.Values ?? [])
                if (!string.IsNullOrWhiteSpace(source) && nodes.ContainsKey(source)) pending.Push(source);
        }
        foreach (var nodeId in nodes.Keys.Where(id => !reachable.Contains(id)).Order(StringComparer.Ordinal))
            issues.Add(new StrategyIrIssueV1("node_unreachable", $"nodes[{nodeId}]", "Node is not reachable from a named strategy output."));
    }

    private static void ValidateLiteral(
        StrategyLiteralV1? literal,
        string path,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (literal is null)
        {
            issues.Add(new StrategyIrIssueV1("literal_null", path, "Literal cannot be null."));
            return;
        }
        if (!Enum.IsDefined(literal.Kind))
        {
            issues.Add(new StrategyIrIssueV1("literal_kind_invalid", $"{path}.kind", "Unknown literal kind."));
            return;
        }
        var valid = literal.Kind switch
        {
            StrategyLiteralKindV1.Boolean => literal.BooleanValue is not null && literal.IntegerValue is null &&
                                             literal.NumberValue is null && literal.TextValue is null,
            StrategyLiteralKindV1.Integer => literal.BooleanValue is null && literal.IntegerValue is not null &&
                                             literal.NumberValue is null && literal.TextValue is null,
            StrategyLiteralKindV1.Number => literal.BooleanValue is null && literal.IntegerValue is null &&
                                            literal.NumberValue is { } number && double.IsFinite(number) && literal.TextValue is null,
            StrategyLiteralKindV1.Text => literal.BooleanValue is null && literal.IntegerValue is null &&
                                          literal.NumberValue is null && !string.IsNullOrWhiteSpace(literal.TextValue),
            _ => false,
        };
        if (!valid)
            issues.Add(new StrategyIrIssueV1("literal_payload_invalid", path,
                "Exactly one finite payload matching the literal kind is required."));
        else if (literal.Kind == StrategyLiteralKindV1.Integer &&
                 literal.IntegerValue is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L)
            issues.Add(new StrategyIrIssueV1("literal_integer_out_of_range", path,
                "Integer literals must be within the exact RFC-8785 binary64 range (+/- 9007199254740991)."));
    }

    private static void RequireCanonicalOrder(
        IEnumerable<string?> values,
        string path,
        ICollection<StrategyIrIssueV1> issues)
    {
        var materialized = values.Where(static value => value is not null).Cast<string>().ToArray();
        if (materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Length)
            issues.Add(new StrategyIrIssueV1("order_contains_duplicates", path, "Canonical ordered list cannot contain duplicates."));
        if (!materialized.SequenceEqual(materialized.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            issues.Add(new StrategyIrIssueV1("order_noncanonical", path, "Entries must be ordered by ordinal id."));
    }

    private static void RequireText(string value, string path, ICollection<StrategyIrIssueV1> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new StrategyIrIssueV1("text_required", path, "Non-empty text is required."));
            return;
        }
        if (!StringComparer.Ordinal.Equals(value, value.Trim()) || value.Any(char.IsControl))
        {
            issues.Add(new StrategyIrIssueV1(
                "text_not_portable",
                path,
                "Text must be trimmed and cannot contain control characters."));
        }
    }

    private static void RequireIdentifier(string value, string path, ICollection<StrategyIrIssueV1> issues)
    {
        if (!IsIdentifier(value))
            issues.Add(new StrategyIrIssueV1("identifier_invalid", path,
                "Identifier must start with a lowercase ASCII letter and contain lowercase letters, digits, underscore, or hyphen."));
    }

    private static void RequireMaximumLength(
        string? value,
        int maximumLength,
        string path,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (value is { Length: var length } && length > maximumLength)
        {
            issues.Add(new StrategyIrIssueV1(
                "text_too_long",
                path,
                $"Text cannot exceed {maximumLength} characters."));
        }
    }

    private static void RequireNamespacedId(string value, string path, ICollection<StrategyIrIssueV1> issues)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('.') ||
            value.Any(static character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' or '@')))
            issues.Add(new StrategyIrIssueV1("namespaced_id_invalid", path, "A lowercase namespaced semantic id is required."));
    }

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value[0] is >= 'a' and <= 'z' &&
        value.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Describe(StrategyOperatorCatalogReferenceV1 catalog) =>
        $"{catalog.CatalogId}@{catalog.CatalogVersion}#{catalog.CatalogHashSha256}";
}
