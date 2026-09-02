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


class KaydetPanelContractTests(unittest.TestCase):
    """Static, source-level checks for ``/api/action/kaydet`` (F3 Kaydet).

    Same rationale as ``NezaketSerbestContractTests`` above: these need no
    live PowerShell listener, so they run — and can catch a regression —
    even off Windows.
    """

    @classmethod
    def setUpClass(cls) -> None:
        cls.source = (REPO_ROOT / "beyin.ps1").read_text(encoding="utf-8")
        match = re.search(
            r"if \(\$Request\.Target -eq '/api/action/kaydet'\) \{\r?\n"
            r"(?:.*?\r?\n)*?  \}\r?\n",
            cls.source,
        )
        assert match, "kaydet route handler not found in beyin.ps1"
        cls.handler = match.group(0)
        function_match = re.search(
            r"function New-KaydetCommand\(.*?\r?\n\}\r?\n",
            cls.source,
            flags=re.DOTALL,
        )
        assert function_match, "New-KaydetCommand not found in beyin.ps1"
        cls.command_builder = function_match.group(0)

    def test_route_is_allowlisted(self) -> None:
        self.assertIn("'/api/action/kaydet'", self.source)
        allow_match = re.search(r"\$allowed = @\(\r?\n(?:.*?\r?\n)*?\s*\)\r?\n", self.source)
        assert allow_match, "route allowlist not found"
        self.assertIn("/api/action/kaydet", allow_match.group(0))

    def test_only_post_is_accepted(self) -> None:
        self.assertIn("$Request.Method -cne 'POST'", self.handler)
        self.assertIn("method_not_allowed", self.handler)

    def test_an_active_operation_refuses_a_new_one_with_409(self) -> None:
        self.assertIn("$script:ActiveOperation", self.handler)
        self.assertIn("operation_in_progress", self.handler)

    def test_missing_or_blank_metin_is_rejected_before_anything_starts(self) -> None:
        self.assertIn("missing_metin", self.handler)
        reject_at = self.handler.index("missing_metin")
        start_at = self.handler.index("Start-PanelCommand")
        self.assertLess(reject_at, start_at)

    def test_the_operation_kind_wired_into_dispatch_is_kaydet(self) -> None:
        self.assertIn("Start-PanelCommand 'kaydet'", self.handler)
        self.assertIn("New-KaydetCommand $metin $baslik", self.handler)

    def test_note_text_is_fed_over_stdin_never_as_an_argument(self) -> None:
        # The whole point: the note text must never land in argv (process
        # listings, shell history) — it goes over stdin, base64-only to
        # survive the EncodedCommand hop, and kaydet.py reads it with --stdin.
        self.assertIn("'--stdin'", self.command_builder)
        self.assertIn("$metin | ", self.command_builder)
        self.assertIn("FromBase64String", self.command_builder)
        # The metin value itself must never be embedded as a literal argv
        # token — only baslik (a short title) is ever added to $arguments.
        self.assertNotIn("$arguments.Add($Metin)", self.command_builder)
        self.assertIn("$arguments.Add($Baslik)", self.command_builder)

    def test_vault_root_and_state_dir_are_passed_through(self) -> None:
        self.assertIn("$script:PanelPaths.Vault", self.command_builder)
        self.assertIn("$script:PanelPaths.State", self.command_builder)

    def test_panel_html_has_a_kaydet_card_wired_to_the_route(self) -> None:
        page = (REPO_ROOT / "gui" / "panel.html").read_text(encoding="utf-8")
        self.assertIn('id="kaydet-metin"', page)
        self.assertIn('id="kaydet-baslik"', page)
        self.assertIn('id="kaydet-button"', page)
        self.assertIn("/api/action/kaydet", page)
        self.assertIn("JSON.stringify({ metin, baslik })", page)
        # The button must be a `.action`, so setActive()'s generic disable
        # sweep covers it while any operation is running — no bespoke wiring.
        self.assertIn('class="action" id="kaydet-button"', page)

    def test_panel_html_never_persists_the_draft_to_local_storage(self) -> None:
        page = (REPO_ROOT / "gui" / "panel.html").read_text(encoding="utf-8")
        self.assertNotIn("localStorage", page)

    def test_draft_is_cleared_in_the_result_handler_not_the_post_handler(self) -> None:
        # CRITICAL fix: a 202 only means the process started — every gate
        # and the write itself still happen inside it. The draft must clear
        # only once the real result (yazildi:true) comes back over SSE, not
        # the instant the POST that launched the process succeeds.
        page = (REPO_ROOT / "gui" / "panel.html").read_text(encoding="utf-8")

        click_match = re.search(
            r"kaydetButton\.addEventListener\('click', async \(\) => \{\r?\n"
            r"(?:.*?\r?\n)*?      \}\);\r?\n",
            page,
        )
        assert click_match, "kaydet click handler not found in panel.html"
        click_handler = click_match.group(0)

        result_match = re.search(
            r"function handleKaydetResult\(payload\) \{\r?\n"
            r"(?:.*?\r?\n)*?      \}\r?\n",
            page,
        )
        assert result_match, "handleKaydetResult not found in panel.html"
        result_handler = result_match.group(0)

        self.assertNotIn("kaydetMetin.value = ''", click_handler)
        self.assertNotIn("kaydetBaslik.value = ''", click_handler)
        self.assertIn("kaydetMetin.value = ''", result_handler)
        self.assertIn("kaydetBaslik.value = ''", result_handler)
        # And the clear in the result handler must itself be conditioned on
        # yazildi — never unconditional.
        self.assertIn("payload.yazildi === true", result_handler)
        clear_at = result_handler.index("kaydetMetin.value = ''")
        condition_at = result_handler.index("payload.yazildi === true")
        self.assertLess(condition_at, clear_at)

    def test_kaydet_result_line_is_detected_by_a_fixed_marker(self) -> None:
        page = (REPO_ROOT / "gui" / "panel.html").read_text(encoding="utf-8")
        self.assertIn("KAYDET_MARKER = 'KAYDET-SONUC '", page)
        self.assertIn("line.startsWith(KAYDET_MARKER)", page)
        # And it matches the exact token kaydet.py itself prints.
        kaydet_source = (REPO_ROOT / "scripts" / "kaydet.py").read_text(encoding="utf-8")
        self.assertIn('RESULT_MARKER = "KAYDET-SONUC "', kaydet_source)

    def test_output_encoding_is_set_before_the_pipe_to_kaydet(self) -> None:
        # FOLLOW-UP fix: PowerShell 5.1 pipes to a native process using
        # $OutputEncoding (OEM/ANSI by default), which would mangle Turkish
        # text before kaydet.py ever sees a byte of it.
        self.assertIn("OutputEncoding", self.command_builder)
        self.assertIn("UTF8Encoding(`$false)", self.command_builder)
        encoding_at = self.command_builder.index("OutputEncoding")
        pipe_at = self.command_builder.index("$metin | ")
        self.assertLess(encoding_at, pipe_at)


class PasaportPanelContractTests(unittest.TestCase):
    """Static, source-level checks for F4 part 2's panel wiring: the
    ``/api/pasaport`` status route, the approve/reject/panodan actions, and
    the listener child's spawn/kill lifecycle. Same rationale as
    ``KaydetPanelContractTests`` — no live PowerShell listener required, so
    these run (and catch a regression) even off Windows.
    """

    @classmethod
    def setUpClass(cls) -> None:
        cls.source = (REPO_ROOT / "beyin.ps1").read_text(encoding="utf-8")
        cls.page = (REPO_ROOT / "gui" / "panel.html").read_text(encoding="utf-8")

        def handler(route: str) -> str:
            match = re.search(
                r"if \(\$Request\.Target -eq '" + re.escape(route) + r"'\) \{\r?\n"
                r"(?:.*?\r?\n)*?  \}\r?\n",
                cls.source,
            )
            assert match, f"{route} route handler not found in beyin.ps1"
            return match.group(0)

        cls.onayla_handler = handler("/api/action/pasaport-onayla")
        cls.reddet_handler = handler("/api/action/pasaport-reddet")
        cls.panodan_handler = handler("/api/action/pasaport-panodan")
        cls.get_handler = handler("/api/pasaport")

        start_match = re.search(
            r"function Start-PasaportIzleyici \{\r?\n(?:.*?\r?\n)*?\}\r?\n", cls.source
        )
        assert start_match, "Start-PasaportIzleyici not found in beyin.ps1"
        cls.start_function = start_match.group(0)

        stop_match = re.search(
            r"function Stop-PasaportIzleyici \{\r?\n(?:.*?\r?\n)*?\}\r?\n", cls.source
        )
        assert stop_match, "Stop-PasaportIzleyici not found in beyin.ps1"
        cls.stop_function = stop_match.group(0)

    def test_every_pasaport_route_is_allowlisted(self) -> None:
        allow_match = re.search(r"\$allowed = @\(\r?\n(?:.*?\r?\n)*?\s*\)\r?\n", self.source)
        assert allow_match, "route allowlist not found"
        allowlist = allow_match.group(0)
        for route in (
            "/api/pasaport",
            "/api/action/pasaport-onayla",
            "/api/action/pasaport-reddet",
            "/api/action/pasaport-panodan",
        ):
            with self.subTest(route=route):
                self.assertIn(f"'{route}'", allowlist)

    def test_get_route_is_readonly(self) -> None:
        self.assertIn("$Request.Method -cne 'GET'", self.get_handler)
        self.assertIn("Invoke-PasaportDurum", self.get_handler)

    def test_onayla_is_gated_by_the_409_active_operation_rule(self) -> None:
        self.assertIn("$script:ActiveOperation", self.onayla_handler)
        self.assertIn("operation_in_progress", self.onayla_handler)
        conflict_at = self.onayla_handler.index("operation_in_progress")
        start_at = self.onayla_handler.index("Start-PanelCommand")
        self.assertLess(conflict_at, start_at)

    def test_panodan_is_also_gated_by_the_409_rule(self) -> None:
        self.assertIn("$script:ActiveOperation", self.panodan_handler)
        self.assertIn("operation_in_progress", self.panodan_handler)

    def test_onayla_passes_raw_hash_through_to_the_command_builder(self) -> None:
        self.assertIn("$body.raw_hash", self.onayla_handler)
        self.assertIn("missing_raw_hash", self.onayla_handler)
        self.assertIn("New-PasaportOnaylaCommand $rawHash", self.onayla_handler)
        self.assertIn("Start-PanelCommand 'pasaport-onayla'", self.onayla_handler)

    def test_raw_hash_is_validated_as_64_lowercase_hex_chars(self) -> None:
        # Both handlers must reject a malformed raw_hash with 400 before it
        # ever reaches a command line — not just an empty one.
        for handler in (self.onayla_handler, self.reddet_handler):
            with self.subTest(handler=handler[:40]):
                self.assertIn("bad_raw_hash", handler)
                self.assertIn("-cnotmatch '^[0-9a-f]{64}$'", handler)
                bad_at = handler.index("bad_raw_hash")
                missing_at = handler.index("missing_raw_hash")
                self.assertLess(missing_at, bad_at)

    def test_reddet_runs_synchronously_not_as_a_streamed_operation(self) -> None:
        # Reject never spawns compile, so unlike approve it must never touch
        # $script:ActiveOperation / Start-PanelCommand — it just runs and
        # returns its JSON result directly.
        self.assertIn("$body.raw_hash", self.reddet_handler)
        self.assertIn("'reddet', $rawHash", self.reddet_handler)
        self.assertNotIn("Start-PanelCommand", self.reddet_handler)
        self.assertNotIn("ActiveOperation", self.reddet_handler)

    def test_listener_spawn_is_guarded_by_the_env_switch(self) -> None:
        self.assertIn("BEYIN_PASAPORT_IZLEYICI", self.start_function)
        self.assertIn("'off'", self.start_function)
        # The off-switch must be checked before the process is ever started.
        off_at = self.start_function.index("BEYIN_PASAPORT_IZLEYICI")
        spawn_at = self.start_function.index("Start-Process")
        self.assertLess(off_at, spawn_at)

    def test_listener_is_spawned_hidden_and_kept_as_a_process_object(self) -> None:
        self.assertIn("pano_izleyici.py", self.start_function)
        self.assertIn("-WindowStyle Hidden", self.start_function)
        self.assertIn("-PassThru", self.start_function)
        self.assertIn("$script:PasaportIzleyiciProcess =", self.start_function)

    def test_listener_is_stopped_with_close_then_kill(self) -> None:
        self.assertIn("CloseMainWindow", self.stop_function)
        self.assertIn(".Kill()", self.stop_function)
        close_at = self.stop_function.index("CloseMainWindow")
        kill_at = self.stop_function.index(".Kill()")
        self.assertLess(close_at, kill_at)

    def test_listener_is_started_at_panel_launch_and_stopped_in_the_finally_block(self) -> None:
        self.assertIn("Start-PasaportIzleyici", self.source)
        self.assertIn("Stop-PasaportIzleyici", self.source)
        # Stop must live in the single `finally` block that both the quit
        # path and the idle-shutdown path funnel through — never restarted.
        finally_match = re.search(r"\} finally \{\r?\n(?:.*?\r?\n)*?\}\r?\n\Z", self.source)
        assert finally_match, "top-level finally block not found"
        self.assertIn("Stop-PasaportIzleyici", finally_match.group(0))

    def test_panel_html_has_a_pasaport_card_wired_to_the_routes(self) -> None:
        self.assertIn('id="pasaport-onayla-button"', self.page)
        self.assertIn('id="pasaport-reddet-button"', self.page)
        self.assertIn('id="pasaport-panodan-button"', self.page)
        self.assertIn("/api/pasaport", self.page)
        self.assertIn("/api/action/pasaport-onayla", self.page)
        self.assertIn("/api/action/pasaport-reddet", self.page)
        self.assertIn("/api/action/pasaport-panodan", self.page)

    def test_approve_and_reject_buttons_send_the_raw_hash(self) -> None:
        self.assertIn("JSON.stringify({ raw_hash: pasaportRawHash })", self.page)

    def test_the_raw_hash_shown_is_a_short_form_not_the_full_hash(self) -> None:
        self.assertIn(".slice(0, 12)", self.page)

    def test_pasaport_card_never_uses_innerhtml(self) -> None:
        card_match = re.search(
            r"function renderPasaport\(data\) \{\r?\n(?:.*?\r?\n)*?      \}\r?\n",
            self.page,
        )
        assert card_match, "renderPasaport not found in panel.html"
        self.assertNotIn("innerHTML", card_match.group(0))
        self.assertIn("textContent", card_match.group(0))

    def test_units_and_blind_spot_items_are_rendered_as_plain_text_nodes(self) -> None:
        card_match = re.search(
            r"function renderPasaport\(data\) \{\r?\n(?:.*?\r?\n)*?      \}\r?\n",
            self.page,
        )
        assert card_match
        body = card_match.group(0)
        self.assertIn("item.textContent = unit", body)
        self.assertIn("item.textContent = '(' + entry.adet", body)

    def test_buttons_are_disabled_while_an_operation_is_active(self) -> None:
        # These must be plain `.action` buttons so the generic disable
        # sweep in setActive() covers them with no bespoke wiring.
        self.assertIn('class="action" id="pasaport-onayla-button"', self.page)
        self.assertIn('class="action" id="pasaport-reddet-button"', self.page)
        self.assertIn('class="action mini-action" id="pasaport-panodan-button"', self.page)


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
