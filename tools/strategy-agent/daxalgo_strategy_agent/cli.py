"""Command line entry point for the production strategy-agent API."""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
from collections.abc import Sequence
from dataclasses import replace
from pathlib import Path
from typing import Any

import uvicorn

from .composition import build_application, preflight
from .queryengine_runtime import RuntimeGateError
from .proof import run_fdax_fixture_proof
from .service import StrategyServiceError
from .settings import StrategyAgentConfigurationError, StrategyAgentSettings


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="daxalgo-strategy-agent",
        description=(
            "Run or preflight the one production QueryEngine -> VibeQuant/CSP workflow."
        ),
    )
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser(
        "preflight",
        help="verify pinned sources, interpreter, model provider, and credentials",
    )
    serve = commands.add_parser("serve", help="serve the production loopback API")
    serve.add_argument("--port", type=int, default=8765)
    serve.add_argument(
        "--log-level",
        choices=("critical", "error", "warning", "info"),
        default="info",
    )
    proof = commands.add_parser(
        "prove-fdax-fixture",
        help="run the real research -> VibeQuant/AKQuant + CSP -> comparison proof",
    )
    proof.add_argument("--input-workspace", required=True)
    proof.add_argument("--store-root", required=True)
    proof.add_argument("--timeout-seconds", type=float, default=900)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    arguments = _parser().parse_args(argv)
    try:
        settings = StrategyAgentSettings.from_environment()
        if arguments.command == "preflight":
            report = preflight(settings)
            print(json.dumps(report.as_dict(), ensure_ascii=False, sort_keys=True))
            return 0
        if arguments.command == "prove-fdax-fixture":
            input_workspace = Path(arguments.input_workspace).expanduser().resolve()
            store_root = Path(arguments.store_root).expanduser().resolve()
            if store_root.exists():
                raise StrategyAgentConfigurationError(
                    "fixture_store", f"proof store root already exists: {store_root}"
                )
            if arguments.timeout_seconds <= 0:
                raise StrategyAgentConfigurationError(
                    "fixture_timeout", "--timeout-seconds must be positive"
                )
            application = build_application(replace(settings, store_root=store_root))
            result = asyncio.run(
                run_fdax_fixture_proof(
                    application,
                    input_workspace,
                    timeout_seconds=arguments.timeout_seconds,
                )
            )
            print(json.dumps(result, ensure_ascii=False, sort_keys=True))
            return 0 if result["status"] == "completed" else 1
        if not 1 <= arguments.port <= 65_535:
            raise StrategyAgentConfigurationError(
                "loopback_port", "--port must be between 1 and 65535"
            )
        application = build_application(settings)
        uvicorn.run(
            application.app,
            host="127.0.0.1",
            port=arguments.port,
            log_level=arguments.log_level,
        )
        return 0
    except (
        StrategyAgentConfigurationError,
        RuntimeGateError,
        StrategyServiceError,
        OSError,
        ValueError,
    ) as exc:
        _print_failure(exc)
        return 2
    except KeyboardInterrupt:
        return 130


def _print_failure(error: Exception) -> None:
    stage = getattr(error, "stage", None) or getattr(error, "code", None)
    detail = getattr(error, "detail", None) or str(error)
    payload: dict[str, Any] = {
        "status": "failed",
        "stage": stage or "composition",
        "reason": detail,
    }
    print(json.dumps(payload, ensure_ascii=False, sort_keys=True), file=sys.stderr)


if __name__ == "__main__":  # pragma: no cover - console-script entry point
    raise SystemExit(main())
