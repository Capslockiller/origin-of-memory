# yazan: codex
# model: gpt-5.6-sol
"""PowerShell setup wizard plan, dry-run, and skill-selection contracts."""

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
class WizardDryRunTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.vault = self.root / "vault"
        self.vault.mkdir()
        self.appdata = self.root / "appdata"
        self.appdata.mkdir()
        self.localappdata = self.root / "localappdata"
        self.localappdata.mkdir()
        self.profile = self.root / "profile"
        self.profile.mkdir()

    def _plan(self, preset: str) -> dict:
        backend = {
            "cloud": "claude",
            "hybrid": "antigravity",
            "local": "ollama",
            "lite": "none",
        }[preset]
        backend_env = {"BEYIN_VAULT": str(self.vault)}
        if backend != "none":
            backend_env["BEYIN_MODEL_BACKEND"] = backend
        if backend == "ollama":
            backend_env["BEYIN_OLLAMA_MODEL_FAST"] = "example-model:8b"
        return {
            "preset": preset,
            "vault": str(self.vault),
            "backend": backend,
            "backend_env": backend_env,
            "mcp": preset in {"local", "lite"},
            "skills": ["beyin-doktor", "beyin-ice-aktar"],
            "force": False,
        }

    def _run_wizard(self, plan: dict) -> subprocess.CompletedProcess[str]:
        answers = self.root / f"{plan.get('preset', 'invalid')}.json"
        answers.write_text(
            json.dumps(plan, ensure_ascii=False),
            encoding="utf-8",
        )
        environment = os.environ.copy()
        environment["APPDATA"] = str(self.appdata)
        environment["LOCALAPPDATA"] = str(self.localappdata)
        environment["USERPROFILE"] = str(self.profile)
        return subprocess.run(
            [
                POWERSHELL,
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(REPO_ROOT / "kur.ps1"),
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

    def _tree(self) -> list[Path]:
        # PowerShell itself materialises AppData\Roaming under a redirected
        # USERPROFILE on startup, so those paths are not wizard writes.
        return sorted(
            path.relative_to(self.root)
            for path in self.root.rglob("*")
            if "AppData" not in path.relative_to(self.root).parts
        )

    def test_all_four_presets_are_deterministic_write_free_dry_runs(self) -> None:
        for preset in ("cloud", "hybrid", "local", "lite"):
            with self.subTest(preset=preset):
                before = self._tree()
                result = self._run_wizard(self._plan(preset))
                output = result.stdout + result.stderr
                self.assertEqual(result.returncode, 0, output)
                self.assertIn(f"[RUN] install.ps1 preset={preset}", output)
                self.assertIn("[DRYRUN][SETX] BEYIN_VAULT=", output)
                self.assertIn(f"[DONE] preset={preset} mode=dry-run", output)
                if preset == "lite":
                    self.assertIn("[SKIP] Hook registration disabled.", output)
                if preset in {"local", "lite"}:
                    self.assertIn("Claude Desktop config not found", output)
                    self.assertIn("[DRYRUN][MCP] Create origin-of-memory", output)
                after = self._tree()
                expected_new = Path(f"{preset}.json")
                self.assertEqual(after, sorted([*before, expected_new]))
                self.assertEqual(list(self.vault.iterdir()), [])

    def test_invalid_plan_names_the_field_and_exits_one(self) -> None:
        plan = self._plan("cloud")
        plan["preset"] = "unknown"
        result = self._run_wizard(plan)
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 1, output)
        self.assertIn("field 'preset'", output)

    def test_interactive_defaults_build_then_execute_one_cloud_plan(self) -> None:
        answers = ["", str(self.vault), *([""] * 9), "y"]
        environment = os.environ.copy()
        environment["APPDATA"] = str(self.appdata)
        environment["LOCALAPPDATA"] = str(self.localappdata)
        environment["USERPROFILE"] = str(self.profile)
        result = subprocess.run(
            [
                POWERSHELL,
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(REPO_ROOT / "kur.ps1"),
                "-DryRun",
            ],
            cwd=REPO_ROOT,
            env=environment,
            input="\n".join(answers) + "\n",
            text=True,
            encoding="utf-8",
            errors="replace",
            capture_output=True,
            check=False,
        )
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, output)
        self.assertIn("Plan JSON:", output)
        self.assertIn("[RUN] install.ps1 preset=cloud", output)
        self.assertIn("[DONE] preset=cloud mode=dry-run", output)
        self.assertEqual(list(self.vault.iterdir()), [])

    def test_install_skill_filter_copies_only_selected_skill_in_dry_run(self) -> None:
        result = subprocess.run(
            [
                POWERSHELL,
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(REPO_ROOT / "install.ps1"),
                "-VaultPath",
                str(self.vault),
                "-SkillFilter",
                "beyin-doktor",
                "-DryRun",
                "-SkipHookRegistration",
            ],
            cwd=REPO_ROOT,
            text=True,
            encoding="utf-8",
            errors="replace",
            capture_output=True,
            check=False,
        )
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, output)
        self.assertIn("skills\\beyin-doktor\\SKILL.md", output)
        self.assertNotIn("skills\\companion\\SKILL.md", output)
        self.assertEqual(list(self.vault.iterdir()), [])

    def test_mcp_dry_run_plans_merge_without_touching_existing_config(self) -> None:
        claude_dir = self.appdata / "Claude"
        claude_dir.mkdir()
        config = claude_dir / "claude_desktop_config.json"
        original = json.dumps(
            {
                "theme": "example",
                "mcpServers": {
                    "unrelated": {"command": "example", "args": []}
                },
            }
        )
        config.write_text(original, encoding="utf-8")
        result = self._run_wizard(self._plan("local"))
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, output)
        self.assertIn("[DRYRUN][BACKUP]", output)
        self.assertIn(
            "[DRYRUN][MCP] Merge origin-of-memory in",
            output,
        )
        self.assertEqual(config.read_text(encoding="utf-8"), original)

    def test_optional_ollama_plan_fields_round_trip_in_dry_run(self) -> None:
        plan = self._plan("local")
        plan["backend_env"]["BEYIN_OLLAMA_MODEL_FAST"] = "qwen3:8b"
        plan["install_runtime"] = True
        plan["pull_models"] = ["qwen3:8b", "gemma3:12b"]
        result = self._run_wizard(plan)
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, output)
        self.assertIn("Install Ollama True", output)
        self.assertIn("qwen3:8b, gemma3:12b", output)
        self.assertIn("[DRYRUN][SETX] BEYIN_OLLAMA_MODEL_SMART=gemma3:12b", output)
        self.assertIn(
            "[SKIP] Ollama guided setup disabled in dry-run; no probe, install, or pull.",
            output,
        )
        self.assertNotIn("[PULL]", output)

    def test_mcp_dry_run_detects_both_standard_and_msix_configs(self) -> None:
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
        standard.parent.mkdir(parents=True)
        virtual.parent.mkdir(parents=True)
        standard.write_text(json.dumps({"theme": "standard"}), encoding="utf-8")
        virtual.write_text(json.dumps({"locale": "virtual"}), encoding="utf-8")
        before_standard = standard.read_bytes()
        before_virtual = virtual.read_bytes()
        result = self._run_wizard(self._plan("local"))
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, output)
        self.assertIn(str(standard), output)
        self.assertIn(str(virtual), output)
        self.assertEqual(output.count("[DRYRUN][MCP] Merge"), 2)
        self.assertEqual(standard.read_bytes(), before_standard)
        self.assertEqual(virtual.read_bytes(), before_virtual)


if __name__ == "__main__":
    unittest.main()
