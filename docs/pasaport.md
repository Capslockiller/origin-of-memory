---
yazan: claude
model: sonnet
---

# F4 "Bağlam Pasaportu" (Context passport)

## What it is

A memory-vault reader (Claude Code, Codex, an MCP client) already gets
retrieval for free — a prompt hook or a search tool reaches straight into
`.state/notes.db`. A **consumer web chat** (ChatGPT, Gemini, claude.ai in a
browser tab, with no local tool access at all) gets none of that: the only
channel in is what a human pastes in, and the only channel out is what a
human copies back.

Bağlam Pasaportu is that channel, worked by hand through the clipboard:

```
scripts/context_pack.py --pasaport   ---->  clipboard  ---->  paste into web chat
                                                                     |
                                                                     v
                                                          web model answers,
                                                     ends its reply with an
                                                       [ODENA-DONUS] block
                                                                     |
                                                                     v
                                              pano_izleyici.py (clipboard listener)
                                                                     |
                                                                     v
                                              pasaport_kapi.py: parse + gate
                                                                     |
                                                                     v
                                         panel "Pasaport" card: one-click approval
                                                                     |
                                                                     v
                                                daily log, via pasaport_kapi.uygula
```

**Part 1: the packager (C1) and the ISTEK ledger (C4)** — the outbound half,
above. **Part 2 (this document also covers it now): the clipboard listener
(C2, `pano_izleyici.py`), the parser and gate pipeline (C3,
`pasaport_kapi.py`), and the panel's "Pasaport" approval card** — the return
half. Nothing a web model's reply says ever reaches the vault without a
human clicking "Onayla" in the panel first. "ODENA" is the protocol's own
name for itself, carried in every marker it emits.

Design principle: **"Delta = speed, ZIP = completeness."** Most exchanges
only need to hand over what changed since the last package under this
paket-id (small, fast, cheap to paste). Occasionally you want the whole
slice regardless of size — `--zip` is that escape hatch, never budgeted.

The ISTEK ledger is the **blind-spot map**: every time a web model says "I
don't have enough context for X", that line item is recorded. Aggregated
across every package ever sent, most-frequent-first, it shows what the
local knowledge base keeps failing to supply — see `istekler` below.

## Building a package

```bash
python scripts/context_pack.py --pasaport "hangi mimari kararı aldık?"
```

This is `context_pack.py`'s original behaviour (`compose_context`, the old
positional-`question` mode with `--clip`/`--no-map`/`-k`/`--vault`) **plus**
a new mode, gated entirely behind `--pasaport`; every existing flag and the
plain-mode output are unchanged when `--pasaport` is absent.

By default a `--pasaport` package is copied to the clipboard (Windows
`clip.exe`, same as the old `--clip` path) and one summary line is printed:

```
paket 454ff4f5f0c6 n=1 2629 karakter, 3 not
```

`--yazdir` prints the full package to stdout instead of touching the
clipboard — useful for inspecting a package before pasting it, or piping it
somewhere that isn't a Windows clipboard at all.

## Package format

A package is four sections joined by blank lines, always in this order:

```
[ODENA-PAKET id:454ff4f5f0c6 n:1 ts:2026-09-02T11:34:44+00:00]

## Kök harita

# Hafıza haritası
...

### knowledge/concepts/mimari-karar-a.md

...not gövdesi...

[ODENA-MANIFEST mimari-karar-a:c18524affb05]

Bu paket kalıcı hafızadan alınan bağlamdır. Yalnızca bu pakette YENİ olan
bilgiyle cevap ver; paket içeriğini tekrarlama.

Cevabının SONUNA, hatırlanmaya değer YENİ bilgi/karar varsa aşağıdaki bloğu
tam olarak bir kez, kısa markdown maddeleriyle ekle:
[ODENA-DONUS id:454ff4f5f0c6]
- ...
[/ODENA-DONUS]

Bağlam yetersizse tahmin ETME; eksik olan her kalemi tek satırda listele ve
orada dur:
[ODENA-ISTEK id:454ff4f5f0c6]
- ...
[/ODENA-ISTEK]

Bu işaretleri kod bloğuna koyma.
```

1. **Header** — `[ODENA-PAKET id:<paket-id> n:<seq> ts:<iso>]`. `paket-id` is
   12 hex characters of `sha256(question + ts + 8 random bytes)`, generated
   once per conversation (`n:1`); `n` increments by one on every `--ek`
   follow-up package sent under the same id.
2. **Root map + notes** — the same material `compose_context` (the old
   mode) builds: `knowledge/index.md` and the top retrieval hits for the
   question, in retrieval order. In delta mode this section is fit into the
   character budget (below); `--zip` includes it whole, unbudgeted.
3. **Manifest** — `[ODENA-MANIFEST <slug>:<hash> …]`, one entry per note
   ever sent under this paket-id, across every package so far (this one and
   every earlier `--ek`). Read from the C4 ledger, not recomputed from
   scratch — see "Cumulative manifest" below. `[ODENA-MANIFEST]` with
   nothing after it means no notes have been sent yet. This section is
   **not** counted against the character budget: it is bookkeeping, not
   content, and grows only as fast as what has already gone through the
   budget once.
4. **Footer** — fixed Turkish instructions to the web model, in plain text
   with square-bracket markers and **no code fences** (copy-pasting through
   a web chat UI's markdown editor can silently swallow fenced content,
   which would delete the very markers the protocol depends on). It tells
   the model to: answer using only information new to this package; end its
   answer with exactly one `[ODENA-DONUS id:...]...[/ODENA-DONUS]` block of
   short markdown bullets if it has anything worth remembering; emit
   `[ODENA-ISTEK id:...]...[/ODENA-ISTEK]` — one missing item per line — and
   stop instead of guessing, if the context wasn't enough; and never wrap
   any of these markers in a code block.

## Delta budget vs. `--zip`

`BEYIN_PASAPORT_DELTA_KARAKTER` (default **4000** characters) bounds the
header + root-map/notes section + footer of a delta package. Fit is greedy:

1. The root map goes in first, whole, if it fits. If not, it shrinks to
   headings only (`#`/`##`/... lines, everything else dropped). If even
   that doesn't fit, the root map is left out entirely.
2. Notes are added in retrieval order, **whole note or not at all** — a
   note is never cut mid-body to make it fit. A note that doesn't fit is
   skipped and the next one is tried.
3. If nothing fits at all beyond the header and footer (the corpus outran
   the budget before the first whole note could go in), the package falls
   back to the **first candidate note's first 1500 characters**, with a
   `[…kesildi]` marker appended — the one place a note IS cut, and the only
   one.

`--zip` skips all of this: the root map goes in whole and every candidate
note goes in whole, regardless of size. Use it when you want the complete
slice and don't mind pasting more.

## `--ek`: follow-up packages

```bash
python scripts/context_pack.py --pasaport --ek --id 454ff4f5f0c6 "yeni soru"
```

A follow-up package for a paket-id that already exists (an unknown or
missing `--id` fails with `pasaport-paket-bilinmiyor`, exit 1). `--ek`:

- reuses the original question if none is given (positional or `--soru`),
  or re-retrieves against a new one if you supply one;
- **excludes every note already in the cumulative manifest** — a note sent
  in package 1 is never sent again in package 2, even if it still matches;
- increments `n`.

### Cumulative manifest

The manifest recorded in the C4 ledger (`pasaport_defteri.Defter.manifest`)
is the union of every note sent under this paket-id so far, not just the
latest package's own notes — package 2's own `notlar` list in the ledger
may hold only `["beta"]`, while `manifest(id)` after package 2 still holds
`{"alfa": ..., "beta": ...}` from package 1 as well. This is what makes
`--ek`'s exclusion correct across an arbitrary number of follow-ups.

## The C4 ISTEK ledger

`scripts/pasaport_defteri.py` — `<state-dir>/pasaport-defteri.json`, one
record per paket-id, written with the repo's usual atomic-write-plus-lock
pattern (`beyin_ortak._atomic_write_json` / `_lock_exclusive`).

**What it stores, and what it deliberately never stores:** note slugs and
their content-hashes (not bodies), the question text, package sizes,
`[ODENA-DONUS]` outcome status/reason/size (not the return text), and
`[ODENA-ISTEK]` line items. A note body or a returned answer's text has no
code path into this file — `context_pack.compose_pasaport` never hands
either to `pasaport_defteri`, and the ledger's own render (`defter_md`)
only has slugs/hashes/sizes/statuses to draw from.

Every package is recorded (`paket_kaydet`) **before** it is copied to the
clipboard, so a package that failed to reach the clipboard for some reason
still shows up in its paket-id's cumulative manifest for the next `--ek`.

Retention: `BEYIN_PASAPORT_DEFTER_TAVAN` (default **200**) paket-ids are
kept; the oldest (by creation time) are pruned on every write past the cap.

### API

- `Defter(state_dir)` — the ledger handle.
- `paket_kaydet(id, *, soru, n, ts, karakter, notlar, zip_mi, manifest_ekle)`
  — record one generated package; creates the paket-id entry at `n=1`.
- `manifest(id) -> dict[slug, hash]` — the cumulative manifest, `{}` for an
  unknown id.
- `donus_kaydet(id, *, ts, karakter, durum, neden="", daily_capa=None)` —
  record one `[ODENA-DONUS]` outcome (`durum` is `"kabul"`, `"karantina"`,
  or `"red"`; anything else falls back to `"red"`). Called by
  `pasaport_kapi.py` — see "C3 — the gate pipeline" below.
- `istek_kaydet(id, maddeler)` — record one `[ODENA-ISTEK]` block's line
  items. Also called by `pasaport_kapi.py`.
- `son_paketler(limit=20)` — recent paket-id summaries, newest first.
- `istek_agregasyonu(state_dir)` — every ISTEK line item across every
  paket-id, most-frequent first, ties broken alphabetically. The blind-spot
  map.
- `defter_md(state_dir) -> str` — the human-readable render; the panel
  builds its own JSON view from the same data instead (`GET /api/pasaport`).

### CLI

```bash
python scripts/pasaport_defteri.py durum [--json]   # recent packages + blind-spot map
python scripts/pasaport_defteri.py goster <id>       # one paket-id's full record
python scripts/pasaport_defteri.py istekler [--json] # aggregated ISTEK list, most frequent first
```

## C3 — `pasaport_kapi.py`: parsing and the gate pipeline

Pure and fully testable on Linux (`scripts/tests/test_pasaport_kapi.py`) —
the only OS-specific piece of part 2 is the clipboard listener below, which
hands text into this module in-process.

### `ayristir(text) -> Ayristirma`

Finds `[ODENA-DONUS id:<id>]…[/ODENA-DONUS]` and/or
`[ODENA-ISTEK id:<id>]…[/ODENA-ISTEK]`, following the same precedent as
`context_bridge._splice`: exactly one BEGIN and one END per kind, the END
after the BEGIN, else refuse with a slug — never guess.

| Slug | Meaning |
|---|---|
| `pasaport-blok-cift` | More than one BEGIN or END marker of the same kind. |
| `pasaport-blok-yarim` | A BEGIN with no matching END, or vice versa. |
| `pasaport-blok-ters` | An END that appears before its BEGIN. |
| `pasaport-id-uyusmaz` | Both a DONUS and an ISTEK block are present with different ids. |

`kaydet.normalize_text` (BOM / CR / U+2028 folding) runs first, then a code
fence line the web UI's markdown editor may have wrapped around the block
is stripped — whitespace around a marker line is tolerated too — but a
genuinely nested or duplicated marker is not: that is `pasaport-blok-cift`,
on purpose. Neither block being present at all is not an error; it means
the reply had nothing worth remembering.

### `kapilar(ayristirma, state_dir, vault_root, now) -> Sonuc`

Before `kapilar` is ever reached, `isle_metin` caps the WHOLE clipboard text
against `BEYIN_PASAPORT_GIRDI_MAX_KARAKTER` (default **60000**) —
`pasaport-girdi-cok-uzun`, `ayristir` never even called, nothing persisted.
That gate exists because none of the per-block caps below bound the input
BEFORE it is parsed — an ODENA-ISTEK block in particular has no cap of its
own until `kapilar` gets to it.

`kapilar` itself runs, in order, once the id is confirmed to exist in the
C4 ledger (`pasaport-paket-bilinmiyor` otherwise — an id the packager never
issued, or one long since pruned):

1. **Size cap.** `BEYIN_PASAPORT_DONUS_MAX_KARAKTER` (default **12000**)
   bounds the DONUS body; over it is `pasaport-donus-cok-uzun`, recorded
   `red` in the ledger, nothing queued.
2. **Secret redaction.** `secret_guard.redact` — same patterns, same
   `[SIR:<kalıp>]` replacement, as every other gate in this repository. A
   hit is a warning on the queued candidate, never a refusal by itself.
3. **Directive-shaped quarantine.** `flush.DIRECTIVE_SHAPED` — same
   posture as `kaydet.py`: on a hit the (already redacted) body is
   preserved verbatim under `<state-dir>/karantina/pasaport-<ts>.md`,
   recorded `karantina` in the ledger, and never queued for approval.
4. **Manifest-dedup.** The DONUS body is split into bullet-line and
   blank-line-separated paragraph units. For each unit, a normalised
   fingerprint (bullet marker stripped, lowercased, punctuation stripped,
   whitespace collapsed) is compared against every line/paragraph of every
   note already listed in this paket-id's cumulative manifest
   (`vault_root/knowledge/concepts/<slug>.md`) — an exact fingerprint match,
   or >= 0.9 token-Jaccard similarity, drops the unit. A manifest note whose
   file is missing, or whose current content hashes differently from the
   manifest's recorded hash (the note changed since the package went out),
   is skipped for dedup entirely and reported as a `manifest-bayat:<slug>`
   warning — a stale comparison target must never cause a false drop. If
   every unit is dropped, nothing new survived: `pasaport-donus-bos`, `red`
   in the ledger, nothing queued.
5. **Neutralise forged provenance.** Every surviving unit has `<!--`/`-->`
   escaped to `&lt;!--`/`--&gt;` (so no attacker-supplied text can shape a
   `<!-- session:... source:... -->` anchor that
   `compile.carry_source_anchors` would later read straight out of the
   daily log and carry, unverified, into a concept note) and any line
   pretending to be the `> dogrulanmamis:` header stripped outright — only
   the ONE real header line below may exist in the written block. A unit
   that neutralises down to nothing is dropped, same as a manifest-dedup
   drop.
6. **Wrap what survives.** The queued body starts with
   `> dogrulanmamis: web dönüşü, kaynak: <paket-id>` followed by the
   surviving units verbatim — content is never rewritten beyond step 5,
   only tagged and filtered.

The ISTEK block (if present) is handled independently: its line items
(bullets stripped, redacted defensively, capped to
`BEYIN_PASAPORT_ISTEK_MAX_MADDE` items — default **20** — of at most 300
characters each, longer items truncated with `…` and the cap reported as a
`pasaport-istek-kirpildi` warning) go straight to `Defter.istek_kaydet`
regardless of what happens to the DONUS block, as long as the id itself is
known. ISTEK never touches the daily log.

### The pending candidate — exactly one at a time

`bekleyen_yaz(state_dir, payload)` replaces
`<state-dir>/pasaport-bekleyen.json` atomically; a still-pending candidate
being overwritten by a newer paste is reported as
`pasaport-bekleyen-degisti`, never silently dropped. The payload carries
the id, sequence number, surviving units, the full body to write, the
dropped-unit count, warnings, and `raw_hash` — `sha256` of the **original**
clipboard text, before any normalisation.

`onayla(state_dir, vault_root, raw_hash)` and
`reddet(state_dir, raw_hash)` both refuse
(`pasaport-bekleyen-uyusmaz`) unless `raw_hash` matches the pending file —
the guard against approving (or rejecting) a candidate a newer paste has
since replaced. `onayla` calls `uygula`, which writes the body to the daily
log with a `source:pasaport` session anchor
(`retrieve.format_session_anchor(f"pasaport-{id}-{n}", ts,
source="pasaport")`), records `kabul` (with `raw_hash`) in the C4 ledger,
then spawns `compile.py --nezaket-del` by reusing `kaydet._spawn_compile`
directly — the identical timeout, kill-tree-on-timeout, and
health-reporting behaviour Kaydet gets. `reddet` records `red` (`neden:
kullanici-reddi`) and touches neither the daily log nor compile.

`onayla` is idempotent against a crash between the daily-log write and
deleting the pending file: before writing, the pending candidate is marked
`{"durum": "uygulaniyor"}` atomically. A retry that finds that marker
already set checks the ledger for a `kabul` entry carrying this exact
`raw_hash` — present means the previous attempt already wrote the daily
log, so this call only deletes the (now redundant) pending file and returns
`{"uygulandi": true, "zaten": true}`; absent means the crash happened
before the write landed, so this call proceeds exactly like a first
attempt. Either way the daily log is written at most once per approval.

### CLI

```bash
python scripts/pasaport_kapi.py isle --stdin              # parse + gate stdin -> pending
python scripts/pasaport_kapi.py isle --dosya paste.txt     # same, from a file
python scripts/pasaport_kapi.py bekleyen [--json]           # show the pending candidate
python scripts/pasaport_kapi.py onayla <raw_hash>            # approve it
python scripts/pasaport_kapi.py reddet <raw_hash>             # reject it
```

`isle`'s machine-readable line is prefixed `PASAPORT-SONUC ` — the panel's
approve action reuses the same marker, because its spawned compile
subprocess inherits this process's stdout exactly the way Kaydet's does.

## C2 — `pano_izleyici.py`: the clipboard listener

Windows-only at runtime; every non-Win32 decision (relevance check,
sequence-skip logic, dispatch into `pasaport_kapi`, heartbeat shape) is
plain Python behind a `_win32_baglama()` seam — the same binding-contract
convention `nezaket.py` uses — and is unit-tested off Windows with fakes
(`scripts/tests/test_pano_izleyici.py`).

**No polling loop.** A message-only window
(`CreateWindowExW` with parent `HWND_MESSAGE`) registers via
`AddClipboardFormatListener`; the process blocks in `GetMessageW` and reacts
only to `WM_CLIPBOARDUPDATE`. On each one, `GetClipboardSequenceNumber` is
cross-checked against the last value seen — an unrelated clipboard change
still fires the message but is skipped without a read.

**Only `CF_UNICODETEXT` is ever read.** `OpenClipboard` retries up to 10
times, 100 ms apart (real Terminal Services/`rdpclip` sessions routinely
hold the clipboard open for a moment); the text is decoded from
`GlobalLock`/`GlobalSize` as UTF-16LE up to the first embedded NUL. No other
clipboard format — image data, file drops, a browser's private
source-URL/HTML formats — is ever enumerated or touched.

If the text contains `[ODENA-DONUS` or `[ODENA-ISTEK`, it is handed to
`pasaport_kapi.isle_metin(text, state_dir, vault_root)` **in-process** —
never a subprocess. A heartbeat file, `<state-dir>/pano-izleyici.json`
(`{pid, started, last_event_ts, events, last_slug}`), is written atomically
after every dispatched event and every 60 s via a `SetTimer` on the message
window — never by polling the clipboard on a timer.

`--once` reads the clipboard a single time and exits — used for manual
testing, and by the panel's "Panodan al" fallback button when the listener
is not running. Off Windows, `main()` prints `pano-izleyici-desteklenmiyor`
and exits 2 unconditionally, `--once` included.

```bash
python scripts/pano_izleyici.py --state-dir <state> --vault-root <vault>          # run the listener
python scripts/pano_izleyici.py --state-dir <state> --vault-root <vault> --once   # one-shot read
```

## The panel — one-click approval, never automatic

`beyin.ps1` spawns `pano_izleyici.py` hidden as its own child when the
panel starts (`Start-Process -WindowStyle Hidden -PassThru`, keeping the
`Process` object) and stops it — `CloseMainWindow()` (which can legitimately
return `false` for a message-only window; that is not a failure) then
`Kill()` — in the single shutdown path both an explicit quit and the idle
timeout funnel through. If the listener exits on its own it is **never**
restarted automatically; its live/dead state is simply what the panel
reports. `BEYIN_PASAPORT_IZLEYICI=off` skips spawning it entirely.

The "Pasaport" card (`GET /api/pasaport`, polled every 5 s) shows the
listener's running/stopped state and last event from its heartbeat file,
the pending candidate — read-only, as its surviving bullet units, dropped
count, warnings, and a short form of `raw_hash` so the reviewer sees
exactly what they are about to approve — and the aggregated ISTEK blind-spot
map. "Onayla → günlüğe" (`POST /api/action/pasaport-onayla {raw_hash}`)
streams through SSE like every other operation, gated by the same
409-before-anything-starts rule; "Reddet" runs synchronously (it touches
neither the daily log nor compile, so it needs no operation slot); "Panodan
al" (`POST /api/action/pasaport-panodan`) runs `pano_izleyici.py --once` as
a normal SSE operation. See [panel.md](panel.md) for the full route
contract.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `BEYIN_PASAPORT_DELTA_KARAKTER` | `4000` | Delta-mode character budget (header + root map/notes + footer; the manifest line is not counted). |
| `BEYIN_PASAPORT_DEFTER_TAVAN` | `200` | Paket-ids kept in the ledger before the oldest are pruned. |
| `BEYIN_PASAPORT_DONUS_MAX_KARAKTER` | `12000` | Size cap on one `[ODENA-DONUS]` body; over it is refused (`pasaport-donus-cok-uzun`), never truncated. |
| `BEYIN_PASAPORT_GIRDI_MAX_KARAKTER` | `60000` | Size cap on the WHOLE pasted reply, checked before `ayristir` ever parses it; over it is refused (`pasaport-girdi-cok-uzun`), nothing persisted. |
| `BEYIN_PASAPORT_ISTEK_MAX_MADDE` | `20` | Max ODENA-ISTEK line items recorded per block; each item is also capped at 300 characters (`…`-truncated). Capping either dimension reports `pasaport-istek-kirpildi`. |
| `BEYIN_PASAPORT_IZLEYICI` | unset | `off` stops `beyin.ps1` from spawning the clipboard listener child at all. |

## Privacy notes

- The package sent to the web chat necessarily carries note bodies — that
  is the point. Nothing in this document changes what leaves the machine
  through the clipboard; it is the same retrieval material `compose_context`
  already builds, just re-shaped and budgeted for a paste target.
- The ledger (`pasaport-defteri.json`) never stores a note body or a
  returned answer's text — only slugs, hashes, sizes, and statuses. See
  "The C4 ISTEK ledger" above.
- `pano_izleyici.py` reads only `CF_UNICODETEXT` from the clipboard — never
  image data, file drops, or any other format (a browser's private
  source-URL/HTML formats included).
- Nothing a web model's reply says reaches the vault without an explicit
  panel approval: `pasaport_kapi.py` gates and queues; only a human clicking
  "Onayla" in the panel (or running `pasaport_kapi.py onayla <hash>`
  directly) ever writes to the daily log.
- A returned DONUS body cannot forge its own provenance: `<!--`/`-->` are
  escaped and any fake `> dogrulanmamis:` line is stripped before the write
  (see step 5 above), so a web reply can never shape a
  `source:claude`/`source:kaydet`/… anchor that `compile.py` would later
  carry into a concept note as if it had come from a trusted session.
- A note's own code fence (```` ``` ````) is substituted with `'''` before
  it ever leaves the machine in an outbound package (`context_pack.py`'s
  `_notlarda_cit_temizle`) — the composed package that gets pasted into a
  web chat UI never itself contains a code fence.

## Windows smoke tests

Everything above this line is exercised by the automated suite, on Linux,
through fakes at the `_win32_baglama()` seam. The following need a real
Windows session and are not automated here:

- Start the panel; confirm the "Pasaport" card shows the listener as
  running within a few seconds and that `<state>/pano-izleyici.json` exists
  with a fresh `started` timestamp.
- Copy a pasted `[ODENA-DONUS]` reply to the clipboard; confirm the card's
  pending candidate appears within the 5 s poll, with the expected bullets,
  and that the heartbeat's `events`/`last_event_ts`/`last_slug` advanced.
- Click "Onayla → günlüğe"; confirm the daily log gained the block with its
  `dogrulanmamis`/session-anchor lines, the ledger shows `kabul`, and
  compile ran (health/`compile-state.json`).
- Click "Reddet" on a fresh candidate; confirm the pending file is cleared,
  the ledger shows `red`, and the daily log is untouched.
- Set `BEYIN_PASAPORT_IZLEYICI=off`, restart the panel, confirm no listener
  process starts and "Panodan al" still works as the manual fallback.
- Close the panel (and separately, let it idle-timeout) with the listener
  running; confirm the listener process exits both times — no orphan.
- Under a Remote Desktop / Terminal Services session, copy a reply while
  `rdpclip` is momentarily holding the clipboard; confirm the retrying
  `OpenClipboard` still picks it up rather than silently missing the event.
- Paste a reply through an actual web chat's markdown editor (fenced code
  block wrapping tolerated) and confirm the round trip still parses.
