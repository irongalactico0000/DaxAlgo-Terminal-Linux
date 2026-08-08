from __future__ import annotations

import csv
import json
from datetime import UTC, datetime
from decimal import Decimal
from pathlib import Path

import pytest

from daxalgo_strategy_agent.contracts import (
    confirmed_intent_sha256,
    research_context_sha256,
    sha256_file,
)
from daxalgo_strategy_agent.headless_fixture import (
    AKQUANT_VERSION,
    AS_OF_UTC,
    CSP_VERSION,
    CSV_COLUMNS,
    QUERY_ENGINE_REVISION,
    SELECTED_END_UTC,
    SELECTED_START_UTC,
    VIBEQUANT_REVISION,
    VIBEQUANT_VERSION,
    fdax_research_context,
    write_fdax_directional_long_fixture,
)


def _all_files(root: Path) -> dict[str, bytes]:
    return {
        path.relative_to(root).as_posix(): path.read_bytes()
        for path in sorted(root.rglob("*"))
        if path.is_file()
    }


def _rows(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8") as stream:
        reader = csv.DictReader(stream)
        assert tuple(reader.fieldnames or ()) == CSV_COLUMNS
        return list(reader)


def _timestamp(value: str) -> datetime:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    assert parsed.tzinfo is not None
    return parsed.astimezone(UTC)


def _return_at(path: Path, timestamp: datetime) -> Decimal:
    rows = _rows(path)
    index = next(
        index
        for index, row in enumerate(rows)
        if _timestamp(row["timestamp"]) == timestamp
    )
    assert index > 0
    return Decimal(rows[index]["close"]) / Decimal(rows[index - 1]["close"]) - 1


def test_fixture_is_byte_deterministic_and_manifest_bound(tmp_path: Path) -> None:
    first_root = tmp_path / "first"
    second_root = tmp_path / "second"
    first_root.mkdir()
    second_root.mkdir()

    first_intent, first_manifest = write_fdax_directional_long_fixture(first_root)
    second_intent, second_manifest = write_fdax_directional_long_fixture(second_root)

    assert first_intent == second_intent
    assert first_manifest == second_manifest
    assert first_manifest.manifest_sha256 == second_manifest.manifest_sha256
    assert _all_files(first_root) == _all_files(second_root)
    assert set(_all_files(first_root)) == {
        "ES.csv",
        "FDAX.csv",
        "FESX.csv",
        "VDAX.csv",
        "manifest.json",
        "research/confirmed-intent.json",
    }
    first_manifest.verify_workspace_files(first_root)
    second_manifest.verify_workspace_files(second_root)

    retained_intent = json.loads(
        (first_root / "research/confirmed-intent.json").read_text(encoding="utf-8")
    )
    retained_manifest = json.loads(
        (first_root / "manifest.json").read_text(encoding="utf-8")
    )
    assert retained_intent == first_intent
    assert retained_manifest == first_manifest.model_dump(mode="json")
    assert first_manifest.confirmed_intent_sha256 == confirmed_intent_sha256(
        first_intent
    )
    assert first_manifest.research_context_sha256 == research_context_sha256(
        fdax_research_context()
    )


def test_fixture_contains_one_primary_three_comparisons_and_causal_rows(
    tmp_path: Path,
) -> None:
    intent, manifest = write_fdax_directional_long_fixture(tmp_path)

    assert manifest.selected_start_utc == SELECTED_START_UTC
    assert manifest.selected_end_utc == SELECTED_END_UTC
    assert manifest.as_of_utc == AS_OF_UTC
    assert [
        (item.instrument, item.role, item.relative_path) for item in manifest.data_files
    ] == [
        ("FDAX", "primary", "FDAX.csv"),
        ("FESX", "comparison", "FESX.csv"),
        ("ES", "comparison", "ES.csv"),
        ("VDAX", "comparison", "VDAX.csv"),
    ]

    for item in manifest.data_files:
        path = tmp_path / item.relative_path
        rows = _rows(path)
        assert len(rows) >= 30
        assert item.sha256 == sha256_file(path)
        timestamps = [_timestamp(row["timestamp"]) for row in rows]
        assert timestamps == sorted(set(timestamps))
        assert all(
            SELECTED_START_UTC <= value <= SELECTED_END_UTC for value in timestamps
        )
        assert all(value <= AS_OF_UTC for value in timestamps)
        assert all(row["date"] == row["timestamp"] for row in rows)
        assert all(row["symbol"] == item.instrument for row in rows)

    scenarios = {item["name"]: item for item in intent["scenarios"]}
    assert scenarios["stale_comparison_no_trade"]["expected"] == "no_trade"
    assert (
        scenarios["confirmed_jump"]["expected"] == "target_fdax_position_percent_0.10"
    )
    assert scenarios["explicit_close"]["expected"] == "close_fdax_long"
    assert intent["execution"]["backtest_initial_cash"] == 1_000_000.0
    assert intent["execution"]["commission_rate"] == 0.0003
    assert intent["execution"]["stamp_tax_rate"] == 0.0
    assert intent["execution"]["slippage_bps"] == 1.0

    research_context = fdax_research_context()
    assert research_context["primary"] == "FDAX"
    assert [item["instrument"] for item in research_context["series"]] == [
        "FDAX",
        "FESX",
        "ES",
        "VDAX",
    ]
    fdax_bars = research_context["series"][0]["bars"]
    confirmed_bar = next(
        item for item in fdax_bars if item["timestamp_utc"] == "2026-08-07T10:00:00Z"
    )
    assert confirmed_bar["indicators"]["return_from_previous_close_pct"] >= 0.8
    assert confirmed_bar["indicators"]["volume_ratio_to_previous_12_mean"] is not None
    assert confirmed_bar["indicators"]["ema_4"] is not None
    assert confirmed_bar["indicators"]["ema_12"] is not None
    assert sum(len(item["bars"]) for item in research_context["series"]) < 50
    assert len(json.dumps(research_context)) < 40_000
    vdax_context_times = {
        item["timestamp_utc"] for item in research_context["series"][3]["bars"]
    }
    assert "2026-08-07T08:55:00Z" in vdax_context_times
    assert "2026-08-07T09:00:00Z" not in vdax_context_times
    assert "2026-08-07T09:05:00Z" not in vdax_context_times

    vdax_times = {_timestamp(row["timestamp"]) for row in _rows(tmp_path / "VDAX.csv")}
    stale_decision_time = datetime(2026, 8, 7, 9, 5, tzinfo=UTC)
    latest_vdax = max(value for value in vdax_times if value < stale_decision_time)
    assert latest_vdax == datetime(2026, 8, 7, 8, 55, tzinfo=UTC)
    assert (stale_decision_time - latest_vdax).total_seconds() == 600

    assert _return_at(tmp_path / "FDAX.csv", stale_decision_time) >= Decimal("0.008")
    assert _return_at(tmp_path / "FESX.csv", stale_decision_time) >= Decimal("0.0035")
    assert _return_at(tmp_path / "ES.csv", stale_decision_time) < Decimal("0.0025")

    confirmed_time = datetime(2026, 8, 7, 10, 0, tzinfo=UTC)
    assert _return_at(tmp_path / "FDAX.csv", confirmed_time) >= Decimal("0.008")
    assert _return_at(tmp_path / "FESX.csv", confirmed_time) >= Decimal("0.0035")
    assert _return_at(tmp_path / "ES.csv", confirmed_time) >= Decimal("0.0025")
    assert _return_at(tmp_path / "VDAX.csv", confirmed_time) <= Decimal("-0.02")


def test_fixture_pins_current_native_components_and_states_native_limits(
    tmp_path: Path,
) -> None:
    intent, manifest = write_fdax_directional_long_fixture(tmp_path)
    pins = {item.component: item for item in manifest.components}

    assert pins["query_engine"].source_revision == QUERY_ENGINE_REVISION
    assert pins["vibequant"].version == VIBEQUANT_VERSION
    assert pins["vibequant"].source_revision == VIBEQUANT_REVISION
    assert pins["akquant"].version == AKQUANT_VERSION
    assert pins["csp"].version == CSP_VERSION

    lock = json.loads(
        (Path(__file__).parents[1] / "upstreams.lock.json").read_text(encoding="utf-8")
    )
    assert pins["query_engine"].source_revision == lock["query_engine"]["revision"]
    assert pins["vibequant"].source_revision == lock["vibequant"]["revision"]
    assert pins["akquant"].version == lock["akquant"]["version"]
    assert pins["csp"].version == lock["csp"]["version"]

    boundaries = intent["native_boundaries"]
    assert intent["decision"]["direction"] == "long_only"
    assert "Not requested" in boundaries["short"]
    assert "Unavailable" in boundaries["vibequant_orders_and_fills"]
    assert "not a trading backtester" in boundaries["csp_backtest"]
    assert intent["execution"]["instrument"] == "FDAX"
    assert intent["execution"]["target_position_percent"] == 0.10
    assert "self.order_target_percent" in intent["execution"]["entry"]
    assert "self.close_position" in intent["lifecycle"]["explicit_close"]


def test_fixture_refuses_to_mix_with_existing_workspace_content(tmp_path: Path) -> None:
    marker = tmp_path / "belongs-to-user.txt"
    marker.write_text("preserve me", encoding="utf-8")

    with pytest.raises(ValueError, match="must be empty"):
        write_fdax_directional_long_fixture(tmp_path)

    assert marker.read_text(encoding="utf-8") == "preserve me"
    assert _all_files(tmp_path) == {"belongs-to-user.txt": b"preserve me"}
