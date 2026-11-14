"""Audio recording → MIDI with a melody track and an accompaniment track, so the usual pipeline can make the score.

Notes come from Spotify's Basic Pitch model (engine/models/basic-pitch/nmp.onnx, Apache 2.0) run with onnxruntime; the
note-building step follows Basic Pitch's own algorithm. A dynamic-programming beat tracker (Ellis 2007) finds the pulse,
and every time is mapped onto those beats, so the MIDI measures follow the music even when the tempo drifts. The
melody is the highest sustained line (skyline); everything else becomes the accompaniment the chords are read from.
The result is an honest starting point: a full band recording usually needs the melody reviewed."""
from pathlib import Path
import struct, math, sys
import numpy as np

SAMPLE_RATE = 22050
FFT_HOP = 256
WINDOW = SAMPLE_RATE * 2 - FFT_HOP          # 43844 samples per model window
OVERLAP_FRAMES = 30
OVERLAP = OVERLAP_FRAMES * FFT_HOP          # 7680
HOP = WINDOW - OVERLAP
FRAMES_PER_SECOND = SAMPLE_RATE / FFT_HOP
FRAMES_PER_WINDOW = 172
MIDI_OFFSET = 21
MODEL = Path(__file__).parent / 'models' / 'basic-pitch' / 'nmp.onnx'
MAX_SECONDS = 15 * 60


def read_wav(path, mono=True):
    """Float32 samples and the sample rate of a PCM (8/16/24/32-bit) or IEEE-float WAV file: mono (the mean of the
    channels) or, with mono=False, shaped (samples, channels)."""
    data = Path(path).read_bytes()
    if data[:4] != b'RIFF' or data[8:12] != b'WAVE': raise ValueError('O arquivo de áudio não está no formato WAV.')
    position, fmt, samples = 12, None, None
    while position + 8 <= len(data):
        chunk, size = data[position:position + 4], struct.unpack('<I', data[position + 4:position + 8])[0]
        body = data[position + 8:position + 8 + size]
        if chunk == b'fmt ':
            tag, channels, rate = struct.unpack('<HHI', body[:8]); bits = struct.unpack('<H', body[14:16])[0]
            if tag == 0xFFFE and len(body) >= 26: tag = struct.unpack('<H', body[24:26])[0]
            fmt = (tag, channels, rate, bits)
        elif chunk == b'data': samples = body
        position += 8 + size + (size & 1)
    if fmt is None or samples is None: raise ValueError('WAV sem formato ou sem dados de áudio.')
    tag, channels, rate, bits = fmt
    if tag == 3 and bits == 32: audio = np.frombuffer(samples[:len(samples) // 4 * 4], '<f4').astype(np.float32)
    elif tag == 3 and bits == 64: audio = np.frombuffer(samples[:len(samples) // 8 * 8], '<f8').astype(np.float32)
    elif tag == 1 and bits == 16: audio = np.frombuffer(samples[:len(samples) // 2 * 2], '<i2').astype(np.float32) / 32768
    elif tag == 1 and bits == 8: audio = (np.frombuffer(samples, np.uint8).astype(np.float32) - 128) / 128
    elif tag == 1 and bits == 24:
        raw = np.frombuffer(samples[:len(samples) // 3 * 3], np.uint8).reshape(-1, 3).astype(np.int32)
        audio = ((raw[:, 0] | raw[:, 1] << 8 | raw[:, 2] << 16) << 8 >> 8).astype(np.float32) / 8388608
    elif tag == 1 and bits == 32: audio = np.frombuffer(samples[:len(samples) // 4 * 4], '<i4').astype(np.float32) / 2147483648
    else: raise ValueError(f'Formato de WAV não suportado ({tag}, {bits} bits).')
    audio = audio[:len(audio) // channels * channels].reshape(-1, channels)
    if mono: audio = audio.mean(axis=1)
    if len(audio) > rate * MAX_SECONDS: raise ValueError('Áudio longo demais (mais de 15 minutos).')
    return audio.astype(np.float32), rate


def resample(audio, rate, target=SAMPLE_RATE, taps=32):
    """Windowed-sinc resampling (Hann window, cut-off below the lower Nyquist), in blocks to bound memory."""
    if rate == target: return audio.astype(np.float32)
    ratio = target / rate; cutoff = min(1.0, ratio) * .95
    count = int(len(audio) * ratio)
    padded = np.concatenate([np.zeros(taps, np.float32), audio, np.zeros(taps + 1, np.float32)])
    result = np.empty(count, np.float32); offsets = np.arange(-taps + 1, taps + 1)
    for start in range(0, count, 65536):
        times = np.arange(start, min(count, start + 65536)) / ratio
        base = np.floor(times).astype(np.int64)
        index = base[:, None] + offsets[None, :]
        distance = times[:, None] - index
        kernel = cutoff * np.sinc(cutoff * distance) * (0.5 + 0.5 * np.cos(np.pi * distance / taps))
        result[start:start + len(times)] = (padded[index + taps] * kernel).sum(axis=1)
    return result


def posteriors(audio, session=None):
    """Note, onset and contour activations per frame (86 per second) for 22050 Hz mono audio."""
    try: import onnxruntime
    except ImportError as error:
        raise ValueError(f'Este Python não tem o componente de reconhecimento de notas (onnxruntime): {sys.executable}. Use o Python que acompanha o TeclaScore (pasta runtime do aplicativo).') from error
    session = session or onnxruntime.InferenceSession(str(MODEL), providers=['CPUExecutionProvider'])
    original = len(audio)
    audio = np.concatenate([np.zeros(OVERLAP // 2, np.float32), audio])
    windows = []
    for start in range(0, len(audio), HOP):
        window = audio[start:start + WINDOW]
        if len(window) < WINDOW: window = np.concatenate([window, np.zeros(WINDOW - len(window), np.float32)])
        windows.append(window)
    outputs = {'note': [], 'onset': [], 'contour': []}
    for start in range(0, len(windows), 16):
        batch = np.stack(windows[start:start + 16])[:, :, None].astype(np.float32)
        contour, note, onset = session.run(['StatefulPartitionedCall:0', 'StatefulPartitionedCall:1', 'StatefulPartitionedCall:2'], {'serving_default_input_2:0': batch})
        outputs['note'].append(note); outputs['onset'].append(onset); outputs['contour'].append(contour)
    frames = int(math.floor(original * FRAMES_PER_SECOND / SAMPLE_RATE))
    def unwrap(parts):
        stacked = np.concatenate(parts)[:, OVERLAP_FRAMES // 2:-(OVERLAP_FRAMES // 2), :]
        return stacked.reshape(-1, stacked.shape[2])[:frames]
    return unwrap(outputs['note']), unwrap(outputs['onset']), unwrap(outputs['contour'])


def frame_times(count):
    """Seconds of each model frame, with Basic Pitch's per-window offset correction."""
    times = np.arange(count) * FFT_HOP / SAMPLE_RATE
    offset = FFT_HOP / SAMPLE_RATE * (FRAMES_PER_WINDOW - WINDOW / FFT_HOP) + .0018
    return times - offset * np.floor(np.arange(count) / FRAMES_PER_WINDOW)


def build_notes(frames, onsets, onset_threshold=.5, frame_threshold=.3, minimum_frames=11, energy_tolerance=11):
    """Basic Pitch's note building: peaks of the onset activation (plus inferred onsets) followed while the note
    activation stays above the threshold. Returns (start frame, end frame, pitch, amplitude)."""
    count = frames.shape[0]
    if count < 3: return []
    diffs = []
    for n in (1, 2):
        shifted = np.concatenate([np.zeros((n, frames.shape[1])), frames])
        diffs.append(shifted[n:, :] - shifted[:-n, :])
    frame_diff = np.min(diffs, axis=0); frame_diff[frame_diff < 0] = 0; frame_diff[:2, :] = 0
    if frame_diff.max() > 0: frame_diff = onsets.max() * frame_diff / frame_diff.max()
    onsets = np.maximum(onsets, frame_diff)
    # A note right at the start of the recording peaks in the first frame: compare against silence before it.
    padded = np.concatenate([np.zeros((1, onsets.shape[1])), onsets, np.zeros((1, onsets.shape[1]))])
    inner = (padded[1:-1] > padded[:-2]) & (padded[1:-1] >= padded[2:])
    peaks = np.where(inner, onsets, 0)
    times, pitches = np.where(peaks >= onset_threshold)
    remaining = frames.copy(); events = []
    for start, index in zip(times[::-1], pitches[::-1]):
        if start >= count - 1: continue
        i, misses = start + 1, 0
        while i < count - 1 and misses < energy_tolerance:
            misses = misses + 1 if remaining[i, index] < frame_threshold else 0
            i += 1
        i -= misses
        if i - start <= minimum_frames: continue
        remaining[start:i, max(0, index - 1):index + 2] = 0
        events.append((int(start), int(i), int(index) + MIDI_OFFSET, float(frames[start:i, index].mean())))
    return sorted(events)


SHORT_FRAMES = 6        # ~70 ms: semicolcheias cantadas em andamento rápido
LONG_FRAMES = 11        # ~128 ms: o mínimo original do Basic Pitch


def filter_short_notes(events, long_frames=LONG_FRAMES, minimum_amplitude=.4, reach=30, leap=7):
    """Notas curtas (entre SHORT_FRAMES e long_frames quadros) só ficam quando são fortes e continuam a linha: outra
    nota começa a até `reach` quadros (~0,35 s) dela, a até `leap` semitons. As demais costumam ser ruído do modelo.
    events são (início, fim, altura, amplitude) em quadros; as notas longas passam sem mudança."""
    ordered = sorted(events); starts = [e[0] for e in ordered]
    import bisect
    kept = []
    for index, event in enumerate(ordered):
        start, end, pitch, amplitude = event
        if end - start > long_frames: kept.append(event); continue
        if amplitude < minimum_amplitude: continue
        lo, hi = bisect.bisect_left(starts, start - reach), bisect.bisect_right(starts, start + reach)
        if any(j != index and abs(ordered[j][2] - pitch) <= leap and ordered[j][2] != pitch for j in range(lo, hi)): kept.append(event)
    return kept


def detect_notes(frames, onsets):
    """Notas do modelo com o mínimo curto (semicolcheias cantadas) e o filtro de amplitude e continuidade."""
    return filter_short_notes(build_notes(frames, onsets, minimum_frames=SHORT_FRAMES))


VOICE_RANGE = (40, 86)          # Mi2–Ré6: vozes masculinas e femininas
INSTRUMENT_RANGE = (52, 96)     # Mi3–Dó7: sanfona, flauta, guitarra solo


def predominant_line(frames, onsets, low=40, high=86, voicing=.3, change=2.5, jump=.3, toggle=1.5, rise=0.0, minimum=5, syllables=None):
    """Uma única linha melódica (a voz principal) das ativações do modelo, por Viterbi: em cada quadro a linha está
    numa altura entre low e high ou em silêncio; ficar na mesma nota é grátis, mudar de nota custa `change` mais `jump`
    por semitom, entrar e sair do silêncio custa `toggle`. Assim vibratos, vozes de apoio e harmônicos soltos não
    viram notas. Notas repetidas (sílabas na mesma altura) se separam pelos picos de ataque; fragmentos com menos de
    `minimum` quadros se juntam à vizinha ou somem. `rise` favorece levemente as notas agudas (linha de instrumento).
    `syllables` (opcional, um booleano por quadro, de syllable_onsets) marca os começos de sílaba vistos na energia da
    pista: eles também separam notas repetidas. Retorna (quadro inicial, quadro final, altura, amplitude)."""
    a, b = low - MIDI_OFFSET, high - MIDI_OFFSET + 1
    acts = np.asarray(frames, np.float64)[:, a:b]
    count, size = acts.shape
    if count < 2: return []
    pitches = np.arange(low, high + 1)
    emit = np.concatenate([np.full((count, 1), math.log(voicing)), np.log(acts + 1e-4) + rise * (pitches - low) / 12], axis=1)
    trans = np.zeros((size + 1, size + 1))
    distance = np.abs(pitches[:, None] - pitches[None, :])
    trans[1:, 1:] = -(change + jump * distance); np.fill_diagonal(trans[1:, 1:], 0)
    trans[0, 1:] = trans[1:, 0] = -toggle
    score = emit[0].copy(); back = np.zeros((count, size + 1), np.int16)
    for t in range(1, count):
        candidates = score[:, None] + trans
        back[t] = candidates.argmax(0); score = candidates.max(0) + emit[t]
    path = np.empty(count, np.int64); path[-1] = int(score.argmax())
    for t in range(count - 1, 0, -1): path[t - 1] = back[t, path[t]]
    onsets = np.asarray(onsets, np.float64)
    spacing = max(minimum, int(round(.09 * FRAMES_PER_SECOND)))   # duas sílabas na mesma altura: pelo menos ~90 ms
    everywhere = onsets[:, a:b].max(axis=1) if syllables is not None else None
    notes = []
    t = 0
    while t < count:
        state = path[t]; end = t
        while end < count and path[end] == state: end += 1
        if state:
            pitch = low + state - 1; column = pitch - MIDI_OFFSET
            strike = onsets[:, max(0, column - 1):column + 2].max(axis=1)
            # O ataque verdadeiro da nota: o pico de ataque mais forte da própria altura logo antes ou logo depois da
            # troca de estado (a troca do Viterbi pode adiantar ou atrasar alguns quadros).
            start = t
            lo, hi = max(0, t - 4, notes[-1][1] if notes else 0), min(end - minimum, t + 14)
            if hi > lo:
                peak = lo + int(np.argmax(strike[lo:hi]))
                if strike[peak] >= .3: start = peak
            if notes and start < notes[-1][1]: start = notes[-1][1]
            cuts = [start]
            for i in range(start + spacing, end - spacing):
                # Uma nova sílaba na mesma altura: pico de ataque e, logo antes, uma queda da nota (a consoante).
                dip = acts[max(start, i - 3):i, state - 1].min() < .75 * acts[i:i + 3, state - 1].max()
                attack = strike[i] >= .5 and strike[i] >= strike[i - 1] and strike[i] > strike[i + 1] and dip
                # Uma sílaba vista na energia só corta se há ali um ataque desta altura (um batimento ou tremolo não
                # tem ataque) e se nenhuma outra altura ataca mais forte (seria a nota seguinte começando).
                syllable = syllables is not None and syllables[i] and strike[i - 1:i + 4].max() >= max(.35, .6 * everywhere[i - 1:i + 4].max())
                if (attack or syllable) and i - cuts[-1] >= spacing: cuts.append(i)
            cuts.append(end)
            if start > t and notes and notes[-1][1] == t: notes[-1][1] = start
            for s, e in zip(cuts, cuts[1:]):
                if e > s: notes.append([s, e, pitch, float(acts[s:e, state - 1].mean())])
        t = end
    # Fragmentos curtos colados a uma nota vizinha próxima (vibrato, deslize) passam para ela; isolados somem.
    changed = True
    while changed:
        changed = False
        for i, note in enumerate(notes):
            if note[1] - note[0] >= minimum: continue
            neighbours = [j for j in (i - 1, i + 1) if 0 <= j < len(notes) and abs(notes[j][2] - note[2]) <= 2
                          and (notes[j][1] == note[0] if j < i else notes[j][0] == note[1])]
            if neighbours:
                j = max(neighbours, key=lambda k: notes[k][1] - notes[k][0])
                if j < i: notes[j][1] = note[1]
                else: notes[j][0] = note[0]
            del notes[i]; changed = True; break
    return [(int(s), int(e), int(p), amp) for s, e, p, amp in notes]


def upper_voice(notes, interval=3, held=.4, contact=.12):
    """A sanfona (e o teclado) muitas vezes alterna a nota da melodia com uma nota mais grave repetida, um pedal
    interno: Mi♭ longo, Dó, Lá♭ longo, Dó, Lá♭… Numa linha só, esse pedal faz a melodia saltar para baixo e para cima
    e ela perde o sentido. Um grupo de notas numa mesma altura, encostado nas vizinhas e pelo menos `interval`
    semitons abaixo das duas, ao lado de uma nota de cima longa (`held` segundos ou mais, mais longa que o grupo), é o
    pedal: sai, e a nota de cima anterior se estende até a seguinte. Passagens rápidas, em que nenhuma nota é longa (o
    riff, um ponteio), ficam como estão. notes em segundos (início, fim, altura, amplitude)."""
    notes = sorted(notes)
    out = []
    i = 0
    while i < len(notes):
        j = i
        while j + 1 < len(notes) and notes[j + 1][2] == notes[i][2] and notes[j + 1][0] - notes[j][1] <= contact: j += 1
        if out and j + 1 < len(notes):
            before, after = out[-1], notes[j + 1]
            span = notes[j][1] - notes[i][0]
            longest = max(before[1] - before[0], after[1] - after[0])
            if (notes[i][0] - before[1] <= contact and after[0] - notes[j][1] <= contact
                    and notes[i][2] <= min(before[2], after[2]) - interval and longest >= held and longest > span):
                out[-1] = (before[0], after[0], before[2], before[3])
                i = j + 1; continue
        out.extend(notes[i:j + 1]); i = j + 1
    return out


def presence(audio, count, relative=.1, smooth=.25):
    """Em quais quadros do modelo uma pista separada soa de verdade: energia (média de `smooth` segundos) acima de
    `relative` vezes o nível dos trechos fortes da própria pista (percentil 90). O que vaza de outros instrumentos para
    a pista da voz fica bem abaixo disso e não vira melodia."""
    audio = np.asarray(audio, np.float64)
    usable = min(count, len(audio) // FFT_HOP)
    energy = np.zeros(count)
    energy[:usable] = (audio[:usable * FFT_HOP].reshape(usable, FFT_HOP) ** 2).mean(axis=1)
    width = max(1, int(smooth * FRAMES_PER_SECOND))
    energy = np.sqrt(np.convolve(energy, np.ones(width) / width, mode='same'))
    if not energy.any(): return np.zeros(count, bool)
    return energy > relative * float(np.percentile(energy, 90))


def syllable_onsets(audio, count, depth=.7, reach=8):
    """Começos de sílaba na energia de uma pista de voz, um booleano por quadro do modelo: um vale da energia (a
    consoante ou a respiração) seguido, a até `reach` quadros, de um pico pelo menos 1/depth vezes mais forte."""
    audio = np.asarray(audio, np.float64)
    usable = min(count, len(audio) // FFT_HOP)
    energy = np.zeros(count)
    energy[:usable] = np.sqrt((audio[:usable * FFT_HOP].reshape(usable, FFT_HOP) ** 2).mean(axis=1))
    energy = np.convolve(energy, np.ones(3) / 3, mode='same')
    marks = np.zeros(count, bool)
    if count < 3 or not energy.any(): return marks
    floor = .05 * float(np.percentile(energy, 90))
    valleys = np.where((energy[1:-1] <= energy[:-2]) & (energy[1:-1] < energy[2:]))[0] + 1
    for i in valleys:
        ahead = energy[i + 1:i + 1 + reach]
        if len(ahead) and ahead.max() > floor and energy[i] < depth * ahead.max(): marks[i + 1] = True
    return marks


def onset_envelope(audio, rate=SAMPLE_RATE):
    """Força dos ataques por quadro do modelo (salto de 256 amostras a 22050 Hz): fluxo espectral positivo do
    logaritmo da magnitude. Com a bateria separada, ele marca o pulso sem as notas longas dos outros instrumentos."""
    audio = np.asarray(audio, np.float64)
    size = 1024
    if len(audio) < size * 2: return np.zeros(1)
    padded = np.concatenate([np.zeros(size // 2), audio, np.zeros(size // 2)])
    count = 1 + (len(padded) - size) // FFT_HOP
    frames = np.lib.stride_tricks.as_strided(padded, (count, size), (padded.strides[0] * FFT_HOP, padded.strides[0]))
    window = np.hanning(size)
    magnitude = np.log1p(100 * np.abs(np.fft.rfft(frames * window, axis=1)))
    flux = np.maximum(0, np.diff(magnitude, axis=0, prepend=magnitude[:1])).sum(axis=1)
    return flux


def beat_track(envelope, fps=FRAMES_PER_SECOND, tightness=100.0):
    """Beat times (seconds) and tempo from an onset-strength envelope: tempo by autocorrelation weighted around 110 BPM,
    beats by dynamic programming (Ellis 2007)."""
    envelope = np.asarray(envelope, np.float64)
    if len(envelope) < fps * 2 or envelope.max() <= 0: return np.array([0.0]), 100.0
    envelope = envelope / envelope.std() if envelope.std() > 0 else envelope
    lags = np.arange(int(fps * 60 / 200), int(fps * 60 / 50))
    centered = envelope - envelope.mean()
    correlation = np.array([np.dot(centered[:-lag], centered[lag:]) for lag in lags])
    bpm_of = 60 * fps / lags
    weight = np.exp(-.5 * (np.log2(bpm_of / 110) / .9) ** 2)
    period = lags[int(np.argmax(correlation * weight))]
    score = envelope.copy(); back = np.full(len(envelope), -1)
    window = np.arange(-2 * period, -int(period / 2) + 1)
    penalty = -tightness * np.log(-window / period) ** 2
    for t in range(len(envelope)):
        candidates = t + window; valid = candidates >= 0
        if not valid.any(): continue
        values = score[candidates[valid]] + penalty[valid]
        best = int(np.argmax(values)); score[t] = envelope[t] + values[best]; back[t] = candidates[valid][best]
    tail = len(envelope) - period
    t = tail + int(np.argmax(score[tail:])) if tail > 0 else int(np.argmax(score))
    beats = []
    while t >= 0: beats.append(t); t = back[t]
    beats = np.array(beats[::-1], np.float64) / fps
    if len(beats) >= 2: bpm = 60 / float(np.median(np.diff(beats)))
    else: bpm = 60 * fps / period
    return beats, bpm


def steady_grid(beats, notes):
    """When the pulse is steady (beat intervals within 4%), a straight grid fitted to the note attacks replaces the
    beat-by-beat positions, so small detection errors do not accumulate into shifted notes."""
    beats = np.asarray(beats, np.float64)
    if len(beats) < 8: return beats
    intervals = np.diff(beats)
    if intervals.std() / intervals.mean() > .04: return beats
    # A gradual change (speeding up or slowing down) keeps the detected beats even when each step is small.
    half = len(intervals) // 2
    if abs(np.median(intervals[:half]) / np.median(intervals[half:]) - 1) > .03: return beats
    period = float(np.median(intervals))
    attacks = np.array([n[0] for n in notes])
    # Refine the period and phase so attacks fall as close as possible to sixteenths of the grid.
    best = (np.inf, period, beats[0])
    for trial in np.linspace(period * .985, period * 1.015, 61):
        for phase in np.linspace(beats[0] - trial / 8, beats[0] + trial / 8, 9):
            offsets = (attacks - phase) / (trial / 4)
            error = float(np.mean(np.abs(offsets - np.round(offsets))))
            if error < best[0]: best = (error, trial, phase)
    _, period, phase = best
    count = int(np.ceil((max(attacks.max(), beats[-1]) - phase) / period)) + 2
    return phase + period * np.arange(count)


def skyline(notes, gap=.06):
    """Melody: at each moment the highest note sounding; a lower note that starts while a higher one sounds stays in
    the accompaniment. notes are (start, end, pitch, amplitude) in seconds."""
    melody, rest = [], []
    for note in sorted(notes, key=lambda n: (n[0], -n[2])):
        if melody and note[0] < melody[-1][1] - gap:
            if note[2] > melody[-1][2] + 2 and note[3] >= melody[-1][3] * .6:
                previous = melody.pop(); rest.append(previous)
                melody.append(note)
            else: rest.append(note)
        else: melody.append(note)
    return melody, rest


def to_ticks(seconds, beats, ppq):
    """Maps a time onto the beat grid: between two detected beats the position is linear; outside, the median beat."""
    beats = np.asarray(beats, np.float64)
    if len(beats) < 2: return int(round(seconds * 2 * ppq))
    period = float(np.median(np.diff(beats)))
    if seconds <= beats[0]: return int(round((seconds - beats[0]) / period * ppq))
    if seconds >= beats[-1]: return int(round((len(beats) - 1 + (seconds - beats[-1]) / period) * ppq))
    i = int(np.searchsorted(beats, seconds) - 1)
    return int(round((i + (seconds - beats[i]) / (beats[i + 1] - beats[i])) * ppq))


def downbeat_phase(notes, beats, ppq=480, meter=4):
    """Which beat of the measure starts it: the one where the accompaniment attacks most (weighted by length)."""
    votes = [0.0] * meter
    for start, end, pitch, amplitude in notes:
        position = to_ticks(start, beats, ppq) / ppq
        if abs(position - round(position)) < .2: votes[int(round(position)) % meter] += (end - start) * amplitude
    return int(np.argmax(votes)) if any(votes) else 0


def meter_guess(notes, beats, ppq=480):
    """3 or 4 beats per measure: the accents (long and low notes weigh more) gather on one beat of every three or of
    every four. Four wins unless three is clearly stronger."""
    accents = {}
    for start, end, pitch, amplitude in notes:
        position = to_ticks(start, beats, ppq) / ppq
        if abs(position - round(position)) < .2:
            beat = int(round(position)); accents[beat] = accents.get(beat, 0) + (end - start) * amplitude * (1.5 if pitch < 55 else 1)
    total = sum(accents.values())
    if total <= 0 or len(accents) < 6: return 4
    def lift(meter):
        shares = [sum(v for b, v in accents.items() if b % meter == phase) / total for phase in range(meter)]
        return max(shares) * meter
    return 3 if lift(3) > lift(4) * 1.15 else 4


def beat_grids(onsets, ppq=480):
    """For each beat, the subdivision the attacks follow: 'straight' (sixteenths), 'triplet' (thirds of the beat) or
    'swing' (two eighths played long-short, written as straight eighths). onsets are ticks on the beat grid. Returns
    (grids by beat, swing) where swing tells that most beats with an offbeat were played long-short."""
    by_beat = {}
    for tick in onsets:
        beat = int((tick + ppq / 8) // ppq)
        by_beat.setdefault(beat, []).append((tick - beat * ppq) / ppq)
    grids = {}; swung = offbeat_beats = 0
    for beat, offsets in by_beat.items():
        inner = [f for f in offsets if .12 < f < .88]
        if not inner: grids[beat] = 'straight'; continue
        offbeat_beats += 1
        error16 = sum(abs(f * 4 - round(f * 4)) / 4 for f in inner) / len(inner)
        error3 = sum(abs(f * 3 - round(f * 3)) / 3 for f in inner) / len(inner)
        thirds = {round(f * 3) for f in inner if abs(f * 3 - round(f * 3)) < .12}
        if error3 < error16 * .6 and error3 < .05:
            if thirds == {2}: grids[beat] = 'swing'; swung += 1
            else: grids[beat] = 'triplet'
        else: grids[beat] = 'straight'
    swing = offbeat_beats >= 4 and swung >= .6 * offbeat_beats
    if not swing: grids = {b: ('triplet' if g == 'swing' else g) for b, g in grids.items()}
    else:
        # In a swung piece, a single late offbeat is the short eighth even when it drifted towards the sixteenth.
        for beat, offsets in by_beat.items():
            inner = [f for f in offsets if .12 < f < .88]
            if grids.get(beat) == 'straight' and len(inner) == 1 and .55 < inner[0] < .85: grids[beat] = 'swing'
    return grids, swing


def snap(tick, grids, ppq=480):
    """A tick moved onto its beat's grid: sixteenths, thirds, or for swing the long-short pair written as eighths."""
    beat = int((tick + ppq / 8) // ppq)
    grid = grids.get(beat, 'straight')
    offset = (tick - beat * ppq) / ppq
    if grid == 'triplet': return beat * ppq + round(offset * 3) * (ppq // 3)
    if grid == 'swing':
        step = round(offset * 3)
        return beat * ppq + (0 if step == 0 else ppq // 2 if step == 2 else ppq if step == 3 else ppq // 3)
    return beat * ppq + round(offset * 4) * (ppq // 4)


CHORD_TEMPLATES = {'': (0, 4, 7), 'm': (0, 3, 7), '7': (0, 4, 7, 10), 'm7': (0, 3, 7, 10)}
NOTE_NAMES = ('C', 'C#', 'D', 'Eb', 'E', 'F', 'F#', 'G', 'Ab', 'A', 'Bb', 'B')


def chord_of(chroma, bass):
    """Best chord for a pitch-class profile (12 energies) and bass profile: cosine match with the chord templates,
    a bonus when the bass is the root, and a small cost for the four-note chords. Returns (root, quality, score)."""
    norm = float(np.linalg.norm(chroma))
    if norm <= 0: return None
    profile = chroma / norm
    bass_total = float(bass.sum())
    best = None
    for root in range(12):
        for quality, intervals in CHORD_TEMPLATES.items():
            template = np.zeros(12)
            for interval in intervals: template[(root + interval) % 12] = 1
            score = float(profile @ template / np.linalg.norm(template))
            if bass_total > 0: score += .15 * float(bass[root]) / bass_total
            if len(intervals) == 4: score -= .04
            if best is None or score > best[2]: best = (root, quality, score)
    return best


def audio_chords(melody, accompaniment, beats, phase=0, span=2, melody_weight=.25):
    # (span is the number of beats per guess: 2 in 4/4, 3 in 3/4)
    """Chords from what was heard, one guess per `span` beats starting on the bar phase. Each segment sums the notes
    sounding in it by pitch class, weighted by overlap and loudness: the accompaniment counts fully, the melody little
    (its passing notes are not the harmony). The lowest accompaniment note forms the bass profile. A segment keeps the
    previous chord unless the new guess is clearly better; segments without a new accompaniment attack keep it too. Returns
    (start, end, root, quality) in seconds."""
    beats = np.asarray(beats, np.float64)
    if len(beats) < span + 1: return []
    result = []
    def score_of(chroma, bass, root, quality):
        template = np.zeros(12)
        for interval in CHORD_TEMPLATES[quality]: template[(root + interval) % 12] = 1
        value = float((chroma / np.linalg.norm(chroma)) @ template / np.linalg.norm(template))
        if bass.sum() > 0: value += .15 * float(bass[root]) / float(bass.sum())
        return value - (.04 if len(CHORD_TEMPLATES[quality]) == 4 else 0)
    for index in range(phase % span, len(beats) - 1, span):
        a, b = beats[index], beats[min(index + span, len(beats) - 1)]
        chroma = np.zeros(12); bass = np.zeros(12)
        sounding = [(max(s, a), min(e, b), p, amp) for s, e, p, amp in accompaniment if s < b and e > a]
        for s, e, p, amp in sounding: chroma[p % 12] += (e - s) * amp
        if sounding:
            lowest = min(p for _, _, p, _ in sounding)
            for s, e, p, amp in sounding:
                if p <= lowest + 2: bass[p % 12] += (e - s) * amp
        for s, e, p, amp in melody:
            if s < b and e > a: chroma[p % 12] += (min(e, b) - max(s, a)) * amp * melody_weight
        # Harmony changes with a new chord attack (two or more notes): a segment where the accompaniment only rings on,
        # or where a single note is struck again, keeps the chord.
        attacked = len({p % 12 for s, _, p, _ in accompaniment if a - .08 <= s < b - .08}) >= 2
        if not sounding or chroma.sum() <= 0 or result and not attacked:
            if result: result[-1] = (result[-1][0], float(b), result[-1][2], result[-1][3])
            continue
        root, quality, score = chord_of(chroma, bass)
        if result:
            previous = result[-1]
            if (previous[2], previous[3]) == (root, quality) or score_of(chroma, bass, previous[2], previous[3]) >= score - .05:
                result[-1] = (previous[0], float(b), previous[2], previous[3]); continue
        result.append((float(a), float(b), root, quality))
    # The beat grid runs on past the music; the last chord ends with the last note, not with an empty measure.
    last = max([e for _, e, _, _ in melody + accompaniment] or [0])
    if result and result[-1][1] > last: result[-1] = (result[-1][0], max(result[-1][0] + .05, last), result[-1][2], result[-1][3])
    return result


# Perfis de tonalidade de Krumhansl–Kessler (maior e menor), para saber quais acordes pertencem à música.
MAJOR_PROFILE = np.array([6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88])
MINOR_PROFILE = np.array([6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17])


def frame_chroma(frames, floor=.1):
    """Basic Pitch note activations (frames × 88 keys) folded into 12 pitch classes per frame; faint activations
    (below `floor`) are the model's doubt, not notes."""
    folded = np.where(frames >= floor, frames, 0)
    chroma = np.zeros((len(frames), 12))
    for key in range(frames.shape[1]): chroma[:, (key + MIDI_OFFSET) % 12] += folded[:, key]
    return chroma


def lowest_chroma(frames, active=.3):
    """The bass of each frame: only the lowest key sounding (activation ≥ `active`), so the harmonics of a bass note and
    the chord above it do not count as bass."""
    chroma = np.zeros((len(frames), 12))
    sounding = frames >= active
    rows = np.where(sounding.any(axis=1))[0]
    lowest = np.argmax(sounding, axis=1)[rows]
    chroma[rows, (lowest + MIDI_OFFSET) % 12] = frames[rows, lowest]
    return chroma


def key_guess(chroma):
    """(tonic, minor) whose Krumhansl–Kessler profile best matches a 12-value pitch-class profile."""
    best = None
    for tonic in range(12):
        for minor, profile in ((False, MAJOR_PROFILE), (True, MINOR_PROFILE)):
            fit = float(np.corrcoef(np.roll(profile, tonic), chroma)[0, 1]) if chroma.std() > 0 else 0.0
            if best is None or fit > best[0]: best = (fit, tonic, minor)
    return best[1], best[2]


def diatonic_chords(tonic, minor):
    """The triads and sevenths built on the scale of the key; in minor, also the major V of the harmonic minor."""
    degrees = ((0, 'm'), (3, ''), (5, 'm'), (7, 'm'), (7, ''), (8, ''), (10, '')) if minor else ((0, ''), (2, 'm'), (4, 'm'), (5, ''), (7, ''), (9, 'm'))
    chords = set()
    for step, quality in degrees:
        root = (tonic + step) % 12
        chords |= {(root, quality), (root, 'm7' if quality == 'm' else '7')}
    return chords


def heard_chords(times, harmony, bass, beats, phase=0, span=2, start=None, end=None, bass_weight=.15, change=.1,
                 key_weight=.04, seventh_cost=.04, seventh_ratio=1.0, quiet=.15):
    """Chords read from the sound itself, as the commercial transcribers do, instead of from the notes already
    written: `harmony` and `bass` are Basic Pitch activations (frames × 88) of the harmony instruments and of the bass
    (with separated stems, the "other" and "bass" stems; for a whole mix, the mix for both), at frame `times`.

    One guess per `span` beats from the bar phase. Each segment sums its activations into a pitch-class profile and a
    bass profile (lowest key per frame), and every chord is scored by its cosine match with the profile, a bonus for the
    share of the bass on its root, a small bonus for the chords of the key and a small cost for the four-note chords —
    a seventh also needs its seventh at least as loud as the triad (`seventh_ratio`), so a passing note in the melody
    does not turn Cm into Cm7. A Viterbi pass then picks the sequence, charging `change` for each new chord, so a
    single unclear segment keeps the chord around it. Silent segments (below `quiet` × the median energy) get no chord.
    Returns (start, end, root, quality) in seconds, between `start` and `end` when given."""
    beats = np.asarray(beats, np.float64)
    if len(beats) < span + 1 or not len(times): return []
    chroma, low = frame_chroma(harmony), lowest_chroma(bass)
    segments = []
    for index in range(phase % span, len(beats) - 1, span):
        a, b = float(beats[index]), float(beats[min(index + span, len(beats) - 1)])
        if end is not None and a >= end - .05: break
        if start is not None and b <= start + .05: continue
        inside = (times >= a) & (times < b)
        segments.append((a, b, chroma[inside].sum(axis=0), low[inside].sum(axis=0), max(int(inside.sum()), 1)))
    if not segments: return []
    energy = np.array([(h.sum() + l.sum()) / frames for _, _, h, l, frames in segments])
    if not (energy > 0).any(): return []
    voiced = energy >= quiet * float(np.median(energy[energy > 0]))
    tonic, minor = key_guess(sum(h + l for _, _, h, l, _ in segments))
    favoured = diatonic_chords(tonic, minor)
    states = [(root, quality) for root in range(12) for quality in CHORD_TEMPLATES]
    templates = np.array([[1.0 if (pc - root) % 12 in CHORD_TEMPLATES[quality] else 0.0 for pc in range(12)] for root, quality in states])
    templates /= np.linalg.norm(templates, axis=1, keepdims=True)
    roots = np.array([root for root, _ in states])
    prior = np.array([(key_weight if state in favoured else 0) - (seventh_cost if len(CHORD_TEMPLATES[state[1]]) == 4 else 0) for state in states])
    sevenths = [(i, [(root + t) % 12 for t in CHORD_TEMPLATES[quality][:3]], (root + CHORD_TEMPLATES[quality][3]) % 12)
                for i, (root, quality) in enumerate(states) if len(CHORD_TEMPLATES[quality]) == 4]
    def emission(h, l):
        profile = h + .5 * l
        norm = float(np.linalg.norm(profile))
        score = templates @ (profile / norm) if norm > 0 else np.zeros(len(states))
        if l.sum() > 0: score = score + bass_weight * l[roots] / l.sum()
        for i, triad, seventh in sevenths:
            if h[seventh] < seventh_ratio * h[triad].mean(): score[i] -= 1
        return score + prior
    result = []
    def decode(run):
        scores = emission(*segments[run[0]][2:4]); back = []
        for j in run[1:]:
            leap = scores.max() - change
            back.append(np.where(scores >= leap, np.arange(len(states)), int(scores.argmax())))
            scores = np.maximum(scores, leap) + emission(*segments[j][2:4])
        path = [int(scores.argmax())]
        for pointers in reversed(back): path.append(int(pointers[path[-1]]))
        for j, state in zip(run, reversed(path)):
            a, b = segments[j][:2]; root, quality = states[state]
            if result and result[-1][2:] == (root, quality) and abs(result[-1][1] - a) < 1e-6: result[-1] = (result[-1][0], b, root, quality)
            else: result.append((a, b, root, quality))
    run = []
    for j in range(len(segments)):
        if voiced[j]: run.append(j)
        elif run: decode(run); run = []
    if run: decode(run)
    if end is not None and result and result[-1][1] > end: result[-1] = (result[-1][0], max(result[-1][0] + .05, end), *result[-1][2:])
    return result


def chord_notes(chords):
    """Block chords for the MIDI: the root in the bass (C2–B2) and the triad or seventh above C3."""
    notes = []
    for start, end, root, quality in chords:
        notes.append((start, end, 36 + root, .45))
        for interval in CHORD_TEMPLATES[quality]: notes.append((start, end, 48 + (root + interval) % 12, .35))
    return notes


def melody_is_clear(melody, beats):
    """A sung or played tune: enough notes per beat and a real range. Otherwise the score should be chords only."""
    if len(beats) < 4 or len(melody) < 8: return False
    pitches = [n[2] for n in melody]
    return len(melody) / len(beats) >= .35 and max(pitches) - min(pitches) >= 5


def write_midi(path, melody, accompaniment, beats, bpm, ppq=480, phase=0, chords=(), meter=4, names=('Melodia (audio)', 'Acompanhamento (audio)', 'Acordes (audio)')):
    """Type 1 MIDI: tempo and 4/4, melody on track 1 (piano, channel 1), accompaniment on track 2 (channel 2) and, when
    given, the chords heard in the audio as block chords on track 3 (channel 3). Beat `phase` of the grid falls on a
    bar line."""
    chords = list(chords)
    first = to_ticks(min([n[0] for n in melody + accompaniment + chords] or [0]), beats, ppq) + (meter - phase) % meter * ppq
    shift = (meter - phase) % meter * ppq - min(0, first // (meter * ppq) * (meter * ppq))  # never negative ticks; keep whole measures
    # Subdivision by beat (sixteenths, triplets or swung eighths), decided from the melody and accompaniment attacks.
    grids, swing = beat_grids([to_ticks(n[0], beats, ppq) + shift for n in melody + accompaniment], ppq)
    bar = meter * ppq
    # Anacrusis: notes leading into the first downbeat start a short first measure instead of a measure of rests.
    starts = [snap(to_ticks(n[0], beats, ppq) + shift, grids, ppq) for n in melody + accompaniment + chords]
    lead = min(starts) if starts else 0
    skip = lead // bar * bar
    pickup = 0
    if lead - skip >= ppq:
        pickup = bar - (lead - skip) // ppq * ppq
        skip = lead // ppq * ppq
    write_midi.last = {'swing': swing, 'triplet_beats': sum(1 for g in grids.values() if g == 'triplet'), 'pickup_beats': pickup // ppq}
    def vlq(value):
        out = [value & 127]; value >>= 7
        while value: out.insert(0, (value & 127) | 128); value >>= 7
        return bytes(out)
    def track(notes, channel, name):
        events = [(0, b'\xff\x03' + vlq(len(name.encode('latin-1'))) + name.encode('latin-1')), (0, bytes([0xC0 | channel, 0]))]
        # A live recording jitters a little around the beat; written music does not: every time goes onto the grid.
        struck = set()
        for start, end, pitch, amplitude in notes:
            a = snap(to_ticks(start, beats, ppq) + shift, grids, ppq) - skip
            # Two attacks of one pitch that fall on the same grid position are one written note.
            if (a, pitch) in struck: continue
            struck.add((a, pitch))
            b = max(a + ppq // 4, snap(to_ticks(end, beats, ppq) + shift, grids, ppq) - skip)
            velocity = max(30, min(120, int(40 + amplitude * 100)))
            events += [(a, bytes([0x90 | channel, pitch, velocity])), (b, bytes([0x80 | channel, pitch, 0]))]
        body = b''; previous = 0
        for tick, message in sorted(events, key=lambda e: (e[0], e[1][0] & 0xF0 != 0x80)):
            body += vlq(tick - previous) + message; previous = tick
        body += b'\x00\xff\x2f\x00'
        return b'MTrk' + struct.pack('>I', len(body)) + body
    tempo = int(round(60_000_000 / max(30, min(240, round(bpm)))))
    signature = b'\xff\x58\x04' + bytes([meter]) + b'\x02\x18\x08'
    # A pickup is a short first measure: the same meter restated at the first downbeat closes it early.
    conductor = b'\x00\xff\x51\x03' + tempo.to_bytes(3, 'big') + b'\x00' + signature + (vlq(pickup) + signature if pickup else b'') + b'\x00\xff\x2f\x00'
    data = b'MThd' + struct.pack('>IHHH', 6, 1, 4 if chords else 3, ppq) + b'MTrk' + struct.pack('>I', len(conductor)) + conductor
    data += track(melody, 0, names[0]) + track(accompaniment, 1, names[1])
    if chords: data += track(chords, 2, names[2])
    Path(path).write_bytes(data)


INSTRUMENT_GAP = 2.0       # segundos sem voz a partir dos quais a melodia vem do solo instrumental
INSTRUMENT_MIN_AMPLITUDE = .35
ACCOMPANIMENT_MIN_SECONDS = .3


SINGING_WINDOW = 4.0       # segundos da janela em que se mede quanto a voz canta
SINGING_COVERAGE = .35     # canto contínuo cobre 40–70% da janela; gritos e falas soltas, no máximo uns 30%


def sung_regions(vocal, end, window=SINGING_WINDOW, threshold=SINGING_COVERAGE, step=.25, close=2.0, shortest=3.0, reach=2.0, link=.6):
    """Trechos em que a voz canta de verdade: a fração de uma janela de `window` segundos coberta por notas da voz
    passa de `threshold`. Interjeições do cantor por cima de um solo ("ai, ai", o nome da banda) ficam abaixo disso,
    e o solo segue como melodia. Trechos separados por menos de `close` segundos se juntam, trechos com menos de
    `shortest` segundos são descartados, e cada trecho se estende às notas da voz encadeadas a ele (pausas de menos de
    `link` segundos) até `reach` segundos das bordas (a janela centrada atrasa o começo e adianta o fim). Retorna [(início, fim)] em segundos."""
    vocal = sorted(vocal)
    if not vocal: return []
    end = max(end or 0, vocal[-1][1])
    times = np.arange(0, end + step, step)
    covered = np.zeros(len(times))
    for s, e, _, _ in vocal:                      # fração exata de cada intervalo de `step` coberta pela nota
        for k in range(int(s / step), min(len(times), int(math.ceil(e / step)))):
            covered[k] += max(0.0, min(e, (k + 1) * step) - max(s, k * step)) / step
    covered = np.minimum(covered, 1)
    width = max(1, int(round(window / step)))
    coverage = np.convolve(covered, np.ones(width) / width, mode='same')
    regions = []
    active = coverage >= threshold
    i = 0
    while i < len(times):
        if not active[i]: i += 1; continue
        j = i
        while j < len(times) and active[j]: j += 1
        a, b = float(times[i]), float(times[j - 1]) + step
        if regions and a - regions[-1][1] < close: regions[-1] = (regions[-1][0], b)
        else: regions.append((a, b))
        i = j
    result = []
    for a, b in regions:
        if b - a < shortest: continue
        # Extensão só por notas encadeadas (pausas de menos de `link` segundos), até `reach` segundos da borda.
        start, finish = a, b
        for s, e, _, _ in reversed(vocal):
            if s < start and e >= start - link and s >= a - reach: start = s
        for s, e, _, _ in vocal:
            if e > finish and s <= finish + link and e <= b + reach: finish = e
        result.append((start, finish))
    return result


def fill_gaps(vocal, instrument, gap=INSTRUMENT_GAP, margin=.15, minimum_amplitude=INSTRUMENT_MIN_AMPLITUDE, end=None, keep=.25):
    """A melodia: a voz onde ela canta de verdade (sung_regions) e o solo instrumental no resto (introdução,
    interlúdios, final, e silêncios da voz de pelo menos `gap` segundos dentro do canto): notas da linha
    instrumental que começam fora da voz. Uma nota longa do solo que ainda soa quando a voz entra (a nota final de
    uma frase da sanfona) é encurtada até ali, se o que sobra tem pelo menos `keep` segundos. notes em segundos
    (início, fim, altura, amplitude); retorna (melodia, quantas notas vieram do instrumento)."""
    regions = sung_regions(vocal, end)
    vocal = sorted(n for n in vocal if any(a <= (n[0] + n[1]) / 2 <= b for a, b in regions))
    edges = [-1.0] + [x for s, e, _, _ in vocal for x in (s, e)] + [end if end is not None else max([n[1] for n in instrument + vocal] or [0]) + 1]
    silences = [(edges[i], edges[i + 1]) for i in range(0, len(edges) - 1, 2) if edges[i + 1] - edges[i] >= gap]
    added = []
    for s, e, pitch, amplitude in instrument:
        if amplitude < minimum_amplitude: continue
        for a, b in silences:
            if a + margin <= s and min(e, b - margin) - s >= min(e - s, keep):
                added.append((s, min(e, b - margin), pitch, amplitude)); break
    return sorted(vocal + added), len(added)


def transcribe(wav_path, midi_path, progress=lambda message: None, separate=None):
    """Full pipeline; returns a report with counts, tempo and the melody range. With the source-separation model
    installed (or separate=True) the recording is split into voice, bass, drums and the other instruments first, as the
    commercial transcribers do; without it, the whole mix goes through the older skyline method."""
    if separate is None:
        import source_separation
        separate = source_separation.available()
    if separate: return transcribe_separated(wav_path, midi_path, progress)
    return transcribe_mix(wav_path, midi_path, progress)


def transcribe_separated(wav_path, midi_path, progress=lambda message: None):
    """Gravação → voz, baixo, bateria e outros (HTDemucs) → a melodia é a linha principal da voz, completada pelo solo
    instrumental onde ninguém canta; o acompanhamento e os acordes vêm do baixo e dos outros instrumentos, sem a
    bateria; o pulso vem da bateria."""
    import source_separation as ss
    progress('Lendo o áudio…')
    raw, rate = read_wav(wav_path, mono=False)
    if len(raw) < rate * 2: raise ValueError('Áudio curto demais (menos de 2 segundos).')
    peak = float(np.abs(raw).max())
    if peak < 1e-4: raise ValueError('O áudio está em silêncio.')
    stereo = ss.to_stereo_44k(raw / peak * .9, rate); del raw
    progress('Separando a voz e os instrumentos…')
    stems = ss.separate(stereo, lambda done, total: progress(f'Separando a voz e os instrumentos… {round(100 * done / total)}%'))
    seconds_total = stereo.shape[1] / ss.SAMPLE_RATE; del stereo
    progress('Ouvindo a voz, o baixo e os instrumentos…')
    import onnxruntime
    session = onnxruntime.InferenceSession(str(MODEL), providers=['CPUExecutionProvider'])
    heard = {}; mono = {}
    for name in ('vocals', 'other', 'bass'):
        mono[name] = resample(stems[name].mean(axis=0), ss.SAMPLE_RATE)
        heard[name] = posteriors(mono[name], session)
    drums = onset_envelope(resample(stems['drums'].mean(axis=0), ss.SAMPLE_RATE)); del stems
    count = len(heard['vocals'][0]); times = frame_times(count)
    # Só há voz onde a pista da voz tem energia; o resto é vazamento dos instrumentos.
    sung = presence(mono['vocals'], count); syllables = syllable_onsets(mono['vocals'], count); del mono
    heard['vocals'] = (heard['vocals'][0] * sung[:, None], heard['vocals'][1] * sung[:, None])
    def seconds(frame): return float(times[min(frame, count - 1)])
    def in_seconds(events): return [(seconds(a), seconds(b), pitch, amplitude) for a, b, pitch, amplitude in events]
    progress('Seguindo a melodia da voz…')
    vocal = in_seconds(predominant_line(heard['vocals'][0], heard['vocals'][1], *VOICE_RANGE, syllables=syllables))
    # A linha do instrumento sem o pedal interno da sanfona: só a voz de cima, que é a melodia.
    instrument = upper_voice(in_seconds(predominant_line(heard['other'][0], heard['other'][1], *INSTRUMENT_RANGE, rise=.3)))
    melody, solo_notes = fill_gaps(vocal, instrument, end=seconds_total)
    # Acompanhamento: o baixo e as notas sustentadas dos outros instrumentos. As notas curtas (ponteios, floreios da
    # sanfona) fariam a análise de harmonia ver um acorde novo a cada tempo.
    accompaniment = sorted([n for n in in_seconds(detect_notes(*heard['other'][:2])) if n[1] - n[0] >= ACCOMPANIMENT_MIN_SECONDS] +
                           [n for n in in_seconds(detect_notes(*heard['bass'][:2])) if n[2] < 60])
    if len(melody) < 8 and len(accompaniment) < 8: raise ValueError('Quase nenhuma nota reconhecida neste áudio.')
    progress('Encontrando o pulso da música…')
    envelope = drums[:count] / (drums.std() or 1)
    bass_onsets = heard['bass'][1].sum(axis=1)
    envelope = envelope + .5 * bass_onsets[:len(envelope)] / (bass_onsets.std() or 1)
    beats, bpm = beat_track(envelope)
    beats = steady_grid(beats, melody + accompaniment)
    if len(beats) >= 2: bpm = 60 / float(np.median(np.diff(beats)))
    meter = meter_guess(accompaniment or melody, beats)
    phase = downbeat_phase(accompaniment or melody, beats, meter=meter)
    progress('Reconhecendo os acordes pelo som…')
    # Os acordes vêm do som do baixo e dos outros instrumentos, não das notas já escritas: as notas curtas da sanfona
    # ficaram de fora do acompanhamento, e a melodia tocada pela sanfona confundiria a harmonia.
    written = melody + accompaniment
    chords = heard_chords(times, heard['other'][0], heard['bass'][0], beats, phase, span=2 if meter == 4 else 3,
                          start=min(n[0] for n in written), end=max(n[1] for n in written)) or audio_chords(melody, accompaniment, beats, phase, span=2 if meter == 4 else 3)
    write_midi(midi_path, melody, accompaniment, beats, bpm, phase=phase, chords=chord_notes(chords), meter=meter)
    pitches = [n[2] for n in melody]
    return {'notes': len(melody) + len(accompaniment), 'melody_notes': len(melody), 'accompaniment_notes': len(accompaniment),
            'chords': [NOTE_NAMES[root] + quality for _, _, root, quality in chords], 'melody_clear': melody_is_clear(melody, beats),
            'bpm': round(bpm, 1), 'meter': meter, 'beats': len(beats), 'downbeat_phase': phase, 'seconds': round(seconds_total, 1),
            'melody_range': [min(pitches), max(pitches)] if pitches else None, 'separated': True,
            'vocal_notes': len(melody) - solo_notes, 'solo_notes': solo_notes, **write_midi.last}


def transcribe_mix(wav_path, midi_path, progress=lambda message: None):
    """The older pipeline on the whole mix (no separation model): skyline melody."""
    progress('Lendo o áudio…')
    audio, rate = read_wav(wav_path)
    if len(audio) < rate * 2: raise ValueError('Áudio curto demais (menos de 2 segundos).')
    peak = float(np.abs(audio).max())
    if peak < 1e-4: raise ValueError('O áudio está em silêncio.')
    audio = resample(audio / peak * .9, rate)
    progress('Ouvindo as notas com o modelo Basic Pitch…')
    frames, onsets, _ = posteriors(audio)
    times = frame_times(len(frames))
    events = detect_notes(frames, onsets)
    def seconds(frame): return float(times[min(frame, len(times) - 1)])
    notes = [(seconds(a), seconds(b), pitch, amplitude) for a, b, pitch, amplitude in events]
    if len(notes) < 8: raise ValueError('Quase nenhuma nota reconhecida neste áudio.')
    progress('Encontrando o pulso da música…')
    beats, bpm = beat_track(onsets.sum(axis=1))
    beats = steady_grid(beats, notes)
    if len(beats) >= 2: bpm = 60 / float(np.median(np.diff(beats)))
    melody, accompaniment = skyline(notes)
    # Compasso pelos acentos, como na gravação pelo teclado (3 ou 4 tempos).
    meter = meter_guess(notes, beats)
    phase = downbeat_phase(accompaniment or melody, beats, meter=meter)
    progress('Reconhecendo os acordes pelo som…')
    chords = heard_chords(times, frames, frames, beats, phase, span=2 if meter == 4 else 3, start=min(n[0] for n in notes),
                          end=max(n[1] for n in notes)) or audio_chords(melody, accompaniment, beats, phase, span=2 if meter == 4 else 3)
    write_midi(midi_path, melody, accompaniment, beats, bpm, phase=phase, chords=chord_notes(chords), meter=meter)
    pitches = [n[2] for n in melody]
    return {'notes': len(notes), 'melody_notes': len(melody), 'accompaniment_notes': len(accompaniment),
            'chords': [NOTE_NAMES[root] + quality for _, _, root, quality in chords], 'melody_clear': melody_is_clear(melody, beats),
            'bpm': round(bpm, 1), 'meter': meter, 'beats': len(beats), 'downbeat_phase': phase, 'seconds': round(len(audio) / SAMPLE_RATE, 1),
            'melody_range': [min(pitches), max(pitches)] if pitches else None, **write_midi.last}


def transcribe_notes(played, midi_path, progress=lambda message: None):
    """Notes played on a MIDI keyboard → a clean MIDI, like the audio pipeline: the tempo is found from the player's
    own attacks (the beat follows the player when they speed up or slow down), every note is placed on that beat grid
    in sixteenths, the measure (3 or 4 beats) and its first beat come from the accents, the top line becomes the melody
    and the rest the accompaniment, and the chords are read from the accompaniment. `played` holds dicts with start
    and end in seconds, pitch and velocity (the sustain pedal is already applied to the ends)."""
    notes = sorted((float(n['start']), max(float(n['end']), float(n['start']) + .05), int(n['pitch']), max(1, min(127, int(n.get('velocity', 80)))) / 127)
                   for n in played if 0 <= int(n['pitch']) <= 127)
    if len(notes) < 4: raise ValueError('Toque pelo menos algumas notas antes de transformar em partitura.')
    origin = notes[0][0]
    notes = [(s - origin, e - origin, p, v) for s, e, p, v in notes]
    progress('Encontrando o andamento do seu jeito de tocar…')
    length = int((max(e for _, e, _, _ in notes) + 1) * FRAMES_PER_SECOND) + 1
    envelope = np.zeros(max(length, int(2.5 * FRAMES_PER_SECOND)))
    for start, end, pitch, velocity in notes:
        envelope[int(start * FRAMES_PER_SECOND)] += velocity * (1.5 if pitch < 55 else 1)
    kernel = np.exp(-.5 * (np.arange(-4, 5) / 1.5) ** 2)
    envelope = np.convolve(envelope, kernel, mode='same')
    beats, bpm = beat_track(envelope)
    beats = steady_grid(beats, notes)
    if len(beats) >= 2: bpm = 60 / float(np.median(np.diff(beats)))
    melody, accompaniment = skyline(notes)
    meter = meter_guess(notes, beats)
    phase = downbeat_phase(accompaniment or melody, beats, meter=meter)
    progress('Lendo os acordes que você tocou…')
    chords = audio_chords(melody, accompaniment, beats, phase, span=2 if meter == 4 else 3)
    write_midi(midi_path, melody, accompaniment, beats, bpm, phase=phase, chords=chord_notes(chords), meter=meter,
               names=('Melodia (gravada)', 'Acompanhamento (gravado)', 'Acordes (gravados)'))
    pitches = [n[2] for n in melody]
    return {'notes': len(notes), 'melody_notes': len(melody), 'accompaniment_notes': len(accompaniment),
            'chords': [NOTE_NAMES[root] + quality for _, _, root, quality in chords], 'melody_clear': melody_is_clear(melody, beats),
            'bpm': round(bpm, 1), 'meter': meter, 'beats': len(beats), 'downbeat_phase': phase,
            'seconds': round(max(e for _, e, _, _ in notes), 1), 'melody_range': [min(pitches), max(pitches)] if pitches else None, **write_midi.last}
