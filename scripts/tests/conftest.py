"""Test-wide guards that must hold no matter which test file is running.

Accounting is on by default in ``claude_runner.run_claude`` — no caller can opt
out of being measured — which means a test that exercises a mocked call would
otherwise append to the developer's own ``scripts/.state/calls.jsonl``. The
fixture below redirects that write into a throwaway directory for every test,
so the suite cannot leave a ledger behind in the checkout.

yazan: claude
model: opus-5
"""

from __future__ import annotations

from pathlib import Path
import sys

import pytest


SCRIPTS_DIR = Path(__file__).resolve().parent.parent
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import claude_runner  # noqa: E402 — needs the sys.path bridge above


@pytest.fixture(autouse=True)
def _isolate_call_ledger(tmp_path, monkeypatch):
    """Point the runner's call ledger at this test's own temporary state dir."""
    monkeypatch.setattr(claude_runner, "STATE_DIR", tmp_path / ".state")
