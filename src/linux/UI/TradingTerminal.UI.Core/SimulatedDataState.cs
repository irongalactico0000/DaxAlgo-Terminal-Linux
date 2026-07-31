namespace TradingTerminal.UI;

/// <summary>
/// App-wide flag indicating whether a synthetic Simulated-broker feed is connected. The shell owns
/// the truth and pushes changes so every UI surface can disclose that its data is not live.
/// </summary>
public static class SimulatedDataState
{
    private static bool _isActive;

    public static bool IsActive => _isActive;

    public static event EventHandler? Changed;

    public static void Set(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
