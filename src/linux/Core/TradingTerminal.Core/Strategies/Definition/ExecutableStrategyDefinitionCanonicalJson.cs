using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Core.Strategies.Definition;

/// <summary>
/// RFC 8785 JSON Canonicalization Scheme used to identify executable strategy definitions.
/// </summary>
public static class ExecutableStrategyDefinitionCanonicalJson
{
    public const string AlgorithmVersion = "rfc8785-jcs/v1";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static string Serialize(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = JsonSerializer.Serialize(value, value.GetType(), SerializerOptions);
        return Canonicalize(json);
    }

    public static string Canonicalize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var builder = new StringBuilder(json.Length);
        WriteCanonical(builder, document.RootElement);
        return builder.ToString();
    }

    public static T Deserialize<T>(string json)
    {
        var canonicalJson = Canonicalize(json);
        return JsonSerializer.Deserialize<T>(canonicalJson, SerializerOptions)
            ?? throw new JsonException($"Payload did not contain a {typeof(T).Name}.");
    }

    public static string Sha256(string json)
    {
        var canonicalJson = Canonicalize(json);
        return HashCanonical(canonicalJson);
    }

    public static string Hash(object value) => HashCanonical(Serialize(value));

    private static string HashCanonical(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            IgnoreReadOnlyProperties = true,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static void WriteCanonical(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteCanonicalObject(builder, element);
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                        builder.Append(',');
                    WriteCanonical(builder, item);
                    first = false;
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                string value;
                try
                {
                    value = element.GetString() ?? string.Empty;
                }
                catch (InvalidOperationException exception)
                {
                    throw new JsonException("RFC 8785 strings cannot contain an unpaired UTF-16 surrogate.", exception);
                }
                WriteCanonicalString(builder, value);
                break;
            case JsonValueKind.Number:
                double number;
                try
                {
                    number = element.GetDouble();
                }
                catch (FormatException exception)
                {
                    throw new JsonException("JSON number cannot be represented as an IEEE-754 binary64 value.", exception);
                }
                builder.Append(CanonicalDouble(number));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new JsonException($"Unsupported JSON value kind {element.ValueKind}.");
        }
    }

    private static void WriteCanonicalObject(StringBuilder builder, JsonElement element)
    {
        var properties = new List<(string Name, JsonElement Value)>();
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            string name;
            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException exception)
            {
                throw new JsonException("RFC 8785 strings cannot contain an unpaired UTF-16 surrogate.", exception);
            }
            RequireValidUnicode(name);
            if (!propertyNames.Add(name))
                throw new JsonException($"JSON object contains duplicate property '{name}'.");
            properties.Add((name, property.Value));
        }

        builder.Append('{');
        var first = true;
        foreach (var property in properties.OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            if (!first)
                builder.Append(',');
            WriteCanonicalString(builder, property.Name);
            builder.Append(':');
            WriteCanonical(builder, property.Value);
            first = false;
        }
        builder.Append('}');
    }

    private static void WriteCanonicalString(StringBuilder builder, string value)
    {
        RequireValidUnicode(value);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                default:
                    if (character < 0x20)
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }

    private static string CanonicalDouble(double value)
    {
        if (!double.IsFinite(value))
            throw new JsonException("RFC 8785 JSON numbers must be finite IEEE-754 binary64 values.");
        if (value == 0d)
            return "0";

        var roundTrip = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentMarker = roundTrip.IndexOf('E');
        if (exponentMarker < 0)
            return roundTrip;

        var mantissa = roundTrip[..exponentMarker];
        var exponent = int.Parse(roundTrip[(exponentMarker + 1)..], CultureInfo.InvariantCulture);
        var magnitude = Math.Abs(value);
        if (magnitude >= 1e-6 && magnitude < 1e21)
            return ScientificToFixed(mantissa, exponent);

        return string.Concat(
            mantissa,
            "e",
            exponent >= 0 ? "+" : "-",
            Math.Abs(exponent).ToString(CultureInfo.InvariantCulture));
    }

    private static string ScientificToFixed(string mantissa, int exponent)
    {
        var negative = mantissa[0] == '-';
        var unsignedMantissa = negative ? mantissa[1..] : mantissa;
        var decimalPoint = unsignedMantissa.IndexOf('.');
        var integerDigits = decimalPoint < 0 ? unsignedMantissa.Length : decimalPoint;
        var digits = decimalPoint < 0
            ? unsignedMantissa
            : unsignedMantissa.Remove(decimalPoint, 1);
        var decimalPosition = integerDigits + exponent;

        string fixedValue;
        if (decimalPosition <= 0)
            fixedValue = string.Concat("0.", new string('0', -decimalPosition), digits);
        else if (decimalPosition >= digits.Length)
            fixedValue = string.Concat(digits, new string('0', decimalPosition - digits.Length));
        else
            fixedValue = string.Concat(digits[..decimalPosition], ".", digits[decimalPosition..]);

        return negative ? string.Concat("-", fixedValue) : fixedValue;
    }

    private static void RequireValidUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    throw new JsonException("RFC 8785 strings cannot contain an unpaired UTF-16 surrogate.");
                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new JsonException("RFC 8785 strings cannot contain an unpaired UTF-16 surrogate.");
            }
        }
    }
}
