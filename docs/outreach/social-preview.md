# Social preview image — specification

**A specification, not an image.** This page says exactly what the 1280×640 PNG
must contain so the orchestrator can generate it. Nothing here is a screenshot
and nothing here should be faked into one.

Upload path: GitHub → Settings → General → Social preview → Upload an image.

---

## Format

| | |
| --- | --- |
| Size | **1280 × 640 px** (GitHub's stated size; it renders at 2:1) |
| Format | PNG, under 1 MB |
| Safe area | Keep all text inside a 1120 × 520 centred box — Slack, Discord and X crop the edges differently |
| Minimum text size | ~28 px for the smallest line; it is read as a ~600 px wide thumbnail in a feed |
| Contrast | Must survive both light and dark chat clients. Do not rely on a mid-grey that works in neither |

## Content — exactly four things

1. **The name**, largest element:

   ```
   Origin of Memory
   ```

2. **One line under it**, the value proposition, verbatim from the README so the
   card and the page agree:

   ```
   Persistent, cross-project memory for Claude Code on Windows
   ```

3. **Three key facts**, as three short items (a row of three, or a stacked
   list). These are the facts, and the wording is deliberate:

   ```
   Sessions summarised and compiled automatically — no tool the model has to call
   recall@3 83.2% · recall@5 91.2% · measured on one personal corpus
   Python 3.12 stdlib only · SQLite FTS5 · no vector DB, no API keys
   ```

   The words "measured on one personal corpus" are **not optional**. A recall
   figure on a shareable card without its caveat is the one way this project
   could accidentally misrepresent itself.

4. **The repository path**, small, bottom corner:

   ```
   github.com/Capslockiller/origin-of-memory
   ```

## Style

- Terminal aesthetic fits the project: monospace, dark background, one accent
  colour. Plain and typographic beats illustrated.
- **No fabricated screenshot.** No invented terminal output, no mocked-up
  session, no fake UI. If a terminal frame is used as decoration, it must be
  empty or contain only the three facts above as text.
- **No logo that implies affiliation.** Do not use Anthropic's or Claude's
  marks, wordmarks or colours-as-branding. The word "Claude Code" in the value
  proposition line is a factual reference to what it works with and is fine;
  a logo is not.
- No stars, no download counts, no badges, no "used by".
- No superlatives.

## What to leave off

- The external code review scores. They are a review, not an endorsement, and a
  score on a promotional card reads as the latter.
- Any number not already published in the README.
- Any claim about adoption, users, or community.
