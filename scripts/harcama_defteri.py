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

Astra B2 (2026-09-06) — ``--ozet`` artık bakım / geliştirme / iş bütçesini de
basar. Bu defter ham transkriptlerden okur, ``calls.jsonl``'den değil; oradaki
``purpose`` etiketi burada yoktur, sınıflandırma yalnız oturumun çalışma
dizinine (``cwd``) dayanır. Ayırt edilemeyen oturum tahmin edilmez,
``sınıflandırılamadı`` hanesinde görünür — bkz. ``_sinif``.

Kullanım:  python harcama_defteri.py --topla       # artımlı biriktir (kanca bunu çağırır)
           python harcama_defteri.py --topla --yeniden   # sıfırdan yeniden kur
           python harcama_defteri.py --ozet        # gün + bütçe + en pahalı oturumlar
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

SURUM = 3
_BOS_HANE = {"istek": 0, "girdi": 0, "cikti": 0, "cache_okuma": 0, "cache_yazma": 0}

# --- Astra B2 (2026-09-06): bakım / geliştirme / iş bütçeleri --------------
#
# Bu defterin kaynağı ham transkriptlerdir, `calls.jsonl` değil; dolayısıyla
# çağrının `purpose` etiketi burada YOKTUR. Elimizdeki tek ayırt edici veri
# oturumun `cwd`'sidir (yoksa proje dizini adı, ki o da cwd'den türetilir).
# Sınıflandırma yalnız buna dayanır ve emin olunamayan hiçbir oturum bir
# gruba itilmez — `sınıflandırılamadı` gerçek bir hanedir, artık değil.
#
# NOT (ölçüldü, 2026-09-07): `claude_runner` alt süreçlere `BEYIN_INVOKED_BY`
# ortam değişkenini geçirir, ama bu değişken transkript satırlarının HİÇBİR
# alanında görünmez; `--session-id` de rastgele bir UUID'dir, önek taşımaz.
# Yani "bizim çalıştırıcımızın açtığı oturum" ancak çalışma dizininden
# tanınabilir: çalıştırıcı her çağrıyı `beyin-flush-*` / `beyin-ingest-*` /
# `beyin-codex-*` gibi önekli geçici bir dizinde, compile ise `compile-stage`
# dizininde koşturur.
GRUP_BAKIM = "bakım"
GRUP_GELISTIRME = "geliştirme"
GRUP_IS = "iş"
GRUP_BILINMIYOR = "sınıflandırılamadı"
GRUP_SIRASI = (GRUP_BAKIM, GRUP_GELISTIRME, GRUP_IS, GRUP_BILINMIYOR)

# Depo kökü: bu dizinin altındaki her oturum geliştirme/benchmark sayılır
# (tools/benchmark de onun altındadır, ayrı bir kurala gerek yok).
GELISTIRME_PARCALARI = ("origin-of-memory",)
# Çalıştırıcının açtığı geçici dizin önekleri + compile'ın sahne dizini.
CALISTIRICI_ONEKLERI = (
    "beyin-flush-",
    "beyin-ingest-",
    "beyin-claude-",
    "beyin-codex-",
    "beyin-agy-",
    "beyin-ollama-",
    "beyin-openai-compat-",
)
CALISTIRICI_PARCALARI = ("compile-stage",)


def _sinif(cwd: str | None, proje: str | None) -> str:
    """Oturumun bütçe grubu; veriden çıkmıyorsa tahmin edilmez.

    Sıra bilinçli: önce depo kuralı (benchmark sahne dizinleri depo altındadır
    ve geliştirme sayılmalıdır), sonra çalıştırıcı işareti, sonra kalan her
    tanımlı çalışma dizini iş.
    """
    ham = cwd or proje or ""
    if not isinstance(ham, str) or not ham.strip():
        return GRUP_BILINMIYOR
    # Proje dizini adı yol ayıracını '-' yapar; iki gösterimi de yakalayalım.
    duz = ham.replace("\\", "-").replace("/", "-").casefold()
    if any(parca in duz for parca in GELISTIRME_PARCALARI):
        return GRUP_GELISTIRME
    if any(parca in duz for parca in CALISTIRICI_PARCALARI):
        return GRUP_BAKIM
    if any(onek in duz for onek in CALISTIRICI_ONEKLERI):
        return GRUP_BAKIM
    return GRUP_IS


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
        "gruplar": {},
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
    veri.setdefault("gruplar", {})
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
    cwd = None
    sira = 0
    try:
        with dosya.open(encoding="utf-8", errors="replace") as h:
            for satir in h:
                if cwd is None and '"cwd"' in satir:
                    try:
                        aday = json.loads(satir)
                    except json.JSONDecodeError:
                        aday = None
                    if isinstance(aday, dict) and isinstance(aday.get("cwd"), str):
                        cwd = aday["cwd"]
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
    return {
        "kaynak": "claude",
        "ilk": ilk,
        "son": son,
        "cwd": cwd,
        "kayitlar": kayitlar,
        "ham": sira,
    }


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
    cwd = None
    try:
        with dosya.open(encoding="utf-8", errors="replace") as h:
            for satir in h:
                if cwd is None and '"cwd"' in satir:
                    try:
                        aday = json.loads(satir)
                    except json.JSONDecodeError:
                        aday = None
                    if isinstance(aday, dict):
                        yuk = aday.get("payload")
                        yuk = yuk if isinstance(yuk, dict) else {}
                        if isinstance(yuk.get("cwd"), str):
                            cwd = yuk["cwd"]
                        elif isinstance(aday.get("cwd"), str):
                            cwd = aday["cwd"]
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
    return {"kaynak": "codex", "ilk": ilk, "son": son, "cwd": cwd, "gunler": gunler}


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
    gruplar: dict[str, dict] = {}

    def _ekle(
        gun: str, model: str, istek: int, g: int, c: int, co: int, cy: int, grup: str
    ) -> None:
        hane = gunluk.setdefault(gun, {}).setdefault(model, _yeni_hane())
        gh = gruplar.setdefault(gun, {}).setdefault(grup, _yeni_hane())
        for hedef in (hane, gh):
            hedef["istek"] += istek
            hedef["girdi"] += g
            hedef["cikti"] += c
            hedef["cache_okuma"] += co
            hedef["cache_yazma"] += cy

    for yol in sorted(defter["dosyalar"]):
        kayit = defter["dosyalar"][yol]
        kimlik = kayit.get("kimlik") or Path(yol).stem
        grup = _sinif(kayit.get("cwd"), kayit.get("proje"))
        modeller: dict[str, dict] = {}
        if kayit.get("kaynak") == "codex":
            for gun, (g, c, co, toplam) in sorted(kayit.get("gunler", {}).items()):
                hane = modeller.setdefault("codex", {**_yeni_hane(), "toplam": 0})
                hane["girdi"] += g
                hane["cikti"] += c
                hane["cache_okuma"] += co
                hane["toplam"] += toplam
                _ekle(gun, "codex", 0, g, c, co, 0, grup)
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
                _ekle(gun, model, 1, g, c, co, cy, grup)
        if not modeller:
            continue
        oturumlar[kimlik] = {
            "dosya": yol,
            "proje": kayit.get("proje") or Path(yol).parent.name,
            "kaynak": kayit.get("kaynak"),
            "grup": grup,
            "cwd": kayit.get("cwd"),
            "ilk": kayit.get("ilk"),
            "son": kayit.get("son"),
            "modeller": modeller,
        }
    defter["oturumlar"] = oturumlar
    defter["gunluk"] = gunluk
    defter["gruplar"] = gruplar
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


def _grup_ozeti(defter: dict, gunler: list[str]) -> None:
    """Bakım / geliştirme / iş bütçesi — pencerede toplanmış hâliyle.

    Yalnız iki sütun basılır: önbellek okuma ve çıktı. Girdi jetonu Claude
    transkriptlerinde önbellekle ezici oranda örtüşür; toplamı raporlamak
    "harcama" izlenimini şişirir, oysa asıl fiyat çıktıda ve önbellek
    okumasındadır.
    """
    toplam: dict[str, dict] = {}
    for gun in gunler:
        for grup, hane in (defter.get("gruplar", {}).get(gun) or {}).items():
            hedef = toplam.setdefault(grup, _yeni_hane())
            for alan in _BOS_HANE:
                hedef[alan] += int(hane.get(alan) or 0)
    if not toplam:
        return
    genel_cikti = sum(int(h["cikti"]) for h in toplam.values()) or 1
    genel_onb = sum(int(h["cache_okuma"]) for h in toplam.values()) or 1
    print(f"  Bütçe ({len(gunler)} gün, amaç grubuna göre):")
    for grup in GRUP_SIRASI:
        hane = toplam.get(grup)
        if not hane:
            continue
        print(
            f"    {grup:<18} istek {hane['istek']:>6} · "
            f"önbellek okuma {hane['cache_okuma']//1000:>7}k "
            f"(%{100*hane['cache_okuma']//genel_onb:>3}) · "
            f"çıktı {hane['cikti']//1000:>6}k "
            f"(%{100*hane['cikti']//genel_cikti:>3})"
        )
    if GRUP_BILINMIYOR not in toplam:
        print(f"    {GRUP_BILINMIYOR:<18} yok — her oturumun cwd'si okunabildi")


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
    _grup_ozeti(defter, gunler)
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
