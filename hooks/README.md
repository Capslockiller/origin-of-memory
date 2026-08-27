# Hafıza kancaları — kayıt düzeni

Bu klasördeki kancaların **tamamı kullanıcı seviyesinde** kayıtlıdır:
`<user>\.claude\settings.json`.
Beyin her projeden yazar VE her projeden okur; proje seviyesindeki
`.claude/settings.json` bilerek boştur (çifte ateşleme olmasın diye).

| Betik | Olay | İş |
|---|---|---|
| `session-start.ps1` | SessionStart | Hafıza enjeksiyonu (Kurallar, Last-Session, Threads, Journal, indeks payı; 16.000 krkt tavan) |
| `prompt-counter.ps1` | UserPromptSubmit | Prompt sayacı + 15'te bir hafıza hatırlatması |
| `memory-retrieve.ps1` | UserPromptSubmit | Faz 3 getirme: mesajdan BM25 sorgusu → ilgili 3 tam not enjeksiyonu (`retrieve.py`, oturum içi tekrar-önleme, p95 ~350 ms) |
| `flush-launch.ps1` | SessionEnd + PreCompact | Transkripti ayrık `flush.py`'ye devreder (daily özetleme) |
| `session-end.ps1` | SessionEnd | Hafıza güncellenmeden kapanan oturum için `needs_reflection` bayrağı |

Kayıt değişikliği gerekirse mevcut kullanıcı ayarlarını koruyarak birleştir.
`BEYIN_INVOKED_BY` ortam değişkeni set olan
oturumlar (derleyici/flush'ın kendi `claude -p` çağrıları) her kancadan ilk
satırda çıkar.
