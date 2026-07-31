using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaxAlgo.Daxq.Contracts;

/// <summary>Enforces the exact lowercase, string-only ExecutionClass wire contract.</summary>
internal sealed class ExecutionClassJsonConverter : JsonConverter<ExecutionClass>
{
    public override ExecutionClass Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("executionClass must be an exact lowercase string.");

        return reader.GetString() switch
        {
            "source_open" => ExecutionClass.SourceOpen,
            "sealed_bytecode" => ExecutionClass.SealedBytecode,
            "server_signal" => ExecutionClass.ServerSignal,
            _ => throw new JsonException("Unsupported executionClass value."),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        ExecutionClass value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            ExecutionClass.SourceOpen => "source_open",
            ExecutionClass.SealedBytecode => "sealed_bytecode",
            ExecutionClass.ServerSignal => "server_signal",
            _ => throw new JsonException("Unsupported ExecutionClass value."),
        });
    }
}
