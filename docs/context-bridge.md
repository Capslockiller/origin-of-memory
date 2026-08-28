# Context bridge — memory for agents without a prompt hook

Per-message injection needs the host to offer a prompt hook. Claude Code does;
most agents do not. The context bridge closes most of that gap the other way
around: instead of pushing memory into every turn, it writes the root map into
the file the agent already loads on its own.

After every successful compile, the bridge mirrors `knowledge/index.md` into a
delimited block inside the agent context files at the vault root:

| File | Read by |
|---|---|
| `AGENTS.md` | Codex and most agents that follow the AGENTS.md convention |
| `GEMINI.md` | Gemini-family CLIs |
| `CLAUDE.md` | Claude Code |

The block looks like this:

```markdown
<!-- beyin:start (generated - refreshed on every compile; edits inside this block are overwritten) -->

> **Memory - root map.** Entry layer of a local knowledge base compiled
> from past sessions; ...

## Kök Harita
...the map...

<!-- beyin:end -->
```

## The two rules that make it safe

**A file that does not exist is never created.** The bridge writes only into
files that are already there. The file's existence is the consent; if you want
Codex to see the memory, create an empty `AGENTS.md` in the vault root and the
next compile fills it in.

**Nothing outside the block is ever rewritten.** On the first run the block is
appended, and every run after that replaces only the text between the markers.
Headings inside the block are demoted one level so the map never outranks the
host document's own structure.

A file whose markers are damaged — one marker missing, duplicated, or in the
wrong order — is **left completely untouched** and reported to the doctor.
Half a marker means a human edited the block by hand, and guessing where it was
supposed to end would destroy their text. One bad file does not stop the others
from refreshing.

The rendered block is scanned by `secret_guard` before anything is written. A
finding aborts the whole refresh for every target, because a secret reaching
this layer means an earlier gate leaked and fanning it out into files other
agents read would make it worse.

Identical content is not rewritten at all, so a compile that changes no concepts
leaves the file's mtime alone.

## Running it

The bridge runs automatically at the end of a successful compile. To run it by
hand:

```bash
python .claude/scripts/context_bridge.py
python .claude/scripts/context_bridge.py --target AGENTS.md   # repeatable
```

Set `BEYIN_CONTEXT_BRIDGE=off` to disable it. Failures never break a compile:
they land in the doctor's state file under the `context-bridge` component and
surface as a warning.

## What this does and does not give you

The block is **static memory**: the agent sees the map of what the knowledge
base holds, plus how to search it. That is enough for the agent to know a topic
exists and go look it up — with `retrieve.py` or the `memory_search` MCP tool.

It is **not** per-message retrieval. Nothing here injects the three most
relevant notes into every turn; that still requires a prompt hook, and that
limit is physics, not a preference. See [`compatibility.md`](compatibility.md)
for which surface each host offers.
