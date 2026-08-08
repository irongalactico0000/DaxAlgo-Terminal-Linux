# Native Strategy Run UI

## Goal

Replace the Strategy Builder Build screen's synthetic four-draft comparison in the configured app
with an honest retained-run view backed by `IStrategyAgentClient`: Research, VibeQuant/AKQuant,
Point72 CSP, and deterministic comparison evidence.

## Plan

1. Add a small native-run partial to `StrategyAuthoringViewModel` that loads one retained run,
   pages its real session/run events, and exposes exact native results, errors, artifact paths,
   hashes, and report JSON.
2. Add explicit start, refresh, cancellation-request, and hash-checked artifact retrieval actions.
3. Show four native evidence panels on Screen 2 whenever the registered client is present and hide
   the prior four generated-draft surface in that configuration.
4. Add focused view-model and XAML contract tests, then build/test the narrow Avalonia targets.

## Blast radius

- `TradingTerminal.Settings` authoring view model only.
- Avalonia Strategy Authoring XAML and one composition comment only.
- `TradingTerminal.App.Avalonia.Tests` focused tests only.
- No Python, chart capture, confirmation schema, CSP/VibeQuant runtime, generated context, or broker
  changes.

## Build filter

Run the new native-run tests first, then all `TradingTerminal.App.Avalonia.Tests`, and build the
Avalonia app project.

## Tests

- Focused native-run VM/XAML tests: **5 passed, 0 failed**.
- Full `TradingTerminal.App.Avalonia.Tests`: **105 passed, 0 failed**.
- `dotnet build src/linux/Shell/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj
  --no-restore`: **passed, 0 warnings, 0 errors**.
- `git diff --check`: passed.
- Live service recovery: A12 returned `completed` / `partially_proven`, both native lanes passed,
  and 371 retained terminal events were available.
- Manual macOS launch: **blocked before window creation**. Avalonia Native could not start the
  platform `RenderTimer` and returned native error `-6661` from both `dotnet run` and the built host.

## Findings

- Production composition already registers `IStrategyAgentClient`; the authoring view model can
  consume it without adding a second coordinator or runtime.
- Chart selection, frozen-context session creation, research chat, and native confirmation are not
  connected to this view yet. The first UI slice therefore loads an already confirmed run ID and
  labels that limitation directly.
- When the native client is registered, Screen 2 no longer requires the legacy four-draft
  confirmation gate. When the client is absent, the existing legacy gate and surface are preserved.
- Native run and session events are paged to the newest retained event and the rendered collections
  retain at most the latest 200 events per stream. The boundary is displayed in the UI.
- Loading is transactional across both event streams: a failed event read for a newly requested run
  leaves the previously loaded identity, status, and all four evidence panels unchanged.
- After a service restart, the run can outlive its in-memory research session. Only the exact
  `research_session_not_found` response enables fallback to real `lane=research` run events, and the
  missing session transcript is labeled in both the header and Research panel.

## Diff summary

- Added `StrategyAuthoringViewModel.NativeStrategyRun.cs`: retained run status, paged events, exact
  lane/report evidence, exact service error code/detail, start/refresh/cancel-request actions, and
  hash-checked artifact retrieval.
- Injected the already registered `IStrategyAgentClient` into the authoring view model and disposed
  pending UI requests deterministically.
- Replaced the configured Build screen content with Research, VibeQuant/AKQuant, CSP, and Compare
  evidence cards; the old synthetic four-draft surface and legacy header are hidden in this mode.
- Added explicit partial-wiring labels and a run-ID bridge instead of claiming chart/research/
  confirmation creation.
- Added focused VM and XAML contract coverage.

## Verification

The real-results UI slice compiles and its full Avalonia test project is green. Its view model can
open Screen 2 without entering the old synthetic lane workflow, load/start/refresh/request-cancel
for an already confirmed native run, inspect exact retained events/results/errors, and retrieve
exact artifacts through the hash-verifying service endpoint. A real window interaction is not
claimed because the macOS app launch stopped at the Avalonia render-timer failure above.

## Risks/deferred

- Live chart-to-research and user-confirmation wiring remains deferred.
- Run refresh is explicit; this slice does not add background polling or automatic resume.
- Cancellation remains the backend's cooperative request semantics; the UI states that an active
  native callback may still report its terminal result.
- Release packaging/configuration of the Python service remains outside this UI slice.
