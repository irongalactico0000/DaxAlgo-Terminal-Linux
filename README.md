# DaxAlgo Terminal Linux

Private Linux and Raspberry Pi edition of DaxAlgo Terminal, using Avalonia on .NET 9.

This repository was extracted from `dhruuvsharma/DaxAlgo-Terminal` on 2026-07-22. It is now an
independent codebase: Windows and Professional changes do not imply work here, and changes here do
not imply work in either Windows repository.

## Build and test

```bash
dotnet build TradingTerminal.Linux.slnx
dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj
dotnet run --project src/linux/Shell/TradingTerminal.App.Avalonia
```

Use `linux/build-and-test.sh` for the full build, test, CLI smoke, and ARM64 restore sequence. The
Docker build is available through `linux/Dockerfile`.

## Layout

- `src/linux/` — product source, including the Avalonia shell and in-tree strategies.
- `tests/linux/` — Linux headless test suite.
- `tools/python-ml/` — optional Python AI sidecar.
- `tools/cpp-backtester/` — optional native fast-backtest helpers.
- `.claude/context/linux/` — generated source and dependency maps.

Configuration templates are checked in as `appsettings*.json`; local secrets belong only in ignored
`appsettings.local.json` or environment variables. Optional broker SDK binaries belong in `lib/` and
remain untracked.

## Codex context

Start with `AGENTS.md`, then load `.claude/context/linux/index.md`, `symbols.md`, and `deps.json`.
This repository has one product tree and no Windows cross-tree obligation.

## Provenance

The extracted source corresponds to public repository revision
`3822dc283e9c1305ac4dbcdd2b37c3a73f954efb`; the Linux-owned paths were clean at extraction time.
The original public Git history remains the archival history before the split.
