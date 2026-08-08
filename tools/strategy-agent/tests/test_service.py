from __future__ import annotations

import asyncio
import hashlib
import threading
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest

from daxalgo_strategy_agent.contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
    NativeLaneResult,
    confirmed_intent_sha256,
    research_context_sha256,
)
from daxalgo_strategy_agent.run_store import NativeRunStore
from daxalgo_strategy_agent.service import StrategyAgentService, StrategyServiceError

CONFIRMED_INTENT = {
    "family": "directional_long_short",
    "strategy": "Trade the confirmed chart event and finish flat.",
    "scenarios": [
        {
            "name": "confirmed_jump",
            "timestamp_utc": "2026-08-08T00:00:00Z",
            "expected": "long",
        }
    ],
}
DEFAULT_RESEARCH_CONTEXT = {"symbol": "AAPL"}


def _manifest(
    input_workspace: Path,
    run_id: str = "native-run-1",
    *,
    context: dict[str, Any] | None = None,
) -> FrozenRunManifest:
    primary = input_workspace / "primary.csv"
    primary.write_text(
        "date,timestamp,open,high,low,close,volume,symbol\n"
        "2026-08-08,2026-08-08T00:00:00Z,100,101,99,100,10,PRIMARY\n",
        encoding="utf-8",
    )
    return FrozenRunManifest(
        run_id=run_id,
        confirmed_intent_sha256=confirmed_intent_sha256(CONFIRMED_INTENT),
        research_context_sha256=research_context_sha256(
            context or DEFAULT_RESEARCH_CONTEXT
        ),
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
                relative_path=primary.name,
                sha256=hashlib.sha256(primary.read_bytes()).hexdigest(),
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


async def _research_stream(session_id: str, message: str, context: dict[str, Any]):
    yield SimpleNamespace(
        type="request_start",
        data={"session_id": session_id, "symbol": context["symbol"]},
    )
    yield SimpleNamespace(type="text_delta", data={"text": f"considered: {message}"})
    yield SimpleNamespace(type="message_stop", data={"stop_reason": "end_turn"})


def _native_result(
    manifest: FrozenRunManifest,
    workspace: Path,
    lane: str,
    *,
    status: str = "passed",
) -> NativeLaneResult:
    artifact = workspace / "native" / lane / "result.txt"
    artifact.parent.mkdir(parents=True, exist_ok=True)
    artifact.write_text(f"genuine-{lane}-artifact", encoding="utf-8")
    return NativeLaneResult(
        run_id=manifest.run_id,
        lane=lane,
        manifest_sha256=manifest.manifest_sha256,
        status=status,
        native_stage="run_task" if lane == "vibequant" else "csp.run",
        framework="transcend-0/VibeQuant" if lane == "vibequant" else "Point72 CSP",
        framework_version="0.1.0" if lane == "vibequant" else "0.18.0",
        source_relative_path=f"native/{lane}/result.txt",
        artifact_relative_paths=(f"native/{lane}/result.txt",),
        artifact_sha256={
            f"native/{lane}/result.txt": hashlib.sha256(
                artifact.read_bytes()
            ).hexdigest()
        },
        observations=(
            {"native": True, "lane": lane, "trade_count": 1}
            if lane == "vibequant"
            else {
                "native": True,
                "lane": lane,
                "events": [
                    {
                        "output": "intent",
                        "timestamp_utc": "2026-08-08T00:00:00Z",
                        "value": "long",
                    }
                ],
            }
        ),
    )


def _service(tmp_path: Path) -> StrategyAgentService:
    def vibequant(manifest: FrozenRunManifest, workspace: Path) -> NativeLaneResult:
        return _native_result(manifest, workspace, "vibequant")

    async def csp(manifest: FrozenRunManifest, workspace: Path) -> NativeLaneResult:
        return _native_result(manifest, workspace, "csp")

    return StrategyAgentService(
        store=NativeRunStore(tmp_path / "store"),
        research_coordinator=_research_stream,
        vibequant_runner=vibequant,
        csp_runner=csp,
        session_id_factory=lambda: "research-1",
    )


@pytest.mark.asyncio
async def test_complete_headless_lifecycle_retains_transcript_artifacts_and_ordered_events(
    tmp_path: Path,
) -> None:
    service = _service(tmp_path)
    research_context = {
        "symbol": "AAPL",
        "comparisons": ["SPY", "QQQ", "VIX"],
    }
    created = service.create_research_session(research_context)
    assert created["status"] == "researching"

    after_message = await service.submit_research_message(
        "research-1", "Is this jump confirmed?"
    )
    assert after_message["message_count"] == 2
    research_events = service.research_events_after("research-1")
    assert [event.sequence for event in research_events] == list(
        range(1, len(research_events) + 1)
    )
    assert [event.stage for event in research_events[2:5]] == [
        "request_start",
        "text_delta",
        "message_stop",
    ]
    assert research_events[3].details == {"text": "considered: Is this jump confirmed?"}

    inputs = tmp_path / "inputs"
    inputs.mkdir()
    manifest = _manifest(inputs, context=research_context)
    confirmed = service.confirm_run("research-1", manifest, inputs, CONFIRMED_INTENT)
    assert confirmed["status"] == "confirmed"
    retained_root = tmp_path / "store" / manifest.run_id
    transcript = retained_root / "research" / "transcript.json"
    assert transcript.is_file()
    assert b"Is this jump confirmed?" in transcript.read_bytes()
    assert b"considered: Is this jump confirmed?" in transcript.read_bytes()
    assert (retained_root / "research" / "confirmed-intent.json").is_file()

    started = await service.start_run(manifest.run_id)
    assert started["status"] == "running"
    finished = await service.wait_for_run(manifest.run_id, timeout=5)
    assert finished["status"] == "completed"
    assert finished["evidence_status"] == "partially_proven"
    assert finished["comparison"]["relative_path"] == "comparison/report.json"
    assert (
        finished["comparison"]["report"]["scenario_checks"][0]["csp"]["status"]
        == "pass"
    )
    assert (
        finished["comparison"]["report"]["scenario_checks"][0]["vibequant"]["status"]
        == "unproven"
    )
    inspected = service.artifact_content(manifest.run_id, "native/vibequant/result.txt")
    assert inspected["encoding"] == "utf-8"
    assert inspected["content"] == "genuine-vibequant-artifact"
    assert finished["lane_states"] == {"vibequant": "passed", "csp": "passed"}
    assert set(finished["results"]) == {"csp", "vibequant"}
    assert (retained_root / "native" / "vibequant" / "result.txt").is_file()
    assert (retained_root / "native" / "csp" / "result.txt").is_file()

    run_events = service.run_events_after(manifest.run_id)
    assert [event.sequence for event in run_events] == list(
        range(1, len(run_events) + 1)
    )
    assert run_events[0].stage == "confirmation"
    assert any(
        event.lane == "vibequant" and event.stage == "run_task" for event in run_events
    )
    assert any(event.lane == "csp" and event.stage == "csp.run" for event in run_events)
    assert run_events[-2].stage == "evidence_report"
    assert run_events[-1].stage == "workflow_completion"
    assert run_events[-1].details["lane_statuses"] == {
        "vibequant": "passed",
        "csp": "passed",
    }
    assert service.run_events_after(manifest.run_id, run_events[-2].sequence) == (
        run_events[-1],
    )

    restarted = StrategyAgentService(
        store=NativeRunStore(tmp_path / "store"),
        research_coordinator=_research_stream,
        vibequant_runner=lambda _manifest, _workspace: None,  # type: ignore[arg-type]
        csp_runner=lambda _manifest, _workspace: None,  # type: ignore[arg-type]
    )
    recovered = restarted.run_status(manifest.run_id)
    assert recovered["status"] == "completed"
    assert recovered["evidence_status"] == "partially_proven"
    assert recovered["session_id"] == "research-1"
    assert (
        restarted.artifact_content(manifest.run_id, "comparison/report.json")["sha256"]
        == finished["comparison"]["sha256"]
    )


@pytest.mark.asyncio
async def test_confirmation_is_idempotent_only_for_same_manifest_and_closes_research(
    tmp_path: Path,
) -> None:
    service = _service(tmp_path)
    service.create_research_session({"symbol": "AAPL"})
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    manifest = _manifest(inputs)
    first = service.confirm_run("research-1", manifest, inputs, CONFIRMED_INTENT)
    second = service.confirm_run("research-1", manifest, inputs, CONFIRMED_INTENT)
    assert first["manifest_sha256"] == second["manifest_sha256"]

    changed = manifest.model_copy(update={"timezone_name": "Etc/UTC"})
    with pytest.raises(StrategyServiceError) as immutable:
        service.confirm_run("research-1", changed, inputs, CONFIRMED_INTENT)
    assert immutable.value.code == "confirmation_is_immutable"

    with pytest.raises(StrategyServiceError) as closed:
        await service.submit_research_message("research-1", "change it")
    assert closed.value.code == "session_already_confirmed"


def test_confirmation_rejects_a_readable_strategy_not_bound_by_manifest(
    tmp_path: Path,
) -> None:
    service = _service(tmp_path)
    service.create_research_session({"symbol": "AAPL"})
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    manifest = _manifest(inputs)

    with pytest.raises(StrategyServiceError) as mismatch:
        service.confirm_run(
            "research-1",
            manifest,
            inputs,
            {"strategy": "different unconfirmed behavior"},
        )

    assert mismatch.value.code == "confirmed_intent_hash_mismatch"
    assert not (tmp_path / "store" / manifest.run_id).exists()


def test_confirmation_rejects_unbound_or_different_frozen_chart_context(
    tmp_path: Path,
) -> None:
    service = _service(tmp_path)
    service.create_research_session(DEFAULT_RESEARCH_CONTEXT)
    inputs = tmp_path / "inputs"
    inputs.mkdir()

    unbound = _manifest(inputs).model_copy(update={"research_context_sha256": None})
    with pytest.raises(StrategyServiceError) as missing:
        service.confirm_run("research-1", unbound, inputs, CONFIRMED_INTENT)
    assert missing.value.code == "research_context_unbound"

    different = _manifest(inputs).model_copy(
        update={"research_context_sha256": research_context_sha256({"symbol": "MSFT"})}
    )
    with pytest.raises(StrategyServiceError) as mismatch:
        service.confirm_run("research-1", different, inputs, CONFIRMED_INTENT)
    assert mismatch.value.code == "research_context_hash_mismatch"
    assert not (tmp_path / "store" / different.run_id).exists()


def test_artifact_inspection_rejects_undeclared_files(tmp_path: Path) -> None:
    service = _service(tmp_path)
    service.create_research_session(DEFAULT_RESEARCH_CONTEXT)
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    manifest = _manifest(inputs)
    service.confirm_run("research-1", manifest, inputs, CONFIRMED_INTENT)

    with pytest.raises(StrategyServiceError) as undeclared:
        service.artifact_content(manifest.run_id, "primary.csv")
    assert undeclared.value.code == "artifact_not_declared"


@pytest.mark.asyncio
async def test_confirmation_rejects_while_research_response_is_in_progress(
    tmp_path: Path,
) -> None:
    started = asyncio.Event()
    release = asyncio.Event()

    async def blocking_research(
        _session_id: str, _message: str, _context: dict[str, Any]
    ):
        started.set()
        await release.wait()
        yield {"type": "text_delta", "data": {"text": "research complete"}}

    service = StrategyAgentService(
        store=NativeRunStore(tmp_path / "store"),
        research_coordinator=blocking_research,
        vibequant_runner=lambda _manifest, _workspace: None,  # type: ignore[arg-type]
        csp_runner=lambda _manifest, _workspace: None,  # type: ignore[arg-type]
        session_id_factory=lambda: "research-race",
    )
    service.create_research_session(DEFAULT_RESEARCH_CONTEXT)
    message_task = asyncio.create_task(
        service.submit_research_message("research-race", "review this chart")
    )
    await started.wait()
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    manifest = _manifest(inputs, run_id="research-race-run")

    with pytest.raises(StrategyServiceError) as in_progress:
        service.confirm_run("research-race", manifest, inputs, CONFIRMED_INTENT)
    assert in_progress.value.code == "research_in_progress"

    release.set()
    await message_task
    confirmed = service.confirm_run("research-race", manifest, inputs, CONFIRMED_INTENT)
    assert confirmed["status"] == "confirmed"


@pytest.mark.asyncio
async def test_research_timeout_is_exact_and_releases_confirmation_lock(
    tmp_path: Path,
) -> None:
    never_release = asyncio.Event()

    async def blocked_research(
        _session_id: str, _message: str, _context: dict[str, Any]
    ):
        await never_release.wait()
        yield {"type": "text_delta", "data": {"text": "unreachable"}}

    service = StrategyAgentService(
        store=NativeRunStore(tmp_path / "store"),
        research_coordinator=blocked_research,
        vibequant_runner=lambda _manifest, _workspace: None,  # type: ignore[arg-type]
        csp_runner=lambda _manifest, _workspace: None,  # type: ignore[arg-type]
        session_id_factory=lambda: "research-timeout",
        research_timeout_seconds=0.01,
    )
    service.create_research_session(DEFAULT_RESEARCH_CONTEXT)

    with pytest.raises(StrategyServiceError) as timed_out:
        await service.submit_research_message("research-timeout", "review")
    assert timed_out.value.code == "research_coordinator_timeout"
    assert timed_out.value.http_status == 504
    assert service.research_events_after("research-timeout")[-1].stage == (
        "message.coordinator_timeout"
    )

    inputs = tmp_path / "inputs"
    inputs.mkdir()
    confirmed = service.confirm_run(
        "research-timeout",
        _manifest(inputs, run_id="timeout-run"),
        inputs,
        CONFIRMED_INTENT,
    )
    assert confirmed["status"] == "confirmed"


def test_cancel_before_start_never_calls_native_callbacks(tmp_path: Path) -> None:
    calls: list[str] = []

    def native(manifest: FrozenRunManifest, workspace: Path) -> NativeLaneResult:
        calls.append(manifest.run_id)
        return _native_result(manifest, workspace, "vibequant")

    service = StrategyAgentService(
        store=NativeRunStore(tmp_path / "store"),
        research_coordinator=_research_stream,
        vibequant_runner=native,
        csp_runner=native,
        session_id_factory=lambda: "research-cancel",
    )
    service.create_research_session({"symbol": "AAPL"})
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    manifest = _manifest(inputs, run_id="cancelled-run")
    service.confirm_run("research-cancel", manifest, inputs, CONFIRMED_INTENT)

    cancelled = service.cancel_run(manifest.run_id)
    assert cancelled["status"] == "cancelled"
    assert cancelled["lane_states"] == {"vibequant": "cancelled", "csp": "cancelled"}
    assert calls == []
    events = service.run_events_after(manifest.run_id)
    assert events[-1].status == "cancelled"
    assert (
        events[-1].message == "Run was cancelled before either native callback started."
    )


@pytest.mark.asyncio
async def test_running_cancel_preserves_real_native_results_and_marks_run_cancelled(
    tmp_path: Path,
) -> None:
    callbacks_started = threading.Event()
    release_callbacks = threading.Event()
    started_count = 0
    count_lock = threading.Lock()

    def runner(lane: str):
        def run(manifest: FrozenRunManifest, workspace: Path) -> NativeLaneResult:
            nonlocal started_count
            with count_lock:
                started_count += 1
                if started_count == 2:
                    callbacks_started.set()
            assert release_callbacks.wait(timeout=5)
            return _native_result(manifest, workspace, lane)

        return run

    service = StrategyAgentService(
        store=NativeRunStore(tmp_path / "store"),
        research_coordinator=_research_stream,
        vibequant_runner=runner("vibequant"),
        csp_runner=runner("csp"),
        session_id_factory=lambda: "research-running-cancel",
    )
    service.create_research_session({"symbol": "AAPL"})
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    manifest = _manifest(inputs, run_id="running-cancel")
    service.confirm_run("research-running-cancel", manifest, inputs, CONFIRMED_INTENT)
    await service.start_run(manifest.run_id)

    assert await asyncio.to_thread(callbacks_started.wait, 5)
    cancellation = service.cancel_run(manifest.run_id)
    assert cancellation["status"] == "running"
    assert cancellation["cancel_requested"] is True
    release_callbacks.set()

    terminal = await service.wait_for_run(manifest.run_id, timeout=5)
    assert terminal["status"] == "cancelled"
    assert terminal["lane_states"] == {"vibequant": "passed", "csp": "passed"}
    assert {result["status"] for result in terminal["results"].values()} == {"passed"}
    events = service.run_events_after(manifest.run_id)
    assert any(event.stage == "cancellation_requested" for event in events)
    assert events[-1].status == "cancelled"


@pytest.mark.asyncio
async def test_invalid_native_result_becomes_exact_actionable_failed_stage(
    tmp_path: Path,
) -> None:
    def wrong_run(manifest: FrozenRunManifest, workspace: Path) -> NativeLaneResult:
        result = _native_result(manifest, workspace, "vibequant")
        return result.model_copy(update={"run_id": "wrong-run"})

    def csp(manifest: FrozenRunManifest, workspace: Path) -> NativeLaneResult:
        return _native_result(manifest, workspace, "csp")

    service = StrategyAgentService(
        store=NativeRunStore(tmp_path / "store"),
        research_coordinator=_research_stream,
        vibequant_runner=wrong_run,
        csp_runner=csp,
        session_id_factory=lambda: "research-invalid",
    )
    service.create_research_session({"symbol": "AAPL"})
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    manifest = _manifest(inputs, run_id="invalid-native")
    service.confirm_run("research-invalid", manifest, inputs, CONFIRMED_INTENT)

    await service.start_run(manifest.run_id)
    finished = await service.wait_for_run(manifest.run_id, timeout=5)
    assert finished["status"] == "failed"
    vibe_result = finished["results"]["vibequant"]
    assert vibe_result["native_stage"] == "result_contract"
    assert "run_id mismatch" in vibe_result["error"]
    assert finished["results"]["csp"]["status"] == "passed"
