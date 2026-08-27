"""Claude arşiv adaptörü: gürültü dizinleri, sidechain/tool_result, tarihleme."""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest import mock

import _helpers
from _helpers import claude_record, claude_tool_result_record, pad_claude_transcript

import flush
import ingest_claude
import ingest_common


OLD = time.time() - 10 * 24 * 60 * 60


class ClaudeArchiveTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.projects = self.root / "projects"
        self.state_dir = self.root / ".state"
        self.state_dir.mkdir(parents=True, exist_ok=True)
        self.addCleanup(self._temporary.cleanup)

    def _transcript(self, project: str, stem: str, records: list[dict]) -> Path:
        path = pad_claude_transcript(
            self.projects / project / f"{stem}.jsonl",
            records,
        )
        import os

        os.utime(path, (OLD, OLD))
        return path

    def test_noise_projects_excluded(self) -> None:
        for name in (
            "C--Users-demo-AppData-Local-Temp-beyin-flush-ahcl9-5y",
            "C--WINDOWS-system32",
            "E--Vault--claude-scripts--state-compile-stage-repro",
            "C--Users-demo-AppData-Local-Temp-claude-x-scratchpad-compile-repro",
        ):
            self.assertTrue(
                ingest_claude.is_noise_project(name),
                msg=name,
            )
        for name in ("E--Workspace", "C--Users-demo-priority", "D--Example-project"):
            self.assertFalse(ingest_claude.is_noise_project(name), msg=name)

    def test_optional_config_is_generic_and_tolerates_missing_file(self) -> None:
        self.assertEqual(
            ingest_claude._load_config(self.root / "missing.json"),
            ((), ()),
        )
        config = self.root / "ingest-config.json"
        config.write_text(
            json.dumps(
                {
                    "extra_projects": ["priority-project"],
                    "exclude_globs": ["*private-cache*"],
                }
            ),
            encoding="utf-8",
        )
        self.assertEqual(
            ingest_claude._load_config(config),
            (("priority-project",), ("*private-cache*",)),
        )

    def test_sidechain_and_tool_result_records_dropped(self) -> None:
        path = self._transcript(
            "E--Workspace",
            "0d47b80f-2436-41bc-a65d-c3ac5b10ee36",
            [
                claude_record("user", "gerçek soru", "2026-08-24T18:00:00.000Z"),
                claude_record(
                    "assistant",
                    "alt ajan cevabı",
                    "2026-08-24T23:00:00.000Z",
                    sidechain=True,
                    model="claude-haiku-4",
                ),
                claude_tool_result_record("2026-08-24T23:30:00.000Z"),
                claude_record(
                    "assistant",
                    "gerçek cevap",
                    "2026-08-24T21:30:00.000Z",
                    model="claude-opus-5",
                ),
            ],
        )
        turns, timestamp, model = ingest_claude.read_transcript(path)
        texts = [text for _role, text in turns]
        self.assertIn("gerçek soru", texts)
        self.assertIn("gerçek cevap", texts)
        self.assertNotIn("alt ajan cevabı", texts)
        self.assertNotIn("komut çıktısı", texts)
        self.assertEqual(model, "claude-opus-5")
        self.assertEqual(timestamp, "2026-08-24T21:30:00.000Z")

    def test_dating_uses_last_included_turn_and_crosses_midnight(self) -> None:
        value = ingest_common.to_local("2026-08-24T21:30:00.000Z")
        assert value is not None
        istanbul = value.astimezone(dt.timezone(dt.timedelta(hours=3)))
        self.assertEqual(
            istanbul.strftime("%Y-%m-%d %H:%M"),
            "2026-08-25 00:30",
        )
        self.assertIsNotNone(value.tzinfo)

    def test_candidates_skip_live_flushed_and_fresh(self) -> None:
        self._transcript(
            "E--Workspace",
            "live-session",
            [claude_record("user", "canlı", "2026-08-24T18:00:00.000Z")],
        )
        fresh = pad_claude_transcript(
            self.projects / "E--Workspace" / "fresh-session.jsonl",
            [claude_record("user", "taze", "2026-08-24T18:00:00.000Z")],
        )
        self.assertTrue(fresh.exists())
        flush._atomic_write_json(
            flush._session_state_path(self.state_dir, "live-session"),
            {"session_id": "live-session", "ts": 1, "status": "ok"},
        )
        state = ingest_common.default_state()
        sessions, skips = ingest_claude.candidates(
            state,
            projects_root=self.projects,
            state_dir=self.state_dir,
        )
        self.assertEqual(sessions, [])
        self.assertEqual(skips, [("live-session", "skipped-live")])

    def test_small_files_and_done_keys_skipped(self) -> None:
        import os

        small = self.projects / "E--Workspace" / "tiny.jsonl"
        small.parent.mkdir(parents=True, exist_ok=True)
        small.write_text("{}\n", encoding="utf-8")
        os.utime(small, (OLD, OLD))
        self._transcript(
            "E--Workspace",
            "done-session",
            [claude_record("user", "bitti", "2026-08-24T18:00:00.000Z")],
        )
        state = ingest_common.default_state()
        ingest_common.record_done(
            state,
            ingest_claude.SOURCE,
            "done-session",
            "appended",
        )
        sessions, skips = ingest_claude.candidates(
            state,
            projects_root=self.projects,
            state_dir=self.state_dir,
        )
        self.assertEqual(sessions, [])
        self.assertEqual(skips, [])

    def test_only_project_and_extra_project_flag(self) -> None:
        self._transcript(
            "priority-project",
            "priority-1",
            [claude_record("user", "priority", "2026-08-24T18:00:00.000Z")],
        )
        self._transcript(
            "regular-project",
            "regular-1",
            [claude_record("user", "regular", "2026-08-24T18:00:00.000Z")],
        )
        state = ingest_common.default_state()
        sessions, _ = ingest_claude.candidates(
            state,
            projects_root=self.projects,
            state_dir=self.state_dir,
            only_project="priority-project",
        )
        self.assertEqual([item.key for item in sessions], ["priority-1"])
        with mock.patch.object(ingest_claude, "EXTRA_PROJECTS", ("priority-project",)):
            self.assertTrue(ingest_claude.is_extra_project(sessions[0]))

        state = ingest_common.default_state()
        sessions, _ = ingest_claude.candidates(
            state,
            projects_root=self.projects,
            state_dir=self.state_dir,
        )
        self.assertEqual(len(sessions), 2)
        with mock.patch.object(ingest_claude, "EXTRA_PROJECTS", ("priority-project",)):
            self.assertFalse(
                ingest_claude.is_extra_project(
                    next(item for item in sessions if item.key == "regular-1")
                )
            )

    def test_broken_line_does_not_abort(self) -> None:
        path = self.projects / "E--Workspace" / "broken.jsonl"
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("w", encoding="utf-8") as handle:
            handle.write("{ bozuk json\n")
            handle.write(
                json.dumps(
                    claude_record("user", "sağlam", "2026-08-24T18:00:00.000Z"),
                    ensure_ascii=False,
                )
                + "\n"
            )
        turns, timestamp, _model = ingest_claude.read_transcript(path)
        self.assertEqual(turns, [("user", "sağlam")])
        self.assertEqual(timestamp, "2026-08-24T18:00:00.000Z")


if __name__ == "__main__":
    unittest.main()
