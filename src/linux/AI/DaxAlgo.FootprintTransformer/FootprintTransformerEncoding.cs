using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Ml;

namespace DaxAlgo.FootprintTransformer;

internal sealed record EncodedFootprintWindow(
    FootprintInferenceInput Input,
    double AnchorPocTick,
    double ReferenceLogVolume);

internal static class FootprintTransformerEncoding
{
    public static EncodedFootprintWindow Encode(FootprintForecastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bars = request.History
            .Skip(request.History.Count - FootprintModelContract.LookbackBars)
            .Select(EncodeBar)
            .ToArray();
        var rowWidth = bars.Max(bar => bar.Rows.Length);
        if (rowWidth > FootprintModelContract.MaximumRows)
            throw new InvalidOperationException("A context bar exceeds the model row bound.");

        var rowFeatures = new float[
            FootprintModelContract.LookbackBars * rowWidth * FootprintModelContract.RowFeatureCount];
        var rowMask = new bool[FootprintModelContract.LookbackBars * rowWidth];
        var barFeatures = new float[
            FootprintModelContract.LookbackBars * FootprintModelContract.BarFeatureCount];
        var barMask = new bool[FootprintModelContract.LookbackBars];

        var anchorPocTick = bars[^1].PocTick;
        var referenceLogVolume = Median(bars[^16..].Select(bar => bar.LogVolume).ToArray());
        var referenceVolume = Math.Exp(referenceLogVolume) - 1.0;
        var baseCumulativeDelta = bars[0].CumulativeDelta;

        for (var barIndex = 0; barIndex < bars.Length; barIndex++)
        {
            var bar = bars[barIndex];
            barMask[barIndex] = true;

            for (var rowIndex = 0; rowIndex < bar.Rows.Length; rowIndex++)
            {
                var row = bar.Rows[rowIndex];
                rowMask[(barIndex * rowWidth) + rowIndex] = true;
                var offset = ((barIndex * rowWidth) + rowIndex) * FootprintModelContract.RowFeatureCount;
                rowFeatures[offset] = Float(Clip(
                    (row.PriceTick - bar.PocTick) / FootprintModelContract.GapScaleTicks,
                    -2.0,
                    2.0));
                rowFeatures[offset + 1] = Float(row.BuyVolume / bar.NormalizedTotalVolume);
                rowFeatures[offset + 2] = Float(row.SellVolume / bar.NormalizedTotalVolume);
                rowFeatures[offset + 3] = Float(row.TotalVolume / bar.NormalizedTotalVolume);
                rowFeatures[offset + 4] = Float(row.Delta / bar.NormalizedTotalVolume);
                rowFeatures[offset + 5] = Float(Math.Log(1.0 + row.BuyVolume) / bar.LogTotalVolume);
                rowFeatures[offset + 6] = Float(Math.Log(1.0 + row.SellVolume) / bar.LogTotalVolume);
                rowFeatures[offset + 7] = row.BidImbalance ? 1.0f : 0.0f;
                rowFeatures[offset + 8] = row.AskImbalance ? 1.0f : 0.0f;
                rowFeatures[offset + 9] = row.ZeroBid ? 1.0f : 0.0f;
                rowFeatures[offset + 10] = row.ZeroAsk ? 1.0f : 0.0f;
            }

            var featureOffset = barIndex * FootprintModelContract.BarFeatureCount;
            barFeatures[featureOffset] = Float(Clip(
                (bar.PocTick - anchorPocTick) / FootprintModelContract.PriceScaleTicks,
                -2.0,
                2.0));
            barFeatures[featureOffset + 1] = Float(ScaledGap(bar.BuyPocTick - bar.PocTick));
            barFeatures[featureOffset + 2] = Float(ScaledGap(bar.SellPocTick - bar.PocTick));
            barFeatures[featureOffset + 3] = Float(ScaledGap(bar.VolumeCentroidTick - bar.PocTick));
            barFeatures[featureOffset + 4] = Float(ScaledGap(bar.BuyCentroidTick - bar.PocTick));
            barFeatures[featureOffset + 5] = Float(ScaledGap(bar.SellCentroidTick - bar.PocTick));
            barFeatures[featureOffset + 6] = Float(Math.Min(
                (bar.PocTick - bar.LowTick) / FootprintModelContract.GapScaleTicks,
                2.0));
            barFeatures[featureOffset + 7] = Float(Math.Min(
                (bar.HighTick - bar.PocTick) / FootprintModelContract.GapScaleTicks,
                2.0));
            barFeatures[featureOffset + 8] = Float(Math.Min(
                (bar.PocTick - bar.ValueAreaLowTick) / FootprintModelContract.GapScaleTicks,
                2.0));
            barFeatures[featureOffset + 9] = Float(Math.Min(
                (bar.ValueAreaHighTick - bar.PocTick) / FootprintModelContract.GapScaleTicks,
                2.0));
            barFeatures[featureOffset + 10] = Float(Clip(
                (bar.LogVolume - referenceLogVolume) / FootprintModelContract.LogVolumeScale,
                -2.0,
                2.0));
            barFeatures[featureOffset + 11] = Float(bar.BuyVolume / bar.NormalizedTotalVolume);
            barFeatures[featureOffset + 12] = Float(bar.SellVolume / bar.NormalizedTotalVolume);
            barFeatures[featureOffset + 13] = Float(bar.Delta / bar.NormalizedTotalVolume);
            barFeatures[featureOffset + 14] = Float(Clip(
                (bar.CumulativeDelta - baseCumulativeDelta)
                / Math.Max(referenceVolume * FootprintModelContract.LookbackBars, 1.0)
                / FootprintModelContract.CumulativeDeltaScale,
                -2.0,
                2.0));
            barFeatures[featureOffset + 15] = Float(Math.Min((double)bar.StackedBuy / bar.Rows.Length, 1.0));
            barFeatures[featureOffset + 16] = Float(Math.Min((double)bar.StackedSell / bar.Rows.Length, 1.0));
            barFeatures[featureOffset + 17] = Float(Math.Min(
                Math.Log(1.0 + bar.Rows.Length) / Math.Log(1.0 + FootprintModelContract.MaximumRows),
                1.0));
            barFeatures[featureOffset + 18] = 1.0f;

            var timestamp = new DateTimeOffset(bar.StartUtc).ToUnixTimeMilliseconds();
            var minute = (timestamp / 60_000) % 1_440;
            var weekday = ((timestamp / 86_400_000) + 3) % 7;
            barFeatures[featureOffset + 19] = Float(Math.Sin(2.0 * Math.PI * minute / 1_440.0));
            barFeatures[featureOffset + 20] = Float(Math.Cos(2.0 * Math.PI * minute / 1_440.0));
            barFeatures[featureOffset + 21] = Float(Math.Sin(2.0 * Math.PI * weekday / 7.0));
            barFeatures[featureOffset + 22] = Float(Math.Cos(2.0 * Math.PI * weekday / 7.0));
        }

        return new EncodedFootprintWindow(
            new FootprintInferenceInput(rowFeatures, rowMask, rowWidth, barFeatures, barMask),
            anchorPocTick,
            referenceLogVolume);
    }

    private static EncodedBar EncodeBar(FootprintBar bar)
    {
        var rows = bar.Rows.Select(row => new EncodedRow(
            PriceTick(row.Price),
            row.BuyVolume,
            row.SellVolume,
            row.BidImbalance,
            row.AskImbalance,
            row.ZeroBid,
            row.ZeroAsk)).ToArray();

        var pocIndex = ArgMax(rows, row => row.TotalVolume);
        var buyPocIndex = ArgMax(rows, row => row.BuyVolume);
        var sellPocIndex = ArgMax(rows, row => row.SellVolume);
        var buyVolume = rows.Sum(row => row.BuyVolume);
        var sellVolume = rows.Sum(row => row.SellVolume);
        var totalVolume = buyVolume + sellVolume;
        var normalizedTotalVolume = Math.Max(totalVolume, 1.0);
        var pocTick = rows[pocIndex].PriceTick;
        var (valueAreaHighTick, valueAreaLowTick) = ValueArea(rows, pocIndex);

        return new EncodedBar(
            bar.StartUtc,
            rows,
            pocTick,
            rows[buyPocIndex].PriceTick,
            rows[sellPocIndex].PriceTick,
            Centroid(rows, row => row.TotalVolume, totalVolume, pocTick),
            Centroid(rows, row => row.BuyVolume, buyVolume, pocTick),
            Centroid(rows, row => row.SellVolume, sellVolume, pocTick),
            rows[^1].PriceTick,
            rows[0].PriceTick,
            valueAreaLowTick,
            valueAreaHighTick,
            buyVolume,
            sellVolume,
            buyVolume - sellVolume,
            bar.CumulativeDelta,
            LongestRun(rows, row => row.AskImbalance),
            LongestRun(rows, row => row.BidImbalance),
            normalizedTotalVolume,
            Math.Max(Math.Log(1.0 + normalizedTotalVolume), 1e-6),
            Math.Log(1.0 + normalizedTotalVolume));
    }

    private static int ArgMax(EncodedRow[] rows, Func<EncodedRow, double> selector)
    {
        var bestIndex = 0;
        var bestValue = selector(rows[0]);
        for (var index = 1; index < rows.Length; index++)
        {
            var value = selector(rows[index]);
            if (value > bestValue)
            {
                bestIndex = index;
                bestValue = value;
            }
        }

        return bestIndex;
    }

    private static (double HighTick, double LowTick) ValueArea(EncodedRow[] rows, int pocIndex)
    {
        var target = rows.Sum(row => row.TotalVolume) * 0.70;
        var accumulated = rows[pocIndex].TotalVolume;
        var high = pocIndex;
        var low = pocIndex;

        while (accumulated < target && (high > 0 || low < rows.Length - 1))
        {
            var above = high > 0 ? rows[high - 1].TotalVolume : -1.0;
            var below = low < rows.Length - 1 ? rows[low + 1].TotalVolume : -1.0;
            if (below > above)
            {
                low++;
                accumulated += rows[low].TotalVolume;
            }
            else
            {
                high--;
                accumulated += rows[high].TotalVolume;
            }
        }

        return (rows[high].PriceTick, rows[low].PriceTick);
    }

    private static double Centroid(
        EncodedRow[] rows,
        Func<EncodedRow, double> volume,
        double totalVolume,
        double fallbackTick) =>
        totalVolume > 0
            ? rows.Sum(row => row.PriceTick * volume(row)) / totalVolume
            : fallbackTick;

    private static int LongestRun(EncodedRow[] rows, Func<EncodedRow, bool> predicate)
    {
        var current = 0;
        var longest = 0;
        foreach (var row in rows)
        {
            if (predicate(row))
            {
                current++;
                longest = Math.Max(longest, current);
            }
            else
            {
                current = 0;
            }
        }

        return longest;
    }

    private static double PriceTick(double price) =>
        Math.Floor((price / FootprintModelContract.RowSize) + 0.5);

    private static double ScaledGap(double tickGap) =>
        Clip(tickGap / FootprintModelContract.GapScaleTicks, -2.0, 2.0);

    private static double Clip(double value, double minimum, double maximum) =>
        Math.Min(Math.Max(value, minimum), maximum);

    private static float Float(double value) => checked((float)value);

    private static double Median(double[] values)
    {
        Array.Sort(values);
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2.0
            : values[middle];
    }

    private sealed record EncodedRow(
        double PriceTick,
        double BuyVolume,
        double SellVolume,
        bool BidImbalance,
        bool AskImbalance,
        bool ZeroBid,
        bool ZeroAsk)
    {
        public double TotalVolume => BuyVolume + SellVolume;
        public double Delta => BuyVolume - SellVolume;
    }

    private sealed record EncodedBar(
        DateTime StartUtc,
        EncodedRow[] Rows,
        double PocTick,
        double BuyPocTick,
        double SellPocTick,
        double VolumeCentroidTick,
        double BuyCentroidTick,
        double SellCentroidTick,
        double LowTick,
        double HighTick,
        double ValueAreaLowTick,
        double ValueAreaHighTick,
        double BuyVolume,
        double SellVolume,
        double Delta,
        double CumulativeDelta,
        int StackedBuy,
        int StackedSell,
        double NormalizedTotalVolume,
        double LogTotalVolume,
        double LogVolume);
}
