# yazan: codex
# model: gpt-5.6-sol
"""Pure, deterministic installation-plan decisions for ``kur.ps1``."""

from __future__ import annotations

import argparse
import base64
import json
import ntpath
import os
from typing import Any


RUNTIME_ORDER = ("ollama", "lm studio", "llama.cpp", "vllm")
DEFAULT_ENDPOINTS = {
    "ollama": "http://127.0.0.1:11434",
    "lm studio": "http://127.0.0.1:1234/v1",
    "llama.cpp": "http://127.0.0.1:8080/v1",
    "vllm": "http://127.0.0.1:8000/v1",
}


def _normalise_runtime_name(value: object) -> str:
    return (
        str(value or "")
        .strip()
        .lower()
        .replace("_", " ")
        .replace("-", " ")
    )


def _runtime_rank(runtime: dict[str, Any]) -> tuple[int, int, str]:
    detected_by = runtime.get("detected_by")
    state_rank = 0 if detected_by in {"port", "both"} else 1
    name = _normalise_runtime_name(runtime.get("name"))
    try:
        order = RUNTIME_ORDER.index(name)
    except ValueError:
        order = len(RUNTIME_ORDER)
    return state_rank, order, name


def detected_runtimes(probe: dict[str, Any]) -> list[dict[str, Any]]:
    """Return detected runtimes in running/installed preference order."""

    rows = probe.get("runtimes") or []
    if not isinstance(rows, list):
        return []
    found = [
        dict(row)
        for row in rows
        if isinstance(row, dict) and row.get("detected_by") is not None
    ]
    return sorted(found, key=_runtime_rank)


def _claude_detected(probe: dict[str, Any]) -> bool:
    commands = probe.get("commands")
    if isinstance(commands, dict) and isinstance(commands.get("claude"), bool):
        return commands["claude"]
    return bool(probe.get("claude"))


def _vault_default(user_profile: str, documents_path: str) -> tuple[str, str]:
    expected_documents = ntpath.join(user_profile, "Documents")
    redirected = ntpath.normcase(ntpath.normpath(documents_path)) != ntpath.normcase(
        ntpath.normpath(expected_documents)
    )
    if redirected:
        return (
            ntpath.join(user_profile, "brain"),
            "Documents is redirected (commonly by OneDrive), so the vault stays under the user profile.",
        )
    return (
        ntpath.join(documents_path, "brain"),
        "Documents is not redirected, so the vault uses the normal Documents location.",
    )


def _model_candidate(recommendations: list[dict[str, Any]]) -> dict[str, Any] | None:
    for candidate in recommendations:
        if isinstance(candidate, dict) and candidate.get("label") in {
            "fits-gpu",
            "cpu-ok",
        }:
            return candidate
    for candidate in recommendations:
        if isinstance(candidate, dict):
            return candidate
    return None


def _openai_url(runtime: dict[str, Any]) -> str:
    name = _normalise_runtime_name(runtime.get("name"))
    raw = str(runtime.get("endpoint") or DEFAULT_ENDPOINTS.get(name, "")).rstrip("/")
    if raw and not raw.lower().endswith("/v1"):
        raw += "/v1"
    return raw


def auto_decide(
    probe: dict[str, Any],
    recommendations: list[dict[str, Any]],
    *,
    user_profile: str,
    documents_path: str,
    mcp_config_exists: bool,
) -> dict[str, Any]:
    """Build the recommended plan without reading environment or filesystem state."""

    claude = _claude_detected(probe)
    runtimes = detected_runtimes(probe)
    runtime = runtimes[0] if runtimes else None
    vault, vault_reason = _vault_default(user_profile, documents_path)
    candidate = _model_candidate(recommendations)
    notes: list[str] = []
    todos: list[str] = []

    if claude and runtime:
        preset = "hybrid"
        backend = str(runtime.get("backend") or "ollama")
        preset_reason = "Claude Code and a local runtime were detected; hybrid keeps capture while using the local backend."
    elif claude:
        preset = "cloud"
        backend = "claude"
        preset_reason = "Claude Code was detected and no local runtime was found; cloud needs no local model setup."
    elif runtime:
        preset = "lite"
        backend = str(runtime.get("backend") or "ollama")
        preset_reason = "A local runtime was detected but Claude Code was not; lite works without hooks or nightly compile."
        notes.append("Lite has no automatic session capture and no nightly compile; use import plus MCP/clipboard retrieval.")
    else:
        preset = "cloud"
        backend = "claude"
        preset_reason = "Neither Claude Code nor a local runtime was detected; cloud is the safe target after Claude Code is installed."
        notes.append("Install Claude Code first before running the real installation.")
        notes.append("Choose Change to see the optional Ollama or LM Studio setup path.")

    backend_environment: dict[str, str] = {"BEYIN_VAULT": vault}
    model_reason = "No local model is needed for this plan."
    if backend != "none":
        backend_environment["BEYIN_MODEL_BACKEND"] = backend
    if backend == "ollama":
        tag = str((candidate or {}).get("tag") or "qwen3:4b")
        backend_environment["BEYIN_OLLAMA_MODEL_FAST"] = tag
        label = str((candidate or {}).get("label") or "fallback")
        model_reason = f"{tag} is the top available {label} catalogue candidate."
    elif backend == "openai-compat":
        assert runtime is not None
        backend_environment["BEYIN_OPENAI_URL"] = _openai_url(runtime)
        backend_environment["BEYIN_OPENAI_MODEL_FAST"] = ""
        model_reason = "OpenAI-compatible runtimes do not expose a safe offline model name."
        todos.append(
            "Set BEYIN_OPENAI_MODEL_FAST to the model identifier shown by the runtime before model-backed jobs run."
        )

    runtime_name = str(runtime.get("name")) if runtime else "none"
    runtime_reason = (
        f"{runtime_name} was preferred because it is "
        f"{'already running' if runtime and runtime.get('detected_by') in {'port', 'both'} else 'installed'}; "
        f"backend={backend}."
        if runtime
        else "No local runtime was detected."
    )
    reasons = {
        "preset": preset_reason,
        "vault": vault_reason,
        "backend": runtime_reason,
        "mcp": (
            "A Claude Desktop config exists, so MCP registration is enabled."
            if mcp_config_exists
            else "No Claude Desktop config exists, so MCP registration is disabled."
        ),
        "skills": "Only the two core operator skills are selected by default.",
        "model": model_reason,
    }
    plan = {
        "preset": preset,
        "vault": vault,
        "backend": backend,
        "backend_env": backend_environment,
        "mcp": bool(mcp_config_exists),
        "skills": ["beyin-doktor", "beyin-ice-aktar"],
        "force": False,
        "install_runtime": False,
        "pull_models": [],
    }
    return {
        "plan": plan,
        "reasons": reasons,
        "selected_runtime": runtime,
        "detected_runtimes": runtimes,
        "notes": notes,
        "todos": todos,
    }


build_plan = auto_decide


def decide_from_context(context: dict[str, Any]) -> dict[str, Any]:
    """JSON-friendly wrapper used by the PowerShell wizard."""

    recommendations = context.get("recommendations") or []
    if not isinstance(recommendations, list):
        raise ValueError("recommendations must be a JSON array")
    return auto_decide(
        context.get("probe") or {},
        recommendations,
        user_profile=str(context.get("user_profile") or ""),
        documents_path=str(context.get("documents_path") or ""),
        mcp_config_exists=bool(context.get("mcp_config_exists")),
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Build the recommended setup plan")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--context-json", help="captured decision inputs")
    source.add_argument("--context-base64", help="UTF-8 JSON encoded as base64")
    args = parser.parse_args(argv)
    payload = args.context_json
    if args.context_base64:
        payload = base64.b64decode(args.context_base64).decode("utf-8")
    context = json.loads(payload)
    print(json.dumps(decide_from_context(context), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(0 if os.environ.get("BEYIN_INVOKED_BY") else main())
