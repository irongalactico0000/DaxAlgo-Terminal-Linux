# TradingTerminal.UI.Avalonia — public API surface (macOS/Avalonia)

Generated from source fingerprint `b2d2bcde9e83`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/UI/TradingTerminal.UI.Avalonia/Controls/BusyOverlay.axaml.cs
```cs
   18: public partial class BusyOverlay : UserControl
   20: public static readonly StyledProperty<bool> IsActiveProperty =
   27: public static readonly StyledProperty<bool> IsBusyProperty = IsActiveProperty;
   29: public static readonly StyledProperty<string> TitleProperty =
   32: public static readonly StyledProperty<string> MessageProperty =
   35: public static readonly StyledProperty<double?> ProgressProperty =
   40: public BusyOverlay()
   49: public bool IsActive
   56: public bool IsBusy
   63: public string Title
   70: public string Message
   77: public double? Progress
   83: protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
```

## src/linux/UI/TradingTerminal.UI.Avalonia/GenericStrategyWindow.axaml.cs
```cs
   12: public partial class GenericStrategyWindow : Window
   14: public GenericStrategyWindow() => InitializeComponent();
```
