"""SessionStart current-material protection and duplicate-start coverage.

yazan: codex
model: gpt-5.6-sol
"""

from __future__ import annotations

import datetime
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

import _helpers  # noqa: F401 - adds scripts/ to sys.path


REPO_ROOT = Path(__file__).resolve().parents[2]
HOOK = REPO_ROOT / "hooks" / "session-start.ps1"
POWERSHELL = shutil.which("powershell")


@unittest.skipUnless(POWERSHELL, "Windows PowerShell is required")
class SessionStartProtectionTests(unittest.TestCase):
    def _write_vault(self, root: Path) -> Path:
        hook = root / ".claude" / "hooks" / "session-start.ps1"
        hook.parent.mkdir(parents=True)
        shutil.copyfile(HOOK, hook)
        companion = root / "fixture-850-Companion"
        companion.mkdir()
        (companion / "Last-Session.md").write_text("## Session: fixture\nlast stays whole\n## Previous\nold\n", encoding="utf-8")
        (companion / "Threads.md").write_text("## Active\n### Thread: protected\n**Status:** stays whole\n## Closed\n", encoding="utf-8")
        (companion / "Kurallar.md").write_text("rule\n" * 20, encoding="utf-8")
        (companion / "Journal.md").write_text("## Journal\n" + ("journal-elastic " * 900) + "\n", encoding="utf-8")
        knowledge = root / "knowledge"
        knowledge.mkdir()
        (knowledge / "index.md").write_text("index-current " * 2000, encoding="utf-8")
        daily = root / "daily"
        daily.mkdir()
        (daily / f"{datetime.date.today():%Y-%m-%d}.md").write_text("daily-current " * 2000 + "FRESHEST_DAILY_MARKER\n", encoding="utf-8")
        return hook

    def _run(self, hook: Path) -> tuple[dict, dict]:
        payload = {"session_id": "lane-d-session", "cwd": "C:\\fixture\\project"}
        env = dict(os.environ)
        env["PATH"] = ""  # Optional quota helper fails fast in this isolated test.
        env["BEYIN_ACILIS_INDEKS_TABAN"] = "1000"
        env["BEYIN_ACILIS_DAILY_TABAN"] = "900"
        result = subprocess.run([POWERSHELL, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(hook)], input=json.dumps(payload), capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        output = json.loads(result.stdout)
        ledger = hook.parent / ".state" / "enjeksiyon.jsonl"
        record = json.loads(ledger.read_text(encoding="utf-8-sig").splitlines()[-1])
        return output, record

    def test_floors_order_ledger_and_same_second_dedup(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            hook = self._write_vault(Path(directory))
            first, record = self._run(hook)
            context = first["hookSpecificOutput"]["additionalContext"]
            self.assertLessEqual(len(context), 16000)
            self.assertGreaterEqual(record["indeks"], 1000)
            self.assertGreaterEqual(record["daily"], 900)
            self.assertIn("journal", record["kirpildi"])
            self.assertEqual(record["session_id"], "lane-d-session")
            self.assertFalse(record["cift"])
            self.assertGreater(context.rfind("[ZAMAN]"), context.rfind("[Bugunun Logu]"))
            self.assertIn("FRESHEST_DAILY_MARKER", context)

            # A duplicate is "same stamp (to the second), same cwd" in the last
            # 32 ledger lines.  Two PowerShell starts landing in one wall-clock
            # second is a race the CI runner loses (1216-test run, 2026-09-08),
            # so the ledger is seeded with a record for each of the next
            # seconds instead — deterministic, and the hook is not touched.
            ledger = hook.parent / ".state" / "enjeksiyon.jsonl"
            seed_from = datetime.datetime.now().astimezone()
            with ledger.open("a", encoding="utf-8") as handle:
                for offset in range(0, 25):
                    stamp = (seed_from + datetime.timedelta(seconds=offset)).isoformat(timespec="seconds")
                    handle.write(json.dumps({"ts": stamp, "cwd": "C:\\fixture\\project", "session_id": "seed", "cift": False}) + "\n")
            second, duplicate = self._run(hook)
            duplicate_context = second["hookSpecificOutput"]["additionalContext"]
            self.assertTrue(duplicate["cift"])
            self.assertEqual(duplicate["session_id"], "lane-d-session")
            self.assertIn("cift acilis", duplicate_context)
            self.assertNotIn("[Bilgi Tabani - Indeks]", duplicate_context)


if __name__ == "__main__":
    unittest.main()
