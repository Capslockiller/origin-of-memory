"""Bayat kota kaynağının "serbest" sayılmaması (Astra A8, 2026-09-06) +
CANLI OKUMA kuralı (Master, 2026-09-08).

Denetimde bulunan hata: 2026-09-05 08:26'da donmuş OAuth önbelleği ertesi gün
boyunca aynı yüzdeleri verdi; her okuma örneklem defterine YENİ zaman
damgasıyla yazıldığı için Δused = 0 çıktı ve yönetici bant "R 0,0 · serbest"
oldu. Ayrıca reset'i geçmiş pencere, gözlem taze olsun olmasın, koşulsuz
"serbest" dönüyordu — %99 dolu bir pencere bile.

2026-09-08 kuralı bunun bir üst basamağı: bayatlığı işaretlemek yetmez, eski
sayı HİÇ BASILMAZ. Buradaki OAuth testleri artık üç şeyi bekliyor —
(a) önbellek taze olsa da her çağrıda ağa çıkılır, (b) okuma düşerse dönüş
``None`` olur ve tanı yazılır, (c) statusline önbelleği dolu bile olsa
kullanılmaz.

Saatler her testte sabitlenir; hiçbir test canlı kasaya ya da ağa dokunmaz.

yazan: claude
model: opus-5
"""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
import sys
import tempfile
import unittest
import unittest.mock
import urllib.error

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


class OauthTaniTests(unittest.TestCase):
    """Yenileme denemesi düşerse NEDENİ önbelleğe yazılır ve satırda görünür.

    Denetim (2026-09-06): OAuth önbelleği 2026-09-05 08:26'dan beri donmuştu ve
    hiçbir yerde nedeni yazmıyordu. Canlı teşhis: erişim jetonunun süresi
    dolmuş, uç 401 "OAuth access token has expired" veriyor. Bu testler ağa
    çıkmaz; her yol sahte bir getirici ile sürülür.
    """

    def setUp(self) -> None:
        self._gecici = tempfile.TemporaryDirectory()
        kok = Path(self._gecici.name)
        self.onbellek = kok / "claude-kota-oauth.json"
        self.kimlik = kok / ".credentials.json"
        self._yamalar = [
            unittest.mock.patch.object(kota, "CLAUDE_OAUTH_CACHE", self.onbellek),
            unittest.mock.patch.object(kota, "CLAUDE_CRED_PATH", self.kimlik),
        ]
        for yama in self._yamalar:
            yama.start()

    def tearDown(self) -> None:
        for yama in reversed(self._yamalar):
            yama.stop()
        self._gecici.cleanup()

    def _bayat_onbellek_yaz(self) -> None:
        eski = dt.datetime.now(dt.timezone.utc) - dt.timedelta(minutes=2158)
        self.onbellek.write_text(
            json.dumps({
                "yazilma": eski.isoformat(),
                "veri": {
                    "five_hour": {"utilization": 36.0,
                                  "resets_at": "2026-09-05T10:59:59+00:00"},
                    "seven_day": {"utilization": 7.0,
                                  "resets_at": "2026-09-12T03:59:59+00:00"},
                },
            }),
            encoding="utf-8",
        )

    def _kimlik_yaz(self, bitis_ms: int) -> None:
        self.kimlik.write_text(
            json.dumps({"claudeAiOauth": {
                "accessToken": "sk-ant-oat01-sahte",
                "expiresAt": bitis_ms,
                "subscriptionType": "max",
                "rateLimitTier": "default_claude_max_5x",
            }}),
            encoding="utf-8",
        )

    def _ham(self) -> dict:
        return json.loads(self.onbellek.read_text(encoding="utf-8"))

    def test_suresi_dolmus_jeton_onbellege_yazilir(self) -> None:
        """Canlı teşhisin birebir hâli: jeton dolmuş, yenileme denenmiyor."""
        self._bayat_onbellek_yaz()
        dun = dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=30)
        self._kimlik_yaz(int(dun.timestamp() * 1000))

        resmi = kota.claude_oauth()

        ham = self._ham()
        self.assertEqual(ham["son_hata"], "jeton-suresi-doldu")
        self.assertEqual(ham["http_status"], 401)
        self.assertTrue(ham["son_deneme"])
        # Tanı kaydı korunur (beyin-doktor okur) ama GÖSTERİME dönmez.
        self.assertNotEqual(ham["yazilma"], ham["son_deneme"])
        self.assertIsNone(resmi)

    def test_http_hatasi_kod_ile_kaydedilir(self) -> None:
        self._bayat_onbellek_yaz()
        yarin = dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)
        self._kimlik_yaz(int(yarin.timestamp() * 1000))

        def dusen_getirici(*_a, **_k):
            raise urllib.error.HTTPError(
                kota.OAUTH_URL, 401, "Unauthorized", None, None
            )

        with unittest.mock.patch.object(
            kota.urllib.request, "urlopen", dusen_getirici
        ):
            resmi = kota.claude_oauth()

        ham = self._ham()
        self.assertEqual(ham["son_hata"], "http-hatasi")
        self.assertEqual(ham["http_status"], 401)
        self.assertIsNone(resmi)
        self.assertEqual(kota.claude_hata_metni(), "401 → /login")

    def test_ag_hatasi_kaydedilir(self) -> None:
        self._bayat_onbellek_yaz()
        yarin = dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)
        self._kimlik_yaz(int(yarin.timestamp() * 1000))

        def dusen_getirici(*_a, **_k):
            raise urllib.error.URLError("baglanti yok")

        with unittest.mock.patch.object(
            kota.urllib.request, "urlopen", dusen_getirici
        ):
            sonuc = kota.claude_oauth()

        self.assertIsNone(sonuc)
        self.assertEqual(self._ham()["son_hata"], "ag-hatasi:URLError")
        self.assertEqual(kota.claude_hata_metni(), "ağ")

    def test_basarili_yenileme_hatayi_temizler(self) -> None:
        self._bayat_onbellek_yaz()
        kota._oauth_tani_yaz("jeton-suresi-doldu", 401)
        yarin = dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)
        self._kimlik_yaz(int(yarin.timestamp() * 1000))
        govde = json.dumps({
            "five_hour": {"utilization": 40.0, "resets_at": "2026-09-06T23:00:00+00:00"},
            "seven_day": {"utilization": 9.0, "resets_at": "2026-09-12T03:59:59+00:00"},
        }).encode("utf-8")

        class _Yanit:
            status = 200

            def read(self):
                return govde

            def __enter__(self):
                return self

            def __exit__(self, *_a):
                return False

        with unittest.mock.patch.object(
            kota.urllib.request, "urlopen", lambda *_a, **_k: _Yanit()
        ):
            resmi = kota.claude_oauth()

        ham = self._ham()
        self.assertIsNone(ham["son_hata"])
        self.assertEqual(ham["http_status"], 200)
        self.assertNotIn("_oauth_hata", resmi)

    def test_taze_onbellek_varken_bile_aga_cikilir(self) -> None:
        """Master 2026-09-08: 300 sn'lik hızlı yol KALDIRILDI — her çağrı canlı."""
        taze = dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=5)
        self.onbellek.write_text(
            json.dumps({
                "yazilma": taze.isoformat(),
                "veri": {"five_hour": {"utilization": 36.0,
                                       "resets_at": "2026-09-05T10:59:59+00:00"}},
            }),
            encoding="utf-8",
        )
        yarin = dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)
        self._kimlik_yaz(int(yarin.timestamp() * 1000))
        sayac = {"n": 0}

        class _Yanit:
            status = 200

            def read(self):
                return json.dumps({
                    "five_hour": {"utilization": 91.0,
                                  "resets_at": "2026-09-08T23:00:00+00:00"},
                }).encode("utf-8")

            def __enter__(self):
                return self

            def __exit__(self, *_a):
                return False

        def sayan(*_a, **_k):
            sayac["n"] += 1
            return _Yanit()

        with unittest.mock.patch.object(kota.urllib.request, "urlopen", sayan):
            resmi = kota.claude_oauth()

        self.assertEqual(sayac["n"], 1)             # önbellek 5 sn taze olsa DA
        self.assertEqual(resmi["five_hour"]["used_percentage"], 91.0)  # taze değer
        self.assertEqual(resmi["_yas_dk"], 0)

    def test_basarisiz_okuma_sayi_basmaz(self) -> None:
        """Okuma düşerse satırda yüzde YOK, "canlı okuma başarısız" var."""
        self._bayat_onbellek_yaz()
        yarin = dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)
        self._kimlik_yaz(int(yarin.timestamp() * 1000))

        def dusen_getirici(*_a, **_k):
            raise urllib.error.HTTPError(kota.OAUTH_URL, 401, "Unauthorized", None, None)

        with unittest.mock.patch.object(
            kota.urllib.request, "urlopen", dusen_getirici
        ):
            resmi = kota.claude_oauth()
            satir = kota.tek_satir(None, {"5s": {}, "7g": {}}, resmi, {})

        self.assertIsNone(resmi)
        self.assertIn("Claude: canlı okuma başarısız (401 → /login)", satir)
        self.assertNotIn("%36", satir)              # bayat önbellekteki sayı
        self.assertIn("bant: bilinmiyor (kaynak yok)", satir)
        self.assertTrue(self._ham()["son_hata"])    # tanı yine de yazıldı

    def test_statusline_onbellegi_kullanilmaz(self) -> None:
        """`claude-kota.json` dolu olsa bile gösterim zincirine GİRMEZ."""
        self.assertFalse(hasattr(kota, "claude_resmi"))
        self.assertFalse(hasattr(kota, "CLAUDE_KOTA_CACHE"))

        yarin = dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)
        self._kimlik_yaz(int(yarin.timestamp() * 1000))
        with unittest.mock.patch.object(
            kota, "claude_oauth", lambda: None
        ):
            self.assertIsNone(kota.resmi_zinciri())

    def test_canli_etiket_gozlem_saatini_basar(self) -> None:
        resmi = {
            "five_hour": {"used_percentage": 36.0, "resets_at": int(SIMDI + 2 * SAAT)},
            "seven_day": {"used_percentage": 7.0, "resets_at": int(SIMDI + 6 * 86400)},
            "_kaynak": "oauth", "_yas_dk": 0, "_gozlem": int(SIMDI),
        }
        hiz = {
            p["id"]: kota_hiz.degerlendir(
                p["id"], p["used"], p["resets_at"], p["pencere_sn"],
                ornekler=[], simdi=SIMDI, gozlem_yas_dk=p["gozlem_yas_dk"],
            )
            for p in kota.pencereler(None, resmi, simdi=SIMDI)
        }

        satir = kota.tek_satir(None, {"5s": {}, "7g": {}}, resmi, hiz)

        beklenen = dt.datetime.fromtimestamp(SIMDI).strftime("[canlı %H:%M]")
        self.assertIn(beklenen, satir)
        self.assertNotIn("[oauth ", satir)

    def test_detay_cozum_satiri_verir(self) -> None:
        satirlar = kota.oauth_tani_satirlari({
            "_oauth_hata": "jeton-suresi-doldu",
            "_oauth_http": 401,
            "_oauth_son_deneme": "2026-09-06T20:28:42+00:00",
        })

        self.assertEqual(len(satirlar), 2)
        self.assertIn("BAŞARISIZ", satirlar[0])
        self.assertIn("HTTP 401", satirlar[0])
        self.assertIn("/login", satirlar[1])

    def test_saglikli_kaynakta_tani_satiri_yok(self) -> None:
        self.assertEqual(kota.oauth_tani_satirlari({"_kaynak": "oauth"}), [])


class KalanYuzdeTests(unittest.TestCase):
    """A-borç 5: "serbest" tek başına boş kota demek değildir."""

    RESET = int(SIMDI + 4 * SAAT)          # 5s penceresinin 4 saati kaldı
    TAZE_RESET = int(SIMDI + 4.9 * SAAT)   # pencere daha yeni açıldı → yedek yok

    def _ornek(self, used: float, reset: int | None = None) -> dict:
        """Ölçüm ufkunun (25 dk) içinde, en az aralığı (12 dk) aşan tek örnek."""
        return {"ts": SIMDI - 20 * 60, "id": "codex-5s", "used": used,
                "resets_at": reset if reset is not None else self.RESET}

    def _d(self, used: float, ornekler: list[dict] | None = None,
           reset: int | None = None) -> dict:
        return kota_hiz.degerlendir(
            "codex-5s", used, reset if reset is not None else self.RESET, 5 * SAAT,
            ornekler=ornekler or [], simdi=SIMDI, gozlem_yas_dk=5,
        )

    def test_yuksek_kullanim_yanma_yokken_kalan_basilir(self) -> None:
        """Gece görülen satır: "Codex 5s %83 [R 0,0 · serbest]" boş sanıldı."""
        d = self._d(83.0, [self._ornek(83.0)])  # Δused = 0 → yanma kanıtı yok

        self.assertEqual(d["bant"], "serbest")
        self.assertEqual(d["R"], 0.0)
        self.assertEqual(kota_hiz.kisa_metin(d), " [R 0,0 · serbest · kalan %17]")

    def test_ornek_hic_yokken_de_kalan_basilir(self) -> None:
        d = self._d(83.0, reset=self.TAZE_RESET)

        self.assertEqual(d["bant"], "serbest")
        self.assertIsNone(d["R"])
        self.assertEqual(kota_hiz.kisa_metin(d), " [serbest · kalan %17]")

    def test_dusuk_kullanimda_kalan_basilmaz(self) -> None:
        d = self._d(12.0, reset=self.TAZE_RESET)

        self.assertEqual(d["bant"], "serbest")
        self.assertNotIn("kalan", kota_hiz.kisa_metin(d))

    def test_gercek_yanma_olculdugunde_kalan_basilmaz(self) -> None:
        """Yanma ölçüldüyse R ve `biter` zaten konuşuyor; kalan% gürültüdür."""
        d = self._d(83.0, [self._ornek(82.7)])

        self.assertEqual(d["bant"], "serbest")
        self.assertTrue(d["yanma"])
        self.assertNotIn("kalan %", kota_hiz.kisa_metin(d))

    def test_bilinmiyor_bandi_degismedi(self) -> None:
        d = kota_hiz.degerlendir(
            "codex-5s", 83.0, int(SIMDI + 4 * SAAT), 5 * SAAT,
            ornekler=[], simdi=SIMDI, gozlem_yas_dk=2158,
        )

        self.assertEqual(kota_hiz.kisa_metin(d), " [? bayat 2158dk]")


class CodexCanliSatirTests(unittest.TestCase):
    """Codex artık CANLI yoklanır: tek kural yaş, `KURAL_RESET` kaldırıldı.

    2026-09-07'de Codex yüzdesi rollout dosyalarından okunuyordu ve gözlem
    reset'e kadar geçerli sayılıyordu. 8 Eylül'de o kural kaldırıldı: rollout
    20:03'te durmuşken canlı uç %100 · rate_limit_reached gösteriyordu.
    """

    def _codex(self, used_p: float = 100.0, reached: str | None = "rate_limit_reached",
               kredi: int | None = 1) -> dict:
        return {
            "primary": {"used_percent": used_p, "window_minutes": 300,
                        "resets_at": int(SIMDI + 86 * 60)},
            "secondary": {"used_percent": 16.0, "window_minutes": 10080,
                          "resets_at": int(SIMDI + 5 * 86400)},
            "credits": {"has_credits": False, "unlimited": False, "balance": "0"},
            "plan_type": "plus", "rate_limit_reached_type": reached,
            "_kaynak": "app-server", "_gozlem": int(SIMDI), "_reset_kredisi": kredi,
        }

    def test_kural_reset_kaldirildi(self) -> None:
        self.assertFalse(hasattr(kota_hiz, "KURAL_RESET"))
        self.assertFalse(hasattr(kota_hiz, "codex_bayat_esik_dk"))
        self.assertFalse(hasattr(kota_hiz, "CODEX_BAYAT_ESIK_DK"))

    def test_pencereler_codex_icin_yas_kurali_tasir(self) -> None:
        resmi = {
            "five_hour": {"used_percentage": 36.0, "resets_at": int(SIMDI + 2 * SAAT)},
            "seven_day": {"used_percentage": 7.0, "resets_at": int(SIMDI + 6 * 86400)},
            "_kaynak": "oauth", "_gozlem": int(SIMDI),
        }

        kural = {p["id"]: p["bayat_kurali"]
                 for p in kota.pencereler(self._codex(), resmi, simdi=SIMDI)}

        self.assertEqual(set(kural.values()), {kota_hiz.KURAL_YAS})

    def test_codex_rollout_taramasi_kaldirildi(self) -> None:
        self.assertFalse(hasattr(kota, "codex_resmi"))
        self.assertFalse(hasattr(kota, "CODEX_SESSIONS"))

    def test_satir_limit_ve_kredi_isaretlerini_basar(self) -> None:
        codex = self._codex()
        hiz = {
            p["id"]: kota_hiz.degerlendir(
                p["id"], p["used"], p["resets_at"], p["pencere_sn"],
                ornekler=[], simdi=SIMDI, gozlem_yas_dk=p["gozlem_yas_dk"],
            )
            for p in kota.pencereler(codex, None, simdi=SIMDI)
        }

        satir = kota.tek_satir(codex, {"5s": {}, "7g": {}}, None, hiz)

        self.assertIn("Codex 5s %100", satir)
        self.assertIn("⚠limit doldu", satir)
        self.assertIn("(sıfırlama kredisi: 1)", satir)
        self.assertIn(dt.datetime.fromtimestamp(SIMDI).strftime("[canlı %H:%M]"), satir)

    def test_isaretler_veri_yoksa_basilmaz(self) -> None:
        codex = self._codex(used_p=16.0, reached=None, kredi=0)

        satir = kota.tek_satir(codex, {"5s": {}, "7g": {}}, None, {})

        self.assertNotIn("⚠limit doldu", satir)
        self.assertNotIn("sıfırlama kredisi", satir)

    def test_codex_okunamazsa_sayi_basilmaz(self) -> None:
        satir = kota.tek_satir(None, {"5s": {}, "7g": {}}, None, {},
                               codex_hata="codex bulunamadı")

        self.assertIn("Codex: canlı okuma başarısız (codex bulunamadı)", satir)
        self.assertNotIn("Codex 5s", satir)
        self.assertIn("bant: bilinmiyor (kaynak yok)", satir)


if __name__ == "__main__":
    unittest.main()
