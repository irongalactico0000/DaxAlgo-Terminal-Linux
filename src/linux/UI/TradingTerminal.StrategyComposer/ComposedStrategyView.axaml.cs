using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Charts;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.OrderBook;
using TradingTerminal.UI;
using TradingTerminal.VolumeFootprint;

namespace TradingTerminal.StrategyComposer;

/// <summary>
/// Default Avalonia live view for an authored strategy that shipped no view. The descriptor's
/// <see cref="ITradingStrategy.DataRequirement"/> selects price chart, order-book, and footprint
/// panels; an L1-only strategy gets a quote card. The authored
/// <see cref="LiveSignalStrategyViewModelBase"/> remains the data context and lifetime owner of the
/// strategy itself, while this control owns and disposes only the auxiliary panel view-models.
/// </summary>
public partial class ComposedStrategyView : UserControl, IDisposable
{
    private readonly IServiceProvider _services;

    private readonly ChartsViewModel? _chartsVm;
    private readonly OrderBookViewModel? _bookVm;
    private readonly VolumeFootprintViewModel? _footprintVm;

    private LiveSignalStrategyViewModelBase? _strategyVm;
    private Window? _hostWindow;
    private string? _pushedInstrumentKey;
    private bool _disposed;

    /// <summary>Parameterless constructor retained for the Avalonia runtime loader and designer.</summary>
    public ComposedStrategyView()
    {
        _services = null!;
        InitializeComponent();
    }

    public ComposedStrategyView(ITradingStrategy descriptor, IServiceProvider services)
        : this()
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(services);
        _services = services;

        var requirement = descriptor.DataRequirement;
        SetupTitle.Text = descriptor.DisplayName;
        SetupDescription.Text = string.IsNullOrWhiteSpace(descriptor.Description)
            ? "Authored in the AI Strategy Builder. The host composed this live view from the data the strategy declares it consumes."
            : descriptor.Description;
        SetupTags.Text = TagsFor(requirement);
        TapeStat.IsVisible = requirement.HasFlag(StrategyDataRequirement.TradeTape);
        HelpText.Text = BuildHelp(requirement);

        var panels = new List<(string Caption, Control Panel)>();
        if (requirement.HasFlag(StrategyDataRequirement.Bars))
        {
            _chartsVm = ActivatorUtilities.CreateInstance<ChartsViewModel>(services, new ChartsEmbedOptions());
            panels.Add(("PRICE · 1m", new ChartsPanel
            {
                Features = ChartsPanelFeatures.Embedded,
                DataContext = _chartsVm,
            }));
        }

        if (requirement.HasFlag(StrategyDataRequirement.Depth))
        {
            _bookVm = CreateEmbeddedViewModel<OrderBookViewModel>(
                services, "TradingTerminal.OrderBook.OrderBookEmbedOptions");
            // Older macOS surface builds preselect SPY in the standalone constructor. Clear that
            // compatibility default immediately so the authored strategy remains the only owner.
            _bookVm.SelectedInstrument = null;
            ApplyOrderBookEmbedPreset(_bookVm);
            panels.Add(("ORDER BOOK · DEPTH", new EmbeddedOrderBookPanel { DataContext = _bookVm }));
        }

        if (requirement.HasFlag(StrategyDataRequirement.TradeTape))
        {
            _footprintVm = CreateEmbeddedViewModel<VolumeFootprintViewModel>(
                services, "TradingTerminal.VolumeFootprint.VolumeFootprintEmbedOptions");
            _footprintVm.SelectedInstrument = null;
            ApplyFootprintEmbedPreset(_footprintVm);
            panels.Add(("FOOTPRINT · TRADE TAPE", new EmbeddedVolumeFootprintPanel { DataContext = _footprintVm }));
        }

        BuildPanelGrid(panels);

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        UpdateRuntimeState();
    }

    /// <summary>The composed panel controls, in Bars/Depth/TradeTape display order.</summary>
    public IReadOnlyList<Control> Panels { get; private set; } = [];

    private void BuildPanelGrid(List<(string Caption, Control Panel)> panels)
    {
        Panels = [.. panels.Select(panel => panel.Panel)];
        if (panels.Count == 0)
        {
            QuoteCard.IsVisible = true;
            return;
        }

        for (var i = 0; i < panels.Count; i++)
        {
            if (i > 0)
            {
                PanelHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var splitter = new GridSplitter
                {
                    Width = 5,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                    Background = Brushes.Transparent,
                };
                Grid.SetColumn(splitter, PanelHost.ColumnDefinitions.Count - 1);
                PanelHost.Children.Add(splitter);
            }

            PanelHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 220,
            });

            var cell = new DockPanel();
            var captionText = new TextBlock { Text = panels[i].Caption };
            captionText.Classes.Add("panel-caption-text");
            var caption = new Border { Child = captionText };
            caption.Classes.Add("panel-caption");
            DockPanel.SetDock(caption, Dock.Top);
            cell.Children.Add(caption);
            cell.Children.Add(panels[i].Panel);

            Grid.SetColumn(cell, PanelHost.ColumnDefinitions.Count - 1);
            PanelHost.Children.Add(cell);
        }
    }

    private static string TagsFor(StrategyDataRequirement requirement)
    {
        var tags = new List<string>();
        if (requirement.HasFlag(StrategyDataRequirement.L1)) tags.Add("L1");
        if (requirement.HasFlag(StrategyDataRequirement.Bars)) tags.Add("Bars");
        if (requirement.HasFlag(StrategyDataRequirement.Depth)) tags.Add("Depth");
        if (requirement.HasFlag(StrategyDataRequirement.TradeTape)) tags.Add("Trade tape");
        return tags.Count == 0 ? "L1" : string.Join("  ·  ", tags);
    }

    private static string BuildHelp(StrategyDataRequirement requirement)
    {
        var lines = new List<string>
        {
            "Composed view — this authored strategy shipped no view, so the host built one from the data it declares it consumes.",
        };
        if (requirement.HasFlag(StrategyDataRequirement.Bars))
            lines.Add("Price — one-minute candles and indicators for the instrument the strategy is trading.");
        if (requirement.HasFlag(StrategyDataRequirement.Depth))
            lines.Add("Order book — the live depth ladder and microstructure metrics seen by OnDepthAsync.");
        if (requirement.HasFlag(StrategyDataRequirement.TradeTape))
            lines.Add("Footprint — volume and order-flow metrics built from the same prints consumed by OnTradeAsync.");
        var panelRequirements = StrategyDataRequirement.Bars
                                | StrategyDataRequirement.Depth
                                | StrategyDataRequirement.TradeTape;
        if ((requirement & panelRequirements) == 0)
            lines.Add("Quote card — this strategy consumes L1 quotes only.");
        lines.Add("Signals — every order or direct signal emitted by the kernel lands in the feed. Arming marks the strategy live; this host does not route real orders.");
        lines.Add("Embedded panels — independent instrument selectors and learned forecasts are disabled. The strategy owns instrument and pause state.");
        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_strategyVm is not null)
            _strategyVm.PropertyChanged -= OnStrategyPropertyChanged;

        _strategyVm = DataContext as LiveSignalStrategyViewModelBase;
        if (_strategyVm is not null)
        {
            _strategyVm.PropertyChanged += OnStrategyPropertyChanged;
            PushInstrument();
            SyncPause();
        }

        UpdateRuntimeState();
    }

    private void OnStrategyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LiveSignalStrategyViewModelBase.SelectedInstrument):
            case nameof(LiveSignalStrategyViewModelBase.IsConfigured):
                PushInstrument();
                break;
            case nameof(LiveSignalStrategyViewModelBase.IsPaused):
                SyncPause();
                break;
        }

        if (e.PropertyName is nameof(LiveSignalStrategyViewModelBase.IsConfigured)
            or nameof(LiveSignalStrategyViewModelBase.IsStreaming)
            or nameof(LiveSignalStrategyViewModelBase.IsAlgoRunning)
            or nameof(LiveSignalStrategyViewModelBase.ValidationError))
        {
            UpdateRuntimeState();
        }
    }

    private void UpdateRuntimeState()
    {
        var configured = _strategyVm?.IsConfigured == true;
        SetupPane.IsVisible = !configured;
        RuntimePane.IsVisible = configured;
        ValidationBanner.IsVisible = !string.IsNullOrWhiteSpace(_strategyVm?.ValidationError);

        var streaming = _strategyVm?.IsStreaming == true;
        StartButton.IsVisible = !streaming;
        StopButton.IsVisible = streaming;

        var armed = _strategyVm?.IsAlgoRunning == true;
        ArmButton.IsVisible = !armed;
        DisarmButton.IsVisible = armed;
        ArmedPill.IsVisible = armed;
    }

    /// <summary>
    /// Pushes the configured strategy instrument into every panel. The symbol and broker form the
    /// identity key so assigning an equivalent picker row does not restart panel subscriptions.
    /// </summary>
    private void PushInstrument()
    {
        if (_strategyVm is not { IsConfigured: true, SelectedInstrument: { } instrument })
            return;

        var key = $"{instrument.Contract.Symbol}|{instrument.Broker}";
        if (key == _pushedInstrumentKey)
            return;
        _pushedInstrumentKey = key;

        if (_bookVm is not null)
            _bookVm.SelectedInstrument = instrument;
        if (_footprintVm is not null)
            _footprintVm.SelectedInstrument = instrument;
        if (_chartsVm is not null)
        {
            _chartsVm.SelectedInstrument = new TradableInstrument(
                instrument.DisplayName,
                instrument.Category,
                instrument.Contract,
                instrument.Broker ?? FallbackBroker());
        }
    }

    private BrokerKind FallbackBroker()
    {
        var selector = _services.GetService<IBrokerSelector>();
        return selector is { Connected.Count: > 0 } connected
            ? connected.Connected[0]
            : BrokerKind.Simulated;
    }

    private void SyncPause()
    {
        if (_strategyVm is null)
            return;

        var paused = _strategyVm.IsPaused;
        if (_chartsVm is not null)
            _chartsVm.IsPaused = paused;
        TrySetBoolean(_bookVm, "IsPaused", paused);
        TrySetBoolean(_footprintVm, "IsPaused", paused);
        foreach (var panel in Panels.OfType<IEmbeddedPausable>())
            panel.SetPaused(paused);
    }

    /// <summary>
    /// Creates the panel VM with its copied Windows embed-options record when that record is present
    /// in the destination surface. The compatibility fallback keeps this composer usable with the
    /// current macOS surface assemblies while they converge on the same constructor seam.
    /// </summary>
    private static T CreateEmbeddedViewModel<T>(IServiceProvider services, string optionsTypeName)
        where T : class
    {
        var optionsType = typeof(T).Assembly.GetType(optionsTypeName, throwOnError: false);
        if (optionsType is null)
            return ActivatorUtilities.CreateInstance<T>(services);

        var constructor = optionsType.GetConstructors()
            .OrderBy(candidate => candidate.GetParameters().Length)
            .FirstOrDefault();
        if (constructor is null)
            return ActivatorUtilities.CreateInstance<T>(services);

        var arguments = constructor.GetParameters()
            .Select(parameter => parameter.HasDefaultValue
                ? parameter.DefaultValue
                : parameter.ParameterType == typeof(bool)
                    ? false
                    : parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null)
            .ToArray();
        var options = constructor.Invoke(arguments);
        return ActivatorUtilities.CreateInstance<T>(services, options);
    }

    private static void ApplyOrderBookEmbedPreset(OrderBookViewModel viewModel)
    {
        TrySetBoolean(viewModel, "MlEnabled", false);
        TrySetBoolean(viewModel, "ShowMlForecast", false);
        TrySetBoolean(viewModel, "ShowHeatmap", true);
        TrySetBoolean(viewModel, "ShowTrades", true);
        TrySetBoolean(viewModel, "ShowMicropriceLine", true);
        TrySetBoolean(viewModel, "ShowImbalanceLane", true);
    }

    private static void ApplyFootprintEmbedPreset(VolumeFootprintViewModel viewModel)
    {
        TrySetBoolean(viewModel, "MlEnabled", false);
        TrySetBoolean(viewModel, "ShowMlPrediction", false);
        TrySetBoolean(viewModel, "ShowPredictedBars", false);
        TrySetBoolean(viewModel, "ShowLinearFit", false);
        TrySetBoolean(viewModel, "ShowQuadraticFit", false);
        TrySetBoolean(viewModel, "ShowCubicFit", false);
        TrySetBoolean(viewModel, "ShowTheilSenFit", false);
        TrySetBoolean(viewModel, "ShowExponentialFit", false);
        TrySetBoolean(viewModel, "ShowLogarithmicFit", false);
        TrySetBoolean(viewModel, "ShowLowessFit", false);
        TrySetBoolean(viewModel, "ShowImbalances", true);
        TrySetBoolean(viewModel, "ShowValueArea", true);
        TrySetBoolean(viewModel, "ShowVolumeProfile", true);
    }

    private static void TrySetBoolean(object? target, string propertyName, bool value)
    {
        var property = target?.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public);
        if (property is { CanWrite: true, PropertyType: not null }
            && property.PropertyType == typeof(bool))
        {
            property.SetValue(target, value);
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_hostWindow is not null || TopLevel.GetTopLevel(this) is not Window host)
            return;
        _hostWindow = host;
        _hostWindow.Closed += OnHostWindowClosed;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => Dispose();

    private void OnHostWindowClosed(object? sender, EventArgs e) => Dispose();

    private async void SaveSnapshot_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage)
            return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"strategy-{Sanitize(_strategyVm?.StrategyId ?? "authored")}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            FileTypeChoices =
            [
                new FilePickerFileType("PNG image") { Patterns = ["*.png"] },
            ],
        });
        if (file is null)
            return;

        try
        {
            var scale = topLevel.RenderScaling;
            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(this);
            await using var stream = await file.OpenWriteAsync();
            bitmap.Save(stream);
            await stream.FlushAsync();
            if (_strategyVm is not null)
                _strategyVm.Status = $"Snapshot saved → {file.Name}";
        }
        catch (Exception ex)
        {
            if (_strategyVm is not null)
                _strategyVm.Status = $"Snapshot failed: {ex.Message}";
        }
    }

    private static string Sanitize(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());

    /// <summary>
    /// Drops panel subscriptions, timers, and channels. The strategy VM is deliberately not disposed
    /// here; the shell owns it, matching hand-authored strategy views. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DataContextChanged -= OnDataContextChanged;
        AttachedToVisualTree -= OnAttachedToVisualTree;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;

        if (_hostWindow is not null)
        {
            _hostWindow.Closed -= OnHostWindowClosed;
            _hostWindow = null;
        }

        if (_strategyVm is not null)
        {
            _strategyVm.PropertyChanged -= OnStrategyPropertyChanged;
            _strategyVm = null;
        }

        _chartsVm?.Dispose();
        _bookVm?.Dispose();
        _footprintVm?.Dispose();
        foreach (var panel in Panels.OfType<IDisposable>())
            panel.Dispose();
    }
}
