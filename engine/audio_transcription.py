"""Audio recording → MIDI with a melody track and an accompaniment track, so the usual pipeline can make the score.

Notes come from Spotify's Basic Pitch model (engine/models/basic-pitch/nmp.onnx, Apache 2.0) run with onnxruntime; the
note-building step follows Basic Pitch's own algorithm. A dynamic-programming beat tracker (Ellis 2007) finds the pulse,
and every time is mapped onto those beats, so the MIDI measures follow the music even when the tempo drifts. The
melody is the highest sustained line (skyline); everything else becomes the accompaniment the chords are read from.
The result is an honest starting point: a full band recording usually needs the melody reviewed."""
from pathlib import Path
import struct, math
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


def read_wav(path):
    """Mono float32 samples and the sample rate of a PCM (8/16/24/32-bit) or IEEE-float WAV file."""
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
    audio = audio[:len(audio) // channels * channels].reshape(-1, channels).mean(axis=1)
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
    import onnxruntime
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
    def vlq(value):
        out = [value & 127]; value >>= 7
        while value: out.insert(0, (value & 127) | 128); value >>= 7
        return bytes(out)
    def track(notes, channel, name):
        events = [(0, b'\xff\x03' + vlq(len(name.encode('latin-1'))) + name.encode('latin-1')), (0, bytes([0xC0 | channel, 0]))]
        grid = ppq // 4  # sixteenth: a live recording jitters a little around the beat; written music does not
        for start, end, pitch, amplitude in notes:
            a = round((to_ticks(start, beats, ppq) + shift) / grid) * grid
            b = max(a + grid, round((to_ticks(end, beats, ppq) + shift) / grid) * grid)
            velocity = max(30, min(120, int(40 + amplitude * 100)))
            events += [(a, bytes([0x90 | channel, pitch, velocity])), (b, bytes([0x80 | channel, pitch, 0]))]
        body = b''; previous = 0
        for tick, message in sorted(events, key=lambda e: (e[0], e[1][0] & 0xF0 != 0x80)):
            body += vlq(tick - previous) + message; previous = tick
        body += b'\x00\xff\x2f\x00'
        return b'MTrk' + struct.pack('>I', len(body)) + body
    tempo = int(round(60_000_000 / max(30, min(240, round(bpm)))))
    conductor = b'\x00\xff\x51\x03' + tempo.to_bytes(3, 'big') + b'\x00\xff\x58\x04' + bytes([meter]) + b'\x02\x18\x08\x00\xff\x2f\x00'
    data = b'MThd' + struct.pack('>IHHH', 6, 1, 4 if chords else 3, ppq) + b'MTrk' + struct.pack('>I', len(conductor)) + conductor
    data += track(melody, 0, names[0]) + track(accompaniment, 1, names[1])
    if chords: data += track(chords, 2, names[2])
    Path(path).write_bytes(data)


def transcribe(wav_path, midi_path, progress=lambda message: None):
    """Full pipeline; returns a report with counts, tempo and the melody range."""
    progress('Lendo o áudio…')
    audio, rate = read_wav(wav_path)
    if len(audio) < rate * 2: raise ValueError('Áudio curto demais (menos de 2 segundos).')
    peak = float(np.abs(audio).max())
    if peak < 1e-4: raise ValueError('O áudio está em silêncio.')
    audio = resample(audio / peak * .9, rate)
    progress('Ouvindo as notas com o modelo Basic Pitch…')
    frames, onsets, _ = posteriors(audio)
    times = frame_times(len(frames))
    events = build_notes(frames, onsets)
    def seconds(frame): return float(times[min(frame, len(times) - 1)])
    notes = [(seconds(a), seconds(b), pitch, amplitude) for a, b, pitch, amplitude in events]
    if len(notes) < 8: raise ValueError('Quase nenhuma nota reconhecida neste áudio.')
    progress('Encontrando o pulso da música…')
    beats, bpm = beat_track(onsets.sum(axis=1))
    beats = steady_grid(beats, notes)
    if len(beats) >= 2: bpm = 60 / float(np.median(np.diff(beats)))
    melody, accompaniment = skyline(notes)
    phase = downbeat_phase(accompaniment or melody, beats)
    progress('Reconhecendo os acordes pelo som…')
    chords = audio_chords(melody, accompaniment, beats, phase)
    write_midi(midi_path, melody, accompaniment, beats, bpm, phase=phase, chords=chord_notes(chords))
    pitches = [n[2] for n in melody]
    return {'notes': len(notes), 'melody_notes': len(melody), 'accompaniment_notes': len(accompaniment),
            'chords': [NOTE_NAMES[root] + quality for _, _, root, quality in chords], 'melody_clear': melody_is_clear(melody, beats),
            'bpm': round(bpm, 1), 'beats': len(beats), 'downbeat_phase': phase, 'seconds': round(len(audio) / SAMPLE_RATE, 1),
            'melody_range': [min(pitches), max(pitches)] if pitches else None}


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
            'seconds': round(max(e for _, e, _, _ in notes), 1), 'melody_range': [min(pitches), max(pitches)] if pitches else None}
