# yazan: codex
# model: gpt-5.6-sol
"""Antigravity (agy) arka ucunun seçim, model eşlemesi ve hata sözleşmesi.

Hiçbir test gerçek bir ikili çalıştırmaz: ``shutil.which`` ve
``subprocess.run`` her koşuda vekille değiştirilir.
"""

from __future__ import annotations

from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

import _helpers  # noqa: F401  (sys.path köprüsü)

import agy_runner
import claude_runner


class _Recorder:
    """subprocess.run vekili: argv ve kwargs'ı yakalar, sabit çıktı döndürür."""

    def __init__(self, stdout: str = "OZET", returncode: int = 0, stderr: str = ""):
        self.stdout = stdout
        self.returncode = returncode
        self.stderr = stderr
        self.args: list[str] = []
        self.kwargs: dict = {}

    def __call__(self, args: list[str], **kwargs):
        self.args = args
        self.kwargs = kwargs
        return subprocess.CompletedProcess(
            args,
            self.returncode,
            self.stdout,
            self.stderr,
        )


class BackendResolutionTests(unittest.TestCase):
    def test_unset_and_explicit_claude(self) -> None:
        self.assertEqual(claude_runner.resolve_backend({}), ("claude", None))
        self.assertEqual(
            claude_runner.resolve_backend({"BEYIN_MODEL_BACKEND": "claude"}),
            ("claude", None),
        )

    def test_antigravity_and_deprecated_gemini_alias(self) -> None:
        self.assertEqual(
            claude_runner.resolve_backend({"BEYIN_MODEL_BACKEND": "antigravity"}),
            ("antigravity", None),
        )
        backend, warning = claude_runner.resolve_backend(
            {"BEYIN_MODEL_BACKEND": "Gemini"}
        )
        self.assertEqual(backend, "antigravity")
        self.assertEqual(warning, "warn:backend-alias-deprecated:gemini")

    def test_unknown_backend_falls_back_to_claude_with_warning(self) -> None:
        backend, warning = claude_runner.resolve_backend(
            {"BEYIN_MODEL_BACKEND": "unknown-backend"}
        )
        self.assertEqual(backend, "claude")
        self.assertEqual(warning, "warn:backend-unknown:unknown-backend")


class DispatchTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.stage = self.root / "stage"
        self.stage.mkdir()
        self.addCleanup(self._temporary.cleanup)
        claude_runner.last_warnings()

    def _run(self, environment: dict[str, str], recorder: _Recorder, **kwargs):
        with mock.patch.dict(
            claude_runner.os.environ,
            environment,
            clear=True,
        ), mock.patch.object(
            claude_runner.shutil,
            "which",
            side_effect=lambda name: f"/bin/{name}",
        ), mock.patch.object(
            claude_runner.subprocess,
            "run",
            side_effect=recorder,
        ), mock.patch.object(
            agy_runner.subprocess,
            "run",
            side_effect=recorder,
        ):
            return claude_runner.run_claude(
                "PROMPT",
                model=kwargs.pop("model", "haiku"),
                tools=kwargs.pop("tools", ""),
                timeout=240,
                cwd=self.stage,
                **kwargs,
            )

    def test_unset_backend_keeps_claude_argv_unchanged(self) -> None:
        recorder = _Recorder()
        output, error = self._run({}, recorder)
        self.assertEqual((output, error), ("OZET", None))
        self.assertEqual(
            recorder.args,
            [
                "/bin/claude",
                "-p",
                "--model",
                "haiku",
                "--output-format",
                "text",
                "--safe-mode",
                "--tools",
                "",
            ],
        )
        self.assertEqual(recorder.kwargs["input"], "PROMPT")

    def test_antigravity_backend_argv(self) -> None:
        recorder = _Recorder()
        output, error = self._run(
            {"BEYIN_MODEL_BACKEND": "antigravity"},
            recorder,
        )
        self.assertEqual((output, error), ("OZET", None))
        self.assertEqual(
            recorder.args,
            [
                "/bin/agy",
                "-p",
                "PROMPT",
                "--model",
                "gemini-3.5-flash-medium",
                "--output-format",
                "text",
            ],
        )
        # Prompt argv'de; stdin kapalı ve BEYIN_INVOKED_BY hâlâ set.
        self.assertNotIn("input", recorder.kwargs)
        self.assertEqual(recorder.kwargs["stdin"], subprocess.DEVNULL)
        self.assertEqual(recorder.kwargs["timeout"], 240)
        self.assertEqual(
            recorder.kwargs["env"]["BEYIN_INVOKED_BY"],
            "beyin-scripts",
        )
        self.assertEqual(claude_runner.last_warnings(), [])

    def test_gemini_alias_behaves_identically_plus_warning(self) -> None:
        alias = _Recorder()
        canonical = _Recorder()
        self._run({"BEYIN_MODEL_BACKEND": "gemini"}, alias)
        alias_warnings = claude_runner.last_warnings()
        self._run({"BEYIN_MODEL_BACKEND": "antigravity"}, canonical)
        self.assertEqual(alias.args, canonical.args)
        self.assertEqual(alias_warnings, ["warn:backend-alias-deprecated:gemini"])

    def test_compile_tool_mode_is_refused_in_antigravity_mode(self) -> None:
        recorder = _Recorder()
        output, error = self._run(
            {"BEYIN_MODEL_BACKEND": "antigravity"},
            recorder,
            model="sonnet",
            tools="Read,Write,Edit,Glob,Grep",
        )
        self.assertEqual(output, None)
        self.assertEqual(error, "antigravity-backend-unsupported:compile")
        self.assertEqual(recorder.args, [])


class CompileBackendTests(unittest.TestCase):
    def test_claude_present_keeps_compile_on_claude(self) -> None:
        with mock.patch.object(
            claude_runner.shutil,
            "which",
            return_value="/bin/claude",
        ):
            backend, warning = claude_runner.compile_backend(
                {"BEYIN_MODEL_BACKEND": "antigravity"}
            )
        self.assertEqual(backend, "claude")
        self.assertEqual(warning, "warn:antigravity-compile-fallback-claude")

    def test_claude_absent_leaves_compile_on_refusing_backend(self) -> None:
        with mock.patch.object(claude_runner.shutil, "which", return_value=None):
            backend, warning = claude_runner.compile_backend(
                {"BEYIN_MODEL_BACKEND": "antigravity"}
            )
        self.assertEqual((backend, warning), ("antigravity", None))

    def test_default_backend_untouched(self) -> None:
        self.assertEqual(claude_runner.compile_backend({}), ("claude", None))


class ModelMappingTests(unittest.TestCase):
    def test_fast_default_and_override(self) -> None:
        self.assertEqual(
            agy_runner.resolve_model("haiku", {}),
            ("gemini-3.5-flash-medium", None),
        )
        self.assertEqual(
            agy_runner.resolve_model(
                "haiku",
                {"BEYIN_AGY_MODEL_FAST": "gemini-x-fast"},
            ),
            ("gemini-x-fast", None),
        )

    def test_smart_unset_degrades_to_fast_with_warning(self) -> None:
        slug, warning = agy_runner.resolve_model("sonnet", {})
        self.assertEqual(slug, "gemini-3.5-flash-medium")
        self.assertEqual(warning, "warn:agy-smart-model-unset:BEYIN_AGY_MODEL_SMART")

    def test_smart_override(self) -> None:
        self.assertEqual(
            agy_runner.resolve_model(
                "sonnet",
                {"BEYIN_AGY_MODEL_SMART": "gemini-x-pro"},
            ),
            ("gemini-x-pro", None),
        )

    def test_unknown_tier_degrades_to_fast_with_warning(self) -> None:
        slug, warning = agy_runner.resolve_model("opus", {})
        self.assertEqual(slug, "gemini-3.5-flash-medium")
        self.assertEqual(warning, "warn:agy-model-unmapped:opus")


class BinaryResolutionTests(unittest.TestCase):
    def test_missing_binary(self) -> None:
        with mock.patch.object(agy_runner.shutil, "which", return_value=None):
            self.assertEqual(
                agy_runner.resolve_binary({}),
                (None, "agy-missing"),
            )

    def test_bin_override_is_honoured(self) -> None:
        seen: list[str] = []

        def which(name: str):
            seen.append(name)
            return "/opt/agy-nightly"

        with mock.patch.object(agy_runner.shutil, "which", side_effect=which):
            prefix, error = agy_runner.resolve_binary(
                {"BEYIN_AGY_BIN": "agy-nightly"}
            )
        self.assertEqual(seen, ["agy-nightly"])
        self.assertEqual((prefix, error), (["/opt/agy-nightly"], None))

    def test_windows_shim_uses_cmd_bridge(self) -> None:
        with mock.patch.object(agy_runner.os, "name", "nt"), mock.patch.object(
            agy_runner.shutil,
            "which",
            return_value=r"C:\bin\agy.CMD",
        ):
            prefix, error = agy_runner.resolve_binary(
                {"COMSPEC": r"C:\Windows\System32\cmd.exe"}
            )
        self.assertEqual(error, None)
        self.assertEqual(
            prefix,
            [r"C:\Windows\System32\cmd.exe", "/d", "/s", "/c", r"C:\bin\agy.CMD"],
        )

    def test_windows_shim_bridge_reaches_argv_without_shell(self) -> None:
        recorder = _Recorder()
        with mock.patch.dict(
            claude_runner.os.environ,
            {
                "BEYIN_MODEL_BACKEND": "antigravity",
                "COMSPEC": r"C:\Windows\System32\cmd.exe",
            },
            clear=True,
        ), mock.patch.object(
            agy_runner.os,
            "name",
            "nt",
        ), mock.patch.object(
            agy_runner.shutil,
            "which",
            return_value=r"C:\bin\agy.cmd",
        ), mock.patch.object(
            agy_runner.subprocess,
            "run",
            side_effect=recorder,
        ):
            with tempfile.TemporaryDirectory() as stage:
                output, error = claude_runner.run_claude(
                    "PROMPT",
                    model="haiku",
                    tools="",
                    timeout=240,
                    cwd=Path(stage),
                )
        self.assertEqual((output, error), ("OZET", None))
        self.assertEqual(
            recorder.args,
            [
                r"C:\Windows\System32\cmd.exe",
                "/d",
                "/s",
                "/c",
                r"C:\bin\agy.cmd",
                "-p",
                "PROMPT",
                "--model",
                "gemini-3.5-flash-medium",
                "--output-format",
                "text",
            ],
        )
        self.assertNotIn("shell", recorder.kwargs)


class ErrorMappingTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.stage = Path(self._temporary.name)
        self.addCleanup(self._temporary.cleanup)

    def _call(self, **patches):
        with mock.patch.dict(agy_runner.os.environ, {}, clear=True):
            with patches["which"], patches["run"]:
                return agy_runner.run_agy(
                    "PROMPT",
                    model="haiku",
                    timeout=240,
                    cwd=self.stage,
                )

    def test_missing_binary(self) -> None:
        result = self._call(
            which=mock.patch.object(agy_runner.shutil, "which", return_value=None),
            run=mock.patch.object(agy_runner.subprocess, "run"),
        )
        self.assertEqual(result, (None, "agy-missing"))

    def test_nonzero_exit_maps_to_exec_error(self) -> None:
        result = self._call(
            which=mock.patch.object(
                agy_runner.shutil, "which", return_value="/bin/agy"
            ),
            run=mock.patch.object(
                agy_runner.subprocess,
                "run",
                side_effect=_Recorder(stdout="", returncode=2, stderr="boom"),
            ),
        )
        self.assertEqual(result, (None, "agy-exec-error"))

    def test_auth_stderr_is_sniffed(self) -> None:
        result = self._call(
            which=mock.patch.object(
                agy_runner.shutil, "which", return_value="/bin/agy"
            ),
            run=mock.patch.object(
                agy_runner.subprocess,
                "run",
                side_effect=_Recorder(
                    stdout="",
                    returncode=1,
                    stderr="Error: not logged in. Please sign in with agy.",
                ),
            ),
        )
        self.assertEqual(result, (None, "agy-auth-missing"))

    def test_oserror_and_timeout(self) -> None:
        which = mock.patch.object(
            agy_runner.shutil, "which", return_value="/bin/agy"
        )
        self.assertEqual(
            self._call(
                which=which,
                run=mock.patch.object(
                    agy_runner.subprocess, "run", side_effect=OSError("nope")
                ),
            ),
            (None, "agy-exec-error"),
        )
        self.assertEqual(
            self._call(
                which=which,
                run=mock.patch.object(
                    agy_runner.subprocess,
                    "run",
                    side_effect=subprocess.TimeoutExpired(cmd="agy", timeout=240),
                ),
            ),
            (None, "agy-timeout"),
        )


class IsolationTests(unittest.TestCase):
    def test_temp_dir_outside_vault_is_used_and_cleaned(self) -> None:
        seen: list[Path] = []
        recorder = _Recorder()

        def run(args, **kwargs):
            seen.append(Path(kwargs["cwd"]))
            return recorder(args, **kwargs)

        with tempfile.TemporaryDirectory() as root:
            vault = Path(root) / "vault"
            vault.mkdir()
            with mock.patch.dict(
                agy_runner.os.environ, {}, clear=True
            ), mock.patch.object(
                agy_runner.shutil, "which", return_value="/bin/agy"
            ), mock.patch.object(
                agy_runner.subprocess, "run", side_effect=run
            ):
                output, error = agy_runner.run_agy(
                    "PROMPT",
                    model="haiku",
                    timeout=240,
                    vault_root=vault,
                )
        self.assertEqual((output, error), ("OZET", None))
        self.assertEqual(len(seen), 1)
        self.assertFalse(seen[0].exists())
        self.assertNotIn(str(vault.resolve()), str(seen[0]))


if __name__ == "__main__":
    unittest.main()
