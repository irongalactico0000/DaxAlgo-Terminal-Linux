"""Deterministic FDAX chart-context fixture for the native headless proof.

This module writes ordinary timestamped CSV inputs plus one readable confirmed-intent JSON
document.  It does not define a strategy language, validate generated strategy semantics, execute
native code, or simulate a market.  The returned :class:`FrozenRunManifest` only binds the fixture
files and the pinned native component identities used by the separate workers.
"""

from __future__ import annotations

import csv
import io
import json
from datetime import UTC, datetime, timedelta
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path
from typing import Any

from .contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
    confirmed_intent_sha256,
    research_context_sha256,
    sha256_bytes,
)

RUN_ID = "fdax-directional-long-v1"
SELECTED_START_UTC = datetime(2026, 8, 7, 8, 0, tzinfo=UTC)
SELECTED_END_UTC = datetime(2026, 8, 7, 11, 55, tzinfo=UTC)
AS_OF_UTC = SELECTED_END_UTC
TIMEFRAME = "5m"
DATA_SOURCE = "daxalgo-synthetic-headless-fixture/v1"

QUERY_ENGINE_REVISION = "f25fab79e611fd904280cabc97d9d2393a0922dc"
VIBEQUANT_REVISION = "1f5442d88ec97b6075ac73a3c4d0b42d1c00a640"
VIBEQUANT_VERSION = "0.1.0"
AKQUANT_VERSION = "0.3.36"
CSP_VERSION = "0.18.0"

CSV_COLUMNS = ("date", "timestamp", "open", "high", "low", "close", "volume", "symbol")
INSTRUMENTS = (
    ("FDAX", "EUREX", "primary"),
    ("FESX", "EUREX", "comparison"),
    ("ES", "CME", "comparison"),
    ("VDAX", "EUREX", "comparison"),
)

_INITIAL_CLOSE = {
    "FDAX": Decimal("18500.00"),
    "FESX": Decimal("4950.00"),
    "ES": Decimal("5325.00"),
    "VDAX": Decimal("16.50"),
}
_NORMAL_RETURN = {
    "FDAX": Decimal("0.00005"),
    "FESX": Decimal("0.00004"),
    "ES": Decimal("0.00003"),
    "VDAX": Decimal("-0.00002"),
}
_STALE_NO_TRADE_INDEX = 13  # 09:05 UTC
_CONFIRMED_JUMP_INDEX = 24  # 10:00 UTC
_EXPLICIT_CLOSE_INDEX = 30  # 10:30 UTC, six five-minute bars after entry
_VDAX_MISSING_INDICES = frozenset({12, 13})  # last value at 08:55 is stale at 09:05
_RESEARCH_EVIDENCE_INDICES = frozenset({11, 12, 13, *range(23, 31)})


def write_fdax_directional_long_fixture(
    workspace: Path | str,
) -> tuple[dict[str, Any], FrozenRunManifest]:
    """Write the reproducible headless fixture into an existing empty directory.

    Four market-data files are written at the workspace root using the filenames required by the
    pinned VibeQuant multi-symbol CSV loader.  The confirmed intent is retained at the location the
    native coordinator consumes, and a human-inspectable manifest copy is written at the root.
    """

    root = _require_empty_workspace(workspace)
    confirmed_intent = _confirmed_intent()
    series_payloads = {
        instrument: _series_csv(instrument) for instrument, _venue, _role in INSTRUMENTS
    }
    research_context = _research_context(series_payloads)
    data_files = tuple(
        FrozenDataFile(
            role=role,
            instrument=instrument,
            venue=venue,
            source=DATA_SOURCE,
            timeframe=TIMEFRAME,
            relative_path=f"{instrument}.csv",
            sha256=sha256_bytes(series_payloads[instrument]),
        )
        for instrument, venue, role in INSTRUMENTS
    )
    manifest = FrozenRunManifest(
        run_id=RUN_ID,
        confirmed_intent_sha256=confirmed_intent_sha256(confirmed_intent),
        research_context_sha256=research_context_sha256(research_context),
        selected_start_utc=SELECTED_START_UTC,
        selected_end_utc=SELECTED_END_UTC,
        as_of_utc=AS_OF_UTC,
        timezone_name="Europe/Berlin",
        data_files=data_files,
        components=(
            ComponentPin(
                component="query_engine",
                version="source",
                source_revision=QUERY_ENGINE_REVISION,
            ),
            ComponentPin(
                component="vibequant",
                version=VIBEQUANT_VERSION,
                source_revision=VIBEQUANT_REVISION,
            ),
            ComponentPin(component="akquant", version=AKQUANT_VERSION),
            ComponentPin(component="csp", version=CSP_VERSION),
        ),
    )

    for relative_path, payload in (
        *(
            (f"{instrument}.csv", series_payloads[instrument])
            for instrument in series_payloads
        ),
        (
            "research/confirmed-intent.json",
            _pretty_json_bytes(confirmed_intent),
        ),
        ("manifest.json", _pretty_json_bytes(manifest.model_dump(mode="json"))),
    ):
        _write_new_file(root, relative_path, payload)

    manifest.verify_workspace_files(root)
    return confirmed_intent, manifest


def fdax_research_context() -> dict[str, Any]:
    """Return the exact structured chart evidence sent to the research QueryEngine."""

    return _research_context(
        {
            instrument: _series_csv(instrument)
            for instrument, _venue, _role in INSTRUMENTS
        }
    )


def _require_empty_workspace(workspace: Path | str) -> Path:
    root = Path(workspace).expanduser().resolve(strict=True)
    if not root.is_dir():
        raise ValueError(f"fixture workspace is not a directory: {root}")
    if next(root.iterdir(), None) is not None:
        raise ValueError(f"fixture workspace must be empty: {root}")
    return root


def _write_new_file(root: Path, relative_path: str, payload: bytes) -> None:
    path = root / relative_path
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(payload)


def _pretty_json_bytes(value: dict[str, Any]) -> bytes:
    return (
        json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            indent=2,
            sort_keys=True,
        )
        + "\n"
    ).encode("utf-8")


def _confirmed_intent() -> dict[str, Any]:
    return {
        "family": "directional_long",
        "title": "FDAX jump confirmed by FESX, ES, and inverse VDAX",
        "summary": (
            "Observe five-minute FDAX moves, require at least two fresh comparison series, "
            "target a 10% long FDAX position after a confirmed bar closes, and explicitly close "
            "it six bars later. This is synthetic headless test evidence, not live-trading "
            "authority."
        ),
        "chart_context": {
            "primary": "FDAX",
            "comparisons": ["FESX", "ES", "VDAX"],
            "timeframe": TIMEFRAME,
            "selected_start_utc": _utc_text(SELECTED_START_UTC),
            "selected_end_utc": _utc_text(SELECTED_END_UTC),
            "as_of_utc": _utc_text(AS_OF_UTC),
            "data_source": DATA_SOURCE,
        },
        "decision": {
            "observe": "FDAX rises at least 0.80% from its previous five-minute close.",
            "qualify": (
                "At least two comparison series must have observations no more than five "
                "minutes old and agree: FESX rises at least 0.35%, ES rises at least 0.25%, "
                "or VDAX falls at least 2.00% from its previous observation."
            ),
            "no_trade": (
                "Do not enter when fewer than two fresh comparisons agree. A comparison more "
                "than five minutes old is stale and cannot count."
            ),
            "direction": "long_only",
        },
        "execution": {
            "instrument": "FDAX",
            "entry": (
                "After the confirmed bar closes, call "
                "self.order_target_percent(symbol='FDAX', target_percent=0.10)."
            ),
            "target_position_percent": 0.10,
            "backtest_initial_cash": 1_000_000.0,
            "commission_rate": 0.0003,
            "stamp_tax_rate": 0.0,
            "slippage_bps": 1.0,
            "comparison_instruments": "Evidence only; never submit orders for FESX, ES, or VDAX.",
            "additional_entries": "None in this first proof.",
        },
        "lifecycle": {
            "explicit_close": (
                "After six complete five-minute bars, call self.close_position(symbol='FDAX'); "
                "for the fixture's 10:00 entry signal, the expected close decision is at "
                "10:30 UTC."
            ),
            "finish": "Do not leave an intentionally open position after the explicit close.",
        },
        "scenarios": [
            {
                "name": "stale_comparison_no_trade",
                "timestamp_utc": _fixture_timestamp(_STALE_NO_TRADE_INDEX),
                "evidence": (
                    "FDAX and FESX cross their thresholds, ES does not, and VDAX's latest "
                    "observation is 08:55 UTC."
                ),
                "expected": "no_trade",
            },
            {
                "name": "confirmed_jump",
                "timestamp_utc": _fixture_timestamp(_CONFIRMED_JUMP_INDEX),
                "evidence": "FDAX, FESX, ES, and inverse VDAX all cross their thresholds.",
                "expected": "target_fdax_position_percent_0.10",
            },
            {
                "name": "explicit_close",
                "timestamp_utc": _fixture_timestamp(_EXPLICIT_CLOSE_INDEX),
                "evidence": "Six complete five-minute bars have elapsed after the entry signal.",
                "expected": "close_fdax_long",
            },
        ],
        "scenario_observability": {
            "csp_intent_stream": (
                "The three listed scenarios are the complete expected intent stream for this "
                "frozen run; emit no additional intent events."
            ),
            "no_trade": (
                "Represent the 09:05 rejected trigger as an explicit no_trade event, rather "
                "than as silence."
            ),
        },
        "expected_aggregates": {
            "vibequant": {"closed_trade_count": 1},
        },
        "native_boundaries": {
            "short": (
                "Not requested in this fixture: the pinned unmodified VibeQuant adapter has not "
                "demonstrated short execution because it does not enable AKQuant short selling."
            ),
            "vibequant_orders_and_fills": (
                "Unavailable from VibeQuant's public RunResult; do not claim raw order or fill "
                "evidence from this lane."
            ),
            "vibequant_comparison_role": (
                "VibeQuant loads all four files as universe symbols and does not enforce a "
                "non-tradable comparison role; generated source must order FDAX only."
            ),
            "csp_backtest": (
                "Unavailable: Point72 CSP runs the reactive graph and timestamped outputs but is "
                "not a trading backtester and produces no native fills, equity, P&L, or metrics."
            ),
        },
    }


def _research_context(series_payloads: dict[str, bytes]) -> dict[str, Any]:
    series = []
    for instrument, venue, role in INSTRUMENTS:
        reader = csv.DictReader(
            io.StringIO(series_payloads[instrument].decode("utf-8"))
        )
        previous_close: Decimal | None = None
        prior_volumes: list[Decimal] = []
        ema_4: Decimal | None = None
        ema_12: Decimal | None = None
        bars: list[dict[str, Any]] = []
        for row in reader:
            close = Decimal(row["close"])
            volume = Decimal(row["volume"])
            return_pct = (
                None
                if previous_close is None
                else _metric((close / previous_close - Decimal("1")) * Decimal("100"))
            )
            prior_window = prior_volumes[-12:]
            volume_ratio = (
                None
                if len(prior_window) < 12
                else _metric(volume / (sum(prior_window) / Decimal("12")))
            )
            ema_4 = _ema(close, ema_4, period=4)
            ema_12 = _ema(close, ema_12, period=12)
            timestamp = datetime.fromisoformat(row["timestamp"].replace("Z", "+00:00"))
            index = int((timestamp - SELECTED_START_UTC).total_seconds() // 300)
            if index in _RESEARCH_EVIDENCE_INDICES:
                bars.append(
                    {
                        "timestamp_utc": row["timestamp"],
                        "open": float(row["open"]),
                        "high": float(row["high"]),
                        "low": float(row["low"]),
                        "close": float(row["close"]),
                        "volume": int(row["volume"]),
                        "indicators": {
                            "return_from_previous_close_pct": return_pct,
                            "volume_ratio_to_previous_12_mean": volume_ratio,
                            "ema_4": _metric(ema_4),
                            "ema_12": _metric(ema_12),
                        },
                    }
                )
            previous_close = close
            prior_volumes.append(volume)
        series.append(
            {
                "instrument": instrument,
                "venue": venue,
                "role": role,
                "timeframe": TIMEFRAME,
                "bars": bars,
            }
        )
    return {
        "schema_version": "daxalgo-frozen-chart-context/v1",
        "primary": "FDAX",
        "comparisons": ["FESX", "ES", "VDAX"],
        "selected_start_utc": _utc_text(SELECTED_START_UTC),
        "selected_end_utc": _utc_text(SELECTED_END_UTC),
        "as_of_utc": _utc_text(AS_OF_UTC),
        "timezone": "Europe/Berlin",
        "timeframe": TIMEFRAME,
        "data_source": DATA_SOURCE,
        "evidence_scope": (
            "Event-focused structured chart snapshot: the stale-trigger window, the confirmed "
            "jump, and every primary-bar time through the six-bar close. Full manifest-bound "
            "CSV histories are supplied separately to native execution."
        ),
        "selected_event_times_utc": [
            _fixture_timestamp(_STALE_NO_TRADE_INDEX),
            _fixture_timestamp(_CONFIRMED_JUMP_INDEX),
            _fixture_timestamp(_EXPLICIT_CLOSE_INDEX),
        ],
        "indicator_definitions": {
            "return_from_previous_close_pct": "Causal one-bar close return in percent.",
            "volume_ratio_to_previous_12_mean": (
                "Current volume divided by the mean of the twelve strictly prior bars; null "
                "until twelve prior bars exist."
            ),
            "ema_4": "Causal four-bar exponential moving average seeded from the first close.",
            "ema_12": (
                "Causal twelve-bar exponential moving average seeded from the first close."
            ),
        },
        "series": series,
    }


def _ema(close: Decimal, previous: Decimal | None, *, period: int) -> Decimal:
    if previous is None:
        return close
    alpha = Decimal("2") / Decimal(period + 1)
    return alpha * close + (Decimal("1") - alpha) * previous


def _metric(value: Decimal) -> float:
    return float(value.quantize(Decimal("0.000001"), rounding=ROUND_HALF_UP))


def _series_csv(instrument: str) -> bytes:
    if instrument not in _INITIAL_CLOSE:
        raise ValueError(f"unsupported fixture instrument: {instrument}")
    lines = [",".join(CSV_COLUMNS)]
    previous_close = _INITIAL_CLOSE[instrument]
    for index in range(48):
        timestamp = SELECTED_START_UTC + timedelta(minutes=5 * index)
        if instrument == "VDAX" and index in _VDAX_MISSING_INDICES:
            continue
        return_rate = _return_for(instrument, index)
        close = _price(previous_close * (Decimal("1") + return_rate))
        open_price = previous_close
        spread = max(open_price, close) * Decimal("0.00035")
        high = _price(max(open_price, close) + spread)
        low = _price(min(open_price, close) - spread)
        volume = _volume(instrument, index)
        timestamp_text = _utc_text(timestamp)
        lines.append(
            ",".join(
                (
                    timestamp_text,
                    timestamp_text,
                    _price_text(open_price),
                    _price_text(high),
                    _price_text(low),
                    _price_text(close),
                    str(volume),
                    instrument,
                )
            )
        )
        previous_close = close
    return ("\n".join(lines) + "\n").encode("utf-8")


def _return_for(instrument: str, index: int) -> Decimal:
    if index == _STALE_NO_TRADE_INDEX:
        return {
            "FDAX": Decimal("0.0085"),
            "FESX": Decimal("0.0040"),
            "ES": Decimal("0.0005"),
            "VDAX": Decimal("0"),
        }[instrument]
    if index == _CONFIRMED_JUMP_INDEX:
        return {
            "FDAX": Decimal("0.0090"),
            "FESX": Decimal("0.0040"),
            "ES": Decimal("0.0030"),
            "VDAX": Decimal("-0.0210"),
        }[instrument]
    return _NORMAL_RETURN[instrument]


def _volume(instrument: str, index: int) -> int:
    base = {"FDAX": 800, "FESX": 1200, "ES": 2200, "VDAX": 350}[instrument]
    multiplier = 3 if index in {_STALE_NO_TRADE_INDEX, _CONFIRMED_JUMP_INDEX} else 1
    return (base + (index % 7) * 17) * multiplier


def _price(value: Decimal) -> Decimal:
    return value.quantize(Decimal("0.01"), rounding=ROUND_HALF_UP)


def _price_text(value: Decimal) -> str:
    return format(_price(value), ".2f")


def _utc_text(value: datetime) -> str:
    return value.astimezone(UTC).isoformat().replace("+00:00", "Z")


def _fixture_timestamp(index: int) -> str:
    return _utc_text(SELECTED_START_UTC + timedelta(minutes=5 * index))
