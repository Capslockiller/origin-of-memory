# Attribution

Where this project came from, what was taken, what was rebuilt, and why the word
is "adapted" rather than "forked".

---

## Upstream: avenoxbeyin v2

**Origin of Memory derives from [avenoxbeyin v2](https://github.com/avenoxai/avenoxbeyin)
by [Avenox](https://avenox.lol), MIT licensed.**

avenoxbeyin is an open-source second brain for Claude Code: an Obsidian vault
driven by hooks, with memory that survives across sessions. Its v2 thesis is the
idea this project is built on and would not exist without:

> **Memory must be a mechanism, not a discipline.**

v1 of that project gave continuity but depended on the model remembering to update
its own memory files at the end of a session. Every time it forgot, that day was
lost. v2 replaced the request with machinery: a `SessionEnd` and a `PreCompact`
hook flush every conversation into a `daily/` log automatically through a small
background model call, and once a day a compile pass turns those logs into linked
articles under `knowledge/`. The next session opens with that knowledge already in
context. Nobody has to remember anything.

Everything downstream in this repository — the `daily/` → `knowledge/` shape, the
hook events chosen, the flush-then-compile split, the "the machine writes, you
relate" division of labour — is that design. The specific names `daily/`,
`knowledge/`, `concepts/`, `index.md` and the five-section daily summary contract
all come from upstream.

## Second-order credit: Karpathy's knowledge-base pattern

avenoxbeyin credits, and this project inherits the credit for, the
knowledge-compilation architecture:

> Andrej Karpathy, LLM knowledge base gist
> <https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f>

The pattern — accumulate raw material, then have a model periodically distil it
into a set of atomic, cross-linked articles rather than letting the raw log grow
without structure — is what `compile.py` implements. Credit passes through, not
around: this project takes the pattern via avenoxbeyin and names both.

## Adapted, not forked

This repository has **no shared git history** with avenoxbeyin. It is not a fork,
and calling it one would misdescribe the relationship in both directions — it
would overstate the code inheritance and understate the debt to the design.

What actually happened:

1. **Clean-room build from the specification.** The implementation was written
   from avenoxbeyin's `SPEC-V2` document, not by copying its source tree. The
   design is upstream's; the code is not upstream's code.
2. **Port to a platform upstream does not support.** avenoxbeyin's tested platform
   is macOS; its hooks are bash, its locking is `fcntl`, its launcher uses
   `osacompile` and AppKit. Windows there is documented as WSL-only and untested.
   This project is native Windows: PowerShell hooks, `msvcrt` region locks, UTF-8
   handling at every process boundary, Windows detached-process creation flags, a
   PowerShell installer. Essentially none of the platform layer could have been
   carried across even if the code had been copied.
3. **Extension with layers upstream does not have.** User-level hook registration
   so memory works from every project rather than only inside the vault; FTS5 BM25
   per-prompt retrieval with Turkish i-folding; the root-map layer that cut the
   compiler's per-call input base; `secret_guard` redaction; the ingest family for
   backfilling Claude Code, Codex, claude.ai and Gemini history; a gold-set
   evaluation methodology; and a parametrised installer with a vault template.

A fork implies a common ancestor commit and the possibility of merging back.
Neither is true here. "Adapted from" is the accurate description, and it is the
one used everywhere in this repository.

## What this means for licensing

Both projects are MIT licensed. Nothing in this attribution creates additional
obligations beyond MIT's — it exists because correct credit is worth more than the
minimum the licence requires.

If you are looking for the **macOS or Linux** version of this idea, go upstream:
<https://github.com/avenoxai/avenoxbeyin>. That is the tested path on those
platforms, and this project has no business competing with it there.

## Divergences worth knowing about

If you know avenoxbeyin and are reading this codebase, these are the places where
the two designs genuinely differ, rather than merely differing in syntax:

| Area | avenoxbeyin v2 | Origin of Memory |
| --- | --- | --- |
| Platform | bash + `python3`, macOS tested | PowerShell + Python 3.12, Windows native |
| Hook scope | vault-oriented | user-level registration, every project |
| Session-start context | knowledge index + memory | same, plus a generated compact root map under a char budget |
| Per-prompt retrieval | none | FTS5 BM25, top 3 full notes, hook-side selection |
| Compiler input | knowledge index | root map + compact duplicate-check registry |
| Compiler isolation | — | 0700 staging tree, manifest diff, allow-listed promotion |
| Secret handling | — | `secret_guard` redaction in, out, and at the promotion gate |
| History import | ChatGPT/Claude/Gemini exports | Claude Code archives, Codex rollouts, claude.ai ZIPs, Gemini Takeout, resumable |
| Optional semantic layer | mem0 tier available | none; lexical only, by design |
| Setup | conversational, driven by Claude reading `SETUP.md` | `install.ps1`, idempotent, `-DryRun`/`-Force` |
| Evaluation | — | judge-free recall@k against a real-question gold set |

Neither column is a scorecard. The extra machinery in the right-hand column exists
because of problems measured in one specific corpus, and it carries costs —
Windows-only, more moving parts, more to break — that the upstream design
deliberately avoids.
