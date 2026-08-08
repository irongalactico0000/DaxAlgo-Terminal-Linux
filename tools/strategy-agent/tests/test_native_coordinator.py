from __future__ import annotations

import asyncio
import hashlib
import inspect
import json
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest

import daxalgo_strategy_agent.native_coordinator as coordinator_module
import daxalgo_strategy_agent.profiles as profiles_module
from daxalgo_strategy_agent.native import csp_worker, vibequant_worker
from daxalgo_strategy_agent.contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
    NativeLaneResult,
    canonical_json_bytes,
    confirmed_intent_sha256,
)
from daxalgo_strategy_agent.native_coordinator import CoordinatedNativeRunners
from daxalgo_strategy_agent.queryengine_runtime import (
    DEFAULT_COORDINATOR_TIMEOUT_SECONDS,
    EXPECTED_FINANCEMANUS_REVISION,
    CoordinatorWorkerOutcome,
    WorkerProfile,
)


CONFIRMED_INTENT = {
    "family": "directional_long_short",
    "strategy": "Enter long only after the frozen primary and comparison series confirm.",
    "scenarios": [
        {"name": "confirmed_jump", "expected": "long"},
        {"name": "stale_comparison", "expected": "no_trade"},
    ],
}


class _FakeTool:
    pass


@dataclass
class _FakeToolResult:
    output: str = ""
    error: str | None = None
    is_error: bool = False

    @classmethod
    def success(cls, output: str, **_artifacts: Any) -> "_FakeToolResult":
        return cls(output=output)

    @classmethod
    def failure(cls, error: str, **_artifacts: Any) -> "_FakeToolResult":
        return cls(error=error, is_error=True)


@dataclass(frozen=True)
class _FakeEvent:
    type: str
    data: dict[str, Any]


class _InvokingDispatch:
    instances: list["_InvokingDispatch"] = []

    def __init__(self, bindings: Any, **kwargs: Any) -> None:
        self.bindings = bindings
        self.run_hash = kwargs["run_hash"]
        self.payload = kwargs["payload"]
        self.profiles = kwargs["profiles"]
        self.workspaces = kwargs["workspaces"]
        self.event_sink = kwargs["event_sink"]
        self.run_calls = 0
        self.events = {
            WorkerProfile.VIBEQUANT: _FakeEvent("tool_start", {"lane": "vibequant"}),
            WorkerProfile.CSP: _FakeEvent("tool_start", {"lane": "csp"}),
        }
        self.__class__.instances.append(self)

    async def run(self, *, timeout_seconds: int) -> str:
        assert timeout_seconds == 17
        self.run_calls += 1
        await asyncio.sleep(0)
        for profile in (WorkerProfile.VIBEQUANT, WorkerProfile.CSP):
            observed = self.event_sink(self.run_hash, profile, self.events[profile])
            if inspect.isawaitable(observed):
                await observed
            fixed_profile = self.profiles[profile]
            assert fixed_profile.profile is profile
            assert len(fixed_profile.tool_factories) == 1
            tool = fixed_profile.tool_factories[0]()
            context = SimpleNamespace(working_dir=self.workspaces[profile])
            if profile is WorkerProfile.VIBEQUANT:
                tool_result = await tool.call(
                    {"task_spec": {"kind": "strategy", "name": "confirmed-jump"}},
                    context,
                )
            else:
                tool_result = await tool.call(
                    {"source": "def build_graph(request):\n    return request\n"},
                    context,
                )
            assert not tool_result.is_error, tool_result.error
        return "both fixed workers completed"


class _NoSubmissionDispatch(_InvokingDispatch):
    instances: list["_NoSubmissionDispatch"] = []

    async def run(self, *, timeout_seconds: int) -> str:
        assert timeout_seconds == 17
        self.run_calls += 1
        await asyncio.sleep(0)
        return "agents returned text without native tool calls"


class _TimedOutDispatch(_NoSubmissionDispatch):
    instances: list["_TimedOutDispatch"] = []

    async def run(self, *, timeout_seconds: int) -> str:
        result = await super().run(timeout_seconds=timeout_seconds)
        self.worker_outcomes = {
            WorkerProfile.VIBEQUANT: CoordinatorWorkerOutcome(
                status="failed", error="Timeout"
            ),
            WorkerProfile.CSP: CoordinatorWorkerOutcome(
                status="completed", error=""
            ),
        }
        return result


@pytest.fixture(autouse=True)
def _fake_tool_contract(monkeypatch: pytest.MonkeyPatch) -> None:
    _InvokingDispatch.instances.clear()
    _NoSubmissionDispatch.instances.clear()
    _TimedOutDispatch.instances.clear()
    monkeypatch.setattr(
        profiles_module,
        "_load_financemanus_tool_contract",
        lambda: (_FakeTool, _FakeToolResult),
    )


def _retained_run(tmp_path: Path) -> tuple[FrozenRunManifest, Path]:
    root = tmp_path / "retained" / "native-run-1"
    (root / "data").mkdir(parents=True)
    primary = root / "data" / "FDAX.csv"
    comparison = root / "data" / "FESX.csv"
    primary.write_text(
        "timestamp,close\n2026-08-08T00:00:00Z,100\n",
        encoding="utf-8",
    )
    comparison.write_text(
        "timestamp,close\n2026-08-08T00:00:00Z,50\n",
        encoding="utf-8",
    )
    manifest = FrozenRunManifest(
        run_id="native-run-1",
        confirmed_intent_sha256=confirmed_intent_sha256(CONFIRMED_INTENT),
        selected_start_utc=datetime(2026, 8, 8, 0, 0, tzinfo=timezone.utc),
        selected_end_utc=datetime(2026, 8, 8, 0, 5, tzinfo=timezone.utc),
        as_of_utc=datetime(2026, 8, 8, 0, 10, tzinfo=timezone.utc),
        timezone_name="UTC",
        data_files=(
            FrozenDataFile(
                role="primary",
                instrument="FDAX",
                venue="EUREX",
                source="fixture",
                timeframe="5m",
                relative_path="data/FDAX.csv",
                sha256=hashlib.sha256(primary.read_bytes()).hexdigest(),
            ),
            FrozenDataFile(
                role="comparison",
                instrument="FESX",
                venue="EUREX",
                source="fixture",
                timeframe="5m",
                relative_path="data/FESX.csv",
                sha256=hashlib.sha256(comparison.read_bytes()).hexdigest(),
            ),
        ),
        components=(
            ComponentPin(
                component="query_engine",
                version="source",
                source_revision=EXPECTED_FINANCEMANUS_REVISION,
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
    intent = root / "research" / "confirmed-intent.json"
    intent.parent.mkdir()
    intent.write_bytes(canonical_json_bytes(CONFIRMED_INTENT))
    return manifest, root


def _native_result(
    manifest: FrozenRunManifest,
    workspace: Path,
    *,
    lane: str,
    source_relative_path: str,
) -> NativeLaneResult:
    source = workspace / source_relative_path
    return NativeLaneResult(
        run_id=manifest.run_id,
        lane=lane,
        manifest_sha256=manifest.manifest_sha256,
        status="passed",
        native_stage="run_task" if lane == "vibequant" else "csp.run",
        framework="transcend-0/VibeQuant" if lane == "vibequant" else "Point72 CSP",
        framework_version="0.1.0" if lane == "vibequant" else "0.18.0",
        source_relative_path=source_relative_path,
        artifact_relative_paths=(source_relative_path,),
        artifact_sha256={
            source_relative_path: hashlib.sha256(source.read_bytes()).hexdigest()
        },
        observations={"native": True},
    )


def _adapter(
    monkeypatch: pytest.MonkeyPatch,
    dispatch_type: type[_InvokingDispatch],
    events: list[tuple[str, str, WorkerProfile, _FakeEvent]],
) -> CoordinatedNativeRunners:
    monkeypatch.setattr(coordinator_module, "CoordinatorDispatch", dispatch_type)

    def vibequant_runner(
        manifest: FrozenRunManifest,
        workspace: Path,
        *,
        task_spec_relative_path: str,
    ) -> NativeLaneResult:
        return _native_result(
            manifest,
            workspace,
            lane="vibequant",
            source_relative_path=task_spec_relative_path,
        )

    def csp_runner(
        manifest: FrozenRunManifest,
        workspace: Path,
        *,
        source_relative_path: str,
    ) -> NativeLaneResult:
        return _native_result(
            manifest,
            workspace,
            lane="csp",
            source_relative_path=source_relative_path,
        )

    async def event_sink(
        run_id: str,
        run_hash: str,
        profile: WorkerProfile,
        event: _FakeEvent,
    ) -> None:
        events.append((run_id, run_hash, profile, event))

    return CoordinatedNativeRunners(
        bindings=SimpleNamespace(
            name="real-bindings-at-composition-time",
            source=SimpleNamespace(revision=EXPECTED_FINANCEMANUS_REVISION),
        ),
        vibequant_config_factory=lambda: SimpleNamespace(model="fixed-vibe-model"),
        csp_config_factory=lambda: SimpleNamespace(model="fixed-csp-model"),
        vibequant_native_runner=vibequant_runner,
        csp_native_runner=csp_runner,
        event_sink=event_sink,
        query_engine_identity={
            "source_revision": EXPECTED_FINANCEMANUS_REVISION,
            "python_version": "3.12.12",
            "model": "test/provider-model",
            "fallback_model": "test/provider-model",
            "providers": ["test"],
        },
        timeout_seconds=17,
    )


@pytest.mark.asyncio
async def test_concurrent_service_callbacks_share_one_dispatch_and_retain_native_artifacts(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest, retained_root = _retained_run(tmp_path)
    forwarded: list[tuple[str, str, WorkerProfile, _FakeEvent]] = []
    adapter = _adapter(monkeypatch, _InvokingDispatch, forwarded)

    vibe_result, csp_result = await asyncio.gather(
        adapter.vibequant(manifest, retained_root),
        adapter.csp(manifest, retained_root),
    )

    assert adapter.prepared_run_count == 1
    assert len(_InvokingDispatch.instances) == 1
    dispatch = _InvokingDispatch.instances[0]
    assert dispatch.run_calls == 1
    assert dispatch.run_hash == hashlib.sha256(dispatch.payload).hexdigest()
    assert (
        retained_root / "coordinator" / "confirmed-job.json"
    ).read_bytes() == dispatch.payload
    confirmed_job = json.loads(dispatch.payload)
    assert confirmed_job["confirmed_intent"] == CONFIRMED_INTENT
    assert confirmed_job["confirmed_intent_sha256"] == manifest.confirmed_intent_sha256
    assert confirmed_job["manifest_sha256"] == manifest.manifest_sha256
    assert confirmed_job["query_engine_runtime"] == {
        "source_revision": EXPECTED_FINANCEMANUS_REVISION,
        "python_version": "3.12.12",
        "model": "test/provider-model",
        "fallback_model": "test/provider-model",
        "providers": ["test"],
    }

    vibe_workspace = dispatch.workspaces[WorkerProfile.VIBEQUANT]
    csp_workspace = dispatch.workspaces[WorkerProfile.CSP]
    assert vibe_workspace != csp_workspace
    assert vibe_workspace.is_relative_to(retained_root)
    assert csp_workspace.is_relative_to(retained_root)
    for workspace in (vibe_workspace, csp_workspace):
        manifest.verify_workspace_files(workspace)
        assert (workspace / "data" / "FDAX.csv").read_bytes() == (
            retained_root / "data" / "FDAX.csv"
        ).read_bytes()

    assert vibe_result.artifact_relative_paths == (
        "lanes/vibequant/agent-input/vibequant-task-spec.json",
    )
    assert csp_result.artifact_relative_paths == (
        "lanes/csp/agent-input/csp-strategy.py",
    )
    for result in (vibe_result, csp_result):
        assert (
            result.observations["query_engine_runtime"]
            == confirmed_job["query_engine_runtime"]
        )
        assert result.source_relative_path in result.artifact_relative_paths
        for relative_path, digest in result.artifact_sha256.items():
            assert (
                hashlib.sha256((retained_root / relative_path).read_bytes()).hexdigest()
                == digest
            )

    assert [(profile, event) for _run_id, _hash, profile, event in forwarded] == [
        (WorkerProfile.VIBEQUANT, dispatch.events[WorkerProfile.VIBEQUANT]),
        (WorkerProfile.CSP, dispatch.events[WorkerProfile.CSP]),
    ]
    assert all(
        run_id == manifest.run_id for run_id, _hash, _profile, _event in forwarded
    )
    assert all(
        run_hash == dispatch.run_hash
        for _run_id, run_hash, _profile, _event in forwarded
    )
    assert forwarded[0][3] is dispatch.events[WorkerProfile.VIBEQUANT]
    assert forwarded[1][3] is dispatch.events[WorkerProfile.CSP]


@pytest.mark.asyncio
async def test_agents_that_never_call_their_native_tool_return_exact_failures(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest, retained_root = _retained_run(tmp_path)
    adapter = _adapter(monkeypatch, _NoSubmissionDispatch, [])

    vibe_result, csp_result = await asyncio.gather(
        adapter.vibequant(manifest, retained_root),
        adapter.csp(manifest, retained_root),
    )

    assert len(_NoSubmissionDispatch.instances) == 1
    assert _NoSubmissionDispatch.instances[0].run_calls == 1
    assert vibe_result.status == "failed"
    assert vibe_result.native_stage == "agent_submission"
    assert vibe_result.error == (
        "vibequant agent completed without calling submit_vibequant_task_spec."
    )
    assert csp_result.status == "failed"
    assert csp_result.native_stage == "agent_submission"
    assert csp_result.error == "csp agent completed without calling submit_csp_source."
    assert vibe_result.observations["submission_observed"] is False
    assert csp_result.observations["submission_observed"] is False


@pytest.mark.asyncio
async def test_swallowed_worker_timeout_is_retained_as_exact_lane_failure(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest, retained_root = _retained_run(tmp_path)
    adapter = _adapter(monkeypatch, _TimedOutDispatch, [])

    vibe_result, csp_result = await asyncio.gather(
        adapter.vibequant(manifest, retained_root),
        adapter.csp(manifest, retained_root),
    )

    assert vibe_result.status == "failed"
    assert vibe_result.native_stage == "agent_timeout"
    assert vibe_result.error == (
        "vibequant FinanceManus worker exceeded the 17-second coordinator timeout "
        "before calling submit_vibequant_task_spec."
    )
    assert vibe_result.observations["failure_code"] == "agent_timeout"
    assert vibe_result.observations["coordinator_worker_status"] == "failed"
    assert vibe_result.observations["coordinator_worker_error"] == "Timeout"
    assert vibe_result.observations["coordinator_timeout_seconds"] == 17
    assert csp_result.native_stage == "agent_submission"
    assert csp_result.error == "csp agent completed without calling submit_csp_source."


def test_native_child_timeouts_are_below_the_coordinator_timeout() -> None:
    assert (
        vibequant_worker.WORKER_TIMEOUT_SECONDS
        < DEFAULT_COORDINATOR_TIMEOUT_SECONDS
    )
    assert csp_worker._CHILD_TIMEOUT_SECONDS < DEFAULT_COORDINATOR_TIMEOUT_SECONDS


def test_preparation_rejects_confirmed_intent_not_bound_by_manifest(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest, retained_root = _retained_run(tmp_path)
    (retained_root / "research" / "confirmed-intent.json").write_bytes(
        canonical_json_bytes({"strategy": "different"})
    )
    adapter = _adapter(monkeypatch, _NoSubmissionDispatch, [])

    with pytest.raises(
        ValueError, match="confirmed intent hash does not match manifest"
    ):
        asyncio.run(adapter.vibequant(manifest, retained_root))

    assert adapter.prepared_run_count == 0


def test_preparation_rejects_manifest_claiming_another_query_engine_revision(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest, retained_root = _retained_run(tmp_path)
    components = tuple(
        item.model_copy(update={"source_revision": "0" * 40})
        if item.component == "query_engine"
        else item
        for item in manifest.components
    )
    mismatched = manifest.model_copy(update={"components": components})
    adapter = _adapter(monkeypatch, _NoSubmissionDispatch, [])

    with pytest.raises(ValueError, match="loaded FinanceManus runtime"):
        asyncio.run(adapter.vibequant(mismatched, retained_root))

    assert adapter.prepared_run_count == 0
    assert not (retained_root / "lanes").exists()
