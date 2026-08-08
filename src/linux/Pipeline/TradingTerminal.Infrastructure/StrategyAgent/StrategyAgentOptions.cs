namespace TradingTerminal.Infrastructure.StrategyAgent;

/// <summary>
/// Configures the dedicated native-strategy agent process and its loopback HTTP client. This is
/// intentionally separate from the daxalgo-ml analyst sidecar and defaults to port 8766.
/// </summary>
public sealed class StrategyAgentOptions
{
    public const string SectionName = "StrategyAgent";
    public const int DefaultPort = 8766;

    /// <summary>Enables process launch and client use. A reachable externally managed service is
    /// still accepted when this switch is off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Starts the service with the desktop host when <see cref="Enabled"/> is true.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Dedicated loopback port. The service always binds to 127.0.0.1.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>Optional packaged strategy-agent executable or launcher.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>Optional directory containing pyproject.toml and daxalgo_strategy_agent.</summary>
    public string PackagePath { get; set; } = "";

    /// <summary>Optional Python 3.12 interpreter containing the package's pinned dependencies.</summary>
    public string PythonPath { get; set; } = "";

    /// <summary>How long the host waits for the exact strategy-agent health response.</summary>
    public int StartupTimeoutSeconds { get; set; } = 60;

    /// <summary>Outer HTTP request timeout. This must exceed the Python research-stage timeout;
    /// native runs continue asynchronously after start.</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>Optional retained-run directory forwarded to the Python service.</summary>
    public string StoreRoot { get; set; } = "";

    /// <summary>Optional pinned FinanceManus QueryEngine source directory.</summary>
    public string QueryEngineRoot { get; set; } = "";

    /// <summary>Optional FinanceManus Python interpreter.</summary>
    public string QueryEnginePython { get; set; } = "";

    /// <summary>Optional provider environment-file path read by the QueryEngine runtime.</summary>
    public string QueryEngineEnvironmentFile { get; set; } = "";

    /// <summary>Optional pinned VibeQuant source directory.</summary>
    public string VibeQuantRoot { get; set; } = "";

    /// <summary>Optional VibeQuant/AKQuant Python interpreter.</summary>
    public string VibeQuantPython { get; set; } = "";

    /// <summary>Optional Point72 CSP Python interpreter.</summary>
    public string CspPython { get; set; } = "";

    /// <summary>Optional explicit upstream lock path.</summary>
    public string UpstreamLockPath { get; set; } = "";
}
