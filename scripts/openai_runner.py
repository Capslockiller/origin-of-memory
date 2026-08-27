#!/usr/bin/env python3
"""OpenAI-compatible backend for text-mode background model calls."""

# yazan: codex · model: gpt-5.6-sol

from __future__ import annotations

import json
import os
from pathlib import Path
import socket
from typing import Any
import urllib.error
import urllib.request


URL_ENV = "BEYIN_OPENAI_URL"
KEY_ENV = "BEYIN_OPENAI_KEY"
FAST_MODEL_ENV = "BEYIN_OPENAI_MODEL_FAST"
SMART_MODEL_ENV = "BEYIN_OPENAI_MODEL_SMART"


def resolve_model(
    model: str,
    environment: dict[str, str] | None = None,
) -> tuple[str | None, str | None]:
    """Map the caller's model tier to an explicitly configured model slug."""
    env = os.environ if environment is None else environment
    if model == "haiku":
        fast = (env.get(FAST_MODEL_ENV) or "").strip()
        if not fast:
            return None, "openai-compat-model-unset"
        return fast, None
    if model == "sonnet":
        smart = (env.get(SMART_MODEL_ENV) or "").strip()
        if smart:
            return smart, None
        fast = (env.get(FAST_MODEL_ENV) or "").strip()
        if not fast:
            return None, "openai-compat-model-unset"
        return fast, "warn:openai-compat-model-unmapped:sonnet"
    return None, "openai-compat-model-unset"


def _endpoint(
    environment: dict[str, str] | None = None,
) -> tuple[str | None, str | None]:
    env = os.environ if environment is None else environment
    base = (env.get(URL_ENV) or "").strip()
    if not base:
        return None, "openai-compat-url-unset"
    return base.rstrip("/") + "/chat/completions", None


def _decode_response(response: Any) -> str:
    try:
        payload = json.loads(response.read().decode("utf-8"))
        choices = payload.get("choices") if isinstance(payload, dict) else None
        first = choices[0] if isinstance(choices, list) and choices else None
        message = first.get("message") if isinstance(first, dict) else None
        text = message.get("content") if isinstance(message, dict) else None
    except (AttributeError, IndexError, UnicodeError, json.JSONDecodeError):
        raise ValueError("openai-compat-bad-response") from None
    if not isinstance(text, str):
        raise ValueError("openai-compat-bad-response")
    return text.strip()


def run_openai(
    prompt: str,
    *,
    model: str,
    timeout: int,
    cwd: Path | None = None,
    vault_root: Path | None = None,
    temporary_prefix: str = "beyin-openai-compat-",
    warnings: list[str] | None = None,
) -> tuple[str | None, str | None]:
    """Run one non-streaming OpenAI-compatible chat completion request."""
    del cwd, vault_root, temporary_prefix  # HTTP calls do not touch the filesystem.
    sink = warnings if warnings is not None else []
    slug, model_result = resolve_model(model)
    if slug is None:
        return None, model_result
    if model_result:
        sink.append(model_result)

    endpoint, endpoint_error = _endpoint()
    if endpoint is None:
        return None, endpoint_error

    body = json.dumps(
        {
            "model": slug,
            "messages": [{"role": "user", "content": prompt}],
            "stream": False,
        },
        ensure_ascii=False,
    ).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    key = (os.environ.get(KEY_ENV) or "").strip()
    if key:
        headers["Authorization"] = f"Bearer {key}"
    request = urllib.request.Request(
        endpoint,
        data=body,
        headers=headers,
        method="POST",
    )

    marker_existed = "BEYIN_INVOKED_BY" in os.environ
    previous_marker = os.environ.get("BEYIN_INVOKED_BY")
    os.environ["BEYIN_INVOKED_BY"] = "beyin-scripts"
    try:
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return _decode_response(response), None
        except urllib.error.HTTPError as exc:
            return None, f"openai-compat-http-{exc.code}"
        except urllib.error.URLError as exc:
            if isinstance(exc.reason, (TimeoutError, socket.timeout)):
                return None, "openai-compat-timeout"
            return None, "openai-compat-missing"
        except (TimeoutError, socket.timeout):
            return None, "openai-compat-timeout"
        except ConnectionRefusedError:
            return None, "openai-compat-missing"
        except ValueError as exc:
            if str(exc) == "openai-compat-bad-response":
                return None, "openai-compat-bad-response"
            raise
    finally:
        if marker_existed:
            assert previous_marker is not None
            os.environ["BEYIN_INVOKED_BY"] = previous_marker
        else:
            os.environ.pop("BEYIN_INVOKED_BY", None)
