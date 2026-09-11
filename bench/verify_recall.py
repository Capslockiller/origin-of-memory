#!/usr/bin/env python3
"""An identity-pinned recall gate: measure the ranking, or refuse to report a number.

Why this exists beside `bench/kos20.py`
---------------------------------------
`kos20.py` already measures recall on the gold set and already emits
`bench/PROVENANCE.md`'s four-part tuple. What it does not do -- what nothing in
`bench/` did before this file -- is make the *identity* of what was measured a
precondition of measuring it. `kos20.py` records `binary_sha256` after the run,
from whatever executable it happened to invoke; it records `dataset.path` as a
bare string and never hashes the gold set; it never hashes the corpus at all.
A run against the wrong exe, a mutated gold file, or a corpus that shifted
underfoot therefore produces a perfectly well-formed, `run_status: "ok"` results
file, and the only defence is a reader comparing hex by eye.

This script inverts that order. The identity of everything that can change an
answer -- executable, corpus, corrections, settings, gold set -- is computed and
compared against caller-supplied expectations *before the first query is issued*,
and recomputed *after the last one*. A mismatch is not a warning appended to a
result; it means no measurement happens at all, or that the measurement that did
happen carries no verdict.

Six things schema 2 fixes that schema 1 got wrong
-------------------------------------------------
Schema 1 shipped with three verified gaps and three unbuilt requirements. Each
is now a named field rather than a paragraph:

1. **The executable's provenance came from the runner's own checkout.** A
   `source_commit` read with `git rev-parse` in the directory the *script* lives
   in says nothing about where the *binary* came from -- the measuring lane and
   the building lane are not the same tree and are not supposed to be. The exe
   is now bound to an externally supplied **candidate manifest**
   (`--candidate-manifest`): a file, written by whoever accepted the candidate,
   that names the accepted source commit and the published exe's SHA-256. The
   runner hashes that manifest, verifies the exe against it, and records the
   runner's own checkout separately under `candidate.runner_checkout_commit`
   with `checkout_is_not_provenance: true` beside it. An exe that does not match
   the manifest is refused before measuring -- that is the "old evidence, new
   exe" gate.

2. **Thresholds were read out of the evidence file's own `thresholds` block.**
   A number that sets its own passing mark is not a gate. The thresholds are now
   constants in this file (`RECALL_AT_3`, `RECALL_AT_5`, `SCORED_MINIMUM`,
   `CLASS_FLOORS`), mirrored by constants in the test that reads the evidence.
   The `thresholds` block is still emitted -- a reader deserves to see what was
   applied -- but it is marked `"authority": "not this file"`, and the test that
   consumes the evidence fails if the block ever declares anything lower than
   the constants.

3. **Only `knowledge/concepts/*.md` was copied.** The product's retrieval path
   also reads `<vault>/<companion>/Duzeltmeler.md` and turns each `## ` block
   into its own document named `Duzeltmeler.md#<n>`
   (`src/Oom/Retrieve/Retrieve.cs`, `Corrections()`), and it reads
   `<vault>/.oom/oom.json` for `retrieve.top`, `perNoteChars`, `totalChars`,
   `minOverlap` and `strictScore`. A snapshot without those two files is not a
   snapshot of what was measured. All three are copied and hashed now, and the
   copy refuses to overwrite: a destination that overlaps the source, or a
   destination that already holds files, is a refusal, never a silent
   `shutil.rmtree`.

4. **`Duzeltmeler.md#<n>` hits were exempted from the existence check
   unconditionally.** Any hit with a `#` in it passed. The corrections file is
   now parsed with the product's own rule and the exact set of ids it can
   legitimately produce is computed; a hit naming an id outside that set, or a
   hit declaring `source: "correction"` without a correction id, is a fault.

5. **Inputs were hashed once.** They are hashed again after the run, and a run
   whose corpus, corrections, settings, gold set or executable moved underneath
   it reports `identity_stability.stable: false` and carries no verdict. This
   check runs *before* the output-integrity refusal, not after it, so a backend
   that both mutates its inputs and returns garbage reports both facts -- a
   refusal that happens too early proves nothing about the checks it skipped.

6. **The index was never separated from the full scan.** Retrieval narrows
   candidates through the FTS index in `state.db` when that index is present and
   its manifest digest still matches the corpus, and **falls back to a full
   corpus scan in silence** when it is missing, stale, or unreadable
   (`CandidateNames`, `corpus-scan:no-index` / `corpus-scan:stale-index` /
   `corpus-scan:sqlite-*`). Nothing in the process's stdout, stderr or exit code
   tells a caller which happened -- `CandidateSource` is `internal`. So an
   indexed run proves the index was used the only way it can be proved from
   outside: a **witness probe**. One note's row is deleted from `notes_fts`
   while `oom_index_meta` is left untouched (so the freshness check still
   passes), and that note's own rare term is queried. If the note comes back,
   candidates were not narrowed and the run was a full scan wearing an index's
   name; the indexed verdict is refused. The pristine index is restored and the
   restore is verified before the measurement runs.

What it deliberately does NOT do
--------------------------------
- It does not rename anything. The gold set's own class labels (`tek-not`,
  `cok-not`, or whatever a given gold file carries) are reported verbatim. This
  script never emits the words "episodic" or "concept" as class names, because
  no measurement in this repository produces such an axis; see the
  `episodic_axis` block, which records that absence structurally instead of
  quietly letting two other labels stand in for it.
- It does not let the hook path or the negative controls stand in for recall.
  They are measured, they are reported, and they live in their own `controls`
  block with their own verdict. `decision.recall_gate` is computed from the
  scored questions and from nothing else.
- It does not invent a second provenance contract. `bench/PROVENANCE.md` SS2-SS4
  is the contract; `bench/provenance.py` is its implementation; this script
  imports that module rather than reimplementing its rules.
- It does not create a state root. An indexed run requires `state.db` to exist
  already; bringing one into existence in order to measure it is how 26 stray
  roots were collected on one machine (Y-161..Y-163).

The scoring predicate (`slug`, `recall_at`) is deliberately identical to
`kos20.py`'s. `selftest` asserts that identity against the imported module
rather than asserting it in a comment.

Python 3 standard library only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import random
import shlex
import shutil
import sqlite3
import subprocess
import sys
import time
from datetime import date
from pathlib import Path
from typing import Any

import provenance

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = 2
CANARY = "kanarya"
CONCEPTS = ("knowledge", "concepts")
COMPANION = "\U0001f52e 850-Companion"
CORRECTIONS_NAME = "Duzeltmeler.md"
SETTINGS = (".oom", "oom.json")
RETRIEVAL_SETTING_KEYS = ("top", "perNoteChars", "totalChars", "minOverlap", "strictScore")

# ---------------------------------------------------------------------------
# The gate. These are the thresholds, and this file is one of the two places
# they are written down -- the other is the test that reads the evidence. They
# are NOT read from the evidence file, because a file that carries both the
# number and the bar it must clear sets its own exam.
RECALL_AT_3 = 0.80
RECALL_AT_5 = 0.88
SCORED_MINIMUM = 125
CLASS_FLOORS: dict[str, dict[str, float]] = {
    "tek-not": {"n": 90, "recall@5": 0.85},
    "cok-not": {"n": 20, "recall@5": 0.90},
}
INDEX_MODES = ("full-scan", "indexed")


# ------------------------------------------------------------------ identity


def tree_sha256(directory: Path, pattern: str = "*.md") -> tuple[str, list[dict[str, Any]]]:
    """A digest over a corpus directory that changes if any name or byte changes.

    The digest covers `name\\0<sha256 of bytes>\\n` for every match, ordinal-sorted
    by name -- so a renamed note, a reordered directory listing, an edited note
    and a deleted note all move it, while the directory's mtime and the order
    the filesystem happens to enumerate in do not. Byte-exact on purpose: a
    CRLF/LF normalisation would make two materially different corpora hash the
    same, and the point of this number is that it cannot be true of two things.
    """
    manifest: list[dict[str, Any]] = []
    digest = hashlib.sha256()
    if not directory.is_dir():
        return "", manifest
    for path in sorted(directory.glob(pattern), key=lambda item: item.name):
        file_hash = provenance.sha256_file(path) or ""
        digest.update(path.name.encode("utf-8"))
        digest.update(b"\0")
        digest.update(file_hash.encode("ascii"))
        digest.update(b"\n")
        manifest.append({"name": path.name, "sha256": file_hash, "bytes": path.stat().st_size})
    return digest.hexdigest(), manifest


def parse_corrections(text: str) -> list[dict[str, Any]]:
    """The product's own splitting rule, reimplemented field for field.

    `src/Oom/Retrieve/Retrieve.cs`, `Corrections()`:

        foreach (var block in ("\\n" + text.Replace("\\r\\n", "\\n")).Split("\\n## ").Skip(1))
        {
            var title = block.Split('\\n')[0].Trim();
            if (title.Length == 0) continue;
            notes.Add(new Note($"Duzeltmeler.md#{notes.Count + 1}", title, ...));
        }

    Two details decide whether a claimed correction id is real, and both are
    easy to get wrong by guessing. The delimiter is `\\n## ` -- H2 exactly, with
    a newline prepended first so a file that opens on `## ` still splits there;
    an H1 or H3 line is body text. And `<n>` counts *accepted* blocks, not
    headings: a `## ` whose title line is empty is skipped and consumes no
    number, so the id is not the heading's ordinal in the file. A test in
    `tests/Oom.Tests/RecallCandidateBindingTests.cs` pins this rule against the
    product rather than against this docstring.
    """
    blocks = ("\n" + text.replace("\r\n", "\n")).split("\n## ")[1:]
    corrections: list[dict[str, Any]] = []
    for block in blocks:
        lines = block.split("\n")
        title = lines[0].strip()
        if not title:
            continue
        retires = [line.lstrip()[7:].strip() for line in lines
                   if line.lstrip().lower().startswith("yerine:")]
        corrections.append({
            "id": f"{CORRECTIONS_NAME}#{len(corrections) + 1}",
            "title": title,
            "retires": retires,
        })
    return corrections


def corrections_identity(vault: Path, companion: str) -> dict[str, Any]:
    path = vault / companion / CORRECTIONS_NAME
    if not path.is_file():
        # Absent is a legitimate state -- `Corrections()` returns `[]` and
        # retrieval degrades to concepts only. It is recorded, not tolerated in
        # silence: a run measured without the hand layer is a different
        # measurement from one measured with it.
        return {"present": False, "path": str(path), "sha256": None,
                "headings": 0, "ids": [], "retires": []}
    text = path.read_text(encoding="utf-8")
    parsed = parse_corrections(text)
    return {
        "present": True,
        "path": str(path),
        "sha256": provenance.sha256_file(path),
        "bytes": path.stat().st_size,
        "headings": len(parsed),
        "ids": [item["id"] for item in parsed],
        "titles": [item["title"] for item in parsed],
        "retires": sorted({name for item in parsed for name in item["retires"]}),
    }


def settings_identity(vault: Path) -> dict[str, Any]:
    """`<vault>/.oom/oom.json` -- the only settings file retrieval reads.

    Five keys under `retrieve` reach the ranker (`Configuration.ReadRetrieve`):
    `top`, `perNoteChars`, `totalChars`, `minOverlap`, `strictScore`. They are
    extracted by name so a reader of the evidence can see the ranking
    parameters that were in force without opening the vault; the file's hash is
    what actually pins them.
    """
    path = vault.joinpath(*SETTINGS)
    if not path.is_file():
        return {"present": False, "path": str(path), "sha256": None, "retrieval_keys": {}}
    record: dict[str, Any] = {"present": True, "path": str(path),
                              "sha256": provenance.sha256_file(path), "retrieval_keys": {}}
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
        block = document.get("retrieve", {}) if isinstance(document, dict) else {}
        if isinstance(block, dict):
            record["retrieval_keys"] = {key: block[key] for key in RETRIEVAL_SETTING_KEYS if key in block}
    except (OSError, json.JSONDecodeError) as error:
        record["parse_error"] = str(error)
    return record


def inventory(vault: Path, gold_path: Path, exe: Path, companion: str) -> dict[str, Any]:
    """Everything that can change an answer, hashed together into one digest.

    The per-input hashes are what a reader checks; `inputs_digest` is what the
    before/after comparison checks, so a single field answers "is this the same
    measurement" without a reader diffing five hex strings by eye.
    """
    corpus_sha, manifest = tree_sha256(vault.joinpath(*CONCEPTS))
    corrections = corrections_identity(vault, companion)
    settings = settings_identity(vault)
    record = {
        "vault": str(vault),
        "corpus": {"dir": "/".join(CONCEPTS), "files": len(manifest), "sha256": corpus_sha or None},
        "corpus_manifest": manifest,
        "corrections": corrections,
        "settings": settings,
        "gold": {"path": str(gold_path), "sha256": provenance.sha256_file(gold_path)},
        "executable": {"path": str(exe), "sha256": provenance.sha256_file(exe)},
    }
    digest = hashlib.sha256()
    for part in (record["corpus"]["sha256"], corrections["sha256"], settings["sha256"],
                 record["gold"]["sha256"], record["executable"]["sha256"]):
        digest.update((part or "-").encode("ascii"))
        digest.update(b"\n")
    record["inputs_digest"] = digest.hexdigest()
    return record


def inputs_delta(before: dict[str, Any], after: dict[str, Any]) -> list[str]:
    """Which named input moved between two inventories. Empty means none did."""
    changed: list[str] = []
    for label, path in (("corpus", ("corpus", "sha256")),
                        ("corrections", ("corrections", "sha256")),
                        ("settings", ("settings", "sha256")),
                        ("gold", ("gold", "sha256")),
                        ("executable", ("executable", "sha256"))):
        left: Any = before
        right: Any = after
        for key in path:
            left = (left or {}).get(key) if isinstance(left, dict) else None
            right = (right or {}).get(key) if isinstance(right, dict) else None
        if left != right:
            changed.append(f"{label}: {left!r} -> {right!r}")
    if before.get("corpus", {}).get("files") != after.get("corpus", {}).get("files"):
        changed.append("corpus.files: "
                       f"{before.get('corpus', {}).get('files')} -> {after.get('corpus', {}).get('files')}")
    return changed


# ------------------------------------------------------------------ snapshot


def _inside(child: Path, parent: Path) -> bool:
    try:
        child.relative_to(parent)
        return True
    except ValueError:
        return False


def snapshot_inputs(source: Path, destination: Path, companion: str,
                    reuse: bool) -> tuple[Path, list[str]]:
    """Copy every result-affecting input to a snapshot, or refuse.

    Two rules schema 1 did not have, both of them destructive-by-omission:

    - **The destination may not be, contain, or sit inside the source.** A
      snapshot that overlaps what it snapshots is not a copy; at best it
      measures the live vault under another name, at worst it writes into it.
    - **A destination that already holds files is a refusal.** Schema 1 called
      `shutil.rmtree` on the concepts directory before copying. Against the
      owner's vault, one mistyped path is an unrecoverable deletion of somebody
      else's data, performed silently, by a measuring tool. `--snapshot-reuse`
      allows an existing snapshot to be measured again, but only after its
      contents are verified to match the source -- it never deletes anything.
    """
    problems: list[str] = []
    if source == destination or _inside(destination, source) or _inside(source, destination):
        problems.append(f"snapshot destination overlaps the source vault: {destination} / {source}")
        return destination, problems

    existing = [item for item in destination.glob("*")] if destination.is_dir() else []
    if existing and not reuse:
        problems.append(f"snapshot destination is not empty ({len(existing)} entries) and "
                        f"--snapshot-reuse was not given; refusing to delete it")
        return destination, problems

    if existing and reuse:
        before = inventory(source, source, source, companion)
        after = inventory(destination, destination, destination, companion)
        if before["corpus"]["sha256"] != after["corpus"]["sha256"] or \
                before["corrections"]["sha256"] != after["corrections"]["sha256"] or \
                before["settings"]["sha256"] != after["settings"]["sha256"]:
            problems.append("--snapshot-reuse was given but the existing snapshot does not match "
                            "the source vault; refusing to measure a stale copy and refusing to "
                            "overwrite it")
        return destination, problems

    (destination.joinpath(*CONCEPTS)).mkdir(parents=True, exist_ok=True)
    for path in sorted(source.joinpath(*CONCEPTS).glob("*.md"), key=lambda item: item.name):
        shutil.copy2(path, destination.joinpath(*CONCEPTS) / path.name)

    corrections_src = source / companion / CORRECTIONS_NAME
    if corrections_src.is_file():
        (destination / companion).mkdir(parents=True, exist_ok=True)
        shutil.copy2(corrections_src, destination / companion / CORRECTIONS_NAME)

    settings_src = source.joinpath(*SETTINGS)
    if settings_src.is_file():
        destination.joinpath(SETTINGS[0]).mkdir(parents=True, exist_ok=True)
        shutil.copy2(settings_src, destination.joinpath(*SETTINGS))

    return destination, problems


# ------------------------------------------------------------------ candidate


def load_candidate_manifest(path: Path | None) -> tuple[dict[str, Any], list[str]]:
    """The externally supplied statement of what was accepted and published.

    The runner does not author this and cannot: the whole point is that the
    binding comes from outside the measuring lane. Minimum shape:

        {"candidate_id": "...", "source_commit": "<40 hex>",
         "publish": {"exe": "oom.exe", "sha256": "<64 hex>"},
         "issued_by": "...", "issued_at": "2026-09-11"}
    """
    if path is None:
        return {}, ["no candidate manifest was supplied (--candidate-manifest); the executable's "
                    "provenance would then be the runner's own checkout, which does not build it"]
    if not path.is_file():
        return {}, [f"candidate manifest not readable: {path}"]
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        return {}, [f"candidate manifest is not readable JSON: {error}"]
    if not isinstance(document, dict):
        return {}, ["candidate manifest is not a JSON object"]

    problems: list[str] = []
    publish = document.get("publish")
    if not isinstance(publish, dict) or not isinstance(publish.get("sha256"), str):
        problems.append("candidate manifest has no publish.sha256; it names no executable")
    if not isinstance(document.get("source_commit"), str):
        problems.append("candidate manifest has no source_commit; it names no accepted tree")
    return document, problems


# ------------------------------------------------------------------ gold set


def load_gold(path: Path, limit: int | None = None) -> list[dict[str, Any]]:
    """Same row contract as `kos20.load_gold`: `soru`, `gold`, `sinif`, optional `lane`."""
    rows: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as handle:
        for index, line in enumerate(handle, 1):
            text = line.strip()
            if not text:
                continue
            row = json.loads(text)
            question = str(row.get("soru", "")).strip()
            if not question:
                raise SystemExit(f"{path}:{index}: empty 'soru'")
            gold = [str(item) for item in row.get("gold", [])]
            sinif = str(row.get("sinif", "bilinmiyor"))
            if not gold and sinif != CANARY:
                raise SystemExit(f"{path}:{index}: empty 'gold' on a non-{CANARY} row")
            rows.append({"id": f"q{index:03d}", "soru": question, "gold": gold,
                         "sinif": sinif, "lane": str(row.get("lane", ""))})
            if limit is not None and len(rows) >= limit:
                break
    if not rows:
        raise SystemExit(f"{path}: no rows")
    return rows


# ------------------------------------------------------------------ scoring
# Identical to bench/kos20.py's predicate on purpose; `selftest` asserts the
# identity against the imported module instead of trusting this comment.


def slug(name: str) -> str:
    """`unity-secimi.md` -> `unity-secimi`; gold slugs carry no extension."""
    return name[:-3] if name.lower().endswith(".md") else name


def recall_at(hits: list[dict[str, Any]], gold: list[str], k: int) -> int:
    top_slugs = {hit["slug"] for hit in hits[:k]}
    return 1 if any(item in top_slugs for item in gold) else 0


def rank_of_first_gold(hits: list[dict[str, Any]], gold: list[str]) -> int | None:
    for rank, hit in enumerate(hits, 1):
        if hit["slug"] in gold:
            return rank
    return None


def summarise(records: list[dict[str, Any]]) -> dict[str, Any]:
    count = len(records)
    if count == 0:
        return {"n": 0, "recall@3": 0.0, "recall@5": 0.0, "mrr@5": 0.0}
    reciprocal = sum(1.0 / record["first_gold_rank"]
                     for record in records
                     if record["first_gold_rank"] and record["first_gold_rank"] <= 5)
    return {
        "n": count,
        "recall@3": round(sum(record["hit@3"] for record in records) / count, 4),
        "recall@5": round(sum(record["hit@5"] for record in records) / count, 4),
        "mrr@5": round(reciprocal / count, 4),
    }


# ------------------------------------------------------------------ backend


def backend_command(exe: Path, vault: Path, batch: Path, top: int,
                    prefix: list[str]) -> list[str]:
    return [*prefix, str(exe), "--vault", str(vault),
            "retrieve", "--batch", str(batch), "--top", str(top)]


def run_batch(exe: Path, vault: Path, rows: list[dict[str, Any]], top: int,
              work: Path, raw_path: Path, prefix: list[str]) -> dict[str, Any]:
    """One process, one JSON line per query, raw stdout persisted verbatim.

    `raw_path` is PROVENANCE.md SS2's `raw_artifact`. Unlike `kos20.py`, which
    writes it under the gitignored `bench/.out/`, the caller here is expected to
    place it beside the results file under `bench/results/`, where a reader of
    the repository can actually open the thing the summary claims to summarise.
    """
    work.mkdir(parents=True, exist_ok=True)
    batch = work / "gold-batch.jsonl"
    with batch.open("w", encoding="utf-8", newline="\n") as handle:
        for row in rows:
            handle.write(json.dumps({"id": row["id"], "soru": row["soru"]}, ensure_ascii=False) + "\n")

    command = backend_command(exe, vault, batch, top, prefix)
    started = time.perf_counter()
    completed = subprocess.run(command, capture_output=True, text=True,
                               encoding="utf-8", cwd=str(ROOT))
    elapsed_ms = (time.perf_counter() - started) * 1000.0

    raw_written: Path | None = None
    try:
        raw_path.parent.mkdir(parents=True, exist_ok=True)
        raw_path.write_text(completed.stdout, encoding="utf-8", newline="\n")
        raw_written = raw_path
    except OSError:
        raw_written = None

    return {
        "command": command,
        "returncode": completed.returncode,
        "stdout": completed.stdout,
        "stderr": completed.stderr,
        "elapsed_ms": round(elapsed_ms, 1),
        "raw_written": raw_written,
    }


def parse_and_check(stdout: str, rows: list[dict[str, Any]], corpus_names: set[str],
                    correction_ids: set[str], top: int) -> tuple[list[list[dict[str, Any]]], list[str]]:
    """Parse the backend's rankings and prove they describe the inputs measured.

    Every finding appended to `faults` makes the run invalid. These are not
    quality judgements about the ranking -- a genuinely bad ranker returns real
    notes in a bad order and scores badly, which is a number. These catch output
    that is not a ranking of these inputs at all, which is not.

    Schema 1 exempted every hit whose name contained a `#` from the existence
    check, on the reasoning that a correction is legitimately not a concept
    file. That is an unconditional amnesty: `Duzeltmeler.md#99` against a file
    with one heading passed, and so did `anything#at-all`. The exemption is now
    a check of its own -- the id must be one the measured corrections file can
    actually produce -- and it runs in both directions, so a hit that declares
    `source: "correction"` while naming a concept is a fault too.
    """
    faults: list[str] = []
    lines = [line for line in stdout.splitlines() if line.strip()]
    if len(lines) != len(rows):
        faults.append(f"alignment: backend returned {len(lines)} lines for {len(rows)} queries")
        return [], faults

    rankings: list[list[dict[str, Any]]] = []
    for row, line in zip(rows, lines):
        try:
            payload = json.loads(line)
        except json.JSONDecodeError as error:
            faults.append(f"{row['id']}: output line is not JSON ({error})")
            rankings.append([])
            continue

        if payload.get("schema_version") != 1:
            faults.append(f"{row['id']}: schema_version={payload.get('schema_version')!r}, expected 1")

        # The executable echoes the query it answered. If that is not the query
        # we sent, the lines are misaligned and every score below is attached to
        # the wrong question -- silently, and in a way a line count cannot see.
        echoed = str(payload.get("query", ""))
        if echoed != row["soru"]:
            faults.append(f"{row['id']}: echoed query does not match the query sent")

        hits: list[dict[str, Any]] = []
        previous: float | None = None
        seen: set[str] = set()
        for hit in payload.get("hits", []):
            name = str(hit.get("name", ""))
            score = float(hit.get("score", 0.0))
            source = str(hit.get("source", ""))
            note = slug(name)
            if "#" in name:
                if name not in correction_ids:
                    faults.append(f"{row['id']}: returned correction id '{name}', which the measured "
                                  f"{CORRECTIONS_NAME} cannot produce "
                                  f"({len(correction_ids)} id(s) loaded)")
                elif source != "correction":
                    faults.append(f"{row['id']}: '{name}' is a correction id but was labelled "
                                  f"source='{source}'")
            else:
                if note not in corpus_names:
                    faults.append(f"{row['id']}: returned '{name}', which is not in the measured corpus")
                if source == "correction":
                    faults.append(f"{row['id']}: '{name}' is labelled source='correction' but is not "
                                  f"a correction id")
            if note in seen:
                faults.append(f"{row['id']}: returned '{name}' twice in one ranking")
            seen.add(note)
            if previous is not None and score > previous + 1e-12:
                faults.append(f"{row['id']}: scores do not descend ({previous} then {score})")
            previous = score
            hits.append({"slug": note, "score": score, "source": source})
        if len(hits) > top:
            faults.append(f"{row['id']}: {len(hits)} hits returned for --top {top}")
        rankings.append(hits)

    scored_rows = [index for index, row in enumerate(rows) if row["sinif"] != CANARY]
    if scored_rows and all(not rankings[index] for index in scored_rows):
        faults.append("every scored query returned an empty ranking")
    return rankings, faults


# ------------------------------------------------------------------ the index


def state_root_for(vault: Path) -> dict[str, Any]:
    """Where the product will look for this vault's `state.db`, derived, not guessed.

    `VaultPaths.Hash` is `SHA256(vault.TrimEnd('\\\\'))` truncated to its first
    eight bytes, lowercase hex, under `%LOCALAPPDATA%\\oom\\`; the vault string
    is whatever `--vault` was given, run through `Path.GetFullPath` and stripped
    of a trailing separator (`VaultPaths.UseVault`). Reproduced here so an
    indexed run can say which file it is talking about without a CLI flag the
    product does not have -- there is no `--state` option anywhere in
    `Program.cs`.
    """
    spelled = os.path.abspath(str(vault)).rstrip("\\")
    digest = hashlib.sha256(spelled.encode("utf-8")).hexdigest()[:16]
    local = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), "AppData", "Local")
    root = Path(local) / "oom" / digest
    return {"vault_as_spelled": spelled, "hash": digest, "state_root": str(root),
            "state_db": str(root / "state.db")}


def read_index_manifest(db: Path) -> dict[str, Any] | None:
    try:
        with sqlite3.connect(f"file:{db}?mode=ro", uri=True) as connection:
            row = connection.execute(
                "SELECT generation, manifest_digest FROM oom_index_meta "
                "ORDER BY generation DESC LIMIT 1").fetchone()
    except sqlite3.Error:
        return None
    return {"generation": row[0], "manifest_digest": row[1]} if row else None


def index_witness_probe(exe: Path, vault: Path, state_db: Path, witness_note: str,
                        witness_query: str, top: int, work: Path,
                        prefix: list[str]) -> dict[str, Any]:
    """Prove -- from process output alone -- that candidates were narrowed by the index.

    The product gives a caller no way to ask. `CandidateSource` records `fts`
    versus `corpus-scan:no-index` / `corpus-scan:stale-index` /
    `corpus-scan:sqlite-<n>`, and it is `internal`; the batch JSON carries
    `schema_version`, `query` and `hits[]{name,score,source,updated}` and
    nothing else; stderr carries only the hook's gate reasons; the exit code is
    always the retrieval's, which is always 0. So the index's use has to be made
    observable, and there is exactly one observable difference between the two
    paths: **a note absent from the candidate set cannot be returned, and a full
    scan returns it.**

    So: back the index up, delete the witness note's row from `notes_fts` while
    leaving `oom_index_meta` alone -- the freshness check compares the corpus
    digest with the stored manifest digest, and neither moves -- then ask for
    that note's own rare term.

    - Witness absent from the answer  => candidates were narrowed => index used.
    - Witness present in the answer   => nothing narrowed => this was a full
      scan, whatever the presence of a `state.db` suggested. The indexed verdict
      is refused; a silent fallback is not indexed evidence.

    The pristine index is copied back afterwards and the restore is verified by
    hash. If the restore cannot be verified the run carries no verdict either:
    measuring against an index this probe damaged would be worse than not
    measuring.
    """
    record: dict[str, Any] = {
        "instrument": "delete the witness note's notes_fts row, leave oom_index_meta untouched, "
                      "then query the witness term and see whether the note can still be reached",
        "note": witness_note, "query": witness_query,
        "state_db": str(state_db),
        "manifest_before": None, "manifest_after": None,
        "rows_deleted": None, "witness_returned": None,
        "narrowing_proven": False, "backed_up": False,
        "restored": False, "restore_verified": False, "error": None,
    }
    backup = work / "index-witness" / "state.db.pristine"
    backup.parent.mkdir(parents=True, exist_ok=True)
    try:
        pristine_sha = provenance.sha256_file(state_db)
        shutil.copy2(state_db, backup)
        record["backed_up"] = True
        record["manifest_before"] = read_index_manifest(state_db)

        with sqlite3.connect(str(state_db)) as connection:
            cursor = connection.execute("DELETE FROM notes_fts WHERE name = ?", (witness_note,))
            record["rows_deleted"] = cursor.rowcount
            connection.commit()

        if not record["rows_deleted"]:
            record["error"] = (f"witness note {witness_note!r} had no row in notes_fts; the probe "
                               f"would prove nothing")
        else:
            command = [*prefix, str(exe), "--vault", str(vault), "retrieve",
                       "--query", witness_query, "--top", str(top), "--json"]
            completed = subprocess.run(command, capture_output=True, text=True,
                                       encoding="utf-8", cwd=str(ROOT))
            record["command"] = " ".join(command)
            record["returncode"] = completed.returncode
            names: list[str] = []
            try:
                names = [str(hit.get("name", ""))
                         for hit in json.loads(completed.stdout.strip() or "{}").get("hits", [])]
            except json.JSONDecodeError as error:
                record["error"] = f"witness probe output is not JSON: {error}"
            record["hits"] = names
            record["witness_returned"] = witness_note in names
            record["narrowing_proven"] = bool(names) and witness_note not in names
            if not names and record["error"] is None:
                record["error"] = ("witness probe returned no hits at all; an empty answer does not "
                                   "distinguish a narrowed candidate set from a broken query")
                record["narrowing_proven"] = False

        # The manifest has to be read back before the restore, or the check is
        # against the file we just put there rather than the one we probed.
        record["manifest_after"] = read_index_manifest(state_db)
    except (OSError, sqlite3.Error) as error:
        record["error"] = f"{type(error).__name__}: {error}"
    finally:
        if record["backed_up"]:
            try:
                shutil.copy2(backup, state_db)
                record["restored"] = True
                record["restore_verified"] = provenance.sha256_file(state_db) == pristine_sha
            except OSError as error:
                record["restored"] = False
                record["error"] = f"restore failed: {error}"
    return record


def full_scan_witness(exe: Path, vault: Path, witness_note: str, witness_query: str,
                      top: int, prefix: list[str]) -> dict[str, Any]:
    """The other half of the same discriminator, for the unindexed path.

    A full scan has no candidate set to narrow, so the witness note must come
    back. If it does not, the run is not the full scan it claims to be -- either
    something narrowed the candidates or the query does not reach the note, and
    in both cases the label on the run is wrong.
    """
    command = [*prefix, str(exe), "--vault", str(vault), "retrieve",
               "--query", witness_query, "--top", str(top), "--json"]
    completed = subprocess.run(command, capture_output=True, text=True,
                               encoding="utf-8", cwd=str(ROOT))
    names: list[str] = []
    error: str | None = None
    try:
        names = [str(hit.get("name", ""))
                 for hit in json.loads(completed.stdout.strip() or "{}").get("hits", [])]
    except json.JSONDecodeError as decode_error:
        error = f"witness probe output is not JSON: {decode_error}"
    return {
        "instrument": "query the witness term with no index present; a full scan must reach the note",
        "note": witness_note, "query": witness_query, "command": " ".join(command),
        "returncode": completed.returncode, "hits": names,
        "witness_returned": witness_note in names,
        "full_scan_proven": witness_note in names,
        "error": error,
    }


# ------------------------------------------------------------------ controls


def hook_controls(exe: Path, vault: Path, rows: list[dict[str, Any]],
                  sample: int, prefix: list[str]) -> dict[str, Any]:
    """The hook path and the negative controls -- measured apart, reported apart.

    `retrieve --hook` is a different code path from `retrieve --batch`: it applies
    an intent gate and a score floor that `Query` never applies, so a recall
    number says nothing whatsoever about it. The `kanarya` rows are the owner's
    own negative controls -- prompts for which no note is relevant -- so any
    injection on them is a false positive. Neither number may be substituted for
    recall, and `decision.recall_gate` is computed without reading this block.
    """
    canaries = [row for row in rows if row["sinif"] == CANARY]
    positives = [row for row in rows if row["sinif"] != CANARY][:sample]
    counts = {CANARY: [0, 0], "gold": [0, 0]}
    detail: list[dict[str, Any]] = []
    for row in canaries + positives:
        payload = json.dumps({"session_id": f"verify-{row['id']}",
                              "hook_event_name": "UserPromptSubmit",
                              "prompt": row["soru"]}, ensure_ascii=False)
        completed = subprocess.run([*prefix, str(exe), "--vault", str(vault), "retrieve", "--hook"],
                                   input=payload, capture_output=True, text=True,
                                   encoding="utf-8", cwd=str(ROOT))
        injected = completed.stdout.strip().startswith("{")
        key = CANARY if row["sinif"] == CANARY else "gold"
        counts[key][0] += int(injected)
        counts[key][1] += 1
        detail.append({"id": row["id"], "sinif": row["sinif"], "injected": injected,
                       "reason": (completed.stdout.strip() or completed.stderr.strip() or None)
                       if not injected else None})

    def rate(key: str) -> float | None:
        return round(counts[key][0] / counts[key][1], 4) if counts[key][1] else None

    false_positive = rate(CANARY)
    injection = rate("gold")
    return {
        "instrument": "oom.exe --vault <vault> retrieve --hook  (stdin: UserPromptSubmit payload)",
        "measures": "the hook path's injection decision; NOT recall, and never a substitute for it",
        "negative_controls": {"label": CANARY, "n": counts[CANARY][1],
                              "injected": counts[CANARY][0], "false_positive_rate": false_positive},
        "positive_sample": {"n": counts["gold"][1], "injected": counts["gold"][0],
                            "injection_rate": injection},
        "detail": detail,
        # A hook that injects nothing at all has a false-positive rate of zero,
        # and that number means nothing whatsoever: silence on the controls is
        # only evidence of discrimination if the same hook speaks on the
        # questions that do have answers. So a run where the positive sample
        # never injected yields no verdict here, not a pass.
        "pass": None if (false_positive is None or not injection) else bool(false_positive == 0.0),
        "vacuous": bool(injection == 0.0) if injection is not None else None,
        "vacuous_note": "the positive sample never injected, so a zero false-positive rate on "
                        "the negative controls distinguishes nothing"
                        if injection == 0.0 else None,
    }


# ------------------------------------------------------------------ the gate


def thresholds_block() -> dict[str, Any]:
    return {
        "recall@3": RECALL_AT_3,
        "recall@5": RECALL_AT_5,
        "scored_minimum": SCORED_MINIMUM,
        "per_sinif": {name: dict(floor) for name, floor in CLASS_FLOORS.items()},
        "authority": "not this file. These values are constants in bench/verify_recall.py and are "
                     "mirrored as constants in tests/Oom.Tests/RecallCandidateBindingTests.cs. "
                     "They are recorded here so a reader can see what was applied; a consumer that "
                     "reads its bar out of this block lets the evidence set its own exam.",
    }


def measure(arguments: argparse.Namespace) -> tuple[dict[str, Any], int]:
    exe = Path(arguments.exe).resolve()
    gold_path = Path(arguments.gold).resolve()
    prefix = shlex.split(arguments.backend_prefix) if arguments.backend_prefix else []
    companion = arguments.companion_dir

    reasons: list[str] = []
    blocking: list[str] = []

    # ---- 1. the candidate manifest: what the exe is supposed to be, stated
    #         from outside this lane.
    manifest_path = Path(arguments.candidate_manifest).resolve() if arguments.candidate_manifest else None
    manifest, manifest_problems = load_candidate_manifest(manifest_path)
    blocking.extend(manifest_problems)

    exe_sha = provenance.sha256_file(exe)
    if exe_sha is None:
        blocking.append(f"executable not readable: {exe}")

    expected_exe = (manifest.get("publish") or {}).get("sha256") if isinstance(manifest.get("publish"), dict) else None
    if arguments.expect_exe_sha256 and expected_exe and \
            arguments.expect_exe_sha256.lower() != str(expected_exe).lower():
        blocking.append("--expect-exe-sha256 contradicts the candidate manifest; the manifest is "
                        "the authority and a run may not be pinned to two different binaries")
    if expected_exe is None:
        expected_exe = arguments.expect_exe_sha256

    checkout_commit = provenance.git_commit(ROOT)
    candidate = {
        "manifest_path": str(manifest_path) if manifest_path else None,
        "manifest_sha256": provenance.sha256_file(manifest_path) if manifest_path else None,
        "candidate_id": manifest.get("candidate_id"),
        "source_commit": manifest.get("source_commit"),
        "publish": manifest.get("publish"),
        "issued_by": manifest.get("issued_by"),
        "issued_at": manifest.get("issued_at"),
        "exe_matches_manifest": None,
        "runner_checkout_commit": checkout_commit,
        "checkout_is_not_provenance": True,
        "checkout_matches_manifest": (None if not isinstance(manifest.get("source_commit"), str)
                                      else bool(checkout_commit and
                                                checkout_commit.lower().startswith(
                                                    str(manifest["source_commit"]).lower()[:7]))),
        "why": "the executable's provenance is this manifest, not the commit the runner happens to "
               "sit on: the lane that measures is not the lane that builds, and git rev-parse in "
               "the measuring tree describes neither the exe nor the tree it came from",
    }

    # ---- 2. snapshot every result-affecting input, then hash them.
    source_vault = Path(arguments.vault).resolve()
    snapshot_record: dict[str, Any] = {"requested": bool(arguments.snapshot), "reused": bool(arguments.snapshot_reuse)}
    if arguments.snapshot:
        # Deliberately not created before the overlap check: a destination inside
        # the source vault must not be brought into existence in order to be
        # refused. The copy creates what it needs, after the checks pass.
        destination = Path(arguments.snapshot).resolve()
        measured_vault, snapshot_problems = snapshot_inputs(source_vault, destination, companion,
                                                            arguments.snapshot_reuse)
        blocking.extend(snapshot_problems)
        snapshot_record.update({"source": str(source_vault), "destination": str(destination),
                                "disjoint": not any("overlaps" in item for item in snapshot_problems),
                                "problems": snapshot_problems})
    else:
        measured_vault = source_vault
        snapshot_record.update({"source": str(source_vault), "destination": None, "disjoint": False,
                                "problems": []})
        reasons.append("no snapshot was taken (--snapshot omitted): the corpus, corrections and "
                       "settings were read from a live directory that can move between queries")

    rows = load_gold(gold_path, arguments.max_queries)
    before = inventory(measured_vault, gold_path, exe, companion)
    scored_rows = [row for row in rows if row["sinif"] != CANARY]

    identity = {
        "executable": {"path": str(exe), "sha256": exe_sha, "expected": expected_exe,
                       "expected_from": "candidate manifest" if manifest else "--expect-exe-sha256",
                       "verified": None},
        "corpus": {"source_vault": str(source_vault), "measured_vault": str(measured_vault),
                   "snapshot": bool(arguments.snapshot), "files": before["corpus"]["files"],
                   "sha256": before["corpus"]["sha256"], "expected": arguments.expect_corpus_sha256,
                   "verified": None},
        "gold": {"path": str(gold_path), "sha256": before["gold"]["sha256"], "rows": len(rows),
                 "scored": len(scored_rows), "canaries": len(rows) - len(scored_rows),
                 "expected": arguments.expect_gold_sha256, "verified": None},
        "corrections": {**before["corrections"], "expected": arguments.expect_corrections_sha256,
                        "verified": None},
        "settings": {**before["settings"], "expected": arguments.expect_settings_sha256,
                     "verified": None},
        "inputs_digest": before["inputs_digest"],
        "backend_prefix": prefix or None,
    }

    for key, actual, expected, label in (
        ("executable", exe_sha, expected_exe, "executable"),
        ("corpus", before["corpus"]["sha256"], arguments.expect_corpus_sha256, "corpus"),
        ("gold", before["gold"]["sha256"], arguments.expect_gold_sha256, "gold set"),
        ("corrections", before["corrections"]["sha256"], arguments.expect_corrections_sha256, "corrections"),
        ("settings", before["settings"]["sha256"], arguments.expect_settings_sha256, "settings"),
    ):
        if expected is None:
            identity[key]["verified"] = None
            reasons.append(f"{label} identity was not pinned; the recorded hash is a description, "
                           f"not a check")
            continue
        matched = (actual is not None and actual.lower() == str(expected).lower())
        identity[key]["verified"] = matched
        if not matched:
            blocking.append(f"{label} identity mismatch: measured {actual!r}, expected {expected!r}")
    candidate["exe_matches_manifest"] = identity["executable"]["verified"] if expected_exe else None

    # ---- 3. which path is being measured, and does the state root agree.
    derived = state_root_for(measured_vault)
    state_db = Path(arguments.state_root).resolve() / "state.db" if arguments.state_root \
        else Path(derived["state_db"])
    index_record: dict[str, Any] = {
        "mode": arguments.index_mode,
        "state_root_derived": derived["state_root"],
        "state_root_used": str(state_db.parent),
        "state_root_overridden": bool(arguments.state_root),
        "state_db": str(state_db),
        "state_db_present": state_db.is_file(),
        "state_db_sha256_before": provenance.sha256_file(state_db),
        "manifest": read_index_manifest(state_db) if state_db.is_file() else None,
        "note": "there is no CLI flag that moves the product's state root; it is derived from the "
                "vault path (VaultPaths.Hash). --state-root exists for fixtures that drive a "
                "backend shim, and any override is recorded here.",
    }
    if arguments.index_mode == "indexed" and not index_record["state_db_present"]:
        blocking.append(f"--index-mode indexed but no state.db exists at {state_db}; this run would "
                        f"silently measure a full corpus scan. Build the index first "
                        f"(oom sweep / oom compile); this gate does not create a state root.")
    if arguments.index_mode == "full-scan" and index_record["state_db_present"]:
        blocking.append(f"--index-mode full-scan but a state.db exists at {state_db}; retrieval "
                        f"would narrow candidates through it and the run would not be the full "
                        f"scan it is labelled")
    if arguments.index_witness_note is None or arguments.index_witness_query is None:
        blocking.append("--index-witness-note and --index-witness-query are required: without a "
                        "witness there is no observable difference between the indexed path and a "
                        "silent fallback to a full scan")
    elif arguments.index_witness_note not in {item["name"] for item in before["corpus_manifest"]}:
        blocking.append(f"witness note {arguments.index_witness_note!r} is not in the measured corpus")

    payload_base: dict[str, Any] = {
        "gate": "recall-evidence",
        "schema": SCHEMA,
        "index_mode": arguments.index_mode,
        "measured": date.today().isoformat(),
        "measured_by": arguments.measured_by,
        "command": literal_command(),
        # PROVENANCE.md SS2's commit is the tree the measured artefact came from.
        # That is the manifest's, not the runner's -- see candidate.why.
        "source_commit": manifest.get("source_commit"),
        "binary_sha256": exe_sha,
        "candidate": candidate,
        "identity": identity,
        "inputs": {"snapshot": snapshot_record, "corpus": before["corpus"],
                   "corrections": before["corrections"], "settings": before["settings"],
                   "inputs_digest": before["inputs_digest"]},
        "index": index_record,
        "thresholds": thresholds_block(),
        "episodic_axis": {
            "measured": False,
            "reason": "No instrument in this repository measures an episodic recall axis. "
                      "The gold set's classes are answer-cardinality labels (one note answers "
                      "vs several); they are reported here under their own names and are NOT "
                      "renamed to 'episodic'/'concept'. This axis is open, not covered.",
        },
    }

    # ---- 4. refuse before measuring, if identity or wiring did not hold.
    if blocking:
        payload = {
            **payload_base,
            "run_status": "invalid",
            "run_status_reasons": blocking + reasons,
            "raw_artifact": "none-kept",
            "substitute": {"active": bool(prefix), "for": str(exe) if prefix else None,
                           "reason": "backend shim stood in for the executable" if prefix else None},
            "refused_before_measuring": True,
            "identity_stability": None,
            "index_witness": None,
            "overall": None,
            "per_sinif": None,
            "controls": None,
            "pass": None,
            "decision": None,
        }
        provenance.redact_verdicts(payload)
        return payload, 2

    work = Path(arguments.work_dir).resolve()
    work.mkdir(parents=True, exist_ok=True)

    # ---- 5. the witness probe, before the measurement, against the same file.
    if arguments.index_mode == "indexed":
        witness = index_witness_probe(exe, measured_vault, state_db, arguments.index_witness_note,
                                      arguments.index_witness_query, arguments.top, work, prefix)
        witness["expected"] = "the witness note must NOT come back once its notes_fts row is gone"
        witness["proven"] = bool(witness["narrowing_proven"] and witness["restore_verified"])
    else:
        witness = full_scan_witness(exe, measured_vault, arguments.index_witness_note,
                                    arguments.index_witness_query, arguments.top, prefix)
        witness["expected"] = "with no index present the witness note must come back"
        witness["proven"] = bool(witness["full_scan_proven"])
    index_record["manifest_after_witness"] = read_index_manifest(state_db) if state_db.is_file() else None

    witness_reasons: list[str] = []
    if not witness["proven"]:
        if arguments.index_mode == "indexed":
            witness_reasons.append(
                "index witness failed: the note whose notes_fts row was deleted still came back, "
                "or the index could not be restored. Retrieval falls back to a full corpus scan in "
                "silence when the index is missing, stale or unreadable, so an unproven witness "
                "means this run is not evidence about the indexed path."
                if witness.get("error") is None else
                f"index witness failed: {witness.get('error')}")
        else:
            witness_reasons.append(
                "full-scan witness failed: the witness note did not come back although no index was "
                "present, so this run is not the unnarrowed scan it is labelled")

    # ---- 6. measure.
    raw_path = Path(arguments.raw).resolve()
    batch = run_batch(exe, measured_vault, rows, arguments.top, work, raw_path, prefix)

    # ---- 7. re-hash the inputs IMMEDIATELY, before any refusal path can skip it.
    #         A refusal that happens earlier than the check it is supposed to be
    #         evidence for proves nothing about that check.
    after = inventory(measured_vault, gold_path, exe, companion)
    changed = inputs_delta(before, after)
    stability = {
        "checked": ["corpus", "corrections", "settings", "gold", "executable"],
        "inputs_digest_before": before["inputs_digest"],
        "inputs_digest_after": after["inputs_digest"],
        "stable": not changed,
        "changed": changed,
        "why": "an input that moved between the first query and the last one splits the run across "
               "two measurements, and nothing in the numbers would say so",
    }
    if changed:
        witness_reasons.append("inputs changed during the run: " + "; ".join(changed))

    corpus_names = {slug(item["name"]) for item in before["corpus_manifest"]}
    correction_ids = set(before["corrections"]["ids"])
    rankings, faults = parse_and_check(batch["stdout"], rows, corpus_names, correction_ids, arguments.top)
    if batch["returncode"] != 0:
        faults.append(f"backend exited {batch['returncode']}: {batch['stderr'].strip()[:300]}")

    raw_written = batch["raw_written"]
    if raw_written is not None:
        try:
            raw_artifact = str(raw_written.relative_to(ROOT)).replace(os.sep, "/")
        except ValueError:
            raw_artifact = str(raw_written)
    else:
        raw_artifact = "none-kept"
        faults.append("no raw artifact was persisted for this run (raw_artifact=none-kept)")

    # A shimmed backend is not the executable whose hash was verified, so no run
    # that uses one may ever assert a pass -- whatever it measured, it did not
    # measure the binary this file names.
    shim_reasons: list[str] = []
    if prefix:
        shim_reasons.append("backend was invoked through a shim; binary_sha256 does not "
                            "describe what actually answered the queries")

    # ---- 8. corrupt output is not a low score; it is not a score.
    if faults:
        # A corrupt backend produces one fault per hit per query -- hundreds of lines saying the
        # same thing. The full count is the fact; the first few are the evidence. Keeping all of
        # them would bury both under a file nobody opens. The raw stdout is committed beside this
        # summary, so nothing is lost that a reader might want to check.
        shown = faults[:40]
        payload = {
            **payload_base,
            "run_status": "invalid",
            "run_status_reasons": shown + witness_reasons + shim_reasons + reasons,
            "raw_artifact": raw_artifact,
            "substitute": {"active": bool(prefix), "for": str(exe) if prefix else None,
                           "reason": "backend shim stood in for the executable" if prefix else None},
            "refused_before_measuring": False,
            "identity_stability": stability,
            "index_witness": witness,
            "output_integrity": {"ok": False, "faults": shown, "fault_count": len(faults),
                                 "truncated": len(faults) > len(shown)},
            "backend": {"command": " ".join(batch["command"]), "elapsed_ms": batch["elapsed_ms"],
                        "returncode": batch["returncode"]},
            "overall": None,
            "per_sinif": None,
            "controls": None,
            "pass": None,
            "decision": None,
        }
        provenance.redact_verdicts(payload)
        return payload, 3

    # ---- 9. score.
    records = []
    for row, hits in zip(rows, rankings):
        records.append({
            "id": row["id"], "sinif": row["sinif"], "gold": row["gold"],
            "hit@3": recall_at(hits, row["gold"], 3),
            "hit@5": recall_at(hits, row["gold"], 5),
            "first_gold_rank": rank_of_first_gold(hits, row["gold"]),
            "top": [{"slug": hit["slug"], "score": round(hit["score"], 6)} for hit in hits],
        })

    scored = [record for record in records if record["sinif"] != CANARY]
    overall = summarise(scored)
    classes = sorted({record["sinif"] for record in scored})
    per_sinif = {name: summarise([r for r in scored if r["sinif"] == name]) for name in classes}

    controls = hook_controls(exe, measured_vault, rows, arguments.hook_sample, prefix) \
        if arguments.hook_sample else None

    run_status, assessed = provenance.assess(
        {"concept_files": before["corpus"]["files"],
         "index_notes": 0 if arguments.index_mode == "full-scan" else
                        (index_record["manifest"] or {}).get("generation"),
         "error": None}, len(scored))
    reasons = assessed + witness_reasons + shim_reasons + reasons
    if shim_reasons or witness_reasons:
        run_status = "invalid"

    enough = len(scored) >= SCORED_MINIMUM
    if not enough:
        reasons.append(f"only {len(scored)} scored questions, the gate requires {SCORED_MINIMUM}")

    passes = {
        "recall@3": bool(overall["recall@3"] >= RECALL_AT_3),
        "recall@5": bool(overall["recall@5"] >= RECALL_AT_5),
        "scored_minimum": bool(enough),
        "per_sinif": {name: bool(per_sinif.get(name, {}).get("n", 0) >= floor["n"] and
                                 per_sinif.get(name, {}).get("recall@5", 0.0) >= floor["recall@5"])
                      for name, floor in CLASS_FLOORS.items()},
        "index_path_proven": bool(witness["proven"]),
        "inputs_stable": bool(stability["stable"]),
    }
    recall_gate = bool(passes["recall@3"] and passes["recall@5"] and passes["scored_minimum"]
                       and all(passes["per_sinif"].values()) and passes["index_path_proven"]
                       and passes["inputs_stable"])

    payload = {
        **payload_base,
        "run_status": run_status,
        "run_status_reasons": reasons,
        "raw_artifact": raw_artifact,
        "substitute": {"active": bool(prefix), "for": str(exe) if prefix else None,
                       "reason": "backend shim stood in for the executable" if prefix else None},
        "refused_before_measuring": False,
        "identity_stability": stability,
        "index_witness": witness,
        "output_integrity": {"ok": True, "faults": []},
        "backend": {"command": " ".join(batch["command"]), "elapsed_ms": batch["elapsed_ms"],
                    "returncode": batch["returncode"], "read_only": True},
        "dataset": {"path": str(gold_path), "rows": len(rows), "scored": len(scored),
                    "canaries_excluded": len(rows) - len(scored), "classes": classes},
        "overall": overall,
        "per_sinif": per_sinif,
        "controls": controls,
        "records": records,
        "corpus_manifest": before["corpus_manifest"],
        "pass": passes,
        "decision": {
            "recall_gate": recall_gate,
            "computed_from": "the scored questions only; controls are reported separately and "
                             "cannot substitute for this verdict",
            "index_mode": arguments.index_mode,
            "controls_gate": None if controls is None else controls["pass"],
        },
    }

    if run_status != "ok":
        nulled = provenance.redact_verdicts(payload)
        payload["run_status_reasons"] = payload["run_status_reasons"] + [
            f"verdicts nulled per PROVENANCE.md SS3: {', '.join(nulled)}" if nulled else
            "no verdict fields needed nulling"]
        return payload, 4

    return payload, 0 if recall_gate else 1


def literal_command() -> str:
    script = Path(sys.argv[0]).resolve()
    try:
        script = script.relative_to(ROOT)
    except ValueError:
        pass
    return " ".join(["python", shlex.quote(str(script).replace(os.sep, "/"))]
                    + [shlex.quote(argument) for argument in sys.argv[1:]])


# Nothing committed under bench/results/ may name this machine. The repo's own
# files already scrub vault and state paths to `<vault>` / `<state-root>`; these
# are the same convention extended to the three prefixes a harness leaks by
# accident -- the repository root, the user profile, and the temp directory.
# Applied on the way out, in write_json, so no emit path can forget it.
def _scrub_map() -> list[tuple[str, str]]:
    import tempfile
    candidates = [
        (str(ROOT), "<repo>"),
        (os.path.expanduser("~"), "<home>"),
        (tempfile.gettempdir(), "<temp>"),
    ]
    pairs: list[tuple[str, str]] = []
    # Longest first: <repo> lives under neither <home> nor <temp> here, but a
    # tree that did would otherwise be half-replaced by the shorter prefix.
    for raw, token in sorted(candidates, key=lambda item: len(item[0]), reverse=True):
        if not raw:
            continue
        for spelling in {raw, raw.replace("\\", "/"), raw.replace("/", "\\")}:
            pairs.append((spelling, token))
    return pairs


def scrub(value: Any, pairs: list[tuple[str, str]]) -> Any:
    if isinstance(value, str):
        result = value
        for raw, token in pairs:
            if raw.lower() in result.lower():
                # Case-insensitive replace, preserving everything else verbatim.
                lowered = result.lower()
                needle = raw.lower()
                out: list[str] = []
                position = 0
                while True:
                    found = lowered.find(needle, position)
                    if found < 0:
                        out.append(result[position:])
                        break
                    out.append(result[position:found])
                    out.append(token)
                    position = found + len(needle)
                result = "".join(out)
                lowered = result.lower()
        return result
    if isinstance(value, dict):
        return {key: scrub(item, pairs) for key, item in value.items()}
    if isinstance(value, list):
        return [scrub(item, pairs) for item in value]
    return value


def write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    cleaned = scrub(payload, _scrub_map())
    path.write_text(json.dumps(cleaned, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
                    encoding="utf-8", newline="\n")


# ------------------------------------------------------------------ fixtures


FILLER = [
    "kasa", "not", "oturum", "gunluk", "derleme", "kanca", "getirme", "indeks",
    "kapi", "olcum", "esik", "kanarya", "sorgu", "siralama", "belge", "kayit",
]

WITNESS_INDEX = 42

FIXTURE_CORRECTIONS = (
    "# Duzeltmeler\n"
    "\n"
    "## Fikstur duzeltmesi bir\n"
    "yerine: fix-003.md\n"
    "Bu duzeltme fikstur icin yazildi.\n"
    "\n"
    "## \n"
    "Basligi bos olan blok atlanir ve numara tuketmez.\n"
    "\n"
    "## Fikstur duzeltmesi iki\n"
    "Ikinci duzeltme.\n"
)

FIXTURE_SETTINGS = json.dumps(
    {"retrieve": {"top": 5, "perNoteChars": 600, "totalChars": 3000,
                  "minOverlap": 1, "strictScore": 0.0}},
    ensure_ascii=False, indent=2) + "\n"


def build_fixture(destination: Path, seed: int = 20260911,
                  notes: int = 160, questions: int = 130) -> dict[str, Path]:
    """A synthetic vault and gold set this lane owns, for proving the wiring.

    Not a stand-in for the owner's gold set and never presented as one: the
    results it produces carry `substitute.active: true`. Its job is to make
    every branch of this script executable without the private corpus -- and to
    be hard enough that the thresholds are actually exercised, which is why one
    question in sixteen is built from shared filler only and is expected to miss.

    It is a whole vault, not a corpus directory: `knowledge/concepts/*.md`, the
    companion `Duzeltmeler.md` and `.oom/oom.json`, because those are the three
    things retrieval reads and a snapshot of one third of them is not a snapshot.
    """
    rng = random.Random(seed)
    concepts = destination.joinpath(*CONCEPTS)
    if concepts.exists():
        shutil.rmtree(concepts)
    concepts.mkdir(parents=True, exist_ok=True)

    names: list[str] = []
    tokens: list[str] = []
    families: list[str] = []
    for index in range(notes):
        name = f"fix-{index:03d}"
        token = f"zeug{index:03d}ma"
        # Every note also carries a family token shared with five others. A
        # question asked with only the family token has six equally good
        # answers and --top 5 returns five of them, so the gold note is inside
        # recall@5's window far more often than inside recall@3's -- which is
        # what makes the two numbers differ here. A fixture where every question
        # either hits at rank 1 or misses entirely reports the same value for
        # both and cannot tell the two thresholds apart; the first cut of this
        # fixture used twelve-note families and did exactly that.
        family = f"kume{index // 6:02d}tipi"
        body_words = [rng.choice(FILLER) for _ in range(rng.randint(18, 34))]
        body = " ".join(body_words)
        concepts.joinpath(f"{name}.md").write_text(
            "---\n"
            f"title: {name} basligi {token}\n"
            "aliases: []\n"
            "tags: []\n"
            "sources: []\n"
            "created: 2026-01-01\n"
            "updated: 2026-01-02\n"
            "---\n"
            f"Bu not {token} terimini aciklar. {family} ailesindendir. {body}\n",
            encoding="utf-8", newline="\n")
        names.append(name)
        tokens.append(token)
        families.append(family)

    companion = destination / COMPANION
    companion.mkdir(parents=True, exist_ok=True)
    companion.joinpath(CORRECTIONS_NAME).write_text(FIXTURE_CORRECTIONS, encoding="utf-8", newline="\n")

    destination.joinpath(SETTINGS[0]).mkdir(parents=True, exist_ok=True)
    destination.joinpath(*SETTINGS).write_text(FIXTURE_SETTINGS, encoding="utf-8", newline="\n")

    gold_path = destination / "gold-fixture.jsonl"
    lines: list[str] = []
    scored_target = questions - 5
    for index in range(scored_target):
        primary = index % notes
        # One question in sixteen is built from shared filler only: no rare token
        # and no family token, so the ranker has nothing distinctive to find and
        # the question is expected to miss outright. Without these the fixture
        # would score 1.000 and prove nothing about a threshold.
        if index % 16 == 15:
            question = " ".join(rng.choice(FILLER) for _ in range(6))
            row = {"soru": question, "gold": [names[primary]], "sinif": "tek-not"}
        # One in six asks with the family token only: six notes answer it
        # equally well, so the gold note lands mid-ranking and the question
        # separates recall@5 from recall@3.
        elif index % 6 == 3:
            row = {"soru": f"{families[primary]} ailesi hakkinda ne yaziyor",
                   "gold": [names[primary]], "sinif": "tek-not"}
        elif index % 5 == 0:
            second = (primary + 7) % notes
            row = {"soru": f"{tokens[primary]} ve {tokens[second]} nedir",
                   "gold": [names[primary], names[second]], "sinif": "cok-not"}
        else:
            row = {"soru": f"{tokens[primary]} hakkinda ne yaziyor",
                   "gold": [names[primary]], "sinif": "tek-not"}
        lines.append(json.dumps(row, ensure_ascii=False))
    for index in range(5):
        lines.append(json.dumps(
            {"soru": f"tesekkurler devam edelim {index}", "gold": [], "sinif": CANARY},
            ensure_ascii=False))
    gold_path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    # The witness query names the witness note's own rare token AND its family
    # token. The rare token alone would make an index-narrowed answer *empty*,
    # and an empty answer cannot tell "the candidate set excluded the note" from
    # "the query reaches nothing" -- so the probe would prove nothing in exactly
    # the case it exists for. With the family token, the five surviving siblings
    # come back either way and the witness's absence is the whole signal.
    return {"vault": destination, "gold": gold_path,
            "witness_note": f"fix-{WITNESS_INDEX:03d}.md",
            "witness_query": f"zeug{WITNESS_INDEX:03d}ma kume{WITNESS_INDEX // 6:02d}tipi"}


def build_fixture_index(state_root: Path, vault: Path) -> Path:
    """A stand-in `state.db` with the two tables the witness probe touches.

    Explicitly **not** the product's index. `Retrieve.Build()` is not reachable
    from the CLI (`oom sweep`, `oom compile` and `oom doctor --fix` call it as a
    side effect of doing something else), and none of those may be run here: two
    of them write into the vault, and this lane does not run `doctor` against
    anything. So the indexed scenarios drive a backend shim against a fixture
    database with the same two tables the real one has -- `notes_fts(name, ...)`
    and `oom_index_meta(generation, manifest_digest, built_at)` -- which is
    enough to exercise the probe's delete/restore path and both of its verdicts.

    What that leaves unproven is whether the real binary's candidate narrowing
    behaves the way the probe assumes. That is not left to a comment either:
    `tests/Oom.Tests/RecallCandidateBindingTests.cs` builds a real index with
    `Retrieve.Build()`, deletes the same row, and asserts the real ranker can no
    longer reach the note.
    """
    state_root.mkdir(parents=True, exist_ok=True)
    database = state_root / "state.db"
    if database.exists():
        database.unlink()
    with sqlite3.connect(str(database)) as connection:
        connection.execute("CREATE TABLE notes_fts(name TEXT, title TEXT, aliases TEXT, "
                           "tags TEXT, body TEXT)")
        connection.execute("CREATE TABLE oom_index_meta(generation INTEGER, manifest_digest TEXT, "
                           "built_at TEXT)")
        for path in sorted(vault.joinpath(*CONCEPTS).glob("*.md"), key=lambda item: item.name):
            text = path.read_text(encoding="utf-8")
            connection.execute("INSERT INTO notes_fts(name, title, aliases, tags, body) "
                               "VALUES(?, ?, ?, ?, ?)", (path.name, path.stem, "", "", text))
        connection.execute("INSERT INTO oom_index_meta(generation, manifest_digest, built_at) "
                           "VALUES(1, ?, '2026-09-11T00:00:00+00:00')",
                           (tree_sha256(vault.joinpath(*CONCEPTS))[0],))
        connection.commit()
    return database


CORRUPT_SHIM = '''#!/usr/bin/env python3
"""A backend that answers with notes that do not exist. Deliberately corrupt.

Invoked by `verify_recall.py selftest` as the negative control for output
integrity: it speaks the executable's JSON protocol perfectly -- right
`schema_version`, right line count, right echoed query, descending scores -- and
every note it names is fabricated. A gate that only counts lines and averages
hit@k reports this as recall 0.000 with `run_status: "ok"`; a gate that checks
its inputs reports that it was not handed a ranking of the measured corpus.

`--corrupt-mode fabricate-correction` is the narrower control: it answers with
real corpus notes and one invented correction id, which is exactly what schema
1's unconditional `"#" in name` amnesty let through.
"""
import json, os, sys

mode = "fabricate"
batch = query = vault = None
argv = sys.argv[1:]
for index, argument in enumerate(argv):
    if argument == "--batch" and index + 1 < len(argv):
        batch = argv[index + 1]
    if argument == "--query" and index + 1 < len(argv):
        query = argv[index + 1]
    if argument == "--vault" and index + 1 < len(argv):
        vault = argv[index + 1]
    if argument == "--corrupt-mode" and index + 1 < len(argv):
        mode = argv[index + 1]

if batch is None and query is None:
    sys.exit(0)

if batch is None:
    print(json.dumps({"schema_version": 1, "query": query, "hits": []}, ensure_ascii=False))
    sys.exit(0)

with open(batch, "r", encoding="utf-8") as handle:
    rows = [json.loads(line) for line in handle if line.strip()]

real = []
if vault:
    concepts = os.path.join(vault, "knowledge", "concepts")
    if os.path.isdir(concepts):
        real = sorted(name for name in os.listdir(concepts) if name.endswith(".md"))

for position, row in enumerate(rows):
    if mode == "truncate" and position >= len(rows) - 3:
        break
    echoed = rows[(position + 1) % len(rows)]["soru"] if mode == "misalign" else row["soru"]
    if mode == "fabricate-correction":
        hits = [{"name": "Duzeltmeler.md#99", "score": 9.0, "source": "correction",
                 "updated": "2026-01-02T00:00:00+00:00"}]
        hits += [{"name": real[(position + rank) % len(real)], "score": 8.0 - rank,
                  "source": "concept", "updated": "2026-01-02T00:00:00+00:00"}
                 for rank in range(4)]
    else:
        hits = [{"name": f"hayali-{position}-{rank}.md", "score": 9.0 - rank,
                 "source": "concept", "updated": "2026-01-02T00:00:00+00:00"}
                for rank in range(5)]
    print(json.dumps({"schema_version": 1, "query": echoed, "hits": hits}, ensure_ascii=False))
'''


INDEX_SHIM = '''#!/usr/bin/env python3
"""A backend that either honours a candidate index or ignores it. Both on purpose.

The product narrows candidates through `notes_fts` when `state.db` is present
and its manifest still matches the corpus, and falls back to scanning the whole
corpus **in silence** when it does not. Nothing in its output says which
happened. This shim reproduces both behaviours so the harness's witness probe
can be shown to tell them apart:

  default        -- read `notes_fts`, answer only from those names (narrowed)
  --ignore-index -- answer from the directory listing, index or no index

`--ignore-index` is the "unused index" control: a `state.db` sits there, the run
is labelled indexed, and the answers came from a full scan. A gate that accepts
the file's existence as proof passes it.
"""
import json, os, re, sqlite3, sys

argv = sys.argv[1:]
def value(flag):
    for index, argument in enumerate(argv):
        if argument == flag and index + 1 < len(argv):
            return argv[index + 1]
    return None

vault = value("--vault")
batch = value("--batch")
query = value("--query")
state_db = value("--shim-state-db")
top = int(value("--top") or 5)
ignore = "--ignore-index" in argv

concepts = os.path.join(vault or "", "knowledge", "concepts")
bodies = {}
for name in sorted(os.listdir(concepts)) if os.path.isdir(concepts) else []:
    if name.endswith(".md"):
        with open(os.path.join(concepts, name), "r", encoding="utf-8") as handle:
            bodies[name] = handle.read().lower()

candidates = None
if not ignore and state_db and os.path.isfile(state_db):
    try:
        connection = sqlite3.connect("file:%s?mode=ro" % state_db, uri=True)
        meta = connection.execute(
            "SELECT manifest_digest FROM oom_index_meta ORDER BY generation DESC LIMIT 1").fetchone()
        if meta is not None:
            candidates = {row[0] for row in connection.execute("SELECT name FROM notes_fts")}
        connection.close()
    except sqlite3.Error:
        candidates = None


def answer(text):
    terms = [term for term in re.split(r"[^0-9a-zA-ZçğıöşüÇĞİÖŞÜ]+", text.lower()) if len(term) > 2]
    scored = []
    for name, body in bodies.items():
        if candidates is not None and name not in candidates:
            continue
        hit = sum(body.count(term) for term in terms)
        if hit:
            scored.append((hit, name))
    scored.sort(key=lambda item: (-item[0], item[1]))
    return [{"name": name, "score": float(count), "source": "concept",
             "updated": "2026-01-02T00:00:00+00:00"} for count, name in scored[:top]]


if batch:
    with open(batch, "r", encoding="utf-8") as handle:
        for line in handle:
            if not line.strip():
                continue
            row = json.loads(line)
            print(json.dumps({"schema_version": 1, "query": row["soru"],
                              "hits": answer(row["soru"])}, ensure_ascii=False))
elif query is not None:
    print(json.dumps({"schema_version": 1, "query": query, "hits": answer(query)},
                     ensure_ascii=False))
'''


MUTATOR_SHIM = '''#!/usr/bin/env python3
"""A pass-through backend that edits the corpus it was asked to rank.

It runs the real executable with the arguments it was handed and forwards the
answer verbatim, then appends a byte to the measured vault's `Duzeltmeler.md`.
The ranking is therefore honest and the output integrity checks all pass -- the
only thing wrong with the run is that its inputs are not the inputs that were
hashed before it started.

This is the control for the check that has to run *after* the measurement and
*before* any refusal: schema 1 computed identity once, up front, so a corpus
that moved underfoot produced a clean `run_status: "ok"` file.
"""
import os, subprocess, sys

argv = sys.argv[1:]
completed = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8")
sys.stdout.write(completed.stdout)
sys.stderr.write(completed.stderr)

vault = None
for index, argument in enumerate(argv):
    if argument == "--vault" and index + 1 < len(argv):
        vault = argv[index + 1]
if vault:
    target = os.path.join(vault, "\U0001f52e 850-Companion", "Duzeltmeler.md")
    if os.path.isfile(target):
        with open(target, "a", encoding="utf-8") as handle:
            handle.write("\\n## Kosum sirasinda eklendi\\nBu blok olcum devam ederken yazildi.\\n")
sys.exit(completed.returncode)
'''


# ------------------------------------------------------------------ selftest


def scenario(name: str, expectation: str, arguments: argparse.Namespace,
             out_dir: Path) -> dict[str, Any]:
    payload, code = measure(arguments)
    write_json(out_dir / f"{name}.json", payload)
    return {
        "scenario": name,
        "expectation": expectation,
        "exit_code": code,
        "run_status": payload["run_status"],
        "decision": payload.get("decision"),
        "pass": payload.get("pass"),
        "refused_before_measuring": payload.get("refused_before_measuring"),
        "index_mode": payload.get("index_mode"),
        "index_witness_proven": (payload.get("index_witness") or {}).get("proven"),
        "inputs_stable": (payload.get("identity_stability") or {}).get("stable"),
        "overall": payload.get("overall"),
        "evidence": f"{name}.json",
        "reasons": payload.get("run_status_reasons", [])[:6],
    }


def selftest(arguments: argparse.Namespace) -> int:
    """Prove the gate on fixtures this lane owns, including every refusal path."""
    # The scoring predicate must be the one bench/kos20.py already uses. Assert
    # it rather than claim it.
    import kos20
    parity = []
    for case in ([{"slug": "a"}, {"slug": "b"}], [], [{"slug": "z"}] * 3):
        for gold in (["a"], ["z"], ["missing"]):
            for k in (3, 5):
                parity.append((recall_at(case, gold, k), kos20.recall_at(case, gold, k)))
    for name in ("Duzeltmeler.md#3", "a-b.MD", "plain", "x.md"):
        parity.append((slug(name), kos20.slug(name)))
    mismatched = [pair for pair in parity if pair[0] != pair[1]]
    if mismatched:
        raise SystemExit(f"scoring predicate has drifted from kos20.py: {mismatched}")

    # The corrections parser must reproduce the product's numbering, including
    # the empty-title block that consumes no number.
    parsed = parse_corrections(FIXTURE_CORRECTIONS)
    if [item["id"] for item in parsed] != ["Duzeltmeler.md#1", "Duzeltmeler.md#2"]:
        raise SystemExit(f"corrections parser disagrees with the product's numbering: {parsed}")

    work = Path(arguments.work_dir).resolve()
    fixture_root = work / "fixture"
    fixture_root.mkdir(parents=True, exist_ok=True)
    fixture = build_fixture(fixture_root)
    corrupt_shim = work / "corrupt_backend.py"
    corrupt_shim.write_text(CORRUPT_SHIM, encoding="utf-8", newline="\n")
    index_shim = work / "index_backend.py"
    index_shim.write_text(INDEX_SHIM, encoding="utf-8", newline="\n")
    mutator_shim = work / "mutator_backend.py"
    mutator_shim.write_text(MUTATOR_SHIM, encoding="utf-8", newline="\n")

    out_dir = Path(arguments.out_dir).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    exe = Path(arguments.exe).resolve()
    exe_sha = provenance.sha256_file(exe)

    # The candidate manifest is supplied from outside the runner. Here "outside"
    # is the fixture directory -- the point being that the runner reads it and
    # does not author it at measurement time.
    manifest_path = work / "candidate-manifest.json"
    manifest_path.write_text(json.dumps({
        "candidate_id": "astra-dalga-2-selftest-fixture",
        "source_commit": provenance.git_commit(ROOT),
        "publish": {"exe": exe.name, "sha256": exe_sha},
        "issued_by": "bench/verify_recall.py selftest (fixture; INT-3 supplies the real one)",
        "issued_at": date.today().isoformat(),
        "note": "A fixture manifest. The binding run's manifest is written by whoever accepts the "
                "unified candidate, in the lane that publishes the exe, and names that exe's hash.",
    }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")

    stale_manifest_path = work / "candidate-manifest-stale.json"
    stale_manifest_path.write_text(json.dumps({
        "candidate_id": "astra-dalga-2-stale",
        "source_commit": "0" * 40,
        "publish": {"exe": exe.name, "sha256": "0" * 64},
        "issued_by": "fixture",
        "issued_at": date.today().isoformat(),
    }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")

    snapshot = work / "snapshot"
    if snapshot.exists():
        # The runner refuses to delete a non-empty destination, so the selftest
        # clears its own scratch copy -- deliberately here, in the fixture
        # builder, and never inside the measuring path.
        shutil.rmtree(snapshot)
    snapshot_inputs(fixture["vault"], snapshot, COMPANION, reuse=False)
    corpus_sha, _ = tree_sha256(snapshot.joinpath(*CONCEPTS))
    gold_sha = provenance.sha256_file(fixture["gold"])
    corrections_sha = provenance.sha256_file(snapshot / COMPANION / CORRECTIONS_NAME)
    settings_sha = provenance.sha256_file(snapshot.joinpath(*SETTINGS))

    fixture_state_root = work / "fixture-state-root"
    build_fixture_index(fixture_state_root, snapshot)
    shim_db = fixture_state_root / "state.db"

    def options(**overrides: Any) -> argparse.Namespace:
        base = dict(exe=str(exe), vault=str(fixture["vault"]), gold=str(fixture["gold"]),
                    snapshot=str(snapshot), snapshot_reuse=True, top=5, max_queries=None,
                    hook_sample=arguments.hook_sample, measured_by=arguments.measured_by,
                    work_dir=str(work), backend_prefix=None,
                    companion_dir=COMPANION,
                    candidate_manifest=str(manifest_path),
                    index_mode="full-scan", state_root=None,
                    index_witness_note=fixture["witness_note"],
                    index_witness_query=fixture["witness_query"],
                    expect_exe_sha256=None, expect_corpus_sha256=corpus_sha,
                    expect_gold_sha256=gold_sha,
                    expect_corrections_sha256=corrections_sha,
                    expect_settings_sha256=settings_sha,
                    raw=str(out_dir / "raw" / "honest.stdout.txt"))
        base.update(overrides)
        return argparse.Namespace(**base)

    shim = lambda script, extra="": (f"{shlex.quote(sys.executable)} {shlex.quote(str(script))}"
                                     + (f" {extra}" if extra else ""))

    results = [
        scenario("selftest-1-honest-full-scan",
                 "candidate manifest verified, inputs snapshotted and stable, no index present and "
                 "the witness note comes back -> a full-scan verdict is issued",
                 options(), out_dir),
        scenario("selftest-2-stale-candidate",
                 "the candidate manifest names a different exe (old evidence, new binary) -> "
                 "refuse BEFORE measuring, no verdict",
                 options(candidate_manifest=str(stale_manifest_path),
                         raw=str(out_dir / "raw" / "stale-candidate.stdout.txt")), out_dir),
        scenario("selftest-3-corrupt-output",
                 "backend returns notes that are not in the corpus -> invalid, no verdict",
                 options(backend_prefix=shim(corrupt_shim),
                         raw=str(out_dir / "raw" / "corrupt.stdout.txt")), out_dir),
        scenario("selftest-4-fabricated-correction",
                 "backend returns Duzeltmeler.md#99 against a two-heading corrections file -> "
                 "invalid; schema 1's unconditional '#' amnesty passed exactly this",
                 options(backend_prefix=shim(corrupt_shim, "--corrupt-mode fabricate-correction"),
                         raw=str(out_dir / "raw" / "fabricated-correction.stdout.txt")), out_dir),
        scenario("selftest-5-overlapping-snapshot",
                 "the snapshot destination sits inside the source vault -> refuse before measuring, "
                 "and nothing is deleted",
                 options(snapshot=str(Path(fixture["vault"]) / "snapshot-inside"),
                         raw=str(out_dir / "raw" / "overlapping.stdout.txt")), out_dir),
        scenario("selftest-6-dirty-snapshot-destination",
                 "the snapshot destination already holds files and --snapshot-reuse was not given "
                 "-> refuse; schema 1 called shutil.rmtree here",
                 options(snapshot_reuse=False,
                         raw=str(out_dir / "raw" / "dirty-destination.stdout.txt")), out_dir),
        scenario("selftest-7-input-changed-midrun",
                 "the corrections file is edited while the run is in flight -> the post-run "
                 "re-hash catches it, identity_stability.stable is false, no verdict",
                 options(backend_prefix=shim(mutator_shim),
                         raw=str(out_dir / "raw" / "input-changed.stdout.txt")), out_dir),
    ]

    # The corrections file was deliberately mutated by scenario 7; restore the
    # fixture so the index scenarios measure the corpus their hashes describe.
    (Path(fixture["vault"]) / COMPANION / CORRECTIONS_NAME).write_text(
        FIXTURE_CORRECTIONS, encoding="utf-8", newline="\n")
    shutil.rmtree(snapshot)
    snapshot_inputs(fixture["vault"], snapshot, COMPANION, reuse=False)

    results += [
        scenario("selftest-8-indexed-narrowing-proven",
                 "a backend that honours the candidate index cannot return the note whose "
                 "notes_fts row was deleted -> the indexed path is proven (the run still carries "
                 "no verdict, because a shim answered it)",
                 options(index_mode="indexed", state_root=str(fixture_state_root),
                         backend_prefix=shim(index_shim, f"--shim-state-db {shlex.quote(str(shim_db))}"),
                         raw=str(out_dir / "raw" / "indexed-proven.stdout.txt")), out_dir),
        scenario("selftest-9-unused-index",
                 "a state.db exists and the run is labelled indexed, but the backend never consults "
                 "it -> the witness note comes back, narrowing is not proven, no verdict",
                 options(index_mode="indexed", state_root=str(fixture_state_root),
                         backend_prefix=shim(index_shim,
                                             f"--shim-state-db {shlex.quote(str(shim_db))} --ignore-index"),
                         raw=str(out_dir / "raw" / "unused-index.stdout.txt")), out_dir),
        scenario("selftest-10-indexed-without-state-db",
                 "--index-mode indexed with no state.db at the state root -> refuse before "
                 "measuring rather than silently measure a full scan",
                 options(index_mode="indexed", state_root=str(work / "empty-state-root"),
                         raw=str(out_dir / "raw" / "no-state-db.stdout.txt")), out_dir),
    ]

    # A forged evidence file: the honest run's numbers with the thresholds edited
    # down until they clear. It is committed so the consumer of this evidence can
    # be shown rejecting it -- a gate that reads its bar out of the file it is
    # grading passes this without noticing.
    honest = json.loads((out_dir / "selftest-1-honest-full-scan.json").read_text(encoding="utf-8"))
    forged = json.loads(json.dumps(honest))
    forged["thresholds"]["recall@3"] = 0.10
    forged["thresholds"]["recall@5"] = 0.10
    forged["thresholds"]["scored_minimum"] = 5
    forged["thresholds"]["per_sinif"] = {"tek-not": {"n": 1, "recall@5": 0.1},
                                         "cok-not": {"n": 1, "recall@5": 0.1}}
    forged["overall"] = {"n": 20, "recall@3": 0.2, "recall@5": 0.25, "mrr@5": 0.2}
    forged["per_sinif"] = {"tek-not": {"n": 15, "recall@3": 0.2, "recall@5": 0.2, "mrr@5": 0.2},
                           "cok-not": {"n": 5, "recall@3": 0.2, "recall@5": 0.2, "mrr@5": 0.2}}
    forged["pass"] = {"recall@3": True, "recall@5": True, "scored_minimum": True,
                      "per_sinif": {"tek-not": True, "cok-not": True},
                      "index_path_proven": True, "inputs_stable": True}
    forged["decision"]["recall_gate"] = True
    forged["forgery"] = {
        "is_forged": True,
        "purpose": "a committed negative control for the consumer of this evidence: every number "
                   "here is below the gate and every verdict reads true, because the thresholds "
                   "block was edited down to meet them",
        "edited": ["thresholds.recall@3", "thresholds.recall@5", "thresholds.scored_minimum",
                   "thresholds.per_sinif", "overall", "per_sinif", "pass", "decision.recall_gate"],
        "must_be_rejected_by": "tests/Oom.Tests/RecallCandidateBindingTests.cs, which holds the "
                               "thresholds as constants and never reads them from here",
    }
    forged["substitute"] = {"active": True, "for": "a real measurement",
                            "reason": "forged fixture; see the forgery block"}
    write_json(out_dir / "forged-1-lowered-thresholds.json", forged)

    summary = {
        "gate": "recall-evidence-selftest",
        "schema": SCHEMA,
        "measured": date.today().isoformat(),
        "measured_by": arguments.measured_by,
        "command": literal_command(),
        "run_status": "ok",
        "run_status_reasons": [],
        "raw_artifact": "bench/results/astra-dalga-2/raw/honest.stdout.txt",
        "source_commit": provenance.git_commit(ROOT),
        "binary_sha256": exe_sha,
        "substitute": {
            "active": True,
            "for": "the owner's gold set at Degerlendirme/gold-sorular.jsonl and the concept vault",
            "reason": "This is the lane's own synthetic fixture vault and gold set, not the "
                      "owner's. It proves the gate's wiring and its refusal paths; it is NOT a "
                      "measurement of the product's recall and no number here describes it.",
        },
        "measures": "that verify_recall.py issues a verdict only when the executable matches an "
                    "externally supplied candidate manifest, every retrieval input was snapshotted "
                    "and stayed still, correction ids are real, and the measured path (full scan or "
                    "indexed) was proven by a witness rather than assumed",
        "does_not_measure": "the product's recall on the owner's gold set; that run happens against "
                            "the final unified candidate's published exe in INT-3. It also does not "
                            "measure the product's own index: the indexed scenarios drive a backend "
                            "shim against a fixture state.db, because Retrieve.Build() is not "
                            "reachable from the CLI without running sweep/compile/doctor. The real "
                            "binary's narrowing is pinned in "
                            "tests/Oom.Tests/RecallCandidateBindingTests.cs instead.",
        "fixture": {
            "corpus_sha256": corpus_sha,
            "corpus_files": len(list(snapshot.joinpath(*CONCEPTS).glob("*.md"))),
            "corrections_sha256": corrections_sha,
            "corrections_ids": [item["id"] for item in parse_corrections(FIXTURE_CORRECTIONS)],
            "settings_sha256": settings_sha,
            "gold_sha256": gold_sha,
            "gold_rows": sum(1 for _ in fixture["gold"].open(encoding="utf-8")),
            "witness_note": fixture["witness_note"],
            "witness_query": fixture["witness_query"],
            "seed": 20260911,
        },
        "thresholds": thresholds_block(),
        "scenarios": results,
        "pass": None,
        "decision": None,
    }

    # Structural, not narrative: the selftest passes only if each scenario behaved
    # the way its expectation says, and that verdict is computed, not asserted.
    by_name = {item["scenario"]: item for item in results}

    def refused(name: str) -> bool:
        item = by_name[name]
        return item["run_status"] == "invalid" and item["refused_before_measuring"] is True \
            and item["decision"] is None

    def invalid_after_measuring(name: str) -> bool:
        item = by_name[name]
        return item["run_status"] == "invalid" and item["refused_before_measuring"] is False \
            and item["decision"] is None

    expectations = {
        "honest full scan issues a verdict":
            by_name["selftest-1-honest-full-scan"]["run_status"] == "ok"
            and by_name["selftest-1-honest-full-scan"]["index_witness_proven"] is True
            and by_name["selftest-1-honest-full-scan"]["inputs_stable"] is True
            and (by_name["selftest-1-honest-full-scan"]["decision"] or {}).get("recall_gate") is True,
        "a stale candidate manifest refuses before measuring":
            refused("selftest-2-stale-candidate"),
        "fabricated corpus notes are refused after measuring":
            invalid_after_measuring("selftest-3-corrupt-output"),
        "a fabricated correction id is refused after measuring":
            invalid_after_measuring("selftest-4-fabricated-correction"),
        "an overlapping snapshot destination refuses before measuring":
            refused("selftest-5-overlapping-snapshot"),
        "a non-empty snapshot destination refuses before measuring":
            refused("selftest-6-dirty-snapshot-destination"),
        "an input that moved during the run leaves no verdict":
            by_name["selftest-7-input-changed-midrun"]["inputs_stable"] is False
            and by_name["selftest-7-input-changed-midrun"]["decision"] is None,
        "an honoured index proves narrowing":
            by_name["selftest-8-indexed-narrowing-proven"]["index_witness_proven"] is True,
        "an ignored index does not prove narrowing and leaves no verdict":
            by_name["selftest-9-unused-index"]["index_witness_proven"] is False
            and by_name["selftest-9-unused-index"]["decision"] is None,
        "indexed with no state.db refuses before measuring":
            refused("selftest-10-indexed-without-state-db"),
    }
    summary["expectations"] = {key: bool(value) for key, value in expectations.items()}
    summary["pass"] = {"every_scenario_behaved_as_specified": bool(all(expectations.values()))}
    summary["decision"] = {"selftest": bool(all(expectations.values())),
                           "recall_gate": None,
                           "why_recall_gate_is_null": "measured against a fixture corpus, not the "
                                                      "gold set; a fixture number is not a product "
                                                      "verdict"}
    write_json(out_dir / "selftest-summary.json", summary)

    print(json.dumps({"scenarios": [{k: r[k] for k in ("scenario", "exit_code", "run_status")}
                                    for r in results],
                      "expectations": summary["expectations"],
                      "selftest": summary["decision"]["selftest"]}, indent=2))
    return 0 if all(expectations.values()) else 1


# ------------------------------------------------------------------ cli


def main() -> int:
    parser = argparse.ArgumentParser(
        description="identity-pinned recall gate over the 2.0 ranking")
    subparsers = parser.add_subparsers(dest="mode", required=True)

    def common(target: argparse.ArgumentParser) -> None:
        target.add_argument("--exe", required=True)
        target.add_argument("--measured-by", default="claude")
        target.add_argument("--hook-sample", type=int, default=20,
                            help="ordinary gold questions in the hook control; 0 skips it")
        target.add_argument("--work-dir", default=str(ROOT / "bench" / ".data" / "verify"))
        target.add_argument("--companion-dir", default=COMPANION,
                            help="where Duzeltmeler.md lives under the vault; the product's "
                                 "RetrieveOptions default, not configurable in oom.json")

    run = subparsers.add_parser("run", help="measure, or refuse to")
    common(run)
    run.add_argument("--vault", required=True)
    run.add_argument("--gold", required=True)
    run.add_argument("--candidate-manifest", required=True,
                     help="JSON naming the accepted source_commit and the published exe's sha256; "
                          "written outside this lane. The runner's own checkout is NOT provenance.")
    run.add_argument("--snapshot", required=True,
                     help="copy every retrieval input here and measure the copy; must not overlap "
                          "the source vault and must be empty unless --snapshot-reuse")
    run.add_argument("--snapshot-reuse", action="store_true",
                     help="measure an existing snapshot after verifying it matches the source; "
                          "never deletes anything")
    run.add_argument("--index-mode", required=True, choices=INDEX_MODES,
                     help="which retrieval path is being measured; the two are measured separately "
                          "and each proves itself with the witness probe")
    run.add_argument("--state-root",
                     help="the state root holding state.db. Defaults to the path the product "
                          "derives from the vault; an override is recorded in the evidence.")
    run.add_argument("--index-witness-note", required=True,
                     help="a corpus note whose notes_fts row the probe removes (indexed) or whose "
                          "reachability proves an unnarrowed scan (full-scan)")
    run.add_argument("--index-witness-query", required=True,
                     help="a query that reaches the witness note and nothing else particularly well")
    run.add_argument("--top", type=int, default=5)
    run.add_argument("--max-queries", type=int)
    run.add_argument("--backend-prefix",
                     help="argv prefix in front of --exe; any value forces run_status away "
                          "from 'ok', since binary_sha256 then describes something that did "
                          "not answer the queries")
    run.add_argument("--expect-exe-sha256",
                     help="redundant with the candidate manifest; a contradiction between the two "
                          "is a refusal")
    run.add_argument("--expect-corpus-sha256")
    run.add_argument("--expect-gold-sha256")
    run.add_argument("--expect-corrections-sha256")
    run.add_argument("--expect-settings-sha256")
    run.add_argument("--raw", required=True,
                     help="where the backend's raw stdout is persisted (PROVENANCE.md SS2)")
    run.add_argument("--out", required=True)

    identity = subparsers.add_parser("identity", help="print the identity of every input and exit")
    identity.add_argument("--exe", required=True)
    identity.add_argument("--vault", required=True)
    identity.add_argument("--gold", required=True)
    identity.add_argument("--companion-dir", default=COMPANION)

    test = subparsers.add_parser("selftest", help="prove the gate on this lane's own fixtures")
    common(test)
    test.add_argument("--out-dir", default=str(ROOT / "bench" / "results" / "astra-dalga-2"))

    arguments = parser.parse_args()

    if arguments.mode == "identity":
        vault = Path(arguments.vault).resolve()
        gold_path = Path(arguments.gold).resolve()
        exe = Path(arguments.exe).resolve()
        rows = load_gold(gold_path)
        record = inventory(vault, gold_path, exe, arguments.companion_dir)
        record.pop("corpus_manifest", None)
        record["gold"]["rows"] = len(rows)
        record["gold"]["scored"] = sum(1 for row in rows if row["sinif"] != CANARY)
        record["state_root"] = state_root_for(vault)
        print(json.dumps(record, ensure_ascii=False, indent=2))
        return 0

    if arguments.mode == "selftest":
        return selftest(arguments)

    payload, code = measure(arguments)
    write_json(Path(arguments.out).resolve(), payload)
    print(json.dumps({"run_status": payload["run_status"],
                      "decision": payload.get("decision"),
                      "overall": payload.get("overall"),
                      "reasons": payload.get("run_status_reasons", [])[:6]}, indent=2))
    return code


if __name__ == "__main__":
    sys.exit(main())
