"""Thin, host-owned adapter for the pinned FinanceManus QueryEngine runtime.

This module intentionally owns no reasoning loop, coordinator, or tool-selection policy.  It
verifies and imports those implementations from a pinned FinanceManus source checkout, constructs
fresh fixed registries and workspace-rooted sessions, and adapts the existing ``Coordinator.run``
prompt-only worker interface with opaque, single-use dispatch tokens.
"""

from __future__ import annotations

import hashlib
import importlib
import inspect
import os
import re
import secrets
import subprocess
import sys
import threading
from dataclasses import dataclass
from enum import Enum
from pathlib import Path
from typing import Any, AsyncIterator, Awaitable, Callable, Mapping, Optional


EXPECTED_FINANCEMANUS_REVISION = "f25fab79e611fd904280cabc97d9d2393a0922dc"
DISPATCH_PROMPT_PREFIX = "daxalgo-dispatch-v1:"
DEFAULT_COORDINATOR_TIMEOUT_SECONDS = 720

_FULL_GIT_SHA_RE = re.compile(r"^[0-9a-f]{40}$")
_FULL_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
_TOKEN_RE = re.compile(r"^[A-Za-z0-9_-]{43}$")
_TASK_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")

_MODULE_PATHS: tuple[tuple[str, str], ...] = (
    ("agent.config", "agent/config.py"),
    ("agent.context", "agent/context.py"),
    ("agent.session", "agent/session.py"),
    ("agent.tool_registry", "agent/tool_registry.py"),
    ("agent.services.coordinator", "agent/services/coordinator.py"),
    ("agent.query_engine", "agent/query_engine.py"),
)


class RuntimeGateError(RuntimeError):
    """A named source, revision, module-origin, or dependency gate failure."""

    def __init__(self, code: str, detail: str) -> None:
        self.code = code
        self.detail = detail
        super().__init__(f"{code}: {detail}")


class DispatchRejected(RuntimeError):
    """A named rejection from the host-owned dispatch boundary."""

    def __init__(self, code: str, detail: str) -> None:
        self.code = code
        self.detail = detail
        super().__init__(f"{code}: {detail}")


class WorkerProfile(str, Enum):
    """Profiles selected by host code, never by a model-supplied string."""

    RESEARCH = "research"
    VIBEQUANT = "vibequant"
    CSP = "csp"


@dataclass(frozen=True)
class CoordinatorWorkerOutcome:
    """Exact terminal state reported by one pinned FinanceManus worker task."""

    status: str
    error: str


@dataclass(frozen=True)
class FinanceManusSourceInfo:
    """Verified source identity and import origins for an upstream checkout."""

    source_root: Path
    revision: str
    agent_package_root: Path
    module_files: tuple[tuple[str, Path], ...]
    class_origins: tuple[tuple[str, Path], ...]
    python_version: str

    def as_dict(self) -> dict[str, Any]:
        return {
            "source_root": str(self.source_root),
            "revision": self.revision,
            "agent_package_root": str(self.agent_package_root),
            "module_files": {name: str(path) for name, path in self.module_files},
            "class_origins": {name: str(path) for name, path in self.class_origins},
            "python_version": self.python_version,
        }


@dataclass(frozen=True)
class FinanceManusBindings:
    """The real imported FinanceManus classes used by the adapter."""

    source: FinanceManusSourceInfo
    Config: type[Any]
    QueryEngine: type[Any]
    ContextManager: type[Any]
    Session: type[Any]
    ToolRegistry: type[Any]
    Coordinator: type[Any]
    CoordinatorConfig: type[Any]
    WorkerTask: type[Any]


def _run_git(source_root: Path, *args: str) -> str:
    env = os.environ.copy()
    env["GIT_OPTIONAL_LOCKS"] = "0"
    try:
        completed = subprocess.run(
            ["git", "-C", str(source_root), *args],
            check=False,
            capture_output=True,
            text=True,
            timeout=10,
            env=env,
        )
    except FileNotFoundError as exc:
        raise RuntimeGateError(
            "financemanus_git_unavailable", "git executable was not found"
        ) from exc
    except subprocess.TimeoutExpired as exc:
        raise RuntimeGateError(
            "financemanus_git_unavailable",
            f"git timed out while inspecting {source_root}",
        ) from exc
    if completed.returncode != 0:
        error_text = (
            completed.stderr or completed.stdout or "git command failed"
        ).strip()
        detail = error_text.splitlines()[0] if error_text else "git command failed"
        raise RuntimeGateError(
            "financemanus_source_unavailable",
            f"source_root={source_root} git_error={detail}",
        )
    return completed.stdout.strip()


def _existing_agent_root() -> Path | None:
    package = sys.modules.get("agent")
    if package is None:
        return None
    package_file = getattr(package, "__file__", None)
    if package_file:
        return Path(package_file).resolve().parent
    package_path = tuple(getattr(package, "__path__", ()))
    if len(package_path) == 1:
        return Path(package_path[0]).resolve()
    return None


def load_financemanus(
    source_root: Path | str,
    *,
    expected_revision: str = EXPECTED_FINANCEMANUS_REVISION,
) -> FinanceManusBindings:
    """Verify a checkout and import the real FinanceManus runtime classes.

    The source checkout must be at the exact revision, have no tracked or untracked changes under
    ``agent/``, and supply every imported module from its expected source path.  The checkout root
    remains on ``sys.path`` because QueryEngine performs lazy absolute imports during construction
    and streaming.
    """

    if not _FULL_GIT_SHA_RE.fullmatch(expected_revision):
        raise ValueError(
            "expected_revision must be a full lowercase 40-character git revision"
        )
    try:
        root = Path(source_root).expanduser().resolve(strict=True)
    except (FileNotFoundError, OSError) as exc:
        raise RuntimeGateError(
            "financemanus_source_unavailable",
            f"source_root={Path(source_root).expanduser()}",
        ) from exc
    if not root.is_dir():
        raise RuntimeGateError(
            "financemanus_source_unavailable", f"source_root={root} is not a directory"
        )

    observed_root = Path(_run_git(root, "rev-parse", "--show-toplevel")).resolve()
    if observed_root != root:
        raise RuntimeGateError(
            "financemanus_source_root_mismatch",
            f"expected={root} observed={observed_root}",
        )
    observed_revision = _run_git(root, "rev-parse", "HEAD")
    if observed_revision != expected_revision:
        raise RuntimeGateError(
            "financemanus_revision_mismatch",
            f"source_root={root} expected={expected_revision} observed={observed_revision}",
        )

    dirty_agent_paths = _run_git(
        root,
        "status",
        "--porcelain=v1",
        "--untracked-files=all",
        "--",
        "agent",
    )
    if dirty_agent_paths:
        first_change = dirty_agent_paths.splitlines()[0]
        raise RuntimeGateError(
            "financemanus_source_dirty",
            f"source_root={root} revision={observed_revision} first_change={first_change}",
        )

    expected_agent_root = (root / "agent").resolve()
    loaded_agent_root = _existing_agent_root()
    if loaded_agent_root is not None and loaded_agent_root != expected_agent_root:
        raise RuntimeGateError(
            "financemanus_module_conflict",
            f"expected_agent_root={expected_agent_root} loaded_agent_root={loaded_agent_root}",
        )

    root_text = str(root)
    if not any(Path(entry or os.curdir).resolve() == root for entry in sys.path):
        sys.path.insert(0, root_text)
    importlib.invalidate_caches()

    modules: dict[str, Any] = {}
    for module_name, relative_path in _MODULE_PATHS:
        expected_file = (root / relative_path).resolve()
        try:
            module = importlib.import_module(module_name)
        except ModuleNotFoundError as exc:
            missing = exc.name or "unknown"
            raise RuntimeGateError(
                "financemanus_dependency_unavailable",
                (
                    f"source_root={root} revision={observed_revision} "
                    f"missing_module={missing} while_importing={module_name}"
                ),
            ) from exc
        except Exception as exc:
            raise RuntimeGateError(
                "financemanus_import_failed",
                (
                    f"source_root={root} revision={observed_revision} module={module_name} "
                    f"error={type(exc).__name__}: {exc}"
                ),
            ) from exc
        module_file_raw = getattr(module, "__file__", None)
        if not module_file_raw:
            raise RuntimeGateError(
                "financemanus_module_root_mismatch",
                f"module={module_name} expected={expected_file} observed=<no __file__>",
            )
        module_file = Path(module_file_raw).resolve()
        if module_file != expected_file:
            raise RuntimeGateError(
                "financemanus_module_root_mismatch",
                f"module={module_name} expected={expected_file} observed={module_file}",
            )
        modules[module_name] = module

    class_modules: dict[str, str] = {
        "Config": "agent.config",
        "QueryEngine": "agent.query_engine",
        "ContextManager": "agent.context",
        "Session": "agent.session",
        "ToolRegistry": "agent.tool_registry",
        "Coordinator": "agent.services.coordinator",
        "CoordinatorConfig": "agent.services.coordinator",
        "WorkerTask": "agent.services.coordinator",
    }
    classes = {
        class_name: getattr(modules[module_name], class_name)
        for class_name, module_name in class_modules.items()
    }
    class_origins: list[tuple[str, Path]] = []
    for class_name, imported_class in classes.items():
        origin = Path(inspect.getfile(imported_class)).resolve()
        expected_origin = Path(modules[class_modules[class_name]].__file__).resolve()
        if origin != expected_origin:
            raise RuntimeGateError(
                "financemanus_module_root_mismatch",
                f"class={class_name} expected={expected_origin} observed={origin}",
            )
        class_origins.append((class_name, origin))

    source_info = FinanceManusSourceInfo(
        source_root=root,
        revision=observed_revision,
        agent_package_root=expected_agent_root,
        module_files=tuple(
            (module_name, Path(modules[module_name].__file__).resolve())
            for module_name, _ in _MODULE_PATHS
        ),
        class_origins=tuple(class_origins),
        python_version=".".join(str(part) for part in sys.version_info[:3]),
    )
    return FinanceManusBindings(source=source_info, **classes)


ToolFactory = Callable[[], Any]
ConfigFactory = Callable[[], Any]


@dataclass(frozen=True)
class FixedQueryEngineProfile:
    """A host-sealed QueryEngine profile with a fresh, exact registry per engine."""

    profile: WorkerProfile
    system_prompt: str
    config_factory: ConfigFactory
    tool_factories: tuple[ToolFactory, ...] = ()
    max_turns: int | None = None

    def __post_init__(self) -> None:
        if type(self.profile) is not WorkerProfile:
            raise TypeError("profile must be a WorkerProfile chosen by host code")
        if not self.system_prompt.strip():
            raise ValueError("system_prompt must not be empty")
        if not callable(self.config_factory):
            raise TypeError("config_factory must be callable")
        if not isinstance(self.tool_factories, tuple) or not all(
            callable(factory) for factory in self.tool_factories
        ):
            raise TypeError("tool_factories must be an immutable tuple of callables")
        if self.max_turns is not None and (
            type(self.max_turns) is not int or self.max_turns < 1
        ):
            raise ValueError("max_turns must be a positive integer or None")


@dataclass(frozen=True)
class QueryEngineHandle:
    """The upstream objects composing one explicit workspace session."""

    profile: WorkerProfile
    workspace: Path
    session_output_root: Path
    registry: Any
    context_manager: Any
    session: Any
    engine: Any


def build_fixed_registry(
    bindings: FinanceManusBindings, profile: FixedQueryEngineProfile
) -> Any:
    """Create an actual FinanceManus ToolRegistry from only the profile's host factories."""

    registry = bindings.ToolRegistry()
    for tool_factory in profile.tool_factories:
        registry.register(tool_factory())
    return registry


def _safe_task_id(task_id: str) -> str:
    if not _TASK_ID_RE.fullmatch(task_id):
        raise ValueError(
            "task_id must be one safe path component of at most 100 characters"
        )
    return task_id


def _contained_output_root(workspace: Path, session_output_root: Path | str) -> Path:
    candidate = Path(session_output_root)
    if not candidate.is_absolute():
        candidate = workspace / candidate
    candidate = candidate.resolve(strict=False)
    if candidate != workspace and workspace not in candidate.parents:
        raise ValueError(
            f"session_output_root must remain inside workspace: workspace={workspace} output={candidate}"
        )
    candidate.mkdir(parents=True, exist_ok=True)
    return candidate.resolve(strict=True)


def create_query_engine(
    bindings: FinanceManusBindings,
    *,
    profile: FixedQueryEngineProfile,
    workspace: Path | str,
    session_output_root: Path | str,
    task_id: str,
    session_id: str | None = None,
) -> QueryEngineHandle:
    """Construct the real QueryEngine with a fixed registry and contained Session output."""

    workspace_root = Path(workspace).resolve(strict=True)
    if not workspace_root.is_dir():
        raise ValueError(f"workspace must be a directory: {workspace_root}")
    output_root = _contained_output_root(workspace_root, session_output_root)
    config = profile.config_factory()
    model = getattr(config, "model", None)
    if not isinstance(model, str) or not model:
        raise TypeError(
            "profile config_factory must return a Config-like object with a model"
        )
    registry = build_fixed_registry(bindings, profile)
    context_manager = bindings.ContextManager(
        working_dir=workspace_root,
        custom_system_prompt=profile.system_prompt,
    )
    session = bindings.Session(
        session_id=session_id,
        task_id=_safe_task_id(task_id),
        model=model,
        output_dir=output_root,
    )
    engine = bindings.QueryEngine(
        config=config,
        tool_registry=registry,
        context_manager=context_manager,
        session=session,
    )
    return QueryEngineHandle(
        profile=profile.profile,
        workspace=workspace_root,
        session_output_root=output_root,
        registry=registry,
        context_manager=context_manager,
        session=session,
        engine=engine,
    )


async def stream_queryengine_events(
    engine: Any,
    prompt: str,
    *,
    max_turns: int | None = None,
) -> AsyncIterator[Any]:
    """Yield every original event from the real ``QueryEngine.stream_submit_message`` call."""

    async for event in engine.stream_submit_message(prompt, max_turns=max_turns):
        yield event


def _validate_run_hash(run_hash: str) -> str:
    if not _FULL_SHA256_RE.fullmatch(run_hash):
        raise ValueError("run_hash must be a lowercase hexadecimal SHA-256 digest")
    return run_hash


@dataclass(frozen=True)
class SealedDispatch:
    """Immutable server-side state addressed by an opaque one-use token."""

    run_hash: str
    profile: WorkerProfile
    payload: bytes
    workspace: Path


class SingleUseDispatchTokens:
    """Process-private issue/consume table that retains only token digests."""

    def __init__(self) -> None:
        self._pending: dict[str, SealedDispatch] = {}
        self._consumed: set[str] = set()
        self._lock = threading.Lock()

    @staticmethod
    def _digest(token: str) -> str:
        return hashlib.sha256(token.encode("ascii")).hexdigest()

    def issue(
        self,
        *,
        run_hash: str,
        profile: WorkerProfile,
        payload: bytes,
        workspace: Path,
    ) -> str:
        _validate_run_hash(run_hash)
        if type(profile) is not WorkerProfile or profile is WorkerProfile.RESEARCH:
            raise TypeError(
                "native dispatch profile must be WorkerProfile.VIBEQUANT or WorkerProfile.CSP"
            )
        if type(payload) is not bytes or not payload:
            raise TypeError("sealed dispatch payload must be non-empty immutable bytes")
        try:
            payload.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise ValueError("sealed dispatch payload must be valid UTF-8") from exc
        observed_run_hash = hashlib.sha256(payload).hexdigest()
        if observed_run_hash != run_hash:
            raise ValueError(
                f"run_hash must equal the sealed payload SHA-256: observed {observed_run_hash}"
            )
        workspace_root = Path(workspace).resolve(strict=True)
        if not workspace_root.is_dir():
            raise ValueError(
                f"dispatch workspace must be a directory: {workspace_root}"
            )
        record = SealedDispatch(run_hash, profile, payload, workspace_root)
        with self._lock:
            while True:
                token = secrets.token_urlsafe(32)
                digest = self._digest(token)
                if digest not in self._pending and digest not in self._consumed:
                    self._pending[digest] = record
                    return f"{DISPATCH_PROMPT_PREFIX}{token}"

    def consume(self, prompt: str, *, expected_run_hash: str) -> SealedDispatch:
        _validate_run_hash(expected_run_hash)
        if not isinstance(prompt, str) or not prompt.startswith(DISPATCH_PROMPT_PREFIX):
            raise DispatchRejected(
                "malformed_dispatch_prompt",
                f"expected {DISPATCH_PROMPT_PREFIX}<opaque-token>",
            )
        token = prompt[len(DISPATCH_PROMPT_PREFIX) :]
        if not _TOKEN_RE.fullmatch(token):
            raise DispatchRejected(
                "malformed_dispatch_prompt",
                f"expected {DISPATCH_PROMPT_PREFIX}<opaque-token>",
            )
        digest = self._digest(token)
        with self._lock:
            if digest in self._consumed:
                raise DispatchRejected(
                    "replayed_dispatch_token", "dispatch token was already consumed"
                )
            record = self._pending.get(digest)
            if record is None:
                raise DispatchRejected(
                    "unknown_dispatch_token", "dispatch token is not host-issued"
                )
            if record.run_hash != expected_run_hash:
                raise DispatchRejected(
                    "cross_run_dispatch_token",
                    "dispatch token is bound to a different run hash",
                )
            del self._pending[digest]
            self._consumed.add(digest)
            return record

    @property
    def pending_count(self) -> int:
        with self._lock:
            return len(self._pending)

    @property
    def consumed_count(self) -> int:
        with self._lock:
            return len(self._consumed)


EventSink = Callable[[str, WorkerProfile, Any], Optional[Awaitable[None]]]


class CoordinatorDispatch:
    """Actual Coordinator callbacks for exactly one VibeQuant worker and one CSP worker."""

    _NATIVE_ORDER = (WorkerProfile.VIBEQUANT, WorkerProfile.CSP)

    def __init__(
        self,
        bindings: FinanceManusBindings,
        *,
        run_hash: str,
        payload: bytes,
        profiles: Mapping[WorkerProfile, FixedQueryEngineProfile],
        workspaces: Mapping[WorkerProfile, Path | str],
        event_sink: EventSink,
        token_store: SingleUseDispatchTokens | None = None,
    ) -> None:
        self.bindings = bindings
        self.run_hash = _validate_run_hash(run_hash)
        if type(payload) is not bytes or not payload:
            raise TypeError("payload must be non-empty immutable bytes")
        try:
            payload.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise ValueError("payload must be valid UTF-8") from exc
        observed_run_hash = hashlib.sha256(payload).hexdigest()
        if observed_run_hash != self.run_hash:
            raise ValueError(
                f"run_hash must equal the sealed payload SHA-256: observed {observed_run_hash}"
            )
        expected_profiles = set(self._NATIVE_ORDER)
        if set(profiles) != expected_profiles:
            raise ValueError(
                "profiles must contain exactly WorkerProfile.VIBEQUANT and WorkerProfile.CSP"
            )
        if set(workspaces) != expected_profiles:
            raise ValueError(
                "workspaces must contain exactly WorkerProfile.VIBEQUANT and WorkerProfile.CSP"
            )
        copied_profiles: dict[WorkerProfile, FixedQueryEngineProfile] = {}
        copied_workspaces: dict[WorkerProfile, Path] = {}
        for profile_name in self._NATIVE_ORDER:
            spec = profiles[profile_name]
            if spec.profile is not profile_name:
                raise ValueError(f"profile mapping mismatch for {profile_name.value}")
            workspace = Path(workspaces[profile_name]).resolve(strict=True)
            if not workspace.is_dir():
                raise ValueError(f"workspace must be a directory: {workspace}")
            copied_profiles[profile_name] = spec
            copied_workspaces[profile_name] = workspace
        if len(set(copied_workspaces.values())) != len(self._NATIVE_ORDER):
            raise ValueError("VibeQuant and CSP must use distinct contained workspaces")
        if not callable(event_sink):
            raise TypeError(
                "event_sink must be callable so every child event can be forwarded"
            )
        self._payload = payload
        self._profiles = copied_profiles
        self._workspaces = copied_workspaces
        self._event_sink = event_sink
        self._token_store = token_store or SingleUseDispatchTokens()
        self._planned = False
        self._run_started = False
        self._coordinator: Any | None = None
        self._worker_outcomes: dict[WorkerProfile, CoordinatorWorkerOutcome] = {}
        self._dispatch_prompts = {
            profile: self._token_store.issue(
                run_hash=self.run_hash,
                profile=profile,
                payload=self._payload,
                workspace=self._workspaces[profile],
            )
            for profile in self._NATIVE_ORDER
        }

    async def plan_fn(self, _task: str) -> list[Any]:
        """Return exactly two real WorkerTasks whose prompts contain only opaque tokens."""

        if self._planned:
            raise DispatchRejected(
                "dispatch_plan_reused", "coordinator plan_fn is single-use"
            )
        self._planned = True
        return [
            self.bindings.WorkerTask(
                id=profile.value,
                prompt=self._dispatch_prompts[profile],
                description=profile.value,
                agent_name=profile.value,
            )
            for profile in self._NATIVE_ORDER
        ]

    async def worker_fn(self, prompt: str) -> str:
        """Consume one sealed token, build its fixed worker, and forward every child event."""

        record = self._token_store.consume(prompt, expected_run_hash=self.run_hash)
        profile_spec = self._profiles[record.profile]
        session_output_root = record.workspace / ".daxalgo-strategy-agent" / "sessions"
        handle = create_query_engine(
            self.bindings,
            profile=profile_spec,
            workspace=record.workspace,
            session_output_root=session_output_root,
            task_id=f"{record.profile.value}-{self.run_hash[:16]}",
        )
        worker_prompt = record.payload.decode("utf-8", errors="strict")
        text_parts: list[str] = []
        async for event in stream_queryengine_events(
            handle.engine,
            worker_prompt,
            max_turns=profile_spec.max_turns,
        ):
            sink_result = self._event_sink(self.run_hash, record.profile, event)
            if inspect.isawaitable(sink_result):
                await sink_result
            if getattr(event, "type", None) == "text_delta":
                data = getattr(event, "data", {})
                if isinstance(data, dict):
                    text = data.get("text", "")
                    if isinstance(text, str):
                        text_parts.append(text)
        return "".join(text_parts)

    def create_coordinator(
        self, *, timeout_seconds: int = DEFAULT_COORDINATOR_TIMEOUT_SECONDS
    ) -> Any:
        """Construct a fresh real FinanceManus Coordinator limited to the two fixed workers."""

        if type(timeout_seconds) is not int or timeout_seconds < 1:
            raise ValueError("timeout_seconds must be a positive integer")
        config = self.bindings.CoordinatorConfig(
            max_workers=2,
            timeout=timeout_seconds,
            team_name=f"daxalgo-{self.run_hash[:12]}",
        )
        return self.bindings.Coordinator(config)

    async def run(
        self, *, timeout_seconds: int = DEFAULT_COORDINATOR_TIMEOUT_SECONDS
    ) -> str:
        """Run the two callbacks through the upstream ``Coordinator.run`` implementation."""

        if self._run_started:
            raise DispatchRejected(
                "coordinator_run_reused", "CoordinatorDispatch.run is single-use"
        )
        self._run_started = True
        self._coordinator = self.create_coordinator(timeout_seconds=timeout_seconds)

        async def retain_worker_outcomes(_task: str, workers: list[Any]) -> str:
            outcomes: dict[WorkerProfile, CoordinatorWorkerOutcome] = {}
            sections: list[str] = []
            for worker in workers:
                worker_id = getattr(worker, "id", "")
                try:
                    profile = WorkerProfile(worker_id)
                except ValueError:
                    continue
                raw_status = getattr(worker, "status", "")
                status = getattr(raw_status, "value", raw_status)
                error = getattr(worker, "error", "")
                result = getattr(worker, "result", "")
                description = getattr(worker, "description", "")
                outcomes[profile] = CoordinatorWorkerOutcome(
                    status=status if isinstance(status, str) else str(status),
                    error=error if isinstance(error, str) else str(error),
                )
                sections.append(
                    f"## {description or worker_id}\n{result or error}"
                )
            self._worker_outcomes = outcomes
            return "\n".join(sections)

        return await self._coordinator.run(
            self.run_hash,
            worker_fn=self.worker_fn,
            plan_fn=self.plan_fn,
            synthesize_fn=retain_worker_outcomes,
        )

    @property
    def coordinator(self) -> Any | None:
        """The real Coordinator instance after ``run`` begins, otherwise ``None``."""

        return self._coordinator

    @property
    def worker_outcomes(self) -> dict[WorkerProfile, CoordinatorWorkerOutcome]:
        """Copy of per-worker terminal states retained before Coordinator returns."""

        return dict(self._worker_outcomes)
