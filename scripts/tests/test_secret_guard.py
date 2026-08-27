"""Synthetic coverage for every credential pattern family."""

# yazan: codex · model: gpt-5.6-sol

from __future__ import annotations

import unittest

import _helpers  # noqa: F401 — adds scripts to sys.path
import secret_guard


POSITIVE_CASES = (
    ("aws-anahtar", "AKIA" + "X" * 16),
    ("google-anahtar", "AIza" + "A" * 35),
    ("github-token", "ghp_" + "B" * 36),
    ("slack-token", "xoxb-" + "C" * 12),
    ("anthropic-anahtar", "sk-ant-" + "D" * 20),
    ("openai-anahtar", "sk-proj-" + "E" * 32),
    ("jwt", "eyJ" + "F" * 8 + "." + "G" * 8 + "." + "H" * 8),
    ("bearer", "Bearer " + "I" * 16),
    ("url-kimlik", "postgres://demo:" + "J" * 12 + "@localhost/db"),
    ("kimlik-atamasi", "password=" + "K" * 12),
    (
        "pem-anahtar",
        "-----BEGIN PRIVATE KEY-----\nSYNTHETIC\n-----END PRIVATE KEY-----",
    ),
)


NEGATIVE_CASES = (
    "AKIA" + "X" * 15,
    "AIza" + "A" * 34,
    "ghp_" + "B" * 35,
    "xoxb-short",
    "sk-ant-short",
    "sk-proj-short",
    "eyJshort.part.value",
    "Bearer short",
    "postgres://demo@localhost/db",
    "api_key=EXAMPLE",
    "-----BEGIN PUBLIC KEY-----\nSYNTHETIC\n-----END PUBLIC KEY-----",
)


class SecretGuardTests(unittest.TestCase):
    pass


def _positive_test(family: str, sample: str):
    def test(self: SecretGuardTests) -> None:
        self.assertIn(family, secret_guard.scan(sample))
        redacted, hits = secret_guard.redact(sample)
        self.assertIn(family, hits)
        self.assertIn(f"[SIR:{family}]", redacted)
        self.assertEqual(secret_guard.scan(redacted), [])

    return test


def _negative_test(sample: str):
    def test(self: SecretGuardTests) -> None:
        self.assertEqual(secret_guard.scan(sample), [])
        self.assertEqual(secret_guard.redact(sample), (sample, []))

    return test


for _index, (_family, _sample) in enumerate(POSITIVE_CASES):
    setattr(
        SecretGuardTests,
        f"test_positive_{_index:02d}_{_family.replace('-', '_')}",
        _positive_test(_family, _sample),
    )

for _index, _sample in enumerate(NEGATIVE_CASES):
    setattr(
        SecretGuardTests,
        f"test_negative_{_index:02d}",
        _negative_test(_sample),
    )


if __name__ == "__main__":
    unittest.main()
