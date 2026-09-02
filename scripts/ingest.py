#!/usr/bin/env python3
# yazan: codex
# model: gpt-5.6-sol
"""Arşiv içe aktarıcı: eski Claude transkriptleri, Codex rollout'ları, web ZIP.

Her yol ``--dry-run`` destekler, tek kilitle koşar ve daima 0 ile çıkar.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import sys
import time
from typing import Any, Sequence

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent))

import ingest_claude
import ingest_codex
import ingest_common
import ingest_gemini
import ingest_web
import nezaket
from ingest_common import Session


DEFAULT_MAX_SESSIONS = 10
DEFAULT_SLEEP = 4.0

TABLE_HEADER = (
    f"{'KAYNAK':<14} {'TARİH':<10} {'SAAT':<5} {'ANAHTAR':<9} "
    f"{'TUR':>4} {'KARAKTER':>9}  KÖKEN"
)


def _order_key(session: Session, source: str) -> tuple[int, dt.datetime]:
    """Eskiden yeniye; yapılandırılmış ek Claude projeleri en sona."""
    group = 0
    if source == ingest_claude.SOURCE and ingest_claude.is_extra_project(session):
        group = 1
    return group, session.when


def _ordered(sessions: list[Session], source: str) -> list[Session]:
    return sorted(sessions, key=lambda item: _order_key(item, source))


def _print_table(
    sessions: list[Session],
    max_sessions: int,
    min_turns: int = ingest_common.MIN_TURNS,
) -> None:
    print(TABLE_HEADER)
    print("-" * len(TABLE_HEADER))
    total_turns = 0
    total_chars = 0
    callable_sessions = 0
    for session in sessions:
        turns, chars = ingest_common.transcript_size(session.turns)
        total_turns += turns
        total_chars += chars
        if turns >= min_turns:
            callable_sessions += 1
        label = session.label or session.source
        print(
            f"{label:<14} {session.when.strftime('%Y-%m-%d'):<10} "
            f"{session.when.strftime('%H:%M'):<5} {session.key[:8]:<9} "
            f"{turns:>4} {chars:>9}  {session.origin}"
        )
    print("-" * len(TABLE_HEADER))
    print(f"TOPLAM aday   : {len(sessions)}")
    print(f"TOPLAM tur    : {total_turns}")
    print(f"TOPLAM karakter: {total_chars}")
    print(
        "Tahmini model çağrısı: "
        f"{min(callable_sessions, max_sessions)} "
        f"(özetlenebilir {callable_sessions}, üst sınır {max_sessions})"
    )
    print("(kuru koşu — hiçbir şey yazılmadı)")


def _process(
    sessions: list[Session],
    source: str,
    state: dict[str, Any],
    args: argparse.Namespace,
    state_dir: Path,
    vault_root: Path,
    min_turns: int = ingest_common.MIN_TURNS,
) -> dict[str, int]:
    counts = {
        "scanned": len(sessions),
        "ingested": 0,
        "skipped": 0,
        "failed": 0,
    }
    calls_made = 0
    for index, session in enumerate(sessions):
        if index >= args.max_sessions:
            break
        will_call = len(session.turns) >= min_turns
        if will_call and calls_made:
            time.sleep(args.sleep)
        if will_call:
            calls_made += 1
        result = ingest_common.summarize_session(
            session,
            vault_root,
            state_dir,
            args.model,
            min_turns,
        )
        if result.status == "ok":
            try:
                daily = ingest_common.append_historical(
                    vault_root,
                    result.summary,
                    session,
                    args.model,
                )
            except OSError:
                ingest_common.record_done(
                    state,
                    source,
                    session.key,
                    "fail:daily-append-failed",
                    watermark=session.watermark,
                )
                counts["failed"] += 1
                _persist(state, source, session, state_dir)
                continue
            ingest_common.record_done(
                state,
                source,
                session.key,
                "appended",
                daily=daily,
                watermark=session.watermark,
            )
            counts["ingested"] += 1
        elif result.status == "bos":
            ingest_common.record_done(
                state,
                source,
                session.key,
                "bos",
                watermark=session.watermark,
            )
            counts["skipped"] += 1
        else:
            ingest_common.record_done(
                state,
                source,
                session.key,
                f"fail:{result.detail}",
                watermark=session.watermark,
            )
            counts["failed"] += 1
        _persist(state, source, session, state_dir)
    return counts


def _persist(
    state: dict[str, Any],
    source: str,
    session: Session,
    state_dir: Path,
) -> None:
    """Her oturumdan sonra deftere yaz — kesinti sonrası kaldığı yerden devam."""
    if source == ingest_codex.SOURCE:
        path = Path(session.origin)
        try:
            file_stat = path.stat()
            ingest_common.record_file(
                state,
                source,
                path.name,
                file_stat.st_size,
                file_stat.st_mtime,
            )
        except OSError:
            pass
    try:
        ingest_common.save_state(state, state_dir)
    except OSError:
        ingest_common.write_health(
            state_dir,
            "state-write-failed",
            component="ingest",
            health_name=ingest_common.HEALTH_NAME,
        )


def _finish(
    source: str,
    state: dict[str, Any],
    counts: dict[str, int],
    state_dir: Path,
) -> None:
    last_run = {
        "source": source,
        "ts": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        "counts": counts,
        # Effective model-call bound, so an `agy-timeout`/`codex-timeout` in this
        # run can be read against the value that produced it.
        "timeout": ingest_common.claude_runner.resolve_timeout("ingest")[0],
    }
    state["last_run"] = last_run
    try:
        ingest_common.save_state(state, state_dir)
    except OSError:
        ingest_common.write_health(
            state_dir,
            "state-write-failed",
            component="ingest",
            health_name=ingest_common.HEALTH_NAME,
        )
    ingest_common.write_health(
        state_dir,
        "",
        counts=counts,
        last_run=last_run,
        component="ingest",
        health_name=ingest_common.HEALTH_NAME,
    )


def run_claude(args: argparse.Namespace, state_dir: Path, vault_root: Path) -> int:
    state = ingest_common.load_state(state_dir)
    sessions, skips = ingest_claude.candidates(
        state,
        state_dir=state_dir,
        only_project=args.only_project,
        retry_failed=args.retry_failed,
    )
    sessions = _ordered(sessions, ingest_claude.SOURCE)
    if args.dry_run:
        _print_table(sessions, args.max_sessions)
        return 0
    for key, status in skips:
        ingest_common.record_done(state, ingest_claude.SOURCE, key, status)
    counts = _process(
        sessions,
        ingest_claude.SOURCE,
        state,
        args,
        state_dir,
        vault_root,
    )
    counts["skipped"] += len(skips)
    _finish(ingest_claude.SOURCE, state, counts, state_dir)
    return 0


def run_codex(args: argparse.Namespace, state_dir: Path, vault_root: Path) -> int:
    state = ingest_common.load_state(state_dir)
    sessions, rejects = ingest_codex.candidates(
        state,
        retry_failed=args.retry_failed,
    )
    sessions = _ordered(sessions, ingest_codex.SOURCE)
    if args.dry_run:
        _print_table(sessions, args.max_sessions)
        print(f"Elenen rollout dosyası: {len(rejects)}")
        return 0
    for name, size, mtime, _reason in rejects:
        ingest_common.record_file(state, ingest_codex.SOURCE, name, size, mtime)
    counts = _process(
        sessions,
        ingest_codex.SOURCE,
        state,
        args,
        state_dir,
        vault_root,
    )
    counts["skipped"] += len(rejects)
    _finish(ingest_codex.SOURCE, state, counts, state_dir)
    return 0


def run_web(args: argparse.Namespace, state_dir: Path, vault_root: Path) -> int:
    state = ingest_common.load_state(state_dir)
    zip_path = args.zip
    if zip_path is None:
        zip_path = ingest_web.newest_zip()
    if zip_path is None:
        print("İçe aktarılacak ZIP bulunamadı (.import klasörü boş).")
        ingest_common.write_health(
            state_dir,
            "web-zip-missing",
            component="ingest",
            health_name=ingest_common.HEALTH_NAME,
        )
        return 0
    sessions, error = ingest_web.candidates(
        state,
        Path(zip_path),
        retry_failed=args.retry_failed,
        resummarize=args.web_resummarize,
        max_conversations=args.max_conversations,
    )
    if error:
        print(f"ZIP okunamadı: {error}")
        ingest_common.write_health(
            state_dir,
            f"web:{error}",
            component="ingest",
            health_name=ingest_common.HEALTH_NAME,
        )
        return 0
    sessions = _ordered(sessions, ingest_web.SOURCE)
    if args.dry_run:
        _print_table(sessions, args.max_sessions)
        return 0
    counts = _process(
        sessions,
        ingest_web.SOURCE,
        state,
        args,
        state_dir,
        vault_root,
    )
    _finish(ingest_web.SOURCE, state, counts, state_dir)
    return 0


def run_gemini(args: argparse.Namespace, state_dir: Path, vault_root: Path) -> int:
    state = ingest_common.load_state(state_dir)
    sessions, error = ingest_gemini.candidates(
        state,
        retry_failed=args.retry_failed,
    )
    if error:
        print(f"Gemini kayıtları okunamadı: {error}")
        ingest_common.write_health(
            state_dir,
            error,
            component="ingest",
            health_name=ingest_common.HEALTH_NAME,
        )
        return 0
    sessions = _ordered(sessions, ingest_gemini.SOURCE)
    if args.dry_run:
        _print_table(sessions, args.max_sessions, min_turns=2)
        return 0
    counts = _process(
        sessions,
        ingest_gemini.SOURCE,
        state,
        args,
        state_dir,
        vault_root,
        min_turns=2,
    )
    _finish(ingest_gemini.SOURCE, state, counts, state_dir)
    return 0


def run_status(args: argparse.Namespace, state_dir: Path, vault_root: Path) -> int:
    state = ingest_common.load_state(state_dir)
    sources = state.get("sources", {})
    if not sources:
        print("Durum defteri boş — henüz hiçbir kaynak işlenmedi.")
    for source in sorted(sources):
        bucket = sources[source] if isinstance(sources[source], dict) else {}
        done = bucket.get("done", {})
        done = done if isinstance(done, dict) else {}
        statuses: dict[str, int] = {}
        for entry in done.values():
            if not isinstance(entry, dict):
                continue
            status = str(entry.get("status", "?"))
            bucket_name = status if not status.startswith("fail") else "fail"
            statuses[bucket_name] = statuses.get(bucket_name, 0) + 1
        files = bucket.get("files", {})
        files_count = len(files) if isinstance(files, dict) else 0
        detail = ", ".join(
            f"{name}: {count}" for name, count in sorted(statuses.items())
        )
        print(
            f"{source:<14} kayıt: {len(done):<5} "
            f"dosya-filtresi: {files_count:<5} {detail}"
        )
    last_run = state.get("last_run", {})
    if isinstance(last_run, dict) and last_run:
        print(f"Son koşu: {json.dumps(last_run, ensure_ascii=False)}")
    else:
        print("Son koşu: yok")
    return 0


def _common_flags(parser: argparse.ArgumentParser, suppress: bool) -> None:
    """Genel bayraklar hem alt komuttan önce hem sonra yazılabilsin diye.

    Alt ayrıştırıcıda varsayılan SUPPRESS'tir; böylece alt komut bayrağı
    yazılmadığında üst ayrıştırıcının değeri ezilmez.
    """

    def default(value: Any) -> Any:
        return argparse.SUPPRESS if suppress else value

    parser.add_argument(
        "--dry-run",
        action="store_true",
        default=default(False),
    )
    parser.add_argument(
        "--max-sessions",
        type=int,
        default=default(DEFAULT_MAX_SESSIONS),
        help=f"Bu koşudaki azami oturum (varsayılan {DEFAULT_MAX_SESSIONS}).",
    )
    parser.add_argument(
        "--sleep",
        type=float,
        default=default(DEFAULT_SLEEP),
        help=f"Model çağrıları arası bekleme (varsayılan {DEFAULT_SLEEP}).",
    )
    parser.add_argument(
        "--model",
        default=default(None),
        help=f"Özetçi model (varsayılan {ingest_common.DEFAULT_MODEL}).",
    )
    parser.add_argument(
        "--retry-failed",
        action="store_true",
        default=default(False),
    )


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    common = argparse.ArgumentParser(add_help=False)
    _common_flags(common, suppress=True)

    parser = argparse.ArgumentParser(description=__doc__)
    _common_flags(parser, suppress=False)
    parser.add_argument(
        "--nezaket-del",
        action="store_true",
        help="Bypass the A7 politeness gate for this run.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    claude_parser = subparsers.add_parser("claude", parents=[common])
    claude_parser.add_argument("--only-project", default=None)
    claude_parser.set_defaults(handler=run_claude)

    codex_parser = subparsers.add_parser("codex", parents=[common])
    codex_parser.set_defaults(handler=run_codex)

    web_parser = subparsers.add_parser("web", parents=[common])
    web_parser.add_argument("--zip", type=Path, default=None)
    web_parser.add_argument("--web-resummarize", action="store_true")
    web_parser.add_argument(
        "--max-conversations",
        type=int,
        default=ingest_web.DEFAULT_MAX_CONVERSATIONS,
    )
    web_parser.set_defaults(handler=run_web)

    gemini_parser = subparsers.add_parser("gemini", parents=[common])
    gemini_parser.set_defaults(handler=run_gemini)

    status_parser = subparsers.add_parser("status", parents=[common])
    status_parser.set_defaults(handler=run_status)

    args = parser.parse_args(argv)
    if args.model is None:
        args.model = "codex" if args.command == "gemini" else ingest_common.DEFAULT_MODEL
    return args


def main(argv: Sequence[str] | None = None) -> int:
    if os.environ.get("BEYIN_INVOKED_BY"):
        return 0

    state_dir = ingest_common.STATE_DIR
    try:
        args = _parse_args(argv)
    except SystemExit as exc:
        if exc.code:
            ingest_common.write_health(
                state_dir,
                "invalid-arguments",
                component="ingest",
                health_name=ingest_common.HEALTH_NAME,
            )
        return 0

    # --dry-run and status take no lock and make no model call, so they are
    # exempt from the gate rather than getting queued for nothing.
    if not (args.dry_run or args.command == "status"):
        effective_argv = list(argv) if argv is not None else list(sys.argv[1:])
        if nezaket.kapi("ingest", effective_argv, state_dir).mesgul:
            return nezaket.EX_TEMPFAIL

    if args.max_sessions < 1 or args.sleep < 0:
        ingest_common.write_health(
            state_dir,
            "invalid-limits",
            component="ingest",
            health_name=ingest_common.HEALTH_NAME,
        )
        return 0

    vault_root = ingest_common.VAULT_ROOT
    try:
        # Kuru koşu ve durum raporu hiçbir dosyaya dokunmaz — kilit de açmaz.
        if args.dry_run or args.command == "status":
            return args.handler(args, state_dir, vault_root)
        with ingest_common.exclusive_lock(state_dir) as acquired:
            if not acquired:
                return 0
            return args.handler(args, state_dir, vault_root)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        ingest_common.write_health(
            state_dir,
            f"input:{str(exc) or exc.__class__.__name__}",
            component="ingest",
            health_name=ingest_common.HEALTH_NAME,
        )
        return 0
    except Exception as exc:  # İçe aktarıcı asla yükselmez.
        ingest_common.write_health(
            state_dir,
            f"unexpected:{exc.__class__.__name__}",
            component="ingest",
            health_name=ingest_common.HEALTH_NAME,
        )
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
