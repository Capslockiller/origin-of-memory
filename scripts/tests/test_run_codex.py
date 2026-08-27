# yazan: codex
# model: gpt-5.6-sol
"""Codex CLI özetçi çağrısının sandbox, stdin ve hata sözleşmesi."""

from __future__ import annotations

from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

import _helpers

import ingest_common


class RunCodexTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.vault = self.root / "vault"
        self.scratch = self.root / "scratch"
        self.vault.mkdir()
        self.addCleanup(self._temporary.cleanup)

    def test_args_stdin_output_file_and_cleanup(self) -> None:
        captured: dict = {}

        def fake_run(args: list[str], **kwargs):
            captured["args"] = args
            captured["kwargs"] = kwargs
            output_path = Path(args[args.index("--output-last-message") + 1])
            output_path.write_text("MERHABA\n", encoding="utf-8")
            return subprocess.CompletedProcess(args, 0, "stdout-yedek", "")

        with mock.patch.object(
            ingest_common,
            "CODEX_TMP_ROOT",
            self.scratch,
        ), mock.patch.object(
            ingest_common.shutil,
            "which",
            return_value=r"C:\bin\codex.exe",
        ), mock.patch.object(
            ingest_common.subprocess,
            "run",
            side_effect=fake_run,
        ):
            text, error = ingest_common._run_codex(
                "Reply with exactly: MERHABA",
                self.vault,
                "gpt-test",
            )

        self.assertEqual((text, error), ("MERHABA", None))
        args = captured["args"]
        self.assertEqual(
            args[:11],
            [
                r"C:\bin\codex.exe",
                "exec",
                "--skip-git-repo-check",
                "--sandbox",
                "read-only",
                "-m",
                "gpt-test",
                "-c",
                'model_reasoning_effort="medium"',
                "--color",
                "never",
            ],
        )
        self.assertEqual(args[-1], "-")
        self.assertIn("--output-last-message", args)
        kwargs = captured["kwargs"]
        self.assertEqual(kwargs["input"], "Reply with exactly: MERHABA")
        self.assertEqual(kwargs["timeout"], 240)
        self.assertTrue(kwargs["capture_output"])
        self.assertEqual(kwargs["encoding"], "utf-8")
        self.assertFalse(Path(kwargs["cwd"]).exists())

    def test_timeout_has_stable_error_and_cleans_tempdir(self) -> None:
        working_dirs: list[Path] = []

        def timeout(_args: list[str], **kwargs):
            working_dirs.append(Path(kwargs["cwd"]))
            raise subprocess.TimeoutExpired(cmd="codex", timeout=240)

        with mock.patch.object(
            ingest_common,
            "CODEX_TMP_ROOT",
            self.scratch,
        ), mock.patch.object(
            ingest_common.shutil,
            "which",
            return_value="codex",
        ), mock.patch.object(
            ingest_common.subprocess,
            "run",
            side_effect=timeout,
        ):
            result = ingest_common._run_codex("kısa", self.vault)
        self.assertEqual(result, (None, "codex-timeout"))
        self.assertEqual(len(working_dirs), 1)
        self.assertFalse(working_dirs[0].exists())

    def test_windows_cmd_wrapper_does_not_use_shell_true(self) -> None:
        captured: dict = {}

        def fake_run(args: list[str], **kwargs):
            captured["args"] = args
            captured["kwargs"] = kwargs
            return subprocess.CompletedProcess(args, 0, "MERHABA", "")

        with mock.patch.object(
            ingest_common,
            "CODEX_TMP_ROOT",
            self.scratch,
        ), mock.patch.object(
            ingest_common.os,
            "name",
            "nt",
        ), mock.patch.dict(
            ingest_common.os.environ,
            {"COMSPEC": r"C:\Windows\System32\cmd.exe"},
        ), mock.patch.object(
            ingest_common.shutil,
            "which",
            return_value=r"C:\bin\codex.cmd",
        ), mock.patch.object(
            ingest_common.subprocess,
            "run",
            side_effect=fake_run,
        ):
            result = ingest_common._run_codex("x", self.vault)
        self.assertEqual(result, ("MERHABA", None))
        self.assertEqual(
            captured["args"][:5],
            [
                r"C:\Windows\System32\cmd.exe",
                "/d",
                "/s",
                "/c",
                r"C:\bin\codex.cmd",
            ],
        )
        self.assertNotIn("shell", captured["kwargs"])

    def test_missing_binary_and_model_routing(self) -> None:
        with mock.patch.object(ingest_common.shutil, "which", return_value=None):
            self.assertEqual(
                ingest_common._run_codex("x", self.vault),
                (None, "codex-cli-missing"),
            )

        calls: list[str] = []

        def stub(prompt: str, root: Path, model: str):
            calls.append(model)
            return "ok", None

        with mock.patch.object(ingest_common, "_run_codex", stub):
            ingest_common._run_claude("x", self.vault, "codex")
            ingest_common._run_claude("x", self.vault, "codex:gpt-explicit")
        self.assertEqual(calls, ["gpt-5.6-sol", "gpt-explicit"])


if __name__ == "__main__":
    unittest.main()
