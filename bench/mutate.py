#!/usr/bin/env python3
"""Mutation check for Origin of Memory 2.0 acceptance gate 9.

Applies one mutant at a time to a single source file, runs the full test suite,
and classifies the mutant as killed when the failing-test count rises above the
baseline. The tree is never left mutated: the original bytes are restored in a
finally block and `git status --porcelain -- src` is verified at the end.

This is a measurement tool under bench/. It is not shipped with oom.exe.

Usage (from the repository root):

    python bench/mutate.py
    python bench/mutate.py --list
    python bench/mutate.py --only M4-directive-never-refuses
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from datetime import date
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_MUTANTS = REPO_ROOT / "bench" / "mutants.json"
DEFAULT_RESULTS = REPO_ROOT / "bench" / "results"

TEST_COMMAND = ["dotnet", "test", "Oom.sln", "-c", "Release", "--no-restore", "-v", "q"]

SUMMARY_PATTERNS = [
    re.compile(
        r"Failed:\s*(?P<failed>\d+),\s*Passed:\s*(?P<passed>\d+),"
        r"\s*Skipped:\s*(?P<skipped>\d+),\s*Total:\s*(?P<total>\d+)"
    ),
    # Turkish CLI output, in case DOTNET_CLI_UI_LANGUAGE is ignored.
    re.compile(
        r"Ba\u015far\u0131s\u0131z:\s*(?P<failed>\d+),\s*Ba\u015far\u0131l\u0131:\s*(?P<passed>\d+),"
        r"\s*Atlanan:\s*(?P<skipped>\d+),\s*Toplam:\s*(?P<total>\d+)"
    ),
]
FAIL_LINE = re.compile(r"\[xUnit\.net[^\]]*\]\s+(?P<name>.+?)\s+\[FAIL\]\s*$")
BUILD_ERROR = re.compile(r"\berror\s+(?:CS|MSB|NETSDK)\d+", re.IGNORECASE)
SCAR_ID = re.compile(r"\bY-\d+")


class Abort(RuntimeError):
    """A safety precondition failed; nothing was mutated."""


# --------------------------------------------------------------------------- git


def git(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["git", *args],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def src_is_clean() -> tuple[bool, str]:
    proc = git("status", "--porcelain", "--", "src")
    if proc.returncode != 0:
        raise Abort(f"git status failed: {proc.stderr.strip()}")
    output = proc.stdout.strip()
    return (output == "", output)


def src_diff_is_empty() -> bool:
    return git("diff", "--quiet", "--", "src").returncode == 0


# ------------------------------------------------------------------- test runner


def run_tests() -> dict:
    env = dict(os.environ)
    env.setdefault("DOTNET_CLI_UI_LANGUAGE", "en")
    env.setdefault("VSLANG", "1033")
    started = time.monotonic()
    proc = subprocess.run(
        TEST_COMMAND,
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
    )
    elapsed = round(time.monotonic() - started, 1)
    output = (proc.stdout or "") + "\n" + (proc.stderr or "")

    counts = None
    for pattern in SUMMARY_PATTERNS:
        match = pattern.search(output)
        if match:
            counts = {k: int(v) for k, v in match.groupdict().items()}
            break

    failing = []
    for line in output.splitlines():
        match = FAIL_LINE.search(line.rstrip())
        if match:
            name = match.group("name").strip()
            if name not in failing:
                failing.append(name)

    return {
        "exit_code": proc.returncode,
        "seconds": elapsed,
        "counts": counts,
        "built": counts is not None,
        "build_errors": sorted(set(BUILD_ERROR.findall(output))) if counts is None else [],
        "failing": failing,
        "line": test_line(counts, proc.returncode, elapsed),
    }


def test_line(counts: dict | None, exit_code: int, elapsed: float) -> str:
    if counts is None:
        return f"build/run failed (exit {exit_code}) in {elapsed}s — no test summary"
    return (
        f"failed {counts['failed']} / passed {counts['passed']} / total {counts['total']}"
        f" in {elapsed}s"
    )


def scars(names: list[str]) -> list[str]:
    found = []
    for name in names:
        match = SCAR_ID.search(name)
        ident = match.group(0) if match else name
        if ident not in found:
            found.append(ident)
    return found


# ----------------------------------------------------------------------- mutants


def load_mutants(path: Path, only: str | None) -> list[dict]:
    data = json.loads(path.read_text(encoding="utf-8"))
    mutants = data["mutants"] if isinstance(data, dict) else data
    if only:
        mutants = [m for m in mutants if m["id"] == only]
        if not mutants:
            raise Abort(f"no mutant with id '{only}' in {path}")
    ids = [m["id"] for m in mutants]
    if len(set(ids)) != len(ids):
        raise Abort("duplicate mutant ids in the definition file")
    return mutants


def encode_for(original: bytes, text: str) -> bytes:
    """Encode a search/replace string using the target file's line ending."""
    newline = b"\r\n" if b"\r\n" in original else b"\n"
    return text.replace("\r\n", "\n").encode("utf-8").replace(b"\n", newline)


def apply_mutant(mutant: dict) -> tuple[Path, bytes]:
    target = REPO_ROOT / mutant["file"]
    if not target.is_file():
        raise Abort(f"{mutant['id']}: file not found: {mutant['file']}")
    original = target.read_bytes()
    search = encode_for(original, mutant["search"])
    replace = encode_for(original, mutant["replace"])
    hits = original.count(search)
    if hits != 1:
        raise Abort(
            f"{mutant['id']}: search string matched {hits} times in {mutant['file']}"
            " (exactly one match required)"
        )
    target.write_bytes(original.replace(search, replace, 1))
    return target, original


def evaluate(mutant: dict, baseline: dict) -> dict:
    record = {
        "id": mutant["id"],
        "boundary": mutant["boundary"],
        "file": mutant["file"],
        "description": mutant["description"],
        "applied": False,
        "test_line": "",
        "killed": False,
        "killing_tests": [],
        "killing_scars": [],
        "note": "",
    }

    target, original = apply_mutant(mutant)
    record["applied"] = True
    try:
        result = run_tests()
    finally:
        target.write_bytes(original)
        if target.read_bytes() != original:
            raise Abort(f"{mutant['id']}: could not restore {mutant['file']}")

    record["test_line"] = result["line"]
    record["seconds"] = result["seconds"]

    if not result["built"]:
        compile_visible = bool(mutant.get("compile_visible", False))
        record["killed"] = compile_visible
        record["note"] = (
            "build failed; mutant was declared compile_visible, counted as killed"
            if compile_visible
            else "build failed for a behavioural mutant — invalid mutant, not a kill"
        )
        record["build_errors"] = result["build_errors"]
        return record

    record["counts"] = result["counts"]
    new_failures = [n for n in result["failing"] if n not in baseline["failing"]]
    record["killing_tests"] = new_failures
    record["killing_scars"] = scars(new_failures)
    record["killed"] = result["counts"]["failed"] > baseline["counts"]["failed"]
    if record["killed"] and not new_failures:
        record["note"] = "failing count rose but no new test name was parsed"
    elif not record["killed"]:
        record["note"] = "survived: no existing test detects this change"
    return record


# ------------------------------------------------------------------------ report


def markdown(baseline: dict, records: list[dict], results_path: Path, clean: bool) -> str:
    killed = sum(1 for r in records if r["killed"])
    lines = [
        "# Mutation check (gate 9)",
        "",
        f"Baseline: {baseline['line']}",
        f"Mutants: {len(records)} — killed {killed}, survived {len(records) - killed}",
        f"Result: {'GREEN' if killed == len(records) else 'RED'}"
        f" (gate 9 needs every mutant killed by an existing test)",
        "",
        "| id | boundary | applied | test line | killed | killing tests |",
        "| --- | --- | --- | --- | --- | --- |",
    ]
    for r in records:
        killing = ", ".join(r["killing_scars"]) or "—"
        lines.append(
            f"| {r['id']} | {r['boundary']} | {'yes' if r['applied'] else 'no'} |"
            f" {r['test_line']} | {'yes' if r['killed'] else 'NO'} | {killing} |"
        )
    survivors = [r for r in records if not r["killed"]]
    if survivors:
        lines += ["", "## Survivors", ""]
        for r in survivors:
            lines.append(f"- `{r['id']}` — {r['description']}")
    lines += [
        "",
        f"Results JSON: `{results_path.relative_to(REPO_ROOT).as_posix()}`",
        f"Working tree after the run: src {'clean' if clean else 'DIRTY — INVESTIGATE'}",
    ]
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description="Gate 9 mutation check for Origin of Memory 2.0.")
    parser.add_argument("--mutants", type=Path, default=DEFAULT_MUTANTS, help="mutant definition file")
    parser.add_argument("--out-dir", type=Path, default=DEFAULT_RESULTS, help="results directory")
    parser.add_argument("--only", help="run a single mutant by id")
    parser.add_argument("--list", action="store_true", help="list mutant ids and exit")
    args = parser.parse_args()

    try:
        mutants = load_mutants(args.mutants, args.only)
    except Abort as error:
        print(f"abort: {error}", file=sys.stderr)
        return 2

    if args.list:
        for mutant in mutants:
            print(f"{mutant['id']}\t{mutant['boundary']}\t{mutant['file']}")
        return 0

    clean, dirty = src_is_clean()
    if not clean:
        print("abort: src/ is not clean; commit or stash first:", file=sys.stderr)
        print(dirty, file=sys.stderr)
        return 2

    print(f"baseline: running {' '.join(TEST_COMMAND)} with no mutant ...", flush=True)
    baseline = run_tests()
    if not baseline["built"]:
        print(f"abort: the baseline build/run failed — {baseline['line']}", file=sys.stderr)
        return 2
    print(f"baseline: {baseline['line']}", flush=True)

    records: list[dict] = []
    try:
        for index, mutant in enumerate(mutants, start=1):
            print(f"[{index}/{len(mutants)}] {mutant['id']} ...", flush=True)
            record = evaluate(mutant, baseline)
            print(
                f"    {record['test_line']} -> {'killed' if record['killed'] else 'SURVIVED'}"
                f" {', '.join(record['killing_scars'])}".rstrip(),
                flush=True,
            )
            records.append(record)
    except Abort as error:
        print(f"abort: {error}", file=sys.stderr)
        return 2

    final_clean, final_dirty = src_is_clean()
    diff_empty = src_diff_is_empty()
    if not final_clean or not diff_empty:
        print("ERROR: src/ is not clean after the run:", file=sys.stderr)
        print(final_dirty, file=sys.stderr)

    args.out_dir.mkdir(parents=True, exist_ok=True)
    results_path = args.out_dir / f"mutation-{date.today().isoformat()}.json"
    payload = {
        "tool": "bench/mutate.py",
        "gate": 9,
        "date": date.today().isoformat(),
        "command": " ".join(TEST_COMMAND),
        "mutant_definitions": args.mutants.relative_to(REPO_ROOT).as_posix(),
        "baseline": {
            "line": baseline["line"],
            "counts": baseline["counts"],
            "failing": baseline["failing"],
            "failing_scars": scars(baseline["failing"]),
            "seconds": baseline["seconds"],
        },
        "mutants": records,
        "killed": sum(1 for r in records if r["killed"]),
        "survived": sum(1 for r in records if not r["killed"]),
        "result": "green" if all(r["killed"] for r in records) else "red",
        "src_clean_after_run": final_clean and diff_empty,
    }
    results_path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print()
    print(markdown(baseline, records, results_path, final_clean and diff_empty))
    return 0 if payload["result"] == "green" and payload["src_clean_after_run"] else 1


if __name__ == "__main__":
    sys.exit(main())
