"""health.json warning ageing and ``durum.py --temizle-uyarilar``.

health.json keeps up to 20 historical warnings and a healthy run never clears
them, so an operator cannot tell a live alarm from a 3-week-old one on
sight. These tests pin the clock so ageing math is exact.

yazan: claude
model: sonnet-5
"""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import durum


class WarningAgeingTests(unittest.TestCase):
    """``summarize_warnings`` ages each entry from its own ts, else the top one."""

    def setUp(self) -> None:
        self.now = dt.datetime(2026, 9, 5, 12, 0).astimezone()

    def test_a_warning_younger_than_a_day_is_not_eski(self) -> None:
        health = {
            "ts": int((self.now - dt.timedelta(hours=1)).timestamp()),
            "warnings": ["fail:rootmap-regen-failed"],
        }

        warnings = durum.summarize_warnings(health, now=self.now)

        self.assertEqual(len(warnings), 1)
        self.assertFalse(warnings[0]["eski"])
        self.assertEqual(warnings[0]["message"], "fail:rootmap-regen-failed")
        self.assertEqual(warnings[0]["age_seconds"], 3600)

    def test_a_warning_older_than_a_day_is_eski(self) -> None:
        health = {
            "ts": int((self.now - dt.timedelta(hours=25)).timestamp()),
            "warnings": ["parked:schema-rejected"],
        }

        warnings = durum.summarize_warnings(health, now=self.now)

        self.assertTrue(warnings[0]["eski"])

    def test_an_entry_with_its_own_ts_uses_that_not_the_top_level_one(self) -> None:
        health = {
            "ts": int((self.now - dt.timedelta(hours=1)).timestamp()),
            "warnings": [
                {
                    "message": "warn:timeout-invalid",
                    "ts": int((self.now - dt.timedelta(hours=48)).timestamp()),
                }
            ],
        }

        warnings = durum.summarize_warnings(health, now=self.now)

        self.assertTrue(warnings[0]["eski"])
        self.assertEqual(warnings[0]["message"], "warn:timeout-invalid")

    def test_the_json_summary_carries_the_eski_flag(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state = Path(tmp) / ".state"
            state.mkdir()
            (state / "health.json").write_text(
                json.dumps(
                    {
                        "ts": int((self.now - dt.timedelta(hours=30)).timestamp()),
                        "warnings": ["old-one"],
                    }
                ),
                encoding="utf-8",
            )

            summary = durum.build_summary(state, now=self.now)

            self.assertEqual(len(summary["warnings"]), 1)
            self.assertIs(summary["warnings"][0]["eski"], True)

    def test_the_table_marks_a_stale_warning(self) -> None:
        from unittest import mock

        with tempfile.TemporaryDirectory() as tmp:
            state = Path(tmp) / ".state"
            state.mkdir()
            (state / "health.json").write_text(
                json.dumps(
                    {
                        "ts": int((self.now - dt.timedelta(hours=30)).timestamp()),
                        "warnings": ["old-one"],
                    }
                ),
                encoding="utf-8",
            )
            summary = durum.build_summary(state, now=self.now)

            with mock.patch("builtins.print") as printer:
                durum._print_table(summary)

        printed = "\n".join(
            str(call.args[0]) for call in printer.call_args_list if call.args
        )
        self.assertIn("old-one", printed)
        self.assertIn("eski", printed)


class TemizleUyarilarTests(unittest.TestCase):
    """``--temizle-uyarilar`` rewrites health.json keeping only fresh warnings."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state = Path(self._temporary.name) / ".state"
        self.state.mkdir()
        self.health = self.state / "health.json"
        self.now = dt.datetime(2026, 9, 5, 12, 0).astimezone()

    def _write_health(self, payload: dict) -> None:
        self.health.write_text(json.dumps(payload), encoding="utf-8")

    def test_cleanup_keeps_young_and_drops_old(self) -> None:
        self._write_health(
            {
                "ts": int((self.now - dt.timedelta(hours=1)).timestamp()),
                "component": "compile",
                "error": "",
                "warnings": [
                    {
                        "message": "young",
                        "ts": int((self.now - dt.timedelta(hours=2)).timestamp()),
                    },
                    {
                        "message": "old",
                        "ts": int((self.now - dt.timedelta(hours=25)).timestamp()),
                    },
                ],
                "counts": {"a": 1},
            }
        )

        result = durum.temizle_uyarilar(self.state, now=self.now)

        self.assertEqual(result, {"kept": 1, "dropped": 1, "changed": True})
        payload = json.loads(self.health.read_text(encoding="utf-8"))
        self.assertEqual(len(payload["warnings"]), 1)
        self.assertEqual(payload["warnings"][0]["message"], "young")
        # Every other key survives untouched.
        self.assertEqual(payload["component"], "compile")
        self.assertEqual(payload["counts"], {"a": 1})

    def test_cleanup_without_per_entry_ts_uses_the_top_level_ts(self) -> None:
        self._write_health(
            {
                "ts": int((self.now - dt.timedelta(hours=25)).timestamp()),
                "component": "compile",
                "error": "boom",
                "warnings": ["boom", "boom again"],
            }
        )

        result = durum.temizle_uyarilar(self.state, now=self.now)

        self.assertEqual(result, {"kept": 0, "dropped": 2, "changed": True})
        payload = json.loads(self.health.read_text(encoding="utf-8"))
        self.assertEqual(payload["warnings"], [])

    def test_cleanup_is_a_no_op_when_nothing_is_old(self) -> None:
        self._write_health(
            {
                "ts": int((self.now - dt.timedelta(hours=1)).timestamp()),
                "warnings": ["fresh"],
            }
        )
        before_mtime = self.health.stat().st_mtime_ns

        result = durum.temizle_uyarilar(self.state, now=self.now)

        self.assertEqual(result, {"kept": 1, "dropped": 0, "changed": False})
        self.assertEqual(self.health.stat().st_mtime_ns, before_mtime)
        payload = json.loads(self.health.read_text(encoding="utf-8"))
        self.assertEqual(payload["warnings"], ["fresh"])

    def test_cleanup_is_a_no_op_without_a_health_file(self) -> None:
        result = durum.temizle_uyarilar(self.state, now=self.now)

        self.assertEqual(result, {"kept": 0, "dropped": 0, "changed": False})
        self.assertFalse(self.health.exists())

    def test_the_shape_stays_byte_compatible_with_the_writer(self) -> None:
        import beyin_ortak

        beyin_ortak.write_health(
            self.state,
            error="warn:example",
            warning=True,
            component="compile",
        )
        # Force the recorded warning to look 25h old via the top-level ts.
        payload = json.loads(self.health.read_text(encoding="utf-8"))
        payload["ts"] = int((self.now - dt.timedelta(hours=25)).timestamp())
        self._write_health(payload)

        durum.temizle_uyarilar(self.state, now=self.now)

        raw = self.health.read_text(encoding="utf-8")
        self.assertTrue(raw.endswith("\n"))
        reloaded = json.loads(raw)
        self.assertEqual(reloaded["warnings"], [])
        self.assertEqual(reloaded["component"], "compile")
        self.assertEqual(reloaded["error"], "warn:example")

    def test_the_cli_flag_reports_and_exits_zero(self) -> None:
        from unittest import mock

        # Days old, not just-over-24h: avoids flakiness from real-clock skew
        # against a fixture time, since main() has no injectable now= here.
        self._write_health(
            {
                "ts": int(
                    (dt.datetime.now().astimezone() - dt.timedelta(days=10)).timestamp()
                ),
                "warnings": ["old"],
            }
        )

        with mock.patch("builtins.print") as printer:
            code = durum.main(
                ["--state-dir", str(self.state), "--temizle-uyarilar"]
            )

        self.assertEqual(code, 0)
        printed = "\n".join(
            str(call.args[0]) for call in printer.call_args_list if call.args
        )
        self.assertIn("temizlendi", printed)


if __name__ == "__main__":
    unittest.main()
