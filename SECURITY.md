<!-- yazan: codex · gpt-5 -->

# Security

This document describes the 2.0 security model, its known limits, and how to report a vulnerability. The rebuild is not yet release-ready; [docs/architecture.md](docs/architecture.md) records the integration gaps visible in the current source tree.

## Threat model

Transcripts, daily logs, concept notes, imported sessions, model responses, and text originating on the web are untrusted. Model refusal is not a security boundary. Origin of Memory instead relies on structural boundaries:

- Model calls receive text and return text. The model is given no tools; `oom.exe` owns every file write.
- `Runner.BuildClaudeRequest` supplies an isolated `CLAUDE_CONFIG_DIR`, a working directory outside the vault, `--tools ""`, `--max-turns 1`, and `OOM_INVOKED_BY=oom`.
- Compile output paths must match `knowledge/concepts/[a-z0-9-]+.md`. A traversal, absolute path, subdirectory, or duplicate path rejects the output.
- `Guards.Gate` applies Unicode normalization, secret masking, Turkish personal-data masking, and directive detection in that order. A directive finding on compile input or output refuses the run.
- Compile validates all notes before publication. It uses temporary files plus `File.Replace`, keeps run-scoped backups, and rolls back when root-map or retrieval-index rebuilding fails.
- Retrieval output labels injected notes as data. MCP is a read-only stdio JSON-RPC surface; it opens no port and runs only when its client starts it.

The current implementation has unit-tested pieces of these boundaries, but several full command paths are not wired together yet. Do not treat a passing unit test as proof that the rebuild is ready for sensitive production use.

## Secrets and personal data

The guard recognizes twelve narrow credential shapes, including Anthropic, OpenAI, GitHub, AWS, Google, and Slack tokens; JWTs; private-key headers; bearer values; password assignments; and credential-bearing connection strings. Matches become `[SIR:<class>]`.

It also recognizes valid Turkish IBAN, TCKN and VKN values, Luhn-valid card numbers, Turkish mobile numbers, and vehicle plates. Matches become `[KVK:<class>]`.

These rules are defense in depth, not a guarantee. A bare password, internal identifier, novel token format, or prose disclosure can pass through. Hand-written vault content is not automatically scanned unless it enters a guarded command path.

## Storage and access

The intended runtime state location, implemented by the installer and vault-path helpers, is:

```text
%LOCALAPPDATA%\oom\<vault-hash>\state.db
%LOCALAPPDATA%\oom\<vault-hash>\backup\
%LOCALAPPDATA%\oom\<vault-hash>\logs\
```

Keeping SQLite WAL files outside a synchronized vault avoids a known sync collision. `.oom\claude-config`, `.oom\quarantine`, the state root, and its backup directory receive a user-only Windows ACL during installation. `calls` rows record counts, timing, model metadata, outcome, and purpose; `State.RecordCall` does not accept prompt or response content.

The vault's `daily/` and `knowledge/` trees are durable user content. They are not secret stores and are not excluded from a remote automatically. If the vault is synchronized or versioned, that content and its history leave the machine according to the chosen sync or VCS configuration.

## Quarantine and notification

A directive-shaped compile result is written under `<vault>\.oom\quarantine\` and is not promoted. The software does not automatically release quarantined content. Inspect it, remove unsafe lines if the content should be retained, and retry through the normal input path.

Only defined intervention classes are eligible for a toast, and duplicate `(class, key)` notifications are suppressed for seven days. If toast registration is unavailable, notification text remains eligible for the next SessionStart block. Toast delivery and persistent notification storage are not fully connected in the current command path.

## Known open gaps

- Health, legal, and financial content is not filtered as a semantic category.
- Third parties mentioned in a session are not detected or asked for consent.
- Text fetched from the web can be summarized when it appears in a transcript. The directive gate does not detect persuasive or fabricated prose.
- Cross-machine locking is cooperative, not distributed. Two machines can race before a synchronized lock observation arrives.
- `Doctor` has validation helpers, but its default probe currently returns synthetic healthy observations and its default `--fix` action is empty.
- `Install.Run(..., fromV0: true)` currently creates a recovery marker but does not implement the full v0 state migration described in the 2.0 specification.
- The default uninstall path removes the per-vault state root; review [docs/install.md](docs/install.md) before using it on irreplaceable local state.

## Out of scope

- Security of Claude Code, the Claude service, local model servers, Windows, storage providers, and backup providers.
- Full-disk encryption, account compromise, and malware running as the same Windows user.
- A multi-user service or authenticated network API; the design is single-user and local.
- Truth validation of ordinary prose. Schema-valid, non-directive text can still be wrong.

## Reporting a vulnerability

Please report security issues **privately** first.

- Preferred: GitHub's private vulnerability reporting on this repository (**Security → Report a vulnerability**).
- If that is unavailable, open a public issue containing only that you found a security issue and how you would like to be contacted. Do not include vulnerability details.

Include, where possible: the affected component, version or commit, Windows and .NET versions, reproduction steps, and what an attacker gains.

This is a small, volunteer-maintained project. There is no service-level agreement and no bug bounty. A best-effort acknowledgement within 14 days and a public advisory after a fix or documented mitigation are the only promises made.

Do not test against anyone else's vault. Do not include real credentials or third-party personal data in a report; redact them first.
