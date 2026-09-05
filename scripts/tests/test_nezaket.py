# yazan: claude
# model: sonnet
"""A7 nezaket (politeness) layer: decision table, izin/queue/durum storage,
entrypoint gate integration, Ollama keep_alive wiring, and the Linux
no-probe contract."""

from __future__ import annotations

import contextlib
import ctypes
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest import mock
import urllib.error

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import compile as compile_module
import ingest
import ingest_common
import nezaket
import ollama_runner
import watcher


class DecisionTableTests(unittest.TestCase):
    """The pure ``karar`` function against the documented rule table."""

    def setUp(self) -> None:
        self.izin = nezaket.IzinListesi.varsayilan()

    def test_decision_rows(self) -> None:
        rows = [
            (
                "izinli-surec",
                nezaket.Okuma("UnrealEditor.exe", None, None, None, None),
                True,
                False,
                "izinli-surec:",
            ),
            (
                "ust-surec-izinli",
                nezaket.Okuma(None, "steam.exe", None, None, None),
                True,
                False,
                "ust-surec:",
            ),
            (
                "ust-surec-steamwebhelper-degil",
                nezaket.Okuma(None, "steamwebhelper.exe", None, None, None),
                False,
                False,
                "serbest",
            ),
            (
                "gpu-esik-ustu",
                nezaket.Okuma(None, None, 75, None, None),
                True,
                False,
                "gpu-yuk:",
            ),
            (
                "gpu-esik-ustu-ama-kendi-ollama-onplan",
                nezaket.Okuma("ollama.exe", None, 90, None, None),
                False,
                False,
                "kendi-yerel-model",
            ),
            (
                "gpu-esik-ustu-ama-kendi-ollama-ust-surec",
                nezaket.Okuma(None, "ollama_llama_server.exe", 90, None, None),
                False,
                False,
                "kendi-yerel-model",
            ),
            (
                "gpu-esik-altinda",
                nezaket.Okuma(None, None, 10, None, None),
                False,
                False,
                "serbest",
            ),
            (
                "tam-ekran-tanimasiz-oyun",
                nezaket.Okuma("SomeGame.exe", None, None, True, None),
                True,
                False,
                "oyun-sezgisi",
            ),
            (
                "tam-ekran-zararsiz-tarayici",
                nezaket.Okuma("chrome.exe", None, None, True, None),
                False,
                False,
                "tam-ekran-zararsiz:",
            ),
            (
                "her-sinyal-bilinmiyor",
                nezaket.Okuma(None, None, None, None, None),
                False,
                True,
                "sinyal-yok",
            ),
            (
                "bosta-serbest-esigi",
                nezaket.Okuma(None, None, None, None, 1000.0),
                False,
                False,
                "bosta",
            ),
            (
                "bosta-izinli-sureci-asamaz",
                nezaket.Okuma("UnrealEditor.exe", None, None, None, 5000.0),
                True,
                False,
                "izinli-surec:",
            ),
        ]
        for name, okuma, mesgul, bilinmiyor, neden_prefix in rows:
            with self.subTest(row=name):
                sonuc = nezaket.karar(okuma, self.izin)
                self.assertEqual(sonuc.mesgul, mesgul)
                self.assertEqual(sonuc.bilinmiyor, bilinmiyor)
                self.assertTrue(
                    sonuc.neden.startswith(neden_prefix),
                    f"{sonuc.neden!r} does not start with {neden_prefix!r}",
                )

    def test_case_insensitive_matching(self) -> None:
        okuma = nezaket.Okuma("unrealeditor.exe", None, None, None, None)
        self.assertTrue(nezaket.karar(okuma, self.izin).mesgul)

    def test_custom_gpu_threshold_and_bosta_window(self) -> None:
        izin = nezaket.IzinListesi(
            surecler=frozenset(),
            ust_surecler=frozenset(),
            gpu_esik=20,
            zararsiz_tam_ekran=frozenset(),
            bosta_serbest_sn=60.0,
        )
        self.assertTrue(nezaket.karar(nezaket.Okuma(None, None, 25, None, None), izin).mesgul)
        sonuc = nezaket.karar(nezaket.Okuma(None, None, None, None, 61.0), izin)
        self.assertFalse(sonuc.mesgul)
        self.assertEqual(sonuc.neden, "bosta")

    def test_own_ollama_gpu_load_is_not_treated_as_gaming(self) -> None:
        """The system's own local model must never look like a game to nezaket."""
        okuma = nezaket.Okuma("Ollama.exe", None, 95, None, None)
        sonuc = nezaket.karar(okuma, self.izin)
        self.assertFalse(sonuc.mesgul)
        self.assertEqual(sonuc.neden, "kendi-yerel-model")

    def test_ollama_process_name_matching_is_case_insensitive(self) -> None:
        okuma = nezaket.Okuma(None, "OLLAMA_LLAMA_SERVER.EXE", 95, None, None)
        sonuc = nezaket.karar(okuma, self.izin)
        self.assertFalse(sonuc.mesgul)
        self.assertEqual(sonuc.neden, "kendi-yerel-model")

    def test_izinli_surec_still_wins_over_the_ollama_exemption(self) -> None:
        """Explicit configuration always outranks the built-in heuristic."""
        izin = nezaket.IzinListesi(
            surecler=frozenset({"ollama.exe"}),
            ust_surecler=frozenset(),
            gpu_esik=60,
            zararsiz_tam_ekran=frozenset(),
            bosta_serbest_sn=900.0,
        )
        sonuc = nezaket.karar(nezaket.Okuma("ollama.exe", None, 90, None, None), izin)
        self.assertTrue(sonuc.mesgul)
        self.assertEqual(sonuc.neden, "izinli-surec:ollama.exe")


class GpuEsikEnvOverrideTests(unittest.TestCase):
    def test_default_gpu_esik_is_unchanged_without_the_env_var(self) -> None:
        with mock.patch.dict(nezaket.os.environ, {}, clear=True):
            self.assertEqual(nezaket.IzinListesi.varsayilan().gpu_esik, nezaket.DEFAULT_GPU_ESIK)

    def test_env_var_overrides_the_builtin_default(self) -> None:
        with mock.patch.dict(nezaket.os.environ, {"BEYIN_NEZAKET_GPU_ESIK": "15"}, clear=True):
            self.assertEqual(nezaket.IzinListesi.varsayilan().gpu_esik, 15)

    def test_env_var_overrides_a_stored_izin_file_value(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state_dir = Path(tmp)
            nezaket.IzinListesi.varsayilan_yaz(state_dir / nezaket.IZIN_NAME)
            payload = json.loads((state_dir / nezaket.IZIN_NAME).read_text(encoding="utf-8"))
            payload["gpu_esik"] = 40
            (state_dir / nezaket.IZIN_NAME).write_text(
                json.dumps(payload), encoding="utf-8"
            )
            with mock.patch.dict(
                nezaket.os.environ, {"BEYIN_NEZAKET_GPU_ESIK": "5"}, clear=True
            ):
                self.assertEqual(nezaket.IzinListesi.yukle(state_dir).gpu_esik, 5)

    def test_a_garbage_env_value_is_ignored(self) -> None:
        with mock.patch.dict(
            nezaket.os.environ, {"BEYIN_NEZAKET_GPU_ESIK": "not-a-number"}, clear=True
        ):
            self.assertEqual(nezaket.IzinListesi.varsayilan().gpu_esik, nezaket.DEFAULT_GPU_ESIK)


class IzinListesiTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state_dir = Path(self._temporary.name)

    def test_missing_file_returns_builtin_defaults(self) -> None:
        izin = nezaket.IzinListesi.yukle(self.state_dir)
        self.assertEqual(izin, nezaket.IzinListesi.varsayilan())
        self.assertIn("unrealeditor.exe", izin.surecler)
        self.assertIn("steam.exe", izin.ust_surecler)
        self.assertIn("chrome.exe", izin.zararsiz_tam_ekran)
        self.assertEqual(izin.gpu_esik, nezaket.DEFAULT_GPU_ESIK)
        self.assertEqual(izin.bosta_serbest_sn, nezaket.DEFAULT_BOSTA_SERBEST_SN)

    def test_partial_custom_file_fills_missing_keys_with_defaults(self) -> None:
        path = self.state_dir / nezaket.IZIN_NAME
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"gpu_esik": 42}), encoding="utf-8")
        izin = nezaket.IzinListesi.yukle(self.state_dir)
        self.assertEqual(izin.gpu_esik, 42)
        self.assertIn("unrealeditor.exe", izin.surecler)  # default preserved
        self.assertEqual(izin.bosta_serbest_sn, nezaket.DEFAULT_BOSTA_SERBEST_SN)

    def test_fully_custom_file_is_honoured(self) -> None:
        path = self.state_dir / nezaket.IZIN_NAME
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(
            json.dumps(
                {
                    "surecler": ["MyApp.exe"],
                    "ust_surecler": ["launcher.exe"],
                    "gpu_esik": 80,
                    "zararsiz_tam_ekran": ["notepad.exe"],
                    "bosta_serbest_sn": 120,
                }
            ),
            encoding="utf-8",
        )
        izin = nezaket.IzinListesi.yukle(self.state_dir)
        self.assertEqual(izin.surecler, frozenset({"myapp.exe"}))
        self.assertEqual(izin.ust_surecler, frozenset({"launcher.exe"}))
        self.assertEqual(izin.gpu_esik, 80)
        self.assertEqual(izin.zararsiz_tam_ekran, frozenset({"notepad.exe"}))
        self.assertEqual(izin.bosta_serbest_sn, 120.0)

    def test_malformed_json_fails_loud(self) -> None:
        path = self.state_dir / nezaket.IZIN_NAME
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("{not-json", encoding="utf-8")
        with self.assertRaises(nezaket.IzinBozukHata) as ctx:
            nezaket.IzinListesi.yukle(self.state_dir)
        self.assertIn(nezaket.IZIN_BOZUK_SLUG, str(ctx.exception))

    def test_wrong_shape_fails_loud(self) -> None:
        path = self.state_dir / nezaket.IZIN_NAME
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"surecler": "not-a-list"}), encoding="utf-8")
        with self.assertRaises(nezaket.IzinBozukHata):
            nezaket.IzinListesi.yukle(self.state_dir)

    def test_root_not_object_fails_loud(self) -> None:
        path = self.state_dir / nezaket.IZIN_NAME
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps([1, 2, 3]), encoding="utf-8")
        with self.assertRaises(nezaket.IzinBozukHata):
            nezaket.IzinListesi.yukle(self.state_dir)

    def test_varsayilan_yaz_round_trips(self) -> None:
        path = self.state_dir / nezaket.IZIN_NAME
        nezaket.IzinListesi.varsayilan_yaz(path)
        self.assertTrue(path.exists())
        izin = nezaket.IzinListesi.yukle(self.state_dir)
        self.assertEqual(izin, nezaket.IzinListesi.varsayilan())


class IngestNezaketDelArgumentOrderTests(unittest.TestCase):
    """``--nezaket-del`` is only defined on ingest.py's top-level parser, not
    on its subparsers — beyin.ps1's Get-NezaketOperationCommand must place it
    BEFORE the queued argv (which, for ingest, starts with the subcommand
    token), never after. This documents why the order matters, in the same
    language the panel replay path uses."""

    def test_leading_nezaket_del_parses_before_the_subcommand(self) -> None:
        namespace = ingest._parse_args(["--nezaket-del", "claude"])
        self.assertTrue(namespace.nezaket_del)
        self.assertEqual(namespace.command, "claude")

    def test_trailing_nezaket_del_after_the_subcommand_is_rejected(self) -> None:
        # --nezaket-del is not registered on the "claude" subparser, so once
        # argparse has consumed "claude" as the subcommand it treats a
        # trailing --nezaket-del as unrecognized and exits 2 — the exact
        # failure mode that made a panel replay of a queued ingest operation
        # silently no-op as "invalid-arguments" before this was fixed.
        with self.assertRaises(SystemExit) as ctx:
            ingest._parse_args(["claude", "--nezaket-del"])
        self.assertEqual(ctx.exception.code, 2)


class KuyrukTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state_dir = Path(self._temporary.name)
        self.kuyruk = nezaket.Kuyruk(self.state_dir)

    def test_ekle_dedups_on_tur_and_argv(self) -> None:
        first = self.kuyruk.ekle("compile", [], "izinli-surec:UnrealEditor.exe")
        second = self.kuyruk.ekle("compile", [], "izinli-surec:UnrealEditor.exe")
        self.assertEqual(first["id"], second["id"])
        self.assertEqual(len(self.kuyruk.listele()), 1)

    def test_ekle_keeps_distinct_argv_separate(self) -> None:
        self.kuyruk.ekle("ingest", ["claude"], "izinli-surec:x")
        self.kuyruk.ekle("ingest", ["codex"], "izinli-surec:x")
        self.assertEqual(len(self.kuyruk.listele()), 2)

    def test_serbest_pops_only_given_ids(self) -> None:
        a = self.kuyruk.ekle("compile", [], "a")
        b = self.kuyruk.ekle("watcher", ["--once"], "b")
        released = self.kuyruk.serbest([a["id"]])
        self.assertEqual([r["id"] for r in released], [a["id"]])
        remaining = self.kuyruk.listele()
        self.assertEqual([r["id"] for r in remaining], [b["id"]])

    def test_serbest_with_no_ids_releases_nothing(self) -> None:
        self.kuyruk.ekle("compile", [], "a")
        released = self.kuyruk.serbest([])
        self.assertEqual(released, [])
        self.assertEqual(len(self.kuyruk.listele()), 1)

    def test_kaldir_discards_without_returning_them_as_runnable(self) -> None:
        a = self.kuyruk.ekle("compile", [], "a")
        discarded = self.kuyruk.kaldir([a["id"]])
        self.assertEqual([r["id"] for r in discarded], [a["id"]])
        self.assertEqual(self.kuyruk.listele(), [])

    def test_queue_file_is_valid_json_after_writes(self) -> None:
        self.kuyruk.ekle("compile", [], "a")
        raw = json.loads((self.state_dir / nezaket.KUYRUK_NAME).read_text(encoding="utf-8"))
        self.assertIn("kayitlar", raw)
        self.assertEqual(len(raw["kayitlar"]), 1)

    def test_en_eski_yas_sn_measures_from_oldest_record(self) -> None:
        record = self.kuyruk.ekle("compile", [], "a")
        # Rewrite the record's timestamp directly to a known past instant.
        records = self.kuyruk._oku()
        records[0]["eklendi"] = "2020-01-01T00:00:00+00:00"
        self.kuyruk._yaz(records)
        import datetime as dt

        now = dt.datetime(2020, 1, 1, 1, 0, 0, tzinfo=dt.timezone.utc).timestamp()
        age = self.kuyruk.en_eski_yas_sn(now=now)
        self.assertAlmostEqual(age, 3600.0, delta=1.0)
        del record

    def test_en_eski_yas_sn_none_when_empty(self) -> None:
        self.assertIsNone(self.kuyruk.en_eski_yas_sn())

    def test_concurrent_ekle_from_threads_keeps_every_record(self) -> None:
        errors: list[BaseException] = []

        def worker(index: int) -> None:
            try:
                self.kuyruk.ekle("ingest", [f"job-{index}"], "concurrent")
            except BaseException as exc:  # pragma: no cover - failure path
                errors.append(exc)

        threads = [threading.Thread(target=worker, args=(i,)) for i in range(12)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()

        self.assertEqual(errors, [])
        records = self.kuyruk.listele()
        self.assertEqual(len(records), 12)
        ids = {record["id"] for record in records}
        self.assertEqual(len(ids), 12)  # no collisions, no clobbered writes
        raw = json.loads((self.state_dir / nezaket.KUYRUK_NAME).read_text(encoding="utf-8"))
        self.assertEqual(len(raw["kayitlar"]), 12)


class DurumTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state_dir = Path(self._temporary.name)
        self.durum = nezaket.Durum(self.state_dir)

    def test_first_busy_reports_transition_and_stamps_started(self) -> None:
        gecis = self.durum.guncelle(nezaket.Karar(True, "izinli-surec:x", False))
        self.assertEqual(gecis, "serbest->mesgul")
        payload = self.durum.oku()
        self.assertTrue(payload["mesgul"])
        self.assertIn("mesgul_basladi", payload)

    def test_returning_to_free_reports_transition_and_clears_started(self) -> None:
        self.durum.guncelle(nezaket.Karar(True, "izinli-surec:x", False))
        gecis = self.durum.guncelle(nezaket.Karar(False, "serbest", False))
        self.assertEqual(gecis, "mesgul->serbest")
        self.assertNotIn("mesgul_basladi", self.durum.oku())

    def test_same_state_reports_no_transition(self) -> None:
        self.durum.guncelle(nezaket.Karar(False, "serbest", False))
        gecis = self.durum.guncelle(nezaket.Karar(False, "bosta", False))
        self.assertIsNone(gecis)

    def test_son_karar_reflects_last_write(self) -> None:
        self.assertIsNone(nezaket.son_karar(self.state_dir))
        self.durum.guncelle(nezaket.Karar(True, "izinli-surec:x", False))
        self.assertTrue(nezaket.son_karar(self.state_dir))
        self.durum.guncelle(nezaket.Karar(False, "serbest", False))
        self.assertFalse(nezaket.son_karar(self.state_dir))

    def test_guncelle_and_oku_take_the_same_exclusive_lock_kuyruk_uses(self) -> None:
        # Same _kilitli()/_lock_exclusive pattern as Kuyruk — an unlocked
        # read-modify-write here could race with a concurrent writer.
        with mock.patch.object(nezaket, "_lock_exclusive") as locker:
            self.durum.guncelle(nezaket.Karar(True, "izinli-surec:x", False))
            self.durum.oku()
        self.assertEqual(locker.call_count, 2)
        self.assertTrue((self.state_dir / f".{nezaket.DURUM_NAME}.lock").exists())


class OkumaOnbellekTests(unittest.TestCase):
    """durum --json's probe cache (Durum.onbellekli_okuma/onbellek_yaz):
    reused inside BEYIN_NEZAKET_ONBELLEK_SN, missed once it ages past it —
    kapi()/mesgul_mu()'s real gating and ``sonda`` never consult it."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state_dir = Path(self._temporary.name)
        self.durum = nezaket.Durum(self.state_dir)

    def test_miss_when_nothing_cached_yet(self) -> None:
        self.assertIsNone(self.durum.onbellekli_okuma(8.0, simdi=lambda: 100.0))

    def test_hit_inside_window_miss_once_older(self) -> None:
        okuma = nezaket.Okuma("blender.exe", None, 12, False, 5.0)
        self.durum.onbellek_yaz(okuma, simdi=lambda: 100.0)

        hit = self.durum.onbellekli_okuma(8.0, simdi=lambda: 107.9)
        self.assertEqual(hit, okuma)

        miss = self.durum.onbellekli_okuma(8.0, simdi=lambda: 108.1)
        self.assertIsNone(miss)

    def test_module_helper_probes_once_then_reuses_within_the_window(self) -> None:
        readings = [
            nezaket.Okuma("a.exe", None, None, None, None),
            nezaket.Okuma("b.exe", None, None, None, None),
        ]
        clock = {"t": 0.0}
        with mock.patch.object(
            nezaket, "_oku_gercek", side_effect=lambda: readings.pop(0)
        ) as prober:
            first = nezaket._onbellekli_oku_gercek(self.state_dir, simdi=lambda: clock["t"])
            clock["t"] = 2.0  # inside the default 8s window
            second = nezaket._onbellekli_oku_gercek(self.state_dir, simdi=lambda: clock["t"])
        self.assertEqual(first.surec, "a.exe")
        self.assertEqual(second.surec, "a.exe")  # cache hit — no second probe
        prober.assert_called_once()

    def test_module_helper_probes_again_once_the_cache_expires(self) -> None:
        readings = [
            nezaket.Okuma("a.exe", None, None, None, None),
            nezaket.Okuma("b.exe", None, None, None, None),
        ]
        clock = {"t": 0.0}
        with mock.patch.object(
            nezaket, "_oku_gercek", side_effect=lambda: readings.pop(0)
        ) as prober:
            nezaket._onbellekli_oku_gercek(self.state_dir, simdi=lambda: clock["t"])
            clock["t"] = 8.1  # past the default 8s window
            second = nezaket._onbellekli_oku_gercek(self.state_dir, simdi=lambda: clock["t"])
        self.assertEqual(second.surec, "b.exe")
        self.assertEqual(prober.call_count, 2)

    def test_env_var_overrides_the_window_default(self) -> None:
        with mock.patch.dict("os.environ", {nezaket.NEZAKET_ONBELLEK_ENV: "0"}):
            self.assertEqual(nezaket._onbellek_sn(), 0.0)
        with mock.patch.dict("os.environ", {nezaket.NEZAKET_ONBELLEK_ENV: "not-a-number"}):
            self.assertEqual(nezaket._onbellek_sn(), nezaket.DEFAULT_ONBELLEK_SN)
        with mock.patch.dict("os.environ", {}, clear=False):
            os_environ_backup = nezaket.os.environ.pop(nezaket.NEZAKET_ONBELLEK_ENV, None)
            try:
                self.assertEqual(nezaket._onbellek_sn(), nezaket.DEFAULT_ONBELLEK_SN)
            finally:
                if os_environ_backup is not None:
                    nezaket.os.environ[nezaket.NEZAKET_ONBELLEK_ENV] = os_environ_backup

    def test_sonda_never_consults_the_cache(self) -> None:
        import inspect

        source = inspect.getsource(nezaket._cmd_sonda)
        self.assertNotIn("onbellek", source)


class VramBosaltTests(unittest.TestCase):
    class _Response:
        def __init__(self, body: bytes):
            self.body = body

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def read(self) -> bytes:
            return self.body

    def test_unloads_every_loaded_model_with_keep_alive_zero(self) -> None:
        ps_body = json.dumps({"models": [{"name": "llama3"}, {"name": "qwen3:8b"}]}).encode("utf-8")
        responses = [self._Response(ps_body), self._Response(b"{}"), self._Response(b"{}")]

        def fake_urlopen(request, timeout=None):
            return responses.pop(0)

        with mock.patch.object(nezaket.urllib.request, "urlopen", side_effect=fake_urlopen) as opened:
            problems = nezaket.vram_bosalt("http://127.0.0.1:11434")

        self.assertEqual(problems, [])
        self.assertEqual(opened.call_count, 3)
        first_call = opened.call_args_list[1][0][0]
        self.assertEqual(first_call.full_url, "http://127.0.0.1:11434/api/generate")
        self.assertEqual(json.loads(first_call.data.decode("utf-8")), {"model": "llama3", "keep_alive": 0})
        second_call = opened.call_args_list[2][0][0]
        self.assertEqual(json.loads(second_call.data.decode("utf-8")), {"model": "qwen3:8b", "keep_alive": 0})

    def test_ps_failure_is_swallowed_into_problem_list(self) -> None:
        with mock.patch.object(
            nezaket.urllib.request,
            "urlopen",
            side_effect=urllib.error.URLError("refused"),
        ):
            problems = nezaket.vram_bosalt("http://127.0.0.1:11434")
        self.assertEqual(len(problems), 1)
        self.assertTrue(problems[0].startswith("ps-basarisiz:"))

    def test_never_raises_when_a_single_unload_fails(self) -> None:
        ps_body = json.dumps({"models": [{"name": "a"}, {"name": "b"}]}).encode("utf-8")

        def fake_urlopen(request, timeout=None):
            url = getattr(request, "full_url", request)
            if url.endswith("/api/ps"):
                return self._Response(ps_body)
            if json.loads(request.data.decode("utf-8"))["model"] == "a":
                raise urllib.error.URLError("nope")
            return self._Response(b"{}")

        with mock.patch.object(nezaket.urllib.request, "urlopen", side_effect=fake_urlopen):
            problems = nezaket.vram_bosalt("http://127.0.0.1:11434")
        self.assertEqual(len(problems), 1)
        self.assertTrue(problems[0].startswith("bosaltma-basarisiz:a:"))

    def test_with_state_dir_only_tracked_models_are_unloaded(self) -> None:
        """A model the user loaded by hand for their own work is left alone."""
        with tempfile.TemporaryDirectory() as tmp:
            state_dir = Path(tmp)
            nezaket.OllamaYukler(state_dir).kaydet("qwen3:8b")

            ps_body = json.dumps(
                {"models": [{"name": "llama3-users-own"}, {"name": "qwen3:8b"}]}
            ).encode("utf-8")
            responses = [self._Response(ps_body), self._Response(b"{}")]

            def fake_urlopen(request, timeout=None):
                return responses.pop(0)

            with mock.patch.object(
                nezaket.urllib.request, "urlopen", side_effect=fake_urlopen
            ) as opened:
                problems = nezaket.vram_bosalt(
                    "http://127.0.0.1:11434", state_dir=state_dir
                )

        self.assertEqual(problems, [])
        # One call for /api/ps, exactly one unload — never llama3-users-own.
        self.assertEqual(opened.call_count, 2)
        unload_call = opened.call_args_list[1][0][0]
        self.assertEqual(
            json.loads(unload_call.data.decode("utf-8")),
            {"model": "qwen3:8b", "keep_alive": 0},
        )

    def test_with_state_dir_and_nothing_tracked_unloads_nothing(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state_dir = Path(tmp)
            ps_body = json.dumps({"models": [{"name": "llama3-users-own"}]}).encode("utf-8")

            with mock.patch.object(
                nezaket.urllib.request, "urlopen", return_value=self._Response(ps_body)
            ) as opened:
                problems = nezaket.vram_bosalt(
                    "http://127.0.0.1:11434", state_dir=state_dir
                )

        self.assertEqual(problems, [])
        opened.assert_called_once()  # only /api/ps — no generate/unload call


class OllamaYuklerTests(unittest.TestCase):
    def test_kaydet_then_yuklenenler_round_trips(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            yukler = nezaket.OllamaYukler(Path(tmp))
            yukler.kaydet("qwen3:8b")
            yukler.kaydet("llama3")
            self.assertEqual(yukler.yuklenenler(), {"qwen3:8b", "llama3"})

    def test_kaydet_is_idempotent(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            yukler = nezaket.OllamaYukler(Path(tmp))
            yukler.kaydet("qwen3:8b")
            yukler.kaydet("qwen3:8b")
            self.assertEqual(yukler.yuklenenler(), {"qwen3:8b"})

    def test_yuklenenler_on_a_missing_file_is_empty(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(nezaket.OllamaYukler(Path(tmp)).yuklenenler(), set())

    def test_kaydet_with_empty_model_name_is_a_noop(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            yukler = nezaket.OllamaYukler(Path(tmp))
            yukler.kaydet("")
            self.assertEqual(yukler.yuklenenler(), set())


class LinuxContractTests(unittest.TestCase):
    """SinyalKaynagi must be entirely inert off Windows."""

    def test_every_probe_returns_none_without_raising(self) -> None:
        kaynak = nezaket.SinyalKaynagi()
        with mock.patch.object(nezaket.os, "name", "posix"):
            self.assertIsNone(kaynak.on_plan_surec())
            self.assertIsNone(kaynak.ust_surec(1234))
            self.assertIsNone(kaynak.gpu_yuk())
            self.assertIsNone(kaynak.tam_ekran_mi())
            self.assertIsNone(kaynak.bosta_saniye())

    def test_oku_gercek_is_all_none_off_windows(self) -> None:
        with mock.patch.object(nezaket.os, "name", "posix"):
            okuma = nezaket._oku_gercek()
        self.assertEqual(okuma, nezaket.Okuma(None, None, None, None, None))


class PriorityFlagTests(unittest.TestCase):
    def test_zero_on_non_windows(self) -> None:
        with mock.patch.object(nezaket.sys, "platform", "linux"):
            self.assertEqual(nezaket.dusuk_oncelik_bayraklari(), 0)

    def test_idle_priority_class_on_windows(self) -> None:
        with mock.patch.object(nezaket.sys, "platform", "win32"):
            self.assertEqual(nezaket.dusuk_oncelik_bayraklari(), 0x00000040)

    def test_kendini_arka_plana_al_is_noop_off_windows(self) -> None:
        with mock.patch.object(nezaket.sys, "platform", "linux"):
            nezaket.kendini_arka_plana_al()  # must not raise, must not touch ctypes


class WindowsBilinmiyorHealthNoteTests(unittest.TestCase):
    """The bilinmiyor health note is a Windows-only courtesy (see kapi())."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state_dir = Path(self._temporary.name)

    def _bilinmiyor_okuma(self) -> nezaket.Okuma:
        return nezaket.Okuma(None, None, None, None, None)

    def test_first_bilinmiyor_writes_note_once_on_windows(self) -> None:
        with mock.patch.object(nezaket, "_windows_mu", return_value=True):
            sonuc = nezaket.kapi(
                "compile", [], self.state_dir, oku=self._bilinmiyor_okuma
            )
            self.assertTrue(sonuc.bilinmiyor)
            health = json.loads((self.state_dir / "health.json").read_text(encoding="utf-8"))
            self.assertEqual(health["error"], nezaket.NEZAKET_BILINMIYOR_SLUG)

            # A second call while still bilinmiyor must not spam a fresh note;
            # clear the error to prove it is not rewritten.
            (self.state_dir / "health.json").write_text(
                json.dumps({"error": ""}), encoding="utf-8"
            )
            nezaket.kapi("compile", [], self.state_dir, oku=self._bilinmiyor_okuma)
            health_after = json.loads((self.state_dir / "health.json").read_text(encoding="utf-8"))
            self.assertEqual(health_after["error"], "")

    def test_bilinmiyor_writes_nothing_off_windows(self) -> None:
        with mock.patch.object(nezaket, "_windows_mu", return_value=False):
            nezaket.kapi("compile", [], self.state_dir, oku=self._bilinmiyor_okuma)
        self.assertFalse((self.state_dir / "health.json").exists())


class KapiTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state_dir = Path(self._temporary.name)

    def _busy_okuma(self) -> nezaket.Okuma:
        return nezaket.Okuma("UnrealEditor.exe", None, None, None, None)

    def _free_okuma(self) -> nezaket.Okuma:
        return nezaket.Okuma(None, None, 5, None, None)

    def test_busy_queues_and_marks_ertelendi(self) -> None:
        sonuc = nezaket.kapi(
            "compile", ["compile"], self.state_dir, oku=self._busy_okuma
        )
        self.assertTrue(sonuc.mesgul)
        kayitlar = nezaket.Kuyruk(self.state_dir).listele()
        self.assertEqual(len(kayitlar), 1)
        self.assertEqual(kayitlar[0]["tur"], "compile")
        self.assertEqual(kayitlar[0]["argv"], ["compile"])
        health = json.loads((self.state_dir / "health.json").read_text(encoding="utf-8"))
        skips = health.get("skips", [])
        self.assertTrue(any(item.get("reason") == nezaket.NEZAKET_ERTELENDI_SLUG for item in skips))

    def test_free_does_not_queue(self) -> None:
        sonuc = nezaket.kapi("compile", [], self.state_dir, oku=self._free_okuma)
        self.assertFalse(sonuc.mesgul)
        self.assertEqual(nezaket.Kuyruk(self.state_dir).listele(), [])

    def test_env_off_skips_probe_entirely(self) -> None:
        probe_calls = {"n": 0}

        def counting_oku() -> nezaket.Okuma:
            probe_calls["n"] += 1
            return self._busy_okuma()

        sonuc = nezaket.kapi(
            "compile",
            [],
            self.state_dir,
            environment={"BEYIN_NEZAKET": "off"},
            oku=counting_oku,
        )
        self.assertFalse(sonuc.mesgul)
        self.assertEqual(probe_calls["n"], 0)
        self.assertFalse((self.state_dir / nezaket.DURUM_NAME).exists())

    def test_nezaket_del_flag_bypasses_gate(self) -> None:
        probe_calls = {"n": 0}

        def counting_oku() -> nezaket.Okuma:
            probe_calls["n"] += 1
            return self._busy_okuma()

        sonuc = nezaket.kapi(
            "compile", ["compile", "--nezaket-del"], self.state_dir, oku=counting_oku
        )
        self.assertFalse(sonuc.mesgul)
        self.assertEqual(probe_calls["n"], 0)

    def test_izin_bozuk_fails_open_and_writes_loud_health(self) -> None:
        path = self.state_dir / nezaket.IZIN_NAME
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("{bozuk", encoding="utf-8")
        sonuc = nezaket.kapi("compile", [], self.state_dir, oku=self._busy_okuma)
        self.assertFalse(sonuc.mesgul)  # fail-open: real work still runs
        health = json.loads((self.state_dir / "health.json").read_text(encoding="utf-8"))
        self.assertIn(nezaket.IZIN_BOZUK_SLUG, health["error"])
        # IzinListesi.yukle itself must still raise when called directly.
        with self.assertRaises(nezaket.IzinBozukHata):
            nezaket.IzinListesi.yukle(self.state_dir)

    def test_transition_to_busy_triggers_vram_bosalt(self) -> None:
        with mock.patch.object(nezaket, "vram_bosalt") as bosalt:
            nezaket.kapi("compile", [], self.state_dir, oku=self._busy_okuma)
        bosalt.assert_called_once()

    def test_no_vram_bosalt_when_already_busy(self) -> None:
        nezaket.kapi("compile", [], self.state_dir, oku=self._busy_okuma)
        with mock.patch.object(nezaket, "vram_bosalt") as bosalt:
            nezaket.kapi("compile", [], self.state_dir, oku=self._busy_okuma)
        bosalt.assert_not_called()

    def test_watcher_and_ingest_share_the_ingest_health_store(self) -> None:
        nezaket.kapi("watcher", [], self.state_dir, oku=self._busy_okuma)
        health = json.loads(
            (self.state_dir / "ingest-health.json").read_text(encoding="utf-8")
        )
        self.assertTrue(
            any(
                item.get("reason") == nezaket.NEZAKET_ERTELENDI_SLUG
                for item in health.get("skips", [])
            )
        )
        self.assertFalse((self.state_dir / "health.json").exists())


class EntrypointGateTests(unittest.TestCase):
    """Each entrypoint must refuse to spawn/lock while busy, and exit 75."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state_dir = Path(self._temporary.name) / ".state"
        self.state_dir.mkdir(parents=True)
        busy_okuma = nezaket.Okuma("UnrealEditor.exe", None, None, None, None)
        patcher = mock.patch.object(nezaket, "_oku_gercek", return_value=busy_okuma)
        patcher.start()
        self.addCleanup(patcher.stop)
        env_patcher = mock.patch.dict("os.environ", {"BEYIN_INVOKED_BY": ""})
        env_patcher.start()
        self.addCleanup(env_patcher.stop)

    def test_compile_main_refuses_to_lock_while_busy(self) -> None:
        counter = mock.Mock()
        with mock.patch.object(compile_module, "STATE_DIR", self.state_dir), mock.patch.object(
            compile_module, "_lock_exclusive", counter
        ):
            code = compile_module.main([])
        self.assertEqual(code, nezaket.EX_TEMPFAIL)
        counter.assert_not_called()
        self.assertEqual(len(nezaket.Kuyruk(self.state_dir).listele()), 1)

    def test_compile_dry_run_is_exempt_from_the_gate(self) -> None:
        # Read-only and lock-free already; the gate must not add a trace here
        # (this also protects test_compile_state's "leaves no trace" contract).
        with mock.patch.object(compile_module, "STATE_DIR", self.state_dir), mock.patch.object(
            compile_module, "VAULT_ROOT", Path(self._temporary.name)
        ):
            daily = Path(self._temporary.name) / "daily"
            daily.mkdir()
            code = compile_module.main(["--dry-run"])
        self.assertEqual(code, 0)
        self.assertFalse((self.state_dir / "health.json").exists())

    def test_watcher_main_refuses_to_sweep_while_busy(self) -> None:
        sweep_counter = mock.Mock()
        with mock.patch.object(ingest_common, "STATE_DIR", self.state_dir), mock.patch.object(
            watcher, "sweep", sweep_counter
        ):
            code = watcher.main(["--once"])
        self.assertEqual(code, nezaket.EX_TEMPFAIL)
        sweep_counter.assert_not_called()
        self.assertEqual(len(nezaket.Kuyruk(self.state_dir).listele()), 1)

    def test_ingest_main_refuses_to_lock_while_busy(self) -> None:
        calls = {"n": 0}

        @contextlib.contextmanager
        def fake_lock(_state_dir):
            calls["n"] += 1
            yield True

        with mock.patch.object(ingest_common, "STATE_DIR", self.state_dir), mock.patch.object(
            ingest_common, "exclusive_lock", fake_lock
        ):
            code = ingest.main(["claude"])
        self.assertEqual(code, nezaket.EX_TEMPFAIL)
        self.assertEqual(calls["n"], 0)
        self.assertEqual(len(nezaket.Kuyruk(self.state_dir).listele()), 1)

    def test_env_off_lets_compile_proceed_to_the_lock(self) -> None:
        counter = mock.Mock()
        with mock.patch.object(compile_module, "STATE_DIR", self.state_dir), mock.patch.object(
            compile_module, "_lock_exclusive", counter
        ), mock.patch.dict("os.environ", {"BEYIN_NEZAKET": "off"}):
            compile_module.main([])
        counter.assert_called_once()

    def test_nezaket_del_lets_compile_proceed_to_the_lock(self) -> None:
        counter = mock.Mock()
        with mock.patch.object(compile_module, "STATE_DIR", self.state_dir), mock.patch.object(
            compile_module, "_lock_exclusive", counter
        ):
            compile_module.main(["--nezaket-del"])
        counter.assert_called_once()


class WatcherLoopTests(unittest.TestCase):
    """Loop-mode watcher must re-evaluate busy/free on every iteration
    instead of gating once before ``while True:`` — a watcher started while
    free must notice a later busy spell instead of spawning model children
    under it, and it must never exit because of busy (only ``--once`` does
    that)."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state_dir = Path(self._temporary.name) / ".state"
        self.state_dir.mkdir(parents=True)
        env_patcher = mock.patch.dict("os.environ", {"BEYIN_INVOKED_BY": ""})
        env_patcher.start()
        self.addCleanup(env_patcher.stop)

    def test_loop_skips_sweeps_while_busy_resumes_when_free_no_queue_record(
        self,
    ) -> None:
        sweep_counter = mock.Mock()
        skip_counter = mock.Mock()
        free_okuma = nezaket.Okuma(None, None, 0, False, 0.0)
        busy_okuma = nezaket.Okuma("UnrealEditor.exe", None, None, None, None)
        # free -> busy -> busy -> free, then the loop must ask again — used
        # here to end the test deterministically rather than to model a real
        # busy/free signal.
        sequence = [free_okuma, busy_okuma, busy_okuma, free_okuma]

        def fake_oku_gercek() -> nezaket.Okuma:
            if sequence:
                return sequence.pop(0)
            raise KeyboardInterrupt

        with mock.patch.object(
            ingest_common, "STATE_DIR", self.state_dir
        ), mock.patch.object(watcher, "sweep", sweep_counter), mock.patch.object(
            nezaket, "_oku_gercek", side_effect=fake_oku_gercek
        ), mock.patch.object(
            watcher, "write_health_skip", skip_counter
        ), mock.patch.object(watcher.time, "sleep", mock.Mock()):
            code = watcher.main([])

        self.assertEqual(code, 0)
        # Swept on the two free reads (index 0 and 3); skipped on both busy
        # reads in between — never exited because of them.
        self.assertEqual(sweep_counter.call_count, 2)
        # One free->busy transition happened (index 0 -> 1); the health skip
        # must be written exactly once for it, not once per busy iteration.
        skip_counter.assert_called_once()
        # Loop mode re-evaluates; it must never enqueue a deferred record.
        self.assertEqual(len(nezaket.Kuyruk(self.state_dir).listele()), 0)

    def test_once_mode_unaffected_gate_then_exit_and_no_reevaluation(self) -> None:
        # --once must still behave exactly like compile: gate once, and if
        # free, sweep exactly once and return — no loop, no re-evaluation.
        sweep_counter = mock.Mock()
        free_okuma = nezaket.Okuma(None, None, 0, False, 0.0)
        with mock.patch.object(
            ingest_common, "STATE_DIR", self.state_dir
        ), mock.patch.object(watcher, "sweep", sweep_counter), mock.patch.object(
            nezaket, "_oku_gercek", return_value=free_okuma
        ):
            code = watcher.main(["--once"])
        self.assertEqual(code, 0)
        sweep_counter.assert_called_once()


class OllamaKeepAliveTests(unittest.TestCase):
    def test_busy_forces_keep_alive_zero_even_with_env_set(self) -> None:
        extras = ollama_runner._request_extras(
            {"BEYIN_OLLAMA_KEEP_ALIVE": "30m"}, mesgul=True
        )
        self.assertEqual(extras["keep_alive"], 0)

    def test_not_busy_forwards_env_keep_alive_string(self) -> None:
        extras = ollama_runner._request_extras(
            {"BEYIN_OLLAMA_KEEP_ALIVE": "30m"}, mesgul=False
        )
        self.assertEqual(extras["keep_alive"], "30m")

    def test_unknown_mesgul_forwards_env_keep_alive(self) -> None:
        extras = ollama_runner._request_extras(
            {"BEYIN_OLLAMA_KEEP_ALIVE": "5m"}, mesgul=None
        )
        self.assertEqual(extras["keep_alive"], "5m")

    def test_unset_env_and_not_busy_omits_keep_alive_entirely(self) -> None:
        extras = ollama_runner._request_extras({}, mesgul=False)
        self.assertNotIn("keep_alive", extras)

    def test_default_call_without_mesgul_is_byte_identical_to_before(self) -> None:
        extras = ollama_runner._request_extras({})
        self.assertEqual(extras, {"think": False, "options": {"num_predict": -1}})

    def test_run_ollama_reads_state_dir_for_busy_decision(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            state_dir = Path(directory)
            nezaket.Durum(state_dir).guncelle(nezaket.Karar(True, "izinli-surec:x", False))

            captured: dict = {}

            class _Response:
                def __enter__(self):
                    return self

                def __exit__(self, *_args):
                    return False

                def read(self) -> bytes:
                    return b'{"response":"ok"}'

            def fake_urlopen(request, timeout=None):
                captured["body"] = json.loads(request.data.decode("utf-8"))
                return _Response()

            environment = {
                "BEYIN_OLLAMA_MODEL_FAST": "local-fast",
                "BEYIN_OLLAMA_URL": "http://127.0.0.1:9999/",
            }
            with mock.patch.dict(
                ollama_runner.os.environ, environment, clear=True
            ), mock.patch.object(
                ollama_runner.urllib.request, "urlopen", side_effect=fake_urlopen
            ):
                ollama_runner.run_ollama(
                    "prompt", model="haiku", timeout=5, state_dir=state_dir
                )
            self.assertEqual(captured["body"]["keep_alive"], 0)

    def test_run_ollama_without_state_dir_omits_keep_alive(self) -> None:
        captured: dict = {}

        class _Response:
            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            def read(self) -> bytes:
                return b'{"response":"ok"}'

        def fake_urlopen(request, timeout=None):
            captured["body"] = json.loads(request.data.decode("utf-8"))
            return _Response()

        environment = {
            "BEYIN_OLLAMA_MODEL_FAST": "local-fast",
            "BEYIN_OLLAMA_URL": "http://127.0.0.1:9999/",
        }
        with mock.patch.dict(
            ollama_runner.os.environ, environment, clear=True
        ), mock.patch.object(
            ollama_runner.urllib.request, "urlopen", side_effect=fake_urlopen
        ):
            ollama_runner.run_ollama("prompt", model="haiku", timeout=5)
        self.assertNotIn("keep_alive", captured["body"])


class Win32BindingTests(unittest.TestCase):
    """``_win32_baglama`` must declare argtypes/restype on every raw WinAPI
    entry point this module calls — left untyped, ctypes assumes a 32-bit
    ``c_int`` return, which is how ``GetTickCount64`` silently truncated and
    idle detection died after ~24.8 days of uptime. Runs on Linux by passing
    fake namespace objects instead of the real ``ctypes.windll`` tables."""

    def setUp(self) -> None:
        self.user32 = mock.MagicMock(name="user32")
        self.kernel32 = mock.MagicMock(name="kernel32")
        nezaket._win32_baglama(self.user32, self.kernel32)

    def test_every_bound_function_has_restype_and_argtypes(self) -> None:
        bound = [
            (self.user32, "GetForegroundWindow"),
            (self.user32, "GetWindowThreadProcessId"),
            (self.kernel32, "OpenProcess"),
            (self.kernel32, "QueryFullProcessImageNameW"),
            (self.kernel32, "CloseHandle"),
            (self.kernel32, "CreateToolhelp32Snapshot"),
            (self.kernel32, "Process32First"),
            (self.kernel32, "Process32Next"),
            (self.user32, "MonitorFromWindow"),
            (self.user32, "GetMonitorInfoW"),
            (self.user32, "GetWindowRect"),
            (self.user32, "GetLastInputInfo"),
            (self.kernel32, "GetTickCount64"),
            (self.kernel32, "GetCurrentProcess"),
            (self.kernel32, "SetPriorityClass"),
        ]
        for namespace, name in bound:
            func = getattr(namespace, name)
            self.assertIsNotNone(
                func.restype, f"{name}.restype was never assigned"
            )
            self.assertIsNotNone(
                func.argtypes, f"{name}.argtypes was never assigned"
            )

    def test_get_tick_count_64_restype_is_64_bit(self) -> None:
        # The specific regression: GetTickCount64 left untyped defaults to a
        # 32-bit signed c_int and silently truncates the millisecond counter.
        self.assertEqual(self.kernel32.GetTickCount64.restype, ctypes.c_uint64)
        self.assertEqual(self.kernel32.GetTickCount64.argtypes, [])

    def test_ensure_win32_baglama_is_noop_off_windows(self) -> None:
        # os.name != "nt" on this test box; must not touch ctypes.windll
        # (which does not exist here) and must not raise.
        with mock.patch.object(nezaket, "_win32_bound", False):
            nezaket._ensure_win32_baglama()  # must not raise


class InvalidHandleTests(unittest.TestCase):
    """``_is_invalid_handle`` must catch both failure shapes a HANDLE-typed
    WinAPI call can return: NULL (-> ``None`` under the correct restype) and
    the all-ones ``INVALID_HANDLE_VALUE`` sentinel (-> a large positive int,
    not ``-1``, not ``None``, on a 64-bit process)."""

    def test_none_is_invalid(self) -> None:
        self.assertTrue(nezaket._is_invalid_handle(None))

    def test_invalid_handle_value_sentinel_is_invalid(self) -> None:
        sentinel = ctypes.c_void_p(-1).value
        self.assertTrue(nezaket._is_invalid_handle(sentinel))

    def test_real_handle_is_valid(self) -> None:
        self.assertFalse(nezaket._is_invalid_handle(0x1234))


if __name__ == "__main__":
    unittest.main()
