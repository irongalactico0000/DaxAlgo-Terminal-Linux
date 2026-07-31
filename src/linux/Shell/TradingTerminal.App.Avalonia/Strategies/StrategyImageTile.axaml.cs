using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace TradingTerminal.App.Avalonia.Strategies;

/// <summary>
/// Displays a strategy screenshot without retaining a file handle, falling back to the DaxAlgo mark
/// when the path is blank, missing, or unreadable.
/// </summary>
public partial class StrategyImageTile : UserControl
{
    public static readonly StyledProperty<string?> ImagePathProperty =
        AvaloniaProperty.Register<StrategyImageTile, string?>(nameof(ImagePath));

    private static readonly Bitmap? Logo = LoadLogo();
    private Bitmap? _custom;

    public StrategyImageTile()
    {
        InitializeComponent();
        FallbackLogo.Source = Logo;
        FallbackText.IsVisible = Logo is null;
        Refresh();
    }

    public string? ImagePath
    {
        get => GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ImagePathProperty && CustomPicture is not null) Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _custom?.Dispose();
        _custom = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    private void Refresh()
    {
        _custom?.Dispose();
        _custom = TryLoad(ImagePath);
        CustomPicture.Source = _custom;
        CustomPicture.IsVisible = _custom is not null;
        FallbackPanel.IsVisible = _custom is null;
    }

    private static Bitmap? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? LoadLogo()
    {
        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://TradingTerminal.Accounts/Assets/DaxAlgoLogo.png"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }
}
