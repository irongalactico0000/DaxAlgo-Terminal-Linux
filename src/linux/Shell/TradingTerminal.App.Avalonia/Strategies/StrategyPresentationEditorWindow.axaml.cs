using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TradingTerminal.App.Avalonia.Strategies;

public partial class StrategyPresentationEditorWindow : Window
{
    public StrategyPresentationEditorWindow() => InitializeComponent();

    private void Save_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
