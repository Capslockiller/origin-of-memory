# Tool-free compile

Compile is the one call in this project that hands a model file-writing tools.
It runs with `--tools Read,Write,Edit,Glob,Grep` and `--permission-mode
acceptEdits` inside a 0700 staging tree, and afterwards we audit what the model
did: manifest diff, path allowlist, directive quarantine, secret guard, schema
gate, atomic promotion.

That design costs two things. It keeps a filesystem-write surface open to a
model, and it is the single reason the local backends cannot compile at all —
none of `agy`, `ollama`, or an OpenAI-compatible endpoint can scope per-call
filesystem permissions, so they refuse the call rather than pretend.

Text mode removes both costs. The model returns structured text; **our code**
writes the files. Everything downstream of the write is byte-for-byte the same
audit chain, because the staging tree still exists and nothing after it knows
which mode produced its contents. Only the hand on the pen changes.

## The contract

```
=== FILE: knowledge/concepts/slug.md ===
<complete file content>
=== END FILE ===
=== FILE: knowledge/log.md ===
<complete file content>
=== END FILE ===
=== DONE ===
```

Delimited markdown rather than JSON, deliberately. The payload is already
markdown with wikilinks and Turkish prose; JSON escaping would add a failure
mode where a single bad escape discards the whole answer. Here a damaged block
costs only that block and the rest still promotes.

Prose outside the blocks is ignored. `=== MORE ===` in place of `=== DONE ===`
asks for a continuation call.

## What the parser refuses

Rejection happens before anything is written, and every rejection is reported
rather than silently swallowed:

| Case | Result |
| --- | --- |
| Path outside the allowlist | block dropped |
| `..`, absolute, drive-letter, or `~` path | block dropped |
| Block with no `=== END FILE ===` (truncated answer) | that block dropped, the rest kept |
| Same path twice in one answer | second dropped — we cannot know which was meant |
| Empty or oversized content | block dropped |
| More than 40 blocks | capped |
| No usable block at all | run fails, nothing written |

The path allowlist is not the parser's own: `_is_allowed_output_file` is passed
in, so text mode owns no policy that tool mode does not already enforce. After
parsing, writes are confined to the staging tree and re-checked after path
resolution, so a symlink cannot redirect one outside.

## Whole files, not edits

Without tools the model cannot read an existing article, and it answers with
complete files. So text mode gives it the full body of the articles it may
rewrite — the same hub-scoped, recent selection the duplicate-check registry
already computes, bounded by `BEYIN_COMPILE_TEXT_BODIES` (default 6) and
`BEYIN_COMPILE_TEXT_BODY_BUDGET` (default 24000 characters).

Those bodies are prompt input, so they pass the same instruction-shaped check
the root map and registry already get. An article that is not shown in full is
explicitly out of bounds for that run.

## Continuation

A long compile may not fit one answer. `=== MORE ===` promotes what arrived and
calls again with the same context plus the list of files already written.
`BEYIN_COMPILE_MAX_TURNS` (default 4) caps the loop; hitting the cap is flagged
loudly and still promotes what was produced, because discarding good articles
for being verbose is the worse failure.

A file written in an earlier turn is not overwritten by a later one.

## Switching modes

```
BEYIN_COMPILE_MODE=tools    # default
BEYIN_COMPILE_MODE=text
```

**The default is still `tools`, and stays there until the measurement below has
run.** The code exists and is tested; its output quality against a real model is
not yet measured, and this project does not change a default on the strength of
an argument.

## The gate that has not run yet

Per the v0.6 plan, text mode becomes the default only after:

1. The same ten daily logs compiled in both modes with the same model, compared
   on concept count, links per concept, frontmatter validity, secret scan, and
   gold-set recall@3. Text must land within 5% of tools on the counts, with any
   recall difference below the significance bar (~16 questions).
2. Compile actually running end to end on `ollama` and `agy`, with the log
   attached.

Item 3 of that gate — the hostile-output battery — **is** done and lives in
`scripts/tests/test_compile_text.py` and
`scripts/tests/test_compile_text_mode.py`: forbidden paths, traversal, absolute
paths, half blocks, duplicates, oversized output, and a leaked credential are
each proven to be stopped, and each proof was verified by mutation — breaking
the check makes the test fail.

Until items 1 and 2 have run against a real model, the honest statement is:
text mode is built and its refusals are proven; its output quality is unmeasured.
