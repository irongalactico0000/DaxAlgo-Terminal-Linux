# Login, Vibe Quant, and main-shell UI parity
Status: complete

## Goal
Compare the current Windows Professional and Mac/Avalonia UI element by element, then correct the Mac Account Login, broker Login, Vibe Quant, and main application shell. Charts and Tools are explicitly out of scope.

## Plan
1. Capture matched baseline states and audit every visible element.
2. Apply the smallest layout/style corrections to the requested Mac surfaces.
3. Re-capture all four surfaces and verify focused/full builds and tests.

## Blast radius
- `src/linux/Shell/TradingTerminal.Accounts/`
- `src/linux/Shell/TradingTerminal.Login/`
- `src/linux/Shell/TradingTerminal.App.Avalonia/{Settings,Shell,Themes}/`
- Focused tests and `tmp/ui-parity/` screenshot evidence.
- Windows repositories are read-only reference inputs explicitly authorized by the request.

## Build filter
- Focused Accounts/Login/App projects and `TradingTerminal.App.Avalonia.Tests`, then `TradingTerminal.Mac.slnx`.

## Tests
- XAML/XML and visual-contract checks.
- Matched screenshot inspection for both login gates, Vibe Quant, and main shell.
- Named Mac solution build and relevant test projects.

## Findings
- The Account gate already shared the Windows structure; its narrow footer clipped the security note beside the actions.
- Broker Login diverged in window geometry, hero copy, neutral row treatment, status indicators, broker marks, form density, services header, and footer alignment.
- The main shell was structurally close; menu/chip geometry and the authoring launch handoff needed alignment.
- Vibe Quant used a larger four-tab layout with an oversized rail/workbench, wrapped composer controls, no simulated-data banner, and simplified transcript cards.

## Diff summary
- Corrected Account footer wrapping and failure-state border treatment.
- Rebuilt Login geometry and state styling around the Windows 960x700 contract; added shared broker marks, neutral accordion rows, compact keyless forms, full-width services toggle, and Windows-aligned action spacing.
- Matched Vibe Quant to the 1100x760 three-region layout, restored the simulated-data banner, compact composer flyouts, three-tab workbench with inline diagnostics, and richer transcript plan/file content.
- Aligned main-shell menu and status-chip geometry and propagated simulated state into Vibe Quant.
- Added focused XAML contract coverage and refreshed generated macOS context from the current workspace.

## Verification
- XML well-formed: 9/9 edited XAML surfaces.
- `TradingTerminal.App.Avalonia`: build passed, 0 warnings / 0 errors.
- `TradingTerminal.App.Avalonia.Tests`: 9/9 passed.
- `TradingTerminal.Mac.slnx`: build passed, 0 warnings / 0 errors.
- Matched Account, broker Login, main-shell, and Vibe Quant Windows/Mac captures visually inspected under `tmp/ui-parity/2026-07-29/after/`.
- Context regenerated (55 projects / 1034 files / 144052 LOC); `check` and `deep-check` passed.
- Targeted `git diff --check` passed; only repository line-ending notices were emitted.

## Risks / deferred
- Native macOS Retina/font/title-bar validation remains a separate hardware check.
- Charts and Tools are explicitly deferred.
- Vibe Quant syntax colouring/line numbers and the Windows-only Expert server surface remain deferred; adding either requires a real Avalonia editor/service rather than a visual-only control.
- The no-provider Vibe state is structurally covered but was not included in the restored-conversation screenshot set.
