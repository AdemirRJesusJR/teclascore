"""Melody from a recording for a MIDI that lacks it: alignment by chromagram and the copy with the new track
(engine/audio_melody_align.py)."""
from pathlib import Path
import hashlib, json, struct, subprocess, sys, tempfile, unittest
import numpy as np
import test_priority_melody  # noqa: F401  (puts engine/ on the import path)
import audio_melody_align as am
from inspect_midi import parse_midi

PPQ = 480
# One chord per 4/4 bar; the accompaniment MIDI has no melody. Root, quality (intervals).
PROGRESSION = [(0, (0, 4, 7)), (9, (0, 3, 7)), (5, (0, 4, 7)), (7, (0, 4, 7)), (4, (0, 3, 7)), (9, (0, 3, 7)), (2, (0, 3, 7)), (7, (0, 4, 7, 10)),
               (0, (0, 4, 7)), (4, (0, 4, 7, 10)), (9, (0, 3, 7)), (5, (0, 4, 7)), (2, (0, 3, 7)), (7, (0, 4, 7)), (0, (0, 4, 7)), (5, (0, 4, 7)),
               (7, (0, 4, 7)), (9, (0, 3, 7)), (5, (0, 4, 7)), (0, (0, 4, 7))]
TEMPOS = [(0, 100), (8 * 4 * PPQ, 90)]       # the MIDI slows down at bar 9


def vlq(value):
    out = [value & 127]; value >>= 7
    while value: out.insert(0, (value & 127) | 128); value >>= 7
    return bytes(out)


def track(events, name):
    body = b'\x00\xff\x03' + vlq(len(name)) + name.encode('latin-1'); previous = 0
    for tick, message in sorted(events, key=lambda e: (e[0], e[1][0] & 0xF0 != 0x80)):
        body += vlq(tick - previous) + message; previous = tick
    body += b'\x00\xff\x2f\x00'
    return b'MTrk' + struct.pack('>I', len(body)) + body


def melody_of(progression, seed=5):
    """A tune over the chords: chord tones on the beats, a passing step on the off-beat of beat 3. (tick, end, pitch)."""
    rng = np.random.default_rng(seed); notes = []
    for bar, (root, quality) in enumerate(progression):
        tones = sorted({60 + (root + i) % 12 + (12 if (root + i) % 12 < 7 else 0) for i in quality})
        start = bar * 4 * PPQ
        pattern = [(0, 1), (1, 1), (2, .5), (2.5, .5), (3, 1)]
        for k, (beat, length) in enumerate(pattern):
            pitch = int(rng.choice(tones)) if k != 3 else notes[-1][2] + (2 if rng.random() < .5 else -1)
            notes.append((start + int(beat * PPQ), start + int((beat + length) * PPQ), pitch))
    return notes


def accompaniment_midi(path, progression, tempos=TEMPOS):
    conductor = []
    for tick, bpm in tempos: conductor.append((tick, b'\xff\x51\x03' + int(60_000_000 / bpm).to_bytes(3, 'big')))
    conductor.append((0, b'\xff\x58\x04\x04\x02\x18\x08'))
    chords, bass = [(0, b'\xc0\x00')], [(0, b'\xc1\x21')]
    for bar, (root, quality) in enumerate(progression):
        a, b = bar * 4 * PPQ, (bar + 1) * 4 * PPQ - 20
        for interval in quality: chords += [(a, bytes([0x90, 48 + (root + interval) % 12, 70])), (b, bytes([0x80, 48 + (root + interval) % 12, 0]))]
        for beat in (0, 2):
            bass += [(a + beat * PPQ, bytes([0x91, 36 + root, 90])), (a + (beat + 2) * PPQ - 40, bytes([0x81, 36 + root, 0]))]
    data = b'MThd' + struct.pack('>IHHH', 6, 1, 3, PPQ) + track(conductor, 'Conductor') + track(chords, 'Piano') + track(bass, 'Baixo')
    Path(path).write_bytes(data)


def seconds_of(tick, tempos=TEMPOS):
    seconds, previous, bpm = 0.0, 0, tempos[0][1]
    for at_tick, value in tempos[1:]:
        if tick <= at_tick: break
        seconds += (at_tick - previous) / PPQ * 60 / bpm; previous, bpm = at_tick, value
    return seconds + (tick - previous) / PPQ * 60 / bpm


def recording(path, progression, melody, offset, speed, rate=22050, tempos=TEMPOS, with_chords=True, warp=lambda s: s):
    """Melody as sine tones (with soft overtones) plus the chords and bass, offset by `offset` seconds and `speed`
    times faster than the MIDI; `warp` bends the MIDI seconds first (a recording whose tempo drifts)."""
    when = lambda tick: offset + warp(seconds_of(tick, tempos)) / speed
    total = when(len(progression) * 4 * PPQ) + 1.5
    t = np.arange(int(total * rate)) / rate; audio = np.zeros_like(t)
    def tone(pitch, start, end, amplitude, harmonics=4):
        f = 440 * 2 ** ((pitch - 69) / 12); a, b = int(start * rate), min(len(t), int((end + .15) * rate))
        tt = t[a:b] - start; duration = end - start
        envelope = np.minimum(1, tt / .01) * np.where(tt < duration, np.exp(-tt * 1.5), np.exp(-duration * 1.5) * np.exp(-(tt - duration) * 30))
        audio[a:b] += amplitude * envelope * sum(np.sin(2 * np.pi * f * k * tt) / k ** 2 for k in range(1, harmonics + 1))
    for start, end, pitch in melody: tone(pitch, when(start), when(end) - .02, .5, harmonics=2)
    if with_chords:
        for bar, (root, quality) in enumerate(progression):
            a, b = when(bar * 4 * PPQ), when((bar + 1) * 4 * PPQ)
            for interval in quality: tone(48 + (root + interval) % 12, a, b - .03, .16)
            tone(36 + root, a, when(bar * 4 * PPQ + 2 * PPQ) - .05, .22); tone(36 + root, when(bar * 4 * PPQ + 2 * PPQ), b - .05, .22)
    pcm = (audio / np.abs(audio).max() * 30000).astype('<i2').tobytes()
    Path(path).write_bytes(b'RIFF' + struct.pack('<I', 36 + len(pcm)) + b'WAVEfmt ' + struct.pack('<IHHIIHH', 16, 1, 1, rate, rate * 2, 2, 16)
                           + b'data' + struct.pack('<I', len(pcm)) + pcm)


class AudioMelodyAlignTests(unittest.TestCase):
    def test_tempo_map_round_trip(self):
        with tempfile.TemporaryDirectory() as folder:
            midi = Path(folder) / 'a.mid'; accompaniment_midi(midi, PROGRESSION)
            tempo = am.TempoMap(parse_midi(midi))
            ticks = np.array([0, PPQ, 32 * PPQ, 40 * PPQ + 120])
            self.assertTrue(np.allclose(tempo.to_seconds(ticks), [seconds_of(int(t)) for t in ticks]))
            self.assertTrue(np.allclose(tempo.to_ticks(tempo.to_seconds(ticks)), ticks))
            line = am.TimeMap.linear(2.0, 1.25)
            self.assertAlmostEqual(float(line.to_audio(10.0)), 10.0, places=6)
            self.assertAlmostEqual(float(line.to_midi(line.to_audio(7.3))), 7.3, places=6)

    def test_offset_and_faster_recording_are_recovered(self):
        melody = melody_of(PROGRESSION)
        with tempfile.TemporaryDirectory() as folder:
            midi, wav, out = Path(folder) / 'acomp.mid', Path(folder) / 'voz.wav', Path(folder) / 'saida' / 'acomp (melodia).mid'
            accompaniment_midi(midi, PROGRESSION)
            original = hashlib.sha256(midi.read_bytes()).hexdigest()
            recording(wav, PROGRESSION, melody, offset=2.3, speed=1.04)
            messages = []
            report = am.align(wav, midi, out, progress=messages.append)
            self.assertAlmostEqual(report['offset_seconds'], 2.3, delta=.1, msg=report)
            self.assertAlmostEqual(report['tempo_factor'], 1.04, delta=.01, msg=report)
            self.assertEqual(report['transpose_semitones'], 0)
            self.assertGreater(report['correlation'], .5, report)
            self.assertGreater(report['compatible_share'], .7, report)
            self.assertTrue(messages)
            # The original is untouched; the copy has every original track plus the melody track.
            self.assertEqual(hashlib.sha256(midi.read_bytes()).hexdigest(), original)
            source, copy = parse_midi(midi), parse_midi(out)
            self.assertEqual(len(copy['tracks']), len(source['tracks']) + 1)
            self.assertEqual(copy['tracks'][-1]['names'], [am.TRACK_NAME])
            self.assertEqual(report['track'], len(source['tracks']))
            self.assertEqual(len([n for n in copy['notes'] if n['track'] < report['track']]), len(source['notes']))
            added = [n for n in copy['notes'] if n['track'] == report['track']]
            self.assertTrue(all(n['channel'] == report['channel'] and n['channel'] not in (1, 2, 10) for n in added))
            # Most tune notes land on their beat with the right pitch.
            hits = sum(any(n['pitch'] == pitch and abs(n['start'] - start) <= PPQ // 8 for n in added) for start, _, pitch in melody)
            self.assertGreaterEqual(hits / len(melody), .85, (hits, len(melody), report))
            saved = json.loads(Path(report['report_path']).read_text(encoding='utf-8'))
            self.assertEqual(saved['source_sha256'], original)
            for field in ('correlation', 'offset_seconds', 'tempo_factor', 'compatible_share', 'method'): self.assertIn(field, saved)

    def test_recording_in_another_key_is_transposed_to_the_midi(self):
        melody = melody_of(PROGRESSION[:16])
        up = [(r + 2) % 12 for r, _ in PROGRESSION[:16]]
        with tempfile.TemporaryDirectory() as folder:
            midi, wav, out = Path(folder) / 'acomp.mid', Path(folder) / 'voz.wav', Path(folder) / 'm.mid'
            accompaniment_midi(midi, PROGRESSION[:16])
            recording(wav, [(r, q) for r, (_, q) in zip(up, PROGRESSION[:16])], [(s, e, p + 2) for s, e, p in melody], offset=1.0, speed=.97)
            report = am.align(wav, midi, out)
            self.assertEqual(report['transpose_semitones'], -2, report)
            added = [n for n in parse_midi(out)['notes'] if n['track'] == report['track']]
            hits = sum(any(n['pitch'] == pitch and abs(n['start'] - start) <= PPQ // 8 for n in added) for start, _, pitch in melody)
            self.assertGreaterEqual(hits / len(melody), .8, (hits, report))

    def test_voice_alone_is_aligned_too(self):
        melody = melody_of(PROGRESSION)
        with tempfile.TemporaryDirectory() as folder:
            midi, wav, out = Path(folder) / 'acomp.mid', Path(folder) / 'voz.wav', Path(folder) / 'm.mid'
            accompaniment_midi(midi, PROGRESSION)
            recording(wav, PROGRESSION, melody, offset=.7, speed=.93, with_chords=False)
            report = am.align(wav, midi, out)
            self.assertAlmostEqual(report['tempo_factor'], .93, delta=.01, msg=report)
            added = [n for n in parse_midi(out)['notes'] if n['track'] == report['track']]
            hits = sum(any(n['pitch'] == pitch and abs(n['start'] - start) <= PPQ // 8 for n in added) for start, _, pitch in melody)
            self.assertGreaterEqual(hits / len(melody), .9, (hits, report))

    def test_tempo_drift_gets_a_piecewise_map(self):
        # The band speeds up 8% after 25 s: one straight line cannot follow it, the map by stretches does.
        melody = melody_of(PROGRESSION)
        with tempfile.TemporaryDirectory() as folder:
            midi, wav, out = Path(folder) / 'acomp.mid', Path(folder) / 'banda.wav', Path(folder) / 'm.mid'
            accompaniment_midi(midi, PROGRESSION)
            recording(wav, PROGRESSION, melody, offset=1.5, speed=1.0, warp=lambda s: s if s < 25 else 25 + (s - 25) / 1.08)
            report = am.align(wav, midi, out)
            self.assertEqual(report['method'], 'por trechos', report)
            self.assertGreater(report['correlation'], report['linear_correlation'])
            added = [n for n in parse_midi(out)['notes'] if n['track'] == report['track']]
            hits = sum(any(n['pitch'] == pitch and abs(n['start'] - start) <= PPQ // 8 for n in added) for start, _, pitch in melody)
            self.assertGreaterEqual(hits / len(melody), .75, (hits, report))

    def test_recording_of_another_song_is_refused_without_writing(self):
        other = [(6, (0, 4, 7)), (11, (0, 4, 7)), (1, (0, 4, 7, 10)), (3, (0, 3, 7)), (8, (0, 3, 7)), (1, (0, 4, 7)), (10, (0, 3, 7)), (6, (0, 4, 7))] * 2
        # The same chords in another order are not the same song either.
        shuffled = [PROGRESSION[i] for i in (3, 7, 1, 12, 0, 18, 5, 9, 14, 2, 16, 4, 11, 19, 6, 8, 13, 10, 17, 15)]
        for name, progression, tempos, chords in (('outra', other, [(0, 132)], True), ('outra-voz', other, [(0, 132)], False),
                                                  ('embaralhada', shuffled, TEMPOS, True)):
            with self.subTest(name), tempfile.TemporaryDirectory() as folder:
                midi, wav, out = Path(folder) / 'acomp.mid', Path(folder) / 'outra.wav', Path(folder) / 'm.mid'
                accompaniment_midi(midi, PROGRESSION)
                recording(wav, progression, melody_of(progression, seed=9), offset=.5, speed=1.0, tempos=tempos, with_chords=chords)
                with self.assertRaises(am.AlignmentRefused) as refused: am.align(wav, midi, out)
                self.assertRegex(str(refused.exception), 'Nada foi gravado')
                self.assertEqual(sorted(p.name for p in Path(folder).iterdir()), ['acomp.mid', 'outra.wav'])

    @staticmethod
    def worker(request_path):
        """engine/worker.py of this checkout as its own process (an embedded Python ignores the script folder)."""
        engine = str(Path(__file__).resolve().parents[1] / 'engine')
        code = ('import runpy,sys;sys.path.insert(0,sys.argv[1]);sys.argv=[sys.argv[1]+"/worker.py",sys.argv[2]];'
                'runpy.run_path(sys.argv[0],run_name="__main__")')
        process = subprocess.run([sys.executable, '-B', '-c', code, engine, str(request_path)], capture_output=True, text=True, encoding='utf8', timeout=240)
        messages = [json.loads(line) for line in process.stdout.splitlines() if line.startswith('{')]
        return process, messages

    def test_worker_writes_the_copy_and_analysis_flags_the_recorded_melody(self):
        melody = melody_of(PROGRESSION)
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); midi, wav = root / 'acomp.mid', root / 'voz.wav'
            accompaniment_midi(midi, PROGRESSION); recording(wav, PROGRESSION, melody, offset=1.2, speed=1.02)
            original = midi.read_bytes()
            copy = root / 'saida' / 'acomp (melodia de gravação).mid'
            request = root / 'align.json'
            request.write_text(json.dumps(dict(mode='alignAudioMelody', workspace=str(root / 'job'), inputMidi=str(midi), inputAudio=str(wav),
                                               outputMidi=str(copy), outputReport=str(copy.with_suffix('.json')))), encoding='utf8')
            process, messages = self.worker(request)
            self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
            self.assertTrue(any(m['kind'] == 'progress' for m in messages))
            result = [m['data'] for m in messages if m['kind'] == 'result'][0]
            self.assertEqual((Path(result['midiPath']), result['track_name']), (copy.resolve(), am.TRACK_NAME))
            self.assertTrue(copy.with_suffix('.json').is_file())
            self.assertEqual(midi.read_bytes(), original)
            # Analysis of the copy with the recorded track as melody asks for a check by ear.
            source = dict(track=result['track'], channel=result['channel'], program=result['program'])
            request = root / 'analyze.json'
            request.write_text(json.dumps(dict(mode='analyze', workspace=str(root / 'job2'), inputMidi=str(copy), useReviewedProfile=False,
                                               melodySources=[source])), encoding='utf8')
            process, messages = self.worker(request)
            self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
            analysis = [m['data'] for m in messages if m['kind'] == 'result'][0]
            self.assertTrue(analysis['melodyFromRecording'])
            self.assertEqual(analysis['reviewPoints'][0]['kind'], 'melody_from_recording')
            self.assertFalse(analysis['reviewPoints'][0]['blocking'])
            self.assertIn('Confira ouvindo', analysis['warnings'][0])

    def test_worker_refusal_writes_nothing(self):
        other = [(6, (0, 4, 7)), (11, (0, 4, 7)), (1, (0, 4, 7, 10)), (3, (0, 3, 7)), (8, (0, 3, 7)), (1, (0, 4, 7)), (10, (0, 3, 7)), (6, (0, 4, 7))] * 2
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); midi, wav = root / 'acomp.mid', root / 'outra.wav'
            accompaniment_midi(midi, PROGRESSION); recording(wav, other, melody_of(other, seed=9), offset=.5, speed=1.0, tempos=[(0, 132)])
            copy = root / 'saida' / 'copia.mid'
            request = root / 'align.json'
            request.write_text(json.dumps(dict(mode='alignAudioMelody', workspace=str(root / 'job'), inputMidi=str(midi), inputAudio=str(wav),
                                               outputMidi=str(copy))), encoding='utf8')
            process, messages = self.worker(request)
            self.assertEqual(process.returncode, 1)
            error = [m for m in messages if m['kind'] == 'error'][0]
            self.assertIn('Nada foi gravado', error['message'])
            self.assertFalse(copy.exists()); self.assertFalse(copy.with_suffix('.alinhamento.json').exists())

    def test_copy_of_a_type_0_midi_becomes_type_1(self):
        body = b'\x00\xff\x51\x03\x07\xa1\x20\x00\x90\x3c\x40\x83\x60\x80\x3c\x00\x00\xff\x2f\x00'
        source = b'MThd' + struct.pack('>IHHH', 6, 0, 1, PPQ) + b'MTrk' + struct.pack('>I', len(body)) + body
        data, index = am.midi_with_track(source, [(0, PPQ, 72, 80), (PPQ, 2 * PPQ, 74, 80)], 2, 73)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'c.mid'; path.write_bytes(data)
            parsed = parse_midi(path)
            self.assertEqual((parsed['format'], len(parsed['tracks']), index), (1, 2, 1))
            self.assertEqual(sorted(n['pitch'] for n in parsed['notes'] if n['track'] == 1), [72, 74])
            self.assertEqual(parsed['tracks'][1]['names'], [am.TRACK_NAME])


if __name__ == '__main__': unittest.main()
