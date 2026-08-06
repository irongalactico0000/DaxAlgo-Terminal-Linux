using System.Globalization;

namespace TradingTerminal.Core.Strategies.Definition;

/// <summary>Stable fail-closed reasons emitted by <see cref="DataAdmissionValidator"/>.</summary>
public static class DataAdmissionIssueCodes
{
    public const string DataKindUnsupported = "DATA_KIND_UNSUPPORTED";
    public const string InstrumentUnbound = "INSTRUMENT_UNBOUND";
    public const string SchemaVersionUnsupported = "SCHEMA_VERSION_UNSUPPORTED";
    public const string TemporalSemanticsMismatch = "TEMPORAL_SEMANTICS_MISMATCH";
    public const string SnapshotHashMissing = "SNAPSHOT_HASH_MISSING";
    public const string CapabilityStale = "CAPABILITY_STALE";
}

/// <summary>
/// One exactly comparable admission mismatch. Expected and actual values are invariant strings so
/// callers can persist or display a verdict without interpreting prose.
/// </summary>
public sealed record DataAdmissionIssue(
    string Code,
    string Path,
    string Expected,
    string Actual,
    string Message);

/// <summary>The deterministic result of binding one authored requirement to captured data.</summary>
public sealed record DataAdmissionResult(
    string RequirementId,
    string CapabilityId,
    string BindingId,
    IReadOnlyList<DataAdmissionIssue> Issues)
{
    public bool IsAdmitted => Issues.Count == 0;
}

/// <summary>
/// Pure, fail-closed admission for TradeIR data bindings. It performs no source discovery, clock
/// reads, hashing, materialization, or runtime-record conversion: all compared facts are explicit
/// immutable inputs and issues are appended in a fixed order.
/// </summary>
public static class DataAdmissionValidator
{
    private const string Missing = "<missing>";
    private const string Present = "<present>";

    public static DataAdmissionResult Assess(
        DataRequirementV1? requirement,
        DataSourceCapabilityV1? capability,
        DataBindingManifestV1? manifest)
    {
        if (requirement is null)
        {
            return Result(
                null,
                capability,
                manifest,
                [Issue(
                    DataAdmissionIssueCodes.DataKindUnsupported,
                    "requirement",
                    nameof(DataRequirementV1),
                    Missing)]);
        }

        if (capability is null)
        {
            return Result(
                requirement,
                null,
                manifest,
                [Issue(
                    DataAdmissionIssueCodes.CapabilityStale,
                    "capability",
                    nameof(DataSourceCapabilityV1),
                    Missing)]);
        }

        if (manifest is null)
        {
            return Result(
                requirement,
                capability,
                null,
                [Issue(
                    DataAdmissionIssueCodes.SnapshotHashMissing,
                    "manifest",
                    nameof(DataBindingManifestV1),
                    Missing)]);
        }

        var issues = new List<DataAdmissionIssue>();
        ValidateDataKinds(requirement, capability, manifest, issues);
        ValidateInstruments(requirement, capability, manifest, issues);
        ValidateSchemas(requirement, capability, manifest, issues);
        ValidateTemporalSemantics(requirement, capability, manifest, issues);
        ValidateDataPolicies(requirement, capability, manifest, issues);
        ValidateSnapshotHashes(requirement, manifest, issues);
        ValidateCapabilityBinding(requirement, capability, manifest, issues);

        return Result(requirement, capability, manifest, issues);
    }

    private static void ValidateDataKinds(
        DataRequirementV1 requirement,
        DataSourceCapabilityV1 capability,
        DataBindingManifestV1 manifest,
        List<DataAdmissionIssue> issues)
    {
        var requirementValid = IsDefined(requirement.DataKind);
        var capabilityValid = IsDefined(capability.DataKind);
        var manifestValid = IsDefined(manifest.DataKind);

        if (!requirementValid)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.DataKindUnsupported,
                "requirement.data_kind",
                "a defined non-unknown TradeIR v1 data kind",
                EnumValue(requirement.DataKind));
        }

        if (!capabilityValid)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.DataKindUnsupported,
                "capability.data_kind",
                "a defined non-unknown TradeIR v1 data kind",
                EnumValue(capability.DataKind));
        }
        else if (requirementValid && capability.DataKind != requirement.DataKind)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.DataKindUnsupported,
                "capability.data_kind",
                EnumValue(requirement.DataKind),
                EnumValue(capability.DataKind));
        }

        if (!manifestValid)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.DataKindUnsupported,
                "manifest.data_kind",
                "a defined non-unknown TradeIR v1 data kind",
                EnumValue(manifest.DataKind));
        }
        else if (requirementValid && manifest.DataKind != requirement.DataKind)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.DataKindUnsupported,
                "manifest.data_kind",
                EnumValue(requirement.DataKind),
                EnumValue(manifest.DataKind));
        }
    }

    private static void ValidateInstruments(
        DataRequirementV1 requirement,
        DataSourceCapabilityV1 capability,
        DataBindingManifestV1 manifest,
        List<DataAdmissionIssue> issues)
    {
        var requested = IndexInstruments(
            requirement.InstrumentSelector?.References,
            "requirement.instrument_selector.references",
            issues,
            requireAtLeastOne: true);
        var available = IndexInstruments(
            capability.AvailableInstruments,
            "capability.available_instruments",
            issues,
            requireAtLeastOne: false);
        var bound = IndexInstruments(
            manifest.BoundInstruments,
            "manifest.bound_instruments",
            issues,
            requireAtLeastOne: false);

        foreach (var (key, expected) in requested.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (!available.TryGetValue(key, out var offered))
            {
                Add(
                    issues,
                    DataAdmissionIssueCodes.InstrumentUnbound,
                    InstrumentPath("capability.available_instruments", key),
                    InstrumentValue(expected),
                    Missing);
            }
            else if (offered != expected)
            {
                Add(
                    issues,
                    DataAdmissionIssueCodes.InstrumentUnbound,
                    InstrumentPath("capability.available_instruments", key),
                    InstrumentValue(expected),
                    InstrumentValue(offered));
            }

            if (!bound.TryGetValue(key, out var binding))
            {
                Add(
                    issues,
                    DataAdmissionIssueCodes.InstrumentUnbound,
                    InstrumentPath("manifest.bound_instruments", key),
                    InstrumentValue(expected),
                    Missing);
            }
            else if (binding != expected)
            {
                Add(
                    issues,
                    DataAdmissionIssueCodes.InstrumentUnbound,
                    InstrumentPath("manifest.bound_instruments", key),
                    InstrumentValue(expected),
                    InstrumentValue(binding));
            }
        }

        foreach (var key in bound.Keys.Except(requested.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.InstrumentUnbound,
                InstrumentPath("manifest.bound_instruments", key),
                "<not bound>",
                InstrumentValue(bound[key]));
        }
    }

    private static Dictionary<string, SourceIndependentInstrumentRef> IndexInstruments(
        IReadOnlyList<SourceIndependentInstrumentRef>? instruments,
        string path,
        List<DataAdmissionIssue> issues,
        bool requireAtLeastOne)
    {
        var result = new Dictionary<string, SourceIndependentInstrumentRef>(StringComparer.Ordinal);
        if (instruments is null || instruments.Count == 0)
        {
            if (requireAtLeastOne)
            {
                Add(
                    issues,
                    DataAdmissionIssueCodes.InstrumentUnbound,
                    path,
                    "at least one source-independent instrument reference",
                    Missing);
            }

            return result;
        }

        for (var index = 0; index < instruments.Count; index++)
        {
            var instrument = instruments[index];
            var itemPath = $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (!IsValidInstrument(instrument))
            {
                Add(
                    issues,
                    DataAdmissionIssueCodes.InstrumentUnbound,
                    itemPath,
                    "a complete source-independent instrument reference",
                    InstrumentValue(instrument));
                continue;
            }

            if (!result.TryAdd(instrument.InstrumentKey, instrument))
            {
                Add(
                    issues,
                    DataAdmissionIssueCodes.InstrumentUnbound,
                    itemPath + ".instrument_key",
                    "a unique instrument key",
                    instrument.InstrumentKey);
            }
        }

        return result;
    }

    private static bool IsValidInstrument(SourceIndependentInstrumentRef? instrument) =>
        instrument is not null &&
        !string.IsNullOrWhiteSpace(instrument.InstrumentKey) &&
        Enum.IsDefined(instrument.AssetClass) &&
        instrument.AssetClass != TradingTerminal.Core.Domain.AssetClass.Unknown &&
        !string.IsNullOrWhiteSpace(instrument.Symbol) &&
        !string.IsNullOrWhiteSpace(instrument.Venue) &&
        !string.IsNullOrWhiteSpace(instrument.Currency);

    private static void ValidateSchemas(
        DataRequirementV1 requirement,
        DataSourceCapabilityV1 capability,
        DataBindingManifestV1 manifest,
        List<DataAdmissionIssue> issues)
    {
        ValidateSchema(requirement.EventSchema, "requirement.event_schema", issues);
        ValidateSchema(capability.EventSchema, "capability.event_schema", issues);
        ValidateSchema(manifest.EventSchema, "manifest.event_schema", issues);

        CompareSchemaDefinition(
            requirement.EventSchema,
            capability.EventSchema,
            "capability.event_schema",
            issues);
        CompareSchemaDefinition(
            requirement.EventSchema,
            manifest.EventSchema,
            "manifest.event_schema",
            issues);

        if (!IsSha256(manifest.SchemaHashSha256))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SchemaVersionUnsupported,
                "manifest.schema_hash_sha256",
                "a lowercase SHA-256 digest",
                Text(manifest.SchemaHashSha256));
        }
        else if (requirement.EventSchema is not null &&
                 !string.Equals(
                     manifest.SchemaHashSha256,
                     requirement.EventSchema.SchemaHashSha256,
                     StringComparison.Ordinal))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SchemaVersionUnsupported,
                "manifest.schema_hash_sha256",
                Text(requirement.EventSchema.SchemaHashSha256),
                manifest.SchemaHashSha256);
        }
    }

    private static void ValidateSchema(
        CanonicalEventSchemaV1? schema,
        string path,
        List<DataAdmissionIssue> issues)
    {
        if (schema is null)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SchemaVersionUnsupported,
                path,
                nameof(CanonicalEventSchemaV1),
                Missing);
            return;
        }

        RequiredSchemaText(schema.SchemaId, path + ".schema_id", issues);
        if (schema.SchemaVersion <= 0)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SchemaVersionUnsupported,
                path + ".schema_version",
                "a positive schema version",
                schema.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        }

        if (!IsSha256(schema.SchemaHashSha256))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SchemaVersionUnsupported,
                path + ".schema_hash_sha256",
                "a lowercase SHA-256 digest",
                Text(schema.SchemaHashSha256));
        }

        if (schema.PayloadFields is null || schema.PayloadFields.Count == 0)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SchemaVersionUnsupported,
                path + ".payload_fields",
                "at least one payload field",
                Missing);
            return;
        }

        var fieldNames = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < schema.PayloadFields.Count; index++)
        {
            var field = schema.PayloadFields[index];
            var fieldPath = $"{path}.payload_fields[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (string.IsNullOrWhiteSpace(field))
            {
                Add(
                    issues,
                    DataAdmissionIssueCodes.SchemaVersionUnsupported,
                    fieldPath,
                    "a non-empty unique field name",
                    Text(field));
            }
            else if (!fieldNames.Add(field))
            {
                Add(
                    issues,
                    DataAdmissionIssueCodes.SchemaVersionUnsupported,
                    fieldPath,
                    "a non-empty unique field name",
                    field);
            }
        }
    }

    private static void CompareSchemaDefinition(
        CanonicalEventSchemaV1? expected,
        CanonicalEventSchemaV1? actual,
        string path,
        List<DataAdmissionIssue> issues)
    {
        if (expected is null || actual is null)
            return;

        CompareSchema(expected.SchemaId, actual.SchemaId, path + ".schema_id", issues);
        CompareSchema(expected.SchemaVersion, actual.SchemaVersion, path + ".schema_version", issues);
        CompareSchema(expected.SchemaHashSha256, actual.SchemaHashSha256, path + ".schema_hash_sha256", issues);

        var expectedPayload = expected.PayloadFields ?? [];
        var actualPayload = actual.PayloadFields ?? [];
        if (!expectedPayload.SequenceEqual(actualPayload, StringComparer.Ordinal))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SchemaVersionUnsupported,
                path + ".payload_fields",
                ListValue(expectedPayload),
                ListValue(actualPayload));
        }
    }

    private static void ValidateTemporalSemantics(
        DataRequirementV1 requirement,
        DataSourceCapabilityV1 capability,
        DataBindingManifestV1 manifest,
        List<DataAdmissionIssue> issues)
    {
        ValidateTemporal(requirement.TemporalSemantics, "requirement.temporal_semantics", issues);
        ValidateTemporal(capability.TemporalSemantics, "capability.temporal_semantics", issues);
        ValidateTemporal(manifest.TemporalSemantics, "manifest.temporal_semantics", issues);

        CompareTemporal(
            requirement.TemporalSemantics,
            capability.TemporalSemantics,
            "capability.temporal_semantics",
            issues);
        CompareTemporal(
            requirement.TemporalSemantics,
            manifest.TemporalSemantics,
            "manifest.temporal_semantics",
            issues);
    }

    private static void ValidateTemporal(
        DataTemporalSemanticsV1? temporal,
        string path,
        List<DataAdmissionIssue> issues)
    {
        if (temporal is null)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.TemporalSemanticsMismatch,
                path,
                nameof(DataTemporalSemanticsV1),
                Missing);
            return;
        }

        ValidateTemporalEnum(temporal.EventTimeBasis, path + ".event_time_basis", issues);
        ValidateTemporalEnum(temporal.TimestampPrecision, path + ".timestamp_precision", issues);
        ValidateTemporalEnum(temporal.Ordering, path + ".ordering", issues);

        var intervalBasis = temporal.EventTimeBasis is
            TradeIrEventTimeBasisV1.IntervalOpenUtc or TradeIrEventTimeBasisV1.IntervalCloseUtc;
        if (intervalBasis && (temporal.Interval is null || temporal.Interval <= TimeSpan.Zero))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.TemporalSemanticsMismatch,
                path + ".interval",
                "a positive interval for interval-based event time",
                TimeValue(temporal.Interval));
        }
        else if (!intervalBasis && temporal.Interval is not null)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.TemporalSemanticsMismatch,
                path + ".interval",
                Missing,
                TimeValue(temporal.Interval));
        }

    }

    private static void CompareTemporal(
        DataTemporalSemanticsV1? expected,
        DataTemporalSemanticsV1? actual,
        string path,
        List<DataAdmissionIssue> issues)
    {
        if (expected is null || actual is null)
            return;

        CompareTemporalValue(
            EnumValue(expected.EventTimeBasis),
            EnumValue(actual.EventTimeBasis),
            path + ".event_time_basis",
            issues);
        CompareTemporalValue(
            EnumValue(expected.TimestampPrecision),
            EnumValue(actual.TimestampPrecision),
            path + ".timestamp_precision",
            issues);
        CompareTemporalValue(
            EnumValue(expected.Ordering),
            EnumValue(actual.Ordering),
            path + ".ordering",
            issues);
        CompareTemporalValue(
            TimeValue(expected.Interval),
            TimeValue(actual.Interval),
            path + ".interval",
            issues);
        CompareTemporalValue(
            expected.RequireAuthoritativeEventTime.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            actual.RequireAuthoritativeEventTime.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            path + ".require_authoritative_event_time",
            issues);
        CompareTemporalValue(
            expected.RequirePointInTimeAvailability.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            actual.RequirePointInTimeAvailability.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            path + ".require_point_in_time_availability",
            issues);
    }

    private static void ValidateDataPolicies(
        DataRequirementV1 requirement,
        DataSourceCapabilityV1 capability,
        DataBindingManifestV1 manifest,
        List<DataAdmissionIssue> issues)
    {
        ValidatePolicy(
            requirement.NormalizationPolicy,
            "requirement.normalization_policy",
            DataAdmissionIssueCodes.DataKindUnsupported,
            issues);
        ValidatePolicy(
            capability.NormalizationPolicy,
            "capability.normalization_policy",
            DataAdmissionIssueCodes.DataKindUnsupported,
            issues);
        ValidatePolicy(
            manifest.NormalizationPolicy,
            "manifest.normalization_policy",
            DataAdmissionIssueCodes.DataKindUnsupported,
            issues);
        ComparePolicy(
            requirement.NormalizationPolicy,
            capability.NormalizationPolicy,
            "capability.normalization_policy",
            DataAdmissionIssueCodes.DataKindUnsupported,
            issues);
        ComparePolicy(
            requirement.NormalizationPolicy,
            manifest.NormalizationPolicy,
            "manifest.normalization_policy",
            DataAdmissionIssueCodes.DataKindUnsupported,
            issues);

        ValidatePolicy(
            requirement.MissingDataPolicy,
            "requirement.missing_data_policy",
            DataAdmissionIssueCodes.DataKindUnsupported,
            issues);
        ValidatePolicy(
            capability.MissingDataPolicy,
            "capability.missing_data_policy",
            DataAdmissionIssueCodes.DataKindUnsupported,
            issues);
        ValidatePolicy(
            manifest.MissingDataPolicy,
            "manifest.missing_data_policy",
            DataAdmissionIssueCodes.DataKindUnsupported,
            issues);
        ComparePolicy(
            requirement.MissingDataPolicy,
            capability.MissingDataPolicy,
            "capability.missing_data_policy",
            DataAdmissionIssueCodes.DataKindUnsupported,
            issues);
        ComparePolicy(
            requirement.MissingDataPolicy,
            manifest.MissingDataPolicy,
            "manifest.missing_data_policy",
            DataAdmissionIssueCodes.DataKindUnsupported,
            issues);

        ValidatePolicy(
            requirement.RevisionPolicy,
            "requirement.revision_policy",
            DataAdmissionIssueCodes.TemporalSemanticsMismatch,
            issues);
        ValidatePolicy(
            capability.RevisionPolicy,
            "capability.revision_policy",
            DataAdmissionIssueCodes.TemporalSemanticsMismatch,
            issues);
        ValidatePolicy(
            manifest.RevisionPolicy,
            "manifest.revision_policy",
            DataAdmissionIssueCodes.TemporalSemanticsMismatch,
            issues);
        ComparePolicy(
            requirement.RevisionPolicy,
            capability.RevisionPolicy,
            "capability.revision_policy",
            DataAdmissionIssueCodes.TemporalSemanticsMismatch,
            issues);
        ComparePolicy(
            requirement.RevisionPolicy,
            manifest.RevisionPolicy,
            "manifest.revision_policy",
            DataAdmissionIssueCodes.TemporalSemanticsMismatch,
            issues);
    }

    private static void ValidateSnapshotHashes(
        DataRequirementV1 requirement,
        DataBindingManifestV1 manifest,
        List<DataAdmissionIssue> issues)
    {
        var requiredHashPresent = !string.IsNullOrWhiteSpace(requirement.RequiredSnapshotHashSha256);
        var requiredHashValid = !requiredHashPresent || IsSha256(requirement.RequiredSnapshotHashSha256);
        var manifestHashValid = IsSha256(manifest.SnapshotHashSha256);
        if (!requiredHashValid)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SnapshotHashMissing,
                "requirement.required_snapshot_hash_sha256",
                "a lowercase SHA-256 digest",
                Text(requirement.RequiredSnapshotHashSha256));
        }

        if (!manifestHashValid)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SnapshotHashMissing,
                "manifest.snapshot_hash_sha256",
                "a lowercase SHA-256 digest",
                Text(manifest.SnapshotHashSha256));
        }
        else if (requiredHashPresent && requiredHashValid &&
                 !string.Equals(
                     manifest.SnapshotHashSha256,
                     requirement.RequiredSnapshotHashSha256,
                     StringComparison.Ordinal))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SnapshotHashMissing,
                "manifest.snapshot_hash_sha256",
                requirement.RequiredSnapshotHashSha256!,
                manifest.SnapshotHashSha256);
        }
    }

    private static void ValidateCapabilityBinding(
        DataRequirementV1 requirement,
        DataSourceCapabilityV1 capability,
        DataBindingManifestV1 manifest,
        List<DataAdmissionIssue> issues)
    {
        RequiredCapabilityText(requirement.RequirementId, "requirement.requirement_id", issues);
        RequiredCapabilityText(capability.CapabilityId, "capability.capability_id", issues);
        RequiredCapabilityText(manifest.BindingId, "manifest.binding_id", issues);
        RequiredCapabilityText(manifest.RequirementId, "manifest.requirement_id", issues);
        RequiredCapabilityText(manifest.CapabilityId, "manifest.capability_id", issues);

        CompareCapability(
            requirement.RequirementId,
            manifest.RequirementId,
            "manifest.requirement_id",
            issues);
        CompareCapability(
            capability.CapabilityId,
            manifest.CapabilityId,
            "manifest.capability_id",
            issues);

        if (capability.Revision <= 0)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                "capability.revision",
                "a positive capability revision",
                capability.Revision.ToString(CultureInfo.InvariantCulture));
        }

        if (manifest.CapabilityRevision != capability.Revision)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                "manifest.capability_revision",
                capability.Revision.ToString(CultureInfo.InvariantCulture),
                manifest.CapabilityRevision.ToString(CultureInfo.InvariantCulture));
        }

        ValidateCapturedAt(capability.CapturedAtUtc, "capability.captured_at_utc", issues);
        ValidateCapturedAt(manifest.CapabilityCapturedAtUtc, "manifest.capability_captured_at_utc", issues);
        if (manifest.CapabilityCapturedAtUtc != capability.CapturedAtUtc)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                "manifest.capability_captured_at_utc",
                DateValue(capability.CapturedAtUtc),
                DateValue(manifest.CapabilityCapturedAtUtc));
        }

        RequiredCapabilityText(capability.AdapterId, "capability.adapter_id", issues);
        RequiredCapabilityText(manifest.AdapterId, "manifest.adapter_id", issues);
        CompareCapability(capability.AdapterId, manifest.AdapterId, "manifest.adapter_id", issues);

        if (capability.AdapterVersion <= 0)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                "capability.adapter_version",
                "a positive adapter version",
                capability.AdapterVersion.ToString(CultureInfo.InvariantCulture));
        }

        if (manifest.AdapterVersion != capability.AdapterVersion)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                "manifest.adapter_version",
                capability.AdapterVersion.ToString(CultureInfo.InvariantCulture),
                manifest.AdapterVersion.ToString(CultureInfo.InvariantCulture));
        }

        var capabilityAdapterHashValid = IsSha256(capability.AdapterHashSha256);
        var manifestAdapterHashValid = IsSha256(manifest.AdapterHashSha256);
        if (!capabilityAdapterHashValid)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                "capability.adapter_hash_sha256",
                "a lowercase SHA-256 digest",
                Text(capability.AdapterHashSha256));
        }

        if (!manifestAdapterHashValid)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                "manifest.adapter_hash_sha256",
                "a lowercase SHA-256 digest",
                Text(manifest.AdapterHashSha256));
        }
        else if (capabilityAdapterHashValid &&
                 !string.Equals(
                     manifest.AdapterHashSha256,
                     capability.AdapterHashSha256,
                     StringComparison.Ordinal))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                "manifest.adapter_hash_sha256",
                capability.AdapterHashSha256,
                manifest.AdapterHashSha256);
        }
    }

    private static void ValidateCapturedAt(
        DateTimeOffset value,
        string path,
        List<DataAdmissionIssue> issues)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                path,
                "a non-default UTC timestamp",
                DateValue(value));
        }
    }

    private static void RequiredSchemaText(
        string? value,
        string path,
        List<DataAdmissionIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SchemaVersionUnsupported,
                path,
                Present,
                Missing);
        }
    }

    private static void RequiredCapabilityText(
        string? value,
        string path,
        List<DataAdmissionIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                path,
                Present,
                Missing);
        }
    }

    private static void CompareSchema<T>(
        T expected,
        T actual,
        string path,
        List<DataAdmissionIssue> issues)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.SchemaVersionUnsupported,
                path,
                Value(expected),
                Value(actual));
        }
    }

    private static void CompareTemporalValue(
        string expected,
        string actual,
        string path,
        List<DataAdmissionIssue> issues)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.TemporalSemanticsMismatch,
                path,
                expected,
                actual);
        }
    }

    private static void CompareCapability(
        string? expected,
        string? actual,
        string path,
        List<DataAdmissionIssue> issues)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Add(
                issues,
                DataAdmissionIssueCodes.CapabilityStale,
                path,
                Text(expected),
                Text(actual));
        }
    }

    private static void ValidatePolicy<T>(
        T value,
        string path,
        string code,
        List<DataAdmissionIssue> issues)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt64(value, CultureInfo.InvariantCulture) == 0)
        {
            Add(
                issues,
                code,
                path,
                "a defined non-unknown data policy",
                EnumValue(value));
        }
    }

    private static void ComparePolicy<T>(
        T expected,
        T actual,
        string path,
        string code,
        List<DataAdmissionIssue> issues)
        where T : struct, Enum
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            Add(issues, code, path, EnumValue(expected), EnumValue(actual));
    }

    private static void ValidateTemporalEnum<T>(
        T value,
        string path,
        List<DataAdmissionIssue> issues)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt64(value, CultureInfo.InvariantCulture) == 0)
        {
            Add(
                issues,
                DataAdmissionIssueCodes.TemporalSemanticsMismatch,
                path,
                "a defined non-unknown temporal value",
                EnumValue(value));
        }
    }

    private static bool IsDefined<T>(T value)
        where T : struct, Enum =>
        Enum.IsDefined(value) && Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
            return false;

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    private static void Add(
        List<DataAdmissionIssue> issues,
        string code,
        string path,
        string expected,
        string actual)
    {
        var issue = Issue(code, path, expected, actual);
        if (!issues.Contains(issue))
            issues.Add(issue);
    }

    private static DataAdmissionIssue Issue(
        string code,
        string path,
        string expected,
        string actual) =>
        new(
            code,
            path,
            expected,
            actual,
            $"Expected '{path}' to be '{expected}', but found '{actual}'.");

    private static DataAdmissionResult Result(
        DataRequirementV1? requirement,
        DataSourceCapabilityV1? capability,
        DataBindingManifestV1? manifest,
        IReadOnlyList<DataAdmissionIssue> issues) =>
        new(
            requirement?.RequirementId ?? string.Empty,
            capability?.CapabilityId ?? string.Empty,
            manifest?.BindingId ?? string.Empty,
            issues);

    private static string InstrumentPath(string prefix, string key) => $"{prefix}[{key}]";

    private static string InstrumentValue(SourceIndependentInstrumentRef? instrument) =>
        instrument is null
            ? Missing
            : string.Join(
                '|',
                Text(instrument.InstrumentKey),
                EnumValue(instrument.AssetClass),
                Text(instrument.Symbol),
                Text(instrument.Venue),
                Text(instrument.Currency));

    private static string ListValue(IEnumerable<string> values) =>
        "[" + string.Join(',', values.Select(Text)) + "]";

    private static string TimeValue(TimeSpan? value) =>
        value?.ToString("c", CultureInfo.InvariantCulture) ?? Missing;

    private static string DateValue(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string EnumValue<T>(T value)
        where T : struct, Enum =>
        Enum.IsDefined(value)
            ? value.ToString()
            : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Missing : value;

    private static string Value<T>(T value) =>
        value switch
        {
            null => Missing,
            string text => Text(text),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? Missing,
        };
}
