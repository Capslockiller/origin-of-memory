# yazan: codex
# model: gpt-5.6-sol
"""Behavioral and source-boundary coverage for the Local Brain panel."""

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
    """Panel transport tests do not call a model and need no ledger sandbox."""
    yield


@unittest.skipUnless(POWERSHELL, "Windows PowerShell is required")
class PanelServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.process = subprocess.Popen(
            [
                POWERSHELL,
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(REPO_ROOT / "beyin.ps1"),
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
        for _ in range(5):
            line = cls.process.stdout.readline()
            output.append(line)
            if line.startswith("PANEL_READY "):
                ready_url = line.removeprefix("PANEL_READY ").strip()
                break
            if cls.process.poll() is not None:
                break
        if not ready_url:
            cls.process.kill()
            raise AssertionError("panel server did not start:\n" + "".join(output))

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
        body: bytes | None = None,
        last_event_id: int | None = None,
        omit_origin: bool = False,
        omit_content_type: bool = False,
    ) -> tuple[int, object, bytes]:
        headers: dict[str, str] = {}
        if not omit_content_type:
            headers["Content-Type"] = "application/json"
        if not omit_origin:
            headers["Origin"] = origin if origin is not None else cls.base
        if cookie:
            headers["Cookie"] = cookie
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
        source = (REPO_ROOT / "beyin.ps1").read_text(encoding="utf-8")
        self.assertIn("[Net.IPAddress]::Loopback, 0", source)
        self.assertNotIn("[Net.IPAddress]::Any", source)
        self.assertNotIn("[Net.IPAddress]::IPv6Any", source)

    def test_api_request_without_session_cookie_is_rejected(self) -> None:
        status, _, _ = self._request_raw("/api/health")
        self.assertEqual(status, 401)

    def test_health_rejects_wrong_origin_even_with_valid_session(self) -> None:
        """This fails if Test-ApiEnvelope's exact Origin check is removed."""
        status, _, _ = self._request_raw(
            "/api/health",
            origin="http://example.invalid",
            cookie=self.cookie,
        )
        self.assertEqual(status, 403)

    def test_bodyless_same_origin_get_is_accepted(self) -> None:
        status, _, body = self._request_raw(
            "/api/today",
            cookie=self.cookie,
            omit_origin=True,
            omit_content_type=True,
        )
        self.assertEqual(status, 200, body.decode("utf-8", "replace"))
        self.assertIn("sessions", json.loads(body))

    def test_health_consumes_the_documented_json_contract(self) -> None:
        status, _, body = self._request_raw(
            "/api/health",
            cookie=self.cookie,
            omit_origin=True,
            omit_content_type=True,
        )
        self.assertEqual(status, 200, body.decode("utf-8", "replace"))
        report = json.loads(body)
        self.assertEqual(report["schema_version"], 1)
        self.assertEqual(
            [row["component"] for row in report["rows"]],
            ["flush", "compile", "ingest"],
        )
        self.assertIn("backends", report["calls"])
        self.assertIn("components", report["calls"])

    def test_doctor_action_runs_and_streams_a_zero_exit_status(self) -> None:
        status, _, body = self._request_raw(
            "/api/action/doctor",
            method="POST",
            cookie=self.cookie,
            body=b"{}",
        )
        self.assertEqual(status, 202, body.decode("utf-8", "replace"))

        last_seen = 0
        events: list[dict] = []
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            status, _, payload = self._request_raw(
                "/api/events",
                cookie=self.cookie,
                last_event_id=last_seen,
            )
            self.assertEqual(status, 200)
            for frame in re.split(r"\r?\n\r?\n", payload.decode("utf-8")):
                if not frame or frame.startswith(":"):
                    continue
                lines = frame.splitlines()
                event_id = int(lines[0].removeprefix("id: "))
                event = json.loads(lines[2].removeprefix("data: "))
                last_seen = max(last_seen, event_id)
                events.append(event)
            if any(event["type"] == "operation-completed" for event in events):
                break
            time.sleep(0.1)

        completed = [
            event
            for event in events
            if event["type"] == "operation-completed"
            and event["operation"] == "doctor"
        ]
        self.assertEqual(len(completed), 1, events)
        self.assertEqual(completed[0]["exit_code"], 0)
        self.assertTrue(
            any(event["type"] == "operation-output" for event in events),
            events,
        )

    def test_unknown_and_parent_paths_are_404_and_not_served(self) -> None:
        for target in ("/unknown", "/%2e%2e/beyin.ps1", "/../beyin.ps1"):
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
                self.assertNotIn(b"Resolve-PanelPaths", response)

    def test_csp_allows_only_its_own_connections(self) -> None:
        request = urllib.request.Request(
            self.base + "/",
            headers={"Host": f"127.0.0.1:{self.port}"},
        )
        with urllib.request.urlopen(request, timeout=10) as response:
            csp = response.headers["Content-Security-Policy"]
        directives = {
            part.strip().split(" ", 1)[0]: part.strip()
            for part in csp.split(";")
            if part.strip()
        }
        self.assertEqual(directives["default-src"], "default-src 'none'")
        self.assertEqual(directives["connect-src"], "connect-src 'self'")
        self.assertNotIn("'unsafe-eval'", csp)

    def test_page_has_no_external_reference_and_exactly_two_tabs(self) -> None:
        page = (REPO_ROOT / "gui" / "panel.html").read_text(encoding="utf-8")
        self.assertIsNone(re.search(r"https?://", page, flags=re.IGNORECASE))
        self.assertNotIn("<link", page.lower())
        self.assertEqual(len(re.findall(r'role="tab"', page)), 2)
        self.assertIn("Run the doctor", page)
        self.assertIn("window.confirm", page)

    def test_no_route_can_delete_anything(self) -> None:
        source = (REPO_ROOT / "beyin.ps1").read_text(encoding="utf-8")
        forbidden = (
            r"\bRemove-Item\b",
            r"\bClear-Content\b",
            r"\[IO\.File\]::Delete",
            r"\[IO\.Directory\]::Delete",
            r"\.Delete\s*\(",
            r"\b(rm|del|erase|rmdir)\b",
        )
        for pattern in forbidden:
            with self.subTest(pattern=pattern):
                self.assertIsNone(re.search(pattern, source, flags=re.IGNORECASE))
        self.assertNotIn("/api/delete", source.lower())
        self.assertNotIn("/api/remove", source.lower())

    def test_server_has_no_setup_or_runtime_acquisition_route(self) -> None:
        source = (REPO_ROOT / "beyin.ps1").read_text(encoding="utf-8")
        self.assertNotIn("/api/install", source.lower())
        self.assertNotIn("kur.ps1", source.lower())
        self.assertNotIn("-answers", source.lower())


@unittest.skipUnless(POWERSHELL, "Windows PowerShell is required")
class PanelIdleShutdownTests(unittest.TestCase):
    def test_an_idle_panel_exits_without_ever_being_used(self) -> None:
        process = subprocess.Popen(
            [
                POWERSHELL,
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(REPO_ROOT / "beyin.ps1"),
                "-NoBrowser",
                "-GraceSeconds",
                "2",
            ],
            cwd=REPO_ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )
        self.addCleanup(process.kill)
        try:
            process.wait(timeout=45)
        except subprocess.TimeoutExpired:
            self.fail("the idle panel never exited")
        self.assertEqual(process.returncode, 0)
