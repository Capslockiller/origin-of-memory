#!/usr/bin/env python3
# yazan: codex · gpt-6
"""Gate-only candidates over shipped BM25F; kos20 scoring, no vault writes.

Build bench/gate/Gate.csproj and publish oom first. Baseline and final snapshots
remain separate. The reflection adapter exports tokens/scores, never note bodies.
"""
import argparse
import hashlib
import json
import math
import os
import platform
import re
import subprocess
from pathlib import Path
import kos20

ROOT = Path(__file__).resolve().parents[1]
WORK = ROOT / '.brief' / 'gate'
EXE = ROOT / 'publish/win-x64/oom.exe'
DRIVER = ROOT / 'bench/gate/bin/Release/net9.0-windows10.0.19041.0/win-x64/Gate.exe'
SOURCE = Path(r'<repo>\bench\vm\.out\hafiza-obegi')
SLICE = ROOT / '.brief/hafiza-obegi'
VAULT = Path(r'<vault>')


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def manifest(vault):
    return {p.name: digest(p) for p in sorted((vault / 'knowledge/concepts').glob('*.md'))}


def run(command, **kwargs):
    result = subprocess.run([str(x) for x in command], capture_output=True, text=True,
                            encoding='utf-8', cwd=ROOT, **kwargs)
    if result.returncode:
        raise RuntimeError(f'{command}: {result.returncode}: {result.stderr[:1000]}')
    return result


def accept(rule, hit, row, n):
    if row['gateReason'] is not None:
        return False
    kind, value = rule
    if kind == 'relative':
        score_ok = hit['score'] >= row['ranked'][0]['score'] * value
    elif kind == 'scaled-linear':
        score_ok = hit['mean'] >= min(1.0, n / 542)
    elif kind == 'scaled-log':
        score_ok = hit['mean'] >= min(1.0, math.log1p(n) / math.log1p(542))
    else:
        score_ok = hit['mean'] >= (1.0 if kind == 'ratio' else value)
    overlap_ok = (hit['overlap'] / max(1, len(row['contentWords'])) >= value
                  if kind == 'ratio' else hit['overlap'] >= 3)
    return score_ok and overlap_ok


def score_gate(rows, diagnostics, rule, k):
    hits = [[{'slug': kos20.slug(h['name'])} for h in d['ranked'][:k]
             if accept(rule, h, d, diagnostics['documents'])] for d in diagnostics['rows']]
    positives = [(r, h) for r, h in zip(rows, hits) if r['gold']]
    negatives = [(r, h) for r, h in zip(rows, hits) if not r['gold']]
    return {'n': len(positives), 'correct': sum(kos20.recall_at(h, r['gold'], k) for r, h in positives),
            'negative_n': len(negatives), 'negative_injections': sum(bool(h) for _, h in negatives),
            'hits': {r['id']: [h['slug'] for h in hs] for r, hs in zip(rows, hits)}}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--phase', choices=['baseline', 'final'], required=True)
    args = parser.parse_args()
    WORK.mkdir(parents=True, exist_ok=True)
    (WORK / 'local').mkdir(exist_ok=True)
    gold = kos20.load_gold(ROOT / '.brief/gold-sorular.jsonl', None)
    slice_rows = []
    for number, question, slug in re.findall(r'^(\d+)\. (.+?) → (\S+)',
                                             (SOURCE / 'OKU.md').read_text(encoding='utf-8'), re.M):
        slice_rows.append({'id': f'S{number}', 'soru': question, 'gold': [slug], 'sinif': 'slice'})
    slice_rows.append({'id': 'S9', 'soru': 'Ay tutulmasi kac dakika surer?', 'gold': [], 'sinif': 'kanarya'})
    assert len(slice_rows) == 9
    assert manifest(SOURCE) == manifest(SLICE)
    result = {'yazan': 'codex', 'model': 'gpt-6', 'phase': args.phase,
              'machine': platform.platform(), 'source_commit': run(['git', 'rev-parse', 'HEAD']).stdout.strip(),
              'exe_sha256': digest(EXE), 'gold_sha256': digest(ROOT / '.brief/gold-sorular.jsonl'),
              'corpora': {}, 'candidates': []}
    for key, vault, rows in [('vault', VAULT, gold), ('slice', SLICE, slice_rows)]:
        before = manifest(vault)
        batch = WORK / f'{key}-batch.jsonl'
        batch.write_text(''.join(json.dumps(r, ensure_ascii=False) + '\n' for r in rows), encoding='utf-8')
        diagnostic_path = WORK / f'{args.phase}-{key}-diagnostics.json'
        # Always execute this build: stale snapshots must never select a rule.
        diagnostics = json.loads(run([DRIVER, vault, batch]).stdout)
        kos20.write_json(diagnostic_path, diagnostics)
        cli_error = None
        try:
            rankings, elapsed = kos20.run_batch(EXE, str(vault), rows, 5, WORK / key)
        except SystemExit as error:
            cli_error = str(error)
            if 'UnauthorizedAccessException' not in cli_error:
                raise
            rankings = [[{'slug': kos20.slug(h['name']), 'score': h['score']} for h in d['query']]
                        for d in diagnostics['rows']]
            elapsed = None
        scored = [(r, h) for r, h in zip(rows, rankings) if r['gold']]
        result['corpora'][key] = {
            'path': str(vault), 'manifest_sha256': before, 'documents': diagnostics['documents'],
            'query_recall_at3': sum(kos20.recall_at(h, r['gold'], 3) for r, h in scored) / len(scored),
            'query_recall_at5': sum(kos20.recall_at(h, r['gold'], 5) for r, h in scored) / len(scored),
            'batch_ms': elapsed, 'cli_error': cli_error, 'diagnostics': diagnostics,
            'query_rankings': {r['id']: h for r, h in zip(rows, rankings)},
            'gold': {r['id']: r['gold'] for r in rows}}
        assert len(rows) == len(diagnostics['rows'])
        assert all(r['id'] == d['id'] for r, d in zip(rows, diagnostics['rows']))
        assert diagnostics['documents'] == len(before)
        assert key != 'slice' or diagnostics['documents'] == 19
        assert all([h['slug'] for h in hs] == [kos20.slug(h['name']) for h in d['query']]
                   for hs, d in zip(rankings, diagnostics['rows'])), 'CLI and driver ranking differ'
        for k in (3, 5):
            actual = [[{'slug': kos20.slug(h['name'])} for h in d['ranked'][:k]
                       if h['name'] in d['hook']] for d in diagnostics['rows']]
            result['corpora'][key][f'hook_recall_at{k}'] = sum(
                kos20.recall_at(h, r['gold'], k) for r, h in zip(rows, actual) if r['gold']) / len(scored)
        result['corpora'][key]['hook_negative_injections'] = sum(
            bool(d['hook']) for r, d in zip(rows, diagnostics['rows']) if not r['gold'])
        if args.phase == 'final':
            assert all([h['name'] for h in d['ranked']
                        if accept(('scaled-linear', 1.0), h, d, diagnostics['documents'])] == d['hook']
                       for d in diagnostics['rows']), 'Measured rule and shipped Hook disagree'
        assert before == manifest(vault), 'corpus changed during measurement'
    rules = [('fixed', 1.0), ('relative', 0.5), ('relative', 0.25),
             ('scaled-linear', 1.0), ('scaled-log', 1.0), ('ratio', 0.5),
             ('fixed', 0.5), ('fixed', 0.25), ('fixed', 0.1), ('fixed', 0.0)]
    for rule in rules:
        entry = {'rule': rule}
        for key, rows in [('vault', gold), ('slice', slice_rows)]:
            entry[key] = {str(k): score_gate(rows, result['corpora'][key]['diagnostics'], rule, k) for k in [3, 5]}
            entry[key]['gate5'] = {str(k): result['corpora'][key][f'query_recall_at{k}'] for k in [3, 5]}
        result['candidates'].append(entry)
        v, s = entry['vault'], entry['slice']
        print(rule, 'hook', v['3']['correct']/125, v['5']['correct']/125,
              'slice', s['3']['correct'], 'neg', v['5']['negative_injections'], s['5']['negative_injections'])
    out = WORK / f'{args.phase}.json'
    kos20.write_json(out, result)
    print(out)
    if args.phase == 'final':
        baseline = json.loads((WORK / 'baseline.json').read_text(encoding='utf-8'))
        for key in ('vault', 'slice'):
            assert baseline['corpora'][key]['manifest_sha256'] == result['corpora'][key]['manifest_sha256']
            assert baseline['corpora'][key]['query_rankings'] == result['corpora'][key]['query_rankings'], 'Ranking changed'
        assert result['corpora']['vault']['query_recall_at3'] >= .80
        assert result['corpora']['vault']['query_recall_at5'] >= .88
        assert result['corpora']['slice']['hook_recall_at3'] == 1
        assert all(c['hook_negative_injections'] == 0 for c in result['corpora'].values())
        combined = {'yazan': 'codex', 'model': 'gpt-6',
                    'selected_rule': 'mean >= strictScore * min(1, N / 542); identity overlap >= minOverlap',
                    'metric_note': 'gate5 is gateless Query recall (existing kos20 contract); hook recall is separate',
                    'baseline': baseline, 'final': result}
        destination = ROOT / 'bench/results/recall-2026-09-10-gate.json'
        kos20.write_json(destination, combined)
        print(destination)


if __name__ == '__main__':
    main()
