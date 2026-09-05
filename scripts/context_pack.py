#!/usr/bin/env python3
"""Compose persistent-memory context for manual use in consumer web chats."""

# yazan: codex · model: gpt-5.6-sol
# F4 "Bağlam Pasaportu" (--pasaport/--zip/--ek) yazan: claude · model: sonnet

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
from typing import Any, Sequence

import pasaport_defteri
import giris_kapisi
import retrieve


SCRIPT_DIR = Path(__file__).resolve().parent
VAULT_ROOT = SCRIPT_DIR.parent.parent
HEADER = "# Persistent memory context\nPaste this above your question."
NO_INDEX = "_Root map unavailable: knowledge/index.md was not found._"
NO_DATABASE = "_Retrieval index unavailable; only the root map is included._"
NO_MATCHES = "_No relevant memory notes were found._"

# --------------------------------------------------------------------------
# F4 C1 — Bağlam Pasaportu packager ("gidiş paketi" / outbound package).
#
# Everything below composes a self-contained ODENA-marked package for pasting
# into a consumer web chat (ChatGPT/Gemini/claude.ai): a delta-budgeted or
# full ("--zip") slice of the same root-map+notes material ``compose_context``
# builds, plus a cumulative manifest of what was already sent under this
# paket-id and Turkish footer instructions telling the web model how to
# answer and how to hand facts back (part 2 reads its ``[ODENA-DONUS]``
# reply from the clipboard; not implemented here). See docs/pasaport.md.
# --------------------------------------------------------------------------

PASAPORT_DELTA_ENV = "BEYIN_PASAPORT_DELTA_KARAKTER"
DEFAULT_PASAPORT_DELTA = 4_000
KESILDI_ISARETI = "[…kesildi]"
ILK_NOT_KIRPMA = 1_500


def compose_context(
    question: str,
    *,
    vault_root: Path = VAULT_ROOT,
    limit: int = 3,
    include_map: bool = True,
) -> str:
    """Return a capped Markdown context block for ``question``."""
    if not 1 <= limit <= 5:
        raise ValueError("note-count-out-of-range")
    root = Path(vault_root)
    sections = [HEADER, giris_kapisi.HOOK_HEADER.rstrip("\n")]

    if include_map:
        index_path = root / "knowledge" / "index.md"
        try:
            root_map = index_path.read_text(encoding="utf-8").strip()
        except (FileNotFoundError, OSError, UnicodeError):
            root_map = NO_INDEX
        sections.append("## Root map\n\n" + root_map)

    db_path = root / ".claude" / "scripts" / ".state" / retrieve.DB_NAME
    if not db_path.is_file():
        sections.append(NO_DATABASE)
        return "\n\n".join(sections) + "\n"

    try:
        result = retrieve.hook_result(question, limit=limit, db_path=db_path)
    except (OSError, sqlite3.Error):
        sections.append(NO_DATABASE)
        return "\n\n".join(sections) + "\n"

    notes = result["notes"]
    if not notes:
        sections.append("## Relevant notes\n\n" + NO_MATCHES)
    else:
        rendered = []
        for note in notes:
            rendered.append(
                f"### knowledge/concepts/{note['name']}.md\n\n{note['body']}"
            )
        sections.append("## Relevant notes\n\n" + "\n\n".join(rendered))
    return "\n\n".join(sections) + "\n"


def copy_to_clipboard(block: str) -> None:
    """Send Unicode text to Windows ``clip.exe`` as UTF-16LE bytes."""
    try:
        result = subprocess.run(
            ["clip.exe"],
            input=block.encode("utf-16le"),
            check=False,
        )
    except OSError as exc:
        raise RuntimeError("clipboard-unavailable") from exc
    if result.returncode != 0:
        raise RuntimeError(f"clipboard-exit-{result.returncode}")


def resolve_pasaport_delta(environment: dict[str, str] | None = None) -> int:
    """``BEYIN_PASAPORT_DELTA_KARAKTER``; unset, junk, or non-positive falls back."""
    env = os.environ if environment is None else environment
    raw = (env.get(PASAPORT_DELTA_ENV) or "").strip()
    if not raw:
        return DEFAULT_PASAPORT_DELTA
    try:
        value = int(raw)
    except ValueError:
        return DEFAULT_PASAPORT_DELTA
    return value if value > 0 else DEFAULT_PASAPORT_DELTA


def _yeni_paket_id(question: str, ts: str) -> str:
    """12 hex chars of ``sha256(question + ts + 8 random bytes)``.

    The random suffix means two packages built from the same question in the
    same second still get different ids — id collision is not a real risk to
    hedge against with retries.
    """
    rastgele = os.urandom(8)
    digest = hashlib.sha256((question + ts).encode("utf-8") + rastgele)
    return digest.hexdigest()[:12]


def _root_map_metni(vault_root: Path) -> str:
    index_path = Path(vault_root) / "knowledge" / "index.md"
    try:
        return index_path.read_text(encoding="utf-8").strip()
    except (FileNotFoundError, OSError, UnicodeError):
        return NO_INDEX


def _sadece_basliklar(root_map_text: str) -> str:
    """Markdown heading lines only — the fallback shrink for a root map that
    does not fit the delta budget whole."""
    basliklar = [line for line in root_map_text.splitlines() if line.lstrip().startswith("#")]
    return "\n".join(basliklar)


def _aday_notlar(question: str, vault_root: Path, limit: int) -> list[dict[str, str]]:
    """Candidate notes in retrieval order — same source ``compose_context`` uses."""
    root = Path(vault_root)
    db_path = root / ".claude" / "scripts" / ".state" / retrieve.DB_NAME
    if not db_path.is_file():
        return []
    try:
        result = retrieve.hook_result(question, limit=limit, db_path=db_path)
    except (OSError, sqlite3.Error):
        return []
    return [{"name": note["name"], "body": note["body"]} for note in result["notes"]]


def _manifest_satiri(manifest: dict[str, str]) -> str:
    if not manifest:
        return "[ODENA-MANIFEST]"
    parcalar = " ".join(f"{slug}:{manifest[slug]}" for slug in sorted(manifest))
    return f"[ODENA-MANIFEST {parcalar}]"


_KOD_CITI = "```"
_KOD_CITI_YERINE = "'''"
KOD_CITI_NOTU = "(Not: pakete alınan not(lar)daki kod çitleri ''' ile değiştirildi.)"


def _notlarda_cit_temizle(
    notes: list[dict[str, str]]
) -> tuple[list[dict[str, str]], bool]:
    """Replace ``` with ''' inside each candidate note's body before it is
    ever embedded in the package text.

    A note body carrying its own code fence could otherwise straddle the
    package's own markers once pasted into a web chat UI's markdown editor
    (exactly the "web UI wrapped the reply in a fence" case
    ``pasaport_kapi._strip_fences`` tolerates on the way back) — cheaper to
    never let one exist in an outbound package than to keep defending
    against it on the way in. The manifest hash recorded for dedup is taken
    from the ORIGINAL (unsanitised) body by the caller, so this substitution
    never desyncs the manifest from what ``retrieve.read_concept`` reads off
    disk.
    """
    changed = False
    cleaned: list[dict[str, str]] = []
    for note in notes:
        body = note["body"]
        if _KOD_CITI in body:
            body = body.replace(_KOD_CITI, _KOD_CITI_YERINE)
            changed = True
        cleaned.append({**note, "body": body})
    return cleaned, changed


def _alt_bilgi(paket_id: str, *, cit_notu: bool = False) -> str:
    """Turkish footer instructions. Plain text, square-bracket markers, no
    code fences — copy-pasting through a web chat UI can swallow fences.

    ``cit_notu`` appends :data:`KOD_CITI_NOTU` — set when
    ``_notlarda_cit_temizle`` actually substituted a fence in one of this
    package's notes, so the reader knows the ``'''`` it is seeing in the
    body above stands in for a real ``` block.
    """
    taban = (
        "Bu paket kalıcı hafızadan alınan bağlamdır. Yalnızca bu pakette YENİ "
        "olan bilgiyle cevap ver; paket içeriğini tekrarlama.\n\n"
        "Cevabının SONUNA, hatırlanmaya değer YENİ bilgi/karar varsa aşağıdaki "
        "bloğu tam olarak bir kez, kısa markdown maddeleriyle ekle:\n"
        f"[ODENA-DONUS id:{paket_id}]\n"
        "- ...\n"
        f"[/ODENA-DONUS]\n\n"
        "Bağlam yetersizse tahmin ETME; eksik olan her kalemi tek satırda "
        "listele ve orada dur:\n"
        f"[ODENA-ISTEK id:{paket_id}]\n"
        "- ...\n"
        f"[/ODENA-ISTEK]\n\n"
        "Bu işaretleri kod bloğuna koyma."
    )
    return taban + ("\n\n" + KOD_CITI_NOTU if cit_notu else "")


def _tam_govde(
    root_map_text: str, notes: list[dict[str, str]]
) -> tuple[str, list[dict[str, str]]]:
    """``--zip``: no budget — the full root map plus every candidate note."""
    parts: list[str] = []
    if root_map_text:
        parts.append("## Kök harita\n\n" + root_map_text)
    for note in notes:
        parts.append(f"### knowledge/concepts/{note['name']}.md\n\n{note['body']}")
    if not parts:
        parts.append(NO_MATCHES)
    return "\n\n".join(parts), list(notes)


def _butceli_govde(
    root_map_text: str,
    notes: list[dict[str, str]],
    header: str,
    footer: str,
    budget: int,
) -> tuple[str, list[dict[str, str]], bool]:
    """Greedy delta-budget fit: root map first, then notes whole-or-omitted.

    Never cuts a note mid-body. When nothing fits beyond the header/footer,
    falls back to the first candidate note's first ``ILK_NOT_KIRPMA``
    characters with a ``KESILDI_ISARETI`` marker, regardless of budget —
    that fallback is the one place a note IS cut, and it is the only one.
    """
    overhead = len(header) + len(footer) + 4  # two "\n\n" joins around the body
    remaining = budget - overhead
    parts: list[str] = []
    dahil: list[dict[str, str]] = []

    if remaining > 0 and root_map_text:
        tam_harita = "## Kök harita\n\n" + root_map_text
        if len(tam_harita) <= remaining:
            parts.append(tam_harita)
            remaining -= len(tam_harita)
        else:
            basliklar = _sadece_basliklar(root_map_text)
            kisa_harita = ("## Kök harita\n\n" + basliklar) if basliklar else ""
            if kisa_harita and len(kisa_harita) <= remaining:
                parts.append(kisa_harita)
                remaining -= len(kisa_harita)

    for note in notes:
        blok = f"### knowledge/concepts/{note['name']}.md\n\n{note['body']}"
        ek_uzunluk = len(blok) + (2 if parts else 0)
        if ek_uzunluk <= remaining:
            parts.append(blok)
            dahil.append(note)
            remaining -= ek_uzunluk

    kesildi = False
    if not parts and notes:
        ilk = notes[0]
        kirpilmis = ilk["body"][:ILK_NOT_KIRPMA]
        blok = f"### knowledge/concepts/{ilk['name']}.md\n\n{kirpilmis}\n\n{KESILDI_ISARETI}"
        parts.append(blok)
        dahil.append({"name": ilk["name"], "body": kirpilmis})
        kesildi = True

    if not parts:
        parts.append(NO_MATCHES)

    return "\n\n".join(parts), dahil, kesildi


def compose_pasaport(
    question: str,
    *,
    vault_root: Path = VAULT_ROOT,
    state_dir: Path | None = None,
    limit: int = 3,
    zip_mi: bool = False,
    ek: bool = False,
    paket_id: str | None = None,
    budget: int | None = None,
    now: dt.datetime | None = None,
) -> dict[str, Any]:
    """Compose one ODENA outbound package and record it in the C4 ledger.

    On success: ``{"hata": None, "paket": str, "id": str, "n": int,
    "karakter": int, "not_sayisi": int, "kesildi": bool}``. On an unknown or
    missing ``--id`` for ``ek=True``: ``{"hata": pasaport_defteri.
    PAKET_BILINMIYOR_SLUG}`` and no package is built or recorded.

    The package is recorded in the ledger (``paket_kaydet``) before the
    caller ever copies it to the clipboard — see ``main``.
    """
    root = Path(vault_root)
    resolved_state = (
        Path(state_dir) if state_dir is not None else root / ".claude" / "scripts" / ".state"
    )
    question, _input_warnings = giris_kapisi.temizle(
        question,
        component="pasaport-input",
        state_dir=resolved_state,
    )
    defter = pasaport_defteri.Defter(resolved_state)
    zaman = now or dt.datetime.now().astimezone()
    ts = zaman.isoformat(timespec="seconds")

    if ek:
        if not paket_id:
            return {"hata": pasaport_defteri.PAKET_BILINMIYOR_SLUG}
        kayit = defter.oku_kayit(paket_id)
        if kayit is None:
            return {"hata": pasaport_defteri.PAKET_BILINMIYOR_SLUG}
        resolved_id = paket_id
        gonderiler = kayit.get("paketler") or []
        n = len(gonderiler) + 1
        onceki_manifest = defter.manifest(resolved_id)
        etkin_soru = question if question else str(kayit.get("soru", ""))
    else:
        resolved_id = _yeni_paket_id(question, ts)
        n = 1
        onceki_manifest = {}
        etkin_soru = question

    header = f"[ODENA-PAKET id:{resolved_id} n:{n} ts:{ts}]"

    root_map_text = _root_map_metni(root)
    notes = _aday_notlar(etkin_soru, root, limit)
    if ek and onceki_manifest:
        notes = [note for note in notes if note["name"] not in onceki_manifest]
    # Hash the ORIGINAL body for the manifest — before fence substitution —
    # so a later package's dedup comparison against retrieve.read_concept
    # (which reads the real, unsanitised note off disk) still matches.
    orijinal_govdeler = {note["name"]: note["body"] for note in notes}
    notes, cit_degisti = _notlarda_cit_temizle(notes)

    footer = _alt_bilgi(resolved_id, cit_notu=cit_degisti)

    if zip_mi:
        body_content, dahil_notlar = _tam_govde(root_map_text, notes)
        kesildi = False
    else:
        body_content, dahil_notlar, kesildi = _butceli_govde(
            root_map_text,
            notes,
            header,
            footer,
            budget if budget is not None else resolve_pasaport_delta(),
        )

    yeni_manifest_ekle = {
        note["name"]: hashlib.sha256(
            orijinal_govdeler.get(note["name"], note["body"]).encode("utf-8")
        ).hexdigest()[:12]
        for note in dahil_notlar
    }
    birlesik_manifest = dict(onceki_manifest)
    birlesik_manifest.update(yeni_manifest_ekle)

    framed_body = giris_kapisi.HOOK_HEADER.rstrip("\n") + "\n\n" + body_content
    paket = "\n\n".join([header, framed_body, _manifest_satiri(birlesik_manifest), footer]) + "\n"
    paket, _output_warnings = giris_kapisi.temizle(
        paket,
        component="pasaport-output",
        state_dir=resolved_state,
    )

    defter.paket_kaydet(
        resolved_id,
        soru=etkin_soru,
        n=n,
        ts=ts,
        karakter=len(paket),
        notlar=[note["name"] for note in dahil_notlar],
        zip_mi=zip_mi,
        manifest_ekle=yeni_manifest_ekle,
    )

    return {
        "hata": None,
        "paket": paket,
        "id": resolved_id,
        "n": n,
        "karakter": len(paket),
        "not_sayisi": len(dahil_notlar),
        "kesildi": kesildi,
    }


def _pasaport_ozet(sonuc: dict[str, Any]) -> str:
    return f"paket {sonuc['id']} n={sonuc['n']} {sonuc['karakter']} karakter, {sonuc['not_sayisi']} not"


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("question", nargs="?", default=None)
    parser.add_argument("--clip", action="store_true")
    parser.add_argument("--no-map", action="store_true")
    parser.add_argument("-k", type=int, default=3, metavar="N")
    parser.add_argument("--vault", type=Path, default=VAULT_ROOT)
    parser.add_argument(
        "--pasaport", action="store_true", help="Build an outbound ODENA package."
    )
    parser.add_argument(
        "--soru", default=None, help="Question for --pasaport (alternative to the positional)."
    )
    parser.add_argument(
        "--zip", action="store_true", help="Pasaport mode without the delta budget."
    )
    parser.add_argument(
        "--ek", action="store_true", help="Follow-up package for an existing --id."
    )
    parser.add_argument("--id", dest="paket_id", default=None, help="Existing paket-id, with --ek.")
    parser.add_argument(
        "--yazdir", action="store_true", help="Print the package to stdout instead of the clipboard."
    )
    parser.add_argument("--state-dir", type=Path, default=None)
    args = parser.parse_args(argv)
    if not 1 <= args.k <= 5:
        parser.error("-k must be between 1 and 5")
    if not args.pasaport and args.question is None:
        parser.error("question is required")
    if args.ek and not args.paket_id:
        parser.error("--ek requires --id")
    if args.pasaport and not args.ek and not (args.soru or args.question):
        parser.error("--pasaport requires a question (positional or --soru)")
    return args


def main(argv: Sequence[str] | None = None) -> int:
    if os.environ.get("BEYIN_INVOKED_BY"):
        return 0
    args = _parse_args(argv)

    if not args.pasaport:
        block = compose_context(
            args.question,
            vault_root=args.vault,
            limit=args.k,
            include_map=not args.no_map,
        )
        if args.clip:
            copy_to_clipboard(block)
        else:
            print(block, end="")
        return 0

    soru = args.soru if args.soru is not None else args.question
    sonuc = compose_pasaport(
        soru or "",
        vault_root=args.vault,
        state_dir=args.state_dir,
        limit=args.k,
        zip_mi=args.zip,
        ek=args.ek,
        paket_id=args.paket_id,
    )
    if sonuc.get("hata"):
        print(sonuc["hata"], file=sys.stderr)
        return 1

    if args.yazdir:
        print(sonuc["paket"], end="")
    else:
        copy_to_clipboard(sonuc["paket"])
        print(_pasaport_ozet(sonuc))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
