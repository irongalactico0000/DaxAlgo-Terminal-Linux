"""Headless session and run orchestration for the two native strategy lanes.

This module owns HTTP-facing lifecycle state, not strategy semantics.  Research is delegated to
an injected QueryEngine coordinator, while the two fixed native callbacks retain their genuine
VibeQuant/AKQuant and Point72 CSP results in :class:`NativeRunStore`.
"""

from __future__ import annotations

import asyncio
import base64
import copy
import inspect
import json
import os
import re
import secrets
import threading
from collections.abc import AsyncIterable, Awaitable, Callable, Iterable, Mapping
from dataclasses import asdict, dataclass, field, is_dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal

from pydantic import BaseModel

from .comparison import build_comparison_report
from .contracts import (
    FrozenRunManifest,
    NativeLaneResult,
    RunEvent,
    canonical_json_bytes,
    research_context_sha256,
    sha256_bytes,
    sha256_file,
)
from .run_store import NativeRunStore, RetainedRun, RunStoreError

FIXED_NATIVE_LANES: tuple[Literal["vibequant", "csp"], ...] = (
    "vibequant",
    "csp",
)

_SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$")

ResearchCoordinator = Callable[[str, str, Mapping[str, Any]], Any]
NativeRunner = Callable[
    [FrozenRunManifest, Path], NativeLaneResult | Awaitable[NativeLaneResult]
]


class StrategyServiceError(RuntimeError):
    """A stable, actionable lifecycle error for the loopback API."""

    def __init__(self, code: str, detail: str, *, http_status: int = 400) -> None:
        self.code = code
        self.detail = detail
        self.http_status = http_status
        super().__init__(f"{code}: {detail}")


@dataclass(frozen=True)
class ResearchEvent:
    sequence: int
    session_id: str
    stage: str
    status: Literal["started", "progress", "passed", "failed"]
    occurred_at_utc: datetime
    message: str
    details: dict[str, Any] = field(default_factory=dict)

    def as_dict(self) -> dict[str, Any]:
        return {
            "sequence": self.sequence,
            "session_id": self.session_id,
            "lane": "research",
            "stage": self.stage,
            "status": self.status,
            "occurred_at_utc": self.occurred_at_utc.isoformat(),
            "message": self.message,
            "details": copy.deepcopy(self.details),
        }


@dataclass
class _ResearchSession:
    session_id: str
    context: dict[str, Any]
    created_at_utc: datetime
    status: Literal["researching", "confirmed"] = "researching"
    confirmed_run_id: str | None = None
    messages: list[dict[str, Any]] = field(default_factory=list)
    events: list[ResearchEvent] = field(default_factory=list)
    message_lock: asyncio.Lock = field(default_factory=asyncio.Lock)


@dataclass
class _RunState:
    session_id: str
    retained: RetainedRun
    status: Literal[
        "confirmed",
        "running",
        "passed",
        "completed",
        "failed",
        "unsupported",
        "cancelled",
    ] = "confirmed"
    cancel_requested: threading.Event = field(default_factory=threading.Event)
    lane_states: dict[str, str] = field(
        default_factory=lambda: {lane: "pending" for lane in FIXED_NATIVE_LANES}
    )
    task: asyncio.Task[None] | None = None


class StrategyAgentService:
    """Coordinates research, immutable confirmation, and exactly two native lanes."""

    def __init__(
        self,
        *,
        store: NativeRunStore,
        research_coordinator: ResearchCoordinator,
        vibequant_runner: NativeRunner,
        csp_runner: NativeRunner,
        session_id_factory: Callable[[], str] | None = None,
        research_timeout_seconds: float = 180,
    ) -> None:
        callbacks = (research_coordinator, vibequant_runner, csp_runner)
        if not all(callable(callback) for callback in callbacks):
            raise TypeError("research and both fixed native callbacks must be callable")
        if (
            not isinstance(research_timeout_seconds, int | float)
            or isinstance(research_timeout_seconds, bool)
            or research_timeout_seconds <= 0
        ):
            raise ValueError("research_timeout_seconds must be positive")
        self._store = store
        self._research_coordinator = research_coordinator
        self._native_runners: dict[str, NativeRunner] = {
            "vibequant": vibequant_runner,
            "csp": csp_runner,
        }
        self._session_id_factory = session_id_factory or (
            lambda: f"session-{secrets.token_urlsafe(18)}"
        )
        self._research_timeout_seconds = float(research_timeout_seconds)
        self._sessions: dict[str, _ResearchSession] = {}
        self._runs: dict[str, _RunState] = {}
        self._state_lock = threading.RLock()

    def create_research_session(self, context: Mapping[str, Any]) -> dict[str, Any]:
        frozen_context = _json_copy(context, label="research context")
        session_id = self._session_id_factory()
        if not _SAFE_ID.fullmatch(session_id):
            raise StrategyServiceError(
                "invalid_session_id",
                "the configured session id factory returned an unsafe identifier",
                http_status=500,
            )
        with self._state_lock:
            if session_id in self._sessions:
                raise StrategyServiceError(
                    "duplicate_session_id",
                    f"research session already exists: {session_id}",
                    http_status=500,
                )
            session = _ResearchSession(
                session_id=session_id,
                context=frozen_context,
                created_at_utc=datetime.now(timezone.utc),
            )
            self._sessions[session_id] = session
            self._append_research_event(
                session,
                stage="session.created",
                status="passed",
                message="Research session created with frozen chart context.",
                details={"context": frozen_context},
            )
        return self.research_session_status(session_id)

    async def submit_research_message(
        self, session_id: str, message: str
    ) -> dict[str, Any]:
        session = self._get_session(session_id)
        if not isinstance(message, str) or not message.strip():
            raise StrategyServiceError("invalid_message", "message must not be empty")
        if len(message) > 100_000:
            raise StrategyServiceError(
                "invalid_message", "message exceeds 100000 characters"
            )

        async with session.message_lock:
            if session.status != "researching":
                raise StrategyServiceError(
                    "session_already_confirmed",
                    "research messages cannot change a confirmed strategy job",
                    http_status=409,
                )
            normalized_message = message.strip()
            session.messages.append(
                {
                    "role": "user",
                    "content": normalized_message,
                    "occurred_at_utc": datetime.now(timezone.utc).isoformat(),
                }
            )
            self._append_research_event(
                session,
                stage="message.submit",
                status="started",
                message="Message submitted to the injected research coordinator.",
                details={"message_index": len(session.messages)},
            )

            try:
                async with asyncio.timeout(self._research_timeout_seconds):
                    emissions = self._research_coordinator(
                        session.session_id,
                        normalized_message,
                        copy.deepcopy(session.context),
                    )
                    assistant_text: list[str] = []
                    async for emission in _iterate_emissions(emissions):
                        event_type, event_data = _normalize_coordinator_event(emission)
                        event_status: Literal["progress", "failed"] = (
                            "failed" if event_type == "error" else "progress"
                        )
                        self._append_research_event(
                            session,
                            stage=event_type,
                            status=event_status,
                            message=f"Research coordinator emitted {event_type}.",
                            details=event_data,
                        )
                        if event_type == "text_delta":
                            text = event_data.get("text")
                            if isinstance(text, str):
                                assistant_text.append(text)
                        if event_type == "error":
                            raise StrategyServiceError(
                                "research_coordinator_failed",
                                str(
                                    event_data.get("message")
                                    or "QueryEngine emitted an error"
                                ),
                                http_status=502,
                            )
                    if assistant_text:
                        session.messages.append(
                            {
                                "role": "assistant",
                                "content": "".join(assistant_text),
                                "occurred_at_utc": datetime.now(
                                    timezone.utc
                                ).isoformat(),
                            }
                        )
                    self._append_research_event(
                        session,
                        stage="message.complete",
                        status="passed",
                        message="Research coordinator completed the message.",
                    )
            except StrategyServiceError:
                raise
            except TimeoutError as exc:
                self._append_research_event(
                    session,
                    stage="message.coordinator_timeout",
                    status="failed",
                    message="Research coordinator exceeded its response deadline.",
                    details={"timeout_seconds": self._research_timeout_seconds},
                )
                raise StrategyServiceError(
                    "research_coordinator_timeout",
                    (
                        "research response exceeded "
                        f"{self._research_timeout_seconds:g} seconds"
                    ),
                    http_status=504,
                ) from exc
            except Exception as exc:
                detail = f"{type(exc).__name__}: {exc}"
                self._append_research_event(
                    session,
                    stage="message.coordinator",
                    status="failed",
                    message="Research coordinator failed.",
                    details={"error": detail},
                )
                raise StrategyServiceError(
                    "research_coordinator_failed",
                    detail,
                    http_status=502,
                ) from exc
        return self.research_session_status(session_id)

    def confirm_run(
        self,
        session_id: str,
        manifest: FrozenRunManifest,
        input_workspace: Path,
        confirmed_intent: Mapping[str, Any],
    ) -> dict[str, Any]:
        session = self._get_session(session_id)
        if session.message_lock.locked():
            raise StrategyServiceError(
                "research_in_progress",
                "wait for the active research response before confirming the strategy",
                http_status=409,
            )
        frozen_intent = _json_copy(confirmed_intent, label="confirmed intent")
        intent_payload = canonical_json_bytes(frozen_intent)
        intent_sha256 = sha256_bytes(intent_payload)
        if intent_sha256 != manifest.confirmed_intent_sha256:
            raise StrategyServiceError(
                "confirmed_intent_hash_mismatch",
                "manifest.confirmed_intent_sha256 does not bind the submitted readable strategy",
                http_status=409,
            )
        context_sha256 = research_context_sha256(session.context)
        if manifest.research_context_sha256 is None:
            raise StrategyServiceError(
                "research_context_unbound",
                "manifest.research_context_sha256 must bind the host-frozen chart context",
                http_status=409,
            )
        if context_sha256 != manifest.research_context_sha256:
            raise StrategyServiceError(
                "research_context_hash_mismatch",
                "manifest.research_context_sha256 does not bind this research session's frozen chart context",
                http_status=409,
            )
        with self._state_lock:
            if session.confirmed_run_id is not None:
                existing = self._runs[session.confirmed_run_id]
                if (
                    existing.retained.manifest.manifest_sha256
                    != manifest.manifest_sha256
                ):
                    raise StrategyServiceError(
                        "confirmation_is_immutable",
                        "this research session is already bound to a different manifest",
                        http_status=409,
                    )
                return self.run_status(existing.retained.manifest.run_id)
            if manifest.run_id in self._runs:
                raise StrategyServiceError(
                    "run_id_already_confirmed",
                    f"run id already belongs to another research session: {manifest.run_id}",
                    http_status=409,
                )

            try:
                retained = self._store.create_run(manifest, input_workspace)
            except (OSError, ValueError, RunStoreError) as exc:
                raise StrategyServiceError(
                    "confirmation_failed",
                    str(exc),
                    http_status=409,
                ) from exc

            transcript_path, transcript_sha256 = self._freeze_research_transcript(
                session, retained
            )
            intent_path = self._freeze_confirmed_intent(retained, intent_payload)
            run = _RunState(session_id=session_id, retained=retained)
            self._runs[manifest.run_id] = run
            session.status = "confirmed"
            session.confirmed_run_id = manifest.run_id
            self._append_research_event(
                session,
                stage="strategy.confirmed",
                status="passed",
                message="User confirmation created one immutable native run manifest.",
                details={
                    "run_id": manifest.run_id,
                    "manifest_sha256": manifest.manifest_sha256,
                    "transcript_sha256": transcript_sha256,
                    "confirmed_intent_relative_path": intent_path,
                    "confirmed_intent_sha256": intent_sha256,
                    "research_context_sha256": context_sha256,
                },
            )
            self._store.append_event(
                manifest.run_id,
                lane="research",
                stage="confirmation",
                status="passed",
                message="Research strategy and frozen run manifest were confirmed.",
                details={
                    "session_id": session_id,
                    "manifest_sha256": manifest.manifest_sha256,
                    "transcript_relative_path": transcript_path,
                    "transcript_sha256": transcript_sha256,
                    "confirmed_intent_relative_path": intent_path,
                    "confirmed_intent_sha256": intent_sha256,
                    "research_context_sha256": context_sha256,
                },
            )
        return self.run_status(manifest.run_id)

    async def start_run(self, run_id: str) -> dict[str, Any]:
        run = self._get_run(run_id)
        with self._state_lock:
            if run.status != "confirmed":
                return self.run_status(run_id)
            if run.cancel_requested.is_set():
                self._cancel_before_dispatch(run)
                return self.run_status(run_id)
            run.status = "running"
            self._store.append_event(
                run_id,
                lane="comparison",
                stage="dispatch",
                status="started",
                message="Dispatching the same frozen manifest to VibeQuant and CSP.",
                details={"lanes": list(FIXED_NATIVE_LANES)},
            )
            run.task = asyncio.create_task(
                self._execute_native_lanes(run),
                name=f"daxalgo-native-{run_id}",
            )
        return self.run_status(run_id)

    def cancel_run(self, run_id: str) -> dict[str, Any]:
        run = self._get_run(run_id)
        with self._state_lock:
            if run.status in {
                "passed",
                "completed",
                "failed",
                "unsupported",
                "cancelled",
            }:
                return self.run_status(run_id)
            first_request = not run.cancel_requested.is_set()
            run.cancel_requested.set()
            if run.status == "confirmed":
                self._cancel_before_dispatch(run)
            elif first_request:
                self._store.append_event(
                    run_id,
                    lane="comparison",
                    stage="cancellation_requested",
                    status="progress",
                    message=(
                        "Cancellation was requested. Already-running native callbacks are allowed "
                        "to report their real terminal result."
                    ),
                )
        return self.run_status(run_id)

    async def wait_for_run(
        self, run_id: str, *, timeout: float | None = None
    ) -> dict[str, Any]:
        run = self._get_run(run_id)
        task = run.task
        if task is not None:
            try:
                await asyncio.wait_for(asyncio.shield(task), timeout=timeout)
            except TimeoutError as exc:
                raise StrategyServiceError(
                    "run_wait_timeout",
                    f"run did not finish within {timeout} seconds",
                    http_status=408,
                ) from exc
        return self.run_status(run_id)

    def research_session_status(self, session_id: str) -> dict[str, Any]:
        session = self._get_session(session_id)
        return {
            "session_id": session.session_id,
            "status": session.status,
            "created_at_utc": session.created_at_utc.isoformat(),
            "confirmed_run_id": session.confirmed_run_id,
            "message_count": len(session.messages),
            "last_event_sequence": len(session.events),
            "context": copy.deepcopy(session.context),
        }

    def research_events_after(
        self,
        session_id: str,
        after_sequence: int = 0,
        *,
        limit: int | None = None,
    ) -> tuple[ResearchEvent, ...]:
        if after_sequence < 0:
            raise StrategyServiceError(
                "invalid_event_cursor", "after_sequence must be non-negative"
            )
        session = self._get_session(session_id)
        events = tuple(
            event for event in session.events if event.sequence > after_sequence
        )
        return events[:limit] if limit is not None else events

    def run_events_after(
        self,
        run_id: str,
        after_sequence: int = 0,
        *,
        limit: int | None = None,
    ) -> tuple[RunEvent, ...]:
        if after_sequence < 0:
            raise StrategyServiceError(
                "invalid_event_cursor", "after_sequence must be non-negative"
            )
        self._get_run(run_id)
        try:
            return self._store.events_after(run_id, after_sequence, limit=limit)
        except RunStoreError as exc:
            raise StrategyServiceError(
                "retained_events_unavailable", str(exc), http_status=500
            ) from exc

    def run_status(self, run_id: str) -> dict[str, Any]:
        run = self._get_run(run_id)
        try:
            retained = self._store.load_run(run_id)
        except (OSError, ValueError, RunStoreError) as exc:
            raise StrategyServiceError(
                "retained_run_unavailable", str(exc), http_status=500
            ) from exc
        events = retained.events
        comparison_report = self._store.load_comparison(run_id)
        comparison = None
        if comparison_report is not None:
            comparison_path = retained.workspace / "comparison" / "report.json"
            comparison = {
                "relative_path": "comparison/report.json",
                "sha256": sha256_file(comparison_path),
                "report": comparison_report,
            }
        return {
            "run_id": run_id,
            "session_id": run.session_id,
            "manifest_sha256": retained.manifest.manifest_sha256,
            "status": run.status,
            "cancel_requested": run.cancel_requested.is_set(),
            "fixed_lanes": list(FIXED_NATIVE_LANES),
            "lane_states": dict(run.lane_states),
            "results": {
                lane: result.model_dump(mode="json", exclude_none=True)
                for lane, result in sorted(retained.results.items())
            },
            "comparison": comparison,
            "evidence_status": (
                comparison_report.get("evidence_status")
                if comparison_report is not None
                else None
            ),
            "last_event_sequence": events[-1].sequence if events else 0,
        }

    def artifact_content(self, run_id: str, relative_path: str) -> dict[str, Any]:
        """Return a declared retained artifact after rechecking its custody hash."""

        self._get_run(run_id)
        try:
            retained = self._store.load_run(run_id)
            declared_hashes = self._declared_artifact_hashes(retained)
        except (OSError, ValueError, RunStoreError) as exc:
            raise StrategyServiceError(
                "retained_artifact_unavailable", str(exc), http_status=500
            ) from exc
        if relative_path not in declared_hashes:
            raise StrategyServiceError(
                "artifact_not_declared",
                f"path is not a declared inspectable artifact: {relative_path}",
                http_status=404,
            )
        try:
            candidate = (retained.workspace / relative_path).resolve(strict=True)
        except (OSError, ValueError) as exc:
            raise StrategyServiceError(
                "retained_artifact_unavailable", str(exc), http_status=500
            ) from exc
        root = retained.workspace.resolve(strict=True)
        if not candidate.is_relative_to(root) or not candidate.is_file():
            raise StrategyServiceError(
                "retained_artifact_unavailable",
                "declared artifact escapes the retained run workspace",
                http_status=500,
            )
        if candidate.stat().st_size > 5 * 1024 * 1024:
            raise StrategyServiceError(
                "artifact_too_large",
                "artifact exceeds the 5 MiB inspection response limit",
                http_status=413,
            )
        payload = candidate.read_bytes()
        observed_sha256 = sha256_bytes(payload)
        if observed_sha256 != declared_hashes[relative_path]:
            raise StrategyServiceError(
                "retained_artifact_hash_mismatch",
                f"retained artifact hash changed: {relative_path}",
                http_status=500,
            )
        try:
            content = payload.decode("utf-8")
            encoding = "utf-8"
        except UnicodeDecodeError:
            content = base64.b64encode(payload).decode("ascii")
            encoding = "base64"
        return {
            "run_id": run_id,
            "relative_path": relative_path,
            "sha256": observed_sha256,
            "size_bytes": len(payload),
            "encoding": encoding,
            "content": content,
        }

    async def _execute_native_lanes(self, run: _RunState) -> None:
        await asyncio.gather(
            *(self._execute_native_lane(run, lane) for lane in FIXED_NATIVE_LANES)
        )
        comparison_error: str | None = None
        evidence_status: str | None = None
        comparison_details: dict[str, Any] = {}
        try:
            retained = self._store.load_run(run.retained.manifest.run_id)
            missing = set(FIXED_NATIVE_LANES) - set(retained.results)
            if missing:
                raise RunStoreError(
                    "comparison requires retained results for: "
                    + ", ".join(sorted(missing))
                )
            intent_path = retained.workspace / "research" / "confirmed-intent.json"
            confirmed_intent = json.loads(intent_path.read_text(encoding="utf-8"))
            if not isinstance(confirmed_intent, dict):
                raise ValueError("retained confirmed intent must be a JSON object")
            report = build_comparison_report(
                retained.manifest,
                confirmed_intent,
                retained.results["vibequant"],
                retained.results["csp"],
            )
            relative_path, payload_sha256, stored_report = (
                self._store.retain_comparison(
                    retained.manifest.run_id,
                    report,
                )
            )
            evidence_status = str(stored_report["evidence_status"])
            comparison_details = {
                "relative_path": relative_path,
                "sha256": payload_sha256,
                "report_hash": stored_report["report_hash"],
                "evidence_status": evidence_status,
            }
            self._store.append_event(
                retained.manifest.run_id,
                lane="comparison",
                stage="evidence_report",
                status="failed" if evidence_status == "failed" else "passed",
                message=(
                    "Retained an exact comparison of native evidence; unproven observations "
                    "remain explicitly unproven."
                ),
                details=comparison_details,
            )
        except Exception as exc:
            comparison_error = f"{type(exc).__name__}: {exc}"
            try:
                self._store.append_event(
                    run.retained.manifest.run_id,
                    lane="comparison",
                    stage="evidence_report",
                    status="failed",
                    message="Could not build or retain the native evidence comparison.",
                    details={"error": comparison_error},
                )
            except Exception:
                pass

        statuses = tuple(run.lane_states[lane] for lane in FIXED_NATIVE_LANES)
        if run.cancel_requested.is_set():
            final_status = "cancelled"
            event_status = "cancelled"
        elif (
            comparison_error is not None
            or evidence_status == "failed"
            or "failed" in statuses
        ):
            final_status = "failed"
            event_status = "failed"
        elif "unsupported" in statuses:
            final_status = "unsupported"
            event_status = "unsupported"
        else:
            final_status = "completed"
            event_status = "passed"
        with self._state_lock:
            run.status = final_status  # type: ignore[assignment]
            self._store.append_event(
                run.retained.manifest.run_id,
                lane="comparison",
                stage="workflow_completion",
                status=event_status,
                message=(
                    "Both native lanes and the evidence comparison reached a terminal result."
                ),
                details={
                    "lane_statuses": dict(run.lane_states),
                    "evidence_status": evidence_status,
                    "comparison": comparison_details,
                    **(
                        {"comparison_error": comparison_error}
                        if comparison_error is not None
                        else {}
                    ),
                },
            )

    async def _execute_native_lane(self, run: _RunState, lane: str) -> None:
        manifest = run.retained.manifest
        if run.cancel_requested.is_set():
            result = self._cancelled_result(manifest, lane, "dispatch")
            self._retain_lane_terminal(run, result)
            return

        run.lane_states[lane] = "running"
        self._store.append_event(
            manifest.run_id,
            lane=lane,
            stage="native_callback",
            status="started",
            message=f"Starting the injected {lane} native callback.",
        )
        runner = self._native_runners[lane]
        try:
            if inspect.iscoroutinefunction(runner):
                observed = await runner(manifest, run.retained.workspace)
            else:
                observed = await asyncio.to_thread(
                    runner, manifest, run.retained.workspace
                )
                if inspect.isawaitable(observed):
                    observed = await observed
        except Exception as exc:
            result = self._failed_result(
                manifest,
                lane,
                "native_callback",
                f"{type(exc).__name__}: {exc}",
            )
        else:
            try:
                result = self._verify_native_result(
                    manifest,
                    lane,
                    observed,
                    run.retained.workspace,
                )
            except Exception as exc:
                result = self._failed_result(
                    manifest,
                    lane,
                    "result_contract",
                    f"{type(exc).__name__}: {exc}",
                )
        try:
            self._retain_lane_terminal(run, result)
        except Exception as exc:
            run.lane_states[lane] = "failed"
            try:
                self._store.append_event(
                    manifest.run_id,
                    lane=lane,
                    stage="result_retention",
                    status="failed",
                    message=f"Could not retain the {lane} native terminal result.",
                    details={"error": f"{type(exc).__name__}: {exc}"},
                )
            except Exception:
                # The retained event store itself is unavailable; run_status will expose that
                # storage failure instead of falsely reporting native completion.
                pass

    def _retain_lane_terminal(self, run: _RunState, result: NativeLaneResult) -> None:
        self._store.retain_result(result)
        run.lane_states[result.lane] = result.status
        details: dict[str, Any] = {
            "framework": result.framework,
            "framework_version": result.framework_version,
            "artifact_relative_paths": list(result.artifact_relative_paths),
            "artifact_sha256": result.artifact_sha256,
            "observations": result.observations,
        }
        if result.error:
            details["error"] = result.error
        self._store.append_event(
            result.run_id,
            lane=result.lane,
            stage=result.native_stage,
            status=result.status,
            message=f"{result.framework} reported {result.status} at {result.native_stage}.",
            details=details,
        )

    def _verify_native_result(
        self,
        manifest: FrozenRunManifest,
        lane: str,
        observed: Any,
        workspace: Path,
    ) -> NativeLaneResult:
        if not isinstance(observed, NativeLaneResult):
            raise TypeError("native callback must return NativeLaneResult")
        if observed.run_id != manifest.run_id:
            raise ValueError(
                f"native result run_id mismatch: expected {manifest.run_id}, observed {observed.run_id}"
            )
        if observed.lane != lane:
            raise ValueError(
                f"native result lane mismatch: expected {lane}, observed {observed.lane}"
            )
        if observed.manifest_sha256 != manifest.manifest_sha256:
            raise ValueError(
                "native result manifest_sha256 does not match the frozen handoff"
            )
        workspace_root = workspace.resolve(strict=True)
        for relative_path, expected_sha256 in observed.artifact_sha256.items():
            artifact = (workspace_root / relative_path).resolve(strict=True)
            if not artifact.is_relative_to(workspace_root):
                raise ValueError(
                    f"native artifact escapes retained workspace: {relative_path}"
                )
            actual_sha256 = sha256_file(artifact)
            if actual_sha256 != expected_sha256:
                raise ValueError(
                    "native artifact hash mismatch for "
                    f"{relative_path}: expected {expected_sha256}, observed {actual_sha256}"
                )
        return observed

    def _cancel_before_dispatch(self, run: _RunState) -> None:
        if run.status == "cancelled":
            return
        for lane in FIXED_NATIVE_LANES:
            if run.lane_states[lane] == "pending":
                self._retain_lane_terminal(
                    run,
                    self._cancelled_result(run.retained.manifest, lane, "dispatch"),
                )
        run.status = "cancelled"
        self._store.append_event(
            run.retained.manifest.run_id,
            lane="comparison",
            stage="dispatch",
            status="cancelled",
            message="Run was cancelled before either native callback started.",
        )

    def _cancelled_result(
        self, manifest: FrozenRunManifest, lane: str, stage: str
    ) -> NativeLaneResult:
        framework, version = self._framework_identity(manifest, lane)
        return NativeLaneResult(
            run_id=manifest.run_id,
            lane=lane,  # type: ignore[arg-type]
            manifest_sha256=manifest.manifest_sha256,
            status="cancelled",
            native_stage=stage,
            framework=framework,
            framework_version=version,
            observations={"native_callback_started": False},
        )

    def _failed_result(
        self, manifest: FrozenRunManifest, lane: str, stage: str, error: str
    ) -> NativeLaneResult:
        framework, version = self._framework_identity(manifest, lane)
        return NativeLaneResult(
            run_id=manifest.run_id,
            lane=lane,  # type: ignore[arg-type]
            manifest_sha256=manifest.manifest_sha256,
            status="failed",
            native_stage=stage,
            framework=framework,
            framework_version=version,
            error=error[:8000],
        )

    @staticmethod
    def _framework_identity(manifest: FrozenRunManifest, lane: str) -> tuple[str, str]:
        component = "vibequant" if lane == "vibequant" else "csp"
        pin = next(item for item in manifest.components if item.component == component)
        framework = "transcend-0/VibeQuant" if lane == "vibequant" else "Point72 CSP"
        return framework, pin.version

    def _get_session(self, session_id: str) -> _ResearchSession:
        with self._state_lock:
            session = self._sessions.get(session_id)
        if session is None:
            raise StrategyServiceError(
                "research_session_not_found",
                f"research session does not exist: {session_id}",
                http_status=404,
            )
        return session

    def _get_run(self, run_id: str) -> _RunState:
        with self._state_lock:
            run = self._runs.get(run_id)
        if run is not None:
            return run
        try:
            exists = self._store.run_exists(run_id)
        except RunStoreError as exc:
            raise StrategyServiceError(
                "run_not_found", f"run does not exist: {run_id}", http_status=404
            ) from exc
        if not exists:
            raise StrategyServiceError(
                "run_not_found", f"run does not exist: {run_id}", http_status=404
            )
        try:
            retained = self._store.load_run(run_id)
            recovered = self._recover_run_state(retained)
        except (OSError, ValueError, RunStoreError) as exc:
            raise StrategyServiceError(
                "retained_run_unavailable", str(exc), http_status=500
            ) from exc
        with self._state_lock:
            return self._runs.setdefault(run_id, recovered)

    def _recover_run_state(self, retained: RetainedRun) -> _RunState:
        confirmation = next(
            (event for event in retained.events if event.stage == "confirmation"),
            None,
        )
        session_id = (
            str(confirmation.details.get("session_id"))
            if confirmation is not None
            and isinstance(confirmation.details.get("session_id"), str)
            else "recovered-session"
        )
        lane_states = {
            lane: (
                retained.results[lane].status if lane in retained.results else "pending"
            )
            for lane in FIXED_NATIVE_LANES
        }
        completion = next(
            (
                event
                for event in reversed(retained.events)
                if event.stage == "workflow_completion"
            ),
            None,
        )
        cancellation_requested = any(
            event.stage == "cancellation_requested" or event.status == "cancelled"
            for event in retained.events
        )
        if completion is not None:
            recovered_status = {
                "passed": "completed",
                "failed": "failed",
                "unsupported": "unsupported",
                "cancelled": "cancelled",
            }.get(completion.status, "failed")
        elif any(event.stage == "dispatch" for event in retained.events):
            recovered_status = "failed"
        else:
            recovered_status = "confirmed"
        recovered = _RunState(
            session_id=session_id,
            retained=retained,
            status=recovered_status,  # type: ignore[arg-type]
            lane_states=lane_states,
        )
        if cancellation_requested:
            recovered.cancel_requested.set()
        return recovered

    def _declared_artifact_hashes(self, retained: RetainedRun) -> dict[str, str]:
        declared: dict[str, str] = {}
        for result in retained.results.values():
            declared.update(result.artifact_sha256)
        confirmation = next(
            (event for event in retained.events if event.stage == "confirmation"),
            None,
        )
        if confirmation is not None:
            for path_key, hash_key in (
                ("transcript_relative_path", "transcript_sha256"),
                ("confirmed_intent_relative_path", "confirmed_intent_sha256"),
            ):
                path = confirmation.details.get(path_key)
                digest = confirmation.details.get(hash_key)
                if isinstance(path, str) and isinstance(digest, str):
                    declared[path] = digest
        comparison = self._store.load_comparison(retained.manifest.run_id)
        if comparison is not None:
            comparison_path = retained.workspace / "comparison" / "report.json"
            declared["comparison/report.json"] = sha256_file(comparison_path)
        return declared

    @staticmethod
    def _append_research_event(
        session: _ResearchSession,
        *,
        stage: str,
        status: Literal["started", "progress", "passed", "failed"],
        message: str,
        details: Mapping[str, Any] | None = None,
    ) -> ResearchEvent:
        safe_details = _json_copy(details or {}, label="research event details")
        event = ResearchEvent(
            sequence=len(session.events) + 1,
            session_id=session.session_id,
            stage=stage,
            status=status,
            occurred_at_utc=datetime.now(timezone.utc),
            message=message,
            details=safe_details,
        )
        session.events.append(event)
        return event

    @staticmethod
    def _freeze_research_transcript(
        session: _ResearchSession, retained: RetainedRun
    ) -> tuple[str, str]:
        relative_path = "research/transcript.json"
        destination = retained.workspace / relative_path
        payload = canonical_json_bytes(
            {
                "schema_version": "daxalgo-research-transcript/v1",
                "session_id": session.session_id,
                "context": session.context,
                "messages": session.messages,
                "events": [event.as_dict() for event in session.events],
            }
        )
        digest = sha256_bytes(payload)
        destination.parent.mkdir(parents=True, exist_ok=True)
        if destination.exists():
            if destination.read_bytes() != payload:
                raise StrategyServiceError(
                    "confirmation_is_immutable",
                    "a different retained research transcript already exists",
                    http_status=409,
                )
        else:
            with destination.open("xb") as stream:
                stream.write(payload)
                stream.flush()
                os.fsync(stream.fileno())
        return relative_path, digest

    @staticmethod
    def _freeze_confirmed_intent(retained: RetainedRun, payload: bytes) -> str:
        relative_path = "research/confirmed-intent.json"
        destination = retained.workspace / relative_path
        if destination.exists():
            if destination.read_bytes() != payload:
                raise StrategyServiceError(
                    "confirmation_is_immutable",
                    "a different readable strategy is already retained",
                    http_status=409,
                )
            return relative_path
        with destination.open("xb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        return relative_path


async def _iterate_emissions(value: Any) -> AsyncIterable[Any]:
    if inspect.isawaitable(value):
        value = await value
    if isinstance(value, AsyncIterable):
        async for item in value:
            yield item
        return
    if value is None:
        return
    if isinstance(value, Iterable) and not isinstance(
        value, (str, bytes, bytearray, Mapping)
    ):
        for item in value:
            yield item
        return
    yield value


def _normalize_coordinator_event(value: Any) -> tuple[str, dict[str, Any]]:
    if isinstance(value, Mapping):
        event_type = value.get("type", "event")
        event_data = value.get("data", value)
    else:
        event_type = getattr(value, "type", None)
        event_data = getattr(value, "data", None)
        if event_type is None or event_data is None:
            if isinstance(value, BaseModel):
                event_type = type(value).__name__
                event_data = value.model_dump(mode="json")
            elif is_dataclass(value):
                event_type = type(value).__name__
                event_data = asdict(value)
            else:
                raise TypeError(
                    "research coordinator emissions must expose type and data"
                )
    if not isinstance(event_type, str) or not event_type.strip():
        raise TypeError("research coordinator event type must be a non-empty string")
    if len(event_type) > 100:
        raise ValueError("research coordinator event type exceeds 100 characters")
    if not isinstance(event_data, Mapping):
        raise TypeError("research coordinator event data must be a JSON object")
    return event_type.strip(), _json_copy(
        event_data, label="research coordinator event"
    )


def _json_copy(value: Mapping[str, Any], *, label: str) -> dict[str, Any]:
    if not isinstance(value, Mapping):
        raise StrategyServiceError("invalid_json_payload", f"{label} must be an object")
    try:
        encoded = json.dumps(
            dict(value),
            allow_nan=False,
            ensure_ascii=False,
            separators=(",", ":"),
            sort_keys=True,
        )
        decoded = json.loads(encoded)
    except (TypeError, ValueError) as exc:
        raise StrategyServiceError(
            "invalid_json_payload", f"{label} is not finite JSON: {exc}"
        ) from exc
    if not isinstance(decoded, dict):
        raise StrategyServiceError("invalid_json_payload", f"{label} must be an object")
    return decoded
