"""Music font and staff size chosen in the app reach the MuseScore style of the generated score."""
from pathlib import Path
import io, os, tempfile, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
from test_priority_melody import A, PPQ, engine, worker
from test_repeats import repeated_midi


class StyleValueTests(unittest.TestCase):
    def test_choices_map_to_style_tags(self):
        self.assertEqual(engine.score_style_values({'music_font': 'Bravura', 'size': 'grande'}),
                         {'musicalSymbolFont': 'Bravura', 'musicalTextFont': 'Bravura Text', 'spatium': '2.0'})
        self.assertEqual(engine.score_style_values({'music_font': 'Leland', 'size': 'normal'}), {})
        self.assertEqual(engine.score_style_values({'music_font': '../x', 'size': '9'}), {})
        self.assertEqual(engine.score_style_values(None), {})

    def test_spacing_choice_moves_the_system_distances(self):
        base = lambda: {'minSystemDistance': '17', 'maxSystemDistance': '24'}
        self.assertEqual(engine.apply_score_spacing(base(), 'normal'), base())
        compact = engine.apply_score_spacing(base(), 'compacto')
        self.assertEqual((compact['minSystemDistance'], compact['maxSystemDistance'], compact['maxSystemSpread']), ('17.0', '19.0', '19.0'))
        ample = engine.apply_score_spacing(base(), 'amplo')
        self.assertEqual((ample['minSystemDistance'], ample['maxSystemDistance'], ample['maxSystemSpread']), ('23.0', '32.0', '32.0'))
        self.assertEqual([engine.score_spacing(c) for c in ({'spacing': 'compacto'}, {'spacing': 'x'}, None)], ['compacto', 'normal', 'normal'])
        # The instrument-label fallback keeps its label room; compact only stops the stretching.
        self.assertEqual(engine.spacing_limits('compacto', 28, 36), (28, 30))


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeStyleTests(unittest.TestCase):
    def test_font_and_size_are_saved_in_the_score(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder); out = base / 'estilo'; out.mkdir()
            path = out / 'estilo.mid'; repeated_midi(path); parsed = engine.parse_midi(path)
            harmony = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=0, root=0, quality='', confidence=1., pitch_classes=[0, 4, 7])])
            config = worker.manual_melody_config([dict(A, track=1)]); config['score_style'] = {'music_font': 'Bravura', 'size': 'grande'}
            with patch.object(engine, 'BATCH', base), redirect_stdout(io.StringIO()):
                xml, report = engine.build_musicxml(parsed, config, harmony, out, 'Estilo')
                final = engine.convert_score(xml, report, out, 'Estilo')
            with zipfile.ZipFile(final) as archive:
                style = archive.read(next(n for n in archive.namelist() if n.endswith('.mss'))).decode('utf-8')
            self.assertIn('<musicalSymbolFont>Bravura</musicalSymbolFont>', style)
            self.assertIn('<spatium>2</spatium>', style.replace('<spatium>2.0</spatium>', '<spatium>2</spatium>'))
            self.assertTrue(report['geometric_layout_qa']['passed'])


if __name__ == '__main__': unittest.main()
