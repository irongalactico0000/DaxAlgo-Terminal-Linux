from __future__ import annotations

import json
from types import SimpleNamespace
from typing import Any

import daxalgo_strategy_agent.cli as cli_module
from daxalgo_strategy_agent.cli import main
from daxalgo_strategy_agent.settings import StrategyAgentConfigurationError


class _SettingsSource:
    value: Any = object()

    @classmethod
    def from_environment(cls) -> Any:
        return cls.value


def test_preflight_command_prints_only_secret_free_report(
    monkeypatch: Any,
    capsys: Any,
) -> None:
    secret = "must-never-be-rendered"
    monkeypatch.setattr(cli_module, "StrategyAgentSettings", _SettingsSource)
    monkeypatch.setattr(
        cli_module,
        "preflight",
        lambda settings: SimpleNamespace(
            as_dict=lambda: {
                "status": "passed",
                "model": "openrouter/openai/gpt-4.1",
                "credential_environment_names": ["OPENROUTER_API_KEY"],
            }
        ),
    )

    assert main(["preflight"]) == 0

    output = capsys.readouterr()
    payload = json.loads(output.out)
    assert payload["status"] == "passed"
    assert payload["credential_environment_names"] == ["OPENROUTER_API_KEY"]
    assert secret not in output.out
    assert output.err == ""


def test_serve_command_uses_only_loopback_and_production_app(
    monkeypatch: Any,
) -> None:
    calls: list[tuple[Any, dict[str, Any]]] = []
    application = SimpleNamespace(app=object())
    monkeypatch.setattr(cli_module, "StrategyAgentSettings", _SettingsSource)
    monkeypatch.setattr(cli_module, "build_application", lambda settings: application)
    monkeypatch.setattr(
        cli_module.uvicorn,
        "run",
        lambda app, **kwargs: calls.append((app, kwargs)),
    )

    assert main(["serve", "--port", "9876", "--log-level", "warning"]) == 0

    assert calls == [
        (
            application.app,
            {"host": "127.0.0.1", "port": 9876, "log_level": "warning"},
        )
    ]


def test_preflight_failure_reports_exact_stage_without_traceback(
    monkeypatch: Any,
    capsys: Any,
) -> None:
    monkeypatch.setattr(cli_module, "StrategyAgentSettings", _SettingsSource)

    def fail(_settings: Any) -> None:
        raise StrategyAgentConfigurationError(
            "query_engine_provider_credentials",
            "OPENROUTER_API_KEY is required for configured openrouter model access",
        )

    monkeypatch.setattr(cli_module, "preflight", fail)

    assert main(["preflight"]) == 2

    output = capsys.readouterr()
    payload = json.loads(output.err)
    assert output.out == ""
    assert payload == {
        "status": "failed",
        "stage": "query_engine_provider_credentials",
        "reason": (
            "OPENROUTER_API_KEY is required for configured openrouter model access"
        ),
    }
