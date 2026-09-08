#!/usr/bin/env python3
"""Kota okuyucu — Codex + Claude resmî yüzdeleri, HER ÇAĞRIDA CANLI, tek satır.

Master kararı 2026-09-08 (60. oturum): "sürekli sıfırdan bilgi çek, eski
bilgiyi okumak hata." Her iki kaynak da her çağrıda ağdan/süreçten taze
okunur; okuma başarısızsa SAYI BASILMAZ — satır "canlı okuma başarısız"
der ve bant `bilinmiyor` olur. Önbellek dosyaları yalnız TANI kaydıdır.

Kaynaklar:
  - Codex: `codex app-server` (stdio JSON-RPC) → `account/rateLimits/read`.
    Resmî uç, jeton harcamaz, ~1,6 s. `primary` = 5 saat (300 dk),
    `secondary` = hafta (10080 dk); ayrıca `credits`, `planType`,
    `rateLimitReachedType` ve `rateLimitResetCredits.availableCount`.
    Sıfırlama kredisi YALNIZ GÖSTERİLİR — betik `resetCredit/consume`
    ya da durum değiştiren başka bir çağrı ASLA yapmaz.
  - Claude: topluluk kaynaklı belgesiz uç
    https://api.anthropic.com/api/oauth/usage — Claude Code'un kendi
    /usage komutunu besleyen SUNUCU TARAFI veri. Kaynak: topluluk
    (github.com/ohugonnot/claude-code-statusline; anthropics/claude-code
    issue #31021, #45133). ~/.claude/.credentials.json içindeki
    accessToken ile Bearer + User-Agent: claude-code/<sürüm> +
    anthropic-beta: oauth-2025-04-20 başlıklarıyla GET edilir; yanlış/eksik
    başlık agresif 429 kovasına düşürür. Uç BELGESİZ ve her an
    kaldırılabilir/şekli değişebilir; ayrıştırıcı savunmacı yazıldı, HER
    hata (ağ/HTTP/JSON) yutulur ve satır sayı yerine hatayı basar.
    Jeton YENİLEME asla denenmez (yalnız erişim jetonu geçerliyse GET
    edilir); jetonun kendisi hiçbir zaman yazdırılmaz/önbelleklenmez.
    `.state\\claude-kota-oauth.json` son yanıt + son hata/HTTP kodunu
    saklar; beyin-doktor bunu okur, GÖSTERİM buradan BESLENMEZ.
  - `claude_harcama()`: ~/.claude/projects/**/*.jsonl usage blokları —
    yerel jeton dökümü, yalnız `--detay`/`--json` için; kota yüzdesi DEĞİL.
ToS-riskli yollar (ChatGPT token'ını belgesiz uca göndermek) bilinçli DIŞARIDA.

Tarihçe (kısa): 2026-08-29'da Codex yüzdesi rollout-*.jsonl dosyalarından,
Claude yüzdesi statusline önbelleğinden okunuyordu; 2026-09-06'da gözlem yaşı
(`gozlem`) eklendi, bayat kaynak "serbest" sayılmasın diye. 2026-09-07'de
rollout için reset kuralı (`KURAL_RESET`) getirildi. 2026-09-08'de üçü de
kaldırıldı: rollout 20:03'te durmuşken canlı uç %100 · rate_limit_reached
gösteriyordu. Artık tek kural `kota_hiz.KURAL_YAS`; gözlem zaten her zaman
"az önce"dir, `BEYIN_KOTA_BAYAT_DK` yalnız emniyet kemeridir.

Kullanım:  python kota.py            # tek satır (SessionStart enjeksiyonu için)
           python kota.py --detay    # çok satırlı döküm
           python kota.py --json     # makine okur
           python kota.py --hizli    # kanca yolu (iki kaynak paralel)
"""
from __future__ import annotations

import argparse
import concurrent.futures
import datetime as dt
import io
import shutil
import subprocess
import sys
import threading

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

CLAUDE_PROJECTS = Path.home() / ".claude" / "projects"
SAAT_5 = 5 * 3600
GUN_7 = 7 * 86400

# ---------------------------------------------------------------------------
# Codex: canlı app-server okuması (stdio JSON-RPC) — rollout taraması KALDIRILDI
# ---------------------------------------------------------------------------

CODEX_ZAMAN_ASIMI_SN = 6.0
# Son okuma denemesinin nedeni; satır sayı basamayınca burayı yazar.
CODEX_SON_HATA: str | None = None


def _codex_komut() -> list[str] | None:
    """`codex app-server`i başlatacak argv; bulunamazsa None.

    Windows'ta PATH'teki `codex` bir npm sarmalayıcısıdır (`.cmd`, ya da Git
    Bash'te uzantısız kabuk betiği); `Popen` onu doğrudan çalıştıramaz
    (WinError 2/193). Bu durumda yanındaki gerçek giriş noktası
    `node_modules/@openai/codex/bin/codex.js` `node` ile koşulur. Gerçek bir
    ikili (`.exe` / POSIX) ise doğrudan çalıştırılır.
    """
    yol = shutil.which("codex")
    if not yol:
        return None
    p = Path(yol)
    if p.suffix.lower() in ("", ".cmd", ".bat"):
        js = p.parent / "node_modules" / "@openai" / "codex" / "bin" / "codex.js"
        if js.exists():
            return ["node", str(js), "app-server"]
        if p.suffix.lower() != "":
            return None
    return [str(p), "app-server"]


def _codex_normalize(ham: dict, kredi: int | None) -> dict:
    """`result.rateLimits` → tek_satir()/pencereler()'in tükettiği ortak şekil."""
    def pencere(anahtar: str, vars_dk: int) -> dict:
        blok = ham.get(anahtar)
        if not isinstance(blok, dict):
            blok = {}
        return {
            "used_percent": blok.get("usedPercent"),
            "window_minutes": blok.get("windowDurationMins") or vars_dk,
            "resets_at": blok.get("resetsAt"),
        }

    krediler = ham.get("credits")
    if not isinstance(krediler, dict):
        krediler = {}
    simdi = int(dt.datetime.now(dt.timezone.utc).timestamp())
    return {
        "primary": pencere("primary", 300),
        "secondary": pencere("secondary", 10080),
        "credits": {
            "has_credits": bool(krediler.get("hasCredits")),
            "unlimited": bool(krediler.get("unlimited")),
            "balance": krediler.get("balance"),
        },
        "plan_type": ham.get("planType"),
        "rate_limit_reached_type": ham.get("rateLimitReachedType"),
        "_kaynak": "app-server",
        # Canlı okuma: gözlem anı = ŞİMDİ. Bayatlık burada yapısal olarak yok.
        "_gozlem": simdi,
        "_dosya_zamani": dt.datetime.fromtimestamp(simdi).isoformat(timespec="minutes"),
        # Yalnız GÖSTERİM: ücretsiz tam sıfırlama kredisi sayısı. Betik
        # `account/rateLimits/resetCredit/consume` çağrısını ASLA yapmaz.
        "_reset_kredisi": kredi,
    }


def _sessiz_oldur(surec) -> None:
    if surec is None:
        return
    try:
        surec.kill()
    except (OSError, ValueError):
        pass


def _codex_canli_ic() -> tuple[dict | None, str | None]:
    argv = _codex_komut()
    if not argv:
        return None, "codex bulunamadı"
    surec = None
    try:
        surec = subprocess.Popen(
            argv,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except (OSError, ValueError) as hata:
        return None, "süreç başlatılamadı:" + type(hata).__name__
    # `readline` bloklar; süreci zamanında öldüren bir bekçi olmazsa zaman
    # aşımı döngüsü hiç dönmez (kanca 8 s bütçesini yakar).
    bekci = threading.Timer(CODEX_ZAMAN_ASIMI_SN, _sessiz_oldur, args=(surec,))
    bekci.daemon = True
    bekci.start()
    try:
        istekler = [
            {"id": 1, "method": "initialize",
             "params": {"clientInfo": {"name": "beyin-kota", "version": "0.1"}}},
            {"method": "initialized"},
            {"id": 2, "method": "account/rateLimits/read", "params": None},
        ]
        try:
            surec.stdin.write("".join(json.dumps(i) + "\n" for i in istekler))
            surec.stdin.flush()
        except OSError as hata:
            return None, "yazılamadı:" + type(hata).__name__

        son = dt.datetime.now(dt.timezone.utc).timestamp() + CODEX_ZAMAN_ASIMI_SN
        yanit: dict | None = None
        while dt.datetime.now(dt.timezone.utc).timestamp() < son:
            satir = surec.stdout.readline()
            if not satir:
                break
            try:
                veri = json.loads(satir)
            except (json.JSONDecodeError, ValueError):
                continue  # bildirim/gürültü satırı
            if isinstance(veri, dict) and veri.get("id") == 2:
                yanit = veri
                break
        if yanit is None:
            return None, "yanıt yok (zaman aşımı %.0fs)" % CODEX_ZAMAN_ASIMI_SN
        if yanit.get("error"):
            kod = (yanit.get("error") or {}).get("code")
            return None, f"uç hata {kod}"
        sonuc = yanit.get("result")
        if not isinstance(sonuc, dict):
            return None, "yanıt çözülemedi"
        rl = sonuc.get("rateLimits")
        if not isinstance(rl, dict):
            return None, "rateLimits alanı yok (oturum açık mı?)"
        krediler = sonuc.get("rateLimitResetCredits")
        kredi = krediler.get("availableCount") if isinstance(krediler, dict) else None
        return _codex_normalize(rl, kredi if isinstance(kredi, int) else None), None
    finally:
        bekci.cancel()
        _sessiz_oldur(surec)


def codex_canli() -> dict | None:
    """Codex resmî yüzdeleri, app-server'dan CANLI. ASLA çökmez; hata → None.

    Başarısızlık nedeni ``CODEX_SON_HATA``'ya yazılır; satır sayı yerine onu
    basar (eski sayı asla gösterilmez — Master kuralı 2026-09-08).
    """
    global CODEX_SON_HATA
    try:
        veri, neden = _codex_canli_ic()
    except Exception as hata:  # pragma: no cover — savunma amaçlı
        veri, neden = None, "beklenmedik:" + type(hata).__name__
    CODEX_SON_HATA = neden
    return veri


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


# ---------------------------------------------------------------------------
# Claude: OAuth kullanım ucu (belgesiz, topluluk kaynaklı — bkz. docstring)
#
# 2026-09-08: statusline önbelleği katmanı (claude-kota.json, 6 saat tolerans)
# gösterim zincirinden ÇIKARILDI. `statusline_kota.py` o dosyayı yazmaya devam
# eder, kota.py artık okumaz.
# ---------------------------------------------------------------------------

CLAUDE_OAUTH_CACHE = Path(r"E:\OdenaOS\.claude\scripts\.state\claude-kota-oauth.json")
CLAUDE_CRED_PATH = Path.home() / ".claude" / ".credentials.json"
OAUTH_URL = "https://api.anthropic.com/api/oauth/usage"
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


def _oauth_atomik_yaz(ham: dict) -> None:
    try:
        CLAUDE_OAUTH_CACHE.parent.mkdir(parents=True, exist_ok=True)
        gecici = CLAUDE_OAUTH_CACHE.with_name(CLAUDE_OAUTH_CACHE.name + ".tmp")
        gecici.write_text(json.dumps(ham, ensure_ascii=False), encoding="utf-8")
        os.replace(gecici, CLAUDE_OAUTH_CACHE)
    except OSError:
        pass  # önbellek yazılamazsa sessiz geç — kritik değil


def _oauth_cache_yaz(veri: dict) -> None:
    """Son yanıtı TANI KAYDI olarak diske düşürür (gösterim kaynağı DEĞİL).

    Atomik: geçici dosya + os.replace. Yalnız SUNUCU YANITI durur — jeton asla.
    """
    _oauth_atomik_yaz({
        "yazilma": dt.datetime.now(dt.timezone.utc).isoformat(),
        "veri": veri,
        "son_deneme": dt.datetime.now(dt.timezone.utc).isoformat(),
        "son_hata": None,
        "http_status": 200,
    })


def _oauth_tani_yaz(hata: str, http_status: int | None = None) -> None:
    """Başarısız okuma denemesini tanı dosyasına iliştirir (Astra A-borç 4).

    ``yazilma`` ve ``veri`` KORUNUR — beyin-doktor son BAŞARILI yanıtı da
    görebilsin diye. Gösterim bu dosyadan beslenmediği için eski yüzdenin
    taze görünme riski artık yapısal olarak yok.
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
    """Her çağrıda AĞA çıkar; başarısızsa None döner (eski sayı ASLA dönmez)."""
    simdi = dt.datetime.now(dt.timezone.utc)
    token, bitis_ms, abone, oran_katmani = _kimlik_oku()

    def etiketle(sozluk: dict) -> dict:
        sozluk = dict(sozluk)
        sozluk["_kaynak"] = "oauth"
        sozluk["_yas_dk"] = 0
        # Canlı okuma: gözlem anı = yanıtın alındığı an (şimdi).
        sozluk["_gozlem"] = int(dt.datetime.now(dt.timezone.utc).timestamp())
        if abone:
            sozluk["_subscriptionType"] = abone
        if oran_katmani:
            sozluk["_rateLimitTier"] = oran_katmani
        return sozluk

    if not token or not bitis_ms:
        _oauth_tani_yaz("kimlik-dosyasi-okunamadi")
        return None
    if bitis_ms <= simdi.timestamp() * 1000:
        # Erişim jetonu süresi dolmuş — YENİLEME DENENMEZ (jeton yenilemek
        # CLI'nin işidir, kota okuyucusunun değil). 2026-09-06 teşhisi: uç
        # gerçekten 401 "OAuth access token has expired" veriyor; çözüm
        # sahibin Claude Code'da yeniden oturum açmasıdır.
        _oauth_tani_yaz("jeton-suresi-doldu", 401)
        return None

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
        return None
    except (urllib.error.URLError, TimeoutError, OSError) as hata:
        _oauth_tani_yaz("ag-hatasi:" + type(hata).__name__, yanit_kodu)
        return None
    except (json.JSONDecodeError, ValueError) as hata:
        _oauth_tani_yaz("yanit-cozulemedi:" + type(hata).__name__, yanit_kodu)
        return None

    _oauth_cache_yaz(yeni_veri)
    return etiketle(_oauth_normalize(yeni_veri))


def resmi_zinciri() -> dict | None:
    """Claude resmî %: yalnız CANLI OAuth okuması. Zincir/yedek katman yok."""
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
                          "gozlem_yas_dk": _yas_dk(codex_gozlem, simdi),
                          # Artık Codex de YOKLANAN bir kaynak (app-server her
                          # çağrıda canlı okunur) → tek kural: yaş.
                          "bayat_kurali": kota_hiz.KURAL_YAS})
    if resmi:
        gozlem = resmi.get("_gozlem")
        yas = _yas_dk(gozlem, simdi)
        if yas is None and isinstance(resmi.get("_yas_dk"), int):
            yas = resmi["_yas_dk"]
        bes = resmi.get("five_hour") or {}
        hafta = resmi.get("seven_day") or {}
        # Canlı okuma → yaş ~0; yaş kuralı yalnız emniyet kemeri olarak durur.
        liste.append({"id": "claude-5s", "used": bes.get("used_percentage"),
                      "resets_at": bes.get("resets_at"), "pencere_sn": SAAT_5,
                      "gozlem": gozlem, "gozlem_yas_dk": yas,
                      "bayat_kurali": kota_hiz.KURAL_YAS})
        liste.append({"id": "claude-hafta", "used": hafta.get("used_percentage"),
                      "resets_at": hafta.get("resets_at"), "pencere_sn": GUN_7,
                      "gozlem": gozlem, "gozlem_yas_dk": yas,
                      "bayat_kurali": kota_hiz.KURAL_YAS})
        for k in resmi.get("_kapsamli") or []:
            liste.append({"id": "claude-" + str(k.get("ad") or "?").lower(),
                          "used": k.get("used_percentage"), "resets_at": k.get("resets_at"),
                          "pencere_sn": GUN_7, "gozlem": gozlem, "gozlem_yas_dk": yas,
                          "bayat_kurali": kota_hiz.KURAL_YAS})
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
                                 gozlem_yas_dk=p.get("gozlem_yas_dk"),
                                 bayat_kurali=p.get("bayat_kurali", kota_hiz.KURAL_YAS))
        if d:
            sonuc[p["id"]] = d
    if kaydet:
        kota_hiz.ornek_yaz(liste, ornekler=ornekler)
    return sonuc


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


OAUTH_HATA_KISA = {
    "jeton-suresi-doldu": "401 → /login",
    "kimlik-dosyasi-okunamadi": "kimlik yok → /login",
}


def _canli_etiket(gozlem: int | None) -> str:
    """`[canlı 23:07]` — gözlem saati, YEREL. Canlı okumanın imzası."""
    if not gozlem:
        return "[canlı ?]"
    return "[canlı " + dt.datetime.fromtimestamp(int(gozlem)).strftime("%H:%M") + "]"


def claude_hata_metni() -> str:
    """Canlı OAuth okuması düşünce satıra girecek NEDEN (sayı yerine)."""
    tani = _oauth_tani_oku()
    hata = tani.get("son_hata")
    http = tani.get("http_status")
    if not hata:
        return "neden bilinmiyor"
    kisa = OAUTH_HATA_KISA.get(str(hata))
    if kisa:
        return kisa
    if hata == "http-hatasi":
        if http == 401:
            return "401 → /login"
        if http == 429:
            return "429 (uç kısıtladı)"
        return f"HTTP {http}" if http else "http-hatasi"
    if str(hata).startswith("ag-hatasi"):
        return "ağ"
    return str(hata)


def oauth_tani_satirlari(resmi: dict | None = None) -> list[str]:
    """``--detay`` için son okuma denemesinin dökümü (boşsa boş liste).

    ``resmi`` verilmezse tanı dosyasından okunur — canlı okuma düştüğünde
    elde bir ``resmi`` sözlüğü kalmadığı için varsayılan yol budur.
    """
    if resmi is None:
        tani = _oauth_tani_oku()
        resmi = {"_oauth_hata": tani.get("son_hata"),
                 "_oauth_http": tani.get("http_status"),
                 "_oauth_son_deneme": tani.get("son_deneme")}
    hata = resmi.get("_oauth_hata")
    if not hata:
        return []
    http = resmi.get("_oauth_http")
    deneme = resmi.get("_oauth_son_deneme") or "?"
    satirlar = [
        "  oauth okuma: BAŞARISIZ · {}{} · son deneme {}".format(
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
              hiz: dict[str, dict] | None = None, codex_hata: str | None = None) -> str:
    """Tek satırlık özet. Canlı okunamayan kaynak SAYI BASMAZ, nedeni basar."""
    if hiz is None:
        hiz = hizlar(codex, resmi)
    parcalar = []
    if codex:
        p = codex.get("primary") or {}
        s = codex.get("secondary") or {}
        isaretler = ""
        if (codex.get("credits") or {}).get("has_credits"):
            isaretler += " ⚠kredi"
        if codex.get("rate_limit_reached_type"):
            isaretler += " ⚠limit doldu"
        kredi = codex.get("_reset_kredisi")
        if isinstance(kredi, int) and kredi > 0:
            # Yalnız bilgi: krediyi harcamak Master'ın kararı, betiğin değil.
            isaretler += f" (sıfırlama kredisi: {kredi})"
        parcalar.append(
            "Codex 5s %{:.0f}{} (reset {}) · hafta %{:.0f}{} (reset {}){} {}".format(
                p.get("used_percent") or 0, kota_hiz.kisa_metin(hiz.get("codex-5s")),
                _reset_metni(p.get("resets_at")),
                s.get("used_percent") or 0, kota_hiz.kisa_metin(hiz.get("codex-hafta")),
                _reset_metni(s.get("resets_at")),
                isaretler,
                _canli_etiket(codex.get("_gozlem")),
            )
        )
    else:
        neden = codex_hata or CODEX_SON_HATA or "neden bilinmiyor"
        parcalar.append(f"Codex: canlı okuma başarısız ({neden})")
    if resmi:
        etiket = _canli_etiket(resmi.get("_gozlem"))
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
        parcalar.append(f"Claude: canlı okuma başarısız ({claude_hata_metni()})")
    yon = kota_hiz.yonetici(hiz.values())
    if not yon:
        parcalar.append(f"bant: {kota_hiz.BANT_BILINMIYOR} (kaynak yok)")
    else:
        if yon["bant"] == kota_hiz.BANT_BILINMIYOR:
            # A8: bilinmiyor "serbest" diye okunamaz; nedeni de satırda durur.
            neden = "bayat" if yon.get("bayat") else (yon.get("not") or "doğrulanmadı")
            parcalar.append("bant: {} ({} {})".format(yon["bant"], yon["id"], neden))
        else:
            parcalar.append("bant: {} ({})".format(yon["bant"], yon["id"]))
    return "[kota] " + " | ".join(parcalar)


def kaynak_satiri(codex: dict | None, resmi: dict | None) -> str:
    """``--detay`` başındaki kaynak künyesi — her okumanın nereden geldiği."""
    if resmi:
        c = "claude oauth canlı " + _canli_etiket(resmi.get("_gozlem"))[7:-1]
    else:
        c = f"claude oauth BAŞARISIZ ({claude_hata_metni()})"
    if codex:
        k = "codex app-server canlı " + _canli_etiket(codex.get("_gozlem"))[7:-1]
    else:
        k = f"codex app-server BAŞARISIZ ({CODEX_SON_HATA or 'neden bilinmiyor'})"
    return f"{c} · {k}"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--detay", action="store_true")
    parser.add_argument("--hizli", action="store_true", help="yalnız 5s penceresi (kanca için)")
    args = parser.parse_args()
    # İki canlı okuma PARALEL: app-server ~1,6 s + OAuth ~1 s ardışık koşarsa
    # session-start.ps1'in 8 sn'lik job bütçesi daralır (kanca dosyası
    # DEĞİŞMEZ, uyum bu tarafta sağlanır).
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as havuz:
        codex_isi = havuz.submit(codex_canli)
        claude_isi = havuz.submit(resmi_zinciri)
        codex = codex_isi.result()
        resmi = claude_isi.result()
    # Yerel jeton dökümü yalnız döküm yollarında; kota yüzdesi buradan gelmez.
    claude = claude_harcama(hizli=args.hizli) if (args.detay or args.json) else {"5s": {}, "7g": {}}
    hiz = hizlar(codex, resmi)
    if args.json:
        print(json.dumps(
            {"codex": codex, "claude": claude, "claude_resmi": resmi, "hiz": hiz},
            ensure_ascii=False,
        ))
        return 0
    print(tek_satir(codex, claude, resmi, hiz))
    if args.detay:
        print("  kaynak: " + kaynak_satiri(codex, resmi))
        for pid, d in hiz.items():
            print(kota_hiz.detay_metni(d, pid))
        if codex and codex.get("plan_type"):
            print(f"  codex plan: {codex['plan_type']}")
        if not resmi:
            for satir in oauth_tani_satirlari():
                print(satir)
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
