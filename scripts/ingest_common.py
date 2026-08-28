#!/usr/bin/env python3
# yazan: codex
# model: gpt-5.6-sol
"""Arşiv kaynaklarını kasanın günlüğüne işleyen ortak omurga.

Her adaptör (Claude arşivi, Codex rollout, web ZIP) yalnız ``Session`` üretir;
özetleme, sır bekçisi, tarihsel günlük ekleme ve durum defteri burada tek
noktadan yürür. Böylece hiçbir adaptör sır bekçisini atlayamaz.
"""

from __future__ import annotations

import contextlib
import datetime as dt
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
from typing import Any, Iterator, NamedTuple, Sequence

from beyin_ortak import _atomic_write_json, _lock_exclusive, write_health
import claude_runner
import flush
import secret_guard


SCRIPT_DIR = flush.SCRIPT_DIR
VAULT_ROOT = flush.VAULT_ROOT
STATE_DIR = flush.STATE_DIR
STATE_NAME = "ingest-state.json"
HEALTH_NAME = "ingest-health.json"
LOCK_NAME = "ingest.lock"
DEFAULT_MODEL = "haiku"
DEFAULT_CODEX_MODEL = "gpt-5.6-sol"
CODEX_TMP_ROOT = Path(
    os.environ.get("BEYIN_TMP") or VAULT_ROOT / ".tmp" / "beyin"
)
STATE_VERSION = 1
# flush.build_flush_prompt beş başlık ister; kısa oturuk özet üretmez.
MIN_TURNS = 5
BACKFILL_NOTE = "> Bu dosya arşivden geriye dönük üretildi."
TERMINAL_STATUSES = ("appended", "bos", "skipped-live")

WHITESPACE = re.compile(r"\s+")


class Session(NamedTuple):
    """Tek bir arşiv oturumunun kaynaktan bağımsız gösterimi."""

    source: str
    key: str
    when: dt.datetime
    turns: list[tuple[str, str]]
    origin: str
    watermark: str = ""
    model: str = ""
    # Günlük başlığında kaynak yerine görünecek etiket (boşsa source kullanılır).
    label: str = ""
    # Adapter'ın gerçek bir kaynak oturum kimliğinden ürettiği canonical anchor.
    anchor: str = ""


class SummaryResult(NamedTuple):
    """summarize_session çıktısı: ``status`` ∈ ok | bos | fail."""

    summary: str
    status: str
    detail: str


def collapse(text: str) -> str:
    """Beyaz boşlukları tek boşluğa indirger (flush.read_transcript ile aynı)."""
    return WHITESPACE.sub(" ", text).strip()


def to_local(value: Any) -> dt.datetime | None:
    """ISO 8601 (UTC ``Z`` dahil) metnini yerel saat dilimine çevirir."""
    if not isinstance(value, str):
        return None
    text = value.strip()
    if not text:
        return None
    if text.endswith(("Z", "z")):
        text = text[:-1] + "+00:00"
    try:
        parsed = dt.datetime.fromisoformat(text)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    return parsed.astimezone()


def state_path(state_dir: Path = STATE_DIR) -> Path:
    return state_dir / STATE_NAME


def health_path(state_dir: Path = STATE_DIR) -> Path:
    return state_dir / HEALTH_NAME


def default_state() -> dict[str, Any]:
    return {"version": STATE_VERSION, "sources": {}, "last_run": {}}


def load_state(state_dir: Path = STATE_DIR) -> dict[str, Any]:
    path = state_path(state_dir)
    if not path.exists():
        return default_state()
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("ingest-state-not-object")
    state = default_state()
    state.update(value)
    if not isinstance(state.get("sources"), dict):
        raise ValueError("ingest-state-sources-invalid")
    if not isinstance(state.get("last_run"), dict):
        state["last_run"] = {}
    state["version"] = STATE_VERSION
    return state


def save_state(state: dict[str, Any], state_dir: Path = STATE_DIR) -> None:
    _atomic_write_json(state_path(state_dir), state)


def source_bucket(state: dict[str, Any], source: str) -> dict[str, Any]:
    sources = state.setdefault("sources", {})
    bucket = sources.setdefault(source, {})
    if not isinstance(bucket.get("done"), dict):
        bucket["done"] = {}
    if not isinstance(bucket.get("files"), dict):
        bucket["files"] = {}
    return bucket


def done_entry(
    state: dict[str, Any],
    source: str,
    key: str,
) -> dict[str, Any] | None:
    bucket = state.get("sources", {}).get(source)
    if not isinstance(bucket, dict):
        return None
    done = bucket.get("done")
    if not isinstance(done, dict):
        return None
    entry = done.get(key)
    return entry if isinstance(entry, dict) else None


def files_map(state: dict[str, Any], source: str) -> dict[str, Any]:
    bucket = state.get("sources", {}).get(source)
    if not isinstance(bucket, dict):
        return {}
    files = bucket.get("files")
    return files if isinstance(files, dict) else {}


def should_skip(entry: dict[str, Any] | None, retry_failed: bool) -> bool:
    """Defterdeki kayıt bu koşuda atlanmalı mı?"""
    if entry is None:
        return False
    status = str(entry.get("status", ""))
    if status in TERMINAL_STATUSES:
        return True
    if status.startswith("fail"):
        return not retry_failed
    return False


def record_done(
    state: dict[str, Any],
    source: str,
    key: str,
    status: str,
    daily: str = "",
    watermark: str = "",
) -> None:
    bucket = source_bucket(state, source)
    entry: dict[str, Any] = {
        "ts": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        "daily": daily,
        "status": status,
    }
    if watermark:
        entry["watermark"] = watermark
    bucket["done"][key] = entry


def record_file(
    state: dict[str, Any],
    source: str,
    name: str,
    size: int,
    mtime: float,
) -> None:
    bucket = source_bucket(state, source)
    bucket["files"][name] = {"size": int(size), "mtime": int(mtime)}


def file_unchanged(
    state: dict[str, Any],
    source: str,
    name: str,
    size: int,
    mtime: float,
) -> bool:
    entry = files_map(state, source).get(name)
    if not isinstance(entry, dict):
        return False
    return entry.get("size") == int(size) and entry.get("mtime") == int(mtime)


def _run_claude_with_model(
    prompt: str,
    vault_root: Path,
    model: str,
    timeout: int | None = None,
) -> tuple[str | None, str | None]:
    """Run Claude with a selectable model through the shared hardened runner."""
    if timeout is None:
        timeout, _warning = claude_runner.resolve_timeout("ingest")
    return claude_runner.run_claude(
        prompt,
        model=model,
        tools="",
        timeout=timeout,
        vault_root=vault_root,
        temporary_prefix="beyin-ingest-",
        component="ingest",
        state_dir=STATE_DIR,
    )


def _run_codex(
    prompt: str,
    vault_root: Path,
    model: str = DEFAULT_CODEX_MODEL,
    timeout: int | None = None,
) -> tuple[str | None, str | None]:
    """Codex CLI'yi vault dışında, salt-okunur sandbox ve stdin ile çalıştırır."""
    if timeout is None:
        # Codex is its own CLI, not a BEYIN_MODEL_BACKEND target, so it takes the
        # BEYIN_INGEST_TIMEOUT override but never the local-inference bump.
        timeout, _warning = claude_runner.resolve_timeout(
            "ingest", backend=claude_runner.BACKEND_CLAUDE
        )
    codex = shutil.which("codex")
    if codex is None:
        return None, "codex-cli-missing"
    command = [codex]
    if os.name == "nt" and Path(codex).suffix.casefold() in {".cmd", ".bat"}:
        # CreateProcess batch dosyasını doğrudan açamaz; shell=True yerine
        # sabit cmd.exe köprüsü kullanılır. Prompt hâlâ yalnız stdin'dedir.
        command = [os.environ.get("COMSPEC", "cmd.exe"), "/d", "/s", "/c", codex]

    try:
        temporary_root = CODEX_TMP_ROOT.resolve()
        vault_resolved = vault_root.resolve()
        try:
            root_inside_vault = (
                os.path.commonpath([temporary_root, vault_resolved])
                == str(vault_resolved)
            )
        except ValueError:
            root_inside_vault = False
        if root_inside_vault:
            return None, "temporary-directory-inside-vault"
        temporary_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(
            prefix="beyin-codex-",
            dir=temporary_root,
        ) as temporary:
            temporary_path = Path(temporary).resolve()
            try:
                inside_vault = (
                    os.path.commonpath([temporary_path, vault_resolved])
                    == str(vault_resolved)
                )
            except ValueError:
                inside_vault = False
            if inside_vault:
                return None, "temporary-directory-inside-vault"
            output_path = temporary_path / "last-message.txt"
            environment = os.environ.copy()
            environment["BEYIN_INVOKED_BY"] = "beyin-scripts"
            result = subprocess.run(
                command
                + [
                    "exec",
                    "--skip-git-repo-check",
                    "--sandbox",
                    "read-only",
                    "-m",
                    model,
                    "-c",
                    'model_reasoning_effort="medium"',
                    "--color",
                    "never",
                    "--output-last-message",
                    str(output_path),
                    "-",
                ],
                input=prompt,
                text=True,
                encoding="utf-8",
                errors="replace",
                capture_output=True,
                cwd=temporary_path,
                env=environment,
                timeout=timeout,
                check=False,
            )
            if result.returncode != 0:
                return None, f"codex-exit-{result.returncode}"
            output = ""
            try:
                if output_path.is_file():
                    output = output_path.read_text(encoding="utf-8").strip()
            except OSError:
                output = ""
            if not output:
                output = result.stdout.strip()
            return output, None
    except subprocess.TimeoutExpired:
        return None, "codex-timeout"
    except OSError:
        return None, "codex-exec-error"


def _run_claude(
    prompt: str,
    vault_root: Path,
    model: str = DEFAULT_MODEL,
    timeout: int | None = None,
) -> tuple[str | None, str | None]:
    # The codex branches resolve their own timeout: they are not a
    # BEYIN_MODEL_BACKEND target, so the local-inference bump must not reach them.
    if model == "codex":
        return _run_codex(prompt, vault_root, DEFAULT_CODEX_MODEL)
    if model.startswith("codex:") and model.removeprefix("codex:"):
        return _run_codex(prompt, vault_root, model.removeprefix("codex:"))
    if timeout is None:
        timeout, _warning = claude_runner.resolve_timeout("ingest")
    if model == DEFAULT_MODEL:
        # flush's runner is reused for the default model, but the bound that
        # applies here is the ingest one — and so is the accounting label.
        return flush._run_claude(prompt, vault_root, timeout, component="ingest")
    return _run_claude_with_model(prompt, vault_root, model, timeout)


def summarize_session(
    session: Session,
    vault_root: Path = VAULT_ROOT,
    state_dir: Path = STATE_DIR,
    model: str = DEFAULT_MODEL,
    min_turns: int = MIN_TURNS,
) -> SummaryResult:
    """Tek boğaz: sır bekçisi girişte ve çıkışta, şema doğrulaması arada."""
    if session.source == "gemini":
        # Adapter günleri 200k gövde karakterinde böler; flush'ın 15k/30-tur
        # penceresi burada yeniden uygulanırsa günün başı sessizce kaybolur.
        full_size = sum(len(text) + 32 for _role, text in session.turns) + 1
        max_chars = full_size
        # Claude, Antigravity and Codex keep the historical full-day payload.
        # Local endpoints share live flush's bounded default and override.
        if model != "codex" and not model.startswith("codex:"):
            backend, _backend_warning = claude_runner.resolve_backend()
            if backend in (
                claude_runner.BACKEND_OLLAMA,
                claude_runner.BACKEND_OPENAI_COMPAT,
            ):
                max_chars, chunk_warning = flush.resolve_flush_chunk_chars()
                if chunk_warning:
                    write_health(
                        state_dir,
                        chunk_warning,
                        warning=True,
                        component="ingest",
                        health_name=HEALTH_NAME,
                    )
        transcript, turn_count = flush.format_turns(
            session.turns,
            max_turns=max(1, len(session.turns)),
            max_chars=max_chars,
        )
    else:
        transcript, turn_count = flush.format_turns(session.turns)
    if turn_count < min_turns:
        return SummaryResult("", "bos", "below-minimum-turns")

    if flush.DIRECTIVE_SHAPED.search(transcript):
        write_health(
            state_dir,
            "warn:directive-shaped-transcript",
            warning=True,
            component="ingest",
            health_name=HEALTH_NAME,
        )

    # Sır bekçisi (giriş): kimlik bilgisi kalıpları özetçiye hiç gitmesin.
    transcript, input_hits = secret_guard.redact(transcript)
    if input_hits:
        write_health(
            state_dir,
            "warn:secret-redacted-input:" + ",".join(input_hits),
            warning=True,
            component="ingest",
            health_name=HEALTH_NAME,
        )

    timeout, timeout_warning = claude_runner.resolve_timeout("ingest")
    if timeout_warning:
        write_health(
            state_dir,
            timeout_warning,
            warning=True,
            component="ingest",
            health_name=HEALTH_NAME,
        )
    summary, error = _run_claude(
        flush.build_flush_prompt(transcript),
        vault_root,
        model,
        timeout,
    )
    for backend_warning in claude_runner.last_warnings():
        write_health(
            state_dir,
            backend_warning,
            warning=True,
            component="ingest",
            health_name=HEALTH_NAME,
        )
    if error is not None:
        return SummaryResult("", "fail", error)
    if not summary:
        return SummaryResult("", "fail", "summary-empty")
    if summary == "FLUSH_BOS":
        return SummaryResult("", "bos", "flush-bos")
    if not flush.validate_summary(summary):
        return SummaryResult("", "fail", "summary-schema-invalid")

    # Sır bekçisi (çıkış): özetçi girişte kaçanı aynen aktarmış olabilir.
    summary, output_hits = secret_guard.redact(summary)
    if output_hits:
        write_health(
            state_dir,
            "warn:secret-redacted-output:" + ",".join(output_hits),
            warning=True,
            component="ingest",
            health_name=HEALTH_NAME,
        )
    return SummaryResult(summary, "ok", "")


def daily_suffix(session: Session, summarizer: str = DEFAULT_MODEL) -> str:
    """``  — codex · gpt-5.6-sol · özet: haiku`` biçiminde başlık eki."""
    parts = [session.label or session.source]
    if session.model:
        parts.append(session.model)
    return " — " + " · ".join(parts) + f" · özet: {summarizer}"


def append_historical(
    vault_root: Path,
    summary: str,
    session: Session,
    summarizer: str = DEFAULT_MODEL,
) -> str:
    """Oturumu kendi tarihindeki günlüğe ekler; yeni dosyaya arşiv notu koyar."""
    daily_dir = vault_root / "daily"
    date_text = session.when.strftime("%Y-%m-%d")
    daily_path = daily_dir / f"{date_text}.md"
    if not daily_path.exists():
        daily_dir.mkdir(parents=True, exist_ok=True)
        daily_path.write_text(
            f"# Günlük Log: {date_text}\n\n{BACKFILL_NOTE}\n\n## Oturumlar\n",
            encoding="utf-8",
        )
    flush._append_daily(
        vault_root,
        summary,
        "ingest",
        session.when,
        suffix=daily_suffix(session, summarizer),
        anchor=session.anchor or None,
    )
    return daily_path.name


@contextlib.contextmanager
def exclusive_lock(state_dir: Path = STATE_DIR) -> Iterator[bool]:
    """Tek koşu güvencesi; kilit başkasındaysa ``False`` verir (sessiz çıkış)."""
    state_dir.mkdir(parents=True, exist_ok=True)
    lock_file = (state_dir / LOCK_NAME).open("a+", encoding="utf-8")
    try:
        try:
            _lock_exclusive(lock_file, blocking=False)
        except (BlockingIOError, OSError):
            yield False
            return
        yield True
    finally:
        try:
            lock_file.close()
        except OSError:
            pass


def transcript_size(turns: Sequence[tuple[str, str]]) -> tuple[int, int]:
    """Dry-run tablosu için (tur sayısı, karakter) — içerik yazdırılmaz."""
    rendered, count = flush.format_turns(list(turns))
    return count, len(rendered)
