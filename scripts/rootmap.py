#!/usr/bin/env python3
"""Generate the compact knowledge root map and topic hubs atomically."""

# yazan: codex · gpt-5.6-sol

from __future__ import annotations

import csv
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
from typing import Any, Iterable
import uuid

from beyin_ortak import _atomic_write_json, write_health

SCRIPT_DIR = Path(__file__).resolve().parent
VAULT_ROOT = SCRIPT_DIR.parent.parent
CONFIG_PATH = SCRIPT_DIR / "hub-config.json"
STATE_DIR = SCRIPT_DIR / ".state"
ROOT_MAP_BUDGET = 4_000
MODEL_ID = "gpt-5.6-sol"

_TURKISH_I = str.maketrans({"I": "ı", "İ": "i"})
_FRONTMATTER = re.compile(r"\A---\s*\n(.*?)\n---\s*(?:\n|\Z)", re.DOTALL)
_WIKILINK = re.compile(r"\[\[([^\]|#]+)(?:#[^\]|]*)?(?:\|[^\]]*)?\]\]")
_INDEX_ROW = re.compile(r"^\|\s*\[\[([^\]]+)\]\]\s*\|(.*)\|\s*$")
_SENTENCE_END = re.compile(r"(?<=[.!?])(?:\s+|\Z)")


class RootMapError(ValueError):
    """The generated map layer failed validation."""


@dataclass(frozen=True)
class Concept:
    name: str
    title: str
    aliases: tuple[str, ...]
    tags: tuple[str, ...]
    updated: str
    body: str
    links: tuple[str, ...]


def turkish_fold(value: str) -> str:
    """Fold text without locale-dependent lower/upper operations."""
    return value.translate(_TURKISH_I).casefold()


def _unquote(value: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        if value[0] == '"':
            try:
                decoded = json.loads(value)
                if isinstance(decoded, str):
                    return decoded
            except json.JSONDecodeError:
                pass
        return value[1:-1].replace("''", "'")
    return value


def _inline_list(value: str) -> list[str]:
    value = value.strip()
    if not (value.startswith("[") and value.endswith("]")):
        return []
    inner = value[1:-1].strip()
    if not inner:
        return []
    return [
        _unquote(item)
        for item in next(csv.reader([inner], skipinitialspace=True))
        if item.strip()
    ]


def _parse_frontmatter(text: str, source: Path) -> tuple[dict[str, Any], str]:
    match = _FRONTMATTER.match(text)
    if match is None:
        raise RootMapError(f"frontmatter-missing:{source.name}")
    lines = match.group(1).splitlines()
    data: dict[str, Any] = {}
    index = 0
    while index < len(lines):
        line = lines[index]
        if not line or line[:1].isspace() or ":" not in line:
            index += 1
            continue
        key, raw = line.split(":", 1)
        key = key.strip()
        raw = raw.strip()
        if raw.startswith("["):
            data[key] = _inline_list(raw)
        elif not raw:
            items: list[str] = []
            cursor = index + 1
            while cursor < len(lines):
                child = lines[cursor]
                child_match = re.match(r"^\s+-\s+(.*)$", child)
                if child_match is None:
                    break
                items.append(_unquote(child_match.group(1)))
                cursor += 1
            data[key] = items
            index = cursor - 1
        else:
            data[key] = _unquote(raw)
        index += 1
    return data, text[match.end() :]


def _link_name(target: str) -> str:
    normalized = target.strip().replace("\\", "/")
    name = normalized.rsplit("/", 1)[-1]
    return name[:-3] if name.endswith(".md") else name


def load_concepts(concepts_dir: Path) -> list[Concept]:
    concepts = []
    for path in sorted(concepts_dir.glob("*.md"), key=lambda item: turkish_fold(item.name)):
        text = path.read_text(encoding="utf-8")
        frontmatter, body = _parse_frontmatter(text, path)
        concepts.append(
            Concept(
                name=path.stem,
                title=str(frontmatter.get("title", path.stem)),
                aliases=tuple(str(item) for item in frontmatter.get("aliases", [])),
                tags=tuple(str(item) for item in frontmatter.get("tags", [])),
                updated=str(frontmatter.get("updated", "")),
                body=body,
                links=tuple(_link_name(item) for item in _WIKILINK.findall(body)),
            )
        )
    return concepts


def _load_config(config_path: Path) -> dict[str, Any]:
    config = json.loads(config_path.read_text(encoding="utf-8"))
    hubs = config.get("hubs")
    catch_all = config.get("catch_all")
    if not isinstance(hubs, list) or not hubs or not isinstance(catch_all, str):
        raise RootMapError("hub-config-invalid")
    ids = [hub.get("id") for hub in hubs if isinstance(hub, dict)]
    if len(ids) != len(hubs) or len(set(ids)) != len(ids) or catch_all not in ids:
        raise RootMapError("hub-config-invalid")
    return config


def assign_memberships(
    concepts: Iterable[Concept], config: dict[str, Any]
) -> dict[str, list[Concept]]:
    """Assign concepts to every matching hub, falling back only on no match."""
    hubs = config["hubs"]
    memberships: dict[str, list[Concept]] = {hub["id"]: [] for hub in hubs}
    catch_all = config["catch_all"]
    for concept in concepts:
        concept_tags = {turkish_fold(tag) for tag in concept.tags}
        folded_name = turkish_fold(concept.name)
        matched = []
        for hub in hubs:
            hub_tags = {turkish_fold(str(tag)) for tag in hub.get("tags", [])}
            keys = [turkish_fold(str(key)) for key in hub.get("title_keys", [])]
            if concept_tags.intersection(hub_tags) or any(
                key in folded_name for key in keys
            ):
                matched.append(hub["id"])
        if not matched:
            matched = [catch_all]
        for hub_id in matched:
            memberships[hub_id].append(concept)
    return memberships


def _parse_index_rows(index_text: str) -> list[tuple[str, str, str]]:
    rows = []
    for line in index_text.splitlines():
        match = _INDEX_ROW.match(line)
        if match is None:
            continue
        tail = match.group(2).rsplit("|", 2)
        if len(tail) != 3:
            continue
        rows.append((match.group(1).strip(), tail[0].strip(), line))
    return rows


def _full_index_text(rows: Iterable[tuple[str, str, str]]) -> str:
    body = "\n".join(row[2] for row in rows)
    return (
        "---\n"
        "yazan: codex\n"
        f"model: {MODEL_ID}\n"
        "---\n\n"
        "# Bilgi Tabanı — Tam Kavram İndeksi\n\n"
        "> Bu tabloyu akşam derleyicisi doldurur ve günceller. Elle satır ekleme.\n\n"
        "| Makale | Özet | Kaynak | Güncellendi |\n"
        "|---|---|---|---|\n"
        f"{body}\n"
    )


def _escape_cell(value: str) -> str:
    return " ".join(value.replace("|", "\\|").split())


def _fallback_summary(concept: Concept) -> str:
    paragraphs = []
    for block in re.split(r"\n\s*\n", concept.body):
        candidate = " ".join(block.split())
        if candidate and not candidate.startswith("#"):
            paragraphs.append(candidate)
    text = paragraphs[0] if paragraphs else concept.title
    sentence = _SENTENCE_END.split(text, maxsplit=1)[0]
    if len(sentence) > 160:
        sentence = sentence[:157].rstrip() + "..."
    return sentence


def _top_tags(members: Iterable[Concept]) -> str:
    counts: dict[str, tuple[str, int]] = {}
    for concept in members:
        for tag in concept.tags:
            folded = turkish_fold(tag)
            display, count = counts.get(folded, (tag, 0))
            counts[folded] = (display, count + 1)
    ordered = sorted(counts.values(), key=lambda item: (-item[1], turkish_fold(item[0])))
    return ", ".join(item[0] for item in ordered[:5]) or "—"


def _root_map_text(
    config: dict[str, Any], memberships: dict[str, list[Concept]]
) -> str:
    lines = [
        "---",
        "yazan: codex",
        f"model: {MODEL_ID}",
        "---",
        "",
        "# Kök Harita",
        "",
        "Bu dosya bilgi tabanının giriş katmanıdır.",
        "Konu merkezleri `knowledge/hubs/` altında, tam kavram tablosu [[index-full]] içindedir.",
        "",
        "| Konu merkezi | Kavram | Son güncelleme | Kapsam | Sık etiketler |",
        "|---|---:|---|---|---|",
    ]
    for hub in config["hubs"]:
        members = memberships[hub["id"]]
        latest = max((member.updated for member in members), default="—") or "—"
        lines.append(
            "| [[hubs/{id}|{ad}]] | {count} | {latest} | {scope} | {tags} |".format(
                id=hub["id"],
                ad=_escape_cell(str(hub["ad"])),
                count=len(members),
                latest=_escape_cell(latest),
                scope=_escape_cell(str(hub["kapsam"])),
                tags=_escape_cell(_top_tags(members)),
            )
        )
    return "\n".join(lines) + "\n"


def boundary_counts(
    source_id: str,
    target_id: str,
    memberships: dict[str, list[Concept]],
) -> tuple[int, int]:
    source = memberships[source_id]
    target = memberships[target_id]
    target_names = {turkish_fold(member.name) for member in target}
    common = len(
        {turkish_fold(member.name) for member in source}.intersection(target_names)
    )
    outgoing = sum(
        1
        for member in source
        for link in member.links
        if turkish_fold(link) in target_names
    )
    return common, outgoing


def _hub_text(
    hub: dict[str, Any],
    config: dict[str, Any],
    memberships: dict[str, list[Concept]],
    summaries: dict[str, str],
    inbound: dict[str, int],
) -> str:
    members = memberships[hub["id"]]
    ordered_members = sorted(members, key=lambda item: turkish_fold(item.name))
    spine = sorted(
        members,
        key=lambda item: (-inbound.get(turkish_fold(item.name), 0), turkish_fold(item.name)),
    )[:5]
    lines = [
        "---",
        "yazan: codex",
        f"model: {MODEL_ID}",
        "---",
        "",
        f"# {hub['ad']}",
        "",
        str(hub["kapsam"]),
        "",
        "## Omurga",
        "",
    ]
    if spine:
        lines.extend(
            f"- [[{member.name}]] — {inbound.get(turkish_fold(member.name), 0)} gelen bağlantı"
            for member in spine
        )
    else:
        lines.append("- Üye yok.")
    lines.extend(["", "## Üyeler", "", "| Kavram | Özet |", "|---|---|"])
    for member in ordered_members:
        summary = summaries.get(turkish_fold(member.name)) or _fallback_summary(member)
        lines.append(f"| [[{member.name}]] | {_escape_cell(summary)} |")
    lines.extend(["", "## Sınır", ""])
    boundaries = []
    for target in config["hubs"]:
        if target["id"] == hub["id"]:
            continue
        common, outgoing = boundary_counts(hub["id"], target["id"], memberships)
        if common or outgoing:
            boundaries.append(
                f"- [[hubs/{target['id']}|{target['ad']}]] — "
                f"ortak üye: {common}; giden wikilink: {outgoing}"
            )
    lines.extend(boundaries or ["- Bağlantılı başka konu merkezi yok."])
    return "\n".join(lines) + "\n"


def _inbound_counts(concepts: Iterable[Concept]) -> dict[str, int]:
    names = {turkish_fold(concept.name) for concept in concepts}
    counts = {name: 0 for name in names}
    for concept in concepts:
        for link in concept.links:
            folded = turkish_fold(link)
            if folded in names:
                counts[folded] += 1
    return counts


def _unlink_quietly(path: Path) -> None:
    try:
        path.unlink()
    except OSError:
        pass


def _publish(staged: dict[Path, Path]) -> None:
    """Publish the whole map layer or none of it.

    The map is one document spread over several files: an index that promises
    hubs, and hubs that answer for it. Renaming them one by one leaves readers
    holding an index whose hubs are a generation behind if the loop dies half
    way. Phase one copies every live target aside as ``<dest>.bak-<run id>``
    and writes the new content next to it as ``<dest>.tmp-<run id>``; phase two
    renames them all. Any failure restores what was already renamed and
    re-raises, so a partial map never reaches the vault.
    """
    run_id = uuid.uuid4().hex[:12]
    entries: list[tuple[Path, Path | None]] = []
    temporaries: list[Path] = []
    renamed: list[tuple[Path, Path | None]] = []
    try:
        for temporary, destination in staged.items():
            destination.parent.mkdir(parents=True, exist_ok=True)
            backup: Path | None = None
            if destination.exists():
                backup = destination.with_name(f"{destination.name}.bak-{run_id}")
                shutil.copy2(destination, backup)
            entries.append((destination, backup))
            target = destination.with_name(f"{destination.name}.tmp-{run_id}")
            shutil.copyfile(temporary, target)
            temporaries.append(target)
        for index, (destination, backup) in enumerate(entries):
            os.replace(temporaries[index], destination)
            renamed.append((destination, backup))
    except BaseException:
        for destination, backup in reversed(renamed):
            try:
                if backup is None:
                    if destination.exists():
                        destination.unlink()
                else:
                    os.replace(backup, destination)
            except OSError:
                pass
        for target in temporaries:
            _unlink_quietly(target)
        for _destination, backup in entries:
            if backup is not None:
                _unlink_quietly(backup)
        raise
    for _destination, backup in entries:
        if backup is not None:
            _unlink_quietly(backup)


def regenerate(
    vault_root: Path = VAULT_ROOT,
    config_path: Path | None = None,
    state_dir: Path | None = None,
    char_budget: int = ROOT_MAP_BUDGET,
) -> dict[str, Any]:
    """Regenerate all map outputs and return publication statistics."""
    vault_root = Path(vault_root)
    config_path = Path(config_path) if config_path is not None else vault_root / ".claude" / "scripts" / "hub-config.json"
    state_dir = Path(state_dir) if state_dir is not None else vault_root / ".claude" / "scripts" / ".state"
    knowledge = vault_root / "knowledge"
    index_path = knowledge / "index.md"
    full_path = knowledge / "index-full.md"
    temp_root: Path | None = None
    try:
        config = _load_config(config_path)
        concepts = load_concepts(knowledge / "concepts")
        memberships = assign_memberships(concepts, config)

        migrating = not full_path.exists()
        if migrating:
            rows = _parse_index_rows(index_path.read_text(encoding="utf-8"))
            full_text = _full_index_text(rows)
        else:
            full_text = full_path.read_text(encoding="utf-8")
            rows = _parse_index_rows(full_text)

        summaries = {turkish_fold(name): summary for name, summary, _line in rows}
        inbound = _inbound_counts(concepts)
        root_text = _root_map_text(config, memberships)
        hub_texts = {
            hub["id"]: _hub_text(hub, config, memberships, summaries, inbound)
            for hub in config["hubs"]
        }

        temp_root = Path(tempfile.mkdtemp(prefix="rootmap-", dir=knowledge))
        staged: dict[Path, Path] = {}
        temp_index = temp_root / "index.md"
        temp_index.write_text(root_text, encoding="utf-8", newline="\n")
        staged[temp_index] = index_path
        if migrating:
            temp_full = temp_root / "index-full.md"
            temp_full.write_text(full_text, encoding="utf-8", newline="\n")
            staged[temp_full] = full_path
        temp_hubs = temp_root / "hubs"
        temp_hubs.mkdir()
        for hub_id, text in hub_texts.items():
            temporary = temp_hubs / f"{hub_id}.md"
            temporary.write_text(text, encoding="utf-8", newline="\n")
            staged[temporary] = knowledge / "hubs" / f"{hub_id}.md"

        uncovered = sorted(
            concept.name
            for concept in concepts
            if not any(
                concept in memberships[hub_id]
                and f"| [[{concept.name}]] |" in hub_texts[hub_id]
                for hub_id in memberships
            )
        )
        if uncovered:
            raise RootMapError(f"concept-uncovered:{uncovered[0]}")
        if len(root_text) > char_budget:
            raise RootMapError(f"root-map-budget:{len(root_text)}/{char_budget}")
        expected_hubs = {hub["id"] for hub in config["hubs"]}
        if set(hub_texts) != expected_hubs:
            raise RootMapError("hub-output-mismatch")
        for temporary in staged:
            if not temporary.is_file() or not temporary.read_text(encoding="utf-8"):
                raise RootMapError(f"staged-output-invalid:{temporary.name}")

        _publish(staged)
        parity = f"{len(rows)}/{len(concepts)}"
        if len(rows) != len(concepts):
            write_health(
                state_dir,
                f"index-full-parity:{parity}",
                warning=True,
                component="rootmap",
            )
        else:
            write_health(state_dir, "", component="rootmap")
        return {
            "index_chars": len(root_text),
            "hub_chars": {hub_id: len(text) for hub_id, text in hub_texts.items()},
            "hub_members": {
                hub_id: len(members) for hub_id, members in memberships.items()
            },
            "catch_all": config["catch_all"],
            "catch_all_size": len(memberships[config["catch_all"]]),
            "parity": parity,
            "parity_ok": len(rows) == len(concepts),
            "migrated": migrating,
        }
    except Exception as exc:
        write_health(state_dir, str(exc), component="rootmap")
        raise
    finally:
        if temp_root is not None:
            shutil.rmtree(temp_root, ignore_errors=True)


def main() -> int:
    report = regenerate()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
