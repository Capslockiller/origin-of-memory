"""Tool-free compile: parser contract, hostile-output battery, staged writes."""

# yazan: odena · claude-opus-5

from __future__ import annotations

from pathlib import Path
import tempfile
import unittest

import _helpers  # noqa: F401  — scripts dizinini sys.path'e ekler

import compile as compile_module
import compile_text


ALLOW = compile_module._is_allowed_output_file


def _block(path: str, body: str = "content\n") -> str:
    return f"=== FILE: {path} ===\n{body}=== END FILE ===\n"


class ParserTests(unittest.TestCase):
    def _parse(self, answer: str) -> compile_text.Parsed:
        return compile_text.parse(answer, is_allowed=ALLOW)

    # -- happy path ------------------------------------------------------

    def test_reads_blocks_and_terminal_marker(self) -> None:
        answer = (
            "Şunları yazdım:\n\n"
            + _block("knowledge/concepts/isik.md", "# Işık\n\ngövde\n")
            + _block("knowledge/log.md", "## [ts] compile\n")
            + "=== DONE ===\n"
        )
        parsed = self._parse(answer)
        self.assertEqual(
            [block.path for block in parsed.blocks],
            ["knowledge/concepts/isik.md", "knowledge/log.md"],
        )
        self.assertEqual(parsed.marker, "DONE")
        self.assertFalse(parsed.wants_more)
        self.assertFalse(parsed.truncated)
        self.assertEqual(parsed.dropped, [])
        self.assertIn("# Işık", parsed.blocks[0].content)

    def test_more_marker_requests_continuation(self) -> None:
        parsed = self._parse(_block("knowledge/concepts/a.md") + "=== MORE ===\n")
        self.assertTrue(parsed.wants_more)
        self.assertFalse(parsed.truncated)

    def test_prose_outside_blocks_is_ignored(self) -> None:
        answer = (
            "Önce şunu düşündüm.\n=== FILE: nope ===\n"  # not a close, just prose
            + _block("knowledge/concepts/a.md")
            + "Bitti.\n=== DONE ===\n"
        )
        parsed = self._parse(answer)
        self.assertEqual([b.path for b in parsed.blocks], ["knowledge/concepts/a.md"])
        self.assertIn("unterminated:nope", parsed.dropped[0])

    # -- hostile battery (plan A4.4) -------------------------------------

    def test_path_outside_the_allowlist_is_dropped(self) -> None:
        answer = (
            _block("daily/2026-08-28.md")
            + _block("knowledge/concepts/ok.md")
            + "=== DONE ===\n"
        )
        parsed = self._parse(answer)
        self.assertEqual([b.path for b in parsed.blocks], ["knowledge/concepts/ok.md"])
        self.assertIn("forbidden-path:daily/2026-08-28.md", parsed.dropped)

    def test_traversal_absolute_and_home_paths_are_dropped(self) -> None:
        for hostile, reason in (
            ("../../.claude/settings.json", "path-traversal"),
            ("knowledge/concepts/../../daily/x.md", "path-traversal"),
            ("/etc/passwd", "absolute-path"),
            ("C:\\Windows\\System32\\x.md", "absolute-path"),
            ("~/.ssh/authorized_keys", "path-traversal"),
        ):
            with self.subTest(hostile):
                answer = _block(hostile) + _block("knowledge/concepts/ok.md") + "=== DONE ===\n"
                parsed = self._parse(answer)
                self.assertEqual(
                    [b.path for b in parsed.blocks], ["knowledge/concepts/ok.md"]
                )
                self.assertTrue(
                    any(item.startswith(reason) for item in parsed.dropped),
                    msg=f"{hostile} -> {parsed.dropped}",
                )

    def test_backslash_path_is_normalised_then_checked(self) -> None:
        parsed = self._parse(
            _block("knowledge\\concepts\\slug.md") + "=== DONE ===\n"
        )
        self.assertEqual(parsed.blocks[0].path, "knowledge/concepts/slug.md")

    def test_half_block_is_dropped_and_the_rest_survives(self) -> None:
        answer = (
            _block("knowledge/concepts/good.md")
            + "=== FILE: knowledge/concepts/cut.md ===\nyarım kald"
        )
        parsed = self._parse(answer)
        self.assertEqual([b.path for b in parsed.blocks], ["knowledge/concepts/good.md"])
        self.assertIn("unterminated:knowledge/concepts/cut.md", parsed.dropped)
        self.assertTrue(parsed.truncated)

    def test_duplicate_path_is_refused_rather_than_guessed(self) -> None:
        answer = (
            _block("knowledge/concepts/a.md", "ilk\n")
            + _block("knowledge/concepts/a.md", "ikinci\n")
            + "=== DONE ===\n"
        )
        parsed = self._parse(answer)
        self.assertEqual(len(parsed.blocks), 1)
        self.assertEqual(parsed.blocks[0].content, "ilk\n")
        self.assertIn("duplicate-path:knowledge/concepts/a.md", parsed.dropped)

    def test_oversized_and_empty_blocks_are_dropped(self) -> None:
        answer = (
            _block("knowledge/concepts/big.md", "x" * (compile_text.MAX_BLOCK_CHARS + 1) + "\n")
            + _block("knowledge/concepts/blank.md", "   \n\n")
            + _block("knowledge/concepts/ok.md")
            + "=== DONE ===\n"
        )
        parsed = self._parse(answer)
        self.assertEqual([b.path for b in parsed.blocks], ["knowledge/concepts/ok.md"])
        self.assertTrue(any(d.startswith("oversized:") for d in parsed.dropped))
        self.assertTrue(any(d.startswith("empty-content:") for d in parsed.dropped))

    def test_answers_with_nothing_usable_raise(self) -> None:
        for answer in ("", "   \n", "hiç blok yok", _block("daily/x.md") + "=== DONE ===\n"):
            with self.subTest(answer[:20]):
                with self.assertRaises(compile_text.ParseError):
                    self._parse(answer)

    def test_block_count_is_capped(self) -> None:
        answer = "".join(
            _block(f"knowledge/concepts/c{index}.md")
            for index in range(compile_text.MAX_BLOCKS + 5)
        ) + "=== DONE ===\n"
        parsed = self._parse(answer)
        self.assertEqual(len(parsed.blocks), compile_text.MAX_BLOCKS)
        self.assertTrue(any(d.startswith("block-limit:") for d in parsed.dropped))


class ApplyTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        # The stage is a *child* of the managed directory: the escape test aims
        # at stage.parent, and that target has to be inside something this test
        # cleans up, not the shared system temp root.
        self.root = Path(self._temporary.name).resolve()
        self.stage = self.root / "stage"
        self.stage.mkdir()

    def test_writes_create_directories_and_normalise_endings(self) -> None:
        written = compile_text.apply_blocks(
            self.stage,
            [compile_text.Block("knowledge/concepts/a.md", "# A\r\n\r\ngövde\r\n\n\n")],
        )
        self.assertEqual(written, ["knowledge/concepts/a.md"])
        target = self.stage / "knowledge" / "concepts" / "a.md"
        self.assertEqual(target.read_bytes(), "# A\n\ngövde\n".encode("utf-8"))

    def test_write_cannot_leave_the_stage(self) -> None:
        outside = self.stage.parent / "escaped.md"
        with self.assertRaises(compile_text.ParseError):
            compile_text.apply_blocks(
                self.stage, [compile_text.Block("../escaped.md", "x\n")]
            )
        self.assertFalse(outside.exists())

    def test_existing_file_is_replaced_whole(self) -> None:
        target = self.stage / "knowledge" / "concepts" / "a.md"
        target.parent.mkdir(parents=True)
        target.write_text("eski gövde\n", encoding="utf-8")
        compile_text.apply_blocks(
            self.stage, [compile_text.Block("knowledge/concepts/a.md", "yeni gövde\n")]
        )
        self.assertEqual(target.read_text(encoding="utf-8"), "yeni gövde\n")


if __name__ == "__main__":
    unittest.main()
