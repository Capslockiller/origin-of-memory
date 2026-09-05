#!/usr/bin/env python3
"""Kota hız katmanı — ileriye bakan R ölçüsü (Master kararı 2026-09-05, 55. oturum).

Eski ölçü "used ÷ elapsed" geriye bakıyordu ve iki yanlış sinyal üretiyordu:
%87'de "kıs" (oysa pencere 8 saat sonra kapanıyordu) ve sıfırlanmış bütçede
"boş dur". Yeni ölçü her pencere için ileriye bakar:

    kalan%         = 100 − used%
    kalan_saat     = (resets_at − şimdi) / 3600
    sürdürülebilir = kalan% / kalan_saat        # bu hızla tam reset'te bitersin
    yanma          = Δused% / Δsaat             # pencerenin son 1/12'inin gerçek hızı (örneklemden)
    R              = yanma / sürdürülebilir     # <1 yeter · >1 reset'ten önce biter
    tükenme        = şimdi + kalan% / yanma     # somut an: "03:40'ta biter"

Bant R'den seçilir. Örnek yoksa (pencere taze) eski used÷elapsed yalnız YEDEK
olarak, `tahmin=True` etiketiyle kullanılır. Yönetici bant = pencerelerin en
yüksek R'si. İki ek sinyal: kalan% < KAPALI_KALAN → her hâlükârda kapalı;
kalan_saat < HARCA_PAY×pencere ve kalan% > HARCA_KALAN → HARCA (bütçe yanacak);
kapalı/karne bantla birlikte HARCA verilmez (çelişir).

Kaynaklar: Ham-Araştırma/2026-09-05-kota-kurali-claude.md, -codex.md,
-bant-esikleri.md. Pencereler iki tarafta da KAYAN; yüzdeler sunucudan gelir,
burada yalnız hızı ölçülür. Örneklem: .state/kota-orneklem.jsonl — her okuma
bir satır ekler; aynı pencere = aynı resets_at (değişince yeni pencere sayılır,
eski örnekler yok sayılır).
"""
from __future__ import annotations

import datetime as dt
import json
import os
from pathlib import Path
from typing import Iterable

ORNEKLEM_YOLU = Path(r"E:\OdenaOS\.claude\scripts\.state\kota-orneklem.jsonl")
ORNEKLEM_TAVAN = 4000        # satır; üstüne çıkınca eski yarısı düşer
UFUK_PAY = 1.0 / 12.0        # yanma hızı pencerenin bu payında ölçülür (SRE 1/12 kuralı: 5s→25dk · hafta→14s)
UFUK_SAAT = 2.0              # yalnız doğrudan yanma_hizi() çağrıları için varsayılan
EN_AZ_ARALIK_DK = 15         # iki örnek en az bu kadar ayrıksa hız hesaplanır
YEDEK_ESIK = 0.10            # pencerenin bu payı geçmeden yedek (used÷elapsed) kullanılmaz

# Bant eşikleri (R) — Master kararı 2026-09-05 (Set C, Ham-Araştırma/2026-09-05-kota-bant-esikleri.md).
BANT_ESIK = (0.9, 1.3, 2.0)  # serbest ≤ e0 · dikkat ≤ e1 · karne ≤ e2 · kapalı > e2
BANT_AD = ("serbest", "dikkat", "karne", "kapalı")
KAPALI_KALAN = 10.0          # kalan% bunun altındaysa bant kapalı, R ne olursa olsun
HARCA_PAY = 12.0 / 168.0     # kalan süre pencerenin bu payının altında (hafta: 12 s · 5 s: 21 dk)
HARCA_KALAN = 50.0           # ve kalan% bunun üstündeyse → HARCA (yalnız serbest/dikkat bantta)


def _simdi() -> float:
    return dt.datetime.now(dt.timezone.utc).timestamp()


# ---------------------------------------------------------------------------
# Örneklem defteri
# ---------------------------------------------------------------------------

def ornek_yaz(pencereler: Iterable[dict], yol: Path = ORNEKLEM_YOLU, simdi: float | None = None) -> int:
    """Her pencere için {ts,id,used,resets_at} satırı ekler. Hata yutulur (kota kritik değil)."""
    simdi = simdi if simdi is not None else _simdi()
    satirlar = []
    for p in pencereler:
        if p.get("used") is None or not p.get("resets_at"):
            continue
        satirlar.append(json.dumps(
            {"ts": int(simdi), "id": p["id"], "used": float(p["used"]), "resets_at": int(p["resets_at"])},
            ensure_ascii=False,
        ))
    if not satirlar:
        return 0
    try:
        yol.parent.mkdir(parents=True, exist_ok=True)
        with yol.open("a", encoding="utf-8") as h:
            h.write("\n".join(satirlar) + "\n")
        _budama(yol)
    except OSError:
        return 0
    return len(satirlar)


def _budama(yol: Path) -> None:
    try:
        satirlar = yol.read_text(encoding="utf-8").splitlines()
    except OSError:
        return
    if len(satirlar) <= ORNEKLEM_TAVAN:
        return
    kalan = satirlar[len(satirlar) // 2:]
    gecici = yol.with_name(yol.name + ".tmp")
    gecici.write_text("\n".join(kalan) + "\n", encoding="utf-8")
    os.replace(gecici, yol)


def ornek_oku(yol: Path = ORNEKLEM_YOLU) -> list[dict]:
    try:
        metin = yol.read_text(encoding="utf-8")
    except OSError:
        return []
    cikti = []
    for satir in metin.splitlines():
        try:
            v = json.loads(satir)
        except json.JSONDecodeError:
            continue
        if isinstance(v, dict) and "ts" in v and "id" in v:
            cikti.append(v)
    return cikti


# ---------------------------------------------------------------------------
# Hız ve R
# ---------------------------------------------------------------------------

def yanma_hizi(pid: str, used: float, resets_at: int, ornekler: list[dict], simdi: float,
               ufuk_saat: float = UFUK_SAAT, en_az_dk: int = EN_AZ_ARALIK_DK) -> float | None:
    """Son `ufuk_saat` içindeki EN ESKİ uygun örneğe göre %/saat. Aynı pencere şartı:
    örneğin resets_at'i şimdikiyle aynı. Kullanım düşmüşse (reset/sıfırlama) None."""
    en_eski = simdi - ufuk_saat * 3600
    adaylar = [
        o for o in ornekler
        if o.get("id") == pid and int(o.get("resets_at") or 0) == int(resets_at)
        and en_eski <= float(o["ts"]) <= simdi - en_az_dk * 60
    ]
    if not adaylar:
        return None
    eski = min(adaylar, key=lambda o: o["ts"])
    d_saat = (simdi - float(eski["ts"])) / 3600
    if d_saat <= 0:
        return None
    d_used = float(used) - float(eski["used"])
    if d_used < 0:
        return None  # sıfırlama — eski örnek başka bir dünyaya ait
    return d_used / d_saat


def bant(R: float | None, kalan: float, esik: tuple[float, float, float] = BANT_ESIK) -> str:
    if kalan < KAPALI_KALAN:
        return BANT_AD[3]
    if R is None:
        return BANT_AD[0]
    if R <= esik[0]:
        return BANT_AD[0]
    if R <= esik[1]:
        return BANT_AD[1]
    if R <= esik[2]:
        return BANT_AD[2]
    return BANT_AD[3]


def degerlendir(pid: str, used: float | None, resets_at: int | None, pencere_sn: int,
                ornekler: list[dict] | None = None, simdi: float | None = None,
                esik: tuple[float, float, float] = BANT_ESIK) -> dict | None:
    """Tek pencere için tam değerlendirme; veri eksikse None."""
    if used is None or not resets_at or not pencere_sn:
        return None
    simdi = simdi if simdi is not None else _simdi()
    ornekler = ornekler if ornekler is not None else []
    used = float(used)
    kalan = max(0.0, 100.0 - used)
    kalan_saat = (int(resets_at) - simdi) / 3600
    if kalan_saat <= 0:
        return {"id": pid, "used": used, "kalan": kalan, "kalan_saat": 0.0, "surdurulebilir": None,
                "yanma": None, "R": None, "tahmin": False, "tukenme": None,
                "bant": BANT_AD[0], "harca": False, "not": "reset geçti"}
    surdurulebilir = kalan / kalan_saat
    ufuk = max(UFUK_PAY * pencere_sn / 3600, 0.25)
    yanma = yanma_hizi(pid, used, int(resets_at), ornekler, simdi, ufuk_saat=ufuk,
                       en_az_dk=min(EN_AZ_ARALIK_DK, int(ufuk * 30)))
    tahmin = False
    if yanma is None:
        # yedek: geriye bakan tempo (used ÷ elapsed) — pencere başı kayan olduğundan tahmindir
        # pencere başında gürültülü (1 saatte %2 → "kapalı" çıkar); pencerenin
        # en az YEDEK_ESIK'i geçmeden yedek de kullanılmaz → R yok, serbest.
        gecen_saat = pencere_sn / 3600 - kalan_saat
        if gecen_saat >= YEDEK_ESIK * pencere_sn / 3600 and used > 0:
            yanma = used / gecen_saat
            tahmin = True
    R = None
    if yanma is not None and surdurulebilir > 0:
        R = yanma / surdurulebilir
    tukenme = None
    if yanma and yanma > 0:
        tukenme = simdi + (kalan / yanma) * 3600
        if tukenme > int(resets_at):
            tukenme = None  # reset'ten önce bitmiyor
    b = bant(R, kalan, esik)
    harca = (kalan_saat < HARCA_PAY * pencere_sn / 3600 and kalan > HARCA_KALAN
             and b in BANT_AD[:2])
    return {
        "id": pid, "used": used, "kalan": kalan, "kalan_saat": kalan_saat,
        "surdurulebilir": surdurulebilir, "yanma": yanma, "R": R, "tahmin": tahmin,
        "tukenme": tukenme, "bant": b, "harca": harca,
    }


def yonetici(degerler: Iterable[dict | None]) -> dict | None:
    """Yönetici bant: en yüksek R'li pencere (kapalı olan her zaman kazanır)."""
    adaylar = [d for d in degerler if d]
    if not adaylar:
        return None
    sira = {ad: i for i, ad in enumerate(BANT_AD)}
    return max(adaylar, key=lambda d: (sira[d["bant"]], d["R"] if d["R"] is not None else -1.0))


# ---------------------------------------------------------------------------
# Metin
# ---------------------------------------------------------------------------

def _sayi(x: float | None, basamak: int = 1) -> str:
    if x is None:
        return "—"
    return f"{x:.{basamak}f}".replace(".", ",")


def _saat_metni(epoch: float | None) -> str:
    if not epoch:
        return "—"
    yerel = dt.datetime.fromtimestamp(epoch)
    if yerel.date() == dt.datetime.now().date():
        return yerel.strftime("%H:%M")
    return yerel.strftime("%d.%m %H:%M")


def kisa_metin(d: dict | None) -> str:
    """Satır içi ek: ` [R 1,3 karne · biter 03:40]` gibi; tahminse `~`."""
    if not d:
        return ""
    parcalar = []
    if d["R"] is not None:
        parcalar.append(("R~" if d["tahmin"] else "R ") + _sayi(d["R"]))
    parcalar.append(d["bant"])
    if d["harca"]:
        parcalar.append("HARCA")
    if d["tukenme"]:
        parcalar.append("biter " + _saat_metni(d["tukenme"]))
    return " [" + " · ".join(parcalar) + "]"


def detay_metni(d: dict | None, ad: str) -> str:
    if not d:
        return f"  {ad}: veri yok"
    return (
        f"  {ad}: kalan %{_sayi(d['kalan'], 0)} / {_sayi(d['kalan_saat'])} saat → "
        f"sürdürülebilir %{_sayi(d['surdurulebilir'], 2)}/s · yanma "
        f"{'~' if d['tahmin'] else ''}%{_sayi(d['yanma'], 2)}/s · R {_sayi(d['R'], 2)} → {d['bant']}"
        + (" · HARCA" if d["harca"] else "")
        + (f" · biter {_saat_metni(d['tukenme'])}" if d["tukenme"] else "")
    )
