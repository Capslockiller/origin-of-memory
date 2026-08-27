# yazan: codex
# model: gpt-5.6-sol
"""Backend-aware live flush transcript bounds and diagnostics."""

from __future__ import annotations

import argparse
import datetime as dt
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers
from _helpers import GOOD_SUMMARY

import flush


class FlushChunkResolutionTests(unittest.TestCase):
    def test_backend_defaults(self) -> None:
        for backend in (None, "claude", "antigravity"):
            environment = {}
            if backend is not None:
                environment["BEYIN_MODEL_BACKEND"] = backend
            with self.subTest(backend=backend):
                self.assertEqual(
                    flush.resolve_flush_chunk_chars(environment),
                    (flush.MAX_TRANSCRIPT_CHARS, None),
                )

        for backend in ("ollama", "openai-compat", "openai"):
            with self.subTest(backend=backend):
                self.assertEqual(
                    flush.resolve_flush_chunk_chars(
                        {"BEYIN_MODEL_BACKEND": backend}
                    ),
                    (24_000, None),
                )

    def test_valid_override_applies_to_every_backend(self) -> None:
        for backend in ("claude", "antigravity", "ollama", "openai-compat"):
            with self.subTest(backend=backend):
                self.assertEqual(
                    flush.resolve_flush_chunk_chars(
                        {
                            "BEYIN_MODEL_BACKEND": backend,
                            "BEYIN_FLUSH_CHUNK_CHARS": "12345",
                        }
                    ),
                    (12_345, None),
                )

    def test_invalid_override_is_ignored_and_warns(self) -> None:
        for raw in ("0", "-2", "oops", ""):
            with self.subTest(raw=raw):
                self.assertEqual(
                    flush.resolve_flush_chunk_chars(
                        {
                            "BEYIN_MODEL_BACKEND": "ollama",
                            "BEYIN_FLUSH_CHUNK_CHARS": raw,
                        }
                    ),
                    (
                        flush.LOCAL_MAX_TRANSCRIPT_CHARS,
                        f"warn:flush-chunk-invalid:{raw}",
                    ),
                )


class FlushChunkStateTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state_dir = self.root / ".state"
        self.state_dir.mkdir()
        self.transcript = self.root / "transcript.jsonl"
        self.transcript.write_text(
            json.dumps(
                {"message": {"role": "user", "content": "kalıcı karar"}}
            )
            + "\n",
            encoding="utf-8",
        )
        self.hook_input = self.root / "hook.json"
        self.hook_input.write_text(
            json.dumps(
                {
                    "session_id": "chunk-state",
                    "transcript_path": str(self.transcript),
                }
            ),
            encoding="utf-8",
        )

    def _run(self, environment: dict[str, str]) -> dict:
        args = argparse.Namespace(
            hook_input=self.hook_input,
            reason="sessionend",
        )
        moment = dt.datetime(2026, 8, 28, 12, tzinfo=dt.timezone.utc)
        with mock.patch.object(
            flush, "STATE_DIR", self.state_dir
        ), mock.patch.object(
            flush, "VAULT_ROOT", self.root
        ), mock.patch.dict(
            flush.os.environ, environment, clear=True
        ), mock.patch.object(
            flush, "_run_claude", return_value=(GOOD_SUMMARY, None)
        ), mock.patch.object(
            flush, "maybe_trigger_compile", return_value=False
        ):
            self.assertEqual(flush._flush_once(args, moment), 0)
        return json.loads(
            flush._session_state_path(
                self.state_dir, "chunk-state"
            ).read_text(encoding="utf-8")
        )

    def test_effective_bound_is_recorded_in_state_detail(self) -> None:
        state = self._run({"BEYIN_MODEL_BACKEND": "openai-compat"})
        self.assertEqual(state["status"], "ok")
        self.assertEqual(
            state["detail"],
            "appended;flush-chunk-chars:24000",
        )

    def test_invalid_override_warning_and_fallback_are_diagnosable(self) -> None:
        state = self._run(
            {
                "BEYIN_MODEL_BACKEND": "ollama",
                "BEYIN_FLUSH_CHUNK_CHARS": "bad",
            }
        )
        self.assertEqual(
            state["detail"],
            "appended;flush-chunk-chars:24000",
        )
        health = json.loads(
            (self.state_dir / "health.json").read_text(encoding="utf-8")
        )
        self.assertIn("warn:flush-chunk-invalid:bad", health["warnings"])


if __name__ == "__main__":
    unittest.main()
