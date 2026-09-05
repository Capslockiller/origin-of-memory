"""Bounded state maintenance and its dry-run-by-default safety contract.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import os
from pathlib import Path

import bakim


NOW = 1_800_000_000.0


def _age(path: Path, seconds: int) -> None:
    stamp = NOW - seconds
    os.utime(path, (stamp, stamp))


def _fixture(state: Path, hooks_state: Path) -> None:
    state.mkdir()
    hooks_state.mkdir()

    old_ledger = state / "retrieve-session-old.json"
    old_ledger.write_text("{}", encoding="utf-8")
    _age(old_ledger, bakim.RETRIEVE_TTL_SECONDS + 1)
    recent_ledger = state / "retrieve-session-recent.json"
    recent_ledger.write_text("{}", encoding="utf-8")
    _age(recent_ledger, bakim.RETRIEVE_TTL_SECONDS - 1)

    old_tmp = state / "tmp-orphan.tmp"
    old_tmp.write_text("partial", encoding="utf-8")
    _age(old_tmp, bakim.TMP_TTL_SECONDS + 1)

    old_empty_lock = state / "flush-old.lock"
    old_empty_lock.touch()
    _age(old_empty_lock, bakim.LOCK_TTL_SECONDS + 1)
    old_nonempty_lock = state / "compile.lock"
    old_nonempty_lock.write_text("{}", encoding="utf-8")
    _age(old_nonempty_lock, bakim.LOCK_TTL_SECONDS + 1)

    injection = state / "enjeksiyon.jsonl"
    injection.write_bytes(b"x" * bakim.ENJEKSIYON_MAX_BYTES)
    (state / "enjeksiyon.jsonl.1").write_text("onceki", encoding="utf-8")

    old_session = hooks_state / "oturum-old"
    old_session.mkdir()
    (old_session / "prompt_count").write_text("4", encoding="ascii")
    _age(old_session, bakim.SESSION_TTL_SECONDS + 1)
    recent_session = hooks_state / "oturum-recent"
    recent_session.mkdir()
    _age(recent_session, bakim.SESSION_TTL_SECONDS - 1)


def test_dry_run_counts_without_changing_anything(tmp_path: Path) -> None:
    state = tmp_path / "state"
    hooks_state = tmp_path / "hooks-state"
    _fixture(state, hooks_state)

    reports = bakim.run_maintenance(state, hooks_state, now=NOW)

    assert [report["eligible"] for report in reports] == [1, 1, 1, 1, 1]
    assert all(report["changed"] == 0 for report in reports)
    assert (state / "retrieve-session-old.json").exists()
    assert (state / "enjeksiyon.jsonl").exists()
    assert (hooks_state / "oturum-old").exists()


def test_uygula_prunes_only_expired_disposable_state(tmp_path: Path) -> None:
    state = tmp_path / "state"
    hooks_state = tmp_path / "hooks-state"
    _fixture(state, hooks_state)

    reports = bakim.run_maintenance(state, hooks_state, apply=True, now=NOW)

    assert [report["changed"] for report in reports] == [1, 1, 1, 1, 1]
    assert not (state / "retrieve-session-old.json").exists()
    assert (state / "retrieve-session-recent.json").exists()
    assert not (state / "tmp-orphan.tmp").exists()
    assert not (state / "flush-old.lock").exists()
    assert (state / "compile.lock").exists()
    assert not (state / "enjeksiyon.jsonl").exists()
    assert (state / "enjeksiyon.jsonl.1").stat().st_size == bakim.ENJEKSIYON_MAX_BYTES
    assert not (hooks_state / "oturum-old").exists()
    assert (hooks_state / "oturum-recent").exists()


def test_cli_defaults_to_dry_run_and_prints_a_table(
    tmp_path: Path, capsys
) -> None:
    state = tmp_path / "state"
    state.mkdir()
    ledger = state / "retrieve-session-old.json"
    ledger.write_text("{}", encoding="utf-8")
    _age(ledger, bakim.RETRIEVE_TTL_SECONDS + 1)

    original_time = bakim.time.time
    bakim.time.time = lambda: NOW
    try:
        assert bakim.main(["--state-dir", str(state)]) == 0
    finally:
        bakim.time.time = original_time

    assert ledger.exists()
    output = capsys.readouterr().out
    assert "retrieve-session ledgers" in output
    assert "would delete" in output


def test_recursion_guard_returns_without_reporting(monkeypatch, capsys) -> None:
    monkeypatch.setenv("BEYIN_INVOKED_BY", "compile")

    assert bakim.main([]) == 0
    assert capsys.readouterr().out == ""
