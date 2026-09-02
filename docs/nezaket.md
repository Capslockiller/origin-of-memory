---
yazan: claude
model: sonnet
---

# A7 nezaket (politeness layer)

## What it does

`scripts/nezaket.py` defers background model work — `compile.py`, `watcher.py`,
and `ingest.py` — while the machine is actively being used for something else:
Unreal compiling shaders, a render in Blender, a fullscreen game, OBS
recording. Instead of a background model call fighting that work for GPU and
CPU, the entrypoint exits before doing anything and the call sits in a queue
until it is explicitly released.

The gate is fail-open by design. Every signal it reads can come back unknown,
and unknown never counts as busy — only positive evidence (an allow-listed
process, a busy GPU, a fullscreen game) blocks a run. On any non-Windows
platform every probe is a no-op that returns `None` immediately, so the gate
does nothing at all off Windows.

## Signals

All probes live behind `SinyalKaynagi` and are Windows-only:

- **Foreground process** — the process owning the foreground window
  (`GetForegroundWindow` → `GetWindowThreadProcessId` →
  `QueryFullProcessImageNameW`).
- **Its parent process** — via a `CreateToolhelp32Snapshot` walk. Only the
  direct parent is checked; `steamwebhelper.exe`, for example, is Steam's
  child, not its parent, so it never counts as `steam.exe` launching
  something.
- **GPU load** — `nvidia-smi --query-gpu=utilization.gpu`, the highest value
  across GPUs. `None` when `nvidia-smi` is not on `PATH`, which also means
  this signal is silent on non-NVIDIA machines.
- **Fullscreen state** — the foreground window's rect compared to its
  monitor's rect.
- **Idle time** — `GetLastInputInfo` against `GetTickCount64`.

Every probe is wrapped so any exception becomes `None` rather than raising —
fail-open, but every `None` is a real, loggable return value (see `sonda`
below), not a swallowed error.

## Decision rules

In priority order:

1. Foreground process name is in `nezaket-izin.json`'s `surecler` → busy.
2. Foreground window's parent process name is in `ust_surecler` → busy.
3. GPU load is at or above `gpu_esik` → busy.
4. Foreground window is fullscreen:
   - process name is in `zararsiz_tam_ekran` (a browser, a video player) →
     **not** busy.
   - anything else → busy ("oyun sezgisi" — the fullscreen-game heuristic).
5. Every signal above is unknown (`None`) → **not** busy, `bilinmiyor=true`.
6. Idle time is at or above `bosta_serbest_sn` (default 900 s / 15 min) and no
   signal above claimed busy → **not** busy ("bosta").
7. Otherwise → **not** busy.

Idle time is checked last on purpose: it can never override a busy verdict
reached above it. A machine sitting idle in front of a compiling Unreal
Editor stays busy no matter how long the keyboard has been quiet.

## Explicit release only

A busy verdict appends one record to `nezaket-kuyruk.json`
(`{id, tur, argv, eklendi, neden}`, deduped on `(tur, argv)`) and the
entrypoint returns exit code **75** (`EX_TEMPFAIL`) without opening its lock
or calling a model. There is **no automatic release and no night window** —
this was a deliberate decision, not an oversight. A queued call runs only
when a human releases it by id, either from the CLI (`nezaket.py serbest
<id>`) or the panel's Nezaket card.

## Env vars

- `BEYIN_NEZAKET=off` — the gate is skipped entirely for that run: no probe,
  no queue entry, no health note. Everything else behaves exactly as before
  this feature existed.
- `--nezaket-del` (a flag on `compile.py`, `watcher.py`, and `ingest.py`) —
  bypasses the gate for one invocation. This is what the panel uses to replay
  a released queue entry without it getting re-queued if the machine is
  somehow still busy.
- `BEYIN_NEZAKET_ARKAPLAN=0` — stops `compile.py` from asking Windows to
  schedule itself in `PROCESS_MODE_BACKGROUND_BEGIN` at startup (on by
  default; no-op off Windows).
- `BEYIN_OLLAMA_KEEP_ALIVE` — passed through as Ollama's `keep_alive` when
  the gate is not busy and this is set; omitted when unset. Ignored while
  busy, when `keep_alive` is forced to `0` instead (see below).

## Ollama VRAM handling

On the transition from free to busy, `vram_bosalt()` calls Ollama's
`GET /api/ps` and asks it to drop every currently-loaded model
(`POST /api/generate {"model": name, "keep_alive": 0}`), so a game or a
render gets the VRAM back instead of sharing it with an idle-loaded model.
While busy, every request the Ollama runner makes carries `keep_alive: 0` for
the same reason. Both directions are best-effort: a failure is folded into a
returned problem list and never raises.

## Child process priority

`compile.py`, `watcher.py`, and `ingest.py` spawn Claude/Codex CLI children
through a couple of shared spawn sites (`claude_runner.py`,
`ingest_common.py`'s `_run_codex`); both now pass
`creationflags=IDLE_PRIORITY_CLASS` on Windows (`0` — a no-op — everywhere
else), so a background model call never outcompetes foreground work for CPU
scheduling even when the gate decided it was fine to run. The Antigravity CLI
spawn site (`agy_runner.py`) does not yet get this flag — see Limits.

## CLI

```
python scripts/nezaket.py durum [--json]     # current decision + queue summary
python scripts/nezaket.py kuyruk              # list the queue as JSON
python scripts/nezaket.py serbest ID [ID...]  # release ids to run
python scripts/nezaket.py kaldir ID [ID...]   # discard ids without running them
python scripts/nezaket.py izin-yaz            # write nezaket-izin.json defaults
python scripts/nezaket.py sonda               # raw signal dump + per-probe timing
```

All commands accept `--state-dir` (defaults to `scripts/.state`, the same
directory `compile.py`/`watcher.py`/`ingest.py` already use).

## Panel

The panel's Nezaket card (polled every 10 s from the page — no new
persistent process) shows the current decision (Meşgul/Serbest and its
reason), the deferred queue with a checkbox per row, and the oldest waiting
time. "Seçilenleri çalıştır" releases the checked ids through
`POST /api/action/nezaket-serbest`. This route never drops work: if another
operation is already running it responds `409 operation_in_progress` and
leaves the queue untouched, checked before anything is popped — no id is
released and no state changes. Otherwise it pops **exactly one** id — the
first of the ones checked — and starts it; every other checked id is left in
the queue exactly as it was. The response is `{started, remaining_selected}`;
the panel shows a note naming how many selected records are still queued
when `remaining_selected > 0`, and click the button again (after the running
operation finishes) to release the next one.

## `nezaket-izin.json`

```json
{
  "surecler": ["UnrealEditor.exe", "blender.exe", "obs64.exe", "..."],
  "ust_surecler": ["steam.exe", "EpicGamesLauncher.exe"],
  "gpu_esik": 60,
  "zararsiz_tam_ekran": ["chrome.exe", "vlc.exe", "..."],
  "bosta_serbest_sn": 900
}
```

Process names are matched case-insensitively. A missing file uses the
built-in defaults (`nezaket.py izin-yaz` writes them out for editing). A
malformed file, or one with the wrong shape (e.g. `surecler` not a list),
**fails loud** — the load raises rather than silently falling back to
defaults, and the gate itself catches that, writes a `nezaket-izin-bozuk`
health entry, and lets the run through unblocked rather than breaking real
work over a bad config file.

## Limits

- Every probe is Windows-only; this whole feature is a no-op on Linux/macOS.
- The fullscreen heuristic is exactly that — a heuristic. It only
  distinguishes "known-harmless fullscreen app" from "anything else"; it
  cannot tell a fullscreen game from, say, a fullscreen terminal running a
  long build.
- GPU load is NVIDIA-only (`nvidia-smi`); it reads `None` on other GPU
  vendors, which is silent, not a false negative — the other signals still
  apply normally.
- `agy_runner.py`'s Antigravity CLI spawn does not get the idle-priority
  flag yet (only the `claude` and `codex` spawn sites do); a follow-up should
  add it there too if the Antigravity backend becomes the common path.
- `--dry-run` on `compile.py`, and `--dry-run`/`status` on `ingest.py`, are
  exempt from the gate — they take no lock and make no model call, so there
  is nothing worth deferring, and gating them would queue a run that was
  never going to do anything.
- `watcher.py` gates differently depending on `--once`. A one-shot run
  behaves exactly like `compile.py`: gate once, and if busy, enqueue and
  exit 75 without sweeping. A long-running watcher (no `--once`) instead
  calls `nezaket.mesgul_mu()` fresh at the top of **every** loop iteration
  and never exits because of a busy reading — it just skips that sweep and
  sleeps. It never enqueues anything either; the deferred-record queue only
  ever gets an entry from a gated `--once` run (or from `compile.py`/
  `ingest.py`). The `nezaket-ertelendi` health skip is written once per
  free→busy transition, not once per skipped sweep, so a watcher parked
  behind a long busy stretch does not spam the health log every interval.
- `durum --json` (what the panel polls every 10 s) reuses the last probed
  `Okuma` instead of probing again when it is younger than
  `BEYIN_NEZAKET_ONBELLEK_SN` seconds (default 8; 0 disables the cache).
  `sonda` always probes fresh — it exists specifically to show live,
  uncached per-probe timing — and so do `kapi()`/`mesgul_mu()`'s real gating
  decisions; only the read-only status view is cached.
- The Ollama runner's busy `keep_alive: 0` override reads `son_karar()` —
  the **last decision `Durum` recorded**, not a live probe. That decision is
  only refreshed when something calls `kapi()`/`mesgul_mu()` (an entrypoint
  run, or a watcher loop iteration) or writes through `_onbellekli_oku_gercek`
  (a `durum --json` poll). If nothing has polled or gated recently, this can
  be stale mid-run — e.g. the panel not open, or a watcher sitting on a long
  `--interval` — and a machine that just became busy may still see one
  `keep_alive`-bearing Ollama call go out before the next decision lands.
