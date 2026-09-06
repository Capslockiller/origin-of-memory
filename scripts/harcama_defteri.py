#!/usr/bin/env python3
"""Harcama defteri — jeton harcamasını arka planda biriktirir.

Master kararı 2026-08-29: "harcanan tokenları da genel olarak kaydet; görev
başına ne kadar harcanıyor, sohbet bazında ne harcamışız — süreç arkada
biriktirsin." Kaynaklar yerel ve kesindir (ccusage deseni):
  - Claude: ~/.claude/projects/**/*.jsonl usage blokları → sohbet (oturum) bazı
  - Codex:  ~/.codex/sessions/**/rollout-*.jsonl token_count kümülatifleri → şerit/görev bazı
Filigran: dosya (boyut, mtime) değişmediyse yeniden okunmaz. Defter atomik
yazılır; hiçbir şey silinmez, yalnız üzerine biriktirilir.

Astra A2 (2026-09-06) — dış denetimin iki bulgusu düzeltildi:
  1. Yineleme: Claude transkriptleri aynı yanıtı birden çok satırda yazar
     (akış güncellemeleri, yeniden denemeler, sıkıştırma). Her kopyada bir
     ``usage`` bloğu vardır; eski sayım hepsini toplardı. Artık her yanıt
     ``message.id`` (yoksa ``requestId``, yoksa ``uuid``) ile anahtarlanır ve
     o anahtarın SON kaydı sayılır. Aynı kimlik iki dosyada görünürse yalnız
     bir kez sayılır (sıralı yol düzeninde ilk dosya sahiplenir).
  2. Gün ataması: günlük kırılım artık oturumun son damgasına değil, her
     kaydın KENDİ damgasına göre kurulur; gece yarısını aşan oturum iki güne
     doğru dağılır.
Kimlik haritası deftere dosya bazında kalıcı yazılır ki artımlı koşularda
(değişmemiş dosya yeniden okunmaz) yineleme durumu kaybolmasın.

Kullanım:  python harcama_defteri.py --topla       # artımlı biriktir (kanca bunu çağırır)
           python harcama_defteri.py --topla --yeniden   # sıfırdan yeniden kur
           python harcama_defteri.py --ozet        # gün + en pahalı oturumlar
           python harcama_defteri.py --ozet --gun 7
Ortam:     BEYIN_HARCAMA_DEFTERI=<yol>  → defteri başka bir dosyaya yazar
           (salt-okuma ölçüm koşuları canlı defteri ezmesin diye).
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

SURUM = 2
_BOS_HANE = {"istek": 0, "girdi": 0, "cikti": 0, "cache_okuma": 0, "cache_yazma": 0}


def _defter_yolu() -> Path:
    """Defterin yolu; ortam değişkeni her çağrıda yeniden okunur."""
    ortam = os.environ.get("BEYIN_HARCAMA_DEFTERI")
    return Path(ortam) if ortam else DEFTER


def _bos_defter() -> dict:
    return {
        "surum": SURUM,
        "filigran": {},
        "dosyalar": {},
        "oturumlar": {},
        "gunluk": {},
    }


def _yukle() -> dict:
    try:
        veri = json.loads(_defter_yolu().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return _bos_defter()
    if not isinstance(veri, dict) or int(veri.get("surum") or 0) < SURUM:
        # sürüm 1 defterinde kimlik haritası yok; yinelenmiş sayıları taşımak
        # yerine sıfırdan kuruyoruz (kaynak dosyalar zaten yerinde).
        return _bos_defter()
    veri.setdefault("filigran", {})
    veri.setdefault("dosyalar", {})
    veri.setdefault("oturumlar", {})
    veri.setdefault("gunluk", {})
    return veri


def _atomik_yaz(veri: dict) -> None:
    hedef = _defter_yolu()
    hedef.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=str(hedef.parent), suffix=".tmp")
    with os.fdopen(fd, "w", encoding="utf-8") as handle:
        json.dump(veri, handle, ensure_ascii=False)
    os.replace(tmp, hedef)


def _gun(ts: str | None) -> str:
    """ISO damgasını yerel (İstanbul) takvim gününe çevirir.

    Transkript damgaları UTC ('...Z'); eski sürüm ilk 10 karakteri keserdi,
    yani UTC gününe yazardı — oysa ``ozet`` yerel ``date.today()`` ile
    karşılaştırıyor. Artık ikisi aynı takvimde.
    """
    if not isinstance(ts, str) or not ts:
        return "?"
    try:
        an = dt.datetime.fromisoformat(ts.replace("Z", "+00:00"))
    except ValueError:
        return ts[:10] or "?"
    if an.tzinfo is not None:
        an = an.astimezone()
    return an.date().isoformat()


def _usage_anahtari(veri: dict, mesaj: dict, sira: int) -> str:
    """Bir yanıtın kimliği: message.id → requestId → uuid → satır sırası."""
    for aday in (mesaj.get("id"), veri.get("requestId"), veri.get("uuid")):
        if isinstance(aday, str) and aday:
            return aday
    return f"#{sira}"


def _claude_dosya_ozeti(dosya: Path) -> dict | None:
    """Tek sohbet dosyasının yanıt-kimliği ile tekilleştirilmiş kayıtları.

    Dönen ``kayitlar``: kimlik → [gün, model, girdi, çıktı, cache_okuma,
    cache_yazma]. Aynı kimlik dosyada birden çok görünürse SON kayıt kalır
    (sonraki satır öncekini geçersiz kılar: akış güncellemesi tamamlanır).
    """
    kayitlar: dict[str, list] = {}
    ilk = son = None
    sira = 0
    try:
        with dosya.open(encoding="utf-8", errors="replace") as h:
            for satir in h:
                if '"usage"' not in satir:
                    continue
                try:
                    veri = json.loads(satir)
                except json.JSONDecodeError:
                    continue
                if not isinstance(veri, dict):
                    continue
                mesaj = veri.get("message") or {}
                if not isinstance(mesaj, dict):
                    mesaj = {}
                kullanim = mesaj.get("usage") or veri.get("usage")
                if not isinstance(kullanim, dict):
                    continue
                sira += 1
                ts = veri.get("timestamp")
                if isinstance(ts, str):
                    ilk = ilk or ts
                    son = ts
                model = str(mesaj.get("model") or veri.get("model") or "?")
                kayitlar[_usage_anahtari(veri, mesaj, sira)] = [
                    _gun(ts),
                    model,
                    int(kullanim.get("input_tokens") or 0),
                    int(kullanim.get("output_tokens") or 0),
                    int(kullanim.get("cache_read_input_tokens") or 0),
                    int(kullanim.get("cache_creation_input_tokens") or 0),
                ]
    except OSError:
        return None
    if not kayitlar:
        return None
    return {"kaynak": "claude", "ilk": ilk, "son": son, "kayitlar": kayitlar, "ham": sira}


def _codex_dosya_ozeti(dosya: Path) -> dict | None:
    """Rollout'un kümülatif ``total_token_usage`` sayacı, güne dağıtılmış.

    ``total_token_usage`` oturum başından beri artan bir sayaçtır (denetim
    notu: artım sanılıp toplanırsa şişer). Eski sürüm zaten yalnız SON kaydı
    alıyordu — yani Codex tarafında çifte sayım YOKTU. Değişen tek şey gün
    ataması: ardışık kümülatif değerlerin farkı alınıp her artım kendi
    damgasının gününe yazılıyor; oturum toplamı birebir aynı kalıyor.
    """
    gunler: dict[str, list] = {}
    onceki = [0, 0, 0, 0]
    son_toplam = None
    ilk = son = None
    try:
        with dosya.open(encoding="utf-8", errors="replace") as h:
            for satir in h:
                if '"token_count"' not in satir:
                    continue
                try:
                    veri = json.loads(satir)
                except json.JSONDecodeError:
                    continue
                if not isinstance(veri, dict):
                    continue
                ilk = ilk or veri.get("timestamp")
                p = veri.get("payload") or {}
                toplam = (p.get("info") or {}).get("total_token_usage") or p.get(
                    "total_token_usage"
                )
                if not isinstance(toplam, dict):
                    continue
                ts = veri.get("timestamp")
                son = ts
                simdi = [
                    int(toplam.get("input_tokens") or 0),
                    int(toplam.get("output_tokens") or 0),
                    int(toplam.get("cached_input_tokens") or 0),
                    int(toplam.get("total_tokens") or 0),
                ]
                # sayaç geri gitmez; gitmişse (yeni oturum devralması) artımı
                # olduğu gibi al, eksiye düşürme.
                artim = [max(0, y - e) for y, e in zip(simdi, onceki)]
                onceki = simdi
                son_toplam = simdi
                if any(artim):
                    hane = gunler.setdefault(_gun(ts), [0, 0, 0, 0])
                    for i in range(4):
                        hane[i] += artim[i]
    except OSError:
        return None
    if son_toplam is None:
        return None
    return {"kaynak": "codex", "ilk": ilk, "son": son, "gunler": gunler}


def _yeni_hane() -> dict:
    return dict(_BOS_HANE)


def _turet(defter: dict) -> int:
    """``dosyalar`` haritasından ``oturumlar`` ve ``gunluk`` kırılımını kurar.

    Dosyalar yol sırasına göre gezilir; bir yanıt kimliği ilk gören dosyaya
    aittir, sonrakiler sayılmaz. Dönen değer: dosyalar-arası yinelenen kayıt
    sayısı.
    """
    gorulen: set[str] = set()
    capraz = 0
    oturumlar: dict[str, dict] = {}
    gunluk: dict[str, dict] = {}

    def _ekle(gun: str, model: str, istek: int, g: int, c: int, co: int, cy: int) -> None:
        hane = gunluk.setdefault(gun, {}).setdefault(model, _yeni_hane())
        hane["istek"] += istek
        hane["girdi"] += g
        hane["cikti"] += c
        hane["cache_okuma"] += co
        hane["cache_yazma"] += cy

    for yol in sorted(defter["dosyalar"]):
        kayit = defter["dosyalar"][yol]
        kimlik = kayit.get("kimlik") or Path(yol).stem
        modeller: dict[str, dict] = {}
        if kayit.get("kaynak") == "codex":
            for gun, (g, c, co, toplam) in sorted(kayit.get("gunler", {}).items()):
                hane = modeller.setdefault("codex", {**_yeni_hane(), "toplam": 0})
                hane["girdi"] += g
                hane["cikti"] += c
                hane["cache_okuma"] += co
                hane["toplam"] += toplam
                _ekle(gun, "codex", 0, g, c, co, 0)
        else:
            for anahtar, satir in kayit.get("kayitlar", {}).items():
                if anahtar in gorulen:
                    capraz += 1
                    continue
                gorulen.add(anahtar)
                gun, model, g, c, co, cy = satir
                hane = modeller.setdefault(model, _yeni_hane())
                hane["istek"] += 1
                hane["girdi"] += g
                hane["cikti"] += c
                hane["cache_okuma"] += co
                hane["cache_yazma"] += cy
                _ekle(gun, model, 1, g, c, co, cy)
        if not modeller:
            continue
        oturumlar[kimlik] = {
            "dosya": yol,
            "proje": kayit.get("proje") or Path(yol).parent.name,
            "kaynak": kayit.get("kaynak"),
            "ilk": kayit.get("ilk"),
            "son": kayit.get("son"),
            "modeller": modeller,
        }
    defter["oturumlar"] = oturumlar
    defter["gunluk"] = gunluk
    defter["capraz_yinelenen"] = capraz
    return capraz


def topla(yeniden: bool = False) -> tuple[int, int]:
    defter = _bos_defter() if yeniden else _yukle()
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
        if filigran.get(anahtar) == imza and anahtar in defter["dosyalar"]:
            atlanan += 1
            continue
        ozet_ = okuyucu(dosya)
        filigran[anahtar] = imza
        if ozet_ is None:
            defter["dosyalar"].pop(anahtar, None)
            continue
        # değişen dosyanın haritası bütünüyle değiştirilir (birikmez)
        defter["dosyalar"][anahtar] = {
            "kimlik": dosya.stem,
            "proje": dosya.parent.name,
            **ozet_,
        }
        yeni += 1
    _turet(defter)
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
    parser.add_argument(
        "--yeniden",
        action="store_true",
        help="filigranı ve kimlik haritasını atıp defteri sıfırdan kurar",
    )
    args = parser.parse_args()
    if args.topla or args.yeniden:
        yeni, atlanan = topla(yeniden=args.yeniden)
        print(f"defter: {yeni} dosya işlendi, {atlanan} değişmemiş atlandı")
    if args.ozet:
        ozet(args.gun)
    if not (args.topla or args.ozet or args.yeniden):
        ozet(args.gun)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
