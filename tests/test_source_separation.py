"""Gravação separada em voz e instrumentos antes da transcrição (engine/source_separation.py e o caminho separado de
engine/audio_transcription.py). Os testes que dependem do modelo HTDemucs (174 MB, baixado pelo build) são pulados
quando ele não está instalado."""
from pathlib import Path
import tempfile, unittest
import numpy as np
import test_priority_melody  # noqa: F401  (puts engine/ on the import path)
import audio_transcription as at
import source_separation as ss
from inspect_midi import parse_midi
from test_audio_transcription import synth_wav, MELODY, CHORDS

HAS_MODEL = ss.available()


def activations(count, line, others=(), level=.8):
    """Ativações sintéticas do modelo: `line` e `others` são listas (início, fim, altura, nível) em quadros."""
    frames = np.full((count, 88), .02); onsets = np.zeros((count, 88))
    for start, end, pitch, amount in list(line) + list(others):
        frames[start:end, pitch - at.MIDI_OFFSET] = np.maximum(frames[start:end, pitch - at.MIDI_OFFSET], amount)
        onsets[start, pitch - at.MIDI_OFFSET] = max(onsets[start, pitch - at.MIDI_OFFSET], .9 if amount >= level else .4)
    return frames, onsets


class SpectrogramTests(unittest.TestCase):
    def test_demucs_spectrogram_round_trip(self):
        # _spec/_ispec reproduzem a STFT normalizada do Demucs: ida e volta devolvem o sinal (exceto as bordas).
        t = np.arange(ss.SEGMENT) / ss.SAMPLE_RATE
        x = np.stack([sum(.1 * np.sin(2 * np.pi * f * t + c) for f in (110, 440, 1250, 5300)) for c in (0, 1)])
        z = ss._spec(x)
        self.assertEqual(z.shape, (2, 2048, 336))
        y = ss._ispec(z, ss.SEGMENT)
        self.assertLess(np.abs(y - x)[:, 5000:-5000].max(), 1e-3)

    def test_mono_and_other_rates_become_stereo_44k(self):
        stereo = ss.to_stereo_44k(np.sin(np.arange(22050) / 5).astype(np.float32), 22050)
        self.assertEqual(stereo.shape, (2, 44100))
        self.assertTrue(np.allclose(stereo[0], stereo[1]))


class MelodyLineTests(unittest.TestCase):
    def test_main_line_beats_backing_voice_and_vibrato(self):
        # Voz principal Dó–Ré–Mi; uma voz de apoio mais fraca uma terça abaixo; um fragmento de vibrato de 3 quadros.
        line = [(10, 60, 60, .8), (60, 63, 61, .8), (63, 110, 62, .8), (110, 160, 64, .8)]
        backing = [(10, 160, 57, .4)]
        frames, onsets = activations(200, line, backing)
        notes = at.predominant_line(frames, onsets, *at.VOICE_RANGE)
        self.assertEqual([n[2] for n in notes], [60, 62, 64])
        self.assertLessEqual(abs(notes[0][0] - 10), 1)

    def test_repeated_syllables_split_only_on_a_new_attack(self):
        # Mesma altura com um novo ataque e uma queda antes dele (consoante): duas notas. Ataque sem queda: uma.
        frames, onsets = activations(200, [(10, 60, 60, .8), (60, 110, 60, .8)])
        frames[56:60, 60 - at.MIDI_OFFSET] = .35
        self.assertEqual(len(at.predominant_line(frames, onsets, *at.VOICE_RANGE)), 2)
        frames, onsets = activations(200, [(10, 60, 60, .8), (60, 110, 60, .8)])
        self.assertEqual(len(at.predominant_line(frames, onsets, *at.VOICE_RANGE)), 1)

    def test_sung_syllables_on_one_pitch_split_at_the_energy_valleys(self):
        # Três sílabas na mesma altura sem queda na ativação do modelo: os vales da energia da voz (consoantes) e um
        # ataque modesto da própria altura separam as notas; um vale sem ataque (tremolo) não separa.
        frames, onsets = activations(200, [(10, 150, 62, .8)])
        for frame in (55, 100): onsets[frame, 62 - at.MIDI_OFFSET] = .45
        rate = at.SAMPLE_RATE; samples = 200 * at.FFT_HOP; t = np.arange(samples) / rate
        level = np.ones(samples)
        for frame in (55, 100, 130):                     # 130: tremolo, sem ataque no modelo
            centre = frame * at.FFT_HOP; level -= .9 * np.exp(-.5 * ((np.arange(samples) - centre + 2 * at.FFT_HOP) / (1.2 * at.FFT_HOP)) ** 2)
        audio = .3 * level * np.sin(2 * np.pi * 294 * t) * ((t > 10 * at.FFT_HOP / rate) & (t < 150 * at.FFT_HOP / rate))
        syllables = at.syllable_onsets(audio, 200)
        notes = at.predominant_line(frames, onsets, *at.VOICE_RANGE, syllables=syllables)
        self.assertEqual([n[2] for n in notes], [62, 62, 62])
        self.assertEqual([abs(n[0] - f) <= 3 for n, f in zip(notes, (10, 55, 100))], [True, True, True])
        self.assertEqual(len(at.predominant_line(frames, onsets, *at.VOICE_RANGE)), 1)

    def test_silence_is_not_a_note(self):
        frames, onsets = activations(200, [(20, 60, 65, .8)])
        notes = at.predominant_line(frames, onsets, *at.VOICE_RANGE)
        self.assertEqual(len(notes), 1)
        self.assertLessEqual(notes[0][1], 62)

    def test_presence_ignores_bleed(self):
        # Canto forte de 1 a 3 s; um vazamento 40 vezes mais fraco de 4 a 6 s não conta como voz.
        rate = at.SAMPLE_RATE; t = np.arange(rate * 7) / rate
        audio = np.where((t >= 1) & (t < 3), .4, 0) * np.sin(2 * np.pi * 220 * t) + np.where((t >= 4) & (t < 6), .01, 0) * np.sin(2 * np.pi * 330 * t)
        count = len(audio) // at.FFT_HOP
        sung = at.presence(audio, count)
        second = lambda s: int(s * at.FRAMES_PER_SECOND)
        self.assertTrue(sung[second(1.5):second(2.5)].all())
        self.assertFalse(sung[second(4.3):second(5.7)].any())

    def test_instrument_solo_fills_long_silences_of_the_voice(self):
        vocal = [(3.0, 3.5, 60, .7), (3.5, 4.0, 62, .7), (10.0, 10.5, 64, .7)]
        instrument = [(0.5, 1.0, 72, .8), (1.0, 1.5, 74, .8), (3.2, 3.6, 76, .8), (5.0, 5.5, 77, .8), (6.0, 6.5, 79, .2)]
        melody, added = at.fill_gaps(vocal, instrument, end=11)
        self.assertEqual(added, 3)                       # introdução (2) e interlúdio (1); a nota fraca fica de fora
        self.assertEqual([n[2] for n in melody], [72, 74, 60, 62, 77, 64])

    def test_same_attack_twice_is_one_written_note(self):
        with tempfile.TemporaryDirectory() as folder:
            midi = Path(folder) / 'd.mid'
            beats = np.arange(0, 20, .5)
            melody = [(1.0, 1.04, 64, .7), (1.05, 1.4, 64, .7), (1.5, 2.0, 65, .7)]
            at.write_midi(midi, melody, [(1.0, 3.0, 48, .5)], beats, 120)
            notes = [n for n in parse_midi(midi)['notes'] if n['track'] == 1]
            self.assertEqual([n['pitch'] for n in sorted(notes, key=lambda n: n['start'])], [64, 65])


class PipelineChoiceTests(unittest.TestCase):
    def test_without_the_model_the_mix_method_runs(self):
        calls = []
        original = (at.transcribe_separated, at.transcribe_mix, ss.available)
        try:
            at.transcribe_separated = lambda *a, **k: calls.append('separated') or {}
            at.transcribe_mix = lambda *a, **k: calls.append('mix') or {}
            ss.available = lambda: False
            at.transcribe('a.wav', 'a.mid')
            ss.available = lambda: True
            at.transcribe('a.wav', 'a.mid')
        finally:
            at.transcribe_separated, at.transcribe_mix, ss.available = original
        self.assertEqual(calls, ['mix', 'separated'])


@unittest.skipUnless(HAS_MODEL, 'modelo HTDemucs não instalado (tools/prepare_offline_dependencies.py --download)')
class SeparationModelTests(unittest.TestCase):
    def test_stems_add_up_to_the_mix(self):
        rate = ss.SAMPLE_RATE; t = np.arange(int(rate * 9)) / rate
        voice = .3 * np.sin(2 * np.pi * 220 * t + 3 * np.sin(2 * np.pi * 5 * t)) * (np.sin(2 * np.pi * .5 * t) > 0)
        bass = .3 * np.sin(2 * np.pi * 55 * t)
        clicks = np.zeros_like(t); clicks[::rate // 2] = 1; drums = np.convolve(clicks, np.exp(-np.arange(2000) / 200) * np.random.default_rng(2).normal(0, .5, 2000))[:len(t)]
        mix = np.stack([voice + bass + drums] * 2)
        stems = ss.separate(mix)
        self.assertEqual(set(stems), set(ss.SOURCES))
        total = sum(stems.values())
        self.assertLess(np.sqrt(((total - mix) ** 2).mean()) / np.sqrt((mix ** 2).mean()), .1)
        self.assertGreater(np.abs(stems['bass']).mean(), np.abs(stems['vocals']).mean() * .2)

    def test_separated_transcription_of_a_tune(self):
        with tempfile.TemporaryDirectory() as folder:
            wav, midi = Path(folder) / 'a.wav', Path(folder) / 'a.mid'
            synth_wav(wav, MELODY, CHORDS)
            report = at.transcribe(wav, midi, separate=True)
            self.assertTrue(report['separated'])
            self.assertAlmostEqual(report['bpm'], 100, delta=1)
            parsed = parse_midi(midi); ppq = parsed['ppq']
            melody = sorted([n for n in parsed['notes'] if n['track'] == 1], key=lambda n: n['start'])
            # Este "piano" de senoides vai para a pista da voz, onde o batimento com os acordes às vezes parece uma nova
            # sílaba na mesma altura: as alturas e os tempos de ataque têm de bater; poucas repetições são toleradas.
            heard = [n for i, n in enumerate(melody) if i == 0 or n['pitch'] != melody[i - 1]['pitch']]
            self.assertEqual([n['pitch'] for n in heard], MELODY)
            self.assertLessEqual(len(melody) - len(heard), 6)
            starts = [n['start'] / ppq for n in heard]
            self.assertGreaterEqual(sum(s == i for i, s in enumerate(starts)), len(MELODY) - 2)
            self.assertEqual(report['chords'][0], 'C')


if __name__ == '__main__': unittest.main()
