"""Bayat kota kaynağının "serbest" sayılmaması (Astra A8, 2026-09-06).

Denetimde bulunan hata: 2026-09-05 08:26'da donmuş OAuth önbelleği ertesi gün
boyunca aynı yüzdeleri verdi; her okuma örneklem defterine YENİ zaman
damgasıyla yazıldığı için Δused = 0 çıktı ve yönetici bant "R 0,0 · serbest"
oldu. Ayrıca reset'i geçmiş pencere, gözlem taze olsun olmasın, koşulsuz
"serbest" dönüyordu — %99 dolu bir pencere bile.

Saatler her testte sabitlenir; hiçbir test canlı kasaya ya da ağa dokunmaz.

yazan: claude
model: opus-5
"""

from __future__ import annotations

import json
from pathlib import Path
import sys
import tempfile
import unittest

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

# kota.py içe aktarılırken sys.stdout'u UTF-8'e sabitliyor (Git Bash cp1254
# çakılması için); pytest'in yakalama borusunu sarınca oturum sonunda
# "closed file" hatası veriyor. Akış içe aktarma boyunca korunur.
# Sarmalayıcı ayrıca `detach` edilir: yoksa çöp toplandığında pytest'in
# yakalama dosyasını da KAPATIYOR ("I/O operation on closed file").
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
import kota_hiz


SIMDI = 1_788_800_000.0  # sabit "şimdi" (epoch saniye)
SAAT = 3600


class BayatBantTests(unittest.TestCase):
    """``degerlendir`` gözlem yaşını bantla birlikte okur."""

    def test_taze_gozlem_normal_bant_verir(self) -> None:
        d = kota_hiz.degerlendir(
            "claude-5s", 36.0, int(SIMDI + 3 * SAAT), 5 * SAAT,
            ornekler=[], simdi=SIMDI, gozlem_yas_dk=4,
        )

        self.assertNotEqual(d["bant"], kota_hiz.BANT_BILINMIYOR)
        self.assertFalse(d["bayat"])

    def test_bayat_gozlem_bilinmiyor_verir(self) -> None:
        d = kota_hiz.degerlendir(
            "claude-5s", 36.0, int(SIMDI + 3 * SAAT), 5 * SAAT,
            ornekler=[], simdi=SIMDI, gozlem_yas_dk=2050,
        )

        self.assertEqual(d["bant"], kota_hiz.BANT_BILINMIYOR)
        self.assertTrue(d["bayat"])
        self.assertIsNone(d["R"])
        self.assertEqual(kota_hiz.kisa_metin(d), " [? bayat 2050dk]")

    def test_esik_ortam_degiskeniyle_gevsetilebilir(self) -> None:
        import os

        os.environ["BEYIN_KOTA_BAYAT_DK"] = "3000"
        try:
            self.assertEqual(kota_hiz.bayat_esik_dk(), 3000)
            d = kota_hiz.degerlendir(
                "claude-5s", 36.0, int(SIMDI + 3 * SAAT), 5 * SAAT,
                ornekler=[], simdi=SIMDI, gozlem_yas_dk=2050,
            )
            self.assertNotEqual(d["bant"], kota_hiz.BANT_BILINMIYOR)
        finally:
            del os.environ["BEYIN_KOTA_BAYAT_DK"]

    def test_gecmis_reset_taze_gozlem_yoksa_bilinmiyor(self) -> None:
        """Denetimin bellek-içi örneği: %99 dolu + reset geçmiş."""
        d = kota_hiz.degerlendir(
            "codex-5s", 99.0, int(SIMDI - 60), 5 * SAAT,
            ornekler=[], simdi=SIMDI, gozlem_yas_dk=None,
        )

        self.assertEqual(d["bant"], kota_hiz.BANT_BILINMIYOR)
        self.assertNotEqual(d["bant"], "serbest")

    def test_gecmis_reset_taze_gozlemle_serbest_kalir(self) -> None:
        d = kota_hiz.degerlendir(
            "codex-5s", 99.0, int(SIMDI - 60), 5 * SAAT,
            ornekler=[], simdi=SIMDI, gozlem_yas_dk=2,
        )

        self.assertEqual(d["bant"], "serbest")
        self.assertEqual(d["not"], "reset geçti")


class YoneticiBantTests(unittest.TestCase):
    """``bilinmiyor`` yönetimi kapalı/karne pencereye bırakır, serbeste bırakmaz."""

    def _d(self, pid: str, bant: str, R: float | None) -> dict:
        return {"id": pid, "bant": bant, "R": R, "bayat": bant == kota_hiz.BANT_BILINMIYOR}

    def test_bilinmiyor_serbesti_yener(self) -> None:
        yon = kota_hiz.yonetici([
            self._d("codex-hafta", "serbest", 0.2),
            self._d("claude-5s", kota_hiz.BANT_BILINMIYOR, None),
        ])

        self.assertEqual(yon["bant"], kota_hiz.BANT_BILINMIYOR)
        self.assertEqual(yon["id"], "claude-5s")

    def test_kapali_bilinmiyoru_yener(self) -> None:
        yon = kota_hiz.yonetici([
            self._d("claude-5s", kota_hiz.BANT_BILINMIYOR, None),
            self._d("codex-5s", "kapalı", 3.4),
        ])

        self.assertEqual(yon["bant"], "kapalı")
        self.assertEqual(yon["id"], "codex-5s")

    def test_karne_bilinmiyoru_yener(self) -> None:
        yon = kota_hiz.yonetici([
            self._d("claude-5s", kota_hiz.BANT_BILINMIYOR, None),
            self._d("codex-5s", "karne", 1.6),
        ])

        self.assertEqual(yon["bant"], "karne")

    def test_dikkat_bilinmiyora_yenilir(self) -> None:
        yon = kota_hiz.yonetici([
            self._d("claude-5s", kota_hiz.BANT_BILINMIYOR, None),
            self._d("codex-5s", "dikkat", 1.1),
        ])

        self.assertEqual(yon["bant"], kota_hiz.BANT_BILINMIYOR)


class OrneklemTekrarTests(unittest.TestCase):
    """Değişmeyen gözlem yeni örnek üretmez — sahte yanma hızının kaynağı buydu."""

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.yol = Path(self.tmp.name) / "kota-orneklem.jsonl"
        self.addCleanup(self.tmp.cleanup)

    def _pencere(self, used: float, gozlem: int) -> dict:
        return {"id": "claude-5s", "used": used, "resets_at": int(SIMDI + 3 * SAAT),
                "gozlem": gozlem, "gozlem_yas_dk": 0}

    def test_ayni_gozlem_ikinci_kez_yazilmaz(self) -> None:
        gozlem = int(SIMDI - 30 * 60)
        yazilan = kota_hiz.ornek_yaz([self._pencere(36.0, gozlem)], yol=self.yol, simdi=SIMDI)
        self.assertEqual(yazilan, 1)

        # Bir saat sonra aynı donmuş önbellek yeniden okunur.
        tekrar = kota_hiz.ornek_yaz(
            [self._pencere(36.0, gozlem)], yol=self.yol, simdi=SIMDI + SAAT
        )

        self.assertEqual(tekrar, 0)
        self.assertEqual(len(self.yol.read_text(encoding="utf-8").splitlines()), 1)

    def test_gozlem_tazelenince_yeni_satir_yazilir(self) -> None:
        kota_hiz.ornek_yaz([self._pencere(36.0, int(SIMDI - 30 * 60))], yol=self.yol, simdi=SIMDI)
        kota_hiz.ornek_yaz(
            [self._pencere(41.0, int(SIMDI + SAAT - 60))], yol=self.yol, simdi=SIMDI + SAAT
        )

        self.assertEqual(len(self.yol.read_text(encoding="utf-8").splitlines()), 2)

    def test_satir_bicimi_geriye_donuk_uyumlu(self) -> None:
        kota_hiz.ornek_yaz([self._pencere(36.0, int(SIMDI - 60))], yol=self.yol, simdi=SIMDI)
        kayit = json.loads(self.yol.read_text(encoding="utf-8").splitlines()[0])

        self.assertEqual(
            sorted(kayit), ["gozlem", "id", "resets_at", "ts", "used"]
        )  # eski dört alan yerinde, yalnız `gozlem` EKLENDİ

    def test_gozlemsiz_eski_satir_temel_olarak_okunur(self) -> None:
        """``gozlem`` alanı olmayan eski satırlar hâlâ yanma hesabına girer."""
        self.yol.write_text(
            json.dumps({"ts": int(SIMDI - 2 * SAAT), "id": "claude-5s", "used": 20.0,
                        "resets_at": int(SIMDI + 3 * SAAT)}) + "\n",
            encoding="utf-8",
        )
        ornekler = kota_hiz.ornek_oku(self.yol)

        hiz = kota_hiz.yanma_hizi("claude-5s", 30.0, int(SIMDI + 3 * SAAT), ornekler,
                                  SIMDI, ufuk_saat=4.0, en_az_dk=15)

        self.assertAlmostEqual(hiz, 5.0)  # 2 saatte %10

    def test_bayat_kaynakta_R_olculmez(self) -> None:
        """Tekrar yazılmadığı için Δt gerçek kalır; bant zaten bilinmiyor olur."""
        gozlem = int(SIMDI - 2050 * 60)
        kota_hiz.ornek_yaz([self._pencere(36.0, gozlem)], yol=self.yol, simdi=SIMDI - SAAT)
        kota_hiz.ornek_yaz([self._pencere(36.0, gozlem)], yol=self.yol, simdi=SIMDI)

        d = kota_hiz.degerlendir(
            "claude-5s", 36.0, int(SIMDI + 3 * SAAT), 5 * SAAT,
            ornekler=kota_hiz.ornek_oku(self.yol), simdi=SIMDI, gozlem_yas_dk=2050,
        )

        self.assertIsNone(d["R"])
        self.assertEqual(kota_hiz.kisa_metin(d), " [? bayat 2050dk]")


class KotaSatirTests(unittest.TestCase):
    """``kota.pencereler`` gözlemi taşır, ``tek_satir`` nedeni yazar."""

    def test_pencereler_gozlem_yasini_tasir(self) -> None:
        resmi = {
            "five_hour": {"used_percentage": 36.0, "resets_at": int(SIMDI - 100)},
            "seven_day": {"used_percentage": 7.0, "resets_at": int(SIMDI + 6 * 86400)},
            "_kapsamli": [{"ad": "Fable", "used_percentage": 12.0,
                           "resets_at": int(SIMDI + 6 * 86400)}],
            "_kaynak": "oauth",
            "_gozlem": int(SIMDI - 2050 * 60),
        }

        liste = kota.pencereler(None, resmi, simdi=SIMDI)

        self.assertEqual([p["id"] for p in liste],
                         ["claude-5s", "claude-hafta", "claude-fable"])
        for p in liste:
            self.assertEqual(p["gozlem_yas_dk"], 2050)

    def test_bayat_claude_serbest_yerine_bilinmiyor_basar(self) -> None:
        resmi = {
            "five_hour": {"used_percentage": 36.0, "resets_at": int(SIMDI - 100)},
            "seven_day": {"used_percentage": 7.0, "resets_at": int(SIMDI + 6 * 86400)},
            "_kaynak": "oauth",
            "_yas_dk": 2050,
            "_bayat": True,
            "_gozlem": int(SIMDI - 2050 * 60),
        }
        hiz = {
            p["id"]: kota_hiz.degerlendir(
                p["id"], p["used"], p["resets_at"], p["pencere_sn"],
                ornekler=[], simdi=SIMDI, gozlem_yas_dk=p["gozlem_yas_dk"],
            )
            for p in kota.pencereler(None, resmi, simdi=SIMDI)
        }

        satir = kota.tek_satir(None, {"5s": {}, "7g": {}}, resmi, hiz)

        self.assertTrue(satir.startswith("[kota] "))  # kanca bu öneki ayrıştırıyor
        self.assertIn("[? bayat 2050dk]", satir)
        self.assertIn("bant: bilinmiyor (claude-5s bayat)", satir)
        self.assertNotIn("serbest", satir)


if __name__ == "__main__":
    unittest.main()
