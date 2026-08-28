"""Omurga: özet sözleşmesi, sır bataryası, tarihsel ekleme, durum defteri."""

from __future__ import annotations

import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers
from _helpers import GOOD_SUMMARY, canned_stub, codex_message, codex_meta, echo_stub
from _helpers import write_jsonl

import flush
import ingest
import ingest_codex
import ingest_common
from ingest_common import Session


SECRET_BATTERY = (
    "AKIAIOSFODNN7EXAMPLE",
    "AIzaSyA1234567890abcdefghijklmnopqrstuv",
    "ghp_" + "a" * 36,
    "xoxb-1234567890-abcdefghij",
    "sk-ant-api03-" + "b" * 40,
    "sk-proj-" + "c" * 40,
    "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r",
    "postgres://kullanici:CokGizliParola1@konak:5432/db",
    "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123",
    "password: hunter2secret",
    "api_key=AbCdEfGhIjKlMnOpQrSt",
    "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----",
)

SECRET_NEEDLES = (
    "AKIAIOSFODNN7EXAMPLE",
    "AIzaSyA1234567890abcdefghijklmnopqrstuv",
    "ghp_" + "a" * 36,
    "xoxb-1234567890-abcdefghij",
    "sk-ant-api03-" + "b" * 40,
    "sk-proj-" + "c" * 40,
    "eyJhbGciOiJIUzI1NiJ9",
    "CokGizliParola1",
    "abcdefghijklmnopqrstuvwxyz0123",
    "hunter2secret",
    "AbCdEfGhIjKlMnOpQrSt",
    "MIIEowIBAAKCAQEA",
)


def _session(
    turns: list[tuple[str, str]],
    when: dt.datetime | None = None,
    source: str = "codex",
    model: str = "",
    label: str = "",
) -> Session:
    moment = when or dt.datetime(2026, 8, 20, 14, 5, tzinfo=dt.timezone.utc)
    return Session(
        source=source,
        key="key-1",
        when=moment.astimezone(),
        turns=turns,
        origin="C:\\fake\\rollout.jsonl",
        watermark="",
        model=model,
        label=label,
    )


class SummaryContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.state_dir = self.root / ".state"
        self.state_dir.mkdir(parents=True)
        self.addCleanup(self._temporary.cleanup)

    def test_validate_summary_round_trip(self) -> None:
        self.assertTrue(flush.validate_summary(GOOD_SUMMARY))
        four_headings = "\n".join(
            line
            for line in GOOD_SUMMARY.splitlines()
            if line != "## Yapılacaklar"
        )
        self.assertFalse(flush.validate_summary(four_headings))
        reordered = (
            "## Önemli Konuşmalar\na\n## Bağlam\nb\n## Alınan Kararlar\nc\n"
            "## Öğrenilenler\nd\n## Yapılacaklar\ne\n"
        )
        self.assertFalse(flush.validate_summary(reordered))
        preamble = "Şöyle özetledim:\n" + GOOD_SUMMARY
        self.assertFalse(flush.validate_summary(preamble))

    def test_short_session_is_bos_without_model_call(self) -> None:
        calls: list[str] = []

        def tripwire(prompt: str, vault_root: Path, timeout: int | None = None, **_kwargs):
            calls.append(prompt)
            return GOOD_SUMMARY, None

        session = _session([("user", "tek tur")])
        with mock.patch.object(flush, "_run_claude", tripwire):
            result = ingest_common.summarize_session(
                session,
                self.root,
                self.state_dir,
            )
        self.assertEqual(result.status, "bos")
        self.assertEqual(calls, [])

    def test_flush_bos_and_schema_failures(self) -> None:
        turns = [("user", f"tur {index}") for index in range(6)]
        session = _session(turns)
        with mock.patch.object(
            flush,
            "_run_claude",
            lambda prompt, root, timeout=None, **_kwargs: ("FLUSH_BOS", None),
        ):
            self.assertEqual(
                ingest_common.summarize_session(
                    session, self.root, self.state_dir
                ).status,
                "bos",
            )
        with mock.patch.object(
            flush,
            "_run_claude",
            lambda prompt, root, timeout=None, **_kwargs: (
                "## Bağlam\nsadece bir başlık",
                None,
            ),
        ):
            result = ingest_common.summarize_session(
                session, self.root, self.state_dir
            )
        self.assertEqual(result.status, "fail")
        self.assertEqual(result.detail, "summary-schema-invalid")
        with mock.patch.object(
            flush,
            "_run_claude",
            lambda prompt, root, timeout=None, **_kwargs: (None, "claude-timeout"),
        ):
            result = ingest_common.summarize_session(
                session, self.root, self.state_dir
            )
        self.assertEqual(result.status, "fail")
        self.assertEqual(result.detail, "claude-timeout")

    def test_secret_battery_never_reaches_daily(self) -> None:
        turns = [("user", "arşiv oturumu başlıyor")]
        for index, secret in enumerate(SECRET_BATTERY):
            turns.append(("user", f"vaka {index}: {secret}"))
            turns.append(("assistant", "anlaşıldı"))
        session = _session(turns, model="gpt-5.6-sol")
        with mock.patch.object(flush, "_run_claude", echo_stub):
            result = ingest_common.summarize_session(
                session,
                self.root,
                self.state_dir,
            )
        self.assertEqual(result.status, "ok")
        daily_name = ingest_common.append_historical(
            self.root,
            result.summary,
            session,
        )
        written = (self.root / "daily" / daily_name).read_text(encoding="utf-8")
        for needle in SECRET_NEEDLES:
            self.assertNotIn(needle, result.summary, msg=needle)
            self.assertNotIn(needle, written, msg=needle)
        self.assertIn("[SIR:", written)

    def test_secret_guard_runs_on_output_only_leak(self) -> None:
        """Model girişte olmayan bir sırrı uydurursa çıkışta yakalanır."""
        turns = [("user", f"tur {index}") for index in range(6)]
        leaked = "ghp_" + "d" * 36

        def leaking(prompt: str, vault_root: Path, timeout: int | None = None, **_kwargs):
            return GOOD_SUMMARY.replace("Karar yok.", f"Token {leaked}"), None

        with mock.patch.object(flush, "_run_claude", leaking):
            result = ingest_common.summarize_session(
                _session(turns),
                self.root,
                self.state_dir,
            )
        self.assertEqual(result.status, "ok")
        self.assertNotIn(leaked, result.summary)
        self.assertIn("[SIR:github-token]", result.summary)


class HistoricalAppendTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.addCleanup(self._temporary.cleanup)

    def test_backfill_note_only_on_created_file(self) -> None:
        session = _session([], model="gpt-5.6-sol")
        name = ingest_common.append_historical(self.root, GOOD_SUMMARY, session)
        text = (self.root / "daily" / name).read_text(encoding="utf-8")
        self.assertIn(ingest_common.BACKFILL_NOTE, text)
        self.assertEqual(text.count(ingest_common.BACKFILL_NOTE), 1)
        self.assertIn("· gpt-5.6-sol · özet: haiku", text)

        ingest_common.append_historical(self.root, GOOD_SUMMARY, session)
        text = (self.root / "daily" / name).read_text(encoding="utf-8")
        self.assertEqual(text.count(ingest_common.BACKFILL_NOTE), 1)
        self.assertEqual(text.count("### Oturum ("), 2)

    def test_existing_daily_gets_no_backfill_note(self) -> None:
        session = _session([], source="arşiv-claude")
        daily_dir = self.root / "daily"
        daily_dir.mkdir()
        name = f"{session.when.strftime('%Y-%m-%d')}.md"
        (daily_dir / name).write_text(
            f"# Günlük Log: {session.when.strftime('%Y-%m-%d')}\n\n## Oturumlar\n",
            encoding="utf-8",
        )
        ingest_common.append_historical(self.root, GOOD_SUMMARY, session)
        text = (daily_dir / name).read_text(encoding="utf-8")
        self.assertNotIn(ingest_common.BACKFILL_NOTE, text)
        self.assertIn(" — arşiv-claude · özet: haiku", text)

    def test_daily_suffix_shapes(self) -> None:
        self.assertEqual(
            ingest_common.daily_suffix(_session([], source="arşiv-claude")),
            " — arşiv-claude · özet: haiku",
        )
        self.assertEqual(
            ingest_common.daily_suffix(
                _session([], source="codex", model="gpt-5.6-sol")
            ),
            " — codex · gpt-5.6-sol · özet: haiku",
        )
        self.assertEqual(
            ingest_common.daily_suffix(
                _session([], source="web", label="web (güncellenmiş)"),
                summarizer="sonnet",
            ),
            " — web (güncellenmiş) · özet: sonnet",
        )


class LedgerTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.vault = self.root / "vault"
        self.state_dir = self.root / ".state"
        self.sessions_root = self.root / "sessions"
        self.state_dir.mkdir(parents=True)
        self.vault.mkdir(parents=True)
        self.addCleanup(self._temporary.cleanup)

    def _rollout(self, key: str, day: str, hour: str) -> Path:
        records = [codex_meta(key, timestamp=f"2026-08-{day}T0{hour}:00:00.000Z")]
        for index in range(8):
            records.append(
                codex_message(
                    "user" if index % 2 == 0 else "assistant",
                    f"{key} mesaj {index}",
                    f"2026-08-{day}T0{hour}:0{index}:00.000Z",
                )
            )
        return write_jsonl(
            self.sessions_root / "2026" / "08" / day
            / f"rollout-2026-08-{day}T0{hour}-00-00-{key}.jsonl",
            records,
        )

    def _run(self, argv: list[str]) -> int:
        args = ingest._parse_args(argv)
        with mock.patch.object(
            ingest_codex, "SESSIONS_ROOT", self.sessions_root
        ), mock.patch.object(flush, "_run_claude", canned_stub):
            return ingest.run_codex(args, self.state_dir, self.vault)

    def test_append_then_skip_is_idempotent(self) -> None:
        self._rollout("alpha", "20", "8")
        self._run(["codex", "--sleep", "0"])
        state = ingest_common.load_state(self.state_dir)
        entry = ingest_common.done_entry(state, "codex", "alpha")
        assert entry is not None
        self.assertEqual(entry["status"], "appended")
        daily_path = self.vault / "daily" / entry["daily"]
        first = daily_path.read_text(encoding="utf-8")

        self._run(["codex", "--sleep", "0"])
        self.assertEqual(daily_path.read_text(encoding="utf-8"), first)

    def test_interrupted_run_resumes(self) -> None:
        self._rollout("alpha", "20", "8")
        self._rollout("beta", "21", "9")
        self._run(["codex", "--sleep", "0", "--max-sessions", "1"])
        state = ingest_common.load_state(self.state_dir)
        done = state["sources"]["codex"]["done"]
        self.assertEqual(sorted(done), ["alpha"])

        self._run(["codex", "--sleep", "0", "--max-sessions", "1"])
        state = ingest_common.load_state(self.state_dir)
        self.assertEqual(sorted(state["sources"]["codex"]["done"]), ["alpha", "beta"])
        self.assertEqual(
            sorted(path.name for path in (self.vault / "daily").glob("*.md")),
            ["2026-08-20.md", "2026-08-21.md"],
        )

    def test_failure_is_retryable_only_with_flag(self) -> None:
        self._rollout("alpha", "20", "8")
        args = ingest._parse_args(["codex", "--sleep", "0"])
        with mock.patch.object(
            ingest_codex, "SESSIONS_ROOT", self.sessions_root
        ), mock.patch.object(
            flush, "_run_claude", lambda prompt, root, timeout=None, **_kwargs: (None, "claude-timeout")
        ):
            ingest.run_codex(args, self.state_dir, self.vault)
        state = ingest_common.load_state(self.state_dir)
        entry = ingest_common.done_entry(state, "codex", "alpha")
        assert entry is not None
        self.assertEqual(entry["status"], "fail:claude-timeout")
        self.assertTrue(ingest_common.should_skip(entry, retry_failed=False))
        self.assertFalse(ingest_common.should_skip(entry, retry_failed=True))

        self._run(["codex", "--sleep", "0", "--retry-failed"])
        state = ingest_common.load_state(self.state_dir)
        self.assertEqual(
            ingest_common.done_entry(state, "codex", "alpha")["status"],
            "appended",
        )

    def test_health_file_is_separate_from_flush_health(self) -> None:
        self._rollout("alpha", "20", "8")
        flush._atomic_write_json(
            self.state_dir / "health.json",
            {"component": "flush", "error": "dokunulmasın"},
        )
        self._run(["codex", "--sleep", "0"])
        flush_health = json.loads(
            (self.state_dir / "health.json").read_text(encoding="utf-8")
        )
        self.assertEqual(flush_health["error"], "dokunulmasın")
        ingest_health = json.loads(
            (self.state_dir / "ingest-health.json").read_text(encoding="utf-8")
        )
        self.assertEqual(ingest_health["component"], "ingest")
        self.assertEqual(ingest_health["counts"]["ingested"], 1)

    def test_dry_run_writes_nothing(self) -> None:
        self._rollout("alpha", "20", "8")
        args = ingest._parse_args(["codex", "--dry-run"])
        with mock.patch.object(
            ingest_codex, "SESSIONS_ROOT", self.sessions_root
        ), mock.patch.object(flush, "_run_claude", canned_stub), mock.patch(
            "builtins.print"
        ):
            ingest.run_codex(args, self.state_dir, self.vault)
        self.assertFalse(ingest_common.state_path(self.state_dir).exists())
        self.assertFalse((self.vault / "daily").exists())

    def test_status_on_empty_state(self) -> None:
        args = ingest._parse_args(["status"])
        printed: list[str] = []
        with mock.patch("builtins.print", lambda *parts: printed.append(" ".join(str(p) for p in parts))):
            code = ingest.run_status(args, self.state_dir, self.vault)
        self.assertEqual(code, 0)
        self.assertTrue(any("boş" in line for line in printed))
        self.assertFalse(ingest_common.state_path(self.state_dir).exists())

    def test_main_returns_zero_when_invoked_by_beyin(self) -> None:
        with mock.patch.dict(
            "os.environ", {"BEYIN_INVOKED_BY": "beyin-scripts"}
        ), mock.patch.object(ingest_common, "STATE_DIR", self.state_dir):
            self.assertEqual(ingest.main(["codex"]), 0)
        self.assertFalse(ingest_common.health_path(self.state_dir).exists())

    def test_main_never_raises_on_bad_arguments(self) -> None:
        with mock.patch.object(
            ingest_common, "STATE_DIR", self.state_dir
        ), mock.patch("sys.stderr"):
            self.assertEqual(ingest.main(["olmayan-komut"]), 0)
            self.assertEqual(ingest.main(["codex", "--max-sessions", "0"]), 0)
        health = json.loads(
            ingest_common.health_path(self.state_dir).read_text(encoding="utf-8")
        )
        self.assertEqual(health["error"], "invalid-limits")


class LocalTimeTests(unittest.TestCase):
    def test_utc_to_local_conversion(self) -> None:
        value = ingest_common.to_local("2026-08-24T21:30:00.000Z")
        assert value is not None
        self.assertIsNotNone(value.tzinfo)
        self.assertEqual(
            value.astimezone(dt.timezone.utc).isoformat(),
            "2026-08-24T21:30:00+00:00",
        )
        self.assertIsNone(ingest_common.to_local(""))
        self.assertIsNone(ingest_common.to_local(None))
        self.assertIsNone(ingest_common.to_local("bozuk-tarih"))


if __name__ == "__main__":
    unittest.main()
