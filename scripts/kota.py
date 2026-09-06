#!/usr/bin/env python3
"""Kota okuyucu — Claude harcaması + Codex resmî yüzdeleri, tek satır.

Master kararı 2026-08-29 (39. oturum): "bu yolları hemen kullanmaya başlayalım".
Kaynaklar (Ham-Araştırma/2026-08-29-kota-okuma.md):
  - Codex: ~/.codex/sessions/**/rollout-*.jsonl içindeki token_count
    olaylarının SON rate_limits alanı — RESMÎ yüzde (bu makinede ölçüldü:
    1.172 olayda alan dolu). 5s = primary (300 dk), hafta = secondary (10080 dk).
  - Claude, katman 1 (statusline): ~/.claude/projects üzerinden statusline
    köprüsünün düşürdüğü rate_limits önbelleği (claude-kota.json) — RESMÎ,
    ama yalnız statusline hook'u tetiklenmişse dolar; bu makinede hiç
    yazılmamıştı (2026-08-29 itibarıyla).
  - Claude, katman 2 (oauth — bu ekleme): topluluk kaynaklı belgesiz uç
    https://api.anthropic.com/api/oauth/usage — Claude Code'un kendi
    /usage komutunu besleyen SUNUCU TARAFI veri. Kaynak: topluluk
    (github.com/ohugonnot/claude-code-statusline; anthropics/claude-code
    issue #31021, #45133). ~/.claude/.credentials.json içindeki
    accessToken ile Bearer + User-Agent: claude-code/<sürüm> +
    anthropic-beta: oauth-2025-04-20 başlıklarıyla GET edilir; yanlış/eksik
    başlık agresif 429 kovasına düşürür. Yanıt disk önbelleğine
    (.state\\claude-kota-oauth.json) yazılır, TABAN 300 sn — bu süreden
    taze önbellek varsa AĞA HİÇ ÇIKILMAZ. Uç BELGESİZ ve her an
    kaldırılabilir/şekli değişebilir; ayrıştırıcı savunmacı yazıldı, HER
    hata (ağ/HTTP/JSON) yutulur ve zincir sessizce bir alt katmana düşer.
    Jeton YENİLEME asla denenmez (yalnız erişim jetonu geçerliyse GET
    edilir); jetonun kendisi hiçbir zaman yazdırılmaz/önbelleklenmez —
    önbellekte yalnız SUNUCU YANITI durur.
  - Claude, katman 3 (harcama): ~/.claude/projects/**/*.jsonl usage
    blokları — KESİN harcama (ccusage deseni), resmî yüzde yoksa son çare.
Zincir: statüsline önbelleği → oauth ucu → yerel harcama dökümü.
ToS-riskli yollar (ChatGPT token'ını belgesiz uca göndermek) bilinçli DIŞARIDA.

BAYAT KAYNAK (Astra A8, 2026-09-06): her pencere, yüzdenin geldiği gözlemin
zaman damgasını (`gozlem`) da taşır — OAuth önbelleğinin yazılma anı, Codex
rollout dosyasının mtime'ı. Gözlem `BEYIN_KOTA_BAYAT_DK` dakikadan (varsayılan
120) eskiyse o pencerenin bantı `bilinmiyor` olur ve satırda `[? bayat 2050dk]`
görünür; `bilinmiyor` asla "serbest" diye okunmaz. Ayrıntı: kota_hiz docstring'i.

Kullanım:  python kota.py            # tek satır (SessionStart enjeksiyonu için)
           python kota.py --detay    # çok satırlı döküm
           python kota.py --json     # makine okur
"""
from __future__ import annotations

import argparse
import datetime as dt
import io
import sys

# Git Bash / boru altinda cp1254 stdout'u Turkce isaretlerde cakiliyordu;
# cikis her zaman UTF-8'e sabitlenir (Windows konsolu da bunu basar).
if hasattr(sys.stdout, "buffer"):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
import json
import os
import urllib.error
import urllib.request
from pathlib import Path

import kota_hiz

CODEX_SESSIONS = Path.home() / ".codex" / "sessions"
CLAUDE_PROJECTS = Path.home() / ".claude" / "projects"
SAAT_5 = 5 * 3600
GUN_7 = 7 * 86400


def codex_resmi() -> dict | None:
    """En taze rollout dosyalarından son dolu rate_limits alanını döndür."""
    if not CODEX_SESSIONS.exists():
        return None
    dosyalar = sorted(
        CODEX_SESSIONS.rglob("rollout-*.jsonl"),
        key=lambda p: p.stat().st_mtime,
        reverse=True,
    )[:12]
    for dosya in dosyalar:
        son = None
        try:
            with dosya.open(encoding="utf-8", errors="replace") as h:
                for satir in h:
                    if '"token_count"' not in satir:
                        continue
                    try:
                        veri = json.loads(satir)
                    except json.JSONDecodeError:
                        continue
                    rl = (veri.get("payload") or {}).get("rate_limits")
                    if rl:
                        son = rl
        except OSError:
            continue
        if son:
            mtime = dosya.stat().st_mtime
            son["_kaynak"] = dosya.name
            son["_dosya_zamani"] = dt.datetime.fromtimestamp(mtime).isoformat(
                timespec="minutes"
            )
            # Gözlem anı = rollout dosyasının mtime'ı; bayatlık ölçüsü buradan.
            son["_gozlem"] = int(mtime)
            return son
    return None


def _claude_kayit_zamani(veri: dict) -> float | None:
    ts = veri.get("timestamp")
    if not isinstance(ts, str):
        return None
    try:
        return dt.datetime.fromisoformat(ts.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


def claude_harcama(hizli: bool = False) -> dict:
    """Son 5 saat (ve hizli değilse 7 gün) için model-bazlı jeton toplamları."""
    simdi = dt.datetime.now(dt.timezone.utc).timestamp()
    toplam = {"5s": {}, "7g": {}}
    if not CLAUDE_PROJECTS.exists():
        return toplam
    tavan = SAAT_5 if hizli else GUN_7
    for dosya in CLAUDE_PROJECTS.rglob("*.jsonl"):
        try:
            yas = simdi - dosya.stat().st_mtime
        except OSError:
            continue
        if yas > tavan + 3600:
            continue
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
                    zaman = _claude_kayit_zamani(veri)
                    if zaman is None or simdi - zaman > GUN_7:
                        continue
                    model = str(mesaj.get("model") or veri.get("model") or "?")
                    cikti = int(kullanim.get("output_tokens") or 0)
                    girdi = int(kullanim.get("input_tokens") or 0)
                    for pencere, sinir in (("5s", SAAT_5), ("7g", GUN_7)):
                        if simdi - zaman <= sinir:
                            hane = toplam[pencere].setdefault(
                                model, {"girdi": 0, "cikti": 0, "istek": 0}
                            )
                            hane["girdi"] += girdi
                            hane["cikti"] += cikti
                            hane["istek"] += 1
        except OSError:
            continue
    return toplam


CLAUDE_KOTA_CACHE = Path(r"E:\OdenaOS\.claude\scripts\.state\claude-kota.json")


def claude_resmi() -> dict | None:
    """Statusline köprüsünün düşürdüğü resmî rate_limits önbelleği (varsa)."""
    try:
        veri = json.loads(CLAUDE_KOTA_CACHE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    yazilma = veri.get("yazilma")
    try:
        yas = dt.datetime.now(dt.timezone.utc) - dt.datetime.fromisoformat(yazilma)
    except (TypeError, ValueError):
        return None
    if yas.total_seconds() > 6 * 3600:
        return None  # bayat — yanlış güven vermektense sus
    rl = veri.get("rate_limits")
    if isinstance(rl, dict):
        rl = dict(rl)
        rl["_yas_dk"] = int(yas.total_seconds() // 60)
        rl["_gozlem"] = int(dt.datetime.fromisoformat(yazilma).timestamp())
        rl["_kaynak"] = "statusline"
        return rl
    return None


# ---------------------------------------------------------------------------
# Katman 2: OAuth kullanım ucu (belgesiz, topluluk kaynaklı — bkz. docstring)
# ---------------------------------------------------------------------------

CLAUDE_OAUTH_CACHE = Path(r"E:\OdenaOS\.claude\scripts\.state\claude-kota-oauth.json")
CLAUDE_CRED_PATH = Path.home() / ".claude" / ".credentials.json"
OAUTH_URL = "https://api.anthropic.com/api/oauth/usage"
OAUTH_CACHE_TABAN_SN = 300  # bu süreden taze önbellek varsa ağa çıkılmaz
OAUTH_UA_VARSAYILAN = "claude-code/2.1.245"


def _iso_epoch(deger) -> int | None:
    """ISO 8601 metnini unix epoch saniyeye çevirir; olmazsa None."""
    if not isinstance(deger, str):
        return None
    try:
        return int(dt.datetime.fromisoformat(deger.replace("Z", "+00:00")).timestamp())
    except ValueError:
        return None


def _oauth_normalize(ham: dict) -> dict:
    """Uç yanıtını tek_satir()'in zaten tükettiği ortak şekle çevirir."""
    def pencere(anahtar: str) -> dict:
        blok = ham.get(anahtar)
        if not isinstance(blok, dict):
            blok = {}
        return {
            "used_percentage": blok.get("utilization"),
            "resets_at": _iso_epoch(blok.get("resets_at")),
        }

    sonuc = {"five_hour": pencere("five_hour"), "seven_day": pencere("seven_day")}

    # Model-kapsamlı haftalık limitler (ör. Fable) — limits[] içindeki
    # weekly_scoped kalemleri; Master kararı 2026-08-29: Fable limiti görünür olacak.
    kapsamli = []
    for kalem in ham.get("limits") or []:
        if not isinstance(kalem, dict) or kalem.get("kind") != "weekly_scoped":
            continue
        model = ((kalem.get("scope") or {}).get("model") or {})
        ad = str(model.get("display_name") or "?")
        kapsamli.append({
            "ad": ad,
            "used_percentage": kalem.get("percent"),
            "resets_at": _iso_epoch(kalem.get("resets_at")),
        })
    sonuc["_kapsamli"] = kapsamli

    # Sonnet'e özgü haftalık yüzde — belgesiz alan, iki olası yerde aranır.
    sonnet = ham.get("seven_day_sonnet")
    if not isinstance(sonnet, dict):
        sonnet = None
    if sonnet is None:
        for kalem in kapsamli:
            if "sonnet" in kalem["ad"].lower():
                sonnet = {
                    "used_percentage": kalem["used_percentage"],
                    "resets_at": kalem["resets_at"],
                }
                break
    sonuc["_seven_day_sonnet"] = sonnet
    ek = ham.get("extra_usage")
    sonuc["_asim"] = bool(isinstance(ek, dict) and ek.get("is_enabled"))
    return sonuc


def _oauth_ham_oku() -> dict:
    """Önbellek dosyasının tamamı (veri + tanı alanları); yoksa boş sözlük."""
    try:
        ham = json.loads(CLAUDE_OAUTH_CACHE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    return ham if isinstance(ham, dict) else {}


def _oauth_tani_oku() -> dict:
    """Son yenileme denemesinin kaydı: son_deneme · son_hata · http_status."""
    ham = _oauth_ham_oku()
    return {
        "son_deneme": ham.get("son_deneme"),
        "son_hata": ham.get("son_hata"),
        "http_status": ham.get("http_status"),
    }


def _oauth_cache_oku() -> tuple[dt.datetime, dict] | None:
    ham = _oauth_ham_oku()
    if not ham:
        return None
    veri = ham.get("veri")
    if not isinstance(veri, dict):
        return None
    try:
        yaz_zaman = dt.datetime.fromisoformat(ham.get("yazilma"))
    except (TypeError, ValueError):
        return None
    if yaz_zaman.tzinfo is None:
        yaz_zaman = yaz_zaman.replace(tzinfo=dt.timezone.utc)
    return yaz_zaman, veri


def _oauth_atomik_yaz(ham: dict) -> None:
    try:
        CLAUDE_OAUTH_CACHE.parent.mkdir(parents=True, exist_ok=True)
        gecici = CLAUDE_OAUTH_CACHE.with_name(CLAUDE_OAUTH_CACHE.name + ".tmp")
        gecici.write_text(json.dumps(ham, ensure_ascii=False), encoding="utf-8")
        os.replace(gecici, CLAUDE_OAUTH_CACHE)
    except OSError:
        pass  # önbellek yazılamazsa sessiz geç — kritik değil


def _oauth_cache_yaz(veri: dict) -> None:
    """Atomik yaz: geçici dosya + os.replace. Yalnız SUNUCU YANITI durur — jeton asla."""
    _oauth_atomik_yaz({
        "yazilma": dt.datetime.now(dt.timezone.utc).isoformat(),
        "veri": veri,
        "son_deneme": dt.datetime.now(dt.timezone.utc).isoformat(),
        "son_hata": None,
        "http_status": 200,
    })


def _oauth_tani_yaz(hata: str, http_status: int | None = None) -> None:
    """Başarısız yenileme denemesini önbelleğe iliştirir (Astra A-borç 4).

    ``yazilma`` ve ``veri`` KORUNUR: gözlem yaşı denemeyle sıfırlanmaz, yoksa
    36 saatlik bayat bir yüzde taze görünürdü. Denetimde görülen hata tam da
    sessiz düşüştü — yenileme yolu çöküyor, satır bunu hiç söylemiyordu.
    """
    ham = _oauth_ham_oku()
    ham["son_deneme"] = dt.datetime.now(dt.timezone.utc).isoformat()
    ham["son_hata"] = hata
    ham["http_status"] = http_status
    _oauth_atomik_yaz(ham)


def _kimlik_oku() -> tuple[str | None, int | None, str | None, str | None]:
    """(accessToken, expiresAt_ms, subscriptionType, rateLimitTier). Salt okunur."""
    try:
        ham = json.loads(CLAUDE_CRED_PATH.read_text(encoding="utf-8"))
        oauth = ham.get("claudeAiOauth")
        if not isinstance(oauth, dict):
            return None, None, None, None
        return (
            oauth.get("accessToken"),
            oauth.get("expiresAt"),
            oauth.get("subscriptionType"),
            oauth.get("rateLimitTier"),
        )
    except (OSError, json.JSONDecodeError):
        return None, None, None, None


def claude_oauth() -> dict | None:
    """OAuth kullanım ucundan resmî % — bkz. modül docstring'i. ASLA çökmez."""
    try:
        return _claude_oauth_ic()
    except Exception:
        return None


def _claude_oauth_ic() -> dict | None:
    simdi = dt.datetime.now(dt.timezone.utc)
    onbellek = _oauth_cache_oku()
    _, _, abone, oran_katmani = _kimlik_oku()

    def etiketle(sozluk: dict, yas_sn: float, bayat: bool = False) -> dict:
        sozluk = dict(sozluk)
        sozluk["_kaynak"] = "oauth"
        sozluk["_yas_dk"] = int(yas_sn // 60)
        # Gözlem anı = önbelleğin yazıldığı an (sunucu yanıtının alındığı an).
        sozluk["_gozlem"] = int(simdi.timestamp() - yas_sn)
        if abone:
            sozluk["_subscriptionType"] = abone
        if oran_katmani:
            sozluk["_rateLimitTier"] = oran_katmani
        if bayat:
            sozluk["_bayat"] = True
        tani = _oauth_tani_oku()
        if tani.get("son_hata"):
            sozluk["_oauth_hata"] = tani["son_hata"]
            sozluk["_oauth_http"] = tani.get("http_status")
            sozluk["_oauth_son_deneme"] = tani.get("son_deneme")
        return sozluk

    if onbellek:
        yaz_zaman, ham_veri = onbellek
        yas_sn = (simdi - yaz_zaman).total_seconds()
        if 0 <= yas_sn < OAUTH_CACHE_TABAN_SN:
            return etiketle(_oauth_normalize(ham_veri), yas_sn)

    def bayat_donus() -> dict | None:
        if onbellek:
            yaz_zaman, ham_veri = onbellek
            yas_sn = max((simdi - yaz_zaman).total_seconds(), 0)
            return etiketle(_oauth_normalize(ham_veri), yas_sn, bayat=True)
        return None

    token, bitis_ms, _, _ = _kimlik_oku()
    if not token or not bitis_ms:
        _oauth_tani_yaz("kimlik-dosyasi-okunamadi")
        return bayat_donus()
    if bitis_ms <= simdi.timestamp() * 1000:
        # Erişim jetonu süresi dolmuş — YENİLEME DENENMEZ (jeton yenilemek
        # CLI'nin işidir, kota okuyucusunun değil). 2026-09-06 teşhisi: uç
        # gerçekten 401 "OAuth access token has expired" veriyor; çözüm
        # sahibin Claude Code'da yeniden oturum açmasıdır.
        _oauth_tani_yaz("jeton-suresi-doldu", 401)
        return bayat_donus()

    yanit_kodu: int | None = None
    try:
        ua = os.environ.get("BEYIN_KOTA_UA", OAUTH_UA_VARSAYILAN)
        istek = urllib.request.Request(
            OAUTH_URL,
            headers={
                "Authorization": f"Bearer {token}",
                "User-Agent": ua,
                "anthropic-beta": "oauth-2025-04-20",
            },
            method="GET",
        )
        with urllib.request.urlopen(istek, timeout=4) as yanit:
            yanit_kodu = getattr(yanit, "status", None)
            govde = yanit.read().decode("utf-8", errors="replace")
        yeni_veri = json.loads(govde)
    except urllib.error.HTTPError as hata:
        _oauth_tani_yaz("http-hatasi", getattr(hata, "code", None))
        return bayat_donus()
    except (urllib.error.URLError, TimeoutError, OSError) as hata:
        _oauth_tani_yaz("ag-hatasi:" + type(hata).__name__, yanit_kodu)
        return bayat_donus()
    except (json.JSONDecodeError, ValueError) as hata:
        _oauth_tani_yaz("yanit-cozulemedi:" + type(hata).__name__, yanit_kodu)
        return bayat_donus()

    _oauth_cache_yaz(yeni_veri)
    return etiketle(_oauth_normalize(yeni_veri), 0)


def resmi_zinciri() -> dict | None:
    """Claude resmî % çözünürlük sırası: statusline önbelleği → oauth ucu."""
    resmi = claude_resmi()
    if resmi:
        return resmi
    return claude_oauth()


_AYLAR = ["Oca", "Şub", "Mar", "Nis", "May", "Haz", "Tem", "Ağu", "Eyl", "Eki", "Kas", "Ara"]
_GUNLER = ["Pzt", "Sal", "Çar", "Per", "Cum", "Cmt", "Paz"]


def _reset_metni(epoch: int | None) -> str:
    """Yenilenme zamanı, VERİDEN: bugünse saat, değilse gün+ay+haftagünü+saat."""
    if not epoch:
        return "?"
    yerel = dt.datetime.fromtimestamp(epoch)
    simdi = dt.datetime.now()
    if yerel <= simdi:
        return "geçti"
    if yerel.date() == simdi.date():
        return yerel.strftime("%H:%M")
    return f"{yerel.day} {_AYLAR[yerel.month - 1]} {_GUNLER[yerel.weekday()]} {yerel.strftime('%H:%M')}"


def _tempo(yuzde, resets_at, pencere_sn: int) -> float | None:
    """Kullanımı pencerede geçen süreye oranlar. 1,0 = tam sürdürülebilir tempo;
    üstü, bütçenin pencere sonundan önce biteceği anlamına gelir (Master kuralı
    2026-08-29: değerlendirme hafta bütçesinin saat temposuna indirgenir)."""
    if yuzde is None or not resets_at:
        return None
    simdi = dt.datetime.now(dt.timezone.utc).timestamp()
    gecen = simdi - (resets_at - pencere_sn)
    if gecen <= 0 or gecen > pencere_sn:
        return None
    beklenen = gecen / pencere_sn * 100
    return (yuzde / beklenen) if beklenen > 0 else None


def _tempo_metni(tempo: float | None) -> str:
    if tempo is None:
        return ""
    isaret = "⚠" if tempo > 1.15 else ""
    return f" ({isaret}{tempo:.1f}×)".replace(".", ",")


def _yas_dk(gozlem: int | None, simdi: float | None = None) -> int | None:
    """Gözlem anının dakika cinsinden yaşı; gözlem yoksa None (= yaş bilinmiyor)."""
    if not gozlem:
        return None
    simdi = simdi if simdi is not None else dt.datetime.now(dt.timezone.utc).timestamp()
    return max(0, int((simdi - int(gozlem)) // 60))


def pencereler(codex: dict | None, resmi: dict | None, simdi: float | None = None) -> list[dict]:
    """Hız katmanının okuduğu ortak pencere listesi.

    Alanlar: id · used · resets_at · pencere_sn · gozlem (kaynak zaman damgası,
    epoch) · gozlem_yas_dk. Gözlem A8'in düzeltmesi: yüzdenin kendisi değil,
    yüzdenin NE ZAMAN gözlendiği bantı belirler.
    """
    liste = []
    if codex:
        codex_gozlem = codex.get("_gozlem")
        for pid, blok, vars_dk in (("codex-5s", codex.get("primary"), 300),
                                   ("codex-hafta", codex.get("secondary"), 10080)):
            blok = blok or {}
            liste.append({"id": pid, "used": blok.get("used_percent"), "resets_at": blok.get("resets_at"),
                          "pencere_sn": int(blok.get("window_minutes") or vars_dk) * 60,
                          "gozlem": codex_gozlem,
                          "gozlem_yas_dk": _yas_dk(codex_gozlem, simdi)})
    if resmi:
        gozlem = resmi.get("_gozlem")
        yas = _yas_dk(gozlem, simdi)
        if yas is None and isinstance(resmi.get("_yas_dk"), int):
            yas = resmi["_yas_dk"]
        bes = resmi.get("five_hour") or {}
        hafta = resmi.get("seven_day") or {}
        liste.append({"id": "claude-5s", "used": bes.get("used_percentage"),
                      "resets_at": bes.get("resets_at"), "pencere_sn": SAAT_5,
                      "gozlem": gozlem, "gozlem_yas_dk": yas})
        liste.append({"id": "claude-hafta", "used": hafta.get("used_percentage"),
                      "resets_at": hafta.get("resets_at"), "pencere_sn": GUN_7,
                      "gozlem": gozlem, "gozlem_yas_dk": yas})
        for k in resmi.get("_kapsamli") or []:
            liste.append({"id": "claude-" + str(k.get("ad") or "?").lower(),
                          "used": k.get("used_percentage"), "resets_at": k.get("resets_at"),
                          "pencere_sn": GUN_7, "gozlem": gozlem, "gozlem_yas_dk": yas})
    return liste


def hizlar(codex: dict | None, resmi: dict | None, kaydet: bool = True) -> dict[str, dict]:
    """Her pencere için kota_hiz.degerlendir; kaydet=True ise örneklem defterine yazar.

    Örneklem defterine yalnız DEĞİŞMİŞ gözlemler yazılır (kota_hiz.ornek_yaz);
    aynı defter hem ölçüm hem yazım için tek kez okunur.
    """
    liste = pencereler(codex, resmi)
    ornekler = kota_hiz.ornek_oku()
    sonuc = {}
    for p in liste:
        d = kota_hiz.degerlendir(p["id"], p["used"], p["resets_at"], p["pencere_sn"], ornekler,
                                 gozlem_yas_dk=p.get("gozlem_yas_dk"))
        if d:
            sonuc[p["id"]] = d
    if kaydet:
        kota_hiz.ornek_yaz(liste, ornekler=ornekler)
    return sonuc


def _oauth_hata_eki(resmi: dict) -> str:
    """`[oauth 2158dk bayat · 401]` — bayatlığın NEDENİ satırda durur.

    Yenileme sessizce düşerse okuyucu yalnız "bayat" görür ve nedenini aramak
    zorunda kalır; denetimde 36 saat böyle geçti (Astra A-borç 4).
    """
    hata = resmi.get("_oauth_hata")
    if not hata:
        return ""
    http = resmi.get("_oauth_http")
    return f" · {http}" if http else f" · {hata}"


OAUTH_COZUM = {
    "jeton-suresi-doldu": (
        "Claude Code'da /login ile yeniden oturum aç — erişim jetonunun süresi "
        "doldu, kota okuyucusu jeton yenilemez (yenileme CLI'nin işidir)."
    ),
    "kimlik-dosyasi-okunamadi": (
        "~/.claude/.credentials.json okunamadı ya da claudeAiOauth bloğu yok; "
        "Claude Code'da /login ile oturum aç."
    ),
}


def oauth_tani_satirlari(resmi: dict) -> list[str]:
    """``--detay`` için son yenileme denemesinin dökümü (boşsa boş liste)."""
    hata = resmi.get("_oauth_hata")
    if not hata:
        return []
    http = resmi.get("_oauth_http")
    deneme = resmi.get("_oauth_son_deneme") or "?"
    satirlar = [
        "  oauth yenileme: BAŞARISIZ · {}{} · son deneme {}".format(
            hata, f" · HTTP {http}" if http else "", deneme
        )
    ]
    cozum = OAUTH_COZUM.get(str(hata))
    if hata == "http-hatasi" and http == 401:
        cozum = OAUTH_COZUM["jeton-suresi-doldu"]
    if cozum:
        satirlar.append(f"  → çözüm: {cozum}")
    return satirlar


def tek_satir(codex: dict | None, claude: dict, resmi: dict | None = None,
              hiz: dict[str, dict] | None = None) -> str:
    if hiz is None:
        hiz = hizlar(codex, resmi)
    parcalar = []
    if codex:
        p = codex.get("primary") or {}
        s = codex.get("secondary") or {}
        parcalar.append(
            "Codex 5s %{:.0f}{} (reset {}) · hafta %{:.0f}{} (reset {}){}".format(
                p.get("used_percent") or 0, kota_hiz.kisa_metin(hiz.get("codex-5s")),
                _reset_metni(p.get("resets_at")),
                s.get("used_percent") or 0, kota_hiz.kisa_metin(hiz.get("codex-hafta")),
                _reset_metni(s.get("resets_at")),
                " ⚠kredi" if (codex.get("credits") or {}).get("has_credits") else "",
            )
        )
    else:
        parcalar.append("Codex: rollout verisi yok")
    if resmi is None:
        resmi = resmi_zinciri()
    if resmi:
        if resmi.get("_kaynak") == "oauth":
            bayat_ek = " bayat" if resmi.get("_bayat") else ""
            etiket = f"[oauth {resmi.get('_yas_dk', '?')}dk{bayat_ek}{_oauth_hata_eki(resmi)}]"
        else:
            etiket = f"[{resmi.get('_yas_dk', '?')}dk önce]"
        bes = resmi.get("five_hour") or {}
        hafta = resmi.get("seven_day") or {}
        satir = "Claude 5s %{:.0f}{} (reset {}) · hafta %{:.0f}{} (reset {})".format(
            bes.get("used_percentage") or 0, kota_hiz.kisa_metin(hiz.get("claude-5s")),
            _reset_metni(bes.get("resets_at")),
            hafta.get("used_percentage") or 0, kota_hiz.kisa_metin(hiz.get("claude-hafta")),
            _reset_metni(hafta.get("resets_at")),
        )
        for kalem in resmi.get("_kapsamli") or []:
            ad = str(kalem.get("ad") or "?")
            satir += " · {} %{:.0f}{} (reset {})".format(
                ad, kalem.get("used_percentage") or 0,
                kota_hiz.kisa_metin(hiz.get("claude-" + ad.lower())),
                _reset_metni(kalem.get("resets_at")),
            )
        if resmi.get("_asim"):
            satir += " ⚠aşım açık (önbellek 5dk)"
        parcalar.append(satir + " " + etiket)
    else:
        c5 = claude.get("5s", {})
        istek = sum(v["istek"] for v in c5.values())
        cikti = sum(v["cikti"] for v in c5.values())
        parcalar.append(
            f"Claude 5s: {istek} istek / ~{cikti//1000}k çıktı-jetonu (resmî % henüz düşmedi)"
        )
    yon = kota_hiz.yonetici(hiz.values())
    if yon:
        if yon["bant"] == kota_hiz.BANT_BILINMIYOR:
            # A8: bilinmiyor "serbest" diye okunamaz; nedeni de satırda durur.
            neden = "bayat" if yon.get("bayat") else (yon.get("not") or "doğrulanmadı")
            parcalar.append("bant: {} ({} {})".format(yon["bant"], yon["id"], neden))
        else:
            parcalar.append("bant: {} ({})".format(yon["bant"], yon["id"]))
    return "[kota] " + " | ".join(parcalar)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--detay", action="store_true")
    parser.add_argument("--hizli", action="store_true", help="yalnız 5s penceresi (kanca için)")
    args = parser.parse_args()
    codex = codex_resmi()
    claude = claude_harcama(hizli=args.hizli)
    resmi = resmi_zinciri()
    hiz = hizlar(codex, resmi)
    if args.json:
        print(json.dumps(
            {"codex": codex, "claude": claude, "claude_resmi": resmi, "hiz": hiz},
            ensure_ascii=False,
        ))
        return 0
    print(tek_satir(codex, claude, resmi, hiz))
    if args.detay:
        for pid, d in hiz.items():
            print(kota_hiz.detay_metni(d, pid))
        if codex:
            print(f"  codex kaynak: {codex.get('_kaynak')} ({codex.get('_dosya_zamani')})")
        for pencere in ("5s", "7g"):
            for model, v in sorted(claude.get(pencere, {}).items()):
                print(
                    f"  claude {pencere} {model}: {v['istek']} istek · "
                    f"girdi {v['girdi']:,} · çıktı {v['cikti']:,}"
                )
        if resmi:
            sonnet = resmi.get("_seven_day_sonnet")
            if sonnet:
                print(
                    "  claude hafta (Sonnet) %{:.0f} (reset {})".format(
                        sonnet.get("used_percentage") or 0,
                        _reset_metni(sonnet.get("resets_at")),
                    )
                )
            abone = resmi.get("_subscriptionType")
            oran = resmi.get("_rateLimitTier")
            if abone or oran:
                print(f"  claude abonelik: {abone or '?'} · oran katmanı: {oran or '?'}")
            for satir in oauth_tani_satirlari(resmi):
                print(satir)
            # Saat temposu dökümü — hafta bütçesi 168 saate bölünür (%0,60/saat).
            hafta = resmi.get("seven_day") or {}
            kalemler = [("hafta", hafta.get("used_percentage"), hafta.get("resets_at"))]
            for k in resmi.get("_kapsamli") or []:
                kalemler.append(
                    (f"hafta {k.get('ad') or '?'}", k.get("used_percentage"), k.get("resets_at"))
                )
            simdi_ts = dt.datetime.now(dt.timezone.utc).timestamp()
            for ad, yuzde, reset in kalemler:
                if yuzde is None or not reset:
                    continue
                gecen_s = (simdi_ts - (reset - GUN_7)) / 3600
                if gecen_s <= 0:
                    continue
                satir = (
                    f"  tempo {ad}: %{yuzde:.0f} / {gecen_s:.1f} saatte"
                    f" → saatte %{yuzde / gecen_s:.2f} (sürdürülebilir %0.60/saat)"
                ).replace(".", ",")
                print(f"{satir} · yenilenme {_reset_metni(reset)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
