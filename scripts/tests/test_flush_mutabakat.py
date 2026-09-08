# yazan: codex · model: gpt-5.6-sol
"""A1-3R quiet sweep and independent reconciliation contracts."""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401
from _helpers import GOOD_SUMMARY

import flush


NOW = dt.datetime(2026, 9, 8, 12, 0, tzinfo=dt.timezone.utc)


def _turn(role: str, text: str, when: dt.datetime) -> str:
    stamp = when.isoformat().replace("+00:00", "Z")
    return json.dumps(
        {"timestamp": stamp, "message": {"role": role, "content": text}}
    )


class ReconcileHarness(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state = self.root / ".claude" / "scripts" / ".state"
        self.state.mkdir(parents=True)
        self.projects = self.root / "projects" / "fixture"
        self.projects.mkdir(parents=True)
        self.calls: list[str] = []

    def transcript(
        self,
        session_id: str,
        times: list[dt.datetime],
    ) -> Path:
        path = self.projects / f"{session_id}.jsonl"
        lines = [
            _turn("user" if index % 2 == 0 else "assistant", f"turn-{index}", when)
            for index, when in enumerate(times)
        ]
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return path

    def patches(self, environment: dict[str, str] | None = None):
        def runner(prompt: str, *_args, **_kwargs):
            self.calls.append(prompt)
            return GOOD_SUMMARY, None

        return (
            mock.patch.object(flush, "STATE_DIR", self.state),
            mock.patch.object(flush, "VAULT_ROOT", self.root),
            mock.patch.dict(flush.os.environ, environment or {}, clear=True),
            mock.patch.object(flush, "_run_claude", side_effect=runner),
            mock.patch.object(flush, "maybe_trigger_compile", return_value=False),
        )


class QuietRuleTests(ReconcileHarness):
    def test_young_tail_is_left_for_a_later_sweep(self) -> None:
        self.transcript("quiet-session", [NOW - dt.timedelta(hours=1), NOW])

        with self.patches()[0], self.patches()[1], self.patches()[2], self.patches()[3], self.patches()[4]:
            counts = flush.sweep(event_time=NOW, projects_dir=self.projects.parent)

        self.assertEqual(counts["ozetlenen"], 0)
        self.assertEqual(counts["atlanan"], 1)
        self.assertEqual(self.calls, [])

    def test_four_hour_old_prefix_overrides_a_young_tail(self) -> None:
        self.transcript("deferred-session", [NOW - dt.timedelta(hours=5), NOW])
        patches = self.patches()
        with patches[0], patches[1], patches[2], patches[3], patches[4]:
            counts = flush.sweep(event_time=NOW, projects_dir=self.projects.parent)

        self.assertEqual(counts["ozetlenen"], 1)
        self.assertEqual(len(self.calls), 1)


class ManifestTests(ReconcileHarness):
    def test_manifest_distinguishes_covered_and_uncovered_sessions(self) -> None:
        times = [NOW - dt.timedelta(hours=2)] * 10
        covered = self.transcript("covered-session", times)
        uncovered = self.transcript("uncovered-session", times)
        del covered, uncovered
        progress = {
            "kapsanan": [[0, 10]],
            "cursor": 10,
            "parca_siniri": None,
            "basarisiz_parca": None,
            "parked": None,
        }
        with mock.patch.object(flush, "STATE_DIR", self.state):
            flush._write_flush_state(
                self.state,
                "covered-session",
                NOW.timestamp(),
                "ok",
                progress=progress,
            )
        daily = self.root / "daily"
        daily.mkdir()
        (daily / "2026-09-08.md").write_text(
            flush.session_anchor("covered-session", NOW) + "\n",
            encoding="utf-8",
        )

        patches = self.patches()
        with patches[0], patches[1], patches[2]:
            manifest = flush.reconcile(
                event_time=NOW, projects_dir=self.projects.parent
            )

        self.assertEqual(manifest["uncovered_session_count"], 1)
        by_id = {entry["session_id"]: entry for entry in manifest["sessions"]}
        self.assertEqual(by_id["covered-session"]["uncovered_turns"], 0)
        self.assertTrue(by_id["covered-session"]["daily_marker"])
        self.assertEqual(by_id["uncovered-session"]["uncovered_turns"], 10)
        self.assertFalse(by_id["uncovered-session"]["daily_marker"])
        persisted = json.loads(
            (self.state / flush.RECONCILE_NAME).read_text(encoding="utf-8")
        )
        self.assertEqual(persisted["uncovered_session_count"], 1)
        health = json.loads((self.state / "health.json").read_text(encoding="utf-8"))
        self.assertIn(
            "warn:kapsanmayan-oturum:uncovered-session:10/10",
            health["warnings"],
        )

    def test_ingress_matches_started_or_terminal_by_child_pid(self) -> None:
        hook_state = self.root / ".claude" / "hooks" / ".state"
        hook_state.mkdir(parents=True)
        old = (NOW - dt.timedelta(minutes=20)).isoformat(timespec="seconds")
        rows = [
            {"ts": old, "hook": "flush-launch", "reason": "sessionend", "session_id": "a", "pid": 101, "started": True},
            {"ts": old, "hook": "flush-launch", "reason": "sessionend", "session_id": "b", "pid": 202, "started": True},
        ]
        (hook_state / flush.INGRESS_LEDGER_NAME).write_text(
            "\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8"
        )
        flush.record_delivery(
            self.state,
            session_id="a",
            reason=flush.REASON_STARTED,
            transcript="a.jsonl",
            when=NOW - dt.timedelta(minutes=19),
            pid=101,
        )

        patches = self.patches()
        with patches[0], patches[1], patches[2]:
            manifest = flush.reconcile(
                event_time=NOW, projects_dir=self.projects.parent
            )

        self.assertEqual(manifest["unmatched_ingress_count"], 1)
        self.assertEqual(manifest["unmatched_ingress"][0]["pid"], 202)
        health = json.loads((self.state / "health.json").read_text(encoding="utf-8"))
        self.assertIn("warn:teslimat-eslesmedi:1", health["warnings"])


if __name__ == "__main__":
    unittest.main()
