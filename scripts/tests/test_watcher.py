"""Hookless watcher capture, anchor dedupe, corrupt input, and health failure."""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest import mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler
from _helpers import GOOD_SUMMARY

import flush
import ingest_common
import retrieve
import watcher


class WatcherTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.vault = self.root / "vault"
        self.state_dir = self.root / ".state"
        self.generic = self.root / "generic"
        self.vault.mkdir()
        self.state_dir.mkdir()
        self.generic.mkdir()
        self.now = time.time()

    def _markdown(self, name: str = "session.md") -> Path:
        path = self.generic / name
        path.write_text(
            "# Export\n\n## User\nRemember the blue gate.\n\n"
            "## Assistant\nThe blue gate is recorded.\n",
            encoding="utf-8",
        )
        os.utime(path, (self.now - 3600, self.now - 3600))
        return path

    def _args(self) -> object:
        return watcher._parse_args(
            [
                "--once",
                "--no-claude",
                "--no-codex",
                "--settle-seconds",
                "0",
                "--sleep",
                "0",
                "--generic",
                f"notes={self.generic}",
            ]
        )

    def _summary(self, *_args: object, **_kwargs: object) -> ingest_common.SummaryResult:
        return ingest_common.SummaryResult(GOOD_SUMMARY, "ok", "")

    def test_new_generic_session_is_captured_through_ingest_path(self) -> None:
        self._markdown()

        with mock.patch.object(
            ingest_common, "summarize_session", side_effect=self._summary
        ) as summarize:
            counts = watcher.sweep(
                self._args(),
                state_dir=self.state_dir,
                vault_root=self.vault,
                now_epoch=self.now,
            )

        self.assertEqual(counts["ingested"], 1)
        summarize.assert_called_once()
        daily = next((self.vault / "daily").glob("*.md"))
        anchors = retrieve.parse_session_anchors(daily.read_text(encoding="utf-8"))
        self.assertEqual(len(anchors), 1)
        self.assertTrue(anchors[0].session.startswith("generic-"))
        state = ingest_common.load_state(self.state_dir)
        entries = state["sources"]["generic:notes"]["done"]
        self.assertEqual(next(iter(entries.values()))["status"], "appended")
        self.assertTrue(next(iter(entries.values()))["watermark"])

    def test_existing_anchor_skips_before_summarization(self) -> None:
        """Mutation guard: removing the anchor check makes this test fail."""
        self._markdown()
        sessions, rejects = watcher.generic_candidates(
            ingest_common.default_state(),
            watcher.GenericRoot("notes", self.generic),
            now_epoch=self.now,
            fresh_seconds=0,
        )
        self.assertEqual(rejects, [])
        self.assertEqual(len(sessions), 1)
        flush._append_daily(
            self.vault,
            GOOD_SUMMARY,
            "sessionend",
            sessions[0].when,
            anchor=sessions[0].anchor,
        )

        with mock.patch.object(
            ingest_common,
            "summarize_session",
            side_effect=AssertionError("anchor guard did not run"),
        ) as summarize:
            counts = watcher.sweep(
                self._args(),
                state_dir=self.state_dir,
                vault_root=self.vault,
                now_epoch=self.now,
            )

        summarize.assert_not_called()
        self.assertEqual(counts["skipped"], 1)
        daily = next((self.vault / "daily").glob("*.md"))
        self.assertEqual(
            len(retrieve.parse_session_anchors(daily.read_text(encoding="utf-8"))),
            1,
        )

    def test_corrupt_transcript_is_skipped_without_killing_sweep(self) -> None:
        broken = self.generic / "broken.jsonl"
        broken.write_text("{definitely not json}\n", encoding="utf-8")
        os.utime(broken, (self.now - 3600, self.now - 3600))

        counts = watcher.sweep(
            self._args(),
            state_dir=self.state_dir,
            vault_root=self.vault,
            now_epoch=self.now,
        )
        second = watcher.sweep(
            self._args(),
            state_dir=self.state_dir,
            vault_root=self.vault,
            now_epoch=self.now,
        )

        self.assertEqual(counts["skipped"], 1)
        self.assertEqual(second["scanned"], 0)
        self.assertFalse((self.vault / "daily").exists())

    def test_unhandled_failure_turns_ingest_health_red(self) -> None:
        with mock.patch.object(
            ingest_common, "STATE_DIR", self.state_dir
        ), mock.patch.object(
            ingest_common, "VAULT_ROOT", self.vault
        ), mock.patch.object(
            watcher, "sweep", side_effect=RuntimeError("boom")
        ):
            self.assertEqual(watcher.main(["--once"]), 0)

        health = json.loads(
            (self.state_dir / ingest_common.HEALTH_NAME).read_text(encoding="utf-8")
        )
        self.assertEqual(health["component"], "ingest")
        self.assertEqual(health["error"], "watcher:unexpected:RuntimeError")


if __name__ == "__main__":
    unittest.main()
