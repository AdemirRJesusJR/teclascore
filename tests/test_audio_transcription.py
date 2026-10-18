"""Audio → MIDI: Basic Pitch notes, beat grid, melody/accompaniment split (engine/audio_transcription.py)."""
from pathlib import Path
import struct, tempfile, unittest
import numpy as np
import test_priority_melody  # noqa: F401  (puts engine/ on the import path)
import audio_transcription as at
from inspect_midi import parse_midi, summarize


def synth_wav(path, melody, chords, bpm=100, rate=44100, tempo_curve=None):
    """Piano-like tones: melody one note per beat, a chord every four beats. tempo_curve maps beat → seconds."""
    beat = 60 / bpm
    when = tempo_curve or (lambda b: b * beat)
    total = when(len(melody)) + 1
    t = np.arange(int(total * rate)) / rate; audio = np.zeros_like(t)
    def tone(pitch, start, duration, amplitude):
        f = 440 * 2 ** ((pitch - 69) / 12); index = (t >= start) & (t < start + duration + .3); tt = t[index] - start
        envelope = np.exp(-tt * 3) * (tt < duration) + np.exp(-duration * 3) * np.exp(-(tt - duration) * 20) * (tt >= duration)
        audio[index] += amplitude * envelope * sum(np.sin(2 * np.pi * f * k * tt) / k ** 1.5 for k in range(1, 6))
    for i, pitch in enumerate(melody): tone(pitch, when(i), (when(i + 1) - when(i)) * .9, .5)
    for i, chord in enumerate(chords):
        for pitch in chord: tone(pitch, when(4 * i), (when(4 * i + 4) - when(4 * i)) * .95, .18)
    pcm = (audio / np.abs(audio).max() * 30000).astype('<i2').tobytes()
    Path(path).write_bytes(b'RIFF' + struct.pack('<I', 36 + len(pcm)) + b'WAVEfmt ' + struct.pack('<IHHIIHH', 16, 1, 1, rate, rate * 2, 2, 16)
                           + b'data' + struct.pack('<I', len(pcm)) + pcm)


MELODY = [72, 74, 76, 77, 79, 77, 76, 74] * 4
CHORDS = [[48, 52, 55], [53, 57, 60], [55, 59, 62], [48, 52, 55]] * 2


class AudioTranscriptionTests(unittest.TestCase):
    def test_wav_formats_and_resampling(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'f.wav'; samples = np.sin(np.arange(4410) / 10).astype('<f4')
            path.write_bytes(b'RIFF' + struct.pack('<I', 36 + samples.nbytes) + b'WAVEfmt ' + struct.pack('<IHHIIHH', 16, 3, 1, 44100, 44100 * 4, 4, 32)
                             + b'data' + struct.pack('<I', samples.nbytes) + samples.tobytes())
            audio, rate = at.read_wav(path)
            self.assertEqual((rate, len(audio)), (44100, 4410))
        tone = np.sin(2 * np.pi * 440 * np.arange(44100) / 44100).astype(np.float32)
        out = at.resample(tone, 44100)
        self.assertEqual(len(out), 22050)
        spectrum = np.abs(np.fft.rfft(out)); self.assertAlmostEqual(np.argmax(spectrum) * 22050 / len(out), 440, delta=2)

    def test_steady_recording_becomes_a_clean_midi(self):
        with tempfile.TemporaryDirectory() as folder:
            wav, midi = Path(folder) / 'a.wav', Path(folder) / 'a.mid'
            synth_wav(wav, MELODY, CHORDS)
            report = at.transcribe(wav, midi)
            self.assertAlmostEqual(report['bpm'], 100, delta=1)
            parsed = parse_midi(midi); ppq = parsed['ppq']
            melody = sorted([n for n in parsed['notes'] if n['track'] == 1], key=lambda n: n['start'])
            self.assertEqual([n['pitch'] for n in melody], MELODY)
            self.assertEqual([n['start'] / ppq for n in melody], [float(i) for i in range(len(MELODY))])
            accompaniment = {n['pitch'] % 12 for n in parsed['notes'] if n['track'] == 2}
            self.assertTrue({0, 4, 7, 5, 9, 11, 2} <= accompaniment)
            # The chords heard in the sound: C F G C, written as block chords on track 3; the tune is clear.
            self.assertEqual(report['chords'][:4], ['C', 'F', 'G', 'C'])
            self.assertTrue(report['melody_clear'])
            bass = sorted([n for n in parsed['notes'] if n['track'] == 3 and n['pitch'] < 48], key=lambda n: n['start'])
            self.assertEqual([n['pitch'] for n in bass][:4], [36, 41, 43, 36])
            self.assertEqual(bass[0]['start'] % (4 * ppq), 0)

    def test_chord_templates(self):
        def profile(*pitch_classes, bass=None):
            chroma = np.zeros(12); low = np.zeros(12)
            for pc in pitch_classes: chroma[pc] += 1
            if bass is not None: low[bass] = 1
            return chroma, low
        self.assertEqual(at.chord_of(*profile(9, 0, 4, bass=9))[:2], (9, 'm'))
        self.assertEqual(at.chord_of(*profile(7, 11, 2, 5, bass=7))[:2], (7, '7'))
        self.assertEqual(at.chord_of(*profile(2, 5, 9, bass=2))[:2], (2, 'm'))
        self.assertFalse(at.melody_is_clear([(i, i + .5, 60, .5) for i in range(20)], list(range(40))))

    def test_drifting_tempo_follows_the_beats(self):
        # Tempo speeding up from 90 to about 110 BPM: positions follow the detected beats, not a fixed grid.
        curve = lambda b: 60 / 90 * b - .0028 * b * b
        with tempfile.TemporaryDirectory() as folder:
            wav, midi = Path(folder) / 'b.wav', Path(folder) / 'b.mid'
            synth_wav(wav, MELODY, CHORDS, tempo_curve=curve)
            at.transcribe(wav, midi)
            parsed = parse_midi(midi); ppq = parsed['ppq']
            melody = sorted([n for n in parsed['notes'] if n['track'] == 1], key=lambda n: n['start'])
            self.assertEqual([n['pitch'] for n in melody], MELODY)
            positions = [n['start'] / ppq for n in melody]
            self.assertLessEqual(max(abs(p - round(p)) for p in positions), .26)
            self.assertEqual(len(set(round(p) for p in positions)), len(MELODY))

    @staticmethod
    def played(melody, chords, when, meter=4, seed=3):
        """A keyboard player: melody one note per beat (with small human timing errors), the left hand striking the
        chord on the first beat of each measure, louder bass. when maps beat → seconds."""
        rng = np.random.default_rng(seed); notes = []
        for i, pitch in enumerate(melody):
            jitter = float(rng.normal(0, .012))
            notes.append(dict(start=when(i) + jitter, end=when(i + 1) - .04, pitch=pitch, velocity=72))
        for bar, chord in enumerate(chords):
            for k, pitch in enumerate(chord):
                notes.append(dict(start=when(bar * meter) + float(rng.normal(0, .01)), end=when(bar * meter + meter) - .05, pitch=pitch, velocity=95 if k == 0 else 70))
        return notes

    def test_keyboard_idea_follows_the_players_tempo(self):
        # 88 BPM slowing down to about 76 at the end; melody must land on whole beats, measures on the chords.
        curve = lambda b: 60 / 88 * b + .0012 * b * b
        chords = [[36, 52, 55, 60], [41, 57, 60, 65], [43, 55, 59, 62], [36, 52, 55, 60]] * 2
        with tempfile.TemporaryDirectory() as folder:
            midi = Path(folder) / 'ideia.mid'
            report = at.transcribe_notes(self.played(MELODY, chords, curve), midi)
            self.assertEqual(report['meter'], 4)
            self.assertEqual(report['chords'][:4], ['C', 'F', 'G', 'C'])
            parsed = parse_midi(midi); ppq = parsed['ppq']
            melody = sorted([n for n in parsed['notes'] if n['track'] == 1], key=lambda n: n['start'])
            self.assertEqual([n['pitch'] for n in melody], MELODY)
            positions = [n['start'] / ppq for n in melody]
            self.assertEqual(positions, [positions[0] + i for i in range(len(MELODY))])
            bass = sorted([n for n in parsed['notes'] if n['track'] == 2 and n['pitch'] < 48], key=lambda n: n['start'])
            self.assertTrue(all(n['start'] % (4 * ppq) == 0 for n in bass), [n['start'] / ppq for n in bass])

    def test_keyboard_waltz_is_three_four(self):
        waltz = [67, 69, 71, 72, 71, 69, 67, 66, 64, 62, 64, 66] * 2
        chords = [[43, 59, 62], [38, 54, 57], [40, 55, 59], [38, 54, 57]] * 2
        with tempfile.TemporaryDirectory() as folder:
            midi = Path(folder) / 'valsa.mid'
            report = at.transcribe_notes(self.played(waltz, chords, lambda b: b * .5, meter=3), midi)
            self.assertEqual(report['meter'], 3)
            self.assertAlmostEqual(report['bpm'], 120, delta=3)
            parsed = parse_midi(midi)
            self.assertEqual([(t['numerator'], t['denominator']) for t in summarize(parsed)['time_signatures']], [(3, 4)])
            bass = sorted([n for n in parsed['notes'] if n['track'] == 2 and n['pitch'] < 48], key=lambda n: n['start'])
            self.assertTrue(all(n['start'] % (3 * parsed['ppq']) == 0 for n in bass))

    def test_silence_and_short_audio_are_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 's.wav'; pcm = bytes(44100 * 4)
            path.write_bytes(b'RIFF' + struct.pack('<I', 36 + len(pcm)) + b'WAVEfmt ' + struct.pack('<IHHIIHH', 16, 1, 1, 44100, 88200, 2, 16) + b'data' + struct.pack('<I', len(pcm)) + pcm)
            with self.assertRaisesRegex(ValueError, 'silêncio'): at.transcribe(path, Path(folder) / 's.mid')


if __name__ == '__main__': unittest.main()
