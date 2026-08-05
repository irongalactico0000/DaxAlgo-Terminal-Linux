namespace TradingTerminal.Core.Strategies.Definition;

public sealed record StrategyModuleIssueV1(string Code, string Path, string Message);

public sealed record StrategyModuleValidationV1(IReadOnlyList<StrategyModuleIssueV1> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Pure pre-compilation validation for module identity, interface, determinism, and requested
/// confinement. A successful result still requires a worker attestation that the OS boundary
/// enforces the declared runtime contract.
/// </summary>
public static class TradeIrModuleValidatorV1
{
    public static StrategyModuleValidationV1 Validate(
        TradeIrModuleV1? module,
        IStrategyOperatorRegistryV1 registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var issues = new List<StrategyModuleIssueV1>();
        if (module is null)
        {
            issues.Add(new("MODULE_REQUIRED", "$", "A strategy module is required."));
            return Result(issues);
        }

        if (!string.Equals(module.SchemaVersion, TradeIrModuleV1.CurrentSchemaVersion, StringComparison.Ordinal))
            issues.Add(new("MODULE_SCHEMA_UNSUPPORTED", "schemaVersion", $"Expected '{TradeIrModuleV1.CurrentSchemaVersion}'."));
        if (!IsIdentifier(module.ModuleId))
            issues.Add(new("MODULE_ID_INVALID", "moduleId", "Module id must be a lowercase stable identifier."));

        switch (module)
        {
            case OperatorGraphModuleV1 graph:
                if (graph.Definition is null)
                    issues.Add(new("GRAPH_REQUIRED", "definition", "The canonical StrategyIR definition is required."));
                else
                    foreach (var issue in StrategyIrValidatorV1.Validate(graph.Definition, registry).Issues)
                        issues.Add(new("GRAPH_INVALID", $"definition.{issue.Path}", $"{issue.Code}: {issue.Message}"));
                break;

            case CSharpModuleV1 csharp:
                ValidateInterface(csharp.Inputs, csharp.Outputs, issues);
                ValidateAddress(csharp.SourceAddress, "sourceAddress", issues);
                RequireText(csharp.LanguageVersion, "languageVersion", issues);
                RequireText(csharp.EntryPoint, "entryPoint", issues);
                ValidateRuntime(csharp.Runtime, issues);
                break;

            case ModelArtifactModuleV1 model:
                ValidateInterface(model.Inputs, model.Outputs, issues);
                ValidateAddress(model.ArtifactAddress, "artifactAddress", issues);
                RequireText(model.Format, "format", issues);
                RequireText(model.EntryPoint, "entryPoint", issues);
                ValidateHash(model.FeatureSchemaHashSha256, "featureSchemaHashSha256", issues);
                ValidateHash(model.OutputSchemaHashSha256, "outputSchemaHashSha256", issues);
                ValidateRuntime(model.Runtime, issues);
                break;

            default:
                issues.Add(new("MODULE_KIND_UNSUPPORTED", "$", $"Unsupported module type '{module.GetType().Name}'."));
                break;
        }

        return Result(issues);
    }

    private static void ValidateInterface(
        IReadOnlyList<StrategyModuleInputV1>? inputs,
        IReadOnlyList<StrategyModuleOutputV1>? outputs,
        ICollection<StrategyModuleIssueV1> issues)
    {
        if (inputs is null)
            issues.Add(new("MODULE_INTERFACE_INVALID", "inputs", "Input declarations are required."));
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                if (input is null || !IsIdentifier(input.InputId) || !ids.Add(input.InputId))
                {
                    issues.Add(new("MODULE_INTERFACE_INVALID", "inputs", "Inputs need unique stable ids and typed values."));
                    continue;
                }
                ValidateValueType(input.ValueType, $"inputs[{input.InputId}].valueType", issues);
            }
            RequireCanonicalInterfaceOrder(inputs.Select(static input => input?.InputId), "inputs", issues);
        }

        if (outputs is not { Count: > 0 })
        {
            issues.Add(new("MODULE_INTERFACE_INVALID", "outputs", "At least one typed strategy output is required."));
            return;
        }

        var outputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            if (output is null || !IsIdentifier(output.OutputId) || !Enum.IsDefined(output.Kind) ||
                !outputIds.Add(output.OutputId))
            {
                issues.Add(new("MODULE_INTERFACE_INVALID", "outputs", "Outputs need unique stable ids, one of the four output kinds, and typed values."));
                continue;
            }

            var path = $"outputs[{output.OutputId}].valueType";
            ValidateValueType(output.ValueType, path, issues);
            if (output.ValueType is not null && !StrategyValueTypeRulesV1.IsCompatible(output.Kind, output.ValueType))
                issues.Add(new("MODULE_OUTPUT_TYPE_MISMATCH", path,
                    $"Output kind '{output.Kind}' is incompatible with type '{output.ValueType.TypeId}'."));
        }
        RequireCanonicalInterfaceOrder(outputs.Select(static output => output?.OutputId), "outputs", issues);
    }

    private static void RequireCanonicalInterfaceOrder(
        IEnumerable<string?> ids,
        string path,
        ICollection<StrategyModuleIssueV1> issues)
    {
        var actual = ids.Where(static id => id is not null).Cast<string>().ToArray();
        if (!actual.SequenceEqual(actual.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            issues.Add(new("MODULE_INTERFACE_NONCANONICAL", path, "Interface declarations must be ordered by stable id."));
    }

    private static void ValidateValueType(
        StrategyValueTypeV1? valueType,
        string path,
        ICollection<StrategyModuleIssueV1> issues) =>
        StrategyValueTypeRulesV1.Validate(valueType, path,
            (code, issuePath, message) => issues.Add(new(
                "MODULE_VALUE_TYPE_INVALID",
                issuePath,
                $"{code}: {message}")));

    private static void ValidateRuntime(
        StrategyModuleRuntimeContractV1? runtime,
        ICollection<StrategyModuleIssueV1> issues)
    {
        if (runtime is null)
        {
            issues.Add(new("MODULE_RUNTIME_UNSAFE", "runtime", "An isolated runtime contract is required."));
            return;
        }

        RequireText(runtime.AbiVersion, "runtime.abiVersion", issues);
        ValidateAddress(runtime.RuntimeAddress, "runtime.runtimeAddress", issues);
        if (!runtime.RequiresIsolatedProcess || runtime.AllowNetwork || runtime.AllowFileSystem ||
            runtime.AllowCredentials || runtime.AllowInterprocessCommunication || runtime.AllowProcessCreation)
            issues.Add(new("MODULE_RUNTIME_UNSAFE", "runtime", "Generated extensions require an isolated process with network, filesystem, credential, IPC, and process creation denied."));
        if (!Enum.IsDefined(runtime.Determinism) || runtime.Determinism == StrategyModuleDeterminismV1.ExternallyDetermined)
            issues.Add(new("MODULE_NONDETERMINISTIC", "runtime.determinism", "Externally determined behavior is not reproducible."));
        if (runtime.Determinism == StrategyModuleDeterminismV1.SeededDeterministic && runtime.RandomSeed is null)
            issues.Add(new("MODULE_NONDETERMINISTIC", "runtime.randomSeed", "Seeded deterministic modules require a pinned seed."));
        if (runtime.Determinism == StrategyModuleDeterminismV1.Deterministic && runtime.RandomSeed is not null)
            issues.Add(new("MODULE_NONDETERMINISTIC", "runtime.randomSeed", "A deterministic module must not depend on a random seed."));
    }

    private static void ValidateAddress(
        TradeIrContentAddressV1? address,
        string path,
        ICollection<StrategyModuleIssueV1> issues)
    {
        if (address is null || address.Algorithm != TradeIrDigestAlgorithmV1.Sha256 || !IsSha256(address.Digest))
            issues.Add(new("MODULE_HASH_INVALID", path, "A lowercase SHA-256 content address is required."));
    }

    private static void ValidateHash(string value, string path, ICollection<StrategyModuleIssueV1> issues)
    {
        if (!IsSha256(value))
            issues.Add(new("MODULE_HASH_INVALID", path, "A lowercase SHA-256 digest is required."));
    }

    private static void RequireText(string value, string path, ICollection<StrategyModuleIssueV1> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(new("MODULE_INTERFACE_INVALID", path, "A non-empty value is required."));
    }

    private static StrategyModuleValidationV1 Result(IEnumerable<StrategyModuleIssueV1> issues) =>
        new(issues.OrderBy(static issue => issue.Path, StringComparer.Ordinal)
            .ThenBy(static issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(static issue => issue.Message, StringComparer.Ordinal)
            .ToArray());

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value[0] is >= 'a' and <= 'z' &&
        value.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
