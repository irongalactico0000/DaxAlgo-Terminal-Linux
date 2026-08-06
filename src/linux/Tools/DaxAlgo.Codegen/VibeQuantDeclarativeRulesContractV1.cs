using System.Text.Json;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Deterministic structural enforcement for the closed Vibe Quant Declarative Rules v1 document.
/// This deliberately stops before catalog semantics, expression typing, causality proof, lowering,
/// data admission, or execution admission.
/// </summary>
internal static class VibeQuantDeclarativeRulesContractV1
{
    public const string SchemaVersion = "vibe-quant/declarative-rules/v1";
    private const int MaxExpressionDepth = 64;

    public static IReadOnlyList<StrategyCandidateGenerationIssueV1> Validate(
        JsonElement document,
        string expectedStrategyId)
    {
        var issues = new List<StrategyCandidateGenerationIssueV1>();
        if (!Object(document, "artifact.document",
                ["$schema", "schemaVersion", "strategy", "clock", "operatorCatalog", "parameters",
                    "dataRequirements", "indicators", "entryRules", "exitRules", "risk", "outputs"],
                ["schemaVersion", "strategy", "clock", "operatorCatalog", "parameters",
                    "dataRequirements", "indicators", "entryRules", "exitRules", "risk", "outputs"], issues))
            return issues;

        if (document.TryGetProperty("$schema", out var schemaReference) &&
            (schemaReference.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(schemaReference.GetString())))
            issues.Add(TypeError("artifact.document.$schema", "a non-empty URI-reference string"));
        ExactText(document, "schemaVersion", SchemaVersion, "artifact.document.schemaVersion", issues);
        ValidateStrategy(document, expectedStrategyId, issues);
        ValidateClock(document, issues);
        ValidateOperatorCatalog(document, issues);
        ValidateParameters(document, issues);
        ValidateDataRequirements(document, issues);
        ValidateIndicators(document, issues);
        ValidateEntryRules(document, issues);
        ValidateExitRules(document, issues);
        ValidateRisk(document, issues);
        ValidateOutputs(document, issues);
        return issues;
    }

    private static void ValidateStrategy(
        JsonElement root,
        string expectedStrategyId,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!ChildObject(root, "strategy", "artifact.document.strategy",
                ["id", "version", "displayName", "summary"],
                ["id", "version", "displayName", "summary"], issues, out var strategy)) return;
        Identifier(strategy, "id", "artifact.document.strategy.id", issues);
        Text(strategy, "version", "artifact.document.strategy.version", issues);
        Text(strategy, "displayName", "artifact.document.strategy.displayName", issues);
        Text(strategy, "summary", "artifact.document.strategy.summary", issues);
        if (strategy.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
            !string.Equals(id.GetString(), expectedStrategyId, StringComparison.Ordinal))
            issues.Add(Error("LANE_SPEC_STRATEGY_ID_CHANGED", "artifact.document.strategy.id",
                "The declarative artifact must preserve the exact host-owned strategy id."));
    }

    private static void ValidateClock(
        JsonElement root,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!ChildObject(root, "clock", "artifact.document.clock",
                ["basis", "timezone", "sessionCalendar", "decisionTiming", "interval"],
                ["basis", "timezone", "sessionCalendar", "decisionTiming", "interval"], issues, out var clock)) return;
        ExactText(clock, "basis", "eventTime", "artifact.document.clock.basis", issues);
        Text(clock, "timezone", "artifact.document.clock.timezone", issues);
        Text(clock, "sessionCalendar", "artifact.document.clock.sessionCalendar", issues);
        EnumText(clock, "decisionTiming", ["onEvent", "intervalClose"],
            "artifact.document.clock.decisionTiming", issues);
        if (clock.TryGetProperty("interval", out var interval))
        {
            var timing = clock.TryGetProperty("decisionTiming", out var decisionTiming) &&
                decisionTiming.ValueKind == JsonValueKind.String
                    ? decisionTiming.GetString()
                    : null;
            if (timing == "onEvent" && interval.ValueKind != JsonValueKind.Null)
                issues.Add(Error("LANE_SPEC_VALUE_INVALID", "artifact.document.clock.interval",
                    "onEvent decision timing requires a null interval."));
            else if (timing == "intervalClose" &&
                     (interval.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(interval.GetString())))
                issues.Add(TypeError("artifact.document.clock.interval", "a non-empty interval string"));
            else if (timing is null && interval.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                issues.Add(TypeError("artifact.document.clock.interval", "a non-empty string or null"));
        }
    }

    private static void ValidateOperatorCatalog(
        JsonElement root,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!ChildObject(root, "operatorCatalog", "artifact.document.operatorCatalog",
                ["catalogId", "catalogVersion", "catalogHashSha256"],
                ["catalogId", "catalogVersion", "catalogHashSha256"], issues, out var catalog)) return;
        Text(catalog, "catalogId", "artifact.document.operatorCatalog.catalogId", issues);
        Text(catalog, "catalogVersion", "artifact.document.operatorCatalog.catalogVersion", issues);
        Sha256(catalog, "catalogHashSha256", "artifact.document.operatorCatalog.catalogHashSha256", issues);
    }

    private static void ValidateParameters(
        JsonElement root,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!ChildArray(root, "parameters", "artifact.document.parameters", 0, issues, out var array)) return;
        ValidateObjectArray(array, "artifact.document.parameters",
            ["id", "type", "description", "default", "minimum", "maximum", "step", "choices"],
            ["id", "type", "description", "default", "minimum", "maximum", "step", "choices"],
            issues, (item, path) =>
            {
                Identifier(item, "id", path + ".id", issues);
                EnumText(item, "type", ["boolean", "integer", "number", "text", "choice"], path + ".type", issues);
                Text(item, "description", path + ".description", issues);
                ValidateParameterValues(item, path, issues);
            });
        UniqueIds(array, "artifact.document.parameters", issues);
    }

    private static void ValidateParameterValues(
        JsonElement parameter,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!parameter.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
            return;
        var type = typeValue.GetString();
        if (!parameter.TryGetProperty("default", out var defaultValue)) return;
        var defaultValid = type switch
        {
            "boolean" => defaultValue.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => IsInteger(defaultValue),
            "number" => defaultValue.ValueKind == JsonValueKind.Number,
            "text" or "choice" => defaultValue.ValueKind == JsonValueKind.String,
            _ => true,
        };
        if (!defaultValid) issues.Add(TypeError(path + ".default", $"a {type} default value"));

        foreach (var property in new[] { "minimum", "maximum", "step" })
        {
            if (!parameter.TryGetProperty(property, out var value)) continue;
            var valid = value.ValueKind == JsonValueKind.Null || type switch
            {
                "integer" => IsInteger(value),
                "number" => value.ValueKind == JsonValueKind.Number,
                _ => false,
            };
            if (!valid) issues.Add(TypeError(path + "." + property,
                type is "integer" or "number" ? $"a {type} or null" : "null"));
            if (property == "step" && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var step) && step <= 0)
                issues.Add(Error("LANE_SPEC_VALUE_INVALID", path + ".step", "A parameter step must be greater than zero."));
        }

        if (!parameter.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            issues.Add(TypeError(path + ".choices", "an array"));
            return;
        }
        ValidateStringArray(choices, path + ".choices", type == "choice" ? 1 : 0, issues, identifiers: false);
        if (type != "choice" && choices.GetArrayLength() != 0)
            issues.Add(Error("LANE_SPEC_VALUE_INVALID", path + ".choices",
                "Only choice parameters may declare choices."));
        if (type == "choice" && defaultValue.ValueKind == JsonValueKind.String &&
            !choices.EnumerateArray().Any(choice => choice.ValueKind == JsonValueKind.String &&
                string.Equals(choice.GetString(), defaultValue.GetString(), StringComparison.Ordinal)))
            issues.Add(Error("LANE_SPEC_VALUE_INVALID", path + ".default",
                "A choice default must be one of the declared choices."));
    }

    private static void ValidateDataRequirements(
        JsonElement root,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!ChildArray(root, "dataRequirements", "artifact.document.dataRequirements", 1, issues, out var array)) return;
        ValidateObjectArray(array, "artifact.document.dataRequirements",
            ["id", "dataKind", "instrumentSelector", "eventSchema", "temporalSemantics",
                "normalizationPolicy", "missingDataPolicy", "revisionPolicy", "requiredSnapshotHashSha256"],
            ["id", "dataKind", "instrumentSelector", "eventSchema", "temporalSemantics",
                "normalizationPolicy", "missingDataPolicy", "revisionPolicy", "requiredSnapshotHashSha256"],
            issues, (item, path) =>
            {
                Identifier(item, "id", path + ".id", issues);
                EnumText(item, "dataKind",
                    ["quoteL1", "trade", "bar", "depth", "scheduledEvent", "fundamental", "corporateEvent", "news", "alternative"],
                    path + ".dataKind", issues);
                EnumText(item, "normalizationPolicy",
                    ["rawUnadjusted", "splitAdjusted", "totalReturnAdjusted", "sourceCanonical"],
                    path + ".normalizationPolicy", issues);
                EnumText(item, "missingDataPolicy", ["reject", "preserveMissing", "forwardFill"],
                    path + ".missingDataPolicy", issues);
                EnumText(item, "revisionPolicy", ["firstPublishedOnly", "latestAvailableAtDecisionTime", "allRevisions"],
                    path + ".revisionPolicy", issues);
                if (item.TryGetProperty("requiredSnapshotHashSha256", out var snapshot) &&
                    snapshot.ValueKind != JsonValueKind.Null)
                    Sha256(item, "requiredSnapshotHashSha256", path + ".requiredSnapshotHashSha256", issues);
                ValidateInstrumentSelector(item, path, issues);
                ValidateEventSchema(item, path, issues);
                ValidateTemporalSemantics(item, path, issues);
            });
        UniqueIds(array, "artifact.document.dataRequirements", issues);
    }

    private static void ValidateInstrumentSelector(
        JsonElement requirement,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        var selectorPath = path + ".instrumentSelector";
        if (!ChildObject(requirement, "instrumentSelector", selectorPath,
                ["mode", "references", "universeId"], ["mode", "references", "universeId"], issues,
                out var selector)) return;
        EnumText(selector, "mode", ["references", "universe"], selectorPath + ".mode", issues);
        if (ChildArray(selector, "references", selectorPath + ".references", 0, issues, out var references))
            ValidateObjectArray(references, selectorPath + ".references",
                ["instrumentKey", "assetClass", "symbol", "venue", "currency"],
                ["instrumentKey", "assetClass", "symbol", "venue", "currency"], issues,
                (item, itemPath) =>
                {
                    Text(item, "instrumentKey", itemPath + ".instrumentKey", issues);
                    EnumText(item, "assetClass", ["equity", "future", "forex", "crypto", "option", "index"],
                        itemPath + ".assetClass", issues);
                    Text(item, "symbol", itemPath + ".symbol", issues);
                    Text(item, "venue", itemPath + ".venue", issues);
                    Text(item, "currency", itemPath + ".currency", issues);
                });
        var mode = selector.TryGetProperty("mode", out var modeValue) && modeValue.ValueKind == JsonValueKind.String
            ? modeValue.GetString()
            : null;
        selector.TryGetProperty("universeId", out var universeId);
        if (universeId.ValueKind != JsonValueKind.Null &&
            (universeId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(universeId.GetString())))
            issues.Add(TypeError(selectorPath + ".universeId", "a non-empty string or null"));
        if (mode == "references")
        {
            if (references.ValueKind == JsonValueKind.Array && references.GetArrayLength() == 0)
                issues.Add(Error("LANE_SPEC_ARRAY_EMPTY", selectorPath + ".references",
                    "Reference mode requires at least one instrument reference."));
            if (universeId.ValueKind != JsonValueKind.Null)
                issues.Add(Error("LANE_SPEC_VALUE_INVALID", selectorPath + ".universeId",
                    "Reference mode requires a null universeId."));
        }
        else if (mode == "universe")
        {
            if (references.ValueKind == JsonValueKind.Array && references.GetArrayLength() != 0)
                issues.Add(Error("LANE_SPEC_VALUE_INVALID", selectorPath + ".references",
                    "Universe mode requires an empty references array."));
            if (universeId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(universeId.GetString()))
                issues.Add(TypeError(selectorPath + ".universeId", "a non-empty universe id"));
        }
    }

    private static void ValidateEventSchema(
        JsonElement requirement,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        var schemaPath = path + ".eventSchema";
        if (!ChildObject(requirement, "eventSchema", schemaPath,
                ["schemaId", "schemaVersion", "schemaHashSha256", "payloadFields"],
                ["schemaId", "schemaVersion", "schemaHashSha256", "payloadFields"], issues, out var schema)) return;
        Text(schema, "schemaId", schemaPath + ".schemaId", issues);
        if (!schema.TryGetProperty("schemaVersion", out var schemaVersion) ||
            !IsInteger(schemaVersion) || schemaVersion.GetInt64() < 1)
            issues.Add(TypeError(schemaPath + ".schemaVersion", "an integer greater than or equal to 1"));
        Sha256(schema, "schemaHashSha256", schemaPath + ".schemaHashSha256", issues);
        if (!schema.TryGetProperty("payloadFields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            issues.Add(TypeError(schemaPath + ".payloadFields", "an array"));
        else
            ValidateStringArray(fields, schemaPath + ".payloadFields", 1, issues, identifiers: true);
    }

    private static void ValidateTemporalSemantics(
        JsonElement requirement,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        var temporalPath = path + ".temporalSemantics";
        if (!ChildObject(requirement, "temporalSemantics", temporalPath,
                ["eventTimeBasis", "interval", "requireAuthoritativeEventTime", "requirePointInTimeAvailability"],
                ["eventTimeBasis", "interval", "requireAuthoritativeEventTime", "requirePointInTimeAvailability"],
                issues, out var temporal)) return;
        EnumText(temporal, "eventTimeBasis",
            ["occurredAtUtc", "intervalOpenUtc", "intervalCloseUtc", "effectiveAtUtc", "publishedAtUtc"],
            temporalPath + ".eventTimeBasis", issues);
        if (temporal.TryGetProperty("interval", out var interval) &&
            interval.ValueKind != JsonValueKind.Null &&
            (interval.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(interval.GetString())))
            issues.Add(TypeError(temporalPath + ".interval", "a non-empty string or null"));
        ExactBoolean(temporal, "requireAuthoritativeEventTime", true,
            temporalPath + ".requireAuthoritativeEventTime", issues);
        ExactBoolean(temporal, "requirePointInTimeAvailability", true,
            temporalPath + ".requirePointInTimeAvailability", issues);
    }

    private static void ValidateIndicators(
        JsonElement root,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!ChildArray(root, "indicators", "artifact.document.indicators", 0, issues, out var array)) return;
        ValidateObjectArray(array, "artifact.document.indicators",
            ["id", "operatorId", "operatorVersion", "inputs", "arguments", "outputType"],
            ["id", "operatorId", "operatorVersion", "inputs", "arguments", "outputType"], issues,
            (item, path) =>
            {
                Identifier(item, "id", path + ".id", issues);
                Text(item, "operatorId", path + ".operatorId", issues);
                Text(item, "operatorVersion", path + ".operatorVersion", issues);
                EnumText(item, "outputType", ["boolean", "integer", "number", "text"],
                    path + ".outputType", issues);
                ValidateNamedExpressions(item, "inputs", path, issues);
                ValidateNamedExpressions(item, "arguments", path, issues);
            });
        UniqueIds(array, "artifact.document.indicators", issues);
    }

    private static void ValidateNamedExpressions(
        JsonElement parent,
        string property,
        string parentPath,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        var path = parentPath + "." + property;
        if (!ChildArray(parent, property, path, 0, issues, out var array)) return;
        ValidateObjectArray(array, path, ["name", "value"], ["name", "value"], issues,
            (item, itemPath) =>
            {
                Identifier(item, "name", itemPath + ".name", issues);
                if (item.TryGetProperty("value", out var expression))
                    ValidateExpression(expression, itemPath + ".value", issues, 0);
            });
    }

    private static void ValidateEntryRules(
        JsonElement root,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!ChildArray(root, "entryRules", "artifact.document.entryRules", 1, issues, out var array)) return;
        ValidateObjectArray(array, "artifact.document.entryRules",
            ["id", "direction", "condition", "quantity", "order", "tags"],
            ["id", "direction", "condition", "quantity", "order", "tags"], issues,
            (item, path) =>
            {
                Identifier(item, "id", path + ".id", issues);
                EnumText(item, "direction", ["long", "short"], path + ".direction", issues);
                if (item.TryGetProperty("condition", out var condition)) ValidateExpression(condition, path + ".condition", issues, 0);
                if (item.TryGetProperty("quantity", out var quantity)) ValidateExpression(quantity, path + ".quantity", issues, 0);
                ValidateTags(item, path, issues);
                ValidateOrder(item, path, issues);
            });
        UniqueIds(array, "artifact.document.entryRules", issues);
    }

    private static void ValidateExitRules(
        JsonElement root,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!ChildArray(root, "exitRules", "artifact.document.exitRules", 1, issues, out var array)) return;
        ValidateObjectArray(array, "artifact.document.exitRules",
            ["id", "appliesTo", "condition", "action", "quantity", "order", "tags"],
            ["id", "appliesTo", "condition", "action", "quantity", "order", "tags"], issues,
            (item, path) =>
            {
                Identifier(item, "id", path + ".id", issues);
                EnumText(item, "appliesTo", ["long", "short", "both"], path + ".appliesTo", issues);
                EnumText(item, "action", ["closePosition", "reducePosition"], path + ".action", issues);
                if (item.TryGetProperty("condition", out var condition)) ValidateExpression(condition, path + ".condition", issues, 0);
                if (item.TryGetProperty("quantity", out var quantity) && quantity.ValueKind != JsonValueKind.Null)
                    ValidateExpression(quantity, path + ".quantity", issues, 0);
                if (item.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String &&
                    item.TryGetProperty("quantity", out quantity))
                {
                    if (action.GetString() == "closePosition" && quantity.ValueKind != JsonValueKind.Null)
                        issues.Add(Error("LANE_SPEC_VALUE_INVALID", path + ".quantity",
                            "closePosition requires a null quantity."));
                    if (action.GetString() == "reducePosition" && quantity.ValueKind == JsonValueKind.Null)
                        issues.Add(Error("LANE_SPEC_VALUE_INVALID", path + ".quantity",
                            "reducePosition requires a quantity expression."));
                }
                ValidateTags(item, path, issues);
                ValidateOrder(item, path, issues);
            });
        UniqueIds(array, "artifact.document.exitRules", issues);
    }

    private static void ValidateOrder(
        JsonElement rule,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        var orderPath = path + ".order";
        if (!ChildObject(rule, "order", orderPath,
                ["type", "timeInForce", "limitPrice", "stopPrice"],
                ["type", "timeInForce", "limitPrice", "stopPrice"], issues, out var order)) return;
        EnumText(order, "type", ["market", "limit", "stop", "stopLimit"], orderPath + ".type", issues);
        EnumText(order, "timeInForce", ["day", "goodTilCanceled", "immediateOrCancel", "fillOrKill"],
            orderPath + ".timeInForce", issues);
        foreach (var property in new[] { "limitPrice", "stopPrice" })
            if (order.TryGetProperty(property, out var expression) && expression.ValueKind != JsonValueKind.Null)
                ValidateExpression(expression, orderPath + "." + property, issues, 0);
        if (order.TryGetProperty("type", out var orderType) && orderType.ValueKind == JsonValueKind.String &&
            order.TryGetProperty("limitPrice", out var limitPrice) &&
            order.TryGetProperty("stopPrice", out var stopPrice))
        {
            var requiresLimit = orderType.GetString() is "limit" or "stopLimit";
            var requiresStop = orderType.GetString() is "stop" or "stopLimit";
            ValidateNullableExpressionRequirement(limitPrice, requiresLimit, orderPath + ".limitPrice", issues);
            ValidateNullableExpressionRequirement(stopPrice, requiresStop, orderPath + ".stopPrice", issues);
        }
    }

    private static void ValidateRisk(
        JsonElement root,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!ChildObject(root, "risk", "artifact.document.risk",
                ["maxConcurrentPositions", "maxOrdersPerSession", "maxGrossExposure", "stopLoss", "takeProfit", "flattenAtSessionEnd"],
                ["maxConcurrentPositions", "maxOrdersPerSession", "maxGrossExposure", "stopLoss", "takeProfit", "flattenAtSessionEnd"],
                issues, out var risk)) return;
        PositiveInteger(risk, "maxConcurrentPositions", "artifact.document.risk.maxConcurrentPositions", false, issues);
        PositiveInteger(risk, "maxOrdersPerSession", "artifact.document.risk.maxOrdersPerSession", true, issues);
        if (risk.TryGetProperty("maxGrossExposure", out var exposure) && exposure.ValueKind != JsonValueKind.Null)
            ValidateExpression(exposure, "artifact.document.risk.maxGrossExposure", issues, 0);
        ValidateProtectiveExit(risk, "stopLoss", issues);
        ValidateProtectiveExit(risk, "takeProfit", issues);
        Boolean(risk, "flattenAtSessionEnd", "artifact.document.risk.flattenAtSessionEnd", issues);
    }

    private static void ValidateProtectiveExit(
        JsonElement risk,
        string property,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!risk.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return;
        var path = "artifact.document.risk." + property;
        if (!Object(value, path, ["kind", "value"], ["kind", "value"], issues)) return;
        EnumText(value, "kind", ["price", "percent", "atrMultiple"], path + ".kind", issues);
        if (value.TryGetProperty("value", out var expression)) ValidateExpression(expression, path + ".value", issues, 0);
    }

    private static void ValidateOutputs(
        JsonElement root,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!ChildArray(root, "outputs", "artifact.document.outputs", 1, issues, out var array)) return;
        ValidateObjectArray(array, "artifact.document.outputs", ["id", "kind", "source"], ["id", "kind", "source"],
            issues, (item, path) =>
            {
                Identifier(item, "id", path + ".id", issues);
                EnumText(item, "kind", ["indicator", "signal", "orderIntent", "diagnostic"], path + ".kind", issues);
                if (ChildObject(item, "source", path + ".source", ["kind", "id"], ["kind", "id"], issues, out var source))
                {
                    EnumText(source, "kind", ["indicator", "entryRule", "exitRule"], path + ".source.kind", issues);
                    Identifier(source, "id", path + ".source.id", issues);
                }
            });
        UniqueIds(array, "artifact.document.outputs", issues);
    }

    private static void ValidateExpression(
        JsonElement expression,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues,
        int depth)
    {
        if (depth > MaxExpressionDepth)
        {
            issues.Add(Error("LANE_SPEC_EXPRESSION_TOO_DEEP", path, "Expression nesting exceeds the v1 safety limit."));
            return;
        }
        if (expression.ValueKind != JsonValueKind.Object)
        {
            issues.Add(TypeError(path, "an expression object"));
            return;
        }
        if (!expression.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String)
        {
            issues.Add(Error("LANE_SPEC_PROPERTY_REQUIRED", path + ".kind", "An expression kind is required."));
            return;
        }
        var kind = kindValue.GetString();
        switch (kind)
        {
            case "literal":
                if (Object(expression, path, ["kind", "value"], ["kind", "value"], issues) &&
                    expression.TryGetProperty("value", out var literal) &&
                    literal.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or
                        JsonValueKind.True or JsonValueKind.False))
                    issues.Add(TypeError(path + ".value", "a boolean, number, or string literal"));
                break;
            case "reference":
                if (Object(expression, path, ["kind", "source", "id", "field"], ["kind", "source", "id", "field"], issues))
                {
                    EnumText(expression, "source", ["parameter", "data", "indicator", "position", "clock"],
                        path + ".source", issues);
                    Identifier(expression, "id", path + ".id", issues);
                    if (expression.TryGetProperty("field", out var field) && field.ValueKind != JsonValueKind.Null &&
                        (field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString())))
                        issues.Add(TypeError(path + ".field", "a non-empty string or null"));
                }
                break;
            case "unary":
                if (Object(expression, path, ["kind", "operator", "operand"], ["kind", "operator", "operand"], issues) &&
                    expression.TryGetProperty("operand", out var operand))
                {
                    EnumText(expression, "operator", ["not", "negate", "absolute"], path + ".operator", issues);
                    ValidateExpression(operand, path + ".operand", issues, depth + 1);
                }
                break;
            case "binary":
                if (Object(expression, path, ["kind", "operator", "left", "right"], ["kind", "operator", "left", "right"], issues))
                {
                    EnumText(expression, "operator",
                        ["equal", "notEqual", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual",
                            "add", "subtract", "multiply", "divide", "crossesAbove", "crossesBelow"],
                        path + ".operator", issues);
                    if (expression.TryGetProperty("left", out var left)) ValidateExpression(left, path + ".left", issues, depth + 1);
                    if (expression.TryGetProperty("right", out var right)) ValidateExpression(right, path + ".right", issues, depth + 1);
                }
                break;
            case "logical":
                if (Object(expression, path, ["kind", "operator", "operands"], ["kind", "operator", "operands"], issues) &&
                    ChildArray(expression, "operands", path + ".operands", 1, issues, out var operands))
                {
                    EnumText(expression, "operator", ["all", "any"], path + ".operator", issues);
                    var index = 0;
                    foreach (var logicalOperand in operands.EnumerateArray())
                        ValidateExpression(logicalOperand, $"{path}.operands[{index++}]", issues, depth + 1);
                }
                break;
            default:
                issues.Add(Error("LANE_SPEC_VALUE_INVALID", path + ".kind", $"Unknown expression kind '{kind}'."));
                break;
        }
    }

    private static void ValidateObjectArray(
        JsonElement array,
        string path,
        IReadOnlyList<string> allowed,
        IReadOnlyList<string> required,
        ICollection<StrategyCandidateGenerationIssueV1> issues,
        Action<JsonElement, string> validate)
    {
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var itemPath = $"{path}[{index++}]";
            if (Object(item, itemPath, allowed, required, issues)) validate(item, itemPath);
        }
    }

    private static void UniqueIds(
        JsonElement array,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String && id.GetString() is { } value && !seen.Add(value))
                issues.Add(Error("LANE_SPEC_ID_DUPLICATE", $"{path}[{index}].id", $"Identifier '{value}' is duplicated."));
            index++;
        }
    }

    private static void ValidateTags(
        JsonElement rule,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!rule.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            issues.Add(TypeError(path + ".tags", "an array"));
            return;
        }
        ValidateStringArray(tags, path + ".tags", 0, issues, identifiers: false);
    }

    private static void ValidateNullableExpressionRequirement(
        JsonElement value,
        bool required,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (required && value.ValueKind == JsonValueKind.Null)
            issues.Add(Error("LANE_SPEC_VALUE_INVALID", path, "This order type requires an expression."));
        else if (!required && value.ValueKind != JsonValueKind.Null)
            issues.Add(Error("LANE_SPEC_VALUE_INVALID", path, "This order type requires null."));
    }

    private static void PositiveInteger(
        JsonElement parent,
        string property,
        string path,
        bool nullable,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!parent.TryGetProperty(property, out var value)) return;
        if (nullable && value.ValueKind == JsonValueKind.Null) return;
        if (!IsInteger(value) || value.GetInt64() < 1)
            issues.Add(TypeError(path, nullable
                ? "an integer greater than or equal to 1, or null"
                : "an integer greater than or equal to 1"));
    }

    private static void Boolean(
        JsonElement parent,
        string property,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!parent.TryGetProperty(property, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            issues.Add(TypeError(path, "a boolean"));
    }

    private static void ExactBoolean(
        JsonElement parent,
        string property,
        bool expected,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!parent.TryGetProperty(property, out var value) ||
            value.ValueKind != (expected ? JsonValueKind.True : JsonValueKind.False))
            issues.Add(Error("LANE_SPEC_VALUE_INVALID", path, $"Expected exact boolean value '{expected.ToString().ToLowerInvariant()}'."));
    }

    private static void Identifier(
        JsonElement parent,
        string property,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            !IsIdentifier(value.GetString()))
            issues.Add(TypeError(path, "an identifier matching ^[A-Za-z][A-Za-z0-9_-]{0,127}$"));
    }

    private static void ValidateStringArray(
        JsonElement array,
        string path,
        int minimumItems,
        ICollection<StrategyCandidateGenerationIssueV1> issues,
        bool identifiers)
    {
        if (array.GetArrayLength() < minimumItems)
            issues.Add(Error("LANE_SPEC_ARRAY_EMPTY", path, $"At least {minimumItems} item(s) are required."));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(value) || identifiers && !IsIdentifier(value))
                issues.Add(TypeError($"{path}[{index}]", identifiers ? "a valid identifier" : "a non-empty string"));
            else if (!seen.Add(value!))
                issues.Add(Error("LANE_SPEC_VALUE_DUPLICATE", $"{path}[{index}]", $"Value '{value}' is duplicated."));
            index++;
        }
    }

    private static bool IsIdentifier(string? value)
    {
        if (value is null || value.Length is < 1 or > 128 || !IsAsciiLetter(value[0])) return false;
        return value.Skip(1).All(static character =>
            IsAsciiLetter(character) || character is >= '0' and <= '9' or '_' or '-');
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);

    private static bool ChildObject(
        JsonElement parent,
        string property,
        string path,
        IReadOnlyList<string> allowed,
        IReadOnlyList<string> required,
        ICollection<StrategyCandidateGenerationIssueV1> issues,
        out JsonElement value)
    {
        if (!parent.TryGetProperty(property, out value)) return false;
        return Object(value, path, allowed, required, issues);
    }

    private static bool Object(
        JsonElement value,
        string path,
        IReadOnlyList<string> allowed,
        IReadOnlyList<string> required,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            issues.Add(TypeError(path, "an object"));
            return false;
        }
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowedSet.Contains(property.Name))
                issues.Add(Error("LANE_SPEC_PROPERTY_UNKNOWN", $"{path}.{property.Name}",
                    $"Property '{property.Name}' is not part of the closed Declarative Rules v1 contract."));
        foreach (var property in required)
            if (!value.TryGetProperty(property, out _))
                issues.Add(Error("LANE_SPEC_PROPERTY_REQUIRED", $"{path}.{property}",
                    $"The closed Declarative Rules v1 contract requires '{property}'."));
        return true;
    }

    private static bool ChildArray(
        JsonElement parent,
        string property,
        string path,
        int minimumItems,
        ICollection<StrategyCandidateGenerationIssueV1> issues,
        out JsonElement value)
    {
        if (!parent.TryGetProperty(property, out value) || value.ValueKind != JsonValueKind.Array)
        {
            issues.Add(TypeError(path, "an array"));
            return false;
        }
        if (value.GetArrayLength() < minimumItems)
            issues.Add(Error("LANE_SPEC_ARRAY_EMPTY", path, $"At least {minimumItems} item(s) are required."));
        return true;
    }

    private static void Text(
        JsonElement parent,
        string property,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString())) issues.Add(TypeError(path, "a non-empty string"));
    }

    private static void ExactText(
        JsonElement parent,
        string property,
        string expected,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
            issues.Add(Error("LANE_SPEC_VALUE_INVALID", path, $"Expected exact value '{expected}'."));
    }

    private static void EnumText(
        JsonElement parent,
        string property,
        IReadOnlyList<string> allowed,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            !allowed.Contains(value.GetString(), StringComparer.Ordinal))
            issues.Add(Error("LANE_SPEC_VALUE_INVALID", path, $"Expected one of: {string.Join(", ", allowed)}."));
    }

    private static void Sha256(
        JsonElement parent,
        string property,
        string path,
        ICollection<StrategyCandidateGenerationIssueV1> issues)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: 64 } hash || hash.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            issues.Add(TypeError(path, "a 64-character lowercase SHA-256 digest"));
    }

    private static StrategyCandidateGenerationIssueV1 TypeError(string path, string expected) =>
        Error("LANE_SPEC_TYPE_INVALID", path, $"Expected {expected}.");

    private static StrategyCandidateGenerationIssueV1 Error(string code, string path, string message) =>
        new(StrategyCandidateGenerationIssueSeverityV1.Error, code, path, message);
}
