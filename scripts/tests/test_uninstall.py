# yazan: codex
# model: gpt-5.6-sol
"""Fabricated-install dry-run coverage for the safe uninstaller."""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO_ROOT = Path(__file__).resolve().parents[2]
POWERSHELL = shutil.which("powershell")


@unittest.skipUnless(POWERSHELL, "Windows PowerShell is required")
class UninstallDryRunTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.vault = self.root / "vault"
        self.profile = self.root / "profile"
        self.appdata = self.root / "appdata"
        self.localappdata = self.root / "localappdata"
        for directory in (
            self.vault,
            self.profile,
            self.appdata,
            self.localappdata,
        ):
            directory.mkdir()
        (self.vault / "daily").mkdir()
        (self.vault / "daily" / "keep.md").write_text("memory", encoding="utf-8")
        self._fabricate_install()

    def _fabricate_install(self) -> None:
        user_claude = self.profile / ".claude"
        user_claude.mkdir()
        hooks_root = self.vault / ".claude" / "hooks"
        scripts_root = self.vault / ".claude" / "scripts"
        hooks_root.mkdir(parents=True)
        scripts_root.mkdir()
        (hooks_root / "session-start.ps1").write_text("fixture", encoding="utf-8")
        (scripts_root / "donanim.py").write_text("fixture", encoding="utf-8")
        skill = user_claude / "skills" / "beyin-doktor"
        skill.mkdir(parents=True)
        (skill / "SKILL.md").write_text("fixture", encoding="utf-8")

        registrations = [
            ("SessionStart", "session-start.ps1", ""),
            ("UserPromptSubmit", "prompt-counter.ps1", ""),
            ("UserPromptSubmit", "memory-retrieve.ps1", ""),
            ("SessionEnd", "flush-launch.ps1", " -Reason sessionend"),
            ("SessionEnd", "session-end.ps1", ""),
            ("PreCompact", "flush-launch.ps1", " -Reason precompact"),
        ]
        hooks: dict[str, list[dict]] = {}
        for event, script, arguments in registrations:
            command = (
                'powershell -NoProfile -ExecutionPolicy Bypass -File "'
                + str(hooks_root / script)
                + '"'
                + arguments
            )
            hooks.setdefault(event, []).append(
                {"hooks": [{"type": "command", "command": command, "timeout": 5}]}
            )
        hooks["SessionStart"].append(
            {"hooks": [{"type": "command", "command": "unrelated", "timeout": 5}]}
        )
        (user_claude / "settings.json").write_text(
            json.dumps({"hooks": hooks}), encoding="utf-8"
        )

        standard = self.appdata / "Claude" / "claude_desktop_config.json"
        virtual = (
            self.localappdata
            / "Packages"
            / "Claude_pzs8sxrjxfjjc"
            / "LocalCache"
            / "Roaming"
            / "Claude"
            / "claude_desktop_config.json"
        )
        for path in (standard, virtual):
            path.parent.mkdir(parents=True)
            path.write_text(
                json.dumps(
                    {
                        "mcpServers": {
                            "origin-of-memory": {"command": "python"},
                            "unrelated": {"command": "example"},
                        }
                    }
                ),
                encoding="utf-8",
            )

    def _snapshot(self) -> dict[str, bytes]:
        return {
            str(path.relative_to(self.root)): path.read_bytes()
            for path in self.root.rglob("*")
            if path.is_file()
        }

    def test_fabricated_install_dry_run_is_complete_and_write_free(self) -> None:
        answers = self.root / "answers.json"
        answers.write_text(
            json.dumps(
                {
                    "vault": str(self.vault),
                    "remove_scripts": True,
                    "remove_hooks": True,
                    "remove_skills": True,
                }
            ),
            encoding="utf-8",
        )
        environment = os.environ.copy()
        environment["USERPROFILE"] = str(self.profile)
        environment["APPDATA"] = str(self.appdata)
        environment["LOCALAPPDATA"] = str(self.localappdata)
        before = self._snapshot()
        result = subprocess.run(
            [
                POWERSHELL,
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(REPO_ROOT / "uninstall.ps1"),
                "-Answers",
                str(answers),
                "-DryRun",
            ],
            cwd=REPO_ROOT,
            env=environment,
            text=True,
            encoding="utf-8",
            errors="replace",
            capture_output=True,
            check=False,
        )
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, output)
        self.assertEqual(output.count("[DRYRUN][UNREGISTER]"), 6)
        self.assertEqual(output.count("[DRYRUN][MCP-REMOVE]"), 2)
        self.assertIn("[DRYRUN][REMOVE]", output)
        self.assertIn("Vault memory content was not touched", output)
        self.assertIn("[DONE] mode=dry-run", output)
        self.assertEqual(self._snapshot(), before)
        self.assertEqual(
            (self.vault / "daily" / "keep.md").read_text(encoding="utf-8"),
            "memory",
        )


if __name__ == "__main__":
    unittest.main()
