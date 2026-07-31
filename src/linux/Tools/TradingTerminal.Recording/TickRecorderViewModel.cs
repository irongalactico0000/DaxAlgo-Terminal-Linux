using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Recording;

/// <summary>
/// Compatibility name used by the first Avalonia shell. It now exposes the current watchlist-based
/// recorder panel instead of the retired direct-file recorder.
/// </summary>
public sealed class TickRecorderViewModel : RecorderPanelViewModel
{
    public TickRecorderViewModel(
        TickRecordingService service,
        IMarketDataRepository repository,
        IInstrumentRegistry registry,
        IBrokerSelector selector,
        ILogger<RecorderPanelViewModel> logger)
        : base(service, repository, registry, selector, logger)
    {
    }
}
