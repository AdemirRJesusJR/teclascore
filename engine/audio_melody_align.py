"""Melody from a recording for a MIDI that has only the accompaniment.

The recording (ideally the voice alone, but a full mix works) is transcribed with Basic Pitch (audio_transcription), and
its time line is aligned with the MIDI's: both become pitch-class profiles over time (chromagrams; the MIDI from its own
notes), and the alignment is the time offset, tempo factor and transposition where the two agree best; the MIDI
attacks against the recording's onsets then fix the exact moment. A finer pass follows tempo drifts: a constrained time
warping by dynamic programming over local offsets (8-second windows scored by attacks and harmony, with a cost for every
change of offset), kept only when the attacks line up clearly better than with the straight line. The highest line of
the recording that the MIDI does not already play becomes the melody; its times go through the inverse map into MIDI seconds and then through the
MIDI's own tempo map into ticks. A new copy of the MIDI receives it as an extra track; the original is never changed.

Nothing is invented: when the recording does not match the MIDI (low correlation, an ambiguous offset, a melody that
falls mostly outside the harmony), the alignment is refused with a message and no file is written."""
from pathlib import Path
import bisect, json, math, struct, os
import numpy as np
import audio_transcription as at
from inspect_midi import parse_midi

TRACK_NAME = 'Melodia (gravação)'
MELODY_PROGRAM = 73                 # flute: stands out over the accompaniment when both are heard
COARSE_RATE, COARSE_SMOOTH = 10, 8  # frames per second and smoothing (frames) of the global search
FINE_RATE, FINE_SMOOTH = 20, 4
MIN_CORRELATION = .3                # mean per-frame Pearson correlation of the chromagrams
MIN_DISTINCTNESS = 1.4              # best offset against the best one more than 3 s away
MIN_COMPATIBLE = .45                # share of melody duration on pitch classes the MIDI is sounding
MIN_COVERAGE = .5                   # share of the MIDI covered by the recording
MIN_MELODY_NOTES = 8
MATCHING_WINDOW = .35               # an 8-second stretch "agrees" at this mean correlation (reported only)


class AlignmentRefused(ValueError):
    """The recording does not fit the MIDI well enough to place a melody on it."""


# ---- MIDI time ---------------------------------------------------------------------------------------------------

class TempoMap:
    """Ticks ↔ seconds through the MIDI's tempo changes (500000 µs per quarter until the first one)."""
    def __init__(self, parsed):
        self.ppq = parsed['ppq']
        changes = {}
        for e in parsed['events']:
            if e['kind'] == 'meta' and e['type'] == 81 and len(e['data']) == 3:
                value = int.from_bytes(e['data'], 'big')
                if value > 0: changes[e['tick']] = value
        ticks, us = [0], [500000]
        for tick, value in sorted(changes.items()):
            if tick == ticks[-1]: us[-1] = value
            else: ticks.append(tick); us.append(value)
        seconds = [0.0]
        for i in range(1, len(ticks)): seconds.append(seconds[-1] + (ticks[i] - ticks[i - 1]) / self.ppq * us[i - 1] / 1e6)
        self.ticks, self.us, self.seconds = np.array(ticks, float), np.array(us, float), np.array(seconds)

    def to_seconds(self, ticks):
        ticks = np.asarray(ticks, float)
        i = np.clip(np.searchsorted(self.ticks, ticks, 'right') - 1, 0, len(self.ticks) - 1)
        return self.seconds[i] + (ticks - self.ticks[i]) / self.ppq * self.us[i] / 1e6

    def to_ticks(self, seconds):
        seconds = np.asarray(seconds, float)
        i = np.clip(np.searchsorted(self.seconds, seconds, 'right') - 1, 0, len(self.seconds) - 1)
        return self.ticks[i] + (seconds - self.seconds[i]) * 1e6 / self.us[i] * self.ppq


class TimeMap:
    """Monotonic piecewise-linear map between MIDI seconds and recording seconds; beyond the anchors the global slope
    continues (recording seconds per MIDI second = 1 / factor)."""
    def __init__(self, midi_points, audio_points, factor):
        self.m, self.a, self.factor = np.asarray(midi_points, float), np.asarray(audio_points, float), float(factor)

    @classmethod
    def linear(cls, offset, factor): return cls([0.0], [offset], factor)

    def to_audio(self, t):
        t = np.asarray(t, float); slope = 1 / self.factor
        if len(self.m) == 1: return self.a[0] + (t - self.m[0]) * slope
        inner = np.interp(t, self.m, self.a)
        return np.where(t < self.m[0], self.a[0] + (t - self.m[0]) * slope, np.where(t > self.m[-1], self.a[-1] + (t - self.m[-1]) * slope, inner))

    def to_midi(self, t):
        t = np.asarray(t, float)
        if len(self.m) == 1: return self.m[0] + (t - self.a[0]) * self.factor
        inner = np.interp(t, self.a, self.m)
        return np.where(t < self.a[0], self.m[0] + (t - self.a[0]) * self.factor, np.where(t > self.a[-1], self.m[-1] + (t - self.a[-1]) * self.factor, inner))


def midi_notes(parsed, tempo):
    """Pitched notes of the MIDI as arrays (start s, end s, pitch, weight)."""
    notes = [n for n in parsed['notes'] if n['channel'] != 10 and n['end'] > n['start']]
    if not notes: raise AlignmentRefused('O MIDI não tem notas com altura definida para comparar com a gravação.')
    starts = tempo.to_seconds([n['start'] for n in notes]); ends = tempo.to_seconds([n['end'] for n in notes])
    return starts, ends, np.array([n['pitch'] for n in notes]), np.array([n['velocity'] / 127 for n in notes])


# ---- Chromagrams -------------------------------------------------------------------------------------------------

def notes_chroma(starts, ends, pitches, weights, rate, length):
    """Energy per pitch class and frame of a list of notes (held at constant weight while they sound)."""
    delta = np.zeros((length + 1, 12))
    a = np.clip(np.round(np.asarray(starts) * rate).astype(int), 0, length)
    b = np.clip(np.maximum(np.round(np.asarray(ends) * rate).astype(int), a + 1), 0, length)
    pcs = np.asarray(pitches) % 12
    np.add.at(delta, (a, pcs), weights); np.add.at(delta, (b, pcs), -np.asarray(weights))
    return np.cumsum(delta, axis=0)[:length]


def audio_chroma(frames, times, rate, length):
    """Basic Pitch note activations folded to pitch classes and averaged into frames of 1/rate seconds."""
    fold = np.zeros((frames.shape[1], 12)); fold[np.arange(frames.shape[1]), (np.arange(frames.shape[1]) + at.MIDI_OFFSET) % 12] = 1
    folded = frames @ fold
    index = np.floor(np.asarray(times) * rate).astype(int); valid = (index >= 0) & (index < length)
    chroma = np.zeros((length, 12)); np.add.at(chroma, index[valid], folded[valid])
    counts = np.bincount(index[valid], minlength=length)[:length]
    return chroma / np.maximum(counts, 1)[:, None]


def smooth(chroma, width):
    if width <= 1: return chroma
    padded = np.concatenate([np.zeros((1, 12)), np.cumsum(chroma, axis=0)])
    half = width // 2; n = len(chroma)
    hi = np.minimum(np.arange(n) + width - half, n); lo = np.maximum(np.arange(n) - half, 0)
    return (padded[hi] - padded[lo]) / (hi - lo)[:, None]


def normalize(chroma):
    """Each frame centered and scaled to unit length, so a dot product is the Pearson correlation of two profiles.
    Frames with almost no energy are left out (zero) and marked inactive."""
    energy = chroma.sum(axis=1)
    positive = energy[energy > 0]
    threshold = .05 * np.percentile(positive, 90) if len(positive) else np.inf
    centered = chroma - chroma.mean(axis=1, keepdims=True)
    norm = np.linalg.norm(centered, axis=1)
    active = (energy > threshold) & (norm > 1e-9)
    result = np.zeros_like(centered); result[active] = centered[active] / norm[active, None]
    return result, active


# ---- Global alignment --------------------------------------------------------------------------------------------

def cross_correlation(fa, fa_active, a_len, m, m_active, n):
    """Sum of frame correlations and count of active frame pairs for every lag (audio frame = MIDI frame + lag)."""
    fm = np.fft.rfft(m, n, axis=0); fm_active = np.fft.rfft(m_active.astype(float), n)
    sums = np.fft.irfft((fa * np.conj(fm)).sum(axis=1), n)
    counts = np.round(np.fft.irfft(fa_active * np.conj(fm_active), n))
    lags = np.arange(-(len(m) - 1), a_len)
    return lags, sums[lags % n], counts[lags % n]


def search(audio_frames, times, midi, factors, rotations, rate, width, lag_window=None):
    """Best (score, factor, rotation, lag, scores by lag) over the given tempo factors and transpositions. The score is
    the summed correlation over the whole of both sequences (so a partial overlap scores less than a full one)."""
    starts, ends, pitches, weights = midi
    duration = float(times[-1]) + 1
    a_len = int(duration * rate) + 1
    a_raw = smooth(audio_chroma(audio_frames, times, rate, a_len), width)
    n = 1 << int(math.ceil(math.log2(a_len + int(ends.max() / min(factors) * rate) + 2)))
    sides = []
    for rotation in rotations:
        a, a_active = normalize(np.roll(a_raw, rotation, axis=1))
        sides.append((rotation, a_active.sum(), np.fft.rfft(a, n, axis=0), np.fft.rfft(a_active.astype(float), n)))
    best = None
    for factor in factors:
        m_len = int(ends.max() / factor * rate) + 2
        m, m_active = normalize(smooth(notes_chroma(starts / factor, ends / factor, pitches, weights, rate, m_len), width))
        for rotation, a_count, fa, fa_active in sides:
            lags, sums, counts = cross_correlation(fa, fa_active, a_len, m, m_active, n)
            total = math.sqrt(max(1, a_count) * max(1, m_active.sum()))
            scores = np.where(counts >= .3 * min(a_count, m_active.sum()), sums / total, -1)
            if lag_window is not None:
                scores = np.where(np.abs(lags - lag_window[0]) <= lag_window[1], scores, -1)
            k = int(np.argmax(scores))
            if best is None or scores[k] > best[0]:
                best = (float(scores[k]), float(factor), int(rotation), int(lags[k]), lags, scores)
    return best


def distinctness(lags, scores, lag, rate):
    """How much the best offset beats the best one at least 3 s away (repeated sections give close rivals)."""
    far = np.abs(lags - lag) > 3 * rate
    rival = scores[far].max() if far.any() else -1
    best = scores[lags == lag][0]
    return float('inf') if rival <= 0 else float(best / rival)


def refine_lag(lags, scores, lag):
    """Sub-frame lag by a parabola through the peak and its neighbours."""
    i = int(np.searchsorted(lags, lag))
    if 0 < i < len(lags) - 1 and min(scores[i - 1], scores[i + 1]) > -1:
        y0, y1, y2 = scores[i - 1], scores[i], scores[i + 1]
        denominator = y0 - 2 * y1 + y2
        if denominator < 0: return lag + .5 * (y0 - y2) / denominator
    return float(lag)


# ---- Map quality and piecewise refinement ------------------------------------------------------------------------

class Features:
    """Fine features of both sides: the recording (transposed by the chosen rotation) and the MIDI in its own seconds."""
    def __init__(self, frames, times, midi, rotation, rate=FINE_RATE, width=FINE_SMOOTH):
        starts, ends, pitches, weights = midi
        self.rate = rate
        a_len = int((float(times[-1]) + 1) * rate) + 1
        self.audio, self.audio_active = normalize(np.roll(smooth(audio_chroma(frames, times, rate, a_len), width), rotation, axis=1))
        m_len = int(ends.max() * rate) + 2
        self.midi, self.midi_active = normalize(smooth(notes_chroma(starts, ends, pitches, weights, rate, m_len), width))

    def evaluate(self, time_map):
        """Mean frame correlation where both sides sound, and the share of the MIDI's sounding frames that the
        recording covers."""
        frames = np.flatnonzero(self.midi_active)
        if not len(frames): return 0.0, 0.0
        j = np.round(time_map.to_audio(frames / self.rate) * self.rate).astype(int)
        inside = (j >= 0) & (j < len(self.audio))
        both = inside.copy(); both[inside] = self.audio_active[j[inside]]
        if not both.any(): return 0.0, float(inside.mean())
        values = (self.audio[j[both]] * self.midi[frames[both]]).sum(axis=1)
        return float(values.mean()), float(inside.mean())


class Attacks:
    """The recording's onset activation and the MIDI's note attacks: chromagrams find the place, attacks the exact
    moment."""
    def __init__(self, onsets, times, midi):
        envelope = onsets.max(axis=1)
        self.envelope = envelope / (envelope.mean() or 1); self.times = np.asarray(times)
        self.attacks = np.unique(np.round(midi[0], 3))

    def score(self, time_map, lo=-np.inf, hi=np.inf, shift=0.0):
        """Mean onset activation (relative to its average) at the MIDI attacks mapped into the recording."""
        attacks = self.attacks[(self.attacks >= lo) & (self.attacks < hi)]
        if not len(attacks): return 0.0
        return float(np.interp(time_map.to_audio(attacks) + shift, self.times, self.envelope, left=0, right=0).mean())

    def best_shift(self, time_map, lo=-np.inf, hi=np.inf, reach=.15, step=.005):
        shifts = np.arange(-reach, reach + step / 2, step)
        values = [self.score(time_map, lo, hi, shift) for shift in shifts]
        return float(shifts[int(np.argmax(values))])


def local_map(attacks, linear, duration, features=None, window=8.0, hop=4.0, reach=1.0, step=.01, jump=1.0, prior=.5, harmony=10.0):
    """Tempo drifts as a smooth curve of local offsets, found by dynamic programming (a constrained time warping).
    The MIDI is cut into overlapping windows; in each, every offset within ±`reach` seconds of the global line is
    scored by how well the MIDI attacks fall on the recording's onsets. Attacks repeat every beat, so a window alone
    can lock onto the wrong beat; the best path through all windows pays for every change of offset between
    neighbours (`jump` per second) and for straying from the global line (`prior` per second), which keeps it on the
    real one. Returns (TimeMap or None, anchors)."""
    shifts = np.arange(-reach, reach + step / 2, step)
    centers, emissions = [], []
    for start in np.arange(0, max(duration - window / 2, hop), hop):
        inside = attacks.attacks[(attacks.attacks >= start) & (attacks.attacks < start + window)]
        centers.append(start + window / 2)
        if len(inside) < 4: emissions.append(np.zeros(len(shifts))); continue
        mapped = linear.to_audio(inside)[None, :] + shifts[:, None]
        values = np.interp(mapped.ravel(), attacks.times, attacks.envelope, left=0, right=0).reshape(mapped.shape).mean(axis=1)
        emission = values / values.max() if values.max() > 0 else values
        if features is not None:
            # The harmony tells beats apart where the attacks cannot: a chord change lands on its own beat.
            rate = features.rate
            frames = np.arange(int(start * rate), min(len(features.midi), int((start + window) * rate)))
            frames = frames[features.midi_active[frames]]
            if len(frames) >= 10:
                j = np.round((linear.to_audio(frames / rate)[None, :] + shifts[:, None]) * rate).astype(int)
                ok = (j >= 0) & (j < len(features.audio)); jj = np.clip(j, 0, len(features.audio) - 1)
                ok &= features.audio_active[jj]
                agreement = np.where(ok, (features.audio[jj] * features.midi[frames][None, :, :]).sum(axis=2), 0).sum(axis=1) / len(frames)
                emission = emission + harmony * (agreement - agreement.max())
        emissions.append(emission)
    if len(centers) < 3 or sum(e.any() for e in emissions) < 3: return None, 0
    cost = jump * np.abs(shifts[:, None] - shifts[None, :])
    score = emissions[0] - prior * np.abs(shifts); back = []
    for emission in emissions[1:]:
        candidates = score[:, None] - cost          # previous state (rows) → this state (columns)
        best = np.argmax(candidates, axis=0); back.append(best)
        score = candidates[best, np.arange(len(shifts))] + emission - prior * np.abs(shifts)
    state = int(np.argmax(score)); path = [state]
    for best in reversed(back): state = int(best[state]); path.append(state)
    offsets = shifts[path[::-1]]
    centers = np.array(centers)
    audio_t = linear.to_audio(centers) + offsets
    # Each anchor then settles on the exact moment of its window's attacks (a few hundredths at most).
    rough = TimeMap(centers, audio_t, linear.factor)
    audio_t = audio_t + np.array([attacks.best_shift(rough, c - window / 2, c + window / 2, reach=.06) if e.any() else 0.0
                                  for c, e in zip(centers, emissions)])
    if np.any(np.diff(audio_t) <= 0): return None, 0
    return TimeMap(centers, audio_t, linear.factor), len(centers)


# ---- Melody ------------------------------------------------------------------------------------------------------

def without_accompaniment(notes, midi, tolerance=.12):
    """Recorded notes that the MIDI itself plays (same pitch, attack within 120 ms, mostly overlapping) are the
    accompaniment heard in a full mix, not the voice. notes are (start, end, pitch, amplitude) in MIDI seconds."""
    starts, ends, pitches, _ = midi
    by_pitch = {}
    for s, e, p in zip(starts, ends, pitches): by_pitch.setdefault(int(p), []).append((s, e))
    by_pitch = {p: np.array(sorted(v)) for p, v in by_pitch.items()}
    kept, removed = [], 0
    for note in notes:
        same = by_pitch.get(note[2])
        if same is not None:
            near = same[np.abs(same[:, 0] - note[0]) <= tolerance]
            overlap = np.minimum(near[:, 1], note[1]) - np.maximum(near[:, 0], note[0]) if len(near) else np.array([])
            shorter = np.minimum(near[:, 1] - near[:, 0], note[1] - note[0]) if len(near) else np.array([])
            if len(near) and np.any(overlap >= .5 * shorter):
                removed += 1; continue
        kept.append(note)
    return kept, removed


def without_overtones(notes, attack=.08):
    """A note one or two octaves above another that starts with it and lasts about as long is
    usually its overtone heard as a note, not a second voice; the overtone is the weaker of the two (a quieter lower
    note re-struck with the melody is the accompaniment ringing on). notes are (start, end, pitch, amplitude)."""
    notes = sorted(notes); starts = [n[0] for n in notes]; kept = []
    for note in notes:
        lo, hi = bisect.bisect_left(starts, note[0] - attack), bisect.bisect_right(starts, note[0] + attack)
        ghost = any(other[2] in (note[2] - 12, note[2] - 24)
                    and min(other[1], note[1]) - max(other[0], note[0]) >= .5 * (note[1] - note[0]) and other[3] >= note[3]
                    for other in notes[lo:hi])
        if not ghost: kept.append(note)
    return kept


def melody_line(notes, attack=.08):
    """One note at a time, as sung: of notes attacked together the highest; a new attack ends the note before it (the
    recording's release tails overlap), except a much weaker lower note under a held one, which is accompaniment."""
    ordered = sorted(without_overtones(notes)); chosen = []
    i = 0
    while i < len(ordered):
        j = i
        while j + 1 < len(ordered) and ordered[j + 1][0] - ordered[i][0] < attack: j += 1
        chosen.append(max(ordered[i:j + 1], key=lambda n: n[2])); i = j + 1
    line = []
    for note in chosen:
        if line and note[0] < line[-1][1]:
            previous = line[-1]
            if note[2] < previous[2] - 2 and note[3] < previous[3] * .5 and note[0] < previous[0] + .5 * (previous[1] - previous[0]): continue
            line[-1] = (previous[0], note[0], previous[2], previous[3])
        line.append(note)
    return line


def window_scores(features, time_map, window=8.0, hop=4.0):
    """Mean frame correlation of each stretch of the MIDI under the map: a right alignment agrees almost everywhere,
    a chance match only here and there."""
    rate = features.rate; m, m_active = features.midi, features.midi_active
    result = []
    for start in np.arange(0, max(len(m) / rate - window / 2, hop), hop):
        frames = np.arange(int(start * rate), min(len(m), int((start + window) * rate)))
        frames = frames[m_active[frames]]
        if len(frames) < window * rate * .4: continue
        j = np.round(time_map.to_audio(frames / rate) * rate).astype(int)
        inside = (j >= 0) & (j < len(features.audio))
        if inside.mean() < .5: continue
        ok = inside.copy(); ok[inside] = features.audio_active[j[inside]]
        result.append(float((features.audio[j[ok]] * m[frames[ok]]).sum(axis=1).sum() / len(frames)))
    return result


def melody_ticks(melody, tempo, ppq, end_tick):
    """Melody (MIDI seconds) → monophonic notes in ticks on the beat subdivisions the attacks follow (sixteenths or
    triplets, decided beat by beat like the audio transcription). Returns (start, end, pitch, velocity)."""
    raw = [(float(tempo.to_ticks(max(0.0, s))), float(tempo.to_ticks(max(0.0, e))), p, a) for s, e, p, a in melody]
    grids, _ = at.beat_grids([r[0] for r in raw], ppq)
    snapped = []
    for start, end, pitch, amplitude in raw:
        a = int(at.snap(start, grids, ppq)); b = int(at.snap(end, grids, ppq))
        snapped.append([a, max(b, a + ppq // 4), int(pitch), max(30, min(120, int(40 + amplitude * 100)))])
    snapped.sort(key=lambda n: (n[0], -n[2]))
    result = []
    for note in snapped:
        if note[0] < 0 or note[0] >= end_tick: continue
        if result and note[0] == result[-1][0]: continue      # same attack: the higher note was kept
        if result: result[-1][1] = min(result[-1][1], note[0])
        result.append(note)
    return [tuple(n) for n in result if n[1] > n[0]]


def compatible_share(notes_ticks, parsed):
    """Share of the melody's duration on a pitch class the MIDI is sounding at the same time (a chord tone), counted
    only where the MIDI sounds at all."""
    pitched = [n for n in parsed['notes'] if n['channel'] != 10 and n['end'] > n['start']]
    starts = np.array([n['start'] for n in pitched]); ends = np.array([n['end'] for n in pitched]); pcs = np.array([n['pitch'] % 12 for n in pitched])
    order = np.argsort(starts); starts, ends, pcs = starts[order], ends[order], pcs[order]
    good = total = 0.0
    for a, b, pitch, _ in notes_ticks:
        upto = np.searchsorted(starts, b, 'left')
        sounding = ends[:upto] > a
        if not sounding.any(): continue
        duration = b - a; total += duration
        if pitch % 12 in set(pcs[:upto][sounding].tolist()): good += duration
    return good / total if total else 0.0


# ---- Writing the copy --------------------------------------------------------------------------------------------

def vlq(value):
    out = [value & 127]; value >>= 7
    while value: out.insert(0, (value & 127) | 128); value >>= 7
    return bytes(out)


def midi_with_track(source_bytes, notes, channel, program, name=TRACK_NAME):
    """The source MIDI's tracks unchanged plus one new track; a type 0 file becomes type 1 (same events, one more
    track). An RMID container is unwrapped."""
    data = source_bytes
    if data[:4] == b'RIFF' and data[8:12] == b'RMID':
        position = 12
        while position + 8 <= len(data):
            size = struct.unpack_from('<I', data, position + 4)[0]
            if data[position:position + 4] == b'data': data = data[position + 8:position + 8 + size]; break
            position += 8 + size + (size & 1)
    if data[:4] != b'MThd': raise ValueError('O MIDI original não tem um cabeçalho válido.')
    size = struct.unpack_from('>I', data, 4)[0]
    fmt, _, division = struct.unpack_from('>HHH', data, 8)
    if fmt > 1: raise ValueError('MIDI tipo 2 não é suportado.')
    chunks, position = [], 8 + size
    while position + 8 <= len(data):
        length = struct.unpack_from('>I', data, position + 4)[0]
        if data[position:position + 4] == b'MTrk' and position + 8 + length <= len(data): chunks.append(data[position:position + 8 + length])
        position += 8 + length
    label = name.encode('utf-8')
    events = [(0, b'\xff\x03' + vlq(len(label)) + label)]
    if program is not None: events.append((0, bytes([0xC0 | (channel - 1), program])))
    for start, end, pitch, velocity in notes:
        events += [(start, bytes([0x90 | (channel - 1), pitch, velocity])), (end, bytes([0x80 | (channel - 1), pitch, 0]))]
    body, previous = b'', 0
    for tick, message in sorted(events, key=lambda e: (e[0], e[1][0] & 0xF0 != 0x80)):
        body += vlq(tick - previous) + message; previous = tick
    body += b'\x00\xff\x2f\x00'
    header = b'MThd' + struct.pack('>IHHH', 6, 1, len(chunks) + 1, division)
    return header + b''.join(chunks) + b'MTrk' + struct.pack('>I', len(body)) + body, len(chunks)


def free_channel(parsed):
    """A channel no track uses (never 10): the new track gets its own instrument. When all are taken, the least used
    one is shared and keeps its instrument (no program change)."""
    used = {}
    for e in parsed['events']:
        if e['kind'] != 'meta' and 'channel' in e: used[e['channel']] = used.get(e['channel'], 0) + 1
    for channel in list(range(1, 10)) + list(range(11, 17)):
        if channel not in used: return channel, MELODY_PROGRAM
    channel = min((c for c in used if c != 10), key=lambda c: used[c])
    return channel, None


# ---- Pipeline ----------------------------------------------------------------------------------------------------

def percent(value): return f'{value * 100:.0f}%'


def align(wav_path, midi_path, output_midi, output_report=None, progress=lambda message: None, session=None, debug=None, audio_source=None):
    """Transcribes the recording, aligns it with the MIDI and writes the copy with the melody track and the report.
    Raises AlignmentRefused (nothing written) when the recording does not fit. audio_source names the original
    recording in the report when wav_path is a temporary decoded copy."""
    output_midi = Path(output_midi)
    output_report = Path(output_report) if output_report else output_midi.with_suffix('.alinhamento.json')
    for target in (output_midi, output_report):
        if target.exists(): raise ValueError(f'Já existe um arquivo em {target}; escolha outro nome.')
    source_bytes = Path(midi_path).read_bytes()
    parsed = parse_midi(midi_path)
    tempo = TempoMap(parsed)
    midi = midi_notes(parsed, tempo)
    midi_seconds = float(midi[1].max())

    progress('Lendo a gravação…')
    audio, rate = at.read_wav(wav_path)
    if len(audio) < rate * 4: raise AlignmentRefused('A gravação é curta demais para alinhar com o MIDI (menos de 4 segundos).')
    peak = float(np.abs(audio).max())
    if peak < 1e-4: raise AlignmentRefused('A gravação está em silêncio.')
    audio = at.resample(audio / peak * .9, rate)
    progress('Ouvindo as notas da gravação com o modelo Basic Pitch…')
    frames, onsets, _ = at.posteriors(audio, session)
    times = at.frame_times(len(frames))
    audio_seconds = len(audio) / at.SAMPLE_RATE

    progress('Comparando a harmonia da gravação com a do MIDI…')
    coarse_factors = np.exp(np.linspace(math.log(.8), math.log(1.25), 31))
    best = search(frames, times, midi, coarse_factors, range(12), COARSE_RATE, COARSE_SMOOTH)
    # A transposition is accepted only when clearly better than keeping the recording's pitches.
    unshifted = search(frames, times, midi, coarse_factors, [0], COARSE_RATE, COARSE_SMOOTH)
    if best[2] != 0 and best[0] < unshifted[0] * 1.1: best = unshifted
    _, factor, rotation, lag, lags, scores = best
    distinct = distinctness(lags, scores, lag, COARSE_RATE)
    progress('Ajustando o deslocamento e o andamento…')
    fine_factors = factor * np.exp(np.linspace(-.016, .016, 33))
    fine = search(frames, times, midi, fine_factors, [rotation], FINE_RATE, FINE_SMOOTH,
                  lag_window=(lag * FINE_RATE / COARSE_RATE, 2 * FINE_RATE))
    _, factor, _, lag, lags, scores = fine
    offset = refine_lag(lags, scores, lag) / FINE_RATE
    linear = TimeMap.linear(offset, factor)
    features = Features(frames, times, midi, rotation)
    attacks = Attacks(onsets, times, midi)
    nudge = attacks.best_shift(linear)
    linear = TimeMap(linear.m, linear.a + nudge, linear.factor)
    correlation, coverage = features.evaluate(linear)
    progress('Procurando variações de andamento por trecho…')
    piecewise, anchors = local_map(attacks, linear, midi_seconds, features)
    windows = len(window_scores(features, linear))
    linear_correlation = correlation
    method, time_map = 'linear', linear
    if piecewise is not None:
        piecewise_correlation, piecewise_coverage = features.evaluate(piecewise)
        # Kept when the attacks line up clearly better and the harmony agrees about as well.
        if attacks.score(piecewise) >= attacks.score(linear) * 1.05 and piecewise_correlation >= correlation - .03:
            method, time_map, correlation, coverage = 'por trechos', piecewise, piecewise_correlation, piecewise_coverage
    stretches = window_scores(features, time_map)
    matching = sum(score >= MATCHING_WINDOW for score in stretches) / len(stretches) if stretches else 0.0

    shift = (rotation + 6) % 12 - 6
    report = {'correlation': round(correlation, 3), 'linear_correlation': round(linear_correlation, 3),
              'offset_seconds': round(float(time_map.to_audio(0.0)), 3), 'onset_adjustment_seconds': round(nudge, 3),
              'tempo_factor': round(factor, 4), 'transpose_semitones': shift, 'method': method,
              'segments': anchors if method == 'por trechos' else 1, 'windows': windows, 'matching_windows': round(matching, 3),
              'attack_agreement': round(attacks.score(time_map), 2),
              'distinctness': round(min(distinct, 99.0), 3), 'coverage': round(coverage, 3),
              'audio_seconds': round(audio_seconds, 1), 'midi_seconds': round(midi_seconds, 1)}
    if debug is not None: debug.update(report, window_scores=[round(s, 3) for s in stretches])
    if correlation < MIN_CORRELATION:
        raise AlignmentRefused(f'A gravação não combina com este MIDI: a harmonia das duas só se parece {percent(max(0, correlation))} '
                               f'(o mínimo é {percent(MIN_CORRELATION)}). Confira se é a mesma música e a mesma versão. Nada foi gravado.')
    if distinct < MIN_DISTINCTNESS:
        raise AlignmentRefused('A gravação não se encaixa em um único ponto do MIDI: outros deslocamentos combinam quase tão bem. '
                               'Pode ser outra música ou versão, ou uma música feita de trechos repetidos iguais. '
                               'Use uma gravação da mesma versão, inteira, do início ao fim. Nada foi gravado.')
    if coverage < MIN_COVERAGE:
        raise AlignmentRefused(f'A gravação cobre só {percent(coverage)} do MIDI. Use uma gravação da música inteira. Nada foi gravado.')

    progress('Separando a linha da melodia…')
    events = at.build_notes(frames, onsets)
    def seconds(frame): return float(times[min(frame, len(times) - 1)])
    heard = [(seconds(a), seconds(b), pitch, amplitude) for a, b, pitch, amplitude in events]
    mapped = []
    for start, end, pitch, amplitude in heard:
        s, e = (float(x) for x in time_map.to_midi([start, end]))
        p = pitch + shift
        if e <= -.25 or s >= midi_seconds + 1 or not 0 <= p <= 127: continue
        mapped.append((max(0.0, s), e, int(p), amplitude))
    voice, removed = without_accompaniment(mapped, midi)
    melody = melody_line(voice)
    notes = melody_ticks(melody, tempo, parsed['ppq'], max(parsed['end_tick'], 1))
    if len(notes) < MIN_MELODY_NOTES:
        raise AlignmentRefused('Quase nenhuma nota de melodia foi reconhecida na gravação depois do alinhamento. '
                               'Uma gravação só da voz costuma funcionar melhor. Nada foi gravado.')
    compatible = compatible_share(notes, parsed)
    report.update(compatible_share=round(compatible, 3), melody_notes=len(notes), accompaniment_notes_removed=removed,
                  melody_range=[min(n[2] for n in notes), max(n[2] for n in notes)])
    if compatible < MIN_COMPATIBLE:
        raise AlignmentRefused(f'Só {percent(compatible)} da melodia reconhecida cai sobre notas dos acordes do MIDI (o mínimo é {percent(MIN_COMPATIBLE)}). '
                               'A gravação pode ser de outra versão ou estar mal alinhada. Nada foi gravado.')

    channel, program = free_channel(parsed)
    data, track = midi_with_track(source_bytes, notes, channel, program)
    report.update(confidence='alta' if correlation >= .55 and compatible >= .65 else 'média',
                  track=track, channel=channel, program=program if program is not None else -1, track_name=TRACK_NAME,
                  source_midi=str(Path(midi_path).resolve()), source_sha256=parsed['sha256'], output_midi=str(output_midi.resolve()),
                  audio=str(audio_source or Path(wav_path).resolve()))
    progress('Gravando a cópia do MIDI com a pista da melodia…')
    output_midi.parent.mkdir(parents=True, exist_ok=True)
    temporary = output_midi.with_name('.' + output_midi.name + '.tmp')
    try:
        with temporary.open('xb') as stream: stream.write(data)
        os.replace(temporary, output_midi)
    finally:
        if temporary.exists(): temporary.unlink()
    try:
        with output_report.open('x', encoding='utf-8') as stream: json.dump(report, stream, ensure_ascii=False, indent=2)
    except OSError:
        output_midi.unlink(missing_ok=True); raise
    report['report_path'] = str(output_report.resolve())
    return report
