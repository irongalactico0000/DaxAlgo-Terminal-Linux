using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using TradingTerminal.Infrastructure.MarketData.Archive.Telegram;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Avalonia bridge for WTelegramClient's synchronous configuration callback. The transport runs on
/// a worker thread; this adapter marshals verification-code and 2FA prompts to the desktop UI.
/// </summary>
public sealed class AvaloniaTelegramAuthPrompt : ITelegramAuthPrompt
{
    public Task<string?> PromptAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (ct.IsCancellationRequested) return Task.FromCanceled<string?>(ct);

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return Task.FromResult<string?>(null);

        var (header, help) = LabelFor(key);
        if (Dispatcher.UIThread.CheckAccess())
            return ShowPromptAsync(desktop, key, header, help, ct);

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        TelegramPromptDialog? dialog = null;
        var cancellation = ct.Register(() =>
        {
            completion.TrySetCanceled(ct);
            Dispatcher.UIThread.Post(() => dialog?.Close(null));
        });

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (completion.Task.IsCompleted) return;
                var owner = FindOwner(desktop);
                if (owner is null)
                {
                    completion.TrySetResult(null);
                    return;
                }

                dialog = new TelegramPromptDialog(header, help, key == "password");
                var result = await dialog.ShowDialog<string?>(owner);
                completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return AwaitCompletionAsync(completion.Task, cancellation);
    }

    private static async Task<string?> ShowPromptAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        string key,
        string header,
        string help,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var owner = FindOwner(desktop);
        if (owner is null) return null;

        var dialog = new TelegramPromptDialog(header, help, key == "password");
        using var cancellation = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(null)));
        return await dialog.ShowDialog<string?>(owner);
    }

    private static async Task<string?> AwaitCompletionAsync(
        Task<string?> completion,
        CancellationTokenRegistration cancellation)
    {
        using (cancellation)
            return await completion.ConfigureAwait(false);
    }

    private static Window? FindOwner(IClassicDesktopStyleApplicationLifetime desktop) =>
        desktop.Windows.FirstOrDefault(window => window.IsActive && window.IsVisible)
        ?? desktop.Windows.FirstOrDefault(window => window.IsVisible)
        ?? (desktop.MainWindow?.IsVisible == true ? desktop.MainWindow : null);

    private static (string Header, string Help) LabelFor(string key) => key switch
    {
        "verification_code" => ("Enter the Telegram code",
            "Telegram just messaged a verification code to your phone or the Telegram app on another device. Type it below."),
        "password" => ("Two-factor password",
            "Your Telegram account has cloud password (2FA) enabled. Enter it to finish logging in."),
        "phone_number" => ("Phone number",
            "Telegram needs your phone in international format (e.g. +91…)."),
        "first_name" => ("First name", "Used only if this phone has never signed up to Telegram."),
        "last_name" => ("Last name", "Used only if this phone has never signed up to Telegram."),
        _ => ($"Telegram needs: {key}", "Enter the value Telegram is asking for."),
    };
}
