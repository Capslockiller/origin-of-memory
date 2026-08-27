#!/usr/bin/env python3
"""Antigravity CLI (`agy`) backend for the background model calls.

Google retired Gemini CLI's serving on 2026-06-18; the successor headless CLI
is Antigravity's ``agy``.  This module covers the text-mode calls only (flush
summarize, ingest summarize).  Tool-mode (compile) is refused upstream in
:mod:`claude_runner` because ``agy`` has no per-invocation permission scoping.

Headless contract (antigravity.google/docs/cli/headless):
``agy -p "<prompt>" --model <slug> --output-format text``
"""

# yazan: codex · model: gpt-5.6-sol

from __future__ import annotations

import os
from pathlib import Path
import re
import shutil
import subprocess

import claude_runner


BIN_ENV = "BEYIN_AGY_BIN"
FAST_MODEL_ENV = "BEYIN_AGY_MODEL_FAST"
SMART_MODEL_ENV = "BEYIN_AGY_MODEL_SMART"
DEFAULT_BIN = "agy"
# The only slug the official docs show.  Others must come from `agy models`;
# we do not invent them.
DEFAULT_FAST_MODEL = "gemini-3.5-flash-medium"
# Windows script shims cannot be started by CreateProcess directly; the same
# fixed cmd.exe bridge used for codex in ingest_common is reused here.
SHIM_SUFFIXES = {".cmd", ".bat"}
AUTH_PATTERN = re.compile(
    r"not\s+(?:logged\s*in|authenticated|signed\s*in)"
    r"|unauthenticated|unauthorized|401"
    r"|no\s+credentials|credentials?\s+(?:not\s+found|missing|expired)"
    r"|please\s+(?:log\s*in|sign\s*in)"
    r"|run\s+`?agy`?\s+to\s+(?:log\s*in|sign\s*in|authenticate)",
    re.IGNORECASE,
)


def resolve_model(
    model: str,
    environment: dict[str, str] | None = None,
) -> tuple[str, str | None]:
    """Map the caller's ``haiku``/``sonnet`` tier onto an agy model slug."""
    env = os.environ if environment is None else environment
    fast = (env.get(FAST_MODEL_ENV) or "").strip() or DEFAULT_FAST_MODEL
    if model == "haiku":
        return fast, None
    if model == "sonnet":
        smart = (env.get(SMART_MODEL_ENV) or "").strip()
        if smart:
            return smart, None
        # No documented smart slug exists; the user must pick one from
        # `agy models`.  Until then we degrade to the fast model, loudly.
        return fast, f"warn:agy-smart-model-unset:{SMART_MODEL_ENV}"
    return fast, f"warn:agy-model-unmapped:{model}"


def resolve_binary(
    environment: dict[str, str] | None = None,
) -> tuple[list[str] | None, str | None]:
    """Locate ``agy`` and build the argv prefix (with the Windows shim bridge)."""
    env = os.environ if environment is None else environment
    name = (env.get(BIN_ENV) or "").strip() or DEFAULT_BIN
    found = shutil.which(name)
    if found is None:
        return None, "agy-missing"
    if os.name == "nt" and Path(found).suffix.casefold() in SHIM_SUFFIXES:
        comspec = env.get("COMSPEC") or "cmd.exe"
        return [comspec, "/d", "/s", "/c", found], None
    return [found], None


def run_agy(
    prompt: str,
    *,
    model: str,
    timeout: int,
    cwd: Path | None = None,
    vault_root: Path | None = None,
    temporary_prefix: str = "beyin-agy-",
    warnings: list[str] | None = None,
) -> tuple[str | None, str | None]:
    """Run one headless, text-mode ``agy`` call under the shared isolation."""
    sink = warnings if warnings is not None else []
    slug, model_warning = resolve_model(model)
    if model_warning:
        sink.append(model_warning)

    prefix, missing = resolve_binary()
    if prefix is None:
        return None, missing

    command = prefix + [
        "-p",
        prompt,
        "--model",
        slug,
        "--output-format",
        "text",
    ]

    environment = os.environ.copy()
    environment["BEYIN_INVOKED_BY"] = "beyin-scripts"

    def invoke(run_directory: Path) -> tuple[str | None, str | None]:
        try:
            result = subprocess.run(
                command,
                stdin=subprocess.DEVNULL,
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
            return None, "agy-timeout"
        except OSError:
            return None, "agy-exec-error"
        if result.returncode != 0:
            stderr = result.stderr or ""
            if AUTH_PATTERN.search(stderr):
                return None, "agy-auth-missing"
            return None, "agy-exec-error"
        return result.stdout.strip(), None

    return claude_runner.run_in_isolated_dir(
        invoke,
        cwd=cwd,
        vault_root=vault_root,
        temporary_prefix=temporary_prefix,
        exec_error="agy-exec-error",
    )
