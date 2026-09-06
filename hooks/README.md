# Memory hooks — registration layout

**All hooks in this directory are registered at user level**, in
`<user>\.claude\settings.json`. The brain therefore writes from every project
AND reads in every project. The project-level `.claude/settings.json` inside the
vault is deliberately left without hook entries, so nothing fires twice when a
session is opened in the vault itself.

| Script | Event | Job |
|---|---|---|
| `session-start.ps1` | SessionStart | Memory injection (Kurallar, Last-Session, Threads, Journal, the knowledge root map's share; 16,000 character cap) |
| `prompt-counter.ps1` | UserPromptSubmit | Prompt counter, plus a memory reminder every 15th prompt |
| `../scripts/retrieve.py hook` | UserPromptSubmit | Retrieval: a BM25 query built from the prompt injects the 3 most relevant full notes (per-session dedupe, relevance floor, measured p95 347 ms). **Registered as a direct `python -X utf8 retrieve.py hook` command — not through a `.ps1`.** |
| `memory-retrieve.ps1` | — (not registered) | Compatibility shim only: pipes stdin to `retrieve.py hook` and passes stdout/exit code back. It carries no skip logic and no relevance gate of its own, so retrieval keeps a single entry point (2026-09-06). |
| `flush-launch.ps1` | SessionEnd + PreCompact | Hands the transcript to a detached `flush.py` (daily summarisation) |
| `flush-launch.ps1 -Tara` | `OdenaOS-Flush` scheduled task, every 8 h | Timed sweep: runs `flush.py --tara` in the foreground with the same backend env. Enumerates every transcript, skips the ones whose `{mtime, size}` has not moved since the last sweep, and lets the turn cursor drop the rest — an idle machine makes no model call (Master, 2026-09-07) |
| `zamanli-flush-kur.ps1` | — (run once, by hand) | Idempotent registration of that task (`-Durum` prints it, `-Kaldir` removes it). Runs **only when the user is logged on** — the sweep needs the user's own Ollama service |
| `session-end.ps1` | SessionEnd | Raises the `needs_reflection` flag when a session closes without the companion memory being updated |

If registration has to change, merge into the user's existing settings rather
than replacing them. Sessions with the `BEYIN_INVOKED_BY` environment variable
set — the compiler's and the flush's own `claude -p` calls — exit on the first
line of every hook.

`flush-launch.ps1` appends one line to `.state/hook-girdi.jsonl`
(`{ts, hook, reason, pid}`) before it reads stdin, so "the hook never fired" can
be told apart from "the hook fired but Python never started". `session-start.ps1`
should get the same one-liner. The ledger is bounded at 512 KB (rotated to
`.1` by the hook itself and by `scripts/bakim.py`).

Full pipeline detail: [../docs/architecture.md](../docs/architecture.md).

---

## Türkçe özet

Bu klasördeki kancaların **tamamı kullanıcı seviyesinde** kayıtlıdır
(`<kullanıcı>\.claude\settings.json`). Beyin her projeden yazar VE her projeden
okur; vault içindeki proje seviyesi `.claude/settings.json` çifte ateşleme
olmasın diye bilerek boş bırakılmıştır. Kayıt değişikliği gerekirse mevcut
kullanıcı ayarlarını koruyarak birleştir. `BEYIN_INVOKED_BY` ortam değişkeni set
olan oturumlar (derleyicinin ve flush'ın kendi `claude -p` çağrıları) her
kancadan ilk satırda çıkar.

`SessionEnd` bir garanti değil, bir nezakettir: uygulama ya da makine
öldürüldüğünde kanca hiç teslim edilmez (54. oturum, 19 saat kayıp). Bu yüzden
`OdenaOS-Flush` zamanlanmış görevi 8 saatte bir `flush-launch.ps1 -Tara`
çalıştırır; süpürge her transkripti gezer, damgası oynamayanı hiç açmaz, açtığı
dosyada yeni tur yoksa modele gitmez. Kaydı `zamanli-flush-kur.ps1` yapar
(kaydı orkestratör atar; script yalnız komutu üretir ve yazdırır).

Getirmenin tek giriş noktası `scripts/retrieve.py hook`'tur ve canlı kayıt
doğrudan o python çağrısıdır; `memory-retrieve.ps1` yalnızca eski kayıtlar için
duran ince bir uyumluluk kabuğudur (kendi eleme mantığı yoktur).
`flush-launch.ps1` stdin'i okumadan önce `.state/hook-girdi.jsonl`'e bir satır
düşer — "kanca hiç ateşlemedi" ile "ateşledi ama Python başlamadı" ayrımı için.
