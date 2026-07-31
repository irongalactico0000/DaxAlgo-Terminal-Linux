# Reproducible Linux-hosted headless build + test for DaxAlgo Terminal macOS.
# Build from the repo root:
#   docker build -f linux/Dockerfile -t daxalgo-mac-headless .
# The image fails the build if the portable macOS product graph fails to compile or any test fails.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .

# macOS solution — portable core (net9.0) + Avalonia shell.
RUN dotnet build TradingTerminal.Mac.slnx -c Release

# Headless test suite.
RUN dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj -c Release

# macOS RID restore probes.
RUN dotnet restore src/linux/Shell/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj -r osx-arm64 \
 && dotnet restore src/linux/Shell/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj -r osx-x64 \
 && echo "macOS RID restore OK"

# Publish the headless backtest CLI as the image entrypoint.
RUN dotnet publish src/linux/Backtest/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "daxalgo-backtest.dll"]
