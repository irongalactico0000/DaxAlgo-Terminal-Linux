using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TradingTerminal.App.Archive;

public partial class TelegramPromptDialog : Window
{
    public TelegramPromptDialog()
        : this("Telegram login", "Enter the value Telegram is asking for.", isSecret: false)
    {
    }

    public TelegramPromptDialog(string headerText, string helpText, bool isSecret)
    {
        InitializeComponent();
        DataContext = new TelegramPromptDialogContext(headerText, helpText);
        if (isSecret) InputBox.PasswordChar = '●';
        Opened += (_, _) => InputBox.Focus();
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        var context = (TelegramPromptDialogContext)DataContext!;
        if (string.IsNullOrWhiteSpace(context.InputValue))
        {
            InputBox.Focus();
            return;
        }

        Close(context.InputValue);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);
}

internal sealed class TelegramPromptDialogContext
{
    public TelegramPromptDialogContext(string header, string help)
    {
        HeaderText = header;
        HelpText = help;
    }

    public string HeaderText { get; }
    public string HelpText { get; }
    public string InputValue { get; set; } = string.Empty;
}
