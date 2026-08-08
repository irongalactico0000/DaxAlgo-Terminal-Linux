from __future__ import annotations

import hashlib
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import httpx
import pytest

from daxalgo_strategy_agent.api import create_app
from daxalgo_strategy_agent.contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
    NativeLaneResult,
    confirmed_intent_sha256,
    research_context_sha256,
)
from daxalgo_strategy_agent.run_store import NativeRunStore
from daxalgo_strategy_agent.service import StrategyAgentService

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
RESEARCH_CONTEXT = {"symbol": "AAPL", "comparisons": ["SPY"]}


def _manifest(workspace: Path) -> FrozenRunManifest:
    source = workspace / "primary.csv"
    source.write_text(
        "date,timestamp,open,high,low,close,volume,symbol\n"
        "2026-08-08,2026-08-08T00:00:00Z,100,101,99,100,10,AAPL\n",
        encoding="utf-8",
    )
    return FrozenRunManifest(
        run_id="api-run",
        confirmed_intent_sha256=confirmed_intent_sha256(CONFIRMED_INTENT),
        research_context_sha256=research_context_sha256(RESEARCH_CONTEXT),
        selected_start_utc=datetime(2026, 8, 8, 0, 0, tzinfo=timezone.utc),
        selected_end_utc=datetime(2026, 8, 8, 0, 5, tzinfo=timezone.utc),
        as_of_utc=datetime(2026, 8, 8, 0, 10, tzinfo=timezone.utc),
        timezone_name="UTC",
        data_files=(
            FrozenDataFile(
                role="primary",
                instrument="AAPL",
                venue="NASDAQ",
                source="fixture",
                timeframe="5m",
                relative_path=source.name,
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


async def _research(session_id: str, message: str, context: dict[str, Any]):
    yield {"type": "text_delta", "data": {"text": f"{context['symbol']}: {message}"}}
    yield {"type": "message_stop", "data": {"stop_reason": "end_turn"}}


def _runner(lane: str):
    def run(manifest: FrozenRunManifest, workspace: Path) -> NativeLaneResult:
        source = workspace / "native" / lane / "strategy.py"
        source.parent.mkdir(parents=True, exist_ok=True)
        source.write_text(f"# genuine {lane} fixture\n", encoding="utf-8")
        relative_path = f"native/{lane}/strategy.py"
        return NativeLaneResult(
            run_id=manifest.run_id,
            lane=lane,
            manifest_sha256=manifest.manifest_sha256,
            status="passed",
            native_stage="run_task" if lane == "vibequant" else "csp.run",
            framework=(
                "transcend-0/VibeQuant" if lane == "vibequant" else "Point72 CSP"
            ),
            framework_version="0.1.0" if lane == "vibequant" else "0.18.0",
            source_relative_path=relative_path,
            artifact_relative_paths=(relative_path,),
            artifact_sha256={
                relative_path: hashlib.sha256(source.read_bytes()).hexdigest()
            },
            observations=(
                {"workspace": workspace.name, "trade_count": 1}
                if lane == "vibequant"
                else {
                    "workspace": workspace.name,
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

    return run


@pytest.mark.asyncio
async def test_http_api_drives_same_service_and_supports_ordered_polling(
    tmp_path: Path,
) -> None:
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    manifest = _manifest(inputs)
    service = StrategyAgentService(
        store=NativeRunStore(tmp_path / "store"),
        research_coordinator=_research,
        vibequant_runner=_runner("vibequant"),
        csp_runner=_runner("csp"),
        session_id_factory=lambda: "api-session",
    )
    transport = httpx.ASGITransport(app=create_app(service))
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as client:
        health = await client.get("/healthz")
        assert health.json() == {
            "status": "ok",
            "service": "daxalgo-native-strategy-agent",
        }

        created = await client.post(
            "/api/v1/strategy-sessions",
            json={"context": RESEARCH_CONTEXT},
        )
        assert created.status_code == 201
        assert created.json()["session_id"] == "api-session"

        message = await client.post(
            "/api/v1/strategy-sessions/api-session/messages",
            json={"message": "confirm the jump"},
        )
        assert message.status_code == 200
        session_events = await client.get(
            "/api/v1/strategy-sessions/api-session/events", params={"after_seq": 1}
        )
        assert session_events.status_code == 200
        event_body = session_events.json()
        assert [event["stage"] for event in event_body["events"]][1:3] == [
            "text_delta",
            "message_stop",
        ]
        assert event_body["next_after_seq"] == event_body["events"][-1]["sequence"]

        confirmed = await client.post(
            "/api/v1/strategy-runs",
            json={
                "session_id": "api-session",
                "manifest": manifest.model_dump(mode="json"),
                "input_workspace": str(inputs),
                "confirmed_intent": CONFIRMED_INTENT,
            },
        )
        assert confirmed.status_code == 201
        assert confirmed.json()["fixed_lanes"] == ["vibequant", "csp"]

        started = await client.post("/api/v1/strategy-runs/api-run/start")
        assert started.status_code == 202
        assert started.json()["status"] == "running"
        await service.wait_for_run("api-run", timeout=5)

        status = await client.get("/api/v1/strategy-runs/api-run")
        assert status.status_code == 200
        assert status.json()["status"] == "completed"
        assert status.json()["evidence_status"] == "partially_proven"
        assert status.json()["comparison"]["report"]["report_hash"]
        assert set(status.json()["results"]) == {"vibequant", "csp"}

        artifact = await client.get(
            "/api/v1/strategy-runs/api-run/artifacts",
            params={"path": "native/csp/strategy.py"},
        )
        assert artifact.status_code == 200
        assert artifact.json()["content"] == "# genuine csp fixture\n"

        first_poll = await client.get("/api/v1/strategy-runs/api-run/events")
        first_body = first_poll.json()
        assert first_body["terminal"] is True
        sequences = [event["sequence"] for event in first_body["events"]]
        assert sequences == list(range(1, len(sequences) + 1))

        cursor = first_body["events"][-2]["sequence"]
        tail_poll = await client.get(
            "/api/v1/strategy-runs/api-run/events", params={"after_seq": cursor}
        )
        tail_body = tail_poll.json()
        assert len(tail_body["events"]) == 1
        assert tail_body["events"][0]["stage"] == "workflow_completion"
        assert tail_body["next_after_seq"] == sequences[-1]


@pytest.mark.asyncio
async def test_http_api_returns_actionable_service_error(tmp_path: Path) -> None:
    service = StrategyAgentService(
        store=NativeRunStore(tmp_path / "store"),
        research_coordinator=_research,
        vibequant_runner=_runner("vibequant"),
        csp_runner=_runner("csp"),
    )
    transport = httpx.ASGITransport(app=create_app(service))
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as client:
        missing = await client.get("/api/v1/strategy-runs/not-found")
    assert missing.status_code == 404
    assert missing.json() == {
        "detail": {
            "code": "run_not_found",
            "message": "run does not exist: not-found",
        }
    }
