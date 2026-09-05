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

**`--min-score`** is a floor on positive `-bm25()` relevance: any hit whose
`-score` falls below it is discarded before ranking.

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

Every one of these degrades to its default on junk input rather than raising.
These run inside hooks, and a hook that crashes takes the session's turn with it.

### Command line

```powershell
# Query
python <vault>\.claude\scripts\retrieve.py query "kalıcı bellek"

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
reports 🟢/🟡/🔴 from the same JSON; a `schema_version` below 2 is the 🟡 case,
an index built before `source_date` existed.

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
