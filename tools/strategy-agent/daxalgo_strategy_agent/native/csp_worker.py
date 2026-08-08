"""Host adapter for source-file-backed Point72 CSP graph execution.

This module parses frozen timestamp/value files and transports them to a supplied Python source
artifact.  The host-owned child calls Point72 ``csp.run`` on the graph returned by the source's
``build_graph(request)`` entrypoint and captures native graph outputs itself.  It does not generate
strategy source, inspect graph logic, simulate a market, or infer orders and fills.
"""

from __future__ import annotations

import csv
import json
import math
import os
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from daxalgo_strategy_agent.contracts import (
    FrozenRunManifest,
    NativeLaneResult,
    sha256_file,
)
from daxalgo_strategy_agent.native.process import (
    NativeProcessTimeout,
    NativeSandboxUnavailable,
    build_macos_sandbox_command,
    run_bounded_process,
)

EXPECTED_CSP_VERSION = "0.18.0"
CSP_REQUEST_SCHEMA_VERSION = "daxalgo-csp-child-request/v1"
CSP_RESULT_SCHEMA_VERSION = "daxalgo-csp-child-result/v1"
CSP_PYTHON_ENV = "DAXALGO_CSP_PYTHON"
CSP_INTEGRATION_SKIP_REASON = "CSP 0.18.0 integration is not configured: set DAXALGO_CSP_PYTHON to its Python executable"
_CHILD_TIMEOUT_SECONDS = 30
_MAX_RESULT_BYTES = 8 * 1024 * 1024
_RESULT_FIELDS = {
    "schema_version",
    "run_id",
    "manifest_sha256",
    "source_sha256",
    "status",
    "stage",
    "framework_version",
    "observations",
    "error",
}


class _WorkerFailure(Exception):
    def __init__(
        self, stage: str, message: str, *, framework_version: str = "unavailable"
    ) -> None:
        super().__init__(message)
        self.stage = stage
        self.framework_version = framework_version


def _utc_text(value: datetime) -> str:
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError("timestamp must include a UTC offset")
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def _parse_timestamp(value: Any, *, context: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{context} must be a non-empty ISO-8601 timestamp")
    text = value.strip()
    if text.endswith(("Z", "z")):
        text = f"{text[:-1]}+00:00"
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError as exc:
        raise ValueError(
            f"{context} is not a valid ISO-8601 timestamp: {value!r}"
        ) from exc
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise ValueError(f"{context} must include a UTC offset")
    return _utc_text(parsed)


def _parse_csv_series(
    path: Path,
    *,
    relative_path: str,
    selected_start_utc: datetime | None = None,
    selected_end_utc: datetime | None = None,
    as_of_utc: datetime | None = None,
) -> list[dict[str, Any]]:
    try:
        stream = path.open("r", encoding="utf-8", newline="")
    except OSError as exc:
        raise ValueError(f"cannot open {relative_path}: {exc}") from exc

    with stream:
        reader = csv.DictReader(stream)
        fieldnames = reader.fieldnames or []
        missing = [name for name in ("timestamp", "close") if name not in fieldnames]
        if missing:
            raise ValueError(
                f"{relative_path} is missing required CSV columns: {', '.join(missing)}"
            )

        observations: list[dict[str, Any]] = []
        previous: datetime | None = None
        for row_number, row in enumerate(reader, start=2):
            timestamp_text = _parse_timestamp(
                row.get("timestamp"),
                context=f"{relative_path}:{row_number} timestamp",
            )
            timestamp = datetime.fromisoformat(timestamp_text.replace("Z", "+00:00"))
            if previous is not None and timestamp <= previous:
                raise ValueError(
                    f"{relative_path}:{row_number} timestamps must be strictly increasing"
                )
            previous = timestamp
            if as_of_utc is not None and timestamp > as_of_utc:
                raise ValueError(
                    f"{relative_path}:{row_number} timestamp exceeds the frozen as-of time"
                )
            if selected_start_utc is not None and timestamp < selected_start_utc:
                raise ValueError(
                    f"{relative_path}:{row_number} timestamp precedes the selected run range"
                )
            if selected_end_utc is not None and timestamp > selected_end_utc:
                raise ValueError(
                    f"{relative_path}:{row_number} timestamp exceeds the selected run range"
                )

            raw_close = row.get("close")
            try:
                close = float(raw_close) if raw_close is not None else math.nan
            except (TypeError, ValueError) as exc:
                raise ValueError(
                    f"{relative_path}:{row_number} close is not a finite number: {raw_close!r}"
                ) from exc
            if not math.isfinite(close):
                raise ValueError(
                    f"{relative_path}:{row_number} close is not a finite number: {raw_close!r}"
                )
            observations.append({"timestamp_utc": timestamp_text, "value": close})

    if not observations:
        raise ValueError(f"{relative_path} contains no observations")
    return observations


def _series_payload(
    manifest: FrozenRunManifest, workspace: Path
) -> list[dict[str, Any]]:
    payload: list[dict[str, Any]] = []
    for item in manifest.data_files:
        path = (workspace / item.relative_path).resolve(strict=True)
        if path.suffix.lower() != ".csv":
            raise ValueError(f"{item.relative_path} must be a timestamp/close CSV file")
        payload.append(
            {
                "role": item.role,
                "instrument": item.instrument,
                "venue": item.venue,
                "source": item.source,
                "timeframe": item.timeframe,
                "relative_path": item.relative_path,
                "sha256": item.sha256,
                "observations": _parse_csv_series(
                    path,
                    relative_path=item.relative_path,
                    selected_start_utc=manifest.selected_start_utc,
                    selected_end_utc=manifest.selected_end_utc,
                    as_of_utc=manifest.as_of_utc,
                ),
            }
        )
    return payload


def _parse_observations(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        raise ValueError("observations must be an array")
    parsed: list[dict[str, Any]] = []
    for index, item in enumerate(value):
        context = f"observations[{index}]"
        if not isinstance(item, dict):
            raise ValueError(f"{context} must be an object")
        if set(item) != {"output", "timestamp_utc", "value"}:
            raise ValueError(
                f"{context} must contain exactly output, timestamp_utc, and value"
            )
        output = item["output"]
        if not isinstance(output, str) or not output.strip() or len(output) > 200:
            raise ValueError(
                f"{context}.output must be a non-empty string of at most 200 characters"
            )
        timestamp = _parse_timestamp(
            item["timestamp_utc"], context=f"{context}.timestamp_utc"
        )
        try:
            normalized_value = json.loads(
                json.dumps(
                    item["value"],
                    allow_nan=False,
                    ensure_ascii=False,
                    separators=(",", ":"),
                    sort_keys=True,
                )
            )
        except (TypeError, ValueError) as exc:
            raise ValueError(f"{context}.value must be finite JSON data") from exc
        parsed.append(
            {
                "output": output.strip(),
                "timestamp_utc": timestamp,
                "value": normalized_value,
            }
        )
    return parsed


def _parse_child_result(
    value: Any,
    *,
    run_id: str,
    manifest_sha256: str,
    source_sha256: str,
) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ValueError("result root must be an object")
    unexpected = sorted(set(value) - _RESULT_FIELDS)
    if unexpected:
        raise ValueError(f"result contains unexpected fields: {', '.join(unexpected)}")
    required = _RESULT_FIELDS - {"error"}
    missing = sorted(required - set(value))
    if missing:
        raise ValueError(f"result is missing fields: {', '.join(missing)}")
    if value["schema_version"] != CSP_RESULT_SCHEMA_VERSION:
        raise ValueError(f"unsupported result schema: {value['schema_version']!r}")
    if value["run_id"] != run_id:
        raise ValueError("result run_id does not match the frozen manifest")
    if value["manifest_sha256"] != manifest_sha256:
        raise ValueError("result manifest_sha256 does not match the frozen manifest")
    if value["source_sha256"] != source_sha256:
        raise ValueError("result source_sha256 does not match the executed source")
    if value["status"] not in {"passed", "failed"}:
        raise ValueError("result status must be passed or failed")
    if (
        not isinstance(value["stage"], str)
        or not value["stage"].strip()
        or len(value["stage"]) > 100
    ):
        raise ValueError(
            "result stage must be a non-empty string of at most 100 characters"
        )
    if (
        not isinstance(value["framework_version"], str)
        or not value["framework_version"].strip()
        or len(value["framework_version"]) > 100
    ):
        raise ValueError(
            "result framework_version must be a non-empty string of at most 100 characters"
        )

    observations = _parse_observations(value["observations"])
    error = value.get("error")
    if value["status"] == "passed":
        if value["stage"] != "csp.run":
            raise ValueError("a passed result must report the csp.run stage")
        if value["framework_version"] != EXPECTED_CSP_VERSION:
            raise ValueError(
                f"passed result must report csp=={EXPECTED_CSP_VERSION}, "
                f"observed {value['framework_version']!r}"
            )
        if error not in (None, ""):
            raise ValueError("a passed result cannot contain an error")
    else:
        if not isinstance(error, str) or not error.strip():
            raise ValueError("a failed result must contain an error")

    return {
        "status": value["status"],
        "stage": value["stage"].strip(),
        "framework_version": value["framework_version"].strip(),
        "observations": observations,
        "error": error.strip() if isinstance(error, str) else None,
    }


def _failure_result(
    manifest: FrozenRunManifest,
    *,
    stage: str,
    error: str,
    source_relative_path: str | None,
    framework_version: str = "unavailable",
    source_exists: bool = False,
    source_sha256: str | None = None,
) -> NativeLaneResult:
    retained_source = bool(source_exists and source_relative_path and source_sha256)
    return NativeLaneResult(
        run_id=manifest.run_id,
        lane="csp",
        manifest_sha256=manifest.manifest_sha256,
        status="failed",
        native_stage=stage,
        framework="Point72 CSP",
        framework_version=framework_version,
        source_relative_path=source_relative_path,
        artifact_relative_paths=(source_relative_path,) if retained_source else (),
        artifact_sha256={source_relative_path: source_sha256}
        if retained_source and source_relative_path and source_sha256
        else {},
        observations={},
        error=error[:8000],
    )


def _contained_path(root: Path, relative_path: str, *, label: str) -> Path:
    candidate_relative = Path(relative_path)
    if candidate_relative.is_absolute() or ".." in candidate_relative.parts:
        raise _WorkerFailure(
            f"worker.{label}", f"{label} path must remain inside the run workspace"
        )
    try:
        candidate = (root / candidate_relative).resolve(strict=True)
    except (FileNotFoundError, OSError) as exc:
        raise _WorkerFailure(
            f"worker.{label}", f"{label} file is unavailable: {relative_path}: {exc}"
        ) from exc
    if not candidate.is_relative_to(root):
        raise _WorkerFailure(
            f"worker.{label}",
            f"{label} file escapes the run workspace: {relative_path}",
        )
    if not candidate.is_file():
        raise _WorkerFailure(
            f"worker.{label}", f"{label} path is not a file: {relative_path}"
        )
    return candidate


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


def run_csp_worker(
    manifest: FrozenRunManifest,
    workspace: Path,
    *,
    python_executable: Path,
    source_relative_path: str,
) -> NativeLaneResult:
    """Execute one supplied CSP source artifact in a contained child process.

    The source exports ``build_graph(request)``.  The child calls the returned graph through the
    genuine pinned ``csp.run`` API and owns the private request/result envelopes.  The source is
    never given the result path and cannot self-report a successful native run.
    """

    reported_source = (
        source_relative_path if 0 < len(source_relative_path) <= 500 else None
    )
    source_exists = False
    source_sha256: str | None = None
    try:
        try:
            root = workspace.resolve(strict=True)
        except (FileNotFoundError, OSError) as exc:
            raise _WorkerFailure(
                "worker.workspace", f"run workspace is unavailable: {workspace}: {exc}"
            ) from exc
        if not root.is_dir():
            raise _WorkerFailure(
                "worker.workspace", f"run workspace is not a directory: {workspace}"
            )

        # Do not resolve a virtual-environment Python symlink to its base interpreter: the symlink
        # path is how Python discovers the configured environment and its CSP installation.
        interpreter = Path(os.path.abspath(os.fspath(python_executable.expanduser())))
        try:
            interpreter.stat()
        except (FileNotFoundError, OSError) as exc:
            raise _WorkerFailure(
                "worker.interpreter",
                f"CSP Python interpreter is unavailable: {python_executable}: {exc}",
            ) from exc
        if not interpreter.is_file() or not os.access(interpreter, os.X_OK):
            raise _WorkerFailure(
                "worker.interpreter",
                f"CSP Python interpreter is not executable: {python_executable}",
            )

        source = _contained_path(root, source_relative_path, label="source")
        source_exists = True
        if source.suffix.lower() != ".py":
            raise _WorkerFailure(
                "worker.source", "CSP source artifact must be a real .py file"
            )
        try:
            source_sha256 = sha256_file(source)
        except OSError as exc:
            raise _WorkerFailure(
                "worker.source", f"could not hash CSP source artifact: {exc}"
            ) from exc

        csp_pins = [item for item in manifest.components if item.component == "csp"]
        if len(csp_pins) != 1 or csp_pins[0].version != EXPECTED_CSP_VERSION:
            observed = csp_pins[0].version if len(csp_pins) == 1 else "missing"
            raise _WorkerFailure(
                "manifest.csp_pin",
                f"expected manifest CSP pin {EXPECTED_CSP_VERSION}, observed {observed}",
            )

        try:
            manifest.verify_workspace_files(root)
        except (OSError, ValueError) as exc:
            raise _WorkerFailure("manifest.verify", str(exc)) from exc

        try:
            series = _series_payload(manifest, root)
        except (OSError, ValueError) as exc:
            raise _WorkerFailure("input.parse", str(exc)) from exc

        request = {
            "schema_version": CSP_REQUEST_SCHEMA_VERSION,
            "run_id": manifest.run_id,
            "manifest_sha256": manifest.manifest_sha256,
            "source_sha256": source_sha256,
            "expected_csp_version": EXPECTED_CSP_VERSION,
            "selected_start_utc": _utc_text(manifest.selected_start_utc),
            "selected_end_utc": _utc_text(manifest.selected_end_utc),
            "as_of_utc": _utc_text(manifest.as_of_utc),
            "timezone_name": manifest.timezone_name,
            "series": series,
        }

        child_path = Path(__file__).with_name("csp_child.py").resolve(strict=True)
        with tempfile.TemporaryDirectory(prefix=".daxalgo-csp-", dir=root) as temporary:
            temporary_path = Path(temporary)
            request_path = temporary_path / "request.json"
            result_path = temporary_path / "result.json"
            request_path.write_text(
                json.dumps(
                    request,
                    allow_nan=False,
                    ensure_ascii=False,
                    separators=(",", ":"),
                    sort_keys=True,
                ),
                encoding="utf-8",
            )
            command = [
                str(interpreter),
                "-I",
                str(child_path),
                "--source",
                str(source),
                "--request",
                str(request_path),
                "--result",
                str(result_path),
            ]
            try:
                command = list(
                    build_macos_sandbox_command(
                        command,
                        interpreter=interpreter,
                        readable_roots=(root, child_path.parent),
                        writable_roots=(root,),
                        immutable_paths=(
                            source,
                            *(
                                root / item.relative_path
                                for item in manifest.data_files
                            ),
                        ),
                    )
                )
            except (NativeSandboxUnavailable, OSError, ValueError) as exc:
                raise _WorkerFailure("worker.sandbox", str(exc)) from exc
            try:
                completed = run_bounded_process(
                    command,
                    cwd=root,
                    env=_clean_child_environment(root),
                    timeout_seconds=_CHILD_TIMEOUT_SECONDS,
                )
            except NativeProcessTimeout as exc:
                raise _WorkerFailure(
                    "csp.run.timeout",
                    f"CSP child exceeded {_CHILD_TIMEOUT_SECONDS} seconds",
                    framework_version=EXPECTED_CSP_VERSION,
                ) from exc
            except OSError as exc:
                raise _WorkerFailure(
                    "worker.interpreter",
                    f"could not launch CSP Python interpreter: {exc}",
                ) from exc

            if completed.stdout_truncated or completed.stderr_truncated:
                raise _WorkerFailure(
                    "csp.output_limit",
                    "CSP child exceeded the bounded stdout/stderr capture limit",
                )

            try:
                manifest.verify_workspace_files(root)
                observed_source_sha256 = sha256_file(source)
            except (OSError, ValueError) as exc:
                raise _WorkerFailure(
                    "manifest.verify",
                    f"frozen inputs changed during CSP execution: {exc}",
                ) from exc
            if observed_source_sha256 != source_sha256:
                raise _WorkerFailure(
                    "source.verify",
                    "CSP source changed during native execution",
                )

            if not result_path.is_file():
                stderr = completed.stderr.strip()
                detail = f": {stderr}" if stderr else ""
                raise _WorkerFailure(
                    "csp.result",
                    f"CSP child returned {completed.returncode} without a result{detail}",
                )
            if result_path.stat().st_size > _MAX_RESULT_BYTES:
                raise _WorkerFailure(
                    "csp.result",
                    f"CSP child result exceeds {_MAX_RESULT_BYTES} bytes",
                )
            try:
                raw_result = json.loads(result_path.read_text(encoding="utf-8"))
                parsed = _parse_child_result(
                    raw_result,
                    run_id=manifest.run_id,
                    manifest_sha256=manifest.manifest_sha256,
                    source_sha256=source_sha256,
                )
            except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
                raise _WorkerFailure(
                    "csp.result", f"invalid CSP child result: {exc}"
                ) from exc
            if parsed["status"] == "passed" and completed.returncode != 0:
                raise _WorkerFailure(
                    "csp.result",
                    f"CSP child returned {completed.returncode} with a passed result",
                )

        if parsed["status"] == "failed":
            return _failure_result(
                manifest,
                stage=parsed["stage"],
                error=parsed["error"] or "CSP child failed without an error",
                source_relative_path=reported_source,
                framework_version=parsed["framework_version"],
                source_exists=True,
                source_sha256=source_sha256,
            )

        return NativeLaneResult(
            run_id=manifest.run_id,
            lane="csp",
            manifest_sha256=manifest.manifest_sha256,
            status="passed",
            native_stage="csp.run",
            framework="Point72 CSP",
            framework_version=parsed["framework_version"],
            source_relative_path=source_relative_path,
            artifact_relative_paths=(source_relative_path,),
            artifact_sha256={source_relative_path: source_sha256},
            observations={
                "native_evidence_kind": "typed_event_graph",
                "native_api": "csp.run",
                "evidence_trust": "host_wrapper_observed_not_security_attested",
                "source_sha256": source_sha256,
                "events": parsed["observations"],
                "fills_claimed": False,
                "profit_and_loss_claimed": False,
                "market_backtest_claimed": False,
            },
        )
    except _WorkerFailure as exc:
        return _failure_result(
            manifest,
            stage=exc.stage,
            error=str(exc),
            source_relative_path=reported_source,
            framework_version=exc.framework_version,
            source_exists=source_exists,
            source_sha256=source_sha256,
        )


__all__ = [
    "CSP_INTEGRATION_SKIP_REASON",
    "CSP_PYTHON_ENV",
    "EXPECTED_CSP_VERSION",
    "run_csp_worker",
]
