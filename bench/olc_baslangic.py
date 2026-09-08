#!/usr/bin/env python3
"""Windows'ta retrieve.py soguk baslangic maliyetlerini ayrisitir.

Her tekrar yeni bir alt surec acarak yorumlayici ve import asamalarini olcer.
"Soguk" burada yeni Python sureci demektir; isletim sistemi dosya onbellegi
bilerek temizlenmez. Olcum ag kullanmaz ve gecici FTS5 verisini cikista siler.
"""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import sys
import time
from typing import Sequence


REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = REPO_ROOT / "scripts"
RETRIEVE_PATH = SCRIPTS_DIR / "retrieve.py"
TEMP_DIR = REPO_ROOT / ".tmp-olcum"
DB_PATH = TEMP_DIR / "notes.db"
DEFAULT_REPEATS = 30
POWERSHELL_REPEATS = 10
NOTE_COUNT = 1_000
QUERY = "hafıza mimarisi"

# retrieve.py'nin modul duzeyindeki gercek importlari. ``hashlib`` yalnizca
# bazi islevlerin icinde tembel import edildigi icin bu soguk import kumesinde
# degildir. Yerel iki modul, retrieve.py ile ayni sekilde scripts/ cwd'sinden
# cozulur.
RETRIEVE_IMPORT_CODE = (
    "import argparse, csv, datetime, dataclasses, json, math, os, pathlib, re, "
    "sqlite3, tempfile, time, typing; import beyin_ortak, sema"
)

BENCH_QUERIES = (
    "hafıza",
    "karar alma",
    "iş akışı",
    "yapay zeka",
    "İstanbul",
    "proje yönetimi",
    "yaratıcı süreç",
    "ikinci beyin",
    "bilgi mimarisi",
    "kavramsal bağlantılar",
    "oyun tasarımı",
    "psikolojik korku",
    "ışık gösterisi",
    "kullanıcı deneyimi",
    "güvenlik sınırı",
    "oturum özeti",
    "kalıcı bellek",
    "üretim ortamı",
    "doğrulama testi",
    "Türkçe tokenizasyon",
)

_TURKISH_I = str.maketrans({"I": "ı", "İ": "i"})
_WORD = re.compile(r"[^\W_]+", re.UNICODE)
_IMPORTTIME_LINE = re.compile(
    r"^import time:\s*(?P<self>\d+)\s*\|\s*(?P<cumulative>\d+)\s*\|\s*(?P<module>.+?)\s*$"
)


class MeasurementError(RuntimeError):
    """Bir olcum asamasi tamamlanamadiginda kullanilir."""


def child_environment() -> dict[str, str]:
    """Olcum alt sureclerinin repo icine .pyc yazmasini engelle."""
    environment = dict(os.environ)
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    return environment


def nearest_rank(values: Sequence[float], percentile: float) -> float:
    """Nearest-rank yuzdeligini dondur."""
    if not values:
        raise ValueError("bos olcum dizisi")
    ordered = sorted(values)
    index = max(0, math.ceil(percentile * len(ordered)) - 1)
    return ordered[index]


def timed_process(
    command: Sequence[str],
    *,
    cwd: Path,
    repeats: int,
) -> list[float]:
    """Komutu her seferinde yeni surecte calistir; sureleri ms olarak dondur."""
    timings: list[float] = []
    for _ in range(repeats):
        started = time.perf_counter_ns()
        completed = subprocess.run(
            list(command),
            cwd=cwd,
            env=child_environment(),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=60,
            check=False,
        )
        elapsed_ms = (time.perf_counter_ns() - started) / 1_000_000
        if completed.returncode != 0:
            detail = completed.stderr.strip() or completed.stdout.strip()
            detail = detail.splitlines()[-1] if detail else "cikis kodu bilinmiyor"
            raise MeasurementError(f"cikis {completed.returncode}: {detail}")
        timings.append(elapsed_ms)
    return timings


def token_text(value: str) -> str:
    """retrieve.py ile ayni ham + bes-karakter token genisletmesini uygula."""
    folded = value.translate(_TURKISH_I).casefold()
    tokens: list[str] = []
    for word in _WORD.findall(folded):
        if len(word) < 3:
            continue
        tokens.append(word)
        if len(word) > 5:
            tokens.append(word[:5])
    return " ".join(tokens)


def build_fixture(path: Path) -> None:
    """retrieve.py schema 2 ile ayni yapida 1000 notluk FTS5 DB kur."""
    phrases = " ".join(BENCH_QUERIES)
    connection = sqlite3.connect(path)
    try:
        connection.executescript(
            """
            PRAGMA journal_mode=DELETE;
            PRAGMA synchronous=FULL;
            CREATE VIRTUAL TABLE notes USING fts5(
                name UNINDEXED,
                title,
                aliases,
                tags,
                body
            );
            CREATE TABLE documents(
                rowid INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                aliases TEXT NOT NULL,
                tags TEXT NOT NULL,
                body TEXT NOT NULL,
                source_date TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """
        )
        for index in range(NOTE_COUNT):
            name = f"sentetik-not-{index:04d}"
            title = f"Sentetik hafiza notu {index:04d}"
            aliases = "bellek bilgi ikinci beyin"
            tags = "hafiza mimarisi proje guvenlik"
            body = f"{phrases}. Sentetik kayit {index:04d}; olcum verisi."
            cursor = connection.execute(
                "INSERT INTO documents"
                "(name, title, aliases, tags, body, source_date) "
                "VALUES (?, ?, ?, ?, ?, ?)",
                (name, title, aliases, tags, body, "2026-01-01T00:00:00+00:00"),
            )
            connection.execute(
                "INSERT INTO notes(rowid, name, title, aliases, tags, body) "
                "VALUES (?, ?, ?, ?, ?, ?)",
                (
                    cursor.lastrowid,
                    name,
                    token_text(title),
                    token_text(aliases),
                    token_text(tags),
                    token_text(body),
                ),
            )
        connection.executemany(
            "INSERT INTO meta(key, value) VALUES (?, ?)",
            (("note_count", str(NOTE_COUNT)), ("built_at", "synthetic")),
        )
        connection.execute("PRAGMA user_version=2")
        connection.commit()
    except sqlite3.OperationalError as exc:
        raise MeasurementError(f"FTS5 DB kurulamadi: {exc}") from exc
    finally:
        connection.close()


def capture_importtime(python: str) -> tuple[float, list[str]]:
    """Bir importtime kosusu ve kümülatif maliyete gore ilk 10 satir."""
    started = time.perf_counter_ns()
    completed = subprocess.run(
        [python, "-X", "importtime", "-c", RETRIEVE_IMPORT_CODE],
        cwd=SCRIPTS_DIR,
        env=child_environment(),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=60,
        check=False,
    )
    elapsed_ms = (time.perf_counter_ns() - started) / 1_000_000
    if completed.returncode != 0:
        detail = completed.stderr.strip().splitlines()
        raise MeasurementError(
            f"importtime cikis {completed.returncode}: "
            f"{detail[-1] if detail else 'ayrinti yok'}"
        )
    parsed: list[tuple[int, str]] = []
    for line in completed.stderr.splitlines():
        match = _IMPORTTIME_LINE.match(line)
        if match is not None:
            parsed.append((int(match.group("cumulative")), line.strip()))
    if not parsed:
        raise MeasurementError("importtime stderr bicimi parse edilemedi")
    parsed.sort(key=lambda item: item[0], reverse=True)
    return elapsed_ms, [line for _, line in parsed[:10]]


def run_bench(python: str) -> tuple[list[float], str]:
    """Mevcut --bench kipini bir kez calistir ve zamanlarini parse et."""
    completed = subprocess.run(
        [
            python,
            str(RETRIEVE_PATH),
            "query",
            "--bench",
            "--db",
            str(DB_PATH),
        ],
        cwd=REPO_ROOT,
        env=child_environment(),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=60,
        check=False,
    )
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip()
        raise MeasurementError(
            f"--bench cikis {completed.returncode}: "
            f"{detail.splitlines()[-1] if detail else 'ayrinti yok'}"
        )
    try:
        report = json.loads(completed.stdout)
        timings = [float(item["ms"]) for item in report["queries"]]
        p95 = float(report["p95_ms"])
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        raise MeasurementError(f"--bench JSON parse edilemedi: {exc}") from exc
    if not timings:
        raise MeasurementError("--bench sorgu zamani dondurmedi")
    return timings, f'"p95_ms": {p95:g}'


def summarize(timings: Sequence[float]) -> tuple[str, str, str]:
    return (
        f"{nearest_rank(timings, 0.50):.3f} ms",
        f"{nearest_rank(timings, 0.95):.3f} ms",
        str(len(timings)),
    )


def failed(reason: str, repeats: int | str) -> tuple[str, str, str]:
    clean = " ".join(reason.replace("|", "/").split())
    value = f"olculemedi: {clean}"
    return value, value, str(repeats)


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "-n",
        "--repeats",
        type=int,
        default=DEFAULT_REPEATS,
        help=f"ilk dort tekrarli olcumun tekrar sayisi (varsayilan: {DEFAULT_REPEATS})",
    )
    args = parser.parse_args(argv)
    if args.repeats < 1:
        parser.error("--repeats en az 1 olmali")
    return args


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    python = shutil.which("python") or sys.executable
    rows: list[tuple[str, str, str, str]] = []
    top_imports: list[str] = []
    bench_p95_line = "olculemedi"

    repeated_stages = (
        ("1. Yalin Python yorumlayici", [python, "-c", "pass"], REPO_ROOT),
        (
            "2. Yorumlayici + retrieve importlari",
            [python, "-c", RETRIEVE_IMPORT_CODE],
            SCRIPTS_DIR,
        ),
    )
    for label, command, cwd in repeated_stages:
        try:
            rows.append((label, *summarize(timed_process(command, cwd=cwd, repeats=args.repeats))))
        except (MeasurementError, OSError, subprocess.SubprocessError) as exc:
            rows.append((label, *failed(str(exc), args.repeats)))

    try:
        importtime_ms, top_imports = capture_importtime(python)
        rows.append(("3. Tek seferlik -X importtime", *summarize([importtime_ms])))
    except (MeasurementError, OSError, subprocess.SubprocessError) as exc:
        rows.append(("3. Tek seferlik -X importtime", *failed(str(exc), 1)))

    powershell = shutil.which("powershell")
    if powershell is None:
        rows.append(
            (
                "5. PowerShell wrapper sureci",
                *failed("powershell PATH uzerinde bulunamadi", POWERSHELL_REPEATS),
            )
        )
    else:
        try:
            timings = timed_process(
                [powershell, "-NoProfile", "-Command", "exit"],
                cwd=REPO_ROOT,
                repeats=POWERSHELL_REPEATS,
            )
            powershell_row = ("5. PowerShell wrapper sureci", *summarize(timings))
        except (MeasurementError, OSError, subprocess.SubprocessError) as exc:
            powershell_row = (
                "5. PowerShell wrapper sureci",
                *failed(str(exc), POWERSHELL_REPEATS),
            )

    cleanup_status = ".tmp-olcum temizlendi"
    cold_row: tuple[str, str, str, str]
    warm_row: tuple[str, str, str, str]
    if TEMP_DIR.exists():
        reason = ".tmp-olcum zaten var; onceden var olan veri silinmedi"
        cold_row = ("4. Uctan uca soguk sorgu", *failed(reason, args.repeats))
        warm_row = ("6. Sicak --bench tabani", *failed(reason, len(BENCH_QUERIES)))
        cleanup_status = ".tmp-olcum korundu (calisma basinda zaten vardi)"
    else:
        try:
            TEMP_DIR.mkdir()
            build_fixture(DB_PATH)
            cold_command = [
                python,
                str(RETRIEVE_PATH),
                "query",
                QUERY,
                "--limit",
                "3",
                "--format",
                "plain",
                "--db",
                str(DB_PATH),
            ]
            cold_row = (
                "4. Uctan uca soguk sorgu",
                *summarize(
                    timed_process(
                        cold_command,
                        cwd=REPO_ROOT,
                        repeats=args.repeats,
                    )
                ),
            )
            bench_timings, bench_p95_line = run_bench(python)
            warm_row = ("6. Sicak --bench tabani", *summarize(bench_timings))
        except (MeasurementError, OSError, sqlite3.Error, subprocess.SubprocessError) as exc:
            reason = str(exc)
            cold_row = ("4. Uctan uca soguk sorgu", *failed(reason, args.repeats))
            warm_row = (
                "6. Sicak --bench tabani",
                *failed(reason, len(BENCH_QUERIES)),
            )
        finally:
            resolved_temp = TEMP_DIR.resolve()
            if resolved_temp.parent != REPO_ROOT.resolve() or resolved_temp.name != ".tmp-olcum":
                cleanup_status = f"GUVENLIK: gecici yol silinmedi: {resolved_temp}"
            else:
                try:
                    shutil.rmtree(resolved_temp)
                except FileNotFoundError:
                    pass
                except OSError as exc:
                    cleanup_status = f".tmp-olcum temizlenemedi: {exc}"

    rows.extend((cold_row, powershell_row, warm_row))
    rows.sort(key=lambda row: int(row[0].split(".", 1)[0]))

    print("| Asama | p50 | p95 | N |")
    print("| --- | ---: | ---: | ---: |")
    for label, p50, p95, count in rows:
        print(f"| {label} | {p50} | {p95} | {count} |")

    print("\nImporttime ilk 10 (kumulatif us azalan):")
    if top_imports:
        for line in top_imports:
            print(f"- `{line}`")
    else:
        print("- olculemedi")
    print(f"\nWarm --bench p95 satiri: `{bench_p95_line}`")
    print(f"Gecici dizin: {cleanup_status}")
    print(f"Yeniden calistirma: `python tools/olc_baslangic.py --repeats {args.repeats}`")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
