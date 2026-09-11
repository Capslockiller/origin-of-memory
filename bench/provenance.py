"""Provenance schema helpers for bench/results, per bench/PROVENANCE.md SS2-SS4.

This module holds the reusable, dependency-free logic that lets a harness
(bench/kos20.py and, in future, its siblings) enforce the contract structurally
instead of by convention:

  - `assess` implements SS3's run_status rules ("ok" | "degraded" | "invalid"),
    the classification a harness MUST compute before it is allowed to print a
    verdict.
  - `redact_verdicts` implements SS3's binding rule that once run_status is not
    "ok", every `pass`/`decision` field anywhere in the payload MUST be null --
    a degraded or invalid run may still report raw numbers, but it may not
    assert that a gate was passed.
  - `sha256_file` / `git_commit` are the two provenance-identity helpers SS2
    asks every results file to carry (`binary_sha256`, `source_commit`).

Standard library only. Pure functions -- no filesystem or subprocess access
except in `sha256_file` and `git_commit`, which are thin, exception-safe
wrappers so callers never have to guard them.
"""

from __future__ import annotations

import hashlib
import subprocess
from pathlib import Path

RUN_STATUSES = ("ok", "degraded", "invalid")


def assess(index: dict, scored_rows: int) -> tuple[str, list[str]]:
    """PROVENANCE.md SS3: classify a run as ok / degraded / invalid.

    "invalid" -- the corpus is empty and the reported numbers are meaningless
    on their own terms: `index["concept_files"]` is None or 0, or
    `scored_rows` is 0.

    "degraded" -- something failed that a reader could not otherwise infer
    from the top-level numbers: `index["error"]` is not None, or
    `index["index_notes"]` is None.

    "invalid" wins over "degraded" when both apply, but the returned reasons
    name every condition found, not only the winning category's -- a reader
    refusing a verdict deserves the full picture, not just the deciding one.
    """
    invalid_reasons: list[str] = []
    degraded_reasons: list[str] = []

    concept_files = index.get("concept_files")
    if concept_files is None or concept_files == 0:
        invalid_reasons.append(f"empty corpus: index.concept_files={concept_files!r}")
    if scored_rows == 0:
        invalid_reasons.append("empty corpus: scored_rows=0")

    error = index.get("error")
    if error is not None:
        degraded_reasons.append(f"index.error is not null: {error}")
    if index.get("index_notes") is None:
        degraded_reasons.append(
            "index.index_notes is null -- a recorded failure a reader could not otherwise infer"
        )

    if invalid_reasons:
        return "invalid", invalid_reasons + degraded_reasons
    if degraded_reasons:
        return "degraded", degraded_reasons
    return "ok", []


def redact_verdicts(payload: dict) -> list[str]:
    """PROVENANCE.md SS3's binding rule: once run_status != "ok", every field
    named `pass` (and everything nested beneath it) and every field named
    `decision` (likewise) MUST be null -- none may read true.

    Walks the payload recursively (dicts and lists), nulls each such key's
    value in place, and returns the dotted paths it nulled so the caller can
    log what happened. Only called when run_status != "ok"; callers must not
    invoke this on a clean run, since it would destroy real verdicts.
    """
    nulled: list[str] = []

    def walk(node: object, path: str) -> None:
        if isinstance(node, dict):
            for key in list(node.keys()):
                child_path = f"{path}.{key}" if path else key
                if key in ("pass", "decision"):
                    if node[key] is not None:
                        nulled.append(child_path)
                        node[key] = None
                    # Already null: nothing to record, and nothing left to walk into.
                else:
                    walk(node[key], child_path)
        elif isinstance(node, list):
            for index, item in enumerate(node):
                walk(item, f"{path}[{index}]")

    walk(payload, "")
    return nulled


def sha256_file(path: Path | str) -> str | None:
    """SHA-256 of a file's bytes, or None if it does not exist / can't be read."""
    file_path = Path(path)
    if not file_path.is_file():
        return None
    digest = hashlib.sha256()
    try:
        with file_path.open("rb") as handle:
            for chunk in iter(lambda: handle.read(1 << 16), b""):
                digest.update(chunk)
    except OSError:
        return None
    return digest.hexdigest()


def git_commit(repo_root: Path | str) -> str | None:
    """`git -C <repo_root> rev-parse HEAD`, or None on any failure (not a repo,
    git missing, detached weirdness, etc.) -- a measurement never fails just
    because provenance couldn't be established."""
    try:
        completed = subprocess.run(
            ["git", "-C", str(repo_root), "rev-parse", "HEAD"],
            capture_output=True,
            text=True,
        )
    except OSError:
        return None
    if completed.returncode != 0:
        return None
    commit = completed.stdout.strip()
    return commit or None
