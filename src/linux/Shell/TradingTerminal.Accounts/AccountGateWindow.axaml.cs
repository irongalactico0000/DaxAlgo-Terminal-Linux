using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TradingTerminal.Accounts;

public sealed partial class AccountGateWindow : Window
{
    private AccountGateViewModel? _viewModel;

    public AccountGateWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closed += OnClosed;
    }

    internal AccountGateWindow(AccountGateViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.Completed += OnCompleted;
    }

    public event Action<bool>? AccessCompleted;

    private void OnCompleted(bool granted)
    {
        AccessCompleted?.Invoke(granted);
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Completed -= OnCompleted;
            _viewModel.Dispose();
            _viewModel = null;
        }

        Closed -= OnClosed;
    }
}
