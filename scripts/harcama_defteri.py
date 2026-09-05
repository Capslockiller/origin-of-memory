#!/usr/bin/env python3
"""Harcama defteri — jeton harcamasını arka planda biriktirir.

Master kararı 2026-08-29: "harcanan tokenları da genel olarak kaydet; görev
başına ne kadar harcanıyor, sohbet bazında ne harcamışız — süreç arkada
biriktirsin." Kaynaklar yerel ve kesindir (ccusage deseni):
  - Claude: ~/.claude/projects/**/*.jsonl usage blokları → sohbet (oturum) bazı
  - Codex:  ~/.codex/sessions/**/rollout-*.jsonl token_count kümülatifleri → şerit/görev bazı
Filigran: dosya (boyut, mtime) değişmediyse yeniden okunmaz. Defter atomik
yazılır; hiçbir şey silinmez, yalnız üzerine biriktirilir.

Kullanım:  python harcama_defteri.py --topla       # artımlı biriktir (kanca bunu çağırır)
           python harcama_defteri.py --ozet        # gün + en pahalı oturumlar
           python harcama_defteri.py --ozet --gun 7
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import tempfile
from pathlib import Path

CLAUDE_PROJECTS = Path.home() / ".claude" / "projects"
CODEX_SESSIONS = Path.home() / ".codex" / "sessions"
DEFTER = Path(r"E:\OdenaOS\.claude\scripts\.state\harcama-defteri.json")


def _yukle() -> dict:
    try:
        return json.loads(DEFTER.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {"surum": 1, "filigran": {}, "oturumlar": {}, "gunluk": {}}


def _atomik_yaz(veri: dict) -> None:
    DEFTER.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=str(DEFTER.parent), suffix=".tmp")
    with os.fdopen(fd, "w", encoding="utf-8") as handle:
        json.dump(veri, handle, ensure_ascii=False)
    os.replace(tmp, DEFTER)


def _gun(ts: str | None) -> str:
    return (ts or "")[:10] or "?"


def _claude_dosya_ozeti(dosya: Path) -> dict | None:
    """Tek sohbet dosyasının model kırılımlı toplamı."""
    modeller: dict[str, dict] = {}
    ilk = son = None
    try:
        with dosya.open(encoding="utf-8", errors="replace") as h:
            for satir in h:
                if '"usage"' not in satir:
                    continue
                try:
                    veri = json.loads(satir)
                except json.JSONDecodeError:
                    continue
                mesaj = veri.get("message") or {}
                kullanim = mesaj.get("usage") or veri.get("usage")
                if not isinstance(kullanim, dict):
                    continue
                ts = veri.get("timestamp")
                if isinstance(ts, str):
                    ilk = ilk or ts
                    son = ts
                model = str(mesaj.get("model") or veri.get("model") or "?")
                hane = modeller.setdefault(
                    model,
                    {"istek": 0, "girdi": 0, "cikti": 0, "cache_okuma": 0, "cache_yazma": 0},
                )
                hane["istek"] += 1
                hane["girdi"] += int(kullanim.get("input_tokens") or 0)
                hane["cikti"] += int(kullanim.get("output_tokens") or 0)
                hane["cache_okuma"] += int(kullanim.get("cache_read_input_tokens") or 0)
                hane["cache_yazma"] += int(kullanim.get("cache_creation_input_tokens") or 0)
    except OSError:
        return None
    if not modeller:
        return None
    return {"kaynak": "claude", "ilk": ilk, "son": son, "modeller": modeller}


def _codex_dosya_ozeti(dosya: Path) -> dict | None:
    """Rollout'un SON kümülatif token_count toplamı (görev/şerit bazı)."""
    son_toplam, ilk = None, None
    try:
        with dosya.open(encoding="utf-8", errors="replace") as h:
            for satir in h:
                if '"token_count"' not in satir:
                    continue
                try:
                    veri = json.loads(satir)
                except json.JSONDecodeError:
                    continue
                ilk = ilk or veri.get("timestamp")
                p = veri.get("payload") or {}
                toplam = (p.get("info") or {}).get("total_token_usage") or p.get(
                    "total_token_usage"
                )
                if isinstance(toplam, dict):
                    son_toplam = {"ts": veri.get("timestamp"), **toplam}
    except OSError:
        return None
    if not son_toplam:
        return None
    return {
        "kaynak": "codex",
        "ilk": ilk,
        "son": son_toplam.pop("ts", None),
        "modeller": {"codex": {
            "girdi": int(son_toplam.get("input_tokens") or 0),
            "cikti": int(son_toplam.get("output_tokens") or 0),
            "cache_okuma": int(son_toplam.get("cached_input_tokens") or 0),
            "toplam": int(son_toplam.get("total_tokens") or 0),
        }},
    }


def topla() -> tuple[int, int]:
    defter = _yukle()
    filigran = defter["filigran"]
    yeni = atlanan = 0
    kaynaklar = []
    if CLAUDE_PROJECTS.exists():
        kaynaklar += [(p, _claude_dosya_ozeti) for p in CLAUDE_PROJECTS.rglob("*.jsonl")]
    if CODEX_SESSIONS.exists():
        kaynaklar += [(p, _codex_dosya_ozeti) for p in CODEX_SESSIONS.rglob("rollout-*.jsonl")]
    for dosya, okuyucu in kaynaklar:
        try:
            st = dosya.stat()
        except OSError:
            continue
        anahtar = str(dosya)
        imza = [st.st_size, int(st.st_mtime)]
        if filigran.get(anahtar) == imza:
            atlanan += 1
            continue
        ozet = okuyucu(dosya)
        if ozet is None:
            filigran[anahtar] = imza
            continue
        kimlik = dosya.stem
        defter["oturumlar"][kimlik] = {
            "dosya": anahtar,
            "proje": dosya.parent.name,
            **ozet,
        }
        filigran[anahtar] = imza
        yeni += 1
    # günlük kırılımı oturumlardan yeniden türet (tek doğruluk kaynağı: oturumlar)
    gunluk: dict[str, dict] = {}
    for kayit in defter["oturumlar"].values():
        gun = _gun(kayit.get("son") or kayit.get("ilk"))
        for model, v in kayit.get("modeller", {}).items():
            hane = gunluk.setdefault(gun, {}).setdefault(
                model, {"istek": 0, "girdi": 0, "cikti": 0, "cache_okuma": 0, "cache_yazma": 0}
            )
            hane["istek"] += int(v.get("istek") or 0)
            hane["girdi"] += int(v.get("girdi") or 0)
            hane["cikti"] += int(v.get("cikti") or 0)
            # 48. oturum (2026-09-02): girdi maliyetinin asıl kütlesi önbellek okuması —
            # günlük toplamda düşürülüyordu, artık taşınıyor (A15).
            hane["cache_okuma"] += int(v.get("cache_okuma") or 0)
            hane["cache_yazma"] += int(v.get("cache_yazma") or 0)
    defter["gunluk"] = gunluk
    defter["son_toplama"] = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")
    _atomik_yaz(defter)
    return yeni, atlanan


def ozet(gun_sayisi: int) -> None:
    defter = _yukle()
    bugun = dt.date.today()
    gunler = [(bugun - dt.timedelta(days=i)).isoformat() for i in range(gun_sayisi)]
    print(f"Harcama defteri — son {gun_sayisi} gün (kayıt: {len(defter['oturumlar'])} oturum)")
    for gun in gunler:
        veriler = defter.get("gunluk", {}).get(gun)
        if not veriler:
            continue
        satir = " · ".join(
            f"{m.split('-')[1] if '-' in m else m}: {v['cikti']//1000}k"
            for m, v in sorted(veriler.items())
        )
        onb = sum(int(v.get("cache_okuma") or 0) for v in veriler.values())
        onb_y = sum(int(v.get("cache_yazma") or 0) for v in veriler.values())
        print(f"  {gun}: {satir} · önbellek okuma {onb//1000}k / yazma {onb_y//1000}k")
    # en pahalı 5 oturum (çıktı jetonuna göre)
    def maliyet(k):
        return sum(int(v.get("cikti") or 0) for v in k.get("modeller", {}).values())
    pahali = sorted(defter["oturumlar"].values(), key=maliyet, reverse=True)[:5]
    print("  En pahalı 5 oturum/görev:")
    for k in pahali:
        print(
            f"    {_gun(k.get('son'))} · {k.get('kaynak')} · {k.get('proje','?')[:40]}"
            f" → {maliyet(k)//1000}k çıktı-jetonu"
        )


def main() -> int:
    import sys
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--topla", action="store_true")
    parser.add_argument("--ozet", action="store_true")
    parser.add_argument("--gun", type=int, default=7)
    args = parser.parse_args()
    if args.topla:
        yeni, atlanan = topla()
        print(f"defter: {yeni} dosya işlendi, {atlanan} değişmemiş atlandı")
    if args.ozet:
        ozet(args.gun)
    if not (args.topla or args.ozet):
        ozet(args.gun)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
