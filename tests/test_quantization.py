"""Source-preserving beat-boundary quantization tests; no MIDI collection required."""
from pathlib import Path
import os, sys, tempfile, unittest

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreQuantizationTests'))
import batch_score as engine

BINARY = (4, 8, 16)
TERNARY = (3, 6, 12)


def positions(bybeat, grids):
    return [round((beat+round(phase*grids[beat])/grids[beat])*engine.DIV)
            for beat in sorted(bybeat) for phase in sorted(bybeat[beat])]


class BoundaryQuantizationTests(unittest.TestCase):
    def test_binary_boundary_reuses_required_neighbour_detail(self):
        phases = {8:[.25,.5,.75,356/384], 9:[0,.4375,.5], 20:[0,.5]}
        grids = {8:4, 9:16, 20:4}
        result = engine._refine_boundary_grids(phases, grids, {b:BINARY for b in grids}, 4*960, 24*960)
        self.assertEqual(result, [{'beat_original':8,'from':4,'to':16}])
        self.assertEqual(grids, {8:16, 9:16, 20:4})
        self.assertEqual(len(positions(phases, grids)), len(set(positions(phases, grids))))
        self.assertIn(round(8.9375*960), positions(phases, grids))

    def test_ternary_boundary_keeps_triplet_family(self):
        phases = {0:[0,1/3,2/3,.94], 1:[0,1/3,2/3]}
        grids = {0:3,1:3}
        engine._refine_boundary_grids(phases, grids, {0:TERNARY,1:TERNARY}, 0, 3*960)
        self.assertEqual(grids, {0:12,1:3})
        result = positions(phases, grids)
        self.assertEqual(result, sorted(set(result)))
        self.assertEqual(result[:3], [0,320,640])

    def test_mixed_families_are_never_replaced_with_each_other(self):
        phases = {0:[.96],1:[.05,1/3,2/3]}
        grids = {0:4,1:3}
        engine._refine_boundary_grids(phases, grids, {0:BINARY,1:TERNARY}, 0, 3*960)
        self.assertIn(grids[0], BINARY)
        self.assertIn(grids[1], TERNARY)
        result = positions(phases, grids)
        self.assertEqual(result, sorted(set(result)))

    def test_refinement_cannot_create_an_internal_collision(self):
        phases = {0:[.105,.16,.93],1:[0]}
        grids = {0:4,1:4}
        engine._refine_boundary_grids(phases, grids, {0:BINARY,1:BINARY}, 0, 3*960)
        # Eighth subdivisions would collapse .105 and .16; sixteenth subdivisions preserve both.
        self.assertEqual(grids, {0:16,1:4})
        self.assertEqual(len(positions(phases,grids)), 4)
        self.assertEqual(len(set(positions(phases,grids))), 4)

    def test_irresolvable_binary_pair_requires_review_even_below_old_threshold(self):
        phases = {0:[.97],1:[.03]}
        grids = {0:4,1:4}
        with self.assertRaisesRegex(engine.NeedsReview, 'revisão rítmica individual'):
            engine._refine_boundary_grids(phases, grids, {0:BINARY,1:BINARY}, 0, 3*960)

    def test_irresolvable_ternary_pair_requires_review(self):
        phases = {0:[.97],1:[.03]}
        grids = {0:3,1:3}
        with self.assertRaises(engine.NeedsReview):
            engine._refine_boundary_grids(phases, grids, {0:TERNARY,1:TERNARY}, 0, 3*960)

    def test_unaffected_beats_remain_exactly_unchanged(self):
        phases = {0:[0,.25,.5,.75],1:[0,.5]}
        grids = {0:4,1:4}
        result = engine._refine_boundary_grids(phases, grids, {0:BINARY,1:BINARY}, 0, 3*960)
        self.assertEqual(result, [])
        self.assertEqual(grids, {0:4,1:4})

    def test_boundary_outside_score_is_not_changed(self):
        phases = {0:[.97],1:[.03]}
        grids = {0:4,1:4}
        result = engine._refine_boundary_grids(phases, grids, {0:BINARY,1:BINARY}, 0, 960)
        self.assertEqual(result, [])

    def test_melody_pipeline_preserves_attack_sequence_and_origin(self):
        quarters = [4+i*.5 for i in range(6)] + [8.25,8.5,8.75,8+356/384,9,9.4375,9.5] + list(range(10,21))
        notes = [dict(start=round(q*384),end=round((q+.3)*384),pitch=[62,69,71][i%3],
                      track=1,channel=1,program=81,velocity=107) for i,q in enumerate(quarters)]
        parsed = {'ppq':384, 'notes':notes}
        config = {'melody_sources':[{'track':1,'channel':1,'program':81}]}
        events, grids, report = engine.melody_events(parsed,config,4*960,22*960)
        self.assertEqual(len(events),len(notes))
        self.assertEqual([n['pitch'] for n in events],[n['pitch'] for n in notes])
        self.assertEqual([n['start'] for n in events],sorted({n['start'] for n in events}))
        self.assertEqual(events[0]['start'],0)
        self.assertEqual(grids[4],16)  # Original beat8 shifted by origin4.
        self.assertEqual(report['quantized_attack_collisions'],0)
        self.assertEqual(report['boundary_grid_refinements'],[{'beat_original':8,'from':4,'to':16}])


if __name__ == '__main__':
    unittest.main(verbosity=2)
