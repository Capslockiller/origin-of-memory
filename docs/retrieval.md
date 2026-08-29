# Retrieval fusion and memory hygiene

What ranks a note, what a note's date means, and what the pipeline is allowed to
skip. Companion to [evaluation.md](evaluation.md), which covers how ranking is
measured rather than how it works.

Everything here is stdlib-only and deterministic: same index plus same query
gives the same order, every time, with no model in the loop.

---

## 1. Two ranking modes

`scripts/retrieve.py` has two ranking paths, selected by `BEYIN_RETRIEVAL`:

| Mode | What it is | Default |
|---|---|---|
| `bm25` | A single signal: FTS5 `bm25(notes, 8, 6, 3, 1)` over title, aliases, tags and body | **yes** |
| `rrf` | Reciprocal Rank Fusion over BM25, recency and tag-overlap channels | opt-in |

`bm25` is the default and is byte-for-byte the behaviour that shipped before
fusion existed — same SQL, same ordering, same `score` field. **`rrf` is
deliberately opt-in until it is measured against the gold set.** Read
[§7 Measurements](#7-measurements) before switching it on; the fused path is
implemented, tested and unmeasured, in that order.

Precedence for the mode is: the `--retrieval` flag, then `$BEYIN_RETRIEVAL`,
then `bm25`. An unrecognised value is not an error — it falls back to `bm25`,
because a retrieval hook that refuses to run is worse than one that ranks the
old way.

## 2. The three signals

Fusion needs ranked lists. These three cost nothing extra — no new dependency,
no second index, no embedding:

1. **BM25** — today's ranking, unchanged.
2. **Recency** — candidates ordered by their newest source date, newest first
   (see [§4](#4-session-anchors-and-what-a-notes-date-means) for where that date
   comes from). Notes with no resolvable date are absent from this list.
3. **Tag/entity overlap** — how many query tokens also appear in the note's
   title, `aliases` or `tags`. Zero-overlap notes are **absent from this list,
   not ranked last** — that is what makes a sparse signal safe to fuse.

Both sides of the overlap count go through the same `expanded_tokens()` the
index uses, so Turkish folding cannot drift between them: `İSTANBUL` and
`istanbul` are one token, and `ISTANBUL` correctly is not — capital `I` folds to
dotless `ı`. On the hot path the note's side is read from the FTS columns, which
already hold that exact token stream, so ranking a match costs a `split()`
rather than a second pass of the tokenizer.

### What can be a candidate

**Only notes present in at least one list are candidates.** In practice the
candidate pool is the FTS match set: a note can only have tag overlap if one of
its indexed fields matched, so the tag list is always a subset of the matches.

The recency list is a **re-ranker, never a source of candidates**: it orders
candidates the other signals already found. Ranking the whole corpus by date
would make every query inherit every note, sorted by nothing that has anything
to do with the question.

## 3. Fusion and recency channel weighting

```
final(note) = Σ channel_weight_i / (k + rank_i)
channel weights = (BM25: 1.0, recency: 1.0, tag overlap: 1.0)
```

`k` defaults to 60 (`BEYIN_RRF_K`), the conventional value. The recency channel
weight defaults to 1.0 (`BEYIN_RRF_RECENCY_CHANNEL_WEIGHT`); `0.0` cleanly
removes recency's contribution while leaving BM25 and tag overlap unchanged.
There is no post-fusion recency multiplier in the default path.

For side-by-side measurement only, `BEYIN_RRF_LEGACY_MULTIPLIER=1` restores the
deprecated former multiplier after fusion:

```
legacy_final(note) = fused(note) × clamp(
    0.5 ** (age_days / half_life), 0.25, 1.0
)
```

Its half-life defaults to 180 days (`BEYIN_RECENCY_HALFLIFE_DAYS`), and `0`
disables the legacy decay. A note with no resolvable date remains unmultiplied
in that comparison path.

### Ties share a rank

Ranks are **competition ranks**: notes with identical evidence get identical
rank (1, 1, 3, …). This matters more than it sounds. `bm25()` returns `0.0` for
a term that appears in every note, so exact ties are routine; with positional
ranks the alphabetical tie-break would silently hand the first note a better
rank in *two* lists at once and manufacture a winner out of its file name.

### `--min-score` gates BM25, not the fused score

`--min-score` is a floor on positive `-bm25()` relevance. In `rrf` mode it is
applied to the **BM25 component only** — the fused score lives on a completely
different scale (hundredths, not units), so comparing them would be meaningless.

One consequence is worth stating plainly: a note filtered out of the BM25 list
by the floor can still enter through the tag-overlap list, with a strictly
smaller fused score. If you need a hard "return nothing weak" cut, use `bm25`
mode, where the floor is absolute.

### Score semantics differ by mode

| Mode | `SearchHit.score` | Better is |
|---|---|---|
| `bm25` | raw `bm25()` | lower (negative) |
| `rrf` | fused RRF sum | higher (positive) |

Anything that reads the score numerically has to know which mode produced it.
Both hook output and the MCP tool return whole notes rather than scores, so this
only affects `--format plain` and direct API callers.

## 4. Session anchors, and what a note's date means

A concept note distilled from a daily log used to have no reliable date: its
frontmatter `updated` reflects when a model last rewrote it, not when the
underlying conversation happened. Session anchors fix that.

```
<!-- session:<session-id> ts:<ISO8601> source:<claude|codex|web|gemini> -->
```

**Written** by `flush.py` into each daily-log session block, immediately under
the `### Oturum (HH:MM)` heading. **Carried** by `compile.py`: every concept note
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

## 5. Maintenance gating

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

## 6. Configuration

| Variable | Default | Effect |
|---|---|---|
| `BEYIN_RETRIEVAL` | `bm25` | `bm25` or `rrf`; anything else falls back to `bm25` |
| `BEYIN_RRF_K` | `60` | RRF constant; below 1 or unparsable falls back to 60 |
| `BEYIN_RRF_RECENCY_CHANNEL_WEIGHT` | `1.0` | Multiplier for the recency channel's RRF contribution; `0.0` removes recency influence |
| `BEYIN_RRF_LEGACY_MULTIPLIER` | unset | Deprecated, comparison-only: exact value `1` restores the former post-fusion multiplier |
| `BEYIN_RECENCY_HALFLIFE_DAYS` | `180` | Legacy multiplier half-life in days; `0` disables legacy decay |
| `BEYIN_COMPILE_MIN_INTERVAL_HOURS` | `20` | Minimum gap after a successful compile; `0` disables the gate |

Every one of these degrades to its default on junk input rather than raising.
These run inside hooks, and a hook that crashes takes the session's turn with it.

### Command line

```powershell
# Query, honouring $BEYIN_RETRIEVAL
python <vault>\.claude\scripts\retrieve.py query "kalıcı bellek"

# Force a mode for one call
python <vault>\.claude\scripts\retrieve.py query "kalıcı bellek" --retrieval rrf

# Latency, per mode (--bench lives under the `query` subcommand)
python <vault>\.claude\scripts\retrieve.py query --bench --retrieval rrf

# Does the index still match the notes on disk?
python <vault>\.claude\scripts\retrieve.py verify --vault-root <vault>
```

`verify` recomputes what the index *should* contain from
`knowledge\concepts\*.md` and diffs it against `notes.db`, printing counts plus
`missing` and `extra` ids and exiting non-zero on any drift. It reads file names
only, so a note with broken frontmatter shows up as missing instead of hiding
the drift behind a parse error. The `beyin-doktor` skill runs it as check 14 and
reports 🟢/🟡/🔴 from the same JSON; a `schema_version` below 2 is the 🟡 case,
an index built before `source_date` existed, which makes `rrf` fall back to
BM25-only ranking until the next rebuild.

## 7. Measurements

**Quality verdict (2026-08-29, sealed):** on the 125-question gold set,
`bm25` scored recall@3 83.2% / recall@5 91.2% while `rrf` (repaired and
legacy alike) scored 69.6% at both cutoffs — a significant loss (McNemar
p=0.003; rrf buries gold notes outside the top 5 instead of demoting them).
**`bm25` stays the default; the fused path's default candidacy is closed**
until a redesigned fusion (channel weights / low-variance channel drop)
measures better on the same set. Run: `Degerlendirme/kos.py --yarisma`,
CSV `yarisma-2026-08-29.csv`.

Hard gate: **p95 under 500 ms**.

Measured with `retrieve.py query --bench` (20 fixed Turkish queries, `limit=3`,
warm connection) against a **synthetic 250-note fixture** — multi-kilobyte
bodies, vocabulary deliberately overlapping the bench queries so most queries
match most of the corpus, which is the worst case for a fused path that has to
consume every match.

| Corpus | `bm25` p95 | `rrf` p95 | Ratio |
|---|---|---|---|
| 250 notes | 1.25 ms | 2.96 ms | 2.4× |
| 500 notes | 2.0 ms | 6.9 ms | 3.5× |
| 1 000 notes | 8.2 ms | 13.0 ms | 1.6× |
| 2 000 notes | 15.3 ms | 25.6 ms | 1.7× |

The 250-note row is the median of five runs; the rest are single runs.

Both modes clear the gate by two orders of magnitude on this fixture, and the
ratio settles around 1.6–1.8× as the corpus grows.

**These are not the gold-set numbers and must not be quoted as such.** The gold
corpus is unpublished (see [evaluation.md](evaluation.md)) and its documented
`bm25` p95 is 347 ms — two orders of magnitude above this fixture, on different
hardware and different note sizes. Applying the measured ratio to that baseline
would put `rrf` somewhere around 550–870 ms, i.e. **over the 500 ms gate**. That
extrapolation is a warning, not a measurement: real queries are more selective
than these bench queries, which shrinks the fused path's extra work. Re-measure
`rrf` on the real corpus before changing the default.

Where the extra cost goes, and what was already removed: `bm25` with `limit=3`
stops consuming rows after three hits, while `rrf` must read every match to fuse
it. Reading the pre-tokenized FTS columns instead of re-tokenizing each row, and
folding `source_date` into the main query instead of issuing a second one, cut
the fused path's p95 by roughly 40% on the 250-note fixture. Note bodies are
fetched only for the notes actually returned.

## 8. Known limits

<!-- yazan: codex · gpt-5 -->
**The former post-fusion recency dominance is fixed by default.** RRF compresses
nearby ranks into a narrow band — with `k=60`, rank 1 contributes `1/61` and
rank 2 contributes `1/62`, a difference of about 1.6% — while the removed
multiplier spanned 4× from 1.0 to its 0.25 floor. That mismatch let a fresh weak
match outrank an old exact match.

The repaired path uses recency only as one fusion channel, so the final score is
the RRF sum. `BEYIN_RRF_RECENCY_CHANNEL_WEIGHT` can scale that one channel and
`0.0` removes its influence; its default remains deliberately untuned at 1.0.
`BEYIN_RRF_LEGACY_MULTIPLIER=1` restores the deprecated multiplier solely for
comparison measurements and must not be treated as the recommended mode.

Historically, the dominance failure was kept visible by
`test_a_fresh_weak_match_can_still_outrank_an_old_exact_one`; that test now pins
the legacy reproduction while a companion test pins the repaired ordering.

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

## 9. Epistemic status in distilled notes

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
