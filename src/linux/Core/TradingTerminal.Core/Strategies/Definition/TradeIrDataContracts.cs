using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Strategies.Definition;

/// <summary>The closed set of canonical input families understood by TradeIR v1 authoring.</summary>
public enum TradeIrDataKindV1
{
    Unknown = 0,
    QuoteL1,
    Trade,
    Bar,
    Depth,
    ScheduledEvent,
    Fundamental,
    CorporateEvent,
    News,
    Alternative,
}

/// <summary>What instant a canonical event timestamp denotes.</summary>
public enum TradeIrEventTimeBasisV1
{
    Unknown = 0,
    OccurredAtUtc,
    IntervalOpenUtc,
    IntervalCloseUtc,
    EffectiveAtUtc,
    PublishedAtUtc,
}

/// <summary>The minimum timestamp precision that must survive source adaptation.</summary>
public enum TradeIrTimestampPrecisionV1
{
    Unknown = 0,
    Seconds,
    Milliseconds,
    Microseconds,
    Nanoseconds,
}

/// <summary>The deterministic ordering key exposed to authored logic.</summary>
public enum TradeIrEventOrderingV1
{
    Unknown = 0,
    EventTime,
    EventTimeThenSourceSequence,
}

/// <summary>How numeric and corporate-action-sensitive values are represented.</summary>
public enum TradeIrNormalizationPolicyV1
{
    Unknown = 0,
    RawUnadjusted,
    SplitAdjusted,
    TotalReturnAdjusted,
    SourceCanonical,
}

/// <summary>How a missing observation is represented; never inferred implicitly by an adapter.</summary>
public enum TradeIrMissingDataPolicyV1
{
    Unknown = 0,
    Reject,
    PreserveMissing,
    ForwardFill,
}

/// <summary>Which revisions of point-in-time information authored logic is allowed to observe.</summary>
public enum TradeIrRevisionPolicyV1
{
    Unknown = 0,
    FirstPublishedOnly,
    LatestAvailableAtDecisionTime,
    AllRevisions,
}

/// <summary>Source-neutral quality facts carried by a canonical event.</summary>
[Flags]
public enum CanonicalEventQualityFlagsV1
{
    None = 0,
    EventTimeApproximate = 1 << 0,
    AvailableTimeApproximate = 1 << 1,
    Corrected = 1 << 2,
    Backfilled = 1 << 3,
    SourceGapBefore = 1 << 4,
}

/// <summary>
/// Time meaning required by an authored input. This is compared exactly during admission; an
/// adapter cannot silently substitute arrival time, change interval anchoring, reduce precision,
/// or discard a required source sequence.
/// </summary>
public sealed record DataTemporalSemanticsV1(
    TradeIrEventTimeBasisV1 EventTimeBasis,
    TradeIrTimestampPrecisionV1 TimestampPrecision,
    TradeIrEventOrderingV1 Ordering,
    TimeSpan? Interval,
    bool RequireAuthoritativeEventTime,
    bool RequirePointInTimeAvailability);

/// <summary>
/// A portable instrument identity used by authored definitions and data manifests. The opaque
/// <see cref="InstrumentKey"/> is stable across installations; this deliberately does not contain
/// a broker symbol, broker-native id, or the store-local <see cref="InstrumentId"/> surrogate.
/// </summary>
public sealed record SourceIndependentInstrumentRef(
    string InstrumentKey,
    AssetClass AssetClass,
    string Symbol,
    string Venue,
    string Currency);

/// <summary>
/// The exact, finite set of portable instrument identities selected by a v1 authored input.
/// Dynamic source catalogs and broker-specific universe queries are intentionally outside v1.
/// </summary>
public sealed record SourceIndependentInstrumentSelectorV1(
    IReadOnlyList<SourceIndependentInstrumentRef> References);

/// <summary>
/// Payload metadata for one canonical authoring schema. Common event identity, point-in-time
/// availability, provenance, revision, and quality fields are fixed by
/// <see cref="CanonicalEventEnvelopeV1"/> rather than repeated in every payload schema.
/// </summary>
public sealed record CanonicalEventSchemaV1(
    string SchemaId,
    int SchemaVersion,
    string SchemaHashSha256,
    IReadOnlyList<string> PayloadFields);

/// <summary>
/// Source-neutral metadata for one canonical authored event. Payload values remain in their typed
/// runtime or persisted records; this envelope does not replace <see cref="Quote"/>,
/// <see cref="TradePrint"/>, <see cref="OhlcvBar"/>, or another domain payload. In particular,
/// <see cref="AvailableAtUtc"/> is the first instant the strategy could have known the fact and is
/// distinct from both economic event time and local ingest time.
/// </summary>
public sealed record CanonicalEventEnvelopeV1(
    string EventId,
    TradeIrDataKindV1 DataKind,
    SourceIndependentInstrumentRef Instrument,
    DateTimeOffset EventTimeUtc,
    DateTimeOffset AvailableAtUtc,
    DateTimeOffset IngestTimeUtc,
    string SourceId,
    long SourceSequence,
    int Revision,
    CanonicalEventQualityFlagsV1 QualityFlags,
    string RawPayloadHashSha256);

/// <summary>A source-agnostic statement of one authored strategy input.</summary>
public sealed record DataRequirementV1(
    string RequirementId,
    TradeIrDataKindV1 DataKind,
    SourceIndependentInstrumentSelectorV1 InstrumentSelector,
    CanonicalEventSchemaV1 EventSchema,
    DataTemporalSemanticsV1 TemporalSemantics,
    TradeIrNormalizationPolicyV1 NormalizationPolicy,
    TradeIrMissingDataPolicyV1 MissingDataPolicy,
    TradeIrRevisionPolicyV1 RevisionPolicy,
    string? RequiredSnapshotHashSha256 = null);

/// <summary>
/// An immutable observation of data a source can bind through one exact adapter. Source identity is
/// opaque and broker-neutral; admission relies only on the declared canonical semantics and hashes.
/// </summary>
public sealed record DataSourceCapabilityV1(
    string CapabilityId,
    int Revision,
    DateTimeOffset CapturedAtUtc,
    TradeIrDataKindV1 DataKind,
    IReadOnlyList<SourceIndependentInstrumentRef> AvailableInstruments,
    CanonicalEventSchemaV1 EventSchema,
    DataTemporalSemanticsV1 TemporalSemantics,
    TradeIrNormalizationPolicyV1 NormalizationPolicy,
    TradeIrMissingDataPolicyV1 MissingDataPolicy,
    TradeIrRevisionPolicyV1 RevisionPolicy,
    string AdapterId,
    int AdapterVersion,
    string AdapterHashSha256);

/// <summary>
/// Immutable admission evidence binding one requirement to one captured source capability and exact
/// materialized bytes. Snapshot, adapter, and schema identities are independent so changing any one
/// of them invalidates the binding without pretending the others changed.
/// </summary>
public sealed record DataBindingManifestV1(
    string BindingId,
    string RequirementId,
    string CapabilityId,
    int CapabilityRevision,
    DateTimeOffset CapabilityCapturedAtUtc,
    TradeIrDataKindV1 DataKind,
    IReadOnlyList<SourceIndependentInstrumentRef> BoundInstruments,
    CanonicalEventSchemaV1 EventSchema,
    DataTemporalSemanticsV1 TemporalSemantics,
    TradeIrNormalizationPolicyV1 NormalizationPolicy,
    TradeIrMissingDataPolicyV1 MissingDataPolicy,
    TradeIrRevisionPolicyV1 RevisionPolicy,
    string SnapshotHashSha256,
    string AdapterId,
    int AdapterVersion,
    string AdapterHashSha256,
    string SchemaHashSha256);
