# yazan: claude · model: sonnet
"""F5 "Kokpit" part 1 — kule.py: job creation, claim/lane exclusivity, the
stale reaper, log streaming, timeout/cancel, the diff-approval pipeline,
archiving, durum.json, and the CLI marker line.

No real `claude`/`codex` binary is ever invoked: every worker-spawn test
patches `popen_factory` with a fake process (`_FakeProcess`/`_FakeStdout`)
that emits pre-scripted lines against a manually-advanced clock, exactly the
same seam `kaydet.py`'s own tests use for its `compile_runner`.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from typing import Any
from unittest import mock

import _helpers  # noqa: F401 — bridges scripts/ onto sys.path

import kule


# --------------------------------------------------------------------------
# Fakes
# --------------------------------------------------------------------------


class _Clock:
    def __init__(self, start: float = 0.0) -> None:
        self.value = start

    def __call__(self) -> float:
        return self.value

    def advance(self, delta: float) -> float:
        self.value += delta
        return self.value


class _FakeStdin:
    def __init__(self) -> None:
        self.written: list[str] = []
        self.closed = False

    def write(self, text: str) -> None:
        self.written.append(text)

    def close(self) -> None:
        self.closed = True


class _FakeStdout:
    """``lines``: a list of ``(text_or_None, delta, callback_or_None)``.

    Each ``readline()`` call advances the shared clock by ``delta``, runs
    ``callback()`` if given (used to simulate a job editing a watched file,
    or a test writing the ``.iptal`` marker, mid-stream), then returns
    ``text`` — or ``""`` (EOF) once the list or a ``None`` entry is hit.
    """

    def __init__(self, clock: _Clock, lines: list[tuple[str | None, float, Any]]) -> None:
        self._clock = clock
        self._lines = list(lines)
        self._i = 0
        # Flipped by _FakeProcess.kill() — a real killed child's stdout
        # pipe closes almost immediately, so the reader thread must stop
        # producing scripted lines the instant the tree is killed, exactly
        # like a real process would.
        self.killed = False

    def readline(self) -> str:
        if self.killed:
            return ""
        if self._i >= len(self._lines):
            return ""
        # A tiny real sleep so a launched worker thread stays "in flight"
        # long enough for the main thread's own pass to finish evaluating
        # every queued job — a real child process takes real wall time
        # too, so this fake must not resolve instantly.
        import time as _time

        _time.sleep(0.03)
        text, delta, callback = self._lines[self._i]
        self._i += 1
        self._clock.advance(delta)
        if callback is not None:
            callback()
        if text is None:
            return ""
        return text if text.endswith("\n") else text + "\n"


class _FakeProcess:
    def __init__(self, argv: list[str], **kwargs: Any) -> None:
        self.argv = list(argv)
        self.kwargs = kwargs
        self.pid = 424242
        self.returncode: int | None = 0
        self.stdin = _FakeStdin() if kwargs.get("stdin") is not None else None
        self.stdout = _FakeStdout(_Clock(), [])
        self.killed = False
        self.wait_calls: list[Any] = []

    def wait(self, timeout: float | None = None) -> int | None:
        self.wait_calls.append(timeout)
        return self.returncode

    def kill(self) -> None:
        self.killed = True
        if self.stdout is not None:
            self.stdout.killed = True


def _factory(clock: _Clock, lines: list[tuple[str | None, float, Any]], *, returncode: int | None = 0,
             captured: list | None = None):
    def make(argv: list[str], **kwargs: Any) -> _FakeProcess:
        process = _FakeProcess(argv, **kwargs)
        process.stdout = _FakeStdout(clock, lines)
        process.returncode = returncode
        if captured is not None:
            captured.append(process)
        return process

    return make


class _BlockingStdout:
    """A stdout whose ``readline()`` never returns on its own — a child
    that produces literally zero output. Only killing the owning process
    (via the ``killed`` property below, which ``_FakeProcess.kill()``
    already sets) unblocks it — exactly like a real killed process's stdout
    pipe closing. Without a reader-thread design, a stream shaped like this
    would hang the whole per-job loop forever; that is precisely the defect
    this fixture exists to catch.
    """

    def __init__(self) -> None:
        self._event = threading.Event()

    def readline(self) -> str:
        self._event.wait()
        return ""

    @property
    def killed(self) -> bool:
        return self._event.is_set()

    @killed.setter
    def killed(self, value: bool) -> None:
        if value:
            self._event.set()


def _hanging_factory(captured: list | None = None):
    """A ``popen_factory`` whose spawned process's stdout blocks forever."""

    def make(argv: list[str], **kwargs: Any) -> _FakeProcess:
        process = _FakeProcess(argv, **kwargs)
        process.stdout = _BlockingStdout()
        process.returncode = None
        if captured is not None:
            captured.append(process)
        return process

    return make


def _dead_pid() -> int:
    """A pid guaranteed to belong to no running process."""
    process = subprocess.Popen([sys.executable, "-c", "pass"])
    process.wait(timeout=10)
    return process.pid


# --------------------------------------------------------------------------
# Base harness
# --------------------------------------------------------------------------


class KuleHarness(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(prefix="kule-test-")
        self.addCleanup(self._tmp.cleanup)
        # Drained BEFORE the tempdir is removed above (addCleanup runs
        # last-registered-first): a worker thread this test launched but
        # never explicitly joined must not still be writing into the
        # tempdir while shutil.rmtree is walking it.
        self._threads_before = set(threading.enumerate())
        self.addCleanup(self._drain_background_threads)
        self.root = Path(self._tmp.name)
        self.state_dir = self.root / ".state"
        self.cwd = self.root / "proje"
        self.cwd.mkdir(parents=True)
        self.kule = kule.Kule(self.state_dir)
        # pytest's own tmp_path (and this harness's own TemporaryDirectory,
        # both under the real system temp root) must count as a VALID job
        # cwd for these tests — only a test that explicitly wants the
        # under-temp refusal should see the real system temp root.
        patcher = mock.patch.object(
            kule, "_system_temp_root", return_value=self.root / "__not-temp__"
        )
        patcher.start()
        self.addCleanup(patcher.stop)

    def _drain_background_threads(self) -> None:
        for thread in threading.enumerate():
            if thread not in self._threads_before and thread is not threading.current_thread():
                thread.join(timeout=5)

    def make_job(self, **overrides: Any) -> dict[str, Any]:
        kwargs = dict(
            tur="claude",
            model="sonnet",
            prompt="Bir görev.",
            cwd=self.cwd,
        )
        kwargs.update(overrides)
        record, error = kule.create_job(self.kule, **kwargs)
        self.assertIsNone(error, error)
        return record


# --------------------------------------------------------------------------
# Job creation validation
# --------------------------------------------------------------------------


class JobCreationTests(KuleHarness):
    def test_missing_model_is_refused(self) -> None:
        record, error = kule.create_job(
            self.kule, tur="claude", model="", prompt="x", cwd=self.cwd
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.MODEL_EKSIK_SLUG)

    def test_whitespace_only_model_is_refused(self) -> None:
        record, error = kule.create_job(
            self.kule, tur="claude", model="   ", prompt="x", cwd=self.cwd
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.MODEL_EKSIK_SLUG)

    def test_unknown_tur_is_refused(self) -> None:
        record, error = kule.create_job(
            self.kule, tur="gemini", model="m", prompt="x", cwd=self.cwd
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.TUR_BILINMIYOR_SLUG)

    def test_cwd_under_system_temp_is_refused(self) -> None:
        with mock.patch.object(kule, "_system_temp_root", return_value=self.root):
            record, error = kule.create_job(
                self.kule, tur="claude", model="sonnet", prompt="x", cwd=self.cwd
            )
        self.assertIsNone(record)
        self.assertEqual(error, kule.CWD_GECERSIZ_SLUG)

    def test_cwd_on_another_drive_than_temp_is_valid(self) -> None:
        # On Windows os.path.commonpath raises ValueError for paths on
        # different drives. That means "cannot be under temp" — the cwd is
        # VALID. The old blanket `except ValueError: return True` rejected
        # every job whose cwd sat on another drive than %TEMP% (caught live
        # in the 2026-09-02 Windows smoke run, temp C: vs vault E:).
        with mock.patch.object(
            kule, "_system_temp_root", return_value=self.root / "__not-temp__"
        ), mock.patch(
            "os.path.commonpath", side_effect=ValueError("different drives")
        ):
            record, error = kule.create_job(
                self.kule, tur="claude", model="sonnet", prompt="x", cwd=self.cwd
            )
        self.assertIsNone(error)
        self.assertEqual(record["durum"], "queued")

    def test_cwd_that_does_not_exist_is_refused(self) -> None:
        record, error = kule.create_job(
            self.kule, tur="claude", model="sonnet", prompt="x", cwd=self.cwd / "yok"
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.CWD_GECERSIZ_SLUG)

    def test_valid_job_is_queued_with_full_shape(self) -> None:
        record = self.make_job()
        self.assertEqual(record["durum"], "queued")
        self.assertEqual(record["tur"], "claude")
        self.assertEqual(record["model"], "sonnet")
        self.assertTrue(record["nezaket_del"])
        self.assertEqual(record["kaynak"], "cli")
        self.assertIn("prompt_sha256", record)
        self.assertEqual(record["prompt_karakter"], len("Bir görev."))
        self.assertTrue(self.kule.job_path(record["id"]).is_file())

    def test_prompt_is_redacted_before_it_is_stored(self) -> None:
        secret_prompt = "anahtar: AKIAABCDEFGHIJKLMNOP burada"
        record = self.make_job(prompt=secret_prompt)
        stored = self.kule.prompt_path(record["id"]).read_text(encoding="utf-8")
        self.assertNotIn("AKIAABCDEFGHIJKLMNOP", stored)
        self.assertTrue(any("secret-redacted-prompt" in w for w in record["uyarilar"]))

    def test_no_secret_means_no_warning(self) -> None:
        record = self.make_job(prompt="sıradan bir görev")
        self.assertEqual(record["uyarilar"], [])

    def test_kaynak_panel_is_recorded(self) -> None:
        record = self.make_job(kaynak="panel")
        self.assertEqual(record["kaynak"], "panel")


# --------------------------------------------------------------------------
# izlenen_dosyalar validation (independent-review issue #2)
# --------------------------------------------------------------------------


class IzlenenValidationTests(KuleHarness):
    def test_relative_path_outside_cwd_is_refused(self) -> None:
        outside = self.root / "disari.txt"
        outside.write_text("x", encoding="utf-8")
        record, error = kule.create_job(
            self.kule,
            tur="claude",
            model="m",
            prompt="x",
            cwd=self.cwd,
            izlenen_dosyalar=["../disari.txt"],
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.IZLENEN_CWD_DISI_SLUG)

    def test_absolute_path_outside_cwd_is_refused(self) -> None:
        outside = self.root / "disari.txt"
        outside.write_text("x", encoding="utf-8")
        record, error = kule.create_job(
            self.kule,
            tur="claude",
            model="m",
            prompt="x",
            cwd=self.cwd,
            izlenen_dosyalar=[str(outside)],
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.IZLENEN_CWD_DISI_SLUG)

    def test_symlink_escape_out_of_cwd_is_refused(self) -> None:
        outside_dir = self.root / "gizli"
        outside_dir.mkdir()
        (outside_dir / "sir.txt").write_text("gizli içerik", encoding="utf-8")
        link = self.cwd / "baglanti"
        try:
            link.symlink_to(outside_dir)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks not supported on this filesystem")
        record, error = kule.create_job(
            self.kule,
            tur="claude",
            model="m",
            prompt="x",
            cwd=self.cwd,
            izlenen_dosyalar=["baglanti/sir.txt"],
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.IZLENEN_CWD_DISI_SLUG)

    def test_more_than_fifty_watched_files_is_refused(self) -> None:
        names = [f"f{i}.txt" for i in range(kule.MAX_IZLENEN_DOSYA + 1)]
        record, error = kule.create_job(
            self.kule, tur="claude", model="m", prompt="x", cwd=self.cwd, izlenen_dosyalar=names
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.IZLENEN_COK_BUYUK_SLUG)

    def test_exactly_fifty_watched_files_is_accepted(self) -> None:
        names = [f"f{i}.txt" for i in range(kule.MAX_IZLENEN_DOSYA)]
        for name in names:
            (self.cwd / name).write_text("x", encoding="utf-8")
        record = self.make_job(izlenen_dosyalar=names)
        self.assertEqual(len(record["izlenen_dosyalar"]), kule.MAX_IZLENEN_DOSYA)

    def test_watched_file_over_the_size_cap_is_refused(self) -> None:
        big = self.cwd / "buyuk.txt"
        big.write_bytes(b"x" * (kule.MAX_IZLENEN_BOYUT + 1))
        record, error = kule.create_job(
            self.kule,
            tur="claude",
            model="m",
            prompt="x",
            cwd=self.cwd,
            izlenen_dosyalar=["buyuk.txt"],
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.IZLENEN_COK_BUYUK_SLUG)

    def test_watched_file_within_cwd_under_the_size_cap_is_accepted(self) -> None:
        ok = self.cwd / "notlar.md"
        ok.write_text("içerik", encoding="utf-8")
        record = self.make_job(izlenen_dosyalar=["notlar.md"])
        self.assertEqual(record["izlenen_dosyalar"], ["notlar.md"])

    def test_read_watched_refuses_a_path_outside_cwd_at_run_time_too(self) -> None:
        # Defense in depth for _read_watched itself (not just create_job):
        # a TOCTOU window between validation and the job actually running.
        outside = self.root / "disari.txt"
        outside.write_text("gizli", encoding="utf-8")
        text = kule._read_watched(str(self.cwd), "../disari.txt")
        self.assertEqual(text, "")


# --------------------------------------------------------------------------
# izinler allowlist (independent-review issue #5)
# --------------------------------------------------------------------------


class IzinValidationTests(KuleHarness):
    def test_each_valid_permission_mode_is_accepted(self) -> None:
        for mode in ("default", "acceptEdits", "plan"):
            record = self.make_job(izinler={"permission_mode": mode})
            self.assertEqual(record["izinler"]["permission_mode"], mode)

    def test_bypass_permissions_mode_is_explicitly_refused(self) -> None:
        record, error = kule.create_job(
            self.kule,
            tur="claude",
            model="m",
            prompt="x",
            cwd=self.cwd,
            izinler={"permission_mode": "bypassPermissions"},
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.IZIN_GECERSIZ_SLUG)

    def test_each_valid_sandbox_is_accepted(self) -> None:
        for mode in ("read-only", "workspace-write"):
            record = self.make_job(izinler={"sandbox": mode})
            self.assertEqual(record["izinler"]["sandbox"], mode)

    def test_danger_full_access_sandbox_is_explicitly_refused(self) -> None:
        record, error = kule.create_job(
            self.kule,
            tur="claude",
            model="m",
            prompt="x",
            cwd=self.cwd,
            izinler={"sandbox": "danger-full-access"},
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.IZIN_GECERSIZ_SLUG)

    def test_well_formed_allowed_tools_is_accepted(self) -> None:
        record = self.make_job(izinler={"allowed_tools": "Read,Write,Bash(git *)"})
        self.assertEqual(record["izinler"]["allowed_tools"], "Read,Write,Bash(git *)")

    def test_allowed_tools_over_200_chars_is_refused(self) -> None:
        record, error = kule.create_job(
            self.kule,
            tur="claude",
            model="m",
            prompt="x",
            cwd=self.cwd,
            izinler={"allowed_tools": "A" * 201},
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.IZIN_GECERSIZ_SLUG)

    def test_allowed_tools_with_disallowed_characters_is_refused(self) -> None:
        record, error = kule.create_job(
            self.kule,
            tur="claude",
            model="m",
            prompt="x",
            cwd=self.cwd,
            izinler={"allowed_tools": "Read; rm -rf /"},
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.IZIN_GECERSIZ_SLUG)

    def test_unknown_izin_key_is_refused(self) -> None:
        record, error = kule.create_job(
            self.kule,
            tur="claude",
            model="m",
            prompt="x",
            cwd=self.cwd,
            izinler={"danger-full-access": True},
        )
        self.assertIsNone(record)
        self.assertEqual(error, kule.IZIN_GECERSIZ_SLUG)

    def test_empty_izin_is_accepted(self) -> None:
        record = self.make_job(izinler={})
        self.assertEqual(record["izinler"], {})


# --------------------------------------------------------------------------
# Claim exclusivity
# --------------------------------------------------------------------------


class ClaimExclusivityTests(KuleHarness):
    def test_two_threads_claiming_the_same_job_only_one_wins(self) -> None:
        record = self.make_job()
        results: list[bool] = []
        lock = threading.Lock()
        barrier = threading.Barrier(2)

        def attempt() -> None:
            barrier.wait()
            won = kule._claim(self.kule, record["id"])
            with lock:
                results.append(won)

        threads = [threading.Thread(target=attempt) for _ in range(2)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()

        self.assertEqual(sorted(results), [False, True])

    def test_release_then_reclaim_succeeds(self) -> None:
        record = self.make_job()
        self.assertTrue(kule._claim(self.kule, record["id"]))
        kule._release_claim(self.kule, record["id"])
        self.assertTrue(kule._claim(self.kule, record["id"]))


# --------------------------------------------------------------------------
# Lane caps
# --------------------------------------------------------------------------


class LaneCapTests(KuleHarness):
    def test_third_job_waits_when_cap_is_two(self) -> None:
        jobs = [self.make_job() for _ in range(3)]
        clock = _Clock()
        # Every fake job "finishes" quickly: two lines then EOF, returncode 0.
        factory = _factory(clock, [("line one", 0.1, None), ("line two", 0.1, None)])

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            with mock.patch.dict(os.environ, {"BEYIN_KULE_CLAUDE_TAVAN": "2"}):
                threads = kule._bir_gecis(self.kule, None, factory)
                self.assertEqual(len(threads), 2)
                for thread in threads:
                    thread.join()

        durum_by_id = {j["id"]: kule._read_job(self.kule, j["id"])["durum"] for j in jobs}
        finished = [d for d in durum_by_id.values() if d != "queued"]
        still_queued = [d for d in durum_by_id.values() if d == "queued"]
        self.assertEqual(len(finished), 2)
        self.assertEqual(len(still_queued), 1)

    def test_third_job_runs_once_a_lane_frees(self) -> None:
        jobs = [self.make_job() for _ in range(3)]
        clock = _Clock()
        factory = _factory(clock, [("line", 0.1, None)])

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            with mock.patch.dict(os.environ, {"BEYIN_KULE_CLAUDE_TAVAN": "2"}):
                threads = kule._bir_gecis(self.kule, None, factory)
                for thread in threads:
                    thread.join()
                # Second pass: the two lanes from the first pass are free again.
                threads2 = kule._bir_gecis(self.kule, None, factory)
                self.assertEqual(len(threads2), 1)
                for thread in threads2:
                    thread.join()

        for job in jobs:
            self.assertNotEqual(kule._read_job(self.kule, job["id"])["durum"], "queued")

    def test_default_caps_are_three_and_four(self) -> None:
        with mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("BEYIN_KULE_CLAUDE_TAVAN", None)
            os.environ.pop("BEYIN_KULE_CODEX_TAVAN", None)
            self.assertEqual(kule._lane_cap("claude"), 3)
            self.assertEqual(kule._lane_cap("codex"), 4)

    def test_junk_cap_env_falls_back_to_default(self) -> None:
        with mock.patch.dict(os.environ, {"BEYIN_KULE_CLAUDE_TAVAN": "not-a-number"}):
            self.assertEqual(kule._lane_cap("claude"), 3)


# --------------------------------------------------------------------------
# Stale reaper
# --------------------------------------------------------------------------


class ReaperTests(KuleHarness):
    def test_dead_claim_pid_marks_job_worker_kayip_and_frees_markers(self) -> None:
        record = self.make_job()
        job = kule._read_job(self.kule, record["id"])
        job["durum"] = "running"
        job["tur"] = "claude"
        kule._write_job(self.kule, job)
        self.assertTrue(kule._write_marker(self.kule.claim_path(record["id"]), _dead_pid()))
        self.assertTrue(
            kule._write_marker(self.kule.lane_path("claude", record["id"]), _dead_pid())
        )

        actions = kule.reap_stale(self.kule)

        self.assertEqual(len(actions), 1)
        self.assertEqual(actions[0]["id"], record["id"])
        updated = kule._read_job(self.kule, record["id"])
        self.assertEqual(updated["durum"], "failed")
        self.assertEqual(updated["hata"], kule.WORKER_KAYIP_SLUG)
        self.assertFalse(self.kule.claim_path(record["id"]).exists())
        self.assertFalse(self.kule.lane_path("claude", record["id"]).exists())

    def test_alive_claim_pid_is_left_alone(self) -> None:
        record = self.make_job()
        job = kule._read_job(self.kule, record["id"])
        job["durum"] = "running"
        kule._write_job(self.kule, job)
        kule._write_marker(self.kule.claim_path(record["id"]), os.getpid())

        actions = kule.reap_stale(self.kule)

        self.assertEqual(actions, [])
        self.assertEqual(kule._read_job(self.kule, record["id"])["durum"], "running")
        kule._release_claim(self.kule, record["id"])

    def test_stale_claim_for_an_already_terminal_job_is_just_cleaned_up(self) -> None:
        record = self.make_job()
        job = kule._read_job(self.kule, record["id"])
        job["durum"] = "succeeded"
        kule._write_job(self.kule, job)
        kule._write_marker(self.kule.claim_path(record["id"]), _dead_pid())

        actions = kule.reap_stale(self.kule)

        self.assertEqual(actions, [])
        self.assertEqual(kule._read_job(self.kule, record["id"])["durum"], "succeeded")
        self.assertFalse(self.kule.claim_path(record["id"]).exists())


# --------------------------------------------------------------------------
# Log streaming + batched son_olay
# --------------------------------------------------------------------------


class StreamingTests(KuleHarness):
    def test_log_lines_are_written_and_son_olay_batches_every_two_seconds(self) -> None:
        record = self.make_job()
        clock = _Clock()
        lines = [
            ("event one", 0.5, None),
            ("event two", 0.5, None),
            ("event three", 1.5, None),  # crosses the 2s batch boundary
            ("event four", 0.1, None),
        ]
        factory = _factory(clock, lines)

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)

        job = kule._read_job(self.kule, record["id"])
        self.assertEqual(job["durum"], "succeeded")
        self.assertEqual(job["olaylar"], 4)
        self.assertEqual(job["son_olay"], "event four")
        log_text = self.kule.log_path(record["id"]).read_text(encoding="utf-8")
        self.assertEqual(
            log_text.splitlines(), ["event one", "event two", "event three", "event four"]
        )

    def test_never_buffers_more_than_the_current_line_between_flushes(self) -> None:
        # Not a memory-measuring test (out of scope); this asserts the
        # observable contract instead: the log file grows incrementally as
        # lines arrive, it is not written once at the very end.
        record = self.make_job()
        clock = _Clock()
        seen_sizes: list[int] = []

        def track() -> None:
            seen_sizes.append(self.kule.log_path(record["id"]).stat().st_size)

        lines = [("a", 0.1, track), ("bb", 0.1, track), ("ccc", 0.1, track)]
        factory = _factory(clock, lines)
        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)
        # Sizes were captured as callbacks BEFORE each line's own write, so
        # they must be non-decreasing and strictly less than the final size.
        final_size = self.kule.log_path(record["id"]).stat().st_size
        self.assertEqual(seen_sizes, sorted(seen_sizes))
        self.assertLess(seen_sizes[0], final_size)


# --------------------------------------------------------------------------
# Log rotation at BEYIN_KULE_LOG_MAX_BAYT (independent-review issue #4)
# --------------------------------------------------------------------------


class LogRotationTests(KuleHarness):
    def test_log_rotates_once_past_the_cap_and_notes_the_job(self) -> None:
        record = self.make_job()
        clock = _Clock()
        lines = [(f"line-{i:03d}-abcdefgh", 0.02, None) for i in range(20)]
        factory = _factory(clock, lines)
        env = {"BEYIN_KULE_LOG_MAX_BAYT": "100"}

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], env, popen_factory=factory, now_fn=clock)

        job = kule._read_job(self.kule, record["id"])
        self.assertTrue(any(kule.LOG_KIRPILDI_SLUG in w for w in job["uyarilar"]))
        log_path = self.kule.log_path(record["id"])
        rotated_path = log_path.with_name(log_path.name + ".1")
        self.assertTrue(rotated_path.is_file())
        self.assertTrue(log_path.is_file())
        # Nothing was silently dropped — every line lands in one of the two
        # generations, just split once instead of growing unboundedly.
        combined = rotated_path.read_text(encoding="utf-8") + log_path.read_text(encoding="utf-8")
        for i in range(20):
            self.assertIn(f"line-{i:03d}", combined)

    def test_normal_sized_log_is_never_rotated(self) -> None:
        record = self.make_job()
        clock = _Clock()
        factory = _factory(clock, [("short line", 0.02, None)])

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)

        job = kule._read_job(self.kule, record["id"])
        self.assertEqual(job["uyarilar"], [])
        log_path = self.kule.log_path(record["id"])
        rotated_path = log_path.with_name(log_path.name + ".1")
        self.assertFalse(rotated_path.is_file())


# --------------------------------------------------------------------------
# Timeout
# --------------------------------------------------------------------------


class TimeoutTests(KuleHarness):
    def test_timeout_kills_the_tree_and_marks_failed(self) -> None:
        record = self.make_job()
        clock = _Clock()
        # Each line jumps the clock well past a 1-second timeout.
        lines = [("still going", 5.0, None) for _ in range(50)]
        factory = _factory(clock, lines)

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            with mock.patch.dict(os.environ, {"BEYIN_KULE_ZAMAN_ASIMI": "1"}):
                kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)

        job = kule._read_job(self.kule, record["id"])
        self.assertEqual(job["durum"], "failed")
        self.assertEqual(job["hata"], kule.ZAMAN_ASIMI_SLUG)

    def test_timeout_fires_even_when_the_child_produces_zero_output(self) -> None:
        """A hung child whose stdout never yields a single line must still
        be killed once BEYIN_KULE_ZAMAN_ASIMI elapses — the poll loop's
        tick (queue.get(timeout=...)) has to fire on its own, not only
        between log lines. Real wall-clock timing (no fake clock): the
        timeout and tick interval are both kept small so this stays fast.
        """
        record = self.make_job()
        captured: list[_FakeProcess] = []
        factory = _hanging_factory(captured)

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            with mock.patch.dict(os.environ, {"BEYIN_KULE_ZAMAN_ASIMI": "1"}):
                started = time.monotonic()
                kule._run_job(
                    self.kule,
                    record["id"],
                    None,
                    popen_factory=factory,
                    tick_aralik=0.05,
                )
                elapsed = time.monotonic() - started

        job = kule._read_job(self.kule, record["id"])
        self.assertEqual(job["durum"], "failed")
        self.assertEqual(job["hata"], kule.ZAMAN_ASIMI_SLUG)
        self.assertEqual(job["olaylar"], 0)
        self.assertTrue(captured[0].killed)
        # Bounded: it fired at ~1s, not "eventually" or "never".
        self.assertLess(elapsed, 3.0)


# --------------------------------------------------------------------------
# Cancellation
# --------------------------------------------------------------------------


class CancellationTests(KuleHarness):
    def test_iptal_mid_run_cancels_and_kills_the_tree(self) -> None:
        record = self.make_job()
        clock = _Clock()
        captured: list[_FakeProcess] = []

        def touch_iptal() -> None:
            kule.iptal(self.kule, record["id"])

        lines = [
            ("line one", 0.1, None),
            ("line two", 0.1, touch_iptal),
            ("line three", 0.1, None),
        ]
        factory = _factory(clock, lines, captured=captured)

        # The job must be "running" for kule.iptal() (called from inside
        # the callback) to touch the marker rather than short-circuit to a
        # bare "cancelled" (that branch is for a still-queued job).
        job = kule._read_job(self.kule, record["id"])
        job["durum"] = "running"
        kule._write_job(self.kule, job)

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)

        job = kule._read_job(self.kule, record["id"])
        self.assertEqual(job["durum"], "cancelled")
        self.assertTrue(captured[0].killed)
        # Only two of the three scripted lines were consumed before the
        # cancellation check broke the loop.
        self.assertEqual(job["olaylar"], 2)

    def test_iptal_on_a_queued_job_cancels_immediately_no_process_involved(self) -> None:
        record = self.make_job()
        job, error = kule.iptal(self.kule, record["id"])
        self.assertIsNone(error)
        self.assertEqual(job["durum"], "cancelled")

    def test_iptal_on_a_terminal_job_is_refused(self) -> None:
        record = self.make_job()
        job = kule._read_job(self.kule, record["id"])
        job["durum"] = "succeeded"
        kule._write_job(self.kule, job)
        result, error = kule.iptal(self.kule, record["id"])
        self.assertIsNone(result)
        self.assertEqual(error, kule.GECIS_GECERSIZ_SLUG)

    def test_iptal_cancels_even_when_the_child_produces_zero_output(self) -> None:
        """Same hung-child fixture as the timeout test, but here a second
        thread calls iptal() shortly after the job starts — the poll
        loop's own tick must notice the .iptal marker without waiting for
        any line, and well before the (much longer) job timeout."""
        record = self.make_job()
        captured: list[_FakeProcess] = []
        factory = _hanging_factory(captured)

        job = kule._read_job(self.kule, record["id"])
        job["durum"] = "running"
        kule._write_job(self.kule, job)

        def touch_after_delay() -> None:
            time.sleep(0.15)
            kule.iptal(self.kule, record["id"])

        canceller = threading.Thread(target=touch_after_delay, daemon=True)
        canceller.start()

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            started = time.monotonic()
            kule._run_job(
                self.kule,
                record["id"],
                None,
                popen_factory=factory,
                tick_aralik=0.05,
            )
            elapsed = time.monotonic() - started
        canceller.join(timeout=5)

        job = kule._read_job(self.kule, record["id"])
        self.assertEqual(job["durum"], "cancelled")
        self.assertEqual(job["olaylar"], 0)
        self.assertTrue(captured[0].killed)
        # Noticed on the next tick after the marker appeared (~0.15s), not
        # at the (default 3600s) job timeout.
        self.assertLess(elapsed, 3.0)


# --------------------------------------------------------------------------
# Diff pipeline
# --------------------------------------------------------------------------


class DiffPipelineTests(KuleHarness):
    def test_changed_watched_file_produces_a_diff_and_waiting_approval(self) -> None:
        watched = self.cwd / "notlar.md"
        watched.write_text("eski içerik\n", encoding="utf-8")
        record = self.make_job(izlenen_dosyalar=["notlar.md"])
        clock = _Clock()

        def edit_file() -> None:
            watched.write_text("yeni içerik\n", encoding="utf-8")

        lines = [("calisiyor", 0.1, edit_file), ("bitti", 0.1, None)]
        factory = _factory(clock, lines)

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)

        job = kule._read_job(self.kule, record["id"])
        self.assertEqual(job["durum"], "waiting-approval")
        self.assertEqual(len(job["diffler"]), 1)
        entry = job["diffler"][0]
        self.assertEqual(entry["dosya"], "notlar.md")
        self.assertGreaterEqual(entry["ekleme"], 1)
        self.assertGreaterEqual(entry["silme"], 1)
        # Stored relative to <state>/kule/, not absolute — see kule.py's
        # `_kule_relative`/`_resolve_kule_yol` (review issue #3).
        self.assertFalse(Path(entry["diff_yol"]).is_absolute())
        self.assertTrue(entry["diff_yol"].startswith("jobs/"))
        diff_text = (self.kule.kule_dir / entry["diff_yol"]).read_text(encoding="utf-8")
        self.assertIn("yeni içerik", diff_text)
        self.assertIn("eski içerik", diff_text)

    def test_unchanged_watched_file_succeeds_with_empty_diff(self) -> None:
        watched = self.cwd / "notlar.md"
        watched.write_text("aynı\n", encoding="utf-8")
        record = self.make_job(izlenen_dosyalar=["notlar.md"])
        clock = _Clock()
        lines = [("calisiyor", 0.1, None)]
        factory = _factory(clock, lines)

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)

        job = kule._read_job(self.kule, record["id"])
        self.assertEqual(job["durum"], "succeeded")
        self.assertEqual(job["diffler"][0]["ekleme"], 0)
        self.assertEqual(job["diffler"][0]["silme"], 0)

    def test_onayla_moves_waiting_approval_to_succeeded(self) -> None:
        watched = self.cwd / "notlar.md"
        watched.write_text("eski\n", encoding="utf-8")
        record = self.make_job(izlenen_dosyalar=["notlar.md"])
        clock = _Clock()

        def edit_file() -> None:
            watched.write_text("yeni\n", encoding="utf-8")

        factory = _factory(clock, [("x", 0.1, edit_file)])
        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)
        self.assertEqual(kule._read_job(self.kule, record["id"])["durum"], "waiting-approval")

        job, error = kule.onayla(self.kule, record["id"])
        self.assertIsNone(error)
        self.assertEqual(job["durum"], "succeeded")
        self.assertEqual(job["onay"]["karar"], "kabul")

    def test_reddet_moves_waiting_approval_to_rejected(self) -> None:
        watched = self.cwd / "notlar.md"
        watched.write_text("eski\n", encoding="utf-8")
        record = self.make_job(izlenen_dosyalar=["notlar.md"])
        clock = _Clock()

        def edit_file() -> None:
            watched.write_text("yeni\n", encoding="utf-8")

        factory = _factory(clock, [("x", 0.1, edit_file)])
        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)

        job, error = kule.reddet(self.kule, record["id"])
        self.assertIsNone(error)
        self.assertEqual(job["durum"], "rejected")
        self.assertEqual(job["onay"]["karar"], "red")

    def test_onayla_on_a_queued_job_is_an_illegal_transition(self) -> None:
        record = self.make_job()
        job, error = kule.onayla(self.kule, record["id"])
        self.assertIsNone(job)
        self.assertEqual(error, kule.GECIS_GECERSIZ_SLUG)

    def test_reddet_on_an_already_succeeded_job_is_illegal(self) -> None:
        record = self.make_job()
        job = kule._read_job(self.kule, record["id"])
        job["durum"] = "succeeded"
        kule._write_job(self.kule, job)
        result, error = kule.reddet(self.kule, record["id"])
        self.assertIsNone(result)
        self.assertEqual(error, kule.GECIS_GECERSIZ_SLUG)

    def test_onayla_on_an_unknown_id_is_refused(self) -> None:
        job, error = kule.onayla(self.kule, "yok-boyle-bir-id")
        self.assertIsNone(job)
        self.assertEqual(error, kule.IS_YOK_SLUG)


# --------------------------------------------------------------------------
# Archiving
# --------------------------------------------------------------------------


class ArchiveTests(KuleHarness):
    def test_finished_jobs_beyond_the_cap_are_moved_not_deleted_oldest_first(self) -> None:
        jobs = []
        for _ in range(5):
            record = self.make_job()
            job = kule._read_job(self.kule, record["id"])
            job["durum"] = "succeeded"
            kule._write_job(self.kule, job)
            jobs.append(record)

        archived = kule._maybe_archive(self.kule, tavan=2)

        self.assertEqual(len(archived), 3)
        expected_oldest = [j["id"] for j in jobs[:3]]
        self.assertEqual(sorted(archived), sorted(expected_oldest))
        for job_id in archived:
            self.assertFalse(self.kule.job_path(job_id).exists())
            self.assertFalse(self.kule.prompt_path(job_id).exists())
            self.assertTrue((self.kule.arsiv_dir / f"{job_id}.json").is_file())
            self.assertTrue((self.kule.arsiv_dir / f"{job_id}.prompt").is_file())
        for job_id in [j["id"] for j in jobs[3:]]:
            self.assertTrue(self.kule.job_path(job_id).is_file())

    def test_archive_never_loses_the_diff_directory(self) -> None:
        watched = self.cwd / "a.md"
        watched.write_text("eski\n", encoding="utf-8")
        record = self.make_job(izlenen_dosyalar=["a.md"])
        clock = _Clock()

        def edit_file() -> None:
            watched.write_text("yeni\n", encoding="utf-8")

        factory = _factory(clock, [("x", 0.1, edit_file)])
        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)
        kule.onayla(self.kule, record["id"])

        kule._maybe_archive(self.kule, tavan=0)

        self.assertFalse(self.kule.job_workdir(record["id"]).exists())
        archived_workdir = self.kule.arsiv_dir / record["id"]
        self.assertTrue((archived_workdir / "diff" / "0.diff").is_file())

    def test_active_jobs_below_the_cap_are_left_alone(self) -> None:
        record = self.make_job()
        job = kule._read_job(self.kule, record["id"])
        job["durum"] = "succeeded"
        kule._write_job(self.kule, job)

        archived = kule._maybe_archive(self.kule, tavan=50)

        self.assertEqual(archived, [])
        self.assertTrue(self.kule.job_path(record["id"]).is_file())

    def test_queued_and_running_jobs_are_never_archived_regardless_of_cap(self) -> None:
        record = self.make_job()
        archived = kule._maybe_archive(self.kule, tavan=0)
        self.assertEqual(archived, [])
        self.assertTrue(self.kule.job_path(record["id"]).is_file())


# --------------------------------------------------------------------------
# once/sonra/diff path resolution across archiving (independent-review
# issue #3)
# --------------------------------------------------------------------------


class DiffPathResolutionTests(KuleHarness):
    def _job_with_diff(self) -> dict[str, Any]:
        watched = self.cwd / "not.md"
        watched.write_text("eski\n", encoding="utf-8")
        record = self.make_job(izlenen_dosyalar=["not.md"])
        clock = _Clock()

        def edit_file() -> None:
            watched.write_text("yeni\n", encoding="utf-8")

        factory = _factory(clock, [("x", 0.1, edit_file)])
        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule._run_job(self.kule, record["id"], None, popen_factory=factory, now_fn=clock)
        kule.onayla(self.kule, record["id"])
        return record

    def test_diffler_paths_are_stored_relative_not_absolute(self) -> None:
        record = self._job_with_diff()
        job = kule._read_job(self.kule, record["id"])
        entry = job["diffler"][0]
        for key in ("once_yol", "sonra_yol", "diff_yol"):
            self.assertFalse(Path(entry[key]).is_absolute(), key)
            self.assertTrue(entry[key].startswith("jobs/"), key)

    def test_archived_job_diff_still_readable_via_cli(self) -> None:
        import contextlib
        import io

        record = self._job_with_diff()
        kule._maybe_archive(self.kule, tavan=0)
        self.assertFalse(self.kule.job_path(record["id"]).exists())
        self.assertTrue((self.kule.arsiv_dir / f"{record['id']}.json").is_file())

        argv = ["--state-dir", str(self.state_dir), "diff", record["id"], "0"]
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            exit_code = kule.main(argv)
        self.assertEqual(exit_code, 0)
        self.assertIn("yeni", buffer.getvalue())
        self.assertIn("eski", buffer.getvalue())

    def test_active_job_diff_readable_via_cli_too(self) -> None:
        import contextlib
        import io

        record = self._job_with_diff()
        argv = ["--state-dir", str(self.state_dir), "diff", record["id"], "0"]
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            exit_code = kule.main(argv)
        self.assertEqual(exit_code, 0)
        self.assertIn("yeni", buffer.getvalue())

    def test_tampered_absolute_diff_yol_outside_kule_dir_is_refused(self) -> None:
        import contextlib
        import io

        record = self._job_with_diff()
        secret = self.root / "sir.txt"
        secret.write_text("dısarıda", encoding="utf-8")
        job = kule._read_job(self.kule, record["id"])
        job["diffler"][0]["diff_yol"] = str(secret)
        kule._write_job(self.kule, job)

        argv = ["--state-dir", str(self.state_dir), "diff", record["id"], "0"]
        out = io.StringIO()
        err = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            exit_code = kule.main(argv)
        self.assertEqual(exit_code, 1)
        self.assertIn(kule.YOL_DISI_SLUG, err.getvalue())
        self.assertNotIn("dısarıda", out.getvalue())


# --------------------------------------------------------------------------
# durum.json
# --------------------------------------------------------------------------


class DurumJsonTests(KuleHarness):
    def test_durum_json_has_every_number_the_panel_needs(self) -> None:
        self.make_job()
        job2 = self.make_job()
        job = kule._read_job(self.kule, job2["id"])
        job["durum"] = "succeeded"
        kule._write_job(self.kule, job)

        payload = kule._write_durum(self.kule)

        self.assertIn("guncellendi", payload)
        self.assertEqual(payload["sayilar"]["queued"], 1)
        self.assertEqual(payload["sayilar"]["succeeded"], 1)
        self.assertIn("claude", payload["seritler"])
        self.assertIn("codex", payload["seritler"])
        self.assertEqual(payload["seritler"]["claude"]["tavan"], 3)
        self.assertEqual(payload["seritler"]["codex"]["tavan"], 4)
        self.assertEqual(len(payload["son_isler"]), 2)
        for entry in payload["son_isler"]:
            for key in ("id", "tur", "model", "durum", "sure_sn", "son_olay", "diff_var"):
                self.assertIn(key, entry)

    def test_durum_json_is_the_file_on_disk(self) -> None:
        self.make_job()
        kule._write_durum(self.kule)
        on_disk = json.loads(self.kule.durum_path.read_text(encoding="utf-8"))
        self.assertEqual(on_disk["sayilar"]["queued"], 1)

    def test_reaper_actions_appear_in_durum_json(self) -> None:
        record = self.make_job()
        job = kule._read_job(self.kule, record["id"])
        job["durum"] = "running"
        kule._write_job(self.kule, job)
        kule._write_marker(self.kule.claim_path(record["id"]), _dead_pid())

        actions = kule.reap_stale(self.kule)
        payload = kule._write_durum(self.kule, reaper_actions=actions)

        ids = [entry["id"] for entry in payload["reaper_eylemleri"]]
        self.assertIn(record["id"], ids)


# --------------------------------------------------------------------------
# --once / dur file
# --------------------------------------------------------------------------


class WorkerLoopTests(KuleHarness):
    def test_once_runs_a_single_pass_and_joins_before_returning(self) -> None:
        self.make_job()
        clock = _Clock()
        factory = _factory(clock, [("line", 0.1, None)])
        sleep_calls: list[float] = []

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule.calis(
                self.state_dir,
                once=True,
                popen_factory=factory,
                sleep_fn=lambda s: sleep_calls.append(s),
            )

        self.assertEqual(sleep_calls, [])  # --once never sleeps between passes
        payload = json.loads(self.kule.durum_path.read_text(encoding="utf-8"))
        self.assertEqual(payload["sayilar"]["succeeded"], 1)

    def test_dur_file_stops_the_continuous_loop(self) -> None:
        self.make_job()
        clock = _Clock()
        factory = _factory(clock, [("line", 0.1, None)])
        calls = {"n": 0}

        def fake_sleep(_seconds: float) -> None:
            calls["n"] += 1
            self.kule.dur_path.parent.mkdir(parents=True, exist_ok=True)
            self.kule.dur_path.touch()

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule.calis(self.state_dir, once=False, popen_factory=factory, sleep_fn=fake_sleep)

        # One pass ran (queued job claimed), one sleep call created the dur
        # file, and the loop returned instead of looping forever.
        self.assertEqual(calls["n"], 1)

    def test_should_stop_callback_also_stops_the_loop(self) -> None:
        self.make_job()
        clock = _Clock()
        factory = _factory(clock, [("line", 0.1, None)])
        state = {"stop": False}

        def fake_sleep(_seconds: float) -> None:
            state["stop"] = True

        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule.calis(
                self.state_dir,
                once=False,
                popen_factory=factory,
                sleep_fn=fake_sleep,
                should_stop=lambda: state["stop"],
            )
        # Got here without hanging: the should_stop callback was honoured.

    def test_pre_existing_dur_file_does_not_block_a_later_run(self) -> None:
        """Regression for independent-review issue #1: `Stop-Kule` writes
        `<state>/kule/dur` and nothing ever deleted it, so every later
        `calis()` call would see it, return immediately, and do nothing —
        forever. `calis()` now removes it once, at startup, before the loop
        is ever entered, since it is a stop signal for the running instance
        only. This run must both process a queued job AND reap an orphan,
        despite `dur` already existing when it starts."""
        orphan = self.make_job()
        job = kule._read_job(self.kule, orphan["id"])
        job["durum"] = "running"
        job["tur"] = "claude"
        kule._write_job(self.kule, job)
        self.assertTrue(kule._write_marker(self.kule.claim_path(orphan["id"]), _dead_pid()))
        self.assertTrue(
            kule._write_marker(self.kule.lane_path("claude", orphan["id"]), _dead_pid())
        )

        queued = self.make_job(prompt="ikinci görev")

        self.kule.dur_path.parent.mkdir(parents=True, exist_ok=True)
        self.kule.dur_path.touch()
        self.assertTrue(self.kule.dur_path.exists())

        clock = _Clock()
        factory = _factory(clock, [("line", 0.1, None)])
        with mock.patch.object(kule, "_resolve_executable", return_value="/bin/true"):
            kule.calis(self.state_dir, once=True, popen_factory=factory, sleep_fn=lambda s: None)

        reaped = kule._read_job(self.kule, orphan["id"])
        self.assertEqual(reaped["durum"], "failed")
        self.assertEqual(reaped["hata"], kule.WORKER_KAYIP_SLUG)
        processed = kule._read_job(self.kule, queued["id"])
        self.assertEqual(processed["durum"], "succeeded")


# --------------------------------------------------------------------------
# CLI marker line
# --------------------------------------------------------------------------


class CliTests(KuleHarness):
    def test_is_ver_prints_the_kule_sonuc_marker_on_json(self) -> None:
        prompt_file = self.root / "prompt.txt"
        prompt_file.write_text("bir görev", encoding="utf-8")
        argv = [
            "--state-dir",
            str(self.state_dir),
            "is-ver",
            "--tur",
            "claude",
            "--model",
            "sonnet",
            "--prompt-dosya",
            str(prompt_file),
            "--cwd",
            str(self.cwd),
            "--json",
        ]
        import io
        import contextlib

        with mock.patch.object(kule, "_system_temp_root", return_value=self.root / "__not-temp__"):
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                exit_code = kule.main(argv)
        self.assertEqual(exit_code, 0)
        output = buffer.getvalue().strip()
        self.assertTrue(output.startswith(kule.RESULT_MARKER))
        payload = json.loads(output[len(kule.RESULT_MARKER):])
        self.assertTrue(payload["olusturuldu"])
        self.assertEqual(payload["is"]["tur"], "claude")

    def test_is_ver_with_no_source_is_refused(self) -> None:
        import io
        import contextlib

        argv = [
            "--state-dir",
            str(self.state_dir),
            "is-ver",
            "--tur",
            "claude",
            "--model",
            "sonnet",
            "--cwd",
            str(self.cwd),
            "--json",
        ]
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            exit_code = kule.main(argv)
        self.assertEqual(exit_code, 1)
        self.assertIn(kule.PROMPT_EKSIK_SLUG, buffer.getvalue())

    def test_beyin_invoked_by_short_circuits_main(self) -> None:
        with mock.patch.dict(os.environ, {"BEYIN_INVOKED_BY": "beyin-scripts"}):
            self.assertEqual(kule.main(["--state-dir", str(self.state_dir), "durum"]), 0)
        self.assertFalse(self.state_dir.exists())


if __name__ == "__main__":
    unittest.main()
