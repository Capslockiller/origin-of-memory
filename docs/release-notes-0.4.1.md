# Release notes — 0.4.1

Completing the panel announced in 0.4.0, and making the download usable.

## Local models, from the window

Backend independence was built in 0.3.0 but it was not usable: switching the
brain onto a local model meant editing an environment variable in a terminal.
The panel now has a **Local models** tab.

It shows what the machine can actually run — hardware and the ranked
recommendations straight from `donanim.py` and `model_oneri.py`, never a number
those scripts did not return — what is installed, read from Ollama's own
`/api/tags`, and which backend the next pipeline run will really use.

Three actions, each confirming first:

- **Pull** streams Ollama's real `completed`/`total` bytes, so progress is
  measured rather than animated, and refuses up front with a number when the
  disk cannot hold the model.
- **Switch** names the exact `BEYIN_MODEL_BACKEND` value and the exact place it
  will be stored *before* writing it. This project has a written rule that
  nothing a live pipeline reads may appear behind the owner's back, and a
  settings toggle is exactly where that rule earns its keep.
- **Try** sends one fixed prompt through the existing runners and reports the
  answer, the model and the latency. One prompt, one answer, no history — it is
  a smoke test, not a chat.

When Ollama is unreachable the tab says so. It does not render an empty
inventory that reads like a measured result.

**Model deletion is deliberately absent.** The panel deletes nothing, and that
holds even for a file the panel itself downloaded.

## Getting in without a terminal

The source zip now carries **`Setup.cmd`** — double-click, it starts the
graphical wizard and falls back to the terminal one if that fails — and
**`Local Brain.cmd`**, which opens the panel. Both only launch what already
exists.

**`OriginOfMemory-Setup-0.4.1.exe`** is attached to this release. It is
unsigned, so read "What Windows will do the first time" in
[`docs/installer.md`](../docs/installer.md) before clicking through the prompts:
SmartScreen's reputation warning and Defender's request to send a sample are
different things, and neither is a detection. Verify the download:

```
SHA256  8c9c089ced5b5410ce312ae92697c893621a362f7b53dc0f3c0e3a1e40213a74
```

## Not verified

Ollama is not installed on the machine this was built on, so the inventory,
pull progress, cancellation and resumption were **never exercised against a
real runtime**, and no backend switch was actually performed. The tab's logic
is tested; its behaviour against a live Ollama is not.
