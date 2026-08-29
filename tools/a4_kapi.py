#!/usr/bin/env python3
"""A4 compile gate: compare tools and text modes in isolated vault copies."""

# yazan: codex · model: gpt-5.6-sol

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
from typing import Any, Sequence


REPO_ROOT = Path(__file__).resolve().parent.parent
LIVE_VAULT = Path(r"E:\OdenaOS")
GATE_ROOT = REPO_ROOT / ".a4-gate"
JOURNAL_PATH = GATE_ROOT / "journal.json"
DAILY_LIST_PATH = GATE_ROOT / "daily-list.json"
MODES = ("tools", "text")
COPY_NAMES = {"tools": "kopya-tools", "text": "kopya-text"}
DAILY_NAME = re.compile(
    r"(?<!\d)(?P<year>\d{4})-(?P<month>\d{2})"
    r"(?:-(?P<day>\d{2}))?(?!\d)"
)
WIKILINK = re.compile(r"\[\[([^\]]+)\]\]")


class GateError(RuntimeError):
    """A harness invariant was not satisfied."""


def _now() -> str:
    return dt.datetime.now().astimezone().isoformat(timespec="seconds")


def _daily_sort_key(path: Path) -> tuple[dt.date, str]:
    match = DAILY_NAME.search(path.stem)
    if match is None:
        return dt.date.max, path.name
    try:
        parsed = dt.date(
            int(match.group("year")),
            int(match.group("month")),
            int(match.group("day") or "1"),
        )
    except ValueError:
        parsed = dt.date.max
    return parsed, path.name


def _inside(path: Path, parent: Path) -> bool:
    try:
        path.resolve().relative_to(parent.resolve())
    except ValueError:
        return False
    return True


def _assert_write_target(path: Path) -> None:
    if not _inside(path, GATE_ROOT):
        raise GateError(f"sandbox-disinda-yazma-reddedildi:{path}")
    if _inside(path, LIVE_VAULT) or path.resolve() == LIVE_VAULT.resolve():
        raise GateError(f"canli-vault-yazma-reddedildi:{path}")


def _atomic_json(path: Path, payload: Any) -> None:
    _assert_write_target(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _read_object(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise GateError(f"json-okunamadi:{path}:{exc}") from exc
    if not isinstance(value, dict):
        raise GateError(f"json-nesne-degil:{path}")
    return value


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _copy_file(source: Path, destination: Path) -> None:
    _assert_write_target(destination)
    if destination.exists():
        if not destination.is_file() or _sha256(source) != _sha256(destination):
            raise GateError(f"mevcut-sandbox-dosyasi-farkli:{destination}")
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)


def _copy_knowledge_tree(source: Path, destination: Path) -> list[str]:
    """Resume a copy without overwriting; derived unreadable views are rebuilt."""
    _assert_write_target(destination)
    destination.mkdir(parents=True, exist_ok=True)
    unreadable: list[str] = []
    for current, directory_names, file_names in os.walk(source):
        current_path = Path(current)
        relative_dir = current_path.relative_to(source)
        target_dir = destination / relative_dir
        _assert_write_target(target_dir)
        target_dir.mkdir(parents=True, exist_ok=True)
        for directory_name in directory_names:
            (target_dir / directory_name).mkdir(exist_ok=True)
        for file_name in file_names:
            source_file = current_path / file_name
            relative = source_file.relative_to(source).as_posix()
            try:
                _copy_file(source_file, target_dir / file_name)
            except PermissionError:
                if relative == "index.md" or relative.startswith("hubs/"):
                    unreadable.append(relative)
                    continue
                raise
    return unreadable


def _regenerate_derived_knowledge(scripts: Path) -> None:
    environment = os.environ.copy()
    environment.pop("BEYIN_INVOKED_BY", None)
    completed = subprocess.run(
        [sys.executable, str(scripts / "rootmap.py")],
        cwd=scripts,
        env=environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    vault = scripts.parent.parent
    expected = [vault / "knowledge" / "index.md"]
    expected.extend((vault / "knowledge" / "hubs").glob("*.md"))
    if completed.returncode != 0 or not expected or not all(
        path.is_file() for path in expected
    ):
        detail = completed.stderr.strip() or completed.stdout.strip() or "çıktı yok"
        raise GateError(f"rootmap-yeniden-uretim-basarisiz:{detail}")


def _recent_dailies() -> list[Path]:
    daily_dir = LIVE_VAULT / "daily"
    if not daily_dir.is_dir():
        raise GateError(f"canli-daily-yok:{daily_dir}")
    paths = sorted(daily_dir.glob("*.md"), key=_daily_sort_key)
    if len(paths) < 10:
        raise GateError(f"on-gunluk-yok:bulunan={len(paths)}")
    return paths[-10:]


def _mode_paths(mode: str) -> tuple[Path, Path, Path]:
    vault = GATE_ROOT / COPY_NAMES[mode]
    scripts = vault / ".claude" / "scripts"
    return vault, scripts, scripts / ".state"


def _new_compile_state() -> dict[str, Any]:
    return {
        "ingested": {},
        "cursor": "",
        "last_run": "",
        "last_status": "ok",
        "runs": [],
        "concepts_manifest": "",
        "quarantined": {},
    }


def _journal_template(names: list[str]) -> dict[str, Any]:
    modes: dict[str, Any] = {}
    for mode in MODES:
        vault, _scripts, state_dir = _mode_paths(mode)
        modes[mode] = {
            "vault": str(vault.resolve()),
            "knowledge_tree": str((vault / "knowledge").resolve()),
            "state_dir": str(state_dir.resolve()),
            "runs": [],
            "active": None,
        }
    return {
        "schema_version": 1,
        "created_at": _now(),
        "source_vault": str(LIVE_VAULT),
        "daily_files": names,
        "modes": modes,
    }


def _validate_existing_preparation(journal: dict[str, Any]) -> list[str]:
    names = journal.get("daily_files")
    modes = journal.get("modes")
    if not isinstance(names, list) or len(names) != 10:
        raise GateError("journal-gunluk-listesi-gecersiz")
    if not all(isinstance(name, str) and name.endswith(".md") for name in names):
        raise GateError("journal-gunluk-adi-gecersiz")
    if not isinstance(modes, dict):
        raise GateError("journal-modlari-gecersiz")
    for mode in MODES:
        vault, scripts, state_dir = _mode_paths(mode)
        if not (vault / "knowledge").is_dir():
            raise GateError(f"sandbox-knowledge-yok:{mode}")
        if not (scripts / "compile.py").is_file():
            raise GateError(f"sandbox-compile-yok:{mode}")
        if not (state_dir / "compile-state.json").is_file():
            raise GateError(f"sandbox-state-yok:{mode}")
        copied = sorted(path.name for path in (vault / "daily").glob("*.md"))
        if sorted(names) != copied:
            raise GateError(f"sandbox-gunlukleri-farkli:{mode}")
    return names


def hazirla(_args: argparse.Namespace) -> int:
    if JOURNAL_PATH.exists():
        journal = _read_object(JOURNAL_PATH)
        names = _validate_existing_preparation(journal)
        for mode in MODES:
            vault, _scripts, state_dir = _mode_paths(mode)
            _sync_attempted_dailies(journal, mode, vault, state_dir)
        print("A4 kapısı zaten hazırlanmış; kayıtlı liste korunuyor:")
        for name in names:
            print(name)
        return 0

    _assert_write_target(GATE_ROOT / ".write-check")
    GATE_ROOT.mkdir(parents=True, exist_ok=True)

    selected = _recent_dailies()
    names = [path.name for path in selected]
    knowledge_source = LIVE_VAULT / "knowledge"
    if not knowledge_source.is_dir():
        raise GateError(f"canli-knowledge-yok:{knowledge_source}")
    runtime_sources = sorted((REPO_ROOT / "scripts").glob("*.py"))
    if not runtime_sources:
        raise GateError("repo-runtime-betikleri-yok")
    hub_config = LIVE_VAULT / ".claude" / "scripts" / "hub-config.json"
    if not hub_config.is_file():
        raise GateError(f"hub-config-yok:{hub_config}")

    for mode in MODES:
        vault, scripts, state_dir = _mode_paths(mode)
        _assert_write_target(vault)
        daily_destination = vault / "daily"
        daily_destination.mkdir(parents=True, exist_ok=True)
        for source in selected:
            _copy_file(source, daily_destination / source.name)
        knowledge_destination = vault / "knowledge"
        unreadable = _copy_knowledge_tree(
            knowledge_source, knowledge_destination
        )
        scripts.mkdir(parents=True, exist_ok=True)
        for source in runtime_sources:
            _copy_file(source, scripts / source.name)
        _copy_file(hub_config, scripts / "hub-config.json")
        state_path = state_dir / "compile-state.json"
        if not state_path.exists():
            _atomic_json(state_path, _new_compile_state())
        if unreadable or not (knowledge_destination / "index.md").is_file():
            _regenerate_derived_knowledge(scripts)
        source_paths = {
            path.relative_to(knowledge_source).as_posix()
            for path in knowledge_source.rglob("*")
        }
        missing = sorted(
            relative
            for relative in source_paths
            if not (knowledge_destination / relative).exists()
        )
        if missing:
            raise GateError(
                f"sandbox-knowledge-eksik:{mode}:{','.join(missing[:3])}"
            )

    _atomic_json(
        DAILY_LIST_PATH,
        {"selected_at": _now(), "daily_files": names},
    )
    _atomic_json(JOURNAL_PATH, _journal_template(names))
    print("A4 kapısı için seçilen 10 günlük (compile sırası):")
    for name in names:
        print(name)
    return 0


def _concept_snapshot(vault: Path) -> dict[str, str]:
    concepts = vault / "knowledge" / "concepts"
    if not concepts.is_dir():
        return {}
    return {
        path.relative_to(concepts).as_posix(): _sha256(path)
        for path in sorted(concepts.rglob("*.md"))
        if path.is_file()
    }


def _quarantine_snapshot(vault: Path) -> list[str]:
    root = vault / ".stage" / "karantina" / "sema"
    if not root.is_dir():
        return []
    return sorted(path.name for path in root.glob("*.md") if path.is_file())


def _health_snapshot(state_dir: Path) -> dict[str, Any]:
    return _read_object(state_dir / "health.json")


def _pending_names(vault: Path, state_dir: Path, ordered: Sequence[str]) -> list[str]:
    state = _read_object(state_dir / "compile-state.json")
    ingested = state.get("ingested", {})
    quarantined = state.get("quarantined", {})
    if not isinstance(ingested, dict) or not isinstance(quarantined, dict):
        raise GateError("compile-state-pending-alanlari-gecersiz")
    pending = []
    for name in ordered:
        path = vault / "daily" / name
        digest = _sha256(path)
        if ingested.get(name) != digest and digest not in quarantined:
            pending.append(name)
    return pending


def _sync_attempted_dailies(
    journal: dict[str, Any], mode: str, vault: Path, state_dir: Path
) -> list[str]:
    """Keep failed measured attempts from consuming another gate invocation."""
    mode_record = journal.get("modes", {}).get(mode, {})
    runs = mode_record.get("runs", []) if isinstance(mode_record, dict) else []
    if not isinstance(runs, list):
        return []
    state_path = state_dir / "compile-state.json"
    state = _read_object(state_path)
    ingested = state.get("ingested", {})
    quarantined = state.get("quarantined", {})
    if not isinstance(ingested, dict) or not isinstance(quarantined, dict):
        raise GateError("compile-state-senkron-alanlari-gecersiz")
    attempted = state.get("a4_gate_attempted", {})
    if not isinstance(attempted, dict):
        attempted = {}
    changed: list[str] = []
    for run in runs:
        if not isinstance(run, dict):
            continue
        daily = run.get("daily")
        if not isinstance(daily, str):
            continue
        daily_path = vault / "daily" / daily
        if not daily_path.is_file():
            raise GateError(f"journal-gunlugu-sandboxda-yok:{mode}:{daily}")
        digest = _sha256(daily_path)
        attempted[daily] = {
            "digest": digest,
            "compile_status": run.get("compile_status", "unknown"),
            "finished_at": run.get("finished_at", ""),
        }
        if ingested.get(daily) != digest and digest not in quarantined:
            ingested[daily] = digest
            changed.append(daily)
    if changed or state.get("a4_gate_attempted") != attempted:
        state["ingested"] = ingested
        state["a4_gate_attempted"] = attempted
        _atomic_json(state_path, state)
    return changed


def _active_template(daily: str, vault: Path, state_dir: Path) -> dict[str, Any]:
    return {
        "daily": daily,
        "started_at": _now(),
        "started_monotonic_note": "wall timer is finalized by the invoking process",
        "before_concepts": _concept_snapshot(vault),
        "before_sema_quarantine": _quarantine_snapshot(vault),
        "before_health": _health_snapshot(state_dir),
    }


def _wikilink_count(path: Path) -> int:
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError):
        return 0
    return len(WIKILINK.findall(text))


def _new_compile_run(
    state_before: dict[str, Any],
    state_after: dict[str, Any],
    daily: str,
) -> dict[str, Any]:
    before_runs = state_before.get("runs", [])
    after_runs = state_after.get("runs", [])
    before_count = len(before_runs) if isinstance(before_runs, list) else 0
    if isinstance(after_runs, list):
        for item in after_runs[before_count:]:
            if isinstance(item, dict) and item.get("daily_file") == daily:
                return item
        for item in reversed(after_runs):
            if isinstance(item, dict) and item.get("daily_file") == daily:
                return item
    return {}


def _secret_metrics(health: dict[str, Any]) -> tuple[int, list[str]]:
    error = health.get("error", "")
    if not isinstance(error, str) or "secret-detected:" not in error:
        return 0, []
    tail = error.split("secret-detected:", 1)[1]
    parts = tail.split(":", 1)
    families = parts[1].split(",") if len(parts) == 2 else []
    return 1, [item for item in families if item]


def _finalize_run(
    *,
    mode: str,
    active: dict[str, Any],
    state_before: dict[str, Any],
    vault: Path,
    state_dir: Path,
    returncode: int,
    stdout: str,
    stderr: str,
    wall_seconds: float,
) -> dict[str, Any]:
    before = active.get("before_concepts", {})
    if not isinstance(before, dict):
        before = {}
    after = _concept_snapshot(vault)
    created = sorted(name for name in after if name not in before)
    updated = sorted(
        name for name in after if name in before and after[name] != before[name]
    )
    touched = created + updated
    links = {
        name: _wikilink_count(vault / "knowledge" / "concepts" / name)
        for name in touched
    }
    sema_before = active.get("before_sema_quarantine", [])
    if not isinstance(sema_before, list):
        sema_before = []
    sema_after = _quarantine_snapshot(vault)
    sema_new = sorted(set(sema_after) - set(sema_before))
    health = _health_snapshot(state_dir)
    secret_count, secret_families = _secret_metrics(health)
    state_after = _read_object(state_dir / "compile-state.json")
    compile_run = _new_compile_run(
        state_before, state_after, str(active.get("daily", ""))
    )
    logical_status = compile_run.get("status")
    if not isinstance(logical_status, str):
        candidate = state_after.get("last_status", "unknown")
        logical_status = candidate if isinstance(candidate, str) else "unknown"
    backend_error = health.get("error", "")
    if not isinstance(backend_error, str):
        backend_error = ""
    quirks = []
    if mode == "text":
        quirks.append(
            "ollama_runner options/think alanı göndermiyor; qwen3 düşünme modu "
            "gecikmeyi artırabilir; num_predict sınırı uygulanmadı"
        )
    return {
        "daily": active.get("daily", ""),
        "started_at": active.get("started_at", ""),
        "finished_at": _now(),
        "concepts_created": created,
        "concepts_updated": updated,
        "concepts": touched,
        "links_per_concept": links,
        "sema_gate_quarantine_count": len(sema_new),
        "sema_quarantine_files": sema_new,
        "frontmatter_sema_failures": len(sema_new),
        "secret_guard_interventions": secret_count,
        "secret_hit_families": secret_families,
        "exit_status": returncode,
        "compile_status": logical_status,
        "backend_error": backend_error,
        "wall_seconds": round(wall_seconds, 3),
        "stdout": stdout,
        "stderr": stderr,
        "quirks": quirks,
    }


def _mode_environment(mode: str) -> dict[str, str]:
    environment = os.environ.copy()
    environment.pop("BEYIN_INVOKED_BY", None)
    environment["BEYIN_COMPILE_MODE"] = mode
    if mode == "tools":
        environment["BEYIN_MODEL_BACKEND"] = "claude"
    else:
        environment["BEYIN_MODEL_BACKEND"] = "ollama"
        environment["BEYIN_OLLAMA_URL"] = "http://127.0.0.1:11434"
        environment["BEYIN_OLLAMA_MODEL_FAST"] = "qwen3:8b"
        environment["BEYIN_OLLAMA_MODEL_SMART"] = "qwen3:8b"
    return environment


def kos(args: argparse.Namespace) -> int:
    journal = _read_object(JOURNAL_PATH)
    names = _validate_existing_preparation(journal)
    modes = journal["modes"]
    mode_record = modes[args.mod]
    vault, scripts, state_dir = _mode_paths(args.mod)
    active = mode_record.get("active")
    if active is not None:
        raise GateError(
            f"tamamlanmamis-journal-kaydi:{args.mod}:{active.get('daily', '')}; "
            "subprocess sonucu bilinmediği için otomatik yeni model çağrısı yapılmadı"
        )

    _sync_attempted_dailies(journal, args.mod, vault, state_dir)
    pending = _pending_names(vault, state_dir, names)
    if not pending:
        print(f"{args.mod}: bekleyen günlük yok")
        return 0
    daily = pending[0]
    active = _active_template(daily, vault, state_dir)
    mode_record["active"] = active
    _atomic_json(JOURNAL_PATH, journal)

    state_before = _read_object(state_dir / "compile-state.json")
    command = [sys.executable, str(scripts / "compile.py"), "--max-calls", "1"]
    started = time.monotonic()
    try:
        completed = subprocess.run(
            command,
            cwd=scripts,
            env=_mode_environment(args.mod),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        wall_seconds = time.monotonic() - started
        result = _finalize_run(
            mode=args.mod,
            active=active,
            state_before=state_before,
            vault=vault,
            state_dir=state_dir,
            returncode=completed.returncode,
            stdout=completed.stdout,
            stderr=completed.stderr,
            wall_seconds=wall_seconds,
        )
    except OSError as exc:
        wall_seconds = time.monotonic() - started
        result = _finalize_run(
            mode=args.mod,
            active=active,
            state_before=state_before,
            vault=vault,
            state_dir=state_dir,
            returncode=127,
            stdout="",
            stderr=f"{exc.__class__.__name__}: {exc}",
            wall_seconds=wall_seconds,
        )

    mode_record.setdefault("runs", []).append(result)
    mode_record["active"] = None
    _atomic_json(JOURNAL_PATH, journal)
    _sync_attempted_dailies(journal, args.mod, vault, state_dir)
    print(f"mod: {args.mod}")
    print(f"daily: {result['daily']}")
    print(f"compile status: {result['compile_status']}")
    print(f"concepts: {', '.join(result['concepts']) or '-'}")
    print(f"wall seconds: {result['wall_seconds']}")
    if result["backend_error"]:
        print(f"backend error: {result['backend_error']}")
    if result["stderr"]:
        print("stderr:")
        print(result["stderr"].rstrip())
    return 0


def _report_row(mode: str, record: dict[str, Any]) -> list[str]:
    runs = record.get("runs", [])
    if not isinstance(runs, list):
        runs = []
    completed = [run for run in runs if isinstance(run, dict)]
    concepts = sum(len(run.get("concepts", [])) for run in completed)
    link_counts = [
        value
        for run in completed
        for value in run.get("links_per_concept", {}).values()
        if isinstance(value, int)
    ]
    mean_links = sum(link_counts) / len(link_counts) if link_counts else 0.0
    sema = sum(
        int(run.get("frontmatter_sema_failures", 0)) for run in completed
    )
    secrets = sum(
        int(run.get("secret_guard_interventions", 0)) for run in completed
    )
    wall = sum(float(run.get("wall_seconds", 0.0)) for run in completed)
    return [
        mode,
        str(len(completed)),
        str(concepts),
        f"{mean_links:.2f}",
        str(sema),
        str(secrets),
        f"{wall:.3f}",
    ]


def rapor(_args: argparse.Namespace) -> int:
    journal = _read_object(JOURNAL_PATH)
    _validate_existing_preparation(journal)
    modes = journal["modes"]
    print("| Mod | Günlük | Kavram | Ort. link/kavram | Frontmatter/sema hatası | Sır müdahalesi | Toplam saniye |")
    print("| --- | ---: | ---: | ---: | ---: | ---: | ---: |")
    for mode in MODES:
        print("| " + " | ".join(_report_row(mode, modes[mode])) + " |")
    print()
    for mode in MODES:
        vault, _scripts, state_dir = _mode_paths(mode)
        print(f"- {mode} knowledge: `{vault / 'knowledge'}`")
        print(f"- {mode} state: `{state_dir}`")
    return 0


def _parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    prepare = subparsers.add_parser("hazirla", help="iki sandbox kopyasını hazırla")
    prepare.set_defaults(handler=hazirla)
    run = subparsers.add_parser("kos", help="bir pending günlüğü derle")
    run.add_argument("--mod", choices=MODES, required=True)
    run.set_defaults(handler=kos)
    report = subparsers.add_parser("rapor", help="markdown karşılaştırmasını yaz")
    report.set_defaults(handler=rapor)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = _parse_args(argv)
        return int(args.handler(args))
    except GateError as exc:
        print(f"a4-kapi hata: {exc}", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("a4-kapi kesildi; journal active kaydı yeni çağrıyı engeller", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
