"""Production bridge from two service callbacks to one FinanceManus dispatch.

The service deliberately exposes one callback per native lane.  This adapter joins those two
callbacks back into one host-owned :class:`CoordinatorDispatch`: both workers receive the exact
same canonical confirmed job, while each worker gets its own copy of the manifest-bound inputs and
its own fixed one-tool QueryEngine profile.  Native frameworks remain responsible for interpreting
and executing their artifacts; this module adds no strategy language, validator, or simulator.
"""

from __future__ import annotations

import asyncio
import json
import shutil
import threading
from collections.abc import Awaitable, Callable, Mapping
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .contracts import (
    FrozenRunManifest,
    NativeLaneResult,
    canonical_json_bytes,
    confirmed_intent_sha256,
    sha256_bytes,
    sha256_file,
)
from .profiles import (
    make_csp_profile,
    make_csp_submission_tool_factory,
    make_vibequant_profile,
    make_vibequant_submission_tool_factory,
)
from .queryengine_runtime import (
    DEFAULT_COORDINATOR_TIMEOUT_SECONDS,
    CoordinatorDispatch,
    CoordinatorWorkerOutcome,
    FinanceManusBindings,
    WorkerProfile,
)


ConfigFactory = Callable[[], Any]
NativeRunner = Callable[..., NativeLaneResult | Awaitable[NativeLaneResult]]
CoordinatorEventSink = Callable[[str, str, WorkerProfile, Any], None | Awaitable[None]]

_LANE_ORDER = (WorkerProfile.VIBEQUANT, WorkerProfile.CSP)
_LANE_TOOL_NAMES = {
    WorkerProfile.VIBEQUANT: "submit_vibequant_task_spec",
    WorkerProfile.CSP: "submit_csp_source",
}
_LANE_FRAMEWORKS = {
    WorkerProfile.VIBEQUANT: ("transcend-0/VibeQuant", "vibequant"),
    WorkerProfile.CSP: ("Point72 CSP", "csp"),
}


@dataclass
class _SharedRun:
    manifest: FrozenRunManifest
    retained_root: Path
    payload: bytes
    run_hash: str
    lane_workspaces: dict[WorkerProfile, Path]
    query_engine_identity: dict[str, Any]
    dispatch: CoordinatorDispatch | None = None
    dispatch_task: asyncio.Task[None] | None = None
    event_loop: asyncio.AbstractEventLoop | None = None
    results: dict[WorkerProfile, NativeLaneResult] = field(default_factory=dict)
    capture_errors: dict[WorkerProfile, str] = field(default_factory=dict)
    result_lock: threading.Lock = field(default_factory=threading.Lock)


class CoordinatedNativeRunners:
    """Expose two service runners backed by exactly one two-worker dispatch per run.

    Pass :meth:`vibequant` and :meth:`csp` directly to ``StrategyAgentService``.  The first callback
    prepares the shared dispatch and starts it; the concurrently invoked second callback awaits the
    same task.  A worker that completes without invoking its only native submission tool receives a
    concrete failed ``NativeLaneResult`` instead of inferred or fabricated native evidence.
    """

    def __init__(
        self,
        *,
        bindings: FinanceManusBindings,
        vibequant_config_factory: ConfigFactory,
        csp_config_factory: ConfigFactory,
        vibequant_native_runner: NativeRunner,
        csp_native_runner: NativeRunner,
        event_sink: CoordinatorEventSink,
        query_engine_identity: dict[str, Any],
        timeout_seconds: int = DEFAULT_COORDINATOR_TIMEOUT_SECONDS,
    ) -> None:
        callables = (
            vibequant_config_factory,
            csp_config_factory,
            vibequant_native_runner,
            csp_native_runner,
            event_sink,
        )
        if not all(callable(item) for item in callables):
            raise TypeError(
                "config factories, native runners, and event_sink must be callable"
            )
        if type(timeout_seconds) is not int or timeout_seconds < 1:
            raise ValueError("timeout_seconds must be a positive integer")
        self._bindings = bindings
        self._config_factories = {
            WorkerProfile.VIBEQUANT: vibequant_config_factory,
            WorkerProfile.CSP: csp_config_factory,
        }
        self._native_runners = {
            WorkerProfile.VIBEQUANT: vibequant_native_runner,
            WorkerProfile.CSP: csp_native_runner,
        }
        self._event_sink = event_sink
        self._query_engine_identity = _finite_json_object(
            query_engine_identity, label="query_engine_identity"
        )
        self._timeout_seconds = timeout_seconds
        self._runs: dict[str, _SharedRun] = {}
        self._state_lock = threading.RLock()

    async def vibequant(
        self, manifest: FrozenRunManifest, retained_workspace: Path
    ) -> NativeLaneResult:
        """Service callback for the genuine VibeQuant/AKQuant lane."""

        return await self._run_lane(
            WorkerProfile.VIBEQUANT, manifest, retained_workspace
        )

    async def csp(
        self, manifest: FrozenRunManifest, retained_workspace: Path
    ) -> NativeLaneResult:
        """Service callback for the genuine Point72 CSP lane."""

        return await self._run_lane(WorkerProfile.CSP, manifest, retained_workspace)

    async def _run_lane(
        self,
        profile: WorkerProfile,
        manifest: FrozenRunManifest,
        retained_workspace: Path,
    ) -> NativeLaneResult:
        shared = self._get_or_create_run(manifest, retained_workspace)
        loop = asyncio.get_running_loop()
        with self._state_lock:
            if shared.event_loop is not None and shared.event_loop is not loop:
                raise RuntimeError(
                    "both native service callbacks must execute on the same event loop"
                )
            if shared.dispatch_task is None:
                shared.event_loop = loop
                shared.dispatch_task = loop.create_task(self._execute_dispatch(shared))
            task = shared.dispatch_task

        await asyncio.shield(task)
        with shared.result_lock:
            return shared.results[profile]

    def _get_or_create_run(
        self, manifest: FrozenRunManifest, retained_workspace: Path
    ) -> _SharedRun:
        retained_root = retained_workspace.expanduser().resolve(strict=True)
        if not retained_root.is_dir():
            raise ValueError(f"retained workspace is not a directory: {retained_root}")
        manifest.verify_workspace_files(retained_root)
        with self._state_lock:
            existing = self._runs.get(manifest.run_id)
            if existing is not None:
                if existing.manifest.manifest_sha256 != manifest.manifest_sha256:
                    raise ValueError(
                        f"run_id {manifest.run_id!r} is already bound to another manifest"
                    )
                if existing.retained_root != retained_root:
                    raise ValueError(
                        f"run_id {manifest.run_id!r} is already bound to another retained workspace"
                    )
                return existing

            shared = self._prepare_run(manifest, retained_root)
            self._runs[manifest.run_id] = shared
            return shared

    def _prepare_run(
        self, manifest: FrozenRunManifest, retained_root: Path
    ) -> _SharedRun:
        query_engine_pin = next(
            item for item in manifest.components if item.component == "query_engine"
        )
        observed_revision = self._bindings.source.revision
        if query_engine_pin.source_revision != observed_revision:
            raise ValueError(
                "manifest query_engine revision does not match the loaded FinanceManus "
                f"runtime: expected {observed_revision}, observed "
                f"{query_engine_pin.source_revision}"
            )
        confirmed_intent = _read_confirmed_intent(retained_root, manifest)
        payload = canonical_json_bytes(
            {
                "confirmed_intent": confirmed_intent,
                "confirmed_intent_sha256": manifest.confirmed_intent_sha256,
                "manifest": manifest.model_dump(mode="json", exclude_none=True),
                "manifest_sha256": manifest.manifest_sha256,
                "query_engine_runtime": self._query_engine_identity,
            }
        )
        run_hash = sha256_bytes(payload)
        _write_once_or_verify(
            retained_root / "coordinator" / "confirmed-job.json", payload
        )
        lane_workspaces = _copy_lane_inputs(retained_root, manifest)
        shared = _SharedRun(
            manifest=manifest,
            retained_root=retained_root,
            payload=payload,
            run_hash=run_hash,
            lane_workspaces=lane_workspaces,
            query_engine_identity=self._query_engine_identity,
        )

        vibequant_tool = make_vibequant_submission_tool_factory(
            manifest=manifest,
            workspace=lane_workspaces[WorkerProfile.VIBEQUANT],
            native_runner=self._native_runners[WorkerProfile.VIBEQUANT],
            result_sink=lambda result: self._capture_result(
                shared, WorkerProfile.VIBEQUANT, result
            ),
        )
        csp_tool = make_csp_submission_tool_factory(
            manifest=manifest,
            workspace=lane_workspaces[WorkerProfile.CSP],
            native_runner=self._native_runners[WorkerProfile.CSP],
            result_sink=lambda result: self._capture_result(
                shared, WorkerProfile.CSP, result
            ),
        )
        profiles = {
            WorkerProfile.VIBEQUANT: make_vibequant_profile(
                config_factory=self._config_factories[WorkerProfile.VIBEQUANT],
                submission_tool_factory=vibequant_tool,
            ),
            WorkerProfile.CSP: make_csp_profile(
                config_factory=self._config_factories[WorkerProfile.CSP],
                submission_tool_factory=csp_tool,
            ),
        }
        shared.dispatch = CoordinatorDispatch(
            self._bindings,
            run_hash=run_hash,
            payload=payload,
            profiles=profiles,
            workspaces=lane_workspaces,
            event_sink=lambda observed_run_hash, profile, event: self._event_sink(
                manifest.run_id, observed_run_hash, profile, event
            ),
        )
        return shared

    async def _execute_dispatch(self, shared: _SharedRun) -> None:
        dispatch_error: str | None = None
        worker_outcomes: Mapping[WorkerProfile, CoordinatorWorkerOutcome] = {}
        try:
            if (
                shared.dispatch is None
            ):  # defensive: construction finishes before publication
                raise RuntimeError("CoordinatorDispatch was not constructed")
            await shared.dispatch.run(timeout_seconds=self._timeout_seconds)
            observed_outcomes = getattr(shared.dispatch, "worker_outcomes", {})
            if isinstance(observed_outcomes, Mapping):
                worker_outcomes = observed_outcomes
        except Exception as exc:
            dispatch_error = f"{type(exc).__name__}: {exc}"
        finally:
            with shared.result_lock:
                for profile in _LANE_ORDER:
                    if profile not in shared.results:
                        shared.results[profile] = _missing_submission_result(
                            shared,
                            profile,
                            dispatch_error=dispatch_error,
                            capture_error=shared.capture_errors.get(profile),
                            worker_outcome=worker_outcomes.get(profile),
                            timeout_seconds=self._timeout_seconds,
                        )

    def _capture_result(
        self,
        shared: _SharedRun,
        profile: WorkerProfile,
        result: NativeLaneResult,
    ) -> None:
        try:
            rebased = _rebase_result(shared, profile, result)
        except Exception as exc:
            with shared.result_lock:
                shared.capture_errors.setdefault(
                    profile, f"{type(exc).__name__}: {exc}"
                )
            raise
        with shared.result_lock:
            if profile in shared.results:
                error = f"{profile.value} submitted more than one NativeLaneResult"
                shared.capture_errors.setdefault(profile, f"ValueError: {error}")
                raise ValueError(error)
            shared.results[profile] = rebased

    @property
    def prepared_run_count(self) -> int:
        """Number of immutable run dispatches prepared by this adapter."""

        with self._state_lock:
            return len(self._runs)


def _read_confirmed_intent(
    retained_root: Path, manifest: FrozenRunManifest
) -> dict[str, Any]:
    path = retained_root / "research" / "confirmed-intent.json"
    try:
        raw = path.read_bytes()
    except OSError as exc:
        raise ValueError(f"confirmed intent is unavailable at {path}: {exc}") from exc
    if len(raw) > 8 * 1024 * 1024:
        raise ValueError("confirmed intent exceeds 8388608 bytes")
    try:
        parsed = json.loads(raw)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"confirmed intent is not valid UTF-8 JSON: {exc}") from exc
    if not isinstance(parsed, dict):
        raise ValueError("confirmed intent must be a JSON object")
    observed = confirmed_intent_sha256(parsed)
    if observed != manifest.confirmed_intent_sha256:
        raise ValueError(
            "confirmed intent hash does not match manifest: "
            f"expected {manifest.confirmed_intent_sha256}, observed {observed}"
        )
    return parsed


def _write_once_or_verify(path: Path, payload: bytes) -> None:
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    try:
        with path.open("xb") as stream:
            stream.write(payload)
            stream.flush()
    except FileExistsError:
        if path.read_bytes() != payload:
            raise ValueError(f"retained coordinator payload already differs: {path}")


def _copy_lane_inputs(
    retained_root: Path, manifest: FrozenRunManifest
) -> dict[WorkerProfile, Path]:
    lanes_root = retained_root / "lanes"
    staging_root = retained_root / ".lanes.creating"
    if lanes_root.exists() or staging_root.exists():
        raise ValueError(
            f"native lane workspaces already exist for run {manifest.run_id}"
        )
    staging_root.mkdir(mode=0o700)
    try:
        for profile in _LANE_ORDER:
            workspace = staging_root / profile.value
            workspace.mkdir(mode=0o700)
            for item in manifest.data_files:
                source = (retained_root / item.relative_path).resolve(strict=True)
                if not source.is_relative_to(retained_root) or not source.is_file():
                    raise ValueError(
                        f"manifest input escapes retained workspace: {item.relative_path}"
                    )
                destination = (workspace / item.relative_path).resolve(strict=False)
                if not destination.is_relative_to(workspace):
                    raise ValueError(
                        f"manifest input escapes {profile.value} workspace: {item.relative_path}"
                    )
                destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                shutil.copy2(source, destination)
                observed = sha256_file(destination)
                if observed != item.sha256:
                    raise ValueError(
                        f"copied {profile.value} input hash mismatch for {item.relative_path}: "
                        f"expected {item.sha256}, observed {observed}"
                    )
            manifest.verify_workspace_files(workspace)
        staging_root.rename(lanes_root)
    except Exception:
        if staging_root.exists():
            shutil.rmtree(staging_root)
        raise
    return {
        profile: (lanes_root / profile.value).resolve(strict=True)
        for profile in _LANE_ORDER
    }


def _rebase_result(
    shared: _SharedRun,
    profile: WorkerProfile,
    result: NativeLaneResult,
) -> NativeLaneResult:
    if not isinstance(result, NativeLaneResult):
        raise TypeError("native submission tool must capture a NativeLaneResult")
    if result.run_id != shared.manifest.run_id:
        raise ValueError("native result run_id does not match the shared run")
    if result.lane != profile.value:
        raise ValueError(
            f"native result lane mismatch: expected {profile.value}, observed {result.lane}"
        )
    if result.manifest_sha256 != shared.manifest.manifest_sha256:
        raise ValueError("native result does not bind the shared manifest")

    lane_root = shared.lane_workspaces[profile]
    rebased_hashes: dict[str, str] = {}
    rebased_paths: list[str] = []
    for relative_path in result.artifact_relative_paths:
        candidate = (lane_root / relative_path).resolve(strict=True)
        if not candidate.is_relative_to(lane_root) or not candidate.is_file():
            raise ValueError(
                f"{profile.value} artifact escapes its lane workspace: {relative_path}"
            )
        observed = sha256_file(candidate)
        expected = result.artifact_sha256[relative_path]
        if observed != expected:
            raise ValueError(
                f"{profile.value} artifact hash mismatch for {relative_path}: "
                f"expected {expected}, observed {observed}"
            )
        rebased = candidate.relative_to(shared.retained_root).as_posix()
        rebased_paths.append(rebased)
        rebased_hashes[rebased] = expected

    source_relative_path = None
    if result.source_relative_path is not None:
        source_relative_path = (
            Path("lanes") / profile.value / result.source_relative_path
        ).as_posix()
    payload = result.model_dump(mode="json")
    observations = dict(result.observations)
    observations["query_engine_runtime"] = shared.query_engine_identity
    payload.update(
        source_relative_path=source_relative_path,
        artifact_relative_paths=tuple(rebased_paths),
        artifact_sha256=rebased_hashes,
        observations=observations,
    )
    return NativeLaneResult.model_validate(payload)


def _missing_submission_result(
    shared: _SharedRun,
    profile: WorkerProfile,
    *,
    dispatch_error: str | None,
    capture_error: str | None,
    worker_outcome: CoordinatorWorkerOutcome | None,
    timeout_seconds: int,
) -> NativeLaneResult:
    framework, component = _LANE_FRAMEWORKS[profile]
    version = next(
        item.version
        for item in shared.manifest.components
        if item.component == component
    )
    tool_name = _LANE_TOOL_NAMES[profile]
    native_stage = "agent_submission"
    failure_observations: dict[str, Any] = {}
    if capture_error is not None:
        error = (
            f"{profile.value} native result could not be retained after {tool_name}: "
            f"{capture_error}"
        )
    elif (
        worker_outcome is not None
        and worker_outcome.status == "failed"
        and worker_outcome.error == "Timeout"
    ):
        native_stage = "agent_timeout"
        error = (
            f"{profile.value} FinanceManus worker exceeded the {timeout_seconds}-second "
            f"coordinator timeout before calling {tool_name}."
        )
        failure_observations = {
            "failure_code": "agent_timeout",
            "coordinator_worker_status": worker_outcome.status,
            "coordinator_worker_error": worker_outcome.error,
            "coordinator_timeout_seconds": timeout_seconds,
        }
    elif worker_outcome is not None and worker_outcome.status == "failed":
        native_stage = "agent_execution"
        detail = worker_outcome.error or "FinanceManus worker failed without an error"
        error = (
            f"{profile.value} FinanceManus worker failed before calling {tool_name}: "
            f"{detail}"
        )
        failure_observations = {
            "failure_code": "agent_execution",
            "coordinator_worker_status": worker_outcome.status,
            "coordinator_worker_error": worker_outcome.error,
        }
    elif dispatch_error is not None:
        error = (
            f"{profile.value} agent did not call {tool_name}; FinanceManus "
            f"CoordinatorDispatch failed: {dispatch_error}"
        )
    else:
        error = f"{profile.value} agent completed without calling {tool_name}."
    return NativeLaneResult(
        run_id=shared.manifest.run_id,
        lane=profile.value,
        manifest_sha256=shared.manifest.manifest_sha256,
        status="failed",
        native_stage=native_stage,
        framework=framework,
        framework_version=version,
        observations={
            "coordinator_run_hash": shared.run_hash,
            "query_engine_runtime": shared.query_engine_identity,
            "required_submission_tool": tool_name,
            "submission_observed": False,
            **failure_observations,
        },
        error=error,
    )


def _finite_json_object(value: Any, *, label: str) -> dict[str, Any]:
    try:
        normalized = json.loads(
            json.dumps(
                value,
                ensure_ascii=False,
                allow_nan=False,
                separators=(",", ":"),
                sort_keys=True,
            )
        )
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{label} must be finite JSON: {exc}") from exc
    if not isinstance(normalized, dict):
        raise TypeError(f"{label} must be a JSON object")
    return normalized


__all__ = ["CoordinatedNativeRunners"]
