---
name: orchestration
description: "Delegation policy: which tier drives, which tier gets which lane, what every brief must carry, and where a delegate's output goes. The apex tier is rationed rather than resident, and any move that sets direction is confirmed with the operator before it is built. Load whenever work is about to be split across agents — before any Agent tool call, before running a Workflow that contains agent() calls, and before spawning a codex fleet."
---

> **Genericized from the author's working set — adapt paths and names to your
> setup. The operator prunes this set before relying on it.** Model names, tier
> ceilings and platform notes below are one operator's configuration and one
> machine's measurements, not universal advice.

# Orchestration (main-loop delegation policy)

A routing policy for a stack with **one rationed apex tier, an elastic main
loop, and cheaper delegate tiers**. Adapted from Avenox's `fable-orchestration`
(MIT), which assumes a main loop resident at the top of the stack. This one is
not resident, and most of what follows is that difference worked through.

Core law: **the driving tier must not do pleb work, and must never be spawned
as a delegate.** Its tokens buy judgment, not throughput.

## The hard rules

1. **Every spawn names its model.** Set `model:` explicitly on every Agent
   call, every Workflow `agent()` call and every `meta.phases` entry, and
   `effort:` where the call takes one. Omit-and-inherit fans the session model
   out across every lane — with the apex tier driving, that turns a grep sweep
   into an apex lane. The rule existed here before the mechanism did.

2. **Whoever is driving is never also a delegate.** When the apex tier drives,
   the tier below takes the judgment lanes. When that tier drives, judgment
   stays in the main loop and the lanes are the worker tier, the trivia tier or
   an external CLI — same-tier lanes spawned from a same-tier session spend the
   budget the synthesis will need.

3. **Two ceilings: three in-process sub-agents, four external CLI lanes**,
   staggered two to five seconds apart. In-process sub-agents share the host
   process and five at once crashed the app twice on the author's machine;
   external CLI lanes are separate OS processes with a different failure
   profile, still unmeasured. ⚠ Neither number is itself a measurement — the
   crash was never diagnosed. Raising either is the operator's call, after a
   ramp run.

4. **Completions are claims, not evidence.** A lane reporting success has
   reported an opinion. Evidence is opening the artifact, running the lane's
   acceptance check yourself, or a number whose source you can name. A green
   check is not proof; logic verified is not environment verified; the
   operator's agreement is not proof either. Measurement is.

5. **Lanes do not touch each other.** A lane reads, interprets and files only
   its own work; another branch's files, logs and output are out of bounds even
   when they look wrong — report a sibling's edits, leave them intact. Conflicts
   go up, never merged sideways.

## Tiers

**The apex tier is rationed, not resident.** The source policy runs its main
loop at the top of the stack permanently; that holds on a subscription with the
headroom for it, and this one does not. So the stack is elastic: **the
second-highest tier drives by default, and the apex is called in rather than
left running.**

- **Apex tier — reserved.** Architecture that would be expensive to reverse,
  contract-sensitive design, cross-cutting synthesis, judgment where being wrong
  costs more than the tokens. Come back down once it is settled.
- **Driving tier — the default main loop.** Specs, integration, conflict
  resolution, and the judgment that does not need the apex.
- **Worker tier — the default delegate.** Exploration, codebase mapping, grep
  sweeps, listing, bulk moves, applying a spec that is already written.
- **Trivia tier — one-shot lookups only.** If the lane needs a judgment, it is
  not a trivia lane.
- **External CLI — two lanes in one binary** (`codex-fleet` carries the
  mechanics). At high reasoning effort it is the spec-bound implementer:
  executes rather than improvises, bounded by its brief. At low/medium effort
  with a read-only sandbox it is the cheap reader — see the rule below.

**Context gathering splits by what it produces** — a pile → the worker tier; a
decision (classification, audit, "what goes where") → the judgment tier.
Measured on the author's stack: the worker tier split files by type where the
organising context was the project, and the judgment tier had to fix it.
**Gate-review lanes take the highest tier available**, whatever they review; but
*whether* an expensive gate runs is the operator's call.

**Cheap reads, expensive decides.** Reading work that produces no decision —
reconnaissance, inventory, log sweeps, "what is in this repo", web research —
goes to the cheapest fitting lane, on either side of the stack, under the
existing subscriptions; no separate budget tool gets installed for this. The
moment the output is a decision, the lane stops being cheap and the judgment
tier takes it; **a cheap lane never holds write access.** Adapted from Avenox's
`omp-fleet` doctrine, whose trap note holds here too: an under-specified model
id silently upgrades a cheap lane to an expensive one — the explicit
`model:`/effort rule above is what prevents it.

**Delegation's threshold is the brief, not the limit.** Delegate when writing
the brief costs less than doing the work, and manage from above; don't delegate
a lane you could finish in the time it takes to specify it.

## Asking before acting

The source policy says: don't ask, just go — collect errors, review at the end,
fix fast. That assumes an operator who already trusts the agent's grasp of the
project. **Where that trust is not yet established, the failure it guards
against is not a bug — it is a project that quietly went somewhere the operator
did not want.** A wrong direction costs more than the question that catches it.

**Execute without asking only while the work is read-only, reversible and
inside what was already agreed.** Exploring, searching, reading, measuring —
go, and don't narrate the permission you don't need.

**Ask before anything that sets direction:** structure, layout, approach,
naming, what gets built and in what order, the first write of a new file, and
every fork where two reasonable paths exist. Surface the fork, don't pick it,
and never bank a decision for later review — by then the work is standing on it.

Four hard stops: **writing outside the lane's OWNS list**, **deleting
anything**, **network or full-auto escalation**, and **any deviation from the
plan already described to the operator** — the last is not a judgment call, it
is a full stop and a question. A hold order ("stand by") means nothing happens
at all, vault writes included. A permanent rule is shown to the operator in full
text before it is written anywhere.

## What every brief carries

A delegate cannot see the conversation, so the brief is its entire contract: the
goal in one line · **OWNS**, the exact paths this lane may write ·
**DO-NOT-TOUCH**, everything else, naming which sibling owns what · the
acceptance check it runs before reporting · the report format · the platform
note below. Shared files (barrels, entry points, validators) are **append-only**
and allocated to one lane at integration. Read lanes stay read-only and are
never told to patch — escalate instead. Briefs are written in English even when
the operator is addressed in another language.

## Where a lane's output goes

**Summarize from the logs; never dump raw stdout into the main loop.** The log
is a transcript, not a report — volatile, noisy, gone with the session. What
returns is the distilled finding, in the operator's language, with its sources
named.

**Raw research is allowed into the vault.** A lane that files its report writes
it itself: `YYYY-MM-DD-topic.md` under the topic's raw-research folder,
frontmatter marking it raw, findings plus sources plus an explicit "could not
verify" list — no transcript, no tool output. No line ceiling and no "only when
asked" gate; filing raw is still not mandatory — the default deliverable remains
the distilled finding in the answer or the synthesis note. Upkeep belongs to the
mechanism: the nightly compiler distills, the health-check skill reports bloat,
and a bloat warning goes up to the operator.

## Concurrent write lanes

Lanes writing to one repo at once clobber each other in a shared checkout: give
each its own worktree at a pinned base commit, one commit per lane,
cherry-picked onto the trunk in completion order with the gate run at every
pick. Prove the gate green on a baseline worktree first. **A lane whose log has
not grown for minutes with zero tool calls is dead**, whatever the process table
says — respawn it with the same brief once `git status` shows no partial work.

## Platform

On Windows with a POSIX shell available, **run command blocks through that shell
rather than inline PowerShell** wherever paths carry non-ASCII characters or
emoji. This is measured on the author's machine, not cautionary: a PowerShell
move once mangled a non-ASCII path and dropped fourteen desktop shortcuts into
the working directory. When PowerShell is unavoidable, write a BOM'd UTF-8
script and run it with `-File`. Logs go to the session scratchpad, not a system
temp path.

## Exceptions

The operator can override any line here for a single run by saying so; absent
that, this policy stands. A deviation is announced with its reason, never taken
silently.

Adapted from **Avenox** ([avenox.lol](https://avenox.lol), MIT). The tier map,
the approval posture and the reporting rule are the operator's and override the
source where they disagree.
