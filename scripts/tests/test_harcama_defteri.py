"""Harcama defteri — yineleme ve gün ataması (Astra A2).

Dış denetimin (gpt-6-astra, 2026-09-06) iki bulgusu burada kilitleniyor:

1. Claude transkriptleri aynı yanıtı birden çok satırda yazar (akış
   güncellemesi, yeniden deneme, sıkıştırma). Her kopyada bir ``usage`` bloğu
   vardır ve eski defter hepsini toplardı. Bir yanıt bir kez sayılmalı, hem de
   SON hâliyle — dosya içinde de, dosyalar arasında da.
2. Günlük kırılım oturumun son damgasına yazılırdı; gece yarısını aşan bir
   oturum bütün harcamasını yanlış güne taşırdı. Artık her kayıt kendi
   damgasının gününe gider.

Codex tarafı (kümülatif ``total_token_usage``) zaten SON kaydı alıyordu — çifte
sayım yoktu; buradaki test onu kanıt olarak sabitliyor ve yeni gün dağıtımının
oturum toplamını değiştirmediğini gösteriyor.

Hiçbir test gerçek ``~/.claude`` ya da canlı deftere dokunmaz.

yazan: claude
model: opus-5
"""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path

import pytest

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import harcama_defteri


def _gun_yerel(ts: str) -> str:
    """Testin kendi hesabı: UTC damga → yerel takvim günü (modülden bağımsız)."""
    return (
        dt.datetime.fromisoformat(ts.replace("Z", "+00:00")).astimezone().date().isoformat()
    )


def _claude_satiri(
    mesaj_kimligi: str,
    ts: str,
    *,
    cikti: int,
    girdi: int = 10,
    cache_okuma: int = 0,
    cache_yazma: int = 0,
    model: str = "claude-opus-5",
    istek_kimligi: str = "req_1",
    uuid: str | None = None,
    cwd: str | None = None,
) -> dict:
    return {
        "type": "assistant",
        "cwd": cwd or "C:\\Users\\musta\\Somewhere",
        "uuid": uuid or f"uuid-{mesaj_kimligi}-{ts}",
        "requestId": istek_kimligi,
        "timestamp": ts,
        "message": {
            "id": mesaj_kimligi,
            "role": "assistant",
            "model": model,
            "usage": {
                "input_tokens": girdi,
                "output_tokens": cikti,
                "cache_read_input_tokens": cache_okuma,
                "cache_creation_input_tokens": cache_yazma,
            },
        },
    }


def _codex_satiri(ts: str, girdi: int, cikti: int, onbellek: int) -> dict:
    return {
        "timestamp": ts,
        "type": "event_msg",
        "payload": {
            "type": "token_count",
            "info": {
                "total_token_usage": {
                    "input_tokens": girdi,
                    "output_tokens": cikti,
                    "cached_input_tokens": onbellek,
                    "total_tokens": girdi + cikti,
                }
            },
        },
    }


@pytest.fixture
def defter_ortami(tmp_path, monkeypatch):
    """Kaynak kökleri ve defter yolu tmp_path içine bakar."""
    projeler = tmp_path / "claude" / "projects"
    codex = tmp_path / "codex" / "sessions"
    projeler.mkdir(parents=True)
    codex.mkdir(parents=True)
    monkeypatch.setattr(harcama_defteri, "CLAUDE_PROJECTS", projeler)
    monkeypatch.setattr(harcama_defteri, "CODEX_SESSIONS", codex)
    monkeypatch.setenv("BEYIN_HARCAMA_DEFTERI", str(tmp_path / "defter.json"))
    return projeler, codex


def _oku() -> dict:
    return json.loads(harcama_defteri._defter_yolu().read_text(encoding="utf-8"))


def _toplam(defter: dict, alan: str) -> int:
    return sum(
        int(v.get(alan) or 0)
        for gun in defter["gunluk"].values()
        for v in gun.values()
    )


# --- (a) aynı yanıt üç kez, çıktı büyüyerek --------------------------------


def test_ayni_mesaj_kimligi_bir_kez_ve_son_degerle_sayilir(defter_ortami):
    projeler, _ = defter_ortami
    ts = "2026-09-03T10:00:00.000Z"
    _helpers.write_jsonl(
        projeler / "proje-a" / "oturum-1.jsonl",
        [
            _claude_satiri("msg_A", ts, cikti=100, cache_okuma=5_000),
            _claude_satiri("msg_A", ts, cikti=900, cache_okuma=5_000),
            _claude_satiri("msg_A", ts, cikti=1_500, cache_okuma=5_000),
        ],
    )
    harcama_defteri.topla()
    defter = _oku()

    gun = _gun_yerel(ts)
    hane = defter["gunluk"][gun]["claude-opus-5"]
    assert hane["istek"] == 1, "üç kopya tek istek olmalı"
    assert hane["cikti"] == 1_500, "son (tamamlanmış) çıktı değeri sayılmalı"
    assert hane["cache_okuma"] == 5_000, "önbellek okuması üçe katlanmamalı"
    assert defter["oturumlar"]["oturum-1"]["modeller"]["claude-opus-5"]["cikti"] == 1_500
    # ham satır sayısı ile tekil kayıt sayısı ayrı ayrı görünür olmalı
    dosya_kaydi = next(iter(defter["dosyalar"].values()))
    assert dosya_kaydi["ham"] == 3
    assert len(dosya_kaydi["kayitlar"]) == 1


def test_kimliksiz_kayitlar_ayri_ayri_sayilir(defter_ortami):
    """``message.id`` yoksa requestId, o da yoksa uuid; hiçbiri yoksa satır."""
    projeler, _ = defter_ortami
    satir_a = _claude_satiri("msg_X", "2026-09-03T10:00:00.000Z", cikti=10)
    satir_b = _claude_satiri("msg_X", "2026-09-03T10:00:01.000Z", cikti=20)
    for satir in (satir_a, satir_b):
        satir["message"].pop("id")
    satir_a["requestId"] = "req_a"
    satir_b["requestId"] = "req_b"
    _helpers.write_jsonl(projeler / "p" / "o.jsonl", [satir_a, satir_b])
    harcama_defteri.topla()
    defter = _oku()
    assert _toplam(defter, "cikti") == 30
    assert _toplam(defter, "istek") == 2


# --- (b) aynı kimlik iki dosyada -------------------------------------------


def test_ayni_kimlik_iki_dosyada_bir_kez_sayilir(defter_ortami):
    projeler, _ = defter_ortami
    ts = "2026-09-03T10:00:00.000Z"
    _helpers.write_jsonl(
        projeler / "proje-a" / "oturum-1.jsonl",
        [_claude_satiri("msg_ORTAK", ts, cikti=700, cache_okuma=1_000)],
    )
    # sıkıştırma sonrası devam dosyası aynı yanıtı yeniden yazar
    _helpers.write_jsonl(
        projeler / "proje-a" / "oturum-2.jsonl",
        [
            _claude_satiri("msg_ORTAK", ts, cikti=700, cache_okuma=1_000),
            _claude_satiri("msg_YENI", ts, cikti=300, cache_okuma=2_000),
        ],
    )
    harcama_defteri.topla()
    defter = _oku()

    assert _toplam(defter, "cikti") == 1_000, "ortak yanıt iki kez sayılmamalı"
    assert _toplam(defter, "cache_okuma") == 3_000
    assert defter["capraz_yinelenen"] == 1
    # yol sırasında ilk dosya sahiplenir
    assert defter["oturumlar"]["oturum-1"]["modeller"]["claude-opus-5"]["cikti"] == 700
    assert defter["oturumlar"]["oturum-2"]["modeller"]["claude-opus-5"]["cikti"] == 300


# --- (c) gece yarısını aşan oturum -----------------------------------------


def test_gece_yarisini_asan_oturum_iki_gune_dagilir(defter_ortami):
    projeler, _ = defter_ortami
    erken = "2026-09-03T08:00:00.000Z"
    gec = "2026-09-04T08:00:00.000Z"
    _helpers.write_jsonl(
        projeler / "p" / "gece.jsonl",
        [
            _claude_satiri("msg_1", erken, cikti=1_000, cache_okuma=10_000),
            _claude_satiri("msg_2", gec, cikti=2_000, cache_okuma=20_000),
        ],
    )
    harcama_defteri.topla()
    defter = _oku()

    gun_erken, gun_gec = _gun_yerel(erken), _gun_yerel(gec)
    assert gun_erken != gun_gec
    assert defter["gunluk"][gun_erken]["claude-opus-5"]["cikti"] == 1_000
    assert defter["gunluk"][gun_gec]["claude-opus-5"]["cikti"] == 2_000
    assert defter["gunluk"][gun_erken]["claude-opus-5"]["cache_okuma"] == 10_000
    assert defter["gunluk"][gun_gec]["claude-opus-5"]["cache_okuma"] == 20_000
    # oturum toplamı bozulmadan duruyor
    assert defter["oturumlar"]["gece"]["modeller"]["claude-opus-5"]["cikti"] == 3_000


# --- (d) filigran: değişmemiş dosya yeniden okunmaz, toplamlar sabit -------


def test_degismemis_dosya_okunmaz_ama_toplamlar_ayakta_kalir(defter_ortami, monkeypatch):
    projeler, _ = defter_ortami
    ts = "2026-09-03T10:00:00.000Z"
    _helpers.write_jsonl(
        projeler / "p" / "o.jsonl",
        [_claude_satiri("msg_A", ts, cikti=1_500, cache_okuma=5_000)],
    )
    yeni, atlanan = harcama_defteri.topla()
    assert (yeni, atlanan) == (1, 0)
    ilk_gunluk = _oku()["gunluk"]

    okundu: list[Path] = []
    gercek = harcama_defteri._claude_dosya_ozeti

    def izleyen(dosya: Path):
        okundu.append(dosya)
        return gercek(dosya)

    monkeypatch.setattr(harcama_defteri, "_claude_dosya_ozeti", izleyen)
    yeni, atlanan = harcama_defteri.topla()
    assert (yeni, atlanan) == (0, 1), "değişmemiş dosya yeniden okunmamalı"
    assert okundu == [], "okuyucu hiç çağrılmamalı"
    assert _oku()["gunluk"] == ilk_gunluk, "tekilleştirme durumu artımlı koşuda korunur"


def test_degisen_dosyanin_haritasi_birikmez_degistirilir(defter_ortami):
    """Dosya büyüyünce eski kimlik haritası eklenmez, yerine yenisi konur."""
    projeler, _ = defter_ortami
    ts = "2026-09-03T10:00:00.000Z"
    yol = projeler / "p" / "o.jsonl"
    _helpers.write_jsonl(yol, [_claude_satiri("msg_A", ts, cikti=100)])
    harcama_defteri.topla()
    _helpers.write_jsonl(
        yol,
        [
            _claude_satiri("msg_A", ts, cikti=100),
            _claude_satiri("msg_A", ts, cikti=800),
        ],
    )
    harcama_defteri.topla()
    defter = _oku()
    assert _toplam(defter, "cikti") == 800
    assert _toplam(defter, "istek") == 1


def test_yeniden_bayragi_defteri_sifirdan_kurar(defter_ortami):
    projeler, _ = defter_ortami
    ts = "2026-09-03T10:00:00.000Z"
    yol = projeler / "p" / "o.jsonl"
    _helpers.write_jsonl(yol, [_claude_satiri("msg_A", ts, cikti=100)])
    harcama_defteri.topla()
    yol.unlink()
    yeni, atlanan = harcama_defteri.topla(yeniden=True)
    assert (yeni, atlanan) == (0, 0)
    defter = _oku()
    assert defter["oturumlar"] == {}
    assert defter["gunluk"] == {}


# --- (e) Codex: kümülatif sayaç -------------------------------------------


def test_codex_kumulatif_sayac_toplanmaz_ve_gune_dagilir(defter_ortami):
    """Kanıt: ``total_token_usage`` kümülatiftir; oturum toplamı SON kayıttır.

    Eski sürüm de son kaydı alıyordu (çifte sayım yoktu). Yeni sürüm aynı
    toplamı verir ama artımları kendi gününe yazar.
    """
    _, codex = defter_ortami
    gun_a = "2026-09-03T08:00:00.000Z"
    gun_b = "2026-09-04T08:00:00.000Z"
    _helpers.write_jsonl(
        codex / "2026" / "09" / "03" / "rollout-2026-09-03T08-00-00-abc.jsonl",
        [
            _codex_satiri(gun_a, 1_000, 100, 500),
            _codex_satiri(gun_a, 3_000, 300, 1_500),
            _codex_satiri(gun_b, 5_000, 700, 2_500),
        ],
    )
    harcama_defteri.topla()
    defter = _oku()

    oturum = next(iter(defter["oturumlar"].values()))
    hane = oturum["modeller"]["codex"]
    assert hane["girdi"] == 5_000, "kümülatif değerler toplanmamalı (1000+3000+5000=9000 değil)"
    assert hane["cikti"] == 700
    assert hane["cache_okuma"] == 2_500

    ga, gb = _gun_yerel(gun_a), _gun_yerel(gun_b)
    assert defter["gunluk"][ga]["codex"]["girdi"] == 3_000
    assert defter["gunluk"][gb]["codex"]["girdi"] == 2_000
    assert defter["gunluk"][ga]["codex"]["cikti"] == 300
    assert defter["gunluk"][gb]["codex"]["cikti"] == 400


# --- ortam / biçim ---------------------------------------------------------


def test_defter_yolu_ortam_degiskeniyle_gecersiz_kilinir(tmp_path, monkeypatch):
    hedef = tmp_path / "alt" / "olcum.json"
    monkeypatch.setenv("BEYIN_HARCAMA_DEFTERI", str(hedef))
    assert harcama_defteri._defter_yolu() == hedef
    monkeypatch.delenv("BEYIN_HARCAMA_DEFTERI")
    assert harcama_defteri._defter_yolu() == harcama_defteri.DEFTER


def test_surum1_defteri_sifirdan_kurulur(defter_ortami):
    """Yinelenmiş sayıları taşıyan eski defter olduğu gibi devralınmaz."""
    eski = {"surum": 1, "filigran": {"x": [1, 2]}, "oturumlar": {"a": {}}, "gunluk": {"g": {}}}
    harcama_defteri._defter_yolu().write_text(json.dumps(eski), encoding="utf-8")
    defter = harcama_defteri._yukle()
    assert defter["surum"] == harcama_defteri.SURUM
    assert defter["filigran"] == {}
    assert defter["oturumlar"] == {}


def test_gun_yerel_takvime_cevirir():
    assert harcama_defteri._gun("2026-09-03T08:00:00.000Z") == _gun_yerel(
        "2026-09-03T08:00:00.000Z"
    )
    assert harcama_defteri._gun(None) == "?"
    assert harcama_defteri._gun("bozuk") == "bozuk"[:10]


def test_ozet_biciminde_degisiklik_yok(defter_ortami, capsys):
    projeler, _ = defter_ortami
    ts = dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
    _helpers.write_jsonl(
        projeler / "p" / "o.jsonl",
        [_claude_satiri("msg_A", ts, cikti=12_000, cache_okuma=340_000, cache_yazma=9_000)],
    )
    harcama_defteri.topla()
    harcama_defteri.ozet(2)
    cikti = capsys.readouterr().out
    assert "Harcama defteri — son 2 gün (kayıt: 1 oturum)" in cikti
    assert "önbellek okuma 340k / yazma 9k" in cikti
    assert "opus: 12k" in cikti
    assert "En pahalı 5 oturum/görev:" in cikti


# --- Astra B2: bakım / geliştirme / iş bütçesi ------------------------------


def test_sinif_depo_altini_gelistirmeye_yazar():
    assert (
        harcama_defteri._sinif(r"E:\OdenaWorks\10-Aktif\origin-of-memory", None)
        == harcama_defteri.GRUP_GELISTIRME
    )
    # tools/benchmark depo altındadır; ayrı bir kural gerekmez
    assert (
        harcama_defteri._sinif(
            r"E:\OdenaWorks\10-Aktif\origin-of-memory\tools\benchmark", None
        )
        == harcama_defteri.GRUP_GELISTIRME
    )
    # benchmark'ın sahne dizini de — çalıştırıcı işareti taşısa bile — geliştirme
    assert (
        harcama_defteri._sinif(
            None,
            "E--OdenaWorks-10-Aktif-origin-of-memory-tools-benchmark"
            "--e2e-vaults-conv-26--stage-compile-stage-1jcxl1c8",
        )
        == harcama_defteri.GRUP_GELISTIRME
    )


def test_sinif_calistirici_izini_bakima_yazar():
    for yol in (
        r"C:\Users\musta\AppData\Local\Temp\beyin-flush-0dm4fa4g",
        r"C:\Users\musta\AppData\Local\Temp\beyin-ingest-01n386vr",
        r"C:\Users\musta\AppData\Local\Temp\beyin-codex-abc",
        r"E:\OdenaOS\.stage\compile-stage-9kruwqop",
    ):
        assert harcama_defteri._sinif(yol, None) == harcama_defteri.GRUP_BAKIM


def test_sinif_kalan_calisma_dizinini_ise_ve_bosu_bilinmiyora_yazar():
    assert harcama_defteri._sinif(r"D:\Epic\Mice360", None) == harcama_defteri.GRUP_IS
    assert harcama_defteri._sinif(None, None) == harcama_defteri.GRUP_BILINMIYOR
    assert harcama_defteri._sinif("   ", "") == harcama_defteri.GRUP_BILINMIYOR


def test_gruplar_cwd_ve_calistirici_izinden_kurulur(defter_ortami):
    projeler, codex = defter_ortami
    ts = "2026-09-03T10:00:00.000Z"
    gun = _gun_yerel(ts)
    # (a) cwd depo altında → geliştirme
    _helpers.write_jsonl(
        projeler / "E--OdenaWorks-10-Aktif-origin-of-memory" / "dev.jsonl",
        [
            _claude_satiri(
                "msg_dev",
                ts,
                cikti=1_000,
                cache_okuma=10_000,
                cwd=r"E:\OdenaWorks\10-Aktif\origin-of-memory",
            )
        ],
    )
    # (b) çalıştırıcının açtığı geçici dizin → bakım
    _helpers.write_jsonl(
        projeler / "C--Users-musta-AppData-Local-Temp-beyin-flush-xy" / "f.jsonl",
        [
            _claude_satiri(
                "msg_flush",
                ts,
                cikti=200,
                cache_okuma=3_000,
                cwd=r"C:\Users\musta\AppData\Local\Temp\beyin-flush-xy",
            )
        ],
    )
    # (c) başka bir yerdeki normal oturum → iş
    _helpers.write_jsonl(
        projeler / "D--Epic-Mice360" / "is.jsonl",
        [
            _claude_satiri(
                "msg_is", ts, cikti=5_000, cache_okuma=40_000, cwd=r"D:\Epic\Mice360"
            )
        ],
    )
    harcama_defteri.topla()
    defter = _oku()

    gruplar = defter["gruplar"][gun]
    assert gruplar[harcama_defteri.GRUP_GELISTIRME]["cikti"] == 1_000
    assert gruplar[harcama_defteri.GRUP_BAKIM]["cikti"] == 200
    assert gruplar[harcama_defteri.GRUP_IS]["cikti"] == 5_000
    assert harcama_defteri.GRUP_BILINMIYOR not in gruplar
    # grup toplamı model kırılımının toplamına eşit olmalı — hiçbir çağrı kaybolmaz
    assert sum(h["cikti"] for h in gruplar.values()) == sum(
        h["cikti"] for h in defter["gunluk"][gun].values()
    )
    assert defter["oturumlar"]["dev"]["grup"] == harcama_defteri.GRUP_GELISTIRME


def test_cwd_okunamayan_oturum_tahmin_edilmez_bilinmiyor_olur(defter_ortami):
    projeler, _ = defter_ortami
    ts = "2026-09-03T10:00:00.000Z"
    satir = _claude_satiri("msg_x", ts, cikti=700)
    satir.pop("cwd")
    _helpers.write_jsonl(projeler / "p" / "o.jsonl", [satir])
    harcama_defteri.topla()
    defter = _oku()
    # cwd okunamadı: alan gerçekten boş kaydedilmiş olmalı, uydurulmuş değil
    assert next(iter(defter["dosyalar"].values()))["cwd"] is None
    # proje dizini adı da bilgi taşımadığında (tek yedek o) grup bilinmiyordur;
    # Windows " " adlı dizin açamadığı için yedeği burada elle boşaltıyoruz
    for kayit in defter["dosyalar"].values():
        kayit["proje"] = ""
    harcama_defteri._turet(defter)

    gruplar = defter["gruplar"][_gun_yerel(ts)]
    assert harcama_defteri.GRUP_BILINMIYOR in gruplar
    assert gruplar[harcama_defteri.GRUP_BILINMIYOR]["cikti"] == 700


def test_codex_oturumu_session_meta_cwdsinden_siniflanir(defter_ortami):
    _projeler, codex = defter_ortami
    ts = "2026-09-03T10:00:00.000Z"
    meta = {
        "timestamp": ts,
        "type": "session_meta",
        "payload": {"cwd": r"E:\OdenaWorks\10-Aktif\origin-of-memory"},
    }
    _helpers.write_jsonl(
        codex / "rollout-x.jsonl", [meta, _codex_satiri(ts, 100, 900, 50)]
    )
    harcama_defteri.topla()
    defter = _oku()

    gruplar = defter["gruplar"][_gun_yerel(ts)]
    assert gruplar[harcama_defteri.GRUP_GELISTIRME]["cikti"] == 900


def test_ozet_butce_tablosunu_basar(defter_ortami, capsys):
    projeler, _ = defter_ortami
    ts = dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
    _helpers.write_jsonl(
        projeler / "p" / "o.jsonl",
        [
            _claude_satiri(
                "msg_A",
                ts,
                cikti=12_000,
                cache_okuma=340_000,
                cwd=r"C:\Users\musta\AppData\Local\Temp\beyin-flush-zz",
            )
        ],
    )
    harcama_defteri.topla()
    harcama_defteri.ozet(2)
    cikti = capsys.readouterr().out
    assert "Bütçe (2 gün, amaç grubuna göre)" in cikti
    assert "bakım" in cikti
    assert "sınıflandırılamadı" in cikti  # yokluğu da açıkça söylenir
