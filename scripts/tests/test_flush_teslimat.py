# yazan: claude · model: claude-opus-5
"""Flush teslimat sözleşmesi: defter, tur imleci ve dürüst sayımlar (A5).

Denetim bulgusu: bir oturum günlüğe hiç ulaşmadığında hiçbir yerde iz kalmıyor,
aynı kuyruk 60 saniyelik koruma yüzünden iki kez özetlenebiliyor ve
``format_turns`` karakter tavanından sonra bile eski tur sayısını bildiriyordu.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401  — scripts dizinini sys.path'e ekler
from _helpers import GOOD_SUMMARY

import flush


MOMENT = dt.datetime(2026, 9, 4, 21, 55, tzinfo=dt.timezone.utc)


class DeliveryHarness(unittest.TestCase):
    session_id = "teslimat"

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state_dir = self.root / ".state"
        self.state_dir.mkdir()
        self.transcript = self.root / "transcript.jsonl"
        self.hook_input = self.root / "hook.json"
        self.hook_input.write_text(
            json.dumps(
                {
                    "session_id": self.session_id,
                    "transcript_path": str(self.transcript),
                }
            ),
            encoding="utf-8",
        )
        self.calls: list[str] = []

    def _write_turns(self, count: int, *, offset: int = 0) -> None:
        lines = []
        for index in range(offset, offset + count):
            role = "user" if index % 2 == 0 else "assistant"
            lines.append(
                json.dumps(
                    {"message": {"role": role, "content": f"tur {index}"}}
                )
            )
        with self.transcript.open("a", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")

    def _run(
        self,
        *,
        result: tuple[str | None, str | None] = (GOOD_SUMMARY, None),
        reason: str = "sessionend",
        moment: dt.datetime = MOMENT,
        environment: dict[str, str] | None = None,
    ) -> int:
        args = argparse.Namespace(hook_input=self.hook_input, reason=reason)

        def stub(prompt: str, *_args, **_kwargs) -> tuple[str | None, str | None]:
            self.calls.append(prompt)
            return result

        with mock.patch.object(
            flush, "STATE_DIR", self.state_dir
        ), mock.patch.object(
            flush, "VAULT_ROOT", self.root
        ), mock.patch.dict(
            flush.os.environ, environment or {}, clear=True
        ), mock.patch.object(
            flush, "_run_claude", side_effect=stub
        ), mock.patch.object(
            flush, "maybe_trigger_compile", return_value=False
        ):
            return flush._flush_once(args, moment)

    def _ledger(self) -> list[dict]:
        path = self.state_dir / flush.DELIVERY_LEDGER_NAME
        if not path.exists():
            return []
        return [
            json.loads(line)
            for line in path.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]

    def _state(self) -> dict:
        return json.loads(
            flush._session_state_path(
                self.state_dir, self.session_id
            ).read_text(encoding="utf-8")
        )

    def _daily_blocks(self) -> list[str]:
        daily = self.root / "daily" / f"{MOMENT.strftime('%Y-%m-%d')}.md"
        if not daily.exists():
            return []
        text = daily.read_text(encoding="utf-8")
        return text.split("### Oturum ")[1:]

    def _health(self) -> dict:
        path = self.state_dir / "health.json"
        if not path.exists():
            return {"error": "", "warnings": []}
        return json.loads(path.read_text(encoding="utf-8"))


class LedgerTests(DeliveryHarness):
    def test_success_writes_an_ok_line_with_the_real_counts(self) -> None:
        self._write_turns(4)

        self.assertEqual(self._run(), 0)

        lines = self._ledger()
        self.assertEqual(len(lines), 1)
        entry = lines[0]
        self.assertEqual(entry["reason"], flush.REASON_OK)
        self.assertTrue(entry["ok"])
        self.assertEqual(entry["session_id"], self.session_id)
        self.assertEqual(entry["transcript"], str(self.transcript))
        self.assertEqual(entry["turns_seen"], 4)
        self.assertEqual(entry["turns_sent"], 4)
        self.assertEqual(entry["chunks"], 1)
        self.assertGreater(entry["chars_sent"], 0)
        # A success is not a warning: health stays clean.
        self.assertEqual(self._health()["error"], "")

    def test_rejected_summary_is_logged_and_warned(self) -> None:
        self._write_turns(4)

        self.assertEqual(self._run(result=("boş cevap", None)), 0)

        entry = self._ledger()[-1]
        self.assertEqual(entry["reason"], flush.REASON_REJECTED)
        self.assertFalse(entry["ok"])
        self.assertEqual(entry["chunks"], 1)
        self.assertIn(flush.REASON_REJECTED, self._health()["warnings"])
        self.assertEqual(self._daily_blocks(), [])

    def test_flush_bos_is_logged_and_warned(self) -> None:
        self._write_turns(4)

        self.assertEqual(self._run(result=("FLUSH_BOS", None)), 0)

        entry = self._ledger()[-1]
        self.assertEqual(entry["reason"], flush.REASON_BOS)
        self.assertEqual(entry["turns_sent"], 4)
        self.assertIn(flush.REASON_BOS, self._health()["warnings"])

    def test_missing_transcript_is_logged_without_a_model_call(self) -> None:
        self.assertEqual(self._run(), 0)

        entry = self._ledger()[-1]
        self.assertEqual(entry["reason"], flush.REASON_MISSING_TRANSCRIPT)
        self.assertEqual(entry["turns_seen"], 0)
        self.assertEqual(entry["chunks"], 0)
        self.assertEqual(self.calls, [])
        self.assertIn(
            flush.REASON_MISSING_TRANSCRIPT, self._health()["warnings"]
        )

    def test_empty_transcript_reports_no_turns(self) -> None:
        self.transcript.write_text("", encoding="utf-8")

        self.assertEqual(self._run(), 0)

        entry = self._ledger()[-1]
        self.assertEqual(entry["reason"], flush.REASON_NO_TURNS)
        self.assertEqual(self.calls, [])
        self.assertIn(flush.REASON_NO_TURNS, self._health()["warnings"])

    def test_ledger_rotates_once_it_outgrows_its_bound(self) -> None:
        path = self.state_dir / flush.DELIVERY_LEDGER_NAME
        path.write_text("x" * 200, encoding="utf-8")

        flush.record_delivery(
            self.state_dir,
            session_id=self.session_id,
            reason=flush.REASON_OK,
            transcript=self.transcript,
            max_bytes=100,
        )

        self.assertTrue(
            (self.state_dir / (flush.DELIVERY_LEDGER_NAME + ".1")).exists()
        )
        self.assertEqual(len(self._ledger()), 1)

    def test_ledger_failures_never_break_the_flush(self) -> None:
        with mock.patch.object(
            flush.Path, "open", side_effect=OSError("disk dolu")
        ):
            flush.record_delivery(
                self.state_dir,
                session_id=self.session_id,
                reason=flush.REASON_OK,
                transcript=self.transcript,
            )


class TurnCursorTests(DeliveryHarness):
    def test_replaying_the_same_transcript_yields_one_daily_block(self) -> None:
        """The 60-second guard let 2026-09-04 be summarised twice, 33 min apart."""
        self._write_turns(4)

        for _ in range(3):
            self.assertEqual(self._run(), 0)

        self.assertEqual(len(self._daily_blocks()), 1)
        self.assertEqual(len(self.calls), 1)
        reasons = [entry["reason"] for entry in self._ledger()]
        self.assertEqual(
            reasons,
            [flush.REASON_OK, flush.REASON_NO_NEW_TURNS, flush.REASON_NO_NEW_TURNS],
        )
        # A quiet re-fire is normal traffic, not a warning.
        self.assertNotIn(
            flush.REASON_NO_NEW_TURNS, self._health().get("warnings", [])
        )
        self.assertEqual(self._state()["last_turn_index"], 4)

    def test_a_growing_transcript_yields_two_disjoint_blocks(self) -> None:
        self._write_turns(4)
        self.assertEqual(self._run(), 0)
        self._write_turns(3, offset=4)
        self.assertEqual(self._run(), 0)

        self.assertEqual(len(self._daily_blocks()), 2)
        self.assertEqual(len(self.calls), 2)
        self.assertIn("tur 0", self.calls[0])
        self.assertNotIn("tur 4", self.calls[0])
        # Second call carries only the new turns — no overlap with the first.
        for index in range(4):
            self.assertNotIn(f"tur {index}", self.calls[1])
        for index in range(4, 7):
            self.assertIn(f"tur {index}", self.calls[1])
        self.assertEqual(self._state()["last_turn_index"], 7)
        seen = [entry["turns_seen"] for entry in self._ledger()]
        sent = [entry["turns_sent"] for entry in self._ledger()]
        self.assertEqual(seen, [4, 7])
        self.assertEqual(sent, [4, 3])

    def test_cursor_does_not_advance_when_the_append_fails(self) -> None:
        self._write_turns(4)

        with mock.patch.object(
            flush, "_append_daily", side_effect=OSError("günlük yazılamadı")
        ):
            self.assertEqual(self._run(), 0)

        self.assertEqual(self._state()["last_turn_index"], 0)
        self.assertEqual(self._ledger()[-1]["reason"], flush.REASON_APPEND_FAILED)

        # The same turns are offered again on the next attempt, not skipped.
        self.assertEqual(self._run(), 0)
        self.assertEqual(len(self._daily_blocks()), 1)
        self.assertEqual(self._state()["last_turn_index"], 4)

    def test_cursor_does_not_advance_on_a_rejected_summary(self) -> None:
        self._write_turns(4)
        self.assertEqual(self._run(result=(None, "claude-timeout")), 0)
        self.assertEqual(self._state()["last_turn_index"], 0)

        self.assertEqual(self._run(), 0)
        self.assertIn("tur 0", self.calls[-1])
        self.assertEqual(len(self._daily_blocks()), 1)

    def test_a_shrunken_transcript_resends_instead_of_skipping(self) -> None:
        self._write_turns(4)
        self.assertEqual(self._run(), 0)
        self.transcript.write_text("", encoding="utf-8")
        self._write_turns(2, offset=100)

        self.assertEqual(self._run(), 0)

        self.assertEqual(len(self._daily_blocks()), 2)
        self.assertIn("tur 100", self.calls[-1])
        self.assertEqual(self._state()["last_turn_index"], 2)


class HonestTurnCountTests(unittest.TestCase):
    def test_char_cap_lowers_the_reported_count(self) -> None:
        turns = [("user", "x" * 1_000) for _ in range(100)]

        rendered, count = flush.format_turns(turns)

        self.assertLessEqual(len(rendered), flush.MAX_TRANSCRIPT_CHARS)
        # Was 30 — the pre-char-cap figure — while only this many were sent.
        self.assertEqual(count, len(rendered.split("\n**")))
        self.assertLess(count, flush.MAX_TURNS)
        self.assertEqual(count, rendered.count("**User:**"))

    def test_uncapped_render_still_reports_every_turn(self) -> None:
        turns = [("user", "kısa"), ("assistant", "cevap")]

        rendered, count = flush.format_turns(turns)

        self.assertEqual(count, 2)
        self.assertEqual(rendered.count("\n") + 1, 2)

    def test_one_oversized_turn_reports_one(self) -> None:
        rendered, count = flush.format_turns([("user", "y" * 40_000)])

        self.assertEqual(count, 1)
        self.assertLessEqual(len(rendered), flush.MAX_TRANSCRIPT_CHARS)

    def test_max_turns_override_defaults_to_the_shipped_value(self) -> None:
        self.assertEqual(flush.resolve_flush_max_turns({}), (flush.MAX_TURNS, None))
        self.assertEqual(
            flush.resolve_flush_max_turns({flush.FLUSH_MAX_TURNS_ENV: "7"}),
            (7, None),
        )
        for raw in ("0", "-1", "oops", ""):
            with self.subTest(raw=raw):
                self.assertEqual(
                    flush.resolve_flush_max_turns({flush.FLUSH_MAX_TURNS_ENV: raw}),
                    (flush.MAX_TURNS, f"warn:flush-max-turns-invalid:{raw}"),
                )

    def test_max_chars_alias_resolves_and_yields_to_the_older_name(self) -> None:
        self.assertEqual(
            flush.resolve_flush_chunk_chars({flush.FLUSH_MAX_CHARS_ENV: "999"}),
            (999, None),
        )
        self.assertEqual(
            flush.resolve_flush_chunk_chars(
                {
                    flush.FLUSH_CHUNK_ENV: "111",
                    flush.FLUSH_MAX_CHARS_ENV: "999",
                }
            ),
            (111, None),
        )
        self.assertEqual(
            flush.resolve_flush_chunk_chars({flush.FLUSH_MAX_CHARS_ENV: "nope"}),
            (flush.MAX_TRANSCRIPT_CHARS, "warn:flush-max-chars-invalid:nope"),
        )


class MaxTurnsAppliedTests(DeliveryHarness):
    def test_env_override_bounds_what_the_model_sees(self) -> None:
        self._write_turns(6)

        self.assertEqual(
            self._run(environment={flush.FLUSH_MAX_TURNS_ENV: "2"}), 0
        )

        self.assertNotIn("tur 0", self.calls[0])
        self.assertIn("tur 5", self.calls[0])
        entry = self._ledger()[-1]
        self.assertEqual(entry["turns_seen"], 6)
        self.assertEqual(entry["turns_sent"], 2)
        # The cursor still clears the whole transcript: what the caps dropped
        # is dropped on purpose, and must not be re-offered forever.
        self.assertEqual(self._state()["last_turn_index"], 6)


if __name__ == "__main__":
    unittest.main()
