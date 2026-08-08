"""Loopback FastAPI routes for the headless native-strategy service."""

from __future__ import annotations

from pathlib import Path
from typing import Any

from fastapi import FastAPI, Query
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field

from .contracts import FrozenRunManifest
from .service import StrategyAgentService, StrategyServiceError


class _RequestModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class CreateResearchSessionRequest(_RequestModel):
    context: dict[str, Any] = Field(default_factory=dict)


class SubmitResearchMessageRequest(_RequestModel):
    message: str = Field(min_length=1, max_length=100_000)


class ConfirmStrategyRunRequest(_RequestModel):
    manifest: FrozenRunManifest
    input_workspace: str = Field(min_length=1, max_length=4096)
    confirmed_intent: dict[str, Any]


class CreateStrategyRunRequest(ConfirmStrategyRunRequest):
    session_id: str = Field(min_length=1, max_length=100)


def create_app(service: StrategyAgentService) -> FastAPI:
    """Expose one service instance without constructing another coordinator or runtime."""

    if not isinstance(service, StrategyAgentService):
        raise TypeError("service must be a StrategyAgentService")

    app = FastAPI(title="DaxAlgo Native Strategy Agent", version="1.0")

    @app.get("/healthz")
    async def health() -> dict[str, str]:
        return {"status": "ok", "service": "daxalgo-native-strategy-agent"}

    @app.exception_handler(StrategyServiceError)
    async def strategy_service_error_handler(
        _request: Any, error: StrategyServiceError
    ) -> JSONResponse:
        return JSONResponse(
            status_code=error.http_status,
            content={"detail": {"code": error.code, "message": error.detail}},
        )

    @app.post("/api/v1/strategy-sessions", status_code=201)
    async def create_research_session(
        request: CreateResearchSessionRequest,
    ) -> dict[str, Any]:
        return service.create_research_session(request.context)

    @app.get("/api/v1/strategy-sessions/{session_id}")
    async def get_research_session(session_id: str) -> dict[str, Any]:
        return service.research_session_status(session_id)

    @app.post("/api/v1/strategy-sessions/{session_id}/messages")
    async def submit_research_message(
        session_id: str,
        request: SubmitResearchMessageRequest,
    ) -> dict[str, Any]:
        return await service.submit_research_message(session_id, request.message)

    @app.get("/api/v1/strategy-sessions/{session_id}/events")
    async def get_research_events(
        session_id: str,
        after_seq: int = Query(default=0, ge=0),
        limit: int = Query(default=200, ge=1, le=500),
    ) -> dict[str, Any]:
        observed = service.research_events_after(session_id, after_seq, limit=limit + 1)
        events = observed[:limit]
        next_after = events[-1].sequence if events else after_seq
        session = service.research_session_status(session_id)
        return {
            "events": [event.as_dict() for event in events],
            "next_after_seq": next_after,
            "has_more": len(observed) > limit,
            "terminal": session["status"] == "confirmed",
        }

    @app.post("/api/v1/strategy-sessions/{session_id}/confirm", status_code=201)
    async def confirm_strategy_run(
        session_id: str,
        request: ConfirmStrategyRunRequest,
    ) -> dict[str, Any]:
        return service.confirm_run(
            session_id,
            request.manifest,
            Path(request.input_workspace),
            request.confirmed_intent,
        )

    @app.post("/api/v1/strategy-runs", status_code=201)
    async def create_strategy_run(
        request: CreateStrategyRunRequest,
    ) -> dict[str, Any]:
        return service.confirm_run(
            request.session_id,
            request.manifest,
            Path(request.input_workspace),
            request.confirmed_intent,
        )

    @app.post("/api/v1/strategy-runs/{run_id}/start", status_code=202)
    async def start_strategy_run(run_id: str) -> dict[str, Any]:
        return await service.start_run(run_id)

    @app.post("/api/v1/strategy-runs/{run_id}/cancel")
    async def cancel_strategy_run(run_id: str) -> dict[str, Any]:
        return service.cancel_run(run_id)

    @app.get("/api/v1/strategy-runs/{run_id}")
    async def get_strategy_run(run_id: str) -> dict[str, Any]:
        return service.run_status(run_id)

    @app.get("/api/v1/strategy-runs/{run_id}/events")
    async def get_strategy_run_events(
        run_id: str,
        after_seq: int = Query(default=0, ge=0),
        limit: int = Query(default=200, ge=1, le=500),
    ) -> dict[str, Any]:
        observed = service.run_events_after(run_id, after_seq, limit=limit + 1)
        events = observed[:limit]
        next_after = events[-1].sequence if events else after_seq
        run = service.run_status(run_id)
        return {
            "events": [event.model_dump(mode="json") for event in events],
            "next_after_seq": next_after,
            "has_more": len(observed) > limit,
            "terminal": run["status"]
            in {"completed", "passed", "failed", "unsupported", "cancelled"},
        }

    @app.get("/api/v1/strategy-runs/{run_id}/artifacts")
    async def get_strategy_run_artifact(
        run_id: str,
        relative_path: str = Query(alias="path", min_length=1, max_length=500),
    ) -> dict[str, Any]:
        return service.artifact_content(run_id, relative_path)

    return app
