# yazan: codex · model: gpt-5.6-sol
"""A1-1C contiguous range commits, bounded drain, fragments and parking."""

from __future__ import annotations

import argparse
import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401
from _helpers import GOOD_SUMMARY

import flush


MOMENT = dt.datetime(2026, 9, 8, 12, 0, tzinfo=dt.timezone.utc)


class RangeHarness(unittest.TestCase):
    session_id = "range-session"

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state = self.root / ".state"
        self.state.mkdir()
        self.transcript = self.root / "session.jsonl"
        self.calls: list[str] = []

    def write_turns(self, texts: list[str]) -> None:
        lines = []
        for index, text in enumerate(texts):
            role = "user" if index % 2 == 0 else "assistant"
            lines.append(json.dumps({"message": {"role": role, "content": text}}))
        self.transcript.write_text("\n".join(lines) + "\n", encoding="utf-8")

    # Not ``run``: that name is ``unittest.TestCase.run`` and overriding it
    # silently replaces the whole test-execution protocol — setUp never fires
    # and every case in the file errors on a missing attribute.
    def flush_once(
        self,
        *,
        reason: str = "sessionend",
        environment: dict[str, str] | None = None,
        result: tuple[str | None, str | None] = (GOOD_SUMMARY, None),
        extra_patches: list[object] | None = None,
    ) -> int:
        def runner(prompt: str, *_args, **_kwargs):
            self.calls.append(prompt)
            return result

        patches = [
            mock.patch.object(flush, "STATE_DIR", self.state),
            mock.patch.object(flush, "VAULT_ROOT", self.root),
            mock.patch.dict(flush.os.environ, environment or {}, clear=True),
            mock.patch.object(flush, "_run_claude", side_effect=runner),
            mock.patch.object(flush, "maybe_trigger_compile", return_value=False),
        ]
        patches.extend(extra_patches or [])
        entered = []
        try:
            for patcher in patches:
                entered.append(patcher)
                patcher.start()
            return flush._flush_once(
                argparse.Namespace(hook_input=None, reason=reason),
                MOMENT,
                hook_input={
                    "session_id": self.session_id,
                    "transcript_path": str(self.transcript),
                },
            )
        finally:
            for patcher in reversed(entered):
                patcher.stop()

    def state_payload(self) -> dict:
        return json.loads(
            flush._session_state_path(self.state, self.session_id).read_text(
                encoding="utf-8"
            )
        )

    def ledger(self, reason: str | None = None) -> list[dict]:
        path = self.state / flush.DELIVERY_LEDGER_NAME
        rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
        return [row for row in rows if reason is None or row["reason"] == reason]

    def daily_text(self) -> str:
        return (self.root / "daily" / "2026-09-08.md").read_text(encoding="utf-8")


class ContiguousDrainTests(RangeHarness):
    def test_206_turns_drain_as_seven_oldest_first_chunks_over_three_runs(self) -> None:
        self.write_turns([f"turn-index-{index}" for index in range(206)])

        for _ in range(3):
            self.flush_once()

        terminal = self.ledger(flush.REASON_OK)
        self.assertEqual(len(terminal), 7)
        self.assertEqual(
            [row["range"] for row in terminal],
            [[0, 30], [30, 60], [60, 90], [90, 120], [120, 150], [150, 180], [180, 206]],
        )
        covered = [index for row in terminal for index in range(*row["range"])]
        self.assertEqual(covered, list(range(206)))
        self.assertEqual(self.state_payload()["kapsanan"], [[0, 206]])
        self.assertEqual(self.daily_text().count("<!-- flush-range "), 7)

    def test_character_cap_preserves_the_oldest_prefix(self) -> None:
        self.write_turns([f"turn-{index}-" + "x" * 30 for index in range(5)])

        self.flush_once(
            environment={
                flush.FLUSH_MAX_CHARS_ENV: "55",
                flush.FLUSH_MAX_CHUNKS_ENV: "2",
            }
        )

        terminal = self.ledger(flush.REASON_OK)
        self.assertEqual([row["range"] for row in terminal], [[0, 1], [1, 2]])
        self.assertIn("turn-0", self.calls[0])
        self.assertIn("turn-1", self.calls[1])
        self.assertNotIn("turn-4", "\n".join(self.calls))
        self.assertEqual(self.state_payload()["last_turn_index"], 2)

    def test_one_oversized_turn_is_fragmented_and_fully_committed(self) -> None:
        self.write_turns(["0123456789" * 20])
        env = {
            flush.FLUSH_MAX_CHARS_ENV: "50",
            flush.FLUSH_MAX_CHUNKS_ENV: "20",
        }

        self.flush_once(environment=env)

        terminal = self.ledger(flush.REASON_OK)
        fragments = [row["fragment"] for row in terminal]
        self.assertGreater(len(fragments), 1)
        self.assertEqual(fragments[0][0], 0)
        self.assertEqual(fragments[-1][1], fragments[-1][2])
        self.assertEqual(
            [left[1] for left in fragments[:-1]],
            [right[0] for right in fragments[1:]],
        )
        self.assertEqual(sum(row["turns_committed"] for row in terminal), 1)
        self.assertEqual(self.state_payload()["kapsanan"], [[0, 1]])
        self.assertNotIn("parca_siniri", self.state_payload())

    def test_existing_daily_marker_recovers_state_without_duplicate(self) -> None:
        self.write_turns([f"turn-{index}" for index in range(4)])
        chunk = {
            "turn_start": 0,
            "turn_end": 4,
            "rendered": "unused",
            "turns_sent": 4,
            "fragment": None,
        }
        marker = flush._range_marker(self.session_id, chunk)
        with mock.patch.object(flush, "STATE_DIR", self.state):
            flush._append_daily(
                self.root,
                GOOD_SUMMARY,
                "sessionend",
                MOMENT,
                anchor=flush.session_anchor(self.session_id, MOMENT) + "\n" + marker,
                idempotency_marker=marker,
            )

        self.flush_once()

        self.assertEqual(self.calls, [])
        self.assertEqual(self.daily_text().count(marker), 1)
        self.assertEqual(self.state_payload()["kapsanan"], [[0, 4]])

    def test_precompact_then_sessionend_never_overlap_or_drop(self) -> None:
        self.write_turns([f"turn-{index}" for index in range(50)])
        env = {flush.FLUSH_MAX_TURNS_ENV: "10"}

        self.flush_once(reason="precompact", environment=env)
        self.flush_once(reason="sessionend", environment=env)

        ranges = [row["range"] for row in self.ledger(flush.REASON_OK)]
        self.assertEqual(ranges, [[0, 10], [10, 20], [20, 30], [30, 40], [40, 50]])
        self.assertEqual(self.state_payload()["kapsanan"], [[0, 50]])
        self.assertEqual(self.daily_text().count("<!-- flush-range "), 5)

    def test_third_failure_parks_without_moving_cursor(self) -> None:
        self.write_turns(["one", "two"])
        for _ in range(3):
            self.flush_once(result=(None, "claude-timeout"))
        calls_at_park = len(self.calls)
        self.flush_once()

        state = self.state_payload()
        self.assertEqual(state["last_turn_index"], 0)
        self.assertEqual(state["kapsanan"], [])
        self.assertEqual(state["parked"]["deneme"], 3)
        self.assertEqual(len(self.calls), calls_at_park)
        self.assertGreaterEqual(len(self.ledger(flush.REASON_PARKED)), 2)

    def test_old_scalar_cursor_upgrades_to_a_committed_range(self) -> None:
        self.write_turns([f"turn-{index}" for index in range(6)])
        flush._session_state_path(self.state, self.session_id).write_text(
            json.dumps({"session_id": self.session_id, "last_turn_index": 4}),
            encoding="utf-8",
        )

        self.flush_once()

        self.assertNotIn("turn-3", self.calls[0])
        self.assertIn("turn-4", self.calls[0])
        self.assertEqual(self.state_payload()["kapsanan"], [[0, 6]])


if __name__ == "__main__":
    unittest.main()
