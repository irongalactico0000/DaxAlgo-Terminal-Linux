from __future__ import annotations

import hashlib
import json
import threading
from datetime import datetime, timezone
from pathlib import Path

import pytest

from daxalgo_strategy_agent.contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
    NativeLaneResult,
)
from daxalgo_strategy_agent.run_store import NativeRunStore, RunStoreError


def _manifest(workspace: Path, run_id: str = "run-1") -> FrozenRunManifest:
    source = workspace / "primary.csv"
    source.write_text(
        "timestamp,open,high,low,close,volume\n2026-08-08T00:00:00Z,1,1,1,1,1\n",
        encoding="utf-8",
    )
    return FrozenRunManifest(
        run_id=run_id,
        confirmed_intent_sha256="f" * 64,
        selected_start_utc=datetime(2026, 8, 8, 0, 0, tzinfo=timezone.utc),
        selected_end_utc=datetime(2026, 8, 8, 0, 5, tzinfo=timezone.utc),
        as_of_utc=datetime(2026, 8, 8, 0, 10, tzinfo=timezone.utc),
        timezone_name="UTC",
        data_files=(
            FrozenDataFile(
                role="primary",
                instrument="PRIMARY",
                venue="fixture",
                source="test",
                timeframe="5m",
                relative_path="primary.csv",
                sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
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


def test_store_copies_inputs_appends_events_and_restores_results(
    tmp_path: Path,
) -> None:
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    store = NativeRunStore(tmp_path / "store")
    manifest = _manifest(inputs)
    retained = store.create_run(manifest, inputs)
    assert store.run_exists(manifest.run_id) is True
    assert store.run_exists("missing-run") is False
    assert retained.manifest.manifest_sha256 == manifest.manifest_sha256
    first = store.append_event(
        manifest.run_id,
        lane="vibequant",
        stage="run_task",
        status="started",
        message="VibeQuant run started.",
    )
    second = store.append_event(
        manifest.run_id,
        lane="csp",
        stage="csp.run",
        status="passed",
        message="CSP graph completed.",
    )
    assert (first.sequence, second.sequence) == (1, 2)
    source = retained.workspace / "csp.py"
    source.write_text("# retained CSP source\n", encoding="utf-8")
    result = NativeLaneResult(
        run_id=manifest.run_id,
        lane="csp",
        manifest_sha256=manifest.manifest_sha256,
        status="passed",
        native_stage="csp.run",
        framework="Point72 CSP",
        framework_version="0.18.0",
        source_relative_path="csp.py",
        artifact_relative_paths=("csp.py",),
        artifact_sha256={"csp.py": hashlib.sha256(source.read_bytes()).hexdigest()},
        observations={"runtime_kind": "typed_event_graph", "backtest": False},
    )
    store.retain_result(result)
    restored = store.load_run(manifest.run_id)
    assert [event.sequence for event in restored.events] == [1, 2]
    assert restored.results["csp"] == result


def test_store_rejects_same_run_id_for_different_manifest(tmp_path: Path) -> None:
    first_inputs = tmp_path / "first"
    second_inputs = tmp_path / "second"
    first_inputs.mkdir()
    second_inputs.mkdir()
    store = NativeRunStore(tmp_path / "store")
    first = _manifest(first_inputs)
    store.create_run(first, first_inputs)
    second = _manifest(second_inputs).model_copy(
        update={"confirmed_intent_sha256": "e" * 64}
    )
    with pytest.raises(RunStoreError, match="another manifest"):
        store.create_run(second, second_inputs)


def test_store_detects_retained_input_tampering(tmp_path: Path) -> None:
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    store = NativeRunStore(tmp_path / "store")
    manifest = _manifest(inputs)
    retained = store.create_run(manifest, inputs)
    (retained.workspace / "primary.csv").write_text("tampered", encoding="utf-8")
    with pytest.raises(ValueError, match="hash mismatch"):
        store.load_run(manifest.run_id)


def test_store_retains_and_verifies_one_hash_bound_comparison(tmp_path: Path) -> None:
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    store = NativeRunStore(tmp_path / "store")
    manifest = _manifest(inputs)
    retained = store.create_run(manifest, inputs)
    report = {
        "schema_version": "daxalgo-native-comparison/v1",
        "manifest_sha256": manifest.manifest_sha256,
        "confirmed_intent_sha256": manifest.confirmed_intent_sha256,
        "evidence_status": "partially_proven",
    }

    relative_path, payload_sha256, stored = store.retain_comparison(
        manifest.run_id, report
    )

    comparison_path = retained.workspace / relative_path
    assert relative_path == "comparison/report.json"
    assert payload_sha256 == hashlib.sha256(comparison_path.read_bytes()).hexdigest()
    assert stored["report_hash"]
    assert store.load_comparison(manifest.run_id) == stored
    assert store.retain_comparison(manifest.run_id, report) == (
        relative_path,
        payload_sha256,
        stored,
    )

    tampered = dict(stored)
    tampered["evidence_status"] = "passed"
    comparison_path.write_text(json.dumps(tampered), encoding="utf-8")
    with pytest.raises(RunStoreError, match="report hash mismatch"):
        store.load_comparison(manifest.run_id)


def test_store_rejects_unbound_or_replaced_comparison(tmp_path: Path) -> None:
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    store = NativeRunStore(tmp_path / "store")
    manifest = _manifest(inputs)
    store.create_run(manifest, inputs)

    with pytest.raises(RunStoreError, match="manifest hash mismatch"):
        store.retain_comparison(
            manifest.run_id,
            {
                "manifest_sha256": "0" * 64,
                "confirmed_intent_sha256": manifest.confirmed_intent_sha256,
            },
        )

    first = {
        "manifest_sha256": manifest.manifest_sha256,
        "confirmed_intent_sha256": manifest.confirmed_intent_sha256,
        "evidence_status": "partially_proven",
    }
    store.retain_comparison(manifest.run_id, first)
    with pytest.raises(RunStoreError, match="different comparison"):
        store.retain_comparison(
            manifest.run_id,
            {**first, "evidence_status": "failed"},
        )


def test_event_polling_is_atomic_during_concurrent_appends(tmp_path: Path) -> None:
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    store = NativeRunStore(tmp_path / "store")
    manifest = _manifest(inputs)
    store.create_run(manifest, inputs)
    errors: list[Exception] = []
    writer_done = threading.Event()

    def writer() -> None:
        try:
            for index in range(100):
                store.append_event(
                    manifest.run_id,
                    lane="comparison",
                    stage=f"event-{index}",
                    status="progress",
                    message="concurrent fixture event",
                )
        except Exception as exc:  # pragma: no cover - asserted in parent thread
            errors.append(exc)
        finally:
            writer_done.set()

    thread = threading.Thread(target=writer)
    thread.start()
    while not writer_done.is_set():
        observed = store.events_after(manifest.run_id, limit=25)
        assert [event.sequence for event in observed] == list(
            range(1, len(observed) + 1)
        )
    thread.join(timeout=5)

    assert errors == []
    assert [event.sequence for event in store.events_after(manifest.run_id)] == list(
        range(1, 101)
    )
