namespace TradingTerminal.StrategyComposer;

/// <summary>Visual-freeze compatibility seam for destination panels whose VM predates IsPaused.</summary>
public interface IEmbeddedPausable
{
    void SetPaused(bool paused);
}
