"""Per-call accounting: what the ledger records, and what it must never record.

The ledger exists to answer "what did that cost and how long did it take" for a
backend comparison. It is a ledger, not a log: the prompt and the response never
enter it, and one test here exists purely to keep proving that.

No model is ever called; the subprocess layer is always mocked.

yazan: claude
model: opus-5
"""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import beyin_ortak
import claude_runner
import durum
import flush
import ingest_common


SECRET_PROMPT = "GIZLI-KULLANICI-METNI-bu-asla-deftere-girmemeli"
MODEL_REPLY = "GIZLI-MODEL-CEVABI-bu-da-girmemeli"

EXPECTED_FIELDS = {
    "ts",
    "backend",
    "component",
    "model_tier",
    "model_slug",
    "input_chars",
    "output_chars",
    "input_tokens_est",
    "output_tokens_est",
    "duration_ms",
    "outcome",
    "input_tokens",
    "output_tokens",
    "cache_read_tokens",
    "cache_write_tokens",
    "model_actual",
    "usage_source",
}


class _Reply:
    """Stand-in for a finished ``subprocess.run``."""

    def __init__(self, stdout: str = MODEL_REPLY, returncode: int = 0) -> None:
        self.stdout = stdout
        self.stderr = ""
        self.returncode = returncode


class LedgerHarness(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state = self.root / ".state"
        self.state.mkdir()
        self.ledger = self.state / beyin_ortak.CALLS_LEDGER_NAME
        claude_runner.last_warnings()

    def _call(
        self,
        environment: dict[str, str] | None = None,
        reply: _Reply | None = None,
        **kwargs,
    ):
        with mock.patch.dict(
            claude_runner.os.environ, environment or {}, clear=True
        ), mock.patch.object(
            claude_runner.shutil, "which", side_effect=lambda name: f"/bin/{name}"
        ), mock.patch.object(
            claude_runner.subprocess, "run", return_value=reply or _Reply()
        ):
            return claude_runner.run_claude(
                kwargs.pop("prompt", SECRET_PROMPT),
                model=kwargs.pop("model", "haiku"),
                tools="",
                timeout=240,
                cwd=self.root,
                component=kwargs.pop("component", "flush"),
                state_dir=self.state,
                **kwargs,
            )

    def _lines(self) -> list[dict]:
        if not self.ledger.exists():
            return []
        return [
            json.loads(line)
            for line in self.ledger.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]


class RecordingTests(LedgerHarness):
    def test_one_call_appends_exactly_one_well_formed_line(self) -> None:
        self._call()

        lines = self._lines()
        self.assertEqual(len(lines), 1)
        record = lines[0]
        self.assertEqual(set(record), EXPECTED_FIELDS)
        self.assertEqual(record["backend"], "claude")
        self.assertEqual(record["component"], "flush")
        self.assertEqual(record["model_tier"], "haiku")
        self.assertEqual(record["model_slug"], claude_runner.CLAUDE_MODEL_IDS["haiku"])
        self.assertEqual(record["outcome"], "ok")
        self.assertEqual(record["usage_source"], "estimate")
        self.assertIsNone(record["input_tokens"])
        self.assertIsNone(record["output_tokens"])
        self.assertIsNone(record["cache_read_tokens"])
        self.assertIsNone(record["cache_write_tokens"])
        self.assertEqual(record["model_actual"], "")
        self.assertEqual(record["input_chars"], len(SECRET_PROMPT))
        self.assertEqual(record["output_chars"], len(MODEL_REPLY))
        self.assertGreaterEqual(record["duration_ms"], 0)
        dt.datetime.fromisoformat(record["ts"])

    def test_three_calls_append_three_lines(self) -> None:
        for _ in range(3):
            self._call()

        self.assertEqual(len(self._lines()), 3)

    def test_the_token_figures_are_chars_over_four(self) -> None:
        self._call()

        record = self._lines()[0]
        self.assertEqual(record["input_tokens_est"], len(SECRET_PROMPT) // 4)
        self.assertEqual(record["output_tokens_est"], len(MODEL_REPLY) // 4)

    def test_a_failure_records_the_error_string_as_the_outcome(self) -> None:
        self._call(reply=_Reply(returncode=3))

        record = self._lines()[0]
        self.assertEqual(record["outcome"], "claude-exit-3")
        self.assertEqual(record["output_chars"], 0)

    def test_a_timeout_is_recorded_too(self) -> None:
        with mock.patch.object(
            claude_runner.shutil, "which", side_effect=lambda name: f"/bin/{name}"
        ), mock.patch.object(
            claude_runner.subprocess,
            "run",
            side_effect=claude_runner.subprocess.TimeoutExpired("claude", 240),
        ):
            claude_runner.run_claude(
                SECRET_PROMPT,
                model="haiku",
                tools="",
                timeout=240,
                cwd=self.root,
                component="flush",
                state_dir=self.state,
            )

        self.assertEqual(self._lines()[0]["outcome"], "claude-timeout")

    def test_a_missing_cli_still_produces_a_line(self) -> None:
        """An error before the process starts is still a call that happened."""
        with mock.patch.object(claude_runner.shutil, "which", return_value=None):
            claude_runner.run_claude(
                SECRET_PROMPT,
                model="haiku",
                tools="",
                timeout=240,
                cwd=self.root,
                component="ingest",
                state_dir=self.state,
            )

        record = self._lines()[0]
        self.assertEqual(record["outcome"], "claude-cli-missing")
        self.assertEqual(record["component"], "ingest")

    def test_the_resolved_slug_is_recorded_for_a_local_backend(self) -> None:
        self._call(
            {
                "BEYIN_MODEL_BACKEND": "antigravity",
                "BEYIN_AGY_MODEL_FAST": "yerel-hizli",
            }
        )

        record = self._lines()[0]
        self.assertEqual(record["backend"], "antigravity")
        self.assertEqual(record["model_tier"], "haiku")
        self.assertEqual(record["model_slug"], "yerel-hizli")

    def test_an_unmapped_tier_records_an_empty_slug_not_a_guess(self) -> None:
        self._call({"BEYIN_MODEL_BACKEND": "ollama"})

        record = self._lines()[0]
        self.assertEqual(record["outcome"], "ollama-model-unset")
        self.assertEqual(record["model_slug"], "")

    def test_a_broken_ledger_directory_never_breaks_the_call(self) -> None:
        """Accounting is reporting: it must not be able to fail a model call."""
        with mock.patch.object(
            beyin_ortak, "_rotate_calls_ledger", side_effect=OSError("disk")
        ):
            output, error = self._call()

        self.assertEqual((output, error), (MODEL_REPLY, None))


class RealUsageTests(LedgerHarness):
    """Real provider usage, captured from the claude CLI's own JSON summary."""

    JSON_REPLY = json.dumps(
        {
            "result": MODEL_REPLY,
            "usage": {
                "input_tokens": 12,
                "output_tokens": 34,
                "cache_read_input_tokens": 999000,
                "cache_creation_input_tokens": 500,
            },
            "modelUsage": {"claude-haiku-4-5-20251001": {"inputTokens": 12}},
        }
    )

    def test_real_usage_is_recorded_when_the_cli_replies_with_json(self) -> None:
        self._call(reply=_Reply(stdout=self.JSON_REPLY))

        record = self._lines()[0]
        self.assertEqual(record["usage_source"], "session-log")
        self.assertEqual(record["input_tokens"], 12)
        self.assertEqual(record["output_tokens"], 34)
        self.assertEqual(record["cache_read_tokens"], 999000)
        self.assertEqual(record["cache_write_tokens"], 500)
        self.assertEqual(record["model_actual"], "claude-haiku-4-5-20251001")
        # The text output handed back to the caller is still the plain reply,
        # not the JSON envelope it arrived in.
        self.assertEqual(record["output_chars"], len(MODEL_REPLY))

    def test_the_caller_still_gets_the_plain_text_result(self) -> None:
        output, error = self._call(reply=_Reply(stdout=self.JSON_REPLY))
        self.assertEqual((output, error), (MODEL_REPLY, None))

    def test_a_non_json_reply_falls_back_to_the_estimate(self) -> None:
        self._call(reply=_Reply(stdout=MODEL_REPLY))

        record = self._lines()[0]
        self.assertEqual(record["usage_source"], "estimate")
        self.assertIsNone(record["input_tokens"])
        self.assertIsNone(record["output_tokens"])
        self.assertIsNone(record["cache_read_tokens"])
        self.assertIsNone(record["cache_write_tokens"])
        self.assertEqual(record["model_actual"], "")

    def test_json_without_a_usage_block_falls_back_to_the_estimate(self) -> None:
        self._call(reply=_Reply(stdout=json.dumps({"result": MODEL_REPLY})))

        record = self._lines()[0]
        self.assertEqual(record["usage_source"], "estimate")
        self.assertEqual(record["output_chars"], len(MODEL_REPLY))

    def test_a_local_backend_call_never_claims_real_usage(self) -> None:
        self._call({"BEYIN_MODEL_BACKEND": "antigravity"})

        record = self._lines()[0]
        self.assertEqual(record["usage_source"], "estimate")

    def test_no_secret_content_leaks_through_the_json_envelope(self) -> None:
        self._call(reply=_Reply(stdout=self.JSON_REPLY))

        raw = self.ledger.read_text(encoding="utf-8")
        self.assertNotIn("GIZLI", raw)


class ModelIdMappingTests(unittest.TestCase):
    """Task 4: explicit model ids per tier, not the drifting CLI aliases."""

    def test_the_documented_ids_are_used_by_default(self) -> None:
        with mock.patch.dict(claude_runner.os.environ, {}, clear=True):
            self.assertEqual(
                claude_runner.resolve_claude_model_id("haiku"),
                "claude-haiku-4-5-20251001",
            )
            self.assertEqual(
                claude_runner.resolve_claude_model_id("sonnet"), "claude-sonnet-5"
            )
            self.assertEqual(
                claude_runner.resolve_claude_model_id("opus"), "claude-opus-5"
            )

    def test_an_env_override_wins_per_tier(self) -> None:
        with mock.patch.dict(
            claude_runner.os.environ,
            {"BEYIN_CLAUDE_MODEL_HAIKU": "claude-haiku-override"},
            clear=True,
        ):
            self.assertEqual(
                claude_runner.resolve_claude_model_id("haiku"), "claude-haiku-override"
            )
            self.assertEqual(
                claude_runner.resolve_claude_model_id("sonnet"), "claude-sonnet-5"
            )

    def test_an_unmapped_tier_passes_through_unchanged(self) -> None:
        with mock.patch.dict(claude_runner.os.environ, {}, clear=True):
            self.assertEqual(
                claude_runner.resolve_claude_model_id("claude-opus-5-custom"),
                "claude-opus-5-custom",
            )

    def test_the_claude_backend_argv_carries_the_mapped_id(self) -> None:
        with mock.patch.dict(
            claude_runner.os.environ, {}, clear=True
        ), mock.patch.object(
            claude_runner.shutil, "which", side_effect=lambda name: f"/bin/{name}"
        ), mock.patch.object(
            claude_runner.subprocess, "run", return_value=_Reply()
        ) as run:
            claude_runner.run_claude(
                SECRET_PROMPT,
                model="haiku",
                tools="",
                timeout=240,
                cwd=Path(tempfile.mkdtemp()),
                component="flush",
                state_dir=Path(tempfile.mkdtemp()),
            )
        argv = run.call_args[0][0]
        self.assertIn("claude-haiku-4-5-20251001", argv)
        self.assertNotIn("haiku", argv)


class ContentLeakTests(LedgerHarness):
    def test_no_prompt_or_response_text_ever_reaches_the_ledger(self) -> None:
        self._call()

        raw = self.ledger.read_text(encoding="utf-8")
        self.assertNotIn(SECRET_PROMPT, raw)
        self.assertNotIn(MODEL_REPLY, raw)
        # Not even a fragment: check the distinctive stem, not just the whole.
        self.assertNotIn("GIZLI", raw)

    def test_the_record_carries_no_field_beyond_the_declared_set(self) -> None:
        """A new field is the only way content could start leaking; pin the set."""
        self._call()

        self.assertEqual(set(self._lines()[0]), EXPECTED_FIELDS)

    def test_record_call_cannot_be_handed_content_at_all(self) -> None:
        """The signature is the guard — counts go in, so text cannot come out."""
        beyin_ortak.record_call(
            self.state,
            backend="claude",
            model_tier="haiku",
            model_slug="haiku",
            component="flush",
            input_chars=len(SECRET_PROMPT),
            output_chars=len(MODEL_REPLY),
            duration_ms=12,
            outcome="ok",
        )

        self.assertNotIn("GIZLI", self.ledger.read_text(encoding="utf-8"))


class ComponentLabelTests(LedgerHarness):
    def test_ingest_borrowing_the_flush_runner_is_still_labelled_ingest(self) -> None:
        """Default-model ingest routes through flush's wrapper; the label must not."""
        with mock.patch.object(flush, "STATE_DIR", self.state), mock.patch.object(
            claude_runner.shutil, "which", side_effect=lambda name: f"/bin/{name}"
        ), mock.patch.object(
            claude_runner.subprocess, "run", return_value=_Reply()
        ):
            ingest_common._run_claude(SECRET_PROMPT, self.root, timeout=240)
            flush._run_claude(SECRET_PROMPT, self.root, 240)

        self.assertEqual(
            [record["component"] for record in self._lines()], ["ingest", "flush"]
        )


class RotationTests(LedgerHarness):
    def _append(self, count: int, max_bytes: int, offset: int = 0) -> None:
        for index in range(count):
            beyin_ortak.record_call(
                self.state,
                backend="claude",
                model_tier="haiku",
                model_slug="haiku",
                component="flush",
                input_chars=offset + index,
                output_chars=0,
                duration_ms=index,
                outcome="ok",
                max_bytes=max_bytes,
            )

    def test_rotation_at_the_cap_keeps_the_newest_lines(self) -> None:
        self._append(200, max_bytes=4_000)

        lines = self._lines()
        self.assertLess(len(lines), 200)
        self.assertGreater(len(lines), 0)
        # The newest write always survives, and the kept block is contiguous.
        self.assertEqual(lines[-1]["input_chars"], 199)
        kept = [record["input_chars"] for record in lines]
        self.assertEqual(kept, list(range(kept[0], 200)))

    def test_every_kept_line_still_parses_after_rotation(self) -> None:
        """The rewrite seeks mid-file, so the partial head line must be dropped."""
        self._append(200, max_bytes=4_000)

        for line in self.ledger.read_text(encoding="utf-8").splitlines():
            if line.strip():
                self.assertEqual(set(json.loads(line)), EXPECTED_FIELDS)

    def test_the_file_stays_bounded(self) -> None:
        self._append(400, max_bytes=4_000)

        self.assertLessEqual(self.ledger.stat().st_size, 4_000 + 400)

    def test_below_the_cap_nothing_is_dropped(self) -> None:
        self._append(20, max_bytes=5 * 1024 * 1024)

        self.assertEqual(len(self._lines()), 20)

    def test_the_default_cap_is_five_megabytes(self) -> None:
        self.assertEqual(beyin_ortak.CALLS_LEDGER_MAX_BYTES, 5 * 1024 * 1024)


class DurumSummaryTests(unittest.TestCase):
    """The summary numbers must match a ledger you can read by hand."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.state = Path(self._temporary.name) / ".state"
        self.state.mkdir(parents=True)
        self.now = dt.datetime(2026, 8, 28, 12, 0).astimezone()

    def _write(self, records: list[dict]) -> None:
        path = self.state / beyin_ortak.CALLS_LEDGER_NAME
        with path.open("w", encoding="utf-8", newline="\n") as handle:
            for record in records:
                handle.write(json.dumps(record, ensure_ascii=False) + "\n")

    def _record(self, **overrides) -> dict:
        record = {
            "ts": (self.now - dt.timedelta(hours=1)).isoformat(timespec="seconds"),
            "backend": "claude",
            "component": "flush",
            "model_tier": "haiku",
            "model_slug": "haiku",
            "input_chars": 400,
            "output_chars": 200,
            "input_tokens_est": 100,
            "output_tokens_est": 50,
            "duration_ms": 1000,
            "outcome": "ok",
        }
        record.update(overrides)
        return record

    def _summary(self) -> dict:
        return durum.summarize_calls(self.state, now=self.now)

    def test_an_absent_ledger_summarises_to_zero(self) -> None:
        summary = self._summary()

        self.assertEqual(summary["total_calls"], 0)
        self.assertEqual(summary["backends"], [])
        self.assertEqual(summary["components"], [])
        self.assertEqual(summary["window_days"], 7)

    def test_counts_durations_and_tokens_match_the_fixture(self) -> None:
        self._write(
            [
                self._record(duration_ms=100),
                self._record(duration_ms=300),
                self._record(duration_ms=500),
                self._record(duration_ms=900, outcome="claude-timeout"),
                self._record(
                    backend="ollama",
                    component="ingest",
                    duration_ms=4000,
                    input_tokens_est=1000,
                    output_tokens_est=7,
                ),
            ]
        )

        summary = self._summary()

        self.assertEqual(summary["total_calls"], 5)
        self.assertEqual(summary["ok_calls"], 4)
        self.assertEqual(summary["failed_calls"], 1)
        claude, ollama = summary["backends"]
        self.assertEqual(claude["backend"], "claude")
        self.assertEqual(claude["calls"], 4)
        self.assertEqual(claude["median_ms"], 400)  # (300 + 500) / 2
        self.assertEqual(claude["p95_ms"], 900)  # nearest rank over 4 samples
        self.assertEqual(ollama["calls"], 1)
        self.assertEqual(ollama["median_ms"], 4000)
        flush_row, ingest_row = summary["components"]
        self.assertEqual(
            (flush_row["component"], flush_row["calls"]), ("flush", 4)
        )
        self.assertEqual(flush_row["input_tokens_est"], 400)
        self.assertEqual(flush_row["output_tokens_est"], 200)
        self.assertEqual(ingest_row["input_tokens_est"], 1000)

    def test_calls_outside_the_window_are_excluded(self) -> None:
        self._write(
            [
                self._record(),
                self._record(
                    ts=(self.now - dt.timedelta(days=8)).isoformat(timespec="seconds")
                ),
            ]
        )

        self.assertEqual(self._summary()["total_calls"], 1)

    def test_an_unreadable_line_loses_one_call_not_the_report(self) -> None:
        path = self.state / beyin_ortak.CALLS_LEDGER_NAME
        self._write([self._record(), self._record()])
        with path.open("a", encoding="utf-8") as handle:
            handle.write("{yarim satir\n")

        self.assertEqual(self._summary()["total_calls"], 2)

    def test_the_json_shape_keeps_its_rows_and_gains_calls(self) -> None:
        self._write([self._record()])

        summary = durum.build_summary(self.state, now=self.now)

        self.assertEqual(summary["schema_version"], durum.SCHEMA_VERSION)
        self.assertEqual(len(summary["rows"]), 3)
        self.assertEqual(summary["calls"]["total_calls"], 1)

    def test_the_table_prints_both_sections(self) -> None:
        self._write([self._record()])

        with mock.patch("builtins.print") as printer:
            durum._print_table(durum.build_summary(self.state, now=self.now))

        printed = "\n".join(str(call.args[0]) for call in printer.call_args_list if call.args)
        self.assertIn("model calls (last 7 days)", printed)
        self.assertIn("in tokens (est)", printed)

    def test_an_empty_ledger_says_so_rather_than_printing_an_empty_grid(self) -> None:
        with mock.patch("builtins.print") as printer:
            durum._print_table(durum.build_summary(self.state))

        printed = "\n".join(str(call.args[0]) for call in printer.call_args_list if call.args)
        self.assertIn("none recorded", printed)

    def test_reporting_still_exits_zero_with_a_corrupt_ledger(self) -> None:
        (self.state / beyin_ortak.CALLS_LEDGER_NAME).write_text(
            "cop\n", encoding="utf-8"
        )

        with mock.patch.dict("os.environ", {"BEYIN_INVOKED_BY": ""}):
            self.assertEqual(durum.main(["--state-dir", str(self.state)]), 0)


if __name__ == "__main__":
    unittest.main()
