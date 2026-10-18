"""Optional chord staff (the keyboard-diagram notes of each chord, below the melody) and solfège names on the diagrams."""
from pathlib import Path
import io, os, tempfile, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
from test_priority_melody import A, PPQ, engine, worker
from test_repeats import repeated_midi

HARMONY = [(0, ''), (7, '7'), (0, ''), (7, '7'), (5, ''), (0, '')]


def harmony():
    return dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=i * 4 * PPQ, root=r, quality=q, confidence=1., pitch_classes=[r]) for i, (r, q) in enumerate(HARMONY)])


class ChordStaffTests(unittest.TestCase):
    def test_chord_staff_holds_each_diagram_chord(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'acordes.mid'; repeated_midi(path); parsed = engine.parse_midi(path)
            config = worker.manual_melody_config([dict(A, track=1)]); config['chord_staff'] = True
            xml, report = engine.build_musicxml(parsed, config, harmony(), Path(folder), 'Acordes')
            parts = engine.E.parse(xml).getroot().findall('part')
            self.assertEqual(len(parts), 2)
            chords = [n for n in parts[1].iter('note') if n.find('pitch') is not None]
            self.assertEqual(report['melody_analysis']['chord_staff_chords'], 6)
            self.assertEqual(len(report['chord_staff_notes']), 3 + 4 + 3 + 4 + 3 + 3)  # C, G7 (4 notes), C, G7, F, C
            self.assertEqual(sum(n.find('chord') is not None for n in chords), len(report['chord_staff_notes']) - 6)
            _, _, pitches = engine.keyboard_asset(7, '7', 2)
            self.assertEqual([p['pitch'] for p in report['chord_staff_notes'] if p['start'] == 3840], pitches)

    def test_solfege_label_on_the_diagrams(self):
        _, letters, _ = engine.keyboard_asset(0, '', 2)
        _, solfege, _ = engine.keyboard_asset(0, '', 2, 'Dó')
        self.assertIn(b'>C<', letters)
        self.assertIn('>Dó<'.encode(), solfege)
        self.assertNotEqual(letters, solfege)


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeChordStaffTests(unittest.TestCase):
    def test_chord_staff_and_solfege_survive_musescore(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder); out = base / 'acordes'; out.mkdir()
            path = out / 'acordes.mid'; repeated_midi(path); parsed = engine.parse_midi(path)
            config = worker.manual_melody_config([dict(A, track=1)]); config.update(chord_staff=True, fold_repeats=True, note_names='solfejo')
            with patch.object(engine, 'BATCH', base), redirect_stdout(io.StringIO()):
                xml, report = engine.build_musicxml(parsed, config, harmony(), out, 'Acordes')
                final = engine.convert_score(xml, report, out, 'Acordes')
            with zipfile.ZipFile(final) as archive:
                native = engine.E.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.mscx'))))
                svgs = [archive.read(n) for n in archive.namelist() if n.endswith('.svg')]
            self.assertEqual(len(native.findall('Score/Staff')), 2)
            self.assertTrue(svgs and all('>Dó<'.encode() in svg for svg in svgs))
            self.assertEqual(len(native.findall('Score/Staff')[0].findall('.//Image')), len(report['chords']))


if __name__ == '__main__': unittest.main()
