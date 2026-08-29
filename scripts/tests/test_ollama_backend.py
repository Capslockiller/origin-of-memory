# yazan: codex
# model: gpt-5.6-sol
"""Ollama backend dispatch, payload, model mapping, and error contract."""

from __future__ import annotations

import json
import os
from pathlib import Path
import socket
import tempfile
import unittest
from unittest import mock
import urllib.error

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import claude_runner
import ollama_runner


class _Response:
    def __init__(self, body: bytes):
        self.body = body

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def read(self) -> bytes:
        return self.body


class OllamaModelTests(unittest.TestCase):
    def test_fast_and_smart_mapping(self) -> None:
        environment = {
            "BEYIN_OLLAMA_MODEL_FAST": "tiny-fast",
            "BEYIN_OLLAMA_MODEL_SMART": "large-smart",
        }
        self.assertEqual(
            ollama_runner.resolve_model("haiku", environment),
            ("tiny-fast", None),
        )
        self.assertEqual(
            ollama_runner.resolve_model("sonnet", environment),
            ("large-smart", None),
        )

    def test_fast_unset_fails_loud(self) -> None:
        self.assertEqual(
            ollama_runner.resolve_model("haiku", {}),
            (None, "ollama-model-unset"),
        )

    def test_smart_unset_falls_back_to_fast_and_warns(self) -> None:
        self.assertEqual(
            ollama_runner.resolve_model(
                "sonnet", {"BEYIN_OLLAMA_MODEL_FAST": "tiny-fast"}
            ),
            ("tiny-fast", "warn:ollama-model-unmapped:sonnet"),
        )
        self.assertEqual(
            ollama_runner.resolve_model("sonnet", {}),
            (None, "ollama-model-unset"),
        )


class OllamaRequestTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.stage = Path(self._temporary.name)
        claude_runner.last_warnings()

    def _run(self, side_effect=None, body: bytes = b'{"response":"YANIT"}'):
        environment = {
            "BEYIN_MODEL_BACKEND": "ollama",
            "BEYIN_OLLAMA_MODEL_FAST": "local-fast",
            "BEYIN_OLLAMA_URL": "http://127.0.0.1:9999/",
        }
        replacement = side_effect if side_effect is not None else _Response(body)
        with mock.patch.dict(
            ollama_runner.os.environ, environment, clear=True
        ), mock.patch.object(
            ollama_runner.urllib.request,
            "urlopen",
            side_effect=replacement if isinstance(replacement, BaseException) else None,
            return_value=None if isinstance(replacement, BaseException) else replacement,
        ) as opened:
            result = claude_runner.run_claude(
                "Türkçe istem",
                model="haiku",
                tools="",
                timeout=42,
                cwd=self.stage,
            )
            marker_after = ollama_runner.os.environ.get("BEYIN_INVOKED_BY")
        return result, opened, marker_after

    def test_dispatch_posts_expected_url_payload_and_timeout(self) -> None:
        (output, error), opened, marker_after = self._run()

        self.assertEqual((output, error), ("YANIT", None))
        request = opened.call_args.args[0]
        self.assertEqual(request.full_url, "http://127.0.0.1:9999/api/generate")
        self.assertEqual(request.method, "POST")
        self.assertEqual(request.get_header("Content-type"), "application/json")
        self.assertEqual(
            json.loads(request.data.decode("utf-8")),
            {
                "model": "local-fast",
                "prompt": "Türkçe istem",
                "stream": False,
                # Thinking OFF by default: on thinking models the think
                # stream eats the budget and the answer truncates mid-block
                # (measured with qwen3:8b, 2026-08-29).
                "think": False,
                "options": {"num_predict": -1},
            },
        )
        self.assertEqual(opened.call_args.kwargs["timeout"], 42)
        self.assertIsNone(marker_after)

    def test_think_and_num_predict_env_overrides(self) -> None:
        with mock.patch.dict(
            os.environ,
            {"BEYIN_OLLAMA_THINK": "1", "BEYIN_OLLAMA_NUM_PREDICT": "2048"},
        ):
            extras = ollama_runner._request_extras()
        self.assertEqual(extras, {"think": True, "options": {"num_predict": 2048}})

    def test_num_predict_garbage_falls_back_to_unlimited(self) -> None:
        with mock.patch.dict(os.environ, {"BEYIN_OLLAMA_NUM_PREDICT": "bozuk"}):
            extras = ollama_runner._request_extras()
        self.assertEqual(extras["options"], {"num_predict": -1})

    def test_sonnet_fallback_warning_reaches_shared_sink(self) -> None:
        environment = {
            "BEYIN_MODEL_BACKEND": "ollama",
            "BEYIN_OLLAMA_MODEL_FAST": "local-fast",
        }
        with mock.patch.dict(
            ollama_runner.os.environ, environment, clear=True
        ), mock.patch.object(
            ollama_runner.urllib.request,
            "urlopen",
            return_value=_Response(b'{"response":"ok"}'),
        ):
            result = claude_runner.run_claude(
                "prompt", model="sonnet", tools="", timeout=1, cwd=self.stage
            )
        self.assertEqual(result, ("ok", None))
        self.assertEqual(
            claude_runner.last_warnings(),
            ["warn:ollama-model-unmapped:sonnet"],
        )

    def test_connection_and_url_errors_map_to_missing(self) -> None:
        for failure in (
            ConnectionRefusedError(),
            urllib.error.URLError(ConnectionRefusedError()),
            urllib.error.URLError("unreachable"),
        ):
            with self.subTest(failure=failure):
                result, _opened, _marker = self._run(side_effect=failure)
                self.assertEqual(result, (None, "ollama-missing"))

    def test_http_status_maps_to_status_error(self) -> None:
        failure = urllib.error.HTTPError(
            "http://localhost", 503, "unavailable", None, None
        )
        result, _opened, _marker = self._run(side_effect=failure)
        self.assertEqual(result, (None, "ollama-http-503"))

    def test_timeouts_map_to_timeout(self) -> None:
        for failure in (
            TimeoutError(),
            socket.timeout(),
            urllib.error.URLError(TimeoutError()),
        ):
            with self.subTest(failure=failure):
                result, _opened, _marker = self._run(side_effect=failure)
                self.assertEqual(result, (None, "ollama-timeout"))

    def test_invalid_json_and_schema_map_to_bad_response(self) -> None:
        for body in (b"not-json", b"{}", b'{"response":7}'):
            with self.subTest(body=body):
                result, _opened, _marker = self._run(body=body)
                self.assertEqual(result, (None, "ollama-bad-response"))


class OllamaCompileTests(unittest.TestCase):
    def test_tool_mode_refuses_without_calling_urlopen(self) -> None:
        with mock.patch.object(
            ollama_runner.urllib.request, "urlopen"
        ) as opened:
            result = claude_runner.run_claude(
                "compile",
                model="sonnet",
                tools="Read,Write",
                timeout=1,
                backend="ollama",
            )
        self.assertEqual(result, (None, "ollama-backend-unsupported:compile"))
        opened.assert_not_called()

    def test_compile_backend_falls_back_to_claude_when_present(self) -> None:
        with mock.patch.object(
            claude_runner.shutil, "which", return_value="/bin/claude"
        ):
            result = claude_runner.compile_backend(
                {"BEYIN_MODEL_BACKEND": "ollama"}
            )
        self.assertEqual(
            result, ("claude", "warn:ollama-compile-fallback-claude")
        )

    def test_compile_backend_preserves_refusing_backend_when_absent(self) -> None:
        with mock.patch.object(claude_runner.shutil, "which", return_value=None):
            result = claude_runner.compile_backend(
                {"BEYIN_MODEL_BACKEND": "ollama"}
            )
        self.assertEqual(result, ("ollama", None))


if __name__ == "__main__":
    unittest.main()
