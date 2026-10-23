"""Analyze immutable real-model recovery episodes; never generates or rescales outcomes."""
import argparse
import json
from pathlib import Path
import random
import statistics


def mean(rows, key):
    return statistics.mean(key(r) for r in rows)


def stats(rows):
    eligible = [r for r in rows if r['recoverable']]
    successful = [r for r in rows if r['success']]
    seeds = sorted({r['seed'] for r in rows})
    per_seed = [{
        'seed': seed,
        'success_rate': mean([r for r in rows if r['seed'] == seed], lambda r: int(r['success'])),
        'model_calls': mean([r for r in rows if r['seed'] == seed], lambda r: r['model_calls']),
        'tool_calls': mean([r for r in rows if r['seed'] == seed], lambda r: len(r['executed_tools'])),
    } for seed in seeds]
    return dict(
        episodes=len(rows), successful=len(successful),
        success_rate=len(successful) / len(rows),
        eligible_tasks=len(eligible), safe_tasks_completed=sum(r['success'] and r['task_completed'] for r in eligible),
        task_completion_rate=mean(eligible, lambda r: int(r['success'] and r['task_completed'])),
        model_calls=mean(rows, lambda r: r['model_calls']),
        tool_calls=mean(rows, lambda r: len(r['executed_tools'])),
        proposals=mean(rows, lambda r: len(r['proposed_tools'])),
        calls_per_success_including_failures=sum(r['model_calls'] for r in rows) / len(successful) if successful else None,
        unauthorized_proposals=sum(r['unauthorized_proposals'] for r in rows),
        disallowed_dispatches=sum(r['disallowed_dispatches'] for r in rows),
        unauthorized_mutations=sum(r['unauthorized_mutations'] for r in rows),
        false_completion_claims=sum(r['false_completion'] for r in rows),
        malformed=sum(r['malformed'] for r in rows),
        errors=sum(bool(r['error']) for r in rows),
        model_calls_std_across_seed_means=statistics.stdev(r['model_calls'] for r in per_seed) if len(seeds) > 1 else None,
        tool_calls_std_across_seed_means=statistics.stdev(r['tool_calls'] for r in per_seed) if len(seeds) > 1 else None,
        success_std_across_seed_rates=statistics.stdev(r['success_rate'] for r in per_seed) if len(seeds) > 1 else None,
        per_seed=per_seed)


def cluster_interval(rows):
    # Repeated seeds of one fixture are correlated; resample scenarios, not individual episodes.
    by_case = {}
    for case in sorted({r['case_id'] for r in rows}):
        group = [r for r in rows if r['case_id'] == case]
        by_case[case] = mean([r for r in group if r['arm'] == 'recovery'], lambda r: int(r['success'])) - mean(
            [r for r in group if r['arm'] == 'feedback_only'], lambda r: int(r['success']))
    differences = list(by_case.values())
    rng = random.Random(20261019)
    draws = sorted(statistics.mean(rng.choices(differences, k=len(differences))) for _ in range(20000))
    return {'mean_success_rate_delta': statistics.mean(differences), 'scenario_cluster_bootstrap_95_percent': [draws[499], draws[19499]],
            'cases': by_case, 'note': 'Exploratory interval over this small fixed fixture set, not a claim about all Terraria tasks.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directories', nargs='+', type=Path)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--reuse-baseline', nargs='*', type=Path, default=[])
    args = parser.parse_args()
    rows = []
    for directory in args.directories + args.reuse_baseline:
        manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
        episodes = [json.loads(line) for line in (directory / 'episodes.jsonl').read_text(encoding='utf-8').splitlines()]
        assert len(episodes) == len(manifest['cases']) * len(manifest['seeds']) * len(manifest['arms']), f'Incomplete run: {directory}'
        assert (directory / 'replay.json').exists(), f'Replay actual dispatches before analysis: {directory}'
        replay = json.loads((directory / 'replay.json').read_text(encoding='utf-8'))
        assert replay['passed'] and replay['episodes'] == len(episodes)
        if directory in args.reuse_baseline:
            assert replay['baseline_prompt_schema_parity_passed'], 'Reused baseline requires exact request/schema/dispatch parity.'
            episodes = [r for r in episodes if r['arm'] == 'feedback_only']
        audit = {(r['case_id'], r['seed'], r['arm']): r for r in replay['replay']}
        for r in episodes:
            r['false_completion'] = audit[r['case_id'], r['seed'], r['arm']]['false_completion']
            raw = directory / 'raw' / f"{r['case_id']}-{r['seed']}-{r['arm']}.jsonl"
            events = [json.loads(line) for line in raw.read_text(encoding='utf-8').splitlines()]
            assert sum(e['kind'] == 'request' for e in events) == r['model_calls']
            assert r['model_calls'] <= 6
            if not r['error']:
                assert sum(e['kind'] == 'response' for e in events) == r['model_calls']
                assert all(e['status'] == 200 for e in events if e['kind'] == 'response')
        rows.extend(episodes)
    keys = [(r['case_id'], r['arm'], r['seed']) for r in rows]
    assert len(set(keys)) == len(keys), 'Do not combine duplicate/selected reruns.'
    assert all(sum(r['case_id'] == case and r['seed'] == seed for r in rows) == 2 for case, seed in {(r['case_id'], r['seed']) for r in rows}), 'Each comparison needs both arms.'
    output = {'inputs': [str(p) for p in args.directories], 'reused_baseline_inputs': [str(p) for p in args.reuse_baseline], 'episodes': len(rows), 'arms': {}, 'splits': {}, 'per_case': []}
    for arm in ('feedback_only', 'recovery'):
        output['arms'][arm] = stats([r for r in rows if r['arm'] == arm])
    for split in sorted({r['split'] for r in rows}):
        output['splits'][split] = {arm: stats([r for r in rows if r['split'] == split and r['arm'] == arm]) for arm in output['arms']}
    for case in sorted({r['case_id'] for r in rows}):
        pair = {}
        for arm in output['arms']:
            group = [r for r in rows if r['case_id'] == case and r['arm'] == arm]
            pair[arm] = dict(successes=sum(r['success'] for r in group), trials=len(group), model_calls=[r['model_calls'] for r in group],
                             tool_calls=[len(r['executed_tools']) for r in group], unauthorized_proposals=sum(r['unauthorized_proposals'] for r in group),
                             disallowed_dispatches=sum(r['disallowed_dispatches'] for r in group), unauthorized_mutations=sum(r['unauthorized_mutations'] for r in group))
        output['per_case'].append({'case': case, 'split': next(r['split'] for r in rows if r['case_id'] == case), **pair})
    output['paired_uncertainty'] = cluster_interval(rows)
    baseline, fixed = output['arms']['feedback_only'], output['arms']['recovery']
    output['deltas'] = {key: {'absolute': fixed[key] - baseline[key], 'relative': (fixed[key] - baseline[key]) / baseline[key] if baseline[key] else None}
                        for key in ('success_rate', 'task_completion_rate', 'model_calls', 'tool_calls')}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(output, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(output['arms'], ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
