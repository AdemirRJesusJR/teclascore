"""Artist attribution must remain separate from composition credits in the native score."""
from pathlib import Path
import os, sys, tempfile, unittest
import xml.etree.ElementTree as E

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreArtistTests'))
import batch_score as engine


class ArtistMetadataTests(unittest.TestCase):
    def apply(self, **values):
        metadata = engine.score_metadata(values, 'Luz divina')
        root = E.fromstring('<museScore><Score><Staff><VBox/><Measure/></Staff></Score></museScore>')
        engine._apply_native_header(root, metadata)
        return metadata, root

    def test_artist_is_optional_and_not_assumed_composer(self):
        for values in ({}, {'scoreArtist': None}, {'scoreArtist': ''}):
            self.assertEqual(engine.score_metadata(values, 'Fonte')['artist'], '')
        metadata, root = self.apply(scoreArtist='Roberto Carlos')
        self.assertEqual(metadata['artist'], 'Roberto Carlos')
        self.assertEqual(metadata['composer'], '')
        self.assertEqual(root.findtext('./Score/metaTag[@name="artist"]'), 'Roberto Carlos')
        self.assertEqual(root.findtext('./Score/metaTag[@name="composer"]'), '')
        self.assertEqual(root.findtext('./Score/Staff/VBox/Text[style="composer"]/text'), 'Intérprete: Roberto Carlos')

    def test_artist_and_composer_have_distinct_metadata_and_labeled_lines(self):
        metadata, root = self.apply(scoreArtist='Artista A', scoreComposer='Compositor B')
        self.assertEqual(root.findtext('./Score/metaTag[@name="composer"]'), 'Compositor B')
        self.assertEqual(root.findtext('./Score/Staff/VBox/Text[style="composer"]/text'), 'Intérprete: Artista A\nAutor: Compositor B')

    def test_legacy_composer_display_remains_unlabeled(self):
        metadata, root = self.apply(scoreComposer='Ana & José')
        self.assertEqual(engine._header_layout(metadata)['composer'], 'Ana & José')
        self.assertEqual(root.findtext('./Score/Staff/VBox/Text[style="composer"]/text'), 'Ana & José')

    def test_artist_markup_is_literal_text_and_whitespace_normalized(self):
        metadata, root = self.apply(scoreArtist='  João\n&\tMaria <dueto>  ')
        decoded = E.fromstring(E.tostring(root))
        text = decoded.find('./Score/Staff/VBox/Text[style="composer"]/text')
        self.assertEqual(text.text, 'Intérprete: João & Maria <dueto>')
        self.assertEqual(len(text), 0)
        self.assertEqual(metadata['artist'], 'João & Maria <dueto>')

    def test_long_artist_and_composer_wrap_and_reserve_header_height(self):
        metadata, root = self.apply(scoreTitle='Uma canção de nome longo ' * 6,
            scoreArtist='Artista convidado de nome muito extenso ' * 3,
            scoreComposer='Compositor de nome muito extenso ' * 3)
        header = engine._header_layout(metadata)
        self.assertGreaterEqual(len(header['composer'].splitlines()), 4)
        self.assertTrue(all(len(line) <= 70 for line in header['composer'].splitlines()))
        self.assertGreater(header['height'], engine._header_layout(engine.score_metadata({}, 'Fonte'))['height'])
        credit = root.find('./Score/Staff/VBox/Text[style="composer"]')
        self.assertEqual(credit.findtext('align'), 'right,top')
        self.assertGreater(float(credit.find('offset').get('y')), header['subtitle_offset'])
        self.assertEqual(root.findtext('./Score/Staff/VBox/boxAutoSize'), '0')
        self.assertEqual(root.findtext('./Score/Staff/VBox/sizeIsSpatiumDependent'), '1')

    def test_clear_artist_preserves_composer_metadata(self):
        _, root = self.apply(scoreArtist='Artista A', scoreComposer='Compositor B')
        engine._apply_native_header(root, engine.score_metadata(dict(scoreArtist='', scoreComposer='Compositor B'), 'Fonte'))
        self.assertEqual(root.findtext('./Score/metaTag[@name="artist"]'), '')
        self.assertEqual(root.findtext('./Score/Staff/VBox/Text[style="composer"]/text'), 'Compositor B')


if __name__ == '__main__': unittest.main()
