# Security

This document describes what the memory pipeline defends against, what it
explicitly does **not** defend against, and how to report a vulnerability.

Read the second section before you install this on a machine where you discuss
anything you would not want written to disk in summarised form.

---

## Threat model

The pipeline reads your Claude Code transcripts, sends them to a model, and
writes the result to disk. That means three trust boundaries matter: what leaves
the machine, what arrives from untrusted sources, and what the compiler is
allowed to touch.

### 1. Credential leakage — partially handled

`scripts/secret_guard.py` runs on both sides of the summariser: the transcript is
redacted before it is sent, and the returned summary is redacted before it is
appended to `daily/`. `compile.py` additionally scans compiler output at the
promotion gate. Matches are replaced with `[SIR:<pattern-name>]` and the pattern
class is recorded in the health file.

**What it catches** — narrow, high-confidence credential shapes:

- PEM private key blocks
- AWS (`AKIA…`), Google (`AIza…`), GitHub (`ghp_`/`gho_`/…), Slack (`xox…`),
  Anthropic (`sk-ant-…`) and OpenAI (`sk-…`) key formats
- JWTs
- Credentials embedded in URLs (`scheme://user:PASSWORD@host` — the password span
  only)
- `Bearer <token>` headers
- Assignment forms: `password:`, `parola:`, `şifre:`, `secret`, `token`,
  `api_key=`, `access_key`, `private_key`, `client_secret`, `auth_token`

Placeholder-looking values (`${VAR}`, `<your-key>`, `REDACTED`, `CHANGEME`,
`EXAMPLE`, `ÖRNEK`, `****`) are skipped so that documentation and examples are
not mangled.

**What it does not catch.** The patterns are deliberately narrow. Anything wider
would corrupt free text. Consequences:

- A secret that does not match one of the shapes above — a bare password typed on
  its own line, an internal hostname, a customer identifier — passes through.
- Redaction is best-effort defence in depth, **not** a guarantee. Do not paste
  credentials into a session on the assumption that the guard will catch them.
- The guard runs only on the automated path. Content you place into the vault by
  hand is never scanned.

### 2. Sensitive personal context — NOT handled

This is the largest known gap, and it is stated plainly because the alternative
would be misleading:

**`secret_guard.py` is a credential filter. It is not a personal-data filter.**

It has no concept of, and takes no action on:

- health information (diagnoses, symptoms, medication, appointments)
- legal status, disputes or proceedings
- financial detail beyond credential-shaped strings
- third parties who never consented to being written down — named people
  discussed in your sessions become named people in your knowledge base
- location, relationships, employment, or anything else a rule-based detector
  cannot reliably recognise

Rule-based detection of this category performs poorly in the highest-precision
band, and that is before considering non-English text. There is no reliable
automated fix available here, so none is claimed.

**Practical consequences.** Everything that reaches a transcript can reach
`daily/`, then `knowledge/`, then back into every future session's context. If
you push your vault to a remote — even a private one — that content goes with it,
along with its git history. Treat the vault with the same care as the most
sensitive conversation you have ever had in a Claude Code session.

**Mitigations available today**, none of them automatic:

- Keep the vault out of any remote you do not fully control, and keep it out of
  backup services you have not audited.
- Do not run the memory pipeline on machines where you handle regulated data.
- Delete or edit a `daily/` file before 18:00 to keep it out of that evening's
  compile — the compiler only processes daily logs whose SHA-256 has changed since
  the last successful run, and re-processes an edited file rather than the
  original.
- Review `knowledge/concepts/` periodically. It is plain Markdown; deleting a note
  is a normal file deletion.

### 3. Untrusted content entering the compile path

Text fetched from the web can arrive in a Claude Code transcript — a fetched page,
a pasted document, a tool result. Claude Code's own `WebFetch` runs fetched pages
in a separate context, but that protection does not extend to text that has
already landed inside a transcript, and the flush hook summarises transcripts.
Summarisation is not sanitisation.

Defences in place:

- Every untrusted region in every prompt is delimited and labelled. `flush.py`
  wraps the transcript in `BEGIN/END UNTRUSTED TRANSCRIPT DATA`; `compile.py`
  wraps the root map, the duplicate-check registry and the daily body in their own
  `UNTRUSTED … DATA` blocks, and the prompt states that nothing inside them may be
  executed as an instruction, system message or tool call.
- Both scripts carry a `DIRECTIVE_SHAPED` pattern that recognises
  instruction-shaped lines (`INSTRUCTION:`, `SYSTEM:`, `TALİMAT:`,
  `IGNORE ALL PREVIOUS:`, …).
- Retrieved notes injected by `memory-retrieve.ps1` are prefixed with an explicit
  statement that their contents are data and that no sentence inside them is to be
  applied as an instruction.
- The compiler's write policy (below) bounds the damage: a successful injection
  still cannot write outside `knowledge/concepts/**`, `knowledge/index-full.md`
  and `knowledge/log.md`.

Residual risk: **prompt-injected content can still become a knowledge note.** A
poisoned page that reaches a transcript may end up summarised into `daily/`,
compiled into a concept, and then retrieved into future sessions as apparently
trusted memory. There is no content exclusion list yet. If you fetch untrusted
pages in a session, consider editing that day's `daily/` file before the evening
compile.

### 4. `claude -p` subprocess hardening

All model calls go through `scripts/claude_runner.py`, which:

- resolves `claude` via `shutil.which` rather than a shell string, and invokes it
  with an argument list — no shell, no interpolation;
- passes the prompt on **stdin**, never as a command-line argument;
- runs with `--safe-mode` and an explicit `--tools` list. The flush call is given
  **no tools at all** (`tools=""`); the compile call is limited to
  `Read,Write,Edit,Glob,Grep` with `--permission-mode acceptEdits` and a matching
  `--allowedTools`;
- sets `BEYIN_INVOKED_BY=beyin-scripts` in the child environment. Every hook and
  every entry-point script exits immediately when that variable is set, so the
  pipeline cannot recurse into itself;
- enforces timeouts (240 s for flush, 900 s for compile) and returns a typed
  error string rather than raising into the hook path;
- for the flush call, runs in a fresh temporary directory and **refuses to run**
  if that directory resolves inside the vault
  (`temporary-directory-inside-vault`);
- for the compile call, runs with the staging tree as the working directory, so
  the live vault is not reachable as a relative path.

The compiler's write policy is enforced after the model returns, not trusted from
it: the staging tree is manifested before and after the run, deletions and type
changes raise `PolicyError`, and only files matching the allow-list are promoted —
each one re-validated against its expected live-side digest and confirmed to
resolve inside `knowledge/` before an atomic replace.

### 5. Local state stays local

- Runtime state lives in `.state/` directories next to the scripts and hooks:
  session timers, prompt counters, ingest and compile bookkeeping, the health
  file, retrieval session ledgers, and the FTS5 database `notes.db`.
- The staging tree lives at `<vault>/.stage/`, created with mode `0700` and
  removed after each run.
- claude.ai export ZIPs are read from `<vault>/.import/`.
- All of these are gitignored (`.state/`, `.stage/`, `.import/`, `notes.db`,
  `*.lock`), so state and index files are not committed.

Note what this does **not** cover: `daily/` and `knowledge/` are your content and
are deliberately not ignored. If you version the vault, they are versioned with
it.

### 6. Out of scope

- The security of Claude Code itself and of the Claude API.
- Full-disk encryption, OS account security, and backup service security.
- Multi-user machines. The design assumes a single trusted user; hooks are
  registered in that user's `settings.json` and state files carry no
  authentication.
- Two machines sharing one synced vault. The compile trigger claim is a local
  file, so concurrent compiles from different machines are not prevented.

---

## Reporting a vulnerability

Please report security issues **privately** first.

- Preferred: GitHub's private vulnerability reporting on this repository
  (**Security → Report a vulnerability**).
- If that is unavailable, open a public issue containing only that you have found
  a security issue and how you would like to be contacted — no details.

Please include, where you can: affected script or hook, the version or commit,
your Windows and Python versions, reproduction steps, and what an attacker gains.

This is a small, volunteer-maintained project. There is no service-level
agreement and no bug bounty. A best-effort acknowledgement within 14 days and a
public advisory once a fix or a documented mitigation exists is what can honestly
be promised.

Please do not test against anyone else's vault, and do not include real
credentials or third-party personal data in a report — redact them first.
