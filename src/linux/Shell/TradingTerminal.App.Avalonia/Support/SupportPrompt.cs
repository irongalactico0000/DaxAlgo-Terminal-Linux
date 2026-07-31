using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportWindow = TradingTerminal.App.Avalonia.Settings.SupportWindow;

namespace TradingTerminal.App.Support;

/// <summary>
/// Shows the support window at most once automatically per launch, after a short random delay,
/// and keeps one live owned window for both automatic and Help-menu entry points.
/// </summary>
internal sealed class SupportPrompt : ISupportPrompt
{
    // v1 matches the Windows shell: show every launch, but at a random moment. Lower this value if
    // a later release makes the prompt probabilistic.
    private const double LaunchShowProbability = 1.0;
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(9);

    private readonly IServiceProvider _services;
    private readonly ILogger<SupportPrompt> _logger;
    private readonly Random _rng = new();

    private bool _firedThisLaunch;
    private DispatcherTimer? _launchTimer;
    private SupportWindow? _current;

    public SupportPrompt(IServiceProvider services, ILogger<SupportPrompt> logger)
    {
        _services = services;
        _logger = logger;
    }

    public void MaybeShowOnLaunch(Window owner)
    {
        if (_firedThisLaunch) return;
        _firedThisLaunch = true;

        if (_rng.NextDouble() > LaunchShowProbability)
        {
            _logger.LogDebug("Support prompt skipped this launch by random gate.");
            return;
        }

        var delaySeconds = MinDelay.TotalSeconds +
                           (_rng.NextDouble() * (MaxDelay.TotalSeconds - MinDelay.TotalSeconds));
        var ownerClosed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };

        void StopTimer()
        {
            timer.Stop();
            timer.Tick -= OnTick;
            owner.Closed -= OnOwnerClosed;
            if (ReferenceEquals(_launchTimer, timer)) _launchTimer = null;
        }

        void OnOwnerClosed(object? sender, EventArgs args)
        {
            ownerClosed = true;
            StopTimer();
        }

        void OnTick(object? sender, EventArgs args)
        {
            StopTimer();
            if (!ownerClosed && owner.IsVisible) Show(owner);
        }

        owner.Closed += OnOwnerClosed;
        timer.Tick += OnTick;
        _launchTimer = timer;
        timer.Start();
    }

    public void Show(Window owner)
    {
        if (_current is not null)
        {
            _current.Activate();
            return;
        }

        var window = _services.GetRequiredService<SupportWindow>();
        window.DataContext = _services.GetRequiredService<SupportViewModel>();

        void OnClosed(object? sender, EventArgs args)
        {
            window.Closed -= OnClosed;
            if (ReferenceEquals(_current, window)) _current = null;
        }

        window.Closed += OnClosed;
        _current = window;
        window.Show(owner);
        _logger.LogInformation("Shown support / feedback window.");
    }
}
