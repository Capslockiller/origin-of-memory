"""Context-bridge splicing, refusal, and consent rules."""

# yazan: odena · claude-opus-5

from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401  — scripts dizinini sys.path'e ekler

import context_bridge


ROOT_MAP = """---
yazan: codex
model: gpt-5.6-sol
---

# Kök Harita

| Konu merkezi | Kavram |
|---|---:|
| [[hubs/tribun|TRIBUN]] | 19 |
"""


class ContextBridgeTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.vault = Path(self._temporary.name).resolve()
        self.state = self.vault / ".state"
        self.state.mkdir()
        knowledge = self.vault / "knowledge"
        knowledge.mkdir()
        (knowledge / "index.md").write_text(ROOT_MAP, encoding="utf-8")

    def _refresh(self, targets=("AGENTS.md",)) -> dict:
        return context_bridge.refresh(
            vault_root=self.vault, state_dir=self.state, targets=targets
        )

    def _health(self) -> dict:
        path = self.state / "health.json"
        if not path.is_file():
            return {}
        return json.loads(path.read_text(encoding="utf-8"))

    # -- consent ---------------------------------------------------------

    def test_missing_file_is_never_created(self) -> None:
        report = self._refresh()
        self.assertEqual(report["targets"]["AGENTS.md"], "missing")
        self.assertFalse((self.vault / "AGENTS.md").exists())

    def test_only_named_targets_are_touched(self) -> None:
        (self.vault / "AGENTS.md").write_text("hello\n", encoding="utf-8")
        (self.vault / "CLAUDE.md").write_text("hello\n", encoding="utf-8")
        self._refresh(targets=("AGENTS.md",))
        self.assertEqual(
            (self.vault / "CLAUDE.md").read_text(encoding="utf-8"), "hello\n"
        )

    # -- splicing --------------------------------------------------------

    def test_first_run_appends_and_keeps_existing_text(self) -> None:
        target = self.vault / "AGENTS.md"
        target.write_text("# Project rules\n\nDo the thing.\n", encoding="utf-8")
        report = self._refresh()
        self.assertEqual(report["targets"]["AGENTS.md"], "inserted")
        text = target.read_text(encoding="utf-8")
        self.assertIn("# Project rules", text)
        self.assertIn("Do the thing.", text)
        self.assertIn(context_bridge.START_PREFIX, text)
        self.assertIn(context_bridge.END_MARKER, text)
        self.assertIn("TRIBUN", text)
        self.assertNotIn("yazan: codex", text)

    def test_map_headings_are_demoted_under_the_host(self) -> None:
        target = self.vault / "AGENTS.md"
        target.write_text("# Host document\n", encoding="utf-8")
        self._refresh()
        text = target.read_text(encoding="utf-8")
        # The host keeps the only H1; the map nests beneath it.
        self.assertIn("\n## Kök Harita\n", text)
        self.assertNotIn("\n# Kök Harita\n", text)
        self.assertEqual(
            [line for line in text.splitlines() if line.startswith("# ")],
            ["# Host document"],
        )

    def test_second_run_replaces_only_the_block(self) -> None:
        target = self.vault / "AGENTS.md"
        target.write_text("KEEP-ABOVE\n", encoding="utf-8")
        self._refresh()
        with target.open("a", encoding="utf-8") as stream:
            stream.write("\nKEEP-BELOW\n")

        (self.vault / "knowledge" / "index.md").write_text(
            ROOT_MAP.replace("TRIBUN]] | 19", "TRIBUN]] | 20"), encoding="utf-8"
        )
        report = self._refresh()
        self.assertEqual(report["targets"]["AGENTS.md"], "updated")
        text = target.read_text(encoding="utf-8")
        self.assertIn("KEEP-ABOVE", text)
        self.assertIn("KEEP-BELOW", text)
        self.assertIn("| 20 |", text)
        self.assertNotIn("| 19 |", text)
        self.assertEqual(text.count(context_bridge.END_MARKER), 1)

    def test_unchanged_map_does_not_rewrite_the_file(self) -> None:
        target = self.vault / "AGENTS.md"
        target.write_text("anchor\n", encoding="utf-8")
        self._refresh()
        before = target.read_bytes()
        stamp = target.stat().st_mtime_ns

        report = self._refresh()
        self.assertEqual(report["targets"]["AGENTS.md"], "unchanged")
        self.assertEqual(target.read_bytes(), before)
        self.assertEqual(target.stat().st_mtime_ns, stamp)

    # -- refusal ---------------------------------------------------------

    def test_half_marker_file_is_left_untouched_and_flagged(self) -> None:
        target = self.vault / "AGENTS.md"
        damaged = "top\n<!-- beyin:start (hand edited) -->\nstranded\n"
        target.write_text(damaged, encoding="utf-8")
        report = self._refresh()
        self.assertEqual(report["targets"]["AGENTS.md"], "skipped:marker-unbalanced")
        self.assertEqual(target.read_text(encoding="utf-8"), damaged)
        self.assertIn("marker-unbalanced", self._health()["error"])

    def test_inverted_markers_are_refused(self) -> None:
        target = self.vault / "AGENTS.md"
        damaged = f"{context_bridge.END_MARKER}\nx\n{context_bridge.START_MARKER}\n"
        target.write_text(damaged, encoding="utf-8")
        report = self._refresh()
        self.assertEqual(report["targets"]["AGENTS.md"], "skipped:marker-inverted")
        self.assertEqual(target.read_text(encoding="utf-8"), damaged)

    def test_duplicated_markers_are_refused(self) -> None:
        target = self.vault / "AGENTS.md"
        damaged = (
            f"{context_bridge.START_MARKER}\na\n{context_bridge.END_MARKER}\n"
            f"{context_bridge.START_MARKER}\nb\n{context_bridge.END_MARKER}\n"
        )
        target.write_text(damaged, encoding="utf-8")
        report = self._refresh()
        self.assertEqual(report["targets"]["AGENTS.md"], "skipped:marker-duplicated")
        self.assertEqual(target.read_text(encoding="utf-8"), damaged)

    def test_one_bad_target_does_not_block_the_others(self) -> None:
        (self.vault / "AGENTS.md").write_text(
            "<!-- beyin:start (broken) -->\n", encoding="utf-8"
        )
        (self.vault / "CLAUDE.md").write_text("fine\n", encoding="utf-8")
        report = self._refresh(targets=("AGENTS.md", "CLAUDE.md"))
        self.assertEqual(report["targets"]["AGENTS.md"], "skipped:marker-unbalanced")
        self.assertEqual(report["targets"]["CLAUDE.md"], "inserted")
        self.assertIn("TRIBUN", (self.vault / "CLAUDE.md").read_text(encoding="utf-8"))

    def test_secret_in_the_map_refuses_every_target(self) -> None:
        target = self.vault / "AGENTS.md"
        target.write_text("untouched\n", encoding="utf-8")
        leaked = ROOT_MAP + "\nghp_" + "a" * 36 + "\n"
        (self.vault / "knowledge" / "index.md").write_text(leaked, encoding="utf-8")
        with self.assertRaises(context_bridge.BridgeError) as caught:
            self._refresh()
        self.assertTrue(str(caught.exception).startswith("secret-detected:"))
        self.assertEqual(target.read_text(encoding="utf-8"), "untouched\n")

    def test_missing_root_map_raises(self) -> None:
        (self.vault / "knowledge" / "index.md").unlink()
        with self.assertRaises(context_bridge.BridgeError):
            self._refresh()

    # -- health ----------------------------------------------------------

    def test_clean_run_clears_the_error_flag(self) -> None:
        (self.vault / "AGENTS.md").write_text("x\n", encoding="utf-8")
        self._refresh()
        health = self._health()
        self.assertEqual(health["component"], "context-bridge")
        self.assertEqual(health["error"], "")

    # -- toggle ----------------------------------------------------------

    def test_toggle_defaults_on_and_reads_the_environment(self) -> None:
        with mock.patch.dict("os.environ", {}, clear=True):
            self.assertTrue(context_bridge.enabled())
        for value in ("off", "0", "false", "NO", " Off "):
            with mock.patch.dict("os.environ", {"BEYIN_CONTEXT_BRIDGE": value}):
                self.assertFalse(context_bridge.enabled(), msg=value)
        with mock.patch.dict("os.environ", {"BEYIN_CONTEXT_BRIDGE": "on"}):
            self.assertTrue(context_bridge.enabled())


if __name__ == "__main__":
    unittest.main()
