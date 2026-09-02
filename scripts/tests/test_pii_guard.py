"""Synthetic coverage for every structural Turkish PII pattern family."""

# yazan: codex · model: gpt-5.6-sol

from __future__ import annotations

import unittest

import _helpers  # noqa: F401 — adds scripts to sys.path
import pii_guard


def _valid_tckn(first_nine: str) -> str:
    digits = [int(char) for char in first_nine]
    tenth = (
        7 * sum(digits[index] for index in (0, 2, 4, 6, 8))
        - sum(digits[index] for index in (1, 3, 5, 7))
    ) % 10
    eleventh = (sum(digits) + tenth) % 10
    return first_nine + str(tenth) + str(eleventh)


def _valid_vkn(first_nine: str) -> str:
    total = 0
    for index, char in enumerate(first_nine):
        partial = (int(char) + 9 - index) % 10
        weighted = (partial * (2 ** (9 - index))) % 9
        if partial != 0 and weighted == 0:
            weighted = 9
        total += weighted
    return first_nine + str((10 - total % 10) % 10)


def _valid_iban_tr(bban: str) -> str:
    check_digits = 98 - (int(bban + "292700") % 97)
    return "TR" + f"{check_digits:02d}" + bban


VALID_TCKN = _valid_tckn("123456789")
VALID_VKN = _valid_vkn("123456789")
VALID_IBAN = _valid_iban_tr("0006100519786457841326")

VALID_CASES = (
    ("tckn", VALID_TCKN),
    ("vkn", VALID_VKN),
    ("iban-tr", VALID_IBAN),
    ("kart", "4539 1488 0343 6467"),
    ("telefon", "+90 (555) 000 00 00"),
    ("plaka", "34 TST 123"),
)

INVALID_CASES = (
    ("tckn", VALID_TCKN[:-1] + str((int(VALID_TCKN[-1]) + 1) % 10)),
    ("vkn", VALID_VKN[:-1] + str((int(VALID_VKN[-1]) + 1) % 10)),
    ("iban-tr", "TR00" + VALID_IBAN[4:]),
    ("kart", "4539 1488 0343 6468"),
    ("telefon", "0632 123 45 67"),
    ("plaka", "82 TST 123"),
)


class PiiGuardTests(unittest.TestCase):
    pass


def _positive_test(family: str, sample: str):
    def test(self: PiiGuardTests) -> None:
        findings = pii_guard.scan(sample)
        self.assertIn(family, [finding.sinif for finding in findings])
        redacted, hits = pii_guard.redact(sample)
        self.assertIn(family, hits)
        self.assertIn(f"[PII:{family}]", redacted)
        self.assertEqual(pii_guard.scan(redacted), [])

    return test


def _negative_test(family: str, sample: str):
    def test(self: PiiGuardTests) -> None:
        self.assertNotIn(family, [finding.sinif for finding in pii_guard.scan(sample)])
        self.assertEqual(pii_guard.redact(sample), (sample, []))

    return test


for _index, (_family, _sample) in enumerate(VALID_CASES):
    setattr(
        PiiGuardTests,
        f"test_valid_{_index:02d}_{_family.replace('-', '_')}",
        _positive_test(_family, _sample),
    )

for _index, (_family, _sample) in enumerate(INVALID_CASES):
    setattr(
        PiiGuardTests,
        f"test_invalid_{_index:02d}_{_family.replace('-', '_')}",
        _negative_test(_family, _sample),
    )


if __name__ == "__main__":
    unittest.main()
