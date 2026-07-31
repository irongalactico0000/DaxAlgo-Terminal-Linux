using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest.Fast;

namespace TradingTerminal.Infrastructure.Backtest.Fast;

/// <summary>
/// Registers <see cref="IFastBacktestRunner"/> with a real
/// <see cref="ProcessFastBacktestRunner"/> when the platform helper is installed with the app or
/// in its user-data directory, falling back to <see cref="NullFastBacktestRunner"/> otherwise.
/// Windows retains the existing <c>tick_backtester.exe</c> lookup; Unix hosts resolve the native
/// <c>tick_backtester</c> binary (or an executable launcher script) without walking a source tree.
/// </summary>
public static class FastBacktestServiceCollectionExtensions
{
    public static IServiceCollection AddFastBacktestRunner(this IServiceCollection services)
    {
        services.AddSingleton<IFastBacktestRunner>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ProcessFastBacktestRunner>>();
            var exePath = ResolveBinary();
            if (exePath is null)
            {
                var nullLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<NullFastBacktestRunner>();
                nullLogger.LogInformation(
                    "Fast backtester helper not found in packaged or user application locations " +
                    "for {BaseDir}; Fast toggle disabled.", AppContext.BaseDirectory);
                return new NullFastBacktestRunner(
                    $"Fast backtester helper not found for {AppContext.BaseDirectory}.");
            }

            logger.LogInformation("Fast backtester resolved: {Path}", exePath);
            return new ProcessFastBacktestRunner(logger, exePath);
        });
        return services;
    }

    private static string? ResolveBinary()
    {
        var baseDir = AppContext.BaseDirectory;
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userAppDir = string.IsNullOrWhiteSpace(localData)
            ? null
            : Path.Combine(localData, "DaxAlgoTerminal");
        return ResolveBinary(baseDir, userAppDir, OperatingSystem.IsWindows());
    }

    internal static string? ResolveBinary(string baseDir, string? userAppDir, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDir);

        var candidates = new List<string>();
        if (isWindows)
        {
            // Preserve the established Windows lookup order.
            candidates.Add(Path.Combine(baseDir, "tick_backtester.exe"));
            candidates.Add(Path.Combine(baseDir, "tools", "cpp-backtester", "bin", "tick_backtester.exe"));
            AddUserCandidates(candidates, userAppDir, "tick_backtester.exe");
        }
        else
        {
            AddUnixCandidates(candidates, baseDir);

            // In an app bundle AppContext.BaseDirectory is Contents/MacOS; helpers may be sealed
            // under Contents/Resources instead of beside the managed host.
            var resourcesDir = Path.GetFullPath(Path.Combine(baseDir, "..", "Resources"));
            AddUnixCandidates(candidates, resourcesDir);

            if (!string.IsNullOrWhiteSpace(userAppDir))
                AddUnixCandidates(candidates, userAppDir);
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void AddUserCandidates(ICollection<string> candidates, string? root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        candidates.Add(Path.Combine(root, fileName));
        candidates.Add(Path.Combine(root, "helpers", fileName));
        candidates.Add(Path.Combine(root, "bin", fileName));
    }

    private static void AddUnixCandidates(ICollection<string> candidates, string root)
    {
        candidates.Add(Path.Combine(root, "tick_backtester"));
        candidates.Add(Path.Combine(root, "tick_backtester.sh"));
        candidates.Add(Path.Combine(root, "helpers", "tick_backtester"));
        candidates.Add(Path.Combine(root, "helpers", "tick_backtester.sh"));
        candidates.Add(Path.Combine(root, "bin", "tick_backtester"));
        candidates.Add(Path.Combine(root, "bin", "tick_backtester.sh"));
    }
}
