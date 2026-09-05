"""Publication is one transaction: every file lands, or none of them do.

Both publishers write several files that only make sense together — a concept
plus the index row that points at it, a root map plus the hubs it promises. A
rename loop that dies half way leaves the vault holding a contradiction that no
later run can detect, because each individual file is well formed.

The failure is injected at the cheapest honest place: ``os.replace`` raises on
the second live destination. No model is ever called.

yazan: claude
model: opus-5
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import compile as compile_module
import retrieve
import rootmap


INDEX_TEXT = "# İndeks\n\n| Makale | Özet | Kaynak | Güncellendi |\n|---|---|---|---|\n"
CONCEPT_TEXT = (
    "---\ntitle: Deneme\naliases: []\ntags: [not]\nsources: [2026-08-26.md]\n"
    "created: 2026-08-26\nupdated: 2026-08-26\n---\n\n# Deneme\n\nGövde.\n"
)


def _fails_on_second_note(module) -> tuple[mock._patch, list[Path]]:
    """Patch ``module.os.replace`` to fail on the second published note.

    Only the forward renames are counted — a staged ``<dest>.tmp-<run id>``
    moving onto a live ``.md`` file. The state and health writes that share
    ``os.replace``, and the rollback's own restores, are left alone.
    """
    real_replace = os.replace
    seen: list[Path] = []

    def flaky(source, destination):
        text = str(destination)
        if text.endswith(".md") and ".tmp-" in str(source):
            seen.append(Path(text))
            if len(seen) == 2:
                raise OSError("sentetik yeniden adlandırma hatası")
        return real_replace(source, destination)

    return mock.patch.object(module.os, "replace", flaky), seen


def _stray_temporaries(root: Path) -> list[str]:
    return sorted(
        path.name
        for path in root.rglob("*")
        if ".tmp-" in path.name or ".bak-" in path.name
    )


class CompilePromotionTransactionTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.state_dir = self.root / ".claude" / "scripts" / ".state"
        self.state_dir.mkdir(parents=True)
        self.state_path = self.state_dir / "compile-state.json"
        self.knowledge = self.root / "knowledge"
        self.concepts = self.knowledge / "concepts"
        self.concepts.mkdir(parents=True)
        (self.knowledge / "connections").mkdir()
        (self.knowledge / "index.md").write_text(INDEX_TEXT, encoding="utf-8")
        (self.knowledge / "index-full.md").write_text(INDEX_TEXT, encoding="utf-8")
        (self.knowledge / "log.md").write_text("# Log\n", encoding="utf-8")
        (self.root / "daily").mkdir()
        (self.concepts / "deneme.md").write_text(CONCEPT_TEXT, encoding="utf-8")

        for name, value in {
            "VAULT_ROOT": self.root,
            "STATE_DIR": self.state_dir,
            "STAGE_ROOT": self.root / ".stage",
        }.items():
            patcher = mock.patch.object(compile_module, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        environment = mock.patch.dict(os.environ, {"BEYIN_INVOKED_BY": ""})
        environment.start()
        self.addCleanup(environment.stop)
        regenerate = mock.patch.object(rootmap, "regenerate")
        regenerate.start()
        self.addCleanup(regenerate.stop)
        build_index = mock.patch.object(retrieve, "build_index")
        build_index.start()
        self.addCleanup(build_index.stop)

        (self.root / "daily" / "2026-08-26.md").write_text(
            "# Günlük Log\n\nİçerik.\n", encoding="utf-8"
        )

    def _state(self) -> dict:
        if not self.state_path.exists():
            return {}
        return json.loads(self.state_path.read_text(encoding="utf-8"))

    def _stub(self, _prompt: str, stage: Path) -> str | None:
        (stage / "knowledge" / "concepts" / "deneme.md").write_text(
            CONCEPT_TEXT.replace("Gövde.", "Yeni gövde."), encoding="utf-8"
        )
        full = stage / "knowledge" / "index-full.md"
        full.write_text(
            full.read_text(encoding="utf-8") + "| [[Deneme]] | ö | k | g |\n",
            encoding="utf-8",
        )
        return None

    def test_a_failed_rename_publishes_nothing_and_keeps_the_daily(self) -> None:
        concept_before = (self.concepts / "deneme.md").read_text(encoding="utf-8")
        index_before = (self.knowledge / "index-full.md").read_text(encoding="utf-8")

        patch, seen = _fails_on_second_note(compile_module)
        with patch, mock.patch.object(compile_module, "_run_claude", self._stub):
            self.assertEqual(compile_module.main([]), 0)

        self.assertEqual(len(seen), 2)
        # The first note was renamed and had to be restored from its backup.
        self.assertEqual(
            (self.concepts / "deneme.md").read_text(encoding="utf-8"), concept_before
        )
        self.assertEqual(
            (self.knowledge / "index-full.md").read_text(encoding="utf-8"),
            index_before,
        )
        self.assertEqual(_stray_temporaries(self.knowledge), [])
        state = self._state()
        self.assertEqual(state["ingested"], {})
        self.assertEqual(state["last_status"], "fail:stage-error")

    def test_a_new_file_rolled_back_leaves_no_trace(self) -> None:
        def stub(_prompt: str, stage: Path) -> str | None:
            (stage / "knowledge" / "concepts" / "yeni.md").write_text(
                CONCEPT_TEXT, encoding="utf-8"
            )
            full = stage / "knowledge" / "index-full.md"
            full.write_text(
                full.read_text(encoding="utf-8") + "| [[Yeni]] | ö | k | g |\n",
                encoding="utf-8",
            )
            return None

        index_before = (self.knowledge / "index-full.md").read_text(encoding="utf-8")
        patch, _seen = _fails_on_second_note(compile_module)
        with patch, mock.patch.object(compile_module, "_run_claude", stub):
            self.assertEqual(compile_module.main([]), 0)

        self.assertFalse((self.concepts / "yeni.md").exists())
        self.assertEqual(
            (self.knowledge / "index-full.md").read_text(encoding="utf-8"),
            index_before,
        )
        self.assertEqual(_stray_temporaries(self.knowledge), [])

    def test_the_daily_is_compiled_again_on_the_next_run(self) -> None:
        patch, _seen = _fails_on_second_note(compile_module)
        with patch, mock.patch.object(compile_module, "_run_claude", self._stub):
            self.assertEqual(compile_module.main([]), 0)
        with mock.patch.object(compile_module, "_run_claude", self._stub):
            self.assertEqual(compile_module.main([]), 0)

        self.assertIn("2026-08-26.md", self._state()["ingested"])
        self.assertIn(
            "Yeni gövde.", (self.concepts / "deneme.md").read_text(encoding="utf-8")
        )

    def test_a_clean_run_leaves_no_backups_behind(self) -> None:
        with mock.patch.object(compile_module, "_run_claude", self._stub):
            self.assertEqual(compile_module.main([]), 0)

        self.assertEqual(_stray_temporaries(self.knowledge), [])
        self.assertIn("2026-08-26.md", self._state()["ingested"])


class RootMapPublishTransactionTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.scripts = self.root / ".claude" / "scripts"
        self.state = self.scripts / ".state"
        self.knowledge = self.root / "knowledge"
        self.concepts = self.knowledge / "concepts"
        self.concepts.mkdir(parents=True)
        self.scripts.mkdir(parents=True)
        (self.scripts / "hub-config.json").write_text(
            json.dumps(
                {
                    "catch_all": "gunluk",
                    "hubs": [
                        {
                            "id": "isik",
                            "ad": "Işık",
                            "kapsam": "Işık konuları",
                            "tags": ["ışık"],
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
                },
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )
        (self.concepts / "rastgele.md").write_text(
            "---\ntitle: rastgele\naliases: []\ntags: []\nsources: [x.md]\n"
            "created: 2026-08-27\nupdated: 2026-08-27\n---\n\n"
            "# rastgele\n\nİlk cümle özettir. İkinci cümle.\n",
            encoding="utf-8",
        )
        (self.knowledge / "index-full.md").write_text(
            "# Tam\n\n| Makale | Özet | Kaynak | Güncellendi |\n|---|---|---|---|\n"
            "| [[rastgele]] | Özet. | x.md | 2026-08-27 |\n",
            encoding="utf-8",
        )
        self.index_before = "ESKİ KÖK HARİTA\n"
        (self.knowledge / "index.md").write_text(
            self.index_before, encoding="utf-8"
        )
        (self.knowledge / "hubs").mkdir()
        self.hub_before = "ESKİ MERKEZ\n"
        (self.knowledge / "hubs" / "isik.md").write_text(
            self.hub_before, encoding="utf-8"
        )

    def test_a_failed_rename_publishes_no_part_of_the_map(self) -> None:
        patch, seen = _fails_on_second_note(rootmap)
        with patch:
            with self.assertRaises(OSError):
                rootmap.regenerate(vault_root=self.root, state_dir=self.state)

        self.assertEqual(len(seen), 2)
        self.assertEqual(
            (self.knowledge / "index.md").read_text(encoding="utf-8"),
            self.index_before,
        )
        self.assertEqual(
            (self.knowledge / "hubs" / "isik.md").read_text(encoding="utf-8"),
            self.hub_before,
        )
        self.assertFalse((self.knowledge / "hubs" / "gunluk.md").exists())
        self.assertEqual(_stray_temporaries(self.knowledge), [])

    def test_a_clean_publication_replaces_every_file(self) -> None:
        rootmap.regenerate(vault_root=self.root, state_dir=self.state)

        self.assertIn(
            "# Kök Harita", (self.knowledge / "index.md").read_text(encoding="utf-8")
        )
        self.assertTrue((self.knowledge / "hubs" / "gunluk.md").is_file())
        self.assertEqual(_stray_temporaries(self.knowledge), [])


if __name__ == "__main__":
    unittest.main()
