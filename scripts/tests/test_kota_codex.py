"""Codex canlı kota okuması — `codex app-server` stdio JSON-RPC (2026-09-08).

Master kuralı (60. oturum): "sürekli sıfırdan bilgi çek, eski bilgiyi okumak
hata." Codex yüzdesi artık rollout dosyalarından değil, resmî uçtan okunuyor:
`initialize` → `initialized` → `account/rateLimits/read`. Fixture, 8 Eylül
22:46'da ölçülen GERÇEK yanıttır (5s %100 · rate_limit_reached, hafta %16,
ücretsiz sıfırlama kredisi 1).

Hiçbir test gerçek `codex` sürecini başlatmaz; `subprocess.Popen` sahte bir
süreçle değiştirilir. Betiğin durum değiştiren bir çağrı (`resetCredit/
consume`) yapmadığı da burada bekçiye bağlanır.

yazan: claude
model: opus-5
"""

from __future__ import annotations

import json
import sys
import unittest
import unittest.mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

# kota.py içe aktarılırken sys.stdout'u UTF-8'e sarıyor; pytest'in yakalama
# borusunu kapatmasın diye akış korunur ve sarmalayıcı detach edilir.
_stdout = sys.stdout
try:
    import kota
finally:
    _sarmalayici = sys.stdout
    sys.stdout = _stdout
    if _sarmalayici is not _stdout:
        try:
            _sarmalayici.detach()
        except Exception:  # pragma: no cover — savunma amaçlı
            pass


# 2026-09-08 22:46 canlı sonda çıktısı (kısaltılmadan gerekli alanlar).
SONDA_YANITI = {
    "id": 2,
    "result": {
        "rateLimits": {
            "limitId": "codex",
            "limitName": None,
            "primary": {"usedPercent": 100, "windowDurationMins": 300,
                        "resetsAt": 1788898323},
            "secondary": {"usedPercent": 16, "windowDurationMins": 10080,
                          "resetsAt": 1789485123},
            "credits": {"hasCredits": False, "unlimited": False, "balance": "0"},
            "individualLimit": None,
            "spendControlReached": False,
            "planType": "plus",
            "rateLimitReachedType": "rate_limit_reached",
        },
        "rateLimitResetCredits": {"availableCount": 1},
        "accountId": "d498a291-ca65-4a4b-93e5-8d6eb4ed98c5",
    },
}

# app-server gerçek hayatta önce bildirim satırları basar; ayrıştırıcı
# `id == 2` gelene kadar bunları atlamalı.
GURULTU = [
    '{"method":"remoteControl/status/changed","params":{"status":"idle"}}\n',
    "not-json\n",
    '{"id":1,"result":{"userAgent":"codex"}}\n',
]


class _SahteAkis:
    def __init__(self, satirlar: list[str]) -> None:
        self._satirlar = list(satirlar)

    def readline(self) -> str:
        return self._satirlar.pop(0) if self._satirlar else ""


class _SahteGirdi:
    def __init__(self) -> None:
        self.yazilan = ""

    def write(self, metin: str) -> None:
        self.yazilan += metin

    def flush(self) -> None:
        pass


class _SahteSurec:
    def __init__(self, satirlar: list[str]) -> None:
        self.stdin = _SahteGirdi()
        self.stdout = _SahteAkis(satirlar)
        self.oldurulen = False

    def kill(self) -> None:
        self.oldurulen = True


class KomutCozumlemeTests(unittest.TestCase):
    """Windows'ta PATH'teki `codex` npm sarmalayıcısıdır — `node` ile koşulur."""

    def _which(self, yol: str):
        return unittest.mock.patch.object(kota.shutil, "which", lambda _a: yol)

    def test_cmd_sarmalayici_node_ile_kosulur(self) -> None:
        with self._which(r"C:\npm\codex.cmd"), \
                unittest.mock.patch.object(kota.Path, "exists", lambda _s: True):
            argv = kota._codex_komut()

        self.assertEqual(argv[0], "node")
        self.assertTrue(argv[1].endswith("codex.js"))
        self.assertIn("@openai", argv[1])
        self.assertEqual(argv[-1], "app-server")

    def test_gercek_ikili_dogrudan_kosulur(self) -> None:
        with self._which(r"C:\bin\codex.exe"):
            self.assertEqual(kota._codex_komut(), [r"C:\bin\codex.exe", "app-server"])

    def test_sarmalayici_var_giris_noktasi_yoksa_none(self) -> None:
        with self._which(r"C:\npm\codex.cmd"), \
                unittest.mock.patch.object(kota.Path, "exists", lambda _s: False):
            self.assertIsNone(kota._codex_komut())

    def test_codex_path_te_yoksa_none(self) -> None:
        with self._which(None):
            self.assertIsNone(kota._codex_komut())


class CanliOkumaTests(unittest.TestCase):
    def setUp(self) -> None:
        kota.CODEX_SON_HATA = None

    def _kos(self, satirlar: list[str]) -> tuple[dict | None, _SahteSurec]:
        surec = _SahteSurec(satirlar)
        with unittest.mock.patch.object(kota, "_codex_komut",
                                        lambda: ["node", "codex.js", "app-server"]), \
                unittest.mock.patch.object(kota.subprocess, "Popen",
                                           lambda *_a, **_k: surec):
            return kota.codex_canli(), surec

    def test_sonda_yaniti_normalize_edilir(self) -> None:
        veri, _ = self._kos(GURULTU + [json.dumps(SONDA_YANITI) + "\n"])

        self.assertEqual(veri["primary"], {"used_percent": 100, "window_minutes": 300,
                                           "resets_at": 1788898323})
        self.assertEqual(veri["secondary"], {"used_percent": 16, "window_minutes": 10080,
                                             "resets_at": 1789485123})
        self.assertEqual(veri["credits"],
                         {"has_credits": False, "unlimited": False, "balance": "0"})
        self.assertEqual(veri["plan_type"], "plus")
        self.assertEqual(veri["rate_limit_reached_type"], "rate_limit_reached")
        self.assertEqual(veri["_reset_kredisi"], 1)
        self.assertEqual(veri["_kaynak"], "app-server")
        self.assertIsNone(kota.CODEX_SON_HATA)

    def test_gozlem_ani_simdi_olur(self) -> None:
        """Canlı okumada gözlem yaşı yapısal olarak ~0'dır."""
        import datetime as dt

        veri, _ = self._kos([json.dumps(SONDA_YANITI) + "\n"])
        simdi = dt.datetime.now(dt.timezone.utc).timestamp()

        self.assertLess(abs(veri["_gozlem"] - simdi), 5)

    def test_el_sikisma_dizisi_dogru(self) -> None:
        _, surec = self._kos([json.dumps(SONDA_YANITI) + "\n"])
        istekler = [json.loads(s) for s in surec.stdin.yazilan.splitlines()]

        self.assertEqual([i.get("method") for i in istekler],
                         ["initialize", "initialized", "account/rateLimits/read"])
        self.assertEqual(istekler[0]["params"]["clientInfo"]["name"], "beyin-kota")
        self.assertEqual(istekler[2]["id"], 2)
        self.assertIsNone(istekler[2]["params"])

    def test_durum_degistiren_cagri_yapilmaz(self) -> None:
        """Ücretsiz sıfırlama kredisi YALNIZ gösterilir, asla tüketilmez."""
        _, surec = self._kos([json.dumps(SONDA_YANITI) + "\n"])

        self.assertNotIn("consume", surec.stdin.yazilan)
        self.assertNotIn("resetCredit", surec.stdin.yazilan)

    def test_surec_her_halukarda_oldurulur(self) -> None:
        _, surec = self._kos([json.dumps(SONDA_YANITI) + "\n"])

        self.assertTrue(surec.oldurulen)

    def test_yanit_gelmezse_none_ve_neden(self) -> None:
        veri, surec = self._kos(GURULTU)  # id==2 hiç gelmiyor, akış biter

        self.assertIsNone(veri)
        self.assertIn("yanıt yok", kota.CODEX_SON_HATA)
        self.assertTrue(surec.oldurulen)

    def test_uc_hatasi_none_verir(self) -> None:
        veri, _ = self._kos([json.dumps({"id": 2, "error": {"code": -32601}}) + "\n"])

        self.assertIsNone(veri)
        self.assertEqual(kota.CODEX_SON_HATA, "uç hata -32601")

    def test_oturum_kapaliysa_none_verir(self) -> None:
        veri, _ = self._kos([json.dumps({"id": 2, "result": {}}) + "\n"])

        self.assertIsNone(veri)
        self.assertIn("rateLimits alanı yok", kota.CODEX_SON_HATA)

    def test_codex_yoksa_none_ve_neden(self) -> None:
        with unittest.mock.patch.object(kota, "_codex_komut", lambda: None):
            veri = kota.codex_canli()

        self.assertIsNone(veri)
        self.assertEqual(kota.CODEX_SON_HATA, "codex bulunamadı")

    def test_surec_baslatilamazsa_none(self) -> None:
        def dusen(*_a, **_k):
            raise OSError(2, "No such file")

        with unittest.mock.patch.object(kota, "_codex_komut",
                                        lambda: ["codex.cmd", "app-server"]), \
                unittest.mock.patch.object(kota.subprocess, "Popen", dusen):
            veri = kota.codex_canli()

        self.assertIsNone(veri)
        self.assertIn("süreç başlatılamadı", kota.CODEX_SON_HATA)


class SatirTests(unittest.TestCase):
    """Sonda verisi satıra dönünce: %100 · ⚠limit doldu · kredi bilgisi."""

    def test_canli_veri_satiri(self) -> None:
        veri = kota._codex_normalize(SONDA_YANITI["result"]["rateLimits"], 1)

        satir = kota.tek_satir(veri, {"5s": {}, "7g": {}}, None, {})

        self.assertIn("Codex 5s %100", satir)
        self.assertIn("hafta %16", satir)
        self.assertIn("⚠limit doldu", satir)
        self.assertIn("(sıfırlama kredisi: 1)", satir)
        self.assertIn("[canlı ", satir)

    def test_kaynak_satiri_iki_kaynagi_da_soyler(self) -> None:
        veri = kota._codex_normalize(SONDA_YANITI["result"]["rateLimits"], 1)
        kota.CODEX_SON_HATA = None

        metin = kota.kaynak_satiri(veri, None)

        self.assertIn("codex app-server canlı", metin)
        self.assertIn("claude oauth BAŞARISIZ", metin)


if __name__ == "__main__":
    unittest.main()
