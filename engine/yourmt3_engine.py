"""As notas da gravação reconhecidas pelo YourMT3+ (Chang et al., 2024), o modelo de transcrição com vários
instrumentos que o TeclaScore usa como motor de áudio quando o componente está instalado (yourmt3_setup.py).

O modelo roda num processo à parte, no Python do componente (yourmt3_runner.py), e devolve as notas de cada
instrumento: voz cantada, baixo, bateria e os demais. Aqui elas viram as três partes de que o motor precisa: a linha
da voz, a linha do instrumento solista e o acompanhamento."""
from pathlib import Path
import json, os, subprocess, tempfile, threading
import yourmt3_setup as setup

RUNNER = Path(__file__).with_name('yourmt3_runner.py')
SINGING = (100,)                 # voz principal no vocabulário do modelo (101 é o coro)
CHORUS = (101,)
BASS = range(32, 40)
BASS_TOP = 52                    # Mi3: acima disso o "baixo" do modelo costuma ser a virada da bateria lida como nota
SHORTEST_VOICE = .07             # segundos: notas de voz mais curtas são estalos do modelo, não sílabas
VOICE_AMPLITUDE, INSTRUMENT_AMPLITUDE, BASS_AMPLITUDE = .8, .6, .7
TIMEOUT = 45 * 60


def available(root=None):
    """O componente YourMT3+ está instalado?"""
    return setup.installed(root) is not None


def recognize(wav_path, progress=lambda message: None, root=None, cpu=False, timeout=TIMEOUT):
    """Roda o modelo na gravação (WAV) e devolve o relatório do yourmt3_runner: {'notes': [{start, end, pitch, program,
    drum, velocity}], 'device', 'seconds'}. Levanta ValueError com a causa quando o componente falta ou falha."""
    root = Path(root or setup.default_root())
    if not available(root): raise ValueError('O componente YourMT3+ não está instalado.')
    env = dict(os.environ, PYTHONUTF8='1', PYTHONIOENCODING='utf-8', WANDB_MODE='disabled', HF_HUB_DISABLE_TELEMETRY='1')
    for name in ('PYTHONHOME', 'PYTHONPATH', 'VIRTUAL_ENV'): env.pop(name, None)
    with tempfile.TemporaryDirectory(prefix='yourmt3-') as scratch:
        output = Path(scratch) / 'notas.json'
        command = [str(setup.python_of(root)), '-X', 'utf8', str(RUNNER), '--codigo', str(setup.code_of(root)),
                   '--audio', str(Path(wav_path).resolve()), '--saida', str(output)] + (['--cpu'] if cpu else [])
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace',
                                   env=env, cwd=str(setup.code_of(root)), creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        tail = []
        # O limite de tempo vale também se o modelo travar sem escrever nada: o relógio encerra o processo.
        watchdog = threading.Timer(timeout, process.kill); watchdog.daemon = True; watchdog.start()
        try:
            for line in process.stdout:
                line = line.rstrip()
                if line.startswith('@'): progress(line[1:])
                elif line: tail = (tail + [line])[-15:]
            process.wait()
        except BaseException:
            process.kill(); raise
        finally:
            expired = not watchdog.is_alive(); watchdog.cancel()
        if expired and process.returncode: raise ValueError(f'O YourMT3+ passou do limite de {round(timeout / 60)} minutos nesta gravação.')
        if process.returncode or not output.is_file():
            raise ValueError('O YourMT3+ não conseguiu ler esta gravação: ' + (' | '.join(tail[-4:]) or f'código {process.returncode}'))
        return json.loads(output.read_text(encoding='utf-8'))


def merge_doubles(notes, window=.06):
    """O modelo às vezes escreve a mesma nota em dois instrumentos (a sanfona como metais e como piano): notas de mesma
    altura que começam a até `window` segundos uma da outra viram uma só, a mais longa. notes são (início, fim, altura,
    amplitude) em segundos."""
    kept = {}
    result = []
    for note in sorted(notes):
        previous = kept.get(note[2])
        if previous is not None and note[0] - result[previous][0] <= window:
            if note[1] > result[previous][1]: result[previous] = (result[previous][0], note[1], note[2], max(note[3], result[previous][3]))
            continue
        kept[note[2]] = len(result); result.append(note)
    return sorted(result)


def single_line(notes, minimum=.05):
    """Uma linha só (a voz canta uma nota por vez): quando uma nota começa antes de a anterior acabar, a anterior termina
    ali; se sobrar menos de `minimum` segundos dela, ela sai. Entre duas notas que começam juntas fica a mais longa."""
    line = []
    for note in sorted(notes, key=lambda n: (n[0], -(n[1] - n[0]))):
        if line and note[0] - line[-1][0] < minimum: continue
        if line and note[0] < line[-1][1]:
            previous = line[-1]
            if note[0] - previous[0] < minimum: line.pop()
            else: line[-1] = (previous[0], note[0], previous[2], previous[3])
        line.append(note)
    return line


def top_line(notes, low, high, gap=.06):
    """A linha de cima dos instrumentos (o solo da sanfona, da guitarra): em cada momento, a nota mais aguda que soa
    entre `low` e `high`. Das notas que começam juntas (a até `gap` segundos) fica a mais aguda; uma nota mais aguda
    que entra depois interrompe a anterior; uma mais grave que entra enquanto a de cima ainda soa é acompanhamento e
    fica de fora."""
    line = []
    for note in sorted((n for n in notes if low <= n[2] <= high), key=lambda n: (n[0], -n[2])):
        if line:
            previous = line[-1]
            if note[0] - previous[0] < gap:                       # começam juntas: um acorde, fica a de cima
                if note[2] > previous[2]: line[-1] = note
                continue
            if note[0] < previous[1]:
                if note[2] <= previous[2] and note[0] < previous[1] - gap: continue
                line[-1] = (previous[0], note[0], previous[2], previous[3])
        line.append(note)
    return line


def parts(report):
    """As notas do modelo separadas por papel, em (início, fim, altura, amplitude): 'voice' (a voz principal, uma linha
    só), 'chorus', 'bass', 'others' (os demais instrumentos de altura definida, sem notas dobradas) e 'drums' (os
    instantes dos ataques da bateria)."""
    voice, chorus, bass, others, drums = [], [], [], [], []
    for note in report.get('notes', []):
        start, end, pitch, program = float(note['start']), float(note['end']), int(note['pitch']), int(note.get('program', 0))
        if end <= start or not 0 <= pitch <= 127: continue
        if note.get('drum'): drums.append(start)
        elif program in SINGING:
            if end - start >= SHORTEST_VOICE: voice.append((start, end, pitch, VOICE_AMPLITUDE))
        elif program in CHORUS: chorus.append((start, end, pitch, VOICE_AMPLITUDE))
        elif program in BASS:
            if pitch < BASS_TOP: bass.append((start, end, pitch, BASS_AMPLITUDE))
        else: others.append((start, end, pitch, INSTRUMENT_AMPLITUDE))
    return {'voice': single_line(voice), 'chorus': sorted(chorus), 'bass': single_line(bass), 'others': merge_doubles(others), 'drums': sorted(drums)}
