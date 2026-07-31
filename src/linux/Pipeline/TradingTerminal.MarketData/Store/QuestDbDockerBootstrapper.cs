using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Npgsql;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Starts a loopback-only QuestDB container without depending on a repository checkout or compose
/// file. Docker Desktop may be started headlessly on macOS when its daemon is not yet available.
/// </summary>
internal static class QuestDbDockerBootstrapper
{
    public static TimeSpan StartupTimeout(MarketDataStoreOptions options) =>
        TimeSpan.FromSeconds(Math.Max(5, options.QuestDbStartupTimeoutSeconds));

    public static bool DockerCliPresent() =>
        TryRunDocker(new[] { "--version" }, TimeSpan.FromSeconds(15), out _, log: null);

    public static bool DockerDaemonReady() =>
        TryRunDocker(new[] { "info" }, TimeSpan.FromSeconds(20), out _, log: null);

    public static bool TryStartDockerEngineCli(ILogger log)
    {
        if (TryRunDocker(
                new[] { "desktop", "start" },
                TimeSpan.FromSeconds(180),
                out var output,
                log: null))
        {
            log.LogInformation("Started the Docker engine through the Docker Desktop CLI.");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(output))
            log.LogDebug("Docker Desktop CLI start was unavailable: {Output}", output.Trim());
        return false;
    }

    public static bool TryLaunchDockerDesktop(ILogger log)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/open")) return false;

        try
        {
            var startInfo = new ProcessStartInfo("/usr/bin/open")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add("-j");
            startInfo.ArgumentList.Add("-a");
            startInfo.ArgumentList.Add("Docker");
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            process.WaitForExit(15000);
            if (!process.HasExited || process.ExitCode != 0) return false;

            log.LogInformation("Launched Docker Desktop in the background.");
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not launch Docker Desktop through macOS Launch Services.");
            return false;
        }
    }

    public static bool IsReachable(string connectionString)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasSafeEndpoints(MarketDataStoreOptions options, out string? reason)
    {
        NpgsqlConnectionStringBuilder pg;
        try
        {
            pg = new NpgsqlConnectionStringBuilder(options.QuestDbPgConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            reason = "QuestDB PG-wire connection string is invalid.";
            return false;
        }

        if (!TryParseIlpEndpoint(options.QuestDbIlpConfig, out var scheme, out var ilpEndpoint))
        {
            reason = "QuestDB ILP-over-HTTP configuration is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(pg.Host) || !IsLoopbackHost(pg.Host))
        {
            reason = "QuestDB PG-wire must use a loopback host.";
            return false;
        }

        if ((!string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
            || !IsLoopbackHost(ilpEndpoint.Host))
        {
            reason = "QuestDB ILP-over-HTTP must use a loopback host.";
            return false;
        }

        if (options.QuestDbLaunchMode == QuestDbLaunchMode.Native
            && (pg.Port != 8812
                || !string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
                || ilpEndpoint.Port != 9000))
        {
            reason = "App-managed QuestDB requires PG-wire port 8812 and HTTP ILP port 9000.";
            return false;
        }

        reason = null;
        return true;
    }

    public static bool TryStartContainer(MarketDataStoreOptions options, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(options.QuestDbContainerName)
            || string.IsNullOrWhiteSpace(options.QuestDbContainerImage)
            || string.IsNullOrWhiteSpace(options.QuestDbVolumeName))
        {
            log.LogWarning("QuestDB container name, image, and volume must all be configured.");
            return false;
        }

        var inspectArguments = new[] { "container", "inspect", options.QuestDbContainerName };
        if (TryRunDocker(inspectArguments, TimeSpan.FromSeconds(30), out _, log: null))
        {
            if (TryRunDocker(
                    new[] { "start", options.QuestDbContainerName },
                    TimeSpan.FromSeconds(60),
                    out var startOutput,
                    log: null))
                return true;

            log.LogWarning(
                "Docker could not start the existing QuestDB container {Container}: {Error}",
                options.QuestDbContainerName,
                startOutput.Trim());
            return false;
        }

        var runArguments = new[]
        {
            "run", "-d", "--pull=missing",
            "--name", options.QuestDbContainerName,
            "--restart", "unless-stopped",
            "-p", "127.0.0.1:9000:9000",
            "-p", "127.0.0.1:8812:8812",
            "-v", $"{options.QuestDbVolumeName}:/var/lib/questdb",
            "-e", "QDB_TELEMETRY_ENABLED=false",
            options.QuestDbContainerImage,
        };
        if (TryRunDocker(runArguments, TimeSpan.FromMinutes(5), out var runOutput, log: null))
            return true;

        log.LogWarning("Docker could not create the QuestDB container: {Error}", runOutput.Trim());
        return false;
    }

    public static bool WaitForDaemon(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (DockerDaemonReady()) return true;
            Thread.Sleep(2000);
        }
        return false;
    }

    public static bool WaitUntilReachable(
        string connectionString,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (IsReachable(connectionString)) return true;
            Thread.Sleep(1000);
        }
        return IsReachable(connectionString);
    }

    private static bool TryParseIlpEndpoint(string config, out string scheme, out Uri endpoint)
    {
        scheme = string.Empty;
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(config)) return false;

        var schemeSeparator = config.IndexOf("::", StringComparison.Ordinal);
        if (schemeSeparator <= 0) return false;
        scheme = config[..schemeSeparator].Trim();

        var address = config[(schemeSeparator + 2)..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(property => property.Split('=', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(parts => parts.Length == 2
                && string.Equals(parts[0], "addr", StringComparison.OrdinalIgnoreCase))?[1];

        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate($"{scheme}://{address}", UriKind.Absolute, out var parsed)
            || parsed.Port is <= 0 or > 65535)
            return false;

        endpoint = parsed;
        return true;
    }

    private static bool IsLoopbackHost(string host)
    {
        var normalized = host.Trim().TrimStart('[').TrimEnd(']');
        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(normalized, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool TryRunDocker(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        out string output,
        ILogger? log)
    {
        output = string.Empty;
        foreach (var executable in DockerCliCandidates())
        {
            try
            {
                var startInfo = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

                using var process = Process.Start(startInfo);
                if (process is null) continue;
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    output = "Docker command timed out.";
                    return false;
                }

                var stdout = standardOutput.GetAwaiter().GetResult();
                var stderr = standardError.GetAwaiter().GetResult();
                output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
                if (process.ExitCode == 0) return true;

                log?.LogDebug(
                    "Docker command exited {ExitCode}: {Error}",
                    process.ExitCode,
                    stderr.Trim());
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log?.LogDebug(ex, "Docker CLI candidate {Path} could not be started.", executable);
            }
        }
        return false;
    }

    private static IEnumerable<string> DockerCliCandidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Docker.app/Contents/Resources/bin/docker";
            yield return "/opt/homebrew/bin/docker";
            yield return "/usr/local/bin/docker";
        }
        yield return "docker";
    }
}
