# Awesome-list entry — draft

**Draft only. Not submitted.** Opening the PR is the operator's hand, one list
at a time, each following that list's own contribution rules. See
[../discoverability.md](../discoverability.md#5-awesome-list-submissions--pending).

Every list has its own entry format — check its `CONTRIBUTING` before pasting
any of this, and match the surrounding entries' voice, length and alphabetical
position rather than these lines.

---

## Default entry line

```markdown
- [Origin of Memory](https://github.com/Capslockiller/origin-of-memory) — Persistent cross-project memory for Claude Code on Windows. Sessions are auto-summarised, compiled nightly into a linked Markdown vault, and injected into every prompt by a hook rather than a tool the model must call. Python stdlib only, no vector DB.
```

## Shorter, for lists that keep entries to one line

```markdown
- [Origin of Memory](https://github.com/Capslockiller/origin-of-memory) — Windows-native persistent memory for Claude Code: auto-summarised sessions, a nightly knowledge compiler, and BM25 retrieval pushed into every prompt.
```

## For an agent-memory or LLM-memory list

```markdown
- [Origin of Memory](https://github.com/Capslockiller/origin-of-memory) — Hook-injected memory for Claude Code: retrieval happens before the turn instead of through a tool the agent has to remember to call. SQLite FTS5, stdlib-only, Windows. Recall measured against a 125-question gold set on a real personal corpus.
```

## For a PKM / second-brain list

```markdown
- [Origin of Memory](https://github.com/Capslockiller/origin-of-memory) — Turns your Claude Code sessions into an Obsidian-compatible Markdown vault: a daily log per session, nightly compilation into cross-linked concept notes, and a searchable root map. Windows.
```

## Rules that apply to all of them

- **Say "Windows" in the entry.** Volunteering the constraint is what earns the
  entry; discovering it after installing is what earns a complaint.
- **No superlatives.** No "best", "powerful", "revolutionary", "seamless".
- **Do not quote a recall figure in a list entry.** It cannot carry the
  "measured on one corpus" caveat in that space, and a number without its caveat
  is a misrepresentation. Save it for the PR description if the maintainer asks.
- **Do not claim adoption.** No user counts, no "used by", no stars.
- If the list is cross-platform in scope, consider not submitting at all rather
  than making a maintainer decline it.
