using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Ml;

namespace DaxAlgo.FootprintTransformer;

public sealed class FootprintTransformerForecastProvider : IFootprintForecastProvider, IDisposable
{
    private readonly IFootprintInferenceSession? _session;
    private readonly FootprintForecastModelMetadata? _metadata;
    private bool _disposed;

    public FootprintTransformerForecastProvider()
    {
        var loaded = EmbeddedFootprintModelLoader.TryLoad();
        _session = loaded?.Session;
        _metadata = loaded?.Metadata;
    }

    internal FootprintTransformerForecastProvider(
        IFootprintInferenceSession session,
        FootprintForecastModelMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(metadata);
        _session = session;
        _metadata = metadata;
    }

    public Task<FootprintForecastResult> ForecastAsync(
        FootprintForecastRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var unavailable = ValidateRequest(request);
        if (unavailable is not null)
            return Task.FromResult(unavailable);

        if (_session is null || _metadata is null)
        {
            return Task.FromResult(FootprintForecastResult.CreateUnavailable(
                request,
                message: "The trained footprint model artifact is unavailable."));
        }

        return Task.Run(() => ForecastCore(request, cancellationToken), cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _session?.Dispose();
    }

    private FootprintForecastResult ForecastCore(
        FootprintForecastRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = FootprintTransformerEncoding.Encode(request);
            var output = _session!.Run(encoded.Input, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Decode(request, encoded, output, _metadata!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return FootprintForecastResult.CreateUnavailable(
                request,
                FootprintForecastStatus.Failed,
                _metadata,
                "Footprint model inference failed closed.");
        }
    }

    private static FootprintForecastResult? ValidateRequest(FootprintForecastRequest request)
    {
        var coordinate = request.Coordinate;
        if (!string.Equals(coordinate.InstrumentKey, "BTCUSDT", StringComparison.Ordinal)
            || coordinate.Source != BrokerKind.Binance
            || coordinate.Interval != FootprintModelContract.Interval
            || coordinate.RowSize != FootprintModelContract.RowSize)
        {
            return FootprintForecastResult.CreateUnavailable(
                request,
                message: "The request coordinate does not match the trained model.");
        }

        if (request.HorizonBars > FootprintModelContract.HorizonBars)
        {
            return FootprintForecastResult.CreateUnavailable(
                request,
                message: "The requested horizon exceeds the trained model horizon.");
        }

        if (request.History.Count < FootprintModelContract.LookbackBars)
        {
            return FootprintForecastResult.CreateUnavailable(
                request,
                FootprintForecastStatus.InsufficientHistory,
                message: "At least 64 contiguous completed bars are required.");
        }

        if (request.History.Any(bar => bar.Quality != FeedQuality.RealTape))
        {
            return FootprintForecastResult.CreateUnavailable(
                request,
                message: "The trained model accepts real trade tape only.");
        }

        if (request.History
            .Skip(request.History.Count - FootprintModelContract.LookbackBars)
            .Any(bar => bar.Rows.Count > FootprintModelContract.MaximumRows))
        {
            return FootprintForecastResult.CreateUnavailable(
                request,
                message: "A context bar exceeds the trained model row bound.");
        }

        return null;
    }

    private static FootprintForecastResult Decode(
        FootprintForecastRequest request,
        EncodedFootprintWindow encoded,
        float[] output,
        FootprintForecastModelMetadata metadata)
    {
        var expectedLength = FootprintModelContract.HorizonBars
            * FootprintModelContract.TargetCount
            * FootprintModelContract.QuantileCount;
        if (output.Length != expectedLength)
            throw new InvalidDataException("The inference output does not match the model contract.");

        var forecasts = new FootprintHorizonForecast[request.HorizonBars];
        var targetStartUtc = request.CutoffUtc;
        for (var horizon = 0; horizon < request.HorizonBars; horizon++)
        {
            var targetEndUtc = targetStartUtc.Add(FootprintModelContract.Interval);
            forecasts[horizon] = new FootprintHorizonForecast(
                horizon + 1,
                targetStartUtc,
                targetEndUtc,
                PriceQuantiles(output, horizon, 0, encoded.AnchorPocTick),
                PriceQuantiles(output, horizon, 1, encoded.AnchorPocTick),
                PriceQuantiles(output, horizon, 2, encoded.AnchorPocTick),
                PriceQuantiles(output, horizon, 3, encoded.AnchorPocTick),
                PriceQuantiles(output, horizon, 4, encoded.AnchorPocTick),
                VolumeQuantiles(output, horizon, encoded.ReferenceLogVolume),
                DirectQuantiles(output, horizon, 6),
                includeDeltaMedian: true);
            targetStartUtc = targetEndUtc;
        }

        return FootprintForecastResult.CreateAvailable(request, metadata, forecasts);
    }

    private static FootprintForecastQuantiles PriceQuantiles(
        float[] output,
        int horizon,
        int target,
        double anchorPocTick)
    {
        var values = ReadQuantiles(output, horizon, target);
        return new FootprintForecastQuantiles(
            Price(anchorPocTick, values.Q10),
            Price(anchorPocTick, values.Q50),
            Price(anchorPocTick, values.Q90));
    }

    private static FootprintForecastQuantiles VolumeQuantiles(
        float[] output,
        int horizon,
        double referenceLogVolume)
    {
        var values = ReadQuantiles(output, horizon, 5);
        return new FootprintForecastQuantiles(
            Volume(referenceLogVolume, values.Q10),
            Volume(referenceLogVolume, values.Q50),
            Volume(referenceLogVolume, values.Q90));
    }

    private static FootprintForecastQuantiles DirectQuantiles(float[] output, int horizon, int target)
    {
        var values = ReadQuantiles(output, horizon, target);
        return new FootprintForecastQuantiles(values.Q10, values.Q50, values.Q90);
    }

    private static (double Q10, double Q50, double Q90) ReadQuantiles(
        float[] output,
        int horizon,
        int target)
    {
        var offset = ((horizon * FootprintModelContract.TargetCount) + target)
            * FootprintModelContract.QuantileCount;
        return (output[offset], output[offset + 1], output[offset + 2]);
    }

    private static double Price(double anchorPocTick, double normalizedOffset) =>
        (anchorPocTick + (normalizedOffset * FootprintModelContract.PriceScaleTicks))
        * FootprintModelContract.RowSize;

    private static double Volume(double referenceLogVolume, double normalizedLogVolume) =>
        Math.Max(
            Math.Exp(referenceLogVolume + (normalizedLogVolume * FootprintModelContract.LogVolumeScale)) - 1.0,
            0.0);
}
