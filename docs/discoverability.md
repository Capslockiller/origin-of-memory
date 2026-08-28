# Discoverability checklist

A live checklist of what has been done to make this repository findable, and
what has not. Update the boxes as things land — a checklist that lies about its
own state is worse than no checklist.

**Every item here is the operator's hand, not automated.** Nothing on this page
is performed by the pipeline, by a hook, by CI, or by an agent. These are actions
a human takes deliberately, in their own account, with their own judgement about
timing and tone. Several of them post to other people's spaces; treat those as
requests, not entitlements.

Status as of **2026-08-28**.

---

## 1. Repository metadata — mostly done

**Operator's hand. GitHub → repository → About.**

- [x] **Description set.** One sentence, under 120 characters so it does not
      truncate in search results.
- [x] **Topics set — 16 of them.** They cover four audiences: the Claude Code
      crowd (`claude-code`, `claude`), the note-taking crowd (`second-brain`,
      `pkm`, `obsidian`), the agent-infrastructure crowd (`agent-memory`,
      `memory`), and precise low-traffic mechanism tags (`fts5`, `hooks`).
      `windows` is deliberately included so nobody arrives, installs, and only
      then discovers the platform constraint. Under-promising on platform is the
      point.
- [ ] **Website field.** Currently empty, which is correct: there is no Pages
      site, and pointing it at anything unrelated is worse than leaving it
      blank. Revisit only if docs are ever published as a site.
- [ ] **Social preview image.** Not uploaded. This is the most visible remaining
      gap — every link shared anywhere renders as a bare grey card until it is
      set. Specification: [outreach/social-preview.md](outreach/social-preview.md).

**Live values, recorded here so this file is the record and not the GitHub UI:**

Description:

> Persistent memory for Claude Code — a second brain: automatic session
> capture, nightly knowledge compiler, FTS5 BM25 retrieval injected into every
> prompt. Runs on Claude, Antigravity, Ollama or any local OpenAI-compatible
> model. Windows-native, stdlib-only, MCP server, setup wizard, Turkish-aware.

Topics (16): `agent-memory` `claude` `claude-code` `fts5` `hooks`
`knowledge-base` `llm` `local-llm` `mcp` `memory` `obsidian` `ollama` `pkm`
`powershell` `second-brain` `windows`

## 2. Repository settings

**Operator's hand. GitHub → Settings.** The full list with reasons:
[outreach/repo-settings.md](outreach/repo-settings.md).

- [x] **Issues enabled.** A project asking for bug reports must have somewhere
      to put them.
- [x] **Discussions.** Enabled 2026-08-28. `.github/ISSUE_TEMPLATE/config.yml`
      points questions at three Discussion categories; verify those categories
      exist in the repository's Discussions settings, since the default set may
      not match the names the config uses. Enabled with the intent to answer —
      an unanswered discussion board reads worse than no board.
- [ ] **Private vulnerability reporting** (Settings → Code security). This is
      what [../SECURITY.md](../SECURITY.md) tells reporters to use, so it must
      actually be on.
- [ ] **Pin the latest release** on the repository home page.
- [ ] **Branch protection on `main`** with "Require status checks" bound to the
      CI workflow, if branch protection is wanted at all.
- [x] Default branch name matches what the README's clone instructions imply.
- [x] The `Capslockiller` account references in the README badges, the clone
      command, the CHANGELOG comparison links and `INSTALL-AGENT.md` are the
      real account.

## 3. Content check

**Operator's hand. Re-run before any outward link is posted.**

- [x] Every relative link resolves.
- [x] `README.tr.md` and `README.md` describe the same system, section for
      section. They will drift again; catch it before the next launch push.
- [x] No personal data anywhere in the tree: no real names, no local paths from
      the author's machine, no vault content, no gold questions. Grep before
      pushing, **and grep the git history too** — a removed file is still in the
      history.
- [ ] CI is green on the default branch, so the README badge is not the first
      thing a visitor sees failing. (Re-check after every push; this box is a
      recurring one.)
- [ ] `install.ps1 -DryRun` has been run on a clean machine and its output
      matches what [install.md](install.md) describes.
- [x] `skills/` contains only what should ship. The installer copies the whole
      directory into the user's skills folder, so anything left here becomes
      active on someone else's machine.
- [ ] The agent install line in §6 has been tested end to end, not just read.

## 4. Releases — done, and now recurring

**Operator's hand. GitHub → Releases.**

- [x] **`v0.1.0`** tagged and released (2026-08-27).
- [x] **`v0.2.0`** tagged and released (2026-08-28). Notes kept in the
      repository at [release-notes-0.2.0.md](release-notes-0.2.0.md).
- [ ] Pin the current release on the repository home page (see §2).
- [x] No installer binaries attached. `git clone` plus the wizard is the
      supported path, and a downloadable artefact invites people to skip
      reading.

A tagged release matters more than it looks: release feeds, package trackers and
"awesome list" maintainers key off tags, and a repository with no releases reads
as unfinished. Keep the cadence — each tag is another discovery surface.

## 5. Awesome-list submissions — pending

**Operator's hand. One PR at a time, each following that list's contribution
rules.** A ready-to-paste entry line is drafted in
[outreach/awesome-list-entry.md](outreach/awesome-list-entry.md).

- [ ] `awesome-claude-code` family
- [ ] Claude Code hooks / skills collections
- [ ] Second-brain and PKM tooling lists
- [ ] Agent-memory / LLM-memory lists

Before opening any of them:

- Read the list's `CONTRIBUTING` file. Most specify entry format, alphabetical
  position, and a minimum maturity bar (stars, releases, documentation).
  Ignoring it is the usual reason a PR sits unmerged.
- Check whether the project actually fits the list's scope. A Windows-only tool
  in a cross-platform list is a reasonable rejection, and submitting anyway
  costs the maintainer time.
- Write the entry in that list's voice, one line, and say "Windows" in it.
  Volunteering the constraint is what earns the entry.
- One PR per list. Do not open six at once; if the first gets feedback about
  framing, the other five are already wrong.

## 6. The agent-distribution channel — live, untested

**Operator's hand. This is the single highest-leverage discovery surface for a
tool whose users are already sitting in an agent.**

[`INSTALL-AGENT.md`](../INSTALL-AGENT.md) is written to be self-contained and
addressed directly to an agent: prerequisites, clone, dry run, install, verify,
troubleshoot, uninstall. A user does not need to read it — they paste its raw
URL to their agent and the agent does the install. This is the pattern upstream
uses with `avenox.lol/codex.md`, and it works because it collapses the install
funnel to one line.

The raw URL:

```
https://raw.githubusercontent.com/Capslockiller/origin-of-memory/main/INSTALL-AGENT.md
```

The line to publish alongside it, in any announcement:

> In Claude Code, paste:
> `Read https://raw.githubusercontent.com/Capslockiller/origin-of-memory/main/INSTALL-AGENT.md and follow it exactly to install my second brain.`

- [ ] Confirm the raw URL resolves and the branch in the path matches the
      default branch.
- [ ] Test the whole flow once on a clean machine, by actually pasting the line
      into an agent and watching it work — a broken install line is worse than
      no install line, because it fails in front of a new user.
- [ ] Consider a short vanity URL that redirects to the raw file, if a domain is
      available. Keep the raw URL working regardless; redirects break.
- [ ] Re-test after any edit to `INSTALL-AGENT.md`. It is the one file where a
      stale instruction is executed rather than read.
- [x] The line points only at a URL the operator controls. Users are being asked
      to let an agent follow instructions from it.

## 7. Link-back to upstream — pending

**Operator's hand. A request, not an expectation.** Draft text:
[outreach/upstream-linkback.md](outreach/upstream-linkback.md).

- [ ] Open an issue on
      [avenoxbeyin](https://github.com/avenoxai/avenoxbeyin) that states plainly
      that this project is adapted from theirs, built clean-room from SPEC-V2,
      and credits them in its README, its changelog and a dedicated attribution
      page; describes what is different in one short paragraph; **asks** whether
      they would consider a "ports and adaptations" line in their README, and
      offers to open the PR themselves; and makes clear that a no is fine.

Do not open the PR to their README unsolicited. Do not describe this project as
a successor, an upgrade, or a v3 of theirs — it is a Windows adaptation with
extra layers, and overclaiming would be both rude and inaccurate.

## 8. Announcement posts — pending, optional

**Operator's hand, entirely optional, and best done days after launch rather
than on the same day** — a repository that gets attention before its issues are
answerable converts interest into abandoned issues. Draft:
[outreach/show-hn.md](outreach/show-hn.md).

- [ ] **Show HN.** Title as `Show HN: <name> – <what it does>`, no marketing
      adjectives. Lead with the honest limitations: Windows-only, measured on
      one corpus, no sensitive-data filtering. HN rewards this and punishes the
      opposite. Be present for the first few hours to answer, or do not post.
- [ ] **r/ClaudeAI** and adjacent subreddits. Read each subreddit's
      self-promotion rules first; several require a flair, a minimum account
      age, or restrict project posts to a weekly thread. A post that breaks the
      rule is removed and costs the account standing.
- [ ] **Anywhere else** — Obsidian forums, PKM communities, Turkish-language
      developer communities where the Turkish support is a genuine
      differentiator. Same principle: read the rules, post once, answer replies.

Across all of these: link the repository, not a blog post about the repository.
State the platform constraint in the first two sentences. Never present a recall
figure without the sentence that says it was measured on one corpus — and use
the corrected numbers (recall@3 83.2%, recall@5 91.2%), not the withdrawn 84%.

## 9. After launch — ongoing

- [ ] Answer the first issues quickly. Early responsiveness sets the
      expectation for whether the project is alive.
- [ ] When someone reports that FTS5 is missing in their Python build, that is a
      documentation bug — fix the docs, not just the issue.
- [ ] Keep [../CHANGELOG.md](../CHANGELOG.md) current and tag releases.
- [ ] If someone builds their own gold set and reports numbers, that is the most
      valuable contribution the project can receive — a second corpus is the
      only thing that turns [evaluation.md](evaluation.md)'s numbers into
      evidence rather than an anecdote. Ask for it explicitly; the
      Show-and-tell discussion category exists for it.
