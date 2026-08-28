# Upstream link-back — draft

**Draft only. Nothing sent.** This is a request to another maintainer, in their
space, and it is theirs to decline. See
[../discoverability.md](../discoverability.md#7-link-back-to-upstream--pending).

Two rules, both non-negotiable:

- **Open an issue, not a pull request.** Editing someone else's README without
  being asked is the rude version of this.
- **Never describe this project as a successor, an upgrade, or a v3 of theirs.**
  It is a Windows adaptation with extra layers. Overclaiming would be both rude
  and inaccurate — and [../attribution.md](../attribution.md) already says so in
  the repository.

---

## The two-sentence entry (what would go in their README)

This is the text the issue offers them. It is written in the third person so it
can be dropped into a "Ports and adaptations" list without editing:

```markdown
**[Origin of Memory](https://github.com/Capslockiller/origin-of-memory)** — a native Windows adaptation of avenoxbeyin v2, built clean-room from SPEC-V2 with PowerShell hooks and stdlib-only Python. It adds user-level hook registration, per-prompt FTS5 retrieval, a root-map layer and a gold-set evaluation; macOS and Linux users are better served here, Windows users there.
```

## The issue that offers it

Title:

```
Windows adaptation of v2 — would you want a link back?
```

Body:

```
Hi — I built a Windows-native adaptation of avenoxbeyin v2 and wanted to
tell you directly rather than have you find it.

It was built clean-room from SPEC-V2, not forked: there is no shared code
history. It credits you in the README, in the changelog and on a dedicated
attribution page, and it is MIT like yours.

What is different: PowerShell hooks instead of bash, stdlib-only Python,
hook registration at user level so memory is written and read from every
project, per-prompt FTS5/BM25 retrieval injected before the turn, a root-map
layer that bounds the compiler's input, credential redaction, an ingest
family for archived conversations, and a gold-set retrieval evaluation.

The ask, and a no is completely fine: would you consider a line in your
README pointing at it for Windows users? Your project is the one that is
actually tested on macOS, and mine is the one that is tested on Windows, so
a reciprocal pointer would send people to the right place. I have a
two-sentence entry drafted and would happily open the PR myself if you would
rather not write it — or I can leave it entirely.

Either way, thanks for SPEC-V2. It is a good spec.
```

## After sending

- If they say yes and ask you to open the PR: match their README's existing
  formatting exactly, one entry, no additions.
- If they say no, or do not answer: nothing changes. The attribution in this
  repository stands on its own and is not conditional on reciprocity.
- Either way, do not raise it a second time.
