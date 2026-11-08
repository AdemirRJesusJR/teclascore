"""Mede a precisão do motor sobre o corpus sintético (ou sobre MIDIs reais) e grava accuracy-report.json.

    python tests/accuracy/run_accuracy.py --baseline tests/accuracy/baseline.json
    python tests/accuracy/run_accuracy.py --baseline tests/accuracy/baseline.json --update-baseline
    python tests/accuracy/run_accuracy.py --real            # MIDIs do usuário; não roda no CI

Cada caso roda o worker de verdade (analyze + generate) num processo separado; sem --native o
MuseScore é substituído (veja engine_driver.py), então a medição é rápida e não precisa dele.
Com --baseline, falha (código 1) se alguma métrica piorar além da tolerância e lista as que melhoraram.
"""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
import make_corpus  # noqa: E402
import metrics  # noqa: E402

SCHEMA = 1
DEFAULT_BASELINE = HERE / 'baseline.json'
DEFAULT_MUSESCORE = r'C:\Program Files\MuseScore 4\bin\MuseScore4.exe'
DEFAULT_REAL = r'P:\2. Documentos\4. Teclado\2. MIDI'
DEFAULT_OUTPUTS = Path(os.path.expanduser('~')) / 'Documents' / 'Partituras geradas'


def default_work():
    return Path(tempfile.gettempdir()) / 'TeclaScoreAccuracy'


def git_commit():
    """Último commit que mudou o motor (engine/), com '+' se houver mudanças não gravadas nele."""
    try:
        result = subprocess.run(['git', 'log', '-1', '--format=%h', '--', 'engine'], cwd=ROOT, capture_output=True, text=True, timeout=10)
        dirty = subprocess.run(['git', 'status', '--porcelain', '--', 'engine'], cwd=ROOT, capture_output=True, text=True, timeout=10)
        if result.stdout.strip() and dirty.stdout.strip():
            return result.stdout.strip() + '+'
        return result.stdout.strip() or None
    except (OSError, subprocess.SubprocessError):
        return None


# ----------------------------------------------------------------------------- execução do motor
def run_engine(midi, sources, workspace, native=None, timeout=600, extra_generate=None):
    """analyze + generate num processo próprio. Devolve (saídas, mensagens do worker)."""
    if workspace.exists():
        shutil.rmtree(workspace)
    workspace.mkdir(parents=True)
    analyze = {'mode': 'analyze', 'workspace': str(workspace), 'inputMidi': str(midi), 'useReviewedProfile': False,
               'melodySources': sources, 'melodySections': []}
    generate = {'mode': 'generate', 'workspace': str(workspace), 'inputMidi': str(midi),
                'analysisPath': str(workspace / 'analysis.json'),
                # Sem --native o executável não é chamado; o worker só confere que o arquivo existe.
                'musescorePath': str(native or sys.executable), 'outputDirectory': str(workspace / 'saida')}
    generate.update(extra_generate or {})
    paths = []
    for name, request in (('request-analyze.json', analyze), ('request-generate.json', generate)):
        path = workspace / name
        path.write_text(json.dumps(request, ensure_ascii=False, indent=1), encoding='utf-8')
        paths.append(str(path))
    command = [sys.executable, '-B', str(HERE / 'engine_driver.py'), *paths] + (['--native'] if native else [])
    started = time.perf_counter()
    try:
        process = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', errors='replace',
                                 timeout=timeout, cwd=str(workspace))
        stdout, stderr, code = process.stdout, process.stderr, process.returncode
    except subprocess.TimeoutExpired as error:
        stdout, stderr, code = error.stdout or '', 'tempo esgotado', -1
        stdout = stdout.decode('utf-8', 'replace') if isinstance(stdout, bytes) else stdout
    seconds = time.perf_counter() - started
    messages = []
    for line in stdout.splitlines():
        try:
            messages.append(json.loads(line))
        except ValueError:
            continue
    outputs = {'seconds': round(seconds, 3), 'returncode': code, 'stderr_tail': stderr[-2000:] if code else '',
               'messages': [m for m in messages if m.get('kind') in ('result', 'error', 'timing')]}
    work = workspace / 'work' / '001'
    for key, path in (('musicxml', work / 'melodia.musicxml'), ('validation', work / 'validacao.json'),
                      ('analysis', workspace / 'analysis.json')):
        outputs[key] = path if path.is_file() else None
    return outputs


def summarize_messages(outputs):
    errors = [m for m in outputs['messages'] if m.get('kind') == 'error']
    results = [m for m in outputs['messages'] if m.get('kind') == 'result']
    timings = {m['mode']: m['seconds'] for m in outputs['messages'] if m.get('kind') == 'timing'}
    analysis = results[0]['data'] if results else {}
    return errors, analysis, timings


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig')) if path else None


# ----------------------------------------------------------------------------- corpus sintético
def evaluate_case(truth_path, work, native):
    truth = read_json(truth_path)
    midi = Path(truth_path).with_name(truth['midi'])
    outputs = run_engine(midi, truth['melody_sources'], work / 'casos' / truth['id'], native)
    errors, analysis, timings = summarize_messages(outputs)
    validation = read_json(outputs['validation']) if outputs['validation'] else None
    score = None
    status = 'ok'
    error = None
    if outputs['musicxml'] and validation is not None and not errors:
        score = metrics.read_musicxml(outputs['musicxml'])
    else:
        status = 'erro'
        error = errors[0]['message'] if errors else (outputs['stderr_tail'] or 'sem MusicXML gerado')
    origin = validation.get('origin_removed_quarters') if validation else None
    values, details = metrics.evaluate(truth, score, origin)
    details.update(seconds=outputs['seconds'], timings=timings,
                   review_points=len(analysis.get('reviewPoints', [])) if isinstance(analysis, dict) else None,
                   rhythm_ready=analysis.get('rhythmReady') if isinstance(analysis, dict) else None)
    if validation:
        lyrics = validation.get('melody_analysis', {}).get('lyrics')
        if isinstance(lyrics, dict):
            details['lyrics_engine'] = {k: lyrics[k] for k in ('syllables', 'attached') if k in lyrics}
    case = {'status': status, 'focus': truth.get('focus', []), 'description': truth.get('description', ''),
            'metrics': values, 'details': details}
    if error:
        case['error'] = error[:500]
    return truth['id'], case


def run_synthetic(args):
    work = Path(args.work)
    corpus = Path(args.corpus) if args.corpus else work / 'corpus'
    if not args.corpus:
        if corpus.exists():
            shutil.rmtree(corpus)
        make_corpus.create(corpus, set(args.case or []))
    manifest = read_json(corpus / 'manifest.json')
    truths = sorted(p for p in corpus.glob('*.json') if p.name != 'manifest.json'
                    and (not args.case or p.stem in args.case))
    if not truths:
        raise SystemExit('Corpus vazio: ' + str(corpus))
    native = args.native if args.native else None
    with ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        results = dict(pool.map(lambda path: evaluate_case(path, work, native), truths))
    cases = {key: results[key] for key in sorted(results)}
    fingerprint = hashlib.sha256(json.dumps(manifest['cases'], sort_keys=True).encode('utf-8')).hexdigest()
    report = {'schema': SCHEMA, 'kind': 'synthetic', 'engine_commit': git_commit(), 'corpus_fingerprint': fingerprint,
              'native_musescore': bool(native), 'tolerance': args.tolerance,
              'metric_descriptions': metrics.DESCRIPTIONS, 'lower_is_better': sorted(metrics.LOWER_IS_BETTER),
              'cases': cases, 'averages': metrics.averages([c['metrics'] for c in cases.values()]),
              'errors': sorted(k for k, c in cases.items() if c['status'] != 'ok'),
              'seconds': round(sum(c['details']['seconds'] for c in cases.values()), 2)}
    return report, manifest


def baseline_document(report, manifest):
    return {'schema': SCHEMA, 'engine_commit': report['engine_commit'], 'corpus_fingerprint': report['corpus_fingerprint'],
            'corpus_cases': manifest['cases'], 'tolerance': report['tolerance'],
            'cases': {key: {'status': case['status'], 'metrics': case['metrics']} for key, case in report['cases'].items()},
            'averages': report['averages']}


# ----------------------------------------------------------------------------- corpus real (opcional)
def local_app_data():
    return Path(os.environ.get('LOCALAPPDATA', Path.home() / 'AppData' / 'Local')) / 'TeclasPartituras'


def reference_score(item, outputs, any_reference=False):
    """Partitura aprovada da biblioteca (ou, com any_reference, a última gerada); procura também na pasta de saída."""
    if not item.get('Approved') and not any_reference:
        return None, None
    score = item.get('ScorePath')
    if not score:
        return None, None
    path = Path(score)
    if not path.is_file() and (Path(outputs) / path.name).is_file():
        path = Path(outputs) / path.name
    return (str(path), 'aprovada' if item.get('Approved') else 'gerada (não aprovada)') if path.is_file() else (None, None)


def real_candidates(directory, limit, outputs=DEFAULT_OUTPUTS, any_reference=False):
    """MIDIs reais com a escolha de pistas do usuário: biblioteca do app, depois requisições de jobs antigos."""
    directory = Path(directory)
    chosen = {}
    settings_path = local_app_data() / 'settings.json'
    if settings_path.is_file():
        try:
            library = read_json(settings_path).get('Library', [])
        except (OSError, ValueError):
            library = []
        for item in library:
            path = item.get('FullPath')
            if not path or item.get('ChordsOnly') or not item.get('MelodySources'):
                continue
            versions = item.get('Versions') or []
            approved_score, kind = reference_score(item, outputs, any_reference)
            sources = [{'track': s['Track'], 'channel': s['Channel'], 'program': s['Program']} for s in item['MelodySources']]
            chosen[str(Path(path)).lower()] = {'midi': path, 'sources': sources, 'reference': approved_score,
                                               'reference_kind': kind,
                                               'origin': 'biblioteca', 'versions': len(versions)}
    jobs = local_app_data() / 'jobs'
    if jobs.is_dir():
        requests = sorted(jobs.glob('*/request-*.json'), key=lambda p: p.stat().st_mtime)
        for request_path in requests:
            try:
                request = read_json(request_path)
            except (OSError, ValueError):
                continue
            if request.get('mode') != 'analyze' or request.get('chordsOnly') or not request.get('melodySources'):
                continue
            key = str(Path(request['inputMidi'])).lower()
            if key in chosen and chosen[key]['origin'] == 'biblioteca':
                continue
            chosen[key] = {'midi': request['inputMidi'], 'sources': request['melodySources'], 'reference': None,
                           'origin': 'job ' + request_path.parent.name}
    root = str(directory).lower().rstrip('\\/')
    selected = [value for key, value in sorted(chosen.items())
                if key.startswith(root) and Path(value['midi']).is_file()]
    return selected[:limit] if limit else selected


def export_reference(score_path, destination, musescore):
    destination.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run([musescore, '-o', str(destination), str(score_path)], capture_output=True, timeout=300)
    return destination if destination.is_file() else None


def truth_from_score(score, name):
    """Gabarito a partir de uma partitura aprovada (linha do tempo da partitura)."""
    notes = [{'onset': n['onset'], 'duration': n['duration'], 'pitch': n['pitch'], 'step': n['step'],
              'alter': n['alter'], 'octave': n['octave']} for n in score['notes']]
    end = max((m['start'] + m['length'] for m in score['measures']), default=0)
    chords = []
    for index, item in enumerate(score['harmonies']):
        stop = score['harmonies'][index + 1]['quarter'] if index + 1 < len(score['harmonies']) else end
        if stop <= item['quarter']:
            continue
        root = item['step'] + ('#' * item['alter'] if item['alter'] > 0 else 'b' * -item['alter'])
        bass = item['bass']
        chords.append({'onset': item['quarter'], 'end': stop, 'root': root, 'root_pc': item['pc'], 'quality': item['quality'],
                       'bass': None if not bass else bass['step'] + ('#' * bass['alter'] if bass['alter'] > 0 else 'b' * -bass['alter']),
                       'bass_pc': None if not bass or bass['pc'] == item['pc'] else bass['pc']})
    lyrics = []
    for note in score['notes']:
        for lyric in note['lyrics'][:1]:
            words = [w for w in lyric['text'].replace('‿', ' ').split() if w]
            for index, word in enumerate(words):
                lyrics.append({'onset': note['onset'], 'text': word, 'spoken': False,
                               'word_start': index > 0 or lyric.get('syllabic', 'single') in ('single', 'begin')})
    keys = score['keys']
    times = score['times']
    first = score['measures'][0]['length'] if score['measures'] else 0
    full = times[0]['beats'] * 4 / times[0]['beat_type'] if times else 4
    changes = []
    for previous, current in zip(keys, keys[1:]):
        if (previous['fifths'], previous['minor']) != (current['fifths'], current['minor']):
            changes.append({'quarter': current['quarter'], 'fifths': current['fifths'], 'minor': current['minor']})
    return {'id': name, 'melody': notes, 'chords': chords, 'lyrics': lyrics,
            'key': {'fifths': keys[0]['fifths'], 'minor': keys[0]['minor']} if keys else {'fifths': 0, 'minor': False},
            'key_changes': changes, 'meter': {'beats': times[0]['beats'], 'beat_type': times[0]['beat_type']} if times else {'beats': 4, 'beat_type': 4},
            'pickup_quarters': first if first < full - 1e-6 else 0, 'bar_quarters': full}


def indicators_without_reference(score, validation, analysis, midi_path):
    """Indicadores sem gabarito: letra encaixada, cobertura da letra pela melodia, revisões, colisões, tempo."""
    sys.path.insert(0, str(ROOT / 'engine'))
    from inspect_midi import parse_midi
    import batch_score
    parsed = parse_midi(midi_path)
    syllables = batch_score.karaoke_syllables(parsed)
    origin = (validation or {}).get('origin_removed_quarters', 0) or 0
    written = [token for note in score['notes'] for lyric in note['lyrics'][:1]
               for token in lyric['text'].replace('‿', ' ').split()]
    onsets = sorted(n['onset'] + origin for n in score['notes'])
    covered = 0
    for syllable in syllables:
        quarter = syllable['tick'] / parsed['ppq']
        if any(abs(onset - quarter) <= 1 for onset in onsets):
            covered += 1
    crowded = sum(1 for note in score['notes'] if note['lyrics'] and len(note['lyrics'][0]['text'].split()) >= 2)
    result = {'syllables_in_midi': len(syllables), 'syllables_written': len(written),
              'syllables_attached_fraction': round(min(1.0, len(written) / len(syllables)), 4) if syllables else None,
              'lyric_melody_coverage': round(covered / len(syllables), 4) if syllables else None,
              'review_points': len((analysis or {}).get('reviewPoints', [])),
              'lyric_collisions': crowded,
              'melody_notes': len(score['notes']), 'chords': len(score['harmonies'])}
    quality = (validation or {}).get('quality')
    if isinstance(quality, dict):
        result['quality_score'] = quality.get('score')
    return result


def run_real(args):
    work = Path(args.work) / 'real'
    candidates = real_candidates(args.real, args.real_limit, args.outputs, args.any_reference)
    musescore = args.native or (DEFAULT_MUSESCORE if Path(DEFAULT_MUSESCORE).is_file() else None)
    items = {}

    def measure(index_candidate):
        index, candidate = index_candidate
        name = f'{index + 1:03d} - ' + Path(candidate['midi']).stem[:60]
        outputs = run_engine(candidate['midi'], candidate['sources'], work / f'{index + 1:03d}', args.native or None)
        errors, analysis, timings = summarize_messages(outputs)
        entry = {'midi': candidate['midi'], 'origin': candidate['origin'], 'seconds': outputs['seconds'], 'timings': timings}
        if errors or not outputs['musicxml']:
            entry.update(status='erro', error=(errors[0]['message'] if errors else outputs['stderr_tail'])[:500])
            return name, entry
        score = metrics.read_musicxml(outputs['musicxml'])
        validation = read_json(outputs['validation'])
        entry['status'] = 'ok'
        entry['indicators'] = indicators_without_reference(score, validation, analysis, candidate['midi'])
        reference = candidate.get('reference')
        if reference and Path(reference).is_file() and musescore:
            exported = export_reference(reference, work / f'{index + 1:03d}' / 'referencia.musicxml', musescore)
            if exported:
                truth = truth_from_score(metrics.read_musicxml(exported), name)
                values, details = metrics.evaluate(truth, score, 0.0)
                entry.update(reference=str(reference), reference_kind=candidate.get('reference_kind'), metrics=values, details=details)
        return name, entry

    with ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        for name, entry in pool.map(measure, list(enumerate(candidates))):
            items[name] = entry
    indicator_names = ('syllables_attached_fraction', 'lyric_melody_coverage', 'review_points', 'lyric_collisions', 'quality_score')
    summary = {}
    for name in indicator_names:
        values = [e['indicators'][name] for e in items.values() if e.get('indicators', {}).get(name) is not None]
        summary[name] = round(sum(values) / len(values), 4) if values else None
    times = [e['seconds'] for e in items.values()]
    summary['seconds_mean'] = round(sum(times) / len(times), 3) if times else None
    referenced = [e['metrics'] for e in items.values() if 'metrics' in e]
    return {'schema': SCHEMA, 'kind': 'real', 'engine_commit': git_commit(), 'directory': str(args.real),
            'files': len(items), 'with_reference': len(referenced), 'errors': sorted(k for k, e in items.items() if e['status'] != 'ok'),
            'indicator_averages': summary, 'reference_averages': metrics.averages(referenced) if referenced else None,
            'items': items}


# ----------------------------------------------------------------------------- saída
def table(report):
    lines = ['métrica                 média']
    for name in metrics.METRICS:
        value = report['averages'].get(name)
        lines.append(f'{name:<22} {"-" if value is None else f"{value:.3f}"}')
    return '\n'.join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--baseline', type=Path, help='compara com esta linha de base (e falha se piorar)')
    parser.add_argument('--update-baseline', action='store_true', help='grava o resultado como nova linha de base')
    parser.add_argument('--output', type=Path, help='accuracy-report.json (padrão: na pasta de trabalho)')
    parser.add_argument('--work', type=Path, default=default_work(), help='pasta de trabalho temporária')
    parser.add_argument('--corpus', type=Path, help='usa um corpus já gerado (padrão: gera um novo)')
    parser.add_argument('--case', action='append', help='mede só este caso (pode repetir)')
    parser.add_argument('--tolerance', type=float, default=0.02, help='piora máxima tolerada por métrica (padrão 0,02)')
    parser.add_argument('--jobs', type=int, default=min(4, os.cpu_count() or 1))
    parser.add_argument('--native', metavar='MUSESCORE', help='gera de verdade com este MuseScore (lento)')
    parser.add_argument('--real', nargs='?', const=DEFAULT_REAL, help='mede MIDIs reais do usuário (não roda no CI)')
    parser.add_argument('--real-limit', type=int, default=12, help='quantos MIDIs reais medir (0 = todos)')
    parser.add_argument('--outputs', type=Path, default=DEFAULT_OUTPUTS, help='pasta de saída das partituras do usuário')
    parser.add_argument('--any-reference', action='store_true',
                        help='usa como referência também a última partitura gerada, mesmo sem aprovação')
    args = parser.parse_args(argv)
    args.work = args.work.resolve()
    if args.corpus:
        args.corpus = args.corpus.resolve()
    args.work.mkdir(parents=True, exist_ok=True)
    output = args.output or args.work / 'accuracy-report.json'
    if args.real:
        report = run_real(args)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding='utf-8')
        print(json.dumps({'report': str(output), 'files': report['files'], 'with_reference': report['with_reference'],
                          'errors': report['errors'], 'indicators': report['indicator_averages']}, ensure_ascii=False))
        return 0
    report, manifest = run_synthetic(args)
    status = 0
    baseline_path = args.baseline or (DEFAULT_BASELINE if args.update_baseline else None)
    if args.baseline and not args.update_baseline:
        baseline = read_json(args.baseline)
        regressions, improvements = metrics.compare(baseline, report, args.tolerance)
        report['comparison'] = {'baseline': str(args.baseline), 'baseline_commit': baseline.get('engine_commit'),
                                'corpus_changed': baseline.get('corpus_fingerprint') != report['corpus_fingerprint'],
                                'regressions': regressions, 'improvements': improvements}
        if args.case:
            # Medição parcial: casos fora da seleção não contam como ausentes, e as médias não são comparáveis.
            report['comparison']['regressions'] = [r for r in regressions if r['scope'] in args.case]
            report['comparison']['improvements'] = [r for r in improvements if r['scope'] in args.case]
        status = 1 if report['comparison']['regressions'] else 0
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding='utf-8')
    if args.update_baseline:
        if args.case:
            raise SystemExit('Atualize a linha de base com o corpus inteiro (sem --case).')
        baseline_path.write_text(json.dumps(baseline_document(report, manifest), ensure_ascii=False, indent=1) + '\n', encoding='utf-8')
    print(table(report))
    summary = {'report': str(output), 'cases': len(report['cases']), 'errors': report['errors'], 'seconds': report['seconds']}
    if 'comparison' in report:
        comparison = report['comparison']
        summary.update(regressions=len(comparison['regressions']), improvements=len(comparison['improvements']),
                       corpus_changed=comparison['corpus_changed'])
        for item in comparison['regressions']:
            print(f"PIOROU   {item['scope']:<28} {item['metric']:<20} {item['baseline']} -> {item['current']}")
        for item in comparison['improvements']:
            print(f"MELHOROU {item['scope']:<28} {item['metric']:<20} {item['baseline']} -> {item['current']}")
    if args.update_baseline:
        summary['baseline_updated'] = str(baseline_path)
    print(json.dumps(summary, ensure_ascii=False))
    return status


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    sys.exit(main())
