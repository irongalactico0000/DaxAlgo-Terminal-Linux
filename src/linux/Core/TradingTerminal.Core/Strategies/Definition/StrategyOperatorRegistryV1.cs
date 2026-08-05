namespace TradingTerminal.Core.Strategies.Definition;

public static class StrategyIrTypeIdsV1
{
    public const string Number = "core.number@1";
    public const string Boolean = "core.boolean@1";
    public const string PortfolioTarget = "portfolio.target@1";
    public const string ExitIntent = "risk.exit_intent@1";
    public const string QuoteIntent = "strategy.quote_intent@1";
    public const string OrderIntent = "strategy.order_intent@1";
}

public sealed record StrategyOperatorBindingContextV1(
    StrategyIrNodeV1 Node,
    IReadOnlyDictionary<string, StrategyValueTypeV1> Inputs,
    IReadOnlyList<DataRequirementV1> DataRequirements);

public sealed record StrategyOperatorBindingResultV1(
    StrategyValueTypeV1? OutputType,
    int MinimumWarmupObservations,
    IReadOnlyList<StrategyIrIssueV1> Issues)
{
    public bool IsValid => OutputType is not null && Issues.Count == 0;
}

public delegate StrategyOperatorBindingResultV1 StrategyOperatorBinderV1(
    StrategyOperatorBindingContextV1 context);

/// <summary>
/// Trusted product metadata for one operator version. The model supplies only the key, named
/// bindings, and literals; this descriptor supplies authoritative ports, type rules, state,
/// placement, and capabilities.
/// </summary>
public sealed class StrategyOperatorDescriptorV1
{
    public StrategyOperatorDescriptorV1(
        StrategyOperatorKeyV1 key,
        IReadOnlyList<string> requiredInputPorts,
        IReadOnlyList<string> optionalInputPorts,
        StrategyOperatorStateKindV1 stateKind,
        StrategyOperatorPlacementV1 placement,
        IReadOnlyList<StrategyCapabilityRequirementV1> capabilities,
        string semanticContractHashSha256,
        StrategyOperatorBinderV1 binder)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.OperatorId);
        if (key.Version <= 0) throw new ArgumentOutOfRangeException(nameof(key));
        ArgumentNullException.ThrowIfNull(requiredInputPorts);
        ArgumentNullException.ThrowIfNull(optionalInputPorts);
        if (requiredInputPorts.Any(string.IsNullOrWhiteSpace) || optionalInputPorts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Operator input-port names must be non-empty.");
        if (requiredInputPorts.Concat(optionalInputPorts).Distinct(StringComparer.Ordinal).Count() !=
            requiredInputPorts.Count + optionalInputPorts.Count)
            throw new ArgumentException("Operator input-port names must be unique.");
        if (!Enum.IsDefined(stateKind)) throw new ArgumentOutOfRangeException(nameof(stateKind));
        if (!Enum.IsDefined(placement)) throw new ArgumentOutOfRangeException(nameof(placement));
        ArgumentNullException.ThrowIfNull(capabilities);
        var capabilitySnapshot = capabilities.ToArray();
        if (capabilitySnapshot.Any(static capability => capability is null ||
                string.IsNullOrWhiteSpace(capability.CapabilityId) ||
                string.IsNullOrWhiteSpace(capability.Reason)))
            throw new ArgumentException("Operator capabilities must have non-empty ids and reasons.", nameof(capabilities));
        if (capabilitySnapshot.Select(static capability => capability.CapabilityId)
            .Distinct(StringComparer.Ordinal).Count() != capabilitySnapshot.Length)
            throw new ArgumentException("Operator capability ids must be unique.", nameof(capabilities));
        if (!IsSha256(semanticContractHashSha256))
            throw new ArgumentException("A lowercase SHA-256 semantic contract hash is required.", nameof(semanticContractHashSha256));
        ArgumentNullException.ThrowIfNull(binder);
        if (binder.GetInvocationList().Length != 1 || !binder.Method.IsStatic || binder.Target is not null)
            throw new ArgumentException(
                $"Operator binders must be a single static method so catalog identity cannot depend on mutable captured state " +
                $"(method={binder.Method.DeclaringType?.FullName}.{binder.Method.Name}, " +
                $"isStatic={binder.Method.IsStatic}, hasTarget={binder.Target is not null}, " +
                $"invocations={binder.GetInvocationList().Length}).",
                nameof(binder));

        Key = new StrategyOperatorKeyV1(key.OperatorId, key.Version);
        RequiredInputPorts = Array.AsReadOnly(requiredInputPorts.ToArray());
        OptionalInputPorts = Array.AsReadOnly(optionalInputPorts.ToArray());
        StateKind = stateKind;
        Placement = placement;
        Capabilities = Array.AsReadOnly(capabilitySnapshot
            .Select(static capability => new StrategyCapabilityRequirementV1(capability.CapabilityId, capability.Reason))
            .ToArray());
        SemanticContractHashSha256 = semanticContractHashSha256;
        BinderIdentityHashSha256 = ComputeBinderIdentityHash(binder);
        Binder = binder;
    }

    public StrategyOperatorKeyV1 Key { get; }
    public IReadOnlyList<string> RequiredInputPorts { get; }
    public IReadOnlyList<string> OptionalInputPorts { get; }
    public StrategyOperatorStateKindV1 StateKind { get; }
    public StrategyOperatorPlacementV1 Placement { get; }
    public IReadOnlyList<StrategyCapabilityRequirementV1> Capabilities { get; }
    public string SemanticContractHashSha256 { get; }
    public string BinderIdentityHashSha256 { get; }
    public StrategyOperatorBinderV1 Binder { get; }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ComputeBinderIdentityHash(StrategyOperatorBinderV1 binder)
    {
        var method = binder.Method;
        var declaringType = method.DeclaringType
            ?? throw new ArgumentException("Operator binder must have a stable declaring type.", nameof(binder));
        return ExecutableStrategyDefinitionCanonicalJson.Hash(new BinderIdentityV1(
            declaringType.Assembly.FullName ?? declaringType.Assembly.GetName().Name ?? string.Empty,
            declaringType.FullName ?? declaringType.Name,
            method.Name,
            method.ReturnType.AssemblyQualifiedName ?? method.ReturnType.FullName ?? method.ReturnType.Name,
            method.GetParameters()
                .Select(static parameter => parameter.ParameterType.AssemblyQualifiedName ??
                    parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
                .ToArray(),
            method.IsGenericMethod
                ? method.GetGenericArguments()
                    .Select(static argument => argument.AssemblyQualifiedName ?? argument.FullName ?? argument.Name)
                    .ToArray()
                : []));
    }

    private sealed record BinderIdentityV1(
        string Assembly,
        string DeclaringType,
        string Method,
        string ReturnType,
        IReadOnlyList<string> ParameterTypes,
        IReadOnlyList<string> GenericArguments);
}

public interface IStrategyOperatorRegistryV1
{
    StrategyOperatorCatalogReferenceV1 Catalog { get; }
    IReadOnlyList<StrategyOperatorKeyV1> Keys { get; }
    bool TryResolve(string operatorId, int version, out StrategyOperatorDescriptorV1 descriptor);
}

/// <summary>
/// Build-time closed world with product-lifetime extension. A definition may use only operator
/// versions installed in this catalog; a new catalog version can add operators without changing
/// the graph schema.
/// </summary>
public sealed class StrategyOperatorRegistryV1 : IStrategyOperatorRegistryV1
{
    private readonly Dictionary<(string Id, int Version), StrategyOperatorDescriptorV1> _operators;

    public StrategyOperatorRegistryV1(
        string catalogId,
        string catalogVersion,
        IEnumerable<StrategyOperatorDescriptorV1> operators) :
        this(CreateDerivedReference(catalogId, catalogVersion), operators, verifyClaimedHash: false)
    {
    }

    /// <summary>
    /// Compatibility overload for persisted catalog references. The supplied hash is a claim to
    /// verify, never the registry identity: descriptor semantics are canonicalized and re-hashed.
    /// </summary>
    public StrategyOperatorRegistryV1(
        StrategyOperatorCatalogReferenceV1 catalog,
        IEnumerable<StrategyOperatorDescriptorV1> operators) :
        this(catalog ?? throw new ArgumentNullException(nameof(catalog)), operators, verifyClaimedHash: true)
    {
    }

    private StrategyOperatorRegistryV1(
        StrategyOperatorCatalogReferenceV1 catalog,
        IEnumerable<StrategyOperatorDescriptorV1> operators,
        bool verifyClaimedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog.CatalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog.CatalogVersion);
        ArgumentNullException.ThrowIfNull(operators);
        var materialized = operators.ToArray();
        _operators = new Dictionary<(string Id, int Version), StrategyOperatorDescriptorV1>();
        foreach (var descriptor in materialized)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            var key = (Id: descriptor.Key.OperatorId, Version: descriptor.Key.Version);
            if (!_operators.TryAdd(key, descriptor))
                throw new ArgumentException($"Duplicate strategy operator {key.Id}@{key.Version}.", nameof(operators));
        }

        var derivedHash = ComputeCatalogHash(catalog.CatalogId, catalog.CatalogVersion, _operators.Values);
        if (verifyClaimedHash && !StringComparer.Ordinal.Equals(catalog.CatalogHashSha256, derivedHash))
            throw new ArgumentException(
                $"Claimed catalog hash '{catalog.CatalogHashSha256}' does not match derived descriptor hash '{derivedHash}'.",
                nameof(catalog));

        Catalog = new StrategyOperatorCatalogReferenceV1(
            catalog.CatalogId,
            catalog.CatalogVersion,
            derivedHash);
        Keys = Array.AsReadOnly(_operators.Values
            .Select(static descriptor => descriptor.Key)
            .OrderBy(static key => key.OperatorId, StringComparer.Ordinal)
            .ThenBy(static key => key.Version)
            .ToArray());
    }

    public StrategyOperatorCatalogReferenceV1 Catalog { get; }
    public IReadOnlyList<StrategyOperatorKeyV1> Keys { get; }

    public bool TryResolve(string operatorId, int version, out StrategyOperatorDescriptorV1 descriptor) =>
        _operators.TryGetValue((operatorId, version), out descriptor!);

    public static StrategyOperatorRegistryV1 CreateDefault() => new(
        "daxalgo.strategy-operators",
        "1.0.0",
        [
            Descriptor("market.quote.mid", [], [], StrategyOperatorStateKindV1.Stateless,
                StrategyOperatorPlacementV1.RestrictedCompute,
                [Capability("data.quote_l1", "Reads a point-in-time L1 quote requirement.")],
                "v1;source=quote-l1;value=(bid+ask)/2;availability=point-in-time;missing=reject",
                BindQuoteMid),

            Descriptor("market.bar.close", [], [], StrategyOperatorStateKindV1.Stateless,
                StrategyOperatorPlacementV1.RestrictedCompute,
                [Capability("data.bar_ohlcv", "Reads a point-in-time OHLCV bar requirement.")],
                "v1;source=bar;value=close;clock=interval-close;availability=point-in-time;missing=reject",
                BindBarClose),

            Descriptor("feature.ema", ["value"], [], StrategyOperatorStateKindV1.Recursive,
                StrategyOperatorPlacementV1.RestrictedCompute,
                [Capability("state.recursive", "Maintains deterministic recursive feature state.")],
                "v1;ema-alpha=2/(period+1);seed=first-value;update=event;missing=reject;reset=run-start;ready=sample-count>=period",
                BindEma),

            Descriptor("feature.rolling_max", ["value"], [], StrategyOperatorStateKindV1.BoundedWindow,
                StrategyOperatorPlacementV1.RestrictedCompute,
                [Capability("state.bounded_window", "Maintains a deterministic bounded observation window.")],
                "v1;window=inclusive-current;missing=reject;reset=run-start;ready=window",
                BindRollingMax),

            Descriptor("time.lag", ["value"], [], StrategyOperatorStateKindV1.BoundedWindow,
                StrategyOperatorPlacementV1.RestrictedCompute,
                [Capability("state.bounded_window", "Retains prior causal observations.")],
                "v1;value=source[t-periods];missing=preserve;reset=run-start;ready=periods",
                BindLag),

            Descriptor("logic.greater_than", ["left", "right"], [], StrategyOperatorStateKindV1.Stateless,
                StrategyOperatorPlacementV1.RestrictedCompute, [],
                "v1;value=left>right;units=equal;axes=identical;missing=propagate", BindGreaterThan),

            Descriptor("cross_section.rank", ["value"], [], StrategyOperatorStateKindV1.Stateless,
                StrategyOperatorPlacementV1.RestrictedCompute,
                [Capability("universe.multi_instrument", "Ranks simultaneous values across instruments.")],
                "v1;axis=instrument;ascending=true;ties=average;missing=exclude;minimum-cardinality=2",
                BindCrossSectionRank),

            Descriptor("portfolio.fixed_quantity", ["decision"], [], StrategyOperatorStateKindV1.Stateless,
                StrategyOperatorPlacementV1.HostPortfolio,
                [Capability("portfolio.fixed_quantity", "Converts a decision into a signed quantity target.")],
                "v1;true=when_true;false=when_false;unit=position.quantity;host-owned=true",
                BindFixedQuantityTarget),

            Descriptor("portfolio.rank_long_short", ["rank"], [], StrategyOperatorStateKindV1.Stateless,
                StrategyOperatorPlacementV1.HostPortfolio,
                [
                    Capability("portfolio.rank_long_short", "Constructs long and short targets from cross-sectional ranks."),
                    Capability("universe.multi_instrument", "Requires a multi-instrument portfolio view."),
                ],
                "v1;long=top-fraction;short=bottom-fraction;gross-quantity=fixed;ties=instrument-key;host-owned=true",
                BindRankLongShortTarget),

            Descriptor("risk.trailing_fraction", ["price", "target"], [], StrategyOperatorStateKindV1.Recursive,
                StrategyOperatorPlacementV1.HostRisk,
                [Capability("risk.trailing_fraction", "Maintains a host-owned trailing protective exit intent.")],
                "v1;trail=fraction;anchor=favorable-extreme-since-position-open;reset=flat-or-reversal;host-owned=true",
                BindTrailingFraction),

            Descriptor("execution.market", ["target"], ["exit"], StrategyOperatorStateKindV1.Stateless,
                StrategyOperatorPlacementV1.HostExecutionIntent,
                [Capability("execution.market", "Requests host conversion of target deltas into market-order intents.")],
                "v1;intent=market;quantity=target-current-position;tif=declared;no-adapter-authority=true",
                BindMarketExecution),
        ]);

    private static StrategyOperatorDescriptorV1 Descriptor(
        string id,
        IReadOnlyList<string> requiredPorts,
        IReadOnlyList<string> optionalPorts,
        StrategyOperatorStateKindV1 state,
        StrategyOperatorPlacementV1 placement,
        IReadOnlyList<StrategyCapabilityRequirementV1> capabilities,
        string semanticContract,
        StrategyOperatorBinderV1 binder) =>
        new(
            new StrategyOperatorKeyV1(id, 1),
            requiredPorts,
            optionalPorts,
            state,
            placement,
            capabilities,
            ExecutableStrategyDefinitionCanonicalJson.Hash(new OperatorSemanticContractV1(semanticContract)),
            binder);

    private static string ComputeCatalogHash(
        string catalogId,
        string catalogVersion,
        IEnumerable<StrategyOperatorDescriptorV1> descriptors) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(new OperatorCatalogManifestV1(
            catalogId,
            catalogVersion,
            descriptors
                .OrderBy(static descriptor => descriptor.Key.OperatorId, StringComparer.Ordinal)
                .ThenBy(static descriptor => descriptor.Key.Version)
                .Select(static descriptor => new OperatorCatalogEntryV1(
                    descriptor.Key.OperatorId,
                    descriptor.Key.Version,
                    descriptor.RequiredInputPorts.Order(StringComparer.Ordinal).ToArray(),
                    descriptor.OptionalInputPorts.Order(StringComparer.Ordinal).ToArray(),
                    descriptor.StateKind,
                    descriptor.Placement,
                    descriptor.Capabilities
                        .OrderBy(static capability => capability.CapabilityId, StringComparer.Ordinal)
                        .ThenBy(static capability => capability.Reason, StringComparer.Ordinal)
                        .ToArray(),
                    descriptor.SemanticContractHashSha256,
                    descriptor.BinderIdentityHashSha256))
                .ToArray()));

    private static StrategyOperatorCatalogReferenceV1 CreateDerivedReference(
        string catalogId,
        string catalogVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogVersion);
        return new StrategyOperatorCatalogReferenceV1(catalogId, catalogVersion, string.Empty);
    }

    private sealed record OperatorSemanticContractV1(string Contract);

    private sealed record OperatorCatalogManifestV1(
        string CatalogId,
        string CatalogVersion,
        IReadOnlyList<OperatorCatalogEntryV1> Operators);

    private sealed record OperatorCatalogEntryV1(
        string OperatorId,
        int OperatorVersion,
        IReadOnlyList<string> RequiredInputPorts,
        IReadOnlyList<string> OptionalInputPorts,
        StrategyOperatorStateKindV1 StateKind,
        StrategyOperatorPlacementV1 Placement,
        IReadOnlyList<StrategyCapabilityRequirementV1> Capabilities,
        string SemanticContractHashSha256,
        string BinderIdentityHashSha256);

    private static StrategyCapabilityRequirementV1 Capability(string id, string reason) => new(id, reason);

    private static StrategyOperatorBindingResultV1 BindQuoteMid(StrategyOperatorBindingContextV1 context) =>
        BindMarketSource(context, TradeIrDataKindV1.QuoteL1, "market.price");

    private static StrategyOperatorBindingResultV1 BindBarClose(StrategyOperatorBindingContextV1 context) =>
        BindMarketSource(context, TradeIrDataKindV1.Bar, "market.price");

    private static StrategyOperatorBindingResultV1 BindEma(StrategyOperatorBindingContextV1 context) =>
        BindWindow(context, "value", "period", minimum: 2);

    private static StrategyOperatorBindingResultV1 BindRollingMax(StrategyOperatorBindingContextV1 context) =>
        BindWindow(context, "value", "window", minimum: 1);

    private static StrategyOperatorBindingResultV1 BindLag(StrategyOperatorBindingContextV1 context) =>
        BindWindow(context, "value", "periods", minimum: 1);

    private static StrategyOperatorBindingResultV1 BindMarketSource(
        StrategyOperatorBindingContextV1 context,
        TradeIrDataKindV1 requiredDataKind,
        string unitTag)
    {
        var issues = CheckParameters(context, ["requirement_id"]);
        if (!TryText(context, "requirement_id", issues, out var requirementId)) return Invalid(issues);
        var requirement = context.DataRequirements.FirstOrDefault(item =>
            item is not null && StringComparer.Ordinal.Equals(item.RequirementId, requirementId));
        if (requirement is null)
        {
            issues.Add(Issue(context, "data_requirement_missing", "parameters.requirement_id",
                $"Data requirement '{requirementId}' was not declared."));
            return Invalid(issues);
        }
        if (requirement.DataKind != requiredDataKind)
        {
            issues.Add(Issue(context, "data_type_mismatch", "parameters.requirement_id",
                $"Operator requires '{requiredDataKind}', but '{requirementId}' declares '{requirement.DataKind}'."));
            return Invalid(issues);
        }

        return Valid(new StrategyValueTypeV1(
            StrategyIrTypeIdsV1.Number,
            AxesFor(requirement),
            unitTag,
            StrategyValueAvailabilityV1.Ready,
            Nullable: false));
    }

    private static StrategyOperatorBindingResultV1 BindWindow(
        StrategyOperatorBindingContextV1 context,
        string inputPort,
        string parameterName,
        long minimum)
    {
        var issues = CheckParameters(context, [parameterName]);
        var input = RequireInput(context, inputPort, StrategyIrTypeIdsV1.Number, issues);
        var hasPeriod = TryInteger(context, parameterName, issues, out var period);
        if (hasPeriod && (period < minimum || period > 1_000_000))
            issues.Add(Issue(context, "parameter_range", $"parameters.{parameterName}",
                $"Parameter must be between {minimum} and 1000000."));
        if (input is null || issues.Count > 0) return Invalid(issues);
        return Valid(input with { Availability = StrategyValueAvailabilityV1.Warmup }, checked((int)period));
    }

    private static StrategyOperatorBindingResultV1 BindGreaterThan(StrategyOperatorBindingContextV1 context)
    {
        var issues = CheckParameters(context, []);
        var left = RequireInput(context, "left", StrategyIrTypeIdsV1.Number, issues);
        var right = RequireInput(context, "right", StrategyIrTypeIdsV1.Number, issues);
        if (left is not null && right is not null)
        {
            if (!left.Axes.SequenceEqual(right.Axes))
                issues.Add(Issue(context, "axis_mismatch", "inputBindings", "Numeric input axes must match exactly."));
            if (!StringComparer.Ordinal.Equals(left.UnitTag, right.UnitTag))
                issues.Add(Issue(context, "unit_mismatch", "inputBindings", "Numeric input units must match exactly."));
        }
        if (left is null || right is null || issues.Count > 0) return Invalid(issues);
        return Valid(new StrategyValueTypeV1(
            StrategyIrTypeIdsV1.Boolean,
            left.Axes,
            "unitless",
            MergeAvailability([left, right]),
            left.Nullable || right.Nullable));
    }

    private static StrategyOperatorBindingResultV1 BindCrossSectionRank(StrategyOperatorBindingContextV1 context)
    {
        var issues = CheckParameters(context, []);
        var input = RequireInput(context, "value", StrategyIrTypeIdsV1.Number, issues);
        var instrumentAxis = input?.Axes.FirstOrDefault(static axis => axis.AxisId == "instrument");
        if (input is not null && instrumentAxis is null)
            issues.Add(Issue(context, "axis_mismatch", "inputBindings.value", "Cross-sectional rank requires the instrument axis."));
        else if (instrumentAxis?.Cardinality is not >= 2)
            issues.Add(Issue(context, "axis_cardinality", "inputBindings.value", "Cross-sectional rank requires at least two instruments."));
        return input is null || issues.Count > 0
            ? Invalid(issues)
            : Valid(input with { UnitTag = "unitless" });
    }

    private static StrategyOperatorBindingResultV1 BindFixedQuantityTarget(StrategyOperatorBindingContextV1 context)
    {
        var issues = CheckParameters(context, ["when_false", "when_true"]);
        var decision = RequireInput(context, "decision", StrategyIrTypeIdsV1.Boolean, issues);
        TryNumber(context, "when_true", issues, out _);
        TryNumber(context, "when_false", issues, out _);
        if (decision is null || issues.Count > 0) return Invalid(issues);
        return Valid(new StrategyValueTypeV1(
            StrategyIrTypeIdsV1.PortfolioTarget,
            decision.Axes,
            "position.quantity",
            decision.Availability,
            Nullable: false));
    }

    private static StrategyOperatorBindingResultV1 BindRankLongShortTarget(StrategyOperatorBindingContextV1 context)
    {
        var issues = CheckParameters(context, ["gross_quantity", "long_fraction", "short_fraction"]);
        var rank = RequireInput(context, "rank", StrategyIrTypeIdsV1.Number, issues);
        if (rank is not null && !rank.Axes.Any(static axis => axis.AxisId == "instrument"))
            issues.Add(Issue(context, "axis_mismatch", "inputBindings.rank", "Rank target requires the instrument axis."));
        var hasLong = TryNumber(context, "long_fraction", issues, out var longFraction);
        var hasShort = TryNumber(context, "short_fraction", issues, out var shortFraction);
        var hasGross = TryNumber(context, "gross_quantity", issues, out var grossQuantity);
        if (hasLong && (longFraction <= 0d || longFraction > 0.5d))
            issues.Add(Issue(context, "parameter_range", "parameters.long_fraction", "Long fraction must be in (0, 0.5]."));
        if (hasShort && (shortFraction <= 0d || shortFraction > 0.5d))
            issues.Add(Issue(context, "parameter_range", "parameters.short_fraction", "Short fraction must be in (0, 0.5]."));
        if (hasGross && grossQuantity <= 0d)
            issues.Add(Issue(context, "parameter_range", "parameters.gross_quantity", "Gross quantity must be positive."));
        if (rank is null || issues.Count > 0) return Invalid(issues);
        return Valid(new StrategyValueTypeV1(
            StrategyIrTypeIdsV1.PortfolioTarget,
            rank.Axes,
            "position.quantity",
            rank.Availability,
            Nullable: false));
    }

    private static StrategyOperatorBindingResultV1 BindTrailingFraction(StrategyOperatorBindingContextV1 context)
    {
        var issues = CheckParameters(context, ["fraction"]);
        var price = RequireInput(context, "price", StrategyIrTypeIdsV1.Number, issues);
        var target = RequireInput(context, "target", StrategyIrTypeIdsV1.PortfolioTarget, issues);
        if (price is not null && price.UnitTag != "market.price")
            issues.Add(Issue(context, "unit_mismatch", "inputBindings.price", "Trailing exit requires market.price."));
        if (price is not null && target is not null &&
            !price.Axes.SequenceEqual(target.Axes))
            issues.Add(Issue(context, "axis_mismatch", "inputBindings", "Price and target axes must match."));
        var hasFraction = TryNumber(context, "fraction", issues, out var fraction);
        if (hasFraction && (fraction <= 0d || fraction >= 1d))
            issues.Add(Issue(context, "parameter_range", "parameters.fraction", "Trailing fraction must be in (0, 1)."));
        if (price is null || target is null || issues.Count > 0) return Invalid(issues);
        return Valid(new StrategyValueTypeV1(
            StrategyIrTypeIdsV1.ExitIntent,
            target.Axes,
            "unitless",
            MergeAvailability([price, target]),
            Nullable: false), minimumWarmup: 1);
    }

    private static StrategyOperatorBindingResultV1 BindMarketExecution(StrategyOperatorBindingContextV1 context)
    {
        var issues = CheckParameters(context, ["time_in_force"]);
        var target = RequireInput(context, "target", StrategyIrTypeIdsV1.PortfolioTarget, issues);
        if (context.Inputs.TryGetValue("exit", out var exit) && exit.TypeId != StrategyIrTypeIdsV1.ExitIntent)
            issues.Add(Issue(context, "type_mismatch", "inputBindings.exit", "Exit binding must produce an exit intent."));
        var hasTimeInForce = TryText(context, "time_in_force", issues, out var timeInForce);
        if (hasTimeInForce && timeInForce is not ("day" or "good_til_cancelled" or "immediate_or_cancel"))
            issues.Add(Issue(context, "parameter_value", "parameters.time_in_force", "Unsupported time-in-force value."));
        if (target is null || issues.Count > 0) return Invalid(issues);
        return Valid(new StrategyValueTypeV1(
            StrategyIrTypeIdsV1.OrderIntent,
            target.Axes,
            "unitless",
            MergeAvailability(context.Inputs.Values),
            Nullable: false));
    }

    private static List<StrategyIrIssueV1> CheckParameters(
        StrategyOperatorBindingContextV1 context,
        IReadOnlyCollection<string> expected)
    {
        var issues = new List<StrategyIrIssueV1>();
        if (context.Node.Parameters is null)
        {
            issues.Add(Issue(context, "parameters_missing", "parameters", "Parameter map is required."));
            return issues;
        }
        foreach (var name in context.Node.Parameters.Keys)
            if (!expected.Contains(name, StringComparer.Ordinal))
                issues.Add(Issue(context, "parameter_unexpected", $"parameters.{name}", $"Unexpected parameter '{name}'."));
        foreach (var name in expected)
            if (!context.Node.Parameters.ContainsKey(name))
                issues.Add(Issue(context, "parameter_missing", $"parameters.{name}", $"Required parameter '{name}' is missing."));
        return issues;
    }

    private static StrategyValueTypeV1? RequireInput(
        StrategyOperatorBindingContextV1 context,
        string port,
        string typeId,
        ICollection<StrategyIrIssueV1> issues)
    {
        if (!context.Inputs.TryGetValue(port, out var input)) return null;
        if (!StringComparer.Ordinal.Equals(input.TypeId, typeId))
        {
            issues.Add(Issue(context, "type_mismatch", $"inputBindings.{port}",
                $"Port '{port}' requires '{typeId}', but received '{input.TypeId}'."));
            return null;
        }
        return input;
    }

    private static bool TryText(
        StrategyOperatorBindingContextV1 context,
        string name,
        ICollection<StrategyIrIssueV1> issues,
        out string value)
    {
        value = string.Empty;
        if (context.Node.Parameters is null ||
            !context.Node.Parameters.TryGetValue(name, out var literal) ||
            literal is null)
            return false;
        if (literal.Kind != StrategyLiteralKindV1.Text || string.IsNullOrWhiteSpace(literal.TextValue))
        {
            issues.Add(Issue(context, "parameter_type", $"parameters.{name}", "Parameter must be non-empty text."));
            return false;
        }
        value = literal.TextValue;
        return true;
    }

    private static bool TryInteger(
        StrategyOperatorBindingContextV1 context,
        string name,
        ICollection<StrategyIrIssueV1> issues,
        out long value)
    {
        value = 0;
        if (context.Node.Parameters is null ||
            !context.Node.Parameters.TryGetValue(name, out var literal) ||
            literal is null)
            return false;
        if (literal.Kind != StrategyLiteralKindV1.Integer || literal.IntegerValue is null)
        {
            issues.Add(Issue(context, "parameter_type", $"parameters.{name}", "Parameter must be an integer."));
            return false;
        }
        value = literal.IntegerValue.Value;
        return true;
    }

    private static bool TryNumber(
        StrategyOperatorBindingContextV1 context,
        string name,
        ICollection<StrategyIrIssueV1> issues,
        out double value)
    {
        value = 0d;
        if (context.Node.Parameters is null ||
            !context.Node.Parameters.TryGetValue(name, out var literal) ||
            literal is null)
            return false;
        if (literal.Kind == StrategyLiteralKindV1.Integer && literal.IntegerValue is { } integer)
        {
            value = integer;
            return true;
        }
        if (literal.Kind == StrategyLiteralKindV1.Number && literal.NumberValue is { } number && double.IsFinite(number))
        {
            value = number;
            return true;
        }
        issues.Add(Issue(context, "parameter_type", $"parameters.{name}", "Parameter must be a finite number."));
        return false;
    }

    private static StrategyValueAvailabilityV1 MergeAvailability(IEnumerable<StrategyValueTypeV1> inputs)
    {
        var values = inputs.Select(static input => input.Availability).ToArray();
        if (values.Contains(StrategyValueAvailabilityV1.MaybeMissing)) return StrategyValueAvailabilityV1.MaybeMissing;
        return values.Contains(StrategyValueAvailabilityV1.Warmup)
            ? StrategyValueAvailabilityV1.Warmup
            : StrategyValueAvailabilityV1.Ready;
    }

    private static StrategyOperatorBindingResultV1 Valid(
        StrategyValueTypeV1 output,
        int minimumWarmup = 0) => new(output, minimumWarmup, []);

    private static StrategyOperatorBindingResultV1 Invalid(IReadOnlyList<StrategyIrIssueV1> issues) =>
        new(null, 0, issues);

    private static StrategyIrIssueV1 Issue(
        StrategyOperatorBindingContextV1 context,
        string code,
        string suffix,
        string message) => new(code, $"nodes[{context.Node.NodeId}].{suffix}", message);

    private static IReadOnlyList<StrategyAxisV1> AxesFor(DataRequirementV1 requirement)
    {
        var references = requirement.InstrumentSelector.References
            .OrderBy(static reference => reference.InstrumentKey, StringComparer.Ordinal)
            .ThenBy(static reference => reference.Symbol, StringComparer.Ordinal)
            .ThenBy(static reference => reference.Venue, StringComparer.Ordinal)
            .ThenBy(static reference => reference.Currency, StringComparer.Ordinal)
            .ToArray();
        var instrumentDomain = ExecutableStrategyDefinitionCanonicalJson.Hash(
            new InstrumentAxisDomainV1(references));
        var timeDomain = ExecutableStrategyDefinitionCanonicalJson.Hash(
            new TimeAxisDomainV1(
                requirement.DataKind,
                requirement.EventSchema.SchemaId,
                requirement.EventSchema.SchemaVersion,
                requirement.EventSchema.SchemaHashSha256,
                requirement.TemporalSemantics));
        return
        [
            new StrategyAxisV1("instrument", $"sha256:{instrumentDomain}", references.Length),
            new StrategyAxisV1("time", $"sha256:{timeDomain}", Cardinality: null),
        ];
    }

    private sealed record InstrumentAxisDomainV1(
        IReadOnlyList<SourceIndependentInstrumentRef> Instruments);

    private sealed record TimeAxisDomainV1(
        TradeIrDataKindV1 DataKind,
        string SchemaId,
        int SchemaVersion,
        string SchemaHashSha256,
        DataTemporalSemanticsV1 TemporalSemantics);
}
