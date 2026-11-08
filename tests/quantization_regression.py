"""Compare reviewed MIDI profiles without rendering or modifying their source files.

The criterion is musical, not byte-identical: no metric of a reviewed profile may get worse than its
recorded value (tests/accuracy/quantization_metrics_baseline.json), and the attacks restored by the
documented corrections must still be present. A correction in the engine therefore passes; event,
grid and statistics hashes are still computed and reported, but only as information.
"""
from pathlib import Path
import argparse, bisect, hashlib, json, os, sys, tempfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreQuantizationRegression'))
import batch_score as engine
from inspect_midi import parse_midi

METRICS_BASELINE = ROOT / 'tests/accuracy/quantization_metrics_baseline.json'
ONSET_TOLERANCE = .25  # 1/16, in quarters (strictly smaller distance)
# Metric -> (direction, tolerance). +1: higher is better; -1: lower is better.
METRIC_RULES = {'attack_recall': (1, .001), 'attack_precision': (1, .001), 'pitch_fidelity': (1, .001),
                'restored_attacks_present': (1, 0), 'onset_error_mean': (-1, .002), 'collisions': (-1, 0)}


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode('utf-8')).hexdigest()


def _near(sorted_values, value):
    """Distance from value to the nearest item of a sorted list."""
    index = bisect.bisect_left(sorted_values, value)
    options = [abs(sorted_values[i] - value) for i in (index - 1, index) if 0 <= i < len(sorted_values)]
    return min(options) if options else float('inf')


def _clustered(onsets, tolerance):
    """Distinct attacks: onsets closer than the engine's onset cluster are one attack."""
    result = []
    for onset in sorted(onsets):
        if not result or onset - result[-1] > tolerance:
            result.append(onset)
    return result


def _matched(expected, written):
    """One-to-one pairs closer than 1/16 (greedy by distance): two attacks never share one written note."""
    pairs = []
    for i, quarter in enumerate(expected):
        low = bisect.bisect_left(written, quarter - ONSET_TOLERANCE)
        high = bisect.bisect_right(written, quarter + ONSET_TOLERANCE)
        pairs += [(abs(written[j] - quarter), i, j) for j in range(low, high) if abs(written[j] - quarter) < ONSET_TOLERANCE]
    used_expected, used_written = set(), set()
    for _, i, j in sorted(pairs):
        if i not in used_expected and j not in used_written:
            used_expected.add(i); used_written.add(j)
    return len(used_expected)


def profile_metrics(parsed, config, origin, end, events, statistics, proof=None):
    """Fidelity of the written attacks to the reviewed sources (no hashes: a correction may improve them)."""
    ppq = parsed['ppq']
    graces = config.get('grace_notes', [])
    def accepted(note, sources):
        return (any(engine._source_accepts(note, source, ppq) for source in sources)
                and (not config.get('velocity_filter') or note['velocity'] in config['velocity_filter'])
                and not any(abs(note['start'] / ppq - g['start_quarter']) < .001 and note['pitch'] == g['pitch'] for g in graces))
    span = lambda quarter: origin / engine.DIV <= quarter < end / engine.DIV
    main = [n for n in parsed['notes'] if accepted(n, config['melody_sources']) and span(n['start'] / ppq)]
    every = [n for n in parsed['notes'] if accepted(n, config['melody_sources'] + config.get('solo_sources', [])) and span(n['start'] / ppq)]
    cluster = config.get('onset_cluster_quarters', .04)
    expected = _clustered({n['start'] / ppq for n in main}, cluster)
    sources = _clustered({n['start'] / ppq for n in every}, cluster)
    written = sorted((e['start'] + origin) / engine.DIV for e in events)
    # Attacks the engine masks on purpose as an inner voice of the same source are not melody attacks.
    masked = statistics.get('masked_inner_attacks', 0)
    recall = min(1., _matched(expected, written) / max(1, len(expected) - masked)) if expected else 1.
    precision = _matched(sources, written) / len(written) if written else 1.
    pitched = sum(any(n['pitch'] == e['pitch'] and abs(n['start'] / ppq - (e['start'] + origin) / engine.DIV) < ONSET_TOLERANCE
                      for n in every) for e in events)
    errors = [_near(sources, q) for q in written]
    restored = 1.
    if proof:
        found = [any(e['pitch'] == item['expected_event']['pitch'] and abs(e['start'] - item['expected_event']['start']) < ONSET_TOLERANCE * engine.DIV
                     for e in events) for item in proof['restored_attacks']]
        restored = sum(found) / len(found) if found else 1.
    return {'attack_recall': round(recall, 4), 'attack_precision': round(precision, 4),
            'pitch_fidelity': round(pitched / len(events), 4) if events else 1.,
            'restored_attacks_present': round(restored, 4),
            'onset_error_mean': round(sum(errors) / len(errors), 4) if errors else 0.,
            'collisions': statistics['quantized_attack_collisions']}


def snapshot(midi_directory, proofs=None):
    profiles = json.loads((ROOT / 'engine/profiles.json').read_text(encoding='utf-8'))
    proofs = proofs or {}
    results = []
    evidence = {}
    for expected_hash, profile in profiles.items():
        config = profile['config']
        if config['status'] != 'ready':
            continue
        path = midi_directory / config['filename']
        parsed = parse_midi(path)
        if parsed['sha256'] != expected_hash:
            raise AssertionError(f'Original MIDI differs from reviewed profile: {path.name}')
        _, meters, _ = engine.meta(parsed)
        origin, end, _ = engine.make_bars(parsed, meters)
        events, grids, statistics = engine.melody_events(parsed, config, origin, end)
        if hashlib.sha256(path.read_bytes()).hexdigest() != expected_hash:
            raise AssertionError(f'Original MIDI changed during inspection: {path.name}')
        results.append(dict(filename=path.name, source_sha256=expected_hash,
                            event_count=len(events), events_sha256=digest(events),
                            grids_sha256=digest(grids), statistics_sha256=digest(statistics),
                            collisions=statistics['quantized_attack_collisions'],
                            metrics=profile_metrics(parsed, config, origin, end, events, statistics, proofs.get(expected_hash))))
        evidence[expected_hash] = dict(source_notes=parsed['notes'], events=events, statistics=statistics)
    return results, evidence


def compare_metrics(previous, current):
    """(regressions, improvements) of one profile; a metric absent from the baseline is not compared."""
    regressions, improvements = [], []
    for name, (direction, tolerance) in METRIC_RULES.items():
        if name not in previous or name not in current:
            continue
        delta = (current[name] - previous[name]) * direction
        item = dict(metric=name, baseline=previous[name], current=current[name])
        if delta < -tolerance - 1e-12:
            regressions.append(item)
        elif delta > tolerance + 1e-12:
            improvements.append(item)
    return regressions, improvements


HASH_FIELDS = ('event_count', 'events_sha256', 'grids_sha256', 'statistics_sha256', 'collisions')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    input_group = parser.add_mutually_exclusive_group(required=True)
    input_group.add_argument('--midi-directory', type=Path)
    input_group.add_argument('--project', type=Path, help='Music project containing the midis directory.')
    parser.add_argument('--baseline', type=Path, default=ROOT/'tests/fixtures/quantization_reviewed_baseline.json')
    parser.add_argument('--metrics-baseline', type=Path, default=METRICS_BASELINE)
    parser.add_argument('--report', type=Path)
    parser.add_argument('--create-baseline', action='store_true', help='record hashes and metrics of the current engine')
    parser.add_argument('--update-metrics', action='store_true', help='record only the metrics of the current engine')
    args = parser.parse_args()
    proofs = json.loads((ROOT/'tests/fixtures/quantization_restored_attacks.json').read_text(encoding='utf-8'))
    proof_map = {item['source_sha256']:item for item in proofs}
    results, evidence = snapshot(args.midi_directory or args.project/'midis', proof_map)
    if args.create_baseline or args.update_metrics:
        if args.create_baseline:
            args.baseline.parent.mkdir(parents=True, exist_ok=True)
            args.baseline.write_text(json.dumps([{k:v for k,v in item.items() if k!='metrics'} for item in results], ensure_ascii=False, indent=2), encoding='utf-8')
        args.metrics_baseline.parent.mkdir(parents=True, exist_ok=True)
        args.metrics_baseline.write_text(json.dumps({item['source_sha256']:dict(filename=item['filename'], **item['metrics']) for item in results},
                                                    ensure_ascii=False, indent=1) + '\n', encoding='utf-8')
        print(f'Baseline saved for {len(results)} reviewed profiles.')
    else:
        previous = json.loads(args.baseline.read_text(encoding='utf-8-sig'))
        expected = json.loads((ROOT/'tests/fixtures/quantization_reviewed_expected.json').read_text(encoding='utf-8-sig'))
        recorded = json.loads(args.metrics_baseline.read_text(encoding='utf-8')) if args.metrics_baseline.is_file() else {}
        previous_map = {item['source_sha256']:item for item in previous}
        # Hash fingerprints: the documented corrections replaced the old ones; both are information only.
        fingerprints = dict(previous_map)
        fingerprints.update({item['source_sha256']:item for item in expected})
        hash_changes, regressions, improvements, failures = [], [], [], []
        corrections = []
        for item in results:
            key = item['source_sha256']
            if {k:item[k] for k in HASH_FIELDS} != {k:fingerprints.get(key, {}).get(k) for k in HASH_FIELDS}:
                hash_changes.append(item['filename'])
            worse, better = compare_metrics(recorded.get(key, {}), item['metrics'])
            regressions += [dict(filename=item['filename'], **row) for row in worse]
            improvements += [dict(filename=item['filename'], **row) for row in better]
            if key in proof_map:
                corrections.append(dict(filename=item['filename'], restored_attacks=len(proof_map[key]['restored_attacks']),
                                        present=item['metrics']['restored_attacks_present']))
                if item['metrics']['restored_attacks_present'] < 1:
                    failures.append(f'Restored attack missing again: {item["filename"]}')
        same_profiles = set(previous_map) == {p['source_sha256'] for p in results}
        report = dict(profile_count=len(results), unchanged=len(results)-len(hash_changes),
                      hash_changes_information_only=hash_changes, documented_corrections=corrections,
                      metric_regressions=regressions, metric_improvements=improvements,
                      profiles_without_metrics=[p['filename'] for p in results if p['source_sha256'] not in recorded],
                      failures=failures, same_profiles=same_profiles, results=results)
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps({k:v for k,v in report.items() if k!='results'}, ensure_ascii=False))
        if regressions or failures or not same_profiles:
            raise SystemExit(1)
