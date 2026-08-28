# Release notes — v0.2.0 (2026-08-28)

The notes published with the `v0.2.0` GitHub release, kept here so the text is
in the repository and not only in a release page. The authoritative, itemised
list of changes is [`CHANGELOG.md`](../CHANGELOG.md#020---2026-08-28); this page
is the reader-facing summary of it.

---

## What changed for you

- **Setup is now a wizard.** `kur.ps1` detects Claude Code, Ollama, LM Studio,
  llama.cpp, vLLM, your hardware and your Claude Desktop MCP config, proposes a
  preset, and shows you the whole plan before writing anything — two screens,
  one Enter each.
- **The background model is now yours to choose.** Session summaries and history
  ingest can run on the Antigravity CLI, a local Ollama server, or any
  OpenAI-compatible endpoint instead of your Claude subscription, and the vault
  is readable from any MCP client. Nightly compile still needs `claude`.
- **The compiler got stricter about what reaches your memory.** A frontmatter
  schema gate stops malformed notes at the promotion boundary, directive-shaped
  content is quarantined instead of merely noted, and a machine-identified lock
  stops two synced machines compiling the same vault at once.

## Upgrading from 0.1.0

**No migration. Re-run the installer with `-Force` and you are done.**

```powershell
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -VaultPath <vault> -Force
```

That claim is worth showing rather than asserting, so here is exactly what
`-Force` does and does not touch:

- It overwrites `<vault>\.claude\scripts\` and `<vault>\.claude\hooks\` with the
  new versions. That is the intended effect — those are program files.
- It **never** copies `.state`, `.stage`, `.import`, or any `.db` / `.lock` /
  `.pyc` file, so your index, health state, locks and staging tree are left
  alone.
- `hub-config.json` is copied only if it does not already exist. Your topic hubs
  survive.
- The vault skeleton (`daily/`, `knowledge/`, `template/vault` content) is
  copied without overwrite, so nothing you have written is replaced.
- Hook registration is idempotent and the six registered hooks are unchanged
  from 0.1.0, so `settings.json` is backed up and then left as it was.

Two things are worth knowing, neither of which requires action:

- **The FTS index gains a column.** Schema 2 adds `documents.source_date` for
  the opt-in `rrf` ranking mode. An index built by 0.1.0 keeps working —
  retrieval falls back to a query without that column — and it is rebuilt
  automatically at the next nightly compile. To rebuild it now:
  `python <vault>\.claude\scripts\retrieve.py build`. Until it is rebuilt,
  `BEYIN_RETRIEVAL=rrf` degrades to BM25-only ranking, and `beyin doktor`
  reports that as 🟡 rather than an error.
- **Every new environment variable is optional.** With nothing set, 0.2.0
  behaves as 0.1.0 did: `claude -p` for every model call, `bm25` ranking, the
  same timeouts. `BEYIN_MODEL_BACKEND=gemini` is accepted as a deprecated alias
  for `antigravity` and warns.

If you installed through the wizard, `kur.ps1` re-run with the same preset does
the same job; `-DryRun` prints every action first.

## Known limits

Unchanged from 0.1.0 unless noted, and stated here rather than left for you to
discover:

- **Windows only.** The hooks are PowerShell and file locking falls back to
  `msvcrt` region locks. There is no tested macOS or Linux path. Those platforms
  are served by the upstream project this one is adapted from,
  [avenoxbeyin](https://github.com/avenoxai/avenoxbeyin).
- **Sensitive-data filtering is still only credential patterns.**
  `secret_guard.py` catches keys, tokens, connection strings and password
  assignments. It does not detect health information, legal status, financial
  detail, or third parties who never consented to being written down. See
  [SECURITY.md](../SECURITY.md).
- **Web-fetched text that enters a transcript can be summarised into the vault.**
  Untrusted-data delimiters are in place; there is no exclusion list.
- **Compile still requires `claude`.** The local and Antigravity backends cover
  summarisation and ingest only. Compile is the one call that writes files, and
  the other backends offer no per-invocation permission scoping, so it fails
  loud with `<backend>-backend-unsupported:compile` rather than running
  unscoped.
- **`rrf` retrieval is opt-in and unmeasured.** It is implemented and tested but
  has not been run against the gold set, and the synthetic benchmark suggests it
  may exceed the 500 ms injection budget on a real corpus. Two ranking questions
  are documented rather than tuned away in
  [docs/retrieval.md](retrieval.md#8-known-limits).
- **The duplicate-check registry is now a partial view.** Bounding it to the
  daily log's hubs plus the 50 most recent concepts (hard cap 400 rows) is what
  cut the compiler's input by 63%; the cost is that the model can miss a
  duplicate that lives outside the selected rows. It is told so in the prompt.
- **Cross-machine compile is prevented cooperatively, not guaranteed.** The lock
  records its owner, but it depends on your sync tool having propagated the lock
  file, so a fast enough race can still slip through.
- **All measured numbers come from one corpus**, one language mix and one
  person's question distribution. See [docs/evaluation.md](evaluation.md),
  including the recall@5 correction issued with this release.
