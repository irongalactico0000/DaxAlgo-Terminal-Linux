using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Infrastructure.Sidecar;

namespace TradingTerminal.Infrastructure.StrategyAgent;

/// <summary>
/// Manages the native-strategy Python service as a dedicated child process. It accepts only the
/// exact strategy-agent health response and never widens the service beyond loopback.
/// </summary>
internal sealed class StrategyAgentHostService : IHostedService, IStrategyAgentHost, IDisposable
{
    private static readonly JsonSerializerOptions HealthJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IOptionsMonitor<StrategyAgentOptions> _options;
    private readonly ILogger<StrategyAgentHostService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JobObjectProcessGuard _guard = new();
    private Process? _process;

    public StrategyAgentHostService(
        IOptionsMonitor<StrategyAgentOptions> options,
        ILogger<StrategyAgentHostService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    private int Port => _options.CurrentValue.Port;
    private string HealthUrl =>
        $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/healthz";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (options.Enabled && options.AutoStart)
            _ = Task.Run(() => EnsureRunningAsync(CancellationToken.None));
        else
            _logger.LogDebug(
                "Native strategy-agent auto-start skipped (Enabled={Enabled}, AutoStart={AutoStart}).",
                options.Enabled,
                options.AutoStart);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        KillProcess();
        return Task.CompletedTask;
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await IsReachableAsync(cancellationToken).ConfigureAwait(false))
            {
                IsRunning = true;
                return true;
            }

            if (!_options.CurrentValue.Enabled)
            {
                IsRunning = false;
                return false;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await IsReachableAsync(cancellationToken).ConfigureAwait(false))
                {
                    IsRunning = true;
                    return true;
                }

                if (_process is { HasExited: false })
                {
                    IsRunning = await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
                    return IsRunning;
                }

                var launch = ResolveLaunch(_options.CurrentValue, Port);
                if (launch is null)
                {
                    _logger.LogWarning(
                        "Native strategy-agent was not launched: no configured or packaged runtime was found. " +
                        "Set StrategyAgent:ExecutablePath or StrategyAgent:PackagePath/PythonPath.");
                    return false;
                }

                var (fileName, args, workDir) = launch.Value;
                _logger.LogInformation(
                    "Starting native strategy-agent on loopback port {Port}: {File} {Args}",
                    Port,
                    fileName,
                    string.Join(' ', args));
                if (!TryStart(fileName, args, workDir, _options.CurrentValue))
                    return false;

                IsRunning = await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
                if (IsRunning)
                    _logger.LogInformation("Native strategy-agent is ready on {HealthUrl}.", HealthUrl);
                else
                    _logger.LogWarning(
                        "Native strategy-agent started but its exact health endpoint did not answer within {Seconds}s.",
                        _options.CurrentValue.StartupTimeoutSeconds);
                return IsRunning;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native strategy-agent launch failed.");
            return false;
        }
    }

    private bool TryStart(
        string fileName,
        IReadOnlyList<string> args,
        string? workDir,
        StrategyAgentOptions options)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workDir ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in args)
                startInfo.ArgumentList.Add(argument);

            ForwardIfConfigured(startInfo, "DAXALGO_STRATEGY_AGENT_STORE", options.StoreRoot);
            ForwardIfConfigured(startInfo, "DAXALGO_QUERY_ENGINE_ROOT", options.QueryEngineRoot);
            ForwardIfConfigured(startInfo, "DAXALGO_QUERY_ENGINE_PYTHON", options.QueryEnginePython);
            ForwardIfConfigured(
                startInfo,
                "DAXALGO_QUERY_ENGINE_ENV_FILE",
                options.QueryEngineEnvironmentFile);
            ForwardIfConfigured(startInfo, "DAXALGO_VIBEQUANT_ROOT", options.VibeQuantRoot);
            ForwardIfConfigured(startInfo, "DAXALGO_VIBEQUANT_PYTHON", options.VibeQuantPython);
            ForwardIfConfigured(startInfo, "DAXALGO_CSP_PYTHON", options.CspPython);
            ForwardIfConfigured(
                startInfo,
                "DAXALGO_STRATEGY_UPSTREAM_LOCK",
                options.UpstreamLockPath);

            var process = Process.Start(startInfo);
            if (process is null)
                return false;

            _process = process;
            _guard.TryAssign(process);
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is { Length: > 0 })
                    _logger.LogDebug("[strategy-agent] {Line}", args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is { Length: > 0 })
                    _logger.LogDebug("[strategy-agent] {Line}", args.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start native strategy-agent process {File}.", fileName);
            return false;
        }
    }

    private async Task<bool> WaitForHealthAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(
            Math.Max(2, _options.CurrentValue.StartupTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                _logger.LogWarning(
                    "Native strategy-agent exited before health was ready (code {ExitCode}).",
                    _process.ExitCode);
                return false;
            }

            if (await IsReachableAsync(cancellationToken).ConfigureAwait(false))
                return true;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(HealthUrl, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            return IsExpectedHealthPayload(payload);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsExpectedHealthPayload(ReadOnlySpan<byte> payload)
    {
        try
        {
            var health = JsonSerializer.Deserialize<HealthResponse>(payload, HealthJsonOptions);
            return health is not null &&
                   string.Equals(health.Status, "ok", StringComparison.Ordinal) &&
                   string.Equals(
                       health.Service,
                       "daxalgo-native-strategy-agent",
                       StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private (string FileName, List<string> Args, string? WorkDir)? ResolveLaunch(
        StrategyAgentOptions options,
        int port)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userAppDir = string.IsNullOrWhiteSpace(localData)
            ? null
            : Path.Combine(localData, "DaxAlgoTerminal");
#if DEBUG
        const bool allowDevelopmentSourceDiscovery = true;
#else
        const bool allowDevelopmentSourceDiscovery = false;
#endif
        return ResolveLaunchForPlatform(
            options,
            port,
            AppContext.BaseDirectory,
            userAppDir,
            OperatingSystem.IsWindows(),
            allowDevelopmentSourceDiscovery);
    }

    internal static (string FileName, List<string> Args, string? WorkDir)? ResolveLaunchForPlatform(
        StrategyAgentOptions options,
        int port,
        string baseDir,
        string? userAppDir,
        bool isWindows,
        bool allowDevelopmentSourceDiscovery = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDir);
        var portArgument = port.ToString(CultureInfo.InvariantCulture);

        if (ResolveConfiguredFile(options.ExecutablePath, baseDir, userAppDir) is { } configured)
            return CreateExecutableLaunch(configured, portArgument, isWindows);

        if (FindPackagedExecutable(baseDir, userAppDir, isWindows) is { } executable)
            return CreateExecutableLaunch(executable, portArgument, isWindows);

        var packageDir = ResolveConfiguredDirectory(options.PackagePath, baseDir, userAppDir) ??
                         FindPackageDirectory(baseDir, userAppDir);
        if (packageDir is null && allowDevelopmentSourceDiscovery)
            packageDir = FindDevelopmentPackageDirectory(baseDir);
        if (packageDir is null)
            return null;

        var python = ResolvePython(options, packageDir, baseDir, userAppDir, isWindows);
        return (
            python,
            new List<string>
            {
                "-m",
                "daxalgo_strategy_agent.cli",
                "serve",
                "--port",
                portArgument,
            },
            packageDir);
    }

    private static (string FileName, List<string> Args, string? WorkDir) CreateExecutableLaunch(
        string executable,
        string portArgument,
        bool isWindows)
    {
        var args = new List<string> { "serve", "--port", portArgument };
        var workDir = Path.GetDirectoryName(executable);
        if (!isWindows && executable.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            args.Insert(0, executable);
            return ("/bin/sh", args, workDir);
        }

        return (executable, args, workDir);
    }

    private static string? FindPackagedExecutable(
        string baseDir,
        string? userAppDir,
        bool isWindows)
    {
        var names = isWindows
            ? new[] { "daxalgo-strategy-agent.exe" }
            : new[]
            {
                "daxalgo-strategy-agent",
                "daxalgo-strategy-agent.sh",
            };
        foreach (var root in RuntimeRoots(baseDir, userAppDir))
        foreach (var name in names)
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? FindPackageDirectory(string baseDir, string? userAppDir)
    {
        foreach (var root in RuntimeRoots(baseDir, userAppDir))
            if (IsPackageDirectory(root))
                return root;
        return null;
    }

    private static IEnumerable<string> RuntimeRoots(string baseDir, string? userAppDir)
    {
        yield return Path.Combine(baseDir, "strategy-agent");
        var resources = Path.GetFullPath(Path.Combine(baseDir, "..", "Resources"));
        yield return Path.Combine(resources, "strategy-agent");
        if (!string.IsNullOrWhiteSpace(userAppDir))
            yield return Path.Combine(userAppDir, "strategy-agent");
    }

    private static string? FindDevelopmentPackageDirectory(string baseDir)
    {
        var marker = FindUpwards(
            baseDir,
            "tools/strategy-agent/daxalgo_strategy_agent/cli.py");
        return marker is null
            ? null
            : Path.GetDirectoryName(Path.GetDirectoryName(marker));
    }

    private static bool IsPackageDirectory(string path) =>
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, "pyproject.toml")) &&
        File.Exists(Path.Combine(path, "daxalgo_strategy_agent", "cli.py"));

    private static string ResolvePython(
        StrategyAgentOptions options,
        string packageDir,
        string baseDir,
        string? userAppDir,
        bool isWindows)
    {
        if (ResolveConfiguredFile(options.PythonPath, baseDir, userAppDir) is { } configured)
            return configured;

        var candidates = isWindows
            ? new[] { Path.Combine(packageDir, ".venv", "Scripts", "python.exe") }
            : new[]
            {
                Path.Combine(packageDir, ".venv", "bin", "python3"),
                Path.Combine(packageDir, ".venv", "bin", "python"),
            };
        return candidates.FirstOrDefault(File.Exists) ?? (isWindows ? "python" : "python3");
    }

    private static string? ResolveConfiguredFile(
        string configuredPath,
        string baseDir,
        string? userAppDir)
    {
        var resolved = ResolveConfiguredPath(configuredPath, baseDir, userAppDir, File.Exists);
        return resolved is null ? null : Path.GetFullPath(resolved);
    }

    private static string? ResolveConfiguredDirectory(
        string configuredPath,
        string baseDir,
        string? userAppDir)
    {
        var resolved = ResolveConfiguredPath(configuredPath, baseDir, userAppDir, IsPackageDirectory);
        return resolved is null ? null : Path.GetFullPath(resolved);
    }

    private static string? ResolveConfiguredPath(
        string configuredPath,
        string baseDir,
        string? userAppDir,
        Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;
        var expanded = ExpandUserPath(
            Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
        if (exists(expanded))
            return expanded;
        if (Path.IsPathRooted(expanded))
            return null;

        var packaged = Path.Combine(baseDir, expanded);
        if (exists(packaged))
            return packaged;
        if (!string.IsNullOrWhiteSpace(userAppDir))
        {
            var userInstalled = Path.Combine(userAppDir, expanded);
            if (exists(userInstalled))
                return userInstalled;
        }

        return null;
    }

    private static string ExpandUserPath(string path)
    {
        if (path != "~" &&
            !path.StartsWith("~/", StringComparison.Ordinal) &&
            !path.StartsWith("~\\", StringComparison.Ordinal))
            return path;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            return path;
        return path.Length == 1 ? profile : Path.Combine(profile, path[2..]);
    }

    private static string? FindUpwards(string startDirectory, string relativePath)
    {
        var directory = new DirectoryInfo(startDirectory);
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, normalized);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private static void ForwardIfConfigured(
        ProcessStartInfo startInfo,
        string variable,
        string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            startInfo.Environment[variable] = value.Trim();
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _logger.LogInformation("Stopped the managed native strategy-agent process.");
            }
        }
        catch
        {
            // Best effort. The Windows Job Object also enforces teardown.
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            IsRunning = false;
        }
    }

    public void Dispose()
    {
        KillProcess();
        _guard.Dispose();
        _gate.Dispose();
    }

    private sealed record HealthResponse(string Status, string Service);
}
