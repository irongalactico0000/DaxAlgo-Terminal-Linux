from __future__ import annotations

import json
from datetime import UTC, datetime
from typing import Any

import pytest

from daxalgo_strategy_agent.comparison import (
    COMPARISON_SCHEMA_VERSION,
    build_comparison_report,
)
from daxalgo_strategy_agent.contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
    NativeLaneResult,
    confirmed_intent_sha256,
)


CONFIRMED_INTENT: dict[str, Any] = {
    "family": "directional_long",
    "execution": {"entry": "Target a 10% long FDAX position."},
    "lifecycle": {"explicit_close": "Close the FDAX long after six bars."},
    "expected_aggregates": {"vibequant": {"closed_trade_count": 1}},
    "scenarios": [
        {
            "name": "stale_comparison_no_trade",
            "timestamp_utc": "2026-08-07T09:05:00Z",
            "expected": "no_trade",
        },
        {
            "name": "confirmed_jump",
            "timestamp_utc": "2026-08-07T10:00:00Z",
            "expected": "target_fdax_position_percent_0.10",
        },
        {
            "name": "explicit_close",
            "timestamp_utc": "2026-08-07T10:30:00Z",
            "expected": "close_fdax_long",
        },
    ],
}


def _manifest(intent: dict[str, Any] = CONFIRMED_INTENT) -> FrozenRunManifest:
    return FrozenRunManifest(
        run_id="fdax-native-proof",
        confirmed_intent_sha256=confirmed_intent_sha256(intent),
        selected_start_utc=datetime(2026, 8, 7, 8, 0, tzinfo=UTC),
        selected_end_utc=datetime(2026, 8, 7, 11, 55, tzinfo=UTC),
        as_of_utc=datetime(2026, 8, 7, 11, 55, tzinfo=UTC),
        timezone_name="Europe/Berlin",
        data_files=(
            FrozenDataFile(
                role="primary",
                instrument="FDAX",
                venue="EUREX",
                source="fixture",
                timeframe="5m",
                relative_path="FDAX.csv",
                sha256="1" * 64,
            ),
        ),
        components=(
            ComponentPin(
                component="query_engine",
                version="source",
                source_revision="f25fab79e611fd904280cabc97d9d2393a0922dc",
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


def _vibequant_result(
    manifest: FrozenRunManifest,
    *,
    status: str = "passed",
    trade_count: int = 1,
) -> NativeLaneResult:
    passed = status == "passed"
    path = "lanes/vibequant/agent-input/vibequant-task-spec.json"
    return NativeLaneResult(
        run_id=manifest.run_id,
        lane="vibequant",
        manifest_sha256=manifest.manifest_sha256,
        status=status,
        native_stage="completed" if passed else "run",
        framework="transcend-0/VibeQuant",
        framework_version="0.1.0",
        source_relative_path=path,
        artifact_relative_paths=(path,),
        artifact_sha256={path: "2" * 64},
        observations={
            "trade_count": trade_count,
            "metrics": {"total_return_pct": -0.0047, "win_rate": 100.0},
            "validation": {"overfit_risk": "high"},
        },
        error=None if passed else "native backtest failed",
    )


def _csp_result(
    manifest: FrozenRunManifest,
    *,
    status: str = "passed",
    events: list[dict[str, Any]] | None = None,
) -> NativeLaneResult:
    passed = status == "passed"
    path = "lanes/csp/agent-input/csp-strategy.py"
    if events is None and passed:
        events = [
            {
                "output": "intent",
                "timestamp_utc": scenario["timestamp_utc"],
                "value": scenario["expected"],
            }
            for scenario in CONFIRMED_INTENT["scenarios"]
        ]
    return NativeLaneResult(
        run_id=manifest.run_id,
        lane="csp",
        manifest_sha256=manifest.manifest_sha256,
        status=status,
        native_stage="csp.run",
        framework="Point72 CSP",
        framework_version="0.18.0",
        source_relative_path=path,
        artifact_relative_paths=(path,),
        artifact_sha256={path: "3" * 64},
        observations={"events": events} if events is not None else {},
        error=None if passed else "native graph failed",
    )


def _expected_intent_events() -> list[dict[str, Any]]:
    return [
        {
            "output": "intent",
            "timestamp_utc": scenario["timestamp_utc"],
            "value": scenario["expected"],
        }
        for scenario in CONFIRMED_INTENT["scenarios"]
    ]


def test_report_binds_inputs_and_keeps_native_evidence_scopes_honest() -> None:
    manifest = _manifest()
    vibequant = _vibequant_result(manifest)
    csp = _csp_result(manifest)

    first = build_comparison_report(manifest, CONFIRMED_INTENT, vibequant, csp)
    second = build_comparison_report(manifest, CONFIRMED_INTENT, vibequant, csp)

    assert first == second
    assert first["schema_version"] == COMPARISON_SCHEMA_VERSION
    assert first["manifest_sha256"] == manifest.manifest_sha256
    assert first["confirmed_intent_sha256"] == manifest.confirmed_intent_sha256
    assert first["evidence_status"] == "partially_proven"
    assert set(first) == {
        "schema_version",
        "run_id",
        "manifest_sha256",
        "confirmed_intent_sha256",
        "evidence_status",
        "lanes",
        "csp_intent_stream_check",
        "scenario_checks",
        "vibequant_public_evidence",
        "aggregate_checks",
    }
    assert "report_hash" not in first

    assert first["lanes"]["vibequant"] == {
        "native_status": "passed",
        "native_stage": "completed",
        "evidence_status": "pass",
        "framework": "transcend-0/VibeQuant",
        "framework_version": "0.1.0",
        "source_relative_path": (
            "lanes/vibequant/agent-input/vibequant-task-spec.json"
        ),
        "artifact_relative_paths": [
            "lanes/vibequant/agent-input/vibequant-task-spec.json"
        ],
        "artifact_sha256": {
            "lanes/vibequant/agent-input/vibequant-task-spec.json": "2" * 64
        },
        "error": None,
    }
    assert first["lanes"]["csp"]["native_stage"] == "csp.run"
    assert first["lanes"]["csp"]["framework"] == "Point72 CSP"
    assert first["csp_intent_stream_check"]["status"] == "pass"
    assert first["csp_intent_stream_check"]["output"] == "intent"

    assert [item["csp"]["status"] for item in first["scenario_checks"]] == [
        "pass",
        "pass",
        "pass",
    ]
    assert [item["vibequant"]["status"] for item in first["scenario_checks"]] == [
        "unproven",
        "unproven",
        "unproven",
    ]
    assert first["scenario_checks"][1]["csp"]["observed"] == [
        {
            "output": "intent",
            "timestamp_utc": "2026-08-07T10:00:00Z",
            "value": "target_fdax_position_percent_0.10",
        }
    ]

    assert first["vibequant_public_evidence"] == {
        "trade_count": 1,
        "metrics": {"total_return_pct": -0.0047, "win_rate": 100.0},
        "validation": {"overfit_risk": "high"},
        "raw_order_or_fill_timestamps_available": False,
        "exact_scenario_behavior_available": False,
    }
    assert first["aggregate_checks"][0]["status"] == "pass"
    assert first["aggregate_checks"][0]["observed"] == {"closed_trade_count": 1}
    assert "Aggregate only" in first["aggregate_checks"][0]["scope"]
    json.dumps(first, allow_nan=False, sort_keys=True)


def test_exact_csp_timestamp_and_json_value_mismatches_fail() -> None:
    intent = {
        "scenarios": [
            {
                "name": "typed_value",
                "timestamp_utc": "2026-08-07T10:00:00Z",
                "expected": 1,
            },
            {
                "name": "missing_timestamp",
                "timestamp_utc": "2026-08-07T10:30:00Z",
                "expected": "close",
            },
        ]
    }
    manifest = _manifest(intent)
    csp = _csp_result(
        manifest,
        events=[
            {
                "output": "intent",
                "timestamp_utc": "2026-08-07T10:00:00Z",
                "value": True,
            },
            {
                "output": "intent",
                "timestamp_utc": "2026-08-07T10:35:00Z",
                "value": "close",
            },
        ],
    )

    report = build_comparison_report(
        manifest,
        intent,
        _vibequant_result(manifest),
        csp,
    )

    assert report["evidence_status"] == "failed"
    assert [item["csp"]["status"] for item in report["scenario_checks"]] == [
        "fail",
        "fail",
    ]
    assert report["scenario_checks"][0]["csp"]["observed"][0]["value"] is True
    assert report["scenario_checks"][1]["csp"]["observed"] == []
    assert report["aggregate_checks"] == []


def test_native_failure_is_failed_but_scenario_evidence_is_unproven() -> None:
    manifest = _manifest()
    csp = _csp_result(manifest, status="failed")

    report = build_comparison_report(
        manifest,
        CONFIRMED_INTENT,
        _vibequant_result(manifest),
        csp,
    )

    assert report["evidence_status"] == "failed"
    assert report["lanes"]["csp"]["native_status"] == "failed"
    assert report["lanes"]["csp"]["evidence_status"] == "fail"
    assert [item["csp"]["status"] for item in report["scenario_checks"]] == [
        "unproven",
        "unproven",
        "unproven",
    ]
    assert "native status is failed" in report["scenario_checks"][0]["csp"]["reason"]


def test_entry_and_close_aggregate_fails_when_trade_count_differs() -> None:
    manifest = _manifest()
    report = build_comparison_report(
        manifest,
        CONFIRMED_INTENT,
        _vibequant_result(manifest, trade_count=0),
        _csp_result(manifest),
    )

    assert report["evidence_status"] == "failed"
    assert report["aggregate_checks"] == [
        {
            "check_id": "vibequant_closed_trade_count",
            "expected": {"closed_trade_count": 1},
            "observed": {"closed_trade_count": 0},
            "status": "fail",
            "reason": (
                "VibeQuant publicly reported 0 closed trades instead of the confirmed count "
                "of 1."
            ),
            "scope": (
                "Aggregate only; this does not prove entry time, close time, order, fill, or "
                "individual scenario behavior."
            ),
        }
    ]


def test_trade_count_is_not_compared_without_explicit_confirmed_aggregate() -> None:
    intent = {
        key: value
        for key, value in CONFIRMED_INTENT.items()
        if key != "expected_aggregates"
    }
    manifest = _manifest(intent)

    report = build_comparison_report(
        manifest,
        intent,
        _vibequant_result(manifest, trade_count=17),
        _csp_result(manifest),
    )

    assert report["aggregate_checks"] == []
    assert report["vibequant_public_evidence"]["trade_count"] == 17
    assert report["evidence_status"] == "partially_proven"


@pytest.mark.parametrize(
    "events",
    [
        [
            *_expected_intent_events(),
            _expected_intent_events()[1],
        ],
        [
            *_expected_intent_events(),
            {
                "output": "intent",
                "timestamp_utc": "2026-08-07T10:00:00Z",
                "value": "unexpected_short",
            },
        ],
        [
            *_expected_intent_events(),
            {
                "output": "intent",
                "timestamp_utc": "2026-08-07T11:00:00Z",
                "value": "unexpected_extra_intent",
            },
        ],
        list(reversed(_expected_intent_events())),
        [{**event, "output": "decision"} for event in _expected_intent_events()],
    ],
    ids=(
        "duplicate",
        "conflict",
        "unexpected-timestamp",
        "wrong-order",
        "wrong-output-channel",
    ),
)
def test_csp_requires_the_exact_complete_intent_stream(
    events: list[dict[str, Any]],
) -> None:
    manifest = _manifest()

    report = build_comparison_report(
        manifest,
        CONFIRMED_INTENT,
        _vibequant_result(manifest),
        _csp_result(manifest, events=events),
    )

    stream = report["csp_intent_stream_check"]
    assert stream["status"] == "fail"
    assert stream["output"] == "intent"
    assert stream["expected"] == _expected_intent_events()
    assert [item["csp"]["status"] for item in report["scenario_checks"]] == [
        "fail",
        "fail",
        "fail",
    ]
    assert report["evidence_status"] == "failed"


def test_comparison_rejects_empty_scenario_set() -> None:
    intent = {"scenarios": []}
    manifest = _manifest(intent)

    with pytest.raises(ValueError, match="at least one scenario"):
        build_comparison_report(
            manifest,
            intent,
            _vibequant_result(manifest),
            _csp_result(manifest),
        )


def test_comparison_rejects_cross_run_or_changed_intent_evidence() -> None:
    manifest = _manifest()
    changed_intent = {**CONFIRMED_INTENT, "family": "changed"}

    with pytest.raises(ValueError, match="confirmed intent hash"):
        build_comparison_report(
            manifest,
            changed_intent,
            _vibequant_result(manifest),
            _csp_result(manifest),
        )

    wrong_manifest_result = _csp_result(manifest).model_copy(
        update={"manifest_sha256": "f" * 64}
    )
    with pytest.raises(ValueError, match="csp result manifest hash"):
        build_comparison_report(
            manifest,
            CONFIRMED_INTENT,
            _vibequant_result(manifest),
            wrong_manifest_result,
        )


def test_non_finite_public_metrics_are_rejected_instead_of_written_as_json() -> None:
    manifest = _manifest()
    vibequant = _vibequant_result(manifest).model_copy(
        update={
            "observations": {
                "trade_count": 1,
                "metrics": {"sharpe": float("nan")},
                "validation": {},
            }
        }
    )

    with pytest.raises(
        ValueError, match="VibeQuant metrics must contain finite JSON data"
    ):
        build_comparison_report(
            manifest,
            CONFIRMED_INTENT,
            vibequant,
            _csp_result(manifest),
        )
