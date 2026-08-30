"""Tool-free compile end to end: same gates, same promotions, different writer.

The parser is covered in test_compile_text.py. What matters here is that the
text path lands in the live vault through the *existing* audit chain -- manifest
diff, path allowlist, directive quarantine, secret guard, schema gate -- and
that a hostile answer is stopped by them without any new policy of its own.
"""

# yazan: odena · claude-opus-5

from __future__ import annotations

from pathlib import Path
import unittest
from unittest import mock

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import compile as compile_module
from test_compile_hygiene import CONCEPT_TEXT, CompileHarness


def _answer(*files: tuple[str, str], marker: str = "DONE") -> str:
    body = "".join(
        f"=== FILE: {path} ===\n{content}=== END FILE ===\n" for path, content in files
    )
    return f"Hazır.\n\n{body}=== {marker} ===\n"


CONCEPT = ("knowledge/concepts/deneme.md", CONCEPT_TEXT)


class TextModeHarness(CompileHarness):
    """CompileHarness with BEYIN_COMPILE_MODE=text bound for the whole test."""

    def setUp(self) -> None:
        super().setUp()
        patcher = mock.patch.dict(
            "os.environ", {"BEYIN_COMPILE_MODE": "text", "BEYIN_INVOKED_BY": ""}
        )
        patcher.start()
        self.addCleanup(patcher.stop)

    def _stub(self, *answers: str):
        """Return a _run_model_text stub that replays answers, then repeats the last."""
        calls: list[str] = []

        def stub(prompt: str) -> tuple[str | None, str | None]:
            index = min(len(calls), len(answers) - 1)
            calls.append(prompt)
            return answers[index], None

        stub.calls = calls  # type: ignore[attr-defined]
        return stub

    def _run(self, stub) -> int:
        with mock.patch.object(compile_module, "_run_model_text", stub):
            return compile_module.main([])

    def _concept(self, name: str = "deneme.md") -> Path:
        return self.root / "knowledge" / "concepts" / name


class TextModePromotionTests(TextModeHarness):
    def test_mode_flag_defaults_to_tools(self) -> None:
        with mock.patch.dict("os.environ", {}, clear=True):
            self.assertEqual(compile_module.compile_mode(), "tools")
        for value in ("text", "TEXT", " text "):
            with mock.patch.dict("os.environ", {"BEYIN_COMPILE_MODE": value}):
                self.assertEqual(compile_module.compile_mode(), "text")
        with mock.patch.dict("os.environ", {"BEYIN_COMPILE_MODE": "nonsense"}):
            self.assertEqual(compile_module.compile_mode(), "tools")

    def test_parsed_blocks_reach_the_live_vault(self) -> None:
        self._daily("2026-08-12.md")
        stub = self._stub(_answer(CONCEPT))

        self.assertEqual(self._run(stub), 0)

        self.assertTrue(self._concept().is_file())
        self.assertEqual(self._concept().read_text(encoding="utf-8"), CONCEPT_TEXT)
        self.assertEqual(self._state()["last_status"], "ok")

    def test_text_and_tool_modes_promote_identical_bytes(self) -> None:
        """The point of the whole phase: only the writer changes."""
        self._daily("2026-08-12.md")
        self._run(self._stub(_answer(CONCEPT)))
        from_text = self._concept().read_bytes()

        self.setUp()  # a second, pristine vault
        self._daily("2026-08-12.md")

        def tool_stub(_prompt: str, stage: Path) -> str | None:
            # newline="\n" so the two stubs deliver byte-identical model output;
            # without it Windows text mode alone would explain any difference
            # and the parity claim would prove nothing.
            (stage / "knowledge" / "concepts" / "deneme.md").write_text(
                CONCEPT_TEXT, encoding="utf-8", newline="\n"
            )
            return None

        with mock.patch.dict("os.environ", {"BEYIN_COMPILE_MODE": "tools"}):
            with mock.patch.object(compile_module, "_run_claude", tool_stub):
                self.assertEqual(compile_module.main([]), 0)

        self.assertEqual(self._concept().read_bytes(), from_text)

    def test_no_usable_block_fails_the_run_without_writing(self) -> None:
        self._daily("2026-08-12.md")

        self.assertEqual(self._run(self._stub("özür dilerim, yapamadım")), 0)

        self.assertFalse(self._concept().exists())
        self.assertTrue(self._state()["last_status"].startswith("fail:"))


class TextModeRefusalTests(TextModeHarness):
    def test_forbidden_path_never_reaches_the_vault(self) -> None:
        daily = self._daily("2026-08-12.md")
        before = daily.read_bytes()
        stub = self._stub(
            _answer(("daily/2026-08-12.md", "ELE GEÇİRİLDİ\n"), CONCEPT)
        )

        self.assertEqual(self._run(stub), 0)

        self.assertEqual(daily.read_bytes(), before)
        self.assertTrue(self._concept().is_file())
        warnings = self._health().get("warnings", [])
        self.assertTrue(
            any("text-dropped" in item for item in warnings), msg=warnings
        )

    def test_traversal_target_is_dropped_and_nothing_escapes(self) -> None:
        outside = self.root.parent / "kacti.md"
        stub = self._stub(_answer(("../kacti.md", "dışarı\n"), CONCEPT))
        self._daily("2026-08-12.md")

        self.assertEqual(self._run(stub), 0)

        self.assertFalse(outside.exists())
        self.assertTrue(self._concept().is_file())

    def test_secret_in_the_answer_stops_the_promotion(self) -> None:
        self._daily("2026-08-12.md")
        leaked = CONCEPT_TEXT + "\nghp_" + "a" * 36 + "\n"
        stub = self._stub(_answer(("knowledge/concepts/deneme.md", leaked)))

        self.assertEqual(self._run(stub), 0)

        self.assertFalse(self._concept().exists())
        self.assertEqual(self._state()["last_status"], "fail:policy")
        self.assertIn("secret-detected", self._health()["error"])


class TextModeContinuationTests(TextModeHarness):
    def test_more_marker_drives_a_second_call_and_names_what_was_written(self) -> None:
        self._daily("2026-08-12.md")
        second = ("knowledge/concepts/ikinci.md", CONCEPT_TEXT)
        stub = self._stub(
            _answer(CONCEPT, marker="MORE"), _answer(second, marker="DONE")
        )

        self.assertEqual(self._run(stub), 0)

        self.assertTrue(self._concept().is_file())
        self.assertTrue(self._concept("ikinci.md").is_file())
        self.assertEqual(len(stub.calls), 2)
        self.assertIn("DEVAM ÇAĞRISI", stub.calls[1])
        self.assertIn("knowledge/concepts/deneme.md", stub.calls[1].split("DEVAM ÇAĞRISI")[1])

    def test_turn_cap_is_flagged_loudly_and_still_promotes(self) -> None:
        self._daily("2026-08-12.md")
        stub = self._stub(_answer(CONCEPT, marker="MORE"))

        with mock.patch.dict("os.environ", {"BEYIN_COMPILE_MAX_TURNS": "2"}):
            self.assertEqual(self._run(stub), 0)

        self.assertEqual(len(stub.calls), 2)
        self.assertTrue(self._concept().is_file())
        self.assertIn("text-turn-cap:2", self._health().get("warnings", []))

    def test_a_repeated_file_across_turns_is_written_once(self) -> None:
        self._daily("2026-08-12.md")
        stub = self._stub(
            _answer(CONCEPT, marker="MORE"),
            _answer(("knowledge/concepts/deneme.md", "SONRAKI\n"), marker="DONE"),
        )

        self.assertEqual(self._run(stub), 0)

        # The first turn's content stands: a file already promoted is not
        # silently overwritten by a later turn that was told not to repeat it.
        self.assertEqual(self._concept().read_text(encoding="utf-8"), CONCEPT_TEXT)


class TextModePromptTests(TextModeHarness):
    def test_existing_article_bodies_are_offered_for_update(self) -> None:
        self._daily("2026-08-12.md")
        existing = self._concept("eski.md")
        existing.write_text(CONCEPT_TEXT, encoding="utf-8")
        full = self.root / "knowledge" / "index-full.md"
        full.write_text(
            full.read_text(encoding="utf-8") + "| [[eski]] | özet | x.md | 2026-08-26 |\n",
            encoding="utf-8",
        )
        stub = self._stub(_answer(CONCEPT))

        self.assertEqual(self._run(stub), 0)

        prompt = stub.calls[0]
        self.assertIn("EXISTING FILE: knowledge/concepts/eski.md", prompt)
        self.assertIn("UNTRUSTED EXISTING ARTICLE DATA", prompt)

    def test_the_output_contract_and_tool_override_are_present(self) -> None:
        self._daily("2026-08-12.md")
        stub = self._stub(_answer(CONCEPT))

        self._run(stub)

        prompt = stub.calls[0]
        self.assertIn("ARAÇSIZ MOD", prompt)
        self.assertIn("=== FILE:", prompt)
        self.assertIn("=== DONE ===", prompt)
        self.assertIn("TAM içeriğini döndür", prompt)

    def test_the_link_richness_instruction_points_at_the_provided_lists(self) -> None:
        """A4.4/§20: link-poverty in text mode is a prompt gap, not a model gap.

        The fix leans on material the prompt already carries (root map +
        registry), so the instruction must name both and ask for more than the
        schema's bare minimum of two links.
        """
        self._daily("2026-08-12.md")
        stub = self._stub(_answer(CONCEPT))

        self._run(stub)

        prompt = stub.calls[0]
        self.assertIn("BAĞLANTI ZENGİNLİĞİ", prompt)
        self.assertIn("EN AZ ÜÇ", prompt)
        self.assertIn("KÖK HARİTA", prompt)
        self.assertIn("YİNELEME-KONTROL KAYDI", prompt)
        # Text mode only: tool mode's prompt must stay untouched by this.
        tool_prompt = compile_module.build_compile_prompt(
            "kök harita", "kayıt", "2026-08-12.md", "gövde", "2026-08-12T00:00:00"
        )
        self.assertNotIn("BAĞLANTI ZENGİNLİĞİ", tool_prompt)


if __name__ == "__main__":
    unittest.main()
