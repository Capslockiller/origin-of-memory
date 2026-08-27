---
name: gece-vardiyasi
description: "Overnight autonomous shift protocol. Triggers ONLY when the operator explicitly starts it with a task — 'gece vardiyası: <görev>' or an unmistakable equivalent ('sabaha kadar şunu koş'). Runs the given task draft-only until morning on this machine: no push, no deploy, no deletes, no sends — irreversible actions queue for morning approval. Writes the shift report to the vault. Do NOT trigger for ordinary long tasks; the phrase and the overnight intent must both be present."
---

> **Genericized from the author's working set — adapt paths and names to your
> setup. The operator prunes this set before relying on it.** The Turkish
> trigger phrase is kept deliberately: it is the invocation surface, and
> translating it would break the trigger.

# Gece Vardiyası — overnight shift protocol

Modeled on Avenox's Hermes experiment (agent as overnight employee,
draft-then-approve), scaled to a single machine and to the `orchestration`
policy in this same skill set. The shape: it runs on the operator's own machine
while it is left on, starts only when the operator hands over a task, and ends
with an approval gate and a morning report exactly as below.

## Start ritual (before any work)

1. A shift needs an explicit task from the operator. No task, no shift — never
   self-assign one.
2. Restate the task in one line plus the planned approach in two or three lines,
   name the stop conditions, and remind the operator ONCE: sleep kills the shift
   — check the machine's sleep settings (`powercfg /a` on Windows) and disable
   sleep. Then begin; no further permission theater.
3. Create the shift's worktree or branch immediately — never work on the live
   branch overnight — and write the report file header (see below) so a crash
   mid-shift still leaves a trace.

## Hard gates (the draft-only contract)

The night produces **drafts and evidence, never commitments**:

- NO `git push`, NO deploy, NO production access of any kind. If anything in
  scope is live, the server is untouchable at night.
- NO deletes, NO file moves outside the shift worktree, NO writes to the vault
  except the report file below.
- NO messages leaving the machine (mail, chat, API posts), NO purchases, NO
  account or configuration changes.
- NO full-auto or full-access sandbox escalation for external CLI lanes, NO new
  permanent rules, NO direction-setting choices — a fork with two reasonable
  paths goes into the morning queue with both paths written out, and the shift
  continues on whatever does NOT depend on the fork; if everything depends on
  it, the shift ends early with the report explaining why.
- Delegation follows `orchestration` unchanged: explicit `model:` on every
  spawn, the stated ceilings, and external CLI lanes read-only or
  workspace-write inside the shift worktree only.

Anything irreversible becomes a queue entry instead: the exact action, why, and
the exact command the operator runs (or approves) in the morning.

## Rhythm

- Work in cycles; after each meaningful unit, append a checkpoint line to the
  report file (time, what, evidence). The report is written DURING the shift,
  not reconstructed at dawn.
- Self-verify per policy: completions are claims — run the acceptance check
  (tests, typecheck, build) before recording a unit as done.
- Stop conditions: task done · blocked on an operator-only decision · the same
  failure three times (record it, move on or stop — never grind a loop) ·
  morning. On stop: finalize the report, update the companion memory files per
  the normal memory protocol, and leave the worktree intact for review.

## Morning report — `<vault>\<companion dir>\Gece-Vardiyası.md`

One file, overwritten per shift. The previous shift's report is moved verbatim
to the archive first (`<vault>\<archive dir>\Gece-Vardiyaları-YYYY-MM.md`, newest
at the top) — the same convention as the last-session file. Sections, in order:

1. **Görev ve sonuç** — one line each: what was assigned, what state it reached.
2. **Yapılanlar** — the checkpoint log (time · unit · evidence source).
3. **Sabah onayı bekleyenler** — the queue: action · reason · exact command. An
   empty section stays present, explicitly marked "(boş)".
4. **Yapılmayanlar ve nedenleri** — skipped, failed, or fork-blocked work.
5. **Kanıt yolları** — worktree or branch name, log locations, test outputs.

Report language follows the operator's. Evidence claims name their source; a
number without a source does not go in the report.
