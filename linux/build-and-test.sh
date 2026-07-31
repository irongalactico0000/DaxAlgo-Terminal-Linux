#!/usr/bin/env bash
# Linux-hosted headless build + test + macOS RID restore probe for DaxAlgo Terminal.
# Run from anywhere; the script resolves the standalone repository root.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "### dotnet $(dotnet --version) on $(uname -m) / $(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-$(uname -s)}")"

echo "### BUILD net9.0 — macOS solution (portable core + Avalonia shell)"
dotnet build TradingTerminal.Mac.slnx -clp:NoSummary -v q

echo "### TEST net9.0 — headless suite"
dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj --nologo -v q | grep -E "Passed!|Failed!|error" || true

echo "### CLI smoke — deterministic data synthesis (strategies are external plug-ins)"
CLI=src/linux/Backtest/TradingTerminal.Backtest.Cli/bin/Debug/net9.0/daxalgo-backtest.dll
dotnet "$CLI" synth --output /tmp/ticks.parquet --ticks 3000 --seed 7
test -s /tmp/ticks.parquet

echo "### RESTORE probes — osx-arm64 and osx-x64"
dotnet restore src/linux/Shell/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj -r osx-arm64 -v q
dotnet restore src/linux/Shell/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj -r osx-x64 -v q

echo "### ALL HEADLESS MACOS CHECKS DONE"
