"""Conservative chord context: all chosen notes must already be evidenced."""
from pathlib import Path
import copy
import json
import os
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir())/'TeclaScoreHarmonyContext'))
import batch_harmony as harmony
import batch_score as score

PPQ = 480


def note(quarter, pitch, duration=1, track=1, channel=1, program=24, velocity=90):
    start = round(quarter*PPQ); end = round((quarter+duration)*PPQ)
    return dict(start=start, end=end, duration=end-start, pitch=pitch,
                track=track, channel=channel, program=program, velocity=velocity)


def fixture(blocks=(), bass=(), extra=(), key=None):
    notes = [note(q, pitch, duration) for q, pitches, duration in blocks for pitch in pitches]
    notes += [note(q, pitch, duration, track=2, channel=2, program=33) for q, pitch, duration in bass]
    notes += list(extra)
    events = [dict(tick=0, track=0, kind='meta', type=88, data=bytes([4, 2, 24, 8]))]
    if key is not None:
        events.append(dict(tick=0, track=0, kind='meta', type=89, data=bytes([key[0] % 256, key[1]])))
    return dict(path='harmony.mid', sha256='synthetic', ppq=PPQ, notes=notes, events=events,
                end_tick=max((n['end'] for n in notes), default=0), tracks=[], format=1)


def contextual(parsed, selections=None):
    return harmony.analyze_harmony(parsed, selections, context_advice=True)


class HarmonyContextTests(unittest.TestCase):
    def assert_supported(self, result):
        for event in result['events']:
            evidence = event['evidence']
            actual = set(evidence['observed_pitch_classes'])
            if evidence['includes_bass_pitch']:
                actual.add(evidence['bass_pc'])
            self.assertLessEqual(set(event['pitch_classes']), actual)
            self.assertEqual(event['pitch_classes'], harmony.chord_pitch_classes(event['root'], event['quality']))
            for alternative in evidence.get('alternatives', []):
                self.assertEqual(harmony.chord_pitch_classes(alternative['root'], alternative['quality']),
                                 event['pitch_classes'])

    def test_opt_in_and_input_immutable(self):
        parsed = fixture([(0, [60, 64, 67, 69], 2), (4, [60, 64, 67], 2)])
        before = copy.deepcopy(parsed)
        legacy = harmony.analyze_harmony(parsed)
        self.assertEqual(legacy, harmony.analyze_harmony(parsed, context_advice=False))
        self.assertNotIn('context_advice', legacy)
        self.assertNotIn('diagnostics', legacy)
        result = contextual(parsed)
        self.assertTrue(result['context_advice'])
        self.assertEqual(parsed, before)
        json.dumps(result)
        self.assert_supported(result)

    def test_c6_am7_equivalence_remains_visible_after_bass_disambiguation(self):
        for bass_pitch, expected in ((36, (0, '6')), (45, (9, 'm7'))):
            with self.subTest(bass=bass_pitch):
                parsed = fixture([(0, [60, 64, 67, 69], 2), (4, [60, 64, 67, 69], 2)],
                                 [(0, bass_pitch, 2), (4, bass_pitch, 2)], key=(0, 0))
                result = contextual(parsed)
                self.assertEqual((result['events'][0]['root'], result['events'][0]['quality']), expected)
                self.assertIn('baixo tocado apoia', result['events'][0]['evidence']['recommendation'])
                self.assertEqual(len(result['events'][0]['evidence']['alternatives']), 1)
                self.assert_supported(result)

    def test_same_tones_tie_breaks_with_key_without_confidence_inflation(self):
        choice = harmony._choose_candidate({0, 4, 7, 9}, {0: 1, 4: 1, 7: 1, 9: 1}, None, context_advice=True)
        self.assertEqual((choice['root'], choice['quality']), (9, 'm7'))
        event = {field: choice[field] for field in ('root', 'quality', 'pitch_classes', 'confidence')}
        event['tick'] = 0
        event['evidence'] = dict(ambiguous=True, method='simultaneous_onset', bass_pc=None,
                                context_candidates=choice['context_candidates'])
        confidence = event['confidence']
        harmony._contextualize_events([event], dict(root=0, fifths=0, minor=False, confidence=.94), PPQ)
        self.assertEqual((event['root'], event['quality']), (0, '6'))
        self.assertEqual(event['confidence'], confidence)
        self.assertTrue(event['evidence']['ambiguous'])
        self.assertTrue(event['evidence']['context_changed_name'])
        self.assertEqual(event['evidence']['alternatives'], [dict(root=9, quality='m7')])

    def test_weak_or_conflicting_key_cannot_rename_a_chord(self):
        for key in (dict(root=0, minor=False, confidence=.4),
                    dict(root=0, minor=False, confidence=.94, midi_key_signature_conflict={'fifths': 3})):
            choice = harmony._choose_candidate({0, 4, 7, 9}, {0: 1, 4: 1, 7: 1, 9: 1}, None, context_advice=True)
            event = {field: choice[field] for field in ('root', 'quality', 'pitch_classes', 'confidence')}
            event.update(tick=0, evidence=dict(ambiguous=True, method='simultaneous_onset', bass_pc=None,
                                              context_candidates=choice['context_candidates']))
            harmony._contextualize_events([event], key, PPQ)
            self.assertEqual((event['root'], event['quality']), (9, 'm7'))

    def test_clear_neighbor_can_break_a_tie_but_guesses_do_not_propagate(self):
        events = []
        for quarter, bass in ((0, 0), (4, None), (12, None)):
            choice = harmony._choose_candidate({0, 4, 7, 9}, {0: 1, 4: 1, 7: 1, 9: 1}, bass, context_advice=True)
            event = {field: choice[field] for field in ('root', 'quality', 'pitch_classes', 'confidence')}
            event.update(tick=quarter*PPQ, evidence=dict(ambiguous=choice['ambiguous'], method='simultaneous_onset',
                bass_pc=bass, context_candidates=choice['context_candidates']))
            events.append(event)
        harmony._contextualize_events(events, dict(root=0, minor=False, confidence=.4), PPQ)
        self.assertEqual([(e['root'], e['quality']) for e in events], [(0, '6'), (0, '6'), (9, 'm7')])
        self.assertIn('acorde vizinho', events[1]['evidence']['recommendation'])

    def test_inverted_triad_is_not_renamed_as_bass_root(self):
        result = contextual(fixture([(0, [64, 67, 72], 2), (4, [67, 72, 76], 2)],
                                    [(0, 40, 2), (4, 43, 2)]))
        self.assertEqual([(event['root'], event['quality']) for event in result['events']], [(0, '')])
        self.assertIn('inversão foi preservada', result['events'][0]['evidence']['recommendation'])
        self.assert_supported(result)

    def test_explicit_chromatic_changes_remain_despite_key_signature(self):
        parsed = fixture([(0, [60, 64, 67], 3), (4, [61, 65, 68], 1),
                          (5, [60, 64, 67], 3), (8, [60, 64, 67], 4)], key=(0, 0))
        result = contextual(parsed)
        self.assertIn((4*PPQ, 1, ''), [(e['tick'], e['root'], e['quality']) for e in result['events']])
        self.assert_supported(result)

    def test_short_ambiguous_chromatic_block_is_preserved_instead_of_smoothed_away(self):
        parsed = fixture([(0, [60, 64, 67], .5), (.75, [61, 65, 69], .2),
                          (1, [60, 64, 67], 3), (4, [60, 64, 67], 4)], key=(0, 0))
        legacy = harmony.analyze_harmony(parsed)
        result = contextual(parsed)
        self.assertFalse(any(e['tick'] == .75*PPQ for e in legacy['events']))
        chromatic = next(e for e in result['events'] if e['tick'] == .75*PPQ)
        self.assertEqual(chromatic['pitch_classes'], [1, 5, 9])
        self.assertTrue(chromatic['evidence']['ambiguous'])
        self.assert_supported(result)

    def test_missing_chord_tones_are_never_invented_from_key_or_bass(self):
        parsed = fixture([(0, [60, 67], 2), (4, [60, 67], 2)], [(0, 36, 2), (4, 36, 2)], key=(0, 0))
        result = contextual(parsed)
        self.assertEqual(result['events'], [])
        self.assertIn('harmony_missing', [d['kind'] for d in result['diagnostics']])

    def test_monophonic_melody_and_drums_cannot_generate_chords(self):
        mono = [note(i*.5, [60, 64, 67, 69][i % 4], .4, track=3, channel=3, program=73)
                for i in range(40)]
        drums = [note(0, pitch, 20, track=9, channel=10) for pitch in [60, 64, 67]]
        for selections in (None, [dict(track=3, channel=3, program=73)]):
            result = contextual(fixture(extra=mono+drums), selections)
            self.assertEqual(result['events'], [])
            self.assertEqual(result['statistics']['pitched_notes'], len(mono))
            self.assertIn('harmony_missing', [d['kind'] for d in result['diagnostics']])

    def test_sustained_arpeggios_and_changes_use_played_notes(self):
        arpeggio = []
        for quarter, pitches in ((0, [60, 64, 67]), (4, [65, 69, 72]),
                                 (8, [67, 71, 74]), (12, [60, 64, 67])):
            arpeggio += [note(quarter+i*.15, pitch, 2-i*.15) for i, pitch in enumerate(pitches)]
        result = contextual(fixture(extra=arpeggio))
        self.assertEqual([event['root'] for event in result['events']], [0, 5, 7, 0])
        self.assertTrue(all(e['evidence']['method'] == 'sustained_polyphony' for e in result['events']))
        self.assert_supported(result)

    def test_substantial_noise_is_rejected_not_rounded_to_a_familiar_chord(self):
        parsed = fixture([(0, [60, 61, 64, 67, 68, 70], 2), (4, [60, 61, 64, 67, 68, 70], 2)], key=(0, 0))
        self.assertEqual(contextual(parsed)['events'], [])

    def test_long_sustained_chord_is_support_not_a_false_gap(self):
        result = contextual(fixture([(0, [60, 64, 67], 24), (24, [65, 69, 72], 4)]))
        self.assertEqual(result['statistics']['unsupported_gaps'], [])
        self.assertNotIn('harmony_gap', [d['kind'] for d in result['diagnostics']])

    def test_intentional_silence_between_chords_does_not_request_harmony(self):
        parsed = fixture([(0, [60, 64, 67], 1), (40, [65, 69, 72], 1)])
        self.assertEqual(len(harmony.analyze_harmony(parsed)['statistics']['unsupported_gaps']), 1)
        result = contextual(parsed)
        self.assertEqual(result['statistics']['unsupported_gaps'], [])
        self.assertNotIn('harmony_gap', [d['kind'] for d in result['diagnostics']])

    def test_disagreeing_sources_do_not_claim_an_equivalent_name_without_one(self):
        competing = [note(q, pitch, 2, track=4, channel=4, program=48)
                     for q in (0, 4) for pitch in (67, 71, 74)]
        result = contextual(fixture([(0, [60, 64, 67], 2), (4, [60, 64, 67], 2)], extra=competing))
        evidence = result['events'][0]['evidence']
        self.assertTrue(evidence['source_disagreement'])
        self.assertEqual(evidence['alternatives'], [])
        self.assertNotIn('Mais de um nome', evidence['recommendation'])
        self.assertIn('Fontes de acompanhamento diferentes discordam', evidence['recommendation'])
        ambiguity = next(d for d in result['diagnostics'] if d['kind'] == 'chord_ambiguity')
        self.assertIn('fontes discordam', ambiguity['message'])

    def test_gap_starts_after_support_ends_includes_intro_and_tail(self):
        melody = [note(i, 72+i % 5, .7, track=3, channel=3, program=73) for i in range(64)]
        parsed = fixture([(20, [60, 64, 67], 1), (41, [65, 69, 72], 1)], extra=melody)
        result = contextual(parsed, [dict(track=3, channel=3, program=73)])
        self.assertEqual(result['statistics']['unsupported_gaps'], [
            dict(start_tick=0, end_tick=20*PPQ), dict(start_tick=21*PPQ, end_tick=41*PPQ),
            dict(start_tick=42*PPQ, end_tick=round(63.7*PPQ))])
        self.assertEqual(len([d for d in result['diagnostics'] if d['kind'] == 'harmony_gap']), 3)

    def test_midi_signature_conflict_has_localized_nonblocking_review(self):
        parsed = fixture([(0, [60, 64, 67], 4), (4, [65, 69, 72], 4),
                          (8, [67, 71, 74, 77], 4), (12, [60, 64, 67], 4)], key=(6, 0))
        result = contextual(parsed)
        self.assertIn('midi_key_signature_conflict', result['key'])
        self.assertIn('key_conflict', [d['kind'] for d in result['diagnostics']])
        points, _, _ = score.review_points(parsed, dict(melody_sources=[dict(track=1, channel=1, program=24)]), result)
        conflict = next(p for p in points if p['kind'] == 'key_conflict')
        self.assertEqual((conflict['measure'], conflict['beat']), (1, 1))
        self.assertFalse(conflict['blocking'])

    def test_ambiguity_is_located_using_trimmed_measure_origin(self):
        melody = [note(4+i*.5, 72+i % 5, .4, track=3, channel=3, program=73) for i in range(40)]
        parsed = fixture([(4, [60, 64, 67], 2), (8, [60, 64, 67, 69], 2),
                          (12, [60, 64, 67], 2)], extra=melody)
        result = contextual(parsed, [dict(track=3, channel=3, program=73)])
        points, ready, _ = score.review_points(parsed, dict(melody_sources=[dict(track=3, channel=3, program=73)]), result)
        self.assertTrue(ready)
        ambiguous = next(point for point in points if point['kind'] == 'chord_ambiguity')
        self.assertEqual((ambiguous['quarter'], ambiguous['measure'], ambiguous['beat']), (8, 2, 1))
        self.assertFalse(ambiguous['blocking'])
        self.assertIn('outra leitura', ambiguous['message'])


if __name__ == '__main__':
    unittest.main()
