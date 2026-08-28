"""The frontmatter schema gate: what it refuses, and what it must never touch.

The gate stops NEW damage at the promotion path. The live corpus predates the
schema, so the tolerant readers keep reading it and the doctor only surveys it.
Those two halves are tested together on purpose — a gate that also broke the
existing vault would pass half of this file and fail the point of it.

No model is ever called; ``compile._run_claude`` is always a stub.

yazan: claude
model: opus-5
"""

from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import compile as compile_module
import retrieve
import rootmap
import sema


INDEX_TEXT = "# İndeks\n\n| Makale | Özet | Kaynak | Güncellendi |\n|---|---|---|---|\n"
VALID = (
    "---\ntitle: Deneme\naliases: []\ntags: [not]\nsources: [2026-08-26.md]\n"
    "created: 2026-08-26\nupdated: 2026-08-26\n---\n\n# Deneme\n\nGövde.\n"
)


def without(key: str) -> str:
    """VALID minus one frontmatter line — the missing-key fixtures."""
    return "\n".join(
        line for line in VALID.splitlines() if not line.startswith(f"{key}:")
    ) + "\n"


class ValidateConceptTests(unittest.TestCase):
    """Every rule in the schema, each with the message it must produce."""

    def problems(self, text: str, name: str = "deneme.md") -> list[str]:
        return sema.validate_concept(text, Path(name))

    def test_a_valid_note_passes_clean(self) -> None:
        self.assertEqual(self.problems(VALID), [])

    def test_a_note_with_no_frontmatter_is_refused(self) -> None:
        self.assertEqual(
            self.problems("# Deneme\n\nGövde.\n"), ["frontmatter-missing:deneme.md"]
        )

    def test_an_unclosed_frontmatter_block_is_refused(self) -> None:
        self.assertEqual(
            self.problems("---\ntitle: Deneme\n\n# Deneme\n"),
            ["frontmatter-missing:deneme.md"],
        )

    def test_an_unreadable_line_names_its_line_number(self) -> None:
        broken = VALID.replace("tags: [not]", "bu bir alan değil")

        self.assertIn("frontmatter-unparsable:deneme.md:line-3", self.problems(broken))

    def test_an_unterminated_inline_list_is_not_read_as_empty(self) -> None:
        """rootmap tolerates this and returns []; a gate that agreed would be no gate."""
        broken = VALID.replace("tags: [not]", "tags: [not, ikinci")

        self.assertIn("frontmatter-unparsable:deneme.md:line-3", self.problems(broken))

    def test_a_duplicate_key_is_reported(self) -> None:
        broken = VALID.replace("tags: [not]", "tags: [not]\ntags: [ikinci]")

        self.assertIn("duplicate-key:deneme.md:tags", self.problems(broken))

    def test_every_required_key_is_required(self) -> None:
        for key in ("title", "created", "updated", "tags", "aliases", "sources"):
            with self.subTest(key=key):
                self.assertIn(
                    f"key-missing:deneme.md:{key}", self.problems(without(key))
                )

    def test_an_empty_title_is_refused(self) -> None:
        for line in ('title: ""', "title:"):
            with self.subTest(line=line):
                broken = VALID.replace("title: Deneme", line)
                self.assertIn("title-empty:deneme.md", self.problems(broken))

    def test_a_list_shaped_title_is_refused(self) -> None:
        broken = VALID.replace("title: Deneme", "title: [Deneme]")

        self.assertIn("title-not-a-string:deneme.md", self.problems(broken))

    def test_a_date_must_be_iso_and_must_exist(self) -> None:
        cases = {
            "26-08-2026": "created",
            "2026-8-6": "created",
            "dün": "created",
            "2026-02-31": "created",
        }
        for value, key in cases.items():
            with self.subTest(value=value):
                broken = VALID.replace("created: 2026-08-26", f"created: {value}")
                self.assertIn(f"date-invalid:deneme.md:{key}", self.problems(broken))

    def test_updated_is_checked_too(self) -> None:
        broken = VALID.replace("updated: 2026-08-26", "updated: yarın")

        self.assertIn("date-invalid:deneme.md:updated", self.problems(broken))

    def test_a_scalar_where_a_list_belongs_is_refused(self) -> None:
        for key in ("tags", "aliases", "sources"):
            with self.subTest(key=key):
                broken = without(key).replace(
                    "title: Deneme", f"title: Deneme\n{key}: tek"
                )
                self.assertIn(f"not-a-list:deneme.md:{key}", self.problems(broken))

    def test_empty_lists_are_valid(self) -> None:
        empties = VALID.replace("tags: [not]", "tags: []").replace(
            "sources: [2026-08-26.md]", "sources: []"
        )

        self.assertEqual(self.problems(empties), [])

    def test_a_block_list_is_valid(self) -> None:
        block = VALID.replace("tags: [not]", "tags:\n  - not\n  - ikinci")

        self.assertEqual(self.problems(block), [])

    def test_good_frontmatter_with_an_empty_body_is_refused(self) -> None:
        for body in ("", "\n\n", "   \n"):
            with self.subTest(body=repr(body)):
                text = VALID.split("---\n\n")[0] + "---\n" + body
                self.assertIn("body-empty:deneme.md", self.problems(text))


class SchemaGateHarness(unittest.TestCase):
    """A temporary vault with the compiler's module-level paths bound to it."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state_dir = self.root / ".claude" / "scripts" / ".state"
        self.state_dir.mkdir(parents=True)
        self.concepts = self.root / "knowledge" / "concepts"
        self.concepts.mkdir(parents=True)
        (self.root / "knowledge" / "connections").mkdir(parents=True)
        (self.root / "knowledge" / "index.md").write_text(INDEX_TEXT, encoding="utf-8")
        (self.root / "knowledge" / "index-full.md").write_text(
            INDEX_TEXT, encoding="utf-8"
        )
        (self.root / "knowledge" / "log.md").write_text("# Log\n", encoding="utf-8")
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
        for module, name in ((rootmap, "regenerate"), (retrieve, "build_index")):
            patcher = mock.patch.object(module, name)
            patcher.start()
            self.addCleanup(patcher.stop)

    def _daily(self, name: str = "2026-08-26.md") -> Path:
        path = self.root / "daily" / name
        path.write_text(f"# Günlük Log\n\nİçerik {name}.\n", encoding="utf-8")
        return path

    def _writer(self, notes: dict[str, str]):
        def stub(_prompt: str, stage: Path) -> str | None:
            for note, text in notes.items():
                (stage / "knowledge" / "concepts" / note).write_text(
                    text, encoding="utf-8"
                )
            return None

        return stub

    def _run(self, notes: dict[str, str], daily: str = "2026-08-26.md") -> None:
        self._daily(daily)
        with mock.patch.object(compile_module, "_run_claude", self._writer(notes)):
            self.assertEqual(compile_module.main([]), 0)

    def _health(self) -> dict:
        path = self.state_dir / "health.json"
        return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}

    def _state(self) -> dict:
        return json.loads(
            (self.state_dir / "compile-state.json").read_text(encoding="utf-8")
        )

    def _sema_quarantine(self) -> list[Path]:
        directory = self.root / ".stage" / "karantina" / "sema"
        return sorted(directory.glob("*.md")) if directory.is_dir() else []


class PromotionGateTests(SchemaGateHarness):
    def test_a_valid_note_still_promotes(self) -> None:
        self._run({"gecerli.md": VALID})

        self.assertTrue((self.concepts / "gecerli.md").is_file())
        self.assertEqual(self._sema_quarantine(), [])
        self.assertEqual(self._health().get("error"), "")

    def test_an_invalid_note_is_held_back_not_promoted(self) -> None:
        self._run({"bozuk.md": without("created")})

        self.assertFalse((self.concepts / "bozuk.md").exists())
        self.assertEqual(
            self._health()["error"],
            "schema-invalid:knowledge/concepts/bozuk.md",
        )
        self.assertEqual(self._state()["runs"][-1]["status"], "ok:schema-invalid")

    def test_the_held_note_is_quarantined_with_its_problems(self) -> None:
        self._run({"bozuk.md": without("created")})

        held = self._sema_quarantine()
        self.assertEqual(len(held), 1)
        # Preserved verbatim: quarantine keeps evidence, it does not edit it.
        self.assertEqual(held[0].read_text(encoding="utf-8"), without("created"))
        sidecar = json.loads(held[0].with_suffix(".json").read_text(encoding="utf-8"))
        self.assertIn("key-missing:bozuk.md:created", sidecar["problems"])
        self.assertEqual(sidecar["source_file"], "knowledge/concepts/bozuk.md")

    def test_nothing_is_ever_repaired(self) -> None:
        """No invented `created` date, in the vault or in the quarantined copy."""
        self._run({"bozuk.md": without("created")})

        held = self._sema_quarantine()[0].read_text(encoding="utf-8")
        self.assertNotIn("created:", held)
        self.assertFalse((self.concepts / "bozuk.md").exists())

    def test_one_bad_file_does_not_block_its_two_siblings(self) -> None:
        self._run(
            {
                "bir.md": VALID,
                "bozuk.md": VALID.replace("created: 2026-08-26", "created: dün"),
                "iki.md": VALID,
            }
        )

        self.assertTrue((self.concepts / "bir.md").is_file())
        self.assertTrue((self.concepts / "iki.md").is_file())
        self.assertFalse((self.concepts / "bozuk.md").exists())
        self.assertEqual(len(self._sema_quarantine()), 1)

    def test_a_note_with_good_frontmatter_and_no_body_is_refused(self) -> None:
        headless = VALID.split("---\n\n")[0] + "---\n"

        self._run({"govdesiz.md": headless})

        self.assertFalse((self.concepts / "govdesiz.md").exists())
        sidecar = json.loads(
            self._sema_quarantine()[0].with_suffix(".json").read_text(encoding="utf-8")
        )
        self.assertEqual(sidecar["problems"], ["body-empty:govdesiz.md"])

    def test_the_daily_is_still_marked_ingested(self) -> None:
        """A held note must not re-queue the same daily every night."""
        self._run({"bozuk.md": without("tags")})

        self.assertIn("2026-08-26.md", self._state()["ingested"])
        self.assertEqual(self._state()["last_status"], "ok")

    def test_the_index_is_not_subject_to_the_concept_schema(self) -> None:
        def stub(_prompt: str, stage: Path) -> str | None:
            full = stage / "knowledge" / "index-full.md"
            full.write_text(
                full.read_text(encoding="utf-8") + "| [[Bir]] | ö | k | g |\n",
                encoding="utf-8",
            )
            return None

        self._daily()
        with mock.patch.object(compile_module, "_run_claude", stub):
            self.assertEqual(compile_module.main([]), 0)

        self.assertEqual(self._sema_quarantine(), [])
        self.assertIn("[[Bir]]", (self.root / "knowledge" / "index-full.md").read_text(
            encoding="utf-8"
        ))


class SurveyTests(unittest.TestCase):
    """The doctor SURVEYS the live corpus. It never modifies or blocks it."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.concepts = self.root / "knowledge" / "concepts"
        self.concepts.mkdir(parents=True)
        # Three good notes and two broken ones — the shape of a real corpus that
        # grew before the schema existed. Both broken notes still have a parsable
        # frontmatter block, which is what the tolerant readers actually require.
        for name in ("bir.md", "iki.md", "uc.md"):
            (self.concepts / name).write_text(
                VALID.replace("Deneme", name[:-3]), encoding="utf-8"
            )
        (self.concepts / "bozuk-tarih.md").write_text(
            VALID.replace("created: 2026-08-26", "created: dün"), encoding="utf-8"
        )
        (self.concepts / "bozuk-alan.md").write_text(
            without("sources"), encoding="utf-8"
        )

    def test_the_survey_counts_both_broken_notes(self) -> None:
        survey = sema.survey_concepts(self.concepts)

        self.assertEqual(survey["checked"], 5)
        self.assertEqual(survey["invalid"], 2)
        self.assertEqual(
            sorted(entry["note"] for entry in survey["sample"]),
            ["bozuk-alan.md", "bozuk-tarih.md"],
        )

    def test_the_sample_is_bounded(self) -> None:
        for index in range(20):
            (self.concepts / f"ek-{index}.md").write_text("bozuk\n", encoding="utf-8")

        survey = sema.survey_concepts(self.concepts)

        self.assertEqual(survey["invalid"], 22)
        self.assertEqual(len(survey["sample"]), sema.SURVEY_SAMPLE)

    def test_a_clean_corpus_surveys_clean(self) -> None:
        for name in ("bozuk-alan.md", "bozuk-tarih.md"):
            (self.concepts / name).unlink()

        self.assertEqual(sema.survey_concepts(self.concepts)["invalid"], 0)

    def test_a_missing_directory_is_not_an_error(self) -> None:
        survey = sema.survey_concepts(self.root / "yok")

        self.assertEqual((survey["checked"], survey["invalid"]), (0, 0))

    def test_the_survey_never_writes(self) -> None:
        before = {
            path.name: (path.read_bytes(), path.stat().st_mtime_ns)
            for path in self.concepts.glob("*.md")
        }

        sema.survey_concepts(self.concepts)

        after = {
            path.name: (path.read_bytes(), path.stat().st_mtime_ns)
            for path in self.concepts.glob("*.md")
        }
        self.assertEqual(before, after)

    def test_verify_carries_the_survey_without_gating_on_it(self) -> None:
        state = self.root / ".state"
        state.mkdir()
        retrieve.build_index(vault_root=self.root, state_dir=state)

        report = retrieve.verify_index(vault_root=self.root, state_dir=state)

        self.assertEqual(report["schema_checked"], 5)
        self.assertEqual(report["schema_invalid_count"], 2)
        # Two notes fail the schema and the index is still consistent: the
        # survey reports, it does not gate.
        self.assertTrue(report["ok"])

    def test_the_survey_is_reported_even_with_no_index(self) -> None:
        report = retrieve.verify_index(
            vault_root=self.root, state_dir=self.root / "yok"
        )

        self.assertEqual(report["error"], "index-missing")
        self.assertEqual(report["schema_invalid_count"], 2)

    def test_the_tolerant_readers_still_read_the_broken_corpus(self) -> None:
        """The gate stops new damage; it must not break the existing vault."""
        state = self.root / ".state"
        state.mkdir()

        built = retrieve.build_index(vault_root=self.root, state_dir=state)
        concepts = rootmap.load_concepts(self.concepts)

        # Both readers take all five notes, schema failures included. Nothing
        # about the gate reaches back into the corpus it now guards.
        self.assertEqual(built["note_count"], 5)
        self.assertEqual(len(concepts), 5)
        self.assertIn("bozuk-tarih", [concept.name for concept in concepts])

    def test_a_note_with_no_frontmatter_at_all_is_a_pre_existing_limit(self) -> None:
        """Tolerance has a floor, and it predates this gate — pinned, not moved.

        ``read_concept`` and ``rootmap._parse_frontmatter`` both refuse a note
        with no frontmatter block at all, so such a note already broke an index
        build before the schema gate existed. The survey reports it; nothing here
        changes what the readers do with it.
        """
        (self.concepts / "bozuk-frontmatter.md").write_text(
            "# Başlıksız\n\nGövde.\n", encoding="utf-8"
        )

        self.assertEqual(sema.survey_concepts(self.concepts)["invalid"], 3)
        with self.assertRaises(retrieve.RetrieveError):
            retrieve.read_concept(self.concepts / "bozuk-frontmatter.md")


if __name__ == "__main__":
    unittest.main()
