using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;

namespace TradingTerminal.App.Avalonia.Diagnostics;

/// <summary>Last-line crash reporting shared by the macOS shell's UI and background work.</summary>
internal static class CrashGuard
{
    private const int MaxReportsKept = 30;
    private static readonly object Gate = new();
    private static DateTime _lastDialogUtc = DateTime.MinValue;
    private static int _installed;
    private static string _appName = "DaxAlgo Terminal";
    private static Action<string, string, string>? _log;

    public static string ReportDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal",
        "crash-reports");

    public static void Install(string appName, Action<string, string, string>? log = null)
    {
        _appName = appName;
        _log = log;
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;

        Dispatcher.UIThread.UnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
    }

    private static void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = WriteReport("dispatcher", e.Exception);
        _log?.Invoke(
            "System",
            "ERROR",
            $"Unhandled UI exception: {e.Exception.GetType().Name}: {e.Exception.Message} " +
            $"(report: {path ?? "n/a"})");
        e.Handled = true;

        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastDialogUtc < TimeSpan.FromSeconds(10)) return;
            _lastDialogUtc = now;
        }

        Dispatcher.UIThread.Post(() => ShowRecoveryNotice(e.Exception, path));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        var exception = e.Exception.GetBaseException();
        var path = WriteReport("task", exception);
        _log?.Invoke(
            "System",
            "WARN",
            $"Unobserved task exception: {exception.GetType().Name}: {exception.Message} " +
            $"(report: {path ?? "n/a"})");
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e) =>
        WriteReport("fatal", e.ExceptionObject as Exception);

    private static void ShowRecoveryNotice(Exception exception, string? reportPath)
    {
        try
        {
            var close = new Button
            {
                Content = "Continue",
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 100,
            };
            var window = new Window
            {
                Title = $"{_appName} — unexpected error",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(24),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text =
                                $"Something went wrong, but the terminal is still running.\n\n" +
                                $"{exception.GetType().Name}: {Truncate(exception.Message, 300)}\n\n" +
                                (reportPath is null ? string.Empty : $"Crash report: {reportPath}\n\n") +
                                "If the terminal looks inconsistent, save your work and restart.",
                            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                        },
                        close,
                    },
                },
            };
            close.Click += (_, _) => window.Close();

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.Windows.FirstOrDefault(candidate => candidate.IsActive) is { } owner)
                window.Show(owner);
            else
                window.Show();
        }
        catch
        {
            // Disk report remains available if the UI is already tearing down.
        }
    }

    private static string? WriteReport(string kind, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(ReportDirectory);
            var path = Path.Combine(
                ReportDirectory,
                $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{kind}.txt");
            File.WriteAllText(
                path,
                $"{_appName}\n" +
                $"When (UTC): {DateTime.UtcNow:O}\n" +
                $"Kind:       {kind}\n" +
                $"OS:         {Environment.OSVersion} · .NET {Environment.Version}\n\n" +
                $"{exception?.ToString() ?? "(no exception object)"}\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            TrimOldReports();
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void TrimOldReports()
    {
        foreach (var file in new DirectoryInfo(ReportDirectory)
                     .GetFiles("crash-*.txt")
                     .OrderByDescending(candidate => candidate.CreationTimeUtc)
                     .Skip(MaxReportsKept))
        {
            try { file.Delete(); }
            catch { }
        }
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";
}
