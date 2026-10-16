from pathlib import Path
import sys, tempfile, unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'engine'))
from worker import score_output_stem, score_output_path


class ScoreFileNames(unittest.TestCase):
    def test_property_order_and_fallbacks(self):
        cases = [
            ('Gustavo Lima', 'A gente fez amor', 'wrong.mid', 'Gustavo Lima - A gente fez amor'),
            ('Roberto Carlos', 'Luz divina', 'karaoplay.mid', 'Roberto Carlos - Luz divina'),
            ('', 'Canção', '', 'Canção'),
            (None, 'Canção 2.0', '', 'Canção 2.0'),
            ('  Artista  ', ' Título  ', '', 'Artista - Título'),
            ('Artista\nA', 'Minha\tcanção', '', 'Artista A - Minha canção'),
            ('Artista/Grupo', 'Título: "ao vivo"?', '', 'Artista-Grupo - Título- -ao vivo--'),
            ('', '../Nome: música?', '', '-Nome- música-'),
            ('', 'CON', '', 'Partitura - CON'),
            ('', 'LPT¹.txt', '', 'Partitura - LPT¹.txt'),
            ('', '..', '', 'Partitura'),
            ('', '', r'C:\coleção\Fonte.mid', 'Fonte'),
            ('Artista', '', r'C:\coleção\Fonte.mscz', 'Artista - Fonte'),
            (None, None, '', 'Partitura'),
            ('', 'Título\0final', '', 'Título-final'),
            ('', 'Canção... ', '', 'Canção'),
        ]
        for artist, title, fallback, expected in cases:
            with self.subTest(expected=expected):
                self.assertEqual(score_output_stem(artist, title, fallback), expected)

    def test_long_unicode_does_not_split_characters(self):
        self.assertEqual(score_output_stem('', '🎹' * 140), '🎹' * 100)

    def test_existing_score_and_review_folder_are_preserved(self):
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            first = score_output_path(root, 'Artista', 'Título', 'wrong.mid')
            self.assertEqual(first.name, 'Artista - Título.mscz')
            first.write_bytes(b'approved score')
            (root/'Artista - Título (2) - revisao').mkdir()
            next_path = score_output_path(root, 'Artista', 'Título', 'wrong.mid')
            self.assertEqual(next_path.name, 'Artista - Título (3).mscz')
            self.assertEqual(first.read_bytes(), b'approved score')
            self.assertFalse(next_path.exists())


if __name__ == '__main__': unittest.main()
