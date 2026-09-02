#!/usr/bin/env python3
"""pii_guard — Türkçe yapısal kişisel veri kapısı (stdlib, sağlama-hane doğrulamalı).

48. oturum (2026-09-02), zaafiyet #7: `secret_guard` kimlik-bilgisi (token/anahtar) filtresidir,
kişisel veri filtresi değildir. Bu modül yapısal Türkçe PII'yi yakalar ve sağlama hanesiyle
doğrular — regex tek başına değil (Presidio TCKN/VKN/plakayı %0 yakalıyor; bkz. Ham-Araştırma
2026-09-02-derin-07 §D1). Kapsam: TCKN (mod-10 çift hane), VKN (mod-10 ağırlıklı), IBAN-TR
(mod-97), banka/kredi kartı (Luhn), TR telefon, araç plakası.

Sözleşme (secret_guard ile aynı biçim): ``scan(text) -> list[Finding]``,
``redact(text) -> (text, [sinif...])``. İçerik hiçbir yere yazılmaz; yalnız sınıf adı döner.
Serbest-metin isim/adres KAPSAM DIŞI (yerel model görevi, ayrı karar).

Bağlandı: flush hattının giriş ve çıkışında yapısal PII redaksiyonu uygulanır.
Kendi kendini sınar:  python pii_guard.py --self-test
"""
from __future__ import annotations

import re
import sys
from dataclasses import dataclass

__all__ = ["Finding", "scan", "redact", "tckn_gecerli", "vkn_gecerli", "iban_tr_gecerli", "luhn_gecerli"]


@dataclass(frozen=True)
class Finding:
    sinif: str      # tckn | vkn | iban-tr | kart | telefon | plaka
    start: int
    end: int


# --- sağlama algoritmaları -------------------------------------------------------------

def tckn_gecerli(s: str) -> bool:
    """T.C. Kimlik No: 11 hane, ilk hane 0 değil; d10 = ((tek*7) - çift) mod 10; d11 = ilk10 mod 10."""
    if not (len(s) == 11 and s.isdigit() and s[0] != "0"):
        return False
    d = [int(c) for c in s]
    tek = d[0] + d[2] + d[4] + d[6] + d[8]
    cift = d[1] + d[3] + d[5] + d[7]
    if (tek * 7 - cift) % 10 != d[9]:
        return False
    return sum(d[:10]) % 10 == d[10]


def vkn_gecerli(s: str) -> bool:
    """Vergi Kimlik No: 10 hane; GİB sağlama algoritması."""
    if not (len(s) == 10 and s.isdigit()):
        return False
    d = [int(c) for c in s]
    toplam = 0
    for i in range(9):
        p = (d[i] + 9 - i) % 10
        q = (p * (2 ** (9 - i))) % 9
        if p != 0 and q == 0:
            q = 9
        toplam += q
    return (10 - (toplam % 10)) % 10 == d[9]


def iban_tr_gecerli(s: str) -> bool:
    """IBAN-TR: TR + 2 kontrol + 22 hane = 26; ISO 7064 mod-97."""
    s = s.replace(" ", "").upper()
    if not (len(s) == 26 and s.startswith("TR") and s[2:].isdigit()):
        return False
    yeniden = s[4:] + "2927" + s[2:4]  # T=29, R=27
    return int(yeniden) % 97 == 1


def luhn_gecerli(s: str) -> bool:
    s = re.sub(r"[ -]", "", s)
    if not (13 <= len(s) <= 19 and s.isdigit()):
        return False
    toplam, cift = 0, False
    for c in reversed(s):
        n = int(c)
        if cift:
            n *= 2
            if n > 9:
                n -= 9
        toplam += n
        cift = not cift
    return toplam % 10 == 0


# --- desenler ----------------------------------------------------------------------------
# Sınırlar: rakam dizisinin iki yanı rakam olmamalı (daha uzun sayıların içinden parça almasın).
_RX_11 = re.compile(r"(?<!\d)\d{11}(?!\d)")
_RX_10 = re.compile(r"(?<!\d)\d{10}(?!\d)")
_RX_IBAN = re.compile(r"\bTR\d{2}(?:[ ]?\d{4}){5}[ ]?\d{2}\b", re.IGNORECASE)
_RX_KART = re.compile(r"(?<!\d)(?:\d[ -]?){13,19}(?!\d)")
# TR telefon: +90/0090/0 + (5xx mobil | 2xx/3xx/4xx sabit) + 7 hane; ayraçlar boşluk/-/()
_RX_TEL = re.compile(
    r"(?<![\d\w])(?:\+90|0090|0)\s?\(?(?:5\d{2}|[234]\d{2})\)?[ -]?\d{3}[ -]?\d{2}[ -]?\d{2}(?!\d)"
)
# Plaka: il kodu 01-81 + boşluk? + 1-3 büyük harf + boşluk? + 2-5 rakam. Türkçe büyük harf yok.
_RX_PLAKA = re.compile(r"\b(?:0[1-9]|[1-7]\d|8[01]) ?[A-Z]{1,3} ?\d{2,5}\b")


def _ekle(bul: list[Finding], sinif: str, m: re.Match, alinan: list[tuple[int, int]]) -> None:
    for a, b in alinan:
        if m.start() < b and m.end() > a:
            return
    bul.append(Finding(sinif, m.start(), m.end()))
    alinan.append((m.start(), m.end()))


def scan(text: str) -> list[Finding]:
    """Metindeki yapısal Türkçe PII'yi bulur; sağlama hanesi tutmayanları ELEMEZ değil, ATLAR."""
    bul: list[Finding] = []
    alinan: list[tuple[int, int]] = []
    for m in _RX_IBAN.finditer(text):
        if iban_tr_gecerli(m.group(0)):
            _ekle(bul, "iban-tr", m, alinan)
    for m in _RX_11.finditer(text):
        if tckn_gecerli(m.group(0)):
            _ekle(bul, "tckn", m, alinan)
    for m in _RX_10.finditer(text):
        if vkn_gecerli(m.group(0)):
            _ekle(bul, "vkn", m, alinan)
    for m in _RX_KART.finditer(text):
        ham = re.sub(r"[ -]", "", m.group(0))
        if 13 <= len(ham) <= 19 and luhn_gecerli(ham):
            _ekle(bul, "kart", m, alinan)
    for m in _RX_TEL.finditer(text):
        _ekle(bul, "telefon", m, alinan)
    for m in _RX_PLAKA.finditer(text):
        _ekle(bul, "plaka", m, alinan)
    bul.sort(key=lambda f: f.start)
    return bul


def redact(text: str) -> tuple[str, list[str]]:
    """Bulguları ``[PII:<sinif>]`` ile değiştirir; (yeni_metin, sınıf_listesi) döner. İçerik dönmez."""
    bul = scan(text)
    if not bul:
        return text, []
    parcalar: list[str] = []
    son = 0
    for f in bul:
        parcalar.append(text[son:f.start])
        parcalar.append(f"[PII:{f.sinif}]")
        son = f.end
    parcalar.append(text[son:])
    return "".join(parcalar), sorted({f.sinif for f in bul})


# --- kendi kendini sınama ------------------------------------------------------------------

def _uret_tckn(ilk9: str) -> str:
    d = [int(c) for c in ilk9]
    d10 = (7 * (d[0] + d[2] + d[4] + d[6] + d[8]) - (d[1] + d[3] + d[5] + d[7])) % 10
    d11 = (sum(d) + d10) % 10
    return ilk9 + str(d10) + str(d11)


def _uret_vkn(ilk9: str) -> str:
    d = [int(c) for c in ilk9]
    toplam = 0
    for i in range(9):
        p = (d[i] + 9 - i) % 10
        q = (p * (2 ** (9 - i))) % 9
        if p != 0 and q == 0:
            q = 9
        toplam += q
    return ilk9 + str((10 - (toplam % 10)) % 10)


def _uret_iban_tr(bban22: str) -> str:
    kontrol = 98 - (int(bban22 + "2927" + "00") % 97)
    return "TR" + f"{kontrol:02d}" + bban22


def _self_test() -> int:
    hata = 0

    def ok(kosul: bool, ad: str) -> None:
        nonlocal hata
        print(("  ok  " if kosul else "  FAIL") + " " + ad)
        if not kosul:
            hata += 1

    t = _uret_tckn("123456789")
    ok(tckn_gecerli(t), f"tckn uretilen gecerli ({t[:3]}...)")
    ok(not tckn_gecerli("12345678901"), "tckn rastgele gecersiz")
    ok(not tckn_gecerli("0" + t[1:]), "tckn 0 ile baslayan gecersiz")
    v = _uret_vkn("123456789")
    ok(vkn_gecerli(v), "vkn uretilen gecerli")
    bozuk_v = v[:-1] + str((int(v[-1]) + 1) % 10)
    ok(not vkn_gecerli(bozuk_v), "vkn bozuk hane gecersiz")
    ib = _uret_iban_tr("0006100519786457841326")
    ok(iban_tr_gecerli(ib), "iban-tr uretilen gecerli")
    ok(not iban_tr_gecerli("TR00" + "0006100519786457841326"), "iban-tr yanlis kontrol gecersiz")
    ok(luhn_gecerli("4539 1488 0343 6467"), "luhn test karti gecerli")
    ok(not luhn_gecerli("4539 1488 0343 6468"), "luhn bozuk gecersiz")

    metin = (
        f"Musteri TCKN {t}, VKN {v}, IBAN {ib}, kart 4539 1488 0343 6467, "
        "tel 0532 123 45 67 ve +90 (212) 223 01 55, plaka 34 ABC 123. "
        "Siparis no 20260902001 ve commit 8b1726ef degismemeli; 1.36 GB, 2026-09-02 de."
    )
    yeni, siniflar = redact(metin)
    ok(siniflar == ["iban-tr", "kart", "plaka", "tckn", "telefon", "vkn"], f"redact siniflar {siniflar}")
    ok(t not in yeni and v not in yeni and ib not in yeni, "redact icerigi sildi")
    ok("20260902001" in yeni and "8b1726ef" in yeni and "2026-09-02" in yeni, "yanlis pozitif yok (siparis/commit/tarih)")
    ok(yeni.count("[PII:telefon]") == 2, "iki telefon")
    # Sağlama tutmayan 11 haneli sayı TCKN sayılmaz
    bozuk_t = t[:-1] + str((int(t[-1]) + 1) % 10)
    ok(not any(f.sinif == "tckn" for f in scan(f"kayit {bozuk_t} sayisi")), "saglamasiz 11 hane atlanir")
    print(f"pii_guard self-test: {'OK' if hata == 0 else str(hata) + ' HATA'}")
    return 1 if hata else 0


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        sys.exit(_self_test())
    veri = sys.stdin.read()
    yeni, siniflar = redact(veri)
    sys.stdout.write(yeni)
    if siniflar:
        sys.stderr.write("pii: " + ",".join(siniflar) + "\n")
