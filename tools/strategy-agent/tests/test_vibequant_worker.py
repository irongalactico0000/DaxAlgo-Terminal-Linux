from __future__ import annotations

import hashlib
import json
import os
import subprocess
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

from daxalgo_strategy_agent.contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
)
from daxalgo_strategy_agent.native import vibequant_worker
from daxalgo_strategy_agent.native import process as native_process

PINNED_VIBEQUANT_REVISION = "1f5442d88ec97b6075ac73a3c4d0b42d1c00a640"
MISSING_INTEGRATION_ENV = (
    "genuine VibeQuant integration requires existing paths in "
    "DAXALGO_VIBEQUANT_PYTHON and DAXALGO_VIBEQUANT_SOURCE_ROOT"
)


def _sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _manifest(
    workspace: Path, *, revision: str = PINNED_VIBEQUANT_REVISION
) -> FrozenRunManifest:
    data = workspace / "bars.csv"
    if not data.exists():
        data.write_text(
            "date,timestamp,open,high,low,close,volume,symbol\n"
            "2026-01-02,2026-01-02T00:00:00Z,100,101,99,100.5,1000,DEMO\n",
            encoding="utf-8",
        )
    return FrozenRunManifest(
        run_id="vibequant-run-1",
        confirmed_intent_sha256="a" * 64,
        selected_start_utc=datetime(2026, 1, 1, tzinfo=UTC),
        selected_end_utc=datetime(2026, 2, 28, tzinfo=UTC),
        as_of_utc=datetime(2026, 3, 1, tzinfo=UTC),
        timezone_name="UTC",
        data_files=(
            FrozenDataFile(
                role="primary",
                instrument="DEMO",
                venue="fixture",
                source="test",
                timeframe="1d",
                relative_path="bars.csv",
                sha256=_sha(data),
            ),
        ),
        components=(
            ComponentPin(
                component="query_engine",
                version="source",
                source_revision="b83ac12e41bcf7069e4aed57932fffc5245a1bfc",
            ),
            ComponentPin(
                component="vibequant",
                version="0.1.0",
                source_revision=revision,
            ),
            ComponentPin(component="akquant", version="0.3.36"),
            ComponentPin(component="csp", version="0.18.0"),
        ),
    )


def test_command_uses_configured_interpreter_and_isolated_mode(tmp_path: Path) -> None:
    python = tmp_path / "python3.12"
    command = vibequant_worker.build_vibequant_command(python)

    assert command[0] == str(python)
    assert command[1] == "-I"
    assert Path(command[2]).name == "vibequant_worker.py"
    assert command[3] == "--child"


def test_result_parser_accepts_one_frame_after_upstream_output() -> None:
    expected = {"protocol": vibequant_worker.CHILD_PROTOCOL, "stage": "completed"}
    stdout = "\n".join(
        (
            "upstream progress output",
            "another upstream line",
            vibequant_worker.RESULT_PREFIX + json.dumps(expected),
        )
    )

    assert vibequant_worker.parse_vibequant_worker_output(stdout) == expected


def test_result_parser_rejects_multiple_frames() -> None:
    frame = vibequant_worker.RESULT_PREFIX + json.dumps(
        {"protocol": vibequant_worker.CHILD_PROTOCOL}
    )
    with pytest.raises(ValueError, match="exactly one"):
        vibequant_worker.parse_vibequant_worker_output(f"{frame}\n{frame}")


@pytest.mark.parametrize(
    ("stdout", "message"),
    (
        ("plain output", "no framed result"),
        (vibequant_worker.RESULT_PREFIX + "{", "invalid JSON"),
        (
            vibequant_worker.RESULT_PREFIX + json.dumps({"protocol": "wrong"}),
            "protocol mismatch",
        ),
    ),
)
def test_result_parser_rejects_invalid_protocol(stdout: str, message: str) -> None:
    with pytest.raises(ValueError, match=message):
        vibequant_worker.parse_vibequant_worker_output(stdout)


def test_worker_hash_binds_request_and_maps_child_result(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    task = workspace / "task.json"
    task.write_text('{"name":"fixture"}', encoding="utf-8")
    manifest = _manifest(workspace)

    source = tmp_path / "VibeQuant"
    for relative in vibequant_worker.REQUIRED_SOURCE_FILES:
        path = source / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("# fixture\n", encoding="utf-8")
    python = tmp_path / "python3.12"
    python.write_text("#!/bin/sh\n", encoding="utf-8")
    python.chmod(0o755)

    calls: list[tuple[tuple[str, ...], dict[str, object]]] = []

    def fake_git_run(
        command: tuple[str, ...], **kwargs: object
    ) -> subprocess.CompletedProcess[str]:
        calls.append((tuple(command), kwargs))
        stdout = PINNED_VIBEQUANT_REVISION + "\n" if "rev-parse" in command else ""
        return subprocess.CompletedProcess(command, 0, stdout, "")

    def fake_bounded(
        command: tuple[str, ...], **kwargs: object
    ) -> native_process.NativeProcessResult:
        calls.append((tuple(command), kwargs))
        request = json.loads(str(kwargs["stdin_text"]))
        assert request["run_id"] == manifest.run_id
        assert request["manifest_sha256"] == manifest.manifest_sha256
        assert request["task_spec_sha256"] == _sha(task)
        assert request["source_revision"] == PINNED_VIBEQUANT_REVISION
        payload = {
            "protocol": vibequant_worker.CHILD_PROTOCOL,
            "run_id": manifest.run_id,
            "manifest_sha256": manifest.manifest_sha256,
            "ok": True,
            "status": "passed",
            "stage": "completed",
            "artifact_relative_paths": ["task.json"],
            "artifact_sha256": {"task.json": _sha(task)},
            "observations": {
                "stages": [
                    {
                        "stage": "completed",
                        "status": "passed",
                        "message": "fixture completed",
                    }
                ],
                "versions": {"vibequant": "0.1.0", "akquant": "0.3.36"},
                "metrics": {"total_return_pct": 1.25},
                "trade_count": 1,
                "capabilities": vibequant_worker.vibequant_capability_facts(),
            },
            "error": None,
        }
        stdout = "upstream log\n" + vibequant_worker.RESULT_PREFIX + json.dumps(payload)
        return native_process.NativeProcessResult(0, stdout, "", False, False)

    monkeypatch.setattr(vibequant_worker.subprocess, "run", fake_git_run)
    monkeypatch.setattr(native_process, "run_bounded_process", fake_bounded)

    result = vibequant_worker.run_vibequant_worker(
        manifest,
        workspace,
        python_executable=python,
        vibequant_source_root=source,
        task_spec_relative_path="task.json",
    )

    assert result.status == "passed"
    assert result.native_stage == "completed"
    assert result.manifest_sha256 == manifest.manifest_sha256
    assert result.framework == "transcend-0/VibeQuant"
    assert result.framework_version == "0.1.0"
    assert result.source_relative_path == "task.json"
    assert result.observations["source_revision"] == PINNED_VIBEQUANT_REVISION
    assert result.observations["task_spec_sha256"] == _sha(task)
    assert result.observations["trade_count"] == 1
    child_command = next(command for command, _kwargs in calls if command[0] != "git")
    assert child_command[0] == "/usr/bin/sandbox-exec"
    assert "-I" in child_command


def test_missing_interpreter_has_exact_failure_stage(tmp_path: Path) -> None:
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    manifest = _manifest(workspace)

    result = vibequant_worker.run_vibequant_worker(
        manifest,
        workspace,
        python_executable=tmp_path / "absent-python",
        vibequant_source_root=tmp_path / "absent-source",
        task_spec_relative_path="task.json",
    )

    assert result.status == "failed"
    assert result.native_stage == "interpreter"
    assert "interpreter is missing" in (result.error or "")


def test_capability_facts_do_not_claim_hidden_native_evidence() -> None:
    facts = vibequant_worker.vibequant_capability_facts()

    assert facts["short_positions"]["supported"] is False
    assert "enable_short_sell" in facts["short_positions"]["reason"]
    assert facts["raw_orders_and_fills"]["supported"] is False
    assert "not raw AKQuant orders" in facts["raw_orders_and_fills"]["reason"]
    assert facts["automatic_terminal_flatten"]["supported"] is False
    assert (
        "does not automatically flatten"
        in facts["automatic_terminal_flatten"]["reason"]
    )


def test_genuine_vibequant_task_plan_run_path(tmp_path: Path) -> None:
    python_text = os.environ.get("DAXALGO_VIBEQUANT_PYTHON")
    source_text = os.environ.get("DAXALGO_VIBEQUANT_SOURCE_ROOT")
    if not python_text or not source_text:
        pytest.skip(MISSING_INTEGRATION_ENV)
    python = Path(python_text)
    source = Path(source_text)
    if not python.is_file() or not source.is_dir():
        pytest.skip(MISSING_INTEGRATION_ENV)

    workspace = tmp_path / "workspace"
    workspace.mkdir()
    bars = workspace / "bars.csv"
    start = datetime(2026, 1, 1, tzinfo=UTC)
    rows = ["date,timestamp,open,high,low,close,volume,symbol"]
    for index in range(45):
        date = start + timedelta(days=index)
        price = 100.0 + index
        rows.append(
            f"{date.date().isoformat()},{date.isoformat().replace('+00:00', 'Z')},"
            f"{price},{price + 1},{price - 1},"
            f"{price + 0.5},{1000 + index},DEMO"
        )
    bars.write_text("\n".join(rows) + "\n", encoding="utf-8")

    strategy_source = """class Strategy(BaseStrategy):
    def __init__(self):
        super().__init__()
        self.index = 0

    def on_bar(self, bar):
        if self.index == 0:
            self.buy(symbol=bar.symbol, quantity=10, tag="integration-entry")
        elif self.index == 10 and self.get_position(bar.symbol) > 0:
            self.close_position(symbol=bar.symbol)
        self.index += 1
"""
    task = {
        "name": "daxalgo-vibequant-integration",
        "intent": "prove the genuine public VibeQuant pipeline",
        "kind": "strategy",
        "data": {"source": "csv", "path": "bars.csv", "symbols": ["DEMO"]},
        "strategy": {"name": "custom", "params": {"source": strategy_source}},
        "execution": {
            "mode": "backtest",
            "initial_cash": 100000.0,
            "commission_rate": 0.0,
            "stamp_tax_rate": 0.0,
            "slippage_bps": 0.0,
        },
        "report": {
            "formats": ["markdown", "json"],
            "html": False,
            "language": "en",
        },
    }
    (workspace / "task.json").write_text(json.dumps(task, indent=2), encoding="utf-8")
    manifest = _manifest(workspace)

    result = vibequant_worker.run_vibequant_worker(
        manifest,
        workspace,
        python_executable=python,
        vibequant_source_root=source,
        task_spec_relative_path="task.json",
    )

    assert result.status == "passed", result.error
    assert result.native_stage == "completed"
    assert result.observations["entrypoints"] == {
        "TaskSpec.from_dict": "src/dsl.py",
        "make_plan": "src/planner.py",
        "run_task": "src/runner.py",
    }
    assert result.observations["strategy_source"] == strategy_source
    assert result.observations["versions"]["vibequant"] == "0.1.0"
    assert result.observations["versions"]["akquant"] == "0.3.36"
    assert result.observations["trade_count"] == 1
    assert result.observations["equity"]["sample_count"] > 0
    assert "equity.csv" in {
        item["name"] for item in result.observations["public_artifacts"]
    }
    planned = [item["tool"] for item in result.observations["plan"]["steps"]]
    executed = [item["tool"] for item in result.observations["executed_steps"]]
    assert planned == executed
    assert "backtest" in executed


def test_genuine_vibequant_run_failure_retains_task_spec_artifact(
    tmp_path: Path,
) -> None:
    python_text = os.environ.get("DAXALGO_VIBEQUANT_PYTHON")
    source_text = os.environ.get("DAXALGO_VIBEQUANT_SOURCE_ROOT")
    if not python_text or not source_text:
        pytest.skip(MISSING_INTEGRATION_ENV)
    python = Path(python_text)
    source = Path(source_text)
    if not python.is_file() or not source.is_dir():
        pytest.skip(MISSING_INTEGRATION_ENV)

    workspace = tmp_path / "workspace"
    workspace.mkdir()
    bars = workspace / "bars.csv"
    rows = ["date,timestamp,open,high,low,close,volume,symbol"]
    start = datetime(2026, 1, 1, tzinfo=UTC)
    for index in range(45):
        date = start + timedelta(days=index)
        rows.append(
            f"{date.date().isoformat()},{date.isoformat().replace('+00:00', 'Z')},"
            "100,101,99,100.5,1000,DEMO"
        )
    bars.write_text("\n".join(rows) + "\n", encoding="utf-8")
    task = {
        "name": "daxalgo-vibequant-failure-evidence",
        "intent": "retain inspectable source after an upstream runtime failure",
        "kind": "strategy",
        "data": {"source": "csv", "path": "bars.csv", "symbols": ["DEMO"]},
        "strategy": {
            "name": "custom",
            "params": {
                "source": (
                    "class Strategy(BaseStrategy):\n"
                    "    def on_bar(self, bar):\n"
                    "        raise RuntimeError('deliberate-native-failure')\n"
                )
            },
        },
        "execution": {"mode": "backtest", "initial_cash": 100000.0},
        "report": {"formats": ["json"], "html": False, "language": "en"},
    }
    task_path = workspace / "task.json"
    task_path.write_text(json.dumps(task, indent=2), encoding="utf-8")
    manifest = _manifest(workspace)

    result = vibequant_worker.run_vibequant_worker(
        manifest,
        workspace,
        python_executable=python,
        vibequant_source_root=source,
        task_spec_relative_path="task.json",
    )

    assert result.status == "failed"
    assert result.native_stage == "run"
    assert "deliberate-native-failure" in (result.error or "")
    assert result.source_relative_path == "task.json"
    assert result.artifact_relative_paths == ("task.json",)
    assert result.artifact_sha256 == {"task.json": _sha(task_path)}


def test_genuine_vibequant_schema_failure_retains_task_spec_artifact(
    tmp_path: Path,
) -> None:
    python_text = os.environ.get("DAXALGO_VIBEQUANT_PYTHON")
    source_text = os.environ.get("DAXALGO_VIBEQUANT_SOURCE_ROOT")
    if not python_text or not source_text:
        pytest.skip(MISSING_INTEGRATION_ENV)
    python = Path(python_text)
    source = Path(source_text)
    if not python.is_file() or not source.is_dir():
        pytest.skip(MISSING_INTEGRATION_ENV)

    workspace = tmp_path / "workspace"
    workspace.mkdir()
    manifest = _manifest(workspace)
    task = {
        "name": "daxalgo-vibequant-schema-failure-evidence",
        "kind": "strategy",
        "data": {"source": "csv", "path": "bars.csv", "symbols": ["DEMO"]},
        "strategy": {
            "name": "custom",
            "params": {"source": "class Strategy(BaseStrategy):\n    pass\n"},
        },
        "execution": {"mode": "backtest"},
        "report": {"invented_key": True},
    }
    task_path = workspace / "task.json"
    task_path.write_text(json.dumps(task, indent=2), encoding="utf-8")

    result = vibequant_worker.run_vibequant_worker(
        manifest,
        workspace,
        python_executable=python,
        vibequant_source_root=source,
        task_spec_relative_path="task.json",
    )

    assert result.status == "failed"
    assert result.native_stage == "task_spec"
    assert "unknown keys under 'report'" in (result.error or "")
    assert result.source_relative_path == "task.json"
    assert result.artifact_relative_paths == ("task.json",)
    assert result.artifact_sha256 == {"task.json": _sha(task_path)}
