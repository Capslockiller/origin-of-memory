# Provenance schema for bench/results

This document is a contract, not a guide. "MUST", "MUST NOT", and "REQUIRED"
are binding. A file under `bench/results/` that does not satisfy this
contract is a claim, not evidence, regardless of what numbers it contains.

## 0. The rule

A published figure counts as evidence only if the file carrying it names,
structurally, all four of:

1. **who measured it** — a person or agent identity.
2. **which command produced it** — the exact invocation, reproducible.
3. **which raw artifact holds the output** — a path to the file this
   summary was written from, distinct from the summary itself.
4. **which commit or binary hash it was measured against** — the exact
   source or executable under test.

Any of the four missing means the number is a claim. Free-text explanation
in a `note` field does not substitute for any of the four — a fact that
matters only when read is not a fact recorded.

## 1. Survey: what bench/results/*.json already carries

Twelve files exist today. None satisfies the full tuple; two come close.

| canonical field (this schema) | existing name(s) found in the corpus | present in |
| --- | --- | --- |
| `measured_by` | `yazan` | `vm-2026-09-10.json`; `recall-2026-09-10-gate.json` (top level and inside `baseline`/`final`) |
| `command` | `command`, `backend.command`, per-step `komut` | `gate10-*.json`, `mutation-2026-09-09.json`, `oom-bench-sentetik-2026-09-09.json`, `recall-2026-09-09*.json` (as `backend.command`), `vm-2026-09-10.json` (per step) |
| `source_commit` | `source_commit` | `vm-2026-09-10.json`; `recall-2026-09-10-gate.json` (inside `baseline`/`final`) |
| `binary_sha256` | `exe_sha256`, `publish_exe.sha256` | `recall-2026-09-10-gate.json` (`exe_sha256`); `vm-2026-09-10.json` (`publish_exe.sha256`, `evidence_exe` has none) |
| `raw_artifact` | none at file level; closest is per-step `kanit` | `vm-2026-09-10.json` only, and only per step, not for the file's own top-level numbers |
| `run_status` | none; closest precedent is `authoritative: false` and `run_kind: "synthetic smoke"` | `yerel-2026-09-09.json` |
| `substitute` | none structural; a prose `note` on a nested object | `recall-2026-09-09*.json` family, buried at `hook_gate_probe.note` |

`measured_by`, `run_status`, `raw_artifact`, and `substitute` as structural
fields are **introduced by this schema** — no file has them under those
names today. `command`, `source_commit`, and `binary_sha256` already exist
under other names in some files; this schema makes those names canonical
and closes the gap in the files that lack them (`gate10-*.json`,
`mutation-2026-09-09.json`, `oom-bench-sentetik-2026-09-09.json`,
`recall-2026-09-09*.json`, `yerel-2026-09-09.json` all lack any commit or
binary hash entirely). Existing files are not rewritten by this document;
they stand as the pre-contract baseline this schema is measured against.

## 2. Required fields — every file under bench/results/

Every `bench/results/*.json` file MUST carry these top-level fields:

- **`measured_by`** (string) — who or what ran the measurement (a person's
  name, or an agent identity such as `"claude"`, `"codex"`). Equivalent to
  the existing `yazan` field; new files MUST use `measured_by`.
- **`command`** (string) — the literal command line that produced this
  file's numbers, reproducible by a reader with the same tree checked out.
- **`run_status`** (string enum: `"ok"` | `"degraded"` | `"invalid"`) — see
  §3. MUST be present even when the run is clean.
- **`raw_artifact`** (string, relative path) — the path to the raw log or
  output file this summary was generated from. MUST NOT be the results
  file's own path. If no raw artifact was kept, `run_status` MUST be
  `"invalid"` and this field MUST hold the string `"none-kept"` — silent
  omission is not permitted.
- At least one of **`source_commit`** (git commit hash the source tree was
  at) or **`binary_sha256`** (SHA-256 of the executable under test).
  Both SHOULD be present when the measurement exercises a built binary;
  `source_commit` alone is acceptable for a source-level tool (e.g. a
  Python script with no compiled artifact).

## 3. Degraded and invalid runs

`run_status` MUST be `"degraded"` if any component of the run recorded a
failure that a reader could not otherwise infer from the top-level numbers
— including but not limited to: a non-null `error` field anywhere in the
file (e.g. `index.error`), a step marked failed, or a skipped stage that a
complete run would have executed. `run_status` MUST be `"invalid"` if the
failure makes the reported numbers meaningless on their own terms (not
merely incomplete).

**Rule (binding): if `run_status` is not `"ok"`, every field named `pass`
or nested under a key named `pass`, and every field under a key named
`decision`, MUST be `null` or absent — none may read `true`.** A degraded
run MAY still report raw metric numbers (`overall`, `recall@N`, etc.) for
diagnostic purposes, but it MUST NOT assert that a gate was passed.

This is not a hypothetical failure mode. `bench/results/recall-2026-09-09-r2-gate.json`
carries `index.error: "FileNotFoundError: <state-root>\\state.db"` in the
same file as `pass.recall@5: true` and `pass.recall@3: true`. That
co-occurrence — a recorded index failure alongside an asserted pass — is
exactly the shape this schema forbids. A file in that shape is not
evidence that the gate passed; it is evidence that the run broke before
the gate could be evaluated, dressed with numbers that survived the
failure by accident.

## 4. Substitute and stand-in measurements

If a run measures with a dataset, probe set, tool, or environment other
than the one a spec (README, gate definition, or prior result) calls for,
the file MUST carry a top-level structural field:

```json
"substitute": {
  "active": true,
  "for": "<name or path of the spec'd artifact this stands in for>",
  "reason": "<why the substitution happened>"
}
```

When no substitution occurred, the field MUST still be present with
`"active": false` — omitting the field entirely is itself an implicit,
unverifiable claim of no substitution, and this schema does not accept
implicit claims. A prose note on an unrelated nested object (e.g. a
`note` string inside `hook_gate_probe`, as in
`bench/results/recall-2026-09-09-r2-gate.json`, which records "substitute
probe: the spec's 30-prompt probe set does not exist in bench/" three
levels deep, nowhere near the top level a reader checks first) does not
satisfy this requirement — the declaration MUST be at the top level, in
the `substitute` key, independent of which nested section happens to use
the substitute.

## 5. Optional fields

Everything else already in use across `bench/results/*.json` — `machine`,
`model`, `thresholds`, `gate`, `backend`, `dataset`, `latency_ms`,
`per_sinif`, `canaries`, `misses`, `records`, and any measurement-specific
structure a tool needs to emit — remains free-form and is not constrained
by this schema. Adding fields beyond §2–§4 never violates this contract;
omitting a field required by §2–§4, or hiding what §3–§4 require to be
structural inside prose, does.

## 6. Scope

This schema governs files under `bench/results/`. It does not modify any
existing file — the survey in §1 is a baseline, not a migration. Every
results file written after this document lands MUST comply in full.
