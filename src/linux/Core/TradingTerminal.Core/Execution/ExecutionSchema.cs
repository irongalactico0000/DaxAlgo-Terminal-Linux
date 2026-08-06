using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Core.Execution;

public static class ExecutionSchema
{
    public const int CurrentVersion = 1;

    public static void RequireSupported(int version, string parameterName = "schemaVersion")
    {
        if (version != CurrentVersion)
            throw new ArgumentOutOfRangeException(parameterName, version, $"Only execution schema {CurrentVersion} is supported.");
    }
}

public enum ExecutionEnvironment
{
    Backtest = 1,
    SimulatedPaper = 2,
}

public enum ExecutionRuntimeMode
{
    Replay = 1,
    ReconcilingQueryOnly = 2,
    RestrictedRecovery = 3,
    Active = 4,
}

/// <summary>Deterministic JSON used for persisted payloads, hashes, and duplicate detection.</summary>
public static class ExecutionCanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Canonicalize(JsonSerializer.Serialize(value, SerializerOptions));
    }

    public static string Canonicalize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static T Deserialize<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new JsonException($"Payload did not contain a {typeof(T).Name}.");
    }

    public static string Sha256(string canonicalJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
    }

    public static string Hash<T>(T value) => Sha256(Serialize(value));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            // Canonicalization sorts object keys, so a polymorphic `kind` discriminator is not
            // guaranteed to be the first property when the payload is read back from the journal.
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new ExecutionIdentifierJsonConverterFactory());
        return options;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported JSON value kind {element.ValueKind}.");
        }
    }
}

internal static class ExecutionValidation
{
    public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        return value;
    }

    public static string RequireText(string value, string parameterName, int maximumLength = 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Value cannot have leading or trailing whitespace.", parameterName);
        if (value.Length > maximumLength)
            throw new ArgumentOutOfRangeException(parameterName, value.Length, $"Value cannot exceed {maximumLength} characters.");
        if (value.Any(char.IsControl))
            throw new ArgumentException("Value cannot contain control characters.", parameterName);
        return value;
    }

    public static string RequireSha256(string value, string parameterName)
    {
        RequireText(value, parameterName, 64);
        if (value.Length != 64 || value.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Value must be a 64-character SHA-256 hexadecimal digest.", parameterName);
        return value.ToLowerInvariant();
    }
}
