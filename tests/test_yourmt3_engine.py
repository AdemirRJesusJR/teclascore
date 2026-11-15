"""The YourMT3+ engine (engine/yourmt3_engine.py, yourmt3_setup.py and its use in audio_transcription.transcribe). The
model itself is a separate component with PyTorch; these tests replace it with notes written by hand, so they run with
the app's private Python and never touch the component installed on the machine."""
from pathlib import Path
import json, os, sys, tempfile, unittest
import numpy as np
import test_priority_melody  # noqa: F401  (puts engine/ on the import path)
import audio_transcription as at
import yourmt3_engine as ye
import yourmt3_setup as setup
from inspect_midi import parse_midi
from test_audio_transcription import synth_wav, MELODY, CHORDS


def recognized(bpm=100, melody=MELODY, chords=CHORDS):
    """What the model would return for synth_wav's recording: the tune as the singing voice, the chords as a piano, the
    chord roots as a bass and a drum hit on every beat."""
    beat = 60 / bpm; notes = []
    for i, pitch in enumerate(melody):
        notes.append({'start': i * beat, 'end': (i + .9) * beat, 'pitch': pitch, 'program': 100, 'drum': False, 'velocity': 1})
        notes.append({'start': i * beat, 'end': i * beat + .05, 'pitch': 36 if i % 2 == 0 else 38, 'program': 128, 'drum': True, 'velocity': 1})
    for i, chord in enumerate(chords):
        for pitch in chord: notes.append({'start': 4 * i * beat, 'end': (4 * i + 3.8) * beat, 'pitch': pitch, 'program': 0, 'drum': False, 'velocity': 1})
        notes.append({'start': 4 * i * beat, 'end': (4 * i + 3.8) * beat, 'pitch': chord[0] - 12, 'program': 33, 'drum': False, 'velocity': 1})
    return {'model': 'teste', 'device': 'cpu', 'seconds': len(melody) * beat + 1, 'notes': notes}


class PartsTests(unittest.TestCase):
    def test_notes_are_split_by_role(self):
        parts = ye.parts({'notes': [
            {'start': 0, 'end': 1, 'pitch': 60, 'program': 100, 'drum': False},
            {'start': 0, 'end': 1, 'pitch': 64, 'program': 101, 'drum': False},
            {'start': 0, 'end': 1, 'pitch': 36, 'program': 33, 'drum': False},
            {'start': 0, 'end': 1, 'pitch': 72, 'program': 56, 'drum': False},
            {'start': .5, 'end': .6, 'pitch': 38, 'program': 128, 'drum': True},
            {'start': 2, 'end': 2, 'pitch': 60, 'program': 100, 'drum': False},      # no length: dropped
            {'start': 2, 'end': 2.04, 'pitch': 67, 'program': 100, 'drum': False},   # a 40 ms blip is not a syllable
            {'start': 2, 'end': 3, 'pitch': 57, 'program': 33, 'drum': False},       # a "bass" above E3: the drum fill
            {'start': 3, 'end': 4, 'pitch': 200, 'program': 0, 'drum': False}]})     # not a MIDI pitch: dropped
        self.assertEqual([n[2] for n in parts['voice']], [60])
        self.assertEqual([n[2] for n in parts['chorus']], [64])
        self.assertEqual([n[2] for n in parts['bass']], [36])
        self.assertEqual([n[2] for n in parts['others']], [72])
        self.assertEqual(parts['drums'], [.5])

    def test_the_same_note_in_two_instruments_is_written_once(self):
        merged = ye.merge_doubles([(1.0, 1.4, 72, .6), (1.03, 1.8, 72, .6), (1.0, 1.5, 76, .6), (2.0, 2.5, 72, .6)])
        self.assertEqual(merged, [(1.0, 1.5, 76, .6), (1.0, 1.8, 72, .6), (2.0, 2.5, 72, .6)])

    def test_the_voice_is_one_line(self):
        line = ye.single_line([(0, 1.2, 60, .8), (1.0, 2.0, 62, .8), (2.0, 3.0, 64, .8), (2.01, 2.2, 67, .8)])
        self.assertEqual(line, [(0, 1.0, 60, .8), (1.0, 2.0, 62, .8), (2.0, 3.0, 64, .8)])

    def test_the_solo_is_the_top_line(self):
        notes = [(0, 2, 60, .6), (0, 2, 64, .6), (0, 1, 79, .6), (1, 2, 81, .6), (1.2, 1.8, 72, .6), (0, 2, 40, .6)]
        self.assertEqual(ye.top_line(notes, 52, 96), [(0, 1, 79, .6), (1, 2, 81, .6)])
        # A short chord struck together is one melody note (the top), even when it is shorter than the tolerance.
        chord = [(2.73, 2.87, 83, .6), (2.74, 2.79, 72, .6), (2.74, 2.79, 79, .6), (2.74, 2.79, 84, .6), (2.87, 2.99, 82, .6)]
        self.assertEqual([n[2] for n in ye.top_line(chord, 52, 96)], [84, 82])


def activations(seconds, notes):
    """Basic Pitch-like note activations (frames × 88) in which each (start, end, pitch) sounds at .8."""
    times = np.arange(int(seconds * at.FRAMES_PER_SECOND)) / at.FRAMES_PER_SECOND
    frames = np.zeros((len(times), 88))
    for start, end, pitch in notes: frames[(times >= start) & (times < end), pitch - at.MIDI_OFFSET] = .8
    return frames, times


class ConfirmedVoiceTests(unittest.TestCase):
    def test_support_reads_the_stem_under_each_note(self):
        frames, times = activations(4, [(0, 1, 60), (1, 2, 64)])
        support = at.note_support([(0, 1, 60, .8), (1, 2, 62, .8), (3, 3.5, 60, .8)], frames, times)
        self.assertAlmostEqual(support[0][0], .8, delta=.02); self.assertEqual(support[0][1], 60)
        self.assertEqual(support[1][:2], (0.0, 64)); self.assertAlmostEqual(support[1][2], .8, delta=.02)
        self.assertEqual(support[2][0], 0.0)

    def test_spoken_words_over_the_solo_are_not_singing(self):
        # 0–8 s: the model writes notes for shouted words the vocal stem does not hold as pitches; 10–22 s: real singing.
        shouted = [(i * .3, i * .3 + .25, 57 + i % 3, .8) for i in range(26)]
        sung = [(10 + i * .3, 10 + i * .3 + .25, 58 + 2 * (i % 2), .8) for i in range(40)]
        frames, times = activations(24, [(a, b, p) for a, b, p, _ in sung])
        vocal, regions, moved = at.confirmed_voice(shouted + sung, frames, times, 24)
        self.assertEqual(len(regions), 1)
        self.assertAlmostEqual(regions[0][0], 10, delta=.5); self.assertAlmostEqual(regions[0][1], 22, delta=.5)
        self.assertEqual((len(vocal), moved), (66, 0))
        # The melody keeps the solo where the voice only shouts.
        solo = [(i * .5, i * .5 + .4, 84, .6) for i in range(16)]
        melody, from_solo = at.fill_gaps(vocal, solo, end=24, regions=regions)
        self.assertEqual(from_solo, 16)
        self.assertFalse([n for n in melody if n[0] < 8 and n[2] < 70])

    def test_the_stem_corrects_a_note_written_a_step_away(self):
        sung = [(i * .3, i * .3 + .25, 60, .8) for i in range(40)]
        heard = [(a, b, 62 if i == 10 else 60) for i, (a, b, _, _) in enumerate(sung)]
        frames, times = activations(13, heard)
        vocal, regions, moved = at.confirmed_voice(sung, frames, times, 13)
        self.assertEqual(moved, 1)
        self.assertEqual([n[2] for n in vocal], [62 if i == 10 else 60 for i in range(40)])
        # A note the stem neither confirms nor contradicts stays as the model wrote it.
        frames[:] = np.where(np.arange(len(frames))[:, None] < np.searchsorted(times, 6), frames, 0)
        vocal, _, moved = at.confirmed_voice(sung, frames, times, 13)
        self.assertEqual([n[2] for n in vocal[25:]], [60] * 15)

    def test_the_written_melody_is_one_line(self):
        beats = np.arange(0, 20) * .5
        melody = [(0, .5, 60, .8), (.51, 1.2, 62, .8), (.53, .6, 65, .8), (1.0, 1.5, 64, .8), (1.5, 2.0, 65, .8)] + [(2 + i * .5, 2.4 + i * .5, 67, .8) for i in range(8)]
        with tempfile.TemporaryDirectory() as folder:
            midi = Path(folder) / 'm.mid'
            at.write_midi(midi, melody, [(0, 2, 48, .5)], beats, 120, single_melody=True)
            parsed = parse_midi(midi); ppq = parsed['ppq']
            line = sorted((n['start'] / ppq, n['end'] / ppq, n['pitch']) for n in parsed['notes'] if n['track'] == 1)[:4]
            # 62 and 65 land on the same position: the longer stays, and it ends where 64 starts.
            self.assertEqual(line, [(0.0, 1.0, 60), (1.0, 2.0, 62), (2.0, 3.0, 64), (3.0, 4.0, 65)])
            at.write_midi(midi, melody, [(0, 2, 48, .5)], beats, 120)
            self.assertEqual(len([n for n in parse_midi(midi)['notes'] if n['track'] == 1 and n['start'] == ppq]), 2)


class EngineChoiceTests(unittest.TestCase):
    def setUp(self):
        self.original = (ye.available, ye.recognize)
    def tearDown(self):
        ye.available, ye.recognize = self.original

    def test_yourmt3_notes_become_the_melody(self):
        ye.available = lambda root=None: True
        ye.recognize = lambda wav, progress=None, **k: recognized()
        with tempfile.TemporaryDirectory() as folder:
            wav, midi = Path(folder) / 'a.wav', Path(folder) / 'a.mid'
            synth_wav(wav, MELODY, CHORDS)
            report = at.transcribe(wav, midi, separate=False)
            self.assertEqual((report['engine'], report['engine_device']), ('yourmt3', 'cpu'))
            self.assertAlmostEqual(report['bpm'], 100, delta=1)
            self.assertEqual((report['vocal_notes'], report['solo_notes']), (len(MELODY), 0))
            parsed = parse_midi(midi); ppq = parsed['ppq']
            melody = sorted([n for n in parsed['notes'] if n['track'] == 1], key=lambda n: n['start'])
            self.assertEqual([n['pitch'] for n in melody], MELODY)
            self.assertEqual([n['start'] / ppq for n in melody], [float(i) for i in range(len(MELODY))])
            # The accompaniment is the model's piano and bass; the chords are still heard in the sound.
            self.assertTrue({36, 41, 43} <= {n['pitch'] for n in parsed['notes'] if n['track'] == 2})
            self.assertEqual(report['chords'][:4], ['C', 'F', 'G', 'C'])

    def test_the_solo_fills_where_nobody_sings(self):
        beat = .6; model = recognized()
        # Eight beats of an instrument alone before the voice comes in (voice shifted eight beats later).
        for note in model['notes']: note['start'] += 8 * beat; note['end'] += 8 * beat
        model['notes'] += [{'start': i * beat, 'end': (i + .9) * beat, 'pitch': 84 + i % 3, 'program': 56, 'drum': False, 'velocity': 1} for i in range(8)]
        ye.available = lambda root=None: True
        ye.recognize = lambda wav, progress=None, **k: model
        with tempfile.TemporaryDirectory() as folder:
            wav, midi = Path(folder) / 'a.wav', Path(folder) / 'a.mid'
            synth_wav(wav, [84, 85, 86] * 3 + MELODY, CHORDS + CHORDS[:2])
            report = at.transcribe(wav, midi, separate=False)
            self.assertGreaterEqual(report['solo_notes'], 6)
            self.assertEqual(report['vocal_notes'], len(MELODY))

    def test_classic_engine_ignores_the_component(self):
        ye.available = lambda root=None: True
        ye.recognize = lambda *a, **k: self.fail('the classic engine must not run the model')
        with tempfile.TemporaryDirectory() as folder:
            wav, midi = Path(folder) / 'a.wav', Path(folder) / 'a.mid'
            synth_wav(wav, MELODY, CHORDS)
            self.assertEqual(at.transcribe(wav, midi, separate=False, engine='classico')['engine'], 'classico')

    def test_a_failure_of_the_model_falls_back_and_says_why(self):
        ye.available = lambda root=None: True
        def broken(*a, **k): raise ValueError('sem memória na placa de vídeo')
        ye.recognize = broken
        with tempfile.TemporaryDirectory() as folder:
            wav, midi = Path(folder) / 'a.wav', Path(folder) / 'a.mid'
            synth_wav(wav, MELODY, CHORDS)
            report = at.transcribe(wav, midi, separate=False)
            self.assertEqual((report['engine'], report['engine_note']), ('classico', 'sem memória na placa de vídeo'))
            self.assertTrue(midi.is_file())
            # Asked for by name, the failure is the answer: nothing is written by another engine.
            midi.unlink()
            with self.assertRaisesRegex(ValueError, 'sem memória'): at.transcribe(wav, midi, separate=False, engine='yourmt3')
            self.assertFalse(midi.exists())

    def test_asking_for_a_missing_component_is_refused(self):
        ye.available = lambda root=None: False
        with self.assertRaisesRegex(ValueError, 'não está instalado'): at.transcribe('a.wav', 'a.mid', separate=False, engine='yourmt3')
        with self.assertRaisesRegex(ValueError, 'desconhecido'): at.transcribe('a.wav', 'a.mid', separate=False, engine='outro')

    def test_without_the_component_the_classic_engine_runs(self):
        ye.available = lambda root=None: False
        with tempfile.TemporaryDirectory() as folder:
            wav, midi = Path(folder) / 'a.wav', Path(folder) / 'a.mid'
            synth_wav(wav, MELODY, CHORDS)
            report = at.transcribe(wav, midi, separate=False)
            self.assertEqual(report['engine'], 'classico'); self.assertNotIn('engine_note', report)


STUB = '''import json, sys, time
arguments = dict(zip(sys.argv[1::2], sys.argv[2::2]))
print('@Reconhecendo… 50%', flush=True)
print('aviso qualquer da biblioteca', flush=True)
if 'trava' in arguments['--audio']: time.sleep(60)
if 'falha' in arguments['--audio']: print('RuntimeError: CUDA out of memory', flush=True); sys.exit(3)
json.dump({'device': 'cpu', 'seconds': 1.0, 'notes': [{'start': 0, 'end': 1, 'pitch': 60, 'program': 100, 'drum': False, 'velocity': 1}]}, open(arguments['--saida'], 'w'))
'''


class ComponentTests(unittest.TestCase):
    def component(self, folder):
        """A folder that looks like an installed component, whose "model" is the stub above run by this Python."""
        root = Path(folder) / 'yourmt3'
        setup.checkpoint_of(root).parent.mkdir(parents=True); setup.checkpoint_of(root).write_bytes(b'modelo')
        (setup.code_of(root) / 'model_helper.py').write_text('', encoding='utf-8')
        setup.python_of(root).parent.mkdir(parents=True); setup.python_of(root).write_bytes(b'')
        (root / setup.MARKER).write_text(json.dumps({'component': setup.COMPONENT, 'revision': setup.REVISION}), encoding='utf-8')
        return root

    def test_installed_needs_every_part(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertIsNone(setup.installed(Path(folder) / 'vazio'))
            root = self.component(folder)
            self.assertEqual(setup.installed(root)['revision'], setup.REVISION)
            self.assertTrue(ye.available(root))
            setup.checkpoint_of(root).unlink()
            self.assertIsNone(setup.installed(root)); self.assertFalse(ye.available(root))
            setup.checkpoint_of(root).write_bytes(b'modelo')
            (root / setup.MARKER).write_text(json.dumps({'component': setup.COMPONENT, 'revision': 'outra'}), encoding='utf-8')
            self.assertIsNone(setup.installed(root))

    def test_the_folder_can_be_chosen_by_the_environment(self):
        previous = os.environ.get('TECLAS_YOURMT3')
        try:
            os.environ['TECLAS_YOURMT3'] = r'D:\modelos\yourmt3'
            self.assertEqual(setup.default_root(), Path(r'D:\modelos\yourmt3'))
        finally:
            if previous is None: del os.environ['TECLAS_YOURMT3']
            else: os.environ['TECLAS_YOURMT3'] = previous

    def test_the_model_runs_in_its_own_process(self):
        original = (setup.python_of, ye.RUNNER)
        with tempfile.TemporaryDirectory() as folder:
            root = self.component(folder)
            stub = Path(folder) / 'stub.py'; stub.write_text(STUB, encoding='utf-8')
            try:
                setup.python_of = lambda root: Path(sys.executable); ye.RUNNER = stub
                messages = []
                wav = Path(folder) / 'a.wav'; wav.write_bytes(b'')
                report = ye.recognize(wav, messages.append, root=root)
                self.assertEqual(messages, ['Reconhecendo… 50%'])
                self.assertEqual(report['notes'][0]['pitch'], 60)
                broken = Path(folder) / 'falha.wav'; broken.write_bytes(b'')
                with self.assertRaisesRegex(ValueError, 'CUDA out of memory'): ye.recognize(broken, messages.append, root=root)
                # A model that hangs is stopped at the time limit instead of holding the transcription forever.
                stuck = Path(folder) / 'trava.wav'; stuck.write_bytes(b'')
                with self.assertRaisesRegex(ValueError, 'passou do limite'): ye.recognize(stuck, messages.append, root=root, timeout=2)
            finally:
                setup.python_of, ye.RUNNER = original
            with self.assertRaisesRegex(ValueError, 'não está instalado'): ye.recognize(wav, root=Path(folder) / 'vazio')


if __name__ == '__main__': unittest.main()
