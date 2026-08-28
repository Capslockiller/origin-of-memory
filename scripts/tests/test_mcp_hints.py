"""MCP tool behaviour hints, over the wire in both protocol eras.

The hints are advisory by spec, so the point of testing them is that a client
which trims or auto-approves on them sees the truth: this server only reads.
"""

# yazan: claude · opus-5

from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import _helpers  # noqa: F401 — scripts dizinini sys.path'e ekler

import mcp_server


SERVER = Path(mcp_server.__file__).resolve()
EXPECTED_HINTS = {
    "readOnlyHint": True,
    "destructiveHint": False,
    "idempotentHint": True,
    "openWorldHint": False,
}


class AnnotationShapeTests(unittest.TestCase):
    def test_every_tool_carries_the_read_only_hints(self) -> None:
        for tool in mcp_server.TOOLS:
            with self.subTest(tool=tool["name"]):
                self.assertEqual(tool["annotations"], EXPECTED_HINTS)

    def test_hints_sit_in_annotations_not_at_the_top_level(self) -> None:
        """Where the spec's ToolAnnotations object lives, not beside `name`."""
        for tool in mcp_server.TOOLS:
            with self.subTest(tool=tool["name"]):
                self.assertEqual(
                    set(tool),
                    {"name", "title", "description", "annotations", "inputSchema"},
                )
                for field in EXPECTED_HINTS:
                    self.assertNotIn(field, tool)

    def test_the_declared_hints_match_what_the_tools_actually_do(self) -> None:
        """No write surface exists, so readOnly/non-destructive are honest."""
        source = SERVER.read_text(encoding="utf-8")

        self.assertNotIn("write_text(", source)
        self.assertNotIn("subprocess", source)
        self.assertTrue(mcp_server.READ_ONLY_ANNOTATIONS["readOnlyHint"])


class AnnotationsOverTheWireTests(unittest.TestCase):
    """A shape assertion on the module is not proof the client receives it."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        (self.root / "knowledge").mkdir(parents=True)
        (self.root / "knowledge" / "index.md").write_text(
            "# Kök Harita\n", encoding="utf-8"
        )

    def _tools(self, lines: list[str]) -> list[dict]:
        process = subprocess.run(
            [sys.executable, str(SERVER), "--vault", str(self.root)],
            input="\n".join(lines) + "\n",
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=60,
        )
        self.assertEqual(process.returncode, 0, msg=process.stderr)
        responses = [
            json.loads(line)
            for line in process.stdout.splitlines()
            if line.strip()
        ]
        return responses[-1]["result"]["tools"]

    def test_modern_revision_delivers_the_hints(self) -> None:
        request = json.dumps(
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/list",
                "params": {
                    "_meta": {
                        mcp_server.META_PROTOCOL_VERSION: mcp_server.MODERN_VERSION,
                        mcp_server.META_CLIENT_CAPABILITIES: {},
                    }
                },
            }
        )

        tools = self._tools([request])

        self.assertEqual(len(tools), 2)
        for tool in tools:
            with self.subTest(tool=tool["name"]):
                self.assertEqual(tool["annotations"], EXPECTED_HINTS)

    def test_legacy_revision_delivers_the_same_hints(self) -> None:
        handshake = json.dumps(
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {
                    "protocolVersion": mcp_server.LEGACY_VERSIONS[0],
                    "capabilities": {},
                    "clientInfo": {"name": "legacy", "version": "1"},
                },
            }
        )
        listing = json.dumps(
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}
        )

        tools = self._tools([handshake, listing])

        for tool in tools:
            with self.subTest(tool=tool["name"]):
                self.assertEqual(tool["annotations"], EXPECTED_HINTS)

    def test_listing_twice_cannot_mutate_the_shared_hints(self) -> None:
        listing = json.dumps(
            {"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}
        )

        first = self._tools([listing])
        second = self._tools([listing])

        self.assertEqual(first, second)
        self.assertEqual(
            mcp_server.TOOLS[0]["annotations"], EXPECTED_HINTS
        )


if __name__ == "__main__":
    unittest.main()
