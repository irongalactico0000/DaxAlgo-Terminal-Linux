namespace DaxAlgo.FootprintTransformer;

internal static class FootprintModelContract
{
    public const int MetadataSchemaVersion = 2;
    public const int LookbackBars = 64;
    public const int HorizonBars = 8;
    public const int MaximumRows = 1_024;
    public const int RowFeatureCount = 11;
    public const int BarFeatureCount = 23;
    public const int TargetCount = 7;
    public const int QuantileCount = 3;
    public const double RowSize = 2.5;
    public const double PriceScaleTicks = 64.0;
    public const double GapScaleTicks = 32.0;
    public const double LogVolumeScale = 4.0;
    public const double CumulativeDeltaScale = 4.0;
    public const double ImbalanceRatio = 3.0;
    public const double QuantityScale = 1_000.0;
    public const string ModelKind = "fdt-n-footprint-distribution-transformer";
    public const string RuntimeStatus = "shadow-only-research";
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    public static readonly string[] RowFeatureNames =
    [
        "price_offset_from_poc",
        "buy_share",
        "sell_share",
        "total_share",
        "delta_share",
        "log_buy_ratio",
        "log_sell_ratio",
        "bid_imbalance",
        "ask_imbalance",
        "zero_bid",
        "zero_ask",
    ];

    public static readonly string[] BarFeatureNames =
    [
        "poc_offset_from_anchor",
        "buy_poc_offset",
        "sell_poc_offset",
        "volume_centroid_offset",
        "buy_centroid_offset",
        "sell_centroid_offset",
        "low_gap",
        "high_gap",
        "value_area_low_gap",
        "value_area_high_gap",
        "log_volume_relative",
        "buy_fraction",
        "sell_fraction",
        "delta_fraction",
        "cumulative_delta_change",
        "stacked_buy_fraction",
        "stacked_sell_fraction",
        "row_count_fraction",
        "feed_quality",
        "minute_sin",
        "minute_cos",
        "weekday_sin",
        "weekday_cos",
    ];

    public static readonly string[] TargetNames =
    [
        "poc_offset",
        "low_offset",
        "high_offset",
        "buy_poc_offset",
        "sell_poc_offset",
        "log_volume_relative",
        "delta_fraction",
    ];
}

internal sealed record FootprintInferenceInput(
    float[] RowFeatures,
    bool[] RowMask,
    int RowCount,
    float[] BarFeatures,
    bool[] BarMask);

internal interface IFootprintInferenceSession : IDisposable
{
    float[] Run(FootprintInferenceInput input, CancellationToken cancellationToken);
}
