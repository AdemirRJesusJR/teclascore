"""Chord symbols written in the user's notation: suffix texts (7M, ø, °) and flats or sharps on altered roots."""
from pathlib import Path
import os, sys, tempfile, unittest
import xml.etree.ElementTree as E

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreChordNotationTests'))
import batch_score as engine
import score_musicxml


def fixture(chords):
    ppq = 480
    notes = [dict(start=i * ppq, end=round((i + .8) * ppq), duration=round(.8 * ppq), track=1, channel=1, program=0, pitch=[60, 64, 67, 72][i % 4], velocity=90) for i in range(32)]
    parsed = dict(path='notation.mid', sha256='synthetic', ppq=ppq, notes=notes, format=1, end_tick=notes[-1]['end'], tracks=[], warnings=[],
                  events=[dict(tick=0, track=0, kind='meta', type=88, data=bytes([4, 2, 24, 8]))])
    config = dict(melody_sources=[dict(track=1, channel=1, program=0)], diagram_inversion=2)
    events = [dict(tick=i * 4 * ppq, root=root, quality=quality, confidence=1., pitch_classes=sorted((root + n) % 12 for n in engine.CHORD_INTERVALS[quality]))
              for i, (root, quality) in enumerate(chords)]
    return parsed, config, dict(key=dict(fifths=0, minor=False, root=0), events=events)


def harmonies(xml):
    tree = E.fromstring(Path(xml).read_bytes()) if Path(str(xml)).exists() else E.fromstring(xml)
    result = []
    for h in tree.iter('harmony'):
        root = h.find('root'); kind = h.find('kind')
        alter = root.findtext('root-alter')
        result.append((root.findtext('root-step') + {'1': '#', '-1': 'b'}.get(alter or '', ''), kind.get('text')))
    return result


class ChordNotationTests(unittest.TestCase):
    def build(self, notation=None, chords=((0, 'maj7'), (11, 'm7b5'), (10, ''), (6, 'dim'))):
        parsed, config, harmony = fixture(chords)
        if notation is not None: config['chord_notation'] = notation
        with tempfile.TemporaryDirectory() as directory:
            xml, _ = engine.build_musicxml(parsed, config, harmony, Path(directory), 'Teste')
            return harmonies(xml if isinstance(xml, (str, bytes)) and not Path(str(xml)).exists() else Path(xml))

    def test_default_keeps_the_internal_names(self):
        found = self.build()[:4]
        self.assertEqual([t for _, t in found], ['maj7', 'm7b5', '', 'dim'])

    def test_user_notation_and_flats(self):
        found = self.build({'texts': {'maj7': '7M', 'm7b5': 'm7(b5)', 'dim': '°'}, 'accidentals': 'flats'})[:4]
        self.assertEqual(found, [('C', '7M'), ('B', 'm7(b5)'), ('Bb', ''), ('Gb', '°')])

    def test_sharps(self):
        found = self.build({'texts': {}, 'accidentals': 'sharps'})[:4]
        self.assertEqual([r for r, _ in found], ['C', 'B', 'A#', 'F#'])

    def test_notation_does_not_leak_between_scores(self):
        self.build({'texts': {'maj7': '7M'}, 'accidentals': 'flats'})
        self.assertEqual(self.build()[0], ('C', 'maj7'))
        self.assertEqual(score_musicxml.CHORD_ACCIDENTALS, 'auto')


if __name__ == '__main__': unittest.main()
