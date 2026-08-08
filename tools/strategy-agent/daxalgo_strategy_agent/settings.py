"""Explicit process boundaries and pinned upstream locations for the strategy-agent service."""

from __future__ import annotations

import json
import os
import subprocess
from dataclasses import dataclass
from pathlib import Path


class StrategyAgentConfigurationError(RuntimeError):
    def __init__(self, stage: str, message: str) -> None:
        super().__init__(message)
        self.stage = stage


@dataclass(frozen=True)
class UpstreamPins:
    query_engine_revision: str
    vibequant_revision: str
    akquant_version: str
    csp_version: str

    @classmethod
    def load(cls, path: Path) -> "UpstreamPins":
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
            return cls(
                query_engine_revision=payload["query_engine"]["revision"],
                vibequant_revision=payload["vibequant"]["revision"],
                akquant_version=payload["akquant"]["version"],
                csp_version=payload["csp"]["version"],
            )
        except Exception as exc:
            raise StrategyAgentConfigurationError(
                "upstream_lock", f"Cannot read upstream lock: {exc}"
            ) from exc


@dataclass(frozen=True)
class StrategyAgentSettings:
    store_root: Path
    query_engine_root: Path
    query_engine_python: Path
    query_engine_env_file: Path | None
    vibequant_root: Path
    vibequant_python: Path
    csp_python: Path
    pins: UpstreamPins

    @classmethod
    def from_environment(
        cls, *, package_root: Path | None = None
    ) -> "StrategyAgentSettings":
        root = (package_root or Path(__file__).resolve().parent.parent).resolve()
        lock_path = Path(
            os.environ.get(
                "DAXALGO_STRATEGY_UPSTREAM_LOCK", root / "upstreams.lock.json"
            )
        )
        query_root = _optional_environment_path("DAXALGO_QUERY_ENGINE_ROOT")
        query_python = _optional_environment_path("DAXALGO_QUERY_ENGINE_PYTHON")
        vibe_root = _optional_environment_path("DAXALGO_VIBEQUANT_ROOT")
        vibe_python = _optional_environment_path("DAXALGO_VIBEQUANT_PYTHON")
        csp_python = _optional_environment_path("DAXALGO_CSP_PYTHON")
        env_value = os.environ.get("DAXALGO_QUERY_ENGINE_ENV_FILE", "").strip()
        return cls(
            store_root=Path(
                os.environ.get("DAXALGO_STRATEGY_AGENT_STORE", root / ".runs")
            )
            .expanduser()
            .resolve(),
            query_engine_root=query_root,
            query_engine_python=query_python,
            query_engine_env_file=Path(env_value).expanduser().resolve()
            if env_value
            else None,
            vibequant_root=vibe_root,
            vibequant_python=vibe_python,
            csp_python=csp_python,
            pins=UpstreamPins.load(lock_path.expanduser().resolve()),
        )

    def validate(self) -> None:
        self._require_configured(self.query_engine_root, "query_engine_source")
        self._require_repo(
            self.query_engine_root,
            self.pins.query_engine_revision,
            "query_engine_source",
        )
        self._require_configured(self.query_engine_python, "query_engine_python")
        self._require_python(self.query_engine_python, "query_engine_python")
        self._require_configured(self.vibequant_root, "vibequant_source")
        self._require_repo(
            self.vibequant_root, self.pins.vibequant_revision, "vibequant_source"
        )
        self._require_configured(self.vibequant_python, "vibequant_python")
        self._require_python(self.vibequant_python, "vibequant_python")
        self._require_configured(self.csp_python, "csp_python")
        self._require_python(self.csp_python, "csp_python")
        if (
            self.query_engine_env_file is not None
            and not self.query_engine_env_file.is_file()
        ):
            raise StrategyAgentConfigurationError(
                "query_engine_credentials",
                f"Configured environment file does not exist: {self.query_engine_env_file}",
            )

    @staticmethod
    def _require_configured(path: Path, stage: str) -> None:
        if path == Path():
            raise StrategyAgentConfigurationError(
                stage, f"Required path is not configured for {stage}"
            )

    @staticmethod
    def _require_python(path: Path, stage: str) -> None:
        if not path.is_file():
            raise StrategyAgentConfigurationError(
                stage, f"Configured Python interpreter does not exist: {path}"
            )
        try:
            completed = subprocess.run(
                [
                    str(path),
                    "-c",
                    "import sys; print('.'.join(map(str, sys.version_info[:3])))",
                ],
                check=True,
                capture_output=True,
                text=True,
                timeout=10,
            )
        except Exception as exc:
            raise StrategyAgentConfigurationError(
                stage, f"Python interpreter failed: {path}: {exc}"
            ) from exc
        if not completed.stdout.strip().startswith("3.12."):
            raise StrategyAgentConfigurationError(
                stage,
                f"Python 3.12 is required, observed {completed.stdout.strip()} at {path}",
            )

    @staticmethod
    def _require_repo(path: Path, expected_revision: str, stage: str) -> None:
        if not path.is_dir() or not (path / ".git").exists():
            raise StrategyAgentConfigurationError(
                stage, f"Configured source repository is missing: {path}"
            )
        try:
            completed = subprocess.run(
                ["git", "-C", str(path), "rev-parse", "HEAD"],
                check=True,
                capture_output=True,
                text=True,
                timeout=10,
            )
        except Exception as exc:
            raise StrategyAgentConfigurationError(
                stage, f"Cannot inspect source revision at {path}: {exc}"
            ) from exc
        observed = completed.stdout.strip()
        if observed != expected_revision:
            raise StrategyAgentConfigurationError(
                stage,
                f"Source revision mismatch at {path}: expected {expected_revision}, observed {observed}",
            )


def read_environment_file(path: Path | None) -> dict[str, str]:
    """Read a simple KEY=VALUE file without changing or logging process-global credentials."""
    if path is None:
        return {}
    values: dict[str, str] = {}
    for line_number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in line:
            raise StrategyAgentConfigurationError(
                "query_engine_credentials",
                f"Invalid environment entry at line {line_number}",
            )
        key, value = line.split("=", 1)
        key = key.strip()
        if (
            not key
            or not (key[0].isalpha() or key[0] == "_")
            or not all(ch.isalnum() or ch == "_" for ch in key)
        ):
            raise StrategyAgentConfigurationError(
                "query_engine_credentials",
                f"Invalid environment key at line {line_number}",
            )
        values[key] = value.strip().strip('"').strip("'")
    return values


def _optional_environment_path(name: str) -> Path:
    value = os.environ.get(name, "").strip()
    return Path(value).expanduser().resolve() if value else Path()
