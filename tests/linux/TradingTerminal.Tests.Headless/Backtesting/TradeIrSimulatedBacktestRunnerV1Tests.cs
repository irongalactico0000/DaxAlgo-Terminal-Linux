using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Backtest.Engine.TradeIr;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Definition;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

public sealed class TradeIrSimulatedBacktestRunnerV1Tests
{
    private static readonly string CandidateHash = new('a', 64);

    [Fact]
    public async Task Exact_module_runs_real_in_process_synthetic_path_with_honest_evidence()
    {
        var module = CreateModule();
        var moduleHash = OperatorGraphModuleCanonicalJsonV1.Hash(module);

        var result = await new TradeIrSimulatedBacktestRunnerV1().RunAsync(new(
            CandidateHash,
            moduleHash,
            module));

        result.Succeeded.Should().BeTrue();
        result.Status.Should().Be(TradeIrSimulatedBacktestStatusV1.Succeeded);
        result.Issues.Should().BeEmpty();
        result.Report.Should().NotBeNull();
        result.Report!.Summary.EventsProcessed.Should().Be(512);
        result.Evidence.Should().NotBeNull();
        result.Evidence!.ExecutionMode.Should().Be(TradeIrSimulatedBacktestContractV1.ExecutionMode);
        result.Evidence.IsWorkerIsolated.Should().BeFalse();
        result.Evidence.IsHistoricalData.Should().BeFalse();
        result.Evidence.SourceCandidateHashSha256.Should().Be(CandidateHash);
        result.Evidence.ModuleHashSha256.Should().Be(moduleHash);
        result.Evidence.DefinitionHashSha256.Should().Be(StrategyIrCanonicalJsonV1.Hash(module.Definition));
        result.Evidence.EventsProcessed.Should().Be(512);
        result.Evidence.SubmittedOrderCount.Should().BeGreaterThan(0);
        AssertSha256(result.Evidence.AdmissionManifestHashSha256);
        AssertSha256(result.Evidence.SyntheticInputHashSha256);
        AssertSha256(result.Evidence.CompilerArtifactHashSha256);
        AssertSha256(result.Evidence.RuntimeArtifactHashSha256);
        AssertSha256(result.Evidence.ExecutionHostArtifactHashSha256);
        AssertSha256(result.Evidence.RuntimeReceiptHashSha256);
    }

    [Fact]
    public async Task Equal_module_and_seed_produce_equal_stable_receipts_and_reports()
    {
        var module = CreateModule();
        var request = new TradeIrSimulatedBacktestRequestV1(
            CandidateHash,
            OperatorGraphModuleCanonicalJsonV1.Hash(module),
            module,
            EventCount: 768,
            Seed: 23);
        var runner = new TradeIrSimulatedBacktestRunnerV1();

        var first = await runner.RunAsync(request);
        var second = await runner.RunAsync(request);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.Evidence.Should().BeEquivalentTo(first.Evidence);
        StableReport(second.Report!).Should().BeEquivalentTo(
            StableReport(first.Report!),
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Module_hash_mismatch_rejects_before_admission_or_execution()
    {
        var module = CreateModule();

        var result = await new TradeIrSimulatedBacktestRunnerV1().RunAsync(new(
            CandidateHash,
            new string('f', 64),
            module));

        result.Status.Should().Be(TradeIrSimulatedBacktestStatusV1.Rejected);
        result.Report.Should().BeNull();
        result.Evidence.Should().BeNull();
        result.Issues.Should().ContainSingle(issue =>
            issue.Code == TradeIrSimulatedBacktestIssueCodesV1.ModuleHashMismatch &&
            issue.Path == "expectedModuleHashSha256");
    }

    [Fact]
    public async Task Package_valid_operator_outside_closed_target_is_rejected()
    {
        var module = CreateModule();
        var nodes = module.Definition.Nodes.Select(node => node.NodeId == "fast"
            ? node with
            {
                OperatorId = "feature.rolling_max",
                Parameters = new Dictionary<string, StrategyLiteralV1>(StringComparer.Ordinal)
                {
                    ["window"] = StrategyLiteralV1.FromInteger(4),
                },
            }
            : node).ToArray();
        module = module with { Definition = module.Definition with { Nodes = nodes } };

        TradeIrModuleValidatorV1.Validate(module, StrategyOperatorRegistryV1.CreateDefault()).IsValid
            .Should().BeTrue("generic package validity is intentionally broader than this runtime target");
        var result = await Run(module);

        result.Status.Should().Be(TradeIrSimulatedBacktestStatusV1.Rejected);
        result.Report.Should().BeNull();
        result.Evidence.Should().BeNull();
        result.Issues.Should().Contain(issue => issue.Code == "TARGET.target_operator_unsupported");
    }

    [Fact]
    public async Task Host_schema_and_materialized_snapshot_binding_fail_closed()
    {
        var module = CreateModule();
        var requirement = module.Definition.DataRequirements.Single();
        var wrongSchema = requirement.EventSchema with { SchemaHashSha256 = new string('f', 64) };
        var schemaDrift = module with
        {
            Definition = module.Definition with
            {
                DataRequirements = [requirement with { EventSchema = wrongSchema }],
            },
        };
        var snapshotDrift = module with
        {
            Definition = module.Definition with
            {
                DataRequirements = [requirement with { RequiredSnapshotHashSha256 = new string('e', 64) }],
            },
        };

        var schemaResult = await Run(schemaDrift);
        var snapshotResult = await Run(snapshotDrift);

        schemaResult.Status.Should().Be(TradeIrSimulatedBacktestStatusV1.Rejected);
        schemaResult.Issues.Should().Contain(issue =>
            issue.Code == $"DATA.{DataAdmissionIssueCodes.SchemaVersionUnsupported}");
        snapshotResult.Status.Should().Be(TradeIrSimulatedBacktestStatusV1.Rejected);
        snapshotResult.Issues.Should().Contain(issue =>
            issue.Code == $"DATA.{DataAdmissionIssueCodes.SnapshotHashMissing}");
        schemaResult.Report.Should().BeNull();
        snapshotResult.Report.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_001)]
    public async Task Invalid_event_count_is_rejected_without_execution(int eventCount)
    {
        var module = CreateModule();
        var result = await new TradeIrSimulatedBacktestRunnerV1().RunAsync(new(
            CandidateHash,
            OperatorGraphModuleCanonicalJsonV1.Hash(module),
            module,
            eventCount));

        result.Status.Should().Be(TradeIrSimulatedBacktestStatusV1.Rejected);
        result.Report.Should().BeNull();
        result.Evidence.Should().BeNull();
        result.Issues.Should().ContainSingle(issue =>
            issue.Code == TradeIrSimulatedBacktestIssueCodesV1.EventCountInvalid);
    }

    [Fact]
    public async Task Cancellation_returns_no_success_report_or_evidence()
    {
        var module = CreateModule();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new TradeIrSimulatedBacktestRunnerV1().RunAsync(new(
            CandidateHash,
            OperatorGraphModuleCanonicalJsonV1.Hash(module),
            module), cts.Token);

        result.Status.Should().Be(TradeIrSimulatedBacktestStatusV1.Cancelled);
        result.Report.Should().BeNull();
        result.Evidence.Should().BeNull();
        result.Issues.Should().ContainSingle(issue =>
            issue.Code == TradeIrSimulatedBacktestIssueCodesV1.Cancelled);
    }

    [Fact]
    public async Task Caller_mutation_after_start_cannot_change_frozen_module()
    {
        var module = CreateModule();
        var mutableNodes = module.Definition.Nodes.ToList();
        module = module with { Definition = module.Definition with { Nodes = mutableNodes } };
        var expectedHash = OperatorGraphModuleCanonicalJsonV1.Hash(module);

        var run = new TradeIrSimulatedBacktestRunnerV1().RunAsync(new(
            CandidateHash,
            expectedHash,
            module,
            EventCount: 1_024));
        mutableNodes.Clear();
        var result = await run;

        result.Succeeded.Should().BeTrue();
        result.Evidence!.ModuleHashSha256.Should().Be(expectedHash);
    }

    [Fact]
    public void Module_canonicalizer_includes_discriminator_and_is_strict()
    {
        var module = CreateModule();
        var canonical = OperatorGraphModuleCanonicalJsonV1.Serialize(module);
        using var document = JsonDocument.Parse(canonical);

        document.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().BeEquivalentTo(["definition", "moduleId", "moduleKind", "schemaVersion"]);
        document.RootElement.GetProperty("moduleKind").GetString().Should().Be("operatorGraph");
        OperatorGraphModuleCanonicalJsonV1.Hash(
                OperatorGraphModuleCanonicalJsonV1.Deserialize(canonical))
            .Should().Be(OperatorGraphModuleCanonicalJsonV1.Hash(module));

        var reordered = $$"""
            {
              "schemaVersion": "{{module.SchemaVersion}}",
              "moduleKind": "operatorGraph",
              "definition": {{StrategyIrCanonicalJsonV1.Serialize(module.Definition)}},
              "moduleId": "{{module.ModuleId}}"
            }
            """;
        OperatorGraphModuleCanonicalJsonV1.Hash(
                OperatorGraphModuleCanonicalJsonV1.Deserialize(reordered))
            .Should().Be(OperatorGraphModuleCanonicalJsonV1.Hash(module));

        var unknown = canonical[..^1] + ",\"unknown\":true}";
        var wrongKind = canonical.Replace("\"operatorGraph\"", "\"csharp\"", StringComparison.Ordinal);
        var duplicate = canonical[..^1] + ",\"moduleId\":\"duplicate\"}";
        FluentActions.Invoking(() => OperatorGraphModuleCanonicalJsonV1.Deserialize(unknown)).Should().Throw<JsonException>();
        FluentActions.Invoking(() => OperatorGraphModuleCanonicalJsonV1.Deserialize(wrongKind)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => OperatorGraphModuleCanonicalJsonV1.Deserialize(duplicate)).Should().Throw<JsonException>();
    }

    private static async Task<TradeIrSimulatedBacktestResultV1> Run(OperatorGraphModuleV1 module) =>
        await new TradeIrSimulatedBacktestRunnerV1().RunAsync(new(
            CandidateHash,
            OperatorGraphModuleCanonicalJsonV1.Hash(module),
            module));

    private static OperatorGraphModuleV1 CreateModule()
    {
        var fixture = BacktestTradeIrTargetV1Tests.CreateFixture();
        var requirement = fixture.Definition.DataRequirements.Single() with
        {
            EventSchema = TradeIrSimulatedBacktestContractV1.CreateEventSchema(),
            RequiredSnapshotHashSha256 = null,
        };
        var definition = fixture.Definition with { DataRequirements = [requirement] };
        return new OperatorGraphModuleV1(
            TradeIrModuleV1.CurrentSchemaVersion,
            "ema-smoke",
            definition);
    }

    private static object StableReport(TradingTerminal.Core.Backtesting.BacktestReport report) => new
    {
        report.Summary.StartUtc,
        report.Summary.EndUtc,
        report.Summary.StartingCash,
        report.Summary.EndingEquity,
        report.Summary.EventsProcessed,
        Metrics = report.Metrics.All.OrderBy(static pair => pair.Key).ToArray(),
        Trades = report.Trades.ToArray(),
        Equity = report.Equity.ToArray(),
        PerInstrument = report.PerInstrument.ToArray(),
    };

    private static void AssertSha256(string value) =>
        value.Should().MatchRegex("^[0-9a-f]{64}$");
}
