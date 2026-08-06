using System.Reflection;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Definition;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class TradeIrDataBindingTests
{
    private const string SchemaHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SnapshotHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string AdapterHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void Exact_requirement_capability_and_manifest_are_admitted()
    {
        var fixture = ValidFixture();

        var result = DataAdmissionValidator.Assess(
            fixture.Requirement,
            fixture.Capability,
            fixture.Manifest);

        result.IsAdmitted.Should().BeTrue();
        result.RequirementId.Should().Be("input.primary-quote");
        result.CapabilityId.Should().Be("canonical.quote-feed");
        result.BindingId.Should().Be("binding.primary-quote.2026-08-04");
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Canonical_envelope_carries_point_in_time_metadata_without_replacing_payload_records()
    {
        var instrument = Instrument();
        var eventTime = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var availableAt = eventTime.AddMilliseconds(2);
        var ingestedAt = availableAt.AddMilliseconds(1);
        var envelope = new CanonicalEventEnvelopeV1(
            "event/quote/42",
            TradeIrDataKindV1.QuoteL1,
            instrument,
            eventTime,
            availableAt,
            ingestedAt,
            "source/canonical-feed",
            42,
            2,
            CanonicalEventQualityFlagsV1.Corrected | CanonicalEventQualityFlagsV1.Backfilled,
            SnapshotHash);

        envelope.AvailableAtUtc.Should().Be(availableAt);
        envelope.EventTimeUtc.Should().BeBefore(envelope.AvailableAtUtc);
        envelope.IngestTimeUtc.Should().BeAfter(envelope.AvailableAtUtc);
        envelope.Revision.Should().Be(2);
        envelope.QualityFlags.Should().HaveFlag(CanonicalEventQualityFlagsV1.Corrected);
        envelope.RawPayloadHashSha256.Should().Be(SnapshotHash);
        typeof(CanonicalEventEnvelopeV1).GetProperties().Select(property => property.Name)
            .Should().NotContain(["Bid", "Ask", "Price", "Payload"]);
    }

    [Fact]
    public void Instrument_authoring_contracts_contain_no_local_or_broker_identity()
    {
        var propertyTypes = typeof(SourceIndependentInstrumentRef)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.PropertyType.FullName ?? property.PropertyType.Name)
            .ToArray();

        propertyTypes.Should().NotContain(typeName => typeName.Contains("Brokers", StringComparison.Ordinal));
        propertyTypes.Should().NotContain(typeof(InstrumentId).FullName);
        Instrument().InstrumentKey.Should().Be("equity/xnas/AAPL");
    }

    [Fact]
    public void Each_primary_mismatch_has_one_exact_code_path_and_value_pair()
    {
        var fixture = ValidFixture();

        AssertSingleIssue(
            DataAdmissionValidator.Assess(
                fixture.Requirement,
                fixture.Capability with { DataKind = TradeIrDataKindV1.Trade },
                fixture.Manifest),
            DataAdmissionIssueCodes.DataKindUnsupported,
            "capability.data_kind",
            "QuoteL1",
            "Trade");

        AssertSingleIssue(
            DataAdmissionValidator.Assess(
                fixture.Requirement,
                fixture.Capability with { AvailableInstruments = [] },
                fixture.Manifest),
            DataAdmissionIssueCodes.InstrumentUnbound,
            "capability.available_instruments[equity/xnas/AAPL]",
            "equity/xnas/AAPL|Equity|AAPL|XNAS|USD",
            "<missing>");

        AssertSingleIssue(
            DataAdmissionValidator.Assess(
                fixture.Requirement,
                fixture.Capability with
                {
                    EventSchema = fixture.Capability.EventSchema with { SchemaVersion = 2 },
                },
                fixture.Manifest),
            DataAdmissionIssueCodes.SchemaVersionUnsupported,
            "capability.event_schema.schema_version",
            "1",
            "2");

        AssertSingleIssue(
            DataAdmissionValidator.Assess(
                fixture.Requirement,
                fixture.Capability with
                {
                    TemporalSemantics = fixture.Capability.TemporalSemantics with
                    {
                        TimestampPrecision = TradeIrTimestampPrecisionV1.Milliseconds,
                    },
                },
                fixture.Manifest),
            DataAdmissionIssueCodes.TemporalSemanticsMismatch,
            "capability.temporal_semantics.timestamp_precision",
            "Microseconds",
            "Milliseconds");

        AssertSingleIssue(
            DataAdmissionValidator.Assess(
                fixture.Requirement,
                fixture.Capability,
                fixture.Manifest with { SnapshotHashSha256 = new string('d', 64) }),
            DataAdmissionIssueCodes.SnapshotHashMissing,
            "manifest.snapshot_hash_sha256",
            SnapshotHash,
            new string('d', 64));

        AssertSingleIssue(
            DataAdmissionValidator.Assess(
                fixture.Requirement,
                fixture.Capability,
                fixture.Manifest with { CapabilityRevision = fixture.Capability.Revision - 1 }),
            DataAdmissionIssueCodes.CapabilityStale,
            "manifest.capability_revision",
            "7",
            "6");
    }

    [Fact]
    public void Admission_issues_are_complete_and_deterministically_ordered()
    {
        var fixture = ValidFixture();
        var capability = fixture.Capability with
        {
            DataKind = TradeIrDataKindV1.Trade,
            AvailableInstruments = [],
            EventSchema = fixture.Capability.EventSchema with { SchemaVersion = 2 },
            TemporalSemantics = fixture.Capability.TemporalSemantics with
            {
                TimestampPrecision = TradeIrTimestampPrecisionV1.Milliseconds,
            },
        };
        var manifest = fixture.Manifest with
        {
            SnapshotHashSha256 = "invalid",
            CapabilityRevision = 6,
            AdapterHashSha256 = new string('d', 64),
        };

        var first = DataAdmissionValidator.Assess(fixture.Requirement, capability, manifest);
        var second = DataAdmissionValidator.Assess(fixture.Requirement, capability, manifest);

        first.Should().BeEquivalentTo(second, options => options.WithStrictOrdering());
        first.IsAdmitted.Should().BeFalse();
        first.Issues.Select(issue => (issue.Code, issue.Path)).Should().Equal(
            (DataAdmissionIssueCodes.DataKindUnsupported, "capability.data_kind"),
            (DataAdmissionIssueCodes.InstrumentUnbound, "capability.available_instruments[equity/xnas/AAPL]"),
            (DataAdmissionIssueCodes.SchemaVersionUnsupported, "capability.event_schema.schema_version"),
            (DataAdmissionIssueCodes.TemporalSemanticsMismatch, "capability.temporal_semantics.timestamp_precision"),
            (DataAdmissionIssueCodes.SnapshotHashMissing, "manifest.snapshot_hash_sha256"),
            (DataAdmissionIssueCodes.CapabilityStale, "manifest.capability_revision"),
            (DataAdmissionIssueCodes.CapabilityStale, "manifest.adapter_hash_sha256"));
    }

    [Fact]
    public void Source_capability_is_not_treated_as_the_frozen_research_snapshot()
    {
        var fixture = ValidFixture();
        var unpinnedRequirement = fixture.Requirement with { RequiredSnapshotHashSha256 = null };
        var independentlyMaterializedManifest = fixture.Manifest with
        {
            SnapshotHashSha256 = new string('d', 64),
        };

        DataAdmissionValidator.Assess(
                unpinnedRequirement,
                fixture.Capability,
                independentlyMaterializedManifest)
            .IsAdmitted.Should().BeTrue();

        typeof(DataSourceCapabilityV1).GetProperties().Select(property => property.Name)
            .Should().NotContain("SnapshotHashSha256");
        typeof(DataBindingManifestV1).GetProperties().Select(property => property.Name)
            .Should().Contain(["SnapshotHashSha256", "AdapterHashSha256", "SchemaHashSha256"]);
    }

    [Fact]
    public void Normalization_missing_data_and_revision_policies_are_admitted_exactly()
    {
        var fixture = ValidFixture();
        var capability = fixture.Capability with
        {
            NormalizationPolicy = TradeIrNormalizationPolicyV1.SplitAdjusted,
        };
        var manifest = fixture.Manifest with
        {
            RevisionPolicy = TradeIrRevisionPolicyV1.AllRevisions,
        };

        var result = DataAdmissionValidator.Assess(fixture.Requirement, capability, manifest);

        result.Issues.Select(issue => (issue.Code, issue.Path, issue.Expected, issue.Actual)).Should().Equal(
            (
                DataAdmissionIssueCodes.DataKindUnsupported,
                "capability.normalization_policy",
                "RawUnadjusted",
                "SplitAdjusted"),
            (
                DataAdmissionIssueCodes.TemporalSemanticsMismatch,
                "manifest.revision_policy",
                "LatestAvailableAtDecisionTime",
                "AllRevisions"));
    }

    [Fact]
    public void Null_inputs_fail_closed_without_a_clock_or_exception()
    {
        var fixture = ValidFixture();

        DataAdmissionValidator.Assess(null, fixture.Capability, fixture.Manifest).Issues
            .Should().ContainSingle().Which.Code.Should().Be(DataAdmissionIssueCodes.DataKindUnsupported);
        DataAdmissionValidator.Assess(fixture.Requirement, null, fixture.Manifest).Issues
            .Should().ContainSingle().Which.Code.Should().Be(DataAdmissionIssueCodes.CapabilityStale);
        DataAdmissionValidator.Assess(fixture.Requirement, fixture.Capability, null).Issues
            .Should().ContainSingle().Which.Code.Should().Be(DataAdmissionIssueCodes.SnapshotHashMissing);
    }

    [Fact]
    public void Admission_code_surface_is_closed_to_the_six_stable_reasons()
    {
        var codes = typeof(DataAdmissionIssueCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        codes.Should().BeEquivalentTo(
        [
            "DATA_KIND_UNSUPPORTED",
            "INSTRUMENT_UNBOUND",
            "SCHEMA_VERSION_UNSUPPORTED",
            "TEMPORAL_SEMANTICS_MISMATCH",
            "SNAPSHOT_HASH_MISSING",
            "CAPABILITY_STALE",
        ]);
    }

    private static void AssertSingleIssue(
        DataAdmissionResult result,
        string code,
        string path,
        string expected,
        string actual)
    {
        result.IsAdmitted.Should().BeFalse();
        result.Issues.Should().Equal(
            new DataAdmissionIssue(
                code,
                path,
                expected,
                actual,
                $"Expected '{path}' to be '{expected}', but found '{actual}'."));
    }

    private static Fixture ValidFixture()
    {
        var instrument = Instrument();
        var schema = new CanonicalEventSchemaV1(
            "canonical.quote-l1",
            1,
            SchemaHash,
            ["ask", "ask_size", "bid", "bid_size"]);
        var temporal = new DataTemporalSemanticsV1(
            TradeIrEventTimeBasisV1.OccurredAtUtc,
            TradeIrTimestampPrecisionV1.Microseconds,
            TradeIrEventOrderingV1.EventTimeThenSourceSequence,
            null,
            RequireAuthoritativeEventTime: true,
            RequirePointInTimeAvailability: true);
        var capturedAt = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var requirement = new DataRequirementV1(
            "input.primary-quote",
            TradeIrDataKindV1.QuoteL1,
            new SourceIndependentInstrumentSelectorV1([instrument]),
            schema,
            temporal,
            TradeIrNormalizationPolicyV1.RawUnadjusted,
            TradeIrMissingDataPolicyV1.Reject,
            TradeIrRevisionPolicyV1.LatestAvailableAtDecisionTime,
            SnapshotHash);
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
            AdapterHash);
        var manifest = new DataBindingManifestV1(
            "binding.primary-quote.2026-08-04",
            requirement.RequirementId,
            capability.CapabilityId,
            capability.Revision,
            capability.CapturedAtUtc,
            TradeIrDataKindV1.QuoteL1,
            [instrument],
            schema,
            temporal,
            TradeIrNormalizationPolicyV1.RawUnadjusted,
            TradeIrMissingDataPolicyV1.Reject,
            TradeIrRevisionPolicyV1.LatestAvailableAtDecisionTime,
            SnapshotHash,
            capability.AdapterId,
            capability.AdapterVersion,
            capability.AdapterHashSha256,
            schema.SchemaHashSha256);

        return new Fixture(requirement, capability, manifest);
    }

    private static SourceIndependentInstrumentRef Instrument() =>
        new("equity/xnas/AAPL", AssetClass.Equity, "AAPL", "XNAS", "USD");

    private sealed record Fixture(
        DataRequirementV1 Requirement,
        DataSourceCapabilityV1 Capability,
        DataBindingManifestV1 Manifest);
}
