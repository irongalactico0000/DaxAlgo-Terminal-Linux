from __future__ import annotations

import builtins
import hashlib
import json
import os
import sys
import types
from datetime import datetime, timezone
from pathlib import Path

import pytest

from daxalgo_strategy_agent.contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
)
from daxalgo_strategy_agent.native import csp_child
from daxalgo_strategy_agent.native.csp_worker import (
    CSP_INTEGRATION_SKIP_REASON,
    CSP_PYTHON_ENV,
    CSP_RESULT_SCHEMA_VERSION,
    _parse_child_result,
    _parse_csv_series,
    run_csp_worker,
)


def _sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _write_series(path: Path, values: tuple[float, float]) -> None:
    path.write_text(
        "timestamp,close\n"
        f"2026-08-08T00:00:00Z,{values[0]}\n"
        f"2026-08-08T00:05:00Z,{values[1]}\n",
        encoding="utf-8",
    )


def _manifest(workspace: Path) -> FrozenRunManifest:
    primary = workspace / "primary.csv"
    comparison = workspace / "comparison.csv"
    _write_series(primary, (100.0, 102.0))
    _write_series(comparison, (50.0, 51.0))
    return FrozenRunManifest(
        run_id="csp-run-1",
        confirmed_intent_sha256="a" * 64,
        selected_start_utc=datetime(2026, 8, 8, 0, 0, tzinfo=timezone.utc),
        selected_end_utc=datetime(2026, 8, 8, 0, 5, tzinfo=timezone.utc),
        as_of_utc=datetime(2026, 8, 8, 0, 10, tzinfo=timezone.utc),
        timezone_name="UTC",
        data_files=(
            FrozenDataFile(
                role="primary",
                instrument="FDAX",
                venue="EUREX",
                source="fixture",
                timeframe="5m",
                relative_path=primary.name,
                sha256=_sha(primary),
            ),
            FrozenDataFile(
                role="comparison",
                instrument="FESX",
                venue="EUREX",
                source="fixture",
                timeframe="5m",
                relative_path=comparison.name,
                sha256=_sha(comparison),
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
                source_revision="1f5442d88ec97b6075ac73a3c4d0b42d1c00a640",
            ),
            ComponentPin(component="akquant", version="0.3.36"),
            ComponentPin(component="csp", version="0.18.0"),
        ),
    )


def _result_payload(manifest: FrozenRunManifest) -> dict[str, object]:
    return {
        "schema_version": CSP_RESULT_SCHEMA_VERSION,
        "run_id": manifest.run_id,
        "manifest_sha256": manifest.manifest_sha256,
        "source_sha256": "b" * 64,
        "status": "passed",
        "stage": "csp.run",
        "framework_version": "0.18.0",
        "observations": [
            {
                "output": "primary:FDAX",
                "timestamp_utc": "2026-08-08T09:00:00+09:00",
                "value": 100.0,
            }
        ],
        "error": None,
    }


def _child_request(manifest: FrozenRunManifest, source: Path) -> dict[str, str]:
    return {
        "run_id": manifest.run_id,
        "manifest_sha256": manifest.manifest_sha256,
        "source_sha256": _sha(source),
        "selected_start_utc": manifest.selected_start_utc.isoformat(),
        "selected_end_utc": manifest.selected_end_utc.isoformat(),
    }


def test_csv_parser_normalizes_offsets_deterministically(tmp_path: Path) -> None:
    path = tmp_path / "offset.csv"
    path.write_text(
        "timestamp,close,unused\n"
        "2026-08-08T09:00:00+09:00,100.25,x\n"
        "2026-08-08T00:05:00Z,101.5,y\n",
        encoding="utf-8",
    )

    first = _parse_csv_series(path, relative_path=path.name)
    second = _parse_csv_series(path, relative_path=path.name)

    assert (
        first
        == second
        == [
            {"timestamp_utc": "2026-08-08T00:00:00Z", "value": 100.25},
            {"timestamp_utc": "2026-08-08T00:05:00Z", "value": 101.5},
        ]
    )


@pytest.mark.parametrize(
    ("body", "message"),
    [
        (
            "timestamp,close\n2026-08-08T00:00:00Z,1\n2026-08-08T00:00:00Z,2\n",
            "timestamps must be strictly increasing",
        ),
        ("timestamp,close\n2026-08-08T00:00:00,1\n", "must include a UTC offset"),
        ("timestamp,close\n2026-08-08T00:00:00Z,nan\n", "close is not a finite number"),
    ],
)
def test_csv_parser_rejects_ambiguous_inputs(
    tmp_path: Path,
    body: str,
    message: str,
) -> None:
    path = tmp_path / "bad.csv"
    path.write_text(body, encoding="utf-8")

    with pytest.raises(ValueError, match=message):
        _parse_csv_series(path, relative_path=path.name)


def test_result_parser_binds_hashes_and_normalizes_observation_time(
    tmp_path: Path,
) -> None:
    manifest = _manifest(tmp_path)
    payload = _result_payload(manifest)

    parsed = _parse_child_result(
        payload,
        run_id=manifest.run_id,
        manifest_sha256=manifest.manifest_sha256,
        source_sha256="b" * 64,
    )

    assert parsed["stage"] == "csp.run"
    assert parsed["framework_version"] == "0.18.0"
    assert parsed["observations"] == [
        {
            "output": "primary:FDAX",
            "timestamp_utc": "2026-08-08T00:00:00Z",
            "value": 100.0,
        }
    ]

    payload["manifest_sha256"] = "c" * 64
    with pytest.raises(ValueError, match="manifest_sha256 does not match"):
        _parse_child_result(
            payload,
            run_id=manifest.run_id,
            manifest_sha256=manifest.manifest_sha256,
            source_sha256="b" * 64,
        )


def test_result_parser_rejects_backtest_claim_fields(tmp_path: Path) -> None:
    manifest = _manifest(tmp_path)
    payload = _result_payload(manifest)
    payload["pnl"] = 12.5

    with pytest.raises(ValueError, match="unexpected fields: pnl"):
        _parse_child_result(
            payload,
            run_id=manifest.run_id,
            manifest_sha256=manifest.manifest_sha256,
            source_sha256="b" * 64,
        )


def test_worker_reports_missing_interpreter_before_native_execution(
    tmp_path: Path,
) -> None:
    manifest = _manifest(tmp_path)
    (tmp_path / "strategy.py").write_text("# supplied source\n", encoding="utf-8")

    result = run_csp_worker(
        manifest,
        tmp_path,
        python_executable=tmp_path / "missing-python",
        source_relative_path="strategy.py",
    )

    assert result.status == "failed"
    assert result.native_stage == "worker.interpreter"
    assert result.framework_version == "unavailable"


def test_worker_reports_missing_source_before_native_execution(tmp_path: Path) -> None:
    manifest = _manifest(tmp_path)

    result = run_csp_worker(
        manifest,
        tmp_path,
        python_executable=Path(sys.executable),
        source_relative_path="missing.py",
    )

    assert result.status == "failed"
    assert result.native_stage == "worker.source"
    assert result.source_relative_path == "missing.py"


def test_worker_reports_changed_frozen_input_at_manifest_stage(tmp_path: Path) -> None:
    manifest = _manifest(tmp_path)
    (tmp_path / "strategy.py").write_text("# supplied source\n", encoding="utf-8")
    (tmp_path / "primary.csv").write_text("changed\n", encoding="utf-8")

    result = run_csp_worker(
        manifest,
        tmp_path,
        python_executable=Path(sys.executable),
        source_relative_path="strategy.py",
    )

    assert result.status == "failed"
    assert result.native_stage == "manifest.verify"
    assert "hash mismatch" in (result.error or "")


def test_child_reports_csp_import_failure_distinctly(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest = _manifest(tmp_path)
    source = tmp_path / "strategy.py"
    source.write_text(
        "raise AssertionError('source must not start')\n", encoding="utf-8"
    )
    request = tmp_path / "request.json"
    request.write_text(json.dumps(_child_request(manifest, source)), encoding="utf-8")
    result = tmp_path / "result.json"
    monkeypatch.setattr(
        sys,
        "argv",
        [
            "csp_child.py",
            "--source",
            str(source),
            "--request",
            str(request),
            "--result",
            str(result),
        ],
    )
    real_import = builtins.__import__

    def missing_csp(name: str, *args: object, **kwargs: object) -> object:
        if name == "csp":
            raise ModuleNotFoundError("No module named 'csp'")
        return real_import(name, *args, **kwargs)

    monkeypatch.setattr(builtins, "__import__", missing_csp)

    assert csp_child.main() == 3
    payload = json.loads(result.read_text(encoding="utf-8"))
    assert payload["stage"] == "csp.import"
    assert payload["framework_version"] == "unavailable"
    assert payload["error"] == "ModuleNotFoundError: No module named 'csp'"


def test_child_reports_source_runtime_failure_as_csp_run(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest = _manifest(tmp_path)
    source = tmp_path / "strategy.py"
    source.write_text("raise RuntimeError('fixture graph failure')\n", encoding="utf-8")
    request = tmp_path / "request.json"
    request.write_text(json.dumps(_child_request(manifest, source)), encoding="utf-8")
    result = tmp_path / "result.json"
    monkeypatch.setitem(
        sys.modules,
        "csp",
        types.SimpleNamespace(__version__="0.18.0", run=lambda *_args, **_kwargs: {}),
    )
    monkeypatch.setattr(
        sys,
        "argv",
        [
            "csp_child.py",
            "--source",
            str(source),
            "--request",
            str(request),
            "--result",
            str(result),
        ],
    )

    assert csp_child.main() == 5
    payload = json.loads(result.read_text(encoding="utf-8"))
    assert payload["stage"] == "csp.run"
    assert payload["framework_version"] == "0.18.0"
    assert payload["error"] == "RuntimeError: fixture graph failure"


def test_child_owns_success_envelope_and_calls_native_csp_run(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest = _manifest(tmp_path)
    source = tmp_path / "strategy.py"
    source.write_text(
        "from pathlib import Path\n\n"
        "Path(__file__).with_name('forged-result.json').write_text('{\"status\":\"passed\"}')\n\n"
        "def build_graph(request):\n"
        "    return lambda: None\n",
        encoding="utf-8",
    )
    request = tmp_path / "request.json"
    request.write_text(json.dumps(_child_request(manifest, source)), encoding="utf-8")
    result = tmp_path / "result.json"
    calls: list[dict[str, object]] = []

    def native_run(
        graph: object, **kwargs: object
    ) -> dict[str, list[tuple[datetime, float]]]:
        calls.append({"graph": graph, **kwargs})
        return {"intent": [(datetime(2026, 8, 8, 0, 5), 1.0)]}

    monkeypatch.setitem(
        sys.modules,
        "csp",
        types.SimpleNamespace(__version__="0.18.0", run=native_run),
    )
    monkeypatch.setattr(
        sys,
        "argv",
        [
            "csp_child.py",
            "--source",
            str(source),
            "--request",
            str(request),
            "--result",
            str(result),
        ],
    )

    assert csp_child.main() == 0
    assert len(calls) == 1
    payload = json.loads(result.read_text(encoding="utf-8"))
    assert payload["status"] == "passed"
    assert payload["stage"] == "csp.run"
    assert payload["observations"] == [
        {
            "output": "intent",
            "timestamp_utc": "2026-08-08T00:05:00Z",
            "value": 1.0,
        }
    ]


_CONFIGURED_CSP_PYTHON = os.environ.get(CSP_PYTHON_ENV)


@pytest.mark.skipif(
    not _CONFIGURED_CSP_PYTHON,
    reason=CSP_INTEGRATION_SKIP_REASON,
)
def test_configured_csp_018_executes_supplied_source_file_and_real_curves(
    tmp_path: Path,
) -> None:
    manifest = _manifest(tmp_path)
    source = tmp_path / "generated_strategy.py"
    source.write_text(
        """from __future__ import annotations

from datetime import datetime, timezone

import csp


def as_csp_time(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    return parsed.astimezone(timezone.utc).replace(tzinfo=None)


def build_graph(request):
    @csp.graph
    def supplied_graph():
        for series in request["series"]:
            curve = csp.curve(
                float,
                [
                    (as_csp_time(item["timestamp_utc"]), item["value"])
                    for item in series["observations"]
                ],
            )
            csp.add_graph_output(f'{series["role"]}:{series["instrument"]}', curve)
    return supplied_graph
""",
        encoding="utf-8",
    )

    result = run_csp_worker(
        manifest,
        tmp_path,
        python_executable=Path(_CONFIGURED_CSP_PYTHON or ""),
        source_relative_path=source.name,
    )

    assert result.status == "passed", result.error
    assert result.native_stage == "csp.run"
    assert result.framework == "Point72 CSP"
    assert result.framework_version == "0.18.0"
    assert result.source_relative_path == source.name
    assert result.artifact_relative_paths == (source.name,)
    assert result.artifact_sha256 == {source.name: _sha(source)}
    assert set(result.observations) == {
        "native_evidence_kind",
        "native_api",
        "evidence_trust",
        "source_sha256",
        "events",
        "fills_claimed",
        "profit_and_loss_claimed",
        "market_backtest_claimed",
    }
    assert result.observations["native_evidence_kind"] == "typed_event_graph"
    assert result.observations["native_api"] == "csp.run"
    assert (
        result.observations["evidence_trust"]
        == "host_wrapper_observed_not_security_attested"
    )
    assert result.observations["source_sha256"] == _sha(source)
    assert result.observations["fills_claimed"] is False
    assert result.observations["profit_and_loss_claimed"] is False
    assert result.observations["market_backtest_claimed"] is False
    assert result.observations["events"] == [
        {
            "output": "primary:FDAX",
            "timestamp_utc": "2026-08-08T00:00:00Z",
            "value": 100.0,
        },
        {
            "output": "primary:FDAX",
            "timestamp_utc": "2026-08-08T00:05:00Z",
            "value": 102.0,
        },
        {
            "output": "comparison:FESX",
            "timestamp_utc": "2026-08-08T00:00:00Z",
            "value": 50.0,
        },
        {
            "output": "comparison:FESX",
            "timestamp_utc": "2026-08-08T00:05:00Z",
            "value": 51.0,
        },
    ]
    assert list(tmp_path.glob(".daxalgo-csp-*")) == []


@pytest.mark.skipif(
    not _CONFIGURED_CSP_PYTHON,
    reason=CSP_INTEGRATION_SKIP_REASON,
)
def test_configured_csp_source_cannot_replace_host_collector_through_main_module(
    tmp_path: Path,
) -> None:
    manifest = _manifest(tmp_path)
    source = tmp_path / "main_module_patch_strategy.py"
    source.write_text(
        '''from __future__ import annotations

import __main__
from datetime import datetime, timezone

import csp


__main__._capture_outputs = lambda _outputs: [
    {
        "output": "forged",
        "timestamp_utc": "2026-08-08T00:05:00Z",
        "value": "not-from-csp-run",
    }
]
__main__._write_result = lambda *args, **kwargs: None


def as_csp_time(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    return parsed.astimezone(timezone.utc).replace(tzinfo=None)


def build_graph(request):
    @csp.graph
    def supplied_graph():
        curve = csp.curve(
            str,
            [(as_csp_time("2026-08-08T00:05:00Z"), "native-csp-output")],
        )
        csp.add_graph_output("intent", curve)
    return supplied_graph
''',
        encoding="utf-8",
    )

    result = run_csp_worker(
        manifest,
        tmp_path,
        python_executable=Path(_CONFIGURED_CSP_PYTHON or ""),
        source_relative_path=source.name,
    )

    assert result.status == "passed", result.error
    assert result.observations["events"] == [
        {
            "output": "intent",
            "timestamp_utc": "2026-08-08T00:05:00Z",
            "value": "native-csp-output",
        }
    ]
    assert (
        result.observations["evidence_trust"]
        == "host_wrapper_observed_not_security_attested"
    )


@pytest.mark.skipif(
    not _CONFIGURED_CSP_PYTHON,
    reason=CSP_INTEGRATION_SKIP_REASON,
)
def test_configured_csp_018_reports_supplied_source_runtime_failure(
    tmp_path: Path,
) -> None:
    manifest = _manifest(tmp_path)
    source = tmp_path / "broken_strategy.py"
    source.write_text(
        "import csp\n\ndef build_graph(request):\n"
        "    raise RuntimeError('configured CSP source failed')\n",
        encoding="utf-8",
    )

    result = run_csp_worker(
        manifest,
        tmp_path,
        python_executable=Path(_CONFIGURED_CSP_PYTHON or ""),
        source_relative_path=source.name,
    )

    assert result.status == "failed"
    assert result.native_stage == "csp.run"
    assert result.framework_version == "0.18.0"
    assert result.error == "RuntimeError: configured CSP source failed"
