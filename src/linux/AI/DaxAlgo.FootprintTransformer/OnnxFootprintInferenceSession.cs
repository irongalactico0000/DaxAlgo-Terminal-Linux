using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TradingTerminal.Core.Ml;

namespace DaxAlgo.FootprintTransformer;

internal sealed class OnnxFootprintInferenceSession : IFootprintInferenceSession
{
    private readonly InferenceSession _session;

    public OnnxFootprintInferenceSession(byte[] modelBytes)
    {
        ArgumentNullException.ThrowIfNull(modelBytes);
        _session = new InferenceSession(modelBytes);
        try
        {
            ValidateContract();
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public float[] Run(FootprintInferenceInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rowFeatures = new DenseTensor<float>(
            input.RowFeatures,
            [1, FootprintModelContract.LookbackBars, input.RowCount, FootprintModelContract.RowFeatureCount]);
        var rowMask = new DenseTensor<bool>(
            input.RowMask,
            [1, FootprintModelContract.LookbackBars, input.RowCount]);
        var barFeatures = new DenseTensor<float>(
            input.BarFeatures,
            [1, FootprintModelContract.LookbackBars, FootprintModelContract.BarFeatureCount]);
        var barMask = new DenseTensor<bool>(
            input.BarMask,
            [1, FootprintModelContract.LookbackBars]);

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("row_features", rowFeatures),
            NamedOnnxValue.CreateFromTensor("row_mask", rowMask),
            NamedOnnxValue.CreateFromTensor("bar_features", barFeatures),
            NamedOnnxValue.CreateFromTensor("bar_mask", barMask),
        };

        using var outputs = _session.Run(inputs, ["quantiles"]);
        var quantiles = outputs.Single().AsTensor<float>().ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (quantiles.Length != FootprintModelContract.HorizonBars
            * FootprintModelContract.TargetCount
            * FootprintModelContract.QuantileCount)
        {
            throw new InvalidDataException("The ONNX model returned an unexpected quantile tensor shape.");
        }

        return quantiles;
    }

    public void Dispose() => _session.Dispose();

    private void ValidateContract()
    {
        if (_session.InputMetadata.Count != 4 || _session.OutputMetadata.Count != 1)
            throw new InvalidDataException("The ONNX graph does not expose the trained model contract.");

        ValidateTensor(
            _session.InputMetadata,
            "row_features",
            typeof(float),
            [null, FootprintModelContract.LookbackBars, null, FootprintModelContract.RowFeatureCount]);
        ValidateTensor(
            _session.InputMetadata,
            "row_mask",
            typeof(bool),
            [null, FootprintModelContract.LookbackBars, null]);
        ValidateTensor(
            _session.InputMetadata,
            "bar_features",
            typeof(float),
            [null, FootprintModelContract.LookbackBars, FootprintModelContract.BarFeatureCount]);
        ValidateTensor(
            _session.InputMetadata,
            "bar_mask",
            typeof(bool),
            [null, FootprintModelContract.LookbackBars]);
        ValidateTensor(
            _session.OutputMetadata,
            "quantiles",
            typeof(float),
            [
                null,
                FootprintModelContract.HorizonBars,
                FootprintModelContract.TargetCount,
                FootprintModelContract.QuantileCount,
            ]);
    }

    private static void ValidateTensor(
        IReadOnlyDictionary<string, NodeMetadata> metadata,
        string name,
        Type elementType,
        int?[] expectedDimensions)
    {
        if (!metadata.TryGetValue(name, out var node)
            || !node.IsTensor
            || node.ElementType != elementType
            || node.Dimensions.Length != expectedDimensions.Length)
        {
            throw new InvalidDataException($"ONNX tensor '{name}' does not match the trained model contract.");
        }

        for (var index = 0; index < expectedDimensions.Length; index++)
        {
            if (expectedDimensions[index] is int expected && node.Dimensions[index] != expected)
            {
                throw new InvalidDataException(
                    $"ONNX tensor '{name}' does not match the trained model dimensions.");
            }
        }
    }
}

internal sealed record LoadedFootprintModel(
    IFootprintInferenceSession Session,
    FootprintForecastModelMetadata Metadata);

internal static class EmbeddedFootprintModelLoader
{
    private const string ModelResourceName = "DaxAlgo.FootprintTransformer.Artifacts.fdt-n.onnx";
    private const string MetadataResourceName = "DaxAlgo.FootprintTransformer.Artifacts.metadata.json";

    public static LoadedFootprintModel? TryLoad()
    {
        try
        {
            var assembly = typeof(EmbeddedFootprintModelLoader).Assembly;
            var modelBytes = ReadResource(assembly, ModelResourceName);
            var metadataBytes = ReadResource(assembly, MetadataResourceName);
            if (modelBytes is null || metadataBytes is null)
                return null;

            var metadata = ValidateMetadata(metadataBytes, modelBytes);
            return metadata is null
                ? null
                : new LoadedFootprintModel(new OnnxFootprintInferenceSession(modelBytes), metadata);
        }
        catch
        {
            return null;
        }
    }

    internal static FootprintForecastModelMetadata? ValidateMetadata(byte[] metadataBytes, byte[] modelBytes)
    {
        using var document = JsonDocument.Parse(metadataBytes);
        var root = document.RootElement;
        if (ReadInt(root, "schema_version") != FootprintModelContract.MetadataSchemaVersion
            || !string.Equals(
                ReadString(root, "model_kind"),
                FootprintModelContract.ModelKind,
                StringComparison.Ordinal)
            || ReadString(root, "model_version") is not { Length: > 0 } modelVersion
            || ReadInt(root, "minimum_history_bars") != FootprintModelContract.LookbackBars
            || ReadInt(root, "maximum_horizon_bars") != FootprintModelContract.HorizonBars
            || !string.Equals(
                ReadString(root, "runtime_status"),
                FootprintModelContract.RuntimeStatus,
                StringComparison.Ordinal)
            || ReadBoolean(root, "creates_order_signals") != false)
        {
            return null;
        }

        if (!root.TryGetProperty("coordinate", out var coordinate)
            || !string.Equals(ReadString(coordinate, "instrument_key"), "BTCUSDT", StringComparison.Ordinal)
            || !string.Equals(ReadString(coordinate, "source"), "Binance", StringComparison.OrdinalIgnoreCase)
            || ReadInt(coordinate, "interval_seconds") != (int)FootprintModelContract.Interval.TotalSeconds
            || ReadDouble(coordinate, "row_size") != FootprintModelContract.RowSize)
        {
            return null;
        }

        if (!root.TryGetProperty("encoding_config", out var encoding)
            || ReadInt(encoding, "lookback_bars") != FootprintModelContract.LookbackBars
            || ReadInt(encoding, "horizon_bars") != FootprintModelContract.HorizonBars
            || ReadInt(encoding, "max_rows") != FootprintModelContract.MaximumRows
            || ReadDouble(encoding, "row_size") != FootprintModelContract.RowSize
            || ReadInt(encoding, "interval_seconds") != (int)FootprintModelContract.Interval.TotalSeconds
            || ReadDouble(encoding, "price_scale_ticks") != FootprintModelContract.PriceScaleTicks
            || ReadDouble(encoding, "gap_scale_ticks") != FootprintModelContract.GapScaleTicks
            || ReadDouble(encoding, "log_volume_scale") != FootprintModelContract.LogVolumeScale
            || ReadDouble(encoding, "cumulative_delta_scale") != FootprintModelContract.CumulativeDeltaScale
            || ReadDouble(encoding, "imbalance_ratio") != FootprintModelContract.ImbalanceRatio
            || ReadDouble(encoding, "quantity_scale") != FootprintModelContract.QuantityScale)
        {
            return null;
        }

        if (!Matches(root, "row_feature_names", FootprintModelContract.RowFeatureNames)
            || !Matches(root, "bar_feature_names", FootprintModelContract.BarFeatureNames)
            || !Matches(root, "target_names", FootprintModelContract.TargetNames)
            || !Matches(root, "quantiles", [0.1, 0.5, 0.9]))
        {
            return null;
        }

        var expectedHash = ReadString(root, "onnx_sha256");
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(modelBytes));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            return null;

        return new FootprintForecastModelMetadata(
            "onnxruntime",
            FootprintModelContract.ModelKind,
            modelVersion);
    }

    private static byte[]? ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
            return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool Matches(JsonElement root, string propertyName, IReadOnlyList<string> expected)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() != expected.Count)
        {
            return false;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !string.Equals(item.GetString(), expected[index++], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(JsonElement root, string propertyName, IReadOnlyList<double> expected)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() != expected.Count)
        {
            return false;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || item.GetDouble() != expected[index++])
                return false;
        }

        return true;
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static double? ReadDouble(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetDouble(out var result)
            ? result
            : null;

    private static bool? ReadBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
