"""Production composition for the native DaxAlgo strategy-agent service.

This is the only place that joins the pinned FinanceManus QueryEngine runtime, the two genuine
native workers, durable run custody, and the loopback FastAPI surface.  It does not add another
strategy representation or execution path.
"""

from __future__ import annotations

import json
import os
import re
import sys
from collections.abc import Callable, Mapping
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Literal

from fastapi import FastAPI

from .api import create_app
from .native.csp_worker import run_csp_worker
from .native.vibequant_worker import run_vibequant_worker
from .native_coordinator import CoordinatedNativeRunners
from .profiles import ResearchQueryEngineCoordinator, make_research_profile
from .queryengine_runtime import (
    FinanceManusBindings,
    RuntimeGateError,
    WorkerProfile,
    load_financemanus,
)
from .run_store import NativeRunStore
from .service import StrategyAgentService
from .settings import (
    StrategyAgentConfigurationError,
    StrategyAgentSettings,
    read_environment_file,
)


_ENVIRONMENT_KEYS = frozenset(
    {
        "ANTHROPIC_API_KEY",
        "EASTMANUS_FALLBACK_MODEL",
        "EASTMANUS_MODEL",
        "OPENAI_API_KEY",
        "OPENROUTER_API_KEY",
    }
)
_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")


@dataclass(frozen=True)
class QueryEngineLimits:
    """Explicit token/turn limits applied to every production QueryEngine instance."""

    max_tokens: int
    max_context_tokens: int
    max_budget_tokens: int
    max_turns_per_query: int
    thinking_enabled: bool = False
    thinking_budget_tokens: int = 0

    def __post_init__(self) -> None:
        values = (
            self.max_tokens,
            self.max_context_tokens,
            self.max_budget_tokens,
            self.max_turns_per_query,
        )
        if any(type(value) is not int or value < 1 for value in values):
            raise ValueError(
                "QueryEngine token and turn limits must be positive integers"
            )
        if type(self.thinking_enabled) is not bool:
            raise TypeError("thinking_enabled must be a bool")
        if (
            type(self.thinking_budget_tokens) is not int
            or self.thinking_budget_tokens < 0
        ):
            raise ValueError("thinking_budget_tokens must be a non-negative integer")


RESEARCH_LIMITS = QueryEngineLimits(
    max_tokens=8_192,
    max_context_tokens=100_000,
    max_budget_tokens=64_000,
    max_turns_per_query=8,
)
NATIVE_WORKER_LIMITS = QueryEngineLimits(
    max_tokens=16_384,
    max_context_tokens=120_000,
    max_budget_tokens=96_000,
    max_turns_per_query=6,
)


@dataclass(frozen=True)
class StrategyAgentPreflight:
    """Secret-free evidence that the production runtime passed its startup gates."""

    status: Literal["passed"]
    query_engine_python: str
    query_engine_revision: str
    model: str
    fallback_model: str
    providers: tuple[str, ...]
    credential_environment_names: tuple[str, ...]
    vibequant_python: str
    vibequant_revision: str
    akquant_version: str
    csp_python: str
    csp_version: str

    def as_dict(self) -> dict[str, Any]:
        return {
            "status": self.status,
            "query_engine_python": self.query_engine_python,
            "query_engine_revision": self.query_engine_revision,
            "model": self.model,
            "fallback_model": self.fallback_model,
            "providers": list(self.providers),
            "credential_environment_names": list(self.credential_environment_names),
            "vibequant_python": self.vibequant_python,
            "vibequant_revision": self.vibequant_revision,
            "akquant_version": self.akquant_version,
            "csp_python": self.csp_python,
            "csp_version": self.csp_version,
        }


@dataclass(frozen=True)
class ProductionApplication:
    """The constructed production graph retained for diagnostics and loopback serving."""

    settings: StrategyAgentSettings
    preflight: StrategyAgentPreflight
    bindings: FinanceManusBindings
    store: NativeRunStore
    research_coordinator: ResearchQueryEngineCoordinator
    native_runners: CoordinatedNativeRunners
    service: StrategyAgentService
    app: FastAPI


@dataclass(frozen=True)
class _PreparedRuntime:
    settings: StrategyAgentSettings
    bindings: FinanceManusBindings
    environment: Mapping[str, str]
    preflight: StrategyAgentPreflight


def preflight(
    settings: StrategyAgentSettings | None = None,
) -> StrategyAgentPreflight:
    """Verify the exact production interpreter, source pins, and model credentials.

    The configured environment file is read without printing or returning any value.  Only the
    five QueryEngine model/provider variables owned by this composition root are installed in the
    process environment.
    """

    return _prepare_runtime(settings).preflight


def build_application(
    settings: StrategyAgentSettings | None = None,
) -> ProductionApplication:
    """Construct the one production service and FastAPI application."""

    prepared = _prepare_runtime(settings)
    configured = prepared.settings
    state_root = configured.store_root
    state_root.mkdir(mode=0o700, parents=True, exist_ok=True)
    research_root = state_root / ".research"
    research_root.mkdir(mode=0o700, exist_ok=True)
    query_output_root = state_root / ".queryengine-output"
    query_output_root.mkdir(mode=0o700, exist_ok=True)
    disabled_plugins_root = state_root / ".plugins-disabled"
    disabled_plugins_root.mkdir(mode=0o700, exist_ok=True)

    research_config_factory = _bounded_config_factory(
        bindings=prepared.bindings,
        environment=prepared.environment,
        limits=RESEARCH_LIMITS,
        output_root=query_output_root / "research",
        plugins_root=disabled_plugins_root,
    )
    vibequant_config_factory = _bounded_config_factory(
        bindings=prepared.bindings,
        environment=prepared.environment,
        limits=NATIVE_WORKER_LIMITS,
        output_root=query_output_root / "vibequant",
        plugins_root=disabled_plugins_root,
    )
    csp_config_factory = _bounded_config_factory(
        bindings=prepared.bindings,
        environment=prepared.environment,
        limits=NATIVE_WORKER_LIMITS,
        output_root=query_output_root / "csp",
        plugins_root=disabled_plugins_root,
    )

    research_coordinator = ResearchQueryEngineCoordinator(
        bindings=prepared.bindings,
        profile=make_research_profile(
            config_factory=research_config_factory,
            max_turns=RESEARCH_LIMITS.max_turns_per_query,
        ),
        workspace_root=research_root,
    )
    store = NativeRunStore(state_root)

    def vibequant_native_runner(
        manifest: Any,
        workspace: Path,
        *,
        task_spec_relative_path: str,
    ) -> Any:
        return run_vibequant_worker(
            manifest,
            workspace,
            python_executable=configured.vibequant_python,
            vibequant_source_root=configured.vibequant_root,
            task_spec_relative_path=task_spec_relative_path,
        )

    def csp_native_runner(
        manifest: Any,
        workspace: Path,
        *,
        source_relative_path: str,
    ) -> Any:
        return run_csp_worker(
            manifest,
            workspace,
            python_executable=configured.csp_python,
            source_relative_path=source_relative_path,
        )

    native_runners = CoordinatedNativeRunners(
        bindings=prepared.bindings,
        vibequant_config_factory=vibequant_config_factory,
        csp_config_factory=csp_config_factory,
        vibequant_native_runner=vibequant_native_runner,
        csp_native_runner=csp_native_runner,
        event_sink=_coordinator_event_sink(store),
        query_engine_identity={
            "source_revision": prepared.preflight.query_engine_revision,
            "python_version": prepared.bindings.source.python_version,
            "model": prepared.preflight.model,
            "fallback_model": prepared.preflight.fallback_model,
            "providers": list(prepared.preflight.providers),
        },
    )
    service = StrategyAgentService(
        store=store,
        research_coordinator=research_coordinator,
        vibequant_runner=native_runners.vibequant,
        csp_runner=native_runners.csp,
    )
    return ProductionApplication(
        settings=configured,
        preflight=prepared.preflight,
        bindings=prepared.bindings,
        store=store,
        research_coordinator=research_coordinator,
        native_runners=native_runners,
        service=service,
        app=create_app(service),
    )


def _prepare_runtime(
    settings: StrategyAgentSettings | None,
) -> _PreparedRuntime:
    configured = settings or StrategyAgentSettings.from_environment()
    configured.validate()
    configured = replace(
        configured,
        query_engine_python=_configured_python_launcher(
            "DAXALGO_QUERY_ENGINE_PYTHON", configured.query_engine_python
        ),
        vibequant_python=_configured_python_launcher(
            "DAXALGO_VIBEQUANT_PYTHON", configured.vibequant_python
        ),
        csp_python=_configured_python_launcher(
            "DAXALGO_CSP_PYTHON", configured.csp_python
        ),
    )
    _require_current_query_engine_interpreter(configured.query_engine_python)

    file_environment = read_environment_file(configured.query_engine_env_file)
    effective_environment = dict(os.environ)
    effective_environment.update(file_environment)
    model = effective_environment.get("EASTMANUS_MODEL", "").strip()
    if not model:
        raise StrategyAgentConfigurationError(
            "query_engine_model",
            "EASTMANUS_MODEL must name the provider-backed QueryEngine model",
        )
    fallback_model = effective_environment.get(
        "EASTMANUS_FALLBACK_MODEL", model
    ).strip()
    if not fallback_model:
        fallback_model = model
    effective_environment["EASTMANUS_MODEL"] = model
    effective_environment["EASTMANUS_FALLBACK_MODEL"] = fallback_model

    provider_requirements = _provider_requirements((model, fallback_model))
    for provider, key_name in provider_requirements:
        if not effective_environment.get(key_name, "").strip():
            raise StrategyAgentConfigurationError(
                "query_engine_provider_credentials",
                f"{key_name} is required for configured {provider} model access",
            )

    # QueryEngine/LiteLLM discovers provider credentials from process environment.  Apply only
    # the explicit model/provider keys, never arbitrary values from the configured file.
    for name in _ENVIRONMENT_KEYS:
        if name in effective_environment:
            os.environ[name] = effective_environment[name]

    try:
        bindings = load_financemanus(
            configured.query_engine_root,
            expected_revision=configured.pins.query_engine_revision,
        )
    except RuntimeGateError as exc:
        raise StrategyAgentConfigurationError(exc.code, exc.detail) from exc

    providers = tuple(dict.fromkeys(provider for provider, _ in provider_requirements))
    credential_names = tuple(
        dict.fromkeys(key_name for _, key_name in provider_requirements)
    )
    report = StrategyAgentPreflight(
        status="passed",
        query_engine_python=str(configured.query_engine_python),
        query_engine_revision=bindings.source.revision,
        model=model,
        fallback_model=fallback_model,
        providers=providers,
        credential_environment_names=credential_names,
        vibequant_python=str(configured.vibequant_python),
        vibequant_revision=configured.pins.vibequant_revision,
        akquant_version=configured.pins.akquant_version,
        csp_python=str(configured.csp_python),
        csp_version=configured.pins.csp_version,
    )
    return _PreparedRuntime(
        settings=configured,
        bindings=bindings,
        environment=effective_environment,
        preflight=report,
    )


def _require_current_query_engine_interpreter(configured_python: Path) -> None:
    configured = Path(os.path.abspath(os.fspath(configured_python.expanduser())))
    current = Path(os.path.abspath(sys.executable))
    if current != configured:
        raise StrategyAgentConfigurationError(
            "query_engine_python",
            "Production composition must run inside the configured QueryEngine Python "
            f"environment: expected {configured}, observed {current}",
        )


def _configured_python_launcher(name: str, validated_python: Path) -> Path:
    """Recover a configured virtualenv launcher after settings validated its target.

    ``Path.resolve`` intentionally follows virtualenv symlinks, so the settings layer's validated
    path can identify only the shared base Python.  Native dependencies live in the virtualenv;
    keep the original environment path after proving that it points at the validated target.
    """

    raw_value = os.environ.get(name, "").strip()
    if not raw_value:
        return Path(os.path.abspath(os.fspath(validated_python.expanduser())))
    launcher = Path(os.path.abspath(os.fspath(Path(raw_value).expanduser())))
    try:
        launcher_target = launcher.resolve(strict=True)
        validated_target = validated_python.resolve(strict=True)
    except OSError as exc:
        raise StrategyAgentConfigurationError(
            name.lower(), f"Configured Python launcher is unavailable: {launcher}"
        ) from exc
    if launcher_target != validated_target:
        raise StrategyAgentConfigurationError(
            name.lower(),
            f"Configured launcher {launcher} does not match validated Python {validated_python}",
        )
    return launcher


def _provider_requirements(
    models: tuple[str, ...],
) -> tuple[tuple[str, str], ...]:
    requirements: list[tuple[str, str]] = []
    for model in models:
        normalized = model.strip().lower()
        if normalized.startswith("openrouter/"):
            requirement = ("openrouter", "OPENROUTER_API_KEY")
        elif normalized.startswith("anthropic/") or normalized.startswith("claude"):
            requirement = ("anthropic", "ANTHROPIC_API_KEY")
        elif normalized.startswith("openai/") or normalized.startswith(
            ("gpt-", "chatgpt-", "o1", "o3", "o4")
        ):
            requirement = ("openai", "OPENAI_API_KEY")
        else:
            raise StrategyAgentConfigurationError(
                "query_engine_model_provider",
                "Unsupported QueryEngine model provider for "
                f"{model!r}; use openrouter/, anthropic/ or openai/",
            )
        if requirement not in requirements:
            requirements.append(requirement)
    return tuple(requirements)


def _bounded_config_factory(
    *,
    bindings: FinanceManusBindings,
    environment: Mapping[str, str],
    limits: QueryEngineLimits,
    output_root: Path,
    plugins_root: Path,
) -> Callable[[], Any]:
    model = environment["EASTMANUS_MODEL"]
    fallback_model = environment["EASTMANUS_FALLBACK_MODEL"]
    anthropic_api_key = environment.get("ANTHROPIC_API_KEY", "")
    openai_api_key = environment.get("OPENAI_API_KEY", "")
    output_root.mkdir(mode=0o700, parents=True, exist_ok=True)

    def factory() -> Any:
        return bindings.Config(
            anthropic_api_key=anthropic_api_key,
            openai_api_key=openai_api_key,
            model=model,
            fallback_model=fallback_model,
            max_tokens=limits.max_tokens,
            max_context_tokens=limits.max_context_tokens,
            max_budget_tokens=limits.max_budget_tokens,
            max_turns_per_query=limits.max_turns_per_query,
            max_output_tokens_recovery_limit=1,
            thinking_enabled=limits.thinking_enabled,
            thinking_budget_tokens=limits.thinking_budget_tokens,
            output_dir=output_root,
            auto_save_interval_seconds=1_800,
            permission_mode="strict",
            always_allow_read_only=False,
            plugins_dir=plugins_root,
            verbose=False,
            debug=False,
        )

    return factory


def _coordinator_event_sink(
    store: NativeRunStore,
) -> Callable[[str, str, WorkerProfile, Any], None]:
    def sink(
        manifest_run_id: str,
        coordinator_run_hash: str,
        profile: WorkerProfile,
        original_event: Any,
    ) -> None:
        if not _SHA256_RE.fullmatch(coordinator_run_hash):
            raise ValueError("coordinator_run_hash must be a lowercase SHA-256 digest")
        event = _exact_json_event(original_event)
        event_type = event["type"]
        stage = f"queryengine.{event_type}"
        if len(stage) > 100:
            raise ValueError("QueryEngine event type exceeds the retained stage limit")
        store.append_event(
            manifest_run_id,
            lane=profile.value,
            stage=stage,
            status="failed" if event_type == "error" else "progress",
            message=f"FinanceManus QueryEngine emitted {event_type}.",
            details={
                "coordinator_run_hash": coordinator_run_hash,
                "profile": profile.value,
                "event": event,
            },
        )

    return sink


def _exact_json_event(event: Any) -> dict[str, Any]:
    event_type = getattr(event, "type", None)
    event_data = getattr(event, "data", None)
    if not isinstance(event_type, str) or not event_type:
        raise TypeError("QueryEngine event must expose a non-empty string type")
    if not isinstance(event_data, dict):
        raise TypeError("QueryEngine event must expose a dictionary data payload")
    try:
        encoded = json.dumps(
            {"type": event_type, "data": event_data},
            allow_nan=False,
            ensure_ascii=False,
            separators=(",", ":"),
            sort_keys=True,
        )
        decoded = json.loads(encoded)
    except (TypeError, ValueError) as exc:
        raise ValueError(f"QueryEngine event is not finite JSON data: {exc}") from exc
    return decoded
