"""Literal repetitions become repeat barlines (score_repeats.py), only when exactly equal and safe."""
from pathlib import Path
import io, os, struct, tempfile, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
from test_priority_melody import A, PPQ, engine, worker
import score_repeats

DIV = 960
BAR = 4 * DIV


def bars(count):
    return [{'start': i * BAR, 'end': (i + 1) * BAR, 'meter': (4, 4), 'partial': False} for i in range(count)]


def phrase(bar_index, pitches):
    return [{'start': bar_index * BAR + i * DIV, 'end': bar_index * BAR + (i + 1) * DIV, 'pitch': p} for i, p in enumerate(pitches)]


class FoldTests(unittest.TestCase):
    def material(self):
        notes = phrase(0, [60, 62, 64, 65]) + phrase(1, [67, 65, 64, 62]) + phrase(2, [60, 62, 64, 65]) + phrase(3, [67, 65, 64, 62]) \
            + phrase(4, [72, 71, 69, 67]) + phrase(5, [65, 64, 62, 60])
        chords = [{'tick': i * BAR, 'root': r, 'quality': q} for i, (r, q) in enumerate([(0, ''), (7, '7'), (0, ''), (7, '7'), (5, ''), (0, '')])]
        return bars(6), notes, chords, {beat: 4 for beat in range(24)}

    def test_two_bars_played_twice_are_written_once(self):
        b, notes, chords, grids = self.material()
        tempos = [{'tick': 0, 'bpm': 100}]
        folds = score_repeats.fold_repeats(b, notes, chords, grids, tempos, [], PPQ, 0, DIV)
        self.assertEqual(folds, [{'first_measure': 1, 'measures': 2, 'removed_quarters': 8.0}])
        self.assertEqual(len(b), 4)
        self.assertTrue(b[0]['repeat_start'] and b[1]['repeat_end'])
        self.assertEqual([n['pitch'] for n in notes if 2 * BAR <= n['start'] < 3 * BAR], [72, 71, 69, 67])
        self.assertEqual([c['root'] for c in chords], [0, 7, 5, 0])
        self.assertEqual(sorted(grids), list(range(16)))
        self.assertEqual(b[-1]['end'], 4 * BAR)

    def test_lyrics_keep_the_repetition_written(self):
        b, notes, chords, grids = self.material()
        notes[8]['lyric'] = {'text': 'sol', 'syllabic': 'single'}
        self.assertEqual(score_repeats.fold_repeats(b, notes, chords, grids, [], [], PPQ, 0, DIV), [])
        self.assertEqual(len(b), 6)

    def test_different_chord_or_tie_across_the_edge_is_not_folded(self):
        b, notes, chords, grids = self.material()
        chords[2]['quality'] = 'm'
        self.assertEqual(score_repeats.fold_repeats(b, notes, chords, grids, [], [], PPQ, 0, DIV), [])
        b, notes, chords, grids = self.material()
        notes[7]['end'] += DIV  # last note of the block tied into the repetition
        self.assertEqual(score_repeats.fold_repeats(b, notes, chords, grids, [], [], PPQ, 0, DIV), [])

    def test_tempo_change_inside_the_repetition_is_not_folded(self):
        b, notes, chords, grids = self.material()
        tempo_tick = (2 * BAR + DIV) * PPQ / DIV
        self.assertEqual(score_repeats.fold_repeats(b, notes, chords, grids, [{'tick': 0}, {'tick': tempo_tick}], [], PPQ, 0, DIV), [])


def repeated_midi(path):
    """Two bars, the same two bars again, then two different bars (quarter notes, 4/4)."""
    def vlq(value):
        data = [value & 127]; value >>= 7
        while value: data.insert(0, (value & 127) | 128); value >>= 7
        return bytes(data)
    pitches = [60, 62, 64, 65, 67, 65, 64, 62] * 2 + [72, 71, 69, 67, 65, 64, 62, 60]
    events = []
    for index, pitch in enumerate(pitches):
        events += [(index * PPQ, bytes([0x90, pitch, 90])), (index * PPQ + PPQ - 1, bytes([0x80, pitch, 0]))]
    body = b''; previous = 0
    for tick, message in events: body += vlq(tick - previous) + message; previous = tick
    body += b'\x00\xff\x2f\x00'
    conductor = b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    path.write_bytes(b'MThd' + struct.pack('>IHHH', 6, 1, 2, PPQ) + b'MTrk' + struct.pack('>I', len(conductor)) + conductor + b'MTrk' + struct.pack('>I', len(body)) + body)


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeRepeatTests(unittest.TestCase):
    def test_repeat_barlines_survive_musescore(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder); out = base / 'ritornelo'; out.mkdir()
            path = out / 'ritornelo.mid'; repeated_midi(path); parsed = engine.parse_midi(path)
            harmony = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=i * 4 * PPQ, root=r, quality=q, confidence=1., pitch_classes=[r])
                                                                    for i, (r, q) in enumerate([(0, ''), (7, '7'), (0, ''), (7, '7'), (5, ''), (0, '')])])
            config = worker.manual_melody_config([dict(A, track=1)]); config['fold_repeats'] = True
            with patch.object(engine, 'BATCH', base), redirect_stdout(io.StringIO()):
                xml, report = engine.build_musicxml(parsed, config, harmony, out, 'Ritornelo')
                final = engine.convert_score(xml, report, out, 'Ritornelo')
            self.assertEqual(report['melody_analysis']['repeat_folds'][0]['measures'], 2)
            with zipfile.ZipFile(final) as archive:
                native = engine.E.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.mscx'))))
            self.assertEqual(len(native.findall('.//Staff/Measure')), 4)
            self.assertEqual(len(native.findall('.//startRepeat')), 1)
            self.assertEqual(len(native.findall('.//endRepeat')), 1)


if __name__ == '__main__': unittest.main()
