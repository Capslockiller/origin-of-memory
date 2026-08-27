# yazan: codex
# model: gpt-5.6-sol
"""OpenAI-compatible dispatch, payload, model mapping, and error contract."""

from __future__ import annotations

import json
from pathlib import Path
import socket
import tempfile
import unittest
from unittest import mock
import urllib.error

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import claude_runner
import openai_runner


class _Response:
    def __init__(self, body: bytes):
        self.body = body

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def read(self) -> bytes:
        return self.body


class OpenAIModelTests(unittest.TestCase):
    def test_fast_and_smart_mapping(self) -> None:
        environment = {
            "BEYIN_OPENAI_MODEL_FAST": "tiny-fast",
            "BEYIN_OPENAI_MODEL_SMART": "large-smart",
        }
        self.assertEqual(
            openai_runner.resolve_model("haiku", environment),
            ("tiny-fast", None),
        )
        self.assertEqual(
            openai_runner.resolve_model("sonnet", environment),
            ("large-smart", None),
        )

    def test_unset_and_sonnet_fallback_contract(self) -> None:
        self.assertEqual(
            openai_runner.resolve_model("haiku", {}),
            (None, "openai-compat-model-unset"),
        )
        self.assertEqual(
            openai_runner.resolve_model(
                "sonnet", {"BEYIN_OPENAI_MODEL_FAST": "tiny-fast"}
            ),
            ("tiny-fast", "warn:openai-compat-model-unmapped:sonnet"),
        )


class OpenAIRequestTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.stage = Path(self._temporary.name)
        claude_runner.last_warnings()

    def _run(
        self,
        *,
        key: str | None = None,
        side_effect=None,
        body: bytes = b'{"choices":[{"message":{"content":" YANIT "}}]}',
        backend: str = "openai-compat",
    ):
        environment = {
            "BEYIN_MODEL_BACKEND": backend,
            "BEYIN_OPENAI_MODEL_FAST": "local-fast",
            "BEYIN_OPENAI_URL": "http://127.0.0.1:9999/v1/",
        }
        if key is not None:
            environment["BEYIN_OPENAI_KEY"] = key
        replacement = side_effect if side_effect is not None else _Response(body)
        with mock.patch.dict(
            openai_runner.os.environ, environment, clear=True
        ), mock.patch.object(
            openai_runner.urllib.request,
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
            marker_after = openai_runner.os.environ.get("BEYIN_INVOKED_BY")
        return result, opened, marker_after

    def test_payload_url_timeout_and_optional_bearer_header(self) -> None:
        (output, error), opened, marker_after = self._run(key="dummy-token")

        self.assertEqual((output, error), ("YANIT", None))
        request = opened.call_args.args[0]
        self.assertEqual(
            request.full_url,
            "http://127.0.0.1:9999/v1/chat/completions",
        )
        self.assertEqual(request.method, "POST")
        self.assertEqual(request.get_header("Content-type"), "application/json")
        self.assertEqual(request.get_header("Authorization"), "Bearer dummy-token")
        self.assertEqual(
            json.loads(request.data.decode("utf-8")),
            {
                "model": "local-fast",
                "messages": [{"role": "user", "content": "Türkçe istem"}],
                "stream": False,
            },
        )
        self.assertEqual(opened.call_args.kwargs["timeout"], 42)
        self.assertIsNone(marker_after)

    def test_bearer_header_is_absent_when_key_is_unset(self) -> None:
        _result, opened, _marker = self._run()
        request = opened.call_args.args[0]
        self.assertIsNone(request.get_header("Authorization"))

    def test_url_unset_fails_before_transport(self) -> None:
        environment = {"BEYIN_OPENAI_MODEL_FAST": "local-fast"}
        with mock.patch.dict(
            openai_runner.os.environ, environment, clear=True
        ), mock.patch.object(
            openai_runner.urllib.request, "urlopen"
        ) as opened:
            result = openai_runner.run_openai(
                "prompt", model="haiku", timeout=1
            )
        self.assertEqual(result, (None, "openai-compat-url-unset"))
        opened.assert_not_called()

    def test_alias_dispatches_and_warns(self) -> None:
        result, _opened, _marker = self._run(backend="openai")
        self.assertEqual(result, ("YANIT", None))
        self.assertEqual(
            claude_runner.last_warnings(),
            ["warn:backend-alias:openai"],
        )

    def test_connection_and_url_errors_map_to_missing(self) -> None:
        for failure in (
            ConnectionRefusedError(),
            urllib.error.URLError(ConnectionRefusedError()),
            urllib.error.URLError("unreachable"),
        ):
            with self.subTest(failure=failure):
                result, _opened, _marker = self._run(side_effect=failure)
                self.assertEqual(result, (None, "openai-compat-missing"))

    def test_http_status_maps_to_status_error(self) -> None:
        failure = urllib.error.HTTPError(
            "http://localhost", 503, "unavailable", None, None
        )
        result, _opened, _marker = self._run(side_effect=failure)
        self.assertEqual(result, (None, "openai-compat-http-503"))

    def test_timeouts_map_to_timeout(self) -> None:
        for failure in (
            TimeoutError(),
            socket.timeout(),
            urllib.error.URLError(TimeoutError()),
        ):
            with self.subTest(failure=failure):
                result, _opened, _marker = self._run(side_effect=failure)
                self.assertEqual(result, (None, "openai-compat-timeout"))

    def test_invalid_json_and_schema_map_to_bad_response(self) -> None:
        for body in (
            b"not-json",
            b"{}",
            b'{"choices":[]}',
            b'{"choices":[{"message":{"content":7}}]}',
        ):
            with self.subTest(body=body):
                result, _opened, _marker = self._run(body=body)
                self.assertEqual(result, (None, "openai-compat-bad-response"))


class OpenAICompileTests(unittest.TestCase):
    def test_tool_mode_refuses_without_calling_urlopen(self) -> None:
        with mock.patch.object(
            openai_runner.urllib.request, "urlopen"
        ) as opened:
            result = claude_runner.run_claude(
                "compile",
                model="sonnet",
                tools="Read,Write",
                timeout=1,
                backend="openai-compat",
            )
        self.assertEqual(
            result,
            (None, "openai-compat-backend-unsupported:compile"),
        )
        opened.assert_not_called()

    def test_compile_backend_falls_back_to_claude_when_present(self) -> None:
        with mock.patch.object(
            claude_runner.shutil, "which", return_value="/bin/claude"
        ):
            result = claude_runner.compile_backend(
                {"BEYIN_MODEL_BACKEND": "openai-compat"}
            )
        self.assertEqual(
            result,
            ("claude", "warn:openai-compat-compile-fallback-claude"),
        )

    def test_compile_backend_preserves_refusal_when_claude_absent(self) -> None:
        with mock.patch.object(claude_runner.shutil, "which", return_value=None):
            result = claude_runner.compile_backend(
                {"BEYIN_MODEL_BACKEND": "openai-compat"}
            )
        self.assertEqual(result, ("openai-compat", None))


if __name__ == "__main__":
    unittest.main()
