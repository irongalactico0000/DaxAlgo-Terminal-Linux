using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.App.Plugins;

/// <summary>
/// Informed consent for an unsigned, unpinned plugin. The safe/default answer is rejection and the
/// decision remains bound to the scanned artifact hash by <see cref="PluginStateStore"/>.
/// </summary>
public partial class PluginConsentDialog : Window
{
    public PluginConsentDialog()
    {
        InitializeComponent();
        Headline = "Run this unverified plugin?";
        PublisherText = "Publisher: unknown.";
        PathText = string.Empty;
        HashText = string.Empty;
        Capabilities = [];
        DataContext = this;
    }

    internal PluginConsentDialog(PluginConsentRequest request) : this()
    {
        Headline = $"Run “{request.DisplayName}”?";
        PublisherText = string.IsNullOrWhiteSpace(request.Publisher)
            ? "Publisher: unknown — this plugin is not signed and was not shipped with the app."
            : $"Publisher: {request.Publisher} (declared, not verified — the plugin is unsigned).";
        PathText = request.AssemblyPath;
        HashText = $"sha256 {Shorten(request.Sha256)}";
        Capabilities = Describe(request.Scan);
        DataContext = null;
        DataContext = this;
    }

    public string Headline { get; private set; }
    public string PublisherText { get; private set; }
    public string PathText { get; private set; }
    public string HashText { get; private set; }
    public IReadOnlyList<string> Capabilities { get; private set; }

    public static Task<bool> AskAsync(PluginConsentRequest request, Window owner)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(owner);
        return new PluginConsentDialog(request).ShowDialog<bool>(owner);
    }

    private static IReadOnlyList<string> Describe(PluginScanReport scan)
    {
        var findings = scan.Findings
            .Where(f => f.Severity != PluginScanSeverity.Clean)
            .Select(f => "• " + f.Detail)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return findings.Count > 0
            ? findings
            : ["• Nothing beyond ordinary strategy code was flagged. That is not a guarantee: a static scan cannot see everything."];
    }

    private static string Shorten(string hash) =>
        hash.Length <= 16 ? hash : $"{hash[..8]}…{hash[^8..]}";

    private void OnRejectClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConsentClicked(object? sender, RoutedEventArgs e) => Close(true);
}

/// <summary>
/// Avalonia adapter for the loader's synchronous consent seam. It is intentionally safe only after
/// the desktop lifetime has a visible owner and when plugin discovery is running off the UI thread.
/// A pre-host/UI-thread call is rejected immediately; a stalled prompt times out and rejects rather
/// than deadlocking startup. The composition root must therefore establish a visible bootstrap owner,
/// run <see cref="PluginLoader"/> on a background thread against the service collection, and only then
/// build/start the final host. Passing this adapter to the current UI-thread pre-host load fails closed.
/// </summary>
public sealed class PluginConsentPrompt : IPluginConsentPrompt
{
    private readonly TimeSpan _timeout;

    public PluginConsentPrompt(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public bool RequestConsent(PluginConsentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Dispatcher.UIThread.CheckAccess())
            return Reject("Plugin consent was requested on the UI thread; refusing to block Avalonia.");

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is null)
        {
            return Reject("Plugin consent was requested before the desktop owner was ready.");
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PluginConsentDialog? dialog = null;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (completion.Task.IsCompleted) return;

                var owner = desktop.Windows.FirstOrDefault(window => window.IsActive)
                    ?? desktop.MainWindow;
                if (owner is null || !owner.IsVisible)
                {
                    completion.TrySetResult(false);
                    return;
                }

                dialog = new PluginConsentDialog(request);
                completion.TrySetResult(await dialog.ShowDialog<bool>(owner));
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Plugin consent prompt failed closed: {ex}");
                completion.TrySetResult(false);
            }
        });

        if (!completion.Task.Wait(_timeout))
        {
            completion.TrySetResult(false);
            Dispatcher.UIThread.Post(() => dialog?.Close(false));
            Trace.TraceWarning("Plugin consent prompt timed out and was rejected.");
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    private static bool Reject(string reason)
    {
        Trace.TraceWarning(reason);
        return false;
    }
}
