#!/usr/bin/env python3
# yazan: claude · model: sonnet
"""Kaydet — the golden path: save straight to the daily log, then compile.

Kaydet is "save a project": whatever text the caller hands it (panel, CLI,
stdin, or a file) is appended to today's daily log exactly the way a hook
flush would, except no model ever sees or reshapes the note first — zero
model tokens are spent on the note itself. Once the note is durably on disk,
``compile.py`` is run to completion immediately, bypassing the A7 nezaket
gate (``--nezaket-del``): an explicit user action is its own permission. See
``docs/kaydet.md`` for the full contract.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import subprocess
import sys
import time
from typing import Any, Callable, Sequence

from beyin_ortak import write_health
import compile as compile_module
import flush
import ingest_common
import nezaket
import retrieve
import secret_guard


# One fixed marker prefixing the single machine-readable ``--json`` output
# line. The panel greps its own child process's stdout for a line starting
# with this exact token — it must survive interleaving with whatever the
# spawned compile subprocess itself prints to the same stream.
RESULT_MARKER = "KAYDET-SONUC "


SCRIPT_DIR = Path(__file__).resolve().parent
VAULT_ROOT = SCRIPT_DIR.parent.parent
STATE_DIR = SCRIPT_DIR / ".state"

# Kaydet keeps its own health file, never ``health.json``: that file's
# top-level ``component``/``error`` fields are flush's and compile's, and a
# write from here would clobber whichever of them wrote last.
HEALTH_NAME = "kaydet-health.json"

# The literal session id every Kaydet call locks under — see
# ``ingest_common.flush_session_lock``. Real hook sessions use Claude Code's
# own session UUIDs, so this can never collide with one.
SESSION_LOCK_ID = "kaydet"
SESSION_SOURCE = "kaydet"

MAX_KARAKTER_ENV = "BEYIN_KAYDET_MAX_KARAKTER"
DEFAULT_MAX_KARAKTER = 20_000
DERLEME_ZAMAN_ASIMI_ENV = "BEYIN_KAYDET_DERLEME_ZAMAN_ASIMI"
DEFAULT_DERLEME_ZAMAN_ASIMI = 900

BOS_SLUG = "kaydet-bos"
COK_UZUN_SLUG = "kaydet-cok-uzun"
KARANTINA_SLUG = "kaydet-karantina"
COKLU_KAYNAK_SLUG = "kaydet-birden-fazla-kaynak"
STDIN_HATA_SLUG = "kaydet-stdin-hata"
DOSYA_HATA_SLUG = "kaydet-dosya-hata"
YAZMA_HATA_SLUG = "kaydet-yazma-hatasi"
DERLEME_EKSIK_SLUG = "kaydet-derleme-eksik"
DERLEME_ZAMAN_ASIMI_SLUG = "kaydet-derleme-zaman-asimi"
DERLEME_BASLATILAMADI_SLUG = "kaydet-derleme-baslatilamadi"
BEKLENMEDIK_SLUG = "kaydet-beklenmedik"

# U+FEFF (byte-order mark). A directive sitting right behind a leading BOM
# is not "after a bare ``\n``" as far as ``re.MULTILINE`` is concerned, so
# it would slip past the ``^`` anchor undetected unless stripped first.
_LEADING_BOM = "\ufeff"
# Every line-separator shape Python's ``re.MULTILINE`` does NOT treat ``^``
# as following. ``DIRECTIVE_SHAPED`` (here via flush.py, and identically in
# compile.py) anchors on ``^`` under MULTILINE, which only fires after a bare
# ``\n`` — a directive sitting right after one of these instead slips past it
# completely undetected. Order matters: CRLF must fold before bare CR so a
# Windows line ending collapses to exactly one ``\n``, not two.
_ALTERNATE_LINE_SEPARATORS = (
    "\r\n",    # CRLF
    "\r",      # bare CR
    "\u2028",  # LINE SEPARATOR
    "\u2029",  # PARAGRAPH SEPARATOR
    "\u0085",  # NEXT LINE (NEL)
)


def normalize_text(text: str) -> str:
    """Fold every line-separator/BOM shape the directive gate would miss.

    Applied to BOTH the title and the body, before every other gate (redact,
    directive-check) and before the write — so the gate sees, and the daily
    log stores, the exact same normalised text. Daily files are LF already,
    so this changes nothing for ordinary input; see docs/kaydet.md.
    """
    if text.startswith(_LEADING_BOM):
        text = text[len(_LEADING_BOM):]
    for separator in _ALTERNATE_LINE_SEPARATORS:
        text = text.replace(separator, "\n")
    return text


def resolve_max_karakter(environment: dict[str, str] | None = None) -> int:
    """``BEYIN_KAYDET_MAX_KARAKTER``; unset, junk, or non-positive falls back."""
    env = os.environ if environment is None else environment
    raw = (env.get(MAX_KARAKTER_ENV) or "").strip()
    if not raw:
        return DEFAULT_MAX_KARAKTER
    try:
        value = int(raw)
    except ValueError:
        return DEFAULT_MAX_KARAKTER
    return value if value > 0 else DEFAULT_MAX_KARAKTER


def resolve_derleme_zaman_asimi(environment: dict[str, str] | None = None) -> int:
    """``BEYIN_KAYDET_DERLEME_ZAMAN_ASIMI``; unset, junk, or non-positive falls back."""
    env = os.environ if environment is None else environment
    raw = (env.get(DERLEME_ZAMAN_ASIMI_ENV) or "").strip()
    if not raw:
        return DEFAULT_DERLEME_ZAMAN_ASIMI
    try:
        value = int(raw)
    except ValueError:
        return DEFAULT_DERLEME_ZAMAN_ASIMI
    return value if value > 0 else DEFAULT_DERLEME_ZAMAN_ASIMI


def _prepare_source(args: argparse.Namespace) -> tuple[str | None, str | None]:
    """Return ``(text, error-slug)``. Reads at most the one requested source.

    ``text`` is ``""`` (not ``None``) when no source was given at all — that
    is simply an empty note, same as an all-whitespace one.
    """
    given = [args.metin is not None, bool(args.stdin), args.dosya is not None]
    if sum(given) > 1:
        return None, COKLU_KAYNAK_SLUG
    if args.metin is not None:
        return args.metin, None
    if args.stdin:
        try:
            # Read raw bytes and decode explicitly, strictly. PowerShell 5.1
            # on Windows can otherwise hand a piping process bytes already
            # mangled by ``$OutputEncoding`` before Python ever sees them
            # (see beyin.ps1's ``New-KaydetCommand``); the fix there keeps
            # the bytes correct, and this decode refuses to silently accept
            # or replace anything that still isn't valid UTF-8 — a corrupted
            # note must fail loudly, never get saved with mangled text.
            raw = sys.stdin.buffer.read()
            return raw.decode("utf-8", errors="strict"), None
        except (OSError, UnicodeDecodeError):
            return None, STDIN_HATA_SLUG
    if args.dosya is not None:
        try:
            return args.dosya.read_text(encoding="utf-8", errors="strict"), None
        except (OSError, UnicodeDecodeError):
            return None, DOSYA_HATA_SLUG
    return "", None


def _compile_script_path(vault_root: Path) -> Path:
    """Same ``<vault>/.claude/scripts/compile.py`` convention flush.py uses."""
    return vault_root / ".claude" / "scripts" / "compile.py"


def _kill_process_tree(process: Any) -> None:
    """Best-effort kill of ``process`` and anything IT spawned. Never raises.

    ``proc.kill()`` alone only kills the immediate child. If that child is
    still inside its own work when the timeout hits, whatever it spawned
    keeps running as an orphan. On Windows, ``taskkill /T`` kills the whole
    process tree rooted at the pid; everywhere else a plain kill is enough
    because POSIX process groups aren't in play here.
    """
    try:
        if sys.platform == "win32":
            subprocess.run(
                ["taskkill", "/T", "/F", "/PID", str(process.pid)],
                capture_output=True,
                timeout=10,
            )
        else:
            process.kill()
    except Exception:
        pass


def _spawn_compile(
    vault_root: Path,
    timeout: int,
    popen_factory: Callable[..., Any] | None = None,
) -> tuple[bool, int | None, str | None]:
    """Run ``compile.py --nezaket-del`` to completion; never raises.

    Returns ``(kosuldu, cikis, hata)``. ``kosuldu`` is true only when the
    subprocess actually returned an exit code — false on a missing script, a
    failure to start it, or a timeout, in which case ``cikis`` stays ``None``
    and ``hata`` names why. Uses ``Popen`` (not ``run``) so a timeout can
    kill the whole process tree via ``_kill_process_tree`` instead of only
    the immediate child.
    """
    script_path = _compile_script_path(vault_root)
    if not script_path.is_file():
        return False, None, DERLEME_EKSIK_SLUG

    environment = os.environ.copy()
    # Mirror flush.maybe_trigger_compile exactly: a compile spawned from
    # inside another beyin call must not see BEYIN_INVOKED_BY (or it would
    # early-return without running at all — compile.main's own guard), and
    # the compile backend is sealed to claude regardless of what backend
    # Kaydet's own environment happens to be configured for.
    environment.pop("BEYIN_INVOKED_BY", None)
    environment["BEYIN_MODEL_BACKEND"] = "claude"

    spawn = popen_factory or subprocess.Popen
    argv = [sys.executable, str(script_path), nezaket.NEZAKET_DEL_FLAG]
    try:
        process = spawn(argv, cwd=str(vault_root), env=environment)
    except OSError:
        return False, None, DERLEME_BASLATILAMADI_SLUG

    try:
        returncode = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        _kill_process_tree(process)
        try:
            # Reap it after the kill so it doesn't linger as a zombie; a
            # process that refuses to die even now is not this function's
            # problem any more — either way we report the timeout.
            process.wait(timeout=10)
        except Exception:
            pass
        return False, None, DERLEME_ZAMAN_ASIMI_SLUG
    return True, returncode, None


def _compose_body(text: str, baslik: str) -> tuple[str, str, list[str]]:
    """Redact, then fold an optional title in — title and text share one gate.

    Returns ``(body, check_text, hits)``. ``body`` is the markdown-formatted
    text that actually lands in the daily log (title bolded on its own
    line); ``check_text`` is the same content WITHOUT that ``**`` wrapping,
    which is what the directive-shaped gate must see — ``DIRECTIVE_SHAPED``
    anchors on true line starts, and a leading ``**`` would hide a
    directive-shaped title (``**TALİMAT: ...**``) from it entirely.
    """
    if baslik:
        redacted_baslik, baslik_hits = secret_guard.redact(baslik)
    else:
        redacted_baslik, baslik_hits = "", []
    redacted_text, text_hits = secret_guard.redact(text)
    hits: list[str] = []
    for name in [*baslik_hits, *text_hits]:
        if name not in hits:
            hits.append(name)
    if baslik:
        body = f"**{redacted_baslik}**\n\n{redacted_text}"
        check_text = f"{redacted_baslik}\n{redacted_text}"
    else:
        body = redacted_text
        check_text = redacted_text
    return body, check_text, hits


def run(
    args: argparse.Namespace,
    now: dt.datetime,
    *,
    compile_runner: Callable[..., Any] | None = None,
) -> tuple[int, dict[str, Any]]:
    """Do the whole Kaydet flow. Never raises: every failure is a slug."""
    vault_root = Path(args.vault_root)
    state_dir = Path(args.state_dir)

    text, source_error = _prepare_source(args)
    if source_error is not None:
        write_health(state_dir, source_error, component="kaydet", health_name=HEALTH_NAME)
        return 1, {"yazildi": False, "hata": source_error}

    # Normalise BEFORE every other gate (and before the write): the empty
    # check, the size cap, redact, and the directive-shaped check must all
    # see — and the daily log must store — the exact same folded text, or a
    # BOM/line-separator shape could slip a directive past the gate that
    # inspected the un-normalised original. See docs/kaydet.md.
    text = normalize_text(text)
    baslik = normalize_text((args.baslik or "").strip())

    if not text.strip():
        write_health(state_dir, BOS_SLUG, component="kaydet", health_name=HEALTH_NAME)
        return 1, {"yazildi": False, "hata": BOS_SLUG}

    max_karakter = resolve_max_karakter()
    if len(text) > max_karakter:
        write_health(state_dir, COK_UZUN_SLUG, component="kaydet", health_name=HEALTH_NAME)
        return 1, {"yazildi": False, "hata": COK_UZUN_SLUG, "karakter": len(text)}

    write_started = time.monotonic()
    anchor_id = "kaydet-" + now.strftime("%Y%m%dT%H%M%S")
    anchor = retrieve.format_session_anchor(
        anchor_id, now.isoformat(timespec="seconds"), source=SESSION_SOURCE
    )
    daily_path = vault_root / "daily" / f"{now.strftime('%Y-%m-%d')}.md"

    with ingest_common.flush_session_lock(SESSION_LOCK_ID, state_dir):
        body, check_text, hits = _compose_body(text, baslik)
        if hits:
            write_health(
                state_dir,
                "warn:secret-redacted-kaydet:" + ",".join(hits),
                warning=True,
                component="kaydet",
                health_name=HEALTH_NAME,
            )

        directive_match = flush.DIRECTIVE_SHAPED.search(check_text)
        if directive_match is not None:
            # Same convention compile.py uses for directive-shaped daily
            # content: preserve verbatim under .stage/karantina, never write
            # it to the daily log, and leave the decision to a human.
            source_file = f"kaydet-{now.strftime('%Y-%m-%d')}.md"
            destination = compile_module._quarantine_content(
                vault_root, source_file, body, directive_match
            )
            write_health(
                state_dir, KARANTINA_SLUG, component="kaydet", health_name=HEALTH_NAME
            )
            return 1, {
                "yazildi": False,
                "hata": KARANTINA_SLUG,
                "karantina_dosyasi": destination.as_posix(),
            }

        try:
            flush._append_daily(
                vault_root, body, "kaydet", now, suffix=" · kaydet", anchor=anchor
            )
        except OSError:
            write_health(
                state_dir, YAZMA_HATA_SLUG, component="kaydet", health_name=HEALTH_NAME
            )
            return 1, {"yazildi": False, "hata": YAZMA_HATA_SLUG}

    write_elapsed = time.monotonic() - write_started
    write_health(state_dir, "", component="kaydet", health_name=HEALTH_NAME)

    timeout = resolve_derleme_zaman_asimi()
    compile_started = time.monotonic()
    kosuldu, cikis, compile_error = _spawn_compile(
        vault_root, timeout, popen_factory=compile_runner
    )
    compile_elapsed = time.monotonic() - compile_started
    if compile_error is not None:
        write_health(
            state_dir,
            compile_error,
            warning=True,
            component="kaydet",
            health_name=HEALTH_NAME,
        )
    elif cikis not in (0, None):
        write_health(
            state_dir,
            f"warn:kaydet-derleme-basarisiz:{cikis}",
            warning=True,
            component="kaydet",
            health_name=HEALTH_NAME,
        )

    return 0, {
        "yazildi": True,
        "dosya": daily_path.as_posix(),
        "capa": anchor,
        "karakter": len(body),
        "sir_karartildi": hits,
        "derleme": {
            "kosuldu": kosuldu,
            "cikis": cikis,
            "sure_sn": round(compile_elapsed, 3),
        },
        "yazma_sure_sn": round(write_elapsed, 3),
    }


def _human_output(result: dict[str, Any]) -> str:
    if not result.get("yazildi"):
        return f"Kaydedilmedi: {result.get('hata', 'bilinmeyen-hata')}"
    derleme = result.get("derleme") or {}
    first = f"Kaydedildi: {result['dosya']} ({result['karakter']} karakter)"
    if derleme.get("kosuldu"):
        second = f"Derleme çalıştı: çıkış {derleme.get('cikis')} ({derleme.get('sure_sn')} sn)"
    else:
        second = "Derleme çalışmadı (bkz. .state/kaydet-health.json)."
    return first + "\n" + second


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "metin", nargs="?", default=None, help="Note text as a positional argument."
    )
    parser.add_argument(
        "--stdin", action="store_true", help="Read the note text from stdin."
    )
    parser.add_argument(
        "--dosya", type=Path, default=None, help="Read the note text from a UTF-8 file."
    )
    parser.add_argument("--vault-root", type=Path, default=VAULT_ROOT)
    parser.add_argument("--state-dir", type=Path, default=STATE_DIR)
    parser.add_argument(
        "--baslik", default="", help="Optional short title for the note."
    )
    parser.add_argument(
        "--json", action="store_true", help="Print the machine-readable result."
    )
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

    now = flush._event_now()
    try:
        args.state_dir.mkdir(parents=True, exist_ok=True)
    except OSError:
        pass
    try:
        exit_code, result = run(args, now)
    except Exception as exc:  # last-resort guard: never a bare traceback
        try:
            write_health(
                args.state_dir,
                f"{BEKLENMEDIK_SLUG}:{exc.__class__.__name__}",
                component="kaydet",
                health_name=HEALTH_NAME,
            )
        except OSError:
            pass
        result = {"yazildi": False, "hata": BEKLENMEDIK_SLUG}
        exit_code = 1

    if args.json:
        # Exactly one line, prefixed with the fixed marker so the panel can
        # find it in its child process's stdout even when interleaved with
        # whatever the spawned compile subprocess itself printed.
        print(RESULT_MARKER + json.dumps(result, ensure_ascii=False))
    else:
        print(_human_output(result))
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
