---
yazan: claude
model: sonnet
---

# F3 Kaydet (Save — the golden path)

## What it does

`scripts/kaydet.py` is the panel's "Kaydet" action and its CLI equivalent:
"flush + compile, local, now — like saving a project." Whatever text it is
given is appended to today's daily log exactly the way a hook flush would,
except **no model ever sees the note first**. There is no summarization
step, no prompt, and no call ledger entry for the note itself — zero model
tokens are spent on it. Once the note is durably on disk, `compile.py` runs
to completion immediately, so the note becomes searchable concept-note
material within one call instead of waiting for the evening trigger.

This is deliberately a different shape from a hook flush: a hook flush
turns a whole transcript into a five-section Turkish summary through a
model; Kaydet just writes down what it was handed.

## Input

Exactly one of:

- a positional argument: `python scripts/kaydet.py "metin"`
- `--stdin` — read all of stdin (this is what the panel uses, so the note
  text never touches argv)
- `--dosya PATH` — read a UTF-8 file

Giving more than one source is refused (`kaydet-birden-fazla-kaynak`);
giving none is treated the same as an empty note.

## Gates, in order

All of these run before anything is written, and the note is discarded (not
truncated, not silently repaired) on any of them:

0. **Normalization.** `kaydet.normalize_text` runs on the title and the body
   **before every other gate below**, and its output — not the original
   text — is what every later gate inspects and what ultimately gets
   written. It strips one leading `U+FEFF` (byte-order mark) and folds
   every line-separator shape Python's `re.MULTILINE` does **not** treat
   `^` as following — CRLF, a bare CR, `U+2028` (LINE SEPARATOR), `U+2029`
   (PARAGRAPH SEPARATOR), and `U+0085` (NEL) — into a plain `\n`. This
   closes a real bypass of gate 4 below: `DIRECTIVE_SHAPED`'s `^` anchor
   under `re.MULTILINE` only fires right after a bare `\n`, so a directive
   sitting immediately behind any of those other shapes (or right behind a
   leading BOM) would otherwise never be seen as being "at a line start" at
   all, and would slip through unquarantined. Ordinary text is unaffected —
   the daily log is LF already, so normalization is a no-op for anything
   that didn't contain one of these shapes; ordinary CRLF input (a Windows
   editor's paste, for instance) is simply saved as LF, same as it would
   read either way.
1. **Empty check.** All-whitespace or empty text refuses with `kaydet-bos`
   and exit 1.
2. **Size cap.** `BEYIN_KAYDET_MAX_KARAKTER` (default 20000 characters,
   measured on the raw source text before any redaction) refuses with
   `kaydet-cok-uzun` and exit 1. The note is never truncated to fit — an
   oversized note is a decision for the caller, not something Kaydet
   quietly shrinks.
3. **Secret redaction.** `secret_guard.redact` runs on the title and the
   body (checked and reported together — a secret in the title is exactly
   as real as one in the body). Any hit is replaced with `[SIR:<kalıp>]`
   and a `warn:secret-redacted-kaydet:<patterns>` health warning is written
   (same slug style as flush's own `warn:secret-redacted-input:...`). The
   **redacted** text is what gets checked and written from here on — a
   secret must never reach the quarantine file either.
4. **Directive-shaped check.** `flush.DIRECTIVE_SHAPED` — the same pattern
   `flush.py` and `compile.py` both use (`SYSTEM:`, `TALİMAT:`, `IGNORE
   ALL...`, etc. at the start of a line) — runs against the redacted title
   and body. A hit means the note is **not** written to the daily log at
   all: instead it is preserved verbatim through compile.py's own
   `_quarantine_content` convention, landing next to compile's own
   quarantined files under `.stage/karantina/`, with the same forensic
   JSON sidecar (matched pattern, offending excerpt, timestamp, source).
   Kaydet exits 1 with `kaydet-karantina`. This check runs proactively,
   before the note ever reaches the daily log, specifically so a
   directive-shaped Kaydet note cannot cause compile.py's own directive
   check to quarantine the **whole day's** daily file (which would catch
   other, unrelated sessions from the same day as collateral damage).

## Anchor and provenance

Kaydet writes a session anchor exactly like a hook flush does, through
`retrieve.format_session_anchor`, with its own declared source:

```
<!-- session:kaydet-<YYYYmmddTHHMMSS> ts:<iso timestamp> source:kaydet -->
```

`"kaydet"` is a member of `retrieve.SESSION_SOURCES` alongside `claude`,
`codex`, `web`, and `gemini`, so the anchor round-trips through
`retrieve.parse_session_anchors` and is carried into a concept note's
`## Kaynaklar` section by the compiler exactly like any other session's
anchor — and is stripped back out of every hit body before anything
reaches a session, same as every other source.

There is **no deduplication**: a note is a note. Saving the same text twice
produces two blocks with two distinct anchors (distinct timestamps), not
one block kept and one dropped.

## The daily block

```
### Oturum (<HH:MM>) · kaydet

<!-- session:kaydet-<...> ts:<...> source:kaydet -->

<body>
```

With `--baslik "..."`, the body is `**<başlık>**` on its own line, a blank
line, then the note text.

## Locking

Two locks, for two different races:

- **Per-call lock.** `ingest_common.flush_session_lock("kaydet", state_dir)`
  — the same lock primitive a hook flush takes for its own session,
  keyed on the literal id `"kaydet"` (a real Claude Code session id can
  never collide with it). This is held across the redact → directive-check
  → write sequence, and released **before** the compile subprocess is
  spawned — compile.py has its own lock and its own nezaket gate, so there
  is no reason to hold Kaydet's lock for the (up to 15-minute) length of a
  compile run.
- **Shared daily-append lock.** `flush._append_daily` now takes a short
  lock around the whole exists-check-then-append sequence, not just the
  per-session lock a hook flush already had. The per-session lock only ever
  serialised a session against **itself** — it never protected the daily
  file against two *different* writers (a hook flush for one session, and
  Kaydet, or two hook sessions) landing on the same file at the same
  moment. Without this, a stale `exists() == False` read followed by the
  header's truncating `write_text` could wipe out another writer's
  already-appended entry. This is the one behavior change to `flush.py`
  outside of Kaydet's own files — output bytes for a single writer are
  unchanged.

  This lock always lives at `flush.STATE_DIR/daily-append.lock` — flush's
  own fixed module constant — **regardless of what `--state-dir` Kaydet
  itself was invoked with**. It has to be: this lock exists to serialise
  Kaydet against a concurrent hook flush writing the same daily file, and a
  hook flush always locks against `flush.STATE_DIR`. Two writers locking on
  two different files (Kaydet's own `--state-dir` versus flush's fixed one)
  would not serialise anything — it would just be two independent locks
  guarding nothing against each other, exactly the race this lock exists to
  close. `--state-dir` only ever changes where Kaydet's own per-call lock
  and health file live, never where this shared lock lives.

## The compile trigger — and why it bypasses nezaket

Once the note is durably written, Kaydet spawns:

```
<python> <vault>/.claude/scripts/compile.py --nezaket-del
```

with `BEYIN_INVOKED_BY` popped from the environment (mirroring
`flush.maybe_trigger_compile` exactly — compile.py's own `main()` returns 0
immediately when that variable is set, so it must not be inherited) and
`BEYIN_MODEL_BACKEND` forced to `claude` (compile is sealed to the claude
backend regardless of what backend Kaydet's own environment happens to be
configured for — see the A4 gate decision `flush.py` already documents).

`--nezaket-del` bypasses the A7 politeness gate (see
[nezaket.md](nezaket.md)) for this one compile run. **This is a deliberate
master decision, not an oversight**: nezaket exists to defer background,
unattended work while the machine is busy with something else. Kaydet's
compile is neither background nor unattended — it is the direct,
synchronous consequence of a human pressing "Kaydet" a moment ago. An
explicit user action is its own permission, exactly the same reasoning the
panel's "Selected: run" nezaket release already relies on.

The compile call runs to completion (`subprocess.run`, not a detached
`Popen`) so its exit code can be reported back. Its timeout is
`BEYIN_KAYDET_DERLEME_ZAMAN_ASIMI` (default 900 seconds). On timeout the
health slug `kaydet-derleme-zaman-asimi` is written — but **Kaydet still
exits 0**: the note itself is safely on disk regardless of what the compile
step does afterward, and that is the whole contract. A non-zero compile
exit code is recorded as a `warn:kaydet-derleme-basarisiz:<code>` health
warning, also without changing Kaydet's own exit code.

## Env vars

- `BEYIN_KAYDET_MAX_KARAKTER` — the size cap (default 20000; unset, blank,
  non-numeric, or non-positive falls back to the default).
- `BEYIN_KAYDET_DERLEME_ZAMAN_ASIMI` — the compile subprocess timeout in
  seconds (default 900; same fallback rules).

## CLI

```
python scripts/kaydet.py "metin"                  # positional text
echo "metin" | python scripts/kaydet.py --stdin
python scripts/kaydet.py --dosya not.txt
python scripts/kaydet.py "metin" --baslik "Başlık"
python scripts/kaydet.py "metin" --json            # machine-readable result
```

`--vault-root` and `--state-dir` follow the same defaults every other
top-level script uses. Human output is two lines: what was written (or the
failure slug), then the compile outcome. `--json` prints:

```json
{
  "yazildi": true,
  "dosya": "<daily path>",
  "capa": "<session anchor>",
  "karakter": 123,
  "sir_karartildi": ["aws-anahtar"],
  "derleme": { "kosuldu": true, "cikis": 0, "sure_sn": 4.201 },
  "yazma_sure_sn": 0.014
}
```

`yazma_sure_sn` and `derleme.sure_sn` are separate, end-to-end wall-clock
measurements of the write gate and the compile step respectively — not
model latency (there is none for the note itself).

That JSON line is itself prefixed with `RESULT_MARKER` (the fixed token
`KAYDET-SONUC `) — so `--json` output is exactly one line reading
`KAYDET-SONUC {...}`, not bare JSON. This lets a caller streaming Kaydet's
combined stdout (the panel does, over SSE) pick the result line out even
when it's interleaved with whatever the spawned compile subprocess itself
printed to the same stream; the human (non-`--json`) output is unaffected
and stays the two plain lines described above. On a gate failure, the JSON
shape is `{"yazildi": false, "hata": "<slug>", ...}` — with `karakter` also
present for `kaydet-cok-uzun`, and `karantina_dosyasi` (the quarantine
file's path) present for `kaydet-karantina`.

## Exit codes

Kaydet's exit code answers exactly one question: **was the note written?**

- **0** — the note is durably in the daily log. This is true even if the
  compile step that follows times out or fails; those outcomes are
  reported through health and the `derleme` field, never through Kaydet's
  own exit code.
- **1** — the note was not written: empty/whitespace-only text
  (`kaydet-bos`), oversized text (`kaydet-cok-uzun`), more than one input
  source given (`kaydet-birden-fazla-kaynak`), a stdin/file read failure
  (`kaydet-stdin-hata` / `kaydet-dosya-hata`), a directive-shaped note
  quarantined instead of written (`kaydet-karantina`), or a filesystem
  write failure (`kaydet-yazma-hatasi`).

## Panel

The panel's **Kaydet** card (see [panel.md](panel.md)) is a textarea, an
optional title field, and a "Kaydet" button, sitting above the tabs so it
is reachable regardless of which tab is open. `POST /api/action/kaydet`
runs `kaydet.py --stdin --json`, feeding the note text over **stdin**, not
as a command-line argument — the same reasoning as every other credential-
or content-bearing panel action: argv can end up in a process listing or
shell history, stdin does not. Like every other operation, only one runs at
a time (`409 operation_in_progress` otherwise) and its output — including
whatever the spawned compile prints — streams over the same SSE
`operation-output` events the rest of the panel uses. The draft lives only
in the textarea's own DOM value; the panel never writes it to
`localStorage`.

The panel does **not** clear the draft on the `202` that starts the
operation — that response only means the process was launched, not that
the note is written; every gate above and the write itself still happen
inside it. Instead, `kaydet.py --json`'s one machine-readable output line
is prefixed with the fixed token `RESULT_MARKER` (`KAYDET-SONUC `), so the
panel can pick it out of the SSE `operation-output` stream even when it's
interleaved with whatever the spawned compile subprocess prints to the
same stdout. Only once that line arrives **and** decodes to `yazildi:
true` does the panel clear the draft; any failure (`kaydet-karantina`,
`kaydet-cok-uzun`, `kaydet-bos`, a write error, a non-zero exit) leaves the
draft exactly as the caller typed it, and shows the failure reason — the
`karantina_dosyasi` path too, for a quarantine — in the result line
instead.

## Health

Kaydet writes to its own health file, `kaydet-health.json` — never the
shared `health.json` that `flush.py` and `compile.py` write to. Sharing
that file would mean Kaydet's `component`/`error` fields overwrite
whichever of flush's or compile's own status was written last, corrupting
`durum.py`'s health summary for both of them.

## Limits

- **`flush.py`'s own `DIRECTIVE_SHAPED` check has the identical
  `re.MULTILINE`-`^`-only-follows-`\n` blind spot** that gate 0 above
  closes for Kaydet's own path (CRLF, bare CR, `U+2028`, `U+2029`,
  `U+0085`, or a directive right behind a leading BOM). Kaydet's
  `normalize_text` only ever runs on Kaydet's own input before it reaches
  `flush._append_daily` — it does not, and by design does not, touch
  `flush.py`'s own regex or any other caller of it (a hook flush's own
  transcript-derived text, or `compile.py`'s scan of an existing daily
  file, are both still exposed to this same bypass). Closing it there is an
  explicit follow-up, out of scope for this change.
