# yazan: codex · model: gpt-5.6-sol
"""Reconciliation coverage in the stable durum JSON and text surfaces."""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401

import durum


class DurumMutabakatTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state = Path(self._temporary.name) / ".state"
        self.state.mkdir()
        self.now = dt.datetime(2026, 9, 8, 12, 0, tzinfo=dt.timezone.utc)

    def test_summary_shows_uncovered_count_and_oldest_source_age(self) -> None:
        (self.state / "mutabakat.json").write_text(
            json.dumps(
                {
                    "uncovered_session_count": 3,
                    "oldest_unprocessed_source_time": (
                        self.now - dt.timedelta(hours=5)
                    ).isoformat(timespec="seconds"),
                    "unmatched_ingress_count": 2,
                }
            ),
            encoding="utf-8",
        )

        summary = durum.build_summary(self.state, now=self.now)

        self.assertEqual(summary["mutabakat"]["uncovered_sessions"], 3)
        self.assertEqual(
            summary["mutabakat"]["oldest_unprocessed_source_age_seconds"],
            5 * 60 * 60,
        )
        self.assertEqual(summary["mutabakat"]["unmatched_ingress"], 2)

        with mock.patch("builtins.print") as printer:
            durum._print_table(summary)
        rendered = "\n".join(
            str(call.args[0]) for call in printer.call_args_list if call.args
        )
        self.assertIn("mutabakat: 3 uncovered sessions", rendered)
        self.assertIn("oldest source 5h", rendered)

    def test_missing_manifest_is_zero_not_unknown(self) -> None:
        result = durum.mutabakat_ozeti(self.state, now=self.now)

        self.assertEqual(result["uncovered_sessions"], 0)
        self.assertIsNone(result["oldest_unprocessed_source_age_seconds"])


if __name__ == "__main__":
    unittest.main()
