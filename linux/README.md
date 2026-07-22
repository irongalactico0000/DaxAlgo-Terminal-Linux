# Linux build and deployment

This directory contains the native Linux verification and container entry points for the standalone
Avalonia repository.

## Full local check

```bash
./linux/build-and-test.sh
```

The script builds `TradingTerminal.Linux.slnx`, runs the headless suite, exercises the backtest CLI,
and probes `linux-arm64` restore for Raspberry Pi.

## Docker

From the repository root:

```bash
docker build -f linux/Dockerfile -t daxalgo-terminal-linux .
```

The final image publishes the headless backtest CLI. Run the Avalonia desktop shell directly on a
Linux host with X11 or Wayland:

```bash
dotnet run --project src/linux/Shell/TradingTerminal.App.Avalonia
```

Optional broker SDK binaries are not included. When absent, their implementations remain compiled
out and the configured fallback clients are used.
