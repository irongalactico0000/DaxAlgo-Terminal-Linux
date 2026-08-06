using System.Reflection;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Definition;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class StrategyCompilationAdmissionV1Tests
{
    [Fact]
    public void Exact_graph_target_capability_and_snapshot_are_admitted_before_compilation()
    {
        var fixture = CreateFixture();

        var result = StrategyCompilationAdmissionV1.Assess(
            fixture.Definition,
            fixture.Registry,
            fixture.Target,
            [fixture.Capability],
            [fixture.Binding]);

        result.CanCompile.Should().BeTrue();
        result.DataAdmissions.Should().ContainSingle().Which.IsAdmitted.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Successful_admission_freezes_exact_inputs_and_is_stable_after_caller_mutation()
    {
        var fixture = CreateFixture();
        var nodeParameters = fixture.Definition.Nodes[0].Parameters.ToDictionary();
        var mutableNodes = new List<StrategyIrNodeV1>
        {
            fixture.Definition.Nodes[0] with { Parameters = nodeParameters },
        };
        var mutableCapabilities = fixture.Target.SupportedCapabilities.ToList();
        var mutableBindings = new List<DataBindingManifestV1> { fixture.Binding };
        var definition = fixture.Definition with { Nodes = mutableNodes };
        var target = fixture.Target with { SupportedCapabilities = mutableCapabilities };

        var first = StrategyCompilationAdmissionV1.AssessAndFreeze(
            definition,
            fixture.Registry,
            target,
            [fixture.Capability],
            mutableBindings);
        var second = StrategyCompilationAdmissionV1.AssessAndFreeze(
            definition,
            fixture.Registry,
            target,
            [fixture.Capability],
            mutableBindings);

        first.CanCompile.Should().BeTrue();
        first.Manifest!.ManifestHashSha256.Should().Be(second.Manifest!.ManifestHashSha256);
        var frozenHash = StrategyIrCanonicalJsonV1.Hash(first.Manifest.ReadDefinitionForCompilation());

        nodeParameters["requirement_id"] = StrategyLiteralV1.FromText("changed");
        mutableNodes.Clear();
        mutableCapabilities.Clear();
        mutableBindings[0] = mutableBindings[0] with { SnapshotHashSha256 = new string('f', 64) };

        StrategyIrCanonicalJsonV1.Hash(first.Manifest.ReadDefinitionForCompilation()).Should().Be(frozenHash);
        first.Manifest.ReadDocument().DataBindings.Should().ContainSingle().Which.Snapshot.Digest
            .Should().Be(fixture.Binding.SnapshotHashSha256);
    }

    [Fact]
    public void Frozen_manifest_rejects_definition_or_manifest_tampering()
    {
        var fixture = CreateFixture();
        var outcome = StrategyCompilationAdmissionV1.AssessAndFreeze(
            fixture.Definition,
            fixture.Registry,
            fixture.Target,
            [fixture.Capability],
            [fixture.Binding]);
        var manifest = outcome.Manifest!;
        var changedDefinition = manifest.CanonicalDefinitionJson.Replace(
            "quote-signal",
            "other-signal",
            StringComparison.Ordinal);
        var changedManifest = manifest.CanonicalManifestJson.Replace(
            StrategyCompilationAdmissionDocumentV1.CurrentAdmissionRulesVersion,
            "trade-ir/compilation-admission/v2",
            StringComparison.Ordinal);

        var readChangedDefinition = () => StrategyCompilationAdmissionManifestV1.Read(
            changedDefinition,
            manifest.CanonicalTargetProfileJson,
            manifest.CanonicalManifestJson,
            manifest.ManifestHashSha256);
        var readChangedManifest = () => StrategyCompilationAdmissionManifestV1.Read(
            manifest.CanonicalDefinitionJson,
            manifest.CanonicalTargetProfileJson,
            changedManifest,
            manifest.ManifestHashSha256);

        readChangedDefinition.Should().Throw<InvalidDataException>().WithMessage("*definitionDocument hash*");
        readChangedManifest.Should().Throw<InvalidDataException>().WithMessage("*manifest hash mismatch*");
    }

    [Fact]
    public void Admission_manifest_cannot_be_publicly_forged_and_rejects_self_consistent_missing_data_pins()
    {
        var fixture = CreateFixture();
        var outcome = StrategyCompilationAdmissionV1.AssessAndFreeze(
            fixture.Definition,
            fixture.Registry,
            fixture.Target,
            [fixture.Capability],
            [fixture.Binding]);
        var manifest = outcome.Manifest!;
        var forgedDocument = manifest.ReadDocument() with { DataBindings = [] };
        var forgedManifestJson = ExecutableStrategyDefinitionCanonicalJson.Serialize(forgedDocument);

        var readForgedManifest = () => StrategyCompilationAdmissionManifestV1.Read(
            manifest.CanonicalDefinitionJson,
            manifest.CanonicalTargetProfileJson,
            forgedManifestJson,
            ExecutableStrategyDefinitionCanonicalJson.Sha256(forgedManifestJson));

        readForgedManifest.Should().Throw<InvalidDataException>()
            .WithMessage("*exactly one data pin*");
        typeof(StrategyCompilationAdmissionManifestV1)
            .GetMethod("Read", BindingFlags.Public | BindingFlags.Static)
            .Should().BeNull("external callers must not mint admitted manifests from self-authored hashes");
        typeof(StrategyCompilationAdmissionManifestV1)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().BeEmpty("external callers must not construct an admission token directly");
    }

    [Fact]
    public void Self_consistent_manifest_rejects_snapshot_and_schema_pins_not_required_by_definition()
    {
        var fixture = CreateFixture();
        var manifest = StrategyCompilationAdmissionV1.AssessAndFreeze(
            fixture.Definition,
            fixture.Registry,
            fixture.Target,
            [fixture.Capability],
            [fixture.Binding]).Manifest!;
        var admittedDocument = manifest.ReadDocument();
        var admittedPin = admittedDocument.DataBindings.Single();
        var wrongSnapshot = admittedDocument with
        {
            DataBindings = [admittedPin with { Snapshot = Address('f') }],
        };
        var wrongSchema = admittedDocument with
        {
            DataBindings = [admittedPin with { EventSchema = Address('f') }],
        };

        Action readWrongSnapshot = () => ReadSelfConsistent(manifest, wrongSnapshot);
        Action readWrongSchema = () => ReadSelfConsistent(manifest, wrongSchema);

        readWrongSnapshot.Should().Throw<InvalidDataException>().WithMessage("*snapshot pin*");
        readWrongSchema.Should().Throw<InvalidDataException>().WithMessage("*schema pin*");
    }

    [Fact]
    public void Self_consistent_manifest_rejects_extra_duplicate_or_wrong_requirement_pins()
    {
        var fixture = CreateFixture();
        var manifest = StrategyCompilationAdmissionV1.AssessAndFreeze(
            fixture.Definition,
            fixture.Registry,
            fixture.Target,
            [fixture.Capability],
            [fixture.Binding]).Manifest!;
        var admittedDocument = manifest.ReadDocument();
        var admittedPin = admittedDocument.DataBindings.Single();
        var duplicateRequirement = admittedDocument with
        {
            DataBindings =
            [
                admittedPin with { BindingId = "binding.extra" },
                admittedPin,
            ],
        };
        var wrongRequirement = admittedDocument with
        {
            DataBindings = [admittedPin with { RequirementId = "other-requirement" }],
        };

        Action readDuplicate = () => ReadSelfConsistent(manifest, duplicateRequirement);
        Action readWrongRequirement = () => ReadSelfConsistent(manifest, wrongRequirement);

        readDuplicate.Should().Throw<InvalidDataException>().WithMessage("*exactly one data pin*");
        readWrongRequirement.Should().Throw<InvalidDataException>().WithMessage("*exactly one data pin*");
    }

    [Fact]
    public void Failed_admission_never_produces_a_compiler_manifest()
    {
        var fixture = CreateFixture();

        var outcome = StrategyCompilationAdmissionV1.AssessAndFreeze(
            fixture.Definition,
            fixture.Registry,
            fixture.Target,
            [fixture.Capability],
            []);

        outcome.CanCompile.Should().BeFalse();
        outcome.Manifest.Should().BeNull();
    }

    [Fact]
    public void Data_semantics_target_capabilities_and_missing_bindings_fail_before_compilation()
    {
        var fixture = CreateFixture();
        var weakerTarget = fixture.Target with { SupportedCapabilities = [] };
        var wrongPrecision = fixture.Capability with
        {
            TemporalSemantics = fixture.Capability.TemporalSemantics with
            {
                TimestampPrecision = TradeIrTimestampPrecisionV1.Milliseconds,
            },
        };

        var dataMismatch = StrategyCompilationAdmissionV1.Assess(
            fixture.Definition,
            fixture.Registry,
            fixture.Target,
            [wrongPrecision],
            [fixture.Binding]);
        var targetMismatch = StrategyCompilationAdmissionV1.Assess(
            fixture.Definition,
            fixture.Registry,
            weakerTarget,
            [fixture.Capability],
            [fixture.Binding]);
        var missingBinding = StrategyCompilationAdmissionV1.Assess(
            fixture.Definition,
            fixture.Registry,
            fixture.Target,
            [fixture.Capability],
            []);

        dataMismatch.CanCompile.Should().BeFalse();
        dataMismatch.Issues.Should().Contain(issue =>
            issue.Code == $"DATA.{DataAdmissionIssueCodes.TemporalSemanticsMismatch}");
        targetMismatch.CanCompile.Should().BeFalse();
        targetMismatch.Issues.Should().Contain(issue => issue.Code == "TARGET.target_capability_unsupported");
        missingBinding.CanCompile.Should().BeFalse();
        missingBinding.Issues.Should().ContainSingle(issue => issue.Code == "ADMISSION_DATA_BINDING_COUNT");
    }

    private static Fixture CreateFixture()
    {
        var registry = StrategyOperatorRegistryV1.CreateDefault();
        var instrument = new SourceIndependentInstrumentRef(
            "equity/xnas/AAPL",
            AssetClass.Equity,
            "AAPL",
            "XNAS",
            "USD");
        var schema = new CanonicalEventSchemaV1(
            "canonical.quote-l1",
            1,
            new string('a', 64),
            ["ask", "ask_size", "bid", "bid_size"]);
        var temporal = new DataTemporalSemanticsV1(
            TradeIrEventTimeBasisV1.OccurredAtUtc,
            TradeIrTimestampPrecisionV1.Microseconds,
            TradeIrEventOrderingV1.EventTimeThenSourceSequence,
            null,
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
            new string('b', 64));
        var definition = new StrategyIntermediateRepresentationV1(
            StrategyIntermediateRepresentationV1.CurrentSchemaVersion,
            "quote-signal",
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
        var validation = StrategyIrValidatorV1.Validate(definition, registry);
        var target = new StrategyIrTargetProfileV1(
            "research-worker-v1",
            ProfileRevision: 1,
            StrategyIntermediateRepresentationV1.CurrentSchemaVersion,
            registry.Catalog,
            "daxalgo.reference-evaluator",
            "0.1.0",
            new string('d', 64),
            "daxalgo.research-worker",
            "0.1.0",
            new string('e', 64),
            validation.Nodes.Select(static node => node.Operator).ToArray(),
            validation.RequiredCapabilities.Select(static capability => capability.CapabilityId).ToArray(),
            [StrategyOperatorPlacementV1.RestrictedCompute]);
        var capturedAt = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var capability = new DataSourceCapabilityV1(
            "canonical.quote-feed",
            7,
            capturedAt,
            TradeIrDataKindV1.QuoteL1,
            [instrument],
            schema,
            temporal,
            TradeIrNormalizationPolicyV1.RawUnadjusted,
            TradeIrMissingDataPolicyV1.Reject,
            TradeIrRevisionPolicyV1.LatestAvailableAtDecisionTime,
            "adapter.quote-l1",
            3,
            new string('c', 64));
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
        return new Fixture(registry, definition, target, capability, binding);
    }

    private static void ReadSelfConsistent(
        StrategyCompilationAdmissionManifestV1 admitted,
        StrategyCompilationAdmissionDocumentV1 document)
    {
        var json = ExecutableStrategyDefinitionCanonicalJson.Serialize(document);
        _ = StrategyCompilationAdmissionManifestV1.Read(
            admitted.CanonicalDefinitionJson,
            admitted.CanonicalTargetProfileJson,
            json,
            ExecutableStrategyDefinitionCanonicalJson.Sha256(json));
    }

    private static TradeIrContentAddressV1 Address(char character) => new(
        TradeIrDigestAlgorithmV1.Sha256,
        new string(character, 64));

    private sealed record Fixture(
        StrategyOperatorRegistryV1 Registry,
        StrategyIntermediateRepresentationV1 Definition,
        StrategyIrTargetProfileV1 Target,
        DataSourceCapabilityV1 Capability,
        DataBindingManifestV1 Binding);
}
