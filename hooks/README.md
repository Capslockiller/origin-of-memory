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
| `memory-retrieve.ps1` | UserPromptSubmit | Retrieval: a BM25 query built from the prompt injects the 3 most relevant full notes (`retrieve.py`, per-session dedupe, measured p95 347 ms) |
| `flush-launch.ps1` | SessionEnd + PreCompact | Hands the transcript to a detached `flush.py` (daily summarisation) |
| `session-end.ps1` | SessionEnd | Raises the `needs_reflection` flag when a session closes without the companion memory being updated |

If registration has to change, merge into the user's existing settings rather
than replacing them. Sessions with the `BEYIN_INVOKED_BY` environment variable
set — the compiler's and the flush's own `claude -p` calls — exit on the first
line of every hook.

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
