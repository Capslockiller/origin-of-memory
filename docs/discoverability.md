# Discoverability checklist

An operator checklist for after the repository goes public.

**Every item below is the operator's hand, not automated.** Nothing here is
performed by the pipeline, by a hook, by CI, or by an agent. These are actions a
human takes deliberately, in their own account, with their own judgement about
timing and tone. Several of them post to other people's spaces; treat those as
requests, not entitlements.

Work top to bottom — the repository should look finished before anyone is invited
to look at it.

---

## 1. Repository metadata

**Operator's hand. GitHub → repository → About.**

Suggested description line (one sentence, under 120 characters so it does not
truncate in search results):

> Persistent memory for Claude Code: auto-summarised sessions, a nightly
> knowledge compiler, and BM25 retrieval injected into every prompt.

Alternative if a shorter line is wanted:

> A second brain for Claude Code — session memory that compiles itself and comes
> back when it is relevant.

Suggested topics:

```
claude-code  claude  second-brain  agent-memory  memory
obsidian  pkm  llm  hooks  fts5  windows
```

Notes on the topic list:

- `claude-code` and `claude` are how people actually search for this category.
- `second-brain`, `pkm` and `obsidian` reach the note-taking audience; the vault is
  Obsidian-compatible even though Obsidian is optional.
- `agent-memory` and `memory` reach the agent-infrastructure audience, which is a
  different crowd with different questions.
- `fts5` and `hooks` are precise, low-traffic tags that reach people looking for
  the specific mechanism.
- `windows` is there so nobody arrives, installs, and then discovers the platform
  constraint. Under-promising on platform is the point.

Also on the About panel: set the website field to the repository's own docs if a
Pages site is ever published; leave it empty otherwise rather than pointing it
somewhere unrelated.

## 2. Repository settings

**Operator's hand. GitHub → Settings.**

- Enable **Issues**. A project asking for bug reports must have somewhere to put
  them.
- Enable **Discussions** only if there is intent to answer them. An unanswered
  discussion board reads worse than no board.
- Enable **private vulnerability reporting** (Settings → Code security). This is
  what [../SECURITY.md](../SECURITY.md) tells reporters to use, so it must
  actually be on.
- Confirm the default branch name matches what the README's clone instructions
  imply.
- Confirm the `Capslockiller` account references in the README badges, the clone command, the
  CHANGELOG comparison links and `INSTALL-AGENT.md` with the real account or
  organisation.

## 3. Pre-launch content check

**Operator's hand. Before any outward link is posted.**

- [ ] CI is green on the default branch, so the README badge is not the first thing
      a visitor sees failing.
- [ ] `install.ps1 -DryRun` has been run on a clean machine and its output matches
      what the README's quickstart describes.
- [ ] No personal data anywhere in the tree: no real names, no local paths from the
      author's machine, no vault content, no gold questions. Grep before pushing,
      and grep the git history too — a removed file is still in the history.
- [ ] `README.tr.md` and `README.md` describe the same system. They will drift;
      catch it before launch, not after.
- [ ] Every relative link resolves.
- [ ] `skills/` contains only what should ship. The installer copies the whole
      directory into the user's skills folder, so anything left here becomes
      active on someone else's machine.
- [ ] The agent install line in §7 has been tested end to end, not just read.

## 4. Cut a v0.1.0 release

**Operator's hand. GitHub → Releases → Draft a new release.**

- Tag `v0.1.0` on the launch commit.
- Title: `v0.1.0`.
- Body: the `## [0.1.0]` section of [../CHANGELOG.md](../CHANGELOG.md), plus one
  short paragraph naming the platform constraint and the fact that the measured
  numbers come from a single corpus. The release notes are frequently the only
  thing a skim-reader reads.
- Do not attach installer binaries. `git clone` plus `install.ps1` is the
  supported path, and a downloadable artefact invites people to skip reading.

A tagged release matters more than it looks: release feeds, package trackers and
"awesome list" maintainers key off tags, and a repository with no releases reads
as unfinished.

## 5. Awesome-list submissions

**Operator's hand. One PR at a time, each following that list's contribution
rules.**

Candidate lists: the `awesome-claude-code` family, Claude Code hooks and skills
collections, second-brain and PKM tooling lists, and agent-memory / LLM-memory
lists.

Before opening any of them:

- Read the list's `CONTRIBUTING` file. Most specify entry format, alphabetical
  position, and a minimum maturity bar (stars, releases, documentation). Ignoring
  it is the usual reason a PR sits unmerged.
- Check whether the project actually fits the list's scope. A Windows-only tool in
  a cross-platform list is a reasonable rejection, and submitting anyway costs the
  maintainer time.
- Write the entry in that list's voice, one line, and say "Windows" in it.
  Volunteering the constraint is what earns the entry.
- One PR per list. Do not open six at once; if the first gets feedback about
  framing, the other five are already wrong.

## 6. The agent-distribution channel

**Operator's hand. This is the single highest-leverage discovery surface for a
tool whose users are already sitting in an agent.**

[`INSTALL-AGENT.md`](../INSTALL-AGENT.md) is written to be self-contained and
addressed directly to an agent: prerequisites, clone, dry run, install, verify,
troubleshoot, uninstall. A user does not need to read it — they paste its raw
URL to their agent and the agent does the install. This is the pattern upstream
uses with `avenox.lol/codex.md`, and it works because it collapses the install
funnel to one line.

Once the repository is public, the raw URL is:

```
https://raw.githubusercontent.com/Capslockiller/origin-of-memory/main/INSTALL-AGENT.md
```

The line to publish alongside it, in the README and in any announcement:

> In Claude Code, paste:
> `Read https://raw.githubusercontent.com/Capslockiller/origin-of-memory/main/INSTALL-AGENT.md and follow it exactly to install my second brain.`

Operator checklist for this channel:

- [ ] Confirm the raw URL resolves after the repo goes public, and that the
      branch in the path matches the default branch.
- [ ] Test the whole flow once on a clean machine, by actually pasting the line
      into an agent and watching it work — a broken install line is worse than
      no install line, because it fails in front of a new user.
- [ ] Consider a short vanity URL that redirects to the raw file, if a domain is
      available. Keep the raw URL working regardless; redirects break.
- [ ] Re-test after any edit to `INSTALL-AGENT.md`. It is the one file where a
      stale instruction is executed rather than read.
- [ ] Never point the line at a URL you do not control. Users are being asked to
      let an agent follow instructions from it.

## 7. Link-back to upstream

**Operator's hand. A request, not an expectation.**

This project is adapted from avenoxbeyin (see
[attribution.md](attribution.md)). A reciprocal link would help users find the
right platform for their machine — macOS and Linux users are better served
upstream, Windows users are better served here.

Suggested approach: open an issue on
[avenoxbeyin](https://github.com/avenoxai/avenoxbeyin) that:

- states plainly that this project is adapted from theirs, built clean-room from
  SPEC-V2, and credits them in its README, its changelog and a dedicated
  attribution page;
- describes what is different in one short paragraph (native Windows, per-prompt
  retrieval, root map, ingest, evaluation);
- **asks** whether they would consider a "ports and adaptations" line in their
  README, and offers to open the PR themselves;
- makes clear that a no is fine.

Do not open the PR to their README unsolicited. Do not describe this project as a
successor, an upgrade, or a v3 of theirs — it is a Windows adaptation with extra
layers, and overclaiming would be both rude and inaccurate.

## 8. Optional announcement posts

**Operator's hand, entirely optional, and best done days after launch rather than
on the same day — a repository that gets attention before its issues are answerable
converts interest into abandoned issues.**

- **Show HN.** Title as `Show HN: <name> – <what it does>`, no marketing adjectives.
  Lead the comment with the honest limitations: Windows-only, measured on one
  corpus, no sensitive-data filtering. HN rewards this and punishes the opposite.
  Be present for the first few hours to answer, or do not post.
- **r/ClaudeAI** and adjacent subreddits. Read each subreddit's self-promotion
  rules first; several require a flair, a minimum account age, or restrict
  project posts to a weekly thread. A post that breaks the rule is removed and
  costs the account standing.
- **Anywhere else** — Obsidian forums, PKM communities, Turkish-language developer
  communities where the Turkish support is a genuine differentiator. Same
  principle: read the rules, post once, answer replies.

Across all of these: link the repository, not a blog post about the repository.
State the platform constraint in the first two sentences. Never present the 83%
recall figure without the sentence that says it was measured on one corpus.

## 9. After launch

**Operator's hand, ongoing.**

- Answer the first issues quickly. Early responsiveness sets the expectation for
  whether the project is alive.
- When someone reports that FTS5 is missing in their Python build, that is a
  documentation bug — fix the README, not just the issue.
- Keep [../CHANGELOG.md](../CHANGELOG.md) current and tag releases. Each tag is
  another discovery surface.
- If someone builds their own gold set and reports numbers, that is the most
  valuable contribution the project can receive — a second corpus is the only
  thing that turns [evaluation.md](evaluation.md)'s numbers into evidence rather
  than an anecdote. Ask for it explicitly.
