"""Isolated adapter for the pinned transcend-0/VibeQuant pipeline.

The host side validates the frozen manifest and source revision, then starts this
file with the configured Python interpreter.  The child imports VibeQuant from
the configured source checkout and calls its public pipeline in this exact
order::

    TaskSpec.from_dict -> make_plan -> run_task

AKQuant is reached only through VibeQuant's ``run_task`` implementation.  This
adapter deliberately does not import an AKQuant API, define another strategy
language, validate generated strategy semantics, or simulate fills.
"""

from __future__ import annotations

import csv
import hashlib
import importlib.metadata
import inspect
import json
import math
import os
import platform
import subprocess
import sys
import tomllib
from collections.abc import Mapping
from datetime import datetime, timezone
from pathlib import Path
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from ..contracts import FrozenRunManifest, NativeLaneResult


CHILD_PROTOCOL = "daxalgo-vibequant-child/v1"
RESULT_PREFIX = "DAXALGO_VIBEQUANT_RESULT="
WORKER_TIMEOUT_SECONDS = 600
REQUIRED_SOURCE_FILES = ("src/dsl.py", "src/planner.py", "src/runner.py")


def vibequant_capability_facts() -> dict[str, dict[str, Any]]:
    """Return version-pinned limitations of VibeQuant's public run surface."""

    return {
        "short_positions": {
            "supported": False,
            "reason": (
                "VibeQuant 0.1.0 does not expose AKQuant margin or "
                "enable_short_sell configuration; a submitted short can be "
                "rejected while the public VibeQuant run still completes."
            ),
        },
        "raw_orders_and_fills": {
            "supported": False,
            "reason": (
                "VibeQuant RunResult.to_dict() and its public persisted "
                "artifacts expose metrics, closed-trade count, and equity, "
                "but not raw AKQuant orders, executions, or fills."
            ),
        },
        "automatic_terminal_flatten": {
            "supported": False,
            "reason": (
                "VibeQuant 0.1.0 does not automatically flatten an open "
                "position at the final bar; the strategy must close it "
                "explicitly before the terminal bar."
            ),
        },
    }


def build_vibequant_command(python_executable: Path) -> tuple[str, ...]:
    """Build the deterministic, isolated child-process command."""

    return (
        str(python_executable),
        "-I",
        str(Path(__file__).resolve()),
        "--child",
    )


def parse_vibequant_worker_output(stdout: str) -> dict[str, Any]:
    """Parse the final framed child result while tolerating upstream logs."""

    framed = [
        line[len(RESULT_PREFIX) :]
        for line in stdout.splitlines()
        if line.startswith(RESULT_PREFIX)
    ]
    if not framed:
        raise ValueError("VibeQuant child produced no framed result")
    if len(framed) != 1:
        raise ValueError(
            f"VibeQuant child produced {len(framed)} framed results; exactly one is required"
        )
    try:
        payload = json.loads(framed[0])
    except json.JSONDecodeError as exc:
        raise ValueError(f"VibeQuant child returned invalid JSON: {exc}") from exc
    if not isinstance(payload, dict):
        raise TypeError("VibeQuant child result must be a JSON object")
    if payload.get("protocol") != CHILD_PROTOCOL:
        raise ValueError(
            "VibeQuant child protocol mismatch: "
            f"expected {CHILD_PROTOCOL!r}, observed {payload.get('protocol')!r}"
        )
    return payload


def run_vibequant_worker(
    manifest: FrozenRunManifest,
    workspace: Path,
    *,
    python_executable: Path,
    vibequant_source_root: Path,
    task_spec_relative_path: str,
) -> NativeLaneResult:
    """Run the genuine VibeQuant pipeline in the configured Python process."""

    from ..contracts import NativeLaneResult
    from .process import (
        NativeProcessTimeout,
        NativeSandboxUnavailable,
        build_macos_sandbox_command,
        run_bounded_process,
    )

    manifest_sha256 = manifest.manifest_sha256
    framework_version = _component_pin(manifest, "vibequant").version
    stages: list[dict[str, Any]] = []

    def failed(stage: str, message: str) -> NativeLaneResult:
        _record_stage(stages, stage, "failed", message)
        relative_source = _safe_relative_text(task_spec_relative_path)
        return NativeLaneResult(
            run_id=manifest.run_id,
            lane="vibequant",
            manifest_sha256=manifest_sha256,
            status="failed",
            native_stage=stage,
            framework="transcend-0/VibeQuant",
            framework_version=framework_version,
            source_relative_path=relative_source,
            observations={
                "stages": stages,
                "capabilities": vibequant_capability_facts(),
            },
            error=_bounded_error(message),
        )

    _record_stage(stages, "manifest", "started", "verifying frozen workspace inputs")
    try:
        workspace_root = workspace.expanduser().resolve(strict=True)
        if not workspace_root.is_dir():
            raise ValueError(f"workspace is not a directory: {workspace}")
        manifest.verify_workspace_files(workspace_root)
        _validate_frozen_csv_ranges(manifest, workspace_root)
    except (OSError, ValueError) as exc:
        return failed("manifest", f"frozen manifest verification failed: {exc}")
    _record_stage(
        stages,
        "manifest",
        "passed",
        "frozen input hashes match the manifest",
        manifest_sha256=manifest_sha256,
    )

    _record_stage(
        stages, "interpreter", "started", "checking configured Python interpreter"
    )
    # Preserve a virtual environment's launcher path.  Resolving its symlink
    # to the base interpreter would silently discard the configured venv and
    # therefore its pinned VibeQuant/AKQuant dependencies.
    interpreter = Path(os.path.abspath(str(python_executable.expanduser())))
    if not interpreter.is_file():
        return failed(
            "interpreter",
            f"configured Python interpreter is missing: {interpreter}",
        )
    if not os.access(interpreter, os.X_OK):
        return failed(
            "interpreter",
            f"configured Python interpreter is not executable: {interpreter}",
        )
    _record_stage(
        stages,
        "interpreter",
        "passed",
        "configured Python interpreter exists and is executable",
    )

    _record_stage(
        stages, "source", "started", "checking configured VibeQuant source root"
    )
    try:
        source_root = vibequant_source_root.expanduser().resolve(strict=True)
    except OSError as exc:
        return failed("source", f"configured VibeQuant source root is missing: {exc}")
    missing_source = [
        relative
        for relative in REQUIRED_SOURCE_FILES
        if not (source_root / relative).is_file()
    ]
    if missing_source:
        return failed(
            "source",
            "configured VibeQuant source root is incomplete; missing: "
            + ", ".join(missing_source),
        )
    _record_stage(
        stages,
        "source",
        "passed",
        "configured source root contains the public VibeQuant entry points",
    )

    _record_stage(
        stages, "revision", "started", "checking pinned VibeQuant source revision"
    )
    expected_revision = _component_pin(manifest, "vibequant").source_revision
    if not expected_revision:
        return failed("revision", "manifest does not pin a VibeQuant source revision")
    try:
        observed_revision = _git_revision(source_root)
    except (OSError, ValueError) as exc:
        return failed("revision", f"could not read VibeQuant source revision: {exc}")
    if observed_revision != expected_revision:
        return failed(
            "revision",
            "VibeQuant source revision mismatch: "
            f"expected {expected_revision}, observed {observed_revision}",
        )
    try:
        dirty_paths = _git_status(source_root)
    except (OSError, ValueError) as exc:
        return failed(
            "revision", f"could not inspect VibeQuant source cleanliness: {exc}"
        )
    if dirty_paths:
        return failed(
            "revision",
            f"configured VibeQuant source checkout is dirty: {dirty_paths.splitlines()[0]}",
        )
    _record_stage(
        stages,
        "revision",
        "passed",
        "configured source checkout matches the manifest pin",
        source_revision=observed_revision,
    )

    _record_stage(
        stages, "task_spec", "started", "checking immutable task-spec artifact"
    )
    try:
        task_relative, task_path = _resolve_workspace_file(
            workspace_root, task_spec_relative_path
        )
        task_spec_sha256 = _sha256_file(task_path)
    except (OSError, ValueError) as exc:
        return failed("task_spec", f"task-spec artifact is unavailable: {exc}")
    _record_stage(
        stages,
        "task_spec",
        "passed",
        "task-spec artifact is contained in the run workspace",
        relative_path=task_relative,
        sha256=task_spec_sha256,
    )

    request = {
        "protocol": CHILD_PROTOCOL,
        "run_id": manifest.run_id,
        "manifest_sha256": manifest_sha256,
        "workspace": str(workspace_root),
        "source_root": str(source_root),
        "source_revision": observed_revision,
        "task_spec_relative_path": task_relative,
        "task_spec_sha256": task_spec_sha256,
        "selected_start_utc": manifest.selected_start_utc.isoformat(),
        "selected_end_utc": manifest.selected_end_utc.isoformat(),
        "as_of_utc": manifest.as_of_utc.isoformat(),
        "data_files": [item.model_dump(mode="json") for item in manifest.data_files],
        "expected_versions": {
            "vibequant": _component_pin(manifest, "vibequant").version,
            "akquant": _component_pin(manifest, "akquant").version,
        },
    }
    command = build_vibequant_command(interpreter)
    _record_stage(
        stages, "sandbox", "started", "building the macOS native-code sandbox"
    )
    try:
        command = build_macos_sandbox_command(
            command,
            interpreter=interpreter,
            readable_roots=(
                workspace_root,
                source_root,
                Path(__file__).resolve().parent,
            ),
            writable_roots=(workspace_root,),
            immutable_paths=(
                task_path,
                *(workspace_root / item.relative_path for item in manifest.data_files),
            ),
        )
    except (NativeSandboxUnavailable, OSError, ValueError) as exc:
        return failed("sandbox", str(exc))
    _record_stage(
        stages,
        "sandbox",
        "passed",
        "generated strategy code has no network access and can write only inside its run workspace",
    )
    _record_stage(stages, "subprocess", "started", "starting isolated VibeQuant worker")
    try:
        completed = run_bounded_process(
            command,
            cwd=workspace_root,
            env=_clean_child_environment(workspace_root),
            stdin_text=json.dumps(request, ensure_ascii=False, separators=(",", ":")),
            timeout_seconds=WORKER_TIMEOUT_SECONDS,
        )
    except NativeProcessTimeout:
        return failed(
            "run",
            f"VibeQuant child exceeded the {WORKER_TIMEOUT_SECONDS}-second timeout",
        )
    except OSError as exc:
        return failed(
            "interpreter", f"could not start configured Python interpreter: {exc}"
        )
    if completed.stdout_truncated or completed.stderr_truncated:
        return failed(
            "subprocess.output_limit",
            "VibeQuant child exceeded the bounded stdout/stderr capture limit",
        )

    try:
        payload = parse_vibequant_worker_output(completed.stdout)
    except (TypeError, ValueError) as exc:
        detail = f"{exc}; child exit code {completed.returncode}"
        if completed.stderr.strip():
            detail += f"; stderr: {_bounded_text(completed.stderr.strip(), 2000)}"
        return failed("subprocess", detail)

    try:
        manifest.verify_workspace_files(workspace_root)
    except (OSError, ValueError) as exc:
        return failed(
            "manifest", f"frozen inputs changed during VibeQuant execution: {exc}"
        )

    if payload.get("run_id") != manifest.run_id:
        return failed("subprocess", "VibeQuant child returned a different run_id")
    if payload.get("manifest_sha256") != manifest_sha256:
        return failed(
            "subprocess", "VibeQuant child returned a different manifest hash"
        )
    if completed.returncode != 0:
        return failed(
            str(payload.get("stage") or "subprocess"),
            f"VibeQuant child exited with code {completed.returncode}: "
            f"{payload.get('error') or 'no error supplied'}",
        )
    _record_stage(
        stages,
        "subprocess",
        "passed",
        "isolated VibeQuant worker returned a bound result envelope",
    )

    observations = payload.get("observations")
    if not isinstance(observations, dict):
        return failed(
            "subprocess", "VibeQuant child observations must be a JSON object"
        )
    child_stages = observations.get("stages")
    if not isinstance(child_stages, list):
        return failed(
            "subprocess", "VibeQuant child did not return its exact stage history"
        )
    observations = dict(observations)
    observations["stages"] = stages + child_stages
    observations["source_revision"] = observed_revision
    observations["task_spec_sha256"] = task_spec_sha256
    observations["child_exit_code"] = completed.returncode
    if completed.stderr.strip():
        observations["child_stderr"] = _bounded_text(completed.stderr.strip(), 4000)

    versions = observations.get("versions")
    if isinstance(versions, dict) and versions.get("vibequant"):
        framework_version = str(versions["vibequant"])
    artifact_paths = payload.get("artifact_relative_paths") or []
    if not isinstance(artifact_paths, list) or not all(
        isinstance(item, str) for item in artifact_paths
    ):
        return failed("artifacts", "VibeQuant child returned invalid artifact paths")
    artifact_sha256 = payload.get("artifact_sha256") or {}
    if not isinstance(artifact_sha256, dict) or not all(
        isinstance(path, str) and isinstance(digest, str)
        for path, digest in artifact_sha256.items()
    ):
        return failed("artifacts", "VibeQuant child returned invalid artifact hashes")
    if set(artifact_sha256) != set(artifact_paths):
        return failed(
            "artifacts", "VibeQuant child did not hash every retained artifact"
        )
    for relative_path, expected_sha256 in artifact_sha256.items():
        try:
            _, artifact_path = _resolve_workspace_file(workspace_root, relative_path)
            observed_sha256 = _sha256_file(artifact_path)
        except (OSError, ValueError) as exc:
            return failed("artifacts", f"VibeQuant artifact is unavailable: {exc}")
        if observed_sha256 != expected_sha256:
            return failed(
                "artifacts",
                f"VibeQuant artifact hash mismatch for {relative_path}: "
                f"expected {expected_sha256}, observed {observed_sha256}",
            )
    status = str(payload.get("status") or "failed")
    if status not in {"passed", "failed", "unsupported", "cancelled"}:
        return failed(
            "subprocess", f"VibeQuant child returned invalid status {status!r}"
        )

    return NativeLaneResult(
        run_id=manifest.run_id,
        lane="vibequant",
        manifest_sha256=manifest_sha256,
        status=status,
        native_stage=str(payload.get("stage") or "subprocess"),
        framework="transcend-0/VibeQuant",
        framework_version=framework_version,
        source_relative_path=task_relative,
        artifact_relative_paths=tuple(artifact_paths),
        artifact_sha256=artifact_sha256,
        observations=observations,
        error=_optional_bounded_error(payload.get("error")),
    )


def _component_pin(manifest: FrozenRunManifest, component: str) -> Any:
    return next(item for item in manifest.components if item.component == component)


def _git_revision(source_root: Path) -> str:
    completed = subprocess.run(
        ("git", "-C", str(source_root), "rev-parse", "HEAD"),
        capture_output=True,
        text=True,
        timeout=10,
        check=False,
    )
    if completed.returncode != 0:
        raise ValueError(completed.stderr.strip() or "git rev-parse failed")
    revision = completed.stdout.strip().lower()
    if len(revision) != 40 or any(ch not in "0123456789abcdef" for ch in revision):
        raise ValueError(f"git returned an invalid revision: {revision!r}")
    return revision


def _clean_child_environment(workspace: Path) -> dict[str, str]:
    temporary = workspace / ".daxalgo-native-tmp"
    temporary.mkdir(mode=0o700, exist_ok=True)
    return {
        "LANG": "C",
        "LC_ALL": "C",
        "PYTHONDONTWRITEBYTECODE": "1",
        "PYTHONNOUSERSITE": "1",
        "PYTHONUNBUFFERED": "1",
        "TMPDIR": str(temporary),
        "TZ": "UTC",
    }


def _git_status(source_root: Path) -> str:
    completed = subprocess.run(
        (
            "git",
            "-C",
            str(source_root),
            "status",
            "--porcelain=v1",
            "--untracked-files=all",
        ),
        capture_output=True,
        text=True,
        timeout=10,
        check=False,
    )
    if completed.returncode != 0:
        raise ValueError(completed.stderr.strip() or "git status failed")
    return completed.stdout.strip()


def _resolve_workspace_file(workspace: Path, relative_text: str) -> tuple[str, Path]:
    relative = Path(relative_text)
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("path must remain relative to the run workspace")
    normalized = relative.as_posix()
    if not normalized or normalized == ".":
        raise ValueError("path must name a file")
    candidate = (workspace / relative).resolve(strict=True)
    if not candidate.is_relative_to(workspace) or not candidate.is_file():
        raise ValueError("path must identify a file inside the run workspace")
    return normalized, candidate


def _validate_frozen_csv_ranges(manifest: Any, workspace: Path) -> None:
    for item in manifest.data_files:
        path = (workspace / item.relative_path).resolve(strict=True)
        if path.suffix.lower() != ".csv":
            raise ValueError(f"{item.relative_path} must be a CSV file")
        with path.open("r", encoding="utf-8", newline="") as stream:
            reader = csv.DictReader(stream)
            if reader.fieldnames is None or "timestamp" not in reader.fieldnames:
                raise ValueError(
                    f"{item.relative_path} must contain a timestamp column"
                )
            previous = None
            count = 0
            for row_number, row in enumerate(reader, 2):
                raw_timestamp = str(row.get("timestamp") or "")
                try:
                    parsed = datetime.fromisoformat(
                        raw_timestamp.replace("Z", "+00:00")
                    )
                except ValueError as exc:
                    raise ValueError(
                        f"{item.relative_path}:{row_number} has an invalid timestamp"
                    ) from exc
                if parsed.tzinfo is None or parsed.utcoffset() is None:
                    raise ValueError(
                        f"{item.relative_path}:{row_number} timestamp must include a UTC offset"
                    )
                timestamp = parsed.astimezone(timezone.utc)
                if previous is not None and timestamp <= previous:
                    raise ValueError(
                        f"{item.relative_path}:{row_number} timestamps must be strictly increasing"
                    )
                if (
                    timestamp < manifest.selected_start_utc
                    or timestamp > manifest.selected_end_utc
                ):
                    raise ValueError(
                        f"{item.relative_path}:{row_number} timestamp is outside the selected run range"
                    )
                if timestamp > manifest.as_of_utc:
                    raise ValueError(
                        f"{item.relative_path}:{row_number} timestamp exceeds the frozen as-of time"
                    )
                previous = timestamp
                count += 1
            if count == 0:
                raise ValueError(f"{item.relative_path} contains no observations")


def _safe_relative_text(value: str) -> str | None:
    path = Path(value)
    if not value or path.is_absolute() or ".." in path.parts:
        return None
    return path.as_posix()


def _record_stage(
    stages: list[dict[str, Any]],
    stage: str,
    status: str,
    message: str,
    **details: Any,
) -> None:
    record: dict[str, Any] = {
        "stage": stage,
        "status": status,
        "message": message,
    }
    if details:
        record["details"] = details
    stages.append(record)


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _bounded_text(value: str, limit: int) -> str:
    return value if len(value) <= limit else value[: limit - 1] + "…"


def _bounded_error(value: str) -> str:
    return _bounded_text(value, 8000)


def _optional_bounded_error(value: Any) -> str | None:
    if value is None:
        return None
    return _bounded_error(str(value))


# ---------------------------------------------------------------- child


def _child_execute(request: Mapping[str, Any]) -> dict[str, Any]:
    stages: list[dict[str, Any]] = []
    run_id = str(request.get("run_id") or "unknown")
    manifest_sha256 = str(request.get("manifest_sha256") or "")
    observations: dict[str, Any] = {
        "stages": stages,
        "capabilities": vibequant_capability_facts(),
        "public_evidence_scope": (
            "VibeQuant RunResult.to_dict(), RunResult.artifacts, and the "
            "public equity.csv artifact only"
        ),
    }
    retained_artifact_paths: list[str] = []
    retained_artifact_sha256: dict[str, str] = {}

    def response(
        *,
        ok: bool,
        stage: str,
        error: str | None = None,
        artifact_paths: list[str] | None = None,
        artifact_sha256: dict[str, str] | None = None,
    ) -> dict[str, Any]:
        return {
            "protocol": CHILD_PROTOCOL,
            "run_id": run_id,
            "manifest_sha256": manifest_sha256,
            "ok": ok,
            "status": "passed" if ok else "failed",
            "stage": stage,
            "artifact_relative_paths": (
                list(retained_artifact_paths)
                if artifact_paths is None
                else artifact_paths
            ),
            "artifact_sha256": (
                dict(retained_artifact_sha256)
                if artifact_sha256 is None
                else artifact_sha256
            ),
            "observations": observations,
            "error": _optional_bounded_error(error),
        }

    _record_stage(stages, "interpreter", "started", "checking child Python runtime")
    if sys.version_info[:2] != (3, 12):
        message = (
            "VibeQuant worker requires Python 3.12; observed "
            f"{platform.python_version()}"
        )
        _record_stage(stages, "interpreter", "failed", message)
        return response(ok=False, stage="interpreter", error=message)
    _record_stage(
        stages,
        "interpreter",
        "passed",
        "child is running on Python 3.12",
        version=platform.python_version(),
    )

    source_root = Path(str(request.get("source_root") or ""))
    workspace = Path(str(request.get("workspace") or ""))
    expected_versions = request.get("expected_versions")
    if not isinstance(expected_versions, dict):
        message = "child request is missing expected component versions"
        _record_stage(stages, "import", "failed", message)
        return response(ok=False, stage="import", error=message)

    _record_stage(
        stages, "import", "started", "importing genuine VibeQuant public entry points"
    )
    try:
        sys.path.insert(0, str(source_root))
        from src.dsl import TaskSpec
        from src.planner import make_plan
        from src.runner import run_task

        entrypoints = {
            "TaskSpec.from_dict": _entrypoint_relative_path(
                TaskSpec.from_dict, source_root
            ),
            "make_plan": _entrypoint_relative_path(make_plan, source_root),
            "run_task": _entrypoint_relative_path(run_task, source_root),
        }
        versions = {
            "python": platform.python_version(),
            "vibequant": _source_project_version(source_root),
            "akquant": importlib.metadata.version("akquant"),
            "vibequant_revision": str(request.get("source_revision") or ""),
        }
        for component in ("vibequant", "akquant"):
            expected = str(expected_versions.get(component) or "")
            observed = str(versions.get(component) or "")
            if not expected or observed != expected:
                raise RuntimeError(
                    f"{component} version mismatch: expected {expected!r}, "
                    f"observed {observed!r}"
                )
    except Exception as exc:  # noqa: BLE001 -- import gate must return exact failures
        message = f"VibeQuant import gate failed ({type(exc).__name__}): {exc}"
        _record_stage(stages, "import", "failed", message)
        return response(ok=False, stage="import", error=message)
    observations["entrypoints"] = entrypoints
    observations["versions"] = versions
    _record_stage(
        stages,
        "import",
        "passed",
        "imported TaskSpec, make_plan, and run_task from the configured checkout",
        entrypoints=entrypoints,
    )

    _record_stage(
        stages, "task_spec", "started", "loading task artifact with TaskSpec.from_dict"
    )
    task_relative = str(request.get("task_spec_relative_path") or "")
    try:
        normalized_relative, task_path = _resolve_workspace_file(
            workspace, task_relative
        )
        observed_task_sha256 = _sha256_file(task_path)
        expected_task_sha256 = str(request.get("task_spec_sha256") or "")
        if observed_task_sha256 != expected_task_sha256:
            raise ValueError(
                "task-spec hash changed after host validation: "
                f"expected {expected_task_sha256}, observed {observed_task_sha256}"
            )
        retained_artifact_paths.append(normalized_relative)
        retained_artifact_sha256[normalized_relative] = observed_task_sha256
        raw = _load_task_mapping(task_path)
        spec = TaskSpec.from_dict(raw)
        _validate_vibe_data_binding(spec, request, workspace)
    except Exception as exc:  # noqa: BLE001 -- TaskSpec owns validation exceptions
        message = f"TaskSpec.from_dict failed ({type(exc).__name__}): {exc}"
        _record_stage(stages, "task_spec", "failed", message)
        return response(ok=False, stage="task_spec", error=message)
    public_source = (
        str(spec.strategy.params.get("source") or "")
        if getattr(spec, "kind", None) == "strategy"
        else ""
    )
    observations["source"] = {
        "task_spec_relative_path": normalized_relative,
        "task_spec_sha256": observed_task_sha256,
        "strategy_source": public_source,
    }
    observations["strategy_source"] = public_source
    _record_stage(
        stages,
        "task_spec",
        "passed",
        "TaskSpec.from_dict accepted the task artifact",
        relative_path=normalized_relative,
        sha256=observed_task_sha256,
    )

    _record_stage(stages, "plan", "started", "calling VibeQuant make_plan")
    try:
        plan = make_plan(spec)
        plan_steps = [
            {
                "index": index,
                "tool": str(step.tool),
                "title_en": str(step.title_en),
                "title_zh": str(step.title_zh),
                "params": _json_safe(step.params),
            }
            for index, step in enumerate(plan.steps, 1)
        ]
        observations["plan"] = {
            "steps": plan_steps,
            "description": plan.describe(getattr(spec.report, "language", "en")),
        }
    except Exception as exc:  # noqa: BLE001 -- upstream planner exceptions are evidence
        message = f"VibeQuant make_plan failed ({type(exc).__name__}): {exc}"
        _record_stage(stages, "plan", "failed", message)
        return response(ok=False, stage="plan", error=message)
    _record_stage(
        stages,
        "plan",
        "passed",
        "VibeQuant make_plan returned an executable plan",
        tools=[step["tool"] for step in plan_steps],
    )

    executed_steps: list[dict[str, Any]] = []

    def on_step(index: int, total: int, step: Any) -> None:
        executed_steps.append(
            {
                "index": int(index),
                "total": int(total),
                "tool": str(step.tool),
                "title_en": str(step.title_en),
                "title_zh": str(step.title_zh),
            }
        )

    _record_stage(stages, "run", "started", "calling VibeQuant run_task")
    try:
        result = run_task(spec, workspace=workspace, on_step=on_step)
        public_result = result.to_dict()
    except Exception as exc:  # noqa: BLE001 -- subprocess boundary must frame run failures
        message = f"VibeQuant run_task raised ({type(exc).__name__}): {exc}"
        observations["executed_steps"] = executed_steps
        _record_stage(stages, "run", "failed", message)
        return response(ok=False, stage="run", error=message)

    observations["executed_steps"] = executed_steps
    observations["upstream_run_id"] = str(public_result.get("run_id") or "")
    observations["kind"] = str(public_result.get("kind") or "")
    observations["metrics"] = _json_safe(public_result.get("metrics") or {})
    observations["trade_count"] = int(public_result.get("num_trades") or 0)
    observations["num_trades"] = int(public_result.get("num_trades") or 0)
    observations["risk"] = _json_safe(public_result.get("risk") or {})
    observations["validation"] = _json_safe(public_result.get("validation") or {})

    if not bool(public_result.get("ok")):
        upstream_error = str(public_result.get("error") or "VibeQuant run failed")
        failed_step = public_result.get("failed_step")
        observations["failed_step"] = failed_step
        message = (
            f"VibeQuant run_task failed at {failed_step or 'unknown'}: {upstream_error}"
        )
        _record_stage(stages, "run", "failed", message)
        return response(ok=False, stage="run", error=message)
    _record_stage(
        stages,
        "run",
        "passed",
        "VibeQuant run_task completed successfully",
        upstream_run_id=observations["upstream_run_id"],
        trade_count=observations["trade_count"],
    )

    _record_stage(
        stages, "artifacts", "started", "capturing VibeQuant public artifacts"
    )
    try:
        public_artifacts, artifact_paths = _public_artifacts(
            public_result.get("artifacts") or {}, workspace
        )
        observations["public_artifacts"] = public_artifacts
        equity_item = next(
            (item for item in public_artifacts if item["name"] == "equity.csv"),
            None,
        )
        if observations["kind"] == "strategy" and equity_item is None:
            raise ValueError("VibeQuant did not expose equity.csv for a strategy run")
        observations["equity"] = (
            _read_equity(workspace / equity_item["relative_path"])
            if equity_item is not None
            else None
        )
    except Exception as exc:  # noqa: BLE001 -- artifact gate must frame every read failure
        message = f"public artifact capture failed ({type(exc).__name__}): {exc}"
        _record_stage(stages, "artifacts", "failed", message)
        return response(ok=False, stage="artifacts", error=message)
    _record_stage(
        stages,
        "artifacts",
        "passed",
        "captured only artifacts exposed by VibeQuant's public RunResult",
        relative_paths=artifact_paths,
    )
    artifact_hashes = {
        item["relative_path"]: item["sha256"] for item in public_artifacts
    }
    artifact_hashes[normalized_relative] = observed_task_sha256
    artifact_paths = sorted(artifact_hashes)
    _record_stage(stages, "completed", "passed", "genuine VibeQuant pipeline completed")
    return response(
        ok=True,
        stage="completed",
        artifact_paths=artifact_paths,
        artifact_sha256=artifact_hashes,
    )


def _entrypoint_relative_path(value: Any, source_root: Path) -> str:
    source_file = inspect.getsourcefile(value)
    if source_file is None:
        raise RuntimeError(f"could not locate source for {value!r}")
    resolved = Path(source_file).resolve(strict=True)
    root = source_root.resolve(strict=True)
    if not resolved.is_relative_to(root):
        raise RuntimeError(
            f"entry point resolved outside configured source root: {resolved}"
        )
    return resolved.relative_to(root).as_posix()


def _source_project_version(source_root: Path) -> str:
    pyproject = source_root / "pyproject.toml"
    with pyproject.open("rb") as stream:
        payload = tomllib.load(stream)
    version = payload.get("project", {}).get("version")
    if not isinstance(version, str) or not version:
        raise RuntimeError("VibeQuant pyproject.toml has no project.version")
    return version


def _load_task_mapping(path: Path) -> dict[str, Any]:
    text = path.read_text(encoding="utf-8")
    if path.suffix.lower() == ".json":
        raw = json.loads(text)
    else:
        import yaml

        raw = yaml.safe_load(text)
    if not isinstance(raw, dict):
        raise TypeError("task spec must decode to a mapping")
    return raw


def _validate_vibe_data_binding(
    spec: Any, request: Mapping[str, Any], workspace: Path
) -> None:
    """Bind the genuine TaskSpec data fields to only the frozen manifest files."""

    if getattr(spec, "kind", None) != "strategy":
        raise ValueError("the native strategy lane requires TaskSpec kind=strategy")
    if getattr(spec.execution, "mode", None) != "backtest":
        raise ValueError("the native strategy lane requires execution.mode=backtest")
    data = spec.data
    if getattr(data, "source", None) != "csv":
        raise ValueError("TaskSpec data.source must be csv for a frozen DaxAlgo run")
    if getattr(data, "universe_rule", None) is not None:
        raise ValueError(
            "TaskSpec universe_rule cannot replace the frozen instrument set"
        )
    if (
        getattr(data, "start", None) is not None
        or getattr(data, "end", None) is not None
    ):
        raise ValueError(
            "TaskSpec data.start/end must be omitted; the frozen CSV defines the run range"
        )

    raw_files = request.get("data_files")
    if not isinstance(raw_files, list) or not raw_files:
        raise ValueError("child request has no frozen data-file bindings")
    expected_symbols: list[str] = []
    expected_files: list[Path] = []
    for index, item in enumerate(raw_files):
        if not isinstance(item, dict):
            raise ValueError(f"data_files[{index}] must be an object")
        instrument = item.get("instrument")
        relative_path = item.get("relative_path")
        expected_sha256 = item.get("sha256")
        if not all(
            isinstance(value, str) and value
            for value in (instrument, relative_path, expected_sha256)
        ):
            raise ValueError(f"data_files[{index}] is incomplete")
        normalized, file_path = _resolve_workspace_file(workspace, relative_path)
        del normalized
        observed_sha256 = _sha256_file(file_path)
        if observed_sha256 != expected_sha256:
            raise ValueError(
                f"frozen data hash mismatch for {relative_path}: "
                f"expected {expected_sha256}, observed {observed_sha256}"
            )
        expected_symbols.append(instrument)
        expected_files.append(file_path)

    if list(getattr(data, "symbols", ())) != expected_symbols:
        raise ValueError(
            f"TaskSpec data.symbols must equal the frozen instruments in order: {expected_symbols}"
        )
    raw_path = str(getattr(data, "path", None) or "")
    relative = Path(raw_path)
    if not raw_path or relative.is_absolute() or ".." in relative.parts:
        raise ValueError(
            "TaskSpec data.path must remain relative to the frozen workspace"
        )
    resolved_path = (workspace / relative).resolve(strict=True)
    root = workspace.resolve(strict=True)
    if not resolved_path.is_relative_to(root):
        raise ValueError("TaskSpec data.path escapes the frozen workspace")

    if len(expected_files) == 1:
        if resolved_path != expected_files[0]:
            raise ValueError("TaskSpec data.path must name the frozen primary CSV")
        return

    parents = {path.parent for path in expected_files}
    if (
        len(parents) != 1
        or resolved_path != next(iter(parents))
        or not resolved_path.is_dir()
    ):
        raise ValueError(
            "multi-series TaskSpec data.path must name their single frozen directory"
        )
    expected_names = {
        f"{symbol}.csv": path.name
        for symbol, path in zip(expected_symbols, expected_files, strict=True)
    }
    mismatched = [name for name, observed in expected_names.items() if name != observed]
    if mismatched:
        raise ValueError(
            "multi-series frozen CSV files must be named <instrument>.csv: "
            + ", ".join(mismatched)
        )


def _public_artifacts(
    raw_artifacts: Any, workspace: Path
) -> tuple[list[dict[str, Any]], list[str]]:
    if not isinstance(raw_artifacts, dict):
        raise TypeError("RunResult.artifacts must be a mapping")
    root = workspace.resolve(strict=True)
    captured: list[dict[str, Any]] = []
    for name, raw_path in sorted(raw_artifacts.items(), key=lambda item: str(item[0])):
        if not isinstance(name, str) or not isinstance(raw_path, str):
            raise TypeError("RunResult.artifacts must map string names to string paths")
        path = Path(raw_path)
        if not path.is_absolute():
            path = root / path
        resolved = path.resolve(strict=True)
        if not resolved.is_relative_to(root) or not resolved.is_file():
            raise ValueError(
                f"public artifact escapes workspace or is not a file: {name}"
            )
        captured.append(
            {
                "name": name,
                "relative_path": resolved.relative_to(root).as_posix(),
                "sha256": _sha256_file(resolved),
                "size_bytes": resolved.stat().st_size,
            }
        )
    relative_paths = sorted({item["relative_path"] for item in captured})
    return captured, relative_paths


def _read_equity(path: Path) -> dict[str, Any]:
    samples: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8", newline="") as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames is None or not {"timestamp", "equity"}.issubset(
            reader.fieldnames
        ):
            raise ValueError("equity.csv must contain timestamp and equity columns")
        for row in reader:
            value = float(row["equity"])
            if not math.isfinite(value):
                raise ValueError("equity.csv contains a non-finite value")
            samples.append({"timestamp": str(row["timestamp"]), "equity": value})
    values = [sample["equity"] for sample in samples]
    return {
        "sample_count": len(samples),
        "initial": values[0] if values else None,
        "final": values[-1] if values else None,
        "minimum": min(values) if values else None,
        "maximum": max(values) if values else None,
        "samples": samples,
    }


def _json_safe(value: Any) -> Any:
    if value is None or isinstance(value, (bool, int, str)):
        return value
    if isinstance(value, float):
        return value if math.isfinite(value) else None
    if isinstance(value, Mapping):
        return {str(key): _json_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_safe(item) for item in value]
    if hasattr(value, "item"):
        try:
            return _json_safe(value.item())
        except Exception:  # noqa: BLE001,S110 -- fallback string is intentionally lossless
            pass
    return str(value)


def _child_main() -> int:
    try:
        request = json.loads(sys.stdin.read())
        if not isinstance(request, dict) or request.get("protocol") != CHILD_PROTOCOL:
            raise ValueError("invalid child request protocol")
        payload = _child_execute(request)
    except Exception as exc:  # noqa: BLE001 -- always frame malformed child requests
        payload = {
            "protocol": CHILD_PROTOCOL,
            "run_id": "unknown",
            "manifest_sha256": "",
            "ok": False,
            "status": "failed",
            "stage": "request",
            "artifact_relative_paths": [],
            "artifact_sha256": {},
            "observations": {
                "stages": [
                    {
                        "stage": "request",
                        "status": "failed",
                        "message": f"invalid child request ({type(exc).__name__}): {exc}",
                    }
                ],
                "capabilities": vibequant_capability_facts(),
            },
            "error": _bounded_error(f"invalid child request: {exc}"),
        }
    print(
        RESULT_PREFIX
        + json.dumps(
            payload,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
            sort_keys=True,
        ),
        flush=True,
    )
    return 0


if __name__ == "__main__":
    if sys.argv[1:] != ["--child"]:
        raise SystemExit("usage: vibequant_worker.py --child")
    raise SystemExit(_child_main())
