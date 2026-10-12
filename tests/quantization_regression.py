"""Compare reviewed MIDI profiles without rendering or modifying their source files."""
from pathlib import Path
import argparse, hashlib, json, os, sys, tempfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreQuantizationRegression'))
import batch_score as engine
from inspect_midi import parse_midi


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode('utf-8')).hexdigest()


def snapshot(midi_directory):
    profiles = json.loads((ROOT / 'engine/profiles.json').read_text(encoding='utf-8'))
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
                            collisions=statistics['quantized_attack_collisions']))
        evidence[expected_hash] = dict(source_notes=parsed['notes'], events=events, statistics=statistics)
    return results, evidence


def verify_correction(previous, current, expected, proof, evidence):
    if current != expected:
        raise AssertionError(f'Corrected output changed beyond its reviewed fingerprint: {current["filename"]}')
    increase = current['event_count']-previous['event_count']
    if increase != proof['expected_attack_increase'] or increase != len(proof['restored_attacks']):
        raise AssertionError(f'Unexpected attack count: {current["filename"]}')
    if current['collisions'] != 0 or previous['collisions'] != increase:
        raise AssertionError(f'Unexpected collision delta: {current["filename"]}')
    if evidence['statistics'].get('boundary_grid_refinements') != proof['boundary_grid_refinements']:
        raise AssertionError(f'Changed grids beyond the documented beats: {current["filename"]}')
    for restored in proof['restored_attacks']:
        source = restored['source']
        if not any(all(note.get(k)==v for k,v in source.items()) for note in evidence['source_notes']):
            raise AssertionError(f'Restored attack is absent from original MIDI: {source}')
        if restored['expected_event'] not in evidence['events']:
            raise AssertionError(f'Restored attack timing differs: {restored["expected_event"]}')
    return increase


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    input_group = parser.add_mutually_exclusive_group(required=True)
    input_group.add_argument('--midi-directory', type=Path)
    input_group.add_argument('--project', type=Path, help='Music project containing the midis directory.')
    parser.add_argument('--baseline', type=Path, default=ROOT/'tests/fixtures/quantization_reviewed_baseline.json')
    parser.add_argument('--report', type=Path)
    parser.add_argument('--create-baseline', action='store_true')
    args = parser.parse_args()
    results, evidence = snapshot(args.midi_directory or args.project/'midis')
    if args.create_baseline:
        args.baseline.parent.mkdir(parents=True, exist_ok=True)
        args.baseline.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
        print(f'Baseline saved for {len(results)} reviewed profiles.')
    else:
        previous = json.loads(args.baseline.read_text(encoding='utf-8-sig'))
        expected = json.loads((ROOT/'tests/fixtures/quantization_reviewed_expected.json').read_text(encoding='utf-8-sig'))
        proofs = json.loads((ROOT/'tests/fixtures/quantization_restored_attacks.json').read_text(encoding='utf-8'))
        previous_map = {item['source_sha256']:item for item in previous}
        expected_map = {item['source_sha256']:item for item in expected}
        proof_map = {item['source_sha256']:item for item in proofs}
        changes, corrections, failures = [], [], []
        restored_count = 0
        for item in results:
            key = item['source_sha256']
            if key in expected_map:
                try:
                    increase = verify_correction(previous_map[key],item,expected_map[key],proof_map[key],evidence[key])
                    restored_count += increase
                    corrections.append(dict(filename=item['filename'],restored_attacks=increase))
                except AssertionError as error:
                    failures.append(str(error))
            elif item != previous_map.get(key):
                changes.append(item['filename'])
        report = dict(profile_count=len(results), unchanged=len(results)-len(changes)-len(corrections)-len(failures),
                      documented_corrections=corrections, restored_attacks=restored_count,
                      unexpected_changes=changes, failures=failures,
                      same_profiles=set(previous_map) == {p['source_sha256'] for p in results},results=results)
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps({k:v for k,v in report.items() if k!='results'}, ensure_ascii=False))
        if changes or failures or not report['same_profiles'] or len(corrections)!=len(expected):
            raise SystemExit(1)
