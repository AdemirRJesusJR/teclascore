"""Testes rápidos da bateria de precisão: corpus determinístico, leitura do MusicXML e métricas."""
from pathlib import Path
import json
import sys
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parents[1] / 'engine'))
import make_corpus  # noqa: E402
import metrics  # noqa: E402
from inspect_midi import parse_midi  # noqa: E402


def perfect_score(truth):
    """A partitura que o gabarito descreve, já no formato de metrics.read_musicxml."""
    lyrics_by_onset = {}
    for item in truth['lyrics']:
        if not item['spoken']:
            lyrics_by_onset.setdefault(item['onset'], []).append(item)
    notes = []
    for note in truth['melody']:
        items = lyrics_by_onset.get(note['onset'], [])
        lyric = []
        if items:
            following = [x for x in truth['lyrics'] if not x['spoken'] and x['onset'] > note['onset']]
            continues = bool(following) and not following[0]['word_start']
            starts = items[0]['word_start']
            syllabic = ('begin' if continues else 'single') if starts else ('middle' if continues else 'end')
            lyric = [{'number': '1', 'text': ' '.join(x['text'] for x in items), 'syllabic': syllabic, 'elision': False}]
        notes.append(dict(note, part=0, chord=False, lyrics=lyric))
    harmonies = [{'quarter': c['onset'], 'step': c['root'][0], 'alter': c['root'].count('#') - c['root'].count('b'),
                  'pc': c['root_pc'], 'quality': c['quality'],
                  'bass': None if c['bass'] is None else {'step': c['bass'][0], 'alter': c['bass'].count('#') - c['bass'].count('b'),
                                                         'pc': c['bass_pc']}} for c in truth['chords']]
    keys = [{'quarter': 0.0, **truth['key']}] + [dict(k) for k in truth['key_changes']]
    bar = truth['bar_quarters']
    measures = []
    start = 0.0
    if truth['pickup_quarters']:
        measures.append({'start': 0.0, 'length': truth['pickup_quarters'], 'implicit': True})
        start = truth['pickup_quarters']
    while start < 200:
        measures.append({'start': start, 'length': bar, 'implicit': False})
        start += bar
    return {'notes': notes, 'harmonies': harmonies, 'keys': keys,
            'times': [{'quarter': 0.0, 'beats': truth['meter']['beats'], 'beat_type': truth['meter']['beat_type']}],
            'measures': measures, 'parts': [{'id': 'P1', 'name': 'Teclado', 'auxiliary': False}]}


XML = '''<?xml version="1.0" encoding="utf-8"?>
<score-partwise version="4.0"><part-list><score-part id="P1"><part-name>Teclado</part-name></score-part>
<score-part id="P2"><part-name>Acordes</part-name></score-part></part-list>
<part id="P1">
 <measure number="1" implicit="yes"><attributes><divisions>2</divisions><key><fifths>0</fifths><mode>major</mode></key>
  <time><beats>4</beats><beat-type>4</beat-type></time><campo-novo>ignorado</campo-novo></attributes>
  <note><pitch><step>G</step><octave>4</octave></pitch><duration>2</duration><voice>1</voice>
   <lyric number="1"><syllabic>single</syllabic><text>que</text><elision>‿</elision><text>eu</text></lyric></note></measure>
 <measure number="2"><attributes><key><fifths>-5</fifths><mode>major</mode></key></attributes>
  <harmony><root><root-step>G</root-step></root><kind text="7(b9)">dominant</kind><bass><bass-step>B</bass-step></bass></harmony>
  <harmony><root><root-step>D</root-step><root-alter>-1</root-alter></root><kind text="6/9">major-sixth</kind><offset>4</offset></harmony>
  <note><pitch><step>G</step><alter>1</alter><octave>4</octave></pitch><duration>8</duration><tie type="start"/><voice>1</voice>
   <lyric number="1"><syllabic>begin</syllabic><text>can</text></lyric></note>
  <note><chord/><pitch><step>E</step><octave>4</octave></pitch><duration>8</duration><voice>1</voice></note>
  <note><grace/><pitch><step>A</step><octave>4</octave></pitch><voice>1</voice></note>
  <backup><duration>8</duration></backup>
  <note><pitch><step>C</step><octave>3</octave></pitch><duration>8</duration><voice>2</voice></note></measure>
 <measure number="3">
  <note><pitch><step>G</step><alter>1</alter><octave>4</octave></pitch><duration>2</duration><tie type="stop"/><voice>1</voice></note>
  <note><pitch><step>A</step><octave>4</octave></pitch><duration>6</duration><voice>1</voice>
   <lyric number="1"><syllabic>end</syllabic><text>tar</text></lyric></note></measure>
</part>
<part id="P2"><measure number="1"><note><pitch><step>C</step><octave>5</octave></pitch><duration>2</duration></note></measure></part>
</score-partwise>'''


class CorpusTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = tempfile.TemporaryDirectory()
        cls.manifest = make_corpus.create(Path(cls.folder.name))

    @classmethod
    def tearDownClass(cls):
        cls.folder.cleanup()

    def test_required_cases_exist(self):
        required = {'reta_44_blocos', 'swing', 'tercinas_colcheia', 'tercinas_parciais', 'tercinas_seminima', 'voz_interna',
                    'compasso_34_sem_meta', 'compasso_68', 'anacruse', 'troca_programa', 'modulacao_meio_tom_meta',
                    'modulacao_meio_tom_sem_meta', 'dominantes_secundarias', 'acordes_sem_quinta', 'baixo_invertido',
                    'pedal_baixo', 'acordes_estendidos', 'karaoke_silabas', 'karaoke_palavras', 'fala_introducao',
                    'letra_deslocada', 'duas_silabas_mesma_nota'}
        self.assertLessEqual(required, set(self.manifest['cases']))

    def test_corpus_is_deterministic(self):
        with tempfile.TemporaryDirectory() as other:
            again = make_corpus.create(Path(other))
        self.assertEqual(self.manifest, again)

    def test_truth_matches_the_midi(self):
        for case in self.manifest['cases']:
            truth = json.loads((Path(self.folder.name) / (case + '.json')).read_text(encoding='utf-8'))
            parsed = parse_midi(Path(self.folder.name) / truth['midi'])
            source = truth['melody_sources'][0]
            melody = [n for n in parsed['notes'] if n['track'] == source['track'] and n['channel'] == source['channel']]
            onsets = {round(n['start'] / parsed['ppq'], 4) for n in melody}
            expected = {round(n['onset'], 4) for n in truth['melody']}
            if case == 'swing':
                continue  # tocado com swing, escrito reto
            self.assertLessEqual(expected, onsets, case)
            for note in truth['melody']:
                self.assertEqual(note['pitch'], 12 * (note['octave'] + 1) + metrics.STEPS[note['step']] + note['alter'], case)
            self.assertGreaterEqual(len(truth['melody']), 20, case)

    def test_perfect_output_scores_one(self):
        for case in self.manifest['cases']:
            truth = json.loads((Path(self.folder.name) / (case + '.json')).read_text(encoding='utf-8'))
            values, _ = metrics.evaluate(truth, perfect_score(truth), 0.0)
            for name, value in values.items():
                if value is None:
                    continue
                self.assertEqual(value, 0.0 if name in metrics.LOWER_IS_BETTER else 1.0, (case, name))

    def test_failed_engine_scores_worst(self):
        truth = json.loads((Path(self.folder.name) / 'karaoke_silabas.json').read_text(encoding='utf-8'))
        values, details = metrics.evaluate(truth, None)
        self.assertTrue(details['failed'])
        self.assertEqual(values['onset_f1'], 0.0)
        self.assertEqual(values['lyrics_dropped'], 1.0)
        self.assertIsNone(values['key_changes'])


class ReaderTests(unittest.TestCase):
    def test_reader_tolerates_new_fields_voices_ties_and_pickup(self):
        score = metrics.read_musicxml(XML)
        self.assertEqual([m['length'] for m in score['measures']], [1.0, 4.0, 4.0])
        melody = [(n['onset'], n['pitch'], n['duration']) for n in score['notes']]
        # G4 (anacruse), G#4 ligada por cima da barra (4+1 tempos), A4; acorde E4, voz 2 grave e pauta auxiliar fora.
        self.assertEqual(melody, [(0.0, 67, 1.0), (1.0, 68, 5.0), (6.0, 69, 3.0)])
        self.assertEqual(score['notes'][0]['lyrics'][0]['text'], 'que eu')
        self.assertEqual([(k['quarter'], k['fifths']) for k in score['keys']], [(0.0, 0), (1.0, -5)])
        self.assertEqual([(h['quarter'], h['pc'], h['quality'], h['bass']['pc'] if h['bass'] else None) for h in score['harmonies']],
                         [(1.0, 7, '7b9', 11), (3.0, 1, '69', None)])

    def test_quality_aliases(self):
        self.assertEqual(metrics.canonical_quality('7M', 'major-seventh'), 'maj7')
        self.assertEqual(metrics.canonical_quality('7(#9)', 'dominant'), '7#9')
        self.assertEqual(metrics.canonical_quality(None, 'dominant', [(9, -1, 'add')]), '7b9')
        self.assertEqual(metrics.canonical_quality('', 'major'), '')
        self.assertEqual(metrics.canonical_quality(None, 'minor-11th'), 'm11')

    def test_lyrics_count_word_and_syllable_placement(self):
        truth = {'lyrics': [{'onset': 0, 'text': 'can', 'word_start': True, 'spoken': False},
                            {'onset': 1, 'text': 'tar', 'word_start': False, 'spoken': False}]}
        whole_word = [{'onset': 0, 'lyrics': [{'text': 'cantar', 'syllabic': 'single'}]}, {'onset': 1, 'lyrics': []}]
        result = metrics.lyric_metrics(truth['lyrics'], whole_word, 0)
        self.assertEqual((result['lyrics_syllables'], result['lyrics_words'], result['lyrics_dropped']), (0.0, 1.0, 1.0))


class CompareTests(unittest.TestCase):
    def test_regressions_and_improvements_respect_direction_and_tolerance(self):
        baseline = {'cases': {'a': {'metrics': {'onset_f1': .9, 'lyrics_dropped': .1, 'key': 1.0}}},
                    'averages': {'onset_f1': .9}}
        current = {'cases': {'a': {'metrics': {'onset_f1': .89, 'lyrics_dropped': .3, 'key': None, 'pitch': 1.0}}},
                   'averages': {'onset_f1': .95}}
        regressions, improvements = metrics.compare(baseline, current, .02)
        self.assertEqual({(r['scope'], r['metric']) for r in regressions}, {('a', 'lyrics_dropped'), ('a', 'key')})
        self.assertEqual({(r['scope'], r['metric']) for r in improvements}, {('a', 'pitch'), ('media', 'onset_f1')})

    def test_missing_case_is_a_regression(self):
        regressions, _ = metrics.compare({'cases': {'a': {'metrics': {}}}, 'averages': {}}, {'cases': {}, 'averages': {}})
        self.assertEqual(regressions[0]['scope'], 'a')


class QuantizationCriterionTests(unittest.TestCase):
    def test_metric_criterion_replaces_hash_equality(self):
        sys.path.insert(0, str(HERE.parent))
        import quantization_regression as regression
        worse, better = regression.compare_metrics({'attack_recall': 1.0, 'collisions': 0, 'onset_error_mean': .03},
                                                   {'attack_recall': .99, 'collisions': 0, 'onset_error_mean': .02})
        self.assertEqual([w['metric'] for w in worse], ['attack_recall'])
        self.assertEqual([b['metric'] for b in better], ['onset_error_mean'])
        # Dois ataques não podem dividir a mesma nota escrita.
        self.assertEqual(regression._matched([1.0, 1.1], [1.05]), 1)


if __name__ == '__main__':
    unittest.main()
