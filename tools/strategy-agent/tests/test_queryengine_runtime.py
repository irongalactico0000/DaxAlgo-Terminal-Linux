from __future__ import annotations

import hashlib
import os
import secrets
import subprocess
from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest

import daxalgo_strategy_agent.queryengine_runtime as runtime_module

from daxalgo_strategy_agent.queryengine_runtime import (
    DISPATCH_PROMPT_PREFIX,
    EXPECTED_FINANCEMANUS_REVISION,
    CoordinatorDispatch,
    DispatchRejected,
    FixedQueryEngineProfile,
    RuntimeGateError,
    SingleUseDispatchTokens,
    WorkerProfile,
    create_query_engine,
    load_financemanus,
    stream_queryengine_events,
)


PAYLOAD = b'{"confirmed_intent_sha256":"abc","scenario_sha256":"def"}'
RUN_A = hashlib.sha256(PAYLOAD).hexdigest()
RUN_B = "b" * 64


def _workspace(tmp_path: Path, name: str) -> Path:
    path = tmp_path / name
    path.mkdir()
    return path


def _configured_financemanus_root() -> Path:
    source_value = os.environ.get("DAXALGO_QUERY_ENGINE_ROOT", "").strip()
    if not source_value:
        pytest.skip(
            "set DAXALGO_QUERY_ENGINE_ROOT for the pinned FinanceManus runtime smoke"
        )
    return Path(source_value)


def test_single_use_tokens_are_opaque_reject_cross_run_and_detect_replay(
    tmp_path: Path,
) -> None:
    workspace = _workspace(tmp_path, "vibequant")
    tokens = SingleUseDispatchTokens()
    prompt = tokens.issue(
        run_hash=RUN_A,
        profile=WorkerProfile.VIBEQUANT,
        payload=PAYLOAD,
        workspace=workspace,
    )

    assert prompt.startswith(DISPATCH_PROMPT_PREFIX)
    assert len(prompt.removeprefix(DISPATCH_PROMPT_PREFIX)) == 43
    assert RUN_A not in prompt
    assert WorkerProfile.VIBEQUANT.value not in prompt
    assert PAYLOAD.decode() not in prompt

    with pytest.raises(DispatchRejected) as cross_run:
        tokens.consume(prompt, expected_run_hash=RUN_B)
    assert cross_run.value.code == "cross_run_dispatch_token"
    assert tokens.pending_count == 1
    assert tokens.consumed_count == 0

    record = tokens.consume(prompt, expected_run_hash=RUN_A)
    assert record.run_hash == RUN_A
    assert record.profile is WorkerProfile.VIBEQUANT
    assert record.payload is PAYLOAD
    assert record.workspace == workspace.resolve()
    assert tokens.pending_count == 0
    assert tokens.consumed_count == 1

    with pytest.raises(DispatchRejected) as replay:
        tokens.consume(prompt, expected_run_hash=RUN_A)
    assert replay.value.code == "replayed_dispatch_token"


@pytest.mark.parametrize(
    "prompt,code",
    [
        ("vibequant", "malformed_dispatch_prompt"),
        (f"{DISPATCH_PROMPT_PREFIX}too-short", "malformed_dispatch_prompt"),
        (f"{DISPATCH_PROMPT_PREFIX}{'A' * 43}", "unknown_dispatch_token"),
    ],
)
def test_dispatch_rejects_malformed_and_unknown_prompts(
    prompt: str,
    code: str,
) -> None:
    with pytest.raises(DispatchRejected) as rejected:
        SingleUseDispatchTokens().consume(prompt, expected_run_hash=RUN_A)
    assert rejected.value.code == code


def test_dispatch_issue_requires_host_enum_and_immutable_utf8_payload(
    tmp_path: Path,
) -> None:
    workspace = _workspace(tmp_path, "workspace")
    tokens = SingleUseDispatchTokens()
    with pytest.raises(TypeError, match="VIBEQUANT or WorkerProfile.CSP"):
        tokens.issue(
            run_hash=RUN_A,
            profile=WorkerProfile.RESEARCH,
            payload=PAYLOAD,
            workspace=workspace,
        )
    with pytest.raises(TypeError, match="immutable bytes"):
        tokens.issue(
            run_hash=RUN_A,
            profile=WorkerProfile.CSP,
            payload=bytearray(PAYLOAD),  # type: ignore[arg-type]
            workspace=workspace,
        )
    with pytest.raises(ValueError, match="payload SHA-256"):
        tokens.issue(
            run_hash=RUN_B,
            profile=WorkerProfile.CSP,
            payload=PAYLOAD,
            workspace=workspace,
        )


class _FakeRegistry:
    def __init__(self) -> None:
        self.tools: list[Any] = []

    def register(self, tool: Any) -> None:
        self.tools.append(tool)


class _FakeContextManager:
    def __init__(self, **kwargs: Any) -> None:
        self.kwargs = kwargs


class _FakeSession:
    def __init__(self, **kwargs: Any) -> None:
        self.kwargs = kwargs


class _FakeQueryEngine:
    def __init__(self, **kwargs: Any) -> None:
        self.kwargs = kwargs


@dataclass(frozen=True)
class _FakeTool:
    name: str


def test_create_query_engine_uses_explicit_workspace_session_and_fresh_fixed_registry(
    tmp_path: Path,
) -> None:
    workspace = _workspace(tmp_path, "worker")
    factory_calls: list[str] = []

    def tool_factory() -> _FakeTool:
        factory_calls.append("called")
        return _FakeTool("native_run")

    config = SimpleNamespace(model="host-fixed-model")
    profile = FixedQueryEngineProfile(
        profile=WorkerProfile.CSP,
        system_prompt="Fixed CSP instructions",
        config_factory=lambda: config,
        tool_factories=(tool_factory,),
        max_turns=4,
    )
    bindings = SimpleNamespace(
        ToolRegistry=_FakeRegistry,
        ContextManager=_FakeContextManager,
        Session=_FakeSession,
        QueryEngine=_FakeQueryEngine,
    )

    first = create_query_engine(
        bindings,
        profile=profile,
        workspace=workspace,
        session_output_root=Path("state/sessions"),
        task_id="csp-aaaaaaaaaaaaaaaa",
    )
    second = create_query_engine(
        bindings,
        profile=profile,
        workspace=workspace,
        session_output_root=Path("state/sessions"),
        task_id="csp-bbbbbbbbbbbbbbbb",
    )

    assert first.workspace == workspace.resolve()
    assert first.session_output_root == (workspace / "state/sessions").resolve()
    assert first.registry is not second.registry
    assert first.registry.tools == [_FakeTool("native_run")]
    assert second.registry.tools == [_FakeTool("native_run")]
    assert factory_calls == ["called", "called"]
    assert first.context_manager.kwargs == {
        "working_dir": workspace.resolve(),
        "custom_system_prompt": "Fixed CSP instructions",
    }
    assert (
        first.session.kwargs["output_dir"] == (workspace / "state/sessions").resolve()
    )
    assert first.session.kwargs["model"] == "host-fixed-model"
    assert first.engine.kwargs == {
        "config": config,
        "tool_registry": first.registry,
        "context_manager": first.context_manager,
        "session": first.session,
    }

    with pytest.raises(ValueError, match="must remain inside workspace"):
        create_query_engine(
            bindings,
            profile=profile,
            workspace=workspace,
            session_output_root=tmp_path / "escaped",
            task_id="csp-escape",
        )


@dataclass(frozen=True)
class _FakeEvent:
    type: str
    data: dict[str, Any]


class _StreamingEngine:
    def __init__(self, events: list[_FakeEvent]) -> None:
        self.events = events
        self.calls: list[tuple[str, int | None]] = []

    async def stream_submit_message(self, prompt: str, *, max_turns: int | None = None):
        self.calls.append((prompt, max_turns))
        for event in self.events:
            yield event


@pytest.mark.asyncio
async def test_stream_queryengine_events_forwards_original_events_without_translation() -> (
    None
):
    events = [
        _FakeEvent("request_start", {"session_id": "s1"}),
        _FakeEvent("text_delta", {"text": "answer"}),
        _FakeEvent("message_stop", {"stop_reason": "end_turn"}),
    ]
    engine = _StreamingEngine(events)

    forwarded = [
        event
        async for event in stream_queryengine_events(
            engine, "sealed payload", max_turns=3
        )
    ]

    assert forwarded == events
    assert all(observed is original for observed, original in zip(forwarded, events))
    assert engine.calls == [("sealed payload", 3)]


@dataclass
class _FakeWorkerTask:
    id: str
    prompt: str
    description: str = ""
    agent_name: str = ""


def _native_profile(profile: WorkerProfile, max_turns: int) -> FixedQueryEngineProfile:
    return FixedQueryEngineProfile(
        profile=profile,
        system_prompt=f"Host-fixed {profile.value} prompt",
        config_factory=lambda: SimpleNamespace(model="fixed"),
        tool_factories=(),
        max_turns=max_turns,
    )


@pytest.mark.asyncio
async def test_coordinator_callbacks_plan_two_opaque_tasks_and_dispatch_fixed_profiles(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    vibe_workspace = _workspace(tmp_path, "vibe")
    csp_workspace = _workspace(tmp_path, "csp")
    profiles = {
        WorkerProfile.VIBEQUANT: _native_profile(WorkerProfile.VIBEQUANT, 2),
        WorkerProfile.CSP: _native_profile(WorkerProfile.CSP, 5),
    }
    workspaces = {
        WorkerProfile.VIBEQUANT: vibe_workspace,
        WorkerProfile.CSP: csp_workspace,
    }
    built: list[dict[str, Any]] = []
    engines: dict[WorkerProfile, _StreamingEngine] = {
        WorkerProfile.VIBEQUANT: _StreamingEngine(
            [_FakeEvent("text_delta", {"text": "vibe-result"})]
        ),
        WorkerProfile.CSP: _StreamingEngine(
            [
                _FakeEvent("request_start", {"session_id": "csp"}),
                _FakeEvent("text_delta", {"text": "csp-result"}),
            ]
        ),
    }

    def engine_builder(_bindings: Any, **kwargs: Any) -> Any:
        built.append(kwargs)
        profile = kwargs["profile"].profile
        return SimpleNamespace(engine=engines[profile])

    forwarded: list[tuple[str, WorkerProfile, _FakeEvent]] = []

    async def event_sink(
        run_hash: str, profile: WorkerProfile, event: _FakeEvent
    ) -> None:
        forwarded.append((run_hash, profile, event))

    monkeypatch.setattr(runtime_module, "create_query_engine", engine_builder)

    dispatch = CoordinatorDispatch(
        SimpleNamespace(WorkerTask=_FakeWorkerTask),
        run_hash=RUN_A,
        payload=PAYLOAD,
        profiles=profiles,
        workspaces=workspaces,
        event_sink=event_sink,
    )

    tasks = await dispatch.plan_fn("ignored model/user text")
    assert [task.id for task in tasks] == ["vibequant", "csp"]
    assert all(task.prompt.startswith(DISPATCH_PROMPT_PREFIX) for task in tasks)
    assert len({task.prompt for task in tasks}) == 2
    assert all(PAYLOAD.decode() not in task.prompt for task in tasks)
    assert all(RUN_A not in task.prompt for task in tasks)

    assert await dispatch.worker_fn(tasks[0].prompt) == "vibe-result"
    assert await dispatch.worker_fn(tasks[1].prompt) == "csp-result"
    assert [entry["profile"].profile for entry in built] == [
        WorkerProfile.VIBEQUANT,
        WorkerProfile.CSP,
    ]
    assert built[0]["workspace"] == vibe_workspace.resolve()
    assert built[1]["workspace"] == csp_workspace.resolve()
    assert built[0]["session_output_root"] == (
        vibe_workspace / ".daxalgo-strategy-agent/sessions"
    )
    assert engines[WorkerProfile.VIBEQUANT].calls == [(PAYLOAD.decode(), 2)]
    assert engines[WorkerProfile.CSP].calls == [(PAYLOAD.decode(), 5)]
    assert [
        (run_hash, profile, event.type) for run_hash, profile, event in forwarded
    ] == [
        (RUN_A, WorkerProfile.VIBEQUANT, "text_delta"),
        (RUN_A, WorkerProfile.CSP, "request_start"),
        (RUN_A, WorkerProfile.CSP, "text_delta"),
    ]

    with pytest.raises(DispatchRejected) as replay:
        await dispatch.worker_fn(tasks[0].prompt)
    assert replay.value.code == "replayed_dispatch_token"
    with pytest.raises(DispatchRejected) as reused_plan:
        await dispatch.plan_fn("second plan")
    assert reused_plan.value.code == "dispatch_plan_reused"


def test_coordinator_dispatch_rejects_profile_or_workspace_widening(
    tmp_path: Path,
) -> None:
    vibe_workspace = _workspace(tmp_path, "vibe")
    csp_workspace = _workspace(tmp_path, "csp")
    profiles = {
        WorkerProfile.VIBEQUANT: _native_profile(WorkerProfile.VIBEQUANT, 2),
        WorkerProfile.CSP: _native_profile(WorkerProfile.CSP, 2),
    }
    workspaces = {
        WorkerProfile.VIBEQUANT: vibe_workspace,
        WorkerProfile.CSP: csp_workspace,
    }
    bindings = SimpleNamespace(WorkerTask=_FakeWorkerTask)

    with pytest.raises(ValueError, match="profiles must contain exactly"):
        CoordinatorDispatch(
            bindings,
            run_hash=RUN_A,
            payload=PAYLOAD,
            profiles={WorkerProfile.VIBEQUANT: profiles[WorkerProfile.VIBEQUANT]},
            workspaces=workspaces,
            event_sink=lambda *_args: None,
        )
    with pytest.raises(ValueError, match="distinct contained workspaces"):
        CoordinatorDispatch(
            bindings,
            run_hash=RUN_A,
            payload=PAYLOAD,
            profiles=profiles,
            workspaces={
                WorkerProfile.VIBEQUANT: vibe_workspace,
                WorkerProfile.CSP: vibe_workspace,
            },
            event_sink=lambda *_args: None,
        )


def test_real_financemanus_imports_match_pinned_revision_and_module_roots() -> None:
    source_root = _configured_financemanus_root()
    try:
        bindings = load_financemanus(source_root)
    except RuntimeGateError as exc:
        pytest.skip(str(exc))

    assert bindings.source.revision == EXPECTED_FINANCEMANUS_REVISION
    assert bindings.source.source_root == source_root.resolve()
    assert bindings.source.agent_package_root == (source_root / "agent").resolve()
    assert bindings.QueryEngine.__module__ == "agent.query_engine"
    assert bindings.ContextManager.__module__ == "agent.context"
    assert bindings.Session.__module__ == "agent.session"
    assert bindings.ToolRegistry.__module__ == "agent.tool_registry"
    assert bindings.Coordinator.__module__ == "agent.services.coordinator"
    for _name, module_file in bindings.source.module_files:
        assert source_root.resolve() in module_file.parents


@pytest.mark.asyncio
async def test_real_financemanus_coordinator_runs_one_dispatcher_and_isolates_worker_failure(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    source_root = _configured_financemanus_root()
    try:
        bindings = load_financemanus(source_root)
    except RuntimeGateError as exc:
        pytest.skip(str(exc))

    vibe_workspace = _workspace(tmp_path, "real-coordinator-vibe")
    csp_workspace = _workspace(tmp_path, "real-coordinator-csp")

    class _FailingEngine:
        async def stream_submit_message(
            self, _prompt: str, *, max_turns: int | None = None
        ):
            del max_turns
            yield _FakeEvent("request_start", {"worker": "vibequant"})
            raise RuntimeError("forced-vibequant-failure")

    engines = {
        WorkerProfile.VIBEQUANT: _FailingEngine(),
        WorkerProfile.CSP: _StreamingEngine(
            [_FakeEvent("text_delta", {"text": "csp-survived"})]
        ),
    }

    def engine_builder(_bindings: Any, **kwargs: Any) -> Any:
        return SimpleNamespace(engine=engines[kwargs["profile"].profile])

    forwarded: list[tuple[WorkerProfile, str]] = []

    def event_sink(_run_hash: str, profile: WorkerProfile, event: _FakeEvent) -> None:
        forwarded.append((profile, event.type))

    monkeypatch.setattr(runtime_module, "create_query_engine", engine_builder)
    dispatch = CoordinatorDispatch(
        bindings,
        run_hash=RUN_A,
        payload=PAYLOAD,
        profiles={
            WorkerProfile.VIBEQUANT: _native_profile(WorkerProfile.VIBEQUANT, 2),
            WorkerProfile.CSP: _native_profile(WorkerProfile.CSP, 2),
        },
        workspaces={
            WorkerProfile.VIBEQUANT: vibe_workspace,
            WorkerProfile.CSP: csp_workspace,
        },
        event_sink=event_sink,
    )

    result = await dispatch.run(timeout_seconds=5)

    assert type(dispatch.coordinator).__module__ == "agent.services.coordinator"
    assert dispatch.coordinator.get_summary() == {
        "total": 2,
        "active": 0,
        "completed": 1,
        "failed": 1,
    }
    assert dispatch.worker_outcomes[WorkerProfile.VIBEQUANT].status == "failed"
    assert (
        dispatch.worker_outcomes[WorkerProfile.VIBEQUANT].error
        == "forced-vibequant-failure"
    )
    assert dispatch.worker_outcomes[WorkerProfile.CSP].status == "completed"
    assert dispatch.worker_outcomes[WorkerProfile.CSP].error == ""
    assert "forced-vibequant-failure" in result
    assert "csp-survived" in result
    assert (WorkerProfile.VIBEQUANT, "request_start") in forwarded
    assert (WorkerProfile.CSP, "text_delta") in forwarded


def test_real_import_revision_mismatch_names_expected_and_observed_revisions() -> None:
    source_root = _configured_financemanus_root()
    observed_revision = subprocess.run(
        ["git", "-C", str(source_root), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    wrong_revision = secrets.token_hex(20)
    if wrong_revision == observed_revision:
        wrong_revision = "0" * 40 if observed_revision != "0" * 40 else "1" * 40
    with pytest.raises(RuntimeGateError) as mismatch:
        load_financemanus(source_root, expected_revision=wrong_revision)
    assert mismatch.value.code == "financemanus_revision_mismatch"
    assert f"expected={wrong_revision}" in str(mismatch.value)
    assert f"observed={observed_revision}" in str(mismatch.value)
