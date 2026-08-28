#!/usr/bin/env python3
"""Serve the vault's memory over MCP (JSON-RPC 2.0 on stdio), read-only.

Dual-era server: it speaks the modern per-request-metadata revision
(``2026-07-28``) and the legacy ``initialize``-handshake revisions
(``2025-11-25`` / ``2025-06-18``) that shipping desktop clients still use.
Framing is newline-delimited JSON, exactly one message per line, as the stdio
transport binding prescribes.  ``stdout`` carries protocol messages only; all
logging goes to ``stderr``.
"""

from __future__ import annotations

import argparse
import io
import json
import logging
from pathlib import Path
import sys
from typing import Any, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))

import retrieve  # noqa: E402  — sibling script, stdlib-only


SERVER_NAME = "origin-of-memory"
SERVER_VERSION = "0.1.0"

MODERN_VERSION = "2026-07-28"
LEGACY_VERSIONS = ("2025-11-25", "2025-06-18")
SUPPORTED_VERSIONS = (MODERN_VERSION, *LEGACY_VERSIONS)

META_PROTOCOL_VERSION = "io.modelcontextprotocol/protocolVersion"
META_CLIENT_CAPABILITIES = "io.modelcontextprotocol/clientCapabilities"
META_SERVER_INFO = "io.modelcontextprotocol/serverInfo"

PARSE_ERROR = -32700
INVALID_REQUEST = -32600
METHOD_NOT_FOUND = -32601
INVALID_PARAMS = -32602
INTERNAL_ERROR = -32603
UNSUPPORTED_PROTOCOL_VERSION = -32022

ROOT_MAP_URI = "memory://root-map"
HUB_URI_PREFIX = "memory://hubs/"

MAX_K = 5
DEFAULT_K = 3

INDEX_MISSING_TEXT = (
    "Memory index not built yet: no retrieval database was found at {path}. "
    "Build it by running `python retrieve.py build` from the vault's script "
    "directory, then call this tool again."
)

INSTRUCTIONS = (
    "This server exposes the user's own long-term memory vault, read-only. "
    "Call `memory_search` before answering anything that depends on the user's "
    "history, past decisions, projects, or stated preferences; the model will "
    "otherwise answer from a blank slate. `memory_root_map` gives the "
    "high-level map of what the vault contains."
)

SEARCH_DESCRIPTION = (
    "Search the user's persistent memory vault and return the full text of the "
    "best-matching notes. USE THIS whenever the user asks about their own "
    "history, past work, earlier decisions, ongoing projects, tools, habits or "
    "preferences, or refers to something as already discussed or already "
    "decided -- and whenever an answer would be materially better with that "
    "context. Retrieval is lexical and deterministic; prefer distinctive nouns "
    "from the user's question as the query. Returns whole notes (capped), not "
    "snippets."
)

ROOT_MAP_DESCRIPTION = (
    "Return the vault's root map: the compact index of every topic hub, how "
    "many concepts each holds, when it was last updated, and its frequent "
    "tags. Use it to orient before searching, or when the user asks what is in "
    "their memory at all."
)

# Behaviour hints live in the tool's `annotations` object in every revision this
# server speaks (2025-06-18 onward).  They are hints, not a security boundary —
# the read-only guarantee is enforced by the server having no write surface at
# all — but a client that trims or auto-approves on them should see the truth:
# both tools only read vault files, repeat calls return the same thing, and
# nothing reaches outside the local vault.
READ_ONLY_ANNOTATIONS: dict[str, Any] = {
    "readOnlyHint": True,
    "destructiveHint": False,
    "idempotentHint": True,
    "openWorldHint": False,
}

TOOLS: tuple[dict[str, Any], ...] = (
    {
        "name": "memory_search",
        "title": "Search persistent memory",
        "description": SEARCH_DESCRIPTION,
        "annotations": dict(READ_ONLY_ANNOTATIONS),
        "inputSchema": {
            "type": "object",
            "properties": {
                "query": {
                    "type": "string",
                    "description": "Search text; distinctive nouns work best.",
                },
                "k": {
                    "type": "integer",
                    "minimum": 1,
                    "maximum": MAX_K,
                    "description": f"How many notes to return (1-{MAX_K}).",
                },
            },
            "required": ["query"],
            "additionalProperties": False,
        },
    },
    {
        "name": "memory_root_map",
        "title": "Read the memory root map",
        "description": ROOT_MAP_DESCRIPTION,
        "annotations": dict(READ_ONLY_ANNOTATIONS),
        "inputSchema": {"type": "object", "additionalProperties": False},
    },
)

LOGGER = logging.getLogger("mcp_server")


class JsonRpcError(Exception):
    """A failure that must be reported as a JSON-RPC error response."""

    def __init__(self, code: int, message: str, data: Any = None) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.data = data


def default_vault_root() -> Path:
    """Mirror the sibling-script convention: ``<vault>/.claude/scripts/x.py``."""
    return Path(__file__).resolve().parent.parent.parent


class MemoryServer:
    """Protocol-level dispatch over a read-only view of one vault."""

    def __init__(self, vault_root: Path) -> None:
        self.vault_root = Path(vault_root).resolve()
        self.legacy_handshake_seen = False

    # ---------------------------------------------------------------- vault

    @property
    def db_path(self) -> Path:
        return self.vault_root / ".claude" / "scripts" / ".state" / retrieve.DB_NAME

    @property
    def index_path(self) -> Path:
        return self.vault_root / "knowledge" / "index.md"

    @property
    def hubs_dir(self) -> Path:
        return self.vault_root / "knowledge" / "hubs"

    def hub_paths(self) -> list[Path]:
        if not self.hubs_dir.is_dir():
            return []
        return sorted(self.hubs_dir.glob("*.md"), key=lambda item: item.name)

    def _read_text(self, path: Path) -> str:
        try:
            return path.read_text(encoding="utf-8")
        except FileNotFoundError:
            raise JsonRpcError(
                INVALID_PARAMS, "Resource not found", {"path": path.name}
            ) from None
        except OSError as exc:
            raise JsonRpcError(INTERNAL_ERROR, f"Cannot read file: {exc}") from None

    # ---------------------------------------------------------------- tools

    def _tool_memory_search(self, arguments: dict[str, Any]) -> dict[str, Any]:
        query = arguments.get("query")
        if not isinstance(query, str) or not query.strip():
            return _tool_error("`query` is required and must be a non-empty string.")
        raw_k = arguments.get("k", DEFAULT_K)
        if isinstance(raw_k, bool) or not isinstance(raw_k, int):
            return _tool_error(f"`k` must be an integer between 1 and {MAX_K}.")
        limit = max(1, min(MAX_K, raw_k))
        if not self.db_path.is_file():
            LOGGER.warning("retrieval index missing at %s", self.db_path)
            return _tool_error(INDEX_MISSING_TEXT.format(path=self.db_path))
        try:
            result = retrieve.hook_result(query, limit=limit, db_path=self.db_path)
        except Exception as exc:  # sqlite/FTS failure must not kill the server
            LOGGER.exception("memory_search failed")
            return _tool_error(f"Memory search failed: {exc}")
        notes = result.get("notes", [])
        if not notes:
            return _text_result(f"No memory notes matched: {query}")
        blocks = [
            f"## {note['name']}\n\n{note['body'].strip()}" for note in notes
        ]
        header = (
            f"{len(notes)} note(s) from the memory vault, "
            f"{result.get('total_chars', 0)} characters total."
        )
        return _text_result(header + "\n\n" + "\n\n---\n\n".join(blocks))

    def _tool_memory_root_map(self, _arguments: dict[str, Any]) -> dict[str, Any]:
        if not self.index_path.is_file():
            return _tool_error(
                "No root map yet: knowledge/index.md does not exist in this vault."
            )
        return _text_result(self._read_text(self.index_path))

    # ------------------------------------------------------------ resources

    def _resource_entries(self) -> list[dict[str, Any]]:
        entries: list[dict[str, Any]] = []
        if self.index_path.is_file():
            entries.append(
                {
                    "uri": ROOT_MAP_URI,
                    "name": "index.md",
                    "title": "Memory root map",
                    "description": (
                        "Entry layer of the knowledge base: every topic hub with "
                        "its concept count, last update and frequent tags."
                    ),
                    "mimeType": "text/markdown",
                }
            )
        for path in self.hub_paths():
            entries.append(
                {
                    "uri": HUB_URI_PREFIX + path.stem,
                    "name": path.name,
                    "title": f"Topic hub: {path.stem}",
                    "description": (
                        "Hub page listing the concepts in this topic, its spine "
                        "and its boundaries with neighbouring hubs."
                    ),
                    "mimeType": "text/markdown",
                }
            )
        return entries

    def _resolve_resource(self, uri: str) -> Path:
        if uri == ROOT_MAP_URI:
            if not self.index_path.is_file():
                raise JsonRpcError(INVALID_PARAMS, "Resource not found", {"uri": uri})
            return self.index_path
        if uri.startswith(HUB_URI_PREFIX):
            stem = uri[len(HUB_URI_PREFIX) :]
            # Match against the listed set only; no path is built from input.
            for path in self.hub_paths():
                if path.stem == stem:
                    return path
        raise JsonRpcError(INVALID_PARAMS, "Resource not found", {"uri": uri})

    # ------------------------------------------------------------- dispatch

    def handle_message(self, message: Any) -> dict[str, Any] | None:
        """Return the response for one parsed message, or None for a notification."""
        if not isinstance(message, dict):
            return _error_response(
                None, INVALID_REQUEST, "Request must be a JSON object"
            )
        if message.get("jsonrpc") != "2.0":
            return _error_response(
                message.get("id"), INVALID_REQUEST, "Missing or invalid `jsonrpc`"
            )
        method = message.get("method")
        if not isinstance(method, str):
            return _error_response(
                message.get("id"), INVALID_REQUEST, "Missing or invalid `method`"
            )
        params = message.get("params", {})
        if params is None:
            params = {}
        if not isinstance(params, dict):
            return _error_response(
                message.get("id"), INVALID_PARAMS, "`params` must be an object"
            )

        if "id" not in message or message["id"] is None:
            self._handle_notification(method)
            return None

        request_id = message["id"]
        if not isinstance(request_id, (str, int)) or isinstance(request_id, bool):
            return _error_response(
                None, INVALID_REQUEST, "`id` must be a string or a number"
            )

        try:
            modern = self._negotiate(method, params)
            result = self._dispatch(method, params)
        except JsonRpcError as exc:
            return _error_response(request_id, exc.code, exc.message, exc.data)
        except Exception as exc:  # never let one bad request kill the loop
            LOGGER.exception("unhandled error while serving %s", method)
            return _error_response(request_id, INTERNAL_ERROR, f"Internal error: {exc}")

        if modern:
            result = {"resultType": "complete", **result}
            meta = dict(result.get("_meta") or {})
            meta[META_SERVER_INFO] = _implementation()
            result["_meta"] = meta
        return {"jsonrpc": "2.0", "id": request_id, "result": result}

    def _handle_notification(self, method: str) -> None:
        if method == "notifications/initialized":
            LOGGER.info("legacy client finished initialization")
        elif method == "notifications/cancelled":
            LOGGER.info("client cancelled a request; nothing long-running to stop")
        else:
            LOGGER.info("ignoring notification: %s", method)

    def _negotiate(self, method: str, params: dict[str, Any]) -> bool:
        """Return True when the request uses modern per-request metadata."""
        meta = params.get("_meta")
        version = meta.get(META_PROTOCOL_VERSION) if isinstance(meta, dict) else None
        if version is None:
            # Legacy request, or a client that omits metadata entirely; both are
            # served with legacy-shaped results.
            return False
        if not isinstance(version, str) or version not in SUPPORTED_VERSIONS:
            raise JsonRpcError(
                UNSUPPORTED_PROTOCOL_VERSION,
                "Unsupported protocol version",
                {"supported": list(SUPPORTED_VERSIONS), "requested": version},
            )
        if version in LEGACY_VERSIONS:
            return False
        if META_CLIENT_CAPABILITIES not in meta:
            raise JsonRpcError(
                INVALID_PARAMS,
                f"Missing required `_meta.{META_CLIENT_CAPABILITIES}`",
            )
        if method == "initialize":
            raise JsonRpcError(
                METHOD_NOT_FOUND,
                "`initialize` is a legacy method; this request declared "
                f"{MODERN_VERSION}, which has no handshake",
            )
        return True

    def _dispatch(self, method: str, params: dict[str, Any]) -> dict[str, Any]:
        if method == "server/discover":
            return {
                "supportedVersions": list(SUPPORTED_VERSIONS),
                "capabilities": _capabilities(),
                "instructions": INSTRUCTIONS,
                "_meta": {META_SERVER_INFO: _implementation()},
            }
        if method == "initialize":
            return self._initialize(params)
        if method == "ping":
            return {}
        if method == "tools/list":
            return {"tools": [dict(tool) for tool in TOOLS]}
        if method == "tools/call":
            return self._call_tool(params)
        if method == "resources/list":
            return {"resources": self._resource_entries()}
        if method == "resources/templates/list":
            return {"resourceTemplates": []}
        if method == "resources/read":
            return self._read_resource(params)
        raise JsonRpcError(METHOD_NOT_FOUND, f"Unknown method: {method}")

    def _initialize(self, params: dict[str, Any]) -> dict[str, Any]:
        self.legacy_handshake_seen = True
        requested = params.get("protocolVersion")
        chosen = (
            requested
            if isinstance(requested, str) and requested in LEGACY_VERSIONS
            else LEGACY_VERSIONS[0]
        )
        LOGGER.info("legacy handshake: requested=%r serving=%s", requested, chosen)
        return {
            "protocolVersion": chosen,
            "capabilities": _capabilities(),
            "serverInfo": _implementation(),
            "instructions": INSTRUCTIONS,
        }

    def _call_tool(self, params: dict[str, Any]) -> dict[str, Any]:
        name = params.get("name")
        arguments = params.get("arguments", {})
        if arguments is None:
            arguments = {}
        if not isinstance(arguments, dict):
            raise JsonRpcError(INVALID_PARAMS, "`arguments` must be an object")
        if name == "memory_search":
            return self._tool_memory_search(arguments)
        if name == "memory_root_map":
            return self._tool_memory_root_map(arguments)
        raise JsonRpcError(INVALID_PARAMS, f"Unknown tool: {name!r}")

    def _read_resource(self, params: dict[str, Any]) -> dict[str, Any]:
        uri = params.get("uri")
        if not isinstance(uri, str):
            raise JsonRpcError(INVALID_PARAMS, "`uri` is required and must be a string")
        path = self._resolve_resource(uri)
        return {
            "contents": [
                {
                    "uri": uri,
                    "mimeType": "text/markdown",
                    "text": self._read_text(path),
                }
            ]
        }


def _implementation() -> dict[str, str]:
    return {"name": SERVER_NAME, "version": SERVER_VERSION, "title": "Origin of Memory"}


def _capabilities() -> dict[str, Any]:
    return {"tools": {}, "resources": {}}


def _text_result(text: str) -> dict[str, Any]:
    return {"content": [{"type": "text", "text": text}], "isError": False}


def _tool_error(text: str) -> dict[str, Any]:
    return {"content": [{"type": "text", "text": text}], "isError": True}


def _error_response(
    request_id: Any, code: int, message: str, data: Any = None
) -> dict[str, Any]:
    error: dict[str, Any] = {"code": code, "message": message}
    if data is not None:
        error["data"] = data
    return {"jsonrpc": "2.0", "id": request_id, "error": error}


def serve(server: MemoryServer, stdin: Any, stdout: Any) -> int:
    """Run the newline-delimited JSON-RPC loop until stdin reaches EOF."""
    for line in stdin:
        line = line.strip()
        if not line:
            continue
        try:
            message = json.loads(line)
        except json.JSONDecodeError as exc:
            LOGGER.warning("malformed JSON on stdin: %s", exc)
            _write(stdout, _error_response(None, PARSE_ERROR, f"Parse error: {exc}"))
            continue
        response = server.handle_message(message)
        if response is not None:
            _write(stdout, response)
    LOGGER.info("stdin closed; shutting down")
    return 0


def _write(stdout: Any, payload: dict[str, Any]) -> None:
    # One message per line, never an embedded newline.
    stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    stdout.flush()


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--vault",
        type=Path,
        default=None,
        help="Vault root; defaults to the script's grandparent directory.",
    )
    parser.add_argument(
        "--log-level",
        default="INFO",
        choices=("DEBUG", "INFO", "WARNING", "ERROR"),
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = _parse_args(argv)
    logging.basicConfig(
        stream=sys.stderr,
        level=getattr(logging, args.log_level),
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    vault_root = args.vault if args.vault is not None else default_vault_root()
    server = MemoryServer(vault_root)
    LOGGER.info("serving vault %s", server.vault_root)
    stdin = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8", newline="")
    stdout = io.TextIOWrapper(
        sys.stdout.buffer, encoding="utf-8", newline="\n", write_through=True
    )
    try:
        return serve(server, stdin, stdout)
    except (BrokenPipeError, KeyboardInterrupt):
        LOGGER.info("transport closed; exiting")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
