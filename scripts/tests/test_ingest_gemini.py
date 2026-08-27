# yazan: codex
# model: gpt-5.6-sol
"""Gemini Takeout ayrıştırma, manifest ve günlük ingest sözleşmeleri."""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers
from _helpers import GOOD_SUMMARY, write_jsonl

import gemini_ayikla
import flush
import ingest
import ingest_common
import ingest_gemini


def _block(body: str) -> str:
    return (
        '<div class="outer-cell mdl-cell mdl-cell--12-col">'
        '<div class="mdl-grid">'
        '<div class="header-cell"><p>Gemini Uygulamaları</p></div>'
        '<div class="content-cell mdl-cell mdl-cell--6-col '
        'mdl-typography--body-1">'
        f"{body}</div>"
        '<div class="content-cell mdl-cell mdl-cell--6-col '
        'mdl-typography--body-1 mdl-typography--text-right"></div>'
        '<div class="content-cell mdl-cell mdl-cell--12-col '
        'mdl-typography--caption">Ürünler: Gemini Uygulamaları</div>'
        "</div></div>"
    )


class BlockParsingTests(unittest.TestCase):
    def test_entities_attachment_usage_and_canvas(self) -> None:
        fixture = "".join(
            (
                _block(
                    "AT&amp;T &lt;deneme&gt;&nbsp;sorgusuna yanıt istendi<br>"
                    "1 dosya eklendi.<br>- <a href='x'>plan &amp; foto.png</a><br>"
                    "26 Ağu 2026 01:19:38 GMT+03:00<br>"
                    "<p>Yanıt &amp; devam</p><ul><li>ilk madde</li></ul>"
                ),
                _block(
                    "Kullanıldı:&nbsp;Gemini Uygulamaları<br>"
                    "19 Tem 2026 18:27:15 GMT+03:00<br>"
                ),
                _block(
                    "http://example.test/0 adlı Gemini Canvas oluşturuldu<br><br>"
                    "10 Ağu 2026 22:16:53 GMT+03:00<br>"
                ),
            )
        )
        records = gemini_ayikla.parse_html(fixture)
        self.assertEqual(len(records), 3)
        question, usage, canvas = records
        self.assertEqual(question.tip, "soru")
        self.assertEqual(
            question.soru,
            "AT&T <deneme> 1 dosya eklendi. - plan & foto.png",
        )
        self.assertNotIn("sorgusuna yanıt istendi", question.soru)
        self.assertEqual(question.cevap, "Yanıt & devam ilk madde")
        self.assertEqual(question.when.isoformat(), "2026-08-26T01:19:38+03:00")
        self.assertEqual(usage.tip, "kullanim")
        self.assertEqual(usage.soru, "Kullanıldı: Gemini Uygulamaları")
        self.assertEqual(canvas.tip, "canvas-bildirim")
        self.assertIn("Gemini Canvas oluşturuldu", canvas.soru)

    def test_keyword_pre_tag_uses_turkish_casefolding(self) -> None:
        self.assertEqual(gemini_ayikla.pre_tag("Python API course"), "learning")
        self.assertEqual(gemini_ayikla.pre_tag("Monthly TAX review"), "finances")
        self.assertEqual(gemini_ayikla.pre_tag("ilişkisiz kayıt"), "diger")


class DayGroupingTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.records_path = self.root / "kayitlar.jsonl"
        self.addCleanup(self._temporary.cleanup)

    @staticmethod
    def _record(
        record_id: str,
        stamp: str,
        chars: int,
        tip: str = "soru",
    ) -> dict:
        return {
            "id": record_id,
            "ts": stamp,
            "chars": chars,
            "tip": tip,
            "on_etiket": "diger",
            "soru": f"soru-{record_id}",
            "cevap": f"cevap-{record_id}",
        }

    def test_calendar_day_grouping_and_200k_split(self) -> None:
        write_jsonl(
            self.records_path,
            [
                self._record("b", "2026-08-20T11:00:00+03:00", 90_000),
                self._record("notice", "2026-08-20T10:30:00+03:00", 10, "kullanim"),
                self._record("a", "2026-08-20T10:00:00+03:00", 120_000),
                self._record("c", "2026-08-21T09:00:00+03:00", 10),
            ],
        )
        sessions, error = ingest_gemini.candidates(
            ingest_common.default_state(),
            records_path=self.records_path,
        )
        self.assertEqual(error, "")
        self.assertEqual(
            [session.key for session in sessions],
            ["gemini:2026-08-20", "gemini:2026-08-20#2", "gemini:2026-08-21"],
        )
        self.assertEqual(sessions[0].turns[0], ("user", "soru-a"))
        self.assertEqual(sessions[1].turns[0], ("user", "soru-b"))
        self.assertEqual(sessions[0].when.hour, 10)
        self.assertTrue(all("notice" not in str(session.turns) for session in sessions))

    def test_missing_store_has_stable_error(self) -> None:
        sessions, error = ingest_gemini.candidates(
            ingest_common.default_state(),
            records_path=self.root / "yok.jsonl",
        )
        self.assertEqual(sessions, [])
        self.assertEqual(error, "gemini-kayitlar-missing")


class ManifestMergeTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.manifest = self.root / "manifest.jsonl"
        self.input = self.root / "sonuc.jsonl"
        write_jsonl(
            self.manifest,
            [
                {"id": "known", "ts": "2026-08-20T10:00:00+03:00", "tip": "soru"},
                {"id": "other", "ts": "2026-08-20T11:00:00+03:00", "tip": "soru"},
            ],
        )
        self.addCleanup(self._temporary.cleanup)

    def test_unknown_id_refused_without_partial_write(self) -> None:
        before = self.manifest.read_text(encoding="utf-8")
        write_jsonl(
            self.input,
            [
                {"id": "known", "konu": "kod", "onem": 2},
                {"id": "unknown", "konu": "başka", "onem": 3},
            ],
        )
        with self.assertRaisesRegex(ValueError, "manifest-unknown-id:unknown"):
            gemini_ayikla.merge_manifest(self.input, self.manifest)
        self.assertEqual(self.manifest.read_text(encoding="utf-8"), before)

    def test_merge_reports_total_coverage(self) -> None:
        write_jsonl(
            self.input,
            [{"id": "known", "konu": "kod", "onem": 2}],
        )
        covered, total = gemini_ayikla.merge_manifest(self.input, self.manifest)
        self.assertEqual((covered, total), (1, 2))
        values = [json.loads(line) for line in self.manifest.read_text(encoding="utf-8").splitlines()]
        self.assertEqual(values[0]["konu"], "kod")
        self.assertNotIn("konu", values[1])


class GeminiCommandTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.state_dir = self.root / ".state"
        self.vault = self.root / "vault"
        self.records_path = self.root / "kayitlar.jsonl"
        self.state_dir.mkdir()
        self.vault.mkdir()
        write_jsonl(
            self.records_path,
            [
                {
                    "id": "single",
                    "ts": "2026-08-20T10:00:00+03:00",
                    "chars": 18,
                    "tip": "soru",
                    "on_etiket": "diger",
                    "soru": "tek soru",
                    "cevap": "tek cevap",
                }
            ],
        )
        self.addCleanup(self._temporary.cleanup)

    def test_default_codex_and_min_turns_two_are_threaded(self) -> None:
        args = ingest._parse_args(["gemini", "--sleep", "0"])
        self.assertEqual(args.model, "codex")
        with mock.patch.object(
            ingest_gemini,
            "RECORDS_PATH",
            self.records_path,
        ), mock.patch.object(
            ingest_common,
            "_run_codex",
            lambda prompt, root, model: (GOOD_SUMMARY, None),
        ):
            code = ingest.run_gemini(args, self.state_dir, self.vault)
        self.assertEqual(code, 0)
        entry = ingest_common.done_entry(
            ingest_common.load_state(self.state_dir),
            "gemini",
            "gemini:2026-08-20",
        )
        assert entry is not None
        self.assertEqual(entry["status"], "appended")

    def test_explicit_model_overrides_gemini_default(self) -> None:
        self.assertEqual(
            ingest._parse_args(["--model", "sonnet", "gemini"]).model,
            "sonnet",
        )
        self.assertEqual(
            ingest._parse_args(["gemini", "--model", "codex:custom"]).model,
            "codex:custom",
        )

    def test_gemini_summary_keeps_content_beyond_flush_15k_window(self) -> None:
        captured: list[str] = []

        def stub(prompt: str, root: Path, model: str):
            captured.append(prompt)
            return GOOD_SUMMARY, None

        session = ingest_common.Session(
            source="gemini",
            key="gemini:2026-08-20",
            when=dt.datetime(2026, 8, 20, 10, tzinfo=dt.timezone(dt.timedelta(hours=3))),
            turns=[
                ("user", "BASLANGIC-" + "a" * 9_000),
                ("assistant", "b" * 9_000 + "-BITIS"),
            ],
            origin=str(self.records_path),
            label="gemini",
        )
        with mock.patch.object(ingest_common, "_run_codex", stub):
            result = ingest_common.summarize_session(
                session,
                self.vault,
                self.state_dir,
                model="codex",
                min_turns=2,
            )
        self.assertEqual(result.status, "ok")
        self.assertEqual(len(captured), 1)
        self.assertIn("BASLANGIC-", captured[0])
        self.assertIn("-BITIS", captured[0])

    def test_gemini_local_summary_uses_backend_aware_bound(self) -> None:
        captured: list[str] = []

        def stub(prompt: str, root: Path):
            captured.append(prompt)
            return GOOD_SUMMARY, None

        session = ingest_common.Session(
            source="gemini",
            key="gemini:2026-08-20",
            when=dt.datetime(2026, 8, 20, 10, tzinfo=dt.timezone.utc),
            turns=[
                ("user", "BASLANGIC-" + "a" * 30_000),
                ("assistant", "b" * 30_000 + "-BITIS"),
            ],
            origin=str(self.records_path),
            label="gemini",
        )
        with mock.patch.dict(
            ingest_common.os.environ,
            {
                "BEYIN_MODEL_BACKEND": "ollama",
                "BEYIN_FLUSH_CHUNK_CHARS": "12345",
            },
            clear=True,
        ), mock.patch.object(flush, "_run_claude", stub):
            result = ingest_common.summarize_session(
                session,
                self.vault,
                self.state_dir,
                model="haiku",
                min_turns=2,
            )
        self.assertEqual(result.status, "ok")
        self.assertEqual(len(captured), 1)
        self.assertNotIn("BASLANGIC-", captured[0])
        self.assertIn("-BITIS", captured[0])
        body = captured[0].split(
            _helpers.BEGIN_MARKER, 1
        )[1].split(_helpers.END_MARKER, 1)[0].strip()
        self.assertLessEqual(len(body), 12_345)

    def test_gemini_local_summary_defaults_to_24k(self) -> None:
        captured: list[str] = []

        def stub(prompt: str, root: Path):
            captured.append(prompt)
            return GOOD_SUMMARY, None

        session = ingest_common.Session(
            source="gemini",
            key="gemini:2026-08-20",
            when=dt.datetime(2026, 8, 20, 10, tzinfo=dt.timezone.utc),
            turns=[
                ("user", "BASLANGIC-" + "a" * 30_000),
                ("assistant", "b" * 30_000 + "-BITIS"),
            ],
            origin=str(self.records_path),
            label="gemini",
        )
        with mock.patch.dict(
            ingest_common.os.environ,
            {"BEYIN_MODEL_BACKEND": "openai-compat"},
            clear=True,
        ), mock.patch.object(flush, "_run_claude", stub):
            result = ingest_common.summarize_session(
                session,
                self.vault,
                self.state_dir,
                model="haiku",
                min_turns=2,
            )
        self.assertEqual(result.status, "ok")
        body = captured[0].split(
            _helpers.BEGIN_MARKER, 1
        )[1].split(_helpers.END_MARKER, 1)[0].strip()
        self.assertLessEqual(len(body), flush.LOCAL_MAX_TRANSCRIPT_CHARS)
        self.assertNotIn("BASLANGIC-", captured[0])
        self.assertIn("-BITIS", captured[0])

    def test_gemini_claude_and_antigravity_keep_full_day(self) -> None:
        session = ingest_common.Session(
            source="gemini",
            key="gemini:2026-08-20",
            when=dt.datetime(2026, 8, 20, 10, tzinfo=dt.timezone.utc),
            turns=[
                ("user", "BASLANGIC-" + "a" * 20_000),
                ("assistant", "b" * 20_000 + "-BITIS"),
            ],
            origin=str(self.records_path),
            label="gemini",
        )
        for backend in ("claude", "antigravity"):
            captured: list[str] = []

            def stub(prompt: str, root: Path):
                captured.append(prompt)
                return GOOD_SUMMARY, None

            with self.subTest(backend=backend), mock.patch.dict(
                ingest_common.os.environ,
                {
                    "BEYIN_MODEL_BACKEND": backend,
                    "BEYIN_FLUSH_CHUNK_CHARS": "1000",
                },
                clear=True,
            ), mock.patch.object(flush, "_run_claude", stub):
                result = ingest_common.summarize_session(
                    session,
                    self.vault,
                    self.state_dir,
                    model="haiku",
                    min_turns=2,
                )
            self.assertEqual(result.status, "ok")
            self.assertIn("BASLANGIC-", captured[0])
            self.assertIn("-BITIS", captured[0])


if __name__ == "__main__":
    unittest.main()
