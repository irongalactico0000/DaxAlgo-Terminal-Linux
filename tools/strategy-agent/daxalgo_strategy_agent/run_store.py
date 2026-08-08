"""Content-addressed custody for native strategy runs and their event streams."""

from __future__ import annotations

import json
import os
import shutil
import threading
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .contracts import (
    FrozenRunManifest,
    NativeLaneResult,
    RunEvent,
    canonical_json_bytes,
    sha256_bytes,
    sha256_file,
)


class RunStoreError(RuntimeError):
    """Raised when retained run state is missing, changed, or malformed."""


@dataclass(frozen=True)
class RetainedRun:
    manifest: FrozenRunManifest
    workspace: Path
    events: tuple[RunEvent, ...]
    results: dict[str, NativeLaneResult]


class NativeRunStore:
    """Owns immutable inputs and append-only observable events for one service instance."""

    def __init__(self, root: Path) -> None:
        self._root = root.resolve()
        self._root.mkdir(parents=True, exist_ok=True)
        self._locks: dict[str, threading.Lock] = {}
        self._locks_guard = threading.Lock()

    @property
    def root(self) -> Path:
        return self._root

    def run_exists(self, run_id: str) -> bool:
        """Return whether a syntactically valid run id has retained custody on disk."""

        return self._run_dir(run_id).is_dir()

    def create_run(
        self, manifest: FrozenRunManifest, input_workspace: Path
    ) -> RetainedRun:
        source_root = input_workspace.resolve(strict=True)
        manifest.verify_workspace_files(source_root)
        run_dir = self._run_dir(manifest.run_id)
        if run_dir.exists():
            retained = self.load_run(manifest.run_id)
            if retained.manifest.manifest_sha256 != manifest.manifest_sha256:
                raise RunStoreError(
                    f"run id already belongs to another manifest: {manifest.run_id}"
                )
            return retained

        staging = self._root / f".{manifest.run_id}.creating"
        if staging.exists():
            raise RunStoreError(
                f"run creation is already in progress: {manifest.run_id}"
            )
        staging.mkdir(mode=0o700)
        try:
            for item in manifest.data_files:
                source = (source_root / item.relative_path).resolve(strict=True)
                destination = (staging / item.relative_path).resolve()
                if not destination.is_relative_to(staging.resolve()):
                    raise RunStoreError(
                        f"data path escapes retained run: {item.relative_path}"
                    )
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, destination)
                if sha256_file(destination) != item.sha256:
                    raise RunStoreError(
                        f"copied data hash mismatch: {item.relative_path}"
                    )
            self._atomic_write(
                staging / "manifest.json", canonical_json_bytes(manifest)
            )
            self._atomic_write(
                staging / "custody.json",
                canonical_json_bytes(
                    {
                        "schema_version": "daxalgo-native-custody/v1",
                        "manifest_sha256": manifest.manifest_sha256,
                        "created_at_utc": datetime.now(timezone.utc).isoformat(),
                    }
                ),
            )
            staging.rename(run_dir)
        except Exception:
            if staging.exists():
                shutil.rmtree(staging)
            raise
        return RetainedRun(manifest=manifest, workspace=run_dir, events=(), results={})

    def append_event(
        self,
        run_id: str,
        *,
        lane: str,
        stage: str,
        status: str,
        message: str,
        details: dict[str, Any] | None = None,
    ) -> RunEvent:
        run_dir = self._run_dir(run_id, require_exists=True)
        with self._run_lock(run_id):
            sequence = self._last_sequence(run_dir) + 1
            event = RunEvent(
                sequence=sequence,
                run_id=run_id,
                lane=lane,
                stage=stage,
                status=status,
                occurred_at_utc=datetime.now(timezone.utc),
                message=message,
                details=details or {},
            )
            with (run_dir / "events.jsonl").open("ab") as stream:
                stream.write(canonical_json_bytes(event) + b"\n")
                stream.flush()
                os.fsync(stream.fileno())
            return event

    def events_after(
        self,
        run_id: str,
        after_sequence: int = 0,
        *,
        limit: int | None = None,
    ) -> tuple[RunEvent, ...]:
        run_dir = self._run_dir(run_id, require_exists=True)
        if limit is not None and (type(limit) is not int or limit < 1):
            raise RunStoreError("event limit must be a positive integer")
        path = run_dir / "events.jsonl"
        if not path.exists():
            return ()
        events: list[RunEvent] = []
        with self._run_lock(run_id):
            with path.open("r", encoding="utf-8") as stream:
                for line_number, line in enumerate(stream, 1):
                    try:
                        event = RunEvent.model_validate_json(line)
                    except Exception as exc:
                        raise RunStoreError(
                            f"invalid event at line {line_number} for {run_id}: {exc}"
                        ) from exc
                    if event.sequence > after_sequence:
                        events.append(event)
                        if limit is not None and len(events) >= limit:
                            break
        return tuple(events)

    def retain_result(self, result: NativeLaneResult) -> Path:
        retained = self.load_run(result.run_id)
        if retained.manifest.manifest_sha256 != result.manifest_sha256:
            raise RunStoreError(f"result manifest hash mismatch for {result.lane}")
        self._verify_result_artifacts(retained.workspace, result)
        path = retained.workspace / "results" / f"{result.lane}.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.exists():
            previous = NativeLaneResult.model_validate_json(
                path.read_text(encoding="utf-8")
            )
            if canonical_json_bytes(previous) != canonical_json_bytes(result):
                raise RunStoreError(
                    f"a different terminal result already exists for {result.lane}"
                )
            return path
        self._atomic_write(path, canonical_json_bytes(result))
        return path

    def retain_comparison(
        self, run_id: str, report: Mapping[str, Any]
    ) -> tuple[str, str, dict[str, Any]]:
        """Retain one immutable, hash-bound comparison artifact for the run."""

        retained = self.load_run(run_id)
        try:
            normalized = json.loads(
                json.dumps(
                    dict(report),
                    ensure_ascii=False,
                    allow_nan=False,
                    separators=(",", ":"),
                    sort_keys=True,
                )
            )
        except (TypeError, ValueError) as exc:
            raise RunStoreError(f"comparison report is not finite JSON: {exc}") from exc
        if not isinstance(normalized, dict):
            raise RunStoreError("comparison report must be a JSON object")
        if "report_hash" in normalized:
            raise RunStoreError("comparison report_hash is owned by the run store")
        if normalized.get("manifest_sha256") != retained.manifest.manifest_sha256:
            raise RunStoreError("comparison report manifest hash mismatch")
        if (
            normalized.get("confirmed_intent_sha256")
            != retained.manifest.confirmed_intent_sha256
        ):
            raise RunStoreError("comparison report confirmed-intent hash mismatch")
        normalized["report_hash"] = sha256_bytes(canonical_json_bytes(normalized))
        payload = canonical_json_bytes(normalized)
        relative_path = "comparison/report.json"
        path = retained.workspace / relative_path
        if path.exists():
            if path.read_bytes() != payload:
                raise RunStoreError("a different comparison report already exists")
        else:
            self._atomic_write(path, payload)
        return relative_path, sha256_bytes(payload), normalized

    def load_comparison(self, run_id: str) -> dict[str, Any] | None:
        """Load and verify the retained comparison report, if one exists."""

        retained = self.load_run(run_id)
        path = retained.workspace / "comparison" / "report.json"
        if not path.exists():
            return None
        try:
            report = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            raise RunStoreError(
                f"retained comparison report is invalid: {exc}"
            ) from exc
        if not isinstance(report, dict):
            raise RunStoreError("retained comparison report must be a JSON object")
        report_hash = report.get("report_hash")
        if not isinstance(report_hash, str):
            raise RunStoreError("retained comparison report has no report_hash")
        hash_input = dict(report)
        del hash_input["report_hash"]
        if sha256_bytes(canonical_json_bytes(hash_input)) != report_hash:
            raise RunStoreError("retained comparison report hash mismatch")
        if report.get("manifest_sha256") != retained.manifest.manifest_sha256:
            raise RunStoreError("retained comparison manifest hash mismatch")
        if (
            report.get("confirmed_intent_sha256")
            != retained.manifest.confirmed_intent_sha256
        ):
            raise RunStoreError("retained comparison confirmed-intent hash mismatch")
        return report

    def load_run(self, run_id: str) -> RetainedRun:
        run_dir = self._run_dir(run_id, require_exists=True)
        try:
            manifest = FrozenRunManifest.model_validate_json(
                (run_dir / "manifest.json").read_text(encoding="utf-8")
            )
            custody = json.loads((run_dir / "custody.json").read_text(encoding="utf-8"))
        except Exception as exc:
            raise RunStoreError(
                f"retained run metadata is invalid for {run_id}: {exc}"
            ) from exc
        if custody.get("manifest_sha256") != manifest.manifest_sha256:
            raise RunStoreError(f"retained manifest custody mismatch for {run_id}")
        manifest.verify_workspace_files(run_dir)
        results: dict[str, NativeLaneResult] = {}
        results_dir = run_dir / "results"
        if results_dir.exists():
            for path in results_dir.glob("*.json"):
                result = NativeLaneResult.model_validate_json(
                    path.read_text(encoding="utf-8")
                )
                if result.manifest_sha256 != manifest.manifest_sha256:
                    raise RunStoreError(f"retained result hash mismatch: {path.name}")
                self._verify_result_artifacts(run_dir, result)
                results[result.lane] = result
        return RetainedRun(
            manifest=manifest,
            workspace=run_dir,
            events=self.events_after(run_id),
            results=results,
        )

    def _last_sequence(self, run_dir: Path) -> int:
        path = run_dir / "events.jsonl"
        if not path.exists():
            return 0
        last = 0
        with path.open("r", encoding="utf-8") as stream:
            for line_number, line in enumerate(stream, 1):
                try:
                    sequence = int(json.loads(line)["sequence"])
                except Exception as exc:
                    raise RunStoreError(
                        f"invalid event sequence at line {line_number}: {exc}"
                    ) from exc
                if sequence != last + 1:
                    raise RunStoreError(
                        f"event sequence is not contiguous at line {line_number}"
                    )
                last = sequence
        return last

    def _run_dir(self, run_id: str, *, require_exists: bool = False) -> Path:
        if (
            not run_id
            or len(run_id) > 100
            or any(
                ch
                not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"
                for ch in run_id
            )
        ):
            raise RunStoreError("run_id contains unsupported characters")
        path = (self._root / run_id).resolve()
        if not path.is_relative_to(self._root):
            raise RunStoreError("run path escapes store root")
        if require_exists and not path.is_dir():
            raise RunStoreError(f"run does not exist: {run_id}")
        return path

    def _run_lock(self, run_id: str) -> threading.Lock:
        with self._locks_guard:
            return self._locks.setdefault(run_id, threading.Lock())

    @staticmethod
    def _verify_result_artifacts(workspace: Path, result: NativeLaneResult) -> None:
        root = workspace.resolve(strict=True)
        for relative_path, expected_sha256 in result.artifact_sha256.items():
            try:
                candidate = (root / relative_path).resolve(strict=True)
            except (FileNotFoundError, OSError) as exc:
                raise RunStoreError(
                    f"retained {result.lane} artifact is unavailable: {relative_path}: {exc}"
                ) from exc
            if not candidate.is_relative_to(root) or not candidate.is_file():
                raise RunStoreError(
                    f"retained {result.lane} artifact escapes workspace: {relative_path}"
                )
            observed_sha256 = sha256_file(candidate)
            if observed_sha256 != expected_sha256:
                raise RunStoreError(
                    f"retained {result.lane} artifact hash mismatch for {relative_path}: "
                    f"expected {expected_sha256}, observed {observed_sha256}"
                )

    @staticmethod
    def _atomic_write(path: Path, content: bytes) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_name(f".{path.name}.tmp")
        with temporary.open("wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
