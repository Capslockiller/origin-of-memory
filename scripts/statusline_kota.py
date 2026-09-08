#!/usr/bin/env python3
"""Statusline köprüsü — Claude Code'un stdin JSON'undaki resmî rate_limits
alanını diske düşürür ve kısa bir durum satırı basar.

Master kararı 2026-08-29: "Claude içinde aynısını yap" — resmî yüzde yerelde
birikir. Alan belgesiz ve sürümler arası kırılgan (issue #45133/#40094):
yoksa sessizce yalnız model adı basılır, önbellek ellenmez.

2026-09-08 (Master, 60. oturum: "eski bilgiyi okumak hata"): kota.py bu
önbelleği ARTIK OKUMAZ. Kota yüzdeleri her çağrıda canlı okunur (Claude için
OAuth ucu, Codex için `codex app-server`); okunamazsa satır sayı basmaz.
Bu dosya yalnız statusline'ın kendi satırını basar ve `claude-kota.json`u
tanı/tarihçe kaydı olarak yazmayı sürdürür — gösterim zincirinde DEĞİLDİR.
Kaynak: Ham-Araştırma/2026-08-29-kota-okuma.md · 2026-09-08-kota-canli-okuma.md
"""
from __future__ import annotations

import datetime as dt
import json
import os
import sys
import tempfile

CACHE = r"E:\OdenaOS\.claude\scripts\.state\claude-kota.json"


def _atomic_write(path: str, payload: dict) -> None:
    dirname = os.path.dirname(path)
    os.makedirs(dirname, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=dirname, suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, ensure_ascii=False)
        os.replace(tmp, path)
    except OSError:
        try:
            os.unlink(tmp)
        except OSError:
            pass


def main() -> int:
    try:
        veri = json.load(sys.stdin)
    except Exception:
        print("beyin")
        return 0
    model = ((veri.get("model") or {}).get("display_name")) or "Claude"
    limits = veri.get("rate_limits")
    parcalar = [str(model)]
    if isinstance(limits, dict) and limits:
        _atomic_write(
            CACHE,
            {
                "rate_limits": limits,
                "yazilma": dt.datetime.now(dt.timezone.utc).isoformat(
                    timespec="seconds"
                ),
                "kaynak": "statusline",
            },
        )
        bes = limits.get("five_hour") or {}
        hafta = limits.get("seven_day") or {}
        if bes.get("used_percentage") is not None:
            parcalar.append(f"5s %{bes['used_percentage']:.0f}")
        if hafta.get("used_percentage") is not None:
            parcalar.append(f"hafta %{hafta['used_percentage']:.0f}")
    print(" | ".join(parcalar))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
