from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

from daxalgo_strategy_agent.native.process import (
    NativeSandboxUnavailable,
    build_macos_sandbox_command,
    run_bounded_process,
)


@pytest.mark.skipif(sys.platform != "darwin", reason="macOS sandbox boundary")
def test_macos_sandbox_blocks_external_files_network_and_frozen_writes(
    tmp_path: Path,
) -> None:
    workspace = tmp_path / "workspace"
    outside = tmp_path / "outside"
    workspace.mkdir()
    outside.mkdir()
    secret = outside / "secret.txt"
    secret.write_text("secret", encoding="utf-8")
    frozen = workspace / "bars.csv"
    frozen.write_text("frozen", encoding="utf-8")
    script = workspace / "probe.py"
    script.write_text(
        "import json, pathlib, socket, sys\n"
        "secret, frozen = map(pathlib.Path, sys.argv[1:])\n"
        "observed = {}\n"
        "try:\n"
        "    secret.read_bytes(); observed['external_read'] = True\n"
        "except OSError:\n"
        "    observed['external_read'] = False\n"
        "try:\n"
        "    frozen.write_text('changed'); observed['frozen_write'] = True\n"
        "except OSError:\n"
        "    observed['frozen_write'] = False\n"
        "try:\n"
        "    socket.socket().connect(('127.0.0.1', 9)); observed['network'] = True\n"
        "except OSError:\n"
        "    observed['network'] = False\n"
        "(pathlib.Path(__file__).parent / 'allowed.txt').write_text('allowed')\n"
        "print(json.dumps(observed, sort_keys=True))\n",
        encoding="utf-8",
    )
    interpreter = Path(sys.executable)
    command = build_macos_sandbox_command(
        (str(interpreter), "-I", str(script), str(secret), str(frozen)),
        interpreter=interpreter,
        readable_roots=(workspace,),
        writable_roots=(workspace,),
        immutable_paths=(frozen,),
    )

    result = run_bounded_process(
        command,
        cwd=workspace,
        env={
            "LANG": "C",
            "LC_ALL": "C",
            "PYTHONDONTWRITEBYTECODE": "1",
            "PYTHONNOUSERSITE": "1",
            "TZ": "UTC",
        },
        timeout_seconds=10,
    )

    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout) == {
        "external_read": False,
        "frozen_write": False,
        "network": False,
    }
    assert frozen.read_text(encoding="utf-8") == "frozen"
    assert (workspace / "allowed.txt").read_text(encoding="utf-8") == "allowed"


def test_sandbox_rejects_a_command_for_another_interpreter(tmp_path: Path) -> None:
    interpreter = Path(sys.executable)
    with pytest.raises((NativeSandboxUnavailable, ValueError)):
        build_macos_sandbox_command(
            ("/usr/bin/python3", "-I", "probe.py"),
            interpreter=interpreter,
            readable_roots=(tmp_path,),
            writable_roots=(tmp_path,),
        )


def test_bounded_process_discards_output_after_limit(tmp_path: Path) -> None:
    result = run_bounded_process(
        (sys.executable, "-I", "-c", "print('x' * 10000)"),
        cwd=tmp_path,
        env={"LANG": "C", "LC_ALL": "C"},
        timeout_seconds=10,
        max_stdout_bytes=128,
    )

    assert result.returncode == 0
    assert result.stdout_truncated is True
    assert len(result.stdout.encode("utf-8")) == 128
