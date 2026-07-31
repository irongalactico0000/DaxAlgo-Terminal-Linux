using Xunit;

namespace TradingTerminal.Tests.Backtesting;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires the Windows PowerShell worker harness.";
    }
}
