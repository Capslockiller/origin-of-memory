#!/usr/bin/env python3
# yazan: claude · model: sonnet
"""C2 — the F4 clipboard listener: a message-only Win32 window that watches
for a pasted ``[ODENA-DONUS]``/``[ODENA-ISTEK]`` web-chat reply and hands it
to ``pasaport_kapi.isle_metin`` in-process, no polling loop.

``AddClipboardFormatListener`` + ``WM_CLIPBOARDUPDATE`` — never a timer that
polls the clipboard. ``GetClipboardSequenceNumber`` is cross-checked on every
update so an unrelated clipboard change (which still fires the message) is
skipped without a read. Only ``CF_UNICODETEXT`` is ever read — no other
clipboard format (image data, file drops, a browser's private HTML/source-URL
formats) is ever enumerated or touched.

Windows-only at runtime; every non-Win32 decision (relevance check,
sequence-skip, dispatch to ``pasaport_kapi``, heartbeat shape) is plain
Python and unit-tested off Windows — see ``_win32_baglama`` and the
``Izleyici`` class below.
"""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import datetime as dt
import os
from pathlib import Path
import sys
import time
from typing import Any, Callable, Sequence

from beyin_ortak import _atomic_write_json
import nezaket
import pasaport_kapi


SCRIPT_DIR = Path(__file__).resolve().parent
VAULT_ROOT = SCRIPT_DIR.parent.parent
STATE_DIR = SCRIPT_DIR / ".state"

HEARTBEAT_NAME = "pano-izleyici.json"
HEARTBEAT_INTERVAL_MS = 60_000
CLIPBOARD_OPEN_RETRIES = 10
CLIPBOARD_OPEN_RETRY_DELAY_S = 0.1

DESTEKLENMIYOR_SLUG = "pano-izleyici-desteklenmiyor"

# The two footer markers isle_metin's own parser looks for — a cheap
# substring check here avoids handing every unrelated clipboard change
# (a copied URL, a screenshot's filename, …) into the parser at all.
ODENA_MARKERS = ("[ODENA-DONUS", "[ODENA-ISTEK")

CF_UNICODETEXT = 13
WM_CLIPBOARDUPDATE = 0x031D
WM_DESTROY = 0x0002
WM_CLOSE = 0x0010
WM_TIMER = 0x0113
HWND_MESSAGE = -3
HEARTBEAT_TIMER_ID = 1


def _metin_ilgili(text: str | None) -> bool:
    """True when clipboard text carries an ODENA marker worth acting on."""
    if not text:
        return False
    return any(marker in text for marker in ODENA_MARKERS)


# --------------------------------------------------------------------------
# Win32 structures + binding. Structures are pure ctypes definitions, safe to
# build on any platform; every function that actually calls a WinAPI routine
# goes through ``_ensure_win32_baglama()`` first — same convention as
# nezaket.py's ``_win32_baglama``.
# --------------------------------------------------------------------------

# ``WINFUNCTYPE`` (stdcall) only exists on Windows; module import must still
# succeed off Windows for the pure logic below to be testable, so this falls
# back to ``CFUNCTYPE`` there — a callback type that is only ever actually
# invoked from ``run()``, which itself is Windows-only.
_FUNCTYPE = getattr(ctypes, "WINFUNCTYPE", ctypes.CFUNCTYPE)
WNDPROC = _FUNCTYPE(
    ctypes.c_long, wintypes.HWND, ctypes.c_uint, wintypes.WPARAM, wintypes.LPARAM
)


class _WNDCLASSEXW(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_uint),
        ("style", ctypes.c_uint),
        ("lpfnWndProc", WNDPROC),
        ("cbClsExtra", ctypes.c_int),
        ("cbWndExtra", ctypes.c_int),
        ("hInstance", wintypes.HINSTANCE),
        ("hIcon", wintypes.HICON),
        ("hCursor", wintypes.HANDLE),
        ("hbrBackground", wintypes.HBRUSH),
        ("lpszMenuName", wintypes.LPCWSTR),
        ("lpszClassName", wintypes.LPCWSTR),
        ("hIconSm", wintypes.HICON),
    ]


class _MSG(ctypes.Structure):
    _fields_ = [
        ("hwnd", wintypes.HWND),
        ("message", ctypes.c_uint),
        ("wParam", wintypes.WPARAM),
        ("lParam", wintypes.LPARAM),
        ("time", ctypes.c_uint32),
        ("pt", wintypes.POINT),
    ]


def _win32_baglama(user32: Any = None, kernel32: Any = None) -> None:
    """Bind ``argtypes``/``restype`` on every raw WinAPI call this module
    makes. Testable off Windows: pass fake namespace objects (two
    ``unittest.mock.MagicMock()``) and inspect the configured attributes.
    """
    if user32 is None:
        user32 = ctypes.windll.user32  # type: ignore[attr-defined]
    if kernel32 is None:
        kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]

    user32.RegisterClassExW.argtypes = [ctypes.POINTER(_WNDCLASSEXW)]
    user32.RegisterClassExW.restype = wintypes.ATOM

    user32.CreateWindowExW.argtypes = [
        wintypes.DWORD,
        wintypes.LPCWSTR,
        wintypes.LPCWSTR,
        wintypes.DWORD,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        wintypes.HWND,
        wintypes.HMENU,
        wintypes.HINSTANCE,
        wintypes.LPVOID,
    ]
    user32.CreateWindowExW.restype = wintypes.HWND

    user32.DefWindowProcW.argtypes = [
        wintypes.HWND,
        ctypes.c_uint,
        wintypes.WPARAM,
        wintypes.LPARAM,
    ]
    user32.DefWindowProcW.restype = ctypes.c_long

    user32.DestroyWindow.argtypes = [wintypes.HWND]
    user32.DestroyWindow.restype = wintypes.BOOL

    user32.AddClipboardFormatListener.argtypes = [wintypes.HWND]
    user32.AddClipboardFormatListener.restype = wintypes.BOOL

    user32.RemoveClipboardFormatListener.argtypes = [wintypes.HWND]
    user32.RemoveClipboardFormatListener.restype = wintypes.BOOL

    user32.GetClipboardSequenceNumber.argtypes = []
    user32.GetClipboardSequenceNumber.restype = wintypes.DWORD

    user32.OpenClipboard.argtypes = [wintypes.HWND]
    user32.OpenClipboard.restype = wintypes.BOOL

    user32.CloseClipboard.argtypes = []
    user32.CloseClipboard.restype = wintypes.BOOL

    user32.GetClipboardData.argtypes = [wintypes.UINT]
    user32.GetClipboardData.restype = wintypes.HANDLE

    kernel32.GlobalLock.argtypes = [wintypes.HANDLE]
    kernel32.GlobalLock.restype = wintypes.LPVOID

    kernel32.GlobalUnlock.argtypes = [wintypes.HANDLE]
    kernel32.GlobalUnlock.restype = wintypes.BOOL

    kernel32.GlobalSize.argtypes = [wintypes.HANDLE]
    kernel32.GlobalSize.restype = ctypes.c_size_t

    user32.GetMessageW.argtypes = [
        ctypes.POINTER(_MSG),
        wintypes.HWND,
        ctypes.c_uint,
        ctypes.c_uint,
    ]
    user32.GetMessageW.restype = ctypes.c_int

    user32.TranslateMessage.argtypes = [ctypes.POINTER(_MSG)]
    user32.TranslateMessage.restype = wintypes.BOOL

    user32.DispatchMessageW.argtypes = [ctypes.POINTER(_MSG)]
    user32.DispatchMessageW.restype = ctypes.c_long

    user32.PostQuitMessage.argtypes = [ctypes.c_int]
    user32.PostQuitMessage.restype = None

    user32.SetTimer.argtypes = [
        wintypes.HWND,
        ctypes.c_size_t,
        ctypes.c_uint,
        wintypes.LPVOID,
    ]
    user32.SetTimer.restype = ctypes.c_size_t

    user32.KillTimer.argtypes = [wintypes.HWND, ctypes.c_size_t]
    user32.KillTimer.restype = wintypes.BOOL

    kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
    kernel32.GetModuleHandleW.restype = wintypes.HMODULE


_win32_bound = False


def _ensure_win32_baglama() -> None:
    """Bind once per process; a no-op off Windows and on every later call."""
    global _win32_bound
    if _win32_bound or os.name != "nt":
        return
    _win32_baglama()
    _win32_bound = True


def _clipboard_metni_oku(
    hwnd: int, *, deneme: int = CLIPBOARD_OPEN_RETRIES, bekleme_sn: float = CLIPBOARD_OPEN_RETRY_DELAY_S
) -> str | None:
    """Read ONLY ``CF_UNICODETEXT`` from the clipboard, ``None`` on any miss.

    ``OpenClipboard`` retries up to ``deneme`` times, 100 ms apart — real
    Windows Terminal Services (rdpclip) routinely holds the clipboard open
    for a brief moment, and a single failed ``OpenClipboard`` must not be
    read as "nothing was copied". Never enumerates or reads any other
    clipboard format.
    """
    _ensure_win32_baglama()
    user32 = ctypes.windll.user32  # type: ignore[attr-defined]
    kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
    opened = False
    for _attempt in range(deneme):
        if user32.OpenClipboard(hwnd):
            opened = True
            break
        time.sleep(bekleme_sn)
    if not opened:
        return None
    try:
        handle = user32.GetClipboardData(CF_UNICODETEXT)
        if not handle:
            return None
        pointer = kernel32.GlobalLock(handle)
        if not pointer:
            return None
        try:
            size = kernel32.GlobalSize(handle)
            raw = ctypes.string_at(pointer, size)
        finally:
            kernel32.GlobalUnlock(handle)
        text = raw.decode("utf-16le", errors="replace")
        nul = text.find("\x00")
        return text[:nul] if nul != -1 else text
    finally:
        user32.CloseClipboard()


def _heartbeat_payload(
    pid: int, started: str, last_event_ts: str | None, events: int, last_slug: str | None
) -> dict[str, Any]:
    return {
        "pid": pid,
        "started": started,
        "last_event_ts": last_event_ts,
        "events": events,
        "last_slug": last_slug,
    }


def _dispatch(
    text: str | None,
    state_dir: Path,
    vault_root: Path,
    *,
    isle: Callable[..., dict[str, Any]],
) -> dict[str, Any] | None:
    """Hand relevant clipboard text to ``pasaport_kapi`` in-process.
    ``None`` when the text carries no ODENA marker — nothing was dispatched.
    """
    if not _metin_ilgili(text):
        return None
    return isle(text, state_dir, vault_root)


class Izleyici:
    """Mutable per-run state (last clipboard sequence number, event count,
    heartbeat fields) plus the pure decisions above. All Win32 I/O lives in
    ``run()``; everything else here is plain data, so a test drives
    ``bir_kere()``/``isle_ve_kaydet()`` directly with fakes — no window, no
    real clipboard, no ``os.name`` mocking required.
    """

    def __init__(
        self,
        state_dir: Path,
        vault_root: Path,
        *,
        isle: Callable[..., dict[str, Any]] | None = None,
        oku: Callable[[], str | None] | None = None,
        simdi: Callable[[], str] | None = None,
    ):
        self.state_dir = Path(state_dir)
        self.vault_root = Path(vault_root)
        self._isle = isle or pasaport_kapi.isle_metin
        self._oku = oku
        self._simdi = simdi or (
            lambda: dt.datetime.now().astimezone().isoformat(timespec="seconds")
        )
        self.son_sira: int | None = None
        self.olay_sayisi = 0
        self.son_olay_ts: str | None = None
        self.son_slug: str | None = None
        self.pid = os.getpid()
        self.baslangic = self._simdi()

    def heartbeat_payload(self) -> dict[str, Any]:
        return _heartbeat_payload(
            self.pid, self.baslangic, self.son_olay_ts, self.olay_sayisi, self.son_slug
        )

    def heartbeat_yaz(self) -> None:
        try:
            _atomic_write_json(self.state_dir / HEARTBEAT_NAME, self.heartbeat_payload())
        except OSError:
            pass

    def clipboard_guncellendi(self, sira: int) -> bool:
        """Sequence-skip logic: ``True`` (and records ``sira``) only when it
        actually differs from the last one seen — an unrelated
        ``WM_CLIPBOARDUPDATE`` (or the very first one, whose baseline this
        call establishes without a read) is treated as "already seen"."""
        if self.son_sira is not None and sira == self.son_sira:
            return False
        self.son_sira = sira
        return True

    def isle_ve_kaydet(self, text: str | None) -> dict[str, Any] | None:
        sonuc = _dispatch(text, self.state_dir, self.vault_root, isle=self._isle)
        if sonuc is None:
            return None
        self.olay_sayisi += 1
        self.son_olay_ts = self._simdi()
        self.son_slug = sonuc.get("hata") or ("bekleyen" if sonuc.get("bekleyen") else "istek")
        self.heartbeat_yaz()
        return sonuc

    def bir_kere(self) -> dict[str, Any] | None:
        """``--once`` mode, and the panel's "Panodan al" fallback: read the
        clipboard a single time and process it if relevant."""
        okuyucu = self._oku or (lambda: _clipboard_metni_oku(0))
        text = okuyucu()
        result = self.isle_ve_kaydet(text)
        self.heartbeat_yaz()
        return result

    def run(self) -> int:
        """The real Windows event loop: a message-only window,
        ``AddClipboardFormatListener``, and a blocking ``GetMessageW`` loop —
        no polling. Returns a process exit code."""
        if os.name != "nt":
            print(DESTEKLENMIYOR_SLUG, file=sys.stderr)
            return 2
        nezaket.kendini_arka_plana_al()
        _ensure_win32_baglama()
        user32 = ctypes.windll.user32  # type: ignore[attr-defined]
        kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]

        @WNDPROC
        def _wndproc(hwnd, message, wparam, lparam):  # pragma: no cover — Windows-only
            if message == WM_CLIPBOARDUPDATE:
                sira = user32.GetClipboardSequenceNumber()
                if self.clipboard_guncellendi(sira):
                    text = _clipboard_metni_oku(hwnd)
                    self.isle_ve_kaydet(text)
                return 0
            if message == WM_TIMER:
                self.heartbeat_yaz()
                return 0
            if message == WM_CLOSE:
                user32.DestroyWindow(hwnd)
                return 0
            if message == WM_DESTROY:
                user32.KillTimer(hwnd, HEARTBEAT_TIMER_ID)
                user32.RemoveClipboardFormatListener(hwnd)
                user32.PostQuitMessage(0)
                return 0
            return user32.DefWindowProcW(hwnd, message, wparam, lparam)

        instance = kernel32.GetModuleHandleW(None)
        class_name = "OomPasaportIzleyici"
        wndclass = _WNDCLASSEXW()
        wndclass.cbSize = ctypes.sizeof(_WNDCLASSEXW)
        wndclass.lpfnWndProc = _wndproc
        wndclass.hInstance = instance
        wndclass.lpszClassName = class_name
        if not user32.RegisterClassExW(ctypes.byref(wndclass)):
            return 1
        hwnd = user32.CreateWindowExW(
            0, class_name, class_name, 0, 0, 0, 0, 0, HWND_MESSAGE, None, instance, None
        )
        if not hwnd:
            return 1
        if not user32.AddClipboardFormatListener(hwnd):
            return 1
        user32.SetTimer(hwnd, HEARTBEAT_TIMER_ID, HEARTBEAT_INTERVAL_MS, None)
        self.heartbeat_yaz()

        message = _MSG()
        while True:
            result = user32.GetMessageW(ctypes.byref(message), None, 0, 0)
            if result <= 0:
                break
            user32.TranslateMessage(ctypes.byref(message))
            user32.DispatchMessageW(ctypes.byref(message))
        return 0


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state-dir", type=Path, default=STATE_DIR)
    parser.add_argument("--vault-root", type=Path, default=VAULT_ROOT)
    parser.add_argument(
        "--once", action="store_true", help="Read the clipboard once, process, and exit."
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = _parse_args(argv)
    except SystemExit as exc:
        return exc.code if isinstance(exc.code, int) else 2
    if os.name != "nt":
        print(DESTEKLENMIYOR_SLUG, file=sys.stderr)
        return 2
    try:
        args.state_dir.mkdir(parents=True, exist_ok=True)
    except OSError:
        pass
    izleyici = Izleyici(args.state_dir, args.vault_root)
    if args.once:
        izleyici.bir_kere()
        return 0
    return izleyici.run()


if __name__ == "__main__":
    raise SystemExit(main())
