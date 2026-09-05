# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

<!-- yazan: claude · fable-5.1 -->
- **`bm25()`'s field weights were never actually applied.** FTS5's
  `bm25(notes, ...)` weights are positional over every column of `notes(name
  UNINDEXED, title, aliases, tags, body)`, including the UNINDEXED `name`
  column — passing four weights for a five-column table silently shifted
  every one of them left, so the documented title=8/aliases=6/tags=3/body=1
  priority never applied (the effective weights were title=6, aliases=3,
  tags=1, body=1). Fixed by passing five weights, `(0.0, 8.0, 6.0, 3.0, 1.0)`,
  now a named `BM25_WEIGHTS` constant. Query-time only; no index rebuild
  needed, no `SCHEMA_VERSION` bump.

### Removed

<!-- yazan: claude · fable-5.1 -->
- **The `rrf` retrieval mode is gone.** Measured against `bm25` on the
  LoCoMo benchmark (hit@5 0.55 vs 0.13) and on every one of 11 public BEIR
  datasets, `rrf` scored significantly worse throughout and collapsed on
  dated, mixed-recency corpora. Deleted: `MODE_RRF`, `_fused_search`,
  `rrf_fuse`, `tag_overlap`/`indexed_tag_overlap`, the recency-channel and
  legacy-multiplier machinery, the `BEYIN_RETRIEVAL`/`BEYIN_RRF_K`/
  `BEYIN_RRF_RECENCY_CHANNEL_WEIGHT`/`BEYIN_RRF_LEGACY_MULTIPLIER`/
  `BEYIN_RECENCY_HALFLIFE_DAYS` environment variables, `resolve_mode`, and
  the `--retrieval` CLI flag. `search()` and `hook_result()` keep accepting
  a `mode` keyword as a no-op for source compatibility with existing
  callers; every call now ranks by BM25 only.

<!-- yazan: codex · gpt-5.6-sol -->
- **The Today panel now reads every Windows daily-session heading.** CRLF line
  endings no longer hide all sessions, PowerShell's automatic Matches variable
  can no longer overwrite the session match collection, and headings carrying
  inline provenance such as (21:16, Cowork, manual layer) are counted with that
  writer label. A PowerShell-backed regression test locks the CRLF and
  annotated-heading contract.

<!-- yazan: claude · opus-5 -->
- **Rejected or empty compiler output no longer consumes the daily it came
  from.** A daily log is marked `ingested` only when every note the run produced
  passed both the directive and the schema gate. Any other ending — a
  quarantined output, a schema rejection, or a model call that wrote nothing —
  now promotes nothing from that run (rejection is all-or-nothing per daily),
  records the file in the new `rejected` state ledger with its digest, reason
  slugs, timestamp and attempt count, and offers it again on the next compile.
  After `MAX_REJECT_ATTEMPTS = 3` attempts on the same content — 2 for an empty
  answer — the entry moves to `parked` with the health warning
  `parked:<reason>`; parked is still not ingested, and editing the daily makes
  it eligible again. Previously the clean half of a rejected run was published
  *and* the source was consumed, so the held notes lost the only material they
  could have been rebuilt from, and a single silent model call could retire a
  daily forever. Old state files without the two new keys load unchanged.
- **Publication is transactional in both publishers.**
  `compile._promote_changes()` and `rootmap._publish()` now stage every write as
  `<dest>.tmp-<run id>` beside a `<dest>.bak-<run id>` copy of the live file,
  then rename in a second loop; a failure restores the destinations already
  renamed, deletes the temporaries and fails the run, so a half-published
  compile or a root map without its hubs can no longer reach the vault.
- **The root map and the search index are rebuilt before the daily is marked
  ingested.** A failed rebuild used to be downgraded to a health warning after
  the source had already been consumed, leaving promoted notes no reader could
  find. The promotion is now rolled back and the run fails with
  `fail:rootmap-regen-failed` / `fail:retrieve-rebuild-failed`.
- **A concept written under a subdirectory is refused.** The output policy
  allowed `knowledge/concepts/<sub>/x.md` while retrieve, the root map and the
  manifest all use the non-recursive `concepts/*.md` glob, so such a note was
  promoted into invisibility. It is now held with the schema-gate reason
  `nested-path`, and the promotion path refuses it a second time.

## [0.5.0] - 2026-09-02

### Added

<!-- yazan: claude · fable-5 -->
- **PII and unicode guards join the flush pipeline.** `scripts/pii_guard.py`
  redacts checksum-validated Turkish structural PII — TCKN (mod-10), VKN
  (GİB algorithm), IBAN-TR (mod-97), cards (Luhn), TR phones, plates —
  silently, by deliberate scope choice: free-text names and addresses stay
  out (they would need a local model and carry a false-positive risk that
  was decided against). `scripts/unicode_guard.py` NFC-normalizes and strips
  zero-width characters, the invisible-but-tokenizable Unicode Tags block
  (U+E0000–E007F), bidi overrides, stray BOMs and control characters, and
  converts U+2028/U+0085 into real newlines — closing the line-separator
  trick that could slip text past the line-anchored `DIRECTIVE_SHAPED`
  quarantine regex. Both mirror `secret_guard`'s contract exactly
  (`scan(text)` / `redact|clean(text) -> (text, [classes])`, stdin filter
  mode, `--self-test`) and are wired into `flush.py` at both ends: unicode
  cleans the transcript *before* the directive-shape check, PII redacts
  transcript and summary beside `secret_guard`, and every hit lands in
  health telemetry (`warn:pii-redacted-input/output:<classes>`,
  `warn:unicode-cleaned-input/output:<classes>`). Tests mirror
  `test_secret_guard.py`; suite grows 818 → 837.

<!-- yazan: claude · sonnet -->
- **F5 "Kokpit" (Cockpit), part 1 — Kule (the tower)**: a standalone,
  stdlib-only, multi-lane background job manager for `claude -p`/`codex
  exec` work, completely separate from the panel (part 2, below) — "the
  panel never computes, it displays," so every number the panel shows
  already lives in `<state_dir>/kule/durum.json`. New
  `scripts/kule.py`: jobs (`is-ver --tur claude|codex --model M
  --prompt-dosya P|--stdin --cwd D`) require an explicit model
  (`kule-model-eksik` otherwise — no silent default), a `cwd` that exists
  and is not under the OS temp root (`kule-cwd-gecersiz` — Codex's sandbox
  cannot read Windows Temp paths), and are prompt-redacted
  (`secret_guard.redact`) before the prompt ever touches disk or a model.
  Multi-lane from the start: `BEYIN_KULE_CLAUDE_TAVAN`/`BEYIN_KULE_CODEX_TAVAN`
  (default 3/4) cap concurrent children per kind, claimed via the same
  `O_CREAT|O_EXCL` marker pattern `flush.py`'s compile-trigger and
  `compile.py`'s machine-id already use — two racing `kule calis`
  processes can never both take the same job or the same lane slot. A
  stale claim/lane marker (owning worker pid confirmed dead — `os.kill`
  POSIX, `OpenProcess`+`GetExitCodeProcess` via `ctypes` on Windows,
  `tasklist` fallback, fails open on a probe error) is reaped and its job
  marked `failed`/`kule-worker-kayip`; on a graceful stop (`dur` file,
  SIGINT/SIGTERM) already-running children are deliberately left running —
  the next `calis` invocation's reaper reclaims them once this process's
  own pid is gone. A job declaring `izlenen_dosyalar` gets a before/after
  snapshot and a `difflib.unified_diff` per file; any non-empty diff parks
  the job at `waiting-approval` until `onayla`/`reddet` (illegal
  transitions refused with `kule-gecis-gecersiz`) — Kule only reads and
  diffs, it never writes to a watched file itself. Finished jobs beyond
  `BEYIN_KULE_TAVAN` (default 50) are moved — never deleted — to
  `<state_dir>/kule/arsiv/`, oldest first; `queued`/`running`/
  `waiting-approval` jobs are never archived. Prompt and log content never
  reach `calls.jsonl` — that ledger stays numeric accounting only
  (`beyin_ortak.record_call`, `component="kule"`). CLI: `is-ver`, `durum`,
  `goster`, `log`, `diff`, `onayla`, `reddet`, `iptal`, `calis [--once]`,
  `arsivle`; `is-ver --json` prints one `KULE-SONUC {...}` line, the same
  marker convention as Kaydet's `KAYDET-SONUC` and Pasaport's
  `PASAPORT-SONUC`. See `docs/kokpit.md` — part 2 (the panel) follows
  below.

<!-- yazan: claude · sonnet -->
- **F5 "Kokpit" (Cockpit), part 2 — panel integration (B1 routes, B4 cards,
  B3 VS Code bridge)**: `beyin.ps1` gains the tower's own spawn/kill
  lifecycle — `Start-Kule`/`Stop-Kule`, mirroring
  `Start-PasaportIzleyici`/`Stop-PasaportIzleyici` exactly: `kule.py calis`
  spawns hidden with the panel and is stopped in the single shutdown path
  both quit and idle-timeout share, writing `<state>/kule/dur` first (the
  same stop-file `calis()`'s own loop already checks), waiting up to 5s,
  then `.Kill()` — any `claude`/`codex` child kule already spawned is
  intentionally left running either way, exactly as part 1 documents.
  `BEYIN_KULE=off` skips spawning it entirely. New routes, none of them
  touching the panel's SSE/`$script:ActiveOperation` slot (kule already runs
  its own multi-lane worker): `GET /api/kule` (durum.json verbatim + a new
  `vscode: {bulundu, yol}` probe result + `calisiyor`), `GET /api/kule/is?id=`
  (job record + last 60 log lines, `id` regex-validated before any file
  read), `GET /api/kule/diff?id=&n=` (stored diff text as `text/plain`, `id`
  and `n` validated before the path — which is always read from the job
  record's own `diffler[n].diff_yol`, never assembled from the query
  string), `POST /api/action/kule-is-ver` (`tur`/`model`/`prompt`/`cwd?`/
  `izlenen?`/`izin?`, prompt piped to `kule.py is-ver --stdin --json
  --kaynak panel` over stdin — bypassing the A7 nezaket gate the same way
  Kaydet does, since kule already stamps `nezaket_del: true`), `POST
  /api/action/kule-onayla`/`-reddet`/`-iptal` (`{id}`), and `POST
  /api/action/kule-vscode` (`{id, n}` — B3's VS Code bridge: if `Find-VsCode`
  found `code` (probed `PATH`, then `%LOCALAPPDATA%`, then `%ProgramFiles%`,
  cached for the session) it runs `code --diff <once> <sonra>` with both
  paths read from the job record; otherwise `409 vscode_yok` and the panel's
  existing stored-diff-text fallback (`GET /api/kule/diff`) covers it — VS
  Code installation stays the owner's decision, never the panel's). New
  "Kokpit" section in `gui/panel.html`: a lane-meter/count-per-status row, an
  "İş ver" form whose submit-disable is local state deliberately **not**
  wired into `setActive()` (not an SSE operation), and a last-20 job list
  with per-row Log/İptal/Diff/Onayla/Reddet buttons — polled every 5s with a
  stacking guard flag, `textContent`-only throughout. B4's ISTEK-defteri
  requirement is satisfied by linking to the *existing* Pasaport blind-spot
  card rather than duplicating it. New static contract tests
  (`scripts/tests/test_panel.py`) plus a real-subprocess test that `kule.py
  is-ver --stdin --json --kaynak panel` prints the `KULE-SONUC` marker line
  the panel parses. See `docs/kokpit.md`.

<!-- yazan: claude · sonnet -->
- **F4 "Bağlam Pasaportu" (Context passport), part 1 — C1 packager + C4
  ISTEK ledger**: a hand-carried memory channel for consumer web chats
  (ChatGPT/Gemini/claude.ai) that have no local tool access at all —
  "gidiş paketi" (outbound package) via the clipboard, gone/come back
  through a human copy-paste. `scripts/context_pack.py` gains `--pasaport`
  (every existing flag and old-mode output are unchanged when it is
  absent): a package is `[ODENA-PAKET id:<12-hex> n:<seq> ts:<iso>]`, then
  the same root-map+notes material `compose_context` already builds, then a
  cumulative `[ODENA-MANIFEST slug:hash …]` of every note sent so far under
  this paket-id, then fixed Turkish footer instructions (no code fences —
  copy-pasting through a web UI can swallow them) telling the model to
  answer with only what's new, hand back an `[ODENA-DONUS]` block of new
  facts, or an `[ODENA-ISTEK]` block per missing item instead of guessing.
  Delta mode (`BEYIN_PASAPORT_DELTA_KARAKTER`, default 4000 chars) fits the
  root map and notes greedily — whole note or omitted, **never** cut
  mid-body, root map shrinks to headings before it's dropped, and a
  from-nothing-fits fallback sends the first note's first 1500 characters
  with a `[…kesildi]` marker; `--zip` sends the same material whole,
  unbudgeted ("Delta = speed, ZIP = completeness"). `--ek --id <id>` builds
  a follow-up package that excludes every already-sent note (read from the
  cumulative manifest, not just the latest package) and increments `n`; an
  unknown or missing `--id` fails with `pasaport-paket-bilinmiyor`. New
  `scripts/pasaport_defteri.py` (`<state-dir>/pasaport-defteri.json`,
  atomic-write-plus-lock, `BEYIN_PASAPORT_DEFTER_TAVAN` retention, default
  200) is the ISTEK ledger — the blind-spot map of what a web model asked
  for that the brain couldn't supply. It records package sizes, note slugs
  and content-hashes, and (since part 2, below) `[ODENA-DONUS]`
  outcome status/size and `[ODENA-ISTEK]` line items, but **never** a note
  body or a returned answer's text; `defter_md()` renders the recent
  packages plus the aggregated ISTEK list, most-frequent-first. CLI:
  `pasaport_defteri.py durum|goster <id>|istekler`. See `docs/pasaport.md`.

<!-- yazan: claude · sonnet -->
- **F4 "Bağlam Pasaportu", part 2 — clipboard listener, gate pipeline, panel
  approval**: the return half of C1. New `scripts/pasaport_kapi.py` (C3,
  pure/Linux-testable) parses a pasted `[ODENA-DONUS]`/`[ODENA-ISTEK]` reply
  (same "exactly one BEGIN/END, END after BEGIN, else refuse" precedent as
  `context_bridge._splice`; `pasaport-blok-cift|yarim|ters`,
  `pasaport-id-uyusmaz`; code fences and marker-line whitespace tolerated)
  and gates a DONUS body through: id-known-in-ledger check
  (`pasaport-paket-bilinmiyor`), a size cap
  (`BEYIN_PASAPORT_DONUS_MAX_KARAKTER`, default 12000 —
  `pasaport-donus-cok-uzun`), `secret_guard.redact`, a `DIRECTIVE_SHAPED`
  quarantine (`<state-dir>/karantina/pasaport-<ts>.md`,
  `pasaport-karantina`), and a new **manifest-dedup** step — DONUS bullets
  whose normalised fingerprint exactly matches or is >= 0.9 token-Jaccard
  similar to a line/paragraph of a note already in this paket-id's manifest
  are dropped (all-dropped is `pasaport-donus-bos`, nothing written); a
  manifest note that is missing or whose content no longer matches its
  recorded hash is skipped for dedup and reported as `manifest-bayat`
  rather than risking a false drop. What survives is wrapped with a
  `> dogrulanmamis: web dönüşü, kaynak: <id>` line and held as the single
  `<state-dir>/pasaport-bekleyen.json` pending candidate — never written to
  the vault until a `raw_hash`-checked `onayla` (approve) or `reddet`
  (reject); a newer paste replacing an unresolved candidate is reported as
  `pasaport-bekleyen-degisti`, never silently dropped, and a stale
  `raw_hash` refuses (`pasaport-bekleyen-uyusmaz`) rather than acting on the
  wrong text. Approval writes the daily log with a `source:pasaport`
  session anchor (`retrieve.SESSION_SOURCES` gains `"pasaport"`), records
  `kabul` in the C4 ledger, and spawns `compile.py --nezaket-del` by reusing
  `kaydet._spawn_compile` directly; ISTEK items are recorded independently
  of the DONUS block's fate and never touch the daily log. New
  `scripts/pano_izleyici.py` (C2, Windows-only at runtime) is a
  message-only window that registers `AddClipboardFormatListener` and reads
  only `CF_UNICODETEXT` (`OpenClipboard` retried for `rdpclip`) on
  `WM_CLIPBOARDUPDATE` — no polling loop, `GetClipboardSequenceNumber`
  cross-checked to skip unrelated clipboard changes — dispatching relevant
  text to `pasaport_kapi.isle_metin` in-process and writing a
  `pano-izleyici.json` heartbeat; `--once` supports manual testing and the
  panel's fallback button; off Windows it exits 2. `beyin.ps1` spawns it
  hidden as its own child when the panel starts
  (`BEYIN_PASAPORT_IZLEYICI=off` disables it) and stops it
  (`CloseMainWindow` then `Kill`) in the single shutdown path both quit and
  idle-timeout share — never restarted if it exits on its own. The panel's
  new "Pasaport" card (`GET /api/pasaport`, polled 5s) shows the listener's
  state, the pending candidate read-only with a short `raw_hash`, and the
  ISTEK blind-spot map; "Onayla → günlüğe" streams through SSE under the
  usual 409-before-anything-starts rule, "Reddet" runs synchronously (no
  compile spawn), and "Panodan al" runs `pano_izleyici.py --once`. See
  `docs/pasaport.md`.

<!-- yazan: claude · sonnet -->
- **F4 "Bağlam Pasaportu", part 2 — security review fixes**: four issues
  from an independent review, fixed on top of the part-2 pipeline above.
  (1) `isle_metin` now caps the WHOLE pasted reply
  (`BEYIN_PASAPORT_GIRDI_MAX_KARAKTER`, default 60000 —
  `pasaport-girdi-cok-uzun`) *before* `ayristir` ever parses it, and ODENA-
  ISTEK items are capped to `BEYIN_PASAPORT_ISTEK_MAX_MADDE` (default 20) of
  at most 300 characters each (`…`-truncated, `pasaport-istek-kirpildi`
  warning) — previously an ISTEK block was unbounded straight into the
  ledger. (2) Every surviving DONUS unit has `<!--`/`-->` escaped to
  `&lt;!--`/`--&gt;` and any forged `> dogrulanmamis:` line stripped before
  the daily-log write, closing an anchor-forgery hole where a crafted
  `<!-- session:x ts:y source:claude -->` in a returned reply would have
  been read by `retrieve.parse_session_anchors`/`compile.carry_source_anchors`
  as a real, trusted session anchor and carried into a concept note. (3)
  `context_pack.py` now substitutes ``` with `'''` in every note body before
  it enters an outbound package (a footer note is added when it does),
  keeping the composed package itself free of code fences while still
  hashing the *original* body for manifest-dedup so a later package's
  comparison against `retrieve.read_concept` still matches. (4) `onayla` is
  now idempotent against a crash between the daily-log write and deleting
  the pending file: the candidate is marked `uygulaniyor` atomically before
  the write, and the ledger's `kabul` entry now carries `raw_hash`
  (`pasaport_defteri.Defter.donus_kaydet` gains the field) so a retry that
  finds the marker already set can tell "already written, just clean up"
  from "crashed before the write, retry it" — the daily log is written at
  most once per approval either way. Also: `pano_izleyici.py` calls
  `KillTimer` on `WM_DESTROY`, and `beyin.ps1` validates `raw_hash` as
  exactly 64 lowercase hex characters before use in both the approve and
  reject routes (`bad_raw_hash`, 400). See `docs/pasaport.md`.

<!-- yazan: claude · sonnet -->
- **F3 "Kaydet" (Save — the golden path)**: `scripts/kaydet.py` is the
  panel's "Kaydet" action and its CLI equivalent — "flush + compile, local,
  now, like saving a project" — for **zero model tokens** spent on the note
  itself. Text (positional argument, `--stdin`, or `--dosya PATH`; exactly
  one source) is redacted (`secret_guard`), checked for directive-shaped
  content (the same `DIRECTIVE_SHAPED` pattern `flush.py`/`compile.py` use —
  a hit quarantines the note through compile's own `_quarantine_content`
  convention instead of writing it), then appended straight to today's
  daily log with a `source:kaydet` session anchor
  (`retrieve.SESSION_SOURCES` gains `"kaydet"`), no model call, no prompt.
  `compile.py --nezaket-del` then runs to completion immediately —
  bypassing the A7 nezaket gate on purpose: an explicit user action is its
  own permission. Kaydet's exit code answers only "was the note written?";
  a compile timeout (`BEYIN_KAYDET_DERLEME_ZAMAN_ASIMI`, default 900s) or
  failure is reported through health and the JSON result's `derleme` field
  without changing it. `flush._append_daily` gains a short shared
  `daily-append.lock` around its exists-check-then-append sequence, closing
  a pre-existing race between any two writers of the same daily file (a
  hook flush and Kaydet, or two hook sessions) that the per-session lock
  never covered — output bytes for a single writer are unchanged. The panel
  gains a Kaydet card (textarea, optional title, "Kaydet" button) above the
  tabs; `POST /api/action/kaydet` feeds the note over **stdin**, never
  argv, and streams like every other operation. See `docs/kaydet.md`.

<!-- yazan: claude · sonnet -->
- **F2 "A7 nezaket" (politeness layer)**: `scripts/nezaket.py` defers
  `compile.py`, `watcher.py`, and `ingest.py` runs while Master Mind is
  actively using the machine, instead of letting a background model call
  fight Unreal/Blender/OBS/a fullscreen game for GPU and CPU. Windows-only
  probes (foreground process and its parent, GPU utilization via
  `nvidia-smi`, fullscreen state, idle time) feed a pure decision function;
  every probe fails open to "unknown" rather than raising, and the gate is a
  complete no-op on non-Windows platforms and when `BEYIN_NEZAKET=off`. A
  busy verdict queues the deferred call in `nezaket-kuyruk.json` and the
  entrypoint exits `75` (`EX_TEMPFAIL`) without spawning anything; release is
  **explicit only** — a queued call runs solely when someone approves it by
  id (`nezaket.py serbest <id>` or the panel), never on a timer or a night
  window. `nezaket-izin.json` (allow-listed processes, parent launchers, GPU
  threshold, harmless-fullscreen list, idle-release window) has built-in
  defaults and fails loud on a malformed file rather than silently falling
  back. On the busy transition, `vram_bosalt()` asks Ollama to drop every
  loaded model's VRAM residency, and the Ollama runner adds
  `keep_alive: 0` to requests made while busy (`BEYIN_OLLAMA_KEEP_ALIVE`
  passes through unchanged when not busy; omitted and byte-identical to
  before when neither applies). Child model-runner processes (`claude`,
  `codex`) start at Windows idle priority, and `compile.py` asks Windows to
  schedule itself in background mode. The panel gains a Nezaket card:
  current decision, the deferred queue with per-row checkboxes, oldest
  waiting time, and a "Seçilenleri çalıştır" button — polled every 10 s, no
  new persistent process. See `docs/nezaket.md`.

<!-- yazan: claude · fable-5 -->
- The Ollama runner accepts `BEYIN_OLLAMA_NUM_CTX` and forwards it as
  `options.num_ctx`. Root cause of a three-times-failed local flush (session 44):
  Ollama truncates input past its default context window silently, so a long
  transcript lost the schema instruction and came back `summary-schema-invalid`;
  with `num_ctx=16384` the same transcript passed on the fourth attempt. Unset
  keeps the request byte-identical to before.

<!-- yazan: claude · fable-5 -->
- Text-mode compilation gains a link-richness instruction (BAGLANTI ZENGINLIGI):
  aim for three-plus wikilinks per concept, drawn from the provided root-map and
  duplicate-check registry lists, without padding the body. Measured on the
  A4 gate with a local 30B backend: 2.30 -> 3.30 links/concept, zero gate
  violations. Asking for four-plus destabilised the local model into
  truncations, so three is the deliberate ceiling until the runner's output
  budget is raised. Tool-mode prompt is byte-identical.
- A flush running on a local backend no longer leaks its backend choice into
  the compiler it triggers: the compile spawn pins BEYIN_MODEL_BACKEND to the
  sealed default.

<!-- yazan: claude · fable-5 -->
- The retrieval hook sheds its PowerShell wrapper: `retrieve.py hook` reads the
  Claude Code hook JSON from stdin itself (same skip rules, byte-identical
  injection) and the query path stops importing `sema`/`rootmap`/`shutil`/
  `tempfile` eagerly. Measured on the live machine: full-chain p95 443 ms with
  the wrapper, 112-127 ms direct. The wrapper stays as a fallback.

<!-- yazan: claude · fable-5 -->
- Recency no longer multiplies the fused score. The old post-fusion multiplier
  was scale-blind (a 1.6% band multiplied by up to 4x), so freshness could bury
  a strong match. Recency now contributes only as an RRF channel with a single
  pre-declared weight (`BEYIN_RRF_RECENCY_CHANNEL_WEIGHT`); the former
  behaviour stays reachable for comparison runs via
  `BEYIN_RRF_LEGACY_MULTIPLIER=1`.
- Sealed the retrieval default after a 125-question gold-set run: `bm25`
  recall@3 83.2% vs `rrf` 69.6% (McNemar p=0.003). `bm25` stays the default
  and the fused path's default candidacy is closed until a redesign measures
  better on the same set. The verdict and the reproduction command live in
  `docs/retrieval.md` §7.
- The Ollama runner now sends `think: false` and a `num_predict` cap
  (`BEYIN_OLLAMA_THINK` / `BEYIN_OLLAMA_NUM_PREDICT`): qwen3's thinking mode is
  on by default and was consuming the whole token budget before any answer.

### Added

<!-- yazan: claude · fable-5 -->
- Three measurement tools, all stdlib-only: `tools/olc_baslangic.py`
  (cold-start breakdown of the retrieval hook), `tools/tr_beir_kos.py`
  (SciFact BEIR anchor, EN and TR legs), `tools/a4_kapi.py` (the two-mode
  compile-gate harness and its report table).

### Fixed

<!-- yazan: claude · fable-5 -->
- **`_atomic_write_json` temp names were pid-only, so the tower's lane
  threads inside one process could collide on the same temp file — a silent
  state-corruption risk on every OS, not just Windows.** CI's 3.13 leg
  caught it as a `LaneCapTests` failure. Temp names now carry
  pid + thread id + a monotonic counter, `os.replace` retries for a bounded
  2 s window (Windows can hold the target briefly), and an 8-thread hammer
  test pins the fix.

<!-- yazan: claude · fable-5 -->
- CI's under-temp refusal test failed on GitHub runners whose `%TEMP%` is an
  8.3 short path (`RUNNERADMIN~1`): the mocked temp root was never resolved,
  so the guard compared a short path against a long one. The mock now
  `.resolve()`s its root, matching what the production guard already did.

<!-- yazan: claude · fable-5 -->
- **First real Windows run of the F2–F5 suite (built on Linux) found and fixed
  three Windows-only defects.** `kule.py`'s cwd guard treated
  `os.path.commonpath`'s different-drives `ValueError` as "invalid cwd", so
  with `%TEMP%` on `C:` every job whose cwd sat on another drive was refused
  (`kule-cwd-gecersiz`); a cwd on another drive cannot be under the temp root,
  so it is now valid. The panel's kule-is-ver handler checked only HTTP 200 and
  painted a refused job (`{olusturuldu:false}`) as "İş oluşturuldu: ?" while
  wiping the prompt; it now surfaces the refusal slug. And
  `kaydet._kill_process_tree` handed a test fake's made-up pid to a live
  `taskkill /F` on win32 — `taskkill` is now reserved for real
  `subprocess.Popen` objects, which also lets the behavioural timeout/cancel
  tests observe the kill on Windows itself. The deletion-scan guard in
  `test_panel.py` no longer misreads the hyphenated Turkish flag
  `--nezaket-del` (delmek — to pierce) as a `del` command. Suite on Windows:
  5 failed → 816 passed.

<!-- yazan: claude · opus-5 -->
- `kur.ps1` handed `model_oneri.py` the hardware probe as a native command
  argument. Windows PowerShell re-splits the quotes inside a JSON blob, so
  argparse saw garbage and exited 2 — reproduced here with a 504-character
  probe before anything was changed. The probe now travels through a temporary
  file that is removed afterwards, and `model_oneri.py` gained `--probe-file`
  beside the existing `--probe-json`. This surfaced while chasing an install
  failure on someone else's machine; it is a real defect and a plausible cause,
  but that log has not been read, so it is not claimed as *the* cause.

## [0.4.1] - 2026-08-28

### Added

<!-- yazan: claude · opus-5 -->
- **Local models tab** in the panel. Backend independence was built but not
  usable: switching to a local model meant editing an environment variable in a
  terminal. The tab shows the machine's hardware and the ranked recommendations
  from `donanim.py` and `model_oneri.py`, the installed inventory from Ollama's
  own `/api/tags`, and which backend the next pipeline run will actually use.
  Three actions, each confirming first: **pull** streams Ollama's real completed
  and total bytes and refuses up front with a number when the disk cannot hold
  the model; **switch** names the exact `BEYIN_MODEL_BACKEND` value and the
  exact place it will be stored before writing it, because nothing a live
  pipeline reads may appear behind the owner's back; **try** sends one fixed
  prompt through the existing runners and reports the answer, model and latency.
  When Ollama is unreachable the tab says so rather than showing an empty
  inventory that reads like a measurement. Model deletion is deliberately
  absent — the panel deletes nothing.
- **`Setup.cmd` and `Local Brain.cmd`** at the repository root, so someone who
  downloaded the source zip can double-click instead of knowing to run a
  PowerShell script. `Setup.cmd` starts the graphical wizard and falls back to
  the terminal one; both only launch what already exists.
- The built installer is now **attached to the release** as
  `OriginOfMemory-Setup-<version>.exe`, with its SHA256 in the release notes —
  an unsigned binary people are asked to double-click should at least be
  verifiable.

## [0.4.0] - 2026-08-28

### Added

<!-- yazan: claude · opus-5 -->
- **A real Windows installer** (`installer/origin-of-memory.iss`, built with Inno
  Setup). The familiar setup window with Back / Next / Cancel, an Add/Remove
  Programs entry and a desktop shortcut. Per-user: it never asks for
  administrator rights and installs nothing system-wide. `kur.ps1` stays the
  installation authority — the wizard collects answers, writes a plan and hands
  it over. Detection is the existing code, not a reimplementation. The vault
  default deliberately avoids a OneDrive-redirected Documents folder, and the
  Ready page itemises exactly what will happen before anything does. The output
  is **unsigned**, so Windows will show a SmartScreen prompt; the wizard never
  tries to bypass it. See [`docs/installer.md`](docs/installer.md).
- **Local Brain, the operations panel** (`beyin.ps1`, `gui/panel.html`,
  `LocalBrain.exe`). A window that shows whether the system is alive and runs
  its operations, opened from a shortcut rather than a terminal. Health is the
  live form of what `beyin doktor` reports, read from `durum.py --json` because
  that contract was already documented as stable for exactly this; Today shows
  the day's sessions, the last flush and the last compile. Four operations —
  doctor, compile, index rebuild, watcher sweep — each confirming first and
  streaming over SSE, so closing the browser drops the view and not the work.
  **Nothing in the panel deletes anything**, and a test greps the server for
  file-removal primitives so that guarantee cannot rot quietly. The launcher is
  a console-free C# binary built with the inbox `csc.exe`; its icon is an
  original placeholder, deliberately not anyone else's mark. See
  [`docs/panel.md`](docs/panel.md).

### Added

<!-- yazan: claude · opus-5 -->
- **Graphical setup wizard, phase one** (`kur-gui.ps1`, `gui/kur.html`). The
  terminal wizard is hard to follow, so the setup surface is moving to a
  browser page driven by a loopback server. The server is PowerShell rather
  than Python because the wizard's own job includes installing Python — a
  wizard that needs Python cannot run on the machine it is meant to fix. A raw
  `TcpListener` on `127.0.0.1` binds unprivileged; `HttpListener` was avoided
  because HTTP.sys URL reservations can demand elevation elsewhere. A
  single-use 256-bit token travels in the URL fragment, so it never reaches the
  server or a log, and is exchanged once for a `SameSite=Strict; HttpOnly`
  cookie; every API call must also carry an exact `Host` and `Origin`. The page
  makes no external request of any kind. Detection is not reimplemented — the
  System Check screen calls what `kur.ps1` already probes — and progress rides
  a sequence-numbered SSE stream with replay, so closing the browser drops the
  event stream without killing the work. **Installation is not yet driven by
  the UI**; see [`docs/gui-wizard.md`](docs/gui-wizard.md) for what phase two
  must add.

## [0.3.0] - 2026-08-28

### Fixed

<!-- yazan: claude · opus-5 -->
- A settle-window check shared by `ingest_claude`, `ingest_codex` and the
  watcher skipped files whose mtime landed a fraction ahead of the clock, even
  when the window was zero and therefore meant "disabled". A skipped candidate
  is never classified, so its rejection vanished and the suite failed
  intermittently while passing in isolation. The window must now be positive to
  filter anything.

### Added

<!-- yazan: codex · gpt-5.6-sol -->
- **Hookless transcript watcher** (`scripts/watcher.py`). Session capture was the
  last memory surface that required Claude Code lifecycle hooks; a quiet
  15-minute scanner now reuses the Claude/Codex ingest adapters, the shared
  summarisation path, and `ingest-state.json` watermarks, so the hook becomes a
  latency optimisation instead of a dependency. A minimal named-folder adapter
  accepts finalized `.md` and `.jsonl` sessions. The watcher holds the existing
  per-session flush lock and rechecks canonical daily anchors before every model
  call, allowing hook and watcher capture to coexist without duplicate records.
  Antigravity capture remains explicitly unimplemented because its official
  documentation establishes only a local conversation-ID cache, not an on-disk
  transcript layout; guessing a path would turn data loss into a silent success.
  No scheduler is registered—the owner still controls that operating-system
  decision. See [`docs/watcher.md`](docs/watcher.md).

<!-- yazan: odena · claude-opus-5 -->
- **Context bridge** (`scripts/context_bridge.py`). Per-message injection needs
  a prompt hook and only some hosts offer one; the bridge closes most of that
  gap from the other side. After a successful compile the root map is written
  into a delimited block inside `AGENTS.md`, `GEMINI.md`, and `CLAUDE.md` at the
  vault root, so an agent that never calls a hook still sees what the knowledge
  base holds and how to search it. A file that does not exist is **never
  created** — its existence is the user's consent — and nothing outside the
  markers is ever rewritten. A file whose markers are damaged is left completely
  untouched and reported, because half a marker means a human edited the block
  and guessing where it ended would destroy their text. Identical content is not
  rewritten, headings are demoted one level so the map never outranks its host,
  and the block is secret-scanned before any write. Toggle with
  `BEYIN_CONTEXT_BRIDGE=off`. See [`docs/context-bridge.md`](docs/context-bridge.md).
- **Tool-free compile mode**, behind `BEYIN_COMPILE_MODE=text`
  (`scripts/compile_text.py`). The model returns a delimited file transcript and
  this project writes the staging tree itself, so compile stops being the one
  call that needs `--tools` and `--permission-mode` — and stops being the one
  call only `claude` can serve. Everything downstream is unchanged: the same
  manifest diff, path allowlist, directive quarantine, secret guard, schema gate
  and atomic promotion audit the result, and a parity test proves both modes
  promote identical bytes from identical model output. Refusals are proven by
  mutation testing: forbidden paths, traversal, absolute paths, truncated
  blocks, duplicates, oversized output and leaked credentials each fail the run
  or drop the block. **The default is still `tools`** and stays there until the
  measured gate in the v0.6 plan has run against a real model. See
  [`docs/tool-free-compile.md`](docs/tool-free-compile.md).

### Changed

<!-- yazan: codex · gpt-5.6-sol -->
- Archive ingest now carries canonical session anchors for Claude Code archive,
  Codex rollout and claude.ai web sessions, so imported provenance and recency
  no longer collapse to note frontmatter when the source supplies a genuine
  session ID. Gemini stays deliberately anchors-free because its Takeout-derived
  day chunks have no genuine session identity to claim.
- The compiler snapshots pre-call concept-note anchors and restores only those
  a model rewrite removed before promotion, so event provenance no longer
  depends on the model obeying a preservation instruction; post-call order and
  model-added anchors remain untouched.

- `docs/compatibility.md` now states which memory surface each host actually
  provides, and separates "the only backend that can compile" into "the only one
  that can compile *in tool mode*".

## [0.2.0] - 2026-08-28

### Added

<!-- yazan: claude · opus-5 -->
- **Frontmatter schema gate** at the compiler's promotion path
  (`scripts/sema.py`). The compiler's prompt always described the concept-note
  schema; nothing enforced it, so a note with broken frontmatter entered the
  index with its title degraded to the filename and empty tags. A staged note
  that fails validation is now **not promoted** — it is routed to
  `.stage/karantina/sema/` with a sidecar naming the problems, health records
  `schema-invalid:<file>`, and its clean siblings in the same run still promote.
  Nothing is ever auto-repaired: inventing a missing `created` date would file a
  fabricated fact as permanent memory. The gate stops **new** damage only —
  `retrieve.build_index` and `rootmap` keep their tolerant behaviour, so an
  existing imperfect corpus keeps indexing and keeps being retrieved.
- `beyin doktor` now surveys the live corpus read-only: `retrieve.py verify`
  reports `schema_checked`, `schema_invalid_count` and up to five offending
  notes with their problems. The survey never affects `ok` and never modifies or
  blocks anything — it is a census of what predates the gate.
- **Per-call accounting** in `.state/calls.jsonl`, appended by
  `claude_runner.run_claude()` — the single choke point every model call already
  passes through. One line per call: timestamp, backend, model tier and resolved
  slug, component, character counts, chars ÷ 4 token estimates (`_est` in the
  field name, because that is what they are), duration and outcome. **No prompt
  or response content** — `record_call()` is handed counts rather than strings,
  so content has no path into the file, and a test asserts the field set never
  grows. Append-only, capped at 5 MB, rotated keeping the newest lines.
- `python scripts/durum.py` gained a call-accounting section: the last 7 days
  per backend with median and p95 duration, and estimated tokens per component.
  `--json` adds a `calls` key alongside the unchanged `rows` contract. See
  "Measuring your own setup" in [docs/local-models.md](docs/local-models.md) for
  how to compare backends with it, and why the token figure skews low on
  Turkish text.

- Opt-in **hybrid retrieval** (`BEYIN_RETRIEVAL=rrf`): reciprocal rank fusion
  over BM25, tag/alias overlap and recency, with a bounded recency multiplier.
  Default stays `bm25` until the fused path is measured against the gold set —
  the synthetic benchmark suggests it may exceed the 500 ms injection gate on a
  real corpus, and two open ranking questions are documented rather than tuned
  away. See [docs/retrieval.md](docs/retrieval.md).
- **Session anchors**: each flushed daily block carries
  `<!-- session:<id> ts:<ISO8601> -->`, the compiler carries it into the concept
  note's sources, and retrieval strips anchors before injection — so a compiled
  claim can be traced back to the session that produced it.
- **Compile hygiene**: content-hash skip for unchanged daily logs, an index
  rebuild gate keyed on a concepts manifest hash, and a minimum-interval gate on
  the nightly trigger. Skips are recorded in health state as skips, never as
  errors.
- **Epistemic-status preservation** in the compiler prompt: hedged statements
  keep their hedge and date, and a new claim that contradicts an existing note
  is recorded as an explicit conflict line instead of silently overwriting it.
- MCP tools now carry behaviour annotations (read-only, non-destructive,
  idempotent, closed-world) so clients can judge them without guessing.
- `beyin doktor` gained an index-consistency check: what the FTS index should
  contain, recomputed from `knowledge/concepts/` and diffed against `notes.db`.

<!-- yazan: claude · opus-5 -->
- `python scripts/durum.py [--json]` — a one-table health summary (component,
  last status, last run, last error/skip, quarantine count) built from
  `health.json`, `ingest-health.json`, `compile-state.json` and
  `last-flush.json`, always exiting 0, with the JSON shape documented as a stable
  contract for the future TUI health tab.
- CI now runs the suite across Python 3.12 and 3.13 on `windows-latest`, and a
  new [docs/compatibility.md](docs/compatibility.md) states the external CLI flag
  surface and HTTP endpoints this was built against — and that anything beyond
  them is untested.
- `beyin doktor` gained a quarantine check reporting the count and newest entry,
  red when non-empty, with the manual release steps.

<!-- yazan: codex · gpt-5.6-sol -->
- Two-screen, one-Enter recommended setup with deterministic JSON planning,
  `-Recommended` agent automation, visible custom defaults, and seven-step
  maximum custom flow.
- Multi-runtime wizard support for Ollama, LM Studio, llama.cpp, and vLLM,
  including OpenAI-compatible endpoint wiring and consent-safe model handling.

<!-- yazan: codex · gpt-5.6-sol -->
- Fault-tolerant Windows hardware probe (`scripts/donanim.py`) and verified-tag
  Ollama fit recommender (`scripts/model_oneri.py`), with interactive wizard
  guidance, explicit non-interactive install/pull plan fields, disk preflight,
  and dry-run isolation.
- Safe `uninstall.ps1` for exact hook/MCP cleanup and separately approved copied
  files, with per-file backups and an explicit vault-memory preservation rule.

<!-- yazan: codex · gpt-5.6-sol -->
- Interview-first PowerShell 5.1 setup wizard (`kur.ps1`) with strict JSON plans,
  cloud/hybrid/local/lite presets, dry-run-safe user environment actions,
  selected-skill installs, optional non-clobbering Claude Desktop MCP merge,
  and preset-specific verification/next-step output.
- `install.ps1 -SkillFilter` for selected skills while preserving the default
  all-skills standalone behavior; lite wizard runs skip hook registration.
- Backend-aware Gemini ingest bounds for local endpoints: 24,000 characters by
  default or `BEYIN_FLUSH_CHUNK_CHARS`, while Claude, Antigravity, and Codex
  retain the existing full-day payload.

<!-- yazan: codex · gpt-5.6-sol -->
- Optional OpenAI-compatible local backend for LM Studio, llama.cpp
  `llama-server`, vLLM, and similar chat endpoints. It uses stdlib `urllib`,
  requires an explicit URL and fast-model slug, supports an optional Bearer
  token, and preserves distinct connection, HTTP, timeout, and response errors.
- Backend-aware live-flush chunking: Ollama and OpenAI-compatible runs default to
  24,000 transcript characters, `BEYIN_FLUSH_CHUNK_CHARS` overrides any backend,
  and the effective bound is recorded in flush state detail.
- Local model selection, rough hardware tiers, and context guidance in
  `docs/local-models.md`.

- Local **MCP memory server** (`scripts/mcp_server.py`, stdlib-only JSON-RPC
  over stdio): `memory_search` and `memory_root_map` tools plus root-map and
  hub resources, read-only, dual protocol era (`2026-07-28` and the legacy
  `initialize` handshake shipping desktop clients still speak). Registration
  and caveats: `docs/mcp.md`.
<!-- yazan: codex · gpt-5.6-sol -->
- Optional local **Ollama backend** for text-mode flush and ingest calls,
  selected with `BEYIN_MODEL_BACKEND=ollama`. It posts non-streaming generate
  requests through stdlib `urllib`, requires an explicit fast model slug, maps
  transport/protocol failures to stable health strings, and falls back to
  `claude` for compile when available while refusing tool mode otherwise.
- Manual clipboard context bridge: `scripts/context_pack.py` composes the root
  map plus capped BM25 notes and can send UTF-16LE text to `clip.exe`;
  `hooks/pano-kopru.ps1` is an unregistered PowerShell 5.1 wrapper for manual
  use or a user-defined shortcut.
- Optional **Antigravity CLI (`agy`) backend** for the background model calls,
  selected with `BEYIN_MODEL_BACKEND=antigravity`. Default behaviour is
  unchanged: with the variable unset every call still goes through `claude -p`
  with byte-identical arguments. `BEYIN_MODEL_BACKEND=gemini` is accepted as a
  deprecated alias and warns (Google retired Gemini CLI's serving on
  2026-06-18; `agy` is the successor).
  - New `scripts/agy_runner.py` runs the documented headless contract
    `agy -p <prompt> --model <slug> --output-format text` with stdin closed,
    the same timeout and out-of-vault temporary-directory discipline as the
    Claude path, and `BEYIN_INVOKED_BY` still set.
  - Binary resolution: `agy` by default, `BEYIN_AGY_BIN` to override, with the
    fixed `cmd.exe /d /s /c` bridge for Windows `.cmd`/`.bat` shims.
  - Model mapping: `haiku` → `BEYIN_AGY_MODEL_FAST` (default
    `gemini-3.5-flash-medium`, the only slug the official docs show);
    `sonnet` → `BEYIN_AGY_MODEL_SMART`, which has no default and degrades to
    the fast model with a `warn:agy-smart-model-unset:…` entry in health state.
  - Distinct failure strings propagated into health state: `agy-missing`,
    `agy-auth-missing` (best-effort stderr sniffing), `agy-exec-error`,
    `agy-timeout`.
  - **Compile is refused** on this backend with
    `antigravity-backend-unsupported:compile`. Compile is the only tool-mode
    call and `agy` offers no per-invocation permission scoping — only a
    user-global allow-list or `--dangerously-skip-permissions`, which this
    repository does not ship. In `antigravity` mode compile keeps using
    `claude` when that binary is on `PATH` and fails loud otherwise.

### Changed

<!-- yazan: claude · opus-5 -->
- The duplicate-check registry is now bounded instead of one row per concept for
  the whole corpus: hub-scoped to the daily log's topics plus the
  `BEYIN_REGISTRY_RECENT` (default 50) most recently updated concepts, hard-capped
  at `BEYIN_REGISTRY_MAX_ROWS` (default 400), with a one-line truncation notice in
  the prompt and a `warn:registry-truncated:<shown>/<total>` health warning —
  67,800 → 15,806 characters on a synthetic 1000-concept corpus.
- `write_health`, `write_health_skip`, `_atomic_write_json`, `_lock_exclusive` and
  `_sha256` now have one implementation in the new `scripts/beyin_ortak.py`, which
  `flush.py`, `compile.py`, `rootmap.py`, `ingest_common.py` and `retrieve.py`
  import; the module-level names they bind keep `flush._sha256`-style access
  working and are asserted to be the same object.
- Model-call timeouts are configurable and backend-aware: `BEYIN_FLUSH_TIMEOUT`,
  `BEYIN_INGEST_TIMEOUT` (both default 240 s, raised to 900 s when the resolved
  backend is `ollama` or `openai-compat`, because local inference is slow) and
  `BEYIN_COMPILE_TIMEOUT` (900 s). An unusable value is ignored with a
  `warn:timeout-invalid:<name>:<value>` health warning, the effective value is
  recorded in each component's state so a timeout is diagnosable, and the
  `claude`/`antigravity` defaults are unchanged. See
  [docs/local-models.md](docs/local-models.md).

### Security

<!-- yazan: claude · opus-5 -->
- Directive-shaped content is now **quarantined instead of noted**: a poisoned
  daily body is copied to `<vault>/.stage/karantina/` with a forensic sidecar and
  is not compiled, a poisoned root map or registry aborts the run with
  `PolicyError("directive-shaped-registry")`, and a poisoned model output is held
  back while its clean siblings still promote — each raising the health *error*
  `quarantine:directive-shaped`, with a documented, deliberately manual release
  path (see [SECURITY.md](SECURITY.md)).
- The compile lock now records `{machine, pid, started_at, hostname}` so a vault
  synced across machines no longer compiles twice: a live lock owned by another
  machine refuses with `skip:compile-locked-by:<machine>`, and a lock older than
  `BEYIN_COMPILE_LOCK_TTL_MIN` (default 120) is broken with a health warning
  naming the previous owner.

### Fixed

<!-- yazan: codex · gpt-5.6-sol -->
- Replaced `setx` persistence with the non-truncating .NET user environment API.
- Claude Desktop MCP registration now handles both standard and MSIX-virtualised
  config paths, backs up every edited file, and keeps dual configs in sync.

## [0.1.0] - 2026-08-27

First public release. This is the initial extraction of a working system into a
standalone repository, so everything is listed as added. The items below are
scoped as *what this project adds over the upstream
[avenoxbeyin v2](https://github.com/avenoxai/avenoxbeyin) base it derives from* —
see [docs/attribution.md](docs/attribution.md) for the lineage.

### Added

#### Native Windows port

- PowerShell hooks (`hooks/session-start.ps1`, `hooks/prompt-counter.ps1`,
  `hooks/memory-retrieve.ps1`, `hooks/flush-launch.ps1`, `hooks/session-end.ps1`)
  replacing the upstream bash hooks. No WSL, no POSIX shell.
- File locking falls back to `msvcrt` region locks where `fcntl` is unavailable,
  in both `flush.py` and `compile.py`.
- UTF-8 hardening throughout: hooks set `[Console]::OutputEncoding` to UTF-8
  without BOM, hook stdin is persisted as BOM-less UTF-8 for strict JSON parsing
  in Python, Python subprocesses are launched with `-X utf8`, and the installer
  writes `settings.json` with a BOM-less UTF-8 encoder.
- Detached background launch uses Windows `creationflags` (`DETACHED_PROCESS |
  CREATE_NO_WINDOW`) with a POSIX `start_new_session` fallback, so the flush hook
  returns in under a second and no console window flashes.
- `flush-launch.ps1` writes the hook payload to a state file and detaches
  `flush.py`, rather than blocking the session-end path.

#### User-level hook registration

- `install.ps1` registers all six hooks in `<user>\.claude\settings.json`, not in
  a project-scoped settings file. Memory is written **and read** from every
  project on the machine, not only from inside the vault folder.
- Registration is idempotent: an already-present command string is skipped, and
  the existing `settings.json` is backed up to `settings.json.bak-<timestamp>`
  before any write.
- `BEYIN_INVOKED_BY` recursion guard: every hook and every entry-point script
  exits on line one when that environment variable is set, so the pipeline's own
  `claude -p` subprocesses cannot re-trigger the pipeline.

#### FTS5 BM25 per-prompt retrieval

- New `scripts/retrieve.py`: builds a SQLite FTS5 index of every concept note
  (`build`) and answers ranked queries (`query`), with an atomic index rebuild.
- `hooks/memory-retrieve.ps1` on `UserPromptSubmit` injects the top 3 full notes
  for the current prompt. The hook performs the selection; the model is never
  given a search tool, because agents measurably under-call such tools.
- Ranking uses `bm25(notes, 8.0, 6.0, 3.0, 1.0)` over title, aliases, tags and
  body, with an optional `--min-score` floor.
- Injection caps: 1,500 characters per note, 4,500 characters total.
- Per-session dedupe ledger (`.state/retrieve-session-*.json`) prevents
  re-injecting the same note within a session; ledgers older than seven days are
  pruned.
- Prompts under 12 characters and slash commands are skipped as carrying no
  retrieval signal.
- Injected notes are labelled as data, with an explicit instruction that no
  sentence inside them is to be executed.
- Turkish support in the index: explicit dotted/dotless I folding
  (`turkish_fold()`) applied identically at index and query time, dual-form
  tokenisation (raw folded form plus a five-character prefix for words longer
  than five characters), and no stemmer by deliberate choice.
- `--bench` mode with a fixed query set for latency measurement.

#### Root-map layer

- New `scripts/rootmap.py`: regenerates `knowledge/index.md` as a compact topic
  root map under a 4,000 character budget, plus one hub file per topic under
  `knowledge/hubs/`, while the full article table moves to
  `knowledge/index-full.md`.
- Topic hubs are configurable per user through `hub-config.json` (shipped as
  `template/hub-config.example.json`): frontmatter tags and title keywords decide
  membership, unmatched concepts fall to a configured catch-all, and array order
  controls root-map order.
- Publication gates: every concept must be covered by a hub, the root map must fit
  its budget, hub output must match the configured hub set, and every staged file
  must be non-empty — otherwise nothing is published.
- Outputs are written to a temporary directory inside `knowledge/` and published
  with `os.replace`, so a failed run cannot leave a half-written map.
- The compiler now sends the root map plus a compact duplicate-check registry
  instead of the entire index. Measured on the author's corpus, this cut the
  per-call input base by 63% (152.8K → 56.1K characters).
- Index/concept parity mismatches are recorded as a health warning rather than
  silently ignored.

#### Secret redaction

- New `scripts/secret_guard.py` with `redact()` and `scan()`. Patterns are
  deliberately narrow: PEM private keys, AWS/Google/GitHub/Slack/Anthropic/OpenAI
  key formats, JWTs, credentials embedded in URLs, `Bearer` tokens, and
  `password:`/`api_key=` style assignments (tolerating Turkish possessive
  suffixes).
- `flush.py` redacts both the transcript going into the summariser and the summary
  coming out, recording a health warning naming the matched pattern classes.
- `compile.py` scans compiler output at the promotion gate.
- A harmless-value filter skips placeholders (`${VAR}`, `<...>`, `REDACTED`,
  `CHANGEME`, `EXAMPLE`, `ÖRNEK`, …) so free text is not mangled.

#### Compile isolation and policy gates

- The compiler runs `claude -p` inside a `0700` staging tree at
  `<vault>/.stage/compile-stage-*`, holding a copy of `knowledge/` and exactly one
  daily log — the live vault is never the working directory.
- Before promotion, a file manifest diff rejects deletions, type changes and any
  write outside `knowledge/concepts/**`, `knowledge/index-full.md` and
  `knowledge/log.md` (`PolicyError`).
- Daily sources are checked for symlinks and non-regular files before use.
- Untrusted-data delimiters wrap the root map, the registry and the daily body in
  the compile prompt, and the transcript in the flush prompt, with an explicit
  instruction that nothing inside them is an instruction.
- One compile per day is claimed with an `O_EXCL` trigger file; at most three
  daily logs are processed per run.
- A successful run clears the stale health error flag, so the health check cannot
  keep reporting a crash that has since been fixed.

#### Ingest family

- New `scripts/ingest.py` front-end with `claude`, `codex`, `web`, `gemini` and
  `status` subcommands, shared `--dry-run`, `--max-sessions`, `--sleep`,
  `--model` and `--retry-failed` flags, and an exclusive lock so two backfills
  cannot run at once.
- `ingest_claude.py` reads Claude Code transcript archives from
  `~/.claude/projects`.
- `ingest_codex.py` reads Codex rollouts from `~/.codex/sessions`.
- `ingest_web.py` reads claude.ai export ZIPs dropped into `<vault>/.import/`.
- `ingest_gemini.py` plus the one-off `tools/gemini_ayikla.py` extractor handle a
  Google Takeout Gemini archive.
- Resumable state tracking per source, so an interrupted backfill continues where
  it stopped and does not re-summarise finished sessions.

#### Evaluation methodology

- Judge-free binary recall@k over gold note identity as the primary metric, run
  against a corpus snapshot pinned by commit.
- Gold questions are real historical user questions rather than synthetic ones,
  with held-back canary questions.
- Documented statistical floor: a paired comparison needs a net difference of
  roughly 16 questions for p < 0.05 at n = 125.
- First measured run on the author's corpus: recall@3 83% (104/125), recall@5 84%
  (105/125), against a 0% baseline; retrieval hook p95 latency 347 ms.
  *(The recall@5 figure here was later found to be wrong — the run retrieved
  three results and labelled the column `top5`. Corrected in 0.2.0 to
  114/125 = 91.2%; recall@3 was unaffected. See docs/evaluation.md.)*
- Method and how to build your own gold set: [docs/evaluation.md](docs/evaluation.md).
  The author's gold questions are not published — they are personal data.

#### Installer, vault template and parametrised config

- `install.ps1` with `-VaultPath`, `-DryRun` and `-Force`: copies scripts, hooks
  and skills to their destinations, skips `__pycache__`/`.state`/`.stage`/
  `.import` and compiled artefacts, registers hooks, and verifies that Python
  3.12+ and the `claude` CLI are present.
- `template/vault/` skeleton with empty `daily/` and `knowledge/` trees.
- `template/hub-config.example.json` with generic English hub definitions,
  installed once and never overwritten — replacing hardcoded personal topics.
- `BEYIN_PYTHON` environment variable to select an interpreter; `py -3` fallback
  when `python` is not on `PATH`.
- Test-only escape hatches `BEYIN_FAKE_HOUR` and `BEYIN_FAKE_NOW` for the evening
  compile trigger.
- Test suite under `scripts/tests/`, runnable with `pytest`.

#### Agent onboarding and skills

- `INSTALL-AGENT.md`: a self-contained file an agent can be pointed at to
  install the system for its user — prerequisite probes (Windows, Python 3.12+,
  `claude` CLI, SQLite FTS5), dry run, install, verification of the first flush,
  a troubleshooting table and manual uninstall steps.
- `AGENTS.md`: repository conventions for agents working inside the codebase —
  test command, stdlib-only policy, why the bilingual naming is intentional, the
  compiler write-policy boundary, and the personal-data grep gate.
- `skills/README.md` documenting the shipped skill set and the pruning caveat:
  the installer copies the whole directory to the user's skills folder.
- Six genericized skills alongside the two mechanism skills — `companion`
  (structure example for the personal identity layer, placeholder content only),
  `orchestration`, `codex-fleet` and `gece-vardiyasi`. Absolute paths, usernames and project names replaced with
  placeholders; Turkish trigger phrases kept; upstream Avenox credits and
  adaptation notes preserved.
- `template/rules.example.md`: seventeen ranked binding rules in the form the
  session hook injects, fifteen genericized from the author's working ruleset
  and two adopted from upstream.

### Known gaps

- Sensitive-data filtering beyond credential patterns is not implemented; see
  [SECURITY.md](SECURITY.md).
- Web-fetched text that enters a transcript can be summarised into the vault.
  Untrusted-data delimiters are in place, but there is no exclusion list.
- Windows only; no tested macOS or Linux path.

[Unreleased]: https://github.com/Capslockiller/origin-of-memory/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/Capslockiller/origin-of-memory/compare/v0.4.1...v0.5.0
[0.4.1]: https://github.com/Capslockiller/origin-of-memory/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/Capslockiller/origin-of-memory/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Capslockiller/origin-of-memory/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Capslockiller/origin-of-memory/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Capslockiller/origin-of-memory/releases/tag/v0.1.0
