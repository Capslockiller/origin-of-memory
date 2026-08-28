---
name: beyin-doktor
description: Health check for the second brain's v2 memory mechanism. Triggers when the user says "beyin doktor", "doktor", or "sağlık kontrolü", or when there is suspicion the memory mechanism is not working. Produces a single-table 🟢/🟡/🔴 report.
yazan: codex
model: gpt-5.6-sol
---

# Beyin Doktor — v2 memory mechanism health check

Resolve the vault from `$env:BEYIN_VAULT`; if it is unset, ask for the vault path.
Resolve Python from `$env:BEYIN_PYTHON`, then `python`, then `py -3`.

Run the checks below ONE BY ONE via Bash/PowerShell and present the result in ONE
table. Each row is 🟢 (healthy), 🟡 (warning), or 🔴 (broken), with a one-line
fix suggestion on red rows. Close with a one-sentence verdict.

## Checks

1. **Hook files** — under `<vault>\.claude\hooks\`, do `session-start.ps1`,
   `session-end.ps1`, `prompt-counter.ps1`, `memory-retrieve.ps1`, and
   `flush-launch.ps1` exist.
2. **User-level wiring** — in `<user>\.claude\settings.json`, do `SessionEnd`
   and `PreCompact` point to `flush-launch.ps1`; is the JSON valid; and are
   unrelated existing hook groups still present.
3. **Vault-level wiring** — report duplicate memory hook registrations in
   `<vault>\.claude\settings.json`. Memory hooks should be registered once at
   user level.
4. **Recursion guard** — do all five `.ps1` files contain a
   `BEYIN_INVOKED_BY` line.
5. **Python + claude CLI** — does the resolved Python run; does
   `claude --version` run.
6. **Scripts** — do `flush.py` and `compile.py` exist, and does
   `python -m py_compile` come back clean.
7. **Daily freshness** — is the newest `.md` under `<vault>\daily\` older
   than 48 hours (🟡 if none exist and the install is younger than 48 hours,
   otherwise 🔴).
8. **Compile status** — `<vault>\.claude\scripts\.state\compile-state.json`:
   is `last_status` `ok`; is `last_run` within 48 hours (🟡 if daily has not
   changed, 🔴 on an actual fail).
9. **health.json** — has `<vault>\.claude\scripts\.state\health.json` logged
   an error in the last 48 hours; report any `directive-shaped` warnings.
10. **Index size** — line count of `<vault>\knowledge\index.md`: >150 is 🟡,
    >300 is 🔴 (`time for a summary index`).
11. **Git** — is `<vault>` a repository; how many files are uncommitted; when
    was the last push to `origin/main`; a push older than 7 days is 🟡.
12. **Kurallar.md** — locate `Kurallar.md` under the companion-memory folder;
    report missing as 🔴 and more than 60 lines as 🟡.
13. **Stale hook input files** — count `.state\hookin-*.json`; files older
    than one hour are 🟡 because flush should sweep them.
14. **Index consistency** — does the FTS index still match the notes on disk.
    Run, with the resolved Python:

    ```powershell
    python <vault>\.claude\scripts\retrieve.py verify --vault-root <vault>
    ```

    It recomputes what the index SHOULD hold from `knowledge\concepts\*.md`
    and diffs that against `.state\notes.db`, printing JSON and exiting 0 only
    when the two agree. Read the fields rather than the exit code alone:

    - `ok: true` and `expected_count == indexed_count == fts_count` → 🟢.
    - `error: "index-missing"` → 🔴 (`retrieval never ran; build it with
      `retrieve.py build``).
    - non-empty `missing` → 🔴; those notes exist on disk but cannot be
      retrieved, so the session hook will never inject them. Name up to five.
    - non-empty `extra` → 🔴; the index still serves notes that were deleted
      or renamed. Name up to five.
    - counts agree but `schema_version` is below 2 → 🟡: an index built before
      the recency signal existed. `rrf` mode falls back to BM25-only ranking
      until the next rebuild.

    The fix for every red variant is the same one line, so give it once:
    `python <vault>\.claude\scripts\retrieve.py build`.

## Report format

| # | Kontrol | Durum | Not |
|---|---------|-------|-----|

At the end, give a one-sentence verdict plus ordered fix steps for red rows.
Write in Turkish. Do not write the table before running every measurement.
