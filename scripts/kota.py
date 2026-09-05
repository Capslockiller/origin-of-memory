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
            son["_kaynak"] = dosya.name
            son["_dosya_zamani"] = dt.datetime.fromtimestamp(
                dosya.stat().st_mtime
            ).isoformat(timespec="minutes")
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


def _oauth_cache_oku() -> tuple[dt.datetime, dict] | None:
    try:
        ham = json.loads(CLAUDE_OAUTH_CACHE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
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


def _oauth_cache_yaz(veri: dict) -> None:
    """Atomik yaz: geçici dosya + os.replace. Yalnız SUNUCU YANITI durur — jeton asla."""
    try:
        CLAUDE_OAUTH_CACHE.parent.mkdir(parents=True, exist_ok=True)
        gecici = CLAUDE_OAUTH_CACHE.with_name(CLAUDE_OAUTH_CACHE.name + ".tmp")
        icerik = json.dumps(
            {"yazilma": dt.datetime.now(dt.timezone.utc).isoformat(), "veri": veri},
            ensure_ascii=False,
        )
        gecici.write_text(icerik, encoding="utf-8")
        os.replace(gecici, CLAUDE_OAUTH_CACHE)
    except OSError:
        pass  # önbellek yazılamazsa sessiz geç — kritik değil


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
        if abone:
            sozluk["_subscriptionType"] = abone
        if oran_katmani:
            sozluk["_rateLimitTier"] = oran_katmani
        if bayat:
            sozluk["_bayat"] = True
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
        return bayat_donus()
    if bitis_ms <= simdi.timestamp() * 1000:
        return bayat_donus()  # erişim jetonu süresi dolmuş — YENİLEME DENENMEZ

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
            govde = yanit.read().decode("utf-8", errors="replace")
        yeni_veri = json.loads(govde)
    except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError,
            json.JSONDecodeError, OSError, ValueError):
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


def pencereler(codex: dict | None, resmi: dict | None) -> list[dict]:
    """Hız katmanının okuduğu ortak pencere listesi: id · used · resets_at · pencere_sn."""
    liste = []
    if codex:
        for pid, blok, vars_dk in (("codex-5s", codex.get("primary"), 300),
                                   ("codex-hafta", codex.get("secondary"), 10080)):
            blok = blok or {}
            liste.append({"id": pid, "used": blok.get("used_percent"), "resets_at": blok.get("resets_at"),
                          "pencere_sn": int(blok.get("window_minutes") or vars_dk) * 60})
    if resmi:
        bes = resmi.get("five_hour") or {}
        hafta = resmi.get("seven_day") or {}
        liste.append({"id": "claude-5s", "used": bes.get("used_percentage"),
                      "resets_at": bes.get("resets_at"), "pencere_sn": SAAT_5})
        liste.append({"id": "claude-hafta", "used": hafta.get("used_percentage"),
                      "resets_at": hafta.get("resets_at"), "pencere_sn": GUN_7})
        for k in resmi.get("_kapsamli") or []:
            liste.append({"id": "claude-" + str(k.get("ad") or "?").lower(),
                          "used": k.get("used_percentage"), "resets_at": k.get("resets_at"),
                          "pencere_sn": GUN_7})
    return liste


def hizlar(codex: dict | None, resmi: dict | None, kaydet: bool = True) -> dict[str, dict]:
    """Her pencere için kota_hiz.degerlendir; kaydet=True ise örneklem defterine yazar."""
    liste = pencereler(codex, resmi)
    ornekler = kota_hiz.ornek_oku()
    sonuc = {}
    for p in liste:
        d = kota_hiz.degerlendir(p["id"], p["used"], p["resets_at"], p["pencere_sn"], ornekler)
        if d:
            sonuc[p["id"]] = d
    if kaydet:
        kota_hiz.ornek_yaz(liste)
    return sonuc


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
            etiket = f"[oauth {resmi.get('_yas_dk', '?')}dk{bayat_ek}]"
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
