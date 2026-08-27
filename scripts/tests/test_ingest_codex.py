"""Codex rollout adaptörü: kabul/red filtreleri, zarf eleme, tavanlar."""

from __future__ import annotations

import datetime as dt
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers
from _helpers import codex_message, codex_meta, codex_turn_context, write_jsonl

import ingest_codex


class CodexRolloutTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.addCleanup(self._temporary.cleanup)

    def _rollout(self, name: str, records: list[dict]) -> Path:
        path = self.root / "2026" / "08" / "24" / name
        return write_jsonl(path, records)

    def test_subagent_thread_rejected(self) -> None:
        path = self._rollout(
            "rollout-2026-08-24T02-00-00-sub.jsonl",
            [
                codex_meta("sub-1", thread_source="subagent"),
                codex_message("user", "merhaba", "2026-08-24T05:00:02.000Z"),
            ],
        )
        session, reason = ingest_codex.read_rollout(path)
        self.assertIsNone(session)
        self.assertEqual(reason, "thread-source-not-user")

    def test_missing_thread_source_rejected(self) -> None:
        path = self._rollout(
            "rollout-2026-08-24T02-00-00-legacy.jsonl",
            [
                codex_meta("legacy-1", thread_source=None),
                codex_message("user", "merhaba", "2026-08-24T05:00:02.000Z"),
            ],
        )
        session, reason = ingest_codex.read_rollout(path)
        self.assertIsNone(session)
        self.assertEqual(reason, "thread-source-not-user")

    def test_noise_cwd_rejected(self) -> None:
        path = self._rollout(
            "rollout-2026-08-24T02-00-00-sys.jsonl",
            [
                codex_meta("sys-1", cwd="C:\\Windows\\System32"),
                codex_message("user", "merhaba", "2026-08-24T05:00:02.000Z"),
            ],
        )
        session, reason = ingest_codex.read_rollout(path)
        self.assertIsNone(session)
        self.assertEqual(reason, "noise-cwd")

    def test_not_session_meta_rejected(self) -> None:
        path = self._rollout(
            "rollout-2026-08-24T02-00-00-odd.jsonl",
            [
                {"type": "event_msg", "payload": {"type": "task_started"}},
                codex_meta("odd-1"),
            ],
        )
        session, reason = ingest_codex.read_rollout(path)
        self.assertIsNone(session)
        self.assertEqual(reason, "not-session-meta")

    def test_user_thread_drops_developer_and_envelopes(self) -> None:
        path = self._rollout(
            "rollout-2026-08-24T02-00-00-user.jsonl",
            [
                codex_meta("user-1"),
                codex_turn_context("gpt-5.6-sol"),
                codex_message(
                    "developer",
                    "<app-context>\nmasaüstü bağlamı\n</app-context>",
                    "2026-08-24T05:00:02.000Z",
                ),
                codex_message(
                    "developer",
                    "sistem yönergesi",
                    "2026-08-24T05:00:03.000Z",
                ),
                codex_message(
                    "user",
                    "<environment_context>\nkabuk\n</environment_context>",
                    "2026-08-24T05:00:04.000Z",
                ),
                codex_message("user", "planı  anlat", "2026-08-24T05:00:05.000Z"),
                codex_message("assistant", "plan şu", "2026-08-24T05:00:06.000Z"),
                codex_message("user", "   ", "2026-08-24T05:00:07.000Z"),
                {
                    "timestamp": "2026-08-24T05:00:08.000Z",
                    "type": "response_item",
                    "payload": {"type": "reasoning", "content": []},
                },
                codex_message("assistant", "bitti", "2026-08-24T05:00:09.000Z"),
            ],
        )
        session, reason = ingest_codex.read_rollout(path)
        self.assertEqual(reason, "")
        assert session is not None
        self.assertEqual(session.key, "user-1")
        self.assertEqual(session.model, "gpt-5.6-sol")
        self.assertEqual(
            session.turns,
            [
                ("user", "planı anlat"),
                ("assistant", "plan şu"),
                ("assistant", "bitti"),
            ],
        )
        # Tarih SON dahil edilen turdan gelir (05:00:09Z), boş turdan değil.
        self.assertEqual(
            session.when.astimezone(dt.timezone.utc).strftime("%H:%M:%S"),
            "05:00:09",
        )

    def test_line_cap_stops_cleanly(self) -> None:
        records = [codex_meta("cap-1")]
        for index in range(ingest_codex.MAX_LINES + 100):
            records.append(
                codex_message(
                    "user",
                    f"satır {index}",
                    "2026-08-24T05:00:02.000Z",
                )
            )
        path = self._rollout("rollout-2026-08-24T02-00-00-cap.jsonl", records)
        session, reason = ingest_codex.read_rollout(path)
        self.assertEqual(reason, "")
        assert session is not None
        self.assertEqual(len(session.turns), ingest_codex.MAX_LINES)

    def test_byte_cap_stops_cleanly(self) -> None:
        with mock.patch.object(ingest_codex, "MAX_BYTES", 400):
            records = [codex_meta("bytes-1")]
            for index in range(50):
                records.append(
                    codex_message(
                        "user",
                        f"uzun satır {index} " + "x" * 60,
                        "2026-08-24T05:00:02.000Z",
                    )
                )
            path = self._rollout(
                "rollout-2026-08-24T02-00-00-bytes.jsonl",
                records,
            )
            session, _ = ingest_codex.read_rollout(path)
        assert session is not None
        self.assertLess(len(session.turns), 50)
        self.assertGreater(len(session.turns), 0)

    def test_candidates_filter_and_file_prefilter(self) -> None:
        good = self._rollout(
            "rollout-2026-08-24T02-00-00-user.jsonl",
            [
                codex_meta("user-1"),
                codex_message("user", "merhaba", "2026-08-24T05:00:02.000Z"),
            ],
        )
        self._rollout(
            "rollout-2026-08-24T02-10-00-sub.jsonl",
            [codex_meta("sub-1", thread_source="subagent")],
        )
        state = {"version": 1, "sources": {}, "last_run": {}}
        sessions, rejects = ingest_codex.candidates(state, sessions_root=self.root)
        self.assertEqual([item.key for item in sessions], ["user-1"])
        self.assertEqual(len(rejects), 1)
        self.assertTrue(rejects[0][3].startswith("reject:"))

        # Dosya haritasına yazılınca aynı dosya bir daha okunmaz.
        stat_result = good.stat()
        import ingest_common

        ingest_common.record_file(
            state,
            ingest_codex.SOURCE,
            good.name,
            stat_result.st_size,
            stat_result.st_mtime,
        )
        sessions, _ = ingest_codex.candidates(state, sessions_root=self.root)
        self.assertEqual(sessions, [])


if __name__ == "__main__":
    unittest.main()
