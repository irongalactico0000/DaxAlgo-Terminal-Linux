from __future__ import annotations

import hashlib
import json
import os
import sys
from datetime import UTC, datetime
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest

import daxalgo_strategy_agent.composition as composition_module
from daxalgo_strategy_agent.composition import (
    NATIVE_WORKER_LIMITS,
    _coordinator_event_sink,
    build_application,
    preflight,
)
from daxalgo_strategy_agent.contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
)
from daxalgo_strategy_agent.queryengine_runtime import WorkerProfile
from daxalgo_strategy_agent.run_store import NativeRunStore
from daxalgo_strategy_agent.settings import (
    StrategyAgentConfigurationError,
    StrategyAgentSettings,
    UpstreamPins,
)


QUERY_REVISION = "f25fab79e611fd904280cabc97d9d2393a0922dc"
VIBE_REVISION = "1f5442d88ec97b6075ac73a3c4d0b42d1c00a640"


class _FakeConfig:
    def __init__(self, **kwargs: Any) -> None:
        self.__dict__.update(kwargs)


def _bindings() -> Any:
    return SimpleNamespace(
        Config=_FakeConfig,
        source=SimpleNamespace(revision=QUERY_REVISION, python_version="3.12.12"),
    )


def _settings(tmp_path: Path, env_text: str) -> StrategyAgentSettings:
    env_file = tmp_path / "query.env"
    env_file.write_text(env_text, encoding="utf-8")
    return StrategyAgentSettings(
        store_root=tmp_path / "store",
        query_engine_root=tmp_path / "financemanus",
        query_engine_python=Path(os.path.abspath(sys.executable)),
        query_engine_env_file=env_file,
        vibequant_root=tmp_path / "vibequant",
        vibequant_python=tmp_path / "vibe-python",
        csp_python=tmp_path / "csp-python",
        pins=UpstreamPins(
            query_engine_revision=QUERY_REVISION,
            vibequant_revision=VIBE_REVISION,
            akquant_version="0.3.36",
            csp_version="0.18.0",
        ),
    )


def _patch_source_gates(monkeypatch: pytest.MonkeyPatch) -> None:
    for name in (
        "DAXALGO_QUERY_ENGINE_PYTHON",
        "DAXALGO_VIBEQUANT_PYTHON",
        "DAXALGO_CSP_PYTHON",
    ):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setattr(StrategyAgentSettings, "validate", lambda _self: None)
    monkeypatch.setattr(
        composition_module, "load_financemanus", lambda *_args, **_kwargs: _bindings()
    )


def test_preflight_loads_openrouter_configuration_without_exposing_secret(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _patch_source_gates(monkeypatch)
    monkeypatch.delenv("OPENROUTER_API_KEY", raising=False)
    monkeypatch.delenv("EASTMANUS_FALLBACK_MODEL", raising=False)
    secret = "never-print-this-provider-secret"
    settings = _settings(
        tmp_path,
        f"EASTMANUS_MODEL=openrouter/openai/gpt-4.1\nOPENROUTER_API_KEY={secret}\n",
    )

    report = preflight(settings)

    serialized = json.dumps(report.as_dict(), sort_keys=True)
    assert report.providers == ("openrouter",)
    assert report.credential_environment_names == ("OPENROUTER_API_KEY",)
    assert report.fallback_model == report.model
    assert secret not in serialized
    assert os.environ["OPENROUTER_API_KEY"] == secret


def test_preflight_checks_every_explicit_model_provider_without_leaking_values(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _patch_source_gates(monkeypatch)
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    settings = _settings(
        tmp_path,
        "EASTMANUS_MODEL=openrouter/openai/gpt-4.1\n"
        "EASTMANUS_FALLBACK_MODEL=anthropic/claude-sonnet-4-6\n"
        "OPENROUTER_API_KEY=present-but-not-reported\n",
    )

    with pytest.raises(StrategyAgentConfigurationError) as missing:
        preflight(settings)

    assert missing.value.stage == "query_engine_provider_credentials"
    assert (
        str(missing.value)
        == "ANTHROPIC_API_KEY is required for configured anthropic model access"
    )
    assert "present-but-not-reported" not in str(missing.value)


def test_preflight_rejects_process_outside_configured_queryengine_environment(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _patch_source_gates(monkeypatch)
    settings = _settings(
        tmp_path,
        "EASTMANUS_MODEL=openai/gpt-4.1\nOPENAI_API_KEY=secret\n",
    )
    settings = StrategyAgentSettings(
        **{
            **settings.__dict__,
            "query_engine_python": tmp_path / "another-venv" / "bin" / "python",
        }
    )

    with pytest.raises(StrategyAgentConfigurationError) as mismatch:
        preflight(settings)

    assert mismatch.value.stage == "query_engine_python"
    assert "must run inside the configured QueryEngine Python environment" in str(
        mismatch.value
    )


def test_build_application_uses_bounded_factories_and_real_worker_wrappers(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _patch_source_gates(monkeypatch)
    settings = _settings(
        tmp_path,
        "EASTMANUS_MODEL=openrouter/openai/gpt-4.1\n"
        "OPENROUTER_API_KEY=provider-secret\n",
    )
    calls: list[tuple[str, Any, Path, dict[str, Any]]] = []

    def fake_vibe(manifest: Any, workspace: Path, **kwargs: Any) -> str:
        calls.append(("vibequant", manifest, workspace, kwargs))
        return "vibe-result"

    def fake_csp(manifest: Any, workspace: Path, **kwargs: Any) -> str:
        calls.append(("csp", manifest, workspace, kwargs))
        return "csp-result"

    monkeypatch.setattr(composition_module, "run_vibequant_worker", fake_vibe)
    monkeypatch.setattr(composition_module, "run_csp_worker", fake_csp)

    application = build_application(settings)

    assert application.app.title == "DaxAlgo Native Strategy Agent"
    assert application.store.root == settings.store_root.resolve()
    assert application.research_coordinator.session_count == 0
    vibe_factory = application.native_runners._config_factories[WorkerProfile.VIBEQUANT]
    csp_factory = application.native_runners._config_factories[WorkerProfile.CSP]
    for config in (vibe_factory(), csp_factory()):
        assert config.model == "openrouter/openai/gpt-4.1"
        assert config.max_tokens == NATIVE_WORKER_LIMITS.max_tokens
        assert config.max_budget_tokens == NATIVE_WORKER_LIMITS.max_budget_tokens
        assert config.max_turns_per_query == NATIVE_WORKER_LIMITS.max_turns_per_query
        assert config.permission_mode == "strict"
        assert config.always_allow_read_only is False
        assert config.thinking_enabled is False

    workspace = tmp_path / "native-workspace"
    workspace.mkdir()
    manifest = object()
    vibe_runner = application.native_runners._native_runners[WorkerProfile.VIBEQUANT]
    csp_runner = application.native_runners._native_runners[WorkerProfile.CSP]
    assert (
        vibe_runner(
            manifest,
            workspace,
            task_spec_relative_path="agent-input/task.json",
        )
        == "vibe-result"
    )
    assert (
        csp_runner(
            manifest,
            workspace,
            source_relative_path="agent-input/strategy.py",
        )
        == "csp-result"
    )
    assert calls == [
        (
            "vibequant",
            manifest,
            workspace,
            {
                "python_executable": settings.vibequant_python,
                "vibequant_source_root": settings.vibequant_root,
                "task_spec_relative_path": "agent-input/task.json",
            },
        ),
        (
            "csp",
            manifest,
            workspace,
            {
                "python_executable": settings.csp_python,
                "source_relative_path": "agent-input/strategy.py",
            },
        ),
    ]


def test_coordinator_sink_retains_exact_json_event_and_hash(
    tmp_path: Path,
) -> None:
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    data = inputs / "primary.csv"
    data.write_text("timestamp,close\n2026-08-08T00:00:00Z,100\n", encoding="utf-8")
    manifest = FrozenRunManifest(
        run_id="event-run",
        confirmed_intent_sha256="a" * 64,
        selected_start_utc=datetime(2026, 8, 8, 0, 0, tzinfo=UTC),
        selected_end_utc=datetime(2026, 8, 8, 0, 5, tzinfo=UTC),
        as_of_utc=datetime(2026, 8, 8, 0, 10, tzinfo=UTC),
        timezone_name="UTC",
        data_files=(
            FrozenDataFile(
                role="primary",
                instrument="FDAX",
                venue="EUREX",
                source="fixture",
                timeframe="5m",
                relative_path=data.name,
                sha256=hashlib.sha256(data.read_bytes()).hexdigest(),
            ),
        ),
        components=(
            ComponentPin(
                component="query_engine",
                version="source",
                source_revision=QUERY_REVISION,
            ),
            ComponentPin(
                component="vibequant",
                version="0.1.0",
                source_revision=VIBE_REVISION,
            ),
            ComponentPin(component="akquant", version="0.3.36"),
            ComponentPin(component="csp", version="0.18.0"),
        ),
    )
    store = NativeRunStore(tmp_path / "store")
    store.create_run(manifest, inputs)
    event = SimpleNamespace(
        type="tool_result",
        data={"tool": "submit_csp_source", "nested": {"passed": True}},
    )
    coordinator_hash = "b" * 64

    _coordinator_event_sink(store)(
        manifest.run_id,
        coordinator_hash,
        WorkerProfile.CSP,
        event,
    )

    retained = store.events_after(manifest.run_id)
    assert len(retained) == 1
    assert retained[0].lane == "csp"
    assert retained[0].stage == "queryengine.tool_result"
    assert retained[0].details == {
        "coordinator_run_hash": coordinator_hash,
        "profile": "csp",
        "event": {
            "type": "tool_result",
            "data": {
                "tool": "submit_csp_source",
                "nested": {"passed": True},
            },
        },
    }
