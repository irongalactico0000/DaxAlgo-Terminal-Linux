using FluentAssertions;
using TradingTerminal.Backtest.Engine.TradeIr;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Definition;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

public sealed class BacktestTradeIrTargetV1Tests
{
    [Fact]
    public void Closed_profile_declares_exact_surface_and_exact_match_is_admitted()
    {
        var fixture = CreateFixture();

        fixture.Target.Profile.SupportedOperators.Should().Equal(
            new StrategyOperatorKeyV1("execution.market", 1),
            new StrategyOperatorKeyV1("feature.ema", 1),
            new StrategyOperatorKeyV1("logic.greater_than", 1),
            new StrategyOperatorKeyV1("market.quote.mid", 1),
            new StrategyOperatorKeyV1("portfolio.fixed_quantity", 1),
            new StrategyOperatorKeyV1("risk.trailing_fraction", 1));
        fixture.Target.Profile.SupportedCapabilities.Should().Equal(
            "data.quote_l1",
            "execution.market",
            "lifecycle.flatten_on_end",
            "portfolio.fixed_quantity",
            "risk.trailing_fraction",
            "state.recursive");
        fixture.Target.Profile.SupportedPlacements.Should().Equal(
            StrategyOperatorPlacementV1.RestrictedCompute,
            StrategyOperatorPlacementV1.HostPortfolio,
            StrategyOperatorPlacementV1.HostRisk,
            StrategyOperatorPlacementV1.HostExecutionIntent);
        fixture.Target.Profile.OperatorCatalog.Should().Be(StrategyOperatorRegistryV1.CreateDefault().Catalog);

        var direct = StrategyCompilationAdmissionV1.Assess(
            fixture.Definition,
            fixture.Target.Registry,
            fixture.Target.Profile,
            [fixture.Capability],
            [fixture.Binding]);
        var engineOwned = fixture.Target.Assess(
            fixture.Definition,
            fixture.Pins,
            [fixture.Capability],
            [fixture.Binding]);

        direct.CanCompile.Should().BeTrue();
        engineOwned.CanCompile.Should().BeTrue();
        engineOwned.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Catalog_operator_and_capability_drift_fail_before_compilation()
    {
        var fixture = CreateFixture();
        var catalogDrift = fixture.Definition with
        {
            OperatorCatalog = fixture.Definition.OperatorCatalog with
            {
                CatalogHashSha256 = new string('0', 64),
            },
        };
        var operatorDrift = ReplaceNode(fixture.Definition, "fast", node => node with
        {
            OperatorId = "feature.rolling_max",
            Parameters = Parameters(("window", StrategyLiteralV1.FromInteger(4))),
        });

        var catalogResult = fixture.Target.Assess(
            catalogDrift,
            fixture.Pins,
            [fixture.Capability],
            [fixture.Binding]);
        var operatorResult = fixture.Target.Assess(
            operatorDrift,
            fixture.Pins,
            [fixture.Capability],
            [fixture.Binding]);

        catalogResult.CanCompile.Should().BeFalse();
        catalogResult.SemanticValidation.Issues.Should().Contain(issue => issue.Code == "catalog_mismatch");
        operatorResult.CanCompile.Should().BeFalse();
        operatorResult.Issues.Should().Contain(issue => issue.Code == "TARGET.target_operator_unsupported");
        operatorResult.Issues.Should().Contain(issue =>
            issue.Code == "TARGET.target_capability_unsupported" &&
            issue.Message.Contains("state.bounded_window", StringComparison.Ordinal));
    }

    [Fact]
    public void Compiler_runtime_and_execution_host_artifact_drift_fail_independently()
    {
        var fixture = CreateFixture();
        var compilerDrift = fixture.Pins with
        {
            Compiler = fixture.Pins.Compiler with { ArtifactHashSha256 = new string('a', 64) },
        };
        var runtimeDrift = fixture.Pins with
        {
            Runtime = fixture.Pins.Runtime with { ArtifactHashSha256 = new string('b', 64) },
        };
        var executionHostDrift = fixture.Pins with
        {
            ExecutionHost = fixture.Pins.ExecutionHost with { ArtifactHashSha256 = new string('f', 64) },
        };

        AssertArtifactDrift(fixture, compilerDrift,
            BacktestTradeIrAdmissionIssueCodesV1.CompilerIdentityMismatch);
        AssertArtifactDrift(fixture, runtimeDrift,
            BacktestTradeIrAdmissionIssueCodesV1.RuntimeIdentityMismatch);
        AssertArtifactDrift(fixture, executionHostDrift,
            BacktestTradeIrAdmissionIssueCodesV1.ExecutionHostIdentityMismatch);
    }

    [Fact]
    public void Factory_rejects_arbitrary_artifact_semantics_and_exposes_no_module_admission()
    {
        var pins = ArtifactPins() with
        {
            Runtime = ArtifactPins().Runtime with { ArtifactId = "third-party.runtime" },
        };

        var create = () => BacktestTradeIrTargetV1.Create(pins);

        create.Should().Throw<ArgumentException>().WithMessage("*daxalgo.tradeir.pure-evaluator@1.0.0*");
        typeof(BacktestTradeIrTargetV1).GetMethods()
            .Where(static method => method.Name == nameof(BacktestTradeIrTargetV1.Assess))
            .SelectMany(static method => method.GetParameters())
            .Select(static parameter => parameter.ParameterType)
            .Should().NotContain(type => typeof(TradeIrModuleV1).IsAssignableFrom(type));
    }

    private static void AssertArtifactDrift(
        Fixture fixture,
        BacktestTradeIrArtifactSetV1 loaded,
        string expectedCode)
    {
        var result = fixture.Target.Assess(
            fixture.Definition,
            loaded,
            [fixture.Capability],
            [fixture.Binding]);

        result.CanCompile.Should().BeFalse();
        result.Issues.Should().ContainSingle(issue => issue.Code == expectedCode);
    }

    internal static Fixture CreateFixture()
    {
        var pins = ArtifactPins();
        var target = BacktestTradeIrTargetV1.Create(pins);
        var instrument = new SourceIndependentInstrumentRef(
            "equity/xnas/ALPHA",
            AssetClass.Equity,
            "ALPHA",
            "XNAS",
            "USD");
        var schema = new CanonicalEventSchemaV1(
            "canonical.quote-l1",
            1,
            new string('1', 64),
            ["ask", "ask_size", "bid", "bid_size"]);
        var temporal = new DataTemporalSemanticsV1(
            TradeIrEventTimeBasisV1.OccurredAtUtc,
            TradeIrTimestampPrecisionV1.Microseconds,
            TradeIrEventOrderingV1.EventTimeThenSourceSequence,
            Interval: null,
            RequireAuthoritativeEventTime: true,
            RequirePointInTimeAvailability: true);
        var requirement = new DataRequirementV1(
            "quotes",
            TradeIrDataKindV1.QuoteL1,
            new SourceIndependentInstrumentSelectorV1([instrument]),
            schema,
            temporal,
            TradeIrNormalizationPolicyV1.RawUnadjusted,
            TradeIrMissingDataPolicyV1.Reject,
            TradeIrRevisionPolicyV1.LatestAvailableAtDecisionTime,
            new string('2', 64));
        var definition = new StrategyIntermediateRepresentationV1(
            StrategyIntermediateRepresentationV1.CurrentSchemaVersion,
            "ema-cross",
            "1.0.0",
            target.Profile.OperatorCatalog,
            StrategyClockKindV1.EventTime,
            [requirement],
            [
                Node("decision", "logic.greater_than", Bindings(("left", "fast"), ("right", "slow"))),
                Node("execution", "execution.market", Bindings(("exit", "exit"), ("target", "target")),
                    Parameters(("time_in_force", StrategyLiteralV1.FromText("day")))),
                Node("exit", "risk.trailing_fraction", Bindings(("price", "price"), ("target", "target")),
                    Parameters(("fraction", StrategyLiteralV1.FromNumber(0.025)))),
                Node("fast", "feature.ema", Bindings(("value", "price")),
                    Parameters(("period", StrategyLiteralV1.FromInteger(4)))),
                Node("price", "market.quote.mid", Bindings(),
                    Parameters(("requirement_id", StrategyLiteralV1.FromText("quotes")))),
                Node("slow", "feature.ema", Bindings(("value", "price")),
                    Parameters(("period", StrategyLiteralV1.FromInteger(12)))),
                Node("target", "portfolio.fixed_quantity", Bindings(("decision", "decision")),
                    Parameters(
                        ("when_false", StrategyLiteralV1.FromNumber(-5)),
                        ("when_true", StrategyLiteralV1.FromNumber(5)))),
            ],
            [
                new StrategyIrOutputBindingV1("order-intent", StrategyIrOutputKindV1.OrderIntent, "execution"),
                new StrategyIrOutputBindingV1("signal", StrategyIrOutputKindV1.Signal, "decision"),
                new StrategyIrOutputBindingV1("target", StrategyIrOutputKindV1.Target, "target"),
            ],
            FlattenOnEnd: true);
        var capturedAt = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var capability = new DataSourceCapabilityV1(
            "canonical.quote-feed",
            1,
            capturedAt,
            TradeIrDataKindV1.QuoteL1,
            [instrument],
            schema,
            temporal,
            TradeIrNormalizationPolicyV1.RawUnadjusted,
            TradeIrMissingDataPolicyV1.Reject,
            TradeIrRevisionPolicyV1.LatestAvailableAtDecisionTime,
            "adapter.quote-l1",
            1,
            new string('3', 64));
        var binding = new DataBindingManifestV1(
            "binding.quotes",
            requirement.RequirementId,
            capability.CapabilityId,
            capability.Revision,
            capability.CapturedAtUtc,
            capability.DataKind,
            [instrument],
            schema,
            temporal,
            capability.NormalizationPolicy,
            capability.MissingDataPolicy,
            capability.RevisionPolicy,
            requirement.RequiredSnapshotHashSha256!,
            capability.AdapterId,
            capability.AdapterVersion,
            capability.AdapterHashSha256,
            schema.SchemaHashSha256);
        return new Fixture(target, pins, definition, capability, binding);
    }

    private static BacktestTradeIrArtifactSetV1 ArtifactPins() => new(
        new BacktestTradeIrArtifactIdentityV1(
            BacktestTradeIrTargetV1.CompilerArtifactId,
            BacktestTradeIrTargetV1.ArtifactVersion,
            new string('c', 64)),
        new BacktestTradeIrArtifactIdentityV1(
            BacktestTradeIrTargetV1.RuntimeArtifactId,
            BacktestTradeIrTargetV1.ArtifactVersion,
            new string('d', 64)),
        new BacktestTradeIrArtifactIdentityV1(
            BacktestTradeIrTargetV1.ExecutionHostArtifactId,
            BacktestTradeIrTargetV1.ArtifactVersion,
            new string('e', 64)));

    private static StrategyIrNodeV1 Node(
        string id,
        string operatorId,
        IReadOnlyDictionary<string, string> bindings,
        IReadOnlyDictionary<string, StrategyLiteralV1>? parameters = null) =>
        new(id, operatorId, 1, bindings, parameters ?? Parameters());

    private static IReadOnlyDictionary<string, string> Bindings(params (string Port, string NodeId)[] values) =>
        values.ToDictionary(static value => value.Port, static value => value.NodeId, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, StrategyLiteralV1> Parameters(
        params (string Name, StrategyLiteralV1 Value)[] values) =>
        values.ToDictionary(static value => value.Name, static value => value.Value, StringComparer.Ordinal);

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

    internal sealed record Fixture(
        BacktestTradeIrTargetV1 Target,
        BacktestTradeIrArtifactSetV1 Pins,
        StrategyIntermediateRepresentationV1 Definition,
        DataSourceCapabilityV1 Capability,
        DataBindingManifestV1 Binding);
}
