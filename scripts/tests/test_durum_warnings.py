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


class RegistrySelectionDemotionTests(unittest.TestCase):
    """``warn:registry-truncated`` telemetridir, arıza değil (Astra A14).

    compile.py bu satırı BAŞARILI her sınırlı kayıt-defteri seçiminde yazar;
    uyarı sayılınca sağlık hattı hiç yeşile dönmüyordu.
    """

    def setUp(self) -> None:
        self.now = dt.datetime(2026, 9, 6, 12, 0).astimezone()

    def test_the_raw_string_is_shown_as_info(self) -> None:
        health = {
            "ts": int((self.now - dt.timedelta(hours=1)).timestamp()),
            "warnings": ["warn:registry-truncated:77/525"],
        }

        warnings = durum.summarize_warnings(health, now=self.now)

        self.assertEqual(warnings[0]["message"], "info:registry-selection:77/525")
        self.assertEqual(warnings[0]["raw"], "warn:registry-truncated:77/525")
        self.assertIs(warnings[0]["info"], True)

    def test_info_entries_do_not_count_as_warnings(self) -> None:
        health = {
            "ts": int((self.now - dt.timedelta(hours=1)).timestamp()),
            "warnings": [
                "warn:registry-truncated:77/525",
                "fail:rootmap-regen-failed",
            ],
        }

        warnings = durum.summarize_warnings(health, now=self.now)

        self.assertEqual(durum._warning_count(warnings), 1)

    def test_a_real_warning_is_untouched(self) -> None:
        health = {
            "ts": int((self.now - dt.timedelta(hours=1)).timestamp()),
            "warnings": ["warn:timeout-invalid"],
        }

        warnings = durum.summarize_warnings(health, now=self.now)

        self.assertEqual(warnings[0]["message"], "warn:timeout-invalid")
        self.assertIs(warnings[0]["info"], False)

    def test_the_headline_separates_warnings_from_info(self) -> None:
        from unittest import mock

        with tempfile.TemporaryDirectory() as tmp:
            state = Path(tmp) / ".state"
            state.mkdir()
            (state / "health.json").write_text(
                json.dumps(
                    {
                        "ts": int((self.now - dt.timedelta(hours=1)).timestamp()),
                        "warnings": [
                            "warn:registry-truncated:77/525",
                            "fail:rootmap-regen-failed",
                        ],
                    }
                ),
                encoding="utf-8",
            )
            summary = durum.build_summary(state, now=self.now)
            self.assertEqual(summary["warning_count"], 1)
            self.assertEqual(summary["info_count"], 1)

            with mock.patch("builtins.print") as printer:
                durum._print_table(summary)

        printed = "\n".join(
            str(call.args[0]) for call in printer.call_args_list if call.args
        )
        self.assertIn("warnings: 1 (+1 info)", printed)
        self.assertIn("info:registry-selection:77/525", printed)


class BekleyenKaynakTests(unittest.TestCase):
    """Derlenmeyi bekleyen günlükler sağlık tablosunda görünür (Astra A14).

    Denetimde tablo ok/ok/ok gösterirken 5 ve 6 Eylül günlükleri derlenmemiş
    bekliyordu; hiçbir satır bunu söylemiyordu.
    """

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.vault = Path(self._temporary.name) / "vault"
        self.state = self.vault / ".claude" / "scripts" / ".state"
        self.state.mkdir(parents=True)
        self.daily = self.vault / "daily"
        self.daily.mkdir()
        self.now = dt.datetime(2026, 9, 6, 12, 0).astimezone()

    def _daily(self, name: str, body: str) -> str:
        import hashlib

        path = self.daily / name
        path.write_text(body, encoding="utf-8")
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def _compile_state(self, ingested: dict, **extra) -> None:
        (self.state / "compile-state.json").write_text(
            json.dumps({"ingested": ingested, "last_status": "ok", **extra}),
            encoding="utf-8",
        )

    def test_two_uningested_dailies_are_pending(self) -> None:
        done = self._daily("2026-09-04.md", "derlendi\n")
        self._daily("2026-09-05.md", "beklemede\n")
        self._daily("2026-09-06.md", "beklemede\n")
        self._compile_state({"2026-09-04.md": done})

        pending = durum.bekleyen_kaynak(self.vault, {"ingested": {"2026-09-04.md": done}})

        self.assertEqual(pending["count"], 2)
        self.assertEqual(pending["oldest"], "2026-09-05.md")
        self.assertEqual(pending["files"], ["2026-09-05.md", "2026-09-06.md"])

    def test_an_edited_daily_becomes_pending_again(self) -> None:
        digest = self._daily("2026-09-04.md", "ilk hâli\n")
        (self.daily / "2026-09-04.md").write_text("düzenlendi\n", encoding="utf-8")

        pending = durum.bekleyen_kaynak(self.vault, {"ingested": {"2026-09-04.md": digest}})

        self.assertEqual(pending["count"], 1)

    def test_quarantined_and_parked_dailies_are_not_pending(self) -> None:
        karantina = self._daily("2026-09-05.md", "karantina\n")
        park = self._daily("2026-09-06.md", "park\n")

        pending = durum.bekleyen_kaynak(
            self.vault,
            {
                "ingested": {},
                "quarantined": {karantina: {"reason": "schema"}},
                "parked": {"2026-09-06.md": {"digest": park, "attempts": 3}},
            },
        )

        self.assertEqual(pending["count"], 0)
        self.assertIsNone(pending["oldest"])

    def test_a_missing_daily_directory_is_not_an_error(self) -> None:
        pending = durum.bekleyen_kaynak(Path(self._temporary.name) / "yok", {})

        self.assertEqual(pending, {"count": 0, "oldest": None, "files": []})

    def test_the_table_prints_the_pending_line(self) -> None:
        from unittest import mock

        done = self._daily("2026-09-04.md", "derlendi\n")
        self._daily("2026-09-05.md", "beklemede\n")
        self._daily("2026-09-06.md", "beklemede\n")
        self._compile_state({"2026-09-04.md": done})

        summary = durum.build_summary(self.state, now=self.now)
        self.assertEqual(summary["bekleyen"]["count"], 2)

        with mock.patch("builtins.print") as printer:
            durum._print_table(summary)
        printed = "\n".join(
            str(call.args[0]) for call in printer.call_args_list if call.args
        )

        # Kök --state-dir'den türetilir: <vault>/.claude/scripts/.state → <vault>
        self.assertIn("bekleyen kaynak: 2 daily uncompiled (oldest 2026-09-05)", printed)

    def test_nothing_pending_says_so(self) -> None:
        from unittest import mock

        done = self._daily("2026-09-04.md", "derlendi\n")
        self._compile_state({"2026-09-04.md": done})

        summary = durum.build_summary(self.state, now=self.now)

        with mock.patch("builtins.print") as printer:
            durum._print_table(summary)
        printed = "\n".join(
            str(call.args[0]) for call in printer.call_args_list if call.args
        )
        self.assertIn("bekleyen kaynak: none pending", printed)


if __name__ == "__main__":
    unittest.main()
