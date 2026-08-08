from __future__ import annotations

import hashlib
from datetime import datetime, timezone
from pathlib import Path

import pytest
from pydantic import ValidationError

from daxalgo_strategy_agent.contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
)


def _sha(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _manifest(tmp_path: Path, comparisons: int = 1) -> FrozenRunManifest:
    files = []
    for index, name in enumerate(
        ["PRIMARY", *[f"COMPARE-{i}" for i in range(comparisons)]]
    ):
        path = tmp_path / f"{name}.csv"
        path.write_bytes(
            f"timestamp,close\n2026-08-08T00:00:00Z,{100 + index}\n".encode()
        )
        files.append(
            FrozenDataFile(
                role="primary" if index == 0 else "comparison",
                instrument=name,
                venue="fixture",
                source="test",
                timeframe="5m",
                relative_path=path.name,
                sha256=_sha(path.read_bytes()),
            )
        )
    return FrozenRunManifest(
        run_id="run-1",
        confirmed_intent_sha256="a" * 64,
        selected_start_utc=datetime(2026, 8, 8, 0, 0, tzinfo=timezone.utc),
        selected_end_utc=datetime(2026, 8, 8, 0, 5, tzinfo=timezone.utc),
        as_of_utc=datetime(2026, 8, 8, 0, 10, tzinfo=timezone.utc),
        timezone_name="UTC",
        data_files=tuple(files),
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


def test_manifest_hash_and_workspace_verification_are_stable(tmp_path: Path) -> None:
    manifest = _manifest(tmp_path)
    assert len(manifest.manifest_sha256) == 64
    manifest.verify_workspace_files(tmp_path)
    assert (
        FrozenRunManifest.model_validate_json(
            manifest.model_dump_json()
        ).manifest_sha256
        == manifest.manifest_sha256
    )


def test_manifest_rejects_more_than_three_comparisons(tmp_path: Path) -> None:
    with pytest.raises(ValidationError, match="at most three comparison"):
        _manifest(tmp_path, comparisons=4)


def test_manifest_detects_changed_input_file(tmp_path: Path) -> None:
    manifest = _manifest(tmp_path)
    (tmp_path / "PRIMARY.csv").write_text("changed", encoding="utf-8")
    with pytest.raises(ValueError, match="hash mismatch"):
        manifest.verify_workspace_files(tmp_path)
