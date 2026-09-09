#!/usr/bin/env python3
"""Kabul kapisi 7 hukmu: Windows Sandbox kosumunun kanit klasorunu okur.

`zincir.cmd` her adimi `[ADIM n] ok|hata|atlandi <kanit>` satiriyla gunluge yazar;
bu arac o satirlari okur ama onlara guvenmez: her adimin kanitini `.out\\` icindeki
dosyalardan bagimsiz olarak yeniden olcer, iki okuma ayrisirsa bunu yazar.

Kullanim:
    python bench/vm/dogrula.py [--out bench/vm/.out] [--results bench/results]

Yalniz Python 3 standart kutuphanesi. Hicbir sey yazmaz, yalniz sonuc JSON'unu uretir.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import sqlite3
import sys
from pathlib import Path

ADIM_SATIRI = re.compile(r"^\[ADIM (\d)\]\s+(ok|hata|atlandi)\s*(.*)$")
BLOK = "### Oturum"
HOOK_OLAYLARI = ("SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact")

BASLIKLAR = {
    1: "oom install + oom doctor",
    2: "gercek oturum (hooklar)",
    3: "8 saat beklemeden sweep -> daily blogu",
    4: "OOM_FAKE_NOW ile compile -> concept/root map/index",
    5: "yeni oturumda enjeksiyon",
    6: "doctor: kapsama 100% / ret 0%",
    7: "claude yokken yerel model tek basina flush",
}


# --------------------------------------------------------------------------- yardimcilar
def oku(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""


def json_oku(path: Path):
    metin = oku(path).strip()
    if not metin:
        return None
    try:
        return json.loads(metin)
    except json.JSONDecodeError:
        return None


def gunluk_adimlari(out: Path) -> dict[int, tuple[str, str]]:
    kayit: dict[int, tuple[str, str]] = {}
    for satir in oku(out / "zincir.log").splitlines():
        eslesme = ADIM_SATIRI.match(satir.strip())
        if eslesme:
            kayit[int(eslesme.group(1))] = (eslesme.group(2), eslesme.group(3).strip())
    return kayit


def kuru_kosum(out: Path) -> bool:
    return "kuru=1" in oku(out / "zincir.log")


def daily_dosyalari(out: Path) -> list[Path]:
    return sorted((out / "daily").glob("*.md"))


def blok_sayisi(out: Path) -> int:
    return sum(oku(p).count(BLOK) for p in daily_dosyalari(out))


def kavramlar(out: Path) -> list[Path]:
    return sorted((out / "knowledge" / "concepts").glob("*.md"))


def state_sorgu(db: Path, sql: str) -> list[tuple]:
    if not db.exists():
        return []
    try:
        baglanti = sqlite3.connect(f"file:{db.as_posix()}?mode=ro", uri=True)
    except sqlite3.Error:
        return []
    try:
        return list(baglanti.execute(sql))
    except sqlite3.Error:
        return []
    finally:
        baglanti.close()


# --------------------------------------------------------------------------- adim olculeri
def adim1(out: Path, kuru: bool) -> tuple[bool | None, str]:
    doctor = json_oku(out / "doctor-1.json")
    ayar = json_oku(out / "settings.json")
    state = out / "state.db"
    if doctor is None:
        return False, "doctor-1.json okunamadi"
    if kuru:
        return None, (
            "kuru kosum: install --dry-run; doctor-1.json semasi gecerli "
            f"(kapsama={doctor.get('coverage')}), hook/gorev/state.db denetimi Sandbox'a birakildi"
        )
    kanca = 0
    if isinstance(ayar, dict):
        hooks = ayar.get("hooks") or {}
        for olay in HOOK_OLAYLARI:
            gruplar = hooks.get(olay) or []
            for grup in gruplar:
                for giris in (grup or {}).get("hooks", []):
                    if "oom.exe" in str(giris.get("command", "")):
                        kanca += 1
    tamam = kanca == 4 and state.exists()
    return tamam, f"settings.json oom hook sayisi={kanca}/4 · state.db={'var' if state.exists() else 'yok'}"


def adim2(out: Path, kuru: bool) -> tuple[bool | None, str]:
    dosya = out / "adim2-claude.txt"
    if kuru:
        return None, "kuru kosum: gercek claude -p oturumu host'ta kosulmaz (BRIEF)"
    boy = dosya.stat().st_size if dosya.exists() else 0
    return boy > 0, f"claude -p ciktisi={boy} bayt"


def adim3(out: Path, gunluk: dict[int, tuple[str, str]]) -> tuple[bool | None, str]:
    kayitli = gunluk.get(3, ("", ""))[1]
    eslesme = re.search(r"blok=(\d+)", kayitli)
    blok3 = int(eslesme.group(1)) if eslesme else -1
    dosyalar = daily_dosyalari(out)
    toplam = blok_sayisi(out)
    ad = ", ".join(p.name for p in dosyalar) or "yok"
    return blok3 == 1, f"ADIM 3 sonunda blok={blok3} (beklenen 1) · daily={ad} · kosum sonu toplam blok={toplam}"


def adim4(out: Path) -> tuple[bool | None, str]:
    kav = kavramlar(out)
    bilgi = out / "knowledge"
    var = {ad: (bilgi / ad).exists() for ad in ("index.md", "index-full.md", "log.md")}
    tamam = len(kav) >= 1 and all(var.values())
    yok = [ad for ad, v in var.items() if not v] or ["-"]
    return tamam, f"kavram={len(kav)} · index/index-full/log eksik={','.join(yok)}"


def adim5(out: Path, kuru: bool) -> tuple[bool | None, str]:
    veri = json_oku(out / "adim5-retrieve.json")
    blok = ""
    if isinstance(veri, dict):
        blok = str((veri.get("hookSpecificOutput") or {}).get("additionalContext", ""))
    adlar = [p.stem for p in kavramlar(out)]
    anilan = [ad for ad in adlar if ad.lower() in blok.lower()]
    if not anilan and blok:
        # Dosya adi degil, baslik gecmis olabilir: kavram basliklarini da dene.
        for p in kavramlar(out):
            ilk = oku(p).splitlines()[:1]
            baslik = ilk[0].lstrip("# ").strip() if ilk else ""
            if baslik and baslik.lower() in blok.lower():
                anilan.append(p.stem)
    tamam = bool(blok) and bool(anilan)
    ek = " · gercek claude -p atlandi (kuru kosum)" if kuru else ""
    return tamam, f"enjeksiyon blogu={len(blok)} karakter · anilan kavram={','.join(anilan) or 'yok'}{ek}"


def adim6(out: Path) -> tuple[bool | None, str]:
    doctor = json_oku(out / "doctor-6.json")
    if not isinstance(doctor, dict):
        return False, "doctor-6.json okunamadi"
    kapsama = float(doctor.get("coverage", 0.0))
    ret = float(doctor.get("rejection_rate", 1.0))
    return (kapsama >= 1.0 and ret <= 0.0), f"kapsama={kapsama:.0%} ret={ret:.0%} bekleyen={doctor.get('pending')}"


def adim7(out: Path, gunluk: dict[int, tuple[str, str]]) -> tuple[bool | None, str]:
    db = out / "state.db"
    cagrilar = state_sorgu(db, "SELECT backend, component, COUNT(*) FROM calls GROUP BY backend, component")
    yerel = [satir for satir in cagrilar if str(satir[0]).lower() == "local"]
    akis = state_sorgu(db, "SELECT outcome, COUNT(*) FROM flush_log GROUP BY outcome")
    toplam = blok_sayisi(out)
    kayitli = gunluk.get(7, ("", ""))[1]
    eslesme = re.search(r"blok sayisi (\d+)", kayitli)
    blok7 = int(eslesme.group(1)) if eslesme else toplam
    ozet = ", ".join(f"{b}/{c}={n}" for b, c, n in cagrilar) or "calls tablosu bos"
    tamam = bool(yerel) and blok7 >= 2
    return tamam, (
        f"calls backend/bilesen: {ozet} · yerel satir={len(yerel)} · "
        f"flush_log={dict((o, n) for o, n in akis)} · daily blok={blok7}"
    )


# --------------------------------------------------------------------------- rapor
def durum_metni(deger: bool | None) -> str:
    return {True: "ok", False: "hata", None: "atlandi"}[deger]


def main(argv: list[str]) -> int:
    kok = Path(__file__).resolve().parents[2]
    ayristirici = argparse.ArgumentParser(description="Kabul kapisi 7 hukmu")
    ayristirici.add_argument("--out", default=str(kok / "bench" / "vm" / ".out"))
    ayristirici.add_argument("--results", default=str(kok / "bench" / "results"))
    ayristirici.add_argument("--tarih", default=dt.date.today().isoformat())
    secenek = ayristirici.parse_args(argv)

    out = Path(secenek.out)
    if not out.exists():
        print(f"kanit klasoru yok: {out}", file=sys.stderr)
        return 2

    kuru = kuru_kosum(out)
    gunluk = gunluk_adimlari(out)
    olculer = {
        1: adim1(out, kuru),
        2: adim2(out, kuru),
        3: adim3(out, gunluk),
        4: adim4(out),
        5: adim5(out, kuru),
        6: adim6(out),
        7: adim7(out, gunluk),
    }

    baslik = "KAPI 7 — TEMIZ WINDOWS ZINCIRI" + (" (HOST KURU KOSUMU)" if kuru else " (SANDBOX)")
    print(baslik)
    print(f"kanit: {out}")
    print()
    print(f"{'adim':<5} {'gunluk':<9} {'olcum':<9} {'ne':<44} kanit")
    print("-" * 150)
    adimlar = []
    for n in range(1, 8):
        olcum, kanit = olculer[n]
        kayitli = gunluk.get(n, ("kayit-yok", ""))[0]
        print(f"{n:<5} {kayitli:<9} {durum_metni(olcum):<9} {BASLIKLAR[n][:44]:<44} {kanit}")
        adimlar.append({
            "adim": n,
            "ne": BASLIKLAR[n],
            "gunluk_durumu": kayitli,
            "olcum_durumu": durum_metni(olcum),
            "kanit": kanit,
            "gunluk_kaniti": gunluk.get(n, ("", ""))[1],
        })

    hatalar = [a for a in adimlar if a["olcum_durumu"] == "hata"]
    atlanan = [a for a in adimlar if a["olcum_durumu"] == "atlandi"]
    if kuru:
        hukum = "kuru-kosum"
        cumle = ("HUKUM: kuru kosum — kapi 7 GECILMEDI sayilir. Bu tablo yalnizca zincir "
                 "araclarinin host uzerinde calistigini gosterir; kapi ancak Sandbox kosumuyla kapanir.")
    elif hatalar:
        hukum = "hata"
        cumle = f"HUKUM: kapi 7 GECILMEDI — hatali adim: {', '.join(str(a['adim']) for a in hatalar)}"
    elif atlanan:
        hukum = "eksik"
        cumle = f"HUKUM: kapi 7 EKSIK — atlanan adim: {', '.join(str(a['adim']) for a in atlanan)}"
    else:
        hukum = "gecti"
        cumle = "HUKUM: kapi 7 GECTI — yedi adim da kaniti ile yesil."
    print("-" * 150)
    print(cumle)

    sonuc = {
        "schema_version": 1,
        "kapi": 7,
        "tarih": secenek.tarih,
        "mod": "kuru-kosum" if kuru else "sandbox",
        "hukum": hukum,
        "kanit_klasoru": str(out),
        "adimlar": adimlar,
    }
    hedef = Path(secenek.results) / f"vm-{secenek.tarih}.json"
    hedef.parent.mkdir(parents=True, exist_ok=True)
    hedef.write_text(json.dumps(sonuc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"yazildi: {hedef}")
    return 0 if hukum == "gecti" else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
