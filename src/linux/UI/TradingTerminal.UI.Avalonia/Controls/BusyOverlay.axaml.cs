using System;
using Avalonia;
using Avalonia.Controls;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// A full-surface loading curtain matching the shared Windows control: a dimmed input-blocking
/// backdrop, centred status card and deterministic sweeping-arc spinner.
/// </summary>
/// <remarks>
/// Place this control last in a root <see cref="Grid"/> and span every row and column. Bind
/// <see cref="IsActive"/> (or its compatibility alias <see cref="IsBusy"/>), <see cref="Title"/>
/// and <see cref="Message"/>. <see cref="Progress"/> is optional: a finite value shows a clamped
/// 0-100 progress bar, <see cref="double.NaN"/> or infinity shows an indeterminate bar, and
/// <see langword="null"/> preserves the Windows spinner-only presentation.
/// </remarks>
public partial class BusyOverlay : UserControl
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<BusyOverlay, bool>(nameof(IsActive));

    /// <summary>
    /// Compatibility name for callers whose busy-state contract uses <c>IsBusy</c>.
    /// Both CLR properties address the same Avalonia property and therefore cannot diverge.
    /// </summary>
    public static readonly StyledProperty<bool> IsBusyProperty = IsActiveProperty;

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<BusyOverlay, string>(nameof(Title), "Loading…");

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<BusyOverlay, string>(nameof(Message), string.Empty);

    public static readonly StyledProperty<double?> ProgressProperty =
        AvaloniaProperty.Register<BusyOverlay, double?>(nameof(Progress));

    private readonly ProgressBar _progressIndicator;

    public BusyOverlay()
    {
        InitializeComponent();
        _progressIndicator = this.FindControl<ProgressBar>("ProgressIndicator")!;
        ApplyBusyState();
        ApplyProgress();
    }

    /// <summary>When true the curtain is shown and blocks input; when false it is click-through.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Alias for <see cref="IsActive"/> used by progress-oriented consumers.</summary>
    public bool IsBusy
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Primary line describing what is being opened.</summary>
    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Secondary line describing the work or data currently in flight.</summary>
    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>Optional completion percentage from 0 through 100.</summary>
    public double? Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsActiveProperty)
            ApplyBusyState();
        else if (change.Property == ProgressProperty && _progressIndicator is not null)
            ApplyProgress();
    }

    private void ApplyBusyState() => IsHitTestVisible = IsActive;

    private void ApplyProgress()
    {
        var progress = Progress;
        _progressIndicator.IsVisible = progress.HasValue;

        if (!progress.HasValue)
        {
            _progressIndicator.IsIndeterminate = false;
            _progressIndicator.Value = 0;
            return;
        }

        _progressIndicator.IsIndeterminate = !double.IsFinite(progress.Value);
        _progressIndicator.Value = double.IsFinite(progress.Value)
            ? Math.Clamp(progress.Value, 0d, 100d)
            : 0d;
    }
}
