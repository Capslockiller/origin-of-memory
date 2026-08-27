"""Sır bekçisi — makine katmanının çıktısına sızan kimlik bilgisi kalıplarını yakalar.

İki kullanım:
- redact(text): eşleşen sırları ``[SIR:<kalıp>]`` ile değiştirir (flush → daily).
- scan(text): yalnız kalıp adlarını döndürür, metne dokunmaz (compile → terfi kapısı).

Kalıplar bilinçli olarak dar tutuldu: hedef, terminale dökülen gerçek kimlik
bilgileri (anahtar, token, bağlantı dizesi, şifre ataması). Serbest metni
bozacak genişlikte kalıp eklenmez.
"""
from __future__ import annotations

import re

# (kalıp adı, düzenli ifade, karartılacak grup numarası — 0: tüm eşleşme)
_RULES: list[tuple[str, re.Pattern[str], int]] = [
    (
        "pem-anahtar",
        re.compile(
            r"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?"
            r"(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|$)",
            re.DOTALL,
        ),
        0,
    ),
    ("aws-anahtar", re.compile(r"\bAKIA[0-9A-Z]{16}\b"), 0),
    ("google-anahtar", re.compile(r"\bAIza[0-9A-Za-z_-]{35}\b"), 0),
    ("github-token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{36,255}\b"), 0),
    ("slack-token", re.compile(r"\bxox[abprs]-[A-Za-z0-9-]{10,}\b"), 0),
    ("anthropic-anahtar", re.compile(r"\bsk-ant-[A-Za-z0-9_-]{20,}\b"), 0),
    ("openai-anahtar", re.compile(r"\bsk-(?:proj-)?[A-Za-z0-9]{32,}\b"), 0),
    (
        "jwt",
        re.compile(
            r"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b"
        ),
        0,
    ),
    # postgres://kullanici:PAROLA@konak — yalnız parola bölümü karartılır.
    (
        "url-kimlik",
        re.compile(r"\b[a-z][a-z0-9+.-]{1,20}://[^/\s:@]{1,64}:([^@\s/]{1,128})@"),
        1,
    ),
    ("bearer", re.compile(r"(?i)\bbearer\s+([A-Za-z0-9._~+/=-]{16,})"), 1),
    # password: hunter2 / api_key=abc123 / "yönetici parolası: x" —
    # yalnız değer karartılır; [^\W\d_]{0,4} Türkçe iyelik eklerini tolere eder.
    (
        "kimlik-atamasi",
        re.compile(
            r"(?i)\b(?:password|passwd|parola|s[ıi]fre|secret|token|"
            r"api[_-]?key|access[_-]?key|private[_-]?key|client[_-]?secret|"
            r"auth[_-]?token)[^\W\d_]{0,4}\s*[:=]\s*[\"']?([^\s\"',;]{6,128})"
        ),
        1,
    ),
]

# Değer gibi görünmeyen atamaları (yer tutucu, süslü parantez, yıldız) esgeç.
_HARMLESS_VALUE = re.compile(
    r"(?i)^(?:\$\{?[a-z_][a-z0-9_]*\}?|<[^>]+>|\*{3,}|x{4,}|\[[^\]]+\]|"
    r"REDACTED|KARARTILDI|NONE|NULL|TRUE|FALSE|CHANGEME|"
    r"PLACEHOLDER|EXAMPLE|ORNEK|ÖRNEK)$"
)


def _finding_spans(text: str) -> list[tuple[int, int, str]]:
    spans: list[tuple[int, int, str]] = []
    for name, pattern, group in _RULES:
        for match in pattern.finditer(text):
            value = match.group(group)
            if value is None:
                continue
            if group != 0 and _HARMLESS_VALUE.match(value):
                continue
            spans.append((match.start(group), match.end(group), name))
    return spans


def scan(text: str) -> list[str]:
    """Metindeki sır kalıplarının adlarını (tekrarsız, sıralı) döndürür."""
    seen: list[str] = []
    for _, _, name in _finding_spans(text):
        if name not in seen:
            seen.append(name)
    return seen


def redact(text: str) -> tuple[str, list[str]]:
    """Sırları ``[SIR:<kalıp>]`` ile değiştirir; (temiz metin, kalıp adları) döner."""
    spans = _finding_spans(text)
    if not spans:
        return text, []
    # Çakışan aralıkları birleştirerek sondan başa değiştir (indeks kaymasın).
    spans.sort(key=lambda span: (span[0], -span[1]))
    merged: list[tuple[int, int, str]] = []
    for start, end, name in spans:
        if merged and start < merged[-1][1]:
            previous_start, previous_end, previous_name = merged[-1]
            merged[-1] = (previous_start, max(previous_end, end), previous_name)
            continue
        merged.append((start, end, name))
    result = text
    for start, end, name in reversed(merged):
        result = f"{result[:start]}[SIR:{name}]{result[end:]}"
    names: list[str] = []
    for _, _, name in merged:
        if name not in names:
            names.append(name)
    return result, names
