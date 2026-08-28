"""Maintenance gating and epistemic-status rules.

Two cheap questions the pipeline used to answer wrongly: "does this run need to
rebuild the index at all?" and "did this trigger fire too soon?".  Plus the
distillation rules that keep a hedge a hedge.  No model is ever called.
"""

# yazan: claude · opus-5

from __future__ import annotations

import datetime as dt
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import compile as compile_module
import flush
import retrieve
import rootmap


INDEX_TEXT = "# İndeks\n\n| Makale | Özet | Kaynak | Güncellendi |\n|---|---|---|---|\n"
CONCEPT_TEXT = (
    "---\ntitle: Deneme\naliases: []\ntags: []\nsources: [x.md]\n"
    "created: 2026-08-26\nupdated: 2026-08-26\n---\n\n# Deneme\n\nGövde.\n"
)
EVENING = dt.datetime(2026, 8, 27, 20, 30).astimezone()


class CompileHarness(unittest.TestCase):
    """A temporary vault with the compiler's module-level paths bound to it."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state_dir = self.root / ".claude" / "scripts" / ".state"
        self.state_dir.mkdir(parents=True)
        self.state_path = self.state_dir / "compile-state.json"

        knowledge = self.root / "knowledge"
        (knowledge / "concepts").mkdir(parents=True)
        (knowledge / "connections").mkdir(parents=True)
        (knowledge / "index.md").write_text(INDEX_TEXT, encoding="utf-8")
        (knowledge / "index-full.md").write_text(INDEX_TEXT, encoding="utf-8")
        (knowledge / "log.md").write_text("# Log\n", encoding="utf-8")
        (self.root / "daily").mkdir()

        for name, value in {
            "VAULT_ROOT": self.root,
            "STATE_DIR": self.state_dir,
            "STAGE_ROOT": self.root / ".stage",
        }.items():
            patcher = mock.patch.object(compile_module, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        environment = mock.patch.dict("os.environ", {"BEYIN_INVOKED_BY": ""})
        environment.start()
        self.addCleanup(environment.stop)
        regenerate = mock.patch.object(rootmap, "regenerate")
        regenerate.start()
        self.addCleanup(regenerate.stop)
        build_index = mock.patch.object(retrieve, "build_index")
        self.build_index = build_index.start()
        self.addCleanup(build_index.stop)

    def _daily(self, name: str, body: str = "İçerik.\n") -> Path:
        path = self.root / "daily" / name
        path.write_text(f"# Günlük Log: {name[:-3]}\n\n{body}", encoding="utf-8")
        return path

    def _state(self) -> dict:
        return json.loads(self.state_path.read_text(encoding="utf-8"))

    def _health(self) -> dict:
        path = self.state_dir / "health.json"
        if not path.exists():
            return {}
        return json.loads(path.read_text(encoding="utf-8"))

    def _skip_reasons(self) -> list[str]:
        return [entry["reason"] for entry in self._health().get("skips", [])]

    def _concept_stub(self, name: str):
        def stub(_prompt: str, stage: Path) -> str | None:
            (stage / "knowledge" / "concepts" / name).write_text(
                CONCEPT_TEXT, encoding="utf-8"
            )
            return None

        return stub

    def _index_only_stub(self, marker: str):
        def stub(_prompt: str, stage: Path) -> str | None:
            full = stage / "knowledge" / "index-full.md"
            full.write_text(
                full.read_text(encoding="utf-8") + f"<!-- {marker} -->\n",
                encoding="utf-8",
            )
            return None

        return stub


class ManifestHashTests(CompileHarness):
    def test_manifest_tracks_content_not_just_names(self) -> None:
        concepts = self.root / "knowledge" / "concepts"
        (concepts / "bir.md").write_text(CONCEPT_TEXT, encoding="utf-8")
        first = compile_module.concepts_manifest_hash(self.root)

        (concepts / "bir.md").write_text(
            CONCEPT_TEXT + "Ek satır.\n", encoding="utf-8"
        )
        second = compile_module.concepts_manifest_hash(self.root)
        (concepts / "iki.md").write_text(CONCEPT_TEXT, encoding="utf-8")
        third = compile_module.concepts_manifest_hash(self.root)

        self.assertNotEqual(first, second)
        self.assertNotEqual(second, third)

    def test_manifest_is_stable_across_calls(self) -> None:
        (self.root / "knowledge" / "concepts" / "bir.md").write_text(
            CONCEPT_TEXT, encoding="utf-8"
        )

        self.assertEqual(
            compile_module.concepts_manifest_hash(self.root),
            compile_module.concepts_manifest_hash(self.root),
        )

    def test_a_missing_concepts_directory_still_hashes(self) -> None:
        empty = self.root / "yok"

        self.assertTrue(compile_module.concepts_manifest_hash(empty))

    def test_deleting_a_note_changes_the_manifest(self) -> None:
        concepts = self.root / "knowledge" / "concepts"
        (concepts / "bir.md").write_text(CONCEPT_TEXT, encoding="utf-8")
        before = compile_module.concepts_manifest_hash(self.root)
        (concepts / "bir.md").unlink()

        self.assertNotEqual(before, compile_module.concepts_manifest_hash(self.root))


class IndexRebuildGateTests(CompileHarness):
    def test_a_concept_change_rebuilds_and_records_the_manifest(self) -> None:
        self._daily("2026-08-12.md")

        with mock.patch.object(
            compile_module, "_run_claude", self._concept_stub("deneme.md")
        ):
            self.assertEqual(compile_module.main([]), 0)

        self.build_index.assert_called_once_with(
            vault_root=self.root, state_dir=self.state_dir
        )
        self.assertEqual(
            self._state()["concepts_manifest"],
            compile_module.concepts_manifest_hash(self.root),
        )
        self.assertEqual(self._skip_reasons(), [])

    def test_an_index_only_change_skips_the_rebuild_loudly(self) -> None:
        self._daily("2026-08-12.md")
        with mock.patch.object(
            compile_module, "_run_claude", self._concept_stub("deneme.md")
        ):
            self.assertEqual(compile_module.main([]), 0)
        self.build_index.reset_mock()

        self._daily("2026-08-13.md")
        with mock.patch.object(
            compile_module, "_run_claude", self._index_only_stub("ikinci")
        ):
            self.assertEqual(compile_module.main([]), 0)

        self.build_index.assert_not_called()
        self.assertIn("skip:index-rebuild:concepts-unchanged", self._skip_reasons())
        # A skip is not a failure: the error flag must stay clear.
        self.assertEqual(self._health().get("error"), "")
        self.assertEqual(self._state()["last_status"], "ok")
        self.assertIn("2026-08-13.md", self._state()["ingested"])

    def test_a_second_concept_change_rebuilds_again(self) -> None:
        self._daily("2026-08-12.md")
        with mock.patch.object(
            compile_module, "_run_claude", self._concept_stub("bir.md")
        ):
            self.assertEqual(compile_module.main([]), 0)
        self.build_index.reset_mock()

        self._daily("2026-08-13.md")
        with mock.patch.object(
            compile_module, "_run_claude", self._concept_stub("iki.md")
        ):
            self.assertEqual(compile_module.main([]), 0)

        self.build_index.assert_called_once()

    def test_a_failed_rebuild_does_not_record_the_manifest(self) -> None:
        self._daily("2026-08-12.md")
        self.build_index.side_effect = OSError("sentetik indeks hatası")

        with mock.patch.object(
            compile_module, "_run_claude", self._concept_stub("deneme.md")
        ):
            self.assertEqual(compile_module.main([]), 0)

        self.assertEqual(self._state()["concepts_manifest"], "")
        self.assertEqual(self._health()["error"], "retrieve-rebuild-failed")

    def test_an_unchanged_daily_never_reaches_the_model(self) -> None:
        self._daily("2026-08-12.md")
        calls: list[str] = []

        def counting(prompt: str, stage: Path) -> str | None:
            calls.append("call")
            return self._concept_stub("deneme.md")(prompt, stage)

        with mock.patch.object(compile_module, "_run_claude", counting):
            self.assertEqual(compile_module.main([]), 0)
            self.assertEqual(compile_module.main([]), 0)
            self.assertEqual(compile_module.main([]), 0)

        self.assertEqual(len(calls), 1)


class SkipHealthTests(CompileHarness):
    def test_repeated_skips_collapse_into_one_counted_entry(self) -> None:
        for _ in range(3):
            compile_module.write_health_skip(self.state_dir, "skip:deneme")

        skips = self._health()["skips"]
        self.assertEqual(len(skips), 1)
        self.assertEqual(skips[0]["reason"], "skip:deneme")
        self.assertEqual(skips[0]["count"], 3)
        self.assertEqual(self._health()["last_skip"]["reason"], "skip:deneme")

    def test_the_skip_list_stays_bounded(self) -> None:
        for index in range(30):
            compile_module.write_health_skip(self.state_dir, f"skip:{index}")

        self.assertEqual(len(self._health()["skips"]), 20)

    def test_a_skip_never_touches_the_error_flag(self) -> None:
        compile_module.write_health(self.state_dir, "gercek-hata")

        compile_module.write_health_skip(self.state_dir, "skip:deneme")

        self.assertEqual(self._health()["error"], "gercek-hata")


class TriggerIntervalTests(unittest.TestCase):
    """The nightly trigger needs a changed daily AND enough elapsed time."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state_dir = self.root / ".claude" / "scripts" / ".state"
        self.state_dir.mkdir(parents=True)
        (self.root / "daily").mkdir()
        (self.root / "daily" / "2026-08-27.md").write_text(
            "# Günlük Log\n\nİçerik.\n", encoding="utf-8"
        )
        self.launched: list[list[str]] = []
        # These must be genuinely absent, not blank: flush treats a set-but-empty
        # BEYIN_FAKE_HOUR as a parse error.
        self._unset("BEYIN_FAKE_HOUR", flush.COMPILE_MIN_INTERVAL_ENV)

    def _unset(self, *names: str) -> None:
        for name in names:
            if name in os.environ:
                self.addCleanup(os.environ.__setitem__, name, os.environ[name])
                del os.environ[name]
            else:
                self.addCleanup(os.environ.pop, name, None)

    def _launcher(self, command, **_kwargs):
        self.launched.append(list(command))
        return mock.Mock()

    def _write_compile_state(self, status: str, hours_ago: float) -> None:
        last_run = (EVENING - dt.timedelta(hours=hours_ago)).isoformat(
            timespec="seconds"
        )
        (self.state_dir / "compile-state.json").write_text(
            json.dumps(
                {"ingested": {}, "last_run": last_run, "last_status": status}
            ),
            encoding="utf-8",
        )

    def _trigger(self) -> bool:
        return flush.maybe_trigger_compile(
            self.root, EVENING, popen_factory=self._launcher
        )

    def _skip_reasons(self) -> list[str]:
        path = self.state_dir / "health.json"
        if not path.exists():
            return []
        payload = json.loads(path.read_text(encoding="utf-8"))
        return [entry["reason"] for entry in payload.get("skips", [])]

    def test_a_recent_success_holds_the_trigger_back(self) -> None:
        self._write_compile_state("ok", hours_ago=2)

        self.assertFalse(self._trigger())
        self.assertEqual(self.launched, [])
        self.assertFalse(
            list(self.state_dir.glob("compile-trigger-*")),
            "no day should be claimed when the gate refuses",
        )
        self.assertTrue(
            any("min-interval" in reason for reason in self._skip_reasons()),
            self._skip_reasons(),
        )

    def test_an_old_enough_success_lets_it_through(self) -> None:
        self._write_compile_state("ok", hours_ago=25)

        self.assertTrue(self._trigger())
        self.assertEqual(len(self.launched), 1)

    def test_a_failed_last_run_does_not_lock_the_gate(self) -> None:
        self._write_compile_state("fail:claude-exit-1", hours_ago=1)

        self.assertTrue(self._trigger())

    def test_a_first_run_with_no_state_is_allowed(self) -> None:
        self.assertTrue(self._trigger())

    def test_an_unparsable_last_run_does_not_lock_the_gate(self) -> None:
        (self.state_dir / "compile-state.json").write_text(
            json.dumps(
                {"ingested": {}, "last_run": "dün akşam", "last_status": "ok"}
            ),
            encoding="utf-8",
        )

        self.assertTrue(self._trigger())

    def test_zero_interval_disables_the_gate(self) -> None:
        self._write_compile_state("ok", hours_ago=0.5)

        with mock.patch.dict(
            "os.environ", {flush.COMPILE_MIN_INTERVAL_ENV: "0"}
        ):
            self.assertTrue(self._trigger())

    def test_a_custom_interval_is_honoured(self) -> None:
        self._write_compile_state("ok", hours_ago=3)

        with mock.patch.dict(
            "os.environ", {flush.COMPILE_MIN_INTERVAL_ENV: "2"}
        ):
            self.assertTrue(self._trigger())

    def test_an_unchanged_daily_still_blocks_even_after_a_long_gap(self) -> None:
        """The gate is an AND: elapsed time alone is not a reason to compile."""
        digest = flush._sha256(self.root / "daily" / "2026-08-27.md")
        (self.state_dir / "compile-state.json").write_text(
            json.dumps(
                {
                    "ingested": {"2026-08-27.md": digest},
                    "last_run": (EVENING - dt.timedelta(days=9)).isoformat(
                        timespec="seconds"
                    ),
                    "last_status": "ok",
                }
            ),
            encoding="utf-8",
        )

        self.assertFalse(self._trigger())
        self.assertEqual(self.launched, [])

    def test_an_already_claimed_day_records_a_skip(self) -> None:
        (self.state_dir / "compile-trigger-2026-08-27").write_text(
            "", encoding="utf-8"
        )

        self.assertFalse(self._trigger())
        self.assertIn(
            "skip:compile-trigger:day-already-claimed", self._skip_reasons()
        )

    def test_before_the_evening_nothing_fires(self) -> None:
        morning = EVENING.replace(hour=9)

        self.assertFalse(
            flush.maybe_trigger_compile(
                self.root, morning, popen_factory=self._launcher
            )
        )


class IntervalSettingTests(unittest.TestCase):
    def test_default_and_fallbacks(self) -> None:
        self.assertEqual(flush.resolve_compile_min_interval_hours({}), 20.0)
        self.assertEqual(
            flush.resolve_compile_min_interval_hours(
                {flush.COMPILE_MIN_INTERVAL_ENV: "6"}
            ),
            6.0,
        )
        self.assertEqual(
            flush.resolve_compile_min_interval_hours(
                {flush.COMPILE_MIN_INTERVAL_ENV: "0"}
            ),
            0.0,
        )
        for junk in ("", "yirmi", "-4"):
            with self.subTest(junk=junk):
                self.assertEqual(
                    flush.resolve_compile_min_interval_hours(
                        {flush.COMPILE_MIN_INTERVAL_ENV: junk}
                    ),
                    20.0,
                )


class EpistemicStatusPromptTests(unittest.TestCase):
    """Item 5 lives in the compiler's own prompt; assert it is actually there."""

    def _prompt(self) -> str:
        return compile_module.build_compile_prompt(
            "KÖK HARİTA",
            "kayıt | takma",
            "2026-08-27.md",
            "GÜNLÜK",
            "2026-08-27T12:00:00+03:00",
        )

    def test_contradictions_get_an_explicit_line(self) -> None:
        prompt = self._prompt()

        self.assertIn("⚠ çelişki", prompt)
        self.assertIn("sessizce silme", prompt)

    def test_hedges_keep_their_hedge_and_their_date(self) -> None:
        prompt = self._prompt()

        self.assertIn("doğrulanmadı", prompt)
        self.assertIn("düz bir\n   olgu cümlesine çevirme", prompt)

    def test_anchors_are_declared_off_limits_to_the_model(self) -> None:
        self.assertIn("<!-- session:... -->", self._prompt())

    def test_the_timestamp_reaches_the_contradiction_rule(self) -> None:
        prompt = self._prompt()

        self.assertNotIn("{iso_timestamp}", prompt)
        self.assertEqual(prompt.count("2026-08-27T12:00:00+03:00"), 2)

    def test_the_prompt_still_formats_with_braces_in_the_data(self) -> None:
        prompt = compile_module.build_compile_prompt(
            "{kok}", "{registry}", "gun.md", "{ govde }", "ts"
        )

        self.assertIn("{ govde }", prompt)


if __name__ == "__main__":
    unittest.main()
