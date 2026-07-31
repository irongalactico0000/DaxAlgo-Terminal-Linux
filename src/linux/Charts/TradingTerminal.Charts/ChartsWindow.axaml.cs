using Avalonia.Controls;

namespace TradingTerminal.Charts;

/// <summary>
/// Standalone native-Avalonia host around <see cref="ChartsPanel"/>. The shell owns and disposes the
/// transient <see cref="ChartsViewModel"/>, matching the Windows window lifetime contract.
/// </summary>
public partial class ChartsWindow : Window
{
    public ChartsWindow() => InitializeComponent();
}
