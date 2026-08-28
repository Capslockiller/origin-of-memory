# yazan: codex
# model: gpt-5.6-sol
"""Behavioral coverage for the loopback-only graphical setup wizard."""

from __future__ import annotations

import json
from pathlib import Path
import re
import shutil
import socket
import subprocess
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request

import pytest


REPO_ROOT = Path(__file__).resolve().parents[2]
POWERSHELL = shutil.which("powershell")


@pytest.fixture(autouse=True)
def _isolate_call_ledger():
    """This server test never calls claude_runner and needs no ledger temp dir."""
    yield


@unittest.skipUnless(POWERSHELL, "Windows PowerShell is required")
class GuiWizardServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.process = subprocess.Popen(
            [
                POWERSHELL,
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(REPO_ROOT / "kur-gui.ps1"),
                "-NoBrowser",
                "-GraceSeconds",
                "120",
            ],
            cwd=REPO_ROOT,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        assert cls.process.stdout is not None
        output: list[str] = []
        ready_url = None
        for _ in range(4):
            line = cls.process.stdout.readline()
            output.append(line)
            if line.startswith("GUI_READY "):
                ready_url = line.removeprefix("GUI_READY ").strip()
                break
            if cls.process.poll() is not None:
                break
        if not ready_url:
            cls.process.kill()
            raise AssertionError("GUI server did not start:\n" + "".join(output))

        parsed = urllib.parse.urlsplit(ready_url)
        cls.host = parsed.hostname
        cls.port = parsed.port
        cls.token = parsed.fragment
        cls.base = f"http://{cls.host}:{cls.port}"
        status, headers, _ = cls._request_raw(
            "/api/session",
            method="POST",
            origin=cls.base,
            body=json.dumps({"token": cls.token}).encode("utf-8"),
        )
        if status != 200:
            cls.process.kill()
            raise AssertionError(f"session exchange failed: {status}")
        cls.cookie = headers["Set-Cookie"].split(";", 1)[0]

    @classmethod
    def tearDownClass(cls) -> None:
        if cls.process.poll() is None:
            try:
                cls._request_raw(
                    "/api/quit",
                    method="POST",
                    origin=cls.base,
                    cookie=cls.cookie,
                    body=b"{}",
                )
                cls.process.wait(timeout=20)
            except Exception:
                cls.process.kill()
                cls.process.wait(timeout=5)

    @classmethod
    def _request_raw(
        cls,
        path: str,
        *,
        method: str = "GET",
        origin: str | None = None,
        cookie: str | None = None,
        host: str | None = None,
        body: bytes | None = None,
        last_event_id: int | None = None,
        omit_origin: bool = False,
        omit_content_type: bool = False,
    ) -> tuple[int, object, bytes]:
        # A real browser omits both of these on a same-origin GET; the flags let
        # a test reproduce that rather than always sending what urllib is told.
        headers: dict[str, str] = {}
        if not omit_content_type:
            headers["Content-Type"] = "application/json"
        if not omit_origin:
            headers["Origin"] = origin if origin is not None else cls.base
        if cookie:
            headers["Cookie"] = cookie
        if host:
            headers["Host"] = host
        if last_event_id is not None:
            headers["Last-Event-ID"] = str(last_event_id)
        request = urllib.request.Request(
            cls.base + path,
            data=body,
            headers=headers,
            method=method,
        )
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                return response.status, response.headers, response.read()
        except urllib.error.HTTPError as error:
            return error.code, error.headers, error.read()

    def test_listener_reports_loopback_endpoint_and_not_a_wildcard(self) -> None:
        self.assertEqual(self.host, "127.0.0.1")
        self.assertIsInstance(self.port, int)
        with socket.create_connection((self.host, self.port), timeout=5):
            pass
        source = (REPO_ROOT / "kur-gui.ps1").read_text(encoding="utf-8")
        self.assertIn("[Net.IPAddress]::Loopback, 0", source)
        self.assertNotIn("[Net.IPAddress]::Any", source)
        self.assertNotIn("[Net.IPAddress]::IPv6Any", source)

    def test_api_request_without_session_cookie_is_rejected(self) -> None:
        status, _, _ = self._request_raw("/api/detect")
        self.assertEqual(status, 401)

    def test_api_rejects_wrong_origin_even_with_valid_session(self) -> None:
        """This must fail if Test-ApiEnvelope's Origin check is removed."""
        status, _, _ = self._request_raw(
            "/api/detect",
            origin="http://example.invalid",
            cookie=self.cookie,
        )
        self.assertEqual(status, 403)

    def test_api_rejects_wrong_host_even_with_valid_session(self) -> None:
        status, _, _ = self._request_raw(
            "/api/detect",
            host=f"localhost:{self.port}",
            cookie=self.cookie,
        )
        self.assertEqual(status, 403)

    def test_unknown_and_parent_paths_are_not_served(self) -> None:
        for target in ("/unknown", "/%2e%2e/kur.ps1", "/../kur.ps1"):
            with self.subTest(target=target):
                request = (
                    f"GET {target} HTTP/1.1\r\n"
                    f"Host: 127.0.0.1:{self.port}\r\n"
                    "Connection: close\r\n\r\n"
                ).encode("ascii")
                with socket.create_connection((self.host, self.port), timeout=5) as client:
                    client.sendall(request)
                    response = bytearray()
                    while True:
                        chunk = client.recv(4096)
                        if not chunk:
                            break
                        response.extend(chunk)
                self.assertTrue(
                    bytes(response).startswith(b"HTTP/1.1 404 Not Found"),
                    response.decode("utf-8", "replace"),
                )
                self.assertNotIn(b"# yazan:", response)

    def test_launch_token_is_single_use(self) -> None:
        status, _, body = self._request_raw(
            "/api/session",
            method="POST",
            origin=self.base,
            body=json.dumps({"token": self.token}).encode("utf-8"),
        )
        self.assertEqual(status, 401, body.decode("utf-8", "replace"))

    def test_page_has_strict_csp_and_no_external_reference(self) -> None:
        request = urllib.request.Request(
            self.base + "/",
            headers={"Host": f"127.0.0.1:{self.port}"},
        )
        with urllib.request.urlopen(request, timeout=10) as response:
            page = response.read().decode("utf-8")
            csp = response.headers["Content-Security-Policy"]
        directives = {
            part.strip().split(" ", 1)[0]: part.strip()
            for part in csp.split(";")
            if part.strip()
        }
        self.assertEqual(directives["default-src"], "default-src 'none'")
        # connect-src is load-bearing, not decoration: without it the page's own
        # fetch() falls back to default-src 'none' and the browser blocks every
        # API call. urllib does not enforce CSP, so only asserting it here keeps
        # the regression visible to a suite that never opens a browser.
        self.assertEqual(directives["connect-src"], "connect-src 'self'")
        self.assertNotIn("'unsafe-eval'", csp)
        for directive in ("style-src", "script-src"):
            self.assertIn("'self'", directives[directive])
        self.assertIsNone(re.search(r"https?://", page, flags=re.IGNORECASE))
        self.assertNotIn("<link", page.lower())
        self.assertIn("<button", page.lower())

    def test_a_bodyless_same_origin_request_is_accepted(self) -> None:
        """A browser sends no Origin and no Content-Type on a same-origin GET.

        Requiring both on every route rejected the page's own calls with 403 and
        415 while every Python test passed, because urllib sends exactly what a
        test hands it and a browser decides these headers for itself.
        """
        # /api/events rather than /api/detect: this asserts the envelope guard,
        # and starting a real operation would renumber the events the SSE test
        # asserts on.
        status, _headers, body = self._request_raw(
            "/api/events",
            cookie=self.cookie,
            last_event_id=0,
            omit_origin=True,
            omit_content_type=True,
        )
        self.assertEqual(status, 200, body.decode("utf-8", "replace"))

    def test_a_wrong_origin_is_still_refused_when_present(self) -> None:
        status, _headers, _body = self._request_raw(
            "/api/events",
            cookie=self.cookie,
            last_event_id=0,
            origin="http://evil.example",
        )
        self.assertEqual(status, 403)

    def test_sse_frames_are_well_formed_sequence_numbered_and_replayable(self) -> None:
        status, _, body = self._request_raw(
            "/api/detect",
            cookie=self.cookie,
        )
        self.assertEqual(status, 202, body.decode("utf-8", "replace"))

        frames: dict[int, dict] = {}
        deadline = time.monotonic() + 30
        last_seen = 0
        while time.monotonic() < deadline:
            status, headers, payload = self._request_raw(
                "/api/events",
                cookie=self.cookie,
                last_event_id=last_seen,
            )
            self.assertEqual(status, 200)
            self.assertEqual(headers.get_content_type(), "text/event-stream")
            text = payload.decode("utf-8")
            for frame in re.split(r"\r?\n\r?\n", text):
                if not frame or frame.startswith(":"):
                    continue
                lines = frame.splitlines()
                self.assertRegex(lines[0], r"^id: \d+$")
                self.assertRegex(lines[1], r"^event: [a-z-]+$")
                self.assertTrue(lines[2].startswith("data: "))
                event_id = int(lines[0].removeprefix("id: "))
                event = json.loads(lines[2].removeprefix("data: "))
                self.assertEqual(event["sequence"], event_id)
                frames[event_id] = event
                last_seen = max(last_seen, event_id)
            if any(event["type"] == "detection-result" for event in frames.values()):
                break
            time.sleep(0.2)

        self.assertTrue(frames, "no SSE event was received")
        self.assertEqual(list(frames), sorted(frames))
        results = [
            event for event in frames.values() if event["type"] == "detection-result"
        ]
        self.assertEqual(len(results), 1, frames)
        self.assertGreaterEqual(len(results[0]["rows"]), 7)
        self.assertIn("Python 3", {row["name"] for row in results[0]["rows"]})

        status, _, replay = self._request_raw(
            "/api/events",
            cookie=self.cookie,
            last_event_id=0,
        )
        self.assertEqual(status, 200)
        self.assertIn(b"id: 1\r\n", replay)
        self.assertIn(b'"sequence":1', replay)


if __name__ == "__main__":
    unittest.main()
