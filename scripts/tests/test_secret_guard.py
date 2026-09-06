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
    # Bağlam duyarlı kimlik bilgileri (Astra A10). Değerler sentetiktir.
    ("contextual-code", "kod: `SYNT-ABCD-12EF`"),
    ("contextual-code", "giriş kodu: 481920"),
    ("contextual-code", "erişim kodu = SYNT-9931"),
    ("contextual-code", "davet kodu → BETA-2026-XY"),
    ("contextual-code", "tek kullanımlık kod: 771903"),
    ("contextual-code", "kodunuz: zx9q4m2"),
    ("contextual-code", "access code is A1B2C3D4"),
    ("contextual-code", "invite code: SYNT-7788-QQ"),
    ("contextual-code", "OTP: 481920"),
    ("contextual-code", "PIN=8471-2290"),
    ("contextual-code", "passcode: hunter42x"),
    ("contextual-code", "Login code: 55-77-99-11"),
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
    # ``contextual-code`` yanlış pozitif olmamalı: Türkçe tamlamalar, commit
    # özetleri, sürümler, tarihler, çapa/wikilink söz dizimi, yer tutucular.
    "kod grafiği bu hafta çıkacak",
    "kod tabanı: python",
    "kod yazılım tarafında duruyor",
    "git 78b8d95",
    "kod: 78b8d95",
    "v0.5.0",
    "kod: v0.5.0",
    "2026-09-04",
    "kod: 2026-09-04",
    "kod: yok",
    "session:abc",
    "kaynak session:2026-09-04-benchmark",
    "[[kod defteri]]",
    "kod: ${TOKEN}",
    "the code is unclear",
)


class SecretGuardTests(unittest.TestCase):
    def test_contextual_code_keeps_the_label_and_redacts_only_the_value(
        self,
    ) -> None:
        cleaned, hits = secret_guard.redact("kullanıcı adı: pilot, kod: `SYNT-ABCD-12EF`")
        self.assertEqual(hits, ["contextual-code"])
        self.assertEqual(
            cleaned,
            "kullanıcı adı: pilot, kod: `[SIR:contextual-code]`",
        )

    def test_contextual_code_does_not_shadow_the_existing_assignment_rule(
        self,
    ) -> None:
        # şifre/parola/password/token bilerek contextual-code'a alınmadı.
        self.assertEqual(
            secret_guard.scan("parola: SyntheticValue9"), ["kimlik-atamasi"]
        )


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
