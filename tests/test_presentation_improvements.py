"""Readable durations, lyric words and accents, highlighted diagrams and the notation quality check."""
from pathlib import Path
import os, sys, tempfile, unittest
import xml.etree.ElementTree as E

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScorePresentationTests'))
import batch_score as engine
import keyboard_svg
import lyrics_accents
import score_quality
from test_priority_melody import A, PPQ, note, parsed

DIV = engine.DIV
NS = '{http://www.w3.org/2000/svg}'


def detached(count=24):
    """A detached melody: a 32nd note on every beat, then silence until the next beat."""
    return [note(i, 60 + i % 5, duration=1 / 8) for i in range(count)]


class ReadableDurationTests(unittest.TestCase):
    def events(self, clean):
        config = {'melody_sources': [A], 'clean_durations': clean}
        notes, _, report = engine.melody_events(parsed(detached()), config, 0, 24 * PPQ)
        return notes, report

    def test_attacks_never_move(self):
        plain, _ = self.events(False)
        clean, report = self.events(True)
        self.assertEqual([n['start'] for n in plain], [n['start'] for n in clean])
        self.assertEqual([n['pitch'] for n in plain], [n['pitch'] for n in clean])
        self.assertGreater(report['durations_cleaned'], 0)

    def test_tiny_notes_become_eighths(self):
        clean, _ = self.events(True)
        self.assertTrue(all(n['end'] - n['start'] == DIV // 2 for n in clean[:-1]), [n['end'] - n['start'] for n in clean[:5]])

    def test_short_gap_is_filled(self):
        stop, changed = engine._readable_stop(0, DIV * 3 // 4, DIV, 8 * DIV, 0, {0: 4})
        self.assertEqual((stop, changed), (DIV, 1))

    def test_long_rest_is_kept(self):
        # An eighth note followed by a long rest keeps its length: the rest is part of the phrase.
        stop, changed = engine._readable_stop(0, DIV // 2, 4 * DIV, 8 * DIV, 0, {0: 4})
        self.assertEqual((stop, changed), (DIV // 2, 0))

    def test_triplet_beats_are_untouched(self):
        stop, changed = engine._readable_stop(0, DIV // 6, DIV, 8 * DIV, 0, {0: 3})
        self.assertEqual((stop, changed), (DIV // 6, 0))

    def test_off_by_default_for_direct_callers(self):
        _, report = self.events(False)
        self.assertNotIn('durations_cleaned', report)


class LyricWordTests(unittest.TestCase):
    def syllables(self, texts):
        events = [{'kind': 'meta', 'type': 1, 'tick': i * PPQ, 'order': i, 'data': t.encode('cp1252')} for i, t in enumerate(texts)]
        return engine.karaoke_syllables({'events': events, 'ppq': PPQ})

    def test_space_at_the_end_of_the_previous_syllable_starts_a_word(self):
        result = self.syllables(['o ', 'seu', ' na', 'mo', 'ra', 'do'] * 4)
        self.assertEqual([s['word_start'] for s in result[:6]], [True, True, True, False, False, False])

    def test_accents_restored_when_the_file_has_none(self):
        result = self.syllables(['vo', 'ce ', 'no ', 'seu ', 'pes', 'co', 'co ', 'co', 'ra', 'cao '] * 2)
        words = ''.join(s['text'] + (' ' if i + 1 < len(result) and result[i + 1]['word_start'] else '') for i, s in enumerate(result))
        self.assertIn('você no seu pescoço coração', words)
        self.assertGreater(result[0]['accents_restored'], 0)

    def test_files_with_accents_are_left_alone(self):
        result = self.syllables(['vo', 'cê ', 'e ', 'vo', 'ce '] * 4)
        self.assertEqual(result[3]['text'] + result[4]['text'], 'voce')

    def test_ambiguous_words_are_not_changed(self):
        for word in ('e', 'esta', 'pais', 'sabia', 'nos', 'la'):
            self.assertIsNone(lyrics_accents.restore_word(word), word)
        self.assertEqual(lyrics_accents.restore_word('Coracao'), 'Coração')
        self.assertEqual(lyrics_accents.restore_word('NAO'), 'NÃO')


class HighlightedDiagramTests(unittest.TestCase):
    def test_every_voicing_fits_its_window_and_marks_its_keys(self):
        for root in range(12):
            for quality in engine.CHORD_INTERVALS:
                for inversion in range(3):
                    for style in ('destacado', 'cinza'):
                        with self.subTest(root=root, quality=quality, inversion=inversion, style=style):
                            _, data, pitches = engine.keyboard_asset(root, quality, inversion, 'C', style)
                            svg = E.fromstring(data)
                            self.assertEqual((svg.get('width'), svg.get('height')), ('282', '84'))
                            marked = sorted(int(e.get('data-midi')) for e in svg.iter() if e.get('data-midi'))
                            self.assertEqual(marked, pitches)
                            self.assertIn(','.join(map(str, pitches)), svg.findtext(NS + 'desc'))
                            names = [t.text for t in svg.iter(NS + 'text') if t.get('font-weight') == 'bold']
                            self.assertEqual(len(names), len(pitches))

    def test_bass_key_is_darker(self):
        _, data, pitches = engine.keyboard_asset(0, 'm', 2, 'C', 'destacado')
        fills = {int(e.get('data-midi')): e.get('fill') for e in E.fromstring(data).iter() if e.get('data-midi')}
        self.assertEqual(fills[pitches[0]], '#1D3F99')
        self.assertTrue(all(fills[p] != '#1D3F99' for p in pitches[1:]))

    def test_names_follow_the_chord(self):
        def names(root, quality, spelling, solfege=False):
            _, data, _ = engine.keyboard_asset(root, quality, 2, 'Dó' if solfege else 'C', 'destacado', False, spelling)
            return {t.text for t in E.fromstring(data).iter(NS + 'text') if t.get('font-weight') == 'bold'}
        self.assertEqual(names(0, 'm', 'C'), {'C', 'Eb', 'G'})
        self.assertEqual(names(6, 'm7', 'F#'), {'F#', 'A', 'C#', 'E'})
        self.assertEqual(names(10, '', 'Bb'), {'Bb', 'D', 'F'})
        self.assertEqual(names(0, 'dim7', 'C'), {'C', 'Eb', 'Gb', 'A'})
        self.assertEqual(names(0, 'm', 'C', True), {'Dó', 'Mib', 'Sol'})

    def test_dots_style_marks_keys_and_names_them_underneath(self):
        for root in range(12):
            for quality in engine.CHORD_INTERVALS:
                for inversion in range(3):
                    with self.subTest(root=root, quality=quality, inversion=inversion):
                        _, data, pitches = engine.keyboard_asset(root, quality, inversion, 'C', 'pontos')
                        svg = E.fromstring(data)
                        self.assertEqual((svg.get('width'), svg.get('height')), ('282', '84'))
                        dots = list(svg.iter(NS + 'circle'))
                        self.assertEqual(sorted(int(c.get('data-midi')) for c in dots), pitches)
                        self.assertTrue(all(r.get('fill') in ('white', '#111111') for r in svg.iter(NS + 'rect')))
                        ring = [c for c in dots if c.get('fill') == 'none']
                        self.assertEqual([int(c.get('data-midi')) for c in ring], [pitches[0]])
                        texts = list(svg.iter(NS + 'text'))
                        self.assertEqual(len(texts), len(pitches))
                        self.assertTrue(all(float(t.get('y')) > 61 for t in texts))
                        self.assertEqual([t.get('font-weight') for t in texts].count('bold'), 1)

    def test_dots_names_split_in_two_rows_only_when_crowded(self):
        self.assertEqual(keyboard_svg.dot_name_rows([29, 57, 85]), ('16', ['79', '79', '79']))
        self.assertEqual(keyboard_svg.dot_name_rows([29, 43, 57]), ('12', ['72', '83', '72']))

    def test_dots_is_the_default_style(self):
        self.assertEqual(keyboard_svg.diagram_style(None), 'pontos')
        self.assertEqual(keyboard_svg.diagram_style('destacado'), 'destacado')

    def test_window_prefers_c_or_f(self):
        self.assertIn(keyboard_svg.window_start([55, 60, 63]) % 12, (0, 5))

    def test_classic_stays_byte_identical(self):
        path, data, _ = engine.keyboard_asset(0, '', 2)
        self.assertEqual(E.fromstring(data).find(NS + 'circle').get('r'), '4.2')
        self.assertEqual(path, engine.md4(data) + '.svg')

    def test_unknown_style_is_rejected(self):
        with self.assertRaises(ValueError):
            keyboard_svg.diagram_style('neon')


class QualityTests(unittest.TestCase):
    def report(self, notes, **extra):
        bars = [{'start': i * 4 * DIV, 'end': (i + 1) * 4 * DIV} for i in range(8)]
        return {'notes': notes, 'bars': bars, 'measures': 8, 'pages': 1, 'melody_analysis': {}, **extra}

    def test_clean_melody_scores_high(self):
        notes = [{'start': i * DIV, 'end': (i + 1) * DIV, 'pitch': 60 + i % 7} for i in range(32)]
        result = score_quality.evaluate(self.report(notes))
        self.assertEqual(result['issues'], [])
        self.assertGreaterEqual(result['score'], 95)

    def test_guide_notes_and_tiny_rests_are_reported(self):
        notes = [{'start': i * DIV, 'end': i * DIV + DIV // 8, 'pitch': 60, 'source': {'track': 1, 'channel': 1, 'program': 0}} for i in range(16)]
        result = score_quality.evaluate(self.report(notes))
        self.assertLess(result['score'], 70)
        self.assertTrue(any('nota-guia' in issue for issue in result['issues']))
        self.assertTrue(any('curtas' in issue for issue in result['issues']))

    def test_crowded_lyrics_are_reported(self):
        notes = [{'start': i * DIV, 'end': (i + 1) * DIV, 'pitch': 60 + i % 7} for i in range(32)]
        result = score_quality.evaluate(self.report(notes, melody_analysis={'lyrics': {'attached': 10, 'crowded_notes': [0, 960, 1920, 2880]}}))
        self.assertTrue(any('palavras' in issue for issue in result['issues']))


class StyleTests(unittest.TestCase):
    def test_footer_and_hidden_staves_reach_the_style(self):
        import inspect
        source = inspect.getsource(engine.convert_score)
        self.assertIn("hideEmptyStaves='1'", source)
        self.assertIn('$:workTitle: · página $P de $n', source)
        self.assertIn("hide.text='2'", source)


if __name__ == '__main__':
    unittest.main()
