using Serilog.Core;
using Serilog.Events;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.App.Avalonia.Composition;

/// <summary>Forwards Serilog events into the app-wide Activity Log.</summary>
internal sealed class ObservableCollectionLogSink : ILogEventSink
{
    private readonly InMemoryLogSink _activityLog;

    public ObservableCollectionLogSink(InMemoryLogSink activityLog) =>
        _activityLog = activityLog;

    public void Emit(LogEvent logEvent) =>
        _activityLog.Append(new LogEntry(
            logEvent.Timestamp.UtcDateTime,
            "System",
            logEvent.Level.ToString(),
            logEvent.RenderMessage()));
}
