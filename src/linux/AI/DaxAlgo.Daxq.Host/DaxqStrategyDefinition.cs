using DaxAlgo.Daxq.Contracts;
using DaxAlgo.Daxq.Vm;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Daxq.Host;

internal sealed class DaxqStrategyDefinition
{
    public DaxqStrategyDefinition(
        LoadedDaxqPackage package,
        DaxqLicensingRuntime licensingRuntime,
        string pluginName,
        bool forceReferenceVm,
        string? nativeRuntimeFailure)
    {
        Package = package;
        LicensingRuntime = licensingRuntime;
        PluginName = pluginName;
        ForceReferenceVm = forceReferenceVm;
        NativeRuntimeFailure = nativeRuntimeFailure;
        Schema = BuildSchema(package.Manifest.Parameters);
        DataRequirement = BuildDataRequirement(package.Manifest.DataRequirements);
    }

    public DaxqManifest Manifest => Package.Manifest;

    public LoadedDaxqPackage Package { get; }

    public DaxqLicensingRuntime LicensingRuntime { get; }

    public string PluginName { get; }

    public bool ForceReferenceVm { get; }

    public string? NativeRuntimeFailure { get; }

    public StrategyParameterSchema Schema { get; }

    public StrategyDataRequirement DataRequirement { get; }

    public DaxqStrategyKernel CreateKernel(Contract contract, StrategyParameters? parameters = null) =>
        new(this, contract, parameters ?? Schema.CreateDefaults());

    public ValueTask<DaxqLicensedProgramSession> ActivateAsync(CancellationToken cancellationToken) =>
        LicensingRuntime.ActivateAsync(Package, PluginName, cancellationToken);

    public double[] CreateParameterValues(StrategyParameters parameters)
    {
        var values = new double[Manifest.Parameters.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var parameter = Manifest.Parameters[index];
            values[index] = parameter.Type switch
            {
                "int" => parameters.GetLong(parameter.Id),
                "float" => parameters.GetDouble(parameter.Id),
                "bool" => parameters.GetBool(parameter.Id) ? 1d : 0d,
                _ => throw new InvalidDataException($"Unsupported DAXQ parameter type '{parameter.Type}'."),
            };
        }
        return values;
    }

    private static StrategyParameterSchema BuildSchema(IEnumerable<DaxqParameterManifest> parameters) =>
        new(parameters.Select(parameter => new StrategyParameter
        {
            Key = parameter.Id,
            DisplayName = parameter.Id,
            Kind = parameter.Type switch
            {
                "int" => ParameterKind.Integer,
                "float" => ParameterKind.Number,
                "bool" => ParameterKind.Boolean,
                _ => throw new InvalidDataException($"Unsupported DAXQ parameter type '{parameter.Type}'."),
            },
            Default = parameter.Type switch
            {
                "int" => parameter.Default.GetInt64(),
                "float" => parameter.Default.GetDouble(),
                "bool" => parameter.Default.GetBoolean(),
                _ => null,
            },
            Min = parameter.Min?.GetDouble(),
            Max = parameter.Max?.GetDouble(),
            Step = parameter.Type == "int" ? 1d : null,
            Group = "Protected strategy",
        }));

    private static StrategyDataRequirement BuildDataRequirement(IEnumerable<string> requirements)
    {
        var result = StrategyDataRequirement.None;
        foreach (var requirement in requirements)
        {
            result |= requirement switch
            {
                "bars" => StrategyDataRequirement.L1 | StrategyDataRequirement.Bars,
                "ticks" => StrategyDataRequirement.L1,
                _ => StrategyDataRequirement.None,
            };
        }
        return result;
    }
}

internal sealed class DaxqTradingStrategyDescriptor(DaxqStrategyDefinition definition) : ITradingStrategy
{
    public string Id => definition.Manifest.StrategyId;

    public string BacktestStrategyId => Id;

    public string DisplayName => definition.Manifest.StrategyId;

    public string Description => $"Protected DAXQ strategy {definition.Manifest.Version}.";

    public StrategyDataRequirement DataRequirement => definition.DataRequirement;
}
