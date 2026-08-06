namespace TradingTerminal.Core.Strategies.Definition;

/// <summary>
/// Canonical persisted identity for an <see cref="OperatorGraphModuleV1"/>. Serializing the concrete
/// CLR type directly can omit the polymorphic <c>moduleKind</c> discriminator, so executable lineage
/// must use this exact four-field document instead.
/// </summary>
public static class OperatorGraphModuleCanonicalJsonV1
{
    public static string Serialize(OperatorGraphModuleV1 module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return ExecutableStrategyDefinitionCanonicalJson.Serialize(ToDocument(module));
    }

    public static string Hash(OperatorGraphModuleV1 module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return ExecutableStrategyDefinitionCanonicalJson.Hash(ToDocument(module));
    }

    public static OperatorGraphModuleV1 Deserialize(string json)
    {
        var document = ExecutableStrategyDefinitionCanonicalJson.Deserialize<OperatorGraphModuleDocumentV1>(json);
        if (!StringComparer.Ordinal.Equals(document.ModuleKind, "operatorGraph"))
            throw new ArgumentException("Expected an operatorGraph TradeIR module.", nameof(json));
        return new OperatorGraphModuleV1(
            document.SchemaVersion,
            document.ModuleId,
            document.Definition);
    }

    private static OperatorGraphModuleDocumentV1 ToDocument(OperatorGraphModuleV1 module) => new(
        ModuleKind: "operatorGraph",
        module.SchemaVersion,
        module.ModuleId,
        module.Definition);

    private sealed record OperatorGraphModuleDocumentV1(
        string ModuleKind,
        string SchemaVersion,
        string ModuleId,
        StrategyIntermediateRepresentationV1 Definition);
}
