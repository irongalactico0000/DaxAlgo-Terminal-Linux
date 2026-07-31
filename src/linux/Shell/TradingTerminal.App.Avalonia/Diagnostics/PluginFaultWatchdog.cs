using System.Diagnostics;
using System.Runtime.Loader;
using Avalonia.Threading;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI.Diagnostics;

namespace TradingTerminal.App.Avalonia.Diagnostics;

/// <summary>Attributes repeated unhandled faults to their collectible plugin load context.</summary>
internal static class PluginFaultWatchdog
{
    private const string PluginContextPrefix = "Plugin:";

    public static IDisposable Attach(
        Dispatcher dispatcher,
        int strikeLimit,
        Action<string, string> onStrikeOut,
        Action<string, string, string>? log = null)
    {
        var tracker = new PluginFaultTracker(strikeLimit);
        DispatcherUnhandledExceptionEventHandler onDispatcher = (_, args) =>
            Observe(args.Exception, tracker, onStrikeOut, log);
        EventHandler<UnobservedTaskExceptionEventArgs> onTask = (_, args) =>
            Observe(args.Exception, tracker, onStrikeOut, log);
        Action<Exception> onReported = exception =>
            Observe(exception, tracker, onStrikeOut, log);

        dispatcher.UnhandledException += onDispatcher;
        TaskScheduler.UnobservedTaskException += onTask;
        PluginFaultEvents.Reported += onReported;
        return new Detach(() =>
        {
            dispatcher.UnhandledException -= onDispatcher;
            TaskScheduler.UnobservedTaskException -= onTask;
            PluginFaultEvents.Reported -= onReported;
        });
    }

    private static void Observe(
        Exception exception,
        PluginFaultTracker tracker,
        Action<string, string> onStrikeOut,
        Action<string, string, string>? log)
    {
        try
        {
            if (!TryAttribute(exception, out var plugin)) return;
            var (strikes, struckOutNow) = tracker.RecordFault(plugin);
            var summary = $"{exception.GetType().Name}: {exception.Message}";
            log?.Invoke(
                "Plugins",
                "Warning",
                $"Unhandled fault #{strikes} attributed to strategy plugin '{plugin}': {summary}");
            if (struckOutNow)
                onStrikeOut(plugin, $"{strikes} unhandled faults this session; last: {summary}");
        }
        catch
        {
            // A diagnostic observer must never make the original fault worse.
        }
    }

    internal static bool TryAttribute(Exception exception, out string plugin)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IPluginFaultAttribution attributed
                && !string.IsNullOrWhiteSpace(attributed.PluginName))
            {
                plugin = attributed.PluginName;
                return true;
            }

            foreach (var frame in new StackTrace(current).GetFrames())
            {
                var assembly = frame?.GetMethod()?.Module.Assembly;
                if (assembly is null) continue;
                var contextName = AssemblyLoadContext.GetLoadContext(assembly)?.Name;
                if (contextName?.StartsWith(PluginContextPrefix, StringComparison.Ordinal) == true)
                {
                    plugin = contextName[PluginContextPrefix.Length..];
                    return true;
                }
            }

            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    if (TryAttribute(inner, out plugin))
                        return true;
        }

        plugin = string.Empty;
        return false;
    }

    private sealed class Detach(Action detach) : IDisposable
    {
        private Action? _detach = detach;
        public void Dispose() => Interlocked.Exchange(ref _detach, null)?.Invoke();
    }
}
