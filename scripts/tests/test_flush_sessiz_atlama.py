# yazan: codex · model: gpt-5.6-sol
"""Eksik transkript görünür olur (çıkış 0); girdi hataları raporlanır."""

from __future__ import annotations

import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401  — scripts dizinini sys.path'e ekler

import flush


class MissingTranscriptTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state_dir = self.root / ".state"
        self.state_dir.mkdir()

        state_patch = mock.patch.object(flush, "STATE_DIR", self.state_dir)
        state_patch.start()
        self.addCleanup(state_patch.stop)

        environment = mock.patch.dict(
            "os.environ",
            {"BEYIN_INVOKED_BY": ""},
        )
        environment.start()
        self.addCleanup(environment.stop)

    def _hook_input(self, session_id: str, transcript_path: Path) -> Path:
        path = self.root / f"{session_id}-hook.json"
        path.write_text(
            json.dumps(
                {
                    "session_id": session_id,
                    "transcript_path": str(transcript_path),
                }
            ),
            encoding="utf-8",
        )
        return path

    def _run(self, hook_input: Path) -> int:
        with mock.patch.object(
            flush,
            "_run_claude",
            side_effect=AssertionError("model must not run"),
        ):
            return flush.main(["--hook-input", str(hook_input)])

    def test_missing_transcript_is_visible_but_still_exits_zero(self) -> None:
        """A5: the hook still succeeds, but the miss is no longer invisible.

        This test used to pin the opposite contract — health untouched — which
        is how session 54 (2026-09-04) could fail to reach the daily log with
        nothing anywhere saying so.
        """
        health_path = self.state_dir / "health.json"
        health_path.write_text(
            '{"error":"","sentinel":"keep"}\n', encoding="utf-8"
        )
        missing = self.root / "does-not-exist.jsonl"
        hook_input = self._hook_input("missing-transcript", missing)

        self.assertEqual(self._run(hook_input), 0)

        health = json.loads(health_path.read_text(encoding="utf-8"))
        self.assertEqual(health["component"], "flush")
        self.assertEqual(health["error"], flush.REASON_MISSING_TRANSCRIPT)
        self.assertIn(flush.REASON_MISSING_TRANSCRIPT, health["warnings"])
        self.assertEqual(health["sentinel"], "keep")

        ledger = self.state_dir / flush.DELIVERY_LEDGER_NAME
        lines = [
            json.loads(line)
            for line in ledger.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
        self.assertEqual(len(lines), 1)
        self.assertEqual(lines[0]["reason"], flush.REASON_MISSING_TRANSCRIPT)
        self.assertEqual(lines[0]["session_id"], "missing-transcript")
        self.assertEqual(lines[0]["transcript"], str(missing))
        self.assertFalse(lines[0]["ok"])

    def test_corrupt_existing_transcript_still_writes_input_error(self) -> None:
        transcript_path = self.root / "corrupt.jsonl"
        transcript_path.write_text("{not-json}\n", encoding="utf-8")
        hook_input = self._hook_input("corrupt-transcript", transcript_path)

        self.assertEqual(self._run(hook_input), 0)
        health = json.loads(
            (self.state_dir / "health.json").read_text(encoding="utf-8")
        )
        self.assertEqual(health["component"], "flush")
        self.assertEqual(health["error"], "input:transcript-jsonl-invalid:1")

    def test_stale_flush_state_sweep_is_best_effort(self) -> None:
        stale_lock = self.state_dir / "flush-old.lock"
        stale_json = self.state_dir / "flush-old.json"
        recent_lock = self.state_dir / "flush-recent.lock"
        unrelated = self.state_dir / "compile.lock"
        for path in (stale_lock, stale_json, recent_lock, unrelated):
            path.write_text("", encoding="utf-8")
        now_epoch = 2_000_000_000.0
        stale_epoch = now_epoch - flush.STALE_FLUSH_STATE_SECONDS - 1
        os.utime(stale_lock, (stale_epoch, stale_epoch))
        os.utime(stale_json, (stale_epoch, stale_epoch))
        os.utime(recent_lock, (now_epoch, now_epoch))
        os.utime(unrelated, (stale_epoch, stale_epoch))

        flush._sweep_stale_flush_state(self.state_dir, now_epoch)

        self.assertFalse(stale_lock.exists())
        self.assertFalse(stale_json.exists())
        self.assertTrue(recent_lock.exists())
        self.assertTrue(unrelated.exists())


if __name__ == "__main__":
    unittest.main()
