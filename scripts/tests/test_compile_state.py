"""compile.py state dayanıklılığı: iyi huylu "no-changes" ve bozuk state.

Hiçbir test gerçek modele çıkmaz; ``compile._run_claude`` her zaman vekille
değiştirilir ve tüm yollar geçici bir vault kökü altında çalışır.
"""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401  — scripts dizinini sys.path'e ekler

import compile as compile_module
import rootmap
import retrieve


INDEX_TEXT = "# İndeks\n\n| Makale | Özet | Kaynak | Güncellendi |\n|---|---|---|---|\n"
CONCEPT_TEXT = (
    "---\ntitle: Deneme\naliases: []\ntags: []\nsources: [x.md]\n"
    "created: 2026-08-26\nupdated: 2026-08-26\n---\n\n# Deneme\n\nGövde.\n"
)


class CompileHarness(unittest.TestCase):
    """Geçici vault kökü kurar ve modül seviyesi yolları oraya bağlar."""

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

        patches = {
            "VAULT_ROOT": self.root,
            "STATE_DIR": self.state_dir,
            "STAGE_ROOT": self.root / ".stage",
        }
        for name, value in patches.items():
            patcher = mock.patch.object(compile_module, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        environment = mock.patch.dict(
            "os.environ",
            {"BEYIN_INVOKED_BY": ""},
        )
        environment.start()
        self.addCleanup(environment.stop)
        rootmap_regenerate = mock.patch.object(rootmap, "regenerate")
        self.rootmap_regenerate = rootmap_regenerate.start()
        self.addCleanup(rootmap_regenerate.stop)
        retrieve_build = mock.patch.object(retrieve, "build_index")
        self.retrieve_build = retrieve_build.start()
        self.addCleanup(retrieve_build.stop)

    def _daily(self, name: str, body: str = "İçerik.\n") -> Path:
        path = self.root / "daily" / name
        path.write_text(f"# Günlük Log: {name[:-3]}\n\n{body}", encoding="utf-8")
        return path

    def _state(self) -> dict:
        return json.loads(self.state_path.read_text(encoding="utf-8"))

    def _health(self) -> dict:
        return json.loads(
            (self.state_dir / "health.json").read_text(encoding="utf-8")
        )

    def _statuses(self) -> list[str]:
        return [entry["status"] for entry in self._state()["runs"]]


class NoChangesDoesNotConsumeTests(CompileHarness):
    """An empty answer is benign for the RUN and never final for the DAILY.

    "The model wrote nothing" is a statement about one call, not about the
    file. Marking the daily ingested on that basis handed the model an
    irreversible authority it was never given: the source would be dropped
    from the queue forever on a single silent call.
    """

    def test_no_changes_does_not_consume_the_daily_and_continues(self) -> None:
        self._daily("2026-08-12.md")
        self._daily("2026-08-13.md")

        def stub(prompt: str, stage: Path) -> str | None:
            if "2026-08-12.md" in prompt:
                return None  # model hiçbir izinli dosyayı değiştirmedi
            target = stage / "knowledge" / "concepts" / "deneme.md"
            target.write_text(CONCEPT_TEXT, encoding="utf-8")
            return None

        with mock.patch.object(compile_module, "_run_claude", stub):
            self.assertEqual(compile_module.main([]), 0)

        state = self._state()
        self.assertNotIn("2026-08-12.md", state["ingested"])
        self.assertEqual(
            state["rejected"]["2026-08-12.md"]["reasons"], ["no-changes"]
        )
        self.assertEqual(state["rejected"]["2026-08-12.md"]["attempts"], 1)
        # The empty call stops nothing: the next daily is still compiled.
        self.assertIn("2026-08-13.md", state["ingested"])
        self.assertEqual(self._statuses(), ["ok:no-changes", "ok"])
        self.assertEqual(state["last_status"], "ok")
        self.assertEqual(state["cursor"], "2026-08-13.md")
        self.assertTrue(
            (self.root / "knowledge" / "concepts" / "deneme.md").exists()
        )

    def test_a_second_empty_answer_parks_the_daily(self) -> None:
        self._daily("2026-08-12.md")
        calls: list[str] = []

        def stub(prompt: str, _stage: Path) -> str | None:
            calls.append("call")
            return None

        with mock.patch.object(compile_module, "_run_claude", stub):
            self.assertEqual(compile_module.main([]), 0)
            self.assertEqual(len(calls), 1)
            self.assertEqual(self._state()["rejected"]["2026-08-12.md"]["attempts"], 1)
            # Re-presented rather than dropped.
            self.assertEqual(compile_module.main([]), 0)
            self.assertEqual(len(calls), 2)
            # Parked: the bound stops the nightly model call, but the file is
            # still not recorded as ingested.
            self.assertEqual(compile_module.main([]), 0)

        state = self._state()
        self.assertEqual(len(calls), 2)
        self.assertEqual(self._statuses(), ["ok:no-changes", "parked:no-changes"])
        self.assertNotIn("2026-08-12.md", state["ingested"])
        self.assertNotIn("2026-08-12.md", state["rejected"])
        parked = state["parked"]["2026-08-12.md"]
        self.assertEqual(parked["reason"], "no-changes")
        self.assertEqual(parked["attempts"], compile_module.MAX_NO_CHANGE_ATTEMPTS)
        self.assertIn("parked:no-changes", self._health()["warnings"])

    def test_editing_a_parked_daily_offers_it_again(self) -> None:
        self._daily("2026-08-12.md")
        calls: list[str] = []

        def empty(prompt: str, _stage: Path) -> str | None:
            calls.append("call")
            return None

        with mock.patch.object(compile_module, "_run_claude", empty):
            for _ in range(3):
                self.assertEqual(compile_module.main([]), 0)
        self.assertEqual(len(calls), 2)

        self._daily("2026-08-12.md", body="Yeni içerik.\n")

        def writer(_prompt: str, stage: Path) -> str | None:
            target = stage / "knowledge" / "concepts" / "deneme.md"
            target.write_text(CONCEPT_TEXT, encoding="utf-8")
            return None

        with mock.patch.object(compile_module, "_run_claude", writer):
            self.assertEqual(compile_module.main([]), 0)

        state = self._state()
        self.assertIn("2026-08-12.md", state["ingested"])
        # A daily that finally compiled keeps no rejection history.
        self.assertNotIn("2026-08-12.md", state["parked"])
        self.assertNotIn("2026-08-12.md", state["rejected"])


class StateCompatibilityTests(CompileHarness):
    def test_a_state_file_without_the_rejection_keys_still_loads(self) -> None:
        self.state_path.write_text(
            json.dumps({"ingested": {"eski.md": "abc"}, "runs": []}),
            encoding="utf-8",
        )

        state = compile_module.load_state(self.state_path)

        self.assertEqual(state["ingested"], {"eski.md": "abc"})
        self.assertEqual(state["rejected"], {})
        self.assertEqual(state["parked"], {})

    def test_wrongly_typed_rejection_keys_are_treated_as_absent(self) -> None:
        self.state_path.write_text(
            json.dumps({"ingested": {}, "rejected": [], "parked": "?"}),
            encoding="utf-8",
        )

        state = compile_module.load_state(self.state_path)

        self.assertEqual(state["rejected"], {})
        self.assertEqual(state["parked"], {})


class GenuineFailureStopsTests(CompileHarness):
    def test_model_error_records_failure_and_halts_queue(self) -> None:
        self._daily("2026-08-12.md")
        self._daily("2026-08-13.md")

        with mock.patch.object(
            compile_module,
            "_run_claude",
            lambda _prompt, _stage: "claude-exit-1",
        ):
            self.assertEqual(compile_module.main([]), 0)

        state = self._state()
        self.assertEqual(state["ingested"], {})
        self.assertEqual(self._statuses(), ["fail:claude-exit-1"])
        self.assertEqual(state["last_status"], "fail:claude-exit-1")
        self.assertEqual(self._health()["error"], "claude-exit-1")

    def test_source_changed_still_stops(self) -> None:
        daily = self._daily("2026-08-12.md")

        def stub(_prompt: str, _stage: Path) -> str | None:
            daily.write_text("değişti\n", encoding="utf-8")
            return None

        with mock.patch.object(compile_module, "_run_claude", stub):
            self.assertEqual(compile_module.main([]), 0)

        self.assertEqual(self._statuses(), ["fail:source-changed"])
        self.assertEqual(self._state()["ingested"], {})


class CorruptStateTests(CompileHarness):
    def test_unreadable_state_is_backed_up_and_traced(self) -> None:
        original = '{"ingested": {"2026-08-12.md": "abc"}, "runs": ['
        self.state_path.write_text(original, encoding="utf-8")
        self._daily("2026-08-12.md")

        self.assertEqual(compile_module.main(["--dry-run"]), 0)

        backups = sorted(self.state_dir.glob("compile-state.corrupt-*.json"))
        self.assertEqual(len(backups), 1)
        self.assertEqual(backups[0].read_text(encoding="utf-8"), original)

        health = self._health()
        warnings = health.get("warnings", [])
        self.assertTrue(
            any("state-reset:JSONDecodeError" in item for item in warnings),
            warnings,
        )
        self.assertTrue(any(backups[0].name in item for item in warnings))
        self.assertIsInstance(health.get("ts"), int)

        state = self._state()
        self.assertEqual(state["ingested"], {})
        self.assertEqual(state["last_status"], "fail:state-or-daily-read-failed")

    def test_bom_prefixed_state_is_backed_up(self) -> None:
        self.state_path.write_bytes(
            b"\xef\xbb\xbf" + json.dumps(compile_module._default_state()).encode()
        )

        self.assertEqual(compile_module.main(["--dry-run"]), 0)

        self.assertEqual(
            len(list(self.state_dir.glob("compile-state.corrupt-*.json"))),
            1,
        )


class MissingStateTests(CompileHarness):
    def test_first_run_leaves_no_backup_or_trace(self) -> None:
        self._daily("2026-08-12.md")

        self.assertEqual(compile_module.main(["--dry-run"]), 0)

        self.assertEqual(
            list(self.state_dir.glob("compile-state.corrupt-*.json")),
            [],
        )
        self.assertFalse((self.state_dir / "health.json").exists())

    def test_default_state_shape(self) -> None:
        state = compile_module.load_state(self.state_path)
        self.assertEqual(state, compile_module._default_state())


class RootMapIntegrationTests(CompileHarness):
    def test_prompt_uses_root_map_and_compact_registry(self) -> None:
        concept = self.root / "knowledge" / "concepts" / "deneme.md"
        concept.write_text(
            CONCEPT_TEXT.replace("aliases: []", 'aliases: ["örnek", "test"]'),
            encoding="utf-8",
        )
        index_full = (
            INDEX_TEXT
            + "| [[deneme]] | Uzun özet prompta girmemeli. | x.md | 2026-08-27 |\n"
        )
        registry = compile_module.build_compact_registry(
            index_full, concept.parent
        )
        prompt = compile_module.build_compile_prompt(
            "KÖK HARİTA",
            registry,
            "2026-08-27.md",
            "GÜNLÜK",
            "2026-08-27T12:00:00+03:00",
        )

        self.assertEqual(registry, "deneme | örnek; test\n")
        self.assertIn("KÖK HARİTA", prompt)
        self.assertIn("deneme | örnek; test", prompt)
        self.assertNotIn("Uzun özet prompta girmemeli", prompt)

    def test_full_index_is_allowed_append_target_but_root_map_is_read_only(self) -> None:
        self.assertTrue(
            compile_module._is_allowed_output_file("knowledge/index-full.md")
        )
        self.assertFalse(compile_module._is_allowed_output_file("knowledge/index.md"))

    def test_successful_compile_promotes_full_index_and_regenerates_map(self) -> None:
        self._daily("2026-08-12.md")

        def stub(_prompt: str, stage: Path) -> str | None:
            full = stage / "knowledge" / "index-full.md"
            full.write_text(
                full.read_text(encoding="utf-8") + "<!-- yeni satır -->\n",
                encoding="utf-8",
            )
            return None

        with mock.patch.object(compile_module, "_run_claude", stub):
            self.assertEqual(compile_module.main([]), 0)

        live_full = self.root / "knowledge" / "index-full.md"
        self.assertIn(
            "<!-- yeni satır -->", live_full.read_text(encoding="utf-8")
        )
        self.rootmap_regenerate.assert_called_once_with(
            vault_root=self.root, state_dir=self.state_dir
        )
        self.retrieve_build.assert_called_once_with(
            vault_root=self.root, state_dir=self.state_dir
        )

    def test_retrieve_rebuild_failure_rolls_back_and_fails_the_run(self) -> None:
        """The index is rebuilt BEFORE the daily is consumed, not after.

        A promotion the index never saw is invisible to every reader, so a
        failed rebuild is not a warning about a finished run — it means the
        run did not finish. The files go back and the daily stays queued.
        """
        self._daily("2026-08-12.md")
        before = (self.root / "knowledge" / "index-full.md").read_text(
            encoding="utf-8"
        )

        def stub(_prompt: str, stage: Path) -> str | None:
            full = stage / "knowledge" / "index-full.md"
            full.write_text(
                full.read_text(encoding="utf-8") + "<!-- değişiklik -->\n",
                encoding="utf-8",
            )
            (stage / "knowledge" / "concepts" / "deneme.md").write_text(
                CONCEPT_TEXT, encoding="utf-8"
            )
            return None

        self.retrieve_build.side_effect = OSError("sentetik indeks hatası")
        with mock.patch.object(compile_module, "_run_claude", stub):
            self.assertEqual(compile_module.main([]), 0)

        state = self._state()
        self.assertEqual(state["last_status"], "fail:retrieve-rebuild-failed")
        self.assertEqual(state["ingested"], {})
        self.assertEqual(state["concepts_manifest"], "")
        self.assertEqual(
            (self.root / "knowledge" / "index-full.md").read_text(
                encoding="utf-8"
            ),
            before,
        )
        self.assertFalse(
            (self.root / "knowledge" / "concepts" / "deneme.md").exists()
        )
        self.assertEqual(self._health()["error"], "retrieve-rebuild-failed")

    def test_root_map_failure_rolls_back_before_the_index_is_touched(self) -> None:
        self._daily("2026-08-12.md")
        before = (self.root / "knowledge" / "index-full.md").read_text(
            encoding="utf-8"
        )

        def stub(_prompt: str, stage: Path) -> str | None:
            full = stage / "knowledge" / "index-full.md"
            full.write_text(
                full.read_text(encoding="utf-8") + "<!-- değişiklik -->\n",
                encoding="utf-8",
            )
            return None

        self.rootmap_regenerate.side_effect = RuntimeError("harita çöktü")
        with mock.patch.object(compile_module, "_run_claude", stub):
            self.assertEqual(compile_module.main([]), 0)

        self.retrieve_build.assert_not_called()
        state = self._state()
        self.assertEqual(state["last_status"], "fail:rootmap-regen-failed")
        self.assertEqual(state["ingested"], {})
        self.assertEqual(
            (self.root / "knowledge" / "index-full.md").read_text(
                encoding="utf-8"
            ),
            before,
        )
        self.assertEqual(self._health()["error"], "rootmap-regen-failed")

    def test_a_rolled_back_daily_is_offered_again(self) -> None:
        self._daily("2026-08-12.md")

        def stub(_prompt: str, stage: Path) -> str | None:
            (stage / "knowledge" / "concepts" / "deneme.md").write_text(
                CONCEPT_TEXT, encoding="utf-8"
            )
            return None

        self.retrieve_build.side_effect = OSError("sentetik indeks hatası")
        with mock.patch.object(compile_module, "_run_claude", stub):
            self.assertEqual(compile_module.main([]), 0)
        self.retrieve_build.side_effect = None
        with mock.patch.object(compile_module, "_run_claude", stub):
            self.assertEqual(compile_module.main([]), 0)

        self.assertIn("2026-08-12.md", self._state()["ingested"])
        self.assertTrue(
            (self.root / "knowledge" / "concepts" / "deneme.md").is_file()
        )


if __name__ == "__main__":
    unittest.main()
