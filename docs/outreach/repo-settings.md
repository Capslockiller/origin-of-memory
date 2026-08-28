# Repository settings to apply

**A list, not an action.** Everything here is applied by hand in the GitHub UI
(or with `gh`) by the operator. This page exists so the reasons travel with the
settings, and so the state of the repository is recorded somewhere other than
the GitHub UI.

Status column as of **2026-08-28**. Live checklist:
[../discoverability.md](../discoverability.md).

---

## Already applied

| Setting | Value | Why |
| --- | --- | --- |
| Description | set | It is the line that appears in search results and in the sidebar of every fork. |
| Topics | 16 set | Topics are the main non-search discovery surface on GitHub, and they reach four different audiences (Claude Code, PKM, agent infrastructure, mechanism). |
| Issues | enabled | A project that ships issue templates and asks for bug reports must have somewhere to put them. |
| Releases | `v0.1.0`, `v0.2.0` | Release feeds, package trackers and awesome-list maintainers key off tags. |

## To apply

### 1. Enable Discussions

**Settings → General → Features → Discussions.**

`.github/ISSUE_TEMPLATE/config.yml` already routes questions, ideas and
"here are my own measured numbers" to three Discussion categories. Until
Discussions is on, those three links 404 from inside the issue chooser, which is
the first thing a new user sees.

Create these categories (or rename the defaults to match the config):

| Category | Slug used in `config.yml` | Format |
| --- | --- | --- |
| Q&A | `q-a` | Question / answer |
| Ideas | `ideas` | Open-ended discussion |
| Show and tell | `show-and-tell` | Open-ended discussion |

**Enable it only with the intent to answer.** An unanswered discussion board
reads worse than no board. If that intent is not there, the honest alternative
is to leave Discussions off and delete the three Discussion entries from
`config.yml`, leaving only the security link.

### 2. Enable private vulnerability reporting

**Settings → Code security → Private vulnerability reporting.**

`SECURITY.md` tells reporters to use it, and
`.github/ISSUE_TEMPLATE/config.yml` links straight to
`/security/advisories/new`. If it is off, the documented reporting channel does
not exist and a reporter's only remaining option is a public issue — which is
the outcome the policy is written to prevent.

### 3. Upload the social preview image

**Settings → General → Social preview.**

Specification: [social-preview.md](social-preview.md). 1280 × 640 PNG. Until
this is set, every link to the repository — in a Show HN comment, a Discord
message, an awesome-list PR — renders as a grey card with no information.

### 4. Homepage / website field

**Repository → About → Website.**

**Recommendation: leave it empty.** There is no GitHub Pages site and no
documentation host, and the alternatives are all worse than blank:

- Pointing it at the repository itself is redundant — the About panel is already
  on the repository.
- Pointing it at the upstream project would imply an affiliation that
  [../attribution.md](../attribution.md) is careful to disclaim.
- Pointing it at a personal site attaches an identity the repository has
  deliberately kept out of its tree.

Revisit only if `docs/` is ever published as a Pages site; at that point the
entry point would be `docs/install.md`.

### 5. Pin the latest release

**Repository home → Releases → the release → "Pin release"** (or the
repository's "Customize pins" if pinning a release is not available in the
current UI).

A visitor who lands on the repository sees the file tree first. The pinned
release is what tells them the project is at `v0.2.0` and moving, rather than a
snapshot someone abandoned.

### 6. Branch protection on `main` — if wanted at all

**Settings → Branches → Add branch ruleset.**

For a single-maintainer project this is a judgement call, not an obligation: it
mainly protects against your own mistakes, at the cost of making every push a
two-step. If it is wanted:

- **Require status checks to pass before merging**, and select the CI workflow's
  jobs — it runs the pytest suite on `windows-latest` against Python 3.12 and
  3.13. This is the setting worth having: it is what keeps the README's test
  badge honest.
- **Require a pull request before merging** is the one that costs the most for a
  solo maintainer. Skip it, or enable it with "Allow specified actors to bypass"
  set to yourself.
- **Do not** enable "Require linear history" or signed commits unless you
  already work that way; both fail confusingly later, at a bad moment.

### 7. Leave alone

- **Funding.** `.github/FUNDING.yml` is a commented-out template. Enabling the
  Sponsor button is a decision about how the project presents itself, not a
  default. It stays off until deliberately turned on.
- **Wiki.** Off. The documentation is in `docs/`, versioned with the code; a
  wiki is a second copy that drifts.
- **Projects.** Off unless actually used.
- **Auto-merge, Discussions-to-issue conversion, and issue forms' auto-labels**
  beyond the `bug` / `enhancement` labels the templates already apply — no
  reason to change any of them yet.

## Labels the templates assume

`bug_report.yml` applies `bug` and `feature_request.yml` applies `enhancement`.
Both exist by default on a new GitHub repository, so nothing needs creating;
if either has been deleted, recreate it or the template's `labels:` entry
silently does nothing.

`dependabot.yml` applies a `dependencies` label to its pull requests. That one
is **not** a default label — create it (Issues → Labels → New label) or
Dependabot's PRs arrive unlabelled. It is cosmetic either way.
