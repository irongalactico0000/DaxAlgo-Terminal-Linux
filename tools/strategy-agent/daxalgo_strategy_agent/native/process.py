"""Bounded native subprocess execution with process-group cleanup."""

from __future__ import annotations

import os
import signal
import subprocess
import sys
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping, Sequence


class NativeProcessTimeout(TimeoutError):
    pass


class NativeSandboxUnavailable(RuntimeError):
    pass


@dataclass(frozen=True)
class NativeProcessResult:
    returncode: int
    stdout: str
    stderr: str
    stdout_truncated: bool
    stderr_truncated: bool


def run_bounded_process(
    command: Sequence[str],
    *,
    cwd: Path,
    env: Mapping[str, str],
    timeout_seconds: int,
    stdin_text: str | None = None,
    max_stdout_bytes: int = 2 * 1024 * 1024,
    max_stderr_bytes: int = 2 * 1024 * 1024,
) -> NativeProcessResult:
    if timeout_seconds < 1:
        raise ValueError("timeout_seconds must be positive")
    if max_stdout_bytes < 1 or max_stderr_bytes < 1:
        raise ValueError("native output limits must be positive")

    process = subprocess.Popen(
        list(command),
        cwd=cwd,
        env=dict(env),
        stdin=subprocess.PIPE if stdin_text is not None else subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        start_new_session=True,
    )
    stdout_buffer = bytearray()
    stderr_buffer = bytearray()
    truncated = {"stdout": False, "stderr": False}

    def drain(stream: object, buffer: bytearray, limit: int, name: str) -> None:
        while True:
            chunk = stream.read(64 * 1024)  # type: ignore[attr-defined]
            if not chunk:
                return
            remaining = limit - len(buffer)
            if remaining > 0:
                buffer.extend(chunk[:remaining])
            if len(chunk) > remaining:
                truncated[name] = True

    assert process.stdout is not None
    assert process.stderr is not None
    readers = (
        threading.Thread(
            target=drain,
            args=(process.stdout, stdout_buffer, max_stdout_bytes, "stdout"),
            daemon=True,
        ),
        threading.Thread(
            target=drain,
            args=(process.stderr, stderr_buffer, max_stderr_bytes, "stderr"),
            daemon=True,
        ),
    )
    for reader in readers:
        reader.start()

    try:
        if stdin_text is not None:
            assert process.stdin is not None
            process.stdin.write(stdin_text.encode("utf-8"))
            process.stdin.close()
        try:
            returncode = process.wait(timeout=timeout_seconds)
        except subprocess.TimeoutExpired as exc:
            _kill_process_group(process.pid)
            process.wait()
            raise NativeProcessTimeout(
                f"native process exceeded {timeout_seconds} seconds"
            ) from exc
    finally:
        # The direct child can exit while a generated strategy leaves descendants behind.  Its
        # unique session/process group is always reaped before returning to the service.
        _kill_process_group(process.pid)
        for reader in readers:
            reader.join(timeout=5)
        process.stdout.close()
        process.stderr.close()

    return NativeProcessResult(
        returncode=returncode,
        stdout=stdout_buffer.decode("utf-8", errors="replace"),
        stderr=stderr_buffer.decode("utf-8", errors="replace"),
        stdout_truncated=truncated["stdout"],
        stderr_truncated=truncated["stderr"],
    )


def build_macos_sandbox_command(
    command: Sequence[str],
    *,
    interpreter: Path,
    readable_roots: Sequence[Path],
    writable_roots: Sequence[Path],
    immutable_paths: Sequence[Path] = (),
) -> tuple[str, ...]:
    """Wrap an exact Python command in the macOS seatbelt used by native lanes."""

    sandbox_exec = Path("/usr/bin/sandbox-exec")
    if sys.platform != "darwin" or not sandbox_exec.is_file():
        raise NativeSandboxUnavailable(
            "macOS sandbox-exec is required for generated strategy code"
        )
    if not command or Path(command[0]) != interpreter:
        raise ValueError(
            "sandboxed native command must start with the configured interpreter"
        )

    interpreter_path = Path(os.path.abspath(os.fspath(interpreter)))
    real_interpreter = interpreter_path.resolve(strict=True)
    venv_root = interpreter_path.parent.parent.resolve(strict=True)
    python_root = real_interpreter.parent.parent.resolve(strict=True)
    read_roots = {
        venv_root,
        python_root,
        *(Path(path).resolve(strict=True) for path in readable_roots),
    }
    write_roots = {Path(path).resolve(strict=True) for path in writable_roots}
    denied_writes = {Path(path).resolve(strict=True) for path in immutable_paths}
    if not write_roots:
        raise ValueError("at least one writable sandbox root is required")
    for path in denied_writes:
        if not any(path == root or path.is_relative_to(root) for root in write_roots):
            raise ValueError(f"immutable path is not under a writable root: {path}")

    profile_parts = [
        "(version 1)",
        '(import "system.sb")',
        "(allow sysctl-read)",
        "(allow file-read-metadata)",
        "(allow process-exec "
        f"(literal {_sbpl_string(interpreter_path)}) "
        f"(literal {_sbpl_string(real_interpreter)}))",
    ]
    if read_roots:
        profile_parts.append(
            "(allow file-read* "
            + " ".join(f"(subpath {_sbpl_string(path)})" for path in sorted(read_roots))
            + ")"
        )
    profile_parts.append(
        "(allow file-write* "
        + " ".join(f"(subpath {_sbpl_string(path)})" for path in sorted(write_roots))
        + ")"
    )
    if denied_writes:
        profile_parts.append(
            "(deny file-write* "
            + " ".join(
                f"(literal {_sbpl_string(path)})" for path in sorted(denied_writes)
            )
            + ")"
        )
    profile_parts.append("(deny network*)")
    profile = " ".join(profile_parts)
    return (str(sandbox_exec), "-p", profile, *command)


def _sbpl_string(path: Path) -> str:
    value = os.fspath(path)
    escaped = value.replace("\\", "\\\\").replace('"', '\\"')
    return f'"{escaped}"'


def _kill_process_group(pid: int) -> None:
    try:
        os.killpg(pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    except PermissionError:
        try:
            os.kill(pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass


__all__ = [
    "NativeProcessResult",
    "NativeProcessTimeout",
    "NativeSandboxUnavailable",
    "build_macos_sandbox_command",
    "run_bounded_process",
]
