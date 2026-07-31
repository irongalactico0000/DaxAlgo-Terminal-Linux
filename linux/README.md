# Headless portability validation

This inherited directory contains Linux-hosted verification for the independent macOS/Avalonia
repository. It is a CI utility, not a second product edition.

## Full local check

```bash
./linux/build-and-test.sh
```

The script builds `TradingTerminal.Mac.slnx`, runs the headless suite, checks strategy-independent
CLI data synthesis, and probes both macOS runtime identifiers.

## Docker

From the repository root:

```bash
docker build -f linux/Dockerfile -t daxalgo-mac-headless .
```

The final image contains the headless backtest CLI for portability checks. It does not represent the
macOS application bundle and contains no concrete strategy implementation. Release `.app` packaging
must run on macOS through `tools/macos/package.sh`.

Optional broker SDK binaries are not included in the image. Interactive Brokers is required by the
macOS release packager unless `IB_API_MODE` explicitly selects another supported mode.
