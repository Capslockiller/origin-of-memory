# Features in detail

> Türkçe: [features.tr.md](features.tr.md)

The full feature list, the annotated pipeline, the Turkish design decisions, and
the companion layer you write yourself. The README keeps the short version; this
page is what it is short *of*. For the implementation rather than the feature,
see [architecture.md](architecture.md).

---

## The pipeline, annotated

```
  session ends                conversation about to compact
  (SessionEnd)                        (PreCompact)
       |                                    |
       +----------------+-------------------+
                        v
                 flush-launch.ps1        detaches in under a second
                        v
                     flush.py            claude -p --model haiku
              reads the transcript, writes a five-section summary
              secret_guard.py redacts credential patterns in and out
                        v
                daily/YYYY-MM-DD.md      written by the machine, not by you
                        |
        (after 18:00, once per day, only if a daily log changed)
                        v
                    compile.py           claude -p --model sonnet
        runs inside an isolated staging copy of knowledge/ + one daily log
        writes only knowledge/concepts/**, index-full.md, log.md
        every change is re-validated before promotion into the live vault
                        v
        +---------------+---------------+
        v                               v
   rootmap.py                      retrieve.py build
   knowledge/index.md              .state/notes.db
   (compact root map)              (SQLite FTS5 index)
   knowledge/hubs/*.md
        |                               |
        v                               v
  session-start.ps1              memory-retrieve.ps1
  SessionStart:                  UserPromptSubmit:
  companion memory +             BM25 over the prompt ->
  root map, 16k char budget      top 3 full notes injected
```

Two properties are load-bearing:

- **The hook does the retrieval, not the model.** Claude is never handed a search
  tool and asked to go look things up — agents under-call such tools. Selection
  happens in the hook, before the model sees the turn.
- **Hooks are registered at user level.** The brain writes from every project and
  reads in every project, not only inside the vault folder.

## Feature list

- **Automatic session capture.** `SessionEnd` and `PreCompact` both flush; a
  conversation that gets compacted mid-session is not lost.
- **Nightly knowledge compilation.** Daily logs become atomic concept articles
  under `knowledge/concepts/`, cross-linked with a written justification for each
  link, plus a single-row-per-article table in `knowledge/index-full.md`.
- **Per-prompt retrieval.** SQLite FTS5 with `bm25(notes, 8, 6, 3, 1)` — title,
  aliases, tags, body — returns the top 3 full notes, capped at 1,500 characters
  per note and 4,500 in total. Trivial prompts (under 12 characters) and slash
  commands are skipped; a per-session ledger prevents re-injecting the same note.
- **Root map layer.** `rootmap.py` keeps `knowledge/index.md` under a 4,000
  character budget as a topic map into `knowledge/hubs/*.md`, with the full table
  kept separately. Every concept is verified to be covered by a hub before
  publication.
- **Compile isolation.** The compiler never edits the live vault. It works in a
  0700 staging tree at `<vault>/.stage/compile-stage-*`; the resulting file
  manifest is diffed, deletions and out-of-scope writes raise a policy error, and
  only allowed paths are promoted atomically.
- **Frontmatter schema gate.** A staged note whose frontmatter does not validate
  is routed to `.stage/karantina/sema/` with a sidecar naming the problems
  instead of being promoted. Nothing is ever auto-repaired — inventing a missing
  date would file a fabricated fact as permanent memory.
- **Secret redaction.** `secret_guard.py` rewrites credential patterns to
  `[SIR:<pattern>]` on the way into and out of the summariser, and scans compiler
  output at the promotion gate.
- **History backfill.** `ingest.py` imports past conversations from Claude Code
  archives (`~/.claude/projects`), Codex rollouts (`~/.codex/sessions`),
  claude.ai export ZIPs and a Google Takeout Gemini archive.
- **Session anchors.** Each flushed daily block carries
  `<!-- session:<id> ts:<ISO8601> -->`; the compiler carries it into the concept
  note's sources and retrieval strips it before injection, so a compiled claim
  can be traced back to the session that produced it.
  See [retrieval.md](retrieval.md#4-session-anchors-and-what-a-notes-date-means).
- **Opt-in hybrid ranking.** `BEYIN_RETRIEVAL=rrf` fuses BM25, tag/alias overlap
  and recency. It is implemented, tested and **unmeasured** against the gold set,
  which is why it is not the default.
- **MCP memory server.** A stdlib-only local MCP server exposes `memory_search`
  and the root map to any MCP-capable client — Claude Desktop included, on the
  free plan — read-only, over stdio. Setup: [mcp.md](mcp.md).
- **Per-call accounting.** `.state/calls.jsonl` records one line per model call —
  backend, model tier, component, character counts, duration, outcome — and
  **no prompt or response content**. `python scripts/durum.py` summarises the
  last 7 days.
- **Health check skill.** `beyin doktor` reports hook wiring, script presence,
  daily-log freshness, quarantine state, index consistency and last compile
  status in a single table.
- **Hookless capture.** `scripts/watcher.py` sweeps settled transcripts through
  the existing ingest pipeline, so capture is a property of the archive on disk
  rather than of the host and the lifecycle hooks become a latency optimisation.
  Claude Code, Codex and a documented generic folder contract are supported.
  Antigravity is **not**: its documentation describes a workspace-to-conversation
  cache, not a local transcript archive, and a fabricated path that silently
  captures nothing is worse than an absent adapter.
- **Context bridge.** The root map is written into a delimited block inside
  `AGENTS.md`, `GEMINI.md` and `CLAUDE.md` at the vault root, so an agent with no
  prompt hook still sees what the knowledge base holds and how to search it. A
  file that does not exist is never created — its existence is the consent — and
  nothing outside the markers is ever rewritten. This is static memory, not
  retrieval. See [context-bridge.md](context-bridge.md).
- **Tool-free compile** (`BEYIN_COMPILE_MODE=text`). The model returns a
  delimited file transcript and this project writes the staging tree itself, so
  compile stops being the one call that needs `--tools` and the one call only
  `claude` can serve. Every downstream gate is unchanged, and a parity test
  proves both modes promote identical bytes. **Default is still `tools`** — the
  refusals are proven by mutation testing, the output quality is not yet
  measured. See [tool-free-compile.md](tool-free-compile.md).
- **First-class Turkish.** See below.

## Turkish support

Turkish is a first-class target, not an afterthought, and the design decisions
are deliberate:

- **Dual-form indexing.** Every word of at least three characters is indexed both
  in its raw folded form and, when longer than five characters, as a
  five-character prefix. Queries are tokenised through exactly the same function,
  so index and query always agree.
- **Explicit i-folding.** Turkish dotted/dotless I (`I` `ı` `İ` `i`) is folded
  through an explicit translation table before `casefold()`, never through
  locale-dependent `lower()`/`upper()`. `turkish_fold()` in both `retrieve.py`
  and `rootmap.py` is the single definition.
- **No stemmer, on purpose.** Snowball's Turkish stemmer over-stems badly,
  collapsing unrelated words into one root. Fixed-length truncation plus the raw
  form was measurably safer than a stemmer that merges distinct concepts.
- The shipped summariser and compiler prompts write Turkish articles. If you want
  another language, edit `build_flush_prompt()` in `scripts/flush.py` and
  `COMPILE_PROMPT` in `scripts/compile.py`; the retrieval layer is
  language-agnostic apart from the Turkish folding, which is harmless for other
  Latin-script languages.

## Skills

The wizard asks about every skill; `beyin-doktor` and `beyin-ice-aktar` default
to yes, and the rest default to no. Direct `install.ps1` still copies all skills
unless `-SkillFilter` is supplied.

Two of them are part of the mechanism: `beyin-doktor` (health check for the
pipeline) and `beyin-ice-aktar` (processes a claude.ai export ZIP into the
vault).

The other four — `companion`, `orchestration`, `codex-fleet`, `gece-vardiyasi` —
are **genericized copies of the author's working set**: delegation policy, an
overnight draft-only shift protocol, and a Codex CLI operating manual. They are
published because the patterns are useful, not because they are right for you,
and several assume tools you may not have installed.

See [../skills/README.md](../skills/README.md) for what each one does and what
"genericized" means here.

## Writing your own companion protocol

`session-start.ps1` injects a *companion memory* layer alongside the generated
root map: it looks for a directory matching `*850-Companion` in the vault and
reads `Last-Session.md`, `Threads.md`, `Kurallar.md` (persistent rules),
`Journal.md` and points the model at `Core.md`.

**Those files are not shipped.** The personal identity layer — who your assistant
is to you, how it addresses you, what rules it must never break — is yours to
write, and a generic template would be worse than nothing. The mechanism only
requires that:

- the directory name ends with `850-Companion`;
- `Last-Session.md` has `## Session:` headings and a `## Previous` boundary;
- `Threads.md` has an `## Active` section (with `### ` items and `**Status:**`
  lines) and a `## Closed` boundary;
- `Kurallar.md` and `Journal.md` are plain Markdown — the first 60 lines and the
  last `##` entry respectively are what get injected.

Missing files are simply skipped. The machine layer (`daily/`, `knowledge/`,
retrieval) works whether or not you write a companion layer.

`../skills/companion/SKILL.md` documents this shape with placeholder content,
and [../template/rules.example.md](../template/rules.example.md) is an example
rules file in the form the hook expects.
