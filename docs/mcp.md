# MCP server — serving the vault to any MCP client

`scripts/mcp_server.py` is a local [Model Context Protocol](https://modelcontextprotocol.io)
server that exposes the vault's memory, **read-only**, to any MCP-capable client:
Claude Desktop, other desktop assistants, editor integrations, or your own
harness. It is stdlib-only Python 3.12 — no `mcp` SDK, no third-party packages,
in keeping with the project's zero-dependencies rule.

Where the hooks *push* memory into a Claude Code session, this server lets a
client *pull* it on demand. The two are independent: you can run either, both,
or neither.

## Protocol

- **Transport:** stdio. The client launches the script as a subprocess and
  speaks newline-delimited JSON-RPC 2.0 over its standard streams — one message
  per line, no embedded newlines, no `Content-Length` framing. `stdout` carries
  protocol messages only; every log line goes to `stderr`.
- **Revisions:** the server is dual-era. It answers the modern revision
  **`2026-07-28`**, in which every request carries its own
  `_meta["io.modelcontextprotocol/protocolVersion"]` and
  `_meta["io.modelcontextprotocol/clientCapabilities"]` and there is no
  handshake, *and* the legacy handshake revisions `2025-11-25` / `2025-06-18`
  that most shipping desktop clients still use. A request that declares a
  version the server does not support gets
  `UnsupportedProtocolVersionError` (`-32022`) listing what it does support.
- **Methods:** `server/discover`, `initialize` (legacy only) plus
  `notifications/initialized`, `ping`, `tools/list`, `tools/call`,
  `resources/list`, `resources/templates/list`, `resources/read`.
- **Shutdown:** the server exits cleanly when its stdin reaches EOF, which is
  the transport's primary graceful-shutdown signal. `notifications/cancelled`
  is accepted and ignored — nothing here runs long enough to cancel.
- **Errors:** malformed JSON gets `-32700`; a non-object, wrong-`jsonrpc`, or
  method-less request gets `-32600`; unknown methods get `-32601`; an unknown
  tool name or resource URI gets `-32602`. None of these end the session — the
  loop keeps serving the next line.

## What it exposes

### Tools

| Tool | Input | Returns |
|---|---|---|
| `memory_search` | `{ "query": string, "k"?: integer 1–5 }` | The full text of the top-*k* matching notes |
| `memory_root_map` | none | The contents of `knowledge/index.md` |

`memory_search` calls `retrieve.hook_result()` directly — the same deterministic
FTS5 path the retrieval hook uses, with the same per-note (1 500 chars) and
per-response (4 500 chars) caps. There is no second copy of the ranking logic.

### Resources

- `memory://root-map` — `knowledge/index.md`
- `memory://hubs/<hub-id>` — one entry per file in `knowledge/hubs/*.md`,
  enumerated at request time, so hubs added by the evening compiler appear
  without restarting the server.

URIs are resolved by matching against the enumerated set, never by joining
client input onto a filesystem path, so `memory://hubs/../../secret` is simply
"not found".

## Vault location

The server finds the vault in one of two ways:

1. `--vault <path>`, which wins if given.
2. Otherwise the sibling-script convention used by every script in this repo:
   the script's grandparent directory. Installed at
   `<vault>\.claude\scripts\mcp_server.py`, that resolves to `<vault>`.

`--log-level DEBUG|INFO|WARNING|ERROR` (default `INFO`) controls the stderr log.

Before `memory_search` returns anything, the retrieval index has to exist at
`<vault>\.claude\scripts\.state\notes.db`. If it does not, the tool returns a
plain text result saying the index is not built yet and how to build it — it
never crashes, and the rest of the session keeps working.

## Registering it with Claude Desktop

Edit `claude_desktop_config.json` (Settings → Developer → Edit Config; on
Windows it lives in `%APPDATA%\Claude\`), and add an `mcpServers` entry:

```json
{
  "mcpServers": {
    "origin-of-memory": {
      "command": "python",
      "args": [
        "C:\\path\\to\\vault\\.claude\\scripts\\mcp_server.py",
        "--vault",
        "C:\\path\\to\\vault"
      ]
    }
  }
}
```

`--vault` is redundant when the script sits in `<vault>\.claude\scripts\`, but
being explicit survives someone moving the file later. Use an absolute
interpreter path (`C:\\Python312\\python.exe`) if `python` is not on the PATH
the desktop app inherits. Restart the app fully after editing; the tools then
appear in the client's tool list.

Local MCP servers of this kind run on every Claude plan, free included — as of
August 2026, per Anthropic's help center; verify current policy at
<https://support.claude.com/en/articles/11176164-use-connectors-to-extend-claude-s-capabilities>.
(Remote/hosted connectors are the ones with plan restrictions.)

Any other MCP client works the same way: point it at
`python <vault>\.claude\scripts\mcp_server.py` as a stdio server.

## Caveat: models under-call retrieval tools

This project measured it. Given a memory tool and a question that clearly
depends on the user's history, a model frequently answers from its own priors
instead of calling the tool. That is the whole reason the Claude Code side of
this repo *pushes* memory in through a hook rather than waiting to be asked.
An MCP tool is by construction a pull mechanism, so it inherits the problem.

Mitigate it in your client, not in the server. Add a standing instruction to
the project/personalization settings of whatever client you register this with,
along the lines of:

> Check `memory_search` before answering questions about my work, my projects,
> or anything I have told you before.

The tool descriptions here are already written to invite the call, but a
standing instruction in the client moves the hit rate far more than wording
does. Treat the tool as a supplement to the push path, not a replacement.

## Security

The server is **read-only**. It has no write surface at all: no tool or
resource creates, edits, or deletes anything in the vault, and it never shells
out. Its entire reach is reading `knowledge/index.md`, `knowledge/hubs/*.md`,
and the notes returned from the FTS5 index.

That said, it exposes vault content to whatever client launches it, with no
authentication — a local stdio server trusts its parent process by design. Two
consequences worth keeping in mind:

- Register it only with clients you would hand the vault to. Anything that can
  spawn the process can read every note the index can return.
- Note bodies reach the model verbatim. The vault's own secret-scrubbing
  (`scripts/secret_guard.py`) runs on the ingest path, upstream of everything
  here; this server adds no further filtering.

Because it is stdio-only, none of the MCP HTTP authorization framework applies,
and nothing listens on a port.
