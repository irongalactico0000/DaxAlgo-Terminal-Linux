using System.Diagnostics;
using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Threading;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.App.Avalonia.Diagnostics;

/// <summary>Dev/CI sweep that constructs, renders, and closes every plugin strategy view.</summary>
internal static class StrategyWindowSmoke
{
    public static async Task<int> RunAsync(
        IStrategyFactory factory,
        string reportPath,
        IEnumerable<string>? loadedPluginNames = null)
    {
        var lines = new List<string>
        {
            $"Strategy window smoke — {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {AppDomain.CurrentDomain.FriendlyName}",
        };
        if (loadedPluginNames is not null)
            lines.Add($"Plugins loaded: {string.Join(", ", loadedPluginNames)}");
        lines.Add($"Catalog strategies: {factory.All.Count}");
        lines.Add(string.Empty);

        var failures = 0;
        Exception? dispatcherFault = null;
        DispatcherUnhandledExceptionEventHandler capture = (_, args) =>
        {
            dispatcherFault ??= args.Exception;
            args.Handled = true;
        };
        Dispatcher.UIThread.UnhandledException += capture;
        try
        {
            foreach (var strategy in factory.All)
            {
                dispatcherFault = null;
                var stopwatch = Stopwatch.StartNew();
                Window? window = null;
                object? viewModel = null;
                try
                {
                    var host = factory.Create(strategy.Id);
                    viewModel = host.ViewModel;
                    window = host.View as Window ?? new Window
                    {
                        Title = host.DisplayName,
                        Content = host.View,
                        Width = 1200,
                        Height = 800,
                    };
                    var context = AssemblyLoadContext.GetLoadContext(host.View.GetType().Assembly)?.Name
                        ?? "default";

                    window.Show();
                    await PumpAsync().ConfigureAwait(true);
                    if (dispatcherFault is not null) throw dispatcherFault;
                    lines.Add(
                        $"PASS  {strategy.Id,-34} {stopwatch.ElapsedMilliseconds,5} ms  " +
                        $"view={host.View.GetType().Name}  ctx={context}");
                }
                catch (Exception exception)
                {
                    failures++;
                    lines.Add(
                        $"FAIL  {strategy.Id,-34} {stopwatch.ElapsedMilliseconds,5} ms  {Flatten(exception)}");
                }
                finally
                {
                    try { window?.Close(); }
                    catch (Exception exception)
                    {
                        lines.Add($"WARN  {strategy.Id,-34} close failed: {Flatten(exception)}");
                    }
                    try { (viewModel as IDisposable)?.Dispose(); }
                    catch (Exception exception)
                    {
                        lines.Add($"WARN  {strategy.Id,-34} dispose failed: {Flatten(exception)}");
                    }
                    dispatcherFault = null;
                    await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                    if (dispatcherFault is not null)
                        lines.Add($"WARN  {strategy.Id,-34} fault during close: {Flatten(dispatcherFault)}");
                }
            }
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= capture;
        }

        lines.Add(string.Empty);
        lines.Add(failures == 0 && factory.All.Count > 0
            ? $"RESULT: PASS ({factory.All.Count} windows opened)"
            : $"RESULT: FAIL ({failures} of {factory.All.Count} failed)");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllLinesAsync(reportPath, lines).ConfigureAwait(false);
        return failures == 0 && factory.All.Count > 0 ? 0 : 1;
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Task.Delay(400).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (Exception? current = exception; current is not null && parts.Count < 5;
             current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message.ReplaceLineEndings(" ")}");
        return string.Join(" <- ", parts);
    }
}
