#!/usr/bin/env python3
"""Local Ollama backend for text-mode background model calls."""

# yazan: codex · model: gpt-5.6-sol

from __future__ import annotations

import json
import os
from pathlib import Path
import socket
from typing import Any
import urllib.error
import urllib.request


URL_ENV = "BEYIN_OLLAMA_URL"
FAST_MODEL_ENV = "BEYIN_OLLAMA_MODEL_FAST"
SMART_MODEL_ENV = "BEYIN_OLLAMA_MODEL_SMART"
THINK_ENV = "BEYIN_OLLAMA_THINK"
NUM_PREDICT_ENV = "BEYIN_OLLAMA_NUM_PREDICT"
NUM_CTX_ENV = "BEYIN_OLLAMA_NUM_CTX"
KEEP_ALIVE_ENV = "BEYIN_OLLAMA_KEEP_ALIVE"
DEFAULT_URL = "http://localhost:11434"


def _request_extras(
    environment: dict[str, str] | None = None,
    *,
    mesgul: bool | None = None,
) -> dict[str, Any]:
    """Fields beyond model/prompt/stream.

    Thinking is OFF unless ``BEYIN_OLLAMA_THINK=1``: on thinking models
    (measured with qwen3:8b) the think stream otherwise eats the budget and
    the answer arrives truncated mid-block. ``num_predict`` defaults to -1
    (unlimited) for the same reason; override via ``BEYIN_OLLAMA_NUM_PREDICT``.

    ``mesgul`` is the A7 politeness gate's last decision (see ``nezaket.py``).
    While busy the request asks Ollama to drop the model right after
    answering (``keep_alive: 0``) instead of holding VRAM. Unknown or not
    busy falls back to ``BEYIN_OLLAMA_KEEP_ALIVE`` when set, and otherwise
    omits the field — byte-identical to before this existed.
    """
    env = os.environ if environment is None else environment
    extras: dict[str, Any] = {"think": (env.get(THINK_ENV) or "").strip() == "1"}
    raw = (env.get(NUM_PREDICT_ENV) or "").strip()
    try:
        num_predict = int(raw) if raw else -1
    except ValueError:
        num_predict = -1
    extras["options"] = {"num_predict": num_predict}
    # num_ctx yalnız BEYIN_OLLAMA_NUM_CTX verilmişse gönderilir; verilmezse
    # istek bugünkü davranışla birebir aynı kalır (sunucu varsayılanı geçerli).
    # Neden: Ollama pencereyi aşan girdiyi SESSİZCE kırpar — uzun transkriptte
    # şema talimatı da kırpılıp summary-schema-invalid üretiyordu (44. oturum).
    raw_ctx = (env.get(NUM_CTX_ENV) or "").strip()
    if raw_ctx:
        try:
            num_ctx = int(raw_ctx)
        except ValueError:
            num_ctx = 0
        if num_ctx > 0:
            extras["options"]["num_ctx"] = num_ctx
    if mesgul:
        extras["keep_alive"] = 0
    else:
        raw_keep_alive = (env.get(KEEP_ALIVE_ENV) or "").strip()
        if raw_keep_alive:
            extras["keep_alive"] = raw_keep_alive
    return extras


def resolve_model(
    model: str,
    environment: dict[str, str] | None = None,
) -> tuple[str | None, str | None]:
    """Map the caller's model tier to an explicitly configured Ollama slug."""
    env = os.environ if environment is None else environment
    if model == "haiku":
        fast = (env.get(FAST_MODEL_ENV) or "").strip()
        if not fast:
            return None, "ollama-model-unset"
        return fast, None
    if model == "sonnet":
        smart = (env.get(SMART_MODEL_ENV) or "").strip()
        if smart:
            return smart, None
        fast = (env.get(FAST_MODEL_ENV) or "").strip()
        if not fast:
            return None, "ollama-model-unset"
        return fast, "warn:ollama-model-unmapped:sonnet"
    return None, "ollama-model-unset"


def _endpoint(environment: dict[str, str] | None = None) -> str:
    env = os.environ if environment is None else environment
    base = (env.get(URL_ENV) or "").strip() or DEFAULT_URL
    return base.rstrip("/") + "/api/generate"


def _decode_response(response: Any) -> str:
    try:
        payload = json.loads(response.read().decode("utf-8"))
    except (AttributeError, UnicodeError, json.JSONDecodeError):
        raise ValueError("ollama-bad-response") from None
    text = payload.get("response") if isinstance(payload, dict) else None
    if not isinstance(text, str):
        raise ValueError("ollama-bad-response")
    return text.strip()


def run_ollama(
    prompt: str,
    *,
    model: str,
    timeout: int,
    cwd: Path | None = None,
    vault_root: Path | None = None,
    state_dir: Path | None = None,
    temporary_prefix: str = "beyin-ollama-",
    warnings: list[str] | None = None,
) -> tuple[str | None, str | None]:
    """Run one non-streaming Ollama generation request."""
    del cwd, vault_root, temporary_prefix  # HTTP calls do not touch the filesystem.
    sink = warnings if warnings is not None else []
    slug, model_result = resolve_model(model)
    if slug is None:
        return None, model_result
    if model_result:
        sink.append(model_result)

    mesgul: bool | None = None
    if state_dir is not None:
        try:
            import nezaket

            mesgul = nezaket.son_karar(state_dir)
        except Exception:
            mesgul = None

    body = json.dumps(
        {
            "model": slug,
            "prompt": prompt,
            "stream": False,
            **_request_extras(mesgul=mesgul),
        },
        ensure_ascii=False,
    ).encode("utf-8")
    request = urllib.request.Request(
        _endpoint(),
        data=body,
        headers={"Content-Type": "application/json"},
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
            return None, f"ollama-http-{exc.code}"
        except urllib.error.URLError as exc:
            if isinstance(exc.reason, (TimeoutError, socket.timeout)):
                return None, "ollama-timeout"
            return None, "ollama-missing"
        except (TimeoutError, socket.timeout):
            return None, "ollama-timeout"
        except ConnectionRefusedError:
            return None, "ollama-missing"
        except ValueError as exc:
            if str(exc) == "ollama-bad-response":
                return None, "ollama-bad-response"
            raise
    finally:
        if marker_existed:
            assert previous_marker is not None
            os.environ["BEYIN_INVOKED_BY"] = previous_marker
        else:
            os.environ.pop("BEYIN_INVOKED_BY", None)
