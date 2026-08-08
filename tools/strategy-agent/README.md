# DaxAlgo native strategy agent

Python 3.12 service that coordinates the pinned local FinanceManus QueryEngine, genuine
transcend-0/VibeQuant -> AKQuant execution, and genuine Point72 CSP execution.

It does not provide a strategy DSL, substitute validator, fill simulator, broker, or live-order
path. VibeQuant and CSP own their native interpretation and execution errors.

## Required configuration

Set paths to existing pinned runtimes. Keep provider credentials in the configured environment file;
never place credential values in arguments, logs, manifests, or retained artifacts.

```bash
export DAXALGO_QUERY_ENGINE_ROOT=/path/to/financemanus
export DAXALGO_QUERY_ENGINE_PYTHON=/path/to/financemanus/.venv/bin/python
export DAXALGO_QUERY_ENGINE_ENV_FILE=/path/to/provider.env
export DAXALGO_VIBEQUANT_ROOT=/path/to/VibeQuant
export DAXALGO_VIBEQUANT_PYTHON=/path/to/VibeQuant/.venv/bin/python
export DAXALGO_CSP_PYTHON=/path/to/csp-venv/bin/python
```

`upstreams.lock.json` is the authority for the accepted source revisions and package versions.

## Commands

Run preflight with the configured FinanceManus Python interpreter:

```bash
python -m daxalgo_strategy_agent.cli preflight
```

Start the loopback service used by the .NET client:

```bash
python -m daxalgo_strategy_agent.cli serve --port 8766
```

Run the provider-backed structured FDAX proof into two paths that do not already exist:

```bash
python -m daxalgo_strategy_agent.cli prove-fdax-fixture \
  --input-workspace /new/path/fdax-input \
  --store-root /new/path/fdax-retained
```

The proof confirmation is scripted. A successful proof demonstrates the backend, genuine native
paths, retention, and comparison; it does not demonstrate human review or chart capture in the app.

## Verification

```bash
python -m pytest -q
uvx --from ruff==0.12.7 ruff check daxalgo_strategy_agent tests
```

Optional integration tests run when the QueryEngine, VibeQuant source/interpreter, and CSP
interpreter environment variables are configured. The genuine VibeQuant worker tests additionally
recognize `DAXALGO_VIBEQUANT_SOURCE_ROOT` as their explicit source-root fixture variable.

## Evidence interpretation

- `completed` means both native lanes reached terminal results and comparison did not fail.
- `partially_proven` is expected when CSP exact timestamped intents pass but the public VibeQuant
  result exposes only aggregate trades/metrics.
- `failed` preserves the exact lane and stage; sibling evidence remains inspectable.
- CSP success is graph execution, never a trading backtest.
