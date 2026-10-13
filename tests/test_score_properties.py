"""Score metadata and keyboard voicings; no MIDI collection or MuseScore required."""
from pathlib import Path
import os, sys, tempfile, unittest
import xml.etree.ElementTree as E

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScorePropertiesTests'))
import batch_score as engine


class ScoreMetadataTests(unittest.TestCase):
    def test_legacy_defaults_and_null_remain_compatible(self):
        title = engine.default_score_title('Norte_ForroAbertura_Show.mid')
        expected = dict(title='Norte ForroAbertura Show', subtitle=engine.DEFAULT_SCORE_SUBTITLE, composer='')
        self.assertEqual(engine.score_metadata({}, title), expected)
        self.assertEqual(engine.score_metadata(dict(scoreTitle=None, scoreSubtitle=None, scoreComposer=None), title), expected)

    def test_empty_title_falls_back_and_empty_other_fields_remove(self):
        self.assertEqual(engine.score_metadata(dict(scoreTitle=' \t', scoreSubtitle='', scoreComposer=' \n'), 'Fonte'),
                         dict(title='Fonte', subtitle='', composer=''))

    def test_default_subtitle_follows_inversion_but_explicit_text_is_preserved(self):
        for inversion, label in enumerate(('posição fundamental', '1ª inversão', '2ª inversão')):
            data = dict(diagram_inversion=inversion)
            self.assertTrue(engine.score_metadata(data, 'Fonte')['subtitle'].endswith(label))
            data['scoreSubtitle'] = 'Meu subtítulo'
            self.assertEqual(engine.score_metadata(data, 'Fonte')['subtitle'], 'Meu subtítulo')

    def test_invalid_values_rejected_before_generation(self):
        for field, limit in [('scoreTitle',160), ('scoreSubtitle',200), ('scoreComposer',120)]:
            for value in (False, 12, [], 'x'*(limit+1), 'texto\x00', 'texto\ud800'):
                with self.subTest(field=field, value=repr(value)), self.assertRaises(ValueError):
                    engine.score_metadata({field:value}, 'Fonte')

    def test_native_header_preserves_unicode_and_escapes_markup_as_text(self):
        metadata = engine.score_metadata(dict(scoreTitle='Canção <do> Sol & Lua',
                scoreSubtitle='Para  teclado\n– versão "João"', scoreComposer='Ana & José <Autor>'), 'Fonte')
        root = E.fromstring('<museScore><Score><Staff><VBox><Text><style>subtitle</style><text>antigo</text></Text></VBox><Measure/></Staff></Score></museScore>')
        engine._apply_native_header(root, metadata)
        decoded = E.fromstring(E.tostring(root))
        for role in ('title','subtitle','composer'):
            text = decoded.find(f'./Score/Staff/VBox/Text[style="{role}"]/text')
            self.assertEqual(text.text, metadata[role])
            self.assertEqual(len(text), 0, 'Literal user text must not become markup')
        self.assertEqual(decoded.findtext('./Score/metaTag[@name="composer"]'), metadata['composer'])
        engine._apply_native_header(root, dict(title='Fonte', subtitle='', composer=''))
        self.assertIsNone(root.find('./Score/Staff/VBox/Text[style="subtitle"]'))
        self.assertIsNone(root.find('./Score/Staff/VBox/Text[style="composer"]'))


class DiagramInversionTests(unittest.TestCase):
    def test_known_triads_sevenths_and_extensions(self):
        expected = {
            '': ([60,64,67], [52,55,60], [55,60,64]),
            'm7': ([60,63,67,70], [51,55,58,60], [55,58,60,63]),
            '9': ([60,62,64,67,70], [52,55,58,60,62], [55,58,60,62,64]),
            'add9': ([60,62,64,67], [52,55,60,62], [55,60,62,64]),
        }
        for quality, inversions in expected.items():
            for inversion, pitches in enumerate(inversions):
                with self.subTest(quality=quality, inversion=inversion):
                    self.assertEqual(engine.keyboard_asset(0, quality, inversion)[2], pitches)

    def test_all_chords_preserve_pitch_classes_and_selected_bass(self):
        for root in range(12):
            for quality, intervals in engine.CHORD_INTERVALS.items():
                for inversion in range(3):
                    with self.subTest(root=root, quality=quality, inversion=inversion):
                        _, data, pitches = engine.keyboard_asset(root, quality, inversion)
                        bass_interval = 0 if inversion==0 else next(x for x in ((3,4,2,5) if inversion==1 else (7,6,8)) if x in intervals)
                        self.assertEqual(pitches[0]%12, (root+bass_interval)%12)
                        self.assertEqual({p%12 for p in pitches}, {(root+i)%12 for i in intervals})
                        self.assertEqual(len(pitches), len(intervals))
                        self.assertTrue(48 <= min(pitches) < max(pitches) <= 71)
                        self.assertIn(','.join(map(str,pitches)), E.fromstring(data).findtext(engine.NS+'desc'))

    def test_second_inversion_assets_are_byte_identical_to_legacy(self):
        # Reproduce the delivered second-inversion serializer to protect existing SVG hashes.
        for root in range(12):
            for quality, intervals in engine.CHORD_INTERVALS.items():
                fifth = next(x for x in (7,6,8) if x in intervals)
                bass = 53+((root+fifth-53)%12)
                pitches = sorted(bass+(root+x-bass)%12 for x in intervals)
                if max(pitches)>71:pitches=[p-12 for p in pitches]
                svg=E.fromstring(engine.svg_keyboard(str(root)+quality,pitches))
                svg.find(engine.NS+'title').text='Acorde em segunda inversão'
                svg.find(engine.NS+'desc').text='Quinta no grave; notas MIDI: '+','.join(map(str,pitches))
                expected=E.tostring(svg,encoding='utf-8',xml_declaration=True)
                path,data,_=engine.keyboard_asset(root,quality)
                self.assertEqual(data,expected)
                self.assertEqual(path,engine.md4(expected)+'.svg')

    def test_invalid_inversions_rejected(self):
        self.assertEqual(engine.diagram_inversion(None), 2)
        for invalid in (-1,3,True,False,1.0,'1',[],{}):
            with self.subTest(invalid=repr(invalid)), self.assertRaises(ValueError):
                engine.keyboard_asset(0,'',invalid)


if __name__ == '__main__':unittest.main()
