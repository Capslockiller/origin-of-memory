#!/usr/bin/env python3
# yazan: codex
# model: gpt-5.6-sol
"""One-off migration tool for extracting a Google Takeout Gemini archive."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass, replace
import datetime as dt
import hashlib
from html.parser import HTMLParser
import json
import os
from pathlib import Path
import re
import sys
import unicodedata
from typing import Any, Iterable, Sequence
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "scripts"))
import secret_guard


SCRIPT_DIR = Path(__file__).resolve().parent
VAULT_ROOT = SCRIPT_DIR.parent.parent
MEMBER_NAME = "Takeout/Etkinliğim/Gemini Uygulamaları/Etkinliğim.html"
RAW_DIR = VAULT_ROOT / "arsiv" / "ham" / "gemini"
STAGE_DIR = VAULT_ROOT / ".stage" / "gemini"
RECORDS_PATH = STAGE_DIR / "kayitlar.jsonl"
MANIFEST_PATH = STAGE_DIR / "manifest.jsonl"
CLASSIFICATION_DIR = STAGE_DIR / "siniflandirma"
TOPIC_DIR = STAGE_DIR / "konu"

BLOCK_RE = re.compile(
    r"<div class=[\"']outer-cell.*?</div></div></div>",
    re.DOTALL,
)
TIMESTAMP_RE = re.compile(
    r"(?P<day>\d{1,2})\s+"
    r"(?P<month>Oca|Şub|Mar|Nis|May|Haz|Tem|Ağu|Eyl|Eki|Kas|Ara)\s+"
    r"(?P<year>\d{4})\s+"
    r"(?P<clock>\d{2}:\d{2}:\d{2})\s+GMT(?P<offset>[+-]\d{2}:\d{2})"
)
BOILERPLATE_RE = re.compile(r"\s*sorgusuna yanıt istendi\b", re.IGNORECASE)
WHITESPACE_RE = re.compile(r"\s+")
MONTHS = {
    "Oca": 1,
    "Şub": 2,
    "Mar": 3,
    "Nis": 4,
    "May": 5,
    "Haz": 6,
    "Tem": 7,
    "Ağu": 8,
    "Eyl": 9,
    "Eki": 10,
    "Kas": 11,
    "Ara": 12,
}
EXPECTED_MONTH_COUNTS = {
    "2025-10": 44,
    "2025-11": 81,
    "2025-12": 56,
    "2026-01": 119,
    "2026-02": 69,
    "2026-03": 249,
    "2026-04": 119,
    "2026-05": 109,
    "2026-06": 69,
    "2026-07": 337,
    "2026-08": 141,
}
EXPECTED_RECORDS = 1393
EXPECTED_DAYS = 172
BATCH_RECORDS = 40
PACK_CHARS = 400_000

KEYWORD_TAGS: tuple[tuple[str, tuple[str, ...]], ...] = (
    ("work-project", ("project", "client", "deadline", "meeting")),
    ("health", ("health", "sleep", "exercise", "nutrition")),
    ("learning", ("course", "study", "exam", "book")),
    ("finances", ("budget", "invoice", "tax", "investment")),
    ("software", ("python", "javascript", "database", "api")),
)


@dataclass(frozen=True)
class GeminiRecord:
    id: str
    when: dt.datetime
    tip: str
    on_etiket: str
    soru: str
    cevap: str

    @property
    def ts(self) -> str:
        return self.when.isoformat(timespec="seconds")

    @property
    def chars(self) -> int:
        return len(self.soru) + len(self.cevap)

    def as_dict(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "ts": self.ts,
            "chars": self.chars,
            "tip": self.tip,
            "on_etiket": self.on_etiket,
            "soru": self.soru,
            "cevap": self.cevap,
        }


class _FirstContentCell(HTMLParser):
    """Bir kayıt bloğundaki ilk sol gövde hücresinin düz metnini toplar."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.div_depth = 0
        self.capture_depth = -1
        self.capturing = False
        self.finished = False
        self.parts: list[str] = []

    def handle_starttag(
        self,
        tag: str,
        attrs: list[tuple[str, str | None]],
    ) -> None:
        if tag == "div":
            self.div_depth += 1
            classes = dict(attrs).get("class", "") or ""
            class_set = set(classes.split())
            if (
                not self.finished
                and not self.capturing
                and "content-cell" in class_set
                and "mdl-typography--body-1" in class_set
                and "mdl-typography--text-right" not in class_set
            ):
                self.capturing = True
                self.capture_depth = self.div_depth
                return
        if self.capturing and tag in {
            "br",
            "p",
            "li",
            "ol",
            "ul",
            "h1",
            "h2",
            "h3",
            "h4",
            "table",
            "tr",
            "td",
        }:
            self.parts.append(" ")

    def handle_endtag(self, tag: str) -> None:
        if tag == "div":
            if self.capturing and self.div_depth == self.capture_depth:
                self.capturing = False
                self.finished = True
            self.div_depth -= 1
            return
        if self.capturing and tag in {
            "p",
            "li",
            "ol",
            "ul",
            "h1",
            "h2",
            "h3",
            "h4",
            "table",
            "tr",
            "td",
        }:
            self.parts.append(" ")

    def handle_data(self, data: str) -> None:
        if self.capturing:
            self.parts.append(data)

    def text(self) -> str:
        return _collapse("".join(self.parts))


def _collapse(text: str) -> str:
    return WHITESPACE_RE.sub(" ", text).strip()


def _normalized(text: str) -> str:
    folded = unicodedata.normalize("NFKD", text.casefold())
    return "".join(character for character in folded if not unicodedata.combining(character))


NORMALIZED_KEYWORD_TAGS = tuple(
    (tag, tuple(_normalized(keyword) for keyword in keywords))
    for tag, keywords in KEYWORD_TAGS
)


def pre_tag(text: str) -> str:
    haystack = _normalized(text)
    for tag, keywords in NORMALIZED_KEYWORD_TAGS:
        if any(keyword in haystack for keyword in keywords):
            return tag
    return "diger"


def _parse_timestamp(match: re.Match[str]) -> dt.datetime:
    month = MONTHS[match.group("month")]
    offset_text = match.group("offset")
    sign = 1 if offset_text[0] == "+" else -1
    hours, minutes = (int(part) for part in offset_text[1:].split(":"))
    zone = dt.timezone(sign * dt.timedelta(hours=hours, minutes=minutes))
    hour, minute, second = (int(part) for part in match.group("clock").split(":"))
    return dt.datetime(
        int(match.group("year")),
        month,
        int(match.group("day")),
        hour,
        minute,
        second,
        tzinfo=zone,
    )


def _stable_id(when: dt.datetime, tip: str, soru: str, cevap: str) -> str:
    payload = "\0".join((when.isoformat(), tip, soru, cevap)).encode("utf-8")
    return "gemini-" + hashlib.sha256(payload).hexdigest()[:20]


def parse_block(block: str) -> GeminiRecord:
    parser = _FirstContentCell()
    parser.feed(block)
    body = parser.text()
    timestamp = TIMESTAMP_RE.search(body)
    if timestamp is None:
        raise ValueError("gemini-timestamp-missing")

    before = _collapse(body[: timestamp.start()])
    after = _collapse(body[timestamp.end() :])
    when = _parse_timestamp(timestamp)
    if "adlı Gemini Canvas oluşturuldu" in before:
        tip = "canvas-bildirim"
        soru = before
        cevap = ""
    elif "sorgusuna yanıt istendi" in before.casefold():
        tip = "soru"
        soru = _collapse(BOILERPLATE_RE.sub(" ", before, count=1))
        cevap = after
    else:
        # Takeout ayrıca "Geri bildirim gönderildi" gibi model diyaloğu
        # olmayan etkinlikler üretiyor; üç-değerli şemada kullanım kaydıdır.
        tip = "kullanim"
        soru = before
        cevap = ""
    tag = pre_tag(f"{soru} {cevap}")
    return GeminiRecord(
        id=_stable_id(when, tip, soru, cevap),
        when=when,
        tip=tip,
        on_etiket=tag,
        soru=soru,
        cevap=cevap,
    )


def parse_html(text: str) -> list[GeminiRecord]:
    blocks = BLOCK_RE.findall(text)
    records = [parse_block(block) for block in blocks]
    ids = [record.id for record in records]
    if len(ids) != len(set(ids)):
        raise ValueError("gemini-record-id-collision")
    return records


def load_takeout(zip_path: Path) -> list[GeminiRecord]:
    try:
        with zipfile.ZipFile(zip_path) as archive:
            try:
                info = archive.getinfo(MEMBER_NAME)
            except KeyError as exc:
                raise ValueError("gemini-html-missing") from exc
            with archive.open(info) as source:
                text = source.read().decode("utf-8")
    except zipfile.BadZipFile as exc:
        raise ValueError("gemini-zip-invalid") from exc
    return parse_html(text)


def validate_snapshot(records: Sequence[GeminiRecord]) -> None:
    months = Counter(record.when.strftime("%Y-%m") for record in records)
    if len(records) != EXPECTED_RECORDS:
        raise ValueError(f"gemini-record-count:{len(records)}")
    if dict(sorted(months.items())) != EXPECTED_MONTH_COUNTS:
        raise ValueError(
            "gemini-month-counts:" + json.dumps(dict(sorted(months.items())))
        )
    days = {record.when.date().isoformat() for record in records}
    if len(days) != EXPECTED_DAYS:
        raise ValueError(f"gemini-day-count:{len(days)}")


def redact_records(
    records: Iterable[GeminiRecord],
) -> tuple[list[GeminiRecord], int, Counter[str]]:
    redacted: list[GeminiRecord] = []
    replacements = 0
    kinds: Counter[str] = Counter()
    for record in records:
        soru, soru_hits = secret_guard.redact(record.soru)
        cevap, cevap_hits = secret_guard.redact(record.cevap)
        for original, cleaned, hits in (
            (record.soru, soru, soru_hits),
            (record.cevap, cevap, cevap_hits),
        ):
            replacements += max(0, cleaned.count("[SIR:") - original.count("[SIR:"))
            for name in hits:
                marker = f"[SIR:{name}]"
                kinds[name] += max(0, cleaned.count(marker) - original.count(marker))
        redacted.append(replace(record, soru=soru, cevap=cevap))
    return redacted, replacements, kinds


def _jsonl(records: Iterable[dict[str, Any]]) -> str:
    return "".join(
        json.dumps(record, ensure_ascii=False, sort_keys=False) + "\n"
        for record in records
    )


def _atomic_write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    try:
        temporary.write_text(text, encoding="utf-8")
        os.replace(temporary, path)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _record_markdown(record: GeminiRecord) -> str:
    heading = record.when.strftime("%Y-%m-%d %H:%M:%S")
    if record.tip == "soru":
        return (
            f"## {heading}\n\n**Soru:** {record.soru}\n\n"
            f"**Cevap:** {record.cevap}\n"
        )
    label = "Canvas bildirimi" if record.tip == "canvas-bildirim" else "Kullanım kaydı"
    return f"## {heading}\n\n**{label}:** {record.soru}\n"


def _raw_month(month: str, records: Sequence[GeminiRecord]) -> str:
    dates = sorted(record.when.date().isoformat() for record in records)
    frontmatter = (
        "---\n"
        "yazan: codex\n"
        "model: gpt-5.6-sol\n"
        "type: ham-arsiv\n"
        "kaynak: gemini\n"
        "uretici: gemini_ayikla.py\n"
        f"baslangic: {dates[0]}\n"
        f"bitis: {dates[-1]}\n"
        f"kayit-sayisi: {len(records)}\n"
        "---\n\n"
        f"# Gemini ham arşiv — {month}\n\n"
    )
    return frontmatter + "\n".join(_record_markdown(record) for record in records)


def _classification_batch(number: int, records: Sequence[GeminiRecord]) -> str:
    text = (
        "---\n"
        "yazan: codex\n"
        "model: gpt-5.6-sol\n"
        "type: gemini-siniflandirma-partisi\n"
        f"parti: {number}\n"
        f"kayit-sayisi: {len(records)}\n"
        "---\n\n"
        f"# Gemini sınıflandırma partisi {number:02d}\n\n"
    )
    sections: list[str] = []
    for record in records:
        excerpt = _collapse(f"Soru: {record.soru} Cevap: {record.cevap}")[:500]
        sections.append(
            f"## {record.id} · {record.when.date().isoformat()} · {record.on_etiket}\n\n"
            f"{excerpt}\n"
        )
    return text + "\n".join(sections)


def write_extraction(records: Sequence[GeminiRecord]) -> None:
    by_month: dict[str, list[GeminiRecord]] = defaultdict(list)
    for record in records:
        by_month[record.when.strftime("%Y-%m")].append(record)
    for month, month_records in sorted(by_month.items()):
        _atomic_write(RAW_DIR / f"{month}.md", _raw_month(month, month_records))

    _atomic_write(RECORDS_PATH, _jsonl(record.as_dict() for record in records))
    manifest = (
        {
            "id": record.id,
            "ts": record.ts,
            "chars": record.chars,
            "tip": record.tip,
            "on_etiket": record.on_etiket,
        }
        for record in records
    )
    _atomic_write(MANIFEST_PATH, _jsonl(manifest))

    questions = [record for record in records if record.tip == "soru"]
    for start in range(0, len(questions), BATCH_RECORDS):
        number = start // BATCH_RECORDS + 1
        batch = questions[start : start + BATCH_RECORDS]
        _atomic_write(
            CLASSIFICATION_DIR / f"parti-{number:02d}.md",
            _classification_batch(number, batch),
        )


def print_report(
    records: Sequence[GeminiRecord],
    redactions: int,
    redaction_kinds: Counter[str],
    dry_run: bool,
) -> None:
    months = Counter(record.when.strftime("%Y-%m") for record in records)
    types = Counter(record.tip for record in records)
    print("AY      KAYIT")
    for month in sorted(months):
        print(f"{month} {months[month]}")
    print(f"TOPLAM KAYIT: {len(records)}")
    print(f"TOPLAM GÜN: {len({record.when.date() for record in records})}")
    print(f"TOPLAM KARAKTER: {sum(record.chars for record in records)}")
    print(
        "TÜRLER: "
        f"soru={types['soru']} kullanim={types['kullanim']} "
        f"canvas-bildirim={types['canvas-bildirim']}"
    )
    print(f"KARARTMA: {redactions}")
    if redaction_kinds:
        detail = ", ".join(f"{name}={count}" for name, count in sorted(redaction_kinds.items()))
        print(f"KARARTMA TÜRLERİ: {detail}")
    if dry_run:
        print("(kuru koşu — hiçbir şey yazılmadı)")


def _read_jsonl(path: Path) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as source:
        for line_number, line in enumerate(source, start=1):
            if not line.strip():
                continue
            try:
                value = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ValueError(f"jsonl-invalid:{path.name}:{line_number}") from exc
            if not isinstance(value, dict):
                raise ValueError(f"jsonl-not-object:{path.name}:{line_number}")
            records.append(value)
    return records


def merge_manifest(input_path: Path, manifest_path: Path = MANIFEST_PATH) -> tuple[int, int]:
    manifest = _read_jsonl(manifest_path)
    results = _read_jsonl(input_path)
    known = {str(record.get("id")) for record in manifest}
    updates: dict[str, tuple[str, int]] = {}
    for result in results:
        record_id = result.get("id")
        konu = result.get("konu")
        onem = result.get("onem")
        if not isinstance(record_id, str) or not record_id:
            raise ValueError("siniflandirma-id-invalid")
        if not isinstance(konu, str) or not konu.strip():
            raise ValueError(f"siniflandirma-konu-invalid:{record_id}")
        # Sözleşme (brief + sınıflandırma prompt'u) onem'i 1|2|3 tamsayı
        # tanımlar; string hâline de tolerans gösterilir.
        if isinstance(onem, bool) or not isinstance(onem, (int, str)):
            raise ValueError(f"siniflandirma-onem-invalid:{record_id}")
        onem_text = str(onem).strip()
        if onem_text not in ("1", "2", "3"):
            raise ValueError(f"siniflandirma-onem-invalid:{record_id}")
        updates[record_id] = (konu.strip(), int(onem_text))
    unknown = sorted(set(updates) - known)
    if unknown:
        raise ValueError("manifest-unknown-id:" + ",".join(unknown))

    for record in manifest:
        record_id = str(record.get("id"))
        if record_id in updates:
            record["konu"], record["onem"] = updates[record_id]
    _atomic_write(manifest_path, _jsonl(manifest))
    questions = [record for record in manifest if record.get("tip") == "soru"]
    covered = sum(
        isinstance(record.get("konu"), str) and bool(record.get("konu"))
        and isinstance(record.get("onem"), int) and record.get("onem") in (1, 2, 3)
        for record in questions
    )
    return covered, len(questions)


def _slug(text: str) -> str:
    value = _normalized(text).replace("ı", "i")
    value = re.sub(r"[^a-z0-9]+", "-", value).strip("-")
    return value or "konu"


def _pack_frontmatter(konu: str, part: int, count: int) -> str:
    return (
        "---\n"
        "yazan: codex\n"
        "model: gpt-5.6-sol\n"
        "type: gemini-konu-paketi\n"
        f"konu: {json.dumps(konu, ensure_ascii=False)}\n"
        f"parti: {part}\n"
        f"kayit-sayisi: {count}\n"
        "---\n\n"
        f"# Gemini konu paketi — {konu} — {part:02d}\n\n"
    )


def package_topics(
    records_path: Path = RECORDS_PATH,
    manifest_path: Path = MANIFEST_PATH,
    output_dir: Path = TOPIC_DIR,
) -> tuple[int, int]:
    record_values = _read_jsonl(records_path)
    manifest_values = _read_jsonl(manifest_path)
    topics = {
        str(record.get("id")): record.get("konu")
        for record in manifest_values
        if isinstance(record.get("konu"), str) and record.get("konu")
    }
    questions = [record for record in record_values if record.get("tip") == "soru"]
    missing = [str(record.get("id")) for record in questions if str(record.get("id")) not in topics]
    if missing:
        raise ValueError(f"manifest-konu-eksik:{len(missing)}")

    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for record in questions:
        grouped[str(topics[str(record.get("id"))])].append(record)
    files_written = 0
    for konu, topic_records in sorted(grouped.items()):
        topic_records.sort(key=lambda record: str(record.get("ts", "")))
        packs: list[list[dict[str, Any]]] = []
        current: list[dict[str, Any]] = []
        current_chars = 0
        for record in topic_records:
            rendered = (
                f"## {str(record['ts']).replace('T', ' ', 1)}\n\n"
                f"**Soru:** {record.get('soru', '')}\n\n"
                f"**Cevap:** {record.get('cevap', '')}\n"
            )
            if current and current_chars + len(rendered) > PACK_CHARS:
                packs.append(current)
                current = []
                current_chars = 0
            current.append(record)
            current_chars += len(rendered)
        if current:
            packs.append(current)
        for index, pack in enumerate(packs, start=1):
            body = "\n".join(
                f"## {str(record['ts']).replace('T', ' ', 1)}\n\n"
                f"**Soru:** {record.get('soru', '')}\n\n"
                f"**Cevap:** {record.get('cevap', '')}\n"
                for record in pack
            )
            _atomic_write(
                output_dir / f"{_slug(konu)}-{index:02d}.md",
                _pack_frontmatter(konu, index, len(pack)) + body,
            )
            files_written += 1
    return files_written, len(questions)


def _parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    extract = subparsers.add_parser("ayikla")
    extract.add_argument("--zip", type=Path, required=True)
    extract.add_argument("--dry-run", action="store_true")
    subparsers.add_parser("paketle")
    merge = subparsers.add_parser("manifest-birlestir")
    merge.add_argument("--girdi", type=Path, required=True)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = _parse_args(argv)
    try:
        if args.command == "ayikla":
            records = load_takeout(args.zip)
            validate_snapshot(records)
            records, redactions, kinds = redact_records(records)
            if not args.dry_run:
                write_extraction(records)
            print_report(records, redactions, kinds, args.dry_run)
            return 0
        if args.command == "manifest-birlestir":
            covered, total = merge_manifest(args.girdi)
            print(f"Kapsama: {covered}/{total}")
            return 0
        files_written, records_packed = package_topics()
        print(f"Paketlenen soru: {records_packed}")
        print(f"Yazılan paket: {files_written}")
        return 0
    except (OSError, UnicodeError, ValueError, zipfile.BadZipFile) as exc:
        print(f"HATA: {str(exc) or exc.__class__.__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
