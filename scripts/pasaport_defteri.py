#!/usr/bin/env python3
# yazan: claude · model: sonnet
"""C4 — the ISTEK ledger for F4 "Bağlam Pasaportu" (context passport).

Records every outbound ODENA package (C1, ``context_pack.py --pasaport``)
under its paket-id: which notes were sent (slug + content-hash, never the
body), what came back (size/status/reason, never the return text), and what
the web model said it was missing (ISTEK line items). The aggregated ISTEK
list across every paket-id is the "kör nokta haritası" (blind-spot map) —
what the brain could not supply, most-requested first.

Secret hygiene by construction: this module is never handed a note body or
an ODENA-DONUS body, so there is no code path by which either could reach
``pasaport-defteri.json``. See ``docs/pasaport.md``.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import sys
from typing import Any, Sequence

from beyin_ortak import _atomic_write_json, _lock_exclusive


SCRIPT_DIR = Path(__file__).resolve().parent
STATE_DIR = SCRIPT_DIR / ".state"
DEFTER_NAME = "pasaport-defteri.json"

TAVAN_ENV = "BEYIN_PASAPORT_DEFTER_TAVAN"
DEFAULT_TAVAN = 200

# The only three outcomes part 2 (the clipboard listener) can record a
# returned ODENA-DONUS block under; anything else folds to "red".
DURUM_DEGERLERI = ("kabul", "karantina", "red")

PAKET_BILINMIYOR_SLUG = "pasaport-paket-bilinmiyor"


def resolve_tavan(environment: dict[str, str] | None = None) -> int:
    """``BEYIN_PASAPORT_DEFTER_TAVAN``; unset, junk, or non-positive falls back."""
    env = os.environ if environment is None else environment
    raw = (env.get(TAVAN_ENV) or "").strip()
    if not raw:
        return DEFAULT_TAVAN
    try:
        value = int(raw)
    except ValueError:
        return DEFAULT_TAVAN
    return value if value > 0 else DEFAULT_TAVAN


def _simdi_iso() -> str:
    return dt.datetime.now().astimezone().isoformat(timespec="seconds")


class Defter:
    """``pasaport-defteri.json``: one record per paket-id, lock-guarded writes.

    Every read goes through the same lock as writes — the file is small and
    read on every package generation (for the cumulative manifest), so a
    torn read during a concurrent write is exactly the failure mode worth
    paying one ``flock`` for.
    """

    def __init__(self, state_dir: Path):
        self.state_dir = Path(state_dir)
        self.path = self.state_dir / DEFTER_NAME
        self.lock_path = self.state_dir / f".{DEFTER_NAME}.lock"

    def _kilitli(self, blocking: bool = True):
        self.state_dir.mkdir(parents=True, exist_ok=True)
        lock_file = self.lock_path.open("a+", encoding="utf-8")
        _lock_exclusive(lock_file, blocking=blocking)
        return lock_file

    def _oku_ic(self) -> dict[str, Any]:
        """Read without locking — for a caller that already holds the lock."""
        if not self.path.exists():
            return {"paketler": {}}
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError, json.JSONDecodeError):
            return {"paketler": {}}
        if not isinstance(raw, dict):
            return {"paketler": {}}
        paketler = raw.get("paketler")
        if not isinstance(paketler, dict):
            paketler = {}
        return {"paketler": paketler}

    def oku(self) -> dict[str, Any]:
        lock_file = self._kilitli()
        try:
            return self._oku_ic()
        finally:
            lock_file.close()

    def oku_kayit(self, paket_id: str) -> dict[str, Any] | None:
        """Full ledger record for ``paket_id``, or ``None`` if unknown."""
        payload = self.oku()
        kayit = payload["paketler"].get(paket_id)
        return kayit if isinstance(kayit, dict) else None

    def manifest(self, paket_id: str) -> dict[str, str]:
        """Every ``slug -> content-hash`` sent so far under ``paket_id``.

        ``{}`` both for an unknown id and for a known id whose first package
        matched no notes — the caller only ever uses this to know what NOT
        to resend, and both cases mean "nothing to exclude yet".
        """
        kayit = self.oku_kayit(paket_id)
        if kayit is None:
            return {}
        manifest = kayit.get("manifest")
        if not isinstance(manifest, dict):
            return {}
        return {str(key): str(value) for key, value in manifest.items()}

    @staticmethod
    def _budandirilmis(paketler: dict[str, Any], tavan: int) -> dict[str, Any]:
        """Keep only the ``tavan`` most-recently-created paket-id entries."""
        if len(paketler) <= tavan:
            return paketler

        def anahtar(item: tuple[str, Any]) -> str:
            kayit = item[1]
            return str(kayit.get("olusturuldu", "")) if isinstance(kayit, dict) else ""

        siralanmis = sorted(paketler.items(), key=anahtar)
        fazla = len(siralanmis) - tavan
        return dict(siralanmis[fazla:])

    def paket_kaydet(
        self,
        paket_id: str,
        *,
        soru: str,
        n: int,
        ts: str,
        karakter: int,
        notlar: Sequence[str],
        zip_mi: bool,
        manifest_ekle: dict[str, str] | None = None,
        tavan: int | None = None,
    ) -> dict[str, Any]:
        """Record one generated package. Creates the paket-id entry at n=1.

        Called by ``context_pack.compose_pasaport`` BEFORE the package is
        copied to the clipboard — a package that is never recorded here
        cannot appear in a later package's cumulative manifest.
        """
        lock_file = self._kilitli()
        try:
            payload = self._oku_ic()
            paketler = payload["paketler"]
            kayit = paketler.get(paket_id)
            if not isinstance(kayit, dict):
                kayit = {
                    "olusturuldu": ts,
                    "soru": soru,
                    "paketler": [],
                    "manifest": {},
                    "donusler": [],
                    "istekler": [],
                }
            gonderiler = kayit.get("paketler")
            if not isinstance(gonderiler, list):
                gonderiler = []
            gonderiler.append(
                {
                    "n": n,
                    "ts": ts,
                    "karakter": karakter,
                    "notlar": list(notlar),
                    "zip": bool(zip_mi),
                }
            )
            kayit["paketler"] = gonderiler
            manifest = kayit.get("manifest")
            if not isinstance(manifest, dict):
                manifest = {}
            if manifest_ekle:
                manifest.update(manifest_ekle)
            kayit["manifest"] = manifest
            paketler[paket_id] = kayit
            payload["paketler"] = self._budandirilmis(
                paketler, resolve_tavan() if tavan is None else tavan
            )
            _atomic_write_json(self.path, payload)
            return dict(payload["paketler"].get(paket_id, kayit))
        finally:
            lock_file.close()

    def donus_kaydet(
        self,
        paket_id: str,
        *,
        ts: str,
        karakter: int,
        durum: str,
        neden: str = "",
        daily_capa: str | None = None,
        raw_hash: str | None = None,
    ) -> dict[str, Any] | None:
        """Record one ODENA-DONUS outcome. ``None`` when ``paket_id`` is unknown.

        Only size, status, reason and (once written) the daily-log anchor are
        stored — never the return text itself. ``raw_hash`` (the sha256 of
        the ORIGINAL clipboard text) is stored alongside a ``"kabul"``
        outcome only so ``pasaport_kapi.onayla`` can recognise, on a
        crash-retry, that this exact candidate was already written.
        """
        lock_file = self._kilitli()
        try:
            payload = self._oku_ic()
            paketler = payload["paketler"]
            kayit = paketler.get(paket_id)
            if not isinstance(kayit, dict):
                return None
            donus: dict[str, Any] = {
                "ts": ts,
                "karakter": int(karakter),
                "durum": durum if durum in DURUM_DEGERLERI else "red",
                "neden": neden,
            }
            if daily_capa:
                donus["daily_capa"] = daily_capa
            if raw_hash:
                donus["raw_hash"] = raw_hash
            donusler = kayit.get("donusler")
            if not isinstance(donusler, list):
                donusler = []
            donusler.append(donus)
            kayit["donusler"] = donusler
            paketler[paket_id] = kayit
            _atomic_write_json(self.path, payload)
            return dict(kayit)
        finally:
            lock_file.close()

    def istek_kaydet(
        self,
        paket_id: str,
        maddeler: Sequence[str],
        *,
        ts: str | None = None,
    ) -> dict[str, Any] | None:
        """Record one ODENA-ISTEK block's line items. ``None`` if unknown id."""
        lock_file = self._kilitli()
        try:
            payload = self._oku_ic()
            paketler = payload["paketler"]
            kayit = paketler.get(paket_id)
            if not isinstance(kayit, dict):
                return None
            istekler = kayit.get("istekler")
            if not isinstance(istekler, list):
                istekler = []
            istekler.append({"ts": ts or _simdi_iso(), "maddeler": list(maddeler)})
            kayit["istekler"] = istekler
            paketler[paket_id] = kayit
            _atomic_write_json(self.path, payload)
            return dict(kayit)
        finally:
            lock_file.close()

    def son_paketler(self, limit: int = 20) -> list[dict[str, Any]]:
        """Summaries of the most recently active paket-ids, newest first."""
        payload = self.oku()
        paketler = payload["paketler"]

        def anahtar(item: tuple[str, Any]) -> str:
            kayit = item[1]
            if not isinstance(kayit, dict):
                return ""
            gonderiler = kayit.get("paketler")
            if isinstance(gonderiler, list) and gonderiler:
                son = gonderiler[-1]
                if isinstance(son, dict) and son.get("ts"):
                    return str(son["ts"])
            return str(kayit.get("olusturuldu", ""))

        siralanmis = sorted(paketler.items(), key=anahtar, reverse=True)
        ozet: list[dict[str, Any]] = []
        for paket_id, kayit in siralanmis[: max(0, limit)]:
            if not isinstance(kayit, dict):
                continue
            gonderiler = kayit.get("paketler")
            if not isinstance(gonderiler, list):
                gonderiler = []
            ozet.append(
                {
                    "id": paket_id,
                    "soru": kayit.get("soru", ""),
                    "olusturuldu": kayit.get("olusturuldu", ""),
                    "gonderi_sayisi": len(gonderiler),
                    "son_n": gonderiler[-1].get("n", 0) if gonderiler else 0,
                    "donus_sayisi": len(kayit.get("donusler") or []),
                    "istek_sayisi": len(kayit.get("istekler") or []),
                }
            )
        return ozet


def istek_agregasyonu(state_dir: Path) -> list[dict[str, Any]]:
    """Aggregated ISTEK line items across every paket-id — the blind-spot map.

    Most frequent first; ties break alphabetically for a deterministic order.
    """
    payload = Defter(state_dir).oku()
    sayac: dict[str, int] = {}
    for kayit in payload["paketler"].values():
        if not isinstance(kayit, dict):
            continue
        for istek in kayit.get("istekler") or []:
            if not isinstance(istek, dict):
                continue
            for madde in istek.get("maddeler") or []:
                if isinstance(madde, str) and madde:
                    sayac[madde] = sayac.get(madde, 0) + 1
    siralanmis = sorted(sayac.items(), key=lambda item: (-item[1], item[0]))
    return [{"madde": madde, "adet": adet} for madde, adet in siralanmis]


def defter_md(state_dir: Path) -> str:
    """Human-readable ledger render for part 2 / the panel.

    Recent packages, then the aggregated ISTEK blind-spot map. Contains only
    what the schema itself holds — slugs, hashes, sizes, statuses — never a
    note body or an ODENA-DONUS body, because neither is ever written here.
    """
    defter = Defter(state_dir)
    son = defter.son_paketler(limit=20)
    agregasyon = istek_agregasyonu(state_dir)

    lines = ["# Pasaport defteri", "", "## Son paketler", ""]
    if not son:
        lines.append("_Henüz paket yok._")
    for ozet in son:
        lines.append(
            f"- `{ozet['id']}` — {ozet['soru']!r} "
            f"({ozet['gonderi_sayisi']} gönderi, son n={ozet['son_n']}, "
            f"{ozet['donus_sayisi']} dönüş, {ozet['istek_sayisi']} istek)"
        )
    lines += ["", "## Kör nokta haritası (ISTEK)", ""]
    if not agregasyon:
        lines.append("_Henüz ISTEK kaydı yok._")
    for satir in agregasyon:
        lines.append(f"- ({satir['adet']}×) {satir['madde']}")
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def _cmd_durum(args: argparse.Namespace, state_dir: Path) -> int:
    if args.json:
        print(json.dumps(Defter(state_dir).son_paketler(limit=20), ensure_ascii=False))
    else:
        print(defter_md(state_dir), end="")
    return 0


def _cmd_goster(args: argparse.Namespace, state_dir: Path) -> int:
    kayit = Defter(state_dir).oku_kayit(args.id)
    if kayit is None:
        print(PAKET_BILINMIYOR_SLUG, file=sys.stderr)
        return 1
    print(json.dumps(kayit, ensure_ascii=False, indent=2))
    return 0


def _cmd_istekler(args: argparse.Namespace, state_dir: Path) -> int:
    agregasyon = istek_agregasyonu(state_dir)
    if args.json:
        print(json.dumps(agregasyon, ensure_ascii=False))
    else:
        for satir in agregasyon:
            print(f"{satir['adet']}\t{satir['madde']}")
    return 0


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state-dir", type=Path, default=STATE_DIR)
    sub = parser.add_subparsers(dest="command", required=True)

    durum_parser = sub.add_parser(
        "durum", help="Recent packages and the blind-spot map."
    )
    durum_parser.add_argument("--json", action="store_true")
    durum_parser.set_defaults(handler=_cmd_durum)

    goster_parser = sub.add_parser(
        "goster", help="Show one paket-id's full ledger record."
    )
    goster_parser.add_argument("id")
    goster_parser.set_defaults(handler=_cmd_goster)

    istekler_parser = sub.add_parser(
        "istekler", help="Aggregated ISTEK line items, most frequent first."
    )
    istekler_parser.add_argument("--json", action="store_true")
    istekler_parser.set_defaults(handler=_cmd_istekler)

    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = _parse_args(argv)
    except SystemExit as exc:
        return exc.code if isinstance(exc.code, int) else 1
    return args.handler(args, args.state_dir)


if __name__ == "__main__":
    raise SystemExit(main())
