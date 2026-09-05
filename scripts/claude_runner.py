#!/usr/bin/env python3
"""Shared hardened model runner with optional local and CLI backends.

``run_claude`` keeps its historical name and signature so every existing caller
works untouched.  ``BEYIN_MODEL_BACKEND`` can dispatch text-mode calls to the
Antigravity CLI, a local Ollama server, or an OpenAI-compatible endpoint instead.
"""

# yazan: codex · model: gpt-5.6-sol

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
from typing import Any, Callable
import uuid

from beyin_ortak import record_call
import nezaket


SCRIPT_DIR = Path(__file__).resolve().parent
STATE_DIR = SCRIPT_DIR / ".state"

BACKEND_ENV = "BEYIN_MODEL_BACKEND"
BACKEND_CLAUDE = "claude"
BACKEND_ANTIGRAVITY = "antigravity"
BACKEND_OLLAMA = "ollama"
BACKEND_OPENAI_COMPAT = "openai-compat"
# Antigravity has no per-invocation permission scoping, so a tool-mode call
# (compile) can only be auto-approved globally.  We refuse instead.
COMPILE_UNSUPPORTED = "antigravity-backend-unsupported:compile"
OLLAMA_COMPILE_UNSUPPORTED = "ollama-backend-unsupported:compile"
OPENAI_COMPAT_COMPILE_UNSUPPORTED = (
    "openai-compat-backend-unsupported:compile"
)

# Model-call timeouts.  Historical values: flush and ingest 240 s, compile 900 s.
# Local inference is far slower than a hosted API — an 8B model summarising a
# 15k-character transcript on CPU can exceed 240 s — so the ollama and
# openai-compat backends raise the *default* for the text-mode calls.  An explicit
# environment value always wins, and the claude/antigravity defaults are unchanged.
TIMEOUT_ENV = {
    "flush": "BEYIN_FLUSH_TIMEOUT",
    "compile": "BEYIN_COMPILE_TIMEOUT",
    "ingest": "BEYIN_INGEST_TIMEOUT",
}
DEFAULT_TIMEOUTS = {"flush": 240, "compile": 900, "ingest": 240}
LOCAL_INFERENCE_TIMEOUT = 900
# Compile is excluded: it already sits at 900 s and never runs on a local backend.
LOCAL_TIMEOUT_KINDS = ("flush", "ingest")

# The claude CLI's tier aliases have drifted from what they used to resolve
# to (``--model haiku`` now lands on Sonnet in this CLI build) — an explicit
# id per tier is the only way to know which model actually ran. Overridable
# per tier via BEYIN_CLAUDE_MODEL_<TIER> without touching this table.
CLAUDE_MODEL_ENV_PREFIX = "BEYIN_CLAUDE_MODEL_"
CLAUDE_MODEL_IDS = {
    "haiku": "claude-haiku-4-5-20251001",
    "sonnet": "claude-sonnet-5",
    "opus": "claude-opus-5",
}


def resolve_claude_model_id(
    tier: str, environment: dict[str, str] | None = None
) -> str:
    """The explicit model id the ``claude`` backend sends for ``tier``.

    An unmapped tier (a slug the caller already passes explicitly) is
    returned unchanged rather than guessed at.
    """
    env = os.environ if environment is None else environment
    override = (env.get(f"{CLAUDE_MODEL_ENV_PREFIX}{tier.upper()}") or "").strip()
    if override:
        return override
    return CLAUDE_MODEL_IDS.get(tier, tier)


_LAST_WARNINGS: list[str] = []
# Real usage for the most recent claude-backend call, filled in by ``_dispatch``
# and drained into the ledger by ``run_claude``. Empty means "no real usage
# available this call" — the ledger then records the chars/4 estimate only.
_LAST_CLAUDE_USAGE: dict[str, Any] = {}


def _parse_claude_json_reply(raw: str) -> tuple[str, dict[str, Any]]:
    """Split a ``claude -p --output-format json`` reply into text and usage.

    The CLI's own JSON summary already aggregates every turn's ``usage``
    block for the session (see its ``iterations``) and names the model(s)
    that actually ran in ``modelUsage`` — resolving alias drift (``haiku``
    silently landing on Sonnet) for free. Anything that does not parse as
    the expected shape falls back to treating ``raw`` as the plain-text
    reply this function used to receive directly, with no usage — a CLI
    quirk must never break the call it is accounting for.
    """
    try:
        parsed = json.loads(raw)
    except (ValueError, TypeError):
        return raw.strip(), {}
    if not isinstance(parsed, dict):
        return raw.strip(), {}
    text = parsed.get("result")
    text = text.strip() if isinstance(text, str) else raw.strip()
    usage = parsed.get("usage")
    if not isinstance(usage, dict):
        return text, {}
    model_usage = parsed.get("modelUsage")
    model_actual = ""
    if isinstance(model_usage, dict) and model_usage:
        model_actual = "+".join(sorted(model_usage.keys()))
    return text, {
        "input_tokens": usage.get("input_tokens"),
        "output_tokens": usage.get("output_tokens"),
        "cache_read_tokens": usage.get("cache_read_input_tokens"),
        "cache_write_tokens": usage.get("cache_creation_input_tokens"),
        "model_actual": model_actual,
        "usage_source": "session-log",
    }


def resolve_timeout(
    kind: str,
    environment: dict[str, str] | None = None,
    backend: str | None = None,
) -> tuple[int, str | None]:
    """Resolve one model call's timeout in seconds plus an optional warning.

    Mirrors ``flush.resolve_flush_chunk_chars``: an unusable environment value is
    reported through health and ignored rather than failing the run, because a
    typo in a variable must never break the session the hook is attached to.
    """
    name = TIMEOUT_ENV[kind]
    env = os.environ if environment is None else environment
    warning = None
    if name in env:
        raw = env.get(name) or ""
        try:
            value = int(raw)
        except ValueError:
            value = 0
        if value > 0:
            return value, None
        warning = f"warn:timeout-invalid:{name}:{raw}"

    if kind in LOCAL_TIMEOUT_KINDS:
        if backend is None:
            backend, _backend_warning = resolve_backend(env)
        if backend in (BACKEND_OLLAMA, BACKEND_OPENAI_COMPAT):
            return LOCAL_INFERENCE_TIMEOUT, warning
    return DEFAULT_TIMEOUTS[kind], warning


def last_warnings() -> list[str]:
    """Drain the warnings recorded by the most recent run (fail loud, run quiet)."""
    drained = list(_LAST_WARNINGS)
    _LAST_WARNINGS.clear()
    return drained


def resolve_backend(
    environment: dict[str, str] | None = None,
) -> tuple[str, str | None]:
    """Map ``BEYIN_MODEL_BACKEND`` to a backend name plus an optional warning."""
    env = os.environ if environment is None else environment
    raw = (env.get(BACKEND_ENV) or "").strip().casefold()
    if raw in ("", BACKEND_CLAUDE):
        return BACKEND_CLAUDE, None
    if raw == BACKEND_ANTIGRAVITY:
        return BACKEND_ANTIGRAVITY, None
    if raw == BACKEND_OLLAMA:
        return BACKEND_OLLAMA, None
    if raw == BACKEND_OPENAI_COMPAT:
        return BACKEND_OPENAI_COMPAT, None
    if raw == "openai":
        return BACKEND_OPENAI_COMPAT, "warn:backend-alias:openai"
    if raw == "gemini":
        # Gemini CLI's serving was retired 2026-06-18; `gemini` now means the
        # Antigravity CLI successor, but the caller is told the name is stale.
        return BACKEND_ANTIGRAVITY, "warn:backend-alias-deprecated:gemini"
    return BACKEND_CLAUDE, f"warn:backend-unknown:{raw}"


def compile_backend(
    environment: dict[str, str] | None = None,
) -> tuple[str, str | None]:
    """Backend for the tool-mode compile call.

    In antigravity, ollama, or openai-compat mode compile keeps running on
    ``claude`` when that binary is on PATH; otherwise the selected backend is
    returned so the call fails loud with its tool-mode refusal.
    """
    backend, warning = resolve_backend(environment)
    if backend not in (
        BACKEND_ANTIGRAVITY,
        BACKEND_OLLAMA,
        BACKEND_OPENAI_COMPAT,
    ):
        return backend, warning
    if shutil.which("claude") is not None:
        fallback_warning = {
            BACKEND_ANTIGRAVITY: "warn:antigravity-compile-fallback-claude",
            BACKEND_OLLAMA: "warn:ollama-compile-fallback-claude",
            BACKEND_OPENAI_COMPAT: (
                "warn:openai-compat-compile-fallback-claude"
            ),
        }[backend]
        return BACKEND_CLAUDE, fallback_warning
    return backend, warning


def run_in_isolated_dir(
    invoke: Callable[[Path], tuple[str | None, str | None]],
    *,
    cwd: Path | None,
    vault_root: Path | None,
    temporary_prefix: str,
    exec_error: str,
) -> tuple[str | None, str | None]:
    """Run ``invoke`` in ``cwd`` or in a throwaway directory outside the vault."""
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
        return None, exec_error


def _resolved_slug(backend: str, model: str) -> str:
    """The concrete slug the backend will use, recorded for the ledger only.

    Resolution is a pure read of the environment, so asking a second time costs
    nothing and cannot change what the call does. An unmapped tier records an
    empty slug rather than a guess — the ledger says what ran, or says nothing.
    """
    try:
        if backend == BACKEND_ANTIGRAVITY:
            import agy_runner

            return agy_runner.resolve_model(model)[0] or ""
        if backend == BACKEND_OLLAMA:
            import ollama_runner

            return ollama_runner.resolve_model(model)[0] or ""
        if backend == BACKEND_OPENAI_COMPAT:
            import openai_runner

            return openai_runner.resolve_model(model)[0] or ""
    except Exception:
        return ""
    # The Claude CLI is given an explicit model id, not the bare tier name —
    # see CLAUDE_MODEL_IDS. This is the id actually sent, recorded here so a
    # tier-to-model drift (like `haiku` silently landing on Sonnet) shows up.
    return resolve_claude_model_id(model)


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
    backend: str | None = None,
    component: str = "unknown",
    state_dir: Path | None = None,
) -> tuple[str | None, str | None]:
    """Run the selected backend, and account for the call in ``.state/calls.jsonl``.

    Accounting sits here because this is the single choke point every model call
    already passes through, so no caller can opt out of being measured. Only
    counts and identifiers are written — see ``beyin_ortak.record_call``.
    """
    _LAST_WARNINGS.clear()
    _LAST_CLAUDE_USAGE.clear()
    if backend is None:
        backend, warning = resolve_backend()
        if warning:
            _LAST_WARNINGS.append(warning)

    resolved_state_dir = STATE_DIR if state_dir is None else state_dir
    started = time.monotonic()
    output, error = _dispatch(
        prompt,
        model=model,
        tools=tools,
        timeout=timeout,
        cwd=cwd,
        vault_root=vault_root,
        temporary_prefix=temporary_prefix,
        permission_mode=permission_mode,
        allowed_tools=allowed_tools,
        backend=backend,
        state_dir=resolved_state_dir,
    )
    record_call(
        resolved_state_dir,
        backend=backend,
        model_tier=model,
        model_slug=_resolved_slug(backend, model),
        component=component,
        input_chars=len(prompt),
        output_chars=len(output or ""),
        duration_ms=int((time.monotonic() - started) * 1000),
        outcome="ok" if error is None else error,
        input_tokens=_LAST_CLAUDE_USAGE.get("input_tokens"),
        output_tokens=_LAST_CLAUDE_USAGE.get("output_tokens"),
        cache_read_tokens=_LAST_CLAUDE_USAGE.get("cache_read_tokens"),
        cache_write_tokens=_LAST_CLAUDE_USAGE.get("cache_write_tokens"),
        model_actual=_LAST_CLAUDE_USAGE.get("model_actual"),
        usage_source=_LAST_CLAUDE_USAGE.get("usage_source", "estimate"),
    )
    return output, error


def _dispatch(
    prompt: str,
    *,
    model: str,
    tools: str,
    timeout: int,
    cwd: Path | None,
    vault_root: Path | None,
    temporary_prefix: str,
    permission_mode: str | None,
    allowed_tools: str | None,
    backend: str,
    state_dir: Path,
) -> tuple[str | None, str | None]:
    """Run the selected backend with the shared isolation and recursion hardening."""
    if backend == BACKEND_ANTIGRAVITY:
        if tools:
            # Tool-mode (compile) needs scoped write permission; agy only offers
            # a user-global allow-list or blanket auto-approval.  Refuse.
            return None, COMPILE_UNSUPPORTED
        import agy_runner

        return agy_runner.run_agy(
            prompt,
            model=model,
            timeout=timeout,
            cwd=cwd,
            vault_root=vault_root,
            temporary_prefix=temporary_prefix,
            warnings=_LAST_WARNINGS,
        )

    if backend == BACKEND_OLLAMA:
        if tools:
            # Ollama's generate endpoint is text-only; it cannot perform the
            # scoped staging-tree writes required by compile.
            return None, OLLAMA_COMPILE_UNSUPPORTED
        import ollama_runner

        return ollama_runner.run_ollama(
            prompt,
            model=model,
            timeout=timeout,
            cwd=cwd,
            vault_root=vault_root,
            state_dir=state_dir,
            temporary_prefix=temporary_prefix,
            warnings=_LAST_WARNINGS,
        )

    if backend == BACKEND_OPENAI_COMPAT:
        if tools:
            # OpenAI-compatible chat endpoints are text-only here; they cannot
            # perform the scoped staging-tree writes required by compile.
            return None, OPENAI_COMPAT_COMPILE_UNSUPPORTED
        import openai_runner

        return openai_runner.run_openai(
            prompt,
            model=model,
            timeout=timeout,
            cwd=cwd,
            vault_root=vault_root,
            temporary_prefix=temporary_prefix,
            warnings=_LAST_WARNINGS,
        )

    claude = shutil.which("claude")
    if claude is None:
        return None, "claude-cli-missing"

    resolved_model_id = resolve_claude_model_id(model)
    # --output-format json (in place of the historical "text") is what makes
    # real usage capture possible: the CLI's own JSON summary already
    # aggregates every turn's usage for the whole session and names the
    # model(s) that actually ran, in one place, with nothing to poll or race.
    # --session-id is passed too, purely for external traceability, even
    # though nothing here needs to locate a transcript file by it.
    command = [
        claude,
        "-p",
        "--model",
        resolved_model_id,
        "--output-format",
        "json",
        "--session-id",
        str(uuid.uuid4()),
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
                creationflags=nezaket.dusuk_oncelik_bayraklari(),
            )
        except subprocess.TimeoutExpired:
            return None, "claude-timeout"
        except OSError:
            return None, "claude-exec-error"
        if result.returncode != 0:
            return None, f"claude-exit-{result.returncode}"
        text, usage = _parse_claude_json_reply(result.stdout)
        if usage:
            _LAST_CLAUDE_USAGE.clear()
            _LAST_CLAUDE_USAGE.update(usage)
        return text, None

    return run_in_isolated_dir(
        invoke,
        cwd=cwd,
        vault_root=vault_root,
        temporary_prefix=temporary_prefix,
        exec_error="claude-exec-error",
    )
