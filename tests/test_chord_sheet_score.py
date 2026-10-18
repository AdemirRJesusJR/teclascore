"""Chord sheet pasted as text becomes a MuseScore chord chart: one measure per chord, words under it, its own lines."""
from pathlib import Path
import io, json, os, tempfile, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
from test_priority_melody import engine, worker
from batch_harmony import CHORD_INTERVALS
from inspect_midi import parse_midi
import score_musicxml

MEASURES = [
    {'root': 'C', 'quality': '', 'words': '', 'lineStart': True}, {'root': 'G', 'quality': '', 'bass': 'B', 'words': ''},
    {'root': 'C', 'quality': '', 'words': 'Asa branca que', 'lineStart': True}, {'root': 'G', 'quality': '7', 'words': 'voa no ser-'},
    {'root': 'A', 'quality': 'm7', 'words': 'tão', 'diagramInversion': 0}, {'root': 'F', 'quality': 'maj7', 'words': ''},
]


class ChordSheetHarmonyTests(unittest.TestCase):
    def test_slash_chord_writes_its_bass(self):
        measure = engine.E.Element('measure')
        score_musicxml.emit_harmony(measure, {'tick': 0, 'root': 7, 'quality': '', 'bass_spelling': 'B'}, 0, engine.spellings(0, False))
        self.assertEqual((measure.findtext('harmony/bass/bass-step'), [e.tag for e in measure.find('harmony')]), ('B', ['root', 'kind', 'bass']))
        measure = engine.E.Element('measure')
        score_musicxml.emit_harmony(measure, {'tick': 0, 'root': 1, 'quality': 'm', 'root_spelling': 'C#', 'bass_spelling': 'Bb'}, 0, engine.spellings(0, False))
        self.assertEqual(measure.findtext('harmony/bass/bass-alter'), '-1')


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeChordSheetScoreTests(unittest.TestCase):
    def test_chord_sheet_becomes_a_score(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder); job = base / 'job'; job.mkdir(); output = base / 'saida'
            request = {'measures': MEASURES, 'scoreTitle': 'Asa Branca', 'diagramInversion': 2, 'musescorePath': engine.EXE, 'outputDirectory': str(output)}
            stream = io.StringIO()
            with patch.object(engine, 'BATCH', base / 'lote'), redirect_stdout(stream):
                (base / 'lote').mkdir()
                worker.chord_sheet_score(request, job, engine, parse_midi, CHORD_INTERVALS)
            result = [json.loads(line) for line in stream.getvalue().splitlines() if line.startswith('{')][-1]['data']
            self.assertTrue(Path(result['scorePath']).is_file() and result['measures'] == 6)
            with zipfile.ZipFile(result['scorePath']) as archive:
                native = engine.E.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.mscx'))))
            self.assertEqual(len(native.findall('.//Staff/Measure')), 6)
            self.assertEqual(len(native.findall('.//Harmony')), 6)
            self.assertEqual(len(native.findall('.//Image')), 6)
            lyrics = [''.join(l.find('text').itertext()) for l in native.iter('Lyrics')]
            self.assertEqual(lyrics, ['Asa branca que', 'voa no ser-', 'tão'])
            self.assertEqual(sum(1 for b in native.iter('LayoutBreak') if b.findtext('subtype') == 'line'), 1)
            self.assertTrue(any(h.find('harmonyInfo/bass') is not None for h in native.iter('Harmony')),
                            'G/B keeps its bass')


if __name__ == '__main__': unittest.main()
