"""Transport and provenance records for native strategy runs.

These records describe files, component versions, and observed native results. They are not a
strategy language and contain no executable trading rules.
"""

from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

SHA256_LENGTH = 64


def canonical_json_bytes(value: BaseModel | dict[str, Any]) -> bytes:
    payload = (
        value.model_dump(mode="json", exclude_none=True)
        if isinstance(value, BaseModel)
        else value
    )
    return json.dumps(
        payload,
        ensure_ascii=False,
        allow_nan=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def confirmed_intent_sha256(value: dict[str, Any]) -> str:
    """Hash the exact user-readable strategy/scenario JSON confirmed by the user."""

    return sha256_bytes(canonical_json_bytes(value))


def research_context_sha256(value: dict[str, Any]) -> str:
    """Hash the exact host-frozen chart context supplied to the research session."""

    return sha256_bytes(canonical_json_bytes(value))


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class ComponentPin(StrictModel):
    component: Literal["query_engine", "vibequant", "akquant", "csp"]
    version: str = Field(min_length=1, max_length=100)
    source_revision: str | None = Field(default=None, min_length=7, max_length=64)

    @model_validator(mode="after")
    def validate_source_revision(self) -> "ComponentPin":
        if self.component in {"query_engine", "vibequant"}:
            revision = self.source_revision or ""
            if len(revision) != 40 or any(
                ch not in "0123456789abcdef" for ch in revision
            ):
                raise ValueError(
                    f"{self.component} must use a full lowercase 40-character Git revision"
                )
        return self


class FrozenDataFile(StrictModel):
    role: Literal["primary", "comparison"]
    instrument: str = Field(min_length=1, max_length=200)
    venue: str = Field(min_length=1, max_length=100)
    source: str = Field(min_length=1, max_length=200)
    timeframe: str = Field(min_length=1, max_length=40)
    relative_path: str = Field(min_length=1, max_length=500)
    sha256: str

    @field_validator("sha256")
    @classmethod
    def validate_sha256(cls, value: str) -> str:
        normalized = value.lower()
        if len(normalized) != SHA256_LENGTH or any(
            ch not in "0123456789abcdef" for ch in normalized
        ):
            raise ValueError("sha256 must be a lowercase hexadecimal SHA-256 digest")
        return normalized

    @field_validator("relative_path")
    @classmethod
    def validate_relative_path(cls, value: str) -> str:
        path = Path(value)
        if path.is_absolute() or ".." in path.parts:
            raise ValueError("relative_path must remain inside the run workspace")
        return path.as_posix()


class FrozenRunManifest(StrictModel):
    schema_version: Literal["daxalgo-native-run-manifest/v1"] = (
        "daxalgo-native-run-manifest/v1"
    )
    run_id: str = Field(min_length=1, max_length=100)
    confirmed_intent_sha256: str
    research_context_sha256: str | None = None
    selected_start_utc: datetime
    selected_end_utc: datetime
    as_of_utc: datetime
    timezone_name: str = Field(min_length=1, max_length=100)
    data_files: tuple[FrozenDataFile, ...]
    components: tuple[ComponentPin, ...]

    @field_validator("confirmed_intent_sha256", "research_context_sha256")
    @classmethod
    def validate_bound_sha256(cls, value: str | None) -> str | None:
        if value is None:
            return None
        return FrozenDataFile.validate_sha256(value)

    @field_validator("selected_start_utc", "selected_end_utc", "as_of_utc")
    @classmethod
    def validate_utc(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("timestamps must include an offset")
        return value.astimezone(timezone.utc)

    @model_validator(mode="after")
    def validate_manifest(self) -> "FrozenRunManifest":
        if self.selected_start_utc >= self.selected_end_utc:
            raise ValueError("selected_start_utc must precede selected_end_utc")
        if self.selected_end_utc > self.as_of_utc:
            raise ValueError("the selected range cannot extend beyond as_of_utc")
        primary = [item for item in self.data_files if item.role == "primary"]
        comparisons = [item for item in self.data_files if item.role == "comparison"]
        if len(primary) != 1:
            raise ValueError("exactly one primary data file is required")
        if len(comparisons) > 3:
            raise ValueError("at most three comparison data files are supported")
        instruments = [item.instrument for item in self.data_files]
        if len(set(instruments)) != len(instruments):
            raise ValueError("data-file instruments must be unique")
        components = [item.component for item in self.components]
        if set(components) != {"query_engine", "vibequant", "akquant", "csp"}:
            raise ValueError(
                "the manifest must pin query_engine, vibequant, akquant, and csp"
            )
        if len(components) != len(set(components)):
            raise ValueError("component pins must be unique")
        return self

    @property
    def manifest_sha256(self) -> str:
        return sha256_bytes(canonical_json_bytes(self))

    def verify_workspace_files(self, workspace: Path) -> None:
        root = workspace.resolve(strict=True)
        for item in self.data_files:
            candidate = (root / item.relative_path).resolve(strict=True)
            if not candidate.is_relative_to(root):
                raise ValueError(f"data file escapes workspace: {item.relative_path}")
            observed = sha256_file(candidate)
            if observed != item.sha256:
                raise ValueError(
                    f"data file hash mismatch for {item.relative_path}: expected {item.sha256}, observed {observed}"
                )


LaneName = Literal["research", "vibequant", "csp", "comparison"]
EventStatus = Literal[
    "started", "progress", "passed", "failed", "unsupported", "cancelled"
]


class RunEvent(StrictModel):
    schema_version: Literal["daxalgo-native-run-event/v1"] = (
        "daxalgo-native-run-event/v1"
    )
    sequence: int = Field(ge=1)
    run_id: str = Field(min_length=1, max_length=100)
    lane: LaneName
    stage: str = Field(min_length=1, max_length=100)
    status: EventStatus
    occurred_at_utc: datetime
    message: str = Field(min_length=1, max_length=4000)
    details: dict[str, Any] = Field(default_factory=dict)

    @field_validator("occurred_at_utc")
    @classmethod
    def normalize_event_time(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("occurred_at_utc must include an offset")
        return value.astimezone(timezone.utc)


class NativeLaneResult(StrictModel):
    schema_version: Literal["daxalgo-native-lane-result/v1"] = (
        "daxalgo-native-lane-result/v1"
    )
    run_id: str = Field(min_length=1, max_length=100)
    lane: Literal["vibequant", "csp"]
    manifest_sha256: str
    status: Literal["passed", "failed", "unsupported", "cancelled"]
    native_stage: str = Field(min_length=1, max_length=100)
    framework: str = Field(min_length=1, max_length=100)
    framework_version: str = Field(min_length=1, max_length=100)
    source_relative_path: str | None = Field(default=None, max_length=500)
    artifact_relative_paths: tuple[str, ...] = ()
    artifact_sha256: dict[str, str] = Field(default_factory=dict)
    observations: dict[str, Any] = Field(default_factory=dict)
    error: str | None = Field(default=None, max_length=8000)

    @field_validator("manifest_sha256")
    @classmethod
    def validate_manifest_sha256(cls, value: str) -> str:
        return FrozenDataFile.validate_sha256(value)

    @field_validator("source_relative_path")
    @classmethod
    def validate_optional_relative_path(cls, value: str | None) -> str | None:
        if value is None:
            return None
        return FrozenDataFile.validate_relative_path(value)

    @field_validator("artifact_relative_paths")
    @classmethod
    def validate_artifact_paths(cls, value: tuple[str, ...]) -> tuple[str, ...]:
        normalized = tuple(
            FrozenDataFile.validate_relative_path(item) for item in value
        )
        if len(normalized) != len(set(normalized)):
            raise ValueError("artifact_relative_paths must be unique")
        return normalized

    @field_validator("artifact_sha256")
    @classmethod
    def validate_artifact_hashes(cls, value: dict[str, str]) -> dict[str, str]:
        return {
            FrozenDataFile.validate_relative_path(path): FrozenDataFile.validate_sha256(
                digest
            )
            for path, digest in value.items()
        }

    @model_validator(mode="after")
    def validate_artifact_binding(self) -> "NativeLaneResult":
        paths = set(self.artifact_relative_paths)
        if set(self.artifact_sha256) != paths:
            raise ValueError(
                "artifact_sha256 must bind every retained artifact path exactly once"
            )
        if self.status == "passed" and self.source_relative_path not in paths:
            raise ValueError(
                "a passed native result must retain and hash its source artifact"
            )
        return self
