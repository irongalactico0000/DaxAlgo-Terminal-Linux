from __future__ import annotations

import json
import subprocess
from pathlib import Path

import pytest

from daxalgo_strategy_agent.settings import (
    StrategyAgentConfigurationError,
    StrategyAgentSettings,
    UpstreamPins,
    read_environment_file,
)


def _repo(path: Path) -> str:
    path.mkdir()
    subprocess.run(["git", "init", "-q", str(path)], check=True)
    subprocess.run(
        ["git", "-C", str(path), "config", "user.email", "test@example.com"], check=True
    )
    subprocess.run(["git", "-C", str(path), "config", "user.name", "Test"], check=True)
    (path / "README").write_text("test", encoding="utf-8")
    subprocess.run(["git", "-C", str(path), "add", "README"], check=True)
    subprocess.run(["git", "-C", str(path), "commit", "-qm", "test"], check=True)
    return subprocess.run(
        ["git", "-C", str(path), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def test_environment_file_is_read_without_mutating_environment(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.delenv("EXAMPLE_SECRET", raising=False)
    path = tmp_path / ".env"
    path.write_text(
        "EXAMPLE_SECRET='secret-value'\nEASTMANUS_MODEL=openrouter/test\n",
        encoding="utf-8",
    )
    values = read_environment_file(path)
    assert values == {
        "EXAMPLE_SECRET": "secret-value",
        "EASTMANUS_MODEL": "openrouter/test",
    }
    assert "EXAMPLE_SECRET" not in __import__("os").environ


def test_settings_reports_exact_revision_mismatch(tmp_path: Path) -> None:
    query_root = tmp_path / "query"
    vibe_root = tmp_path / "vibe"
    query_revision = _repo(query_root)
    vibe_revision = _repo(vibe_root)
    settings = StrategyAgentSettings(
        store_root=tmp_path / "store",
        query_engine_root=query_root,
        query_engine_python=Path(__import__("sys").executable),
        query_engine_env_file=None,
        vibequant_root=vibe_root,
        vibequant_python=Path(__import__("sys").executable),
        csp_python=Path(__import__("sys").executable),
        pins=UpstreamPins(
            query_engine_revision="0" * 40,
            vibequant_revision=vibe_revision,
            akquant_version="0.3.36",
            csp_version="0.18.0",
        ),
    )
    with pytest.raises(StrategyAgentConfigurationError) as raised:
        settings.validate()
    assert raised.value.stage == "query_engine_source"
    assert query_revision in str(raised.value)


def test_upstream_lock_loads_required_versions(tmp_path: Path) -> None:
    path = tmp_path / "upstreams.lock.json"
    path.write_text(
        json.dumps(
            {
                "query_engine": {"revision": "a" * 40},
                "vibequant": {"revision": "b" * 40},
                "akquant": {"version": "0.3.36"},
                "csp": {"version": "0.18.0"},
            }
        ),
        encoding="utf-8",
    )
    pins = UpstreamPins.load(path)
    assert pins.csp_version == "0.18.0"


def test_unset_source_paths_do_not_fall_back_to_current_directory(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    lock_path = tmp_path / "upstreams.lock.json"
    lock_path.write_text(
        json.dumps(
            {
                "query_engine": {"revision": "a" * 40},
                "vibequant": {"revision": "b" * 40},
                "akquant": {"version": "0.3.36"},
                "csp": {"version": "0.18.0"},
            }
        ),
        encoding="utf-8",
    )
    for name in (
        "DAXALGO_QUERY_ENGINE_ROOT",
        "DAXALGO_QUERY_ENGINE_PYTHON",
        "DAXALGO_VIBEQUANT_ROOT",
        "DAXALGO_VIBEQUANT_PYTHON",
        "DAXALGO_CSP_PYTHON",
    ):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setenv("DAXALGO_STRATEGY_UPSTREAM_LOCK", str(lock_path))

    settings = StrategyAgentSettings.from_environment(package_root=tmp_path)

    assert settings.query_engine_root == Path()
    with pytest.raises(StrategyAgentConfigurationError) as raised:
        settings.validate()
    assert raised.value.stage == "query_engine_source"
    assert "not configured" in str(raised.value)
