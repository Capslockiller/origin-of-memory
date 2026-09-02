# Release notes — 0.5.0

The brain learns to be polite, to save, to travel, and to delegate — and the
whole of it survives its first real Windows day.

## Nezaket: the brain yields the machine

Background model work (flush, compile) now defers while a foreground
application you named is busy — measured from real signals (GPU load, focused
process), not a guess — and resumes when the machine is idle again. The panel
can release the hold explicitly. See `docs/nezaket.md`.

## Kaydet: a zero-token save button

"Save this" no longer costs a model call. `kaydet.py` writes a redacted note
straight into the daily log under the same append lock the flush pipeline
uses, stamps its own anchor, and triggers compilation afterwards. The panel
gets a card for it; the `KAYDET-SONUC` marker line makes it scriptable. See
`docs/kaydet.md`.

## Bağlam Pasaportu: memory for chats that have no tools

Consumer web chats (ChatGPT, Gemini, claude.ai) can't reach the brain — so
the brain now travels by clipboard. `--pasaport` builds an outbound package
with a cumulative manifest ("only what's new"), a clipboard watcher catches
the `[ODENA-DONUS]` answer, and everything returning passes the full gate
chain — size cap, anchor-forgery guard, secret redaction, directive-shaped
quarantine, manifest dedup — before a human approves it into the daily log.
What the web model *asked for and didn't get* lands in the ISTEK ledger: a
map of the brain's blind spots. See `docs/pasaport.md`.

## Kokpit: a tower and a window on it

`kule.py` is a standalone multi-lane job manager for `claude -p`/`codex exec`
work: explicit model per job, capped lanes, prompt redaction before disk,
before/after diffs that park a job at `waiting-approval`, and an archive that
never deletes. The panel gained the matching Kokpit card — job form, lane
meters, per-job Log/Diff/Approve/Reject — and a VS Code bridge that opens a
parked job's diff as a real `code --diff` when VS Code exists, falling back to
stored diff text when it doesn't. The constitution held: the panel never
computes, it displays. See `docs/kokpit.md`.

## Two new guards on the flush pipe

`pii_guard.py` silently redacts checksum-validated Turkish structural PII
(TCKN, VKN, IBAN-TR, cards, phones, plates) on both the transcript going into
the summarizer and the summary coming out. `unicode_guard.py` strips
zero-width characters, the invisible Unicode Tags block, bidi overrides and
line-separator tricks *before* the directive-shaped quarantine regex runs —
closing the known evasion. Free-text names and addresses are deliberately out
of scope. Every hit is visible in health telemetry.

## Retrieval, faster and settled

The session-start hook dropped from a measured p95 of 443 ms to 112–127 ms
(direct-python stdin mode, import diet; the PowerShell wrapper stays as a
fallback). Recency moved into the RRF fusion as a weighted channel.
And the BM25-vs-RRF question is closed with a measurement: BM25 stays the
default (recall@3 83.2%, recall@5 91.2% on the gold set; RRF measured
significantly worse, p=0.003). Three stdlib measurement tools ship so the
numbers can be reproduced: `olc_baslangic.py`, `tr_beir_kos.py`, `a4_kapi.py`.

## The first real Windows day

Everything above was built on Linux. Its first run on a real Windows machine
found **five genuine defects** — a different-drive cwd wrongly refused, a
refused job painted as success, a live `taskkill` aimed at a test fake, an
8.3-short-path mock in CI, and a pid-only temp name that let tower threads
collide on atomic writes (a silent corruption risk on every OS) — all fixed
with pinning tests. Suite on Windows: 837 passed, 1 skipped; CI green on
3.12 and 3.13.

## Not verified

The passport's paste round-trip was exercised against the clipboard and the
gate chain end-to-end, but **never against a real web chat session**; the
nezaket probe has not run with the target application actually busy in the
foreground; and kill-while-running orphan reaping was reasoned and tested at
the unit level, not exercised by pulling the plug. The MCP server now reports
version 0.5.0.
