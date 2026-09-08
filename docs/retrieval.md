# Retrieval and memory hygiene

What ranks a note, what a note's date means, and what the pipeline is allowed to
skip. Companion to [evaluation.md](evaluation.md), which covers how ranking is
measured rather than how it works.

Everything here is stdlib-only and deterministic: same index plus same query
gives the same order, every time, with no model in the loop.

---

## 1. Ranking: BM25 over four weighted fields

`scripts/retrieve.py` ranks with a single signal: FTS5's `bm25()` over the
`notes` virtual table, weighted so a title match outranks a body match.

```sql
CREATE VIRTUAL TABLE notes USING fts5(name UNINDEXED, title, aliases, tags, body);
```

<!-- yazan: codex · gpt-5.6-sol -->
Rows now come from two sources. `knowledge/concepts/*.md` remains the distilled
concept layer. `Last-Session.md`, `Threads.md`, and `Journal.md` in the vault
directory matching `*850-Companion` form the human-written **hand layer**.
Hand files split at `##`/`###`; sections over 1,500 characters split again at
blank lines or bullets into passages of at most 1,200 characters. Heading paths
stay in titles, wikilinks and bold lead words become tags, and each row carries
`source`, `source_file`, `heading`, and `source_date` (`guncel`) provenance.

`bm25(notes, ...)` weights are **positional over every column of the table,
UNINDEXED ones included** — not just the indexed ones. `name` occupies the
first position even though it can never match, so the call must pass five
weights, not four:

```python
BM25_WEIGHTS = (0.0, 8.0, 6.0, 3.0, 1.0)  # name, title, aliases, tags, body
```

Passing only four weights silently shifts every one of them left: `title`
would get `aliases`' weight, `aliases` would get `tags`', `tags` would get
`body`'s, and `body`'s own weight would be dropped entirely — the field
priority the schema intends (title > aliases > tags > body) never actually
applies. This was a real, shipped defect, fixed by adding the leading `0.0`
placeholder; no index rebuild is needed, since it is a query-time-only change
that reads the same columns as before.

**`--min-score`** (the `query` subcommand) is a floor on positive `-bm25()`
relevance: any hit whose `-score` falls below it is discarded before ranking.
The `hook` subcommand — the live `UserPromptSubmit` entry point — reads the
same floor from `BEYIN_RETRIEVE_MIN_SCORE` (default `0.0`, i.e. off) and
layers a separate relevance gate on top; see [§9](#9-the-hook-relevance-gate)
below.

### Scoped hand authority

The strongest concept BM25 score is the reference for each query. A hand hit
moves before concept hits only when its positive relevance is at least
`BEYIN_EL_KATMANI_ORAN` times that reference (default `0.6`). Qualifying hand
hits come first, concept hits fill the remaining result/body budget, and weaker
hand hits remain after them. If no concept matches, hand hits rank normally.
Each emitted hand passage starts with visible provenance:

```text
[el katmanı · Threads.md › Active › Speaking · 2026-09-18]
```

The 1,500-character per-result and 4,500-character total caps are unchanged.

### Correction exclusion contract

`Duzeltmeler.md` sits beside the hand files, and **its grammar is owned by
`scripts/duzelt.py`**, not by this module. Retrieval hands the file's text to
`duzelt.ayristir()` and reads the records back; it does not carry a second
regex for the same blocks, because two parsers for one file is exactly how the
compile side and the query side come to disagree about what a human wrote.

A block is one header comment on a single line, then line-based fields:

```text
<!-- duzeltme kavram=<slug> durum=<bekliyor|uygulandi> ts=<ISO8601> kaynak=<...> -->
iddia: <the sentence that is wrong>
dogru: <the sentence that is right>
```

`gecersiz=evet` and `uygulandi_ts=<ISO8601>` are optional extra attributes, and
`not:` is an optional extra field. The block ends at the first blank line.

What retrieval does with it:

- **`durum=bekliyor`** — the target concept is never emitted. Nobody has fixed
  the note yet, so whatever it says about this subject is still wrong.
- **`durum=uygulandi`** — the target is emitted again *only if the wrong
  sentence really is gone*, tested with `duzelt.iddia_kalmis_mi()` against the
  body about to be injected. "The compiler rewrote the file" is not proof, and
  the index can also be older than the fix; when the claim is still there, or
  the block records no `iddia:` to test, the note stays hidden. This is the
  same accountability test lane B uses before it will close a correction, so a
  note can never be readable here and unfixed there.
- A concept with a non-empty `superseded_by:` in frontmatter is never emitted,
  correction or no correction.
- When a hidden note would have been a hit and the block has a `dogru:` line,
  the first 300 characters of that line are injected in its place under a
  `[düzeltme · <slug>]` header. With no `dogru:` line the hit simply
  disappears — silence is better than a sentence known to be false.
- Every exclusion is recorded as `exclude:correction` in the session ledger,
  with the excluded slugs, so a surprising absence is answerable afterwards.

The ledger file is found the same way the hand files are (the vault directory
matching `*850-Companion`) rather than through `duzelt.yol()`, whose canonical
emoji name would make the two layers read different directories on an install
that named it differently.

## 2. A fused-ranking mode was tried and removed

A previous revision added an opt-in Reciprocal Rank Fusion mode (`rrf`) over
BM25, recency and tag-overlap channels, gated behind `BEYIN_RETRIEVAL=rrf`.
It was measured against BM25 on the LoCoMo long-context benchmark and all 11
public BEIR-family datasets in the benchmark harness
(`tools/benchmark/`): LoCoMo hit@5 was 0.55 for `bm25` versus 0.13 for `rrf`,
and `rrf` scored worse than `bm25` on every one of the 11 BEIR sets too — the
fusion collapses on the dated, mixed-recency corpora this system actually
indexes. `rrf` and everything it depended on (the RRF arithmetic, the
recency and tag-overlap channels, the legacy post-fusion multiplier, the
`BEYIN_RETRIEVAL`/`BEYIN_RRF_K`/`BEYIN_RRF_RECENCY_CHANNEL_WEIGHT`/
`BEYIN_RRF_LEGACY_MULTIPLIER`/`BEYIN_RECENCY_HALFLIFE_DAYS` environment
variables, and the `--retrieval` CLI flag) have been deleted. `search()` and
`hook_result()` still accept a `mode` keyword for source compatibility with
existing callers, but it is now a no-op: every call ranks by BM25.

`documents.source_date` (see [§3](#3-session-anchors-and-what-a-notes-date-means)
below) is unrelated to this and stays — other code reads a note's resolved
source date independent of ranking.

## 3. Session anchors, and what a note's date means

A concept note distilled from a daily log used to have no reliable date: its
frontmatter `updated` reflects when a model last rewrote it, not when the
underlying conversation happened. Session anchors fix that.

```
<!-- session:<session-id> ts:<ISO8601> source:<claude|codex|web|gemini|kaydet|pasaport> -->
```

**Written** by `flush.py` or `kaydet.py` into each daily-log session block,
immediately under the `### Oturum (HH:MM)` heading — `kaydet.py`'s own
`source:kaydet` anchors follow the identical shape and the identical
round-trip; see `docs/kaydet.md`. `pasaport_kapi.py` writes `source:pasaport`
anchors the same way for an approved `[ODENA-DONUS]` reply; see
`docs/pasaport.md`. **Carried** by `compile.py`: every concept note
created or updated from a daily block gets that block's anchors appended to its
`## Kaynaklar` section. **Stripped** by `retrieve.py`, both at index build time
and again on every hit body.

Anchors are bookkeeping, not context. Stripping them at build time means the
session id never becomes a searchable token — otherwise a query for "claude"
would match every note in the vault — and stripping them again at query time
means an index built by an older version cannot leak one into a session either.

<!-- yazan: claude · opus-5 -->
The same holds for **retired** anchors. `compile.py --capa-temizle` moves ghost
anchors out of the active block into one
`<!-- gecmis-capalar: session:<id> ts:<stamp> source:<kind>; ... -->` line
inside the note body. That line deliberately no longer matches
`SESSION_ANCHOR` — being unmatched is what makes it inactive provenance — but
that also meant nothing was removing it, so every retired session id, timestamp
and source word sat in the indexed body as ordinary searchable text.
`strip_session_anchors()` now removes that comment too, on its own line or
inline, so retired history is readable in the file and invisible to both the
index and the injected context.

The daily log is untrusted data, so an anchor is never copied verbatim out of
it. Both the writer and the carrier re-render through
`retrieve.format_session_anchor()`, which strips anything that could close the
comment early or inject a newline, and falls back to a digest when a session id
sanitises down to nothing.

### Date resolution order

`documents.source_date` is resolved once per note at build time, best evidence
first:

1. The **newest anchor `ts:`** in the note body — a real event timestamp.
2. Frontmatter `updated`, then `modified`, then `created`.
3. The file's mtime.

Stored normalised to UTC ISO8601, so lexicographic order is chronological order.
A naive date such as `2026-08-27` is read as UTC midnight.

## 4. Maintenance gating

Two things used to happen more often than they needed to.

**The FTS index was rebuilt after every compiled daily log**, even when that
log produced no concept change — a full re-read and re-tokenize of the whole
corpus to discover nothing. `compile.py` now records a manifest hash
(`concepts_manifest` in `compile-state.json`): one SHA-256 over the name and
content digest of exactly the files `build_index` reads. Unchanged manifest,
no rebuild. The manifest is recorded only after a *successful* rebuild, so a
failed one retries rather than latching.

Hand memory is decoupled from that compiler cycle. `retrieve.py yenile` deletes
and recreates only hand rows in one SQLite transaction. A full `build` stores a
nanosecond mtime stamp for each of the three Companion files in `meta`; the live
`hook` compares those stamps before searching and runs the same hand-only
refresh when a file changed, appeared, or disappeared. Concept rows stay intact.

**The nightly trigger fired on a changed daily log alone.** It is now gated on
both conditions:

- a daily log actually changed, **and**
- at least `BEYIN_COMPILE_MIN_INTERVAL_HOURS` (default 20) have passed since the
  last run that finished `ok`.

A *failed* last run does not hold the gate shut — one bad night must not silence
the compiler for a day. Neither does an unparsable or missing timestamp.

### Skips are loud

A skip is not a failure, so it does not belong in `health.json`'s `error` field,
which the doctor reads as breakage. It does not belong in silence either, or
"why did nothing compile last night?" has no answer anywhere. Both scripts write
skips to a separate structure in the same file:

```json
{
  "error": "",
  "skips":     [{ "reason": "skip:index-rebuild:concepts-unchanged", "ts": 0, "count": 3 }],
  "last_skip": { "reason": "skip:index-rebuild:concepts-unchanged", "ts": 0, "component": "compile" }
}
```

Repeated skips collapse into one counted entry, and the list is capped at 20, so
a nightly no-op cannot grow the file without bound. Current reasons:

| Reason | Meaning |
|---|---|
| `skip:index-rebuild:concepts-unchanged` | The compile touched no concept file; the index is already correct |
| `skip:compile-trigger:min-interval:<elapsed>h<<minimum>h` | A successful run is too recent |
| `skip:compile-trigger:day-already-claimed` | Today's `compile-trigger-<date>` file already exists |

## 5. Configuration

| Variable | Default | Effect |
|---|---|---|
| `BEYIN_COMPILE_MIN_INTERVAL_HOURS` | `20` | Minimum gap after a successful compile; `0` disables the gate |
| `BEYIN_EL_KATMANI_ORAN` | `0.6` | Minimum hand-hit/best-concept relevance ratio for hand-first authority |

Every one of these degrades to its default on junk input rather than raising.
These run inside hooks, and a hook that crashes takes the session's turn with it.

### Command line

```powershell
# Query
python <vault>\.claude\scripts\retrieve.py query "kalıcı bellek"

# Full concept + hand rebuild (`--vault-root` is equivalent)
python <vault>\.claude\scripts\retrieve.py build --vault <vault>

# Hand-only refresh (`refresh` is an English alias)
python <vault>\.claude\scripts\retrieve.py yenile --vault <vault>

# Latency (--bench lives under the `query` subcommand)
python <vault>\.claude\scripts\retrieve.py query --bench

# Does the index still match the notes on disk?
python <vault>\.claude\scripts\retrieve.py verify --vault-root <vault>
```

`verify` recomputes what the index *should* contain from
`knowledge\concepts\*.md` and diffs it against `notes.db`, printing counts plus
`missing` and `extra` ids and exiting non-zero on any drift. It reads file names
only, so a note with broken frontmatter shows up as missing instead of hiding
the drift behind a parse error. The `beyin-doktor` skill runs it as check 14 and
reports 🟢/🟡/🔴 from the same JSON. Schema version 3 is the first version with
hand-layer provenance and concept supersession fields.

## 6. Measurements

**Quality verdict (sealed):** on the LoCoMo long-context benchmark, `bm25`
scored hit@5 0.55 against `rrf`'s 0.13, and `bm25` also outscored `rrf` on
every one of the 11 public BEIR-family datasets in the benchmark harness. The
fused path collapsed specifically on dated, mixed-recency corpora — the kind
this system actually indexes. `bm25` is the only ranking path; see
[§2](#2-a-fused-ranking-mode-was-tried-and-removed) for what was removed and why.

Hard gate: **p95 under 500 ms**.

Measured with `retrieve.py query --bench` (20 fixed Turkish queries, `limit=3`,
warm connection) against a **synthetic 250-note fixture** — multi-kilobyte
bodies, vocabulary deliberately overlapping the bench queries so most queries
match most of the corpus, which is close to the worst case for this workload.

| Corpus | `bm25` p95 |
|---|---|
| 250 notes | 1.25 ms |
| 500 notes | 2.0 ms |
| 1 000 notes | 8.2 ms |
| 2 000 notes | 15.3 ms |

The 250-note row is the median of five runs; the rest are single runs. This
clears the gate by two orders of magnitude on this fixture.

**These are not the gold-set numbers and must not be quoted as such.** The gold
corpus is unpublished (see [evaluation.md](evaluation.md)) and its documented
`bm25` p95 is 347 ms — two orders of magnitude above this fixture, on different
hardware and different note sizes.

`bm25` with `limit=3` stops consuming rows after three hits. Note bodies are
fetched only for the notes actually returned.

The Phase 1 acceptance fixture contains ten synthetic episodic questions across
the three Companion files plus a stale concept contradicting the current
Speaking entry. Measured top-3 recall is **10/10**: the current hand passage is
first for the contradiction and the stale concept is second. This is a
regression-fixture result, not a claim about a private corpus.

## 7. Known limits

<!-- yazan: codex · gpt-5.6-sol -->
**Archive anchor coverage follows the source's identity guarantees.** Claude
Code archive, Codex rollout and claude.ai web imports now render the same
canonical anchor as the live flush path and pass it through `_append_daily()`;
their `source:` values are `claude`, `codex` and `web`. Gemini remains partial:
the Takeout-derived canonical records expose activity IDs, not a conversation or
session ID, and the adapter groups them into synthetic day-sized chunks. It
therefore deliberately omits an anchor instead of presenting the synthetic
ingest key as session provenance, so those notes still fall back to frontmatter
dates.

**Anchor preservation is deterministic, but provenance is still note-level.**
Before the model call, the compiler snapshots every existing concept note's
anchors. After the call and before promotion, it restores only pre-call anchors
that vanished from rewritten notes, then carries the current daily block's
anchors as before. Existing post-call anchors keep their order and model-added
anchors are retained. This prevents a rewrite from silently losing earlier
event dates; it does not identify which individual sentence within a note came
from which anchored session.

**The interval gate can delay a wanted compile.** A run that found nothing to do
still counts as a successful run, so a no-op at 18:05 followed by a real session
at 20:00 leaves that session's log waiting for the next night. This is the
specified behaviour (the gate is an `AND`), and it is the conservative
direction: at worst a daily log is compiled a day later than it could have been.

## 8. Epistemic status in distilled notes

The compiler's distillation instruction now carries three rules about certainty,
because the failure mode is invisible once it happens — a hedge that becomes a
flat assertion reads exactly like a fact.

- A statement that was hedged in the transcript keeps its hedge **and its date**
  in the note ("said once, unconfirmed, 2026-08-27").
- A claim that was uncertain in the transcript must not become a flat assertion.
  The Turkish hedging markers the rule names explicitly — *sanırım*, *galiba*,
  *denemedim ama*, *bir kez* — are the ones that were being dropped.
- A contradiction between a new daily log and an existing note is recorded as an
  explicit `⚠ çelişki: <old> / <new> (<timestamp>)` line. The old statement is
  not silently overwritten.

The rules live in `COMPILE_PROMPT` in the compiler's own language and register;
`AGENTS.md` explains why model-facing prompts here stay Turkish.

## 9. The hook relevance gate

`retrieve.py hook` is the live `UserPromptSubmit` entry point (D1): it reads
the hook JSON from stdin directly, rather than through a PowerShell wrapper
around `query`, and returns nothing (`skip:internal`) when `BEYIN_INVOKED_BY`
is set — the recursion guard that used to live only in that wrapper. Before
this gate the hook injected on almost every prompt: query tokens were
OR-joined, `min_score` defaulted to `0`, and the only skips were "under 12
characters" and "starts with `/`". Measured against the live 527-note index,
all 30 probe prompts injected, including ones with no retrieval-worthy intent
at all.

Two further skips run before the index is even opened: `skip:short` (under
`HOOK_MIN_PROMPT_LEN`, 12 characters) and `skip:slash`, then `skip:intent`
(`prompt_hafiza_ister()`). Intent rejects fenced code, prompts with fewer than
three content words, JSON/hook envelopes, and input beginning with or made
mostly from `<task-notification>`, `<system-reminder>`, `<command-name>`, or
`<local-command-stdout>`. A first word naming a tool/edit also skips as before.

What actually decides relevance is **token overlap, not the BM25 score**: on
the measured corpus, memory-worthy top-1 hits scored 11.4–37.7 and junk hits
scored 6.0–20.9 — ranges that overlap almost completely, so no single score
threshold separates them. A candidate is kept only when at least 2 distinct
content words for a prompt of at most 6 content words, or 3 above that,
(folded, stopword-filtered, at least `GATE_MIN_TOKEN_LEN` (4) characters)
occur in the candidate's own title/aliases/tags — the body is excluded, since
a 1,500-character note mentions a lot of words in passing — or its score
clears the `BEYIN_RETRIEVE_STRICT_SCORE` escape hatch (default `25.0`,
chosen above the strongest measured junk top-1 hit). The gate is **opt-in**:
`context_pack.py` and the MCP `memory_search` tool ask an explicit question
and keep the raw ranking; only the prompt-submit hook turns it on.

Hand-layer passages (`source = el-katmani`) get one exception to the
body-is-excluded rule, because their "title" is just a heading typed in the
moment and their tags are only whatever wikilinks or bold lead words happen
to appear in the section — a passage can squarely answer a question and still
share a single content word with it. For `el-katmani` hits only, `token_overlap()`
also runs the first `GATE_HAND_BODY_CHAR_CAP` (400) characters of the
passage's own body through `gate_tokens()` (the same fold/stopword/path-chunk/
length rules the query already went through) and folds the result into the
metadata pool before counting overlap. Concept notes are unaffected — their
title/aliases/tags are written on purpose at compile time, so they keep the
metadata-only rule and the anti-junk behaviour it buys.

| Variable | Default | Effect |
|---|---|---|
| `BEYIN_RETRIEVE_MIN_SCORE` | `0.0` | Floor on positive `-bm25()` relevance, applied before the gate |
| `BEYIN_RETRIEVE_STRICT_SCORE` | `25.0` | A hit at or above this score is admitted without token overlap |
| `BEYIN_EL_KATMANI_ORAN` | `0.6` | Hand-first threshold relative to the best concept score |

`gate_tokens()` also drops path-shaped chunks (slashes, drive paths, `.py`,
`.md`, `.ps1`), hexadecimal ids of seven or more characters, and one shared
set of generic Turkish caller words. Hook paths and phrases such as “dur bana
soru sorma” therefore cannot manufacture overlap.

Every decision — `inject`, `skip:internal`, `skip:short`, `skip:slash`,
`skip:intent`, `skip:score` (nothing scored at all), `skip:token-overlap`
(hits existed, none passed) — is logged to the per-session ledger with that
`reason`.

**Dedup is query-aware.** The ledger used to key on the note's name alone, so
a note suppressed for one question could never resurface for a materially
different one asked later in the same session. Suppression now keys on
`<query_signature>:<note>`, where `query_signature()` hashes the prompt's
folded token set — two prompts that fold to the same tokens share a
signature, which is exactly when re-showing a note is repetition rather than
a fresh question.
