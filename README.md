<!-- yazan: codex · gpt-5 -->

# Origin of Memory 2.0

Origin of Memory is a Windows-native memory pipeline for Claude Code. Version 2.0 is being rebuilt as one self-contained C#/.NET executable: capture, compilation, retrieval, health checks, installation, and the read-only MCP surface live under one program rather than an interpreter-and-script chain.

Memory is a mechanism, not a habit. Three decisions define the design:

- the shipped product is one self-contained `oom.exe`;
- the scheduled sweep is the authoritative write path and hooks are accelerators;
- models receive text and return text, while only `oom.exe` writes files.

## Status

The rebuild is in progress. `main` is the 2.0 line. The previous Python/PowerShell implementation remains readable on branch [`v0`](https://github.com/Capslockiller/origin-of-memory/tree/v0); its last release is [`v0.7.0`](https://github.com/Capslockiller/origin-of-memory/releases/tag/v0.7.0). It is reference material, not code for the rebuild.

Test measurement on 2026-09-09:

```text
Başarısız! - Başarısız:     7, Başarılı:    92, Atlanan:     0, Toplam:    99, Süre: 2 s - Oom.Tests.dll (net9.0)
```

This is not a release claim. Measured by the orchestrator on `main` (commit `a739c5b`, 2026-09-09) with `dotnet test Oom.sln -c Release`. The seven remaining red scars (Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098) assert behaviour without building their fixtures; their rulings are recorded in `progress.md` and the owner decided to leave them red for now. Known integration gaps are marked in [docs/architecture.md](docs/architecture.md) and [docs/install.md](docs/install.md); a per-scar table (`docs/scars.md`) is still to be written.

## Repository layout

- `src/Oom/<Component>/` — the C# executable, divided by responsibility.
- `tests/Oom.Tests/Scars/` — 99 xUnit regression tests, one `Fact` per historical scar.
- `bench/` — Python measurement tools. They are not shipped with `oom.exe`.
- `docs/` — architecture, installation, scar status, attribution, and release history.

## Build and test

The project currently targets `net9.0-windows10.0.19041.0`. The binding target is .NET 10 LTS; changing the target framework after that SDK is installed is the D1 transition.

```powershell
dotnet build Oom.sln -c Release
dotnet test Oom.sln -c Release
```

The executable project is configured for `win-x64`, `SelfContained`, and `PublishSingleFile`.

## Commands

This is the short 2.0 command contract. Several end-to-end bindings remain incomplete while the rebuild is in progress.

| Command | Purpose |
| --- | --- |
| `context [--json]` | Emit the SessionStart context block. |
| `retrieve --hook` | Run gated BM25 retrieval for `UserPromptSubmit`. |
| `retrieve --query "<query>" --json` | Return raw ranked memory for integrations. |
| `flush --session <id> --reason sessionend\|precompact` | Summarise one session; the hook path is an accelerator. |
| `sweep [--dry-run]` | Run the authoritative scheduled capture path. |
| `compile [--dry-run]` | Compile daily logs into concept notes. |
| `ingest claude\|codex [--dry-run] [--max N]` | Backfill archived transcripts. |
| `doctor [--fix] [--json]` | Report health and request repairs. |
| `save "<text>" [--compile]` | Write a direct daily checkpoint. |
| `save --session-json <file>` | Send an external `Session` object through the flush path. |
| `mcp [--enable\|--disable]` | Serve or configure the read-only MCP surface. |
| `install [--uninstall] [--from-v0]` | Install, remove, or migrate the mechanism. |
| `bench [--backend claude\|local]` | Run acceptance measurements. |

## Scar discipline

The rebuild started with the scar inventory, before implementation. Each `Y-001` through `Y-099` maps to exactly one xUnit `Fact`; a scar closes only when that test is green. Tests are kept red before the corresponding code exists, and a claim is not substituted for a passing test or recorded measurement.

## Acceptance gates

Release requires more than a successful build: all scar and solution tests plus Windows publish; line-budget and dependency checks; no embedded user paths; retrieval recall parity; archive and live capture targets; a clean Windows 11 install-to-retrieval run; 30 unattended days including catch-up after downtime; security mutants; local-model measurement; fixed ingest/MCP/save contracts; and tests for the four stable extension interfaces. See [bench/README.md](bench/README.md) for the measurement boundary.

## Credits

The design derives from [avenoxbeyin v2](https://github.com/avenoxai/avenoxbeyin) by Avenox (MIT), carries forward Andrej Karpathy's knowledge-base pattern, and follows Origin of Memory v0 by Odena Studio (MIT). See [docs/attribution.md](docs/attribution.md).
