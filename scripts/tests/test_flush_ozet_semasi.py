# yazan: claude · model: opus-5
"""Faz 1 A-I1: hoşgörülü özet doğrulayıcı ve reddedilen özetlerin saklanması.

2026-09-08 süpürmesinde 20 özetin 6'sı `summary-schema-invalid` ile reddedildi
ve aynı oturumlar yeniden denemede geçti: Haiku araya kendi cümlesini koyuyor,
başlıkları `###`e indiriyor ya da kalınlaştırıyor. Sözleşme beş bölüm ve
sıraları; başlığın seviyesi ve süsü değil. Ret hâlâ mümkün, ama artık ham çıktı
`.state/red/` altında durduğu için teşhis edilebilir.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401  — scripts dizinini sys.path'e ekler
from _helpers import GOOD_SUMMARY

import flush


MOMENT = dt.datetime(2026, 9, 8, 19, 25, tzinfo=dt.timezone.utc)

BODY = {
    "Bağlam": "Oturum arşivden geldi.",
    "Önemli Konuşmalar": "Kısa bir konuşma.",
    "Alınan Kararlar": "Karar yok.",
    "Öğrenilenler": "Bir şey öğrenildi.",
    "Yapılacaklar": "Yapılacak yok.",
}


def summary(
    *,
    level: str = "##",
    decorate: str = "{}",
    preamble: str = "",
    sections: tuple[str, ...] = flush.EXPECTED_SECTIONS,
    trailing: str = "",
) -> str:
    blocks = [
        f"{level} {decorate.format(section)}{trailing}\n{BODY.get(section, 'x')}"
        for section in sections
    ]
    return preamble + "\n".join(blocks) + "\n"


class ToleratedShapeTests(unittest.TestCase):
    """Shapes that differ only in decoration must validate."""

    def test_contract_shape_still_validates(self) -> None:
        self.assertTrue(flush.validate_summary(GOOD_SUMMARY))
        self.assertTrue(flush.validate_summary(summary()))

    def test_preamble_before_the_first_heading_is_dropped(self) -> None:
        self.assertTrue(
            flush.validate_summary(summary(preamble="Şöyle özetledim:\n\n"))
        )
        self.assertTrue(
            flush.validate_summary(
                summary(preamble="Here is the summary you asked for.\n\n")
            )
        )

    def test_third_level_headings_are_normalised_to_second(self) -> None:
        self.assertTrue(flush.validate_summary(summary(level="###")))

    def test_a_document_title_above_the_sections_is_preamble(self) -> None:
        self.assertTrue(
            flush.validate_summary(
                summary(level="###", preamble="# Oturum Özeti\n\n")
            )
        )

    def test_bold_and_trailing_whitespace_in_heading_text(self) -> None:
        self.assertTrue(flush.validate_summary(summary(decorate="**{}**")))
        self.assertTrue(flush.validate_summary(summary(trailing="   ")))
        self.assertTrue(flush.validate_summary(summary(decorate="*{}*")))

    def test_closed_atx_headings(self) -> None:
        self.assertTrue(flush.validate_summary(summary(trailing=" ##")))

    def test_every_tolerance_at_once(self) -> None:
        self.assertTrue(
            flush.validate_summary(
                summary(
                    level="###",
                    decorate="**{}**",
                    preamble="# Özet\n\nİşte istediğin özet:\n\n",
                    trailing="  ",
                )
            )
        )


class RejectedShapeTests(unittest.TestCase):
    """The parts that are the contract stay enforced."""

    def test_a_missing_section_is_still_rejected(self) -> None:
        self.assertFalse(
            flush.validate_summary(
                summary(sections=flush.EXPECTED_SECTIONS[:4])
            )
        )

    def test_a_reordered_section_is_still_rejected(self) -> None:
        swapped = (
            flush.EXPECTED_SECTIONS[1],
            flush.EXPECTED_SECTIONS[0],
        ) + flush.EXPECTED_SECTIONS[2:]
        self.assertFalse(flush.validate_summary(summary(sections=swapped)))

    def test_a_preamble_cannot_hide_a_section_out_of_order(self) -> None:
        """Bağlam last, everything else first: the preamble rule must not save it."""
        rotated = flush.EXPECTED_SECTIONS[1:] + (flush.EXPECTED_SECTIONS[0],)
        self.assertFalse(flush.validate_summary(summary(sections=rotated)))

    def test_a_duplicated_section_is_rejected(self) -> None:
        doubled = (flush.EXPECTED_SECTIONS[0],) + flush.EXPECTED_SECTIONS
        self.assertFalse(flush.validate_summary(summary(sections=doubled)))

    def test_prose_with_no_headings_is_rejected(self) -> None:
        self.assertFalse(flush.validate_summary("Bugün pek bir şey olmadı."))
        self.assertFalse(flush.validate_summary(""))

    def test_first_level_headings_are_not_the_contract(self) -> None:
        self.assertFalse(flush.validate_summary(summary(level="#")))


class RedStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state = self.root / ".state"
        self.state.mkdir()

    def test_a_rejected_summary_is_written_and_named(self) -> None:
        raw = "Şöyle özetledim ama başlıkları unuttum."
        path = flush.persist_rejected_summary(
            self.state, "oturum-1", raw, MOMENT
        )

        self.assertIsNotNone(path)
        written = Path(path)
        self.assertEqual(written.parent, self.state / flush.RED_DIR_NAME)
        self.assertEqual(written.read_text(encoding="utf-8"), raw)
        self.assertTrue(written.name.startswith("oturum-1-2026"))
        self.assertTrue(written.name.endswith(".md"))

    def test_an_unsafe_session_id_cannot_escape_the_directory(self) -> None:
        path = flush.persist_rejected_summary(
            self.state, "../../etc/passwd", "gövde", MOMENT
        )
        self.assertIsNotNone(path)
        self.assertEqual(
            Path(path).parent.resolve(),
            (self.state / flush.RED_DIR_NAME).resolve(),
        )

    def test_two_rejections_in_the_same_second_do_not_collide(self) -> None:
        first = flush.persist_rejected_summary(self.state, "s", "bir", MOMENT)
        second = flush.persist_rejected_summary(self.state, "s", "iki", MOMENT)
        self.assertNotEqual(first, second)
        self.assertEqual(Path(first).read_text(encoding="utf-8"), "bir")
        self.assertEqual(Path(second).read_text(encoding="utf-8"), "iki")

    def test_an_empty_summary_is_not_stored(self) -> None:
        self.assertIsNone(flush.persist_rejected_summary(self.state, "s", "", MOMENT))
        self.assertFalse((self.state / flush.RED_DIR_NAME).exists())

    def test_the_directory_is_capped_at_fifty_files_oldest_first(self) -> None:
        directory = self.state / flush.RED_DIR_NAME
        directory.mkdir()
        for index in range(60):
            stale = directory / f"eski-{index:03d}.md"
            stale.write_text(str(index), encoding="utf-8")
            os.utime(stale, (1_700_000_000 + index, 1_700_000_000 + index))

        newest = flush.persist_rejected_summary(self.state, "yeni", "son", MOMENT)

        remaining = sorted(path.name for path in directory.glob("*.md"))
        self.assertEqual(len(remaining), flush.RED_MAX_FILES)
        self.assertIn(Path(newest).name, remaining)
        self.assertNotIn("eski-000.md", remaining)
        self.assertIn("eski-059.md", remaining)


class RejectionLedgerTests(unittest.TestCase):
    """End to end: a bad summary parks nothing, commits nothing, leaves evidence."""

    session_id = "red-session"

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state = self.root / ".state"
        self.state.mkdir()
        self.transcript = self.root / "session.jsonl"
        self.transcript.write_text(
            "\n".join(
                json.dumps({"message": {"role": "user", "content": f"tur {index}"}})
                for index in range(3)
            )
            + "\n",
            encoding="utf-8",
        )

    def _flush(self, model_output: str) -> None:
        with mock.patch.object(flush, "STATE_DIR", self.state), mock.patch.object(
            flush, "VAULT_ROOT", self.root
        ), mock.patch.dict(flush.os.environ, {}, clear=True), mock.patch.object(
            flush, "_run_claude", return_value=(model_output, None)
        ), mock.patch.object(
            flush, "maybe_trigger_compile", return_value=False
        ):
            flush._flush_once(
                argparse.Namespace(hook_input=None, reason="sessionend"),
                MOMENT,
                hook_input={
                    "session_id": self.session_id,
                    "transcript_path": str(self.transcript),
                },
            )

    def _ledger(self) -> list[dict]:
        path = self.state / flush.DELIVERY_LEDGER_NAME
        return [
            json.loads(line)
            for line in path.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]

    def test_rejection_stores_the_raw_output_and_names_it_in_the_ledger(self) -> None:
        self._flush("Beş bölüm yerine tek paragraf yazdım.")

        rejected = [
            row for row in self._ledger() if row["reason"] == flush.REASON_REJECTED
        ]
        self.assertEqual(len(rejected), 1)
        red = rejected[0].get("red")
        self.assertTrue(red, "rejected ledger line must name the stored output")
        self.assertEqual(
            Path(red).read_text(encoding="utf-8"),
            "Beş bölüm yerine tek paragraf yazdım.",
        )
        self.assertEqual(Path(red).parent, self.state / flush.RED_DIR_NAME)

        state = json.loads(
            flush._session_state_path(self.state, self.session_id).read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual(state["kapsanan"], [])
        self.assertEqual(state["basarisiz_parca"]["deneme"], 1)
        self.assertFalse((self.root / "daily").exists())

    def test_a_tolerated_shape_reaches_the_daily_log(self) -> None:
        self._flush(summary(level="###", decorate="**{}**", preamble="İşte özet:\n\n"))

        reasons = [row["reason"] for row in self._ledger()]
        self.assertIn(flush.REASON_OK, reasons)
        self.assertNotIn(flush.REASON_REJECTED, reasons)
        self.assertFalse((self.state / flush.RED_DIR_NAME).exists())
        daily = (self.root / "daily" / "2026-09-08.md").read_text(encoding="utf-8")
        self.assertIn("Bağlam", daily)


if __name__ == "__main__":
    unittest.main()
