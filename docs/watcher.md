<!-- yazan: codex · gpt-5.6-sol -->

# Hookless transcript watcher

`scripts/watcher.py` captures settled transcript files without asking the host
for a session-end event. It discovers sessions with the existing Claude and
Codex ingest adapters, sends their `Session` objects through the shared ingest
summariser and append path, and records progress in the existing
`ingest-state.json`. There is no second summarisation implementation or watcher
state file.

The lifecycle hook remains useful: it normally captures a Claude Code session
immediately, while the watcher is deliberately delayed so it does not read a
file that is still being written. Capture no longer depends on that hook.

## Running it

One manual sweep:

```powershell
python scripts/watcher.py --once
```

Persistent loop, sweeping every 15 minutes by default:

```powershell
python scripts/watcher.py
```

The default roots are `~/.claude/projects` and `~/.codex/sessions`. Override
them with `--claude-root` and `--codex-root`; disable either default source with
`--no-claude` or `--no-codex`. `--interval` and `--settle-seconds` are seconds;
both default to 900. A transcript younger than the settle interval is left for a
later sweep. `--max-sessions`, `--sleep`, and `--model` retain the ingest
family's bounded-call controls.

Healthy sweeps print nothing. An unhandled error writes a `watcher:...` error to
`ingest-health.json`, making the ingest row red in `python scripts/durum.py`. A
later healthy sweep clears only a watcher-owned error; it does not erase an
unrelated ingest failure.

No scheduler is registered by this repository. If the owner later chooses
Windows Task Scheduler, the proposed action is:

```text
python "<vault>\.claude\scripts\watcher.py" --once
```

with a 15-minute repeating trigger. Registration, credentials, missed-run
policy, and wake behavior are intentionally a separate decision.

## Generic adapter contract

Add one or more user-named roots with `--generic NAME=PATH`, for example:

```powershell
python scripts/watcher.py --once --generic exported=D:\chat-exports
```

Files are discovered recursively. One finalized, immutable file is one session;
its relative path is its stable identity and its mtime is the session time.
Renaming a file therefore creates a new identity. Supported file contracts are:

- `.jsonl`: one JSON object per line, with `role` equal to `user` or
  `assistant` and `content` as a string. Empty lines and other roles are ignored.
- `.md`: alternating exact level-two headings `## User` and `## Assistant`;
  the text until the next such heading is that turn's content. Text outside
  those sections is ignored.

Unreadable, malformed, or turn-free files are skipped without ending the sweep.
Their size and mtime use the ingest ledger's existing file filter, so an
unchanged bad file is not reopened every 15 minutes; editing it makes it eligible
again. Generic anchors use the provenance schema's `web` external-import tag,
while the session identifier itself begins `generic-` and the daily heading
names the configured `generic:NAME` source.

## Double-write protection

Every candidate has a canonical `<!-- session:<id> ... -->` anchor. Before any
model call, the watcher acquires the exact per-session lock used by `flush.py`
and searches the daily logs for that session id while holding the lock. If an
anchor already exists, it records `skipped-live` and does not summarise or
append. If the watcher wins the race, it appends and writes the existing flush
session state before releasing the lock, so a waiting hook observes the same
completed session. Hooks and watcher may therefore run together.

Discovery watermarks are stored on the normal per-session entries in
`ingest-state.json`. The watcher resumes after the newest terminal watermark;
failed sessions remain retryable. No parallel watermark or lock exists.

## Source support

| Source | Status | Evidence / contract |
| --- | --- | --- |
| Claude Code | implemented | existing `~/.claude/projects/**/*.jsonl` adapter |
| Codex | implemented | existing `~/.codex/sessions` rollout adapter |
| Generic `.md` / `.jsonl` folder | implemented | contract above; user supplies a named root |
| Antigravity CLI (`agy`) | **not implemented** | official docs expose only `~/.gemini/antigravity-cli/cache/last_conversations.json`, a workspace-to-conversation-ID map, and say the CLI queries its backend to load a conversation; they do not establish a local transcript archive ([Resume documentation](https://www.antigravity.google/docs/cli/commands/resume/)) |

The `agy` cache is not a transcript and is not parsed. An adapter should be added
only when Google documents a local export/archive contract or a real supported
layout can be verified.
