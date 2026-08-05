using System.Reflection;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Definition;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class TradeIrCoreContractsTests
{
    private static readonly StrategyValueTypeV1 Score = new(
        StrategyIrTypeIdsV1.Number,
        [
            new StrategyAxisV1("instrument", "test.instrument-universe", Cardinality: null),
            new StrategyAxisV1("time", "test.event-time", Cardinality: null),
        ],
        "unitless",
        StrategyValueAvailabilityV1.Ready,
        Nullable: false);

    private static readonly StrategyValueTypeV1 PortfolioTarget = new(
        StrategyIrTypeIdsV1.PortfolioTarget,
        [new StrategyAxisV1("instrument", "test.instrument-universe", Cardinality: null)],
        "position.quantity",
        StrategyValueAvailabilityV1.Ready,
        Nullable: false);

    [Fact]
    public void Module_union_has_one_canonical_graph_and_two_content_addressed_extension_lanes()
    {
        var registry = StrategyOperatorRegistryV1.CreateDefault();
        var definition = SignalOnlyDefinition(registry);
        var graph = new OperatorGraphModuleV1(TradeIrModuleV1.CurrentSchemaVersion, "safe-graph", definition);
        var runtime = SafeRuntime();
        var outputs = new[]
        {
            new StrategyModuleOutputV1("signal", StrategyIrOutputKindV1.Signal, Score),
        };
        var csharp = new CSharpModuleV1(
            TradeIrModuleV1.CurrentSchemaVersion,
            "custom-csharp",
            [new StrategyModuleInputV1("features", Score)],
            outputs,
            Address('a'),
            "13.0",
            "Strategies.Custom.Evaluate",
            runtime);
        var model = new ModelArtifactModuleV1(
            TradeIrModuleV1.CurrentSchemaVersion,
            "model-alpha",
            [new StrategyModuleInputV1("features", Score)],
            outputs,
            Address('b'),
            "onnx",
            "score",
            new string('c', 64),
            new string('d', 64),
            runtime);

        graph.Definition.Should().BeSameAs(definition);
        csharp.SourceAddress.Should().Be(Address('a'));
        model.ArtifactAddress.Should().Be(Address('b'));
        TradeIrModuleValidatorV1.Validate(graph, registry).IsValid.Should().BeTrue();
        TradeIrModuleValidatorV1.Validate(csharp, registry).IsValid.Should().BeTrue();
        TradeIrModuleValidatorV1.Validate(model, registry).IsValid.Should().BeTrue();

        typeof(OperatorGraphModuleV1).GetProperties().Select(static property => property.Name)
            .Should().NotContain(["Nodes", "Operators", "OperatorSpecs"]);
    }

    [Fact]
    public void The_only_export_kinds_are_signal_target_quote_intent_and_order_intent()
    {
        Enum.GetValues<StrategyIrOutputKindV1>().Should().Equal(
            StrategyIrOutputKindV1.Signal,
            StrategyIrOutputKindV1.Target,
            StrategyIrOutputKindV1.QuoteIntent,
            StrategyIrOutputKindV1.OrderIntent);
    }

    [Fact]
    public void Extension_admission_rejects_privileged_or_nondeterministic_runtime_requests()
    {
        var registry = StrategyOperatorRegistryV1.CreateDefault();
        var unsafeRuntime = SafeRuntime() with
        {
            Determinism = StrategyModuleDeterminismV1.ExternallyDetermined,
            RequiresIsolatedProcess = false,
            AllowNetwork = true,
        };
        var module = new CSharpModuleV1(
            TradeIrModuleV1.CurrentSchemaVersion,
            "unsafe-module",
            [],
            [new StrategyModuleOutputV1("signal", StrategyIrOutputKindV1.Signal, Score)],
            Address('a'),
            "13.0",
            "Unsafe.Evaluate",
            unsafeRuntime);

        TradeIrModuleValidatorV1.Validate(module, registry).Issues.Select(static issue => issue.Code)
            .Should().Contain(["MODULE_RUNTIME_UNSAFE", "MODULE_NONDETERMINISTIC"]);
    }

    [Fact]
    public void Extension_interface_accepts_target_only_output_without_forcing_a_signal()
    {
        var module = new CSharpModuleV1(
            TradeIrModuleV1.CurrentSchemaVersion,
            "target-module",
            [],
            [new StrategyModuleOutputV1("target", StrategyIrOutputKindV1.Target, PortfolioTarget)],
            Address('a'),
            "13.0",
            "Targets.Evaluate",
            SafeRuntime());

        TradeIrModuleValidatorV1.Validate(module, StrategyOperatorRegistryV1.CreateDefault())
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Extension_interface_rejects_malformed_types_and_output_kind_type_mismatch()
    {
        var malformed = new StrategyValueTypeV1(
            "unversioned",
            [
                new StrategyAxisV1("instrument", "", Cardinality: 0),
                new StrategyAxisV1("instrument", "duplicate", Cardinality: 1),
            ],
            "",
            (StrategyValueAvailabilityV1)999,
            Nullable: false);
        var module = new ModelArtifactModuleV1(
            TradeIrModuleV1.CurrentSchemaVersion,
            "bad-types",
            [new StrategyModuleInputV1("features", malformed)],
            [new StrategyModuleOutputV1("signal", StrategyIrOutputKindV1.Signal, PortfolioTarget)],
            Address('b'),
            "onnx",
            "score",
            new string('c', 64),
            new string('d', 64),
            SafeRuntime());

        var issues = TradeIrModuleValidatorV1.Validate(module, StrategyOperatorRegistryV1.CreateDefault()).Issues;

        issues.Should().Contain(issue => issue.Code == "MODULE_VALUE_TYPE_INVALID");
        issues.Should().Contain(issue => issue.Code == "MODULE_OUTPUT_TYPE_MISMATCH");
    }

    [Fact]
    public void Extension_interface_requires_canonical_order_and_full_intent_type_semantics()
    {
        var wrongIntent = new StrategyValueTypeV1(
            StrategyIrTypeIdsV1.OrderIntent,
            [],
            "position.quantity",
            StrategyValueAvailabilityV1.Ready,
            Nullable: true);
        var module = new CSharpModuleV1(
            TradeIrModuleV1.CurrentSchemaVersion,
            "noncanonical-interface",
            [
                new StrategyModuleInputV1("z_features", Score),
                new StrategyModuleInputV1("a_features", Score),
            ],
            [
                new StrategyModuleOutputV1("z_signal", StrategyIrOutputKindV1.Signal, Score),
                new StrategyModuleOutputV1("a_order", StrategyIrOutputKindV1.OrderIntent, wrongIntent),
            ],
            Address('a'),
            "13.0",
            "Intent.Evaluate",
            SafeRuntime());

        var issues = TradeIrModuleValidatorV1.Validate(module, StrategyOperatorRegistryV1.CreateDefault()).Issues;

        issues.Should().Contain(issue => issue.Code == "MODULE_INTERFACE_NONCANONICAL" && issue.Path == "inputs");
        issues.Should().Contain(issue => issue.Code == "MODULE_INTERFACE_NONCANONICAL" && issue.Path == "outputs");
        issues.Should().Contain(issue => issue.Code == "MODULE_OUTPUT_TYPE_MISMATCH" &&
            issue.Path == "outputs[a_order].valueType");
    }

    [Fact]
    public void Public_module_contract_has_no_risk_command_adapter_broker_or_credential_handle()
    {
        var contractTypes = new[]
        {
            typeof(TradeIrModuleV1),
            typeof(OperatorGraphModuleV1),
            typeof(CSharpModuleV1),
            typeof(ModelArtifactModuleV1),
            typeof(StrategyModuleInputV1),
            typeof(StrategyModuleOutputV1),
            typeof(StrategyIntermediateRepresentationV1),
            typeof(StrategyIrNodeV1),
            typeof(StrategyIrOutputBindingV1),
        };

        var propertyNames = contractTypes
            .SelectMany(static type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Select(static property => property.Name)
            .ToArray();
        propertyNames.Should().NotContain(name =>
            name.Contains("RiskDecision", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ExecutionCommand", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Adapter", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Broker", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Submission", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Handle", StringComparison.OrdinalIgnoreCase));
    }

    private static StrategyIntermediateRepresentationV1 SignalOnlyDefinition(StrategyOperatorRegistryV1 registry)
    {
        var instrument = new SourceIndependentInstrumentRef(
            "equity/xnas/AAPL",
            TradingTerminal.Core.Domain.AssetClass.Equity,
            "AAPL",
            "XNAS",
            "USD");
        var requirement = new DataRequirementV1(
            "quotes",
            TradeIrDataKindV1.QuoteL1,
            new SourceIndependentInstrumentSelectorV1([instrument]),
            new CanonicalEventSchemaV1("canonical.quote-l1", 1, new string('e', 64), ["ask", "bid"]),
            new DataTemporalSemanticsV1(
                TradeIrEventTimeBasisV1.OccurredAtUtc,
                TradeIrTimestampPrecisionV1.Microseconds,
                TradeIrEventOrderingV1.EventTimeThenSourceSequence,
                null,
                RequireAuthoritativeEventTime: true,
                RequirePointInTimeAvailability: true),
            TradeIrNormalizationPolicyV1.RawUnadjusted,
            TradeIrMissingDataPolicyV1.Reject,
            TradeIrRevisionPolicyV1.LatestAvailableAtDecisionTime);
        return new StrategyIntermediateRepresentationV1(
            StrategyIntermediateRepresentationV1.CurrentSchemaVersion,
            "mid-signal",
            "1.0.0",
            registry.Catalog,
            StrategyClockKindV1.EventTime,
            [requirement],
            [new StrategyIrNodeV1(
                "price",
                "market.quote.mid",
                1,
                new Dictionary<string, string>(),
                new Dictionary<string, StrategyLiteralV1>
                {
                    ["requirement_id"] = StrategyLiteralV1.FromText("quotes"),
                })],
            [new StrategyIrOutputBindingV1("signal", StrategyIrOutputKindV1.Signal, "price")],
            FlattenOnEnd: false);
    }

    private static StrategyModuleRuntimeContractV1 SafeRuntime() => new(
        "strategy-module-abi/v1",
        Address('f'),
        StrategyModuleDeterminismV1.Deterministic,
        RandomSeed: null,
        RequiresIsolatedProcess: true,
        AllowNetwork: false,
        AllowFileSystem: false,
        AllowCredentials: false,
        AllowInterprocessCommunication: false,
        AllowProcessCreation: false);

    private static TradeIrContentAddressV1 Address(char character) => new(
        TradeIrDigestAlgorithmV1.Sha256,
        new string(character, 64));
}
