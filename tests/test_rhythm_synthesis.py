"""Síntese rítmica e notação única (score_synthesis.py, 1.8.0): cada regra age só na escrita.

Nenhuma regra muda um ataque, uma altura ou a duração total da partitura; os testes de invariância comparam a
geração com todas as regras desligadas e ligadas. O teste nativo (TECLAS_TEST_NATIVE_LABELS=1) grava a partitura no
MuseScore e confere os sinais % e as marcas de andamento no .mscz reaberto.
"""
from pathlib import Path
import hashlib, io, json, os, struct, sys, tempfile, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
import xml.etree.ElementTree as E

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreSynthesisTests'))
import batch_score as engine
import melody_spelling
import score_musicxml
import score_quality
import score_synthesis
from test_priority_melody import A, PPQ, note, parsed
from test_instrument_labels import harmony

DIV = engine.DIV
BAR = 4 * DIV
ALL_RULES = {'clean_durations': True, 'rhythm_grid': True, 'duration_synthesis': True, 'phrase_endings': True,
             'unified_tempo': True, 'repeat_synthesis': True, 'unified_spelling': True}


def bars(count, meter=(4, 4)):
    length = round(meter[0] * 4 / meter[1] * DIV)
    return [{'start': i * length, 'end': (i + 1) * length, 'meter': meter, 'partial': False} for i in range(count)]


def song(bars_of_notes):
    """Melodia sintética: por compasso, uma lista de (tempo de início, duração, altura) em semínimas."""
    result = []
    for index, items in enumerate(bars_of_notes):
        for start, duration, pitch in items:
            result.append(note(index * 4 + start, pitch, A, duration))
    return result


class GridTests(unittest.TestCase):
    def test_eighth_song_with_one_sixteenth_beat(self):
        starts = [b * DIV + o for b in range(16) for o in (0, DIV // 2)] + [20 * DIV + DIV // 4]
        grid = score_synthesis.dominant_grid(starts, {}, 0, lambda g: g % 3 == 0)
        self.assertEqual((grid['dominant'], grid['unit']), ('eighth', DIV // 2))
        self.assertEqual(grid['exception_beats'], [20])
        self.assertEqual(grid['exceptions'], [{'beat': 20, 'class': 'sixteenth'}])

    def test_sixteenth_and_triplet_songs(self):
        starts = [b * DIV + o for b in range(16) for o in (0, DIV // 4, DIV // 2)]
        self.assertEqual(score_synthesis.dominant_grid(starts, {}, 0, lambda g: False)['dominant'], 'sixteenth')
        starts = [b * DIV + o for b in range(16) for o in (0, DIV // 3, 2 * DIV // 3)] + [20 * DIV + DIV // 2]
        grid = score_synthesis.dominant_grid(starts, {b: 3 for b in range(16)}, 0, lambda g: g % 3 == 0)
        self.assertEqual((grid['dominant'], grid['unit']), ('triplet', DIV // 3))
        self.assertEqual(grid['exception_beats'], [20])

    def test_quarter_song_has_no_exceptions(self):
        grid = score_synthesis.dominant_grid([b * DIV for b in range(16)], {}, 0, lambda g: False)
        self.assertEqual((grid['dominant'], grid['exception_beats']), ('quarter', []))

    def test_end_snaps_to_the_grid_but_never_past_the_next_attack(self):
        self.assertEqual(score_synthesis.snap_end(0, 720, DIV, 8 * DIV, DIV // 2), (DIV, 1))      # empate: a mais longa
        self.assertEqual(score_synthesis.snap_end(0, 600, DIV, 8 * DIV, DIV // 2), (DIV // 2, 1))
        self.assertEqual(score_synthesis.snap_end(0, 720, 720, 8 * DIV, DIV // 2), (720, 0))      # legato fica
        self.assertEqual(score_synthesis.snap_end(0, 720, 800, 8 * DIV, DIV // 2), (DIV // 2, 1))  # não passa do ataque
        self.assertEqual(score_synthesis.snap_end(0, 100, 200, 8 * DIV, DIV // 2), (100, 0))      # não encosta no início


class PhraseEndingTests(unittest.TestCase):
    def test_chained_rests_to_the_beat_end_are_filled(self):
        self.assertEqual(score_synthesis.phrase_end(0, DIV // 4, DIV, 0, BAR, DIV, 8 * BAR), (DIV, 1))
        self.assertEqual(score_synthesis.phrase_end(0, DIV // 4, 2 * DIV, 0, BAR, DIV, 8 * BAR), (DIV, 1))

    def test_whole_eighth_rest_stays(self):
        self.assertEqual(score_synthesis.phrase_end(0, DIV // 2, 2 * DIV, 0, BAR, DIV, 8 * BAR), (DIV // 2, 0))

    def test_note_before_the_barline_grows_to_it(self):
        start = 3 * DIV
        self.assertEqual(score_synthesis.phrase_end(start, start + DIV // 4, BAR + DIV, 0, BAR, DIV, 8 * BAR), (BAR, 1))

    def test_next_attack_inside_the_beat_is_never_crossed(self):
        self.assertEqual(score_synthesis.phrase_end(0, DIV // 4, DIV // 2, 0, BAR, DIV, 8 * BAR), (DIV // 4, 0))

    def test_compound_beat(self):
        beat = DIV * 3 // 2
        self.assertEqual(score_synthesis.phrase_end(0, DIV // 4, beat, 0, 2 * beat, beat, 8 * BAR), (beat, 1))
        self.assertEqual(score_synthesis.phrase_end(0, DIV // 2, beat, 0, 2 * beat, beat, 8 * BAR), (DIV // 2, 0))


class TempoTests(unittest.TestCase):
    def test_micro_variations_are_merged_and_real_changes_kept(self):
        tempos = [{'tick': t * 960, 'bpm': b} for t, b in enumerate((119.8, 120.2, 118.4, 110, 105, 104.6, 100))]
        kept, report = score_synthesis.unify_tempos(tempos)
        self.assertEqual([t['bpm'] for t in kept], [120, 110, 105, 100])
        self.assertEqual(report, {'tempo_marks_merged': 3, 'tempo_marks_rounded': 1, 'tempo_marks_written': 4})

    def test_rallentando_in_small_steps_survives(self):
        tempos = [{'tick': t * 960, 'bpm': 120 - t} for t in range(13)]
        kept, _ = score_synthesis.unify_tempos(tempos)
        self.assertEqual([t['bpm'] for t in kept], [120, 117, 114, 111, 108])

    def test_repeated_time_signatures_on_a_barline_are_removed(self):
        meters = [{'tick': 0, 'numerator': 4, 'denominator': 4}, {'tick': 4 * PPQ * 4, 'numerator': 4, 'denominator': 4},
                  {'tick': 9 * PPQ * 4 + 2 * PPQ, 'numerator': 4, 'denominator': 4}, {'tick': 12 * PPQ * 4, 'numerator': 3, 'denominator': 4}]
        kept, removed = score_synthesis.dedupe_meters(meters, PPQ)
        self.assertEqual(removed, 1)
        self.assertEqual([m['tick'] for m in kept], [0, 9 * PPQ * 4 + 2 * PPQ, 12 * PPQ * 4])

    def test_make_bars_keeps_whole_bars_with_a_restated_meter(self):
        notes = song([[(0, 1, 60), (1, 1, 62), (2, 1, 64), (3, 1, 65)]] * 8)
        data = parsed(notes)
        data['events'] = [{'kind': 'meta', 'type': 88, 'tick': 0, 'data': bytes([4, 2, 24, 8])},
                          {'kind': 'meta', 'type': 88, 'tick': 4 * PPQ * 4, 'data': bytes([4, 2, 24, 8])}]
        summary, meters, _ = engine.meta(data)
        self.assertEqual(summary['time_signatures_merged'], 1)
        _, _, made = engine.make_bars(data, meters)
        self.assertFalse(any(bar['partial'] for bar in made))


class MeasureRepeatTests(unittest.TestCase):
    def material(self):
        x = [(0, 1, 60), (1, 1, 62), (2, 1, 64), (3, 1, 65)]
        y = [(0, 1, 67), (1, 1, 69), (2, 1, 71), (3, 1, 72)]
        notes = [{'start': n['start'], 'end': n['end'], 'pitch': n['pitch']} for n in song([x, x, x, y, y, [(0, 4, 60)]])]
        chords = [{'tick': 0, 'root': 0, 'quality': ''}]
        return bars(6), notes, chords

    def test_identical_bars_are_marked(self):
        b, notes, chords = self.material()
        self.assertEqual(score_synthesis.measure_repeats(b, notes, chords), [1, 2, 4])
        self.assertTrue(all(b[i].get('measure_repeat') for i in (1, 2, 4)))

    def test_lyrics_chords_sections_and_ties_prevent_the_sign(self):
        b, notes, chords = self.material()
        notes[4]['lyric'] = {'text': 'lua', 'syllabic': 'single'}
        self.assertEqual(score_synthesis.measure_repeats(b, notes, chords), [4])  # nem depois de um compasso cantado
        b, notes, chords = self.material()
        chords.append({'tick': 2 * BAR, 'root': 7, 'quality': '7'})
        self.assertEqual(score_synthesis.measure_repeats(b, notes, chords), [1, 4])
        b, notes, chords = self.material()
        self.assertEqual(score_synthesis.measure_repeats(b, notes, chords, excluded=[4]), [1, 2])
        b, notes, chords = self.material()
        notes[3]['end'] += DIV  # última nota do compasso 1 ligada ao compasso 2
        self.assertEqual(score_synthesis.measure_repeats(b, notes, chords), [2, 4])  # o compasso seguinte ao ligado volta a ser igual ao anterior

    def test_musicxml_carries_the_sign_and_every_note(self):
        notes = song([[(0, 1, 60), (1, 1, 62), (2, 1, 64), (3, 1, 65)]] * 4 + [[(0, 4, 67)]] * 2 + [[(0, 2, 60), (2, 2, 62)]])
        config = {'melody_sources': [A], 'repeat_synthesis': True}
        with tempfile.TemporaryDirectory() as directory, redirect_stdout(io.StringIO()):
            path, report = engine.build_musicxml(parsed(notes), config, harmony(), Path(directory), 'Percent')
            tree = E.parse(path)
        measures = tree.findall('.//measure')
        starts = [m.find('attributes/measure-style/measure-repeat') for m in measures]
        # Quatro compassos iguais ficam x % % % (não um ritornelo de dois); depois, as semibreves e o fim.
        self.assertEqual([None if s is None else s.get('type') for s in starts], [None, 'start', None, None, 'stop', 'start', 'stop'])
        self.assertEqual(report['melody_analysis']['repeat_synthesis']['measure_repeats'], 4)
        self.assertNotIn('repeat_folds', report['melody_analysis'])
        self.assertEqual(len(measures[1].findall('note')), 4)  # as notas continuam escritas sob o sinal
        self.assertEqual(len(report['notes']), len(notes))


class FoldGuardTests(unittest.TestCase):
    """O ritornelo da síntese de repetições nunca apaga versos escritos como texto nem dobra compassos só de pausas."""

    def test_sung_lines_under_the_bars_block_the_fold_and_the_sign(self):
        import score_repeats
        x = [(0, 1, 60), (1, 1, 62), (2, 1, 64), (3, 1, 65)]
        notes = [{'start': n['start'], 'end': n['end'], 'pitch': n['pitch']} for n in song([x, x, x, x, [(0, 4, 67)]])]
        b = bars(5)
        for index, text in ((0, 'Galanteador, bom de papo'), (2, 'Falei que era empresário')):
            b[index]['lyric_texts'] = [{'text': text, 'offset': 0}]
        folds = score_repeats.fold_repeats(b, notes, [{'tick': 0, 'root': 0, 'quality': ''}], {k: 4 for k in range(20)}, [], [], PPQ, 0, DIV)
        self.assertEqual(folds, [])
        self.assertEqual(len(b), 5)
        # Nem o sinal %: um compasso com verso escrito, ou logo depois dele, fica por extenso.
        self.assertEqual(score_synthesis.measure_repeats(b, notes, [{'tick': 0, 'root': 0, 'quality': ''}]), [])

    def test_rest_only_bars_are_written_out(self):
        import score_repeats
        notes = [{'start': 8 * BAR, 'end': 8 * BAR + DIV, 'pitch': 60}, {'start': 9 * BAR, 'end': 9 * BAR + DIV, 'pitch': 62}]
        b = bars(10)
        folds = score_repeats.fold_repeats(b, notes, [{'tick': 0, 'root': 0, 'quality': ''}], {k: 4 for k in range(40)}, [], [], PPQ, 0, DIV)
        self.assertEqual(folds, [])
        self.assertEqual(len(b), 10)


class DurationConventionTests(unittest.TestCase):
    def figures(self, start, end, bar, rest=False, conventions=True):
        return [(s, d) for s, d, _ in score_musicxml.pieces(start, end, {}, bar, rest=rest, conventions=conventions)]

    def test_rests_are_grouped_by_beat_without_syncopation_or_dots(self):
        bar = bars(1)[0]
        self.assertEqual(self.figures(240, 960, bar, rest=True), [(240, 240), (480, 480)])
        self.assertEqual(self.figures(0, 2880, bar, rest=True), [(0, 1920), (1920, 960)])
        self.assertEqual(self.figures(480, 1920, bar, rest=True), [(480, 480), (960, 960)])
        self.assertEqual(self.figures(0, 720, bar, rest=True), [(0, 480), (480, 240)])
        # Sem a opção, a escrita anterior continua igual.
        self.assertEqual(self.figures(0, 2880, bar, rest=True, conventions=False), [(0, 2880)])

    def test_offbeat_dotted_quarter_ending_on_the_half_bar_is_one_figure(self):
        bar = bars(1)[0]
        self.assertEqual(self.figures(480, 1920, bar), [(480, 1440)])
        self.assertEqual(self.figures(480, 1920, bar, conventions=False), [(480, 960), (1440, 480)])
        self.assertEqual(self.figures(480, 2400, bar), [(480, 1440), (1920, 480)])  # atravessa a metade: liga

    def test_compound_meter_shows_the_groups_of_three(self):
        bar = bars(1, (6, 8))[0]
        self.assertEqual(self.figures(0, 1920, bar), [(0, 1440), (1440, 480)])
        self.assertEqual(self.figures(0, 2880, bar), [(0, 2880)])
        self.assertEqual(self.figures(0, 1440, bar, rest=True), [(0, 1440)])
        self.assertEqual(self.figures(0, 2400, bar, rest=True), [(0, 1440), (1440, 960)])


class SpellingUnityTests(unittest.TestCase):
    def test_repeated_pitch_class_keeps_one_spelling_in_the_bar(self):
        notes = [{'start': 0, 'pitch': 70, 'spelling': ('B', -1)}, {'start': DIV, 'pitch': 70, 'spelling': ('A', 1)},
                 {'start': 2 * DIV, 'pitch': 82, 'spelling': ('A', 1)}, {'start': 5 * DIV, 'pitch': 70, 'spelling': ('A', 1)}]
        kinds = {id(notes[0]): 'chord', id(notes[1]): 'free', id(notes[2]): 'chromatic', id(notes[3]): 'chromatic'}
        changed = melody_spelling.unify_spellings(notes, bars(2), kinds, lambda _: (0, False))
        self.assertEqual([n['spelling'] for n in notes[:3]], [('B', -1)] * 3)
        self.assertEqual(notes[3]['spelling'], ('A', 1))  # outro compasso: a condução cromática fica
        self.assertEqual(changed, 2)

    def test_free_notes_follow_the_spelling_the_key_region_uses_most(self):
        notes = [{'start': i * BAR, 'pitch': 70, 'spelling': ('B', -1)} for i in range(3)]
        notes.append({'start': 3 * BAR, 'pitch': 70, 'spelling': ('A', 1)})
        kinds = {id(n): 'chord' for n in notes[:3]}; kinds[id(notes[3])] = 'free'
        self.assertEqual(melody_spelling.unify_spellings(notes, bars(4), kinds, lambda _: (-2, False)), 1)
        self.assertEqual(notes[3]['spelling'], ('B', -1))

    def test_spell_notes_reports_what_decided_each_spelling(self):
        notes = [{'start': i * DIV, 'pitch': p} for i, p in enumerate((68, 60, 61, 62))]
        kinds = {}
        melody_spelling.spell_notes(notes, [dict(tick=0, root=4, quality='7')], lambda _: (0, False),
                                    lambda _: engine.spellings(0), lambda q: engine.CHORD_INTERVALS[q], kinds=kinds)
        self.assertEqual([kinds[id(n)] for n in notes], ['chord', 'chromatic', 'chromatic', 'chord'])


class InvarianceTests(unittest.TestCase):
    """A música escrita com todas as regras é a mesma música: ataques, alturas e duração total não mudam."""

    def material(self):
        x = [(0, .7, 60), (1.5, .3, 62), (2.5, .1, 64), (3, .6, 65)]          # colcheias, finais soltos
        y = [(0, .4, 67), (.75, .2, 69), (1.5, 1.2, 71), (3, .1, 72)]        # uma semicolcheia isolada
        z = [(0, .5, 72), (3, .1, 71), (3.5, .4, 69)]                         # pausa longa a partir do contratempo
        return song([x, x, x, y, x, x, y, z, x, x, y, z, [(0, 3.8, 60)]])

    def parsed_with_tempos(self, notes):
        data = parsed(notes)
        data['events'] = [{'kind': 'meta', 'type': 81, 'tick': t * PPQ * 4, 'data': int(60000000 / b).to_bytes(3, 'big')}
                          for t, b in ((0, 119.8), (1, 120.2), (2, 119.6), (8, 100), (9, 99.7))]
        return data

    def test_melody_events_keep_attacks_and_pitches(self):
        notes = self.material()
        plain, _, plain_report = engine.melody_events(self.parsed_with_tempos(notes), {'melody_sources': [A]}, 0, 13 * BAR)
        full, _, report = engine.melody_events(self.parsed_with_tempos(notes), dict(ALL_RULES, melody_sources=[A]), 0, 13 * BAR)
        self.assertEqual([(n['start'], n['pitch']) for n in plain], [(n['start'], n['pitch']) for n in full])
        for current, following in zip(full, full[1:]):
            self.assertLess(current['start'], current['end'])
            self.assertLessEqual(current['end'], following['start'])
        self.assertEqual(plain_report['rhythm_grid']['dominant'], 'eighth')
        self.assertNotIn('ends_snapped', plain_report['rhythm_grid'])
        self.assertNotIn('phrase_endings', plain_report)
        self.assertGreater(report['rhythm_grid']['ends_snapped'], 0)
        self.assertEqual(report['rhythm_grid']['exception_beats'], [12, 24, 40])  # a semicolcheia dos compassos Y
        # Sem a grade, os finais de frase completam sozinhos a nota antes das pausas miúdas, com os mesmos ataques.
        config = {'melody_sources': [A], 'clean_durations': True, 'phrase_endings': True}
        phrased, _, phrase = engine.melody_events(self.parsed_with_tempos(notes), config, 0, 13 * BAR)
        self.assertGreater(phrase['phrase_endings'], 0)
        self.assertEqual([(n['start'], n['pitch']) for n in plain], [(n['start'], n['pitch']) for n in phrased])

    def test_score_keeps_the_same_music_and_counts_every_rule(self):
        notes = self.material()
        results = {}
        for name, config in (('plain', {'melody_sources': [A]}), ('full', dict(ALL_RULES, melody_sources=[A]))):
            with tempfile.TemporaryDirectory() as directory, redirect_stdout(io.StringIO()):
                path, report = engine.build_musicxml(self.parsed_with_tempos(notes), config, harmony(), Path(directory), name)
                tree = E.parse(path)
            results[name] = (report, tree)
        plain, full = results['plain'][0], results['full'][0]
        analysis = full['melody_analysis']
        # A síntese de repetições dobra o bloco repetido (x x y) num ritornelo: a música escrita mais a repetida
        # soma os mesmos compassos; antes da dobra, cada ataque e cada altura são os mesmos.
        folded = sum(f['removed_quarters'] * DIV for f in analysis['repeat_folds'])
        self.assertEqual(sum(b['end'] - b['start'] for b in plain['bars']), sum(b['end'] - b['start'] for b in full['bars']) + folded)
        self.assertEqual(analysis['repeat_synthesis']['folded_measures'], 3)
        kept = [(n['start'], n['pitch']) for n in plain['notes'] if n['start'] < 4 * BAR]
        self.assertEqual([(n['start'], n['pitch']) for n in full['notes'] if n['start'] < 4 * BAR], kept)
        self.assertEqual(analysis['unified_tempo']['tempo_marks_written'], 2)
        self.assertEqual(analysis['unified_tempo']['tempo_marks_merged'], 3)
        self.assertEqual(analysis['unified_tempo']['opening_bpm'], 120)
        self.assertEqual(analysis['repeat_synthesis']['measure_repeats'], 2)
        self.assertGreater(analysis['duration_synthesis']['rests_regrouped'], 0)
        self.assertIn('unified_spellings', analysis)
        # Sem dobrar repetições, a partitura escrita tem exatamente os mesmos ataques, alturas e compassos.
        with tempfile.TemporaryDirectory() as directory, redirect_stdout(io.StringIO()):
            _, same = engine.build_musicxml(self.parsed_with_tempos(notes), dict(ALL_RULES, melody_sources=[A], repeat_synthesis=False), harmony(), Path(directory), 'same')
        self.assertEqual([(n['start'], n['pitch']) for n in plain['notes']], [(n['start'], n['pitch']) for n in same['notes']])
        self.assertEqual(plain['bars'], same['bars'])
        tempo_marks = results['full'][1].findall('.//metronome/per-minute')
        self.assertEqual([m.text for m in tempo_marks], ['120', '100'])
        self.assertEqual(len(results['plain'][1].findall('.//direction/sound[@tempo]')), 5)
        self.assertEqual(len(results['full'][1].findall('.//direction/sound[@tempo]')), 2)
        # Cada compasso continua com a duração do compasso.
        for measure, bar in zip(results['full'][1].findall('.//measure'), full['bars']):
            total = sum(int(n.findtext('duration', '0')) for n in measure.findall('note') if n.find('chord') is None and n.find('grace') is None)
            self.assertEqual(total, bar['end'] - bar['start'])

    def test_quality_reports_the_counts(self):
        analysis = {'rhythm_grid': {'dominant': 'eighth', 'name': 'colcheias', 'beats_with_attacks': 40, 'counts': {'eighth': 30, 'sixteenth': 6, 'quarter': 4}, 'exception_beats': [1, 2, 3, 4, 5, 6], 'exceptions': [{'quarter': b, 'class': 'sixteenth'} for b in range(1, 7)], 'ends_snapped': 7},
                    'duration_synthesis': {'rests_regrouped': 3, 'notes_regrouped': 1}, 'phrase_endings': 4,
                    'unified_tempo': {'tempo_marks_merged': 12, 'tempo_marks_rounded': 1, 'opening_bpm': 120},
                    'repeat_synthesis': {'measure_repeats': 2, 'folded_measures': 4}, 'unified_spellings': 3}
        melody = [{'start': i * DIV, 'end': (i + 1) * DIV, 'pitch': 60 + i % 7} for i in range(32)]
        result = score_quality.evaluate({'notes': melody, 'bars': bars(8), 'measures': 8, 'pages': 1, 'melody_analysis': analysis})
        self.assertEqual(result['metrics']['synthesis']['grid_exceptions'], 6)
        self.assertTrue(any('fora da grade' in issue for issue in result['issues']))
        self.assertLess(result['score'], 100)
        notes = result['notes']
        self.assertTrue(any('7 finais de nota' in line for line in notes), notes)
        self.assertTrue(any('12 microvariações' in line for line in notes), notes)
        self.assertTrue(any('2 compassos com %' in line for line in notes), notes)
        self.assertTrue(any('3 grafias' in line for line in notes), notes)


def write_midi(path, notes, tempos):
    def vlq(value):
        data = [value & 127]; value >>= 7
        while value: data.insert(0, (value & 127) | 128); value >>= 7
        return bytes(data)
    conductor = []
    for tick, bpm in tempos: conductor.append((tick, b'\xff\x51\x03' + int(60000000 / bpm).to_bytes(3, 'big')))
    conductor.insert(0, (0, b'\xff\x58\x04\x04\x02\x18\x08'))
    track = b''; previous = 0
    for tick, message in sorted(conductor): track += vlq(tick - previous) + message; previous = tick
    track += b'\x00\xff\x2f\x00'
    data = b'MThd' + struct.pack('>IHHH', 6, 1, 2, PPQ) + b'MTrk' + struct.pack('>I', len(track)) + track
    events = [(0, 0, bytes([0xc0, 0]))]
    for n in notes: events += [(n['start'], 1, bytes([0x90, n['pitch'], n['velocity']])), (n['end'], 0, bytes([0x80, n['pitch'], 0]))]
    track = b''; previous = 0
    for tick, _, message in sorted(events): track += vlq(tick - previous) + message; previous = tick
    track += b'\x00\xff\x2f\x00'
    path.write_bytes(data + b'MTrk' + struct.pack('>I', len(track)) + track)


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeSynthesisTests(unittest.TestCase):
    def test_percent_signs_and_tempo_marks_survive_the_musescore_round_trip(self):
        x = [(0, .4, 60), (1, .4, 62), (2, .4, 64), (3, .4, 65)]
        y = [(0, .4, 67), (1, .4, 69), (2, .4, 71), (3, .4, 72)]
        notes = song([x, x, x, y, y, [(0, 3.8, 60)]])
        tempos = [(0, 120), (BAR // DIV * PPQ, 119.8), (2 * 4 * PPQ, 120.3), (4 * 4 * PPQ, 100), (5 * 4 * PPQ, 99.5)]
        with tempfile.TemporaryDirectory() as directory, redirect_stdout(io.StringIO()):
            out = Path(directory); midi = out / 'source.mid'; write_midi(midi, notes, tempos)
            before = hashlib.sha256(midi.read_bytes()).hexdigest()
            data = engine.parse_midi(midi)
            config = dict(ALL_RULES, melody_sources=[A], index=0, filename='source.mid', reserve_high_melody=True)
            with patch.object(engine, 'BATCH', out):
                xml, report = engine.build_musicxml(data, config, harmony(), out, 'sintese')
                final = engine.convert_score(xml, report, out, 'sintese')
            self.assertEqual(hashlib.sha256(midi.read_bytes()).hexdigest(), before)
            self.assertTrue(report['native_reopened'])
            analysis = report['melody_analysis']
            self.assertEqual(analysis['repeat_synthesis']['measure_repeats'], 3)
            self.assertEqual(analysis['unified_tempo']['tempo_marks_written'], 2)
            with zipfile.ZipFile(final) as archive:
                native = E.fromstring(archive.read(next(name for name in archive.namelist() if name.endswith('.mscx'))))
            self.assertEqual(len(native.findall('.//MeasureRepeat')), 3)
            self.assertEqual(len(native.findall('.//Tempo')), 2)
            self.assertEqual(report['melody_attacks_verified'], len(notes))
            self.assertTrue(report['quality']['notes'])


if __name__ == '__main__':
    unittest.main()
