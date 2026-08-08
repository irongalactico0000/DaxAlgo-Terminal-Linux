"""Reproducible provider-backed proof of the one native strategy workflow."""

from __future__ import annotations

from pathlib import Path
from typing import Any

from .composition import ProductionApplication
from .headless_fixture import (
    fdax_research_context,
    write_fdax_directional_long_fixture,
)


async def run_fdax_fixture_proof(
    application: ProductionApplication,
    input_workspace: Path,
    *,
    timeout_seconds: float = 900,
) -> dict[str, Any]:
    """Run research, confirmation, both native lanes, and comparison once.

    The caller must provide a path that does not yet exist. This function never replaces prior
    proof data and returns only secret-free retained evidence identifiers.
    """

    root = input_workspace.expanduser().resolve()
    if root.exists():
        raise ValueError(f"proof input workspace already exists: {root}")
    root.mkdir(mode=0o700, parents=True)
    confirmed_intent, manifest = write_fdax_directional_long_fixture(root)
    chart_context = fdax_research_context()

    session = application.service.create_research_session(chart_context)
    session_id = session["session_id"]
    await application.service.submit_research_message(
        session_id,
        (
            "Review the frozen FDAX chart event with FESX, ES, and VDAX confirmation. "
            "Use the supplied structured OHLCV bars and causal indicator values. Explain the "
            "09:05 rejected trigger, the 10:00 confirmed long, and the 10:30 lifecycle close. "
            "State what the evidence proves and what remains unavailable. Research only; do "
            "not write strategy code."
        ),
    )
    application.service.confirm_run(
        session_id,
        manifest,
        root,
        confirmed_intent,
    )
    await application.service.start_run(manifest.run_id)
    terminal = await application.service.wait_for_run(
        manifest.run_id, timeout=timeout_seconds
    )

    results = terminal["results"]
    comparison = terminal.get("comparison")
    return {
        "run_id": manifest.run_id,
        "session_id": session_id,
        "confirmation_mode": "scripted_headless_fixture",
        "status": terminal["status"],
        "evidence_status": terminal.get("evidence_status"),
        "manifest_sha256": manifest.manifest_sha256,
        "research_context_sha256": manifest.research_context_sha256,
        "confirmed_intent_sha256": manifest.confirmed_intent_sha256,
        "lane_states": terminal["lane_states"],
        "lanes": {
            lane: {
                "status": result["status"],
                "native_stage": result["native_stage"],
                "framework": result["framework"],
                "framework_version": result["framework_version"],
                "artifact_relative_paths": result.get("artifact_relative_paths", []),
                **({"error": result["error"]} if result.get("error") else {}),
            }
            for lane, result in sorted(results.items())
        },
        "comparison": (
            {
                "relative_path": comparison["relative_path"],
                "sha256": comparison["sha256"],
                "report_hash": comparison["report"]["report_hash"],
            }
            if comparison is not None
            else None
        ),
        "last_event_sequence": terminal["last_event_sequence"],
    }


__all__ = ["run_fdax_fixture_proof"]
