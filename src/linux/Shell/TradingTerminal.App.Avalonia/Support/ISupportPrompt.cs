using Avalonia.Controls;

namespace TradingTerminal.App.Support;

/// <summary>
/// Shows the support window and centralises its once-per-launch policy and single-instance
/// lifetime for both startup and Help-menu callers.
/// </summary>
public interface ISupportPrompt
{
    /// <summary>
    /// Called once after the main window appears. Shows the window after a short randomised delay
    /// when the launch gate accepts, provided the owner is still open.
    /// </summary>
    void MaybeShowOnLaunch(Window owner);

    /// <summary>Unconditionally shows or re-activates the support window.</summary>
    void Show(Window owner);
}
