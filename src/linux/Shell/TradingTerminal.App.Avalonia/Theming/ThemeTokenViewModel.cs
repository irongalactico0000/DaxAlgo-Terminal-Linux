using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TradingTerminal.App.Avalonia.Theming;

/// <summary>
/// One editable Theme Studio row. Every valid edit is pushed immediately into the running
/// application, while hex and A/R/G/B representations stay synchronized.
/// </summary>
public sealed class ThemeTokenViewModel : ObservableObject
{
    private readonly IThemeManager _manager;
    private bool _suppress;
    private Color _color;

    public ThemeTokenViewModel(IThemeManager manager, ThemeToken token)
    {
        _manager = manager;
        DisplayName = token.DisplayName;
        PrimaryKey = token.PrimaryKey;
        LinkedColorKey = token.LinkedColorKey;
        IsGradient = token.Kind == ThemeTokenKind.Gradient;

        if (IsGradient)
        {
            Stops = new ObservableCollection<GradientStopViewModel>(
                token.GradientStops.Select((color, index) =>
                    new GradientStopViewModel(index, color, ApplyGradient)));
        }
        else
        {
            Stops = new ObservableCollection<GradientStopViewModel>();
            _color = token.SolidValue;
        }
    }

    public string DisplayName { get; }

    public string PrimaryKey { get; }

    public string? LinkedColorKey { get; }

    public bool IsGradient { get; }

    public bool IsSolid => !IsGradient;

    public ObservableCollection<GradientStopViewModel> Stops { get; }

    public Color Color => _color;

    public IBrush Swatch => new SolidColorBrush(_color);

    public string Hex
    {
        get => ThemeColorUtil.ToHex(_color);
        set
        {
            if (_suppress || !ThemeColorUtil.TryParse(value, out var color) || color == _color)
                return;
            SetColor(color, syncHex: false);
        }
    }

    public byte A
    {
        get => _color.A;
        set => SetChannel(value, (color, channel) => Color.FromArgb(channel, color.R, color.G, color.B));
    }

    public byte R
    {
        get => _color.R;
        set => SetChannel(value, (color, channel) => Color.FromArgb(color.A, channel, color.G, color.B));
    }

    public byte G
    {
        get => _color.G;
        set => SetChannel(value, (color, channel) => Color.FromArgb(color.A, color.R, channel, color.B));
    }

    public byte B
    {
        get => _color.B;
        set => SetChannel(value, (color, channel) => Color.FromArgb(color.A, color.R, color.G, channel));
    }

    public IBrush GradientPreview
    {
        get
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = RelativePoint.TopLeft,
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            };
            for (var index = 0; index < Stops.Count; index++)
            {
                brush.GradientStops.Add(new GradientStop(
                    Stops[index].Color,
                    Stops.Count <= 1 ? 0 : (double)index / (Stops.Count - 1)));
            }

            return brush;
        }
    }

    private void SetChannel(byte value, Func<Color, byte, Color> build)
    {
        if (_suppress)
            return;
        var next = build(_color, value);
        if (next != _color)
            SetColor(next, syncHex: true);
    }

    private void SetColor(Color color, bool syncHex)
    {
        _color = color;
        _manager.SetColorOverride(PrimaryKey, color);
        if (LinkedColorKey is not null)
            _manager.SetColorOverride(LinkedColorKey, color);

        _suppress = true;
        if (syncHex)
            OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(A));
        OnPropertyChanged(nameof(R));
        OnPropertyChanged(nameof(G));
        OnPropertyChanged(nameof(B));
        OnPropertyChanged(nameof(Swatch));
        _suppress = false;
    }

    private void ApplyGradient()
    {
        _manager.SetGradientOverride(PrimaryKey, Stops.Select(stop => stop.Color).ToList());
        OnPropertyChanged(nameof(GradientPreview));
    }
}

/// <summary>One editable colour stop inside a gradient token.</summary>
public sealed class GradientStopViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _suppress;
    private Color _color;

    public GradientStopViewModel(int index, Color color, Action onChanged)
    {
        Index = index;
        _color = color;
        _onChanged = onChanged;
    }

    public int Index { get; }

    public string Label => $"Stop {Index + 1}";

    public Color Color => _color;

    public IBrush Swatch => new SolidColorBrush(_color);

    public string Hex
    {
        get => ThemeColorUtil.ToHex(_color);
        set
        {
            if (_suppress || !ThemeColorUtil.TryParse(value, out var color) || color == _color)
                return;
            SetColor(color, syncHex: false);
        }
    }

    public byte A
    {
        get => _color.A;
        set => SetChannel(value, (color, channel) => Color.FromArgb(channel, color.R, color.G, color.B));
    }

    public byte R
    {
        get => _color.R;
        set => SetChannel(value, (color, channel) => Color.FromArgb(color.A, channel, color.G, color.B));
    }

    public byte G
    {
        get => _color.G;
        set => SetChannel(value, (color, channel) => Color.FromArgb(color.A, color.R, channel, color.B));
    }

    public byte B
    {
        get => _color.B;
        set => SetChannel(value, (color, channel) => Color.FromArgb(color.A, color.R, color.G, channel));
    }

    private void SetChannel(byte value, Func<Color, byte, Color> build)
    {
        if (_suppress)
            return;
        var next = build(_color, value);
        if (next != _color)
            SetColor(next, syncHex: true);
    }

    private void SetColor(Color color, bool syncHex)
    {
        _color = color;
        _suppress = true;
        if (syncHex)
            OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(A));
        OnPropertyChanged(nameof(R));
        OnPropertyChanged(nameof(G));
        OnPropertyChanged(nameof(B));
        OnPropertyChanged(nameof(Swatch));
        _suppress = false;
        _onChanged();
    }
}

internal static class ThemeColorUtil
{
    public static string ToHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    public static bool TryParse(string? value, out Color color)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return Color.TryParse(value, out color);
        color = default;
        return false;
    }
}
