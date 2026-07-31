using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.UI;

namespace DaxAlgo.Daxq.Host;

internal sealed class DaxqLiveSignalStrategyViewModel : LiveSignalStrategyViewModelBase
{
    private readonly DaxqStrategyDefinition _definition;
    private DaxqStrategyKernel? _activeKernel;

    public DaxqLiveSignalStrategyViewModel(
        DaxqStrategyDefinition definition,
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<DaxqLiveSignalStrategyViewModel> logger)
        : base(
            definition.Manifest.StrategyId,
            definition.Manifest.StrategyId,
            services,
            notifications,
            clock,
            routerFactory,
            logger)
    {
        _definition = definition;
    }

    protected override StrategyDataRequirement DataRequirement => _definition.DataRequirement;

    protected override IBacktestStrategy BuildStrategy(Contract contract)
    {
        _activeKernel?.Dispose();
        _activeKernel = _definition.CreateKernel(contract);
        return _activeKernel;
    }

    protected override Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars)
    {
        _activeKernel?.SeedBars(bars);
        return Task.CompletedTask;
    }
}
