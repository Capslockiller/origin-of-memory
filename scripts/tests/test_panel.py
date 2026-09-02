# yazan: codex
# model: gpt-5.6-sol
"""Behavioral and source-boundary coverage for the Local Brain panel."""

from __future__ import annotations

import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import tempfile
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
        cls.unreachable_ollama = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        cls.unreachable_ollama.bind(("127.0.0.1", 0))
        ollama_port = cls.unreachable_ollama.getsockname()[1]
        environment = os.environ.copy()
        environment["BEYIN_OLLAMA_URL"] = f"http://127.0.0.1:{ollama_port}"
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
            env=environment,
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
        cls.unreachable_ollama.close()

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

    def test_local_model_routes_reject_a_missing_session(self) -> None:
        routes = (
            ("/api/local-models", "GET", None),
            ("/api/action/pull", "POST", b"{}"),
            ("/api/action/pull-cancel", "POST", b"{}"),
            ("/api/action/backend", "POST", b"{}"),
            ("/api/action/try", "POST", b"{}"),
        )
        for path, method, body in routes:
            with self.subTest(path=path):
                status, _, _ = self._request_raw(
                    path,
                    method=method,
                    body=body,
                )
                self.assertEqual(status, 401)

    def test_local_model_routes_reject_a_wrong_origin(self) -> None:
        routes = (
            ("/api/local-models", "GET", None),
            ("/api/action/pull", "POST", b"{}"),
            ("/api/action/pull-cancel", "POST", b"{}"),
            ("/api/action/backend", "POST", b"{}"),
            ("/api/action/try", "POST", b"{}"),
        )
        for path, method, body in routes:
            with self.subTest(path=path):
                status, _, _ = self._request_raw(
                    path,
                    method=method,
                    origin="http://example.invalid",
                    cookie=self.cookie,
                    body=body,
                )
                self.assertEqual(status, 403)

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
        for target in (
            "/unknown",
            "/api/local-models/unknown",
            "/api/action/pull/unknown",
            "/api/action/delete",
            "/%2e%2e/beyin.ps1",
            "/../beyin.ps1",
        ):
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

    def test_page_has_no_external_reference_and_exactly_three_tabs(self) -> None:
        page = (REPO_ROOT / "gui" / "panel.html").read_text(encoding="utf-8")
        self.assertIsNone(re.search(r"https?://", page, flags=re.IGNORECASE))
        self.assertNotIn("<link", page.lower())
        self.assertEqual(len(re.findall(r'role="tab"', page)), 3)
        self.assertIn(">Local models</button>", page)
        self.assertIn("Run the doctor", page)
        self.assertIn("window.confirm", page)

    def test_backend_switch_rejects_unknown_backend_value(self) -> None:
        """Removing the backend allow-list changes this error and fails the test."""
        status, _, body = self._request_raw(
            "/api/action/backend",
            method="POST",
            cookie=self.cookie,
            body=json.dumps(
                {
                    "backend": "unknown-backend",
                    "confirmation": (
                        "Set BEYIN_MODEL_BACKEND=unknown-backend in Windows "
                        "user environment (HKCU\\Environment)"
                    ),
                }
            ).encode("utf-8"),
        )
        self.assertEqual(status, 400, body.decode("utf-8", "replace"))
        self.assertEqual(json.loads(body)["error"], "invalid_backend")

    def test_backend_switch_requires_the_disclosed_change_confirmation(self) -> None:
        status, _, body = self._request_raw(
            "/api/action/backend",
            method="POST",
            cookie=self.cookie,
            body=b'{"backend":"ollama"}',
        )
        self.assertEqual(status, 400, body.decode("utf-8", "replace"))
        report = json.loads(body)
        self.assertEqual(report["error"], "confirmation_required")
        self.assertIn("BEYIN_MODEL_BACKEND=ollama", report["confirmation"])
        self.assertIn("HKCU\\Environment", report["confirmation"])

    def test_ollama_unreachable_degrades_without_fabricating_inventory(self) -> None:
        status, _, body = self._request_raw(
            "/api/local-models",
            cookie=self.cookie,
            omit_origin=True,
            omit_content_type=True,
        )
        self.assertEqual(status, 200, body.decode("utf-8", "replace"))
        report = json.loads(body)
        self.assertTrue(report["python_available"])
        self.assertIn("ram_gb", report["computer"])
        self.assertIn("free_disk_gb", report["computer"])
        self.assertTrue(report["recommendations"], report)
        self.assertTrue(
            all(
                row["label"] in {"fits-gpu", "tight", "cpu-ok", "no-fit"}
                and "size_gb" in row
                and "why" in row
                for row in report["recommendations"]
            )
        )
        self.assertIn(
            report["active_backend"]["backend"],
            {"claude", "antigravity", "ollama", "openai-compat"},
        )
        self.assertEqual(
            set(report["active_backend"]["models"]),
            {"fast", "smart"},
        )
        ollama = report["ollama"]
        self.assertIn(ollama["status"], {"not-installed", "not-running"})
        self.assertIsNone(ollama["models"])
        if ollama["status"] == "not-installed":
            self.assertIn("not installed", ollama["message"])
        else:
            self.assertIn("installed but is not running", ollama["message"])

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
        self.assertNotIn("/api/action/delete", source.lower())
        self.assertNotIn("/api/action/remove", source.lower())
        page = (REPO_ROOT / "gui" / "panel.html").read_text(encoding="utf-8")
        self.assertNotIn("remove model", page.lower())
        self.assertNotIn("delete model", page.lower())

    def test_server_has_no_setup_or_runtime_acquisition_route(self) -> None:
        source = (REPO_ROOT / "beyin.ps1").read_text(encoding="utf-8")
        self.assertNotIn("/api/install", source.lower())
        self.assertNotIn("kur.ps1", source.lower())
        self.assertNotIn("-answers", source.lower())


class NezaketSerbestContractTests(unittest.TestCase):
    """Static, source-level checks for the ``/api/action/nezaket-serbest``
    "never drop work" contract. Unlike PanelServerTests above these need no
    live PowerShell listener — they inspect beyin.ps1's source text directly
    for the required ordering and response shape, so they run (and can
    catch a regression) even off Windows."""

    @classmethod
    def setUpClass(cls) -> None:
        source = (REPO_ROOT / "beyin.ps1").read_text(encoding="utf-8")
        match = re.search(
            r"if \(\$Request\.Target -eq '/api/action/nezaket-serbest'\) \{\r?\n"
            r"(?:.*?\r?\n)*?  \}\r?\n",
            source,
        )
        assert match, "nezaket-serbest route handler not found in beyin.ps1"
        cls.handler = match.group(0)

    def test_active_operation_is_checked_before_any_queue_pop(self) -> None:
        # This route must never drop work: if an operation is already
        # running there is nothing safe to start, so the 409 must come
        # before nezaket.py serbest is ever invoked — popping first (the
        # previous behaviour) could pop ids from the queue with no way left
        # to start anything for them.
        conflict_at = self.handler.index("operation_in_progress")
        pop_at = self.handler.index("'serbest',")
        self.assertLess(conflict_at, pop_at)

    def test_only_the_first_selected_id_is_released(self) -> None:
        self.assertIn("$firstId = [string]$ids[0]", self.handler)
        self.assertIn("'serbest', $firstId", self.handler)
        # The previous, reverted contract released every selected id in one
        # call by appending the whole $ids array to the argv.
        self.assertNotIn("+ $ids)", self.handler)

    def test_response_shape_is_started_and_remaining_selected(self) -> None:
        self.assertIn("started = $started", self.handler)
        self.assertIn("remaining_selected = [Math]::Max(0, $ids.Count - 1)", self.handler)
        # The previous, reverted response shape.
        self.assertNotIn("remaining_to_start", self.handler)
        self.assertNotIn("released = $released", self.handler)

    def test_panel_html_shows_409_and_remaining_selected_as_a_visible_notice(
        self,
    ) -> None:
        page = (REPO_ROOT / "gui" / "panel.html").read_text(encoding="utf-8")
        self.assertIn('id="nezaket-notice"', page)
        # The 409 must be surfaced, not swallowed into the scrolling output
        # log alone.
        self.assertIn("response.status === 409", page)
        # The still-queued remainder must be called out by name.
        self.assertIn("result.remaining_selected", page)
        self.assertIn("kuyrukta bekliyor", page)


@unittest.skipUnless(POWERSHELL, "Windows PowerShell is required")
class PanelPullPreflightTests(unittest.TestCase):
    def test_pull_with_insufficient_disk_is_refused(self) -> None:
        with tempfile.TemporaryDirectory(prefix="panel-probe-") as temporary:
            temporary_path = Path(temporary)
            driver = temporary_path / "probe_driver.py"
            launcher = temporary_path / "python.cmd"
            driver.write_text(
                """
import json
import sys

arguments = sys.argv[1:]
if any(value.endswith("donanim.py") for value in arguments):
    print(json.dumps({
        "ram_gb": 8.0,
        "cpu": {"name": "Test CPU", "physical_cores": 4, "logical_cores": 8},
        "gpus": [],
        "free_disk_gb": 0.1,
        "model_store": "<model-store>",
        "commands": {"ollama": True},
        "os_build": "test",
        "notes": [],
    }))
elif any(value.endswith("model_oneri.py") for value in arguments):
    print(json.dumps([{
        "tag": "qwen3:4b",
        "size_gb": 2.5,
        "need_gb": 4.0,
        "label": "cpu-ok",
        "role": "fast",
        "why": "Captured recommendation.",
    }]))
else:
    raise SystemExit(2)
""".lstrip(),
                encoding="utf-8",
            )
            launcher.write_text(
                f'@echo off\r\n"{sys.executable}" "{driver}" %*\r\n',
                encoding="utf-8",
            )
            environment = os.environ.copy()
            environment["BEYIN_PYTHON"] = str(launcher)
            environment["BEYIN_OLLAMA_URL"] = "http://127.0.0.1:1"
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
                    "120",
                ],
                cwd=REPO_ROOT,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=environment,
            )
            self.addCleanup(process.kill)
            assert process.stdout is not None
            ready_url = None
            output: list[str] = []
            for _ in range(5):
                line = process.stdout.readline()
                output.append(line)
                if line.startswith("PANEL_READY "):
                    ready_url = line.removeprefix("PANEL_READY ").strip()
                    break
            self.assertIsNotNone(ready_url, "".join(output))
            parsed = urllib.parse.urlsplit(ready_url)
            base = f"http://{parsed.hostname}:{parsed.port}"

            def request(
                path: str,
                *,
                body: bytes,
                cookie: str | None = None,
            ) -> tuple[int, object, bytes]:
                headers = {"Content-Type": "application/json", "Origin": base}
                if cookie:
                    headers["Cookie"] = cookie
                outgoing = urllib.request.Request(
                    base + path,
                    data=body,
                    headers=headers,
                    method="POST",
                )
                try:
                    with urllib.request.urlopen(outgoing, timeout=20) as response:
                        return response.status, response.headers, response.read()
                except urllib.error.HTTPError as error:
                    return error.code, error.headers, error.read()

            cookie = None
            try:
                status, headers, body = request(
                    "/api/session",
                    body=json.dumps({"token": parsed.fragment}).encode("utf-8"),
                )
                self.assertEqual(status, 200, body.decode("utf-8", "replace"))
                cookie = headers["Set-Cookie"].split(";", 1)[0]
                status, _, body = request(
                    "/api/action/pull",
                    cookie=cookie,
                    body=b'{"model":"qwen3:4b"}',
                )
                self.assertEqual(status, 409, body.decode("utf-8", "replace"))
                report = json.loads(body)
                self.assertEqual(report["error"], "insufficient_disk")
                self.assertEqual(report["free_disk_gb"], 0.1)
                self.assertEqual(report["required_disk_gb"], 3.75)
                self.assertRegex(
                    report["message"],
                    r"0[,.]10 GB free, 3[,.]75 GB required",
                )
            finally:
                if cookie and process.poll() is None:
                    try:
                        request("/api/quit", cookie=cookie, body=b"{}")
                        process.wait(timeout=20)
                    except Exception:
                        process.kill()
                        process.wait(timeout=5)


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
