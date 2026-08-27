# yazan: codex
# model: gpt-5.6-sol
"""Manual context-pack composition and UTF-16LE clipboard bridge."""

from __future__ import annotations

import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import context_pack
import retrieve


class ContextPackHarness(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.concepts = self.root / "knowledge" / "concepts"
        self.state = self.root / ".claude" / "scripts" / ".state"
        self.concepts.mkdir(parents=True)
        self.state.mkdir(parents=True)

    def write_map(self, text: str = "# Hafıza haritası\n\nKök içerik.") -> None:
        (self.root / "knowledge" / "index.md").write_text(text, encoding="utf-8")

    def write_note(self, name: str, body: str) -> None:
        (self.concepts / f"{name}.md").write_text(
            "---\n"
            f"title: Müşterek {name}\n"
            "aliases: []\n"
            f"tags: {json.dumps(('müşterek',), ensure_ascii=False)}\n"
            "---\n\n"
            + body,
            encoding="utf-8",
        )

    def build(self) -> None:
        retrieve.build_index(vault_root=self.root, state_dir=self.state)


class CompositionTests(ContextPackHarness):
    def test_header_map_and_top_three_notes_are_composed(self) -> None:
        self.write_map()
        for index in range(4):
            self.write_note(str(index), f"müşterek not {index}")
        self.build()

        block = context_pack.compose_context("müşterek", vault_root=self.root)

        self.assertTrue(block.startswith(context_pack.HEADER + "\n\n"))
        self.assertIn("# Hafıza haritası", block)
        self.assertEqual(block.count("### knowledge/concepts/"), 3)
        self.assertNotIn("müşterek not 3", block)

    def test_retrieval_caps_are_preserved(self) -> None:
        for index in range(4):
            self.write_note(str(index), "müşterek " + chr(65 + index) * 2_000)
        self.build()

        block = context_pack.compose_context(
            "müşterek", vault_root=self.root, limit=5, include_map=False
        )
        bodies = block.split("### knowledge/concepts/")[1:]
        rendered_bodies = [item.split("\n\n", 1)[1].rstrip() for item in bodies]

        self.assertEqual(sum(map(len, rendered_bodies)), retrieve.TOTAL_BODY_CAP)
        self.assertTrue(
            all(len(body) <= retrieve.PER_NOTE_CAP for body in rendered_bodies)
        )
        self.assertEqual(len(rendered_bodies), 3)

    def test_no_map_skips_root_map_section(self) -> None:
        self.write_map("MAP-MARKER")
        block = context_pack.compose_context(
            "soru", vault_root=self.root, include_map=False
        )
        self.assertNotIn("Root map", block)
        self.assertNotIn("MAP-MARKER", block)
        self.assertIn(context_pack.NO_DATABASE, block)

    def test_empty_state_notices(self) -> None:
        self.write_map("MAP-ONLY")
        without_db = context_pack.compose_context("soru", vault_root=self.root)
        self.assertIn("MAP-ONLY", without_db)
        self.assertIn(context_pack.NO_DATABASE, without_db)

        self.write_note("bir", "aranan içerik")
        self.build()
        (self.root / "knowledge" / "index.md").unlink()
        without_map = context_pack.compose_context("aranan", vault_root=self.root)
        self.assertIn(context_pack.NO_INDEX, without_map)
        self.assertIn("aranan içerik", without_map)

    def test_no_matches_notice(self) -> None:
        self.write_note("bir", "başka içerik")
        self.build()
        block = context_pack.compose_context("eşleşmeyen", vault_root=self.root)
        self.assertIn(context_pack.NO_MATCHES, block)


class ClipboardTests(unittest.TestCase):
    def test_clip_exe_receives_utf16le_bytes(self) -> None:
        block = "Türkçe: ığüşöçİ\n"
        completed = subprocess.CompletedProcess(["clip.exe"], 0)
        with mock.patch.object(
            context_pack.subprocess, "run", return_value=completed
        ) as run:
            context_pack.copy_to_clipboard(block)

        self.assertEqual(run.call_args.args[0], ["clip.exe"])
        self.assertEqual(run.call_args.kwargs["input"], block.encode("utf-16le"))
        self.assertFalse(run.call_args.kwargs["check"])
        self.assertEqual(
            run.call_args.kwargs["input"].decode("utf-16le"),
            block,
        )


if __name__ == "__main__":
    unittest.main()
