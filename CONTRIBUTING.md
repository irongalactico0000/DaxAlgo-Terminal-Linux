# Contributing

Read `AGENTS.md` and `.claude/context/PROTOCOL.md` before making a change. Keep the scope macOS-only,
preserve unrelated work, and verify the smallest affected project before the full solution.

```bash
dotnet build TradingTerminal.Mac.slnx
dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj
```

Never commit credentials or optional broker SDK binaries. Use feature branches and keep commits
focused on one coherent change.
