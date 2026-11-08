"""Section marks, phrase-aligned systems and the key/time/tempo line under the title."""
from pathlib import Path
import os, sys, tempfile, unittest

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreSectionTests'))
import batch_score as engine
import score_musicxml
import score_sections

DIV = 960
BAR = 4 * DIV
VERSE_CHORDS = [(0, 'major'), (7, 'major'), (9, 'minor'), (5, 'major'), (2, 'minor'), (4, 'minor'), (10, 'major'), (7, '7')]
CHORUS_CHORDS = [(5, 'major'), (0, 'major'), (7, 'major'), (9, 'minor'), (2, 'minor'), (4, '7'), (8, 'major'), (0, 'sus4')]
VERSE_TUNE = [60, 63, 66, 69, 72, 75, 78, 81]
CHORUS_TUNE = [61, 64, 67, 70, 73, 76, 79, 82]


def song(parts):
    """Bars, notes and chords for parts given as (kind, words): the words tag names the verse ('v1', 'v2') or the
    chorus ('r'), so a chorus repeats its words and verses do not; 'intro' and 'final' have no words."""
    bars, notes, chords = [], [], []
    for kind, words in parts:
        length = 2 if kind in ('intro', 'final') else 8
        for i in range(length):
            start = len(bars) * BAR
            bars.append({'start': start, 'end': start + BAR, 'meter': (4, 4)})
            root, quality = (CHORUS_CHORDS if kind == 'chorus' else VERSE_CHORDS)[i]
            chords.append({'tick': start, 'root': root, 'quality': quality})
            pitch = (CHORUS_TUNE if kind == 'chorus' else VERSE_TUNE)[i]
            for beat in range(4):
                item = {'start': start + beat * DIV, 'end': start + (beat + 1) * DIV, 'pitch': pitch + beat % 2 * 2}
                if words: item['lyric'] = {'text': f'{words}x{i}y{beat}'}
                notes.append(item)
    return bars, notes, chords


VERSE1, VERSE2, CHORUS = 'v1', 'v2', 'r'


class SectionTests(unittest.TestCase):
    def test_song_parts_get_marks(self):
        bars, notes, chords = song([('intro', None), ('verse', VERSE1), ('chorus', CHORUS),
                                    ('verse', VERSE2), ('chorus', CHORUS), ('chorus', CHORUS), ('final', None)])
        labels = [(s['bar'], s['label']) for s in score_sections.detect_sections(bars, notes, chords, DIV)]
        # The chorus played twice in a row keeps one mark.
        self.assertEqual(labels, [(0, 'Intro'), (2, 'A'), (10, 'Refrão'), (18, 'A'), (26, 'Refrão'), (42, 'Final')])

    def test_no_marks_without_structure(self):
        bars, notes, chords = song([('verse', VERSE1)])
        self.assertEqual(score_sections.detect_sections(bars, notes, chords, DIV), [])

    def test_systems_start_at_sections(self):
        bars, notes, chords = song([('intro', None), ('verse', VERSE1), ('chorus', CHORUS)])
        starts, lines = score_musicxml.layout_bars(bars, notes, chords, section_starts=(2, 10))
        self.assertIn(2, starts)
        self.assertIn(10, starts)
        self.assertEqual([bar for line in lines for bar in line], list(range(len(bars))))
        self.assertTrue(all(len(line) <= score_musicxml.BARS_PER_SYSTEM for line in lines))

    def test_lyric_width_grows_with_the_syllable(self):
        self.assertGreater(score_musicxml.lyric_width('raiar'), score_musicxml.lyric_width('vem'))


class HeaderInfoTests(unittest.TestCase):
    def test_key_names(self):
        self.assertEqual(engine.key_name({'fifths': -3, 'minor': True}), 'Dó menor (Cm)')
        self.assertEqual(engine.key_name({'fifths': 4}), 'Mi maior (E)')
        self.assertEqual(engine.key_name({'fifths': 3, 'minor': True}), 'Fá sustenido menor (F#m)')
        self.assertIsNone(engine.key_name({'fifths': 9}))

    def test_line_with_original_key(self):
        line = engine.header_info({'fifths': 0}, (4, 4), 85.6, original_key={'fifths': -3})
        self.assertEqual(line, 'Tom: Dó maior (C), original Mi bemol maior · Compasso 4/4 · ♩ = 86')

    def test_line_marks_key_changes(self):
        self.assertIn('(muda de tom)', engine.header_info({'fifths': 1}, (3, 4), 120, key_changes=1))


if __name__ == '__main__':
    unittest.main()
