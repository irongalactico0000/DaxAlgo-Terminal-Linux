namespace TradingTerminal.Core.Strategies.Definition;

/// <summary>
/// Structural rules shared by trusted operator output validation and isolated extension-module
/// interfaces. These rules validate the portable type declaration, not the meaning of custom
/// type ids; output-role compatibility is checked separately.
/// </summary>
internal static class StrategyValueTypeRulesV1
{
    public static void Validate(
        StrategyValueTypeV1? valueType,
        string path,
        Action<string, string, string> addIssue)
    {
        ArgumentNullException.ThrowIfNull(addIssue);
        if (valueType is null)
        {
            addIssue("value_type_required", path, "A typed value declaration is required.");
            return;
        }

        if (!IsVersionedTypeId(valueType.TypeId))
            addIssue("value_type_id_invalid", $"{path}.typeId",
                "Type id must be a lowercase namespaced id followed by a positive @version.");
        if (!IsPortableText(valueType.UnitTag, 128))
            addIssue("value_unit_invalid", $"{path}.unitTag", "A trimmed, bounded unit tag without control characters is required.");
        if (!Enum.IsDefined(valueType.Availability))
            addIssue("value_availability_invalid", $"{path}.availability", "Availability must be a defined value.");

        if (valueType.Axes is null)
        {
            addIssue("value_axes_required", $"{path}.axes", "An axis collection is required; use an empty collection for a scalar.");
            return;
        }

        var axisIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var axis in valueType.Axes)
        {
            if (axis is null)
            {
                addIssue("value_axis_invalid", $"{path}.axes", "Axis declarations cannot be null.");
                continue;
            }

            var axisPath = $"{path}.axes[{axis.AxisId}]";
            if (!IsStableName(axis.AxisId))
                addIssue("value_axis_invalid", $"{axisPath}.axisId", "Axis id must be a lowercase stable name.");
            else if (!axisIds.Add(axis.AxisId))
                addIssue("value_axis_duplicate", axisPath, "Axis ids must be unique within a value type.");
            if (!IsPortableText(axis.DomainId, 256))
                addIssue("value_axis_invalid", $"{axisPath}.domainId", "A trimmed, bounded axis-domain identity without control characters is required.");
            if (axis.Cardinality is <= 0)
                addIssue("value_axis_invalid", $"{axisPath}.cardinality", "Axis cardinality must be positive when specified.");
        }

        if (!valueType.Axes.Where(static axis => axis is not null)
                .Select(static axis => axis.AxisId)
                .SequenceEqual(valueType.Axes.Where(static axis => axis is not null)
                    .Select(static axis => axis.AxisId)
                    .Order(StringComparer.Ordinal), StringComparer.Ordinal))
            addIssue("value_axes_noncanonical", $"{path}.axes", "Axes must be ordered by axis id.");
    }

    public static bool IsCompatible(StrategyIrOutputKindV1 kind, StrategyValueTypeV1 valueType) => kind switch
    {
        StrategyIrOutputKindV1.Signal =>
            valueType.TypeId is StrategyIrTypeIdsV1.Boolean or StrategyIrTypeIdsV1.Number,
        StrategyIrOutputKindV1.Target =>
            valueType.TypeId == StrategyIrTypeIdsV1.PortfolioTarget &&
            valueType.UnitTag == "position.quantity" && !valueType.Nullable,
        StrategyIrOutputKindV1.QuoteIntent =>
            valueType.TypeId == StrategyIrTypeIdsV1.QuoteIntent &&
            valueType.UnitTag == "unitless" && !valueType.Nullable,
        StrategyIrOutputKindV1.OrderIntent =>
            valueType.TypeId == StrategyIrTypeIdsV1.OrderIntent &&
            valueType.UnitTag == "unitless" && !valueType.Nullable,
        _ => false,
    };

    private static bool IsVersionedTypeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.LastIndexOf('@');
        if (separator <= 0 || separator == value.Length - 1) return false;
        if (value.Length > 128) return false;
        var name = value[..separator];
        var version = value.AsSpan(separator + 1);
        var segments = name.Split('.');
        if (segments.Length < 2 || segments.Any(static segment => !IsStableName(segment)))
            return false;
        return int.TryParse(version, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0;
    }

    private static bool IsStableName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static bool IsPortableText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        StringComparer.Ordinal.Equals(value, value.Trim()) &&
        !value.Any(char.IsControl);
}
