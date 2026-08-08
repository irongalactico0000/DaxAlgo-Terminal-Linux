"""Host-owned child runner for a supplied Point72 CSP graph artifact.

The supplied Python file exports ``build_graph(request)`` and returns a genuine CSP graph callable.
This child—not the supplied file—calls the pinned ``csp.run`` API, captures its graph outputs, and
writes the internal result envelope. The source never receives the result path through its public
contract. Generated Python still shares one interpreter with its CSP graph, so this is observable
native-run evidence rather than a hardened attestation boundary.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import runpy
import sys
import types
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping

EXPECTED_CSP_VERSION = "0.18.0"
RESULT_SCHEMA_VERSION = "daxalgo-csp-child-result/v1"
GRAPH_FACTORY_NAME = "build_graph"


def _arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--source", required=True)
    parser.add_argument("--request", required=True)
    parser.add_argument("--result", required=True)
    return parser.parse_args()


def _read_request(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("request root must be an object")
    return value


def _write_result(
    result_path: Path,
    request: Mapping[str, Any],
    *,
    status: str,
    stage: str,
    framework_version: str,
    observations: list[dict[str, Any]],
    error: str | None,
) -> None:
    payload = {
        "schema_version": RESULT_SCHEMA_VERSION,
        "run_id": request.get("run_id"),
        "manifest_sha256": request.get("manifest_sha256"),
        "source_sha256": request.get("source_sha256"),
        "status": status,
        "stage": stage,
        "framework_version": framework_version,
        "observations": observations,
        "error": error,
    }
    result_path.write_text(
        json.dumps(
            payload,
            allow_nan=False,
            ensure_ascii=False,
            separators=(",", ":"),
            sort_keys=True,
        ),
        encoding="utf-8",
    )


def _write_failure(
    result_path: Path,
    request: Mapping[str, Any],
    *,
    stage: str,
    framework_version: str,
    error: str,
) -> None:
    _write_result(
        result_path,
        request,
        status="failed",
        stage=stage,
        framework_version=framework_version,
        observations=[],
        error=error,
    )


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _as_csp_time(value: Any) -> datetime:
    if not isinstance(value, str) or not value:
        raise ValueError("run boundary must be an ISO-8601 timestamp")
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise ValueError("run boundary must include a UTC offset")
    return parsed.astimezone(timezone.utc).replace(tzinfo=None)


def _json_value(value: Any) -> Any:
    if value is None or isinstance(value, (bool, int, str)):
        return value
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError("CSP output contains a non-finite float")
        return value
    if isinstance(value, Mapping):
        return {str(key): _json_value(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_value(item) for item in value]
    if hasattr(value, "item"):
        return _json_value(value.item())
    raise TypeError(f"CSP output is not finite JSON data: {type(value).__name__}")


def _capture_outputs(outputs: Any) -> list[dict[str, Any]]:
    if not isinstance(outputs, Mapping):
        raise TypeError("csp.run must return a mapping of graph outputs")
    observations: list[dict[str, Any]] = []
    for output_name, ticks in outputs.items():
        if not isinstance(output_name, str) or not output_name:
            raise TypeError("CSP graph output names must be non-empty strings")
        for tick in ticks:
            if not isinstance(tick, (list, tuple)) or len(tick) != 2:
                raise TypeError(f"CSP output {output_name!r} contains an invalid tick")
            timestamp, value = tick
            if not isinstance(timestamp, datetime):
                raise TypeError(f"CSP output {output_name!r} timestamp is not datetime")
            if timestamp.tzinfo is None or timestamp.utcoffset() is None:
                timestamp = timestamp.replace(tzinfo=timezone.utc)
            else:
                timestamp = timestamp.astimezone(timezone.utc)
            observations.append(
                {
                    "output": output_name,
                    "timestamp_utc": timestamp.isoformat().replace("+00:00", "Z"),
                    "value": _json_value(value),
                }
            )
    return observations


def main() -> int:
    args = _arguments()
    source_path = Path(args.source).resolve(strict=True)
    request_path = Path(args.request).resolve(strict=True)
    result_path = Path(args.result)

    try:
        request = _read_request(request_path)
    except Exception as exc:
        print(
            f"invalid CSP child request: {type(exc).__name__}: {exc}", file=sys.stderr
        )
        return 2

    expected_source_sha256 = request.get("source_sha256")
    try:
        observed_source_sha256 = _sha256_file(source_path)
    except OSError as exc:
        _write_failure(
            result_path,
            request,
            stage="source.verify",
            framework_version="unavailable",
            error=f"{type(exc).__name__}: {exc}",
        )
        return 6
    if expected_source_sha256 != observed_source_sha256:
        _write_failure(
            result_path,
            request,
            stage="source.verify",
            framework_version="unavailable",
            error=(
                "source SHA-256 mismatch: "
                f"expected {expected_source_sha256}, observed {observed_source_sha256}"
            ),
        )
        return 6

    try:
        import csp
    except Exception as exc:
        _write_failure(
            result_path,
            request,
            stage="csp.import",
            framework_version="unavailable",
            error=f"{type(exc).__name__}: {exc}",
        )
        return 3

    framework_version = str(getattr(csp, "__version__", "unknown"))
    if framework_version != EXPECTED_CSP_VERSION:
        _write_failure(
            result_path,
            request,
            stage="csp.version",
            framework_version=framework_version,
            error=f"expected csp=={EXPECTED_CSP_VERSION}, observed csp=={framework_version}",
        )
        return 4

    # Capture host callables before loading generated source and hide the runner's real __main__
    # module while that source and its graph execute. This prevents the ordinary import-__main__
    # monkeypatch path from replacing the collector/result writer. It is defense in depth, not a
    # claim that arbitrary Python in one interpreter can be cryptographically attested.
    native_csp_run = csp.run
    capture_native_outputs = _capture_outputs
    write_native_failure = _write_failure
    write_native_result = _write_result
    old_argv = sys.argv
    old_sys_path = sys.path.copy()
    old_main_module = sys.modules.get("__main__")
    isolated_main_module = types.ModuleType("__main__")
    isolated_main_module.__file__ = str(source_path)
    sys.argv = [str(source_path)]
    sys.path.insert(0, str(source_path.parent))
    sys.modules["__main__"] = isolated_main_module
    try:
        namespace = runpy.run_path(str(source_path), run_name="daxalgo_csp_strategy")
        graph_factory = namespace.get(GRAPH_FACTORY_NAME)
        if not callable(graph_factory):
            raise TypeError(
                f"CSP source must export callable {GRAPH_FACTORY_NAME}(request)"
            )
        graph = graph_factory(request)
        if not callable(graph):
            raise TypeError(
                f"{GRAPH_FACTORY_NAME}(request) must return a CSP graph callable"
            )
        outputs = native_csp_run(
            graph,
            starttime=_as_csp_time(request.get("selected_start_utc")),
            endtime=_as_csp_time(request.get("selected_end_utc")),
        )
        observations = capture_native_outputs(outputs)
    except Exception as exc:
        write_native_failure(
            result_path,
            request,
            stage="csp.run",
            framework_version=framework_version,
            error=f"{type(exc).__name__}: {exc}",
        )
        return 5
    finally:
        if old_main_module is None:
            sys.modules.pop("__main__", None)
        else:
            sys.modules["__main__"] = old_main_module
        sys.argv = old_argv
        sys.path[:] = old_sys_path

    write_native_result(
        result_path,
        request,
        status="passed",
        stage="csp.run",
        framework_version=framework_version,
        observations=observations,
        error=None,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
