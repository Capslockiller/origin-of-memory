"""Root-map membership, migration, validation, and boundary tests."""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest

import _helpers  # noqa: F401  — scripts dizinini sys.path'e ekler

import rootmap


def _config() -> dict:
    return {
        "catch_all": "gunluk",
        "hubs": [
            {
                "id": "isik",
                "ad": "Işık",
                "kapsam": "Işık konuları",
                "tags": ["IŞIK"],
                "title_keys": ["İST"],
            },
            {
                "id": "diger",
                "ad": "Diğer",
                "kapsam": "Diğer konular",
                "tags": ["ortak"],
                "title_keys": [],
            },
            {
                "id": "gunluk",
                "ad": "Günlük",
                "kapsam": "Eşleşmeyen konular",
                "tags": [],
                "title_keys": [],
            },
        ],
    }


def _concept(
    name: str,
    tags: tuple[str, ...] = (),
    links: tuple[str, ...] = (),
    aliases: tuple[str, ...] = (),
) -> rootmap.Concept:
    body = "Açıklama. " + " ".join(f"[[{link}]]" for link in links)
    return rootmap.Concept(
        name=name,
        title=name,
        aliases=aliases,
        tags=tags,
        updated="2026-08-27",
        body=body,
        links=links,
    )


class MembershipTests(unittest.TestCase):
    def test_multi_membership_and_turkish_i_folding(self) -> None:
        by_tag = _concept("etiket-eslesmesi", ("ışık", "ortak"))
        by_title = _concept("istanbul-notu")

        result = rootmap.assign_memberships([by_tag, by_title], _config())

        self.assertEqual(result["isik"], [by_tag, by_title])
        self.assertEqual(result["diger"], [by_tag])
        self.assertEqual(result["gunluk"], [])
        self.assertEqual(rootmap.turkish_fold("Iİıi"), "ıiıi")

    def test_no_match_goes_only_to_catch_all(self) -> None:
        unmatched = _concept("rastgele", ("bilinmeyen",))

        result = rootmap.assign_memberships([unmatched], _config())

        self.assertEqual(result["gunluk"], [unmatched])
        self.assertEqual(result["isik"], [])
        self.assertEqual(result["diger"], [])


class RootMapHarness(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.scripts = self.root / ".claude" / "scripts"
        self.state = self.scripts / ".state"
        self.knowledge = self.root / "knowledge"
        self.concepts = self.knowledge / "concepts"
        self.concepts.mkdir(parents=True)
        self.scripts.mkdir(parents=True)
        (self.scripts / "hub-config.json").write_text(
            json.dumps(_config(), ensure_ascii=False), encoding="utf-8"
        )

    def _write_concept(
        self,
        name: str,
        tags: str,
        links: tuple[str, ...] = (),
        aliases: str = "[]",
    ) -> None:
        related = "\n".join(f"- [[{link}]] — bağlantı" for link in links)
        (self.concepts / f"{name}.md").write_text(
            "---\n"
            f"title: {name}\n"
            f"aliases: {aliases}\n"
            f"tags: {tags}\n"
            "sources: [x.md]\n"
            "created: 2026-08-27\n"
            "updated: 2026-08-27\n"
            "---\n\n"
            f"# {name}\n\nİlk cümle özettir. İkinci cümle.\n\n"
            f"## İlgili Kavramlar\n\n{related}\n",
            encoding="utf-8",
        )

    def _legacy_index(self) -> str:
        return (
            "# Eski\n\n"
            "| Makale | Özet | Kaynak | Güncellendi |\n"
            "|---|---|---|---|\n"
            "| [[istanbul-notu]] | Özet bir. | x.md | 2026-08-27 |\n"
            "|---|---|---|---|\n"
            "| [[rastgele]] | Özet iki. | x.md | 2026-08-27 |\n"
        )


class MigrationTests(RootMapHarness):
    def test_one_time_migration_keeps_rows_and_drops_stray_separators(self) -> None:
        self._write_concept("istanbul-notu", "[]")
        self._write_concept("rastgele", "[]")
        (self.knowledge / "index.md").write_text(
            self._legacy_index(), encoding="utf-8"
        )

        report = rootmap.regenerate(vault_root=self.root)

        full = (self.knowledge / "index-full.md").read_text(encoding="utf-8")
        self.assertIn(
            "| [[istanbul-notu]] | Özet bir. | x.md | 2026-08-27 |", full
        )
        self.assertIn("| [[rastgele]] | Özet iki. | x.md | 2026-08-27 |", full)
        self.assertEqual(full.splitlines().count("|---|---|---|---|"), 1)
        self.assertTrue(report["migrated"])
        self.assertEqual(report["parity"], "2/2")
        self.assertIn(
            "# Kök Harita",
            (self.knowledge / "index.md").read_text(encoding="utf-8"),
        )

        before = full
        rootmap.regenerate(vault_root=self.root)
        self.assertEqual(
            (self.knowledge / "index-full.md").read_text(encoding="utf-8"), before
        )


class ValidationTests(RootMapHarness):
    def test_validation_failure_publishes_nothing(self) -> None:
        self._write_concept("rastgele", "[]")
        old_index = "ORIGINAL INDEX\n"
        old_hub = "ORIGINAL HUB\n"
        (self.knowledge / "index.md").write_text(old_index, encoding="utf-8")
        (self.knowledge / "hubs").mkdir()
        (self.knowledge / "hubs" / "gunluk.md").write_text(
            old_hub, encoding="utf-8"
        )

        with self.assertRaises(rootmap.RootMapError):
            rootmap.regenerate(vault_root=self.root, char_budget=1)

        self.assertEqual((self.knowledge / "index.md").read_text(), old_index)
        self.assertEqual(
            (self.knowledge / "hubs" / "gunluk.md").read_text(), old_hub
        )
        self.assertFalse((self.knowledge / "index-full.md").exists())

    def test_character_budget_error_names_size_and_budget(self) -> None:
        self._write_concept("rastgele", "[]")
        (self.knowledge / "index.md").write_text(
            self._legacy_index(), encoding="utf-8"
        )

        with self.assertRaisesRegex(rootmap.RootMapError, r"root-map-budget:\d+/20"):
            rootmap.regenerate(vault_root=self.root, char_budget=20)


class BoundaryTests(unittest.TestCase):
    def test_common_members_and_directional_links_are_counted_separately(self) -> None:
        a = _concept("a", links=("b", "c"))
        b = _concept("b", links=("c",))
        c = _concept("c")
        memberships = {"source": [a, b], "target": [b, c]}

        self.assertEqual(
            rootmap.boundary_counts("source", "target", memberships),
            (1, 3),
        )
        self.assertEqual(
            rootmap.boundary_counts("target", "source", memberships),
            (1, 0),
        )


if __name__ == "__main__":
    unittest.main()
