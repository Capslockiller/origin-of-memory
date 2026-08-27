# Skills

<!-- yazan: codex · gpt-5.6-sol -->
Claude Code skills that ship with this repository. The setup wizard asks about
each skill: `beyin-doktor` and `beyin-ice-aktar` default to yes, while the rest
default to no. Direct `install.ps1` still copies this whole directory unless
`-SkillFilter <comma-separated names>` is supplied.

> **Select before you rely on this set.** Two of these skills are the memory
> mechanism itself and are the reason the repository exists. The other six are
> *genericized copies of the author's working set* — they encode one person's
> tooling, tiers and habits, and several of them describe tools you may not have
> installed. They are published because the patterns are useful, not because they
> are correct for you. Leave them unselected in the wizard, filter the direct
> installer, or remove them from `<user>\.claude\skills\` afterwards.

## Core — part of the memory mechanism

| Skill | Trigger | What it does |
| --- | --- | --- |
| `beyin-doktor` | "beyin doktor", "doktor", "sağlık kontrolü" | Health check for the memory pipeline: hook wiring, scripts, interpreter, daily-log freshness, last compile status, as one 🟢/🟡/🔴 table. |
| `beyin-ice-aktar` | "beyin içe aktar", "içe aktar", "zip işle" | Processes a claude.ai conversation export ZIP into the vault. |

These two are referenced by [../docs/architecture.md](../docs/architecture.md)
and are the ones to keep if you keep nothing else.

## Working set — genericized, optional

| Skill | Trigger | What it does |
| --- | --- | --- |
| `companion` | "companion protokolünü başlat", "/companion" | **Structure example** for the personal identity layer the memory hooks read. Ships with placeholder content only — see below. |
| `orchestration` | Loaded before splitting work across agents | Delegation policy: which tier drives, which tier gets which lane, what a brief must carry, where a delegate's output goes. |
| `codex-fleet` | "use codex", "codex exec", "spawn a codex fleet", image generation | Operating manual for the Codex CLI: invocation, image generation, worktree-isolated multi-lane fleets. |
| `gece-vardiyasi` | "gece vardiyası: `<task>`" | Overnight draft-only shift protocol: irreversible actions queue for morning approval instead of executing. |

### What "genericized" means here

Each of the six carries a header note saying so. Concretely:

- Absolute paths, usernames, vault names, project names and personal identifiers
  were replaced with `<vault>`, `<user>`, `<repo>` and similar placeholders.
- Turkish trigger phrases were **kept** — they are the invocation surface, and
  translating them would break the trigger. Bilingual naming is intentional
  throughout this repository; see [../AGENTS.md](../AGENTS.md).
- `odena-orchestration` was renamed `orchestration` and the companion skill was
  renamed `companion`, since the original names were a personal companion name.
- Measured claims that came from one machine (concurrency ceilings, tool
  inventories, latency notes) were kept but re-labelled as the author's
  measurements rather than presented as universal.

### The `companion` skill is a shape, not an identity

`hooks/session-start.ps1` injects files from a `*850-Companion` directory in the
vault: an identity core, a last-session bridge, active threads, and a persistent
rules file. **Those files are not shipped, and the `companion` skill does not
contain the author's.** What it contains is the structure — which file plays
which role, what each must look like for the hook to parse it, and placeholder
content you replace with your own.

See the "Writing your own companion protocol" section of
[../README.md](../README.md) for the parsing contract the hook enforces, and
[../template/rules.example.md](../template/rules.example.md) for an example of
the persistent-rules file.

### Skills that assume external tools

`codex-fleet` describes workflows around the Codex CLI. It does nothing on its
own and is inert if you do not have that tool. `orchestration` and `gece-vardiyasi` reference
each other and `codex-fleet`; if you delete one, read the others for dangling
references before relying on them.

## Attribution

`codex-fleet` is an adaptation of a skill by
[Avenox](https://avenox.lol) (MIT), and keeps its upstream credits and
adaptation notes. `orchestration` adapts Avenox's `fable-orchestration` and says
so in its own text. See [../docs/attribution.md](../docs/attribution.md).
