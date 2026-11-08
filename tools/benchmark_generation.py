"""Benchmark opcional da geração real: MIDI sintético → worker (analyze + generate) → tempos por etapa.

Precisa do MuseScore instalado; por isso não roda no CI. Exemplo (Python embutido do app):

    Aplicativo\\runtime\\python.exe -B tools\\benchmark_generation.py --measures 96 --repeat 2

Imprime o tempo de cada processo do worker e o campo ``timings`` do validacao.json
(análise, MusicXML, cada chamada ao MuseScore com o tipo, passes de espaçamento, análise de SVG,
prévia). Com ``--engine`` aponta para outra pasta engine/ (por exemplo, de outra versão) para
comparar; um worker antigo, sem ``timings``, mostra só o tempo total dos processos.
"""
from pathlib import Path
import argparse, json, os, struct, subprocess, sys, tempfile, time

ROOT = Path(__file__).resolve().parents[1]
PPQ = 480
MELODY = dict(track=1, channel=1, program=73)  # flauta
CHORDS = dict(track=2, channel=2, program=0)   # piano
PROGRESSION = [(60, 64, 67), (55, 59, 62), (57, 60, 64), (53, 57, 60)]  # C G Am F
# Frase de dois compassos em semínimas e colcheias, sempre na escala de dó.
PHRASE = [(72, 1), (74, .5), (76, .5), (77, 1), (76, 1), (74, 1.5), (72, .5), (71, 1), (72, 1)]


def vlq(value):
    data = [value & 127]; value >>= 7
    while value:
        data.insert(0, (value & 127) | 128); value >>= 7
    return bytes(data)


def track(events):
    body = b''; previous = 0
    for tick, _, message in sorted(events):
        body += vlq(tick - previous) + message; previous = tick
    body += b'\x00\xff\x2f\x00'
    return b'MTrk' + struct.pack('>I', len(body)) + body


def synthetic_midi(path, measures):
    """Melodia e acordes sintéticos em 4/4 a 100 bpm, ``measures`` compassos."""
    conductor = b'\x00\xff\x51\x03' + (600000).to_bytes(3, 'big') + b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    melody = [(0, 0, bytes([0xc0, MELODY['program']]))]; chords = [(0, 0, bytes([0xc1, CHORDS['program']]))]
    tick = 0
    while tick < measures * 4 * PPQ:
        for pitch, beats in PHRASE:
            length = round(beats * PPQ)
            melody += [(tick, 2, bytes([0x90, pitch, 90])), (tick + length - 20, 1, bytes([0x80, pitch, 0]))]
            tick += length
    for measure in range(measures):
        start = measure * 4 * PPQ
        for pitch in PROGRESSION[measure % len(PROGRESSION)]:
            chords += [(start, 2, bytes([0x91, pitch - 12, 70])), (start + 4 * PPQ - 20, 1, bytes([0x81, pitch - 12, 0]))]
    path.write_bytes(b'MThd' + struct.pack('>IHHH', 6, 1, 3, PPQ) + b'MTrk' + struct.pack('>I', len(conductor)) + conductor
                     + track(melody) + track(chords))


def run_worker(python, engine, request, workspace):
    workspace.mkdir(parents=True, exist_ok=True)
    path = workspace / f"request-{request['mode']}.json"
    path.write_text(json.dumps(request, ensure_ascii=False), encoding='utf-8')
    environment = dict(os.environ, PYTHONIOENCODING='utf-8', PARTITURAS_JOB=str(workspace / 'work'))
    start = time.perf_counter()
    run = subprocess.run([python, '-u', str(Path(engine) / 'worker.py'), str(path)], capture_output=True, text=True,
                         encoding='utf-8', errors='replace', env=environment, cwd=workspace)
    elapsed = time.perf_counter() - start
    messages = [json.loads(line) for line in run.stdout.splitlines() if line.startswith('{')]
    final = next((m for m in reversed(messages) if m.get('kind') in ('result', 'error')), None)
    if run.returncode or final is None or final['kind'] != 'result':
        raise SystemExit(f"Falha no modo {request['mode']}: {(final or {}).get('message') or run.stderr[-800:]}")
    return final['data'], elapsed


def print_timings(timings):
    if not timings:
        print('  (worker sem o campo timings)'); return
    for name, seconds in timings.get('stages', {}).items():print(f'  {name:<22}{seconds:8.2f} s')
    calls = {}
    for call in timings.get('musescore_calls', []):
        count, total = calls.get(call['kind'], (0, 0.)); calls[call['kind']] = (count + 1, total + call['seconds'])
    for kind, (count, total) in calls.items():print(f'  MuseScore {kind:<20}{count:3d}x {total:7.2f} s')
    print(f"  passes de espaçamento: {timings.get('spacing_passes', 0)}; conversões: {timings.get('conversions', 0)}")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--musescore', default=r'C:\Program Files\MuseScore 4\bin\MuseScore4.exe')
    parser.add_argument('--python', default=sys.executable)
    parser.add_argument('--engine', type=lambda value: str(Path(value).resolve()), default=str(ROOT / 'engine'))
    parser.add_argument('--measures', type=int, default=64)
    parser.add_argument('--repeat', type=int, default=1)
    parser.add_argument('--output', type=Path, help='pasta de trabalho (padrão: uma pasta temporária nova)')
    args = parser.parse_args()
    if not Path(args.musescore).is_file():raise SystemExit('MuseScore não encontrado; informe --musescore.')
    base = (args.output or Path(tempfile.mkdtemp(prefix='teclas-benchmark-'))).resolve()
    base.mkdir(parents=True, exist_ok=True)
    midi = base / 'sintetico.mid'; synthetic_midi(midi, max(4, args.measures))
    for index in range(args.repeat):
        workspace = base / f'job-{index + 1}'
        analysis, analyze_seconds = run_worker(args.python, args.engine, dict(mode='analyze', inputMidi=str(midi), workspace=str(workspace),
                                                                              melodySources=[MELODY]), workspace)
        result, generate_seconds = run_worker(args.python, args.engine, dict(
            mode='generate', inputMidi=str(midi), workspace=str(workspace), analysisPath=analysis['analysisPath'],
            outputDirectory=str(workspace / 'saida'), musescorePath=args.musescore), workspace)
        print(f"Execução {index + 1}: analyze {analyze_seconds:.2f} s, generate {generate_seconds:.2f} s, {result['pages']} páginas")
        print_timings(result.get('timings'))
    print(f'Arquivos em {base}')


if __name__ == '__main__':
    main()
