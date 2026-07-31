using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace TradingTerminal.App.Login;

/// <summary>Small clipboard seam so the login view-model stays independent of Avalonia.</summary>
public interface ILoginClipboard
{
    Task SetTextAsync(string text);
}

internal sealed class AvaloniaLoginClipboard : ILoginClipboard
{
    public async Task SetTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var window = desktop.Windows.FirstOrDefault(candidate => candidate.IsActive)
            ?? desktop.MainWindow;
        if (window?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
