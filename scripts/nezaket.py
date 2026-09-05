#!/usr/bin/env python3
# yazan: claude · model: sonnet
"""A7 nezaket (politeness) layer: defer background model work while busy.

Windows-only signal probes (foreground process, its parent, GPU load,
fullscreen state, idle time) feed a pure decision function.  Everything here
is fail-open: an unavailable or exception-raising probe reports ``None``
rather than crashing the caller, and an unknown signal never gets treated as
"busy" — only positive evidence (an allow-listed process, a busy GPU, a
fullscreen game) blocks a background run.  On non-Windows platforms every
probe is a no-op that returns ``None`` immediately.

The release valve is explicit only: deferred operations sit in a queue file
until a human (through ``serbest`` here, or the panel's "Selected: run" button)
releases them by id.  There is no automatic release and no night window.
"""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import datetime as dt
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.request
import uuid
from dataclasses import dataclass
from typing import Any, Callable, Sequence

from beyin_ortak import _atomic_write_json, _lock_exclusive, write_health, write_health_skip


SCRIPT_DIR = Path(__file__).resolve().parent
STATE_DIR = SCRIPT_DIR / ".state"

IZIN_NAME = "nezaket-izin.json"
KUYRUK_NAME = "nezaket-kuyruk.json"
DURUM_NAME = "nezaket-durum.json"

EX_TEMPFAIL = 75

IZIN_BOZUK_SLUG = "nezaket-izin-bozuk"
NEZAKET_ERTELENDI_SLUG = "nezaket-ertelendi"
NEZAKET_BILINMIYOR_SLUG = "nezaket-bilinmiyor"
NEZAKET_KAPALI_ENV = "BEYIN_NEZAKET"
NEZAKET_ARKAPLAN_ENV = "BEYIN_NEZAKET_ARKAPLAN"
NEZAKET_ONBELLEK_ENV = "BEYIN_NEZAKET_ONBELLEK_SN"
NEZAKET_DEL_FLAG = "--nezaket-del"

# durum --json is polled by the panel every 10 s and each probe spawns
# nvidia-smi plus walks the whole process table; a cached Okuma this young
# is reused instead of probing again. `sonda` never consults this — it always
# probes fresh, on purpose, since its whole job is to show live probe timing.
DEFAULT_ONBELLEK_SN = 8.0
OKUMA_ONBELLEK_KEY = "okuma_onbellek"
OKUMA_ONBELLEK_TS_KEY = "okuma_onbellek_ts"

# Mirrors ollama_runner.DEFAULT_URL; kept independent so this module never has
# to import a runner (no cycle either way, but the seam stays one-directional).
DEFAULT_OLLAMA_URL = "http://localhost:11434"

DEFAULT_GPU_ESIK = 60
DEFAULT_BOSTA_SERBEST_SN = 900.0
# Overrides gpu_esik (from nezaket-izin.json or the built-in default) without
# touching that file — a quick knob for a machine whose GPU baseline differs.
# Unset changes nothing: the documented default stays 60.
GPU_ESIK_ENV = "BEYIN_NEZAKET_GPU_ESIK"


def _gpu_esik_env_override(environment: dict[str, str] | None = None) -> int | None:
    env = os.environ if environment is None else environment
    raw = (env.get(GPU_ESIK_ENV) or "").strip()
    if not raw:
        return None
    try:
        value = int(raw)
    except ValueError:
        return None
    return value if value > 0 else None

# Dört sınıf: canlı düzenleyici, 3D/render, previs, kayıt — hepsi meşguliyeti
# doğrudan işaret eder; kullanıcı nezaket-izin.json ile genişletebilir.
DEFAULT_SURECLER = (
    "UnrealEditor.exe",
    "UnrealEditor-Cmd.exe",
    "UE4Editor.exe",
    "blender.exe",
    "3dsmax.exe",
    "Substance Painter.exe",
    "Adobe Substance 3D Painter.exe",
    "Substance Designer.exe",
    "Adobe Substance 3D Designer.exe",
    "maya.exe",
    "obs64.exe",
    "obs32.exe",
)
DEFAULT_UST_SURECLER = ("steam.exe", "EpicGamesLauncher.exe")
DEFAULT_ZARARSIZ_TAM_EKRAN = (
    "chrome.exe",
    "msedge.exe",
    "firefox.exe",
    "vlc.exe",
    "mpv.exe",
    "wmplayer.exe",
)


# --------------------------------------------------------------------------
# Windows-only raw probes.  Each is a thin ctypes call; every caller-facing
# wrapper below (SinyalKaynagi's methods) swallows any exception into None.
#
# The structures below are pure ctypes definitions — safe to build on any
# platform — but every function that actually CALLS a WinAPI routine goes
# through ``_ensure_win32_baglama()`` first, which sets explicit
# ``argtypes``/``restype`` on every entry point this module uses. Without
# that, ctypes assumes an untyped foreign function returns a 32-bit signed
# ``int`` — which silently truncates/misreads a 64-bit ``HANDLE`` or, worse,
# ``GetTickCount64``'s millisecond counter (the exact bug that made idle
# detection go quietly wrong after ~24.8 days of uptime, once the c_int
# rollover point was reached).
# --------------------------------------------------------------------------


class _RECT(ctypes.Structure):
    _fields_ = [
        ("left", ctypes.c_long),
        ("top", ctypes.c_long),
        ("right", ctypes.c_long),
        ("bottom", ctypes.c_long),
    ]


class _MONITORINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_uint32),
        ("rcMonitor", _RECT),
        ("rcWork", _RECT),
        ("dwFlags", ctypes.c_uint32),
    ]


class _LASTINPUTINFO(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32), ("dwTime", ctypes.c_uint32)]


class _PROCESSENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", ctypes.c_uint32),
        ("cntUsage", ctypes.c_uint32),
        ("th32ProcessID", ctypes.c_uint32),
        ("th32DefaultHeapID", ctypes.c_void_p),
        ("th32ModuleID", ctypes.c_uint32),
        ("cntThreads", ctypes.c_uint32),
        ("th32ParentProcessID", ctypes.c_uint32),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", ctypes.c_uint32),
        ("szExeFile", ctypes.c_char * 260),
    ]


TH32CS_SNAPPROCESS = 0x00000002
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
MONITOR_DEFAULTTONEAREST = 2


def _win32_baglama(user32: Any = None, kernel32: Any = None) -> None:
    """Bind ``argtypes``/``restype`` on every raw WinAPI call this module
    makes.

    Testable off Windows: pass fake ``user32``/``kernel32`` namespace objects
    (e.g. two ``unittest.mock.MagicMock()``) and inspect the configured
    ``.argtypes``/``.restype`` afterwards. With no arguments it binds the
    real ``ctypes.windll`` tables, which only exist on Windows.
    """
    if user32 is None:
        user32 = ctypes.windll.user32  # type: ignore[attr-defined]
    if kernel32 is None:
        kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]

    user32.GetForegroundWindow.argtypes = []
    user32.GetForegroundWindow.restype = wintypes.HWND

    user32.GetWindowThreadProcessId.argtypes = [
        wintypes.HWND,
        ctypes.POINTER(wintypes.DWORD),
    ]
    user32.GetWindowThreadProcessId.restype = wintypes.DWORD

    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE

    kernel32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        wintypes.LPWSTR,
        ctypes.POINTER(wintypes.DWORD),
    ]
    kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL

    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL

    kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE

    kernel32.Process32First.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(_PROCESSENTRY32),
    ]
    kernel32.Process32First.restype = wintypes.BOOL

    kernel32.Process32Next.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(_PROCESSENTRY32),
    ]
    kernel32.Process32Next.restype = wintypes.BOOL

    user32.MonitorFromWindow.argtypes = [wintypes.HWND, wintypes.DWORD]
    user32.MonitorFromWindow.restype = wintypes.HMONITOR

    user32.GetMonitorInfoW.argtypes = [
        wintypes.HMONITOR,
        ctypes.POINTER(_MONITORINFO),
    ]
    user32.GetMonitorInfoW.restype = wintypes.BOOL

    user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(_RECT)]
    user32.GetWindowRect.restype = wintypes.BOOL

    user32.GetLastInputInfo.argtypes = [ctypes.POINTER(_LASTINPUTINFO)]
    user32.GetLastInputInfo.restype = wintypes.BOOL

    # GetTickCount64 returns ULONGLONG (64-bit milliseconds since boot). Left
    # untyped, ctypes assumes c_int (32-bit signed) and the value silently
    # wraps/garbles once uptime crosses that range — idle detection would
    # then compare against a corrupted tick count forever, with no error.
    kernel32.GetTickCount64.argtypes = []
    kernel32.GetTickCount64.restype = ctypes.c_uint64

    kernel32.GetCurrentProcess.argtypes = []
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE

    kernel32.SetPriorityClass.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel32.SetPriorityClass.restype = wintypes.BOOL


_win32_bound = False


def _ensure_win32_baglama() -> None:
    """Bind once per process; a no-op off Windows and on every later call."""
    global _win32_bound
    if _win32_bound or os.name != "nt":
        return
    _win32_baglama()
    _win32_bound = True


def _is_invalid_handle(handle: int | None) -> bool:
    """True for a null or ``INVALID_HANDLE_VALUE`` HANDLE.

    With ``restype`` correctly declared as ``HANDLE`` (``c_void_p``), ctypes
    reports a NULL handle as ``None`` — and ``INVALID_HANDLE_VALUE`` (the
    all-ones bit pattern some WinAPI calls, e.g. ``CreateToolhelp32Snapshot``,
    return on failure instead of NULL) as a large positive int, not ``-1`` and
    not ``None``. A caller must treat both as failure.
    """
    if handle is None:
        return True
    return handle == ctypes.c_void_p(-1).value


def _foreground_window_pid() -> int | None:
    _ensure_win32_baglama()
    user32 = ctypes.windll.user32  # type: ignore[attr-defined]
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return None
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return int(pid.value) or None


def _process_image_name(pid: int) -> str | None:
    _ensure_win32_baglama()
    kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if _is_invalid_handle(handle):
        return None
    try:
        size = wintypes.DWORD(260)
        buffer = ctypes.create_unicode_buffer(size.value)
        ok = kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size))
        if not ok:
            return None
        path = buffer.value
        if not path:
            return None
        return path.rsplit("\\", 1)[-1]
    finally:
        kernel32.CloseHandle(handle)


def _process_snapshot() -> dict[int, tuple[int, str]]:
    """pid -> (parent_pid, exe_name), read once via CreateToolhelp32Snapshot."""
    _ensure_win32_baglama()
    kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
    snapshot = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if _is_invalid_handle(snapshot):
        return {}
    table: dict[int, tuple[int, str]] = {}
    try:
        entry = _PROCESSENTRY32()
        entry.dwSize = ctypes.sizeof(_PROCESSENTRY32)
        if not kernel32.Process32First(snapshot, ctypes.byref(entry)):
            return {}
        while True:
            name = entry.szExeFile.decode("mbcs", errors="replace")
            table[int(entry.th32ProcessID)] = (int(entry.th32ParentProcessID), name)
            if not kernel32.Process32Next(snapshot, ctypes.byref(entry)):
                break
    finally:
        kernel32.CloseHandle(snapshot)
    return table


def _parent_process_name(pid: int) -> str | None:
    table = _process_snapshot()
    entry = table.get(pid)
    if entry is None:
        return None
    parent_pid, _own_name = entry
    parent = table.get(parent_pid)
    if parent is None:
        return None
    return parent[1]


def _nvidia_gpu_utilization() -> int | None:
    executable = shutil.which("nvidia-smi")
    if not executable:
        return None
    completed = subprocess.run(
        [executable, "--query-gpu=utilization.gpu", "--format=csv,noheader,nounits"],
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=3,
    )
    values: list[int] = []
    for line in completed.stdout.splitlines():
        raw = line.strip().split(",")[0].strip() if line.strip() else ""
        try:
            values.append(int(float(raw)))
        except ValueError:
            continue
    return max(values) if values else None


def _foreground_is_fullscreen() -> bool | None:
    _ensure_win32_baglama()
    user32 = ctypes.windll.user32  # type: ignore[attr-defined]
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return None
    rect = _RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        return None
    monitor = user32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
    if not monitor:
        return None
    info = _MONITORINFO()
    info.cbSize = ctypes.sizeof(_MONITORINFO)
    if not user32.GetMonitorInfoW(monitor, ctypes.byref(info)):
        return None
    mon = info.rcMonitor
    return (rect.left, rect.top, rect.right, rect.bottom) == (
        mon.left,
        mon.top,
        mon.right,
        mon.bottom,
    )


def _idle_seconds() -> float | None:
    _ensure_win32_baglama()
    user32 = ctypes.windll.user32  # type: ignore[attr-defined]
    kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
    info = _LASTINPUTINFO()
    info.cbSize = ctypes.sizeof(_LASTINPUTINFO)
    if not user32.GetLastInputInfo(ctypes.byref(info)):
        return None
    tick = kernel32.GetTickCount64()
    idle_ms = tick - info.dwTime
    if idle_ms < 0:
        return None
    return idle_ms / 1000.0


class SinyalKaynagi:
    """Cheap Windows-only signal probes.

    Every method returns ``None`` instead of raising — both off Windows and on
    any probe failure on Windows itself. Fail-open, but every ``None`` is a
    real return value the caller can see and log, not a swallowed exception.
    """

    def on_plan_surec(self) -> str | None:
        if os.name != "nt":
            return None
        try:
            pid = _foreground_window_pid()
            if pid is None:
                return None
            return _process_image_name(pid)
        except Exception:
            return None

    def ust_surec(self, pid: int | None) -> str | None:
        if os.name != "nt" or pid is None:
            return None
        try:
            return _parent_process_name(pid)
        except Exception:
            return None

    def gpu_yuk(self) -> int | None:
        if os.name != "nt":
            return None
        try:
            return _nvidia_gpu_utilization()
        except Exception:
            return None

    def tam_ekran_mi(self) -> bool | None:
        if os.name != "nt":
            return None
        try:
            return _foreground_is_fullscreen()
        except Exception:
            return None

    def bosta_saniye(self) -> float | None:
        if os.name != "nt":
            return None
        try:
            return _idle_seconds()
        except Exception:
            return None


@dataclass(frozen=True)
class Okuma:
    surec: str | None
    ust_surec: str | None
    gpu: int | None
    tam_ekran: bool | None
    bosta_sn: float | None


@dataclass(frozen=True)
class Karar:
    mesgul: bool
    neden: str
    bilinmiyor: bool = False


def _oku_gercek(kaynak: SinyalKaynagi | None = None) -> Okuma:
    """Read every signal through one ``SinyalKaynagi`` instance."""
    kaynak = kaynak or SinyalKaynagi()
    pid: int | None = None
    if os.name == "nt":
        try:
            pid = _foreground_window_pid()
        except Exception:
            pid = None
    return Okuma(
        surec=kaynak.on_plan_surec(),
        ust_surec=kaynak.ust_surec(pid),
        gpu=kaynak.gpu_yuk(),
        tam_ekran=kaynak.tam_ekran_mi(),
        bosta_sn=kaynak.bosta_saniye(),
    )


def karar(okuma: Okuma, izin: "IzinListesi") -> Karar:
    """Pure decision function — no I/O, no probes, no clock.

    Priority order: an allow-listed process, then an allow-listed parent
    process, then this system's own Ollama activity, then GPU load, then the
    fullscreen heuristic, then "every signal is unknown", then the
    idle-release window, then the default free state. Idle time is checked
    last on purpose — it can never override a busy verdict reached above it,
    so a compiling Unreal Editor stays "busy" no matter how long the keyboard
    has been quiet.
    """
    surec_cf = okuma.surec.casefold() if okuma.surec else None
    ust_cf = okuma.ust_surec.casefold() if okuma.ust_surec else None

    if surec_cf is not None and surec_cf in izin.surecler:
        return Karar(mesgul=True, neden=f"izinli-surec:{okuma.surec}", bilinmiyor=False)

    if ust_cf is not None and ust_cf in izin.ust_surecler:
        return Karar(mesgul=True, neden=f"ust-surec:{okuma.ust_surec}", bilinmiyor=False)

    # A local Ollama server (its own foreground console, or a child process
    # of one) driving the GPU is this pipeline's own inference, not a game —
    # never defer compile for it. Checked before the GPU-load heuristic on
    # purpose, since that heuristic is exactly what used to misread it.
    if (surec_cf is not None and "ollama" in surec_cf) or (
        ust_cf is not None and "ollama" in ust_cf
    ):
        return Karar(mesgul=False, neden="kendi-yerel-model", bilinmiyor=False)

    if okuma.gpu is not None and okuma.gpu >= izin.gpu_esik:
        return Karar(mesgul=True, neden=f"gpu-yuk:{okuma.gpu}", bilinmiyor=False)

    if okuma.tam_ekran is True:
        if surec_cf is not None and surec_cf in izin.zararsiz_tam_ekran:
            return Karar(
                mesgul=False, neden=f"tam-ekran-zararsiz:{okuma.surec}", bilinmiyor=False
            )
        return Karar(mesgul=True, neden="oyun-sezgisi", bilinmiyor=False)

    if (
        okuma.surec is None
        and okuma.ust_surec is None
        and okuma.gpu is None
        and okuma.tam_ekran is None
        and okuma.bosta_sn is None
    ):
        return Karar(mesgul=False, neden="sinyal-yok", bilinmiyor=True)

    if okuma.bosta_sn is not None and okuma.bosta_sn >= izin.bosta_serbest_sn:
        return Karar(mesgul=False, neden="bosta", bilinmiyor=False)

    return Karar(mesgul=False, neden="serbest", bilinmiyor=False)


# --------------------------------------------------------------------------
# nezaket-izin.json
# --------------------------------------------------------------------------


class IzinBozukHata(ValueError):
    """nezaket-izin.json exists but does not parse as the documented shape."""


def _string_list(value: Any, key: str) -> list[str]:
    if not isinstance(value, list) or not all(isinstance(item, str) for item in value):
        raise TypeError(f"{key} bir metin listesi olmalı")
    return value


def _number(value: Any, key: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError(f"{key} bir sayı olmalı")
    return float(value)


@dataclass(frozen=True)
class IzinListesi:
    surecler: frozenset[str]
    ust_surecler: frozenset[str]
    gpu_esik: int
    zararsiz_tam_ekran: frozenset[str]
    bosta_serbest_sn: float

    @classmethod
    def varsayilan(cls) -> "IzinListesi":
        return cls(
            surecler=frozenset(name.casefold() for name in DEFAULT_SURECLER),
            ust_surecler=frozenset(name.casefold() for name in DEFAULT_UST_SURECLER),
            gpu_esik=_gpu_esik_env_override() or DEFAULT_GPU_ESIK,
            zararsiz_tam_ekran=frozenset(
                name.casefold() for name in DEFAULT_ZARARSIZ_TAM_EKRAN
            ),
            bosta_serbest_sn=DEFAULT_BOSTA_SERBEST_SN,
        )

    @classmethod
    def yukle(cls, state_dir: Path) -> "IzinListesi":
        path = Path(state_dir) / IZIN_NAME
        if not path.exists():
            return cls.varsayilan()
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError, json.JSONDecodeError) as exc:
            raise IzinBozukHata(f"{IZIN_BOZUK_SLUG}: {exc}") from exc
        if not isinstance(raw, dict):
            raise IzinBozukHata(f"{IZIN_BOZUK_SLUG}: kök bir JSON nesnesi olmalı")
        try:
            surecler = _string_list(raw.get("surecler", list(DEFAULT_SURECLER)), "surecler")
            ust_surecler = _string_list(
                raw.get("ust_surecler", list(DEFAULT_UST_SURECLER)), "ust_surecler"
            )
            zararsiz = _string_list(
                raw.get("zararsiz_tam_ekran", list(DEFAULT_ZARARSIZ_TAM_EKRAN)),
                "zararsiz_tam_ekran",
            )
            gpu_esik = _number(raw.get("gpu_esik", DEFAULT_GPU_ESIK), "gpu_esik")
            bosta_serbest_sn = _number(
                raw.get("bosta_serbest_sn", DEFAULT_BOSTA_SERBEST_SN), "bosta_serbest_sn"
            )
        except TypeError as exc:
            raise IzinBozukHata(f"{IZIN_BOZUK_SLUG}: {exc}") from exc
        return cls(
            surecler=frozenset(name.casefold() for name in surecler),
            ust_surecler=frozenset(name.casefold() for name in ust_surecler),
            gpu_esik=_gpu_esik_env_override() or int(gpu_esik),
            zararsiz_tam_ekran=frozenset(name.casefold() for name in zararsiz),
            bosta_serbest_sn=float(bosta_serbest_sn),
        )

    @staticmethod
    def varsayilan_yaz(path: Path) -> None:
        payload = {
            "surecler": list(DEFAULT_SURECLER),
            "ust_surecler": list(DEFAULT_UST_SURECLER),
            "gpu_esik": DEFAULT_GPU_ESIK,
            "zararsiz_tam_ekran": list(DEFAULT_ZARARSIZ_TAM_EKRAN),
            "bosta_serbest_sn": DEFAULT_BOSTA_SERBEST_SN,
        }
        _atomic_write_json(Path(path), payload)


# --------------------------------------------------------------------------
# nezaket-kuyruk.json — the deferred-operation queue
# --------------------------------------------------------------------------


class Kuyruk:
    """Deferred-operation queue: released only by explicit id, never by a clock."""

    def __init__(self, state_dir: Path):
        self.state_dir = Path(state_dir)
        self.path = self.state_dir / KUYRUK_NAME
        self.lock_path = self.state_dir / f".{KUYRUK_NAME}.lock"

    def _oku(self) -> list[dict[str, Any]]:
        if not self.path.exists():
            return []
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError, json.JSONDecodeError):
            return []
        records = raw.get("kayitlar") if isinstance(raw, dict) else None
        return [item for item in records if isinstance(item, dict)] if isinstance(records, list) else []

    def _yaz(self, records: list[dict[str, Any]]) -> None:
        _atomic_write_json(self.path, {"kayitlar": records})

    def _kilitli(self, blocking: bool = True):
        self.state_dir.mkdir(parents=True, exist_ok=True)
        lock_file = self.lock_path.open("a+", encoding="utf-8")
        _lock_exclusive(lock_file, blocking=blocking)
        return lock_file

    def ekle(self, tur: str, argv: Sequence[str], neden: str) -> dict[str, Any]:
        """Append one deferred call, deduped on (tur, argv)."""
        argv_list = list(argv)
        lock_file = self._kilitli()
        try:
            records = self._oku()
            for record in records:
                if record.get("tur") == tur and record.get("argv") == argv_list:
                    return record
            record = {
                "id": uuid.uuid4().hex[:12],
                "tur": tur,
                "argv": argv_list,
                "eklendi": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
                "neden": neden,
            }
            records.append(record)
            self._yaz(records)
            return record
        finally:
            lock_file.close()

    def listele(self) -> list[dict[str, Any]]:
        lock_file = self._kilitli()
        try:
            return self._oku()
        finally:
            lock_file.close()

    def en_eski_yas_sn(self, now: float | None = None) -> float | None:
        records = self.listele()
        if not records:
            return None
        current = time.time() if now is None else now
        oldest: float | None = None
        for record in records:
            try:
                when = dt.datetime.fromisoformat(str(record.get("eklendi")))
            except ValueError:
                continue
            epoch = when.timestamp()
            if oldest is None or epoch < oldest:
                oldest = epoch
        if oldest is None:
            return None
        return max(0.0, current - oldest)

    def _pop(self, ids: Sequence[str]) -> list[dict[str, Any]]:
        wanted = set(ids)
        if not wanted:
            return []
        lock_file = self._kilitli()
        try:
            records = self._oku()
            released = [record for record in records if record.get("id") in wanted]
            remaining = [record for record in records if record.get("id") not in wanted]
            if released:
                self._yaz(remaining)
            return released
        finally:
            lock_file.close()

    def serbest(self, ids: Sequence[str]) -> list[dict[str, Any]]:
        """Pop exactly the given ids — the only way records leave this queue
        by intent to run them. There is no code path that pops without ids."""
        return self._pop(ids)

    def kaldir(self, ids: Sequence[str]) -> list[dict[str, Any]]:
        """Pop and discard the given ids — the operation will not run."""
        return self._pop(ids)


# --------------------------------------------------------------------------
# nezaket-durum.json — last decision, for transition detection and
# ``son_karar`` (used by the Ollama keep_alive integration).
# --------------------------------------------------------------------------


class Durum:
    def __init__(self, state_dir: Path):
        self.state_dir = Path(state_dir)
        self.path = self.state_dir / DURUM_NAME
        self.lock_path = self.state_dir / f".{DURUM_NAME}.lock"

    def _kilitli(self, blocking: bool = True):
        self.state_dir.mkdir(parents=True, exist_ok=True)
        lock_file = self.lock_path.open("a+", encoding="utf-8")
        _lock_exclusive(lock_file, blocking=blocking)
        return lock_file

    def _oku_ic(self) -> dict[str, Any]:
        """Read without locking — for a caller that already holds the lock."""
        if not self.path.exists():
            return {}
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError, json.JSONDecodeError):
            return {}
        return raw if isinstance(raw, dict) else {}

    def oku(self) -> dict[str, Any]:
        lock_file = self._kilitli()
        try:
            return self._oku_ic()
        finally:
            lock_file.close()

    def guncelle(self, karar_sonucu: Karar) -> str | None:
        lock_file = self._kilitli()
        try:
            onceki = self._oku_ic()
            onceki_mesgul = bool(onceki.get("mesgul"))
            now = dt.datetime.now().astimezone().isoformat(timespec="seconds")
            payload = dict(onceki)
            payload["mesgul"] = karar_sonucu.mesgul
            payload["neden"] = karar_sonucu.neden
            payload["bilinmiyor"] = karar_sonucu.bilinmiyor
            payload["ts"] = now
            gecis: str | None = None
            if karar_sonucu.mesgul and not onceki_mesgul:
                payload["mesgul_basladi"] = now
                gecis = "serbest->mesgul"
            elif not karar_sonucu.mesgul and onceki_mesgul:
                payload.pop("mesgul_basladi", None)
                gecis = "mesgul->serbest"
            _atomic_write_json(self.path, payload)
            return gecis
        finally:
            lock_file.close()

    def onbellekli_okuma(
        self, max_yas_sn: float, *, simdi: Callable[[], float] = time.time
    ) -> Okuma | None:
        """The cached Okuma if one was stored and is younger than
        ``max_yas_sn``; ``None`` on a miss (never present, too old, or
        malformed — every miss just means "probe fresh")."""
        lock_file = self._kilitli()
        try:
            payload = self._oku_ic()
        finally:
            lock_file.close()
        stamped = payload.get(OKUMA_ONBELLEK_TS_KEY)
        cached = payload.get(OKUMA_ONBELLEK_KEY)
        if not isinstance(stamped, (int, float)) or not isinstance(cached, dict):
            return None
        if simdi() - stamped > max_yas_sn:
            return None
        try:
            return Okuma(
                surec=cached.get("surec"),
                ust_surec=cached.get("ust_surec"),
                gpu=cached.get("gpu"),
                tam_ekran=cached.get("tam_ekran"),
                bosta_sn=cached.get("bosta_sn"),
            )
        except TypeError:
            return None

    def onbellek_yaz(self, okuma: Okuma, *, simdi: Callable[[], float] = time.time) -> None:
        """Persist a fresh probe result for ``onbellekli_okuma`` to reuse."""
        lock_file = self._kilitli()
        try:
            payload = self._oku_ic()
            payload[OKUMA_ONBELLEK_KEY] = {
                "surec": okuma.surec,
                "ust_surec": okuma.ust_surec,
                "gpu": okuma.gpu,
                "tam_ekran": okuma.tam_ekran,
                "bosta_sn": okuma.bosta_sn,
            }
            payload[OKUMA_ONBELLEK_TS_KEY] = simdi()
            _atomic_write_json(self.path, payload)
        finally:
            lock_file.close()


def son_karar(state_dir: Path) -> bool | None:
    """Last recorded busy decision, or ``None`` if unknown/unrecorded."""
    try:
        payload = Durum(state_dir).oku()
    except Exception:
        return None
    if "mesgul" not in payload:
        return None
    return bool(payload["mesgul"])


# --------------------------------------------------------------------------
# nezaket-ollama-yukler.json — model names THIS system loaded via
# ollama_runner, so vram_bosalt never evicts a model the user loaded by hand.
# --------------------------------------------------------------------------

OLLAMA_YUKLER_NAME = "nezaket-ollama-yukler.json"


class OllamaYukler:
    """Tracks which Ollama model names this pipeline itself asked to load."""

    def __init__(self, state_dir: Path):
        self.state_dir = Path(state_dir)
        self.path = self.state_dir / OLLAMA_YUKLER_NAME
        self.lock_path = self.state_dir / f".{OLLAMA_YUKLER_NAME}.lock"

    def _kilitli(self, blocking: bool = True):
        self.state_dir.mkdir(parents=True, exist_ok=True)
        lock_file = self.lock_path.open("a+", encoding="utf-8")
        _lock_exclusive(lock_file, blocking=blocking)
        return lock_file

    def _oku(self) -> list[str]:
        if not self.path.exists():
            return []
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError, json.JSONDecodeError):
            return []
        models = raw.get("modeller") if isinstance(raw, dict) else None
        if not isinstance(models, list):
            return []
        return [name for name in models if isinstance(name, str) and name]

    def kaydet(self, model: str) -> None:
        """Remember that this pipeline asked Ollama to load ``model``.

        Called by ``ollama_runner`` right before it issues the generate
        request that makes Ollama load the model — the only place this
        system ever causes a load, so anything not recorded here is the
        user's own, untouched by ``vram_bosalt``.
        """
        if not model:
            return
        lock_file = self._kilitli()
        try:
            models = self._oku()
            if model not in models:
                models.append(model)
                _atomic_write_json(self.path, {"modeller": models})
        finally:
            lock_file.close()

    def yuklenenler(self) -> set[str]:
        lock_file = self._kilitli()
        try:
            return set(self._oku())
        finally:
            lock_file.close()


# --------------------------------------------------------------------------
# VRAM release on the serbest -> mesgul transition (Ç4 keep_alive support)
# --------------------------------------------------------------------------


def vram_bosalt(url: str | None = None, state_dir: Path | None = None) -> list[str]:
    """Ask Ollama to drop VRAM residency for models this system itself loaded.

    With ``state_dir`` given, only models recorded in
    ``nezaket-ollama-yukler.json`` (via ``ollama_runner``) are unloaded — a
    model the user loaded by hand for their own work is left alone. Without
    ``state_dir`` there is no tracked set to consult, so every currently
    loaded model is unloaded (the historical, pre-tracking behaviour).

    Never raises: every failure is folded into the returned problem list.
    """
    base = (url or os.environ.get("BEYIN_OLLAMA_URL") or DEFAULT_OLLAMA_URL).rstrip("/")
    problems: list[str] = []
    izinli: set[str] | None = None
    if state_dir is not None:
        try:
            izinli = OllamaYukler(state_dir).yuklenenler()
        except Exception as exc:
            problems.append(f"yukler-okunamadi:{exc.__class__.__name__}")
            izinli = set()
    try:
        with urllib.request.urlopen(base + "/api/ps", timeout=2) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except Exception as exc:
        problems.append(f"ps-basarisiz:{exc.__class__.__name__}")
        return problems
    models = payload.get("models") if isinstance(payload, dict) else None
    if not isinstance(models, list):
        return problems
    for entry in models:
        name = entry.get("name") if isinstance(entry, dict) else None
        if not isinstance(name, str) or not name:
            continue
        if izinli is not None and name not in izinli:
            continue
        body = json.dumps({"model": name, "keep_alive": 0}).encode("utf-8")
        request = urllib.request.Request(
            base + "/api/generate",
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=2) as response:
                response.read()
        except Exception as exc:
            problems.append(f"bosaltma-basarisiz:{name}:{exc.__class__.__name__}")
    return problems


# --------------------------------------------------------------------------
# Child priority
# --------------------------------------------------------------------------


def dusuk_oncelik_bayraklari() -> int:
    """``subprocess`` creationflags to start a child at idle priority."""
    if sys.platform == "win32":
        return 0x00000040  # IDLE_PRIORITY_CLASS
    return 0


def kendini_arka_plana_al() -> None:
    """Ask Windows to schedule THIS process in background mode.

    No-op off Windows, and opt-out via ``BEYIN_NEZAKET_ARKAPLAN=0``.
    """
    if sys.platform != "win32":
        return
    if (os.environ.get(NEZAKET_ARKAPLAN_ENV) or "1").strip() == "0":
        return
    try:
        _ensure_win32_baglama()
        PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000
        kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
        # GetCurrentProcess() is a pseudo-handle that is ALWAYS the -1 bit
        # pattern by design (not a failure) — it must not go through
        # _is_invalid_handle().
        handle = kernel32.GetCurrentProcess()
        kernel32.SetPriorityClass(handle, PROCESS_MODE_BACKGROUND_BEGIN)
    except Exception:
        pass


# --------------------------------------------------------------------------
# The gate
# --------------------------------------------------------------------------


def _windows_mu() -> bool:
    """A tiny, separately-patchable seam over ``os.name == "nt"``.

    Kept apart from the inline check the probes use so a test can force the
    Windows branch of ``kapi`` without touching the real ``os.name`` — doing
    that directly would make every subsequent ``pathlib.Path()`` call in the
    same block try to build a ``WindowsPath`` and blow up on a POSIX host.
    """
    return os.name == "nt"


def _health_hedefi(tur: str) -> tuple[str, str]:
    """(component, health_name) — the exact store compile/watcher/ingest
    already write their own health failures to, so a gated run shows up in
    the same doctor output as everything else."""
    if tur == "compile":
        return "compile", "health.json"
    if tur in ("watcher", "ingest"):
        return "ingest", "ingest-health.json"
    return "nezaket", "health.json"


def _erken_karar(env: dict[str, str], argv: Sequence[str]) -> Karar | None:
    """The off-switch and ``--nezaket-del`` bypass — shared by ``kapi`` and
    ``mesgul_mu`` and checked before either one probes anything."""
    if (env.get(NEZAKET_KAPALI_ENV) or "").strip().lower() == "off":
        return Karar(mesgul=False, neden="nezaket-kapali", bilinmiyor=False)
    if NEZAKET_DEL_FLAG in argv:
        return Karar(mesgul=False, neden="nezaket-del", bilinmiyor=False)
    return None


def _degerlendir(
    tur: str,
    state_dir: Path,
    env: dict[str, str],
    oku: Callable[[], Okuma] | None,
) -> Karar:
    """Probe, decide, persist to ``Durum``, and release VRAM on a
    serbest->mesgul transition — the part ``kapi`` and ``mesgul_mu`` share.
    Neither the off-switch/``--nezaket-del`` bypass nor the deferred-record
    enqueue lives here; those are each caller's own concern.
    """
    component, health_name = _health_hedefi(tur)
    try:
        izin = IzinListesi.yukle(state_dir)
    except IzinBozukHata as exc:
        write_health(state_dir, f"{IZIN_BOZUK_SLUG}:{exc}", component=component, health_name=health_name)
        return Karar(mesgul=False, neden=IZIN_BOZUK_SLUG, bilinmiyor=True)

    okuma = oku() if oku is not None else _oku_gercek()
    sonuc = karar(okuma, izin)

    durum = Durum(state_dir)
    onceki = durum.oku()
    onceki_bilinmiyor = bool(onceki.get("bilinmiyor"))
    gecis = durum.guncelle(sonuc)

    if gecis == "serbest->mesgul":
        vram_bosalt(env.get("BEYIN_OLLAMA_URL"), state_dir=state_dir)

    if sonuc.bilinmiyor and not onceki_bilinmiyor and _windows_mu():
        # Off Windows every signal is None by design (the probes are
        # Windows-only), so "unknown" is the permanent, expected state there
        # rather than an anomaly — flagging it would just be noise on every
        # single run. On Windows itself it is worth one note per transition.
        write_health(state_dir, NEZAKET_BILINMIYOR_SLUG, warning=True, component=component, health_name=health_name)

    return sonuc


def kapi(
    tur: str,
    argv: Sequence[str],
    state_dir: Path,
    environment: dict[str, str] | None = None,
    oku: Callable[[], Okuma] | None = None,
) -> Karar:
    """The gate every entrypoint calls before any lock or model work.

    Returns the ``Karar``; the caller's contract is to exit 75 when
    ``mesgul`` is true, without spawning anything else first.
    """
    env = os.environ if environment is None else environment
    erken = _erken_karar(env, argv)
    if erken is not None:
        return erken

    sonuc = _degerlendir(tur, state_dir, env, oku)
    if sonuc.mesgul:
        component, health_name = _health_hedefi(tur)
        Kuyruk(state_dir).ekle(tur, argv, sonuc.neden)
        write_health_skip(state_dir, NEZAKET_ERTELENDI_SLUG, component=component, health_name=health_name)
    return sonuc


def mesgul_mu(
    state_dir: Path,
    argv: Sequence[str],
    environment: dict[str, str] | None = None,
    oku: Callable[[], Okuma] | None = None,
) -> Karar:
    """Re-evaluate busy/free without enqueueing anything.

    For a long-running loop (the watcher, past its first sweep) that must
    re-check on every iteration instead of gating once at start-up and then
    never looking again: same probe -> decide -> ``Durum.guncelle`` ->
    ``vram_bosalt``-on-transition pipeline as ``kapi``, honouring the same
    off-switch and ``--nezaket-del`` bypass, but it never calls
    ``Kuyruk.ekle`` — a loop that re-checks every iteration has nothing to
    defer, only a sweep to skip for now. The caller (not this function)
    decides what "only once per transition" means for its own health note.
    """
    env = os.environ if environment is None else environment
    erken = _erken_karar(env, argv)
    if erken is not None:
        return erken
    return _degerlendir("watcher", state_dir, env, oku)


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def _onbellek_sn() -> float:
    raw = os.environ.get(NEZAKET_ONBELLEK_ENV)
    if raw is None:
        return DEFAULT_ONBELLEK_SN
    try:
        deger = float(raw)
    except ValueError:
        return DEFAULT_ONBELLEK_SN
    return deger if deger >= 0 else DEFAULT_ONBELLEK_SN


def _onbellekli_oku_gercek(
    state_dir: Path, *, simdi: Callable[[], float] = time.time
) -> Okuma:
    """Probe, unless a cached reading younger than the configured window is
    already sitting in ``Durum`` — the ``durum --json`` status view is what
    this is for; it is polled by the panel every 10 s and each fresh probe
    spawns nvidia-smi and walks the whole process table. Gating decisions
    (``kapi``/``mesgul_mu``) and ``sonda`` never call this — they always
    probe fresh."""
    durum = Durum(state_dir)
    onbellekteki = durum.onbellekli_okuma(_onbellek_sn(), simdi=simdi)
    if onbellekteki is not None:
        return onbellekteki
    okuma = _oku_gercek()
    durum.onbellek_yaz(okuma, simdi=simdi)
    return okuma


def _durum_ozeti(state_dir: Path) -> dict[str, Any]:
    izin_hatasi: str | None = None
    try:
        izin = IzinListesi.yukle(state_dir)
    except IzinBozukHata as exc:
        izin = IzinListesi.varsayilan()
        izin_hatasi = str(exc)
    okuma = _onbellekli_oku_gercek(state_dir)
    sonuc = karar(okuma, izin)
    kuyruk = Kuyruk(state_dir)
    kayitlar = kuyruk.listele()
    return {
        "mesgul": sonuc.mesgul,
        "neden": sonuc.neden,
        "bilinmiyor": sonuc.bilinmiyor,
        "okuma": {
            "surec": okuma.surec,
            "ust_surec": okuma.ust_surec,
            "gpu": okuma.gpu,
            "tam_ekran": okuma.tam_ekran,
            "bosta_sn": okuma.bosta_sn,
        },
        "kuyruk": {
            "adet": len(kayitlar),
            "en_eski_yas_sn": kuyruk.en_eski_yas_sn(),
            "kayitlar": kayitlar,
        },
        "izin_hatasi": izin_hatasi,
    }


def _cmd_durum(args: argparse.Namespace, state_dir: Path) -> int:
    ozet = _durum_ozeti(state_dir)
    if args.json:
        print(json.dumps(ozet, ensure_ascii=False))
        return 0
    print(("Meşgul" if ozet["mesgul"] else "Serbest") + " — " + ozet["neden"])
    if ozet["bilinmiyor"]:
        print("(sinyaller bilinmiyor)")
    if ozet["izin_hatasi"]:
        print(f"izin dosyası bozuk: {ozet['izin_hatasi']}")
    kuyruk = ozet["kuyruk"]
    print(f"kuyrukta {kuyruk['adet']} kayıt, en eski yaş: {kuyruk['en_eski_yas_sn']}")
    return 0


def _cmd_kuyruk(_args: argparse.Namespace, state_dir: Path) -> int:
    kayitlar = Kuyruk(state_dir).listele()
    print(json.dumps(kayitlar, ensure_ascii=False))
    return 0


def _cmd_serbest(args: argparse.Namespace, state_dir: Path) -> int:
    kayitlar = Kuyruk(state_dir).serbest(args.ids)
    print(json.dumps(kayitlar, ensure_ascii=False))
    return 0


def _cmd_kaldir(args: argparse.Namespace, state_dir: Path) -> int:
    kayitlar = Kuyruk(state_dir).kaldir(args.ids)
    print(json.dumps(kayitlar, ensure_ascii=False))
    return 0


def _cmd_izin_yaz(_args: argparse.Namespace, state_dir: Path) -> int:
    path = state_dir / IZIN_NAME
    IzinListesi.varsayilan_yaz(path)
    print(str(path))
    return 0


def _cmd_sonda(_args: argparse.Namespace, _state_dir: Path) -> int:
    kaynak = SinyalKaynagi()
    pid_start = time.perf_counter()
    pid = _foreground_window_pid() if os.name == "nt" else None
    pid_ms = (time.perf_counter() - pid_start) * 1000
    print(f"foreground_pid: {pid!r} ({pid_ms:.2f} ms)")
    for isim, fonksiyon in (
        ("on_plan_surec", kaynak.on_plan_surec),
        ("ust_surec", lambda: kaynak.ust_surec(pid)),
        ("gpu_yuk", kaynak.gpu_yuk),
        ("tam_ekran_mi", kaynak.tam_ekran_mi),
        ("bosta_saniye", kaynak.bosta_saniye),
    ):
        start = time.perf_counter()
        try:
            deger: Any = fonksiyon()
        except Exception as exc:  # sonda hiçbir zaman çökmemeli
            deger = f"HATA:{exc}"
        sure_ms = (time.perf_counter() - start) * 1000
        print(f"{isim}: {deger!r} ({sure_ms:.2f} ms)")
    return 0


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state-dir", type=Path, default=STATE_DIR)
    sub = parser.add_subparsers(dest="command", required=True)

    durum_parser = sub.add_parser("durum", help="Current decision, signals, and queue.")
    durum_parser.add_argument("--json", action="store_true")
    durum_parser.set_defaults(handler=_cmd_durum)

    kuyruk_parser = sub.add_parser("kuyruk", help="List the deferred-operation queue.")
    kuyruk_parser.set_defaults(handler=_cmd_kuyruk)

    serbest_parser = sub.add_parser("serbest", help="Release queued ids to run.")
    serbest_parser.add_argument("ids", nargs="+")
    serbest_parser.set_defaults(handler=_cmd_serbest)

    kaldir_parser = sub.add_parser("kaldir", help="Discard queued ids without running them.")
    kaldir_parser.add_argument("ids", nargs="+")
    kaldir_parser.set_defaults(handler=_cmd_kaldir)

    izin_parser = sub.add_parser("izin-yaz", help="Write the editable defaults file.")
    izin_parser.set_defaults(handler=_cmd_izin_yaz)

    sonda_parser = sub.add_parser("sonda", help="Raw signal dump with per-probe timing.")
    sonda_parser.set_defaults(handler=_cmd_sonda)

    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = _parse_args(argv)
    except SystemExit as exc:
        return exc.code if isinstance(exc.code, int) else 1
    try:
        return args.handler(args, args.state_dir)
    except IzinBozukHata as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
