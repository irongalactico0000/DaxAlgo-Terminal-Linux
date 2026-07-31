using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TradingTerminal.Recording;

public partial class RecorderPanelView : UserControl
{
    public RecorderPanelView() => InitializeComponent();

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RecorderPanelViewModel panel &&
            sender is Button { DataContext: RecorderEntry entry })
        {
            panel.Service.Remove(entry);
        }
    }
}
