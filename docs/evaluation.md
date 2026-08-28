# Evaluation

How the retrieval layer was measured, why the metric is deliberately dumb, what
the numbers can and cannot support, and how to build your own gold set.

Headline numbers: [../README.md](../README.md#measured-results).

---

## 1. What was measured

Exactly one thing: **did the right note surface?**

Not answer quality, not helpfulness, not summary faithfulness. The change this
project makes is to *retrieval* — which notes reach the model's context. Scoping
the metric to retrieval is what makes it defensible: synthetic and proxy questions
preserve retrieval rankings far better than they preserve generation rankings, so
a retrieval-only metric survives the weaknesses of a small personal evaluation
that a generation metric would not.

## 2. The gold set

- **130 questions**, of which **125 are scored** and **5 are held back as
  canaries** — kept out of the scored set so a suspiciously good result can be
  sanity-checked against questions that were never used for tuning.
- **Real historical questions, not synthetic ones.** Every question is something
  the author actually asked, recovered from a Gemini archive and Claude Code
  transcripts, plus 8 live questions from the day of the run. Synthetic question
  generation was rejected: a model asked to write questions about a corpus writes
  questions that the corpus's own vocabulary answers, which inflates recall.
- **One gold note per question.** Each question is annotated with the identity of
  the single concept note that should answer it.
- **Pinned corpus snapshot.** The evaluation records the commit of the vault it
  was run against. Recall against a moving corpus is not a number, it is a mood.
- Questions carry a class label (for example, single-note versus multi-note) and a
  source label, so failures can be grouped rather than merely counted.

**The gold questions are not published in this repository, and will not be.**
They are one person's real questions, drawn from real conversations about work,
health, family and finances. Publishing them would publish the corpus. This means
the headline numbers cannot be independently reproduced — that is a genuine
limitation of the evidence, stated rather than hidden. What *is* reproducible is
the method, and the harness that produces the numbers is the shipped
`retrieve.py` itself.

## 3. Metric: judge-free binary recall@k

For each question, run the retrieval query exactly as the hook does, take the top
k results, and score 1 if the gold note identity appears among them, 0 otherwise.
Recall@k is the mean. No partial credit, no similarity threshold, no model in the
loop.

```
recall@k = (number of questions whose gold note is in the top k) / (number scored)
```

Reported: recall@3, because the hook injects three notes, and recall@5 as a
sensitivity check on whether the cut-off is where the loss is.

### Why no LLM judge

An LLM judge was considered and rejected for three independent reasons:

1. **The corpus is model-written.** The concept notes are compiled by a model.
   Judges reward low-perplexity, model-like text, so a judge scoring model-written
   notes is grading its own dialect.
2. **Self-preference is documented.** Judges measurably prefer their own outputs
   over equivalent alternatives, which is precisely the confound in (1).
3. **Multilingual judging is unreliable.** Inter-judge agreement drops sharply
   outside English, and this corpus is predominantly Turkish. Published audits of
   memory benchmarks have found standard LLM judges accepting a large share of
   deliberately wrong answers.

A judge would have added a noisy, biased layer on top of a question that has an
exact answer: is this note's filename in this list? Binary identity matching has
no free parameters, no prompt to tune, and no way to accidentally measure
eloquence.

The cost of this choice is honest: recall@k does **not** measure whether the model
used the retrieved note well. It measures whether the note was there to be used.
A separate generation-quality evaluation would need a different design.

## 4. Results and baseline

| Metric | Value |
| --- | --- |
| recall@3 | 104/125 = 83.2% |
| recall@5 | 114/125 = 91.2% |
| Baseline (before this system) | 0% |
| Retrieval hook latency, p95 | 347 ms |

**The 0% baseline is literal, not rhetorical.** Before this work, the read-side
hook was registered at *project* scope, for a directory in which no working
sessions were ever opened. Sessions ran elsewhere. No note reached any session,
so the gold note was in the top k zero times out of 125. The write side was
already user-level, which is how the corpus existed at all: the brain wrote from
everywhere and read in one folder.

**Correction (2026-08-28).** This document previously reported recall@5 as
105/125 = 84%, and drew a conclusion from the apparently flat curve between k=3
and k=5. Both were wrong. The original results file shows the `top5` column
never held more than three candidates — the run had retrieved with `limit=3`
and the column was mislabelled. A correct re-run at `limit=5` gives
**114/125 = 91.2%**, with recall@3 unchanged at 104/125, which is what
validates the re-run rather than the corpus having drifted.

**Read recall@3 → recall@5 carefully.** 83.2% → 91.2%: ten questions have their
gold note sitting in positions four and five. Whether to spend injection budget
on them is a real trade-off — each extra note costs characters in every prompt —
but the notes are there, which is the opposite of what the earlier flat curve
suggested.

**Latency.** p95 347 ms for the whole hook path, including Python interpreter
startup, against a hard 5-second `UserPromptSubmit` timeout — and, more
importantly, against a user waiting for their prompt to be processed. The design
budget set before implementation was roughly 500 ms; the FTS5 approach was chosen
over embeddings partly because CPU transformer startup does not fit in a hook at
all.

## 5. The statistical floor — read this before comparing two configurations

The gold set is not large enough to resolve small differences.

At n = 125, a paired comparison between two retrieval configurations needs a net
difference of roughly **16 questions** to reach p < 0.05.

"Net" matters: what the test consumes is discordant pairs — questions where one
configuration hits and the other misses. Twenty questions that flip in one
direction and twelve back the other way is a net of eight, not thirty-two, and it
is not significant.

Consequences:

- A change worth **a few points** of recall is **invisible** at this scale.
  Detecting a 3-point effect needs roughly n ≈ 1000 questions.
- Do not tune BM25 weights or `--min-score` against this set and declare the
  winner. You will be fitting noise, and the canary questions exist partly to
  catch it.
- A personal gold set answers "did this large change work?" It does not answer
  "which of these two similar configurations is better?"
- Report absolute counts (`104/125`), not just percentages. Percentages hide how
  few questions are actually moving.

## 6. Building your own gold set

The numbers above are one corpus, one language mix, one person's question
distribution. If you install this, measure your own. It costs an afternoon.

1. **Wait for a corpus.** Compile enough daily logs that `knowledge/concepts/`
   holds a few hundred notes, and pin the commit you are testing against.
2. **Harvest real questions, do not invent them.** Search your own history — Claude
   Code transcripts, other assistants' exports, your notes — for questions you
   genuinely asked. Aim for at least 60; 100+ is better, and remember §5 about
   what even 125 cannot resolve.
3. **Keep them as you asked them.** Typos, missing diacritics, mid-sentence
   pivots, wrong terminology. That is the actual input distribution. Cleaning the
   questions up makes the evaluation easier and makes it lie.
4. **Annotate one gold note per question.** Open the concept that should answer it
   and record its filename stem. If no note answers it, that question tests the
   compiler, not retrieval — set it aside in a separate bucket rather than
   scoring it as a retrieval failure.
5. **Hold back canaries.** Reserve a handful of questions, never look at them
   while iterating, and use them once at the end.
6. **Score with the shipped query path**, so you measure the system rather than a
   reimplementation of it:

   ```powershell
   python scripts\retrieve.py query "<your question>" --limit 5 --format plain
   ```

   Store one row per question — id, class, source, question, gold, top-5, hit@3,
   hit@5 — as CSV. That layout keeps failure analysis possible: the top-5 column
   is what tells you *why* a question missed.
7. **Record the corpus commit and the date** alongside the result. A recall number
   without a corpus snapshot is not comparable to anything, including itself next
   week.
8. **Re-run after corpus growth, not after every tweak.** The floor in §5 means
   frequent re-runs mostly measure noise.

### Reading failures

The useful output is not the percentage, it is the 20-odd rows that missed. Group
them:

- **Wrong vocabulary** — the question and the note describe the same thing in
  different words. This is a compiler/aliases problem, not a ranking problem: put
  the question's words into the note's `aliases`.
- **Note does not exist** — the compiler never made a concept for it. Fix belongs
  upstream, in the compile prompt or the source coverage.
- **Ranked but below k** — genuine ranking failure; check whether the recall@5
  column rescues it. On the corrected run, ten of these were rescued at
  positions four and five, which is why the top-5 column is worth storing.
- **Query too short or filtered** — the hook skips prompts under 12 characters and
  slash commands. Real usage does this too; count it, do not exempt it.

## 7. Threats to validity

- **Single corpus, single author, single language mix.** Nothing here establishes
  that 83% transfers to your vault.
- **Gold annotation is by the corpus owner**, who also wrote the system. The
  choice of which note "should" answer a question is a judgement call, and it was
  made by an interested party.
- **Questions are drawn from history that also fed the corpus.** A question asked
  in a session that was later compiled into a note is not an independent probe of
  the retrieval layer, it is closer to a memorisation check. The canary set and
  the class labels limit this but do not remove it.
- **One run.** Retrieval is deterministic given a fixed corpus and query, so
  repeated runs add nothing — but that also means the numbers carry no variance
  estimate across corpus states.
- **The 0% baseline compares a working system against a broken registration**,
  not against a competing retrieval design. It establishes that memory now
  arrives; it does not establish that BM25 beats any particular alternative.
