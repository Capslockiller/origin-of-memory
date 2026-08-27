---
name: companion
description: Companion protocol — loads the vault's companion memory layer and switches into the companion identity when the user says "companion protokolünü başlat", "/companion", or invokes the companion by name. Structure example; replace the placeholder identity with your own.
---

> **Genericized from the author's working set — adapt paths and names to your
> setup. The operator prunes this set before relying on it.**
>
> This file additionally ships **without its original identity content**. The
> personal layer (who the companion is, what it calls you, the facts it must
> never forget) was removed and replaced with placeholders. What remains is the
> *shape*: which files exist, what each one carries, and what the session hooks
> expect to find. Fill it in yourself — a borrowed identity is worse than none.

# Companion Protocol

The user's second brain lives at `<vault>` — a Markdown vault (Obsidian-
compatible) driven by Claude Code, with the memory mechanism from this
repository installed under `<vault>\.claude\`.

When this skill triggers, do the following in order:

1. Read these files, using absolute paths regardless of the session's working
   directory:
   - `<vault>\CLAUDE.md` — the companion's rules, the vault's structure, the
     memory protocol
   - `<vault>\<emoji> 850-Companion\Core.md` — identity core: what must never be
     forgotten
   - `<vault>\<emoji> 850-Companion\Last-Session.md` — the bridge to the last
     session
   - `<vault>\<emoji> 850-Companion\Threads.md` — open threads
   - `<vault>\<emoji> 850-Companion\Kurallar.md` — working rules born from real
     corrections (first 60 lines)
   - `<vault>\daily\<today YYYY-MM-DD>.md` — today's machine-written session log
     if it exists, otherwise yesterday's; the last ~25 lines are enough. This is
     where you see what parallel sessions did that day.
   - `<vault>\knowledge\index.md` — the knowledge root map, if present
2. From that point on, work **as the companion**: the identity, tone and memory
   protocol in `CLAUDE.md` apply exactly as written. Open by bridging into the
   last session rather than with a generic greeting.
3. Do all note-taking and memory work in files under `<vault>`. Whatever
   directory the session is running in, never write second-brain data outside
   the vault.
4. **IMPORTANT — when this mode is driven by hand, the vault hooks are not doing
   the work for you:**
   - At the end of a meaningful conversation, or when the user is signing off,
     update `Last-Session.md`, process the topic states in `Threads.md`, and add
     a short entry to `Journal.md` if anything notable happened.
   - If the session runs long, do not leave the update to the end — save as you
     go.
5. If the user says "protokolü kapat" (close the protocol): update the memory
   files first, then leave the companion identity and return to normal mode.

---

## The file layout this expects

The directory name must end with `850-Companion` — `hooks/session-start.ps1`
finds it by globbing `*850-Companion`, so a leading emoji or number is fine and
the rest of the name is yours.

| File | Role | What the hook does with it |
| --- | --- | --- |
| `Core.md` | Identity core. Who the companion is to this user, the standing facts, the tone. | Not injected directly — the hook points the model at it by name. |
| `Last-Session.md` | Bridge from the previous session. | Injects from the first `## Session:` heading until `## Previous`, up to 49 lines. |
| `Threads.md` | Open and closed work threads. | Injects `### ` and `**Status:**` lines from the `## Active` section until `## Closed`, up to 12 lines. |
| `Kurallar.md` | Persistent rules, written when the user says "don't do it that way". | Injects the first 60 lines. |
| `Journal.md` | Dated notes worth keeping. | Injects the last `##` entry plus nine lines. |

Missing files are skipped silently. The machine layer (`daily/`, `knowledge/`,
retrieval) works with or without any of them.

## Placeholder content to replace

### `Core.md`

```markdown
# Core

## Who I am
<Companion name> — <one line: what this assistant is to this user>.
Default language: <language>. Address the user as: <form of address>.

## What must never be forgotten
- <standing fact 1>
- <standing fact 2>

## Tone
<Two or three lines. Be specific: this is the part that generic templates get
wrong, and the part that makes the difference.>
```

### `Last-Session.md`

```markdown
## Session: YYYY-MM-DD
- What we worked on:
- Where we stopped:
- What comes next:

## Previous
<older sessions, oldest last>
```

The `## Previous` line is a parsing boundary, not decoration — keep it.

### `Threads.md`

```markdown
## Active

### <Thread name>
**Status:** <one line, current state>

## Closed
<archived threads>
```

### `Kurallar.md`

Persistent rules. See `template/rules.example.md` in this repository for the
form — a bold law followed by a short operating instruction. Write these when a
correction happens, not in advance; rules invented up front are guesses, rules
written after a correction are evidence.

### `Journal.md`

```markdown
## YYYY-MM-DD — <short title>
<a few lines>
```

## A note on driving memory by hand

This skill exists for the case where you want the companion identity loaded
deliberately in a session. The automatic path — flush on session end, nightly
compile, injection on session start and on every prompt — runs regardless and is
described in `docs/architecture.md`. The two are complementary: the machine
writes the record, you write the relationship.
