# Release notes — 0.3.0

The release where Claude Code stopped being a requirement.

## Capture no longer needs a hook

`scripts/watcher.py` sweeps settled transcripts through the existing ingest
pipeline. Capture is now a property of the archive on disk rather than of the
host, and the `SessionEnd` / `PreCompact` hooks demote to a latency
optimisation. Claude Code and Codex reuse the existing ingest adapters; a
generic named-folder contract covers `.md` and `.jsonl` from anywhere else.

Hooks and the watcher can run at the same time without producing two records
of one session: the watcher takes the existing per-session flush lock and then
checks every daily log for the canonical session anchor before summarising.
Removing that check fails a test — the guard is proven to run, not asserted to.

**Antigravity is deliberately not implemented.** Its documentation describes a
workspace-to-conversation-ID cache, not a local transcript archive, so there is
no layout to read. A fabricated path that silently captures nothing is worse
than an absent adapter, and the docs say so instead of implying coverage.

## Memory for agents that have no hook at all

The **context bridge** writes the root map into a delimited block inside
`AGENTS.md`, `GEMINI.md` and `CLAUDE.md` at the vault root, so an agent that
never calls a hook still sees what the knowledge base holds and how to search
it. A file that does not exist is never created — its existence is the user's
consent — and nothing outside the markers is ever rewritten. A file whose
markers are damaged is left completely untouched and reported.

This is static memory, not retrieval. `docs/compatibility.md` now states which
surface each host actually provides, and the README says the same in plain
words rather than the flattering version.

## Compile stopped being the call only one backend could serve

`BEYIN_COMPILE_MODE=text` makes the model return a delimited file transcript
which this project writes itself, so the `--tools` and `--permission-mode`
surface disappears and any backend can compile. Everything downstream is
unchanged: the same manifest diff, path allowlist, directive quarantine,
secret guard, schema gate and atomic promotion audit the result. A parity test
proves both modes promote identical bytes from identical model output.

**The default is still `tools`.** The code is built and its refusals are proven
by mutation testing — forbidden paths, traversal, absolute paths, truncated
blocks, duplicate paths, oversized output and leaked credentials each fail the
run or drop the block — but its output quality against a real model is not
measured yet, and this project does not move a default on an argument.

## Provenance became deterministic

Anchor preservation used to depend on the model obeying an instruction in the
prompt. The compiler now snapshots concept-note anchors before the call and
restores only the pre-call anchors a rewrite removed. Archive imports from
Claude Code, Codex and claude.ai now carry the same canonical anchor a live
flush produces. Gemini stays anchor-free on purpose: its Takeout day chunks
have no genuine session identity, and presenting a synthetic key as provenance
would file a fabricated fact as memory.

## Fixed

The uninstall test never passed in CI. It fabricated hook registrations with a
raw temporary path while `uninstall.ps1` normalised through
`[IO.Path]::GetFullPath`, which expands 8.3 short names — and GitHub runners
hand out a `TEMP` that carries one. Eight consecutive pushes were red,
including the 0.2.0 release commit, while the badge on this page said
otherwise. The product was never affected; the test was.

`kur.ps1` no longer leaves Windows PowerShell's progress rendering enabled
during the Ollama installer download, which severely slowed the transfer.

`ingest_claude`, `ingest_codex` and the new watcher shared a settle-window
check written as `current - mtime < fresh_seconds`. With the window set to
zero — which every caller reads as "disabled" — a file whose mtime landed a
fraction ahead of the clock was still skipped, and a skipped candidate is
never classified, so its rejection simply vanished. Measured here, roughly one
freshly written file in eight carries a future mtime, which made the suite fail
about two runs in three while passing in isolation. The window now has to be
positive to filter anything.

## Still open, on purpose

- Tool-free compile is unmeasured; the default stays `tools`.
- Hybrid retrieval (`BEYIN_RETRIEVAL=rrf`) is unmeasured; the default stays
  `bm25`, and its recency-domination limit is documented rather than hidden.
- No scheduler entry is registered for the watcher — the docs describe the task
  and wiring it is left to the operator.
- Windows only. Not "probably fine" elsewhere — untested.
