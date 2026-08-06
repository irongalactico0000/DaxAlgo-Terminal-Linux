namespace TradingTerminal.TradeIr.Runtime;

/// <summary>
/// Exact semantic contracts implemented by this runtime. Headless compatibility tests hash these
/// strings using the Core catalog convention, so a catalog change requires an explicit runtime
/// review rather than silently reinterpreting an admitted plan.
/// </summary>
public static class TradeIrRuntimeSemanticsV1
{
    public const string Version = "daxalgo.tradeir.runtime/v1";

    public const string QuoteMidContract =
        "v1;source=quote-l1;value=(bid+ask)/2;availability=point-in-time;missing=reject";

    public const string EmaContract =
        "v1;ema-alpha=2/(period+1);seed=first-value;update=event;missing=reject;reset=run-start;ready=sample-count>=period";

    public const string GreaterThanContract =
        "v1;value=left>right;units=equal;axes=identical;missing=propagate";

    public const string FixedQuantityContract =
        "v1;true=when_true;false=when_false;unit=position.quantity;host-owned=true";

    public const string TrailingFractionContract =
        "v1;trail=fraction;anchor=favorable-extreme-since-position-open;reset=flat-or-reversal;host-owned=true";

    public const string MarketIntentContract =
        "v1;intent=market;quantity=target-current-position;tif=declared;no-adapter-authority=true";
}
