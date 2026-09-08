# yazan: claude · model: claude-opus-5
"""Zamanlı süpürge (`flush.py --tara`) sözleşmesi.

Master kararı (2026-09-07): "8 saatte bir flush çalışsın; son flush'tan sonra
değişiklik yoksa çalışmasın." 54. oturumda SessionEnd kancası hiç teslim
edilmedi ve 19 saatlik transkript günlüğe düşmedi; süpürge flush'ı oturum
sonundan bağımsız kılar. Buradaki testler iki tasarruf kapısını da bağlar —
dosya damgası ve tur imleci — ve gece derleme kuralının gevşetilmiş halini
(``>=18:00`` **ya da** son başarıdan bu yana ``>=20 saat``) sabitler.
"""

from __future__ import annotations

import datetime as dt
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401  — scripts dizinini sys.path'e ekler
from _helpers import GOOD_SUMMARY

import flush


MOMENT = dt.datetime(2026, 9, 7, 2, 30, tzinfo=dt.timezone.utc)
SESSION = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"


class SweepHarness(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state_dir = self.root / ".state"
        self.state_dir.mkdir()
        self.projects = self.root / "projects" / "E--Proje"
        self.projects.mkdir(parents=True)
        self.transcript = self.projects / f"{SESSION}.jsonl"
        self.calls: list[str] = []

    # --- fikstürler ---------------------------------------------------
    def _write_turns(
        self,
        count: int,
        *,
        offset: int = 0,
        when: dt.datetime = MOMENT,
        path: Path | None = None,
    ) -> None:
        stamp = (
            when.astimezone(dt.timezone.utc)
            .isoformat(timespec="milliseconds")
            .replace("+00:00", "Z")
        )
        lines = []
        for index in range(offset, offset + count):
            role = "user" if index % 2 == 0 else "assistant"
            lines.append(
                json.dumps(
                    {
                        "cwd": "E:\\Proje",
                        "timestamp": stamp,
                        "message": {"role": role, "content": f"tur {index}"},
                    }
                )
            )
        target = path or self.transcript
        with target.open("a", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")

    def _append_noise(self) -> None:
        """Dosyayı büyütür ama tek bir tur eklemez (araç sonucu kaydı)."""
        with self.transcript.open("a", encoding="utf-8") as handle:
            handle.write(
                json.dumps(
                    {
                        "type": "user",
                        "message": {
                            "role": "user",
                            "content": [
                                {"type": "tool_result", "content": "cikti"}
                            ],
                        },
                    }
                )
                + "\n"
            )

    def _age(self, hours: float) -> None:
        stamp = (MOMENT - dt.timedelta(hours=hours)).timestamp()
        os.utime(self.transcript, (stamp, stamp))

    # --- koşucu -------------------------------------------------------
    def _sweep(
        self,
        *,
        result: tuple[str | None, str | None] = (GOOD_SUMMARY, None),
        since_hours: float = 8.0,
        dry_run: bool = False,
        moment: dt.datetime = MOMENT,
        lock_stub: object | None = None,
    ) -> dict[str, int]:
        def stub(prompt: str, *_args, **_kwargs) -> tuple[str | None, str | None]:
            self.calls.append(prompt)
            return result

        patches = [
            mock.patch.object(flush, "STATE_DIR", self.state_dir),
            mock.patch.object(flush, "VAULT_ROOT", self.root),
            mock.patch.dict(flush.os.environ, {}, clear=True),
            mock.patch.object(flush, "_run_claude", side_effect=stub),
            mock.patch.object(flush, "maybe_trigger_compile", return_value=False),
        ]
        if lock_stub is not None:
            patches.append(
                mock.patch.object(flush, "_lock_exclusive", side_effect=lock_stub)
            )
        with _nested(patches):
            return flush.sweep(
                event_time=moment,
                projects_dir=self.projects.parent,
                since_hours=since_hours,
                dry_run=dry_run,
            )

    # --- okuyucular ---------------------------------------------------
    def _ledger(self) -> list[dict]:
        path = self.state_dir / flush.DELIVERY_LEDGER_NAME
        if not path.exists():
            return []
        return [
            json.loads(line)
            for line in path.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]

    def _sweep_state(self) -> dict:
        path = self.state_dir / flush.SWEEP_STATE_NAME
        if not path.exists():
            return {}
        return json.loads(path.read_text(encoding="utf-8"))

    def _daily_path(self, when: dt.datetime = MOMENT) -> Path:
        # Süpürge artık girdiyi oturumun son turuna göre tarihler; beklenen
        # dosya yerel saate göre seçilir (makinenin saat dilimi ne olursa).
        local = when.astimezone()
        return self.root / "daily" / f"{local.strftime('%Y-%m-%d')}.md"

    def _daily_blocks(self, when: dt.datetime = MOMENT) -> list[str]:
        daily = self._daily_path(when)
        if not daily.exists():
            return []
        return daily.read_text(encoding="utf-8").split("### Oturum ")[1:]


class _nested:
    """``mock.patch`` yığınını tek ``with`` altında toplar (3.12 uyumlu)."""

    def __init__(self, patches):
        self._patches = patches

    def __enter__(self):
        for patch in self._patches:
            patch.start()
        return self

    def __exit__(self, *exc):
        for patch in reversed(self._patches):
            patch.stop()
        return False


class SweepTests(SweepHarness):
    def test_a_changed_transcript_lands_one_daily_block(self) -> None:
        self._write_turns(4)

        counts = self._sweep()

        self.assertEqual(counts["taranan"], 1)
        self.assertEqual(counts["degisen"], 1)
        self.assertEqual(counts["ozetlenen"], 1)
        self.assertEqual(counts["hatali"], 0)
        self.assertEqual(len(self.calls), 1)
        self.assertEqual(len(self._daily_blocks()), 1)
        entry = [e for e in self._ledger() if e.get("reason") == flush.REASON_OK]
        self.assertEqual(len(entry), 1)
        self.assertEqual(entry[0]["session_id"], SESSION)

    def test_an_unchanged_transcript_is_never_opened_again(self) -> None:
        """'Değişiklik yoksa çalışmasın' — dosya damgası katmanı."""
        self._write_turns(4)
        self._sweep()

        counts = self._sweep()

        self.assertEqual(counts["taranan"], 1)
        self.assertEqual(counts["degisen"], 0)
        self.assertEqual(counts["atlanan"], 1)
        self.assertEqual(len(self.calls), 1, "ikinci süpürge model çağırmamalı")
        self.assertEqual(len(self._daily_blocks()), 1)

    def test_a_changed_file_with_no_new_turns_calls_no_model(self) -> None:
        """'Değişiklik yoksa çalışmasın' — tur imleci katmanı."""
        self._write_turns(4)
        self._sweep()
        self._append_noise()

        counts = self._sweep()

        self.assertEqual(counts["degisen"], 1, "dosya damgası değişti")
        self.assertEqual(counts["ozetlenen"], 0)
        self.assertEqual(counts["atlanan"], 1)
        self.assertEqual(len(self.calls), 1, "yeni tur yok, model çağrısı yok")
        self.assertEqual(
            self._ledger()[-2]["reason"], flush.REASON_NO_NEW_TURNS
        )
        self.assertEqual(len(self._daily_blocks()), 1)

    def test_new_turns_after_a_sweep_produce_a_second_block(self) -> None:
        self._write_turns(4)
        self._sweep()
        self._write_turns(3, offset=4)

        counts = self._sweep()

        self.assertEqual(counts["ozetlenen"], 1)
        self.assertEqual(len(self._daily_blocks()), 2)
        for index in range(4):
            self.assertNotIn(f"tur {index}", self.calls[1])
        self.assertIn("tur 6", self.calls[1])

    def test_a_locked_session_is_skipped_not_queued(self) -> None:
        self._write_turns(4)

        def refuse(_handle, blocking: bool) -> None:
            if not blocking:
                raise BlockingIOError("canlı kanca flush'ı sürüyor")

        counts = self._sweep(lock_stub=refuse)

        self.assertEqual(counts["degisen"], 1)
        self.assertEqual(counts["atlanan"], 1)
        self.assertEqual(counts["ozetlenen"], 0)
        self.assertEqual(self.calls, [])
        self.assertEqual(self._ledger()[0]["reason"], flush.REASON_LOCKED)
        # Kilitli oturumun damgası yazılmaz: bir sonraki süpürge yeniden dener.
        self.assertEqual(self._sweep_state()["transkriptler"], {})

    def test_a_known_transcript_older_than_the_window_is_not_opened(self) -> None:
        """Yaş penceresi yalnız damgası bilinen transkriptler için geçerli."""
        self._write_turns(4)
        self._sweep()  # damga yazılır
        self._age(hours=10)

        counts = self._sweep(since_hours=8.0)

        self.assertEqual(counts["taranan"], 1)
        self.assertEqual(counts["degisen"], 0)
        self.assertEqual(counts["atlanan"], 1)
        self.assertEqual(len(self.calls), 1, "ikinci süpürge model çağırmamalı")
        self.assertEqual(len(self._daily_blocks()), 1)

    def test_a_never_seen_transcript_ignores_the_age_window(self) -> None:
        """Geç kalan süpürge, hiç görülmemiş oturumu düşürmez (2026-09-08)."""
        self._write_turns(4)
        self._age(hours=30)

        counts = self._sweep(since_hours=8.0)

        self.assertEqual(counts["taranan"], 1)
        self.assertEqual(counts["degisen"], 1)
        self.assertEqual(counts["ozetlenen"], 1)
        self.assertEqual(counts["atlanan"], 0)
        self.assertEqual(len(self.calls), 1)
        self.assertEqual(len(self._daily_blocks()), 1)

    def test_a_never_seen_but_flushed_transcript_is_not_summarised_twice(
        self,
    ) -> None:
        """Yaş kapısı kalktı; ikinci özeti hâlâ tur imleci engelliyor."""
        self._write_turns(4)
        self._age(hours=30)
        self._sweep(since_hours=8.0)

        # Damga yazıldı ama dosyaya dokunulmadı: ikinci süpürge onu açmaz.
        counts = self._sweep(since_hours=0)

        self.assertEqual(counts["ozetlenen"], 0)
        self.assertEqual(counts["atlanan"], 1)
        self.assertEqual(len(self.calls), 1)
        self.assertEqual(len(self._daily_blocks()), 1)

    def test_zero_since_hours_lifts_the_time_bound(self) -> None:
        self._write_turns(4)
        self._age(hours=200)

        counts = self._sweep(since_hours=0)

        self.assertEqual(counts["ozetlenen"], 1)

    def test_the_watermark_and_summary_line_are_persisted(self) -> None:
        self._write_turns(4)

        counts = self._sweep()

        state = self._sweep_state()
        self.assertEqual(
            state["son_tarama_ts"], MOMENT.isoformat(timespec="seconds")
        )
        stamp = state["transkriptler"][str(self.transcript)]
        self.assertEqual(stamp["size"], self.transcript.stat().st_size)
        self.assertAlmostEqual(
            stamp["mtime"], self.transcript.stat().st_mtime, places=3
        )
        summary = self._ledger()[-1]
        self.assertEqual(summary["reason"], flush.SWEEP_REASON)
        self.assertEqual(summary["taranan"], counts["taranan"])
        self.assertEqual(summary["degisen"], 1)
        self.assertEqual(summary["ozetlenen"], 1)
        self.assertEqual(summary["atlanan"], 0)
        self.assertEqual(summary["disarida"], 0)
        self.assertEqual(summary["hatali"], 0)

    def test_a_rejected_summary_is_retried_on_the_next_sweep(self) -> None:
        self._write_turns(4)

        counts = self._sweep(result=(None, "claude-timeout"))

        self.assertEqual(counts["hatali"], 1)
        self.assertEqual(self._sweep_state()["transkriptler"], {})

        second = self._sweep()
        self.assertEqual(second["ozetlenen"], 1)
        self.assertEqual(len(self._daily_blocks()), 1)

    def test_dry_run_writes_nothing_and_calls_no_model(self) -> None:
        self._write_turns(4)

        counts = self._sweep(dry_run=True)

        self.assertEqual(counts["degisen"], 1)
        self.assertEqual(counts["ozetlenen"], 1, "özetlenecekti")
        self.assertEqual(self.calls, [])
        self.assertEqual(self._daily_blocks(), [])
        self.assertEqual(self._ledger(), [])
        self.assertFalse((self.state_dir / flush.SWEEP_STATE_NAME).exists())
        self.assertFalse(list(self.state_dir.glob("flush-*.json")))

    def test_a_corrupt_transcript_is_counted_not_fatal(self) -> None:
        self.transcript.write_text("{bozuk\n", encoding="utf-8")
        healthy = self.projects / f"{SESSION[:-1]}9.jsonl"
        healthy.write_text(
            json.dumps({"message": {"role": "user", "content": "merhaba"}})
            + "\n",
            encoding="utf-8",
        )

        counts = self._sweep()

        self.assertEqual(counts["taranan"], 2)
        self.assertEqual(counts["hatali"], 1)
        self.assertEqual(counts["ozetlenen"], 1)

    def test_subagent_and_compile_stage_transcripts_stay_outside(self) -> None:
        """`projects` altındaki her .jsonl bir oturum değildir (2026-09-08)."""
        subagents = self.projects / SESSION / "subagents"
        subagents.mkdir(parents=True)
        self._write_turns(4, path=subagents / "agent-ae1b1e29.jsonl")
        stage = self.projects.parent / "E--Proje--stage-compile-stage-9ppa1d16"
        stage.mkdir()
        self._write_turns(4, path=stage / f"{SESSION[:-1]}9.jsonl")

        counts = self._sweep()

        self.assertEqual(counts["disarida"], 2)
        self.assertEqual(counts["taranan"], 0)
        self.assertEqual(counts["degisen"], 0)
        self.assertEqual(self.calls, [], "alt ajan/derleyici modele gitmez")
        self.assertEqual(self._daily_blocks(), [])
        self.assertEqual(self._sweep_state()["transkriptler"], {})
        self.assertEqual(self._ledger()[-1]["disarida"], 2)

    def test_a_real_session_beside_the_excluded_ones_is_still_flushed(
        self,
    ) -> None:
        self._write_turns(4)
        subagents = self.projects / SESSION / "subagents"
        subagents.mkdir(parents=True)
        self._write_turns(4, path=subagents / "agent-ae1b1e29.jsonl")

        counts = self._sweep()

        self.assertEqual(counts["disarida"], 1)
        self.assertEqual(counts["taranan"], 1)
        self.assertEqual(counts["ozetlenen"], 1)
        self.assertEqual(len(self._daily_blocks()), 1)

    def test_the_entry_is_dated_by_the_sessions_last_turn(self) -> None:
        """Süpürge saati değil, oturumun kendi saati (2026-09-08)."""
        session_moment = dt.datetime(2026, 9, 6, 12, 25, tzinfo=dt.timezone.utc)
        self._write_turns(2, when=session_moment - dt.timedelta(hours=2))
        self._write_turns(2, offset=2, when=session_moment)
        self._age(hours=35)
        local = session_moment.astimezone()

        counts = self._sweep(since_hours=8.0)

        self.assertEqual(counts["ozetlenen"], 1)
        self.assertEqual(self._daily_blocks(MOMENT), [], "süpürge günü değil")
        blocks = self._daily_blocks(session_moment)
        self.assertEqual(len(blocks), 1)
        self.assertTrue(blocks[0].startswith(f"({local.strftime('%H:%M')})"))
        daily = self._daily_path(session_moment).read_text(encoding="utf-8")
        self.assertIn(flush.session_anchor(SESSION, local), daily)
        self.assertIn(f"ts:{local.isoformat(timespec='seconds')}", daily)

    def test_a_transcript_without_timestamps_falls_back_to_the_file_stamp(
        self,
    ) -> None:
        self.transcript.write_text(
            json.dumps({"message": {"role": "user", "content": "merhaba"}})
            + "\n",
            encoding="utf-8",
        )
        self._age(hours=30)
        expected = (MOMENT - dt.timedelta(hours=30)).astimezone()

        counts = self._sweep(since_hours=8.0)

        self.assertEqual(counts["ozetlenen"], 1)
        blocks = self._daily_blocks(expected)
        self.assertEqual(len(blocks), 1)
        self.assertTrue(blocks[0].startswith(f"({expected.strftime('%H:%M')})"))

    def test_an_empty_projects_directory_is_a_no_op(self) -> None:
        counts = self._sweep()

        self.assertEqual(counts["taranan"], 0)
        self.assertEqual(self.calls, [])
        self.assertEqual(self._sweep_state()["transkriptler"], {})


class HookPathDateTests(SweepHarness):
    """Kanca yolu değişmedi: girdi kancanın kendi saatiyle tarihlenir."""

    def test_the_hook_still_dates_the_entry_with_the_event_time(self) -> None:
        session_moment = dt.datetime(2026, 9, 6, 12, 25, tzinfo=dt.timezone.utc)
        self._write_turns(4, when=session_moment)
        payload = {
            "session_id": SESSION,
            "transcript_path": str(self.transcript),
            "reason": "sessionend",
        }

        patches = [
            mock.patch.object(flush, "STATE_DIR", self.state_dir),
            mock.patch.object(flush, "VAULT_ROOT", self.root),
            mock.patch.dict(flush.os.environ, {}, clear=True),
            mock.patch.object(flush, "_run_claude", return_value=(GOOD_SUMMARY, None)),
            mock.patch.object(flush, "maybe_trigger_compile", return_value=False),
        ]
        with _nested(patches):
            flush._flush_once(
                flush.argparse.Namespace(hook_input=None, reason="sessionend"),
                MOMENT,
                hook_input=payload,
            )

        # Kanca yolunda tarih de saat de kancanın kendi damgasından gelir —
        # yerel saate çevrilmez, süpürgedeki gibi transkriptten okunmaz.
        daily = (
            self.root / "daily" / f"{MOMENT.strftime('%Y-%m-%d')}.md"
        ).read_text(encoding="utf-8")
        blocks = daily.split("### Oturum ")[1:]
        self.assertEqual(len(blocks), 1)
        self.assertTrue(blocks[0].startswith(f"({MOMENT.strftime('%H:%M')})"))
        self.assertIn(flush.session_anchor(SESSION, MOMENT), daily)


class ProjectsDirTests(unittest.TestCase):
    def test_flag_beats_env_beats_default(self) -> None:
        self.assertEqual(
            flush.resolve_projects_dir(Path("E:/a"), {flush.PROJECTS_DIR_ENV: "E:/b"}),
            Path("E:/a"),
        )
        self.assertEqual(
            flush.resolve_projects_dir(None, {flush.PROJECTS_DIR_ENV: "E:/b"}),
            Path("E:/b"),
        )
        self.assertEqual(
            flush.resolve_projects_dir(None, {}),
            Path.home() / ".claude" / "projects",
        )


class ArgumentTests(unittest.TestCase):
    def test_tara_needs_no_hook_input(self) -> None:
        args = flush._parse_args(["--tara", "--since-hours", "3", "--dry-run"])

        self.assertTrue(args.tara)
        self.assertTrue(args.dry_run)
        self.assertEqual(args.since_hours, 3.0)
        self.assertIsNone(args.hook_input)

    def test_hook_input_and_tara_are_mutually_exclusive(self) -> None:
        for argv in ([], ["--tara", "--hook-input", "x.json"]):
            with self.subTest(argv=argv):
                with self.assertRaises(SystemExit):
                    flush._parse_args(argv)

    def test_hook_path_still_parses(self) -> None:
        args = flush._parse_args(
            ["--hook-input", "x.json", "--reason", "precompact"]
        )

        self.assertFalse(args.tara)
        self.assertEqual(args.reason, "precompact")


class CompileWindowTests(unittest.TestCase):
    """Master 2026-09-07: ``>=18:00`` YA DA son başarıdan bu yana ``>=20 saat``."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state_dir = self.root / ".claude" / "scripts" / ".state"
        self.state_dir.mkdir(parents=True)
        (self.root / "daily").mkdir()
        (self.root / "daily" / "2026-09-07.md").write_text(
            "# Günlük Log\n\nİçerik.\n", encoding="utf-8"
        )
        self.launched: list[list[str]] = []

    def _launcher(self, command, **_kwargs):
        self.launched.append(list(command))
        return mock.Mock()

    def _state(self, status: str, hours_ago: float, when: dt.datetime) -> None:
        (self.state_dir / "compile-state.json").write_text(
            json.dumps(
                {
                    "ingested": {},
                    "last_run": (
                        when - dt.timedelta(hours=hours_ago)
                    ).isoformat(timespec="seconds"),
                    "last_status": status,
                }
            ),
            encoding="utf-8",
        )

    def _trigger(self, when: dt.datetime, environment=None) -> bool:
        with mock.patch.dict(flush.os.environ, environment or {}, clear=True):
            return flush.maybe_trigger_compile(
                self.root, when, popen_factory=self._launcher
            )

    def test_the_evening_door_still_opens(self) -> None:
        evening = dt.datetime(2026, 9, 7, 19, 0).astimezone()
        self._state("ok", hours_ago=25, when=evening)

        self.assertTrue(self._trigger(evening))

    def test_daytime_opens_after_twenty_hours(self) -> None:
        morning = dt.datetime(2026, 9, 7, 9, 0).astimezone()
        self._state("ok", hours_ago=21, when=morning)

        self.assertTrue(self._trigger(morning))
        self.assertEqual(len(self.launched), 1)

    def test_daytime_stays_shut_below_twenty_hours(self) -> None:
        morning = dt.datetime(2026, 9, 7, 9, 0).astimezone()
        self._state("ok", hours_ago=6, when=morning)

        self.assertFalse(self._trigger(morning))
        self.assertEqual(self.launched, [])

    def test_daytime_with_no_recorded_success_keeps_the_old_rule(self) -> None:
        """İlk kurulum sabahın dokuzunda derleme başlatmasın."""
        morning = dt.datetime(2026, 9, 7, 9, 0).astimezone()

        self.assertFalse(self._trigger(morning))

    def test_the_evening_hour_is_env_overridable(self) -> None:
        afternoon = dt.datetime(2026, 9, 7, 15, 0).astimezone()

        self.assertFalse(self._trigger(afternoon))
        self.assertTrue(
            self._trigger(
                afternoon, {flush.COMPILE_EVENING_HOUR_ENV: "15"}
            )
        )

    def test_evening_hour_setting_falls_back_on_junk(self) -> None:
        self.assertEqual(flush.resolve_compile_evening_hour({}), 18)
        self.assertEqual(
            flush.resolve_compile_evening_hour(
                {flush.COMPILE_EVENING_HOUR_ENV: "6"}
            ),
            6,
        )
        for junk in ("", "akşam", "-1", "24"):
            with self.subTest(junk=junk):
                self.assertEqual(
                    flush.resolve_compile_evening_hour(
                        {flush.COMPILE_EVENING_HOUR_ENV: junk}
                    ),
                    18,
                )


class SweepCompileTests(SweepHarness):
    def test_the_sweep_asks_for_a_compile_once(self) -> None:
        self._write_turns(4)

        with mock.patch.object(
            flush, "STATE_DIR", self.state_dir
        ), mock.patch.object(
            flush, "VAULT_ROOT", self.root
        ), mock.patch.dict(
            flush.os.environ, {}, clear=True
        ), mock.patch.object(
            flush, "_run_claude", return_value=(GOOD_SUMMARY, None)
        ), mock.patch.object(
            flush, "maybe_trigger_compile", return_value=False
        ) as trigger:
            flush.sweep(
                event_time=MOMENT,
                projects_dir=self.projects.parent,
                since_hours=8.0,
            )

        # Bir kez başarılı flush içinden, bir kez süpürgenin sonunda; gün
        # talebi (compile-trigger-YYYY-MM-DD) ikinciyi zaten yutar.
        self.assertGreaterEqual(trigger.call_count, 1)
        self.assertEqual(trigger.call_args.args[0], self.root)

    def test_a_dry_sweep_never_asks_for_a_compile(self) -> None:
        self._write_turns(4)

        with mock.patch.object(
            flush, "STATE_DIR", self.state_dir
        ), mock.patch.object(
            flush, "VAULT_ROOT", self.root
        ), mock.patch.dict(
            flush.os.environ, {}, clear=True
        ), mock.patch.object(
            flush, "_run_claude", return_value=(GOOD_SUMMARY, None)
        ), mock.patch.object(
            flush, "maybe_trigger_compile", return_value=False
        ) as trigger:
            flush.sweep(
                event_time=MOMENT,
                projects_dir=self.projects.parent,
                since_hours=8.0,
                dry_run=True,
            )

        trigger.assert_not_called()


if __name__ == "__main__":
    unittest.main()
