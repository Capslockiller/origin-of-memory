"""The shared entry gate has one fixed guard order and telemetry contract.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

from pathlib import Path
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import giris_kapisi


class GirisKapisiTests(unittest.TestCase):
    def test_guard_order_is_unicode_then_secret_then_pii(self) -> None:
        calls: list[str] = []

        def step(name: str):
            def apply(text: str) -> tuple[str, list[str]]:
                calls.append(name)
                return text + name, []

            return apply

        with tempfile.TemporaryDirectory() as temporary, mock.patch.object(
            giris_kapisi.unicode_guard, "clean", side_effect=step("unicode")
        ), mock.patch.object(
            giris_kapisi.secret_guard, "redact", side_effect=step("secret")
        ), mock.patch.object(
            giris_kapisi.pii_guard, "redact", side_effect=step("pii")
        ):
            cleaned, warnings = giris_kapisi.temizle(
                "x", component="ingest-input", state_dir=Path(temporary)
            )

        self.assertEqual(calls, ["unicode", "secret", "pii"])
        self.assertEqual(cleaned, "xunicodesecretpii")
        self.assertEqual(warnings, [])

    def test_warning_slugs_match_flush_and_use_component_health_file(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            state_dir = Path(temporary)
            cleaned, warnings = giris_kapisi.temizle(
                "gizli\u200b api_key=FakeGateKey123456 TCKN 10000000146",
                component="ingest-input",
                state_dir=state_dir,
            )
            health = (state_dir / "ingest-health.json").read_text(encoding="utf-8")

        self.assertNotIn("FakeGateKey123456", cleaned)
        self.assertNotIn("10000000146", cleaned)
        self.assertEqual(
            [warning.split(":", 2)[0] for warning in warnings],
            [
                "warn",
                "warn",
                "warn",
            ],
        )
        self.assertIn("warn:unicode-cleaned-input:zero-width", health)
        self.assertIn("warn:secret-redacted-input:kimlik-atamasi", health)
        self.assertIn("warn:pii-redacted-input:tckn", health)


if __name__ == "__main__":
    unittest.main()
