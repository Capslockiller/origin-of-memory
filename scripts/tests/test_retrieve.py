"""Deterministic FTS5 retrieval, Turkish folding, caps, and trust boundary."""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import retrieve

SCRIPTS_DIR = Path(__file__).resolve().parent.parent


class RetrieveHarness(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.concepts = self.root / "knowledge" / "concepts"
        self.state = self.root / ".claude" / "scripts" / ".state"
        self.concepts.mkdir(parents=True)
        self.state.mkdir(parents=True)

    @property
    def db(self) -> Path:
        return self.state / retrieve.DB_NAME

    def write_note(
        self,
        name: str,
        *,
        title: str | None = None,
        aliases: tuple[str, ...] = (),
        tags: tuple[str, ...] = (),
        body: str = "Gövde metni.",
    ) -> None:
        alias_text = json.dumps(aliases, ensure_ascii=False)
        tag_text = json.dumps(tags, ensure_ascii=False)
        (self.concepts / f"{name}.md").write_text(
            "---\n"
            f"title: {title or name}\n"
            f"aliases: {alias_text}\n"
            f"tags: {tag_text}\n"
            "sources: [sentetik.md]\n"
            "created: 2026-08-27\n"
            "updated: 2026-08-27\n"
            "---\n\n"
            f"{body}",
            encoding="utf-8",
        )

    def build(self) -> dict:
        return retrieve.build_index(vault_root=self.root, state_dir=self.state)


class BuildAndTokenTests(RetrieveHarness):
    def test_build_records_meta_and_turkish_i_matches(self) -> None:
        self.write_note("istanbul", title="istanbul belleği")

        report = self.build()
        hits = retrieve.search("İSTANBUL", db_path=self.db)

        self.assertEqual(report["note_count"], 1)
        self.assertEqual([hit.name for hit in hits], ["istanbul"])
        connection = sqlite3.connect(self.db)
        try:
            meta = dict(connection.execute("SELECT key, value FROM meta"))
        finally:
            connection.close()
        self.assertEqual(meta["note_count"], "1")
        self.assertTrue(meta["built_at"])
        self.assertEqual(retrieve.turkish_fold("Iİıi"), "ıiıi")

    def test_f5_conflates_inflected_and_base_forms(self) -> None:
        self.write_note("kavramsal", body="Kavramların bellekteki ilişkisi.")
        self.build()

        hits = retrieve.search("kavram", db_path=self.db)

        self.assertEqual([hit.name for hit in hits], ["kavramsal"])


class RankingTests(RetrieveHarness):
    def test_title_hit_outranks_body_hit(self) -> None:
        self.write_note("title-hit", title="Zümrüt", body="Sıradan gövde.")
        self.write_note("body-hit", title="Sıradan", body="Zümrüt gövde eşleşmesi.")
        self.build()

        hits = retrieve.search("zümrüt", limit=2, db_path=self.db)

        self.assertEqual([hit.name for hit in hits], ["title-hit", "body-hit"])
        self.assertLess(hits[0].score, hits[1].score)


class HookOutputTests(RetrieveHarness):
    def test_session_dedup_returns_next_ranked_note(self) -> None:
        self.write_note("bir", title="Ortak bir")
        self.write_note("iki", title="Ortak iki")
        self.build()

        first = retrieve.hook_result(
            "ortak", limit=1, session="session-1", db_path=self.db
        )
        second = retrieve.hook_result(
            "ortak", limit=1, session="session-1", db_path=self.db
        )
        third = retrieve.hook_result(
            "ortak", limit=1, session="session-1", db_path=self.db
        )

        names = [first["notes"][0]["name"], second["notes"][0]["name"]]
        self.assertEqual(set(names), {"bir", "iki"})
        self.assertEqual(third, {"notes": [], "total_chars": 0})
        ledger = self.state / "retrieve-session-session-1.json"
        self.assertEqual(
            set(json.loads(ledger.read_text(encoding="utf-8"))["returned"]),
            {"bir", "iki"},
        )

    def test_per_note_and_overall_caps_are_enforced(self) -> None:
        for index in range(4):
            self.write_note(
                f"uzun-{index}",
                title=f"Müşterek {index}",
                body="müşterek " + (chr(65 + index) * 2_000),
            )
        self.build()

        result = retrieve.hook_result("müşterek", limit=4, db_path=self.db)

        self.assertEqual(result["total_chars"], retrieve.TOTAL_BODY_CAP)
        self.assertEqual(sum(note["chars"] for note in result["notes"]), 4_500)
        self.assertTrue(
            all(note["chars"] <= retrieve.PER_NOTE_CAP for note in result["notes"])
        )
        self.assertEqual(len(result["notes"]), 3)
        self.assertTrue(all("---" not in note["body"] for note in result["notes"]))


class QueryBoundaryTests(RetrieveHarness):
    def test_fts5_syntax_is_data_not_query_language(self) -> None:
        self.write_note("guvenli", body="Eşleşen güvenli gövde.")
        self.write_note("ilgisiz", body="Tamamen başka içerik.")
        self.build()

        hits = retrieve.search(
            'eşleşen" ) OR (NEAR(foo bar)) "', limit=10, db_path=self.db
        )

        self.assertEqual([hit.name for hit in hits], ["guvenli"])

    def test_min_score_can_silence_weak_results(self) -> None:
        self.write_note("eslesme", title="Eşleşme")
        self.build()

        result = retrieve.hook_result(
            "eşleşme", min_score=1_000_000.0, db_path=self.db
        )

        self.assertEqual(result, {"notes": [], "total_chars": 0})


class HookStdinTests(RetrieveHarness):
    """D1: retrieve.py's own stdin-hook mode mirrors memory-retrieve.ps1."""

    def test_valid_payload_returns_hook_specific_output(self) -> None:
        self.write_note("ortak", title="Ortak konu", body="Ortak konu gövdesi.")
        self.build()

        raw = json.dumps({"prompt": "ortak konu hakkında bilgi ver", "session_id": "s1"})
        output = retrieve.run_hook_stdin(raw, db_path=self.db, state_dir=self.state)

        self.assertIsNotNone(output)
        payload = json.loads(output)
        context = payload["hookSpecificOutput"]["additionalContext"]
        self.assertEqual(
            payload["hookSpecificOutput"]["hookEventName"], "UserPromptSubmit"
        )
        self.assertIn("[Hafiza - Ilgili Notlar]", context)
        self.assertIn("--- knowledge/concepts/ortak.md ---", context)
        self.assertIn("Ortak konu gövdesi.", context)

    def test_user_input_field_wins_over_prompt(self) -> None:
        self.write_note("ortak", title="Ortak konu", body="Ortak konu gövdesi.")
        self.build()

        raw = json.dumps(
            {
                "user_input": "ortak konu hakkında bilgi ver",
                "prompt": "kısa",
                "session_id": "s1",
            }
        )
        output = retrieve.run_hook_stdin(raw, db_path=self.db, state_dir=self.state)

        self.assertIsNotNone(output)

    def test_short_prompt_is_skipped_silently(self) -> None:
        self.write_note("ortak", title="Ortak konu")
        self.build()

        raw = json.dumps({"prompt": "kısa mesaj", "session_id": "s1"})
        self.assertEqual(len("kısa mesaj"), 10)

        output = retrieve.run_hook_stdin(raw, db_path=self.db, state_dir=self.state)

        self.assertIsNone(output)

    def test_slash_command_is_skipped_silently(self) -> None:
        self.write_note("ortak", title="Ortak konu")
        self.build()

        raw = json.dumps({"prompt": "/compact ortak konu devam", "session_id": "s1"})

        output = retrieve.run_hook_stdin(raw, db_path=self.db, state_dir=self.state)

        self.assertIsNone(output)

    def test_malformed_json_exits_silently_not_crash(self) -> None:
        output = retrieve.run_hook_stdin(
            "{not valid json", db_path=self.db, state_dir=self.state
        )
        self.assertIsNone(output)

    def test_empty_stdin_exits_silently(self) -> None:
        self.assertIsNone(
            retrieve.run_hook_stdin("", db_path=self.db, state_dir=self.state)
        )

    def test_missing_prompt_field_exits_silently(self) -> None:
        raw = json.dumps({"session_id": "s1"})
        self.assertIsNone(
            retrieve.run_hook_stdin(raw, db_path=self.db, state_dir=self.state)
        )

    def test_no_matches_exits_silently(self) -> None:
        self.write_note("ilgisiz", title="Tamamen ilgisiz kavram")
        self.build()

        raw = json.dumps(
            {"prompt": "zqxvbnwplk fjhgtresd uyocmiaqz", "session_id": "s1"}
        )
        output = retrieve.run_hook_stdin(raw, db_path=self.db, state_dir=self.state)

        self.assertIsNone(output)

    def test_cli_hook_subcommand_end_to_end(self) -> None:
        self.write_note("ortak", title="Ortak konu", body="Ortak konu gövdesi.")
        self.build()
        raw = json.dumps({"prompt": "ortak konu hakkında bilgi ver", "session_id": "s1"})

        completed = subprocess.run(
            [
                sys.executable,
                "-X",
                "utf8",
                str(SCRIPTS_DIR / "retrieve.py"),
                "hook",
                "--db",
                str(self.db),
                "--state-dir",
                str(self.state),
            ],
            input=raw,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=30,
        )

        self.assertEqual(completed.returncode, 0)
        payload = json.loads(completed.stdout)
        self.assertIn(
            "Ortak konu gövdesi.",
            payload["hookSpecificOutput"]["additionalContext"],
        )

    def test_cli_hook_subcommand_malformed_json_exits_zero(self) -> None:
        completed = subprocess.run(
            [
                sys.executable,
                "-X",
                "utf8",
                str(SCRIPTS_DIR / "retrieve.py"),
                "hook",
                "--db",
                str(self.db),
                "--state-dir",
                str(self.state),
            ],
            input="{not valid json",
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=30,
        )

        self.assertEqual(completed.returncode, 0)
        self.assertEqual(completed.stdout.strip(), "")


class ImportHygieneTests(unittest.TestCase):
    """D2: the query path must not eagerly pay for build/verify-only imports."""

    def test_module_import_defers_sema_and_build_only_modules(self) -> None:
        completed = subprocess.run(
            [
                sys.executable,
                "-c",
                (
                    "import sys, json; import retrieve; "
                    "print(json.dumps(sorted(sys.modules)))"
                ),
            ],
            cwd=SCRIPTS_DIR,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=30,
        )

        self.assertEqual(completed.returncode, 0, completed.stderr)
        loaded = set(json.loads(completed.stdout))
        for deferred in ("sema", "rootmap", "shutil", "tempfile"):
            self.assertNotIn(
                deferred,
                loaded,
                f"{deferred!r} should not load on `import retrieve` alone",
            )


if __name__ == "__main__":
    unittest.main()
