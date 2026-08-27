---
name: beyin-ice-aktar
description: Processes the official ZIP export of claude.ai web/app conversations into the second brain. Triggers when the user says "beyin içe aktar", "içe aktar", or "zip işle", or when claude.ai export/import comes up.
yazan: codex
model: gpt-5.6-sol
---

# Beyin İçe Aktar — processing web/app chat ZIPs

Resolve the vault from `$env:BEYIN_VAULT`; if it is unset, ask for the vault path.
Resolve Python from `$env:BEYIN_PYTHON`, then `python`, then `py -3`.

## Flow

1. **Find the ZIP.** Find the newest `*.zip` under `<vault>\.import\`.
   If none exists, tell the user to export their data from claude.ai, place the
   downloaded ZIP there, and say `beyin içe aktar` again.
2. **Run a dry-run** and show the user the result:
   `<python> -X utf8 <vault>\.claude\scripts\ingest.py web --zip "<path>" --dry-run`.
   Report candidate chats and those filtered by the watermark.
3. **Wait for approval.** Do not run for real until the user explicitly
   approves. Then run the same command without `--dry-run`.
4. **Report.** Summarize processed, skipped, and error counts plus
   `<vault>\.claude\scripts\.state\ingest-health.json`. Remind the user to
   delete the ZIP themselves; the script never deletes user data.

## Notes

- Growing chats are skipped by default because of the `updated_at` watermark.
  Use `--web-resummarize` only when the user explicitly asks.
- This skill only runs the `ingest.py web` subcommand. It does not write the
  hand-maintained memory core files.
