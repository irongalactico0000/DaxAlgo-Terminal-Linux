"""Deterministic comparison of retained native strategy-run evidence.

This module does not interpret or execute a strategy.  It compares the user's confirmed,
timestamped scenario values with the values emitted by a completed Point72 CSP graph and exposes
only the aggregate evidence published by VibeQuant.  In particular, a VibeQuant trade count is not
promoted into invented order, fill, or per-scenario timestamp evidence.
"""

from __future__ import annotations

import json
from collections.abc import Mapping
from typing import Any, Literal

from .contracts import (
    FrozenRunManifest,
    NativeLaneResult,
    canonical_json_bytes,
    confirmed_intent_sha256,
)

COMPARISON_SCHEMA_VERSION = "daxalgo-native-comparison/v1"
_CSP_SCENARIO_OUTPUT = "intent"
CheckStatus = Literal["pass", "fail", "unproven"]


def build_comparison_report(
    manifest: FrozenRunManifest,
    confirmed_intent: Mapping[str, Any],
    vibequant_result: NativeLaneResult,
    csp_result: NativeLaneResult,
) -> dict[str, Any]:
    """Build a finite, JSON-serializable report from retained native results.

    Contract/provenance mismatches raise ``ValueError`` because results from different immutable
    jobs cannot be compared.  Native execution failures remain report evidence rather than being
    raised.
    """

    intent = _copy_json_mapping(confirmed_intent, "confirmed_intent")
    intent_sha256 = confirmed_intent_sha256(intent)
    if intent_sha256 != manifest.confirmed_intent_sha256:
        raise ValueError("confirmed intent hash does not match the frozen manifest")

    _validate_lane_binding(manifest, vibequant_result, "vibequant")
    _validate_lane_binding(manifest, csp_result, "csp")

    scenarios = _confirmed_scenarios(intent)
    csp_events, csp_events_error = _csp_events(csp_result)
    csp_stream_check = _csp_intent_stream_check(
        scenarios,
        csp_events=csp_events,
        csp_events_error=csp_events_error,
    )
    scenario_checks = [
        _scenario_check(
            scenario,
            csp_events=csp_events,
            csp_stream_check=csp_stream_check,
        )
        for scenario in scenarios
    ]

    aggregate_checks: list[dict[str, Any]] = []
    expected_closed_trade_count = _expected_vibequant_closed_trade_count(intent)
    if expected_closed_trade_count is not None:
        aggregate_checks.append(
            _closed_trade_count_check(vibequant_result, expected_closed_trade_count)
        )

    lane_checks = {
        "vibequant": _native_check_status(vibequant_result.status),
        "csp": _native_check_status(csp_result.status),
    }
    all_statuses: list[CheckStatus] = list(lane_checks.values())
    all_statuses.append(csp_stream_check["status"])
    for check in scenario_checks:
        all_statuses.append(check["csp"]["status"])
        all_statuses.append(check["vibequant"]["status"])
    all_statuses.extend(check["status"] for check in aggregate_checks)

    report = {
        "schema_version": COMPARISON_SCHEMA_VERSION,
        "run_id": manifest.run_id,
        "manifest_sha256": manifest.manifest_sha256,
        "confirmed_intent_sha256": intent_sha256,
        "evidence_status": _overall_status(all_statuses),
        "lanes": {
            "vibequant": _lane_summary(vibequant_result, lane_checks["vibequant"]),
            "csp": _lane_summary(csp_result, lane_checks["csp"]),
        },
        "csp_intent_stream_check": csp_stream_check,
        "scenario_checks": scenario_checks,
        "vibequant_public_evidence": _vibequant_public_evidence(vibequant_result),
        "aggregate_checks": aggregate_checks,
    }
    # This final serialization is an invariant check: callers always receive finite JSON data.
    json.dumps(report, ensure_ascii=False, allow_nan=False, sort_keys=True)
    return report


def _copy_json_mapping(value: Mapping[str, Any], context: str) -> dict[str, Any]:
    copied = _copy_json(dict(value), context)
    if not isinstance(copied, dict):
        raise ValueError(f"{context} must be a JSON object")
    return copied


def _copy_json(value: Any, context: str) -> Any:
    try:
        encoded = json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
            sort_keys=True,
        )
        return json.loads(encoded)
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{context} must contain finite JSON data") from exc


def _validate_lane_binding(
    manifest: FrozenRunManifest,
    result: NativeLaneResult,
    expected_lane: Literal["vibequant", "csp"],
) -> None:
    if result.lane != expected_lane:
        raise ValueError(
            f"expected the {expected_lane} result, observed lane {result.lane}"
        )
    if result.run_id != manifest.run_id:
        raise ValueError(f"{expected_lane} result run_id does not match the manifest")
    if result.manifest_sha256 != manifest.manifest_sha256:
        raise ValueError(
            f"{expected_lane} result manifest hash does not match the manifest"
        )


def _confirmed_scenarios(intent: Mapping[str, Any]) -> list[dict[str, Any]]:
    raw_scenarios = intent.get("scenarios")
    if not isinstance(raw_scenarios, list):
        raise ValueError("confirmed_intent.scenarios must be a JSON array")
    if not raw_scenarios:
        raise ValueError(
            "confirmed_intent.scenarios must contain at least one scenario"
        )

    scenarios: list[dict[str, Any]] = []
    names: set[str] = set()
    for index, raw_scenario in enumerate(raw_scenarios):
        context = f"confirmed_intent.scenarios[{index}]"
        if not isinstance(raw_scenario, Mapping):
            raise ValueError(f"{context} must be a JSON object")
        scenario = _copy_json_mapping(raw_scenario, context)
        name = scenario.get("name")
        timestamp = scenario.get("timestamp_utc")
        if not isinstance(name, str) or not name:
            raise ValueError(f"{context}.name must be a non-empty string")
        if name in names:
            raise ValueError("confirmed scenario names must be unique")
        if not isinstance(timestamp, str) or not timestamp:
            raise ValueError(f"{context}.timestamp_utc must be a non-empty string")
        if "expected" not in scenario:
            raise ValueError(f"{context}.expected is required")
        names.add(name)
        scenarios.append(scenario)
    return scenarios


def _csp_events(
    result: NativeLaneResult,
) -> tuple[list[dict[str, Any]] | None, str | None]:
    if result.status != "passed":
        return None, f"CSP native status is {result.status} at {result.native_stage}."
    raw_events = result.observations.get("events")
    if not isinstance(raw_events, list):
        return None, "The passed CSP result contains no events array."

    events: list[dict[str, Any]] = []
    for index, raw_event in enumerate(raw_events):
        context = f"csp observations.events[{index}]"
        if not isinstance(raw_event, Mapping):
            return None, f"{context} is not an object."
        timestamp = raw_event.get("timestamp_utc")
        output = raw_event.get("output")
        if not isinstance(timestamp, str) or not timestamp:
            return None, f"{context}.timestamp_utc is unavailable."
        if not isinstance(output, str) or not output:
            return None, f"{context}.output is unavailable."
        if "value" not in raw_event:
            return None, f"{context}.value is unavailable."
        try:
            event = _copy_json_mapping(raw_event, context)
        except ValueError as exc:
            return None, str(exc)
        events.append(
            {
                "output": event["output"],
                "timestamp_utc": event["timestamp_utc"],
                "value": event["value"],
            }
        )
    return events, None


def _scenario_check(
    scenario: Mapping[str, Any],
    *,
    csp_events: list[dict[str, Any]] | None,
    csp_stream_check: Mapping[str, Any],
) -> dict[str, Any]:
    timestamp = scenario["timestamp_utc"]
    expected = _copy_json(scenario["expected"], f"scenario {scenario['name']} expected")
    observed = (
        []
        if csp_events is None
        else [
            event
            for event in csp_events
            if event["output"] == _CSP_SCENARIO_OUTPUT
            and event["timestamp_utc"] == timestamp
        ]
    )
    if csp_stream_check["status"] == "unproven":
        csp_check = {
            "status": "unproven",
            "observed": observed,
            "reason": csp_stream_check["reason"],
        }
    elif csp_stream_check["status"] == "fail":
        csp_check = {
            "status": "fail",
            "observed": observed,
            "reason": (
                "The complete CSP intent stream differs from the confirmed scenario stream; "
                "an isolated matching event cannot pass this scenario."
            ),
        }
    else:
        csp_check = {
            "status": "pass",
            "observed": observed,
            "reason": (
                "The complete CSP intent stream exactly matches every confirmed timestamp and "
                "JSON value."
            ),
        }

    return {
        "scenario_name": scenario["name"],
        "timestamp_utc": timestamp,
        "expected": expected,
        "csp": csp_check,
        "vibequant": {
            "status": "unproven",
            "observed": None,
            "reason": (
                "VibeQuant's public result exposes aggregate closed-trade and metric evidence, "
                "not raw order/fill events or exact per-scenario timestamps."
            ),
        },
    }


def _csp_intent_stream_check(
    scenarios: list[dict[str, Any]],
    *,
    csp_events: list[dict[str, Any]] | None,
    csp_events_error: str | None,
) -> dict[str, Any]:
    expected = [
        {
            "output": _CSP_SCENARIO_OUTPUT,
            "timestamp_utc": scenario["timestamp_utc"],
            "value": _copy_json(
                scenario["expected"], f"scenario {scenario['name']} expected"
            ),
        }
        for scenario in scenarios
    ]
    if csp_events is None:
        return {
            "output": _CSP_SCENARIO_OUTPUT,
            "status": "unproven",
            "expected": expected,
            "observed": [],
            "reason": csp_events_error
            or "CSP timestamped output evidence is unavailable.",
        }

    observed = [
        event for event in csp_events if event["output"] == _CSP_SCENARIO_OUTPUT
    ]
    exact = canonical_json_bytes({"events": observed}) == canonical_json_bytes(
        {"events": expected}
    )
    return {
        "output": _CSP_SCENARIO_OUTPUT,
        "status": "pass" if exact else "fail",
        "expected": expected,
        "observed": observed,
        "reason": (
            "The complete CSP intent stream exactly matches the confirmed scenario stream."
            if exact
            else (
                "The complete CSP intent stream must contain exactly one corresponding event per "
                "confirmed scenario, in confirmed order, with no duplicate, conflicting, or "
                "unexpected intent events."
            )
        ),
    }


def _expected_vibequant_closed_trade_count(intent: Mapping[str, Any]) -> int | None:
    raw_aggregates = intent.get("expected_aggregates")
    if raw_aggregates is None:
        return None
    if not isinstance(raw_aggregates, Mapping):
        raise ValueError("confirmed_intent.expected_aggregates must be a JSON object")
    raw_vibequant = raw_aggregates.get("vibequant")
    if raw_vibequant is None:
        return None
    if not isinstance(raw_vibequant, Mapping):
        raise ValueError(
            "confirmed_intent.expected_aggregates.vibequant must be a JSON object"
        )
    expected = raw_vibequant.get("closed_trade_count")
    if expected is None:
        return None
    if not isinstance(expected, int) or isinstance(expected, bool) or expected < 0:
        raise ValueError(
            "confirmed_intent.expected_aggregates.vibequant.closed_trade_count must be a "
            "non-negative integer"
        )
    return expected


def _closed_trade_count_check(
    result: NativeLaneResult, expected: int
) -> dict[str, Any]:
    observed = result.observations.get("trade_count")
    if result.status != "passed":
        status: CheckStatus = "unproven"
        reason = (
            f"VibeQuant native status is {result.status} at {result.native_stage}; "
            "aggregate closed-trade evidence is unavailable."
        )
    elif not isinstance(observed, int) or isinstance(observed, bool) or observed < 0:
        status = "unproven"
        reason = "VibeQuant did not publish a valid non-negative closed-trade count."
    elif observed == expected:
        status = "pass"
        reason = f"VibeQuant publicly reported the confirmed count of {expected} closed trades."
    else:
        status = "fail"
        reason = (
            f"VibeQuant publicly reported {observed} closed trades instead of the confirmed "
            f"count of {expected}."
        )
    return {
        "check_id": "vibequant_closed_trade_count",
        "expected": {"closed_trade_count": expected},
        "observed": {"closed_trade_count": observed},
        "status": status,
        "reason": reason,
        "scope": (
            "Aggregate only; this does not prove entry time, close time, order, fill, or "
            "individual scenario behavior."
        ),
    }


def _lane_summary(
    result: NativeLaneResult, evidence_status: CheckStatus
) -> dict[str, Any]:
    return {
        "native_status": result.status,
        "native_stage": result.native_stage,
        "evidence_status": evidence_status,
        "framework": result.framework,
        "framework_version": result.framework_version,
        "source_relative_path": result.source_relative_path,
        "artifact_relative_paths": list(result.artifact_relative_paths),
        "artifact_sha256": dict(sorted(result.artifact_sha256.items())),
        "error": result.error,
    }


def _vibequant_public_evidence(result: NativeLaneResult) -> dict[str, Any]:
    trade_count = result.observations.get("trade_count")
    metrics = result.observations.get("metrics")
    validation = result.observations.get("validation")
    return {
        "trade_count": _copy_json(trade_count, "VibeQuant trade_count"),
        "metrics": _copy_json(metrics, "VibeQuant metrics"),
        "validation": _copy_json(validation, "VibeQuant validation"),
        "raw_order_or_fill_timestamps_available": False,
        "exact_scenario_behavior_available": False,
    }


def _native_check_status(native_status: str) -> CheckStatus:
    if native_status == "passed":
        return "pass"
    if native_status == "unsupported":
        return "unproven"
    return "fail"


def _overall_status(statuses: list[CheckStatus]) -> str:
    if "fail" in statuses:
        return "failed"
    if "unproven" in statuses:
        return "partially_proven"
    return "passed"


__all__ = ["COMPARISON_SCHEMA_VERSION", "build_comparison_report"]
