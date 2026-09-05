#!/usr/bin/env python3
"""Every external text entry crosses the same three privacy guards.

``component`` may carry an ``-input`` or ``-output`` suffix.  The suffix is
used in the warning slug while the base name selects the component's existing
health file.  Returned warning strings are the exact strings written to health;
callers may also surface them in their own result objects without rebuilding
telemetry vocabulary.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

from pathlib import Path

from beyin_ortak import write_health
import pii_guard
import retrieve
import secret_guard
import unicode_guard


HOOK_HEADER = retrieve.HOOK_HEADER

_HEALTH_NAMES = {
    "flush": "health.json",
    "ingest": "ingest-health.json",
    "kaydet": "kaydet-health.json",
    "pasaport": "pasaport-health.json",
}
_PHASES = ("input", "output")


def _hedef(component: str) -> tuple[str, str, str]:
    owner, separator, phase = component.rpartition("-")
    if not separator or phase not in _PHASES or not owner:
        owner = component
        phase = component
    return owner, phase, _HEALTH_NAMES.get(owner, "health.json")


def temizle(
    text: str,
    *,
    component: str,
    state_dir: Path,
) -> tuple[str, list[str]]:
    """Clean ``text`` in unicode -> secret -> PII order and report warnings."""
    owner, phase, health_name = _hedef(component)
    warnings: list[str] = []

    text, unicode_hits = unicode_guard.clean(text)
    if unicode_hits:
        warnings.append("warn:unicode-cleaned-" + phase + ":" + ",".join(unicode_hits))

    text, secret_hits = secret_guard.redact(text)
    if secret_hits:
        warnings.append("warn:secret-redacted-" + phase + ":" + ",".join(secret_hits))

    text, pii_hits = pii_guard.redact(text)
    if pii_hits:
        warnings.append("warn:pii-redacted-" + phase + ":" + ",".join(pii_hits))

    for warning in warnings:
        write_health(
            Path(state_dir),
            warning,
            warning=True,
            component=owner,
            health_name=health_name,
        )
    return text, warnings
