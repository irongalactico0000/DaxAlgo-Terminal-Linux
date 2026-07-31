# TradingTerminal.StrategyComposer

The native Avalonia default-view composer for an AI-authored strategy that supplies a descriptor and
live view-model but no custom view. It is a physical port of the shared Windows composer: the Core
contract, DI seam, descriptor routing, instrument/pause wiring, and ownership rules are retained; only
WPF controls and lifecycle hooks are replaced.

## Composition

The descriptor's `StrategyDataRequirement` selects panels in a fixed order:

| Flag | macOS panel | Embedded behaviour |
|---|---|---|
| `Bars` | native `ChartsPanel` | `ChartsPanelFeatures.Embedded`, 1-minute bars |
| `Depth` | embedded native order-book surface | strategy-owned instrument, toolbar and ML omitted |
| `TradeTape` | embedded native footprint surface | strategy-owned instrument, regression and ML omitted |
| L1 only | live quote card | no auxiliary panel |

Every composition also retains the instrument setup, stream/arm controls, quote/status header, named
view presets, CSV/snapshot actions, contextual help, signal feed, and armed/paused footer.

Panel view-models receive the strategy's configured instrument and pause state. They are disposed when
the control leaves the visual tree or its host window closes; the authored strategy view-model remains
owned by the shell.

No concrete strategy implementation belongs in this project.

## Integration

1. Add `TradingTerminal.StrategyComposer.csproj` to `TradingTerminal.Mac.slnx`.
2. Reference it from the Avalonia app project.
3. Call `services.AddStrategyViewComposer()` after registering Charts, Order Book, and Volume
   Footprint dependencies.

The authored installer and SDK bootstrap already resolve
`TradingTerminal.Core.Strategies.Authoring.IAuthoredStrategyViewComposer`; no strategy-specific
registration is required.
