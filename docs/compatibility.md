# Compatibility

What this project was built and tested against. Anything outside this page is
untested — not "probably fine", untested.

This page exists because every model call in the pipeline is a subprocess or an
HTTP request against something this repository does not ship and cannot version.
When one of those surfaces changes, the failure looks like a health entry, not a
crash, so it is worth knowing exactly what was assumed.

## Python

| | |
| --- | --- |
| Required | CPython **3.12+** |
| CI matrix | **3.12** and **3.13** on `windows-latest` |
| Dependencies | none — standard library only (see [AGENTS.md](../AGENTS.md)) |

`pytest` is a development dependency, used only under `scripts/tests/`. The
runtime imports nothing outside the standard library, so a bare CPython install
is enough.

## Operating system

Windows. The hooks are PowerShell, and file locking falls back to `msvcrt`
region locks where `fcntl` is unavailable. `beyin_ortak._lock_exclusive()` keeps
both paths, so the Python side is portable in principle, but the hook layer is
not and no macOS or Linux path is tested here.

## Model backends

### `claude` CLI — the default, and the only one that can compile

The **flag surface used** is exactly:

```
-p --model --output-format --safe-mode --tools --permission-mode --allowedTools
```

The prompt goes on **stdin**, never as an argument. `--output-format` is always
`text`. The compile call additionally needs `--permission-mode acceptEdits` with
a matching `--allowedTools`, because it is the one call that writes files.

If a future `claude` release renames or removes any of those flags, the call
fails and the run records a health error rather than proceeding. There is no
version detection and no fallback flag set — this repository does not attempt to
support more than one CLI generation at a time.

### `agy` — Antigravity headless

Built against the **2026-08 headless contract**:

```
agy -p "<prompt>" --model <slug> --output-format text
```

Note the difference from `claude`: `agy` takes the prompt as an **argument**, not
on stdin. This backend is summarisation-only. It **refuses the compile call** —
it cannot provide the scoped staging-tree writes that `--tools` plus
`acceptEdits` give, and silently compiling without those gates would remove the
compiler's security boundary. Binary resolution can be overridden with
`BEYIN_AGY_BIN`.

### `ollama` — local inference

`POST {BEYIN_OLLAMA_URL}/api/generate`, default base `http://localhost:11434`,
non-streaming, response read from the `response` field. Model slugs come from
`BEYIN_OLLAMA_MODEL_FAST` / `BEYIN_OLLAMA_MODEL_SMART`. Summarisation only; the
compile call is refused for the same reason as `agy`.

### OpenAI-compatible endpoints

`POST {BEYIN_OPENAI_URL}/chat/completions` with a `Bearer` key from
`BEYIN_OPENAI_KEY`, response read from `choices[0].message.content`. This is the
*shape* of the OpenAI chat API, which many local and hosted servers implement;
what is tested is that shape, not any particular provider. Summarisation only.

Local-model guidance, including timeouts:
[docs/local-models.md](local-models.md).

## What "untested" means here

- **No version pinning is possible.** These are external binaries and endpoints
  resolved at run time from `PATH` or a URL. The repository cannot assert a
  version, so it does not pretend to.
- **No version detection is performed.** Nothing runs `claude --version` and
  branches on it. A changed flag surface surfaces as a failed call.
- **Failures are quiet by design.** Every entry point catches its own errors,
  writes a reason through `write_health()`, and returns 0 so the session it is
  attached to is never broken. A backend that stopped working shows up in
  `python scripts/durum.py` or the `beyin doktor` skill, not as an error in your
  terminal. Check there first when summaries stop appearing.
- **Newer is not assumed compatible.** A later `claude` or `agy` release may work
  unchanged; it has not been verified, and the honest statement is that
  compatibility beyond the versions above is unknown.
