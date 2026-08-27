"""flush._append_daily geriye uyumluluk: ``suffix=None`` bayt-aynı davranmalı."""

from __future__ import annotations

import datetime as dt
from pathlib import Path
import tempfile
import unittest

import _helpers
from _helpers import GOOD_SUMMARY

import flush


MOMENT = dt.datetime(2026, 8, 20, 14, 5, tzinfo=dt.timezone.utc).astimezone()
DATE_TEXT = MOMENT.strftime("%Y-%m-%d")
TIME_TEXT = MOMENT.strftime("%H:%M")


class AppendDailySuffixTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.addCleanup(self._temporary.cleanup)

    def _daily(self) -> Path:
        return self.root / "daily" / f"{DATE_TEXT}.md"

    def _expected(self, suffix: str) -> str:
        return (
            f"# Günlük Log: {DATE_TEXT}\n\n## Oturumlar\n"
            f"\n### Oturum ({TIME_TEXT}){suffix}\n\n{GOOD_SUMMARY}\n"
        )

    def test_sessionend_default_is_byte_identical(self) -> None:
        flush._append_daily(self.root, GOOD_SUMMARY, "sessionend", MOMENT)
        self.assertEqual(
            self._daily().read_text(encoding="utf-8"),
            self._expected(""),
        )

    def test_precompact_default_is_byte_identical(self) -> None:
        flush._append_daily(self.root, GOOD_SUMMARY, "precompact", MOMENT)
        self.assertEqual(
            self._daily().read_text(encoding="utf-8"),
            self._expected(", compaction öncesi"),
        )

    def test_explicit_none_matches_positional_default(self) -> None:
        flush._append_daily(self.root, GOOD_SUMMARY, "precompact", MOMENT, None)
        self.assertEqual(
            self._daily().read_text(encoding="utf-8"),
            self._expected(", compaction öncesi"),
        )

    def test_custom_suffix_renders(self) -> None:
        suffix = " — codex · gpt-5.6-sol · özet: haiku"
        flush._append_daily(
            self.root,
            GOOD_SUMMARY,
            "ingest",
            MOMENT,
            suffix=suffix,
        )
        self.assertEqual(
            self._daily().read_text(encoding="utf-8"),
            self._expected(suffix),
        )

    def test_empty_suffix_overrides_precompact_default(self) -> None:
        flush._append_daily(self.root, GOOD_SUMMARY, "precompact", MOMENT, "")
        self.assertEqual(
            self._daily().read_text(encoding="utf-8"),
            self._expected(""),
        )


if __name__ == "__main__":
    unittest.main()
