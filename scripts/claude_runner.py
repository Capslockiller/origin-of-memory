#!/usr/bin/env python3
"""Shared hardened Claude CLI runner for the memory scripts."""

# yazan: codex · model: gpt-5.6-sol

from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def run_claude(
    prompt: str,
    *,
    model: str,
    tools: str,
    timeout: int,
    cwd: Path | None = None,
    vault_root: Path | None = None,
    temporary_prefix: str = "beyin-claude-",
    permission_mode: str | None = None,
    allowed_tools: str | None = None,
) -> tuple[str | None, str | None]:
    """Run ``claude -p`` with the shared isolation and recursion hardening."""
    claude = shutil.which("claude")
    if claude is None:
        return None, "claude-cli-missing"

    command = [
        claude,
        "-p",
        "--model",
        model,
        "--output-format",
        "text",
        "--safe-mode",
        "--tools",
        tools,
    ]
    if permission_mode is not None:
        command.extend(["--permission-mode", permission_mode])
    if allowed_tools is not None:
        command.extend(["--allowedTools", allowed_tools])

    environment = os.environ.copy()
    environment["BEYIN_INVOKED_BY"] = "beyin-scripts"

    def invoke(run_directory: Path) -> tuple[str | None, str | None]:
        try:
            result = subprocess.run(
                command,
                input=prompt,
                text=True,
                encoding="utf-8",
                errors="replace",
                capture_output=True,
                cwd=run_directory,
                env=environment,
                timeout=timeout,
                check=False,
            )
        except subprocess.TimeoutExpired:
            return None, "claude-timeout"
        except OSError:
            return None, "claude-exec-error"
        if result.returncode != 0:
            return None, f"claude-exit-{result.returncode}"
        return result.stdout.strip(), None

    if cwd is not None:
        return invoke(cwd)

    if vault_root is None:
        raise ValueError("vault-root-required-without-cwd")
    try:
        with tempfile.TemporaryDirectory(prefix=temporary_prefix) as temporary:
            temporary_path = Path(temporary).resolve()
            vault_resolved = vault_root.resolve()
            try:
                inside_vault = (
                    os.path.commonpath([temporary_path, vault_resolved])
                    == str(vault_resolved)
                )
            except ValueError:
                inside_vault = False
            if inside_vault:
                return None, "temporary-directory-inside-vault"
            return invoke(temporary_path)
    except OSError:
        return None, "claude-exec-error"
