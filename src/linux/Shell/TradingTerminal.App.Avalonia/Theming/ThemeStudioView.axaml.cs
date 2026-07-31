using Avalonia.Controls;

namespace TradingTerminal.App.Avalonia.Theming;

public partial class ThemeStudioView : UserControl
{
    public ThemeStudioView()
    {
        InitializeComponent();
    }

    public ThemeStudioView(IThemeManager manager)
        : this()
    {
        DataContext = new ThemeStudioViewModel(
            manager,
            new AvaloniaThemeFilePicker(() => TopLevel.GetTopLevel(this)));
    }
}
