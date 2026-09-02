#!/usr/bin/env python3
# yazan: claude · model: sonnet
"""F5 "Kokpit" part 1 — the tower: a stdlib-only multi-lane job manager.

Kule ("the tower") runs `claude`/`codex` CLI jobs as background children,
one lane per running job, capped per kind (`BEYIN_KULE_CLAUDE_TAVAN` /
`BEYIN_KULE_CODEX_TAVAN`). It is a separate, headless component from the
panel — the constitution here is "the panel never computes, it displays":
every number a future panel would show is written to `<state_dir>/kule/durum.json`
by this module, never derived on the fly by a reader.

Job records live at `<state_dir>/kule/jobs/<id>.json`; the prompt (already
redacted via `secret_guard.redact` before it ever touches disk or a model)
at `<id>.prompt`; the child's streamed event log at `<id>.log`. A job that
declares `izlenen_dosyalar` gets a before/after snapshot and a unified diff
per file; a non-empty diff parks the job at `waiting-approval` until a human
calls `onayla`/`reddet`. See `docs/kokpit.md` for the full contract.

Jobs given from the panel (`--kaynak panel`) are explicit user actions, same
as Kaydet — they bypass the A7 nezaket gate (`nezaket_del: true` on every
record) rather than sitting in its deferred queue.
"""

from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import difflib
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from typing import Any, Callable, Sequence

from beyin_ortak import _atomic_write_json, _lock_exclusive, record_call, write_health
import kaydet
import nezaket
import secret_guard


SCRIPT_DIR = Path(__file__).resolve().parent
VAULT_ROOT = SCRIPT_DIR.parent.parent
STATE_DIR = SCRIPT_DIR / ".state"

HEALTH_NAME = "kule-health.json"
DURUM_NAME = "durum.json"

RESULT_MARKER = "KULE-SONUC "

CLAUDE_TAVAN_ENV = "BEYIN_KULE_CLAUDE_TAVAN"
CODEX_TAVAN_ENV = "BEYIN_KULE_CODEX_TAVAN"
DEFAULT_CLAUDE_TAVAN = 3
DEFAULT_CODEX_TAVAN = 4

KULE_TAVAN_ENV = "BEYIN_KULE_TAVAN"
DEFAULT_KULE_TAVAN = 50

ZAMAN_ASIMI_ENV = "BEYIN_KULE_ZAMAN_ASIMI"
DEFAULT_ZAMAN_ASIMI = 3600

ARALIK_ENV = "BEYIN_KULE_ARALIK"
DEFAULT_ARALIK = 2.0

LOG_MAX_BAYT_ENV = "BEYIN_KULE_LOG_MAX_BAYT"
DEFAULT_LOG_MAX_BAYT = 20 * 1024 * 1024

# `izlenen_dosyalar` caps — same posture as the lane/archive caps above: a
# generous default that only ever bites a genuinely malformed or hostile
# request, never a normal one.
MAX_IZLENEN_DOSYA = 50
MAX_IZLENEN_BOYUT = 5 * 1024 * 1024

# `izinler` allowlist — the only keys/values a job record may carry through
# to the spawned `claude`/`codex` child. `bypassPermissions` and
# `danger-full-access` are not typos here: they are the two values this
# allowlist exists specifically to keep out.
IZIN_PERMISSION_MODES = frozenset({"default", "acceptEdits", "plan"})
IZIN_SANDBOX_MODES = frozenset({"read-only", "workspace-write"})
IZIN_ALLOWED_TOOLS_RE = re.compile(r"^[A-Za-z0-9_,() *-]{0,200}$")

# Job durum lifecycle. `TERMINAL_DURUMLAR` are the ones the archive cap counts.
DURUM_DEGERLERI = (
    "queued",
    "running",
    "waiting-approval",
    "succeeded",
    "failed",
    "cancelled",
    "rejected",
)
TERMINAL_DURUMLAR = frozenset({"succeeded", "failed", "cancelled", "rejected"})

MODEL_EKSIK_SLUG = "kule-model-eksik"
CWD_GECERSIZ_SLUG = "kule-cwd-gecersiz"
TUR_BILINMIYOR_SLUG = "kule-tur-bilinmiyor"
WORKER_KAYIP_SLUG = "kule-worker-kayip"
ZAMAN_ASIMI_SLUG = "kule-zaman-asimi"
GECIS_GECERSIZ_SLUG = "kule-gecis-gecersiz"
IS_YOK_SLUG = "kule-is-yok"
CLAUDE_CLI_EKSIK_SLUG = "kule-claude-cli-eksik"
CODEX_CLI_EKSIK_SLUG = "kule-codex-cli-eksik"
BASLATILAMADI_SLUG = "kule-baslatilamadi"
PROMPT_EKSIK_SLUG = "kule-prompt-eksik"
BIRDEN_FAZLA_KAYNAK_SLUG = "kule-birden-fazla-kaynak"
STDIN_HATA_SLUG = "kule-stdin-hata"
DOSYA_HATA_SLUG = "kule-dosya-hata"
IZLENEN_CWD_DISI_SLUG = "kule-izlenen-cwd-disi"
IZLENEN_COK_BUYUK_SLUG = "kule-izlenen-cok-buyuk"
YOL_DISI_SLUG = "kule-yol-disi"
IZIN_GECERSIZ_SLUG = "kule-izin-gecersiz"
LOG_KIRPILDI_SLUG = "kule-log-kirpildi"


# --------------------------------------------------------------------------
# The job store
# --------------------------------------------------------------------------


class Kule:
    """Path calculator for `<state_dir>/kule/` — no I/O of its own."""

    def __init__(self, state_dir: Path):
        self.state_dir = Path(state_dir)
        self.kule_dir = self.state_dir / "kule"
        self.jobs_dir = self.kule_dir / "jobs"
        self.arsiv_dir = self.kule_dir / "arsiv"
        self.claims_dir = self.kule_dir / "claims"
        self.lanes_dir = self.kule_dir / "lanes"
        self.locks_dir = self.kule_dir / "locks"
        self.durum_path = self.kule_dir / DURUM_NAME
        self.dur_path = self.kule_dir / "dur"

    def ensure(self) -> None:
        for path in (
            self.jobs_dir,
            self.arsiv_dir,
            self.claims_dir,
            self.lanes_dir,
            self.locks_dir,
        ):
            path.mkdir(parents=True, exist_ok=True)

    def job_path(self, job_id: str) -> Path:
        return self.jobs_dir / f"{job_id}.json"

    def prompt_path(self, job_id: str) -> Path:
        return self.jobs_dir / f"{job_id}.prompt"

    def log_path(self, job_id: str) -> Path:
        return self.jobs_dir / f"{job_id}.log"

    def iptal_path(self, job_id: str) -> Path:
        return self.jobs_dir / f"{job_id}.iptal"

    def job_workdir(self, job_id: str) -> Path:
        return self.jobs_dir / job_id

    def claim_path(self, job_id: str) -> Path:
        return self.claims_dir / job_id

    def lane_dir(self, tur: str) -> Path:
        return self.lanes_dir / tur

    def lane_path(self, tur: str, job_id: str) -> Path:
        return self.lane_dir(tur) / job_id

    def lock_path(self, job_id: str) -> Path:
        return self.locks_dir / f"{job_id}.lock"


def _now_iso() -> str:
    return dt.datetime.now().astimezone().isoformat(timespec="seconds")


def _load_job_file(path: Path) -> dict[str, Any] | None:
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, json.JSONDecodeError):
        return None
    return raw if isinstance(raw, dict) else None


def _read_job(kule: Kule, job_id: str) -> dict[str, Any] | None:
    """Active (not archived) job record, or `None`."""
    return _load_job_file(kule.job_path(job_id))


def _write_job(kule: Kule, record: dict[str, Any]) -> None:
    _atomic_write_json(kule.job_path(record["id"]), record)


def _find_job(kule: Kule, job_id: str) -> tuple[dict[str, Any] | None, str | None]:
    """`(record, "active"|"arsiv")`, searching active jobs then the archive."""
    path = kule.job_path(job_id)
    if path.is_file():
        return _load_job_file(path), "active"
    arsiv_path = kule.arsiv_dir / f"{job_id}.json"
    if arsiv_path.is_file():
        return _load_job_file(arsiv_path), "arsiv"
    return None, None


def _list_active_jobs(kule: Kule) -> list[dict[str, Any]]:
    if not kule.jobs_dir.exists():
        return []
    jobs: list[dict[str, Any]] = []
    for path in kule.jobs_dir.glob("*.json"):
        job = _load_job_file(path)
        if job is not None:
            jobs.append(job)
    return jobs


def _log_file_path(kule: Kule, job_id: str, location: str) -> Path:
    base = kule.jobs_dir if location == "active" else kule.arsiv_dir
    return base / f"{job_id}.log"


def _tail_log(kule: Kule, job_id: str, location: str, n: int) -> list[str]:
    try:
        text = _log_file_path(kule, job_id, location).read_text(
            encoding="utf-8", errors="replace"
        )
    except OSError:
        return []
    lines = text.splitlines()
    return lines[-n:] if n > 0 else lines


# --------------------------------------------------------------------------
# Environment-tunable knobs — same "unset/junk/non-positive falls back to
# default" convention as kaydet.resolve_max_karakter / nezaket's `_number`.
# --------------------------------------------------------------------------


def _resolve_int_env(name: str, default: int, environment: dict[str, str] | None = None) -> int:
    env = os.environ if environment is None else environment
    raw = (env.get(name) or "").strip()
    if not raw:
        return default
    try:
        value = int(raw)
    except ValueError:
        return default
    return value if value > 0 else default


def _resolve_float_env(
    name: str, default: float, environment: dict[str, str] | None = None
) -> float:
    env = os.environ if environment is None else environment
    raw = (env.get(name) or "").strip()
    if not raw:
        return default
    try:
        value = float(raw)
    except ValueError:
        return default
    return value if value > 0 else default


def _lane_cap(tur: str, environment: dict[str, str] | None = None) -> int:
    if tur == "claude":
        return _resolve_int_env(CLAUDE_TAVAN_ENV, DEFAULT_CLAUDE_TAVAN, environment)
    return _resolve_int_env(CODEX_TAVAN_ENV, DEFAULT_CODEX_TAVAN, environment)


def _kule_tavan(environment: dict[str, str] | None = None) -> int:
    return _resolve_int_env(KULE_TAVAN_ENV, DEFAULT_KULE_TAVAN, environment)


def _zaman_asimi(environment: dict[str, str] | None = None) -> int:
    return _resolve_int_env(ZAMAN_ASIMI_ENV, DEFAULT_ZAMAN_ASIMI, environment)


def _aralik(environment: dict[str, str] | None = None) -> float:
    return _resolve_float_env(ARALIK_ENV, DEFAULT_ARALIK, environment)


def _log_max_bayt(environment: dict[str, str] | None = None) -> int:
    return _resolve_int_env(LOG_MAX_BAYT_ENV, DEFAULT_LOG_MAX_BAYT, environment)


# --------------------------------------------------------------------------
# cwd validation — codex's sandbox cannot read paths under the OS temp root
# (see skills/codex-fleet/SKILL.md, "the sandbox cannot read Windows Temp
# paths"). `_system_temp_root` is its own function so a test can monkeypatch
# it: pytest's own `tmp_path` fixture sits under the real system temp root,
# so a test that wants a *valid* cwd must relocate what counts as "temp".
# --------------------------------------------------------------------------


def _system_temp_root() -> Path:
    return Path(tempfile.gettempdir()).resolve()


def _cwd_gecersiz(cwd: Path) -> bool:
    if not cwd.is_dir():
        return True
    try:
        resolved = cwd.resolve()
        temp_root = _system_temp_root()
        return os.path.commonpath([temp_root, resolved]) == str(temp_root)
    except (OSError, ValueError):
        return True


def _resolve_under(root: Path, candidate: Path) -> Path | None:
    """Resolve `candidate` (absolute, or relative to `root`) and return the
    resolved path only if it is inside `root`'s own resolved (symlink-safe)
    tree; `None` otherwise. Used both for `izlenen_dosyalar` (must stay under
    the job's cwd) and for diff/once/sonra asset paths (must stay under
    `<state>/kule/`)."""
    try:
        root_resolved = root.resolve()
        target = candidate if candidate.is_absolute() else root / candidate
        target_resolved = target.resolve()
    except OSError:
        return None
    try:
        if os.path.commonpath([root_resolved, target_resolved]) != str(root_resolved):
            return None
    except ValueError:
        # commonpath raises on e.g. mixed drives on Windows — different
        # roots entirely, so definitely not contained.
        return None
    return target_resolved


def _validate_izlenen(cwd: Path, izlenen_dosyalar: Sequence[str]) -> str | None:
    """`None` on success, else the slug to refuse the job with. Every
    watched path must resolve (symlink-safe) inside `cwd`; the list is
    capped at `MAX_IZLENEN_DOSYA` entries and each existing file at
    `MAX_IZLENEN_BOYUT` bytes — both caps share `IZLENEN_COK_BUYUK_SLUG`."""
    if len(izlenen_dosyalar) > MAX_IZLENEN_DOSYA:
        return IZLENEN_COK_BUYUK_SLUG
    for rel in izlenen_dosyalar:
        resolved = _resolve_under(cwd, Path(rel))
        if resolved is None:
            return IZLENEN_CWD_DISI_SLUG
        try:
            if resolved.is_file() and resolved.stat().st_size > MAX_IZLENEN_BOYUT:
                return IZLENEN_COK_BUYUK_SLUG
        except OSError:
            pass
    return None


def _validate_izin(izinler: dict[str, Any] | None) -> str | None:
    """`None` on success, else `IZIN_GECERSIZ_SLUG`. Only three keys are
    ever accepted, each with its own closed set/pattern of values — anything
    else (an unknown key, or `permission_mode`/`sandbox` set to something
    like `bypassPermissions`/`danger-full-access`) is refused outright."""
    if not izinler:
        return None
    for key, value in izinler.items():
        if key == "permission_mode":
            if value not in IZIN_PERMISSION_MODES:
                return IZIN_GECERSIZ_SLUG
        elif key == "allowed_tools":
            text = str(value)
            if len(text) > 200 or not IZIN_ALLOWED_TOOLS_RE.match(text):
                return IZIN_GECERSIZ_SLUG
        elif key == "sandbox":
            if value not in IZIN_SANDBOX_MODES:
                return IZIN_GECERSIZ_SLUG
        else:
            return IZIN_GECERSIZ_SLUG
    return None


# --------------------------------------------------------------------------
# Job creation
# --------------------------------------------------------------------------


def create_job(
    kule: Kule,
    *,
    tur: str,
    model: str,
    prompt: str,
    cwd: Path,
    izlenen_dosyalar: Sequence[str] = (),
    izinler: dict[str, Any] | None = None,
    kaynak: str = "cli",
) -> tuple[dict[str, Any] | None, str | None]:
    """Validate and persist a new job. Never raises: every failure is a slug."""
    if tur not in ("claude", "codex"):
        return None, TUR_BILINMIYOR_SLUG
    if not str(model or "").strip():
        return None, MODEL_EKSIK_SLUG
    cwd = Path(cwd)
    if _cwd_gecersiz(cwd):
        return None, CWD_GECERSIZ_SLUG
    izlenen_hata = _validate_izlenen(cwd, izlenen_dosyalar)
    if izlenen_hata is not None:
        return None, izlenen_hata
    izin_hata = _validate_izin(izinler)
    if izin_hata is not None:
        return None, izin_hata

    redacted_prompt, hits = secret_guard.redact(prompt)
    job_id = uuid.uuid4().hex[:12]
    now = _now_iso()
    record: dict[str, Any] = {
        "id": job_id,
        "tur": tur,
        "model": str(model),
        "prompt_sha256": hashlib.sha256(prompt.encode("utf-8")).hexdigest(),
        "prompt_karakter": len(prompt),
        "cwd": cwd.resolve().as_posix(),
        "olusturuldu": now,
        # ISO seconds (above) is the human-facing field; ordering by it alone
        # is not stable within the same wall-clock second (and a directory
        # listing is not insertion-ordered), so every "creation order" sort
        # in this module keys on this nanosecond counter instead.
        "olusturuldu_ns": time.time_ns(),
        "durum": "queued",
        "baslangic": None,
        "bitis": None,
        "sure_sn": None,
        "cikis": None,
        "son_olay": "",
        "olaylar": 0,
        "artefaktlar": [],
        "diffler": [],
        "hata": None,
        "kaynak": kaynak if kaynak in ("panel", "cli") else "cli",
        "nezaket_del": True,
        "izlenen_dosyalar": list(izlenen_dosyalar),
        "izinler": dict(izinler) if izinler else {},
        "uyarilar": (
            [f"warn:secret-redacted-prompt:{','.join(hits)}"] if hits else []
        ),
    }

    kule.ensure()
    kule.prompt_path(job_id).write_text(redacted_prompt, encoding="utf-8", newline="\n")
    _write_job(kule, record)
    _write_durum(kule)
    return record, None


# --------------------------------------------------------------------------
# Claim / lane markers — O_CREAT|O_EXCL, the same claim pattern as
# flush.py's compile-trigger (~L513) and compile.py's machine-id (~L1403).
# --------------------------------------------------------------------------


def _write_marker(path: Path, pid: int) -> bool:
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    except FileExistsError:
        return False
    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(str(pid) + "\n")
        handle.flush()
        os.fsync(handle.fileno())
    return True


def _read_marker_pid(path: Path) -> int | None:
    try:
        raw = path.read_text(encoding="utf-8").strip()
    except OSError:
        return None
    try:
        return int(raw)
    except ValueError:
        return None


def _remove_marker(path: Path) -> None:
    try:
        path.unlink()
    except FileNotFoundError:
        pass
    except OSError:
        pass


def _claim(kule: Kule, job_id: str) -> bool:
    """Exactly one worker may hold this — the exclusivity guarantee."""
    return _write_marker(kule.claim_path(job_id), os.getpid())


def _release_claim(kule: Kule, job_id: str) -> None:
    _remove_marker(kule.claim_path(job_id))


def _lane_claim(kule: Kule, tur: str, job_id: str) -> bool:
    return _write_marker(kule.lane_path(tur, job_id), os.getpid())


def _release_lane(kule: Kule, tur: str, job_id: str) -> None:
    if not tur:
        return
    _remove_marker(kule.lane_path(tur, job_id))


def _lane_count(kule: Kule, tur: str) -> int:
    path = kule.lane_dir(tur)
    if not path.exists():
        return 0
    return sum(1 for _ in path.iterdir())


# --------------------------------------------------------------------------
# pid liveness — POSIX via os.kill(pid, 0); Windows via OpenProcess with
# explicit argtypes/restype (same discipline as nezaket._win32_baglama),
# falling back to `tasklist` if OpenProcess itself is unavailable.
# --------------------------------------------------------------------------


def _pid_alive_tasklist(pid: int) -> bool:
    try:
        result = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}"],
            capture_output=True,
            text=True,
            timeout=5,
            check=False,
        )
        return str(pid) in result.stdout
    except Exception:
        # Fail open: a probe failure must never cause a false reap of a
        # still-running job.
        return True


def _pid_alive_windows(pid: int) -> bool:
    try:
        import ctypes
        from ctypes import wintypes

        kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
        kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel32.CloseHandle.restype = wintypes.BOOL
        kernel32.GetExitCodeProcess.argtypes = [
            wintypes.HANDLE,
            ctypes.POINTER(wintypes.DWORD),
        ]
        kernel32.GetExitCodeProcess.restype = wintypes.BOOL

        process_query_limited_information = 0x1000
        still_active = 259
        handle = kernel32.OpenProcess(process_query_limited_information, False, pid)
        if not handle:
            return _pid_alive_tasklist(pid)
        try:
            code = wintypes.DWORD()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return _pid_alive_tasklist(pid)
            return code.value == still_active
        finally:
            kernel32.CloseHandle(handle)
    except Exception:
        return _pid_alive_tasklist(pid)


def _pid_alive(pid: int | None) -> bool:
    if pid is None or pid <= 0:
        return False
    if os.name == "nt":
        return _pid_alive_windows(pid)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        # Exists, just not signalable by us — still alive.
        return True
    except OSError:
        return False
    return True


# --------------------------------------------------------------------------
# Stale reaper
# --------------------------------------------------------------------------


def _mark_worker_kayip(kule: Kule, job_id: str, tur: str, actions: list[dict[str, Any]]) -> None:
    job = _read_job(kule, job_id)
    if job is not None and job.get("durum") in ("queued", "running"):
        job["durum"] = "failed"
        job["hata"] = WORKER_KAYIP_SLUG
        job["bitis"] = _now_iso()
        _write_job(kule, job)
        actions.append({"id": job_id, "eylem": "worker-kayip", "ts": _now_iso()})
    _release_lane(kule, tur, job_id)
    _release_claim(kule, job_id)


def reap_stale(kule: Kule) -> list[dict[str, Any]]:
    """Remove claim/lane markers whose owning worker process is gone.

    A dead marker's job is moved to ``failed``/``kule-worker-kayip`` only if
    it was still ``queued`` or ``running`` — a marker left behind after the
    job already reached a terminal state (a release that raced the reaper)
    is just cleaned up quietly.
    """
    actions: list[dict[str, Any]] = []
    handled: set[str] = set()

    if kule.claims_dir.exists():
        for claim_file in sorted(kule.claims_dir.iterdir()):
            job_id = claim_file.name
            pid = _read_marker_pid(claim_file)
            if pid is not None and _pid_alive(pid):
                continue
            handled.add(job_id)
            job = _read_job(kule, job_id)
            tur = str(job.get("tur", "")) if job is not None else ""
            _mark_worker_kayip(kule, job_id, tur, actions)

    if kule.lanes_dir.exists():
        for tur_dir in sorted(p for p in kule.lanes_dir.iterdir() if p.is_dir()):
            for lane_file in sorted(tur_dir.iterdir()):
                job_id = lane_file.name
                if job_id in handled:
                    continue
                pid = _read_marker_pid(lane_file)
                if pid is not None and _pid_alive(pid):
                    continue
                _mark_worker_kayip(kule, job_id, tur_dir.name, actions)

    if actions:
        try:
            write_health(
                kule.state_dir,
                "warn:kule-worker-kayip:" + ",".join(a["id"] for a in actions),
                warning=True,
                component="kule",
                health_name=HEALTH_NAME,
            )
        except Exception:
            pass
    return actions


# --------------------------------------------------------------------------
# Diff pipeline (B3)
# --------------------------------------------------------------------------


def _read_watched(cwd: str, rel: str) -> str:
    """Read one watched file's current text — symlink-safe: `create_job`
    already refused anything outside `cwd` at request time, but the world
    can change between then and a job actually running (a symlink swapped
    in after validation, say), so this re-checks containment rather than
    trusting the stored string."""
    src = _resolve_under(Path(cwd), Path(rel))
    if src is None:
        return ""
    try:
        return src.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""


def _snapshot(kule: Kule, job_id: str, izlenen: Sequence[str], phase: str, cwd: str) -> list[Path]:
    paths = []
    for n, rel in enumerate(izlenen):
        text = _read_watched(cwd, rel)
        dest = kule.job_workdir(job_id) / phase / str(n)
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(text, encoding="utf-8", newline="\n")
        paths.append(dest)
    return paths


def _kule_relative(kule: Kule, path: Path) -> str:
    """`path` (always under `<state>/kule/` at the moment this is called —
    a job's diffs are only ever computed while it is still active, i.e.
    under `jobs/`) expressed relative to `<state>/kule/`, e.g.
    `jobs/<id>/diff/0.diff`. Stored instead of an absolute path so archiving
    (which moves the whole `jobs/<id>` directory to `arsiv/<id>`) cannot
    leave the record pointing at a path that no longer exists — see
    `_resolve_kule_yol`, which reattaches this against the job's CURRENT
    location at read time."""
    return path.relative_to(kule.kule_dir).as_posix()


def _resolve_kule_yol(kule: Kule, location: str, job_id: str, stored_rel: str) -> Path | None:
    """Resolve a once/sonra/diff path recorded by `_kule_relative` (or, for
    an absolute path, whatever a tampered record claims) against the job's
    CURRENT location — `jobs/` while active, `arsiv/` once `_archive_job`
    has moved the whole `jobs/<id>` directory in one piece. The stored
    string's own `jobs/<id>`-or-`arsiv/<id>` prefix is never trusted for
    the location, only for recognising the shape: the base directory always
    comes from `location`, which the caller just read the job record out
    of, and `job_id` from the trusted record itself. Returns `None` — the
    caller refuses with `YOL_DISI_SLUG` — for anything that does not end up
    inside `<state>/kule/`, including a tampered absolute path."""
    if not stored_rel:
        return None
    raw = Path(stored_rel)
    base_dir = kule.jobs_dir if location == "active" else kule.arsiv_dir
    if raw.is_absolute():
        candidate = raw
    else:
        parts = raw.parts
        if len(parts) >= 2 and parts[0] in ("jobs", "arsiv") and parts[1] == job_id:
            candidate = base_dir.joinpath(job_id, *parts[2:])
        else:
            candidate = kule.kule_dir / raw
    return _resolve_under(kule.kule_dir, candidate)


def _compute_diffs(
    kule: Kule, job: dict[str, Any], cwd: str
) -> tuple[list[dict[str, Any]], bool]:
    izlenen = job.get("izlenen_dosyalar") or []
    job_id = job["id"]
    sonra_paths = _snapshot(kule, job_id, izlenen, "sonra", cwd)
    diffler: list[dict[str, Any]] = []
    any_nonempty = False
    for n, rel in enumerate(izlenen):
        once_path = kule.job_workdir(job_id) / "once" / str(n)
        sonra_path = sonra_paths[n]
        once_text = once_path.read_text(encoding="utf-8") if once_path.exists() else ""
        sonra_text = sonra_path.read_text(encoding="utf-8") if sonra_path.exists() else ""
        diff_lines = list(
            difflib.unified_diff(
                once_text.splitlines(keepends=True),
                sonra_text.splitlines(keepends=True),
                fromfile=f"{rel} (once)",
                tofile=f"{rel} (sonra)",
            )
        )
        ekleme = sum(1 for line in diff_lines if line.startswith("+") and not line.startswith("+++"))
        silme = sum(1 for line in diff_lines if line.startswith("-") and not line.startswith("---"))
        diff_path = kule.job_workdir(job_id) / "diff" / f"{n}.diff"
        diff_path.parent.mkdir(parents=True, exist_ok=True)
        diff_path.write_text("".join(diff_lines), encoding="utf-8", newline="\n")
        if ekleme or silme:
            any_nonempty = True
        diffler.append(
            {
                "dosya": rel,
                # Relative to `<state>/kule/` — never absolute — so an
                # archive move doesn't strand these; see `_resolve_kule_yol`.
                "once_yol": _kule_relative(kule, once_path),
                "sonra_yol": _kule_relative(kule, sonra_path),
                "diff_yol": _kule_relative(kule, diff_path),
                "ekleme": ekleme,
                "silme": silme,
            }
        )
    return diffler, any_nonempty


# --------------------------------------------------------------------------
# Spawn plans — mirrors claude_runner.run_claude's argv/env conventions for
# the claude side, and ingest_common._run_codex's `--skip-git-repo-check`
# plus codex-fleet's sandbox defaults for the codex side.
# --------------------------------------------------------------------------


def _resolve_executable(tur: str) -> str | None:
    name = "claude" if tur == "claude" else "codex"
    return shutil.which(name)


def _spawn_plan(
    executable: str,
    tur: str,
    job: dict[str, Any],
    environment: dict[str, str] | None,
    prompt_text: str,
) -> tuple[list[str], dict[str, Any], str | None]:
    env = dict(os.environ if environment is None else environment)
    env["BEYIN_INVOKED_BY"] = "kule"
    cwd = job.get("cwd", "")
    izinler = job.get("izinler") or {}
    common_kwargs: dict[str, Any] = dict(
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
        cwd=cwd,
        env=env,
        creationflags=nezaket.dusuk_oncelik_bayraklari(),
    )
    if tur == "claude":
        argv = [
            executable,
            "-p",
            "--model",
            str(job["model"]),
            "--output-format",
            "stream-json",
            "--verbose",
        ]
        permission_mode = izinler.get("permission_mode")
        if permission_mode:
            argv += ["--permission-mode", str(permission_mode)]
        allowed_tools = izinler.get("allowed_tools")
        if allowed_tools:
            argv += ["--allowedTools", str(allowed_tools)]
        kwargs = dict(common_kwargs, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        return argv, kwargs, prompt_text

    sandbox = str(izinler.get("sandbox") or "workspace-write")
    argv = [
        executable,
        "exec",
        "--json",
        "-m",
        str(job["model"]),
        "--skip-git-repo-check",
        "--sandbox",
        sandbox,
        "-C",
        cwd,
        "--",
        prompt_text,
    ]
    kwargs = dict(common_kwargs, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    return argv, kwargs, None


# --------------------------------------------------------------------------
# The worker body — one job, run to completion.
# --------------------------------------------------------------------------


def _finish_job(kule: Kule, job: dict[str, Any], durum: str, *, hata: str | None = None) -> None:
    job["durum"] = durum
    if hata is not None:
        job["hata"] = hata
    job["bitis"] = _now_iso()
    if job.get("sure_sn") is None:
        job["sure_sn"] = 0.0
    _write_job(kule, job)


DEFAULT_TICK_ARALIK = 1.0


class _RotatingLog:
    """Appends to `<id>.log`; once it passes `max_bayt` it is rotated ONCE
    to `<id>.log.1` (overwriting any prior `.log.1`) and a fresh, empty
    `<id>.log` continues from there — a single generation, not a chained
    logrotate. `rotated` flips to `True` the moment that happens, so the
    caller can note `LOG_KIRPILDI_SLUG` on the job record exactly once."""

    def __init__(self, path: Path, max_bayt: int):
        self.path = path
        self.max_bayt = max_bayt
        self.rotated = False
        try:
            self.written = path.stat().st_size if path.exists() else 0
        except OSError:
            self.written = 0
        self.handle = path.open("a", encoding="utf-8", newline="\n")

    def write_line(self, line: str) -> None:
        data = line + "\n"
        self.handle.write(data)
        self.written += len(data.encode("utf-8"))
        if self.max_bayt > 0 and self.written > self.max_bayt and not self.rotated:
            self.rotated = True
            self.handle.close()
            rotated_path = self.path.with_name(self.path.name + ".1")
            try:
                os.replace(self.path, rotated_path)
            except OSError:
                pass
            self.handle = self.path.open("a", encoding="utf-8", newline="\n")
            self.written = 0

    def close(self) -> None:
        try:
            self.handle.close()
        except Exception:
            pass


def _reader_loop(stream: Any, line_queue: "queue.Queue[tuple[str, str | None]]") -> None:
    """Background thread body: push every line onto the queue, then an EOF
    sentinel. Runs entirely separately from the poll loop below so a child
    that never writes anything cannot block that loop's own timeout/iptal
    checks — only this thread's own `readline()` call blocks, and it is
    left to die with the child once the tree is killed."""
    if stream is None:
        line_queue.put(("eof", None))
        return
    while True:
        try:
            raw_line = stream.readline()
        except Exception:
            raw_line = ""
        if raw_line == "":
            line_queue.put(("eof", None))
            return
        line_queue.put(("line", raw_line))


def _run_job(
    kule: Kule,
    job_id: str,
    environment: dict[str, str] | None = None,
    *,
    popen_factory: Callable[..., Any] | None = None,
    now_fn: Callable[[], float] = time.monotonic,
    tick_aralik: float = DEFAULT_TICK_ARALIK,
) -> None:
    """Run one already-claimed job to completion. Never raises."""
    job = _read_job(kule, job_id)
    tur = str(job.get("tur", "")) if job is not None else ""
    try:
        if job is None:
            return

        job["durum"] = "running"
        job["baslangic"] = _now_iso()
        _write_job(kule, job)

        cwd = job.get("cwd", "")
        izlenen = job.get("izlenen_dosyalar") or []
        if izlenen:
            _snapshot(kule, job_id, izlenen, "once", cwd)

        executable = _resolve_executable(tur)
        if executable is None:
            slug = CLAUDE_CLI_EKSIK_SLUG if tur == "claude" else CODEX_CLI_EKSIK_SLUG
            _finish_job(kule, job, "failed", hata=slug)
            return

        try:
            prompt_text = kule.prompt_path(job_id).read_text(encoding="utf-8")
        except OSError:
            prompt_text = ""

        argv, spawn_kwargs, stdin_text = _spawn_plan(executable, tur, job, environment, prompt_text)

        spawn = popen_factory or subprocess.Popen
        try:
            process = spawn(argv, **spawn_kwargs)
        except OSError:
            _finish_job(kule, job, "failed", hata=BASLATILAMADI_SLUG)
            return

        if stdin_text is not None and getattr(process, "stdin", None) is not None:
            try:
                process.stdin.write(stdin_text)
                process.stdin.close()
            except Exception:
                pass

        timeout = _zaman_asimi(environment)
        iptal_path = kule.iptal_path(job_id)
        log_path = kule.log_path(job_id)
        started = now_fn()
        last_flush = started
        event_count = 0
        last_line = ""
        output_chars = 0
        cancelled = False
        timed_out = False

        log_path.parent.mkdir(parents=True, exist_ok=True)
        line_queue: "queue.Queue[tuple[str, str | None]]" = queue.Queue()
        reader = threading.Thread(
            target=_reader_loop, args=(process.stdout, line_queue), daemon=True
        )
        reader.start()
        log_kirpildi = False
        rotating_log = _RotatingLog(log_path, _log_max_bayt(environment))
        try:
            while True:
                try:
                    kind, payload = line_queue.get(timeout=tick_aralik)
                except queue.Empty:
                    # No line arrived within this tick — the child may be
                    # producing nothing at all. Fall through to the same
                    # timeout/iptal checks a line-carrying tick gets: a
                    # silent, hung child must be just as reachable as a
                    # chatty one.
                    kind, payload = "tick", None

                if kind == "line":
                    line = (payload or "").rstrip("\n")
                    was_rotated = rotating_log.rotated
                    rotating_log.write_line(line)
                    if rotating_log.rotated and not was_rotated:
                        log_kirpildi = True
                    output_chars += len(line) + 1
                    event_count += 1
                    if line.strip():
                        last_line = line[:200]

                now = now_fn()
                # Batched update: at most once every 2s, never one write per
                # line — a chatty stream-json child must not turn this into
                # an atomic-write storm.
                if now - last_flush >= 2.0:
                    job["son_olay"] = last_line
                    job["olaylar"] = event_count
                    _write_job(kule, job)
                    last_flush = now
                # Checked on every tick — a line, a real timeout wait, or
                # EOF — never only "between log lines": a child producing no
                # output at all must still be cancellable/killable.
                if iptal_path.exists():
                    cancelled = True
                    kaydet._kill_process_tree(process)
                    break
                if timeout > 0 and (now - started) > timeout:
                    timed_out = True
                    kaydet._kill_process_tree(process)
                    break
                # NOT checked here on purpose: `<state_dir>/kule/dur` is the
                # outer `calis()` loop's own stop signal, never a per-job
                # kill switch — a running child is deliberately left running
                # on a graceful stop (see docs/kokpit.md "Orphan / reaper
                # semantics"). Killing it here would contradict that.
                if kind == "eof":
                    break
        finally:
            rotating_log.close()

        reader.join(timeout=5)
        try:
            returncode = process.wait(timeout=15)
        except Exception:
            try:
                returncode = process.poll()
            except Exception:
                returncode = None

        sure_sn = round(now_fn() - started, 3)
        job["son_olay"] = last_line
        job["olaylar"] = event_count
        job["cikis"] = returncode
        job["sure_sn"] = sure_sn

        try:
            iptal_path.unlink()
        except FileNotFoundError:
            pass
        except OSError:
            pass

        if cancelled:
            job["durum"] = "cancelled"
            outcome = "kule-iptal"
        elif timed_out:
            job["durum"] = "failed"
            job["hata"] = ZAMAN_ASIMI_SLUG
            outcome = ZAMAN_ASIMI_SLUG
        elif returncode not in (0, None):
            job["hata"] = f"kule-cikis-{returncode}"
            job["durum"] = "failed"
            outcome = job["hata"]
        else:
            if izlenen:
                diffler, any_nonempty = _compute_diffs(kule, job, cwd)
                job["diffler"] = diffler
                job["durum"] = "waiting-approval" if any_nonempty else "succeeded"
            else:
                job["durum"] = "succeeded"
            outcome = "ok"

        if log_kirpildi:
            job.setdefault("uyarilar", []).append(f"warn:{LOG_KIRPILDI_SLUG}")

        job["bitis"] = _now_iso()
        _write_job(kule, job)

        try:
            record_call(
                kule.state_dir,
                backend=tur,
                model_tier=str(job.get("model", "")),
                model_slug=str(job.get("model", "")),
                component="kule",
                input_chars=int(job.get("prompt_karakter", 0)),
                output_chars=output_chars,
                duration_ms=int(sure_sn * 1000),
                outcome=outcome,
            )
        except Exception:
            pass
    except Exception as exc:  # a job must never crash the worker loop
        try:
            if job is not None:
                job["durum"] = "failed"
                job["hata"] = f"kule-beklenmedik:{exc.__class__.__name__}"
                job["bitis"] = _now_iso()
                _write_job(kule, job)
        except Exception:
            pass
        try:
            write_health(
                kule.state_dir,
                f"kule-beklenmedik:{exc.__class__.__name__}",
                component="kule",
                health_name=HEALTH_NAME,
            )
        except Exception:
            pass
    finally:
        _release_lane(kule, tur, job_id)
        _release_claim(kule, job_id)
        try:
            _write_durum(kule)
        except Exception:
            pass


# --------------------------------------------------------------------------
# Approval / rejection / cancellation transitions — each guarded by a
# per-job lock (`beyin_ortak._lock_exclusive`) so two concurrent calls on
# the same id can't race each other.
# --------------------------------------------------------------------------


@contextlib.contextmanager
def _job_lock(kule: Kule, job_id: str):
    kule.locks_dir.mkdir(parents=True, exist_ok=True)
    lock_file = kule.lock_path(job_id).open("a+", encoding="utf-8")
    try:
        _lock_exclusive(lock_file, blocking=True)
        yield
    finally:
        lock_file.close()


def onayla(kule: Kule, job_id: str) -> tuple[dict[str, Any] | None, str | None]:
    with _job_lock(kule, job_id):
        job = _read_job(kule, job_id)
        if job is None:
            return None, IS_YOK_SLUG
        if job.get("durum") != "waiting-approval":
            return None, GECIS_GECERSIZ_SLUG
        job["durum"] = "succeeded"
        job["onay"] = {"ts": _now_iso(), "karar": "kabul"}
        _write_job(kule, job)
    _write_durum(kule)
    return job, None


def reddet(kule: Kule, job_id: str) -> tuple[dict[str, Any] | None, str | None]:
    with _job_lock(kule, job_id):
        job = _read_job(kule, job_id)
        if job is None:
            return None, IS_YOK_SLUG
        if job.get("durum") != "waiting-approval":
            return None, GECIS_GECERSIZ_SLUG
        job["durum"] = "rejected"
        job["onay"] = {"ts": _now_iso(), "karar": "red"}
        _write_job(kule, job)
    _write_durum(kule)
    return job, None


def iptal(kule: Kule, job_id: str) -> tuple[dict[str, Any] | None, str | None]:
    with _job_lock(kule, job_id):
        job = _read_job(kule, job_id)
        if job is None:
            return None, IS_YOK_SLUG
        durum = job.get("durum")
        if durum not in ("queued", "running"):
            return None, GECIS_GECERSIZ_SLUG
        if durum == "queued":
            job["durum"] = "cancelled"
            job["bitis"] = _now_iso()
            _write_job(kule, job)
            _write_durum(kule)
            return job, None
        # Running: the worker thread checks for this marker between log
        # lines and kills the tree itself — see `_run_job`.
        kule.iptal_path(job_id).parent.mkdir(parents=True, exist_ok=True)
        kule.iptal_path(job_id).touch(exist_ok=True)
    return job, None


# --------------------------------------------------------------------------
# Archive — never deletes, only moves the whole `jobs/<id>*` set to `arsiv/`.
# --------------------------------------------------------------------------


def _archive_job(kule: Kule, job_id: str) -> None:
    kule.arsiv_dir.mkdir(parents=True, exist_ok=True)
    for src, name in (
        (kule.job_path(job_id), f"{job_id}.json"),
        (kule.prompt_path(job_id), f"{job_id}.prompt"),
        (kule.log_path(job_id), f"{job_id}.log"),
        (kule.iptal_path(job_id), f"{job_id}.iptal"),
    ):
        if src.exists():
            os.replace(src, kule.arsiv_dir / name)
    src_dir = kule.job_workdir(job_id)
    if src_dir.exists():
        dest_dir = kule.arsiv_dir / job_id
        if dest_dir.exists():
            shutil.rmtree(dest_dir)
        os.replace(src_dir, dest_dir)


def _maybe_archive(kule: Kule, tavan: int | None = None) -> list[str]:
    cap = _kule_tavan() if tavan is None else tavan
    jobs = _list_active_jobs(kule)
    finished = [job for job in jobs if job.get("durum") in TERMINAL_DURUMLAR]
    if len(finished) <= cap:
        return []
    finished.sort(key=lambda job: job.get("olusturuldu_ns", 0))
    fazla = len(finished) - cap
    archived: list[str] = []
    for job in finished[:fazla]:
        _archive_job(kule, job["id"])
        archived.append(job["id"])
    return archived


# --------------------------------------------------------------------------
# durum.json — the one file the (future) panel is allowed to read for
# numbers; recomputed atomically after every state change.
# --------------------------------------------------------------------------


def _write_durum(
    kule: Kule,
    *,
    reaper_actions: Sequence[dict[str, Any]] | None = None,
    tavan: int | None = None,
) -> dict[str, Any]:
    archived = _maybe_archive(kule, tavan=tavan)
    jobs = _list_active_jobs(kule)

    sayilar = {durum: 0 for durum in DURUM_DEGERLERI}
    for job in jobs:
        durum = job.get("durum")
        if durum in sayilar:
            sayilar[durum] += 1

    seritler = {
        tur: {"dolu": _lane_count(kule, tur), "tavan": _lane_cap(tur)}
        for tur in ("claude", "codex")
    }

    jobs_sorted = sorted(jobs, key=lambda job: job.get("olusturuldu_ns", 0), reverse=True)
    son_isler = [
        {
            "id": job.get("id"),
            "tur": job.get("tur"),
            "model": job.get("model"),
            "durum": job.get("durum"),
            "sure_sn": job.get("sure_sn"),
            "son_olay": job.get("son_olay", ""),
            "diff_var": bool(job.get("diffler")),
        }
        for job in jobs_sorted[:20]
    ]

    onceki: dict[str, Any] = {}
    try:
        if kule.durum_path.exists():
            loaded = json.loads(kule.durum_path.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                onceki = loaded
    except (OSError, ValueError, json.JSONDecodeError):
        onceki = {}
    reaper_gecmis = onceki.get("reaper_eylemleri")
    if not isinstance(reaper_gecmis, list):
        reaper_gecmis = []
    if reaper_actions:
        reaper_gecmis = (reaper_gecmis + list(reaper_actions))[-20:]
    if archived:
        reaper_gecmis = (
            reaper_gecmis
            + [{"id": job_id, "eylem": "arsivlendi", "ts": _now_iso()} for job_id in archived]
        )[-20:]

    payload = {
        "guncellendi": _now_iso(),
        "sayilar": sayilar,
        "seritler": seritler,
        "son_isler": son_isler,
        "reaper_eylemleri": reaper_gecmis,
    }
    _atomic_write_json(kule.durum_path, payload)
    return payload


# --------------------------------------------------------------------------
# The worker loop
# --------------------------------------------------------------------------


def _queued_in_creation_order(kule: Kule) -> list[dict[str, Any]]:
    jobs = [job for job in _list_active_jobs(kule) if job.get("durum") == "queued"]
    jobs.sort(key=lambda job: job.get("olusturuldu_ns", 0))
    return jobs


def _bir_gecis(
    kule: Kule,
    environment: dict[str, str] | None,
    popen_factory: Callable[..., Any] | None,
) -> list[threading.Thread]:
    """One pass: reap stale, then claim+spawn each eligible queued job."""
    reaper_actions = reap_stale(kule)
    threads: list[threading.Thread] = []
    for job in _queued_in_creation_order(kule):
        tur = job.get("tur")
        if tur not in ("claude", "codex"):
            continue
        if _lane_count(kule, tur) >= _lane_cap(tur, environment):
            continue
        if not _claim(kule, job["id"]):
            continue
        if not _lane_claim(kule, tur, job["id"]):
            _release_claim(kule, job["id"])
            continue
        thread = threading.Thread(
            target=_run_job,
            args=(kule, job["id"], environment),
            kwargs={"popen_factory": popen_factory},
            daemon=True,
        )
        thread.start()
        threads.append(thread)
    _write_durum(kule, reaper_actions=reaper_actions)
    return threads


def calis(
    state_dir: Path,
    *,
    once: bool = False,
    environment: dict[str, str] | None = None,
    popen_factory: Callable[..., Any] | None = None,
    sleep_fn: Callable[[float], None] = time.sleep,
    should_stop: Callable[[], bool] | None = None,
) -> None:
    """The worker loop. `--once` runs one pass and joins everything it
    launched before returning; otherwise it loops until the `dur` file
    appears or `should_stop()` says to — checked at the top of every pass,
    never mid-pass. On stop, already-running children are left running:
    their lane/claim markers stay in place and the next `calis` call's
    `reap_stale` reclaims them once this process's pid goes away.

    `dur` is a stop signal for THIS running instance only, not a durable
    "stay stopped" marker — `Stop-Kule` writes it and this loop's own pass
    reads it, but nothing else ever deletes it, so a leftover `dur` from a
    previous run would otherwise make every future `calis()` return
    immediately without ever processing a job again. It is therefore removed
    once, right here, before the loop is ever entered (`beyin.ps1`'s
    `Start-Kule` also removes it before spawning, belt and braces).
    """
    kule = Kule(state_dir)
    kule.ensure()
    nezaket.kendini_arka_plana_al()
    try:
        kule.dur_path.unlink()
    except FileNotFoundError:
        pass
    except OSError:
        pass
    while True:
        if kule.dur_path.exists() or (should_stop is not None and should_stop()):
            return
        threads = _bir_gecis(kule, environment, popen_factory)
        if once:
            for thread in threads:
                thread.join()
            _write_durum(kule)
            return
        sleep_fn(_aralik(environment))


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def _izin_kv(value: str) -> tuple[str, str]:
    key, sep, val = value.partition("=")
    if not sep:
        raise argparse.ArgumentTypeError("izin KEY=VALUE olmalı")
    return key.strip(), val.strip()


def _print_result(result: dict[str, Any], as_json: bool) -> None:
    if as_json:
        print(RESULT_MARKER + json.dumps(result, ensure_ascii=False))
    else:
        print(json.dumps(result, ensure_ascii=False, indent=2))


def _cmd_is_ver(args: argparse.Namespace, state_dir: Path) -> int:
    kule = Kule(state_dir)

    verilen = sum([bool(args.stdin), args.prompt_dosya is not None])
    if verilen == 0:
        _print_result({"olusturuldu": False, "hata": PROMPT_EKSIK_SLUG}, args.json)
        return 1
    if verilen > 1:
        _print_result({"olusturuldu": False, "hata": BIRDEN_FAZLA_KAYNAK_SLUG}, args.json)
        return 1

    if args.stdin:
        try:
            prompt = sys.stdin.buffer.read().decode("utf-8", errors="strict")
        except (OSError, UnicodeDecodeError):
            _print_result({"olusturuldu": False, "hata": STDIN_HATA_SLUG}, args.json)
            return 1
    else:
        try:
            prompt = args.prompt_dosya.read_text(encoding="utf-8", errors="strict")
        except (OSError, UnicodeDecodeError):
            _print_result({"olusturuldu": False, "hata": DOSYA_HATA_SLUG}, args.json)
            return 1

    izinler = dict(args.izin) if args.izin else {}
    record, error = create_job(
        kule,
        tur=args.tur,
        model=args.model,
        prompt=prompt,
        cwd=args.cwd,
        izlenen_dosyalar=args.izlenen,
        izinler=izinler,
        kaynak=args.kaynak,
    )
    if error is not None:
        _print_result({"olusturuldu": False, "hata": error}, args.json)
        return 1
    _print_result({"olusturuldu": True, "is": record}, args.json)
    return 0


def _cmd_durum(args: argparse.Namespace, state_dir: Path) -> int:
    kule = Kule(state_dir)
    kule.ensure()
    payload = _write_durum(kule)
    if args.json:
        print(json.dumps(payload, ensure_ascii=False))
        return 0
    print("Kule durumu:")
    for durum, adet in payload["sayilar"].items():
        print(f"  {durum}: {adet}")
    for tur, bilgi in payload["seritler"].items():
        print(f"  serit[{tur}]: {bilgi['dolu']}/{bilgi['tavan']}")
    return 0


def _cmd_goster(args: argparse.Namespace, state_dir: Path) -> int:
    kule = Kule(state_dir)
    job, location = _find_job(kule, args.id)
    if job is None:
        print(IS_YOK_SLUG, file=sys.stderr)
        return 1
    log_kuyruk = _tail_log(kule, args.id, location, 30)
    if args.json:
        print(json.dumps({"is": job, "log_kuyruk": log_kuyruk}, ensure_ascii=False))
        return 0
    print(json.dumps(job, ensure_ascii=False, indent=2))
    print("--- son 30 satır ---")
    for line in log_kuyruk:
        print(line)
    return 0


def _cmd_log(args: argparse.Namespace, state_dir: Path) -> int:
    kule = Kule(state_dir)
    job, location = _find_job(kule, args.id)
    if job is None:
        print(IS_YOK_SLUG, file=sys.stderr)
        return 1
    for line in _tail_log(kule, args.id, location, args.n):
        print(line)
    return 0


def _cmd_diff(args: argparse.Namespace, state_dir: Path) -> int:
    kule = Kule(state_dir)
    job, location = _find_job(kule, args.id)
    if job is None:
        print(IS_YOK_SLUG, file=sys.stderr)
        return 1
    diffler = job.get("diffler") or []
    if args.n < 0 or args.n >= len(diffler):
        print("kule-diff-yok", file=sys.stderr)
        return 1
    resolved = _resolve_kule_yol(
        kule, location or "active", job["id"], diffler[args.n].get("diff_yol", "")
    )
    if resolved is None:
        print(YOL_DISI_SLUG, file=sys.stderr)
        return 1
    try:
        print(resolved.read_text(encoding="utf-8"), end="")
    except OSError:
        print("kule-diff-okunamadi", file=sys.stderr)
        return 1
    return 0


def _cmd_onayla(args: argparse.Namespace, state_dir: Path) -> int:
    job, error = onayla(Kule(state_dir), args.id)
    if error is not None:
        print(error, file=sys.stderr)
        return 1
    print(json.dumps(job, ensure_ascii=False))
    return 0


def _cmd_reddet(args: argparse.Namespace, state_dir: Path) -> int:
    job, error = reddet(Kule(state_dir), args.id)
    if error is not None:
        print(error, file=sys.stderr)
        return 1
    print(json.dumps(job, ensure_ascii=False))
    return 0


def _cmd_iptal(args: argparse.Namespace, state_dir: Path) -> int:
    job, error = iptal(Kule(state_dir), args.id)
    if error is not None:
        print(error, file=sys.stderr)
        return 1
    print(json.dumps(job, ensure_ascii=False))
    return 0


def _cmd_calis(args: argparse.Namespace, state_dir: Path) -> int:
    stop_requested = {"v": False}

    def _handle_signal(_signum: int, _frame: Any) -> None:
        stop_requested["v"] = True

    for sig in (getattr(signal, "SIGTERM", None), getattr(signal, "SIGINT", None)):
        if sig is None:
            continue
        try:
            signal.signal(sig, _handle_signal)
        except (ValueError, OSError):
            pass  # not the main thread, or unsupported on this platform

    def _sleep(seconds: float) -> None:
        if not stop_requested["v"]:
            time.sleep(seconds)

    try:
        calis(
            state_dir,
            once=args.once,
            sleep_fn=_sleep,
            should_stop=lambda: stop_requested["v"],
        )
    except KeyboardInterrupt:
        pass
    return 0


def _cmd_arsivle(_args: argparse.Namespace, state_dir: Path) -> int:
    kule = Kule(state_dir)
    kule.ensure()
    archived = _maybe_archive(kule)
    payload = _write_durum(kule)
    print(json.dumps({"arsivlenen": archived, "durum": payload}, ensure_ascii=False))
    return 0


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state-dir", type=Path, default=STATE_DIR)
    parser.add_argument("--vault-root", type=Path, default=VAULT_ROOT)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("is-ver", help="Create a new job.")
    p.add_argument("--tur", required=True, choices=("claude", "codex"))
    p.add_argument("--model", required=True)
    p.add_argument("--prompt-dosya", type=Path, default=None)
    p.add_argument("--stdin", action="store_true")
    p.add_argument("--cwd", type=Path, default=Path.cwd())
    p.add_argument("--izlenen", action="append", default=[])
    p.add_argument("--izin", action="append", type=_izin_kv, default=[])
    p.add_argument("--kaynak", choices=("panel", "cli"), default="cli")
    p.add_argument("--json", action="store_true")
    p.set_defaults(handler=_cmd_is_ver)

    p = sub.add_parser("durum", help="Summary of all jobs and lanes.")
    p.add_argument("--json", action="store_true")
    p.set_defaults(handler=_cmd_durum)

    p = sub.add_parser("goster", help="One job's full record + recent log.")
    p.add_argument("id")
    p.add_argument("--json", action="store_true")
    p.set_defaults(handler=_cmd_goster)

    p = sub.add_parser("log", help="Tail one job's event log.")
    p.add_argument("id")
    p.add_argument("--n", type=int, default=100)
    p.set_defaults(handler=_cmd_log)

    p = sub.add_parser("diff", help="Print one stored diff.")
    p.add_argument("id")
    p.add_argument("n", type=int)
    p.set_defaults(handler=_cmd_diff)

    p = sub.add_parser("onayla", help="Approve a waiting-approval job.")
    p.add_argument("id")
    p.set_defaults(handler=_cmd_onayla)

    p = sub.add_parser("reddet", help="Reject a waiting-approval job.")
    p.add_argument("id")
    p.set_defaults(handler=_cmd_reddet)

    p = sub.add_parser("iptal", help="Cancel a queued or running job.")
    p.add_argument("id")
    p.set_defaults(handler=_cmd_iptal)

    p = sub.add_parser("calis", help="Run the worker loop.")
    p.add_argument("--once", action="store_true")
    p.set_defaults(handler=_cmd_calis)

    p = sub.add_parser("arsivle", help="Force an archive pass.")
    p.set_defaults(handler=_cmd_arsivle)

    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    # Same defensive boundary every other top-level beyin entrypoint keeps:
    # this must never be reachable from inside a model's own tool call.
    if os.environ.get("BEYIN_INVOKED_BY"):
        return 0
    try:
        args = _parse_args(argv)
    except SystemExit as exc:
        return exc.code if isinstance(exc.code, int) else 2
    try:
        return args.handler(args, args.state_dir)
    except Exception as exc:  # last-resort guard: never a bare traceback
        try:
            write_health(
                args.state_dir,
                f"kule-beklenmedik:{exc.__class__.__name__}",
                component="kule",
                health_name=HEALTH_NAME,
            )
        except OSError:
            pass
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
