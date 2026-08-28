# Show HN post — draft

**Draft only. Not posted.** Posting is the operator's hand, and only when there
is time to sit with it for the first few hours and answer. See
[../discoverability.md](../discoverability.md#8-announcement-posts--pending-optional).

---

## Title

```
Show HN: Origin of Memory – persistent memory for Claude Code (Windows)
```

## Body

```
Every Claude Code session is summarised into a daily log; a nightly compiler
distils those logs into a linked Markdown knowledge base; a hook injects the
relevant notes into every prompt. The model never has to remember to call a
search tool — the hook selects before the turn starts.

Measured on my own vault, against 125 real questions I had actually asked:
recall@3 83.2% (104/125), recall@5 91.2%, p95 hook latency 347 ms. The 0%
baseline is literal — the read hook had been registered at project scope for
a folder I never worked in.

Honest limits. Windows only; the hooks are PowerShell. One corpus, one
person's questions, and the gold set can't be published because it's my real
conversations, so the numbers aren't independently reproducible. Redaction
covers credential patterns only, not health, legal or financial detail.
Python 3.12 stdlib, no dependencies, no vector database.

MIT: https://github.com/Capslockiller/origin-of-memory
```

## Notes for whoever posts it

- The body above is **147 words**, link line included. If it needs to be shorter, cut the first
  paragraph's second sentence — never the limits paragraph.
- **Lead with the number and the limits, in that order, and never separate
  them.** Both paragraphs have to survive together or neither should be posted.
- Do not add adjectives to the title. `Show HN: <name> – <what it does>` is the
  form, and the platform constraint belongs in it.
- Do not mention the external code review here. It is a review, not an
  endorsement, and on HN it will read as one.
- Be ready to answer, in the first hour: why not embeddings (hook latency
  budget, CPU transformer startup does not fit), why not a tool call (agents
  under-call retrieval tools), why Windows (that is the machine it was built
  on), and why the gold set is not published (it is real personal
  conversations).
- If someone asks for the recall number to be reproduced, the honest answer is
  that it cannot be, and that a second corpus from someone else is the single
  most valuable contribution the project could receive.
