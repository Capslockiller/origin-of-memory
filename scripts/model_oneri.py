# yazan: codex
# model: gpt-5.6-sol
"""Hardware-fit recommendations for the verified Ollama model catalogue."""

from __future__ import annotations

import argparse
import json
import os
from typing import Any

try:
    from .donanim import collect_probe
except ImportError:  # direct ``python scripts/model_oneri.py`` execution
    from donanim import collect_probe


CATALOGUE: tuple[dict[str, Any], ...] = (
    {"tag": "qwen3:4b", "size_gb": 2.5, "role": "fast", "note": "Compact Qwen option."},
    {"tag": "qwen3:8b", "size_gb": 5.2, "role": "fast", "note": "Mid-size Qwen option."},
    {"tag": "qwen3:14b", "size_gb": 9.3, "role": "smart", "note": "Larger Qwen option."},
    {"tag": "qwen3:30b", "size_gb": 19.0, "role": "smart", "note": "Largest verified Qwen option."},
    {"tag": "gemma3:4b", "size_gb": 3.3, "role": "fast", "note": "Compact Gemma option."},
    {"tag": "gemma3:12b", "size_gb": 8.1, "role": "smart", "note": "Mid-size Gemma option."},
    {"tag": "gemma3:27b", "size_gb": 17.0, "role": "smart", "note": "Largest verified Gemma option."},
)


def model_need_gb(size_gb: float) -> float:
    return round(size_gb * 1.2 + 1.0, 2)


def pull_need_gb(size_gb: float) -> float:
    return round(size_gb * 1.5, 2)


def catalogue_entry(tag: str) -> dict[str, Any] | None:
    return next((dict(item) for item in CATALOGUE if item["tag"] == tag), None)


def _available_vram(probe: dict[str, Any]) -> float | None:
    values = [
        gpu.get("vram_gb")
        for gpu in probe.get("gpus", [])
        if isinstance(gpu.get("vram_gb"), (int, float))
    ]
    return max(values) if values else None


def _label(need: float, vram: float | None, ram: float | None) -> str:
    if vram is not None and need <= vram:
        return "fits-gpu"
    if vram is not None and need <= vram * 1.15:
        return "tight"
    if ram is not None and need <= ram * 0.6:
        return "cpu-ok"
    return "no-fit"


def _why(
    item: dict[str, Any], label: str, need: float, vram: float | None, ram: float | None
) -> str:
    prefix = f"{item['note']} Need estimate {need:.2f} GB."
    if label == "fits-gpu":
        return f"{prefix} Reported GPU VRAM is {vram:.2f} GB."
    if label == "tight":
        return f"{prefix} Reported GPU VRAM is {vram:.2f} GB; fit is tight."
    if label == "cpu-ok":
        return f"{prefix} Reported system RAM is {ram:.2f} GB; CPU use is expected."
    resources = []
    if vram is not None:
        resources.append(f"GPU VRAM is {vram:.2f} GB")
    if ram is not None:
        resources.append(f"system RAM is {ram:.2f} GB")
    detail = " and ".join(resources) if resources else "GPU VRAM and system RAM are unknown"
    return f"{prefix} No catalogue candidate fits because {detail}."


def recommend(probe: dict[str, Any]) -> list[dict[str, Any]]:
    """Return fit candidates best-first, or the smallest explanatory no-fit."""

    vram = _available_vram(probe)
    ram_value = probe.get("ram_gb")
    ram = float(ram_value) if isinstance(ram_value, (int, float)) else None
    candidates: list[dict[str, Any]] = []
    for source in CATALOGUE:
        need = model_need_gb(float(source["size_gb"]))
        label = _label(need, vram, ram)
        candidates.append(
            {
                "tag": source["tag"],
                "size_gb": source["size_gb"],
                "need_gb": need,
                "label": label,
                "role": source["role"],
                "why": _why(source, label, need, vram, ram),
            }
        )

    fitting = [candidate for candidate in candidates if candidate["label"] != "no-fit"]
    if not fitting:
        smallest = min(candidates, key=lambda candidate: (candidate["size_gb"], candidate["tag"]))
        return [smallest]
    rank = {"fits-gpu": 0, "tight": 1, "cpu-ok": 2}
    return sorted(
        fitting,
        key=lambda candidate: (
            rank[candidate["label"]],
            -candidate["size_gb"],
            candidate["tag"],
        ),
    )


def _format_table(candidates: list[dict[str, Any]]) -> str:
    headings = ("tag", "size_gb", "need_gb", "label", "role", "why")
    widths = {
        heading: max(len(heading), *(len(str(row[heading])) for row in candidates))
        for heading in headings[:-1]
    }
    header = "  ".join(f"{heading:<{widths[heading]}}" for heading in headings[:-1]) + "  why"
    lines = [header, "  ".join("-" * widths[heading] for heading in headings[:-1]) + "  ---"]
    for row in candidates:
        lines.append(
            "  ".join(f"{str(row[heading]):<{widths[heading]}}" for heading in headings[:-1])
            + f"  {row['why']}"
        )
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Recommend verified Ollama models")
    parser.add_argument("--json", action="store_true", help="emit JSON")
    parser.add_argument("--probe-json", help="use a captured probe JSON object")
    args = parser.parse_args(argv)
    probe = json.loads(args.probe_json) if args.probe_json else collect_probe()
    candidates = recommend(probe)
    if args.json:
        print(json.dumps(candidates, ensure_ascii=False, indent=2))
    else:
        print(_format_table(candidates))
    return 0


if __name__ == "__main__":
    raise SystemExit(0 if os.environ.get("BEYIN_INVOKED_BY") else main())
