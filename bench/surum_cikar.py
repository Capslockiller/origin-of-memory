#!/usr/bin/env python3
"""Extract historical retrieve.py checkpoints without checking them out."""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
from typing import Any


ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
VERSIONS = ROOT / ".versions"


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _git_show(repo: Path, ref: str, path: str) -> bytes:
    command = ["git", "-c", f"safe.directory={repo.as_posix()}", "-C", str(repo), "show", f"{ref}:{path}"]
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if result.returncode:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace").strip())
    return result.stdout


def _find_legacy_repo() -> Path:
    configured = os.environ.get("BENCHMARK_LEGACY_REPO")
    candidates = [Path(configured)] if configured else []
    if not candidates:
        try:
            for path in Path(REPO.anchor).iterdir():
                try:
                    if (path / ".git").exists():
                        candidates.append(path)
                except OSError:
                    continue
            candidates.sort(key=lambda path: path.name.lower())
        except OSError as exc:
            raise RuntimeError(f"cannot scan drive root for legacy repository: {exc}") from exc
    for candidate in candidates:
        command = [
            "git",
            "-c",
            f"safe.directory={candidate.as_posix()}",
            "-C",
            str(candidate),
            "cat-file",
            "-e",
            "0349cb6f^{commit}",
        ]
        result = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        if result.returncode == 0:
            return candidate.resolve()
    hint = " Set BENCHMARK_LEGACY_REPO to its path." if not configured else ""
    raise RuntimeError(f"legacy repository containing commit 0349cb6f was not found.{hint}")


def _specs(legacy_repo: Path) -> tuple[dict[str, Any], ...]:
    return (
        {"version": "V1", "repo": legacy_repo, "ref": "0349cb6f", "base": ".claude/scripts", "files": ("retrieve.py",), "profile": "A"},
        {"version": "V2", "repo": legacy_repo, "ref": "03d78024", "base": ".claude/scripts", "files": ("retrieve.py", "beyin_ortak.py", "sema.py"), "profile": "B"},
        {"version": "V3", "repo": REPO, "ref": "85a172d", "base": "scripts", "files": ("retrieve.py",), "profile": "A"},
        {"version": "V4", "repo": REPO, "ref": "1913d31", "base": "scripts", "files": ("retrieve.py",), "profile": "B"},
        {"version": "V5", "repo": REPO, "ref": "04e3681", "base": "scripts", "files": ("retrieve.py", "beyin_ortak.py", "sema.py"), "profile": "B"},
        {"version": "V6", "repo": REPO, "ref": "2345b8e", "base": "scripts", "files": ("retrieve.py", "beyin_ortak.py"), "profile": "B"},
        {"version": "V7", "repo": REPO, "ref": "v0.5.0", "base": "scripts", "files": ("retrieve.py", "beyin_ortak.py"), "profile": "B"},
        {"version": "V8", "repo": legacy_repo, "ref": "17509cd8", "base": ".claude/scripts", "files": ("retrieve.py", "beyin_ortak.py"), "profile": "B"},
        {"version": "V9", "repo": REPO, "ref": "WORKING TREE", "base": "scripts", "files": ("retrieve.py", "beyin_ortak.py"), "profile": "B"},
    )


def main() -> int:
    VERSIONS.mkdir(parents=True, exist_ok=True)
    records: list[dict[str, Any]] = []
    for spec in _specs(_find_legacy_repo()):
        target_dir = VERSIONS / spec["version"]
        target_dir.mkdir(parents=True, exist_ok=True)
        hashes: dict[str, str] = {}
        for filename in spec["files"]:
            relative = f"{spec['base']}/{filename}"
            if spec["ref"] == "WORKING TREE":
                source = spec["repo"] / relative
                data = source.read_bytes()
            else:
                data = _git_show(spec["repo"], spec["ref"], relative)
            target = target_dir / filename
            temporary = target.with_suffix(target.suffix + ".tmp")
            temporary.write_bytes(data)
            os.replace(temporary, target)
            hashes[filename] = _sha256(data)
        record = {
            "version": spec["version"],
            "repo": str(spec["repo"]),
            "ref": spec["ref"],
            "source_path": f"{spec['base']}/retrieve.py",
            "profile": spec["profile"],
            "files": hashes,
            "retrieve_sha256": hashes["retrieve.py"],
        }
        records.append(record)
        print(f"{spec['version']}: {len(hashes)} file(s), retrieve.py {hashes['retrieve.py']}")

    groups: list[list[str]] = []
    by_hash: dict[str, list[str]] = {}
    for record in records:
        by_hash.setdefault(record["retrieve_sha256"], []).append(record["version"])
    seen: set[str] = set()
    for record in records:
        digest = record["retrieve_sha256"]
        if digest not in seen:
            groups.append(by_hash[digest])
            seen.add(digest)
    representative = {version: group[0] for group in groups for version in group}
    for record in records:
        record["identical_to"] = [value for value in by_hash[record["retrieve_sha256"]] if value != record["version"]]
        record["representative"] = representative[record["version"]]
    manifest = {
        "format_version": 1,
        "yazan": "codex",
        "model": "gpt-5.6-sol",
        "versions": records,
        "identical_groups": groups,
        "distinct": [group[0] for group in groups],
    }
    manifest_path = VERSIONS / "manifest.json"
    temporary = manifest_path.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, manifest_path)
    print("identical groups: " + "; ".join("=".join(group) for group in groups if len(group) > 1))
    print("distinct: " + ",".join(manifest["distinct"]))
    print(f"manifest: {manifest_path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
