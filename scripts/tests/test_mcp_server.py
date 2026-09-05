"""Drive the MCP server as a real subprocess over newline-delimited JSON-RPC."""

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
import retrieve


SERVER = Path(mcp_server.__file__).resolve()
MODERN = mcp_server.MODERN_VERSION
LEGACY = mcp_server.LEGACY_VERSIONS[0]

MODERN_META = {
    mcp_server.META_PROTOCOL_VERSION: MODERN,
    mcp_server.META_CLIENT_CAPABILITIES: {},
    "io.modelcontextprotocol/clientInfo": {"name": "test", "version": "0"},
}


def request(request_id, method: str, params: dict | None = None) -> str:
    body = dict(params or {})
    body["_meta"] = {**MODERN_META, **body.get("_meta", {})}
    return json.dumps(
        {"jsonrpc": "2.0", "id": request_id, "method": method, "params": body},
        ensure_ascii=False,
    )


class ServerHarness(unittest.TestCase):
    """Builds a synthetic vault; every exchange goes through a real process."""

    build_index = True

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.concepts = self.root / "knowledge" / "concepts"
        self.hubs = self.root / "knowledge" / "hubs"
        self.state = self.root / ".claude" / "scripts" / ".state"
        self.concepts.mkdir(parents=True)
        self.hubs.mkdir(parents=True)
        self.state.mkdir(parents=True)
        (self.root / "knowledge" / "index.md").write_text(
            "# Kök Harita\n\n| Konu merkezi | Kavram |\n|---|---|\n"
            "| [[hubs/bellek|Bellek]] | 1 |\n",
            encoding="utf-8",
        )
        (self.hubs / "bellek.md").write_text(
            "# Bellek\n\n## Üyeler\n\n| Kavram | Özet |\n|---|---|\n",
            encoding="utf-8",
        )
        (self.hubs / "uretim.md").write_text("# Üretim\n", encoding="utf-8")
        self.write_note("kalici-bellek", body="Kalıcı bellek katmanı sürekliliktir.")
        if self.build_index:
            retrieve.build_index(vault_root=self.root, state_dir=self.state)

    def write_note(self, name: str, *, body: str) -> None:
        (self.concepts / f"{name}.md").write_text(
            "---\n"
            f"title: {name}\n"
            'aliases: []\n'
            'tags: ["bellek"]\n'
            "created: 2026-08-27\n"
            "updated: 2026-08-27\n"
            "---\n\n"
            f"{body}\n",
            encoding="utf-8",
        )

    def exchange(self, lines: list[str], timeout: int = 60) -> tuple[list, str]:
        """Feed raw lines to the server, close stdin, collect its responses."""
        process = subprocess.run(
            [sys.executable, str(SERVER), "--vault", str(self.root)],
            input="\n".join(lines) + "\n",
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=timeout,
        )
        self.assertEqual(process.returncode, 0, msg=process.stderr)
        responses = [
            json.loads(line) for line in process.stdout.splitlines() if line.strip()
        ]
        return responses, process.stderr

    def results(self, lines: list[str]) -> list:
        responses, _stderr = self.exchange(lines)
        return responses


class ModernSessionTests(ServerHarness):
    def test_full_session_over_stdio(self) -> None:
        responses = self.results(
            [
                request(1, "server/discover"),
                request(2, "tools/list"),
                request(
                    3,
                    "tools/call",
                    {"name": "memory_search", "arguments": {"query": "bellek", "k": 2}},
                ),
                request(4, "resources/list"),
                request(5, "resources/read", {"uri": mcp_server.ROOT_MAP_URI}),
            ]
        )

        self.assertEqual([item["id"] for item in responses], [1, 2, 3, 4, 5])
        discover = responses[0]["result"]
        self.assertEqual(discover["resultType"], "complete")
        self.assertIn(MODERN, discover["supportedVersions"])
        self.assertIn("tools", discover["capabilities"])
        self.assertIn("resources", discover["capabilities"])
        self.assertEqual(
            discover["_meta"][mcp_server.META_SERVER_INFO]["name"],
            mcp_server.SERVER_NAME,
        )

        names = [tool["name"] for tool in responses[1]["result"]["tools"]]
        self.assertEqual(names, ["memory_search", "memory_root_map"])
        schema = responses[1]["result"]["tools"][0]["inputSchema"]
        self.assertEqual(schema["required"], ["query"])

        call = responses[2]["result"]
        self.assertFalse(call["isError"])
        self.assertIn("Kalıcı bellek katmanı", call["content"][0]["text"])
        self.assertLess(
            call["content"][0]["text"].index(mcp_server.giris_kapisi.HOOK_HEADER.strip()),
            call["content"][0]["text"].index("Kalıcı bellek katmanı"),
        )

        uris = [item["uri"] for item in responses[3]["result"]["resources"]]
        self.assertEqual(
            uris,
            [
                mcp_server.ROOT_MAP_URI,
                mcp_server.HUB_URI_PREFIX + "bellek",
                mcp_server.HUB_URI_PREFIX + "uretim",
            ],
        )

        contents = responses[4]["result"]["contents"]
        self.assertEqual(contents[0]["uri"], mcp_server.ROOT_MAP_URI)
        self.assertEqual(contents[0]["mimeType"], "text/markdown")
        self.assertIn("Kök Harita", contents[0]["text"])

    def test_hub_resource_and_root_map_tool(self) -> None:
        responses = self.results(
            [
                request(1, "resources/read", {"uri": mcp_server.HUB_URI_PREFIX + "bellek"}),
                request(2, "tools/call", {"name": "memory_root_map"}),
            ]
        )

        self.assertIn("# Bellek", responses[0]["result"]["contents"][0]["text"])
        self.assertIn("Kök Harita", responses[1]["result"]["content"][0]["text"])

    def test_search_caps_are_inherited_from_retrieve(self) -> None:
        self.write_note("uzun-not", body="bellek " + ("dolgu " * 2_000))
        retrieve.build_index(vault_root=self.root, state_dir=self.state)

        responses = self.results(
            [request(1, "tools/call", {"name": "memory_search", "arguments": {"query": "bellek", "k": 5}})]
        )

        text = responses[0]["result"]["content"][0]["text"]
        self.assertLess(len(text), retrieve.TOTAL_BODY_CAP + 1_000)

    def test_notification_gets_no_response_and_stdout_stays_clean(self) -> None:
        responses, stderr = self.exchange(
            [
                json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}),
                request(7, "tools/list"),
            ]
        )

        self.assertEqual([item["id"] for item in responses], [7])
        self.assertIn("serving vault", stderr)


class ErrorPathTests(ServerHarness):
    def test_malformed_and_invalid_requests(self) -> None:
        responses = self.results(
            [
                "{not json at all",
                json.dumps({"jsonrpc": "1.0", "id": 2, "method": "tools/list"}),
                json.dumps({"jsonrpc": "2.0", "id": 3}),
                request(4, "no/such/method"),
                request(5, "tools/call", {"name": "nope"}),
                request(6, "resources/read", {"uri": "memory://hubs/../../secret"}),
                json.dumps(
                    {
                        "jsonrpc": "2.0",
                        "id": 7,
                        "method": "tools/list",
                        "params": {
                            "_meta": {
                                mcp_server.META_PROTOCOL_VERSION: "1900-01-01",
                                mcp_server.META_CLIENT_CAPABILITIES: {},
                            }
                        },
                    }
                ),
                json.dumps(
                    {
                        "jsonrpc": "2.0",
                        "id": 8,
                        "method": "tools/list",
                        "params": {
                            "_meta": {mcp_server.META_PROTOCOL_VERSION: MODERN}
                        },
                    }
                ),
                request(9, "tools/list"),
            ]
        )

        codes = {item.get("id"): item["error"]["code"] for item in responses[:-1]}
        self.assertEqual(codes[None], mcp_server.PARSE_ERROR)
        self.assertEqual(codes[2], mcp_server.INVALID_REQUEST)
        self.assertEqual(codes[3], mcp_server.INVALID_REQUEST)
        self.assertEqual(codes[4], mcp_server.METHOD_NOT_FOUND)
        self.assertEqual(codes[5], mcp_server.INVALID_PARAMS)
        self.assertEqual(codes[6], mcp_server.INVALID_PARAMS)
        self.assertEqual(codes[7], mcp_server.UNSUPPORTED_PROTOCOL_VERSION)
        self.assertEqual(codes[8], mcp_server.INVALID_PARAMS)
        unsupported = next(item for item in responses if item.get("id") == 7)
        self.assertIn(MODERN, unsupported["error"]["data"]["supported"])
        # The loop survives every one of them.
        self.assertIn("tools", responses[-1]["result"])

    def test_bad_tool_arguments_are_tool_errors_not_crashes(self) -> None:
        responses = self.results(
            [
                request(1, "tools/call", {"name": "memory_search", "arguments": {}}),
                request(
                    2,
                    "tools/call",
                    {"name": "memory_search", "arguments": {"query": "x", "k": "many"}},
                ),
            ]
        )

        for response in responses:
            self.assertTrue(response["result"]["isError"])


class MissingIndexTests(ServerHarness):
    build_index = False

    def test_missing_database_reports_index_not_built(self) -> None:
        responses = self.results(
            [
                request(
                    1,
                    "tools/call",
                    {"name": "memory_search", "arguments": {"query": "bellek"}},
                ),
                request(2, "tools/list"),
            ]
        )

        result = responses[0]["result"]
        self.assertTrue(result["isError"])
        self.assertIn("not built yet", result["content"][0]["text"])
        self.assertIn("tools", responses[1]["result"])


class LegacySessionTests(ServerHarness):
    def test_initialize_handshake_and_calls(self) -> None:
        def legacy(request_id, method, params=None):
            return json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": request_id,
                    "method": method,
                    "params": params or {},
                },
                ensure_ascii=False,
            )

        responses = self.results(
            [
                legacy(
                    1,
                    "initialize",
                    {
                        "protocolVersion": LEGACY,
                        "capabilities": {},
                        "clientInfo": {"name": "legacy", "version": "1"},
                    },
                ),
                json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}),
                legacy(2, "tools/list"),
                legacy(
                    3,
                    "tools/call",
                    {"name": "memory_search", "arguments": {"query": "bellek"}},
                ),
                legacy(4, "resources/list"),
            ]
        )

        initialize = responses[0]["result"]
        self.assertEqual(initialize["protocolVersion"], LEGACY)
        self.assertEqual(initialize["serverInfo"]["name"], mcp_server.SERVER_NAME)
        self.assertNotIn("resultType", initialize)
        self.assertEqual([item["id"] for item in responses], [1, 2, 3, 4])
        self.assertFalse(responses[2]["result"]["isError"])
        self.assertTrue(responses[3]["result"]["resources"])

    def test_modern_request_rejects_initialize(self) -> None:
        responses = self.results([request(1, "initialize", {"protocolVersion": MODERN})])

        self.assertEqual(
            responses[0]["error"]["code"], mcp_server.METHOD_NOT_FOUND
        )


class VaultRootTests(unittest.TestCase):
    def test_default_vault_root_follows_sibling_script_convention(self) -> None:
        expected = Path(mcp_server.__file__).resolve().parent.parent.parent
        self.assertEqual(mcp_server.default_vault_root(), expected)


if __name__ == "__main__":
    unittest.main()
