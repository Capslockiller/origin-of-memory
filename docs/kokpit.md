---
yazan: claude
model: sonnet
---

# F5 "Kokpit" (Cockpit) — part 1: Kule (the tower)

## What it is

The tower gives jobs, the panel shows. `scripts/kule.py` is a standalone,
headless, multi-lane job manager for background `claude`/`codex` CLI work —
completely separate from the (not-yet-built) panel. Every number a future
panel would show — queue counts, lane occupancy, a job's status, its last
event line, its diffs — is written by this module to a state file first.
The panel's job, when it exists, is to read those files and render them; it
must never compute a count or a status itself. That is the constitution
this part follows even though part 1 has no panel at all yet.

A job here is one `claude -p` or one `codex exec` invocation, run as a
background child, streamed to a log, and tracked through a small state
machine until it succeeds, fails, is cancelled, or (if it touched files you
asked to watch) waits for your approval.

Jobs created with `--kaynak panel` are explicit user actions, the same
posture Kaydet takes: they bypass the A7 nezaket gate entirely
(`nezaket_del: true` is stamped on every job record) rather than sitting in
its deferred queue. A `--kaynak cli` job (the CLI default) does too — Kule
does not currently gate its own job intake through nezaket at all; nezaket
governs the *existing* compile/watcher/ingest entrypoints, and every one of
those keeps working exactly as before. Kule only calls
`nezaket.kendini_arka_plana_al()` (put this process in background scheduling
priority) and `nezaket.dusuk_oncelik_bayraklari()` (spawn children at idle
priority) — courtesy toward the machine, not a gate on the work.

## Job lifecycle

```
queued ──────────► running ──┬──► succeeded            (no watched files, or all unchanged)
  │                            │
  │ iptal()                    ├──► waiting-approval ──► succeeded   (onayla)
  │ (no process yet)           │        │                 └► rejected  (reddet)
  ▼                            │        └─(only reachable via onayla/reddet)
cancelled                      ├──► failed     (non-zero exit, timeout, missing CLI, crash)
                                └──► cancelled  (iptal() touched the .iptal marker mid-run)
```

- **`queued`** — created, not yet claimed by a worker.
- **`running`** — claimed, child process spawned, log streaming.
- **`waiting-approval`** — the child exited 0, at least one watched file's
  diff was non-empty. Terminal only via `onayla` (→ `succeeded`) or `reddet`
  (→ `rejected`).
- **`succeeded`** — child exited 0, and either no files were watched or
  every watched file was unchanged.
- **`failed`** — non-zero exit, a timeout kill, a missing `claude`/`codex`
  binary, a failure to spawn, or an unhandled exception in the worker
  itself. `hata` names which.
- **`cancelled`** — `iptal()` was called: immediately if the job was still
  `queued` (nothing to kill), or once the running worker noticed the
  `.iptal` marker and killed the tree.
- **`rejected`** — a human called `reddet` on a `waiting-approval` job; the
  diff is discarded (never applied — nothing in this module ever touches
  the watched files' real content, it only reads and records).

Any other transition (e.g. `onayla` on a `queued` job, `iptal` on a
`succeeded` one) is refused with `kule-gecis-gecersiz` — the job is
untouched.

## Store layout — `<state_dir>/kule/`

```
jobs/<id>.json        job record (atomic write)
jobs/<id>.prompt       the prompt, ALREADY redacted (secret_guard.redact)
jobs/<id>.log          the child's streamed stdout, appended line by line
jobs/<id>.log.1        one prior generation, once .log passed BEYIN_KULE_LOG_MAX_BAYT
jobs/<id>.iptal        cancellation marker (present ⇒ the worker kills the tree)
jobs/<id>/once/<n>     snapshot of watched file n before the run
jobs/<id>/sonra/<n>    snapshot of watched file n after the run
jobs/<id>/diff/<n>.diff   unified diff for watched file n
claims/<id>            O_CREAT|O_EXCL marker: which worker pid owns this job
lanes/<tur>/<id>       O_CREAT|O_EXCL marker: one occupied lane slot
locks/<id>.lock        per-job lock guarding onayla/reddet/iptal transitions
arsiv/                 the same file set as jobs/, for archived (never deleted) jobs
durum.json             the one file a reader needs — see below
dur                    stop-file: calis()'s continuous loop exits when this exists;
                       calis() removes any leftover one at its own startup — see
                       "Orphan / reaper semantics"
```

`jobs/<id>.json` fields: `id`, `tur` (`"claude"`|`"codex"`), `model` (an
explicit slug/tier — a job without one is refused, see below), `prompt_sha256`,
`prompt_karakter`, `cwd`, `olusturuldu` (ISO, display only) /
`olusturuldu_ns` (nanosecond counter — every "creation order" sort in this
module keys on this, not the ISO field, because two jobs created within the
same wall-clock second would otherwise sort arbitrarily), `durum`,
`baslangic`/`bitis`/`sure_sn`, `cikis` (child exit code), `son_olay` (last
non-blank log line, ≤200 chars), `olaylar` (line count), `artefaktlar`,
`diffler`, `hata`, `kaynak` (`"panel"`|`"cli"`), `nezaket_del`,
`izlenen_dosyalar`, `izinler`, `uyarilar` (secret-redaction warnings from
job creation), and — once a job reaches `waiting-approval`/terminal via
`onayla`/`reddet` — `onay: {ts, karar}`.

Prompt and log content never reach `calls.jsonl` — that ledger
(`beyin_ortak.record_call`, reused exactly as `claude_runner.run_claude`
calls it) is numeric accounting only: backend, model, character counts,
duration, outcome. `component="kule"`.

## Model is mandatory

Every job names an explicit model slug/tier — `is-ver` without `--model`, or
with an empty/whitespace one, is refused with `kule-model-eksik`. There is
no default model here (unlike `ingest_common.DEFAULT_MODEL`): a background
job you didn't watch running must never silently fall back to something you
didn't choose.

## Lanes and caps

A **lane** is one occupied concurrency slot for one job kind. `BEYIN_KULE_CLAUDE_TAVAN`
(default **3**) and `BEYIN_KULE_CODEX_TAVAN` (default **4**) cap how many
`claude`/`codex` children can run at once — Kural #14. A pass
(`_bir_gecis`) walks queued jobs in creation order; a job is claimed and
spawned only if its kind's lane count is currently below the cap. A job
that doesn't fit stays `queued` and is reconsidered on the next pass, once
some other job in that lane finishes and its marker is removed.

Claim and lane markers both use the same `O_CREAT|O_EXCL` pattern as
`flush.py`'s compile-trigger (~L513) and `compile.py`'s machine-id (~L1403):
the file is created only if it does not already exist, so two `kule calis`
processes racing to claim the same job (or the same lane slot) can never
both win. The marker's content is the *worker process's own pid*, used only
by the stale reaper below.

## Diff-approval flow (B3)

A job may declare `izlenen_dosyalar` — paths (absolute, or relative to its
`cwd`) the worker should watch. Every watched path is validated at job
creation, symlink-safe (`Path.resolve()`, `os.path.commonpath`): it must
resolve inside the job's own `cwd` (`kule-izlenen-cwd-disi` otherwise), the
list is capped at 50 entries, and each existing file at 5 MB — both caps
share `kule-izlenen-cok-buyuk`. `_read_watched` re-checks the same
containment at run time (not just at creation), closing the TOCTOU window a
symlink swapped in after validation would otherwise open. Before the child
spawns, each watched file's current content (or `""` if it doesn't exist
yet) is snapshotted to `once/<n>`. After the child exits 0, each file is
snapshotted again to `sonra/<n>`, and `difflib.unified_diff` produces
`diff/<n>.diff` plus `ekleme`/`silme` (added/removed line) counts in the
job record's `diffler[]`. If **any** watched file's diff is non-empty, the
job parks at `waiting-approval` instead of `succeeded` — a human must call
`onayla` or `reddet` before it's final. Kule itself never writes to a
watched file; it only reads, snapshots, and diffs.

`diffler[]`'s `once_yol`/`sonra_yol`/`diff_yol` are stored **relative to
`<state>/kule/`** (e.g. `jobs/<id>/diff/0.diff`), never absolute — a job's
whole `jobs/<id>` directory moves to `arsiv/<id>` when it archives (see
"Archiving" below), which would otherwise strand an absolute path pointing
at a location that no longer exists. `kule.py diff`/the panel's
`Get-KuleDiffText`/`kule-vscode` route resolve these against the job's
CURRENT location (`jobs/` while active, `arsiv/` once archived) and
re-validate the resolved path still lands under `<state>/kule/` before
opening it — refusing `kule-yol-disi` (CLI) / `kule_yol_disi` (panel HTTP,
400) otherwise, including against a tampered absolute path. The panel's
fallback is to display the stored diff text directly — it never recomputes
a diff itself, per the same "panel never computes" rule.

A job's `izinler` accepts only three keys, each from a closed set/pattern:
`permission_mode` ∈ `{default, acceptEdits, plan}`, `sandbox` ∈
`{read-only, workspace-write}`, and `allowed_tools` (a string, ≤ 200 chars,
`[A-Za-z0-9_,() *-]`). Any other key, or a value outside its set/pattern —
`bypassPermissions` and `danger-full-access` explicitly included — is
refused with `kule-izin-gecersiz` at job creation. The panel's
`/api/action/kule-is-ver` route runs the same allowlist before it ever
calls `kule.py`, as a fast pre-check; `create_job` stays the authoritative
one.

## Orphan / reaper semantics

`reap_stale(kule)` walks every claim marker (then, as a second sweep, every
lane marker not already handled) and checks whether its recorded pid is
still alive — `os.kill(pid, 0)` on POSIX, `OpenProcess`+`GetExitCodeProcess`
via `ctypes` (explicit `argtypes`/`restype`, the same discipline as
`nezaket._win32_baglama`) on Windows, falling back to parsing `tasklist`
output if `OpenProcess` itself is unavailable. A probe failure fails
**open** (never falsely reaps a live job).

If a marker's pid is dead and its job is still `queued`/`running`, the job
is moved to `failed`/`kule-worker-kayip`, and both its claim and lane
markers are released. If the job already reached a terminal state (the
marker just outlived a clean release), the marker is removed quietly with
no status change. `calis()` calls `reap_stale` at the top of every pass.

**On stop, running children are left running.** `calis()`'s continuous loop
returns as soon as it sees the `dur` file (or a `should_stop()` callback,
used by the CLI's own SIGINT/SIGTERM handling) — it does not kill or wait
for whatever it already launched. The marker files it wrote carry *this
worker process's own pid*, so once that process actually exits, the next
`kule calis` invocation's `reap_stale` sees a dead pid on the marker and
reclaims the job as `kule-worker-kayip` — even though the underlying
`claude`/`codex` child may keep running on its own as an untracked orphan
process. This is a deliberate, documented limitation, not an oversight: a
clean multi-process handoff of a live child is out of scope for part 1.

**`dur` is a stop signal for the running instance only, not a durable
"stay stopped" marker.** `Stop-Kule` writes it and a running `calis()`
loop reads it, but nothing else ever deleted it — a leftover `dur` from a
previous run would otherwise make every future `calis()` call return
immediately, forever, without ever claiming a job again. `calis()` removes
it once, right at its own startup, before the loop is ever entered; the
panel's `Start-Kule` also moves it out of the way (to `dur.onceki` — moved,
not deleted, same posture as archiving below) before spawning a fresh
process, belt and braces.

## Cancellation and timeout

`iptal <id>` on a `queued` job cancels it immediately (nothing to kill). On
a `running` job it touches `jobs/<id>.iptal`; a **background reader
thread** owns the only blocking call (`process.stdout.readline()`) and
pushes each line — plus a final EOF sentinel — onto a `queue.Queue`. The
job's own poll loop never calls `readline()` itself: it does
`queue.get(timeout=1.0)` (a real wall-clock wait, not tied to a line ever
arriving) and, on **every tick** — a line arrived, the 1-second wait simply
elapsed with nothing queued, or EOF — checks, in order: has
`jobs/<id>.iptal` appeared (kill the tree, `cancelled`)? has
`BEYIN_KULE_ZAMAN_ASIMI` (default **3600** seconds) elapsed since the job
started (kill the tree, `failed`/`kule-zaman-asimi`)? was this tick an EOF
(stop streaming, move to exit-code handling)? A child that never writes a
single byte to stdout is therefore just as cancellable and just as subject
to the timeout as a chatty one — the poll loop's own tick is what notices,
not the arrival of a line. On either kill, the tree goes down via
`kaydet._kill_process_tree` (POSIX `process.kill()`, Windows
`taskkill /T /F`), the same helper `kaydet.py` uses for its own
compile-spawn timeout; the reader thread's blocked `readline()` then
unblocks on its own once the (now-dead) child's stdout pipe closes, and is
joined with a short grace period.

The **`dur` file is deliberately not part of this per-job tick.** It is the
outer `calis()` loop's own stop signal (see "Orphan / reaper semantics"
above) — checking it here and killing the tree on it would contradict "on
stop, running children are left running." Only `.iptal` and the timeout
kill a job's own child; `dur` only stops the worker from claiming *new*
jobs.

`son_olay`/`olaylar` are batched to the job record at most once every 2
seconds while streaming — never once per line, and not on every 1-second
tick either — so neither a chatty child nor the poll loop itself can turn
this into an atomic-write storm.

`jobs/<id>.log` is capped at `BEYIN_KULE_LOG_MAX_BAYT` (default **20 MiB**):
once a line's write pushes it past the cap, the current log is rotated
*once* — moved (`os.replace`, never deleted) to `jobs/<id>.log.1`,
overwriting any prior one — and streaming continues into a fresh, empty
`<id>.log`. This is a single generation, not a chained logrotate. The job
record notes `warn:kule-log-kirpildi` in `uyarilar` the moment this
happens. The panel's own log tail (`Get-KuleJobDetail`) never reads more
than the last 64 KB of whatever `.log` currently holds regardless of this
cap, via a `FileStream` seek rather than loading the whole file.

## Archiving

`durum.json` is rewritten (and an archive pass runs) after every
job-store-level state change — creation, a worker finishing a job, an
`onayla`/`reddet`/`iptal`, and a `reap_stale` pass. (The 2-second batched
`son_olay` updates during streaming do **not** each trigger this — only the
events above do.) If more than `BEYIN_KULE_TAVAN` (default **50**) jobs in a
*terminal* state (`succeeded`/`failed`/`cancelled`/`rejected`) exist, the
oldest ones beyond the cap are **moved** — `os.replace`, whole
`jobs/<id>.json`/`.prompt`/`.log`/`.iptal` plus the `jobs/<id>/`
once/sonra/diff directory — to `arsiv/`. Nothing is ever deleted. `queued`,
`running`, and `waiting-approval` jobs are never archived regardless of the
cap — they are still active or still need a human decision.
`goster`/`log`/`diff` look in `jobs/` first, then `arsiv/`, so an archived
job stays fully inspectable.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `BEYIN_KULE_CLAUDE_TAVAN` | `3` | Max concurrent `claude` jobs. |
| `BEYIN_KULE_CODEX_TAVAN` | `4` | Max concurrent `codex` jobs. |
| `BEYIN_KULE_TAVAN` | `50` | Terminal job records kept before the oldest are archived. |
| `BEYIN_KULE_ZAMAN_ASIMI` | `3600` | Per-job timeout in seconds before the tree is killed. |
| `BEYIN_KULE_ARALIK` | `2` | Seconds `calis()`'s continuous loop sleeps between passes. |
| `BEYIN_KULE_LOG_MAX_BAYT` | `20971520` (20 MiB) | Per-job `.log` size before a one-time rotation to `.log.1`. |

Every one of these follows the repo's usual convention: unset, unparsable,
or non-positive falls back to the default rather than erroring.

## CLI

```bash
python scripts/kule.py is-ver --tur claude --model sonnet --prompt-dosya p.txt --cwd D [--izlenen F...] [--izin K=V ...] [--kaynak panel|cli] [--json]
python scripts/kule.py durum [--json]
python scripts/kule.py goster <id> [--json]          # record + last 30 log lines
python scripts/kule.py log <id> [--n N]               # tail the event log
python scripts/kule.py diff <id> <n>                   # print one stored diff
python scripts/kule.py onayla <id>
python scripts/kule.py reddet <id>
python scripts/kule.py iptal <id>
python scripts/kule.py calis [--once]                  # the worker loop
python scripts/kule.py arsivle                         # force an archive pass
```

`--state-dir`/`--vault-root` are top-level flags — they come **before** the
subcommand, e.g. `kule.py --state-dir X is-ver ...`, following the same
`argparse` subparser shape every other beyin CLI in this repo uses.

`is-ver --json` prints exactly one line prefixed `KULE-SONUC ` (the same
marker convention as `kaydet.py`'s `KAYDET-SONUC` and `pasaport_kapi.py`'s
`PASAPORT-SONUC`) so a future panel's spawned child can find it in stdout
regardless of whatever else the process printed. `--izin` accepts
`KEY=VALUE` pairs merged into the job's `izinler`: `permission_mode` and
`allowed_tools` for a `claude` job (passed through as `--permission-mode`/
`--allowedTools`), `sandbox` for a `codex` job (default `workspace-write`,
passed through as `--sandbox`).

Like every other top-level beyin entrypoint, `main()` returns immediately
(exit 0, doing nothing) if `BEYIN_INVOKED_BY` is already set — a model
running inside its own tool call must never be able to invoke `kule.py`
itself.

## Security notes

- **Prompt redaction happens before anything else.** `secret_guard.redact`
  runs on the prompt at job-creation time, before it is written to
  `jobs/<id>.prompt` and before it is ever handed to a spawned
  `claude`/`codex` process — there is no code path by which an unredacted
  prompt reaches disk or a model. A hit is recorded as a
  `warn:secret-redacted-prompt:<patterns>` entry in the job's `uyarilar`,
  never a refusal.
- **Only two job kinds exist**: `claude` and `codex`, both validated at
  creation (`kule-tur-bilinmiyor` otherwise). There is no generic "run any
  command" job type.
- **The cwd rule.** A job's `cwd` must exist and must **not** be under the
  OS temp root (`kule-cwd-gecersiz` otherwise) — Codex's sandbox cannot
  read paths under Windows Temp (measured; see
  `skills/codex-fleet/SKILL.md`, "the sandbox cannot read Windows Temp
  paths"), and the same restriction is applied uniformly to both job kinds
  rather than only to `codex` ones.
- **Env isolation.** Every spawned child gets `BEYIN_INVOKED_BY=kule` in
  its environment — the same isolation convention `claude_runner.run_claude`
  uses (`BEYIN_INVOKED_BY=beyin-scripts`) and `ingest_common._run_codex`
  uses, so a model running inside a Kule job cannot recursively invoke
  another top-level beyin entrypoint (they all check this variable and
  return immediately).

## Part 2 — the panel

Everything above (part 1) is unchanged; part 2 is `beyin.ps1`/`gui/panel.html`
reading it. The panel still never computes: every value shown comes from
`kule/durum.json`, a job record, a job log, or a stored diff file.

### Kule's own lifecycle

`beyin.ps1` spawns `kule.py calis` hidden as its own child when the panel
starts (`Start-Kule`, mirroring `Start-PasaportIzleyici`) and stops it
(`Stop-Kule`) in the single shutdown path both an explicit quit and the idle
timeout funnel through — same posture as the pasaport listener: **born with
the panel, dies with it**. `BEYIN_KULE=off` skips spawning it entirely,
checked before anything else, same as `BEYIN_PASAPORT_IZLEYICI=off`.

Stopping is graceful-then-forced: `Stop-Kule` writes `<state>/kule/dur` (the
same stop-file `calis()`'s own loop checks at the top of every pass — see
"Orphan / reaper semantics" above), waits up to 5 seconds for the process to
notice and exit on its own, and only then calls `.Kill()`. **Any
`claude`/`codex` child kule already spawned for a running job is
intentionally left running** either way, exactly as documented above for
`calis()`'s own graceful stop — the next `kule calis` (the next panel
launch) reaps it as `kule-worker-kayip` once this process's pid is gone.
`Start-Kule` moves any leftover `dur` out of the way (to `dur.onceki`)
before spawning — belt and braces alongside `calis()`'s own startup
cleanup, so a stale marker from an earlier stop can never make a freshly
spawned worker sit idle forever.

### The VS Code bridge (B3) — and its fallback

Installing VS Code is the owner's job, not the panel's. `Find-VsCode` probes,
in this fixed order, cached for the whole panel session: `Get-Command code`
(the `code` CLI on `PATH`), then
`%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd`, then
`%ProgramFiles%\Microsoft VS Code\bin\code.cmd`. The result is exposed in
`GET /api/kule` as `vscode: {bulundu: bool, yol: str|null}`.

- If found: the job list's "VS Code'da aç" button
  (`POST /api/action/kule-vscode {id, n}`) runs
  `Start-Process code --diff <once> <sonra>`. The two paths are read from
  the job record's own `diffler[n].once_yol`/`sonra_yol` fields — written
  only by `kule.py` itself — **never** assembled from the request; a
  request naming an out-of-range `n` gets `404 kule_diff_yok`, never a
  guessed path. Both fields are relative to `<state>/kule/` and are
  re-resolved (`Resolve-KuleGoreliYol`) against the job's current location
  before either path is handed to `code` — a resolution that would land
  outside `<state>/kule/` gets `400 kule_yol_disi` instead.
- If not found: `409 vscode_yok` is returned instead, and the panel falls
  back to what it already has — the stored diff text from
  `diff/<n>.diff`, fetched via `GET /api/kule/diff` and shown read-only in
  a `<pre>`. The fallback is not degraded functionality bolted on
  afterward; it is the same "panel never recomputes a diff" contract
  `docs/kokpit.md`'s part 1 already promised for a future panel.

### Routes

All new routes sit behind the same session-cookie/Origin/Host envelope
(`Test-ApiEnvelope`) every other route does, and — unlike the *existing*
maintenance/pasaport-onayla operations — none of them touch
`$script:ActiveOperation` or `Start-PanelCommand`/SSE at all. Kule already
runs its own multi-lane worker streaming into job logs; these routes are
quick file reads/writes answered synchronously, so the panel's
one-operation-at-a-time slot stays completely free for doctor/compile/
index/watcher/pull/try/kaydet/pasaport-onayla, exactly as before.

| Route | Method | Does |
|---|---|---|
| `/api/kule` | GET | `kule/durum.json` verbatim, plus `vscode` and `calisiyor` (the tower child still alive?). |
| `/api/kule/is?id=<id>` | GET | One job's record + its log's last 60 lines. `id` validated `^[0-9a-f]{8,32}$` before any file is touched — `400` otherwise, `404 kule_is_yok` if the id is well-formed but unknown. Reads `jobs/`, then `arsiv/`. |
| `/api/kule/diff?id=<id>&n=<int>` | GET | The stored diff text (`text/plain`) for watched file `n`. `id` validated as above, `n` validated as a non-negative integer — both **before** any path is built, and the actual filesystem path always comes from the job record's own `diffler[n].diff_yol` (relative to `<state>/kule/`), resolved against the job's current location and re-validated as staying under `<state>/kule/` — `400 kule_yol_disi` if not, `404 kule_diff_yok` if the diff simply doesn't exist. |
| `/api/action/kule-is-ver` | POST `{tur, model, prompt, cwd?, izlenen?, izin?}` | Runs `kule.py is-ver --stdin --json --kaynak panel …`, prompt piped over stdin (never argv), returns the parsed `KULE-SONUC` JSON. `tur` ∈ `{claude, codex}`; `model` non-empty, ≤ 64 chars, `^[A-Za-z0-9._:-]+$`; `cwd` defaults to the vault root and must be absolute (kule.py's own `cwd`-not-under-temp rule is still the final word). `izlenen`/`izin` get the same fast, textual pre-check kule.py's own `create_job` authoritatively enforces (cwd containment + 50-file/5 MB caps; the three-key `izinler` allowlist) — `400 kule_izlenen_cwd_disi`/`kule_izlenen_cok_buyuk`/`kule_izin_gecersiz` before a subprocess is ever spawned. Bypasses the A7 nezaket gate the same way Kaydet does — this is an explicit user action, and kule already stamps `nezaket_del: true` on every job it creates. |
| `/api/action/kule-onayla` / `-reddet` / `-iptal` | POST `{id}` | Runs `kule.py onayla`/`reddet`/`iptal <id>`, `id` validated first. `200` with `{basarili: true, is: <job>}` on success, `409` with `{basarili: false, hata: <slug>}` on a refused transition (`kule-is-yok`, `kule-gecis-gecersiz`, …). |
| `/api/action/kule-vscode` | POST `{id, n}` | See "The VS Code bridge" above. |

Every PowerShell-side JSON array field that could be exactly one element
(`son_isler`, `reaper_eylemleri`, a job's `diffler`/`izlenen_dosyalar`/
`artefaktlar`/`uyarilar`) is re-wrapped with `@(...)` before it goes back
out — the same `ConvertFrom-Json` single-element-array collapse pitfall
`Invoke-NezaketDurum`/`Invoke-PasaportDurum` already guard against, and the
single most common shape for a Kule job's `diffler` (one watched file).

### The Kokpit card

A new "Kokpit" section (below Pasaport, above Operations) shows:

- A **lane meter row** — `claude: dolu/tavan`, `codex: dolu/tavan` — and a
  count chip per `durum` value, both read straight from `durum.json`'s
  `seritler`/`sayilar`.
- An **"İş ver" form** — tür (radio), model (a text input prefilled
  `sonnet`, with a datalist offering `sonnet`/`opus`/`haiku`/`gpt-5.6-sol`),
  prompt textarea, an optional absolute cwd, and optional watched files (one
  path per line) — submitting `POST /api/action/kule-is-ver` and showing the
  returned job id. The draft lives only in the form fields (never
  `localStorage`); the submit button's in-flight disable is local state
  (`kokpitSubmitting`), deliberately **not** wired into `setActive()` — this
  is not an SSE operation, so it must not flicker on/off with whatever
  unrelated operation happens to be running.
- A **job list** (the last 20 from `durum.json`) — id (short), tür, model,
  durum, süre, son olay, and per-row buttons: "Log" (fetches
  `/api/kule/is`, shows the last lines read-only), "İptal" (queued/running
  only), "Diff" (when the job has any — expands per watched file into a
  "Göster" button showing the stored diff text, plus "VS Code'da aç" when
  `vscode.bulundu`), and "Onayla"/"Reddet" (`waiting-approval` only).

`GET /api/kule` is polled every 5 seconds, guarded by a stacking flag
(`kokpitPolling`) so a slow response never causes two requests to be
in flight at once — every row and button is built with `createElement`/
`textContent`, never `innerHTML`.

B4's "ISTEK defteri kartı" requirement is satisfied by the **existing**
Pasaport blind-spot-map card (`docs/pasaport.md`, "The C4 ISTEK ledger") —
Kokpit adds only a one-line anchor link to it rather than a second copy.

### Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `BEYIN_KULE` | unset | `off` stops `beyin.ps1` from spawning the tower child (`kule.py calis`) at all. |

### Windows smoke tests

Everything above this line is exercised by the automated suite (part 1's
behavioural tests plus part 2's static, source-level contract tests and a
real-subprocess `is-ver` test — none of which need a live PowerShell
listener except the ones already gated `@unittest.skipUnless(POWERSHELL,
...)`). The following need a real Windows session and are not automated
here:

- Start the panel; confirm `kule/durum.json` appears within a few seconds
  and the Kokpit card's lane meters/counts populate.
- Submit a job through "İş ver" against a real `claude` (or `codex`) CLI;
  watch it move `queued` → `running` → `succeeded`/`waiting-approval` in the
  job list without a page reload.
- Give the job one watched file with an expected edit; confirm it parks at
  `waiting-approval`, "Diff" shows the correct stored diff text, "Onayla"
  moves it to `succeeded`, and a fresh job's "Reddet" moves it to
  `rejected` without touching the watched file.
- With VS Code installed and `code` on `PATH`, click "VS Code'da aç" on a
  waiting-approval job's diff; confirm VS Code opens a diff view of the
  exact `once`/`sonra` snapshot files. Rename `code` off `PATH` (or set
  `BEYIN_KULE=off` and re-run part 1's `kule.py` directly to produce a job
  without VS Code available) and confirm the button is absent and the
  stored diff text still renders read-only instead.
- Close the panel (and separately, let it idle-timeout) while a job is
  `running`; confirm `kule/dur` appears, the tower process exits within 5
  seconds, and the underlying `claude`/`codex` child is left running as an
  orphan (by design — see "Orphan / reaper semantics"); starting the panel
  again should reap that orphaned job as `kule-worker-kayip`.
- Set `BEYIN_KULE=off`, restart the panel, confirm no `kule.py calis`
  process starts and the Kokpit card shows `calisiyor: false` while
  `durum.json` (if present from an earlier run) still renders.
