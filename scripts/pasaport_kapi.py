#!/usr/bin/env python3
# yazan: claude · model: sonnet
"""C3 — parse an ODENA-DONUS/ODENA-ISTEK web-chat reply, gate it the way
``kaydet.py`` gates a note, and hold the surviving candidate for one-click
panel approval. Nothing here ever writes to the daily log on its own —
``uygula``/``onayla`` is the only path from a parsed reply to the vault, and
it only runs after an explicit approval. See docs/pasaport.md part 2.

Pure and fully testable on Linux: the only OS-specific piece of F4 part 2 is
the clipboard listener (``pano_izleyici.py``), which hands text to
``isle_metin`` here in-process — this module itself touches only the
filesystem (ledger, quarantine, pending-candidate file, daily log).
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import sys
from dataclasses import dataclass, field
from typing import Any, Sequence

from beyin_ortak import _atomic_write_json, write_health
import flush
import kaydet
import pasaport_defteri
import retrieve
import secret_guard


SCRIPT_DIR = Path(__file__).resolve().parent
VAULT_ROOT = SCRIPT_DIR.parent.parent
STATE_DIR = SCRIPT_DIR / ".state"

# Kapı keeps its own health file, mirroring kaydet.py's own convention — the
# top-level "component"/"error" fields in health.json belong to compile.
HEALTH_NAME = "pasaport-health.json"

RESULT_MARKER = "PASAPORT-SONUC "
BEKLEYEN_NAME = "pasaport-bekleyen.json"

MAX_DONUS_ENV = "BEYIN_PASAPORT_DONUS_MAX_KARAKTER"
DEFAULT_MAX_DONUS = 12_000

# Top-level gate on the WHOLE clipboard text, checked in isle_metin() before
# ayristir() ever runs — the DONUS-body cap above only bounds one already-
# parsed block, so an unbounded ISTEK block (or garbage with no block at
# all) could otherwise reach the regex-based parser at any size.
GIRDI_MAX_ENV = "BEYIN_PASAPORT_GIRDI_MAX_KARAKTER"
DEFAULT_MAX_GIRDI = 60_000
GIRDI_COK_UZUN_SLUG = "pasaport-girdi-cok-uzun"

ISTEK_MAX_MADDE_ENV = "BEYIN_PASAPORT_ISTEK_MAX_MADDE"
DEFAULT_ISTEK_MAX_MADDE = 20
ISTEK_MADDE_MAX_UZUNLUK = 300
ISTEK_KESILDI_ISARETI = "…"

# --------------------------------------------------------------------------
# Refusal / outcome slugs
# --------------------------------------------------------------------------

BLOK_CIFT_SLUG = "pasaport-blok-cift"
BLOK_YARIM_SLUG = "pasaport-blok-yarim"
BLOK_TERS_SLUG = "pasaport-blok-ters"
ID_UYUSMAZ_SLUG = "pasaport-id-uyusmaz"
PAKET_BILINMIYOR_SLUG = pasaport_defteri.PAKET_BILINMIYOR_SLUG
COK_UZUN_SLUG = "pasaport-donus-cok-uzun"
KARANTINA_SLUG = "pasaport-karantina"
BOS_SLUG = "pasaport-donus-bos"
YAZMA_HATA_SLUG = "pasaport-yazma-hatasi"
BEKLEYEN_YOK_SLUG = "pasaport-bekleyen-yok"
BEKLEYEN_UYUSMAZ_SLUG = "pasaport-bekleyen-uyusmaz"

MANIFEST_BAYAT_WARN = "manifest-bayat"
BEKLEYEN_DEGISTI_WARN = "pasaport-bekleyen-degisti"
KULLANICI_REDDI_NEDEN = "kullanici-reddi"
ISTEK_KIRPILDI_WARN = "pasaport-istek-kirpildi"

# ``bekleyen`` payload ``durum`` values. Absent/"bekliyor" means "not yet
# approved"; "uygulaniyor" is written atomically BEFORE the daily-log write
# so a crash between the write and deleting the pending file leaves a
# durable marker `onayla` can recognise on retry instead of writing twice.
BEKLEYEN_DURUM_UYGULANIYOR = "uygulaniyor"


# --------------------------------------------------------------------------
# C3.1 — ayristir(): find and validate the ODENA-DONUS / ODENA-ISTEK blocks.
#
# Same "exactly one BEGIN, exactly one END, END after BEGIN, else refuse
# with a slug — never guess" precedent as context_bridge._splice.
# --------------------------------------------------------------------------


class AyristirmaHata(ValueError):
    """The reply text is ODENA-marker-shaped but malformed. Carries a slug."""

    def __init__(self, slug: str):
        super().__init__(slug)
        self.slug = slug


@dataclass(frozen=True)
class Ayristirma:
    id: str | None
    donus_body: str | None
    istek_body: str | None


# A code fence line the web UI's markdown editor wrapped around the block —
# stripped wholesale before marker search; tolerated, not required.
_FENCE_LINE = re.compile(r"(?m)^[ \t]*`{3,}[^\n]*\n?")

_BEGIN_DONUS = re.compile(r"(?m)^[ \t]*\[ODENA-DONUS[ \t]+id:(?P<id>[^\]\s]+)\][ \t]*$")
_END_DONUS = re.compile(r"(?m)^[ \t]*\[/ODENA-DONUS\][ \t]*$")
_BEGIN_ISTEK = re.compile(r"(?m)^[ \t]*\[ODENA-ISTEK[ \t]+id:(?P<id>[^\]\s]+)\][ \t]*$")
_END_ISTEK = re.compile(r"(?m)^[ \t]*\[/ODENA-ISTEK\][ \t]*$")


def _strip_fences(text: str) -> str:
    return _FENCE_LINE.sub("", text)


def _extract_block(
    text: str, begin_re: re.Pattern[str], end_re: re.Pattern[str]
) -> tuple[str, str] | None:
    """``(id, body)`` for one BEGIN/END pair, ``None`` if the kind is absent.

    Raises :class:`AyristirmaHata` for every malformed shape: more than one
    BEGIN or END (``pasaport-blok-cift``), a BEGIN with no matching END or
    vice versa (``pasaport-blok-yarim``), or an END that precedes its BEGIN
    (``pasaport-blok-ters``).
    """
    begins = list(begin_re.finditer(text))
    ends = list(end_re.finditer(text))
    if len(begins) > 1 or len(ends) > 1:
        raise AyristirmaHata(BLOK_CIFT_SLUG)
    if bool(begins) != bool(ends):
        raise AyristirmaHata(BLOK_YARIM_SLUG)
    if not begins:
        return None
    begin, end = begins[0], ends[0]
    if end.start() < begin.start():
        raise AyristirmaHata(BLOK_TERS_SLUG)
    return begin.group("id"), text[begin.end() : end.start()].strip("\n")


def ayristir(text: str) -> Ayristirma:
    """Parse ODENA-DONUS / ODENA-ISTEK blocks out of one pasted reply.

    ``kaydet.normalize_text`` runs first (BOM / CR / U+2028 folding — the
    same normalisation the directive-shaped gate depends on), then code
    fences the web UI may have wrapped around a block are stripped. Neither
    block present is not an error — an ordinary reply with nothing to
    remember returns ``Ayristirma(None, None, None)``.
    """
    normalised = _strip_fences(kaydet.normalize_text(text))
    donus = _extract_block(normalised, _BEGIN_DONUS, _END_DONUS)
    istek = _extract_block(normalised, _BEGIN_ISTEK, _END_ISTEK)
    donus_id, donus_body = donus if donus is not None else (None, None)
    istek_id, istek_body = istek if istek is not None else (None, None)
    if donus_id is not None and istek_id is not None and donus_id != istek_id:
        raise AyristirmaHata(ID_UYUSMAZ_SLUG)
    combined_id = donus_id or istek_id
    return Ayristirma(id=combined_id, donus_body=donus_body, istek_body=istek_body)


# --------------------------------------------------------------------------
# C3.2 — kapilar(): the gate pipeline for a parsed DONUS/ISTEK pair.
# --------------------------------------------------------------------------


@dataclass
class Sonuc:
    hata: str | None
    id: str | None = None
    n: int = 0
    birimler: list[str] = field(default_factory=list)
    govde: str = ""
    dusen_adet: int = 0
    uyarilar: list[str] = field(default_factory=list)
    istek_maddeleri: list[str] = field(default_factory=list)
    karantina_dosyasi: str | None = None


def resolve_max_donus(environment: dict[str, str] | None = None) -> int:
    """``BEYIN_PASAPORT_DONUS_MAX_KARAKTER``; unset, junk, non-positive fall back."""
    env = os.environ if environment is None else environment
    raw = (env.get(MAX_DONUS_ENV) or "").strip()
    if not raw:
        return DEFAULT_MAX_DONUS
    try:
        value = int(raw)
    except ValueError:
        return DEFAULT_MAX_DONUS
    return value if value > 0 else DEFAULT_MAX_DONUS


def resolve_max_girdi(environment: dict[str, str] | None = None) -> int:
    """``BEYIN_PASAPORT_GIRDI_MAX_KARAKTER``; unset, junk, non-positive fall back."""
    env = os.environ if environment is None else environment
    raw = (env.get(GIRDI_MAX_ENV) or "").strip()
    if not raw:
        return DEFAULT_MAX_GIRDI
    try:
        value = int(raw)
    except ValueError:
        return DEFAULT_MAX_GIRDI
    return value if value > 0 else DEFAULT_MAX_GIRDI


def resolve_istek_max_madde(environment: dict[str, str] | None = None) -> int:
    """``BEYIN_PASAPORT_ISTEK_MAX_MADDE``; unset, junk, non-positive fall back."""
    env = os.environ if environment is None else environment
    raw = (env.get(ISTEK_MAX_MADDE_ENV) or "").strip()
    if not raw:
        return DEFAULT_ISTEK_MAX_MADDE
    try:
        value = int(raw)
    except ValueError:
        return DEFAULT_ISTEK_MAX_MADDE
    return value if value > 0 else DEFAULT_ISTEK_MAX_MADDE


_BULLET_LINE = re.compile(r"^[ \t]*[-*•][ \t]+")
_BULLET_STRIP = re.compile(r"^[ \t]*[-*•][ \t]*")
_PUNCT = re.compile(r"[^\w\s]", re.UNICODE)
_WS = re.compile(r"\s+")


def _split_units(text: str) -> list[str]:
    """Split a body into bullet-line and blank-line-separated paragraph units."""
    units: list[str] = []
    current: list[str] = []

    def _flush() -> None:
        if current:
            joined = "\n".join(current).strip()
            if joined:
                units.append(joined)
            current.clear()

    for line in text.splitlines():
        if _BULLET_LINE.match(line):
            _flush()
            current.append(line)
        elif not line.strip():
            _flush()
        else:
            current.append(line)
    _flush()
    return units


def _fingerprint(unit: str) -> str:
    """Normalised fingerprint: bullet marker stripped, lowercased, collapsed
    whitespace, punctuation removed."""
    text = _BULLET_STRIP.sub("", unit)
    text = _PUNCT.sub("", text)
    text = _WS.sub(" ", text).strip().lower()
    return text


def _jaccard(a: set[str], b: set[str]) -> float:
    if not a or not b:
        return 0.0
    union = len(a | b)
    return len(a & b) / union if union else 0.0


def _manifest_dedup(
    units: Sequence[str],
    paket_id: str,
    defter: pasaport_defteri.Defter,
    vault_root: Path,
) -> tuple[list[str], int, list[str]]:
    """Drop units that duplicate (exact fingerprint, or >= 0.9 token-Jaccard
    similar) a line/paragraph of a note already sent under this paket-id.

    A manifest note whose file is missing, or whose current content hashes
    differently from the manifest's recorded hash, is skipped entirely for
    dedup (its lines never match anything) and reported via
    ``manifest-bayat`` — a stale comparison target must never cause a false
    drop.
    """
    manifest = defter.manifest(paket_id)
    warnings: list[str] = []
    note_fingerprints: set[str] = set()
    note_token_sets: list[set[str]] = []

    for slug, stored_hash in manifest.items():
        note_path = Path(vault_root) / "knowledge" / "concepts" / f"{slug}.md"
        try:
            concept = retrieve.read_concept(note_path)
        except (OSError, retrieve.RetrieveError):
            warnings.append(f"{MANIFEST_BAYAT_WARN}:{slug}")
            continue
        current_hash = hashlib.sha256(concept.body.encode("utf-8")).hexdigest()[:12]
        if current_hash != stored_hash:
            warnings.append(f"{MANIFEST_BAYAT_WARN}:{slug}")
            continue
        for note_unit in _split_units(concept.body):
            fingerprint = _fingerprint(note_unit)
            if not fingerprint:
                continue
            note_fingerprints.add(fingerprint)
            note_token_sets.append(set(fingerprint.split()))

    surviving: list[str] = []
    dropped = 0
    for unit in units:
        fingerprint = _fingerprint(unit)
        tokens = set(fingerprint.split())
        is_duplicate = bool(fingerprint) and fingerprint in note_fingerprints
        if not is_duplicate and tokens:
            for note_tokens in note_token_sets:
                if _jaccard(tokens, note_tokens) >= 0.9:
                    is_duplicate = True
                    break
        if is_duplicate:
            dropped += 1
        else:
            surviving.append(unit)
    return surviving, dropped, warnings


def _istek_maddeler(body: str) -> tuple[list[str], bool]:
    """One item per line, bullets stripped, secrets redacted defensively,
    capped to :func:`resolve_istek_max_madde` items of at most
    :data:`ISTEK_MADDE_MAX_UZUNLUK` characters each — an ODENA-ISTEK block is
    otherwise unbounded web-model output landing straight in the ledger,
    unlike ODENA-DONUS which is capped and quarantine-gated. Returns
    ``(items, kirpildi)`` — ``kirpildi`` is ``True`` when either the item
    count or an individual item's length was cut down.
    """
    max_madde = resolve_istek_max_madde()
    candidates: list[str] = []
    for line in body.splitlines():
        stripped = _BULLET_STRIP.sub("", line).strip()
        if stripped:
            candidates.append(stripped)

    kirpildi = len(candidates) > max_madde
    items: list[str] = []
    for raw in candidates[:max_madde]:
        redacted, _hits = secret_guard.redact(raw)
        if len(redacted) > ISTEK_MADDE_MAX_UZUNLUK:
            redacted = redacted[: ISTEK_MADDE_MAX_UZUNLUK - 1].rstrip() + ISTEK_KESILDI_ISARETI
            kirpildi = True
        items.append(redacted)
    return items, kirpildi


_DOGRULANMAMIS_LINE = re.compile(
    r"(?m)^[ \t]*(?:[-*•][ \t]*)?>[ \t]*dogrulanmamis:.*(?:\r?\n|\Z)"
)


def _neutralize_unit(unit: str) -> str:
    """Defang forged provenance inside a surviving DONUS unit before it is
    ever wrapped into the daily-log payload.

    Escapes ``<!--``/``-->`` so no attacker-supplied text can shape a
    ``<!-- session:... ts:... source:... -->`` anchor that
    ``compile.carry_source_anchors`` would later read out of the daily log
    and carry, unverified, into a concept note (impersonating a real
    session source). Also drops any line pretending to be our own
    ``> dogrulanmamis:`` provenance header — only the ONE line ``kapilar``
    itself prepends may exist in the written block.
    """
    text = unit.replace("<!--", "&lt;!--").replace("-->", "--&gt;")
    text = _DOGRULANMAMIS_LINE.sub("", text)
    return text.strip("\n")


def _next_n(kayit: dict[str, Any]) -> int:
    donusler = kayit.get("donusler")
    return (len(donusler) if isinstance(donusler, list) else 0) + 1


def _karantina_yaz(state_dir: Path, content: str, now: dt.datetime) -> Path:
    """Preserve suspicious ODENA-DONUS content verbatim — same posture as
    ``kaydet.py``'s quarantine, kept under ``<state_dir>/karantina``."""
    karantina_dir = Path(state_dir) / "karantina"
    karantina_dir.mkdir(parents=True, exist_ok=True, mode=0o700)
    try:
        karantina_dir.chmod(0o700)
    except OSError:
        pass
    stamp = now.strftime("%Y%m%dT%H%M%S")
    destination = karantina_dir / f"pasaport-{stamp}.md"
    n = 1
    while destination.exists():
        n += 1
        destination = karantina_dir / f"pasaport-{stamp}-{n}.md"
    destination.write_text(content, encoding="utf-8", newline="\n")
    try:
        destination.chmod(0o600)
    except OSError:
        pass
    return destination


def kapilar(
    ayristirma: Ayristirma, state_dir: Path, vault_root: Path, now: dt.datetime
) -> Sonuc:
    """The gate pipeline. Never raises: every failure is a slug on ``Sonuc``.

    Order: (1) the id must be a known paket-id; (2) size cap; (3) secret
    redaction; (4) directive-shaped quarantine; (5) manifest-dedup; (6)
    neutralise forged provenance (HTML-comment anchors, a fake
    ``dogrulanmamis`` line) in every survivor; (7) wrap what survives with
    the real ``dogrulanmamis`` provenance line. ISTEK items are recorded
    independently of whatever happens to the DONUS block, as long as the id
    is known — capped and length-truncated, they never touch the daily log.

    The WHOLE clipboard text is additionally capped before it ever reaches
    this function — see ``isle_metin``'s own top-level size gate.
    """
    state_dir = Path(state_dir)
    vault_root = Path(vault_root)
    defter = pasaport_defteri.Defter(state_dir)
    ts = now.isoformat(timespec="seconds")
    uyarilar: list[str] = []

    paket_id = ayristirma.id
    if paket_id is None:
        return Sonuc(hata=None)

    kayit = defter.oku_kayit(paket_id)
    if kayit is None:
        return Sonuc(hata=PAKET_BILINMIYOR_SLUG, id=paket_id)

    istek_maddeleri: list[str] = []
    if ayristirma.istek_body:
        istek_maddeleri, istek_kirpildi = _istek_maddeler(ayristirma.istek_body)
        if istek_maddeleri:
            defter.istek_kaydet(paket_id, istek_maddeleri, ts=ts)
        if istek_kirpildi:
            uyarilar.append(ISTEK_KIRPILDI_WARN)

    if not ayristirma.donus_body:
        return Sonuc(hata=None, id=paket_id, uyarilar=uyarilar, istek_maddeleri=istek_maddeleri)

    donus_body = ayristirma.donus_body
    max_karakter = resolve_max_donus()
    if len(donus_body) > max_karakter:
        defter.donus_kaydet(
            paket_id, ts=ts, karakter=len(donus_body), durum="red", neden=COK_UZUN_SLUG
        )
        return Sonuc(
            hata=COK_UZUN_SLUG, id=paket_id, uyarilar=uyarilar, istek_maddeleri=istek_maddeleri
        )

    redacted_body, hits = secret_guard.redact(donus_body)
    if hits:
        uyarilar.append("secret-redacted-pasaport:" + ",".join(hits))

    directive_match = flush.DIRECTIVE_SHAPED.search(redacted_body)
    if directive_match is not None:
        karantina_dosyasi = _karantina_yaz(state_dir, redacted_body, now)
        defter.donus_kaydet(
            paket_id,
            ts=ts,
            karakter=len(redacted_body),
            durum="karantina",
            neden=KARANTINA_SLUG,
        )
        return Sonuc(
            hata=KARANTINA_SLUG,
            id=paket_id,
            uyarilar=uyarilar,
            istek_maddeleri=istek_maddeleri,
            karantina_dosyasi=karantina_dosyasi.as_posix(),
        )

    units = _split_units(redacted_body)
    surviving, dropped, manifest_warnings = _manifest_dedup(
        units, paket_id, defter, vault_root
    )
    uyarilar.extend(manifest_warnings)

    # Neutralise forged provenance (HTML-comment anchors, a fake
    # "> dogrulanmamis:" header) in every survivor before it can reach the
    # daily log. A unit that neutralises down to nothing (it WAS only a
    # forged header line) is dropped here, same as a manifest-dedup drop.
    neutralized: list[str] = []
    for unit in surviving:
        cleaned = _neutralize_unit(unit)
        if cleaned.strip():
            neutralized.append(cleaned)
        else:
            dropped += 1
    surviving = neutralized

    if not surviving:
        defter.donus_kaydet(
            paket_id, ts=ts, karakter=len(redacted_body), durum="red", neden=BOS_SLUG
        )
        return Sonuc(
            hata=BOS_SLUG,
            id=paket_id,
            dusen_adet=dropped,
            uyarilar=uyarilar,
            istek_maddeleri=istek_maddeleri,
        )

    header_line = f"> dogrulanmamis: web dönüşü, kaynak: {paket_id}"
    govde = header_line + "\n\n" + "\n".join(surviving)
    return Sonuc(
        hata=None,
        id=paket_id,
        n=_next_n(kayit),
        birimler=surviving,
        govde=govde,
        dusen_adet=dropped,
        uyarilar=uyarilar,
        istek_maddeleri=istek_maddeleri,
    )


# --------------------------------------------------------------------------
# C3.3 — the pending-candidate file: exactly one at a time, panel-approved.
# --------------------------------------------------------------------------


def bekleyen_yaz(state_dir: Path, payload: dict[str, Any]) -> bool:
    """Replace the single pending candidate. Returns ``True`` when a
    still-pending (unapproved, unrejected) candidate was overwritten —
    the caller reports that as ``pasaport-bekleyen-degisti``."""
    path = Path(state_dir) / BEKLEYEN_NAME
    existed = path.exists()
    _atomic_write_json(path, payload)
    return existed


def bekleyen_oku(state_dir: Path) -> dict[str, Any] | None:
    path = Path(state_dir) / BEKLEYEN_NAME
    if not path.exists():
        return None
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, json.JSONDecodeError):
        return None
    return raw if isinstance(raw, dict) else None


def _bekleyen_sil(state_dir: Path) -> None:
    try:
        (Path(state_dir) / BEKLEYEN_NAME).unlink()
    except FileNotFoundError:
        pass


# --------------------------------------------------------------------------
# C3.4 — isle_metin(): the full pipeline from raw clipboard text to a
# pending candidate (or a recorded refusal). Called in-process by
# pano_izleyici.py, and by this module's own ``isle`` CLI subcommand.
# --------------------------------------------------------------------------


def isle_metin(
    text: str, state_dir: Path, vault_root: Path, *, now: dt.datetime | None = None
) -> dict[str, Any]:
    """Parse, gate, and (on success) queue one pasted reply for approval.

    ``raw_hash`` is computed from the ORIGINAL text handed in — before any
    normalisation — so the panel's approval step can refuse to act on a
    pending candidate that has since been replaced by a newer paste.

    A top-level size gate on the WHOLE clipboard text runs first, before
    ``ayristir`` ever sees it — nothing downstream (the per-block markers,
    the DONUS-body cap) bounds the input BEFORE it is parsed, and an
    ODENA-ISTEK block in particular has no cap of its own until
    ``kapilar`` gets to it. Nothing is persisted on this refusal.
    """
    now = now or dt.datetime.now().astimezone()
    state_dir = Path(state_dir)
    raw_hash = hashlib.sha256(text.encode("utf-8", errors="replace")).hexdigest()

    max_girdi = resolve_max_girdi()
    if len(text) > max_girdi:
        write_health(
            state_dir, GIRDI_COK_UZUN_SLUG, component="pasaport", health_name=HEALTH_NAME
        )
        return {"hata": GIRDI_COK_UZUN_SLUG, "id": None, "bekleyen": False}

    try:
        ayristirma = ayristir(text)
    except AyristirmaHata as exc:
        write_health(state_dir, exc.slug, component="pasaport", health_name=HEALTH_NAME)
        return {"hata": exc.slug, "id": None, "bekleyen": False}

    sonuc = kapilar(ayristirma, state_dir, vault_root, now)

    if sonuc.hata is not None:
        write_health(state_dir, sonuc.hata, component="pasaport", health_name=HEALTH_NAME)
        return {
            "hata": sonuc.hata,
            "id": sonuc.id,
            "bekleyen": False,
            "dusen_adet": sonuc.dusen_adet,
            "uyarilar": sonuc.uyarilar,
            "istek_maddeleri": sonuc.istek_maddeleri,
            "karantina_dosyasi": sonuc.karantina_dosyasi,
        }

    if not sonuc.govde:
        write_health(state_dir, "", component="pasaport", health_name=HEALTH_NAME)
        return {
            "hata": None,
            "id": sonuc.id,
            "bekleyen": False,
            "istek_maddeleri": sonuc.istek_maddeleri,
        }

    payload = {
        "id": sonuc.id,
        "n": sonuc.n,
        "birimler": sonuc.birimler,
        "govde": sonuc.govde,
        "dusen_adet": sonuc.dusen_adet,
        "uyarilar": sonuc.uyarilar,
        "raw_hash": raw_hash,
        "ts": now.isoformat(timespec="seconds"),
    }
    replaced = bekleyen_yaz(state_dir, payload)
    if replaced:
        write_health(
            state_dir, BEKLEYEN_DEGISTI_WARN, warning=True, component="pasaport", health_name=HEALTH_NAME
        )
    else:
        write_health(state_dir, "", component="pasaport", health_name=HEALTH_NAME)

    return {
        "hata": None,
        "id": sonuc.id,
        "bekleyen": True,
        "raw_hash": raw_hash,
        "dusen_adet": sonuc.dusen_adet,
        "uyarilar": sonuc.uyarilar,
        "istek_maddeleri": sonuc.istek_maddeleri,
    }


# --------------------------------------------------------------------------
# C3.5 — uygula() / onayla() / reddet(): the write step, gated on approval.
# --------------------------------------------------------------------------


def uygula(
    state_dir: Path,
    vault_root: Path,
    pending: dict[str, Any],
    now: dt.datetime,
    *,
    popen_factory: Any = None,
    raw_hash: str | None = None,
) -> dict[str, Any]:
    """Write the approved candidate to the daily log, record ``kabul`` in
    the ledger, then spawn ``compile.py --nezaket-del`` exactly the way
    ``kaydet.py`` does (the same helper, reused directly).

    ``raw_hash``, when given, is stored on the ``kabul`` ledger entry — the
    only way ``onayla`` can recognise, on a crash-retry, that THIS exact
    candidate was already written rather than writing it a second time.
    """
    state_dir = Path(state_dir)
    vault_root = Path(vault_root)
    paket_id = str(pending.get("id"))
    n = int(pending.get("n") or 1)
    body = str(pending.get("govde") or "")
    ts = now.isoformat(timespec="seconds")
    anchor = retrieve.format_session_anchor(f"pasaport-{paket_id}-{n}", ts, source="pasaport")

    try:
        flush._append_daily(vault_root, body, "pasaport", now, suffix=" · pasaport", anchor=anchor)
    except OSError:
        write_health(state_dir, YAZMA_HATA_SLUG, component="pasaport", health_name=HEALTH_NAME)
        return {"hata": YAZMA_HATA_SLUG, "uygulandi": False}

    defter = pasaport_defteri.Defter(state_dir)
    defter.donus_kaydet(
        paket_id, ts=ts, karakter=len(body), durum="kabul", daily_capa=anchor, raw_hash=raw_hash
    )
    write_health(state_dir, "", component="pasaport", health_name=HEALTH_NAME)

    timeout = kaydet.resolve_derleme_zaman_asimi()
    kosuldu, cikis, compile_error = kaydet._spawn_compile(
        vault_root, timeout, popen_factory=popen_factory
    )
    if compile_error is not None:
        write_health(
            state_dir, compile_error, warning=True, component="pasaport", health_name=HEALTH_NAME
        )
    elif cikis not in (0, None):
        write_health(
            state_dir,
            f"warn:pasaport-derleme-basarisiz:{cikis}",
            warning=True,
            component="pasaport",
            health_name=HEALTH_NAME,
        )

    return {
        "hata": None,
        "uygulandi": True,
        "id": paket_id,
        "capa": anchor,
        "dosya": (vault_root / "daily" / f"{now.strftime('%Y-%m-%d')}.md").as_posix(),
        "derleme": {"kosuldu": kosuldu, "cikis": cikis},
    }


def onayla(
    state_dir: Path,
    vault_root: Path,
    raw_hash: str,
    *,
    now: dt.datetime | None = None,
    popen_factory: Any = None,
) -> dict[str, Any]:
    """Approve the pending candidate — only if ``raw_hash`` still matches
    it (a newer paste replaces the pending file, invalidating a stale
    approval click).

    Idempotent against a crash between the daily-log write and deleting the
    pending file: before writing, the pending candidate is marked
    ``uygulaniyor`` (atomically, same as every other pending-file write). A
    retry that finds that marker already set checks the ledger for a
    ``kabul`` entry carrying this exact ``raw_hash`` — if one is there, the
    previous attempt already wrote the daily log and this call only cleans
    up the pending file; if not, the crash happened before the write and
    this call proceeds exactly like a first attempt.
    """
    now = now or dt.datetime.now().astimezone()
    state_dir = Path(state_dir)
    pending = bekleyen_oku(state_dir)
    if pending is None:
        return {"hata": BEKLEYEN_YOK_SLUG, "uygulandi": False}
    if pending.get("raw_hash") != raw_hash:
        return {"hata": BEKLEYEN_UYUSMAZ_SLUG, "uygulandi": False}

    paket_id = str(pending.get("id"))
    if pending.get("durum") == BEKLEYEN_DURUM_UYGULANIYOR:
        defter = pasaport_defteri.Defter(state_dir)
        kayit = defter.oku_kayit(paket_id) or {}
        for donus in kayit.get("donusler") or []:
            if (
                isinstance(donus, dict)
                and donus.get("durum") == "kabul"
                and donus.get("raw_hash") == raw_hash
            ):
                _bekleyen_sil(state_dir)
                return {
                    "hata": None,
                    "uygulandi": True,
                    "zaten": True,
                    "id": paket_id,
                    "mesaj": "zaten uygulandi",
                }
        # No matching "kabul" entry — the earlier attempt crashed BEFORE
        # the write landed. Fall through and retry it below.

    marked = dict(pending)
    marked["durum"] = BEKLEYEN_DURUM_UYGULANIYOR
    bekleyen_yaz(state_dir, marked)

    result = uygula(state_dir, vault_root, pending, now, popen_factory=popen_factory, raw_hash=raw_hash)
    _bekleyen_sil(state_dir)
    return result


def reddet(state_dir: Path, raw_hash: str, *, now: dt.datetime | None = None) -> dict[str, Any]:
    """Reject the pending candidate — same hash check as ``onayla``."""
    now = now or dt.datetime.now().astimezone()
    state_dir = Path(state_dir)
    pending = bekleyen_oku(state_dir)
    if pending is None:
        return {"hata": BEKLEYEN_YOK_SLUG, "reddedildi": False}
    if pending.get("raw_hash") != raw_hash:
        return {"hata": BEKLEYEN_UYUSMAZ_SLUG, "reddedildi": False}
    paket_id = str(pending.get("id"))
    defter = pasaport_defteri.Defter(state_dir)
    defter.donus_kaydet(
        paket_id,
        ts=now.isoformat(timespec="seconds"),
        karakter=len(str(pending.get("govde") or "")),
        durum="red",
        neden=KULLANICI_REDDI_NEDEN,
    )
    _bekleyen_sil(state_dir)
    return {"hata": None, "reddedildi": True, "id": paket_id}


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def _cmd_isle(args: argparse.Namespace, state_dir: Path) -> int:
    if args.dosya is not None:
        try:
            text = args.dosya.read_text(encoding="utf-8", errors="strict")
        except (OSError, UnicodeDecodeError):
            print(RESULT_MARKER + json.dumps({"hata": "pasaport-dosya-hata"}, ensure_ascii=False))
            return 1
    else:
        try:
            raw = sys.stdin.buffer.read()
            text = raw.decode("utf-8", errors="strict")
        except (OSError, UnicodeDecodeError):
            print(RESULT_MARKER + json.dumps({"hata": "pasaport-stdin-hata"}, ensure_ascii=False))
            return 1
    result = isle_metin(text, state_dir, args.vault_root)
    print(RESULT_MARKER + json.dumps(result, ensure_ascii=False))
    return 0 if result.get("hata") is None else 1


def _cmd_bekleyen(args: argparse.Namespace, state_dir: Path) -> int:
    pending = bekleyen_oku(state_dir)
    if args.json:
        print(json.dumps(pending, ensure_ascii=False))
    elif pending is None:
        print("Bekleyen paket yok.")
    else:
        print(f"paket {pending.get('id')} n={pending.get('n')} raw_hash={pending.get('raw_hash')}")
    return 0


def _cmd_onayla(args: argparse.Namespace, state_dir: Path) -> int:
    result = onayla(state_dir, args.vault_root, args.raw_hash)
    # Marker-prefixed, same convention as kaydet.py's RESULT_MARKER: the
    # spawned compile subprocess inherits this process's stdout, so the
    # panel must be able to pick this exact line out of whatever compile
    # itself printed while `onayla` was waiting on it.
    print(RESULT_MARKER + json.dumps(result, ensure_ascii=False))
    return 0 if result.get("hata") is None else 1


def _cmd_reddet(args: argparse.Namespace, state_dir: Path) -> int:
    # No RESULT_MARKER here, unlike ``onayla``: reject never spawns compile,
    # so the panel calls it synchronously and parses stdout as bare JSON
    # (Invoke-PanelPythonJson) — a marker prefix would break that parse.
    result = reddet(state_dir, args.raw_hash)
    print(json.dumps(result, ensure_ascii=False))
    return 0 if result.get("hata") is None else 1


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state-dir", type=Path, default=STATE_DIR)
    parser.add_argument("--vault-root", type=Path, default=VAULT_ROOT)
    sub = parser.add_subparsers(dest="command", required=True)

    isle_parser = sub.add_parser("isle", help="Parse + gate a pasted reply.")
    source = isle_parser.add_mutually_exclusive_group()
    source.add_argument("--stdin", action="store_true", help="Read the reply from stdin (default).")
    source.add_argument("--dosya", type=Path, default=None, help="Read the reply from a UTF-8 file.")
    isle_parser.set_defaults(handler=_cmd_isle)

    bekleyen_parser = sub.add_parser("bekleyen", help="Show the pending candidate, if any.")
    bekleyen_parser.add_argument("--json", action="store_true")
    bekleyen_parser.set_defaults(handler=_cmd_bekleyen)

    onayla_parser = sub.add_parser("onayla", help="Approve the pending candidate.")
    onayla_parser.add_argument("raw_hash")
    onayla_parser.set_defaults(handler=_cmd_onayla)

    reddet_parser = sub.add_parser("reddet", help="Reject the pending candidate.")
    reddet_parser.add_argument("raw_hash")
    reddet_parser.set_defaults(handler=_cmd_reddet)

    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    if os.environ.get("BEYIN_INVOKED_BY"):
        return 0
    try:
        args = _parse_args(argv)
    except SystemExit as exc:
        return exc.code if isinstance(exc.code, int) else 2
    try:
        args.state_dir.mkdir(parents=True, exist_ok=True)
    except OSError:
        pass
    return args.handler(args, args.state_dir)


if __name__ == "__main__":
    raise SystemExit(main())
