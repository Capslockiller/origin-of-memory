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
  `IGNORE ALL PREVIOUS:`, …). In the compiler this pattern is a **gate**, not a
  note — see *Quarantine* below.
- Retrieved notes injected by `memory-retrieve.ps1` are prefixed with an explicit
  statement that their contents are data and that no sentence inside them is to be
  applied as an instruction.
- The compiler's write policy (below) bounds the damage: a successful injection
  still cannot write outside `knowledge/concepts/**`, `knowledge/index-full.md`
  and `knowledge/log.md`.

#### Quarantine — the compiler's three directive-shaped gates

Until 2026-08, a `DIRECTIVE_SHAPED` hit only wrote `warn:directive-shaped-input`
to health and the compile proceeded, so injected text could become a permanent
concept note. It now stops the content instead. `compile.py` checks three places
and **quarantines rather than deletes** — removing content is the operator's
decision, not the pipeline's:

1. **Daily body.** That daily log is not compiled this run. It is copied
   verbatim to `<vault>/.stage/karantina/<YYYY-MM-DD>-<sha8>.md` (directory mode
   `0700`, files `0600`) beside a sidecar `.json` recording the matched pattern,
   up to 300 characters of the offending excerpt, the timestamp and the source
   filename. Health records the **error** `quarantine:directive-shaped`, and the
   file's SHA-256 is written to the `quarantined` map in `compile-state.json` so
   the next run does not re-quarantine it. Because the key is a content hash,
   **editing the file makes it eligible again** — no manual un-flagging step.
2. **Root map or duplicate-check registry.** A hit here means the vault itself is
   already poisoned, so nothing is salvageable in isolation: the run raises
   `PolicyError("directive-shaped-registry")` and aborts. Nothing is promoted,
   the model is never called, and `last_status` becomes `fail:policy`.
3. **Model output, before promotion.** Every staged file that would be promoted
   is scanned. A hit means that file is not promoted and its content goes to
   quarantine with the same error; its clean siblings in the same run still
   promote. This is what stops a poisoned daily that slipped past gate 1 from
   being laundered into a concept note by the model.

**Manual release path** — deliberately not automated:

1. `python scripts/durum.py` shows the quarantine count; `beyin-doktor` reports
   it as 🔴 with the newest entry.
2. Read `<vault>/.stage/karantina/<entry>.json` to see what matched and why.
3. Read the `.md` beside it. Decide whether the content is worth keeping.
4. To release it, **edit out the directive-shaped lines** and move the file back
   into `daily/`. The next compile sees a new hash and processes it normally.
5. To discard it, delete both files. Nothing else references them.

Never script step 4. An automatic release is the same thing as no gate at all.

`.stage/` is gitignored and skipped by the uninstaller, so quarantined evidence
is neither committed nor swept away by a reinstall. It is also never pruned
automatically — the directory grows until you empty it.

**Residual risk — what quarantine still does not catch:**

- **Malicious but not directive-shaped content.** The pattern matches a line that
  *opens like an instruction* (`SYSTEM:`, `TALİMAT:`, `IGNORE ALL PREVIOUS:`, …).
  Prose that persuades rather than commands — a plausible false "fact" about your
  project, a poisoned definition, a fabricated decision written in ordinary
  sentences — matches nothing and compiles normally. This is the larger half of
  the injection problem and it remains open.
- **Prose-shaped injection is the expected evasion.** Anyone who knows this gate
  exists can trivially rewrite around it. Treat it as removing the laziest class
  of attack, not as a boundary.
- **Retrieval is not gated.** The pattern guards the compile path. Notes already
  in `knowledge/` from before this gate existed are not rescanned, and
  `memory-retrieve.ps1` injects what the index holds.
- **False positives cost you a compile.** A legitimate daily log that quotes a
  system prompt, or pastes a transcript containing `SYSTEM:`, is quarantined too.
  That is the intended trade — the recovery is an edit, not data loss.
- **The gate is line-anchored.** A directive placed mid-line, or split across
  lines, is not matched.
- **Partial promotion can leave a dangling index row.** When a concept note is
  held back — by this gate or by the schema gate below — and a clean
  `index-full.md` in the same run is promoted, the index can name an article
  that was never written. Harmless, and the next compile corrects it, but it
  will look odd if you read the index first.

There is still no content exclusion list. If you fetch untrusted pages in a
session, editing that day's `daily/` file before the evening compile remains the
strongest thing you can do.

#### The frontmatter schema gate

A second, non-security gate runs in the same promotion path and is routed the
same way, so the two are worth reading together. `scripts/sema.py` validates
every staged concept note against the schema the compiler's own prompt asks
for — a parsable frontmatter block with no duplicate keys, a non-empty string
`title`, `created` and `updated` as real `YYYY-MM-DD` dates, `tags`, `aliases`
and `sources` as lists, and a non-empty body. A note that misses any of these is
**not promoted**. It is copied to `<vault>/.stage/karantina/sema/` (same `0700`
directory, `0600` files) beside a sidecar `.json` listing the problems, health
records the error `schema-invalid:<file>`, and its clean siblings in the same
run still promote.

It runs **after** `secret_guard.scan()`, deliberately. A secret must fail the
whole run no matter what else is wrong with the file; a schema miss must only
cost that one file.

Three properties matter more than the rule list:

- **Nothing is ever repaired.** Guessing a missing `created` date would invent a
  fact and file it as permanent memory. The gate names the problem and stops;
  filling it in is a human edit.
- **It stops new damage only.** `retrieve.build_index` and `rootmap` stay
  tolerant, so the notes already in `knowledge/` — written before the schema was
  enforced — keep indexing and keep being retrieved. The gate is not applied to
  them retroactively and cannot be.
- **It is not a security boundary.** A schema-valid note can still be poisoned
  prose, and a schema-invalid one is usually just a sloppy write. This gate
  protects the corpus's shape, not its truth. The directive-shaped gates above
  are the security ones.

`beyin doktor` surveys the live corpus read-only and reports how many notes
would fail today (`retrieve.py verify` carries `schema_invalid_count`). That
number is a census of what predates the gate, never a verdict on the vault, and
nothing acts on it automatically.

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
  the live vault is not reachable as a relative path;
- appends one accounting line per call to `.state/calls.jsonl` — timestamp,
  backend, model tier and resolved slug, component, character counts, estimated
  tokens, duration and outcome. **No prompt and no response text.**
  `beyin_ortak.record_call()` is handed character counts rather than the strings
  themselves, so there is no path by which content can reach the file; the
  outcome field carries this repository's own fixed error vocabulary, never
  model output. It is a ledger, not a log, and a test asserts it stays one.

The compiler's write policy is enforced after the model returns, not trusted from
it: the staging tree is manifested before and after the run, deletions and type
changes raise `PolicyError`, and only files matching the allow-list are promoted —
each one re-validated against its expected live-side digest and confirmed to
resolve inside `knowledge/` before an atomic replace.

### 5. Local state stays local

- Runtime state lives in `.state/` directories next to the scripts and hooks:
  session timers, prompt counters, ingest and compile bookkeeping, the health
  file, retrieval session ledgers, the model-call ledger `calls.jsonl` (capped
  at 5 MB, oldest lines dropped), and the FTS5 database `notes.db`.
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
- Two machines sharing one synced vault are **partially** handled as of 2026-08.
  `compile.lock` now records `{machine, pid, started_at, hostname}`, and a run
  that finds a live lock owned by another machine refuses with
  `skip:compile-locked-by:<machine>` instead of compiling alongside it. The
  machine id is the hostname plus a random per-install suffix stored in
  `.state/machine-id`; it carries no user identity beyond the hostname. This is
  cooperative, not a distributed lock: it depends on the sync tool having
  propagated the lock file, so a fast enough race between two machines can still
  slip through. Stale locks older than `BEYIN_COMPILE_LOCK_TTL_MIN` (default 120
  minutes) are broken with a health warning naming the previous owner, which
  means a machine that dies mid-compile costs at most that delay.

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
