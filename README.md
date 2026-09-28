# Origin of Memory

[![CI](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml/badge.svg)](https://github.com/Capslockiller/origin-of-memory/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Capslockiller/origin-of-memory)](https://github.com/Capslockiller/origin-of-memory/releases)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)
[![License: PolyForm Noncommercial](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-blue)](LICENSE)

Persistent memory for Claude Code sessions, kept as plain Markdown in an Obsidian vault.

`oom` is a single .NET 9 console executable. It hooks into Claude Code, summarises each session into a daily log, compiles the daily logs into concept notes, and injects the relevant memory back at the start of the next session under a fixed character/byte budget. Summaries are produced by the `claude` CLI on the same machine, gated on the way out; nothing else is invoked. (bkz. docs/iddialar.md#readme-intro)

## How it works

1. **Session start** — `context` builds the session-start block under a single ≤ 8.000-character / ≤ 8.500-byte budget. It prints nine sections, in this order: `[Bildirim]`, `[Zaman]`, `[Hafıza — Son Oturum]`, `[Hafıza — Aktif Threadler]`, `[Hafıza — Kurallar]`, `[Hafıza — Düzeltmeler]`, `[Hafıza — Son Journal]`, `[Bilgi Tabanı — Arama]` (a one-line hint and a ready-to-run `retrieve --query SORGU` command carrying the full path of the running `oom.exe` and the vault) and `[Bugünün Logu]` (today's daily, fenced as data); `context --json` lists the same nine names, without brackets or the `Hafıza — ` prefix, in its `sections` array. The budget is paid first to Bildirim, Zaman, the thread list (every active title, the status of the first five capped at 200 characters), Kurallar, Düzeltmeler and the search line, which are never cut; then to Son Journal (≤ 300 characters) and Son Oturum (≤ 1.000); today's log takes the rest. An oversized `context.capChars` in `oom.json` is ignored, not honoured. (bkz. docs/iddialar.md#readme-how-context)
2. **During the session** — `nudge` prints a clock line on every prompt and, every `nudgeEvery` prompts, asks the session to record what it learned. (bkz. docs/iddialar.md#readme-how-nudge)
3. **Session end / compaction** — `flush` summarises the transcript with the fast model, masks any secret in the result, refuses and quarantines a summary that carries a prompt-injection directive, and only then appends a block to `daily/YYYY-MM-DD.md`. (bkz. docs/iddialar.md#readme-how-flush)
4. **`oom sweep`** — finds transcripts that changed on disk and flushes the ones the hooks missed, then reports the pending-daily count. Nothing schedules it and it never starts a compile itself, at any hour; run it yourself or from Task Scheduler. (bkz. docs/iddialar.md#readme-how-sweep)
5. **`oom compile`** — folds every pending daily log into `knowledge/` concept notes with the smart model, newest day first, and rebuilds the root map. It always compiles what's pending when you run it; `compile --dry-run` prints the plan and the real last-compile timestamp read from `daily_ingest`. (bkz. docs/iddialar.md#readme-how-compile)
6. **On request** — `retrieve` or the MCP tools (`memory_search`, `memory_root_map`, `memory_note`) search `knowledge/concepts`, `daily/`, `Duzeltmeler.md` and the hand layer (`Last-Session.md`, `Threads.md`, `Journal.md`) with BM25 and return a bounded, masked excerpt fenced as data, not instruction. (bkz. docs/iddialar.md#readme-how-retrieve)

Every memory file you edit by hand is Markdown; `oom` never asks you to edit anything else. (bkz. docs/iddialar.md#readme-how-cache)

## Requirements

- Windows 10/11
- .NET 9 SDK
- [Claude Code](https://docs.anthropic.com/en/docs/claude-code) with the `claude` CLI on `PATH`
- An Obsidian vault, or any folder of Markdown files

## Install

```
dotnet build Oom.sln -c Release
oom --vault <vault> install
```

Run `install` from wherever the build lives; the hooks point at that executable. It writes `<vault>\.oom\vault.json` and `<vault>\.oom\oom.json` (defaults below, never overwritten on an existing install) and adds four hooks to `<vault>\.claude\settings.json`: (bkz. docs/iddialar.md#readme-install-writes)

| Hook | Command |
|---|---|
| SessionStart | `oom context` |
| UserPromptSubmit | `oom nudge` |
| SessionEnd | `oom flush --reason sessionend` |
| PreCompact | `oom flush --reason precompact` |

`install --uninstall` removes those hook entries again. (bkz. docs/iddialar.md#readme-install-uninstall)

Outside the vault, `oom` keeps its state under `%LOCALAPPDATA%\oom\<vault-hash>\`: the SQLite state (a cache, deletable at any time) plus a few small artifacts next to it — a short-lived runner smoke-check cache, a health-write-failure log, `compile`'s own transient backup copies (discarded once `compile` finishes), an empty `logs\` folder `doctor --fix` creates, and `red\`, the `flush` quarantine store: every summary `flush` refused (a prompt-injection directive, a model refusal) or rejected for its shape, and the error output of every failed summarizer call, kept with secrets masked next to a `.reason` file where there is one — `oom` never deletes it. It also leaves one small lock file per daily under `%TEMP%\oom\locks\` and runs the `claude` CLI from `%TEMP%\oom\run\`. The same vault path always hashes to the same directory regardless of drive-letter case, slash direction, or trailing slash. (bkz. docs/iddialar.md#readme-install-state)

## Commands

Every command and flag `oom` accepts, exactly as `oom --help` prints it: (bkz. docs/iddialar.md#readme-commands)

```
oom [--vault <yol>] <komut>

  --vault <yol>                                      kasa kökü olarak <yol> kullanılır

  context [--json]                                   oturum başlangıcı hafıza bağlamını gösterir
    --json                                            bölümleri ve metni JSON olarak gösterir
  nudge [--session <kimlik>]                         istemi kaydeder ve oturum zamanlamasını gösterir
    --session <kimlik>                               kanca oturumu yerine bu oturumu kullanır
  flush [--session <kimlik>] [--reason <neden>]      oturum dökümünü daily/ içine özetler
    --session <kimlik>                               özetlenecek oturumu belirtir
    --transcript <dosya>                             kanca dökümü yerine bu dosyayı okur
    --reason <neden>                                 nedeni belirtir: precompact, sweep, ingest, sessionend
    --detached                                        ayrı süreç başlatmadan aynı süreç içinde işler
  sweep [--dry-run]                                  değişen dökümleri yapılandırılmış köklerde tarar
    --dry-run                                         yazmadan ve indekslemeden sonucu gösterir
  compile [--dry-run]                                bekleyen daily kayıtlarını bilgi notlarına derler
    --dry-run                                         daily kayıtlarını derlemeden planı gösterir
  retrieve --query <sorgu> [--json] [--top N] [--batch <dosya>]  indekslenmiş notlarda arar
    --query <sorgu>                                  aranacak sorguyu belirtir
    --json                                            eşleşmeleri JSON olarak gösterir
    --top N                                           en fazla N eşleşme döndürür
    --batch <dosya>                                  dosyadaki her satırı ayrı sorgu olarak işler
  doctor [--fix] [--json] [--quiet] [--all]          kasa, indeks, kanca ve durum sağlığını denetler
    --fix                                             durum, kök harita ve arama indeksini onarır
    --json                                            sağlık sonucunu JSON olarak gösterir
    --quiet                                           yalnız uyarı ve hataları stderr'e yazar
    --all                                             bilgi düzeyindeki tüm satırları da gösterir
  save "<serbest metin>" | --session-json <dosya>    bugünün daily kaydına denetim noktası ekler
    --session-json <dosya>                            oturum JSON dosyasından denetim noktası alır
  mcp                                                 salt okunur MCP arabirimini stdio üzerinden çalıştırır
  install [--uninstall]                              Claude kanca kayıtlarını kurar veya kaldırır
    --uninstall                                       bu yürütülebilir dosyanın Claude kancalarını kaldırır
  kit status [--json] [--kit <dizin>]                bileşen × hedef başına kuru/güncel/farklı/bozuk durumunu göster
    --json                                            kit status çıktısını JSON olarak üret
    --kit <dizin>                                     kit kökü olarak <exe-dizini>/kit yerine <dizin> kullan
  kit install [--dry-run] [--force] [--only <ad>] [--kit <dizin>]  kit bileşenlerini ev profiline kur
    --dry-run                                         hiçbir şey yazmadan neyin değişeceğini bildir
    --force                                           farklı olan hedefi yedekleyip üzerine yaz
    --only <ad>                                       yalnızca bu adlı bileşenle sınırla
    --kit <dizin>                                     kit kökü olarak <exe-dizini>/kit yerine <dizin> kullan
```

This repository ships the kit engine without kit content: there is no `kit/` directory here. `kit status` and `kit install` need `--kit <dizin>` pointing at your own kit (a directory with a `manifest.json`); without one they report `kit yok`. (bkz. docs/iddialar.md#readme-kit-engine)

`oom --version` prints `3.1.0+<commit>` (the short `git rev-parse` hash the build was made from, or `3.1.0+unknown` when git is unavailable at build time); the MCP server's `initialize` response carries the identical string in `serverInfo.version`. (bkz. docs/iddialar.md#readme-version)

### Environment variables

`OOM_INVOKED_BY` is the recursion guard: `oom` sets `OOM_INVOKED_BY=oom` on the `claude` process it starts for a summary, and whenever the variable holds any non-empty value, `context`, `retrieve`, `nudge`, `flush`, `sweep` and `compile` exit 0 at once and print nothing — a shell or CI job that exports it silences those six commands without a warning. (bkz. docs/iddialar.md#readme-env-invoked-by)

`OOM_LOCALAPPDATA`, `OOM_USERPROFILE`, `OOM_TEST_ROOT` and `OOM_FAKE_NOW` are test seams (see [CONTRIBUTING.md](CONTRIBUTING.md#test-seams)) that, in that order, move the state root, the profile root `doctor` checks hooks in and `kit` installs into, the directory whose transcripts `sweep` skips as its own mechanism artifacts (default `%TEMP%`), and the clock; nothing sets them in normal use. (bkz. docs/iddialar.md#readme-env-seams)

## Configuration — `.oom/oom.json`

A fresh `install` writes the table below; keys this version no longer reads are flagged in the last column instead of being silently accepted. (bkz. docs/iddialar.md#readme-config-table)

| Key | Default | Meaning |
|---|---|---|
| `backend.claude.fast` / `.smart` | `claude-haiku-4-5-20251001` / `claude-sonnet-5` | Models used for flush and compile |
| `sweep.roots` | `~\.claude\projects`, `~\.codex\sessions` | Where transcripts are looked for |
| `sweep.minTurns` / `maxSessionsPerRun` | 3 / 20 | Sweep limits |
| `sweep.everyHours` | — | **Dead**: no longer read; any value warns `bilinmeyen ayar: everyHours` on stderr |
| `flush.mode` | `dilim` | `dilim`: summarise only the last `flush.sliceTurns` turns and skip sub-agent transcripts. `tam`: summarise every turn |
| `flush.sliceTurns` | 30 | Turns kept per session in `dilim` mode |
| `compile.maxDailiesPerRun` | 3 | Daily notes folded into concepts per `oom compile` run |
| `compile.eveningHour` / `minIntervalHours` | 18 / 20 | Parsed for backward compatibility but no longer consulted: `oom compile` folds whatever is pending whenever it is run, and `oom sweep` never starts a compile at any hour |
| `context.companionDir` / `capChars` | `🔮 850-Companion` / 8000 | Companion folder and the context budget's character cap (values above 8000 are ignored, not honoured) |
| `retrieve.top` / `perNoteChars` / `totalChars` | 3 / 1500 / 4500 | Retrieval limits |
| `nudgeEvery` / `reflectionMinPrompts` | 15 / 5 | Nudge cadence and minimum prompts before a reflection is asked for |
| `mcp.enabled` | true | Whether `oom mcp` serves |

A key this table does not list — under a known section or at the top level — makes `oom` print `bilinmeyen ayar: <key>` on stderr instead of accepting it silently. (bkz. docs/iddialar.md#readme-config-unknown-key)

## Vault layout

- `daily/` — one file per day, one block per flushed session, anchored with the session id and turn range (bkz. docs/iddialar.md#readme-vault-daily)
- `knowledge/` — compiled concept notes, hubs, and the root map (bkz. docs/iddialar.md#readme-vault-knowledge)
- `<companionDir>/` — hand-written files: `Last-Session.md`, `Threads.md`, `Kurallar.md`, `Duzeltmeler.md` and `Journal.md` are all injected at session start; of those, `Last-Session.md`, `Threads.md`, `Journal.md` and `Duzeltmeler.md` are also indexed for `retrieve`/MCP search — `Kurallar.md` is injected but never indexed (never a search result). Any other file placed here (e.g. `Core.md`) is neither injected nor indexed by `oom` itself. (bkz. docs/iddialar.md#readme-vault-companion)
- `.oom/` — `oom.json` and `vault.json`, plus `quarantine/`, where `compile` keeps a model output it refused (secrets masked) (bkz. docs/iddialar.md#readme-vault-oom)

Summaries pass through a high-confidence secret masker before they are written: known token prefixes (`ghp_`, `gho_`, `github_pat_`, `sk-ant-`, `AKIA`), a `user:pass@` inside a URL, a 32+ character hex/base64 run that contains a digit (base64: also both letter cases) within 40 characters of `anahtar`/`şifre` (any suffix) or `password`/`key`/`token` (singular or plural), and a password token after `şifre`/`parola` (any suffix, e.g. `şifresi`), `password(s)`, `passwd`, `pwd` or a bare `pass` (for `pass`, only a token holding a digit or symbol) — at least 6 characters after a `:` or `=` (a trailing `.` `,` `;` `:` `)` `]` `}` quote or `*` is neither counted nor masked, so `şifre=abcde` stays as it is), or at least 6 characters wrapped in backticks — are all replaced with `<prefix>****(maskelendi)`, where `<prefix>` is the known token prefix or the URL scheme and empty otherwise, before the text reaches `daily/`, the search index, `retrieve` or MCP output. This does not catch every possible secret shape; treat any daily log as something a teammate could read. (bkz. docs/iddialar.md#readme-vault-masking)

## Development

```
dotnet build Oom.sln -c Release
dotnet test Oom.sln -c Release
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for how changes are made (including the test seams `oom` reads — `OOM_LOCALAPPDATA`, `OOM_FAKE_NOW`, `OOM_USERPROFILE`, `OOM_TEST_ROOT` — and the harness-only `OOM_KABUL_EXE` and `OOM_SCAR_ROOT`) and [SECURITY.md](SECURITY.md) for reporting a vulnerability.

## License

[PolyForm Noncommercial 1.0.0](LICENSE). Anyone may use, change and share it for noncommercial purposes. Commercial use requires a separate commercial license from Odena Studio: odenastudio@gmail.com.
