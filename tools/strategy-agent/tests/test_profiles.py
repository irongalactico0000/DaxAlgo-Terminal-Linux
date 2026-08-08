from __future__ import annotations

import asyncio
import hashlib
import json
import os
import threading
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from types import SimpleNamespace
from typing import Any, ClassVar

import pytest

import daxalgo_strategy_agent.profiles as profiles_module
from daxalgo_strategy_agent.contracts import (
    ComponentPin,
    FrozenDataFile,
    FrozenRunManifest,
    NativeLaneResult,
)
from daxalgo_strategy_agent.profiles import (
    ResearchQueryEngineCoordinator,
    make_csp_profile,
    make_csp_submission_tool_factory,
    make_research_profile,
    make_vibequant_profile,
    make_vibequant_submission_tool_factory,
)
from daxalgo_strategy_agent.queryengine_runtime import (
    EXPECTED_FINANCEMANUS_REVISION,
    WorkerProfile,
    load_financemanus,
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


@dataclass(frozen=True)
class _FakeEvent:
    type: str
    data: dict[str, Any]


class _FakeQueryEngine:
    instances: ClassVar[list[_FakeQueryEngine]] = []

    def __init__(self, **kwargs: Any) -> None:
        self.kwargs = kwargs
        self.calls: list[tuple[str, int | None]] = []
        self.__class__.instances.append(self)

    async def stream_submit_message(self, prompt: str, *, max_turns: int | None = None):
        self.calls.append((prompt, max_turns))
        yield _FakeEvent("request_start", {"call": len(self.calls)})
        yield _FakeEvent("text_delta", {"text": f"answer-{len(self.calls)}"})
        yield _FakeEvent("message_stop", {"stop_reason": "end_turn"})


class _FakeTool:
    pass


@dataclass
class _FakeToolResult:
    output: str = ""
    error: str | None = None
    artifacts: dict[str, Any] | None = None
    is_error: bool = False

    @classmethod
    def success(cls, output: str, **artifacts: Any) -> _FakeToolResult:
        return cls(output=output, artifacts=artifacts)

    @classmethod
    def failure(cls, error: str, **artifacts: Any) -> _FakeToolResult:
        return cls(error=error, artifacts=artifacts, is_error=True)


@pytest.fixture(autouse=True)
def _reset_fake_engines() -> None:
    _FakeQueryEngine.instances.clear()


@pytest.fixture
def fake_tool_contract(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(
        profiles_module,
        "_load_financemanus_tool_contract",
        lambda: (_FakeTool, _FakeToolResult),
    )


def _fake_bindings() -> Any:
    return SimpleNamespace(
        ToolRegistry=_FakeRegistry,
        ContextManager=_FakeContextManager,
        Session=_FakeSession,
        QueryEngine=_FakeQueryEngine,
    )


def _manifest(
    workspace: Path, *, run_id: str = "native-profile-run"
) -> FrozenRunManifest:
    primary = workspace / "primary.csv"
    primary.write_text(
        "date,timestamp,open,high,low,close,volume,symbol\n"
        "2026-08-08,2026-08-08T00:00:00Z,100,101,99,100,10,PRIMARY\n",
        encoding="utf-8",
    )
    return FrozenRunManifest(
        run_id=run_id,
        confirmed_intent_sha256="a" * 64,
        selected_start_utc=datetime(2026, 8, 8, 0, 0, tzinfo=UTC),
        selected_end_utc=datetime(2026, 8, 8, 0, 5, tzinfo=UTC),
        as_of_utc=datetime(2026, 8, 8, 0, 10, tzinfo=UTC),
        timezone_name="UTC",
        data_files=(
            FrozenDataFile(
                role="primary",
                instrument="PRIMARY",
                venue="fixture",
                source="test",
                timeframe="5m",
                relative_path=primary.name,
                sha256=hashlib.sha256(primary.read_bytes()).hexdigest(),
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


def _result(
    manifest: FrozenRunManifest,
    workspace: Path,
    *,
    lane: str,
    relative_path: str,
) -> NativeLaneResult:
    artifact = workspace / relative_path
    return NativeLaneResult(
        run_id=manifest.run_id,
        lane=lane,
        manifest_sha256=manifest.manifest_sha256,
        status="passed",
        native_stage="completed" if lane == "vibequant" else "csp.run",
        framework="transcend-0/VibeQuant" if lane == "vibequant" else "Point72 CSP",
        framework_version="0.1.0" if lane == "vibequant" else "0.18.0",
        source_relative_path=relative_path,
        artifact_relative_paths=(relative_path,),
        artifact_sha256={
            relative_path: hashlib.sha256(artifact.read_bytes()).hexdigest()
        },
        observations={"genuine_native_callback": True},
    )


@pytest.mark.asyncio
async def test_research_coordinator_reuses_one_real_handle_and_sends_context_once(
    tmp_path: Path,
) -> None:
    root = tmp_path / "research"
    root.mkdir()
    profile = make_research_profile(
        config_factory=lambda: SimpleNamespace(model="host-fixed-model"),
        max_turns=3,
    )
    coordinator = ResearchQueryEngineCoordinator(
        bindings=_fake_bindings(),
        profile=profile,
        workspace_root=root,
    )
    context = {
        "primary": "AAPL",
        "comparisons": ["SPY", "QQQ", "VIX"],
        "selected_range": {"start": "10:00", "end": "10:30"},
    }

    first = [
        event
        async for event in coordinator("research-1", "Is the jump confirmed?", context)
    ]
    second = [
        event
        async for event in coordinator(
            "research-1", "Use a limit or market order?", dict(context)
        )
    ]

    assert coordinator.session_count == 1
    assert len(_FakeQueryEngine.instances) == 1
    engine = _FakeQueryEngine.instances[0]
    assert coordinator.handle_for("research-1").engine is engine
    assert engine.kwargs["tool_registry"].tools == []
    assert len(engine.calls) == 2
    first_prompt, first_turns = engine.calls[0]
    assert first_turns == 3
    assert "<frozen_chart_context_json>" in first_prompt
    assert '"primary":"AAPL"' in first_prompt
    assert "Is the jump confirmed?" in first_prompt
    assert engine.calls[1] == ("Use a limit or market order?", 3)
    assert first[0] is not second[0]
    assert [event.type for event in first] == [
        "request_start",
        "text_delta",
        "message_stop",
    ]


@pytest.mark.asyncio
async def test_research_coordinator_rejects_changed_frozen_context(
    tmp_path: Path,
) -> None:
    root = tmp_path / "research"
    root.mkdir()
    coordinator = ResearchQueryEngineCoordinator(
        bindings=_fake_bindings(),
        profile=make_research_profile(
            config_factory=lambda: SimpleNamespace(model="fixed")
        ),
        workspace_root=root,
    )
    _ = [event async for event in coordinator("s1", "first", {"symbol": "AAPL"})]

    with pytest.raises(ValueError, match="context changed"):
        _ = [event async for event in coordinator("s1", "second", {"symbol": "MSFT"})]
    assert len(_FakeQueryEngine.instances) == 1


@pytest.mark.asyncio
async def test_vibequant_tool_writes_task_spec_once_and_returns_exact_native_json(
    tmp_path: Path,
    fake_tool_contract: None,
) -> None:
    workspace = tmp_path / "vibequant"
    workspace.mkdir()
    manifest = _manifest(workspace)
    calls: list[tuple[dict[str, Any], str]] = []
    captured: list[NativeLaneResult] = []

    def native_runner(
        observed_manifest: FrozenRunManifest,
        observed_workspace: Path,
        *,
        task_spec_relative_path: str,
    ) -> NativeLaneResult:
        calls.append(
            (
                json.loads((observed_workspace / task_spec_relative_path).read_text()),
                task_spec_relative_path,
            )
        )
        return _result(
            observed_manifest,
            observed_workspace,
            lane="vibequant",
            relative_path=task_spec_relative_path,
        )

    tool = make_vibequant_submission_tool_factory(
        manifest=manifest,
        workspace=workspace,
        native_runner=native_runner,
        result_sink=captured.append,
    )()
    api_schema = tool.to_api_schema()
    assert set(api_schema) == {"name", "description", "parameters"}
    assert api_schema["parameters"] is tool.input_schema
    assert api_schema["parameters"]["required"] == ["task_spec"]
    task_spec = {
        "name": "native-long",
        "kind": "strategy",
        "strategy": {
            "name": "custom",
            "params": {
                "source": (
                    "class Strategy(BaseStrategy):\n"
                    "    def on_bar(self, bar):\n"
                    "        pass\n"
                ),
            },
        },
    }

    first = await tool.call(
        {"task_spec": task_spec}, SimpleNamespace(working_dir=workspace)
    )
    second = await tool.call(
        {"task_spec": {"name": "replacement"}},
        SimpleNamespace(working_dir=workspace),
    )

    assert first.is_error is False
    assert json.loads(first.output) == captured[0].model_dump(mode="json")
    assert first.artifacts == {
        "native_lane_result": captured[0].model_dump(mode="json")
    }
    assert calls == [(task_spec, "agent-input/vibequant-task-spec.json")]
    assert second.is_error is True
    assert "single-use" in second.error
    assert (
        json.loads((workspace / "agent-input/vibequant-task-spec.json").read_text())
        == task_spec
    )


@pytest.mark.asyncio
async def test_csp_tool_passes_source_unchanged_without_an_invented_validator(
    tmp_path: Path,
    fake_tool_contract: None,
) -> None:
    workspace = tmp_path / "csp"
    workspace.mkdir()
    manifest = _manifest(workspace, run_id="csp-profile-run")
    observed_source: list[str] = []

    async def native_runner(
        observed_manifest: FrozenRunManifest,
        observed_workspace: Path,
        *,
        source_relative_path: str,
    ) -> NativeLaneResult:
        observed_source.append(
            (observed_workspace / source_relative_path).read_text(encoding="utf-8")
        )
        return _result(
            observed_manifest,
            observed_workspace,
            lane="csp",
            relative_path=source_relative_path,
        )

    tool = make_csp_submission_tool_factory(
        manifest=manifest,
        workspace=workspace,
        native_runner=native_runner,
    )()
    api_schema = tool.to_api_schema()
    assert set(api_schema) == {"name", "description", "parameters"}
    assert api_schema["parameters"] is tool.input_schema
    assert api_schema["parameters"]["required"] == ["source"]
    # Deliberately not valid Python. The profile is transport only; the genuine CSP child owns
    # syntax/import/graph/run failures rather than a new host regex or pseudo-validator.
    source = "this is deliberately passed to the native CSP runner unchanged\n"
    result = await tool.call({"source": source}, SimpleNamespace(working_dir=workspace))

    assert result.is_error is False
    assert observed_source == [source]
    assert json.loads(result.output)["native_stage"] == "csp.run"


@pytest.mark.asyncio
async def test_submission_tool_rejects_wrong_workspace_before_writing(
    tmp_path: Path,
    fake_tool_contract: None,
) -> None:
    workspace = tmp_path / "expected"
    workspace.mkdir()
    wrong = tmp_path / "wrong"
    wrong.mkdir()
    manifest = _manifest(workspace)

    def must_not_run(*args: Any, **kwargs: Any) -> NativeLaneResult:
        raise AssertionError("native runner must not be called")

    tool = make_csp_submission_tool_factory(
        manifest=manifest,
        workspace=workspace,
        native_runner=must_not_run,
    )()
    result = await tool.call(
        {"source": "def build_graph(request): ..."},
        SimpleNamespace(working_dir=wrong),
    )

    assert result.is_error is True
    assert "workspace mismatch" in result.error
    assert not (workspace / "agent-input/csp-strategy.py").exists()


def test_profiles_have_only_their_fixed_tool_and_disclose_native_limits() -> None:
    def factory() -> object:
        return object()

    def config_factory() -> SimpleNamespace:
        return SimpleNamespace(model="fixed")

    research = make_research_profile(config_factory=config_factory)
    vibequant = make_vibequant_profile(
        config_factory=config_factory,
        submission_tool_factory=factory,
    )
    csp = make_csp_profile(
        config_factory=config_factory,
        submission_tool_factory=factory,
    )

    assert research.profile is WorkerProfile.RESEARCH
    assert research.tool_factories == ()
    assert vibequant.profile is WorkerProfile.VIBEQUANT
    assert vibequant.tool_factories == (factory,)
    vibequant_prompt = " ".join(vibequant.system_prompt.split())
    assert "has not demonstrated working short execution" in vibequant_prompt
    assert "does not automatically flatten" in vibequant_prompt
    assert 'get_history(count, symbol, field="close")' in vibequant_prompt
    assert "never pass periods=" in vibequant_prompt
    assert "integer of nanoseconds since epoch" in vibequant_prompt
    assert "separate on_bar calls" in vibequant_prompt
    assert "300_000_000_000" in vibequant_prompt
    assert "after every instrument callback" in vibequant_prompt
    assert "not only inside an ``if bar.symbol == primary`` branch" in vibequant_prompt
    assert "self.entry_order_submitted = False" in vibequant_prompt
    assert "self.close_order_submitted" in vibequant_prompt
    assert "cannot by itself prevent duplicate submissions" in vibequant_prompt
    assert "include_performance or include_positions" in vibequant_prompt
    assert "preserve upstream defaults" in vibequant_prompt
    assert csp.profile is WorkerProfile.CSP
    assert csp.tool_factories == (factory,)
    csp_prompt = " ".join(csp.system_prompt.split())
    assert "not a trading backtester" in csp_prompt
    assert "does not natively produce" in csp_prompt
    assert "csp.now() may be called only inside a node" in csp_prompt
    assert "csp.PushMode.NON_COLLAPSING" in csp_prompt
    assert "sort comparison events before the primary event" in csp_prompt
    assert "Do not use csp.apply" in csp_prompt
    assert "never call events()" in csp_prompt
    assert "Do not sort on timestamp alone" in csp_prompt
    assert "guessed absolute prices" in csp_prompt
    assert "never ``return None``" in csp_prompt
    assert "one finished decision node" in csp_prompt
    assert "s_last_return[symbol] = value / previous - 1.0" in csp_prompt
    assert "never subtract a just-updated value" in csp_prompt
    assert "exhaustive expected ``intent`` output stream" in csp_prompt
    assert "explicit ``no_trade``" in csp_prompt
    assert "never hard-code scenario timestamps" in csp_prompt
    assert "s_entry_emitted" in csp_prompt
    assert "s_close_emitted" in csp_prompt
    assert "only on a primary-instrument event" in csp_prompt
    assert "without a latched ``s_close_emitted`` guard is invalid" in csp_prompt


@pytest.mark.asyncio
async def test_cancelling_native_thread_waits_until_runner_releases_process_custody(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "native-custody"
    workspace.mkdir()
    manifest = _manifest(workspace, run_id="native-custody")
    started = threading.Event()
    release = threading.Event()
    finished = threading.Event()

    def bounded_runner(
        _manifest: FrozenRunManifest,
        _workspace: Path,
        *,
        source_relative_path: str,
    ) -> None:
        assert source_relative_path == "agent-input/source.py"
        started.set()
        assert release.wait(timeout=5)
        finished.set()
        raise RuntimeError("late native cleanup failure")

    invocation = asyncio.create_task(
        profiles_module._invoke_native_runner(
            bounded_runner,
            manifest,
            workspace,
            source_relative_path="agent-input/source.py",
        )
    )
    assert await asyncio.to_thread(started.wait, 1)

    invocation.cancel()
    await asyncio.sleep(0)
    assert not invocation.done()

    release.set()
    with pytest.raises(asyncio.CancelledError):
        await invocation
    assert finished.is_set()


def test_real_financemanus_tool_registry_interface_when_configured(
    tmp_path: Path,
) -> None:
    source_value = os.environ.get("DAXALGO_QUERY_ENGINE_ROOT", "").strip()
    if not source_value:
        pytest.skip(
            "set DAXALGO_QUERY_ENGINE_ROOT for the pinned FinanceManus interface smoke"
        )
    bindings = load_financemanus(
        Path(source_value),
        expected_revision=EXPECTED_FINANCEMANUS_REVISION,
    )
    workspace = tmp_path / "native"
    workspace.mkdir()
    manifest = _manifest(workspace, run_id="real-interface-smoke")

    def unused_runner(*args: Any, **kwargs: Any) -> NativeLaneResult:
        raise AssertionError("registry smoke must not invoke the native worker")

    tool = make_csp_submission_tool_factory(
        manifest=manifest,
        workspace=workspace,
        native_runner=unused_runner,
    )()
    agent_tool = __import__("agent.tool", fromlist=["Tool"])
    assert isinstance(tool, agent_tool.Tool)
    registry = bindings.ToolRegistry()
    registry.register(tool)
    assert registry.count == 1
    assert registry.get("submit_csp_source") is tool
    assert registry.get_for_api() == [tool.to_api_schema()]
    assert registry.get_for_api()[0]["parameters"] == tool.input_schema
    assert "input_schema" not in registry.get_for_api()[0]
