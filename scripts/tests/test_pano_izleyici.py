# yazan: claude · model: sonnet
"""C2 pano_izleyici.py: everything testable off Windows — relevance check,
clipboard-sequence-number skip logic, heartbeat shape, dispatch into
pasaport_kapi through a fake reader/parser, ``--once`` mode, non-Windows
exit, and the ctypes ``argtypes``/``restype`` binding contract."""

from __future__ import annotations

import ctypes
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import pano_izleyici


class RelevanceTests(unittest.TestCase):
    def test_donus_marker_is_relevant(self) -> None:
        self.assertTrue(pano_izleyici._metin_ilgili("blah [ODENA-DONUS id:x] blah"))

    def test_istek_marker_is_relevant(self) -> None:
        self.assertTrue(pano_izleyici._metin_ilgili("blah [ODENA-ISTEK id:x] blah"))

    def test_unrelated_text_is_not_relevant(self) -> None:
        self.assertFalse(pano_izleyici._metin_ilgili("just a copied URL, nothing else"))

    def test_none_and_empty_text_are_not_relevant(self) -> None:
        self.assertFalse(pano_izleyici._metin_ilgili(None))
        self.assertFalse(pano_izleyici._metin_ilgili(""))


class NonWindowsExitTests(unittest.TestCase):
    def test_main_prints_slug_and_exits_2_off_windows(self) -> None:
        with mock.patch.object(pano_izleyici.os, "name", "posix"):
            exit_code = pano_izleyici.main([])
        self.assertEqual(exit_code, 2)

    def test_once_mode_also_exits_2_off_windows(self) -> None:
        with mock.patch.object(pano_izleyici.os, "name", "posix"):
            exit_code = pano_izleyici.main(["--once"])
        self.assertEqual(exit_code, 2)


class IzleyiciHarness(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.state = self.root / "state"
        self.vault = self.root / "vault"
        self.state.mkdir(parents=True)
        self.vault.mkdir(parents=True)

    def _heartbeat(self) -> dict:
        path = self.state / pano_izleyici.HEARTBEAT_NAME
        return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}


class SequenceSkipTests(IzleyiciHarness):
    def test_first_sequence_number_is_treated_as_a_real_update(self) -> None:
        izleyici = pano_izleyici.Izleyici(self.state, self.vault)
        self.assertTrue(izleyici.clipboard_guncellendi(1))
        self.assertEqual(izleyici.son_sira, 1)

    def test_the_same_sequence_number_again_is_skipped(self) -> None:
        izleyici = pano_izleyici.Izleyici(self.state, self.vault)
        izleyici.clipboard_guncellendi(5)
        self.assertFalse(izleyici.clipboard_guncellendi(5))

    def test_a_changed_sequence_number_is_a_real_update(self) -> None:
        izleyici = pano_izleyici.Izleyici(self.state, self.vault)
        izleyici.clipboard_guncellendi(5)
        self.assertTrue(izleyici.clipboard_guncellendi(6))
        self.assertEqual(izleyici.son_sira, 6)


class HeartbeatTests(IzleyiciHarness):
    def test_heartbeat_file_shape(self) -> None:
        izleyici = pano_izleyici.Izleyici(
            self.state, self.vault, simdi=lambda: "2026-09-02T10:00:00+00:00"
        )
        izleyici.heartbeat_yaz()
        payload = self._heartbeat()
        self.assertEqual(payload["pid"], izleyici.pid)
        self.assertEqual(payload["started"], "2026-09-02T10:00:00+00:00")
        self.assertIsNone(payload["last_event_ts"])
        self.assertEqual(payload["events"], 0)
        self.assertIsNone(payload["last_slug"])

    def test_heartbeat_updates_after_a_dispatched_event(self) -> None:
        clock = iter(["2026-09-02T10:00:00+00:00", "2026-09-02T10:05:00+00:00"])
        izleyici = pano_izleyici.Izleyici(
            self.state,
            self.vault,
            isle=lambda text, state_dir, vault_root: {"hata": None, "bekleyen": True},
            simdi=lambda: next(clock),
        )
        izleyici.isle_ve_kaydet("[ODENA-DONUS id:abc]")
        payload = self._heartbeat()
        self.assertEqual(payload["events"], 1)
        self.assertEqual(payload["last_event_ts"], "2026-09-02T10:05:00+00:00")
        self.assertEqual(payload["last_slug"], "bekleyen")


class DispatchTests(IzleyiciHarness):
    def test_irrelevant_text_is_never_dispatched(self) -> None:
        calls: list[str] = []
        izleyici = pano_izleyici.Izleyici(
            self.state,
            self.vault,
            isle=lambda text, state_dir, vault_root: calls.append(text) or {"hata": None},
        )
        result = izleyici.isle_ve_kaydet("just a normal copied sentence")
        self.assertIsNone(result)
        self.assertEqual(calls, [])
        self.assertEqual(izleyici.olay_sayisi, 0)

    def test_relevant_text_is_handed_to_isle_metin_in_process(self) -> None:
        captured: dict = {}

        def fake_isle(text, state_dir, vault_root):
            captured["text"] = text
            captured["state_dir"] = state_dir
            captured["vault_root"] = vault_root
            return {"hata": None, "bekleyen": True, "raw_hash": "deadbeef"}

        izleyici = pano_izleyici.Izleyici(self.state, self.vault, isle=fake_isle)
        text = "[ODENA-DONUS id:abc123]\n- yeni bilgi\n[/ODENA-DONUS]"
        result = izleyici.isle_ve_kaydet(text)

        self.assertEqual(captured["text"], text)
        self.assertEqual(captured["state_dir"], self.state)
        self.assertEqual(captured["vault_root"], self.vault)
        self.assertEqual(result["raw_hash"], "deadbeef")
        self.assertEqual(izleyici.olay_sayisi, 1)
        self.assertEqual(izleyici.son_slug, "bekleyen")

    def test_a_gate_refusal_is_recorded_as_its_slug(self) -> None:
        izleyici = pano_izleyici.Izleyici(
            self.state,
            self.vault,
            isle=lambda text, state_dir, vault_root: {"hata": "pasaport-karantina", "bekleyen": False},
        )
        izleyici.isle_ve_kaydet("[ODENA-DONUS id:abc]")
        self.assertEqual(izleyici.son_slug, "pasaport-karantina")


class OnceModeTests(IzleyiciHarness):
    def test_once_mode_reads_through_the_injected_reader_and_dispatches(self) -> None:
        captured: dict = {}

        def fake_reader() -> str:
            return "[ODENA-ISTEK id:abc123]\n- eksik\n[/ODENA-ISTEK]"

        def fake_isle(text, state_dir, vault_root):
            captured["text"] = text
            return {"hata": None, "bekleyen": False, "istek_maddeleri": ["eksik"]}

        izleyici = pano_izleyici.Izleyici(self.state, self.vault, isle=fake_isle, oku=fake_reader)
        result = izleyici.bir_kere()

        self.assertIn("ODENA-ISTEK", captured["text"])
        self.assertEqual(result["istek_maddeleri"], ["eksik"])
        self.assertTrue((self.state / pano_izleyici.HEARTBEAT_NAME).exists())

    def test_once_mode_with_irrelevant_clipboard_text_dispatches_nothing(self) -> None:
        izleyici = pano_izleyici.Izleyici(
            self.state, self.vault, oku=lambda: "just a copied paragraph"
        )
        result = izleyici.bir_kere()
        self.assertIsNone(result)
        # The heartbeat is still written even when nothing was relevant —
        # the panel's fallback button needs proof the read actually ran.
        self.assertTrue((self.state / pano_izleyici.HEARTBEAT_NAME).exists())

    def test_once_mode_with_no_clipboard_text_is_a_clean_no_op(self) -> None:
        izleyici = pano_izleyici.Izleyici(self.state, self.vault, oku=lambda: None)
        result = izleyici.bir_kere()
        self.assertIsNone(result)


class Win32BindingTests(unittest.TestCase):
    """``_win32_baglama`` must set ``argtypes``/``restype`` on every raw
    WinAPI call this module makes — same regression class as nezaket.py's
    own binding test (an untyped call defaults to a 32-bit ``c_int``)."""

    def setUp(self) -> None:
        self.user32 = mock.MagicMock(name="user32")
        self.kernel32 = mock.MagicMock(name="kernel32")
        pano_izleyici._win32_baglama(self.user32, self.kernel32)

    def test_every_bound_function_has_restype_and_argtypes(self) -> None:
        bound = [
            (self.user32, "RegisterClassExW"),
            (self.user32, "CreateWindowExW"),
            (self.user32, "DefWindowProcW"),
            (self.user32, "DestroyWindow"),
            (self.user32, "AddClipboardFormatListener"),
            (self.user32, "RemoveClipboardFormatListener"),
            (self.user32, "GetClipboardSequenceNumber"),
            (self.user32, "OpenClipboard"),
            (self.user32, "CloseClipboard"),
            (self.user32, "GetClipboardData"),
            (self.kernel32, "GlobalLock"),
            (self.kernel32, "GlobalUnlock"),
            (self.kernel32, "GlobalSize"),
            (self.user32, "GetMessageW"),
            (self.user32, "TranslateMessage"),
            (self.user32, "DispatchMessageW"),
            (self.user32, "PostQuitMessage"),
            (self.user32, "SetTimer"),
            (self.user32, "KillTimer"),
            (self.kernel32, "GetModuleHandleW"),
        ]
        for namespace, name in bound:
            func = getattr(namespace, name)
            self.assertIsInstance(
                func.argtypes, list, f"{name}.argtypes was never assigned"
            )
            # PostQuitMessage genuinely returns void (restype = None is the
            # correct, explicit declaration) — what must never happen is the
            # attribute staying untouched (a MagicMock auto-vivifies it).
            self.assertNotIsInstance(
                func.restype, mock.MagicMock, f"{name}.restype was never assigned"
            )

    def test_ensure_win32_baglama_is_noop_off_windows(self) -> None:
        with mock.patch.object(pano_izleyici, "_win32_bound", False):
            pano_izleyici._ensure_win32_baglama()  # must not raise


if __name__ == "__main__":
    unittest.main()
