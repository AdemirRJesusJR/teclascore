"""Approved rules: harmonic doubt is reported, never silent; a MIDI known to lack its melody is flagged."""
from pathlib import Path
import os
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir())/'TeclaScoreBusinessRules'))
import batch_harmony as harmony
import batch_score as score
import worker

PPQ = 480


def parsed_in_four(bars=4):
    """Four 4/4 bars of a plain melody, enough for the bar map used by review points."""
    notes = [dict(start=q*PPQ, end=(q+1)*PPQ, duration=PPQ, pitch=60+(q % 5), track=1, channel=0,
                  program=0, velocity=90) for q in range(bars*4)]
    events = [dict(tick=0, track=0, kind='meta', type=88, data=bytes([4, 2, 24, 8]))]
    return dict(path='regras.mid', sha256='synthetic', ppq=PPQ, notes=notes, events=events,
                end_tick=bars*4*PPQ, tracks=[], format=1)


class DiscardedChordTests(unittest.TestCase):
    def test_only_a_different_chord_is_kept_as_doubt(self):
        events = [dict(tick=0, root=0, quality=''), dict(tick=8*PPQ, root=9, quality='m')]
        discarded = [dict(tick=PPQ, root=0, quality='', confidence=.6),          # repeats C: noise
                     dict(tick=2*PPQ, root=7, quality='', confidence=.62),       # G inside C: doubt
                     dict(tick=3*PPQ, root=7, quality='', confidence=.61),       # same doubt again
                     dict(tick=9*PPQ, root=7, quality='', confidence=.6)]        # G again, under Am
        kept = harmony._discarded_changes(discarded, events)
        self.assertEqual([(d['tick'], d['root']) for d in kept], [(2*PPQ, 7), (9*PPQ, 7)])

    def test_thresholds_leave_room_for_doubtful_chords(self):
        self.assertLess(harmony._MINIMUM_CHORD, harmony.DOUBTFUL_CHORD)


class ReviewPointTests(unittest.TestCase):
    def points(self, events, discarded=()):
        result = dict(events=events, key=dict(fifths=0, minor=False),
                      statistics=dict(uncertain_discarded=list(discarded)))
        config = dict(melody_sources=[dict(track=1, channel=0, program=0)], solo_sources=[])
        points, _, _ = score.review_points(parsed_in_four(), config, result)
        return points

    def test_doubtful_chord_is_flagged_and_edited_chord_is_not(self):
        points = self.points([dict(tick=0, root=0, quality='', confidence=.7),
                              dict(tick=4*PPQ, root=5, quality='', confidence=0),
                              dict(tick=8*PPQ, root=7, quality='', confidence=.93)])
        flagged = [p['measure'] for p in points if p['kind'] == 'chord_low_confidence']
        self.assertEqual(flagged, [1])

    def test_discarded_changes_become_one_point_per_bar(self):
        points = self.points([dict(tick=0, root=0, quality='', confidence=.93)],
                             [dict(tick=PPQ, root=7, quality='', confidence=.62),
                              dict(tick=2*PPQ, root=2, quality='m', confidence=.6),
                              dict(tick=9*PPQ, root=9, quality='m', confidence=.61)])
        discarded = [p for p in points if p['kind'] == 'chord_discarded']
        self.assertEqual([p['measure'] for p in discarded], [1, 3])
        self.assertIn('G', discarded[0]['message'])
        self.assertTrue(all(not p['blocking'] for p in discarded))

    def test_song_start_point_lands_on_the_first_bar(self):
        point = score.song_start_review_point(parsed_in_four(), 'source_melody_incomplete', 'aviso')
        self.assertEqual((point['measure'], point['kind'], point['blocking']), (1, 'source_melody_incomplete', False))


class SourceDiagnosisTests(unittest.TestCase):
    def test_missing_and_partial_melodies_are_explained(self):
        missing = worker.source_diagnosis({'config': {'status': 'no_melody', 'reason': 'Só bases e arpejos'}})
        partial = worker.source_diagnosis({'config': {'status': 'partial_melody', 'reason': 'Faltam dois refrões.'}})
        self.assertEqual(missing['status'], 'no_melody')
        self.assertIn('Só bases e arpejos.', missing['message'])
        self.assertIn('Só cifras', missing['message'])
        self.assertIn('Faltam dois refrões.', partial['message'])
        self.assertNotIn('..', partial['message'])

    def test_ready_or_unknown_midis_have_no_warning(self):
        self.assertIsNone(worker.source_diagnosis({'config': {'status': 'ready'}}))
        self.assertIsNone(worker.source_diagnosis(None))

    def test_every_non_ready_profile_has_a_message(self):
        import json
        profiles = json.loads((ROOT/'engine'/'profiles.json').read_text(encoding='utf8'))
        statuses = {p['config'].get('status') for p in profiles.values()} - {'ready'}
        self.assertTrue(statuses <= set(worker.SOURCE_DIAGNOSES), statuses)


if __name__ == '__main__':
    unittest.main()
