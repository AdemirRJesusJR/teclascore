"""Bounded recovery for distinct piano attacks that nearest-grid rounding merges."""
from pathlib import Path
import os, sys, tempfile, unittest
import xml.etree.ElementTree as E

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreAttackTests'))
import batch_score as engine

BINARY = (4, 8, 16)
TERNARY = (3, 6, 12)


def fixture(quarters, ppq=960):
    notes = [dict(start=round(q*ppq), end=round((q+.2)*ppq), duration=round(.2*ppq),
                  pitch=[60,64,67][index%3], track=5, channel=4, program=0, velocity=80)
             for index,q in enumerate(quarters)]
    parsed = dict(ppq=ppq, notes=notes, events=[], end_tick=max(n['end'] for n in notes))
    return parsed, dict(melody_sources=[dict(track=5,channel=4,program=0)])


class DistinctAttackTests(unittest.TestCase):
    def test_boundary_tie_preserves_both_attacks_with_bounded_error(self):
        phases = {12:[.4166666667,.96875], 13:[2/96,.5], 20:[.13]}
        grids = {12:4,13:4,20:4}
        positions,refinements,adjustments = engine._separate_quantized_attacks(
            phases,grids,{beat:BINARY for beat in grids},0,22*960)
        self.assertEqual(positions[12.96875],12*960+900)
        self.assertEqual(positions[13+2/96],13*960)
        self.assertEqual(positions[20.13],20*960+240)  # Unrelated coarse rounding stays unchanged.
        self.assertEqual(grids[20],4)
        self.assertEqual(len(positions),5)
        self.assertEqual(len(set(positions.values())),5)
        self.assertTrue(all(abs(item['adjustment_quarters']) <= 1/16+1e-9 for item in adjustments))

    def test_internal_three_attack_cluster_keeps_order_and_all_attacks(self):
        phases = {0:[.49,.5,.51]};grids = {0:16}
        positions,_,_ = engine._separate_quantized_attacks(phases,grids,{0:BINARY},0,960)
        self.assertEqual(list(positions.values()),[420,480,540])

    def test_dense_four_attack_cluster_still_requires_review(self):
        phases = {0:[.49,.5,.51,.52]};grids = {0:16}
        with self.assertRaisesRegex(engine.NeedsReview,'revisão rítmica individual') as failure:
            engine._separate_quantized_attacks(phases,grids,{0:BINARY},0,960)
        self.assertEqual(failure.exception.quarters,[.49,.5,.51,.52])
        self.assertEqual(grids,{0:16})  # Failure cannot partially alter the original grids.

    def test_fast_run_refines_only_failed_beat_without_losing_any_pitch(self):
        # Synthetic performance: consecutive descending attacks faster than
        # the old 64th-note grid, with ordinary beats on either side.
        run=[110.48,110+255/480,110+281/480,110+307/480,110+331/480,
             110+351/480,110+373/480,110+393/480,110+418/480]
        quarters=list(range(90,110))+run+[112,113]
        parsed,config=fixture(quarters,480)
        config.update(merge_mode='priority',melody_sources=config['melody_sources']+[dict(track=6,channel=4,program=0)])
        for note,pitch in zip(parsed['notes'][20:],[69,68,66,64,63,61,59,57,56]):note['pitch']=pitch
        events,grids,report=engine.melody_events(parsed,config,88*960,116*960)
        self.assertEqual([event['pitch'] for event in events],[note['pitch'] for note in parsed['notes']])
        self.assertEqual(len(events),len(quarters))
        self.assertEqual(report['quantized_attack_collisions'],0)
        self.assertEqual(report['boundary_grid_refinements'],[{'beat_original':110,'from':16,'to':32}])
        self.assertEqual({beat+88 for beat,grid in grids.items() if grid==32},{110})
        for original,event in zip(quarters[20:29],events[20:29]):
            self.assertLessEqual(abs(event['start']/960+88-original),1/32+1e-9)
        for event in events:
            self.assertGreater(event['end'],event['start'])
            self.assertEqual(sum(piece[1] for piece in engine.pieces(event['start'],event['end'],grids)),event['end']-event['start'])
        measure=E.Element('measure')
        engine.emit_note(measure,0,30,False,dict(start=0,end=30,pitch=60),engine.spellings(0))
        self.assertEqual(measure.findtext('note/type'),'128th')

    def test_finer_triplet_grid_preserves_family_and_emits_supported_duration(self):
        phases={0:[.23,.28,.33,.38,.43,.48],3:[.13]};grids={0:12,3:4}
        positions,refinements,_=engine._separate_quantized_attacks(phases,grids,{0:TERNARY,3:BINARY},0,4*960)
        self.assertEqual(grids,{0:24,3:4})
        self.assertEqual(len(positions),7)
        self.assertEqual(list(positions.values()),sorted(set(positions.values())))
        self.assertEqual(positions[3.13],3*960+240)
        measure=E.Element('measure')
        engine.emit_note(measure,0,40,True,dict(start=0,end=40,pitch=60),engine.spellings(0))
        self.assertEqual(measure.findtext('note/type'),'64th')
        self.assertEqual(measure.findtext('note/time-modification/actual-notes'),'3')

    def test_ternary_and_mixed_grids_keep_their_note_families(self):
        for left in (TERNARY,BINARY):
            phases = {0:[.97],1:[.03,1/3,2/3]};grids = {0:left[0],1:3}
            positions,_,adjustments = engine._separate_quantized_attacks(
                phases,grids,{0:left,1:TERNARY},0,2*960)
            values=list(positions.values())
            self.assertEqual(values,sorted(set(values)))
            self.assertEqual(grids,{0:left[-1],1:12})
            self.assertTrue(all(abs(item['adjustment_quarters']) <= 1/item['grid']+1e-9
                                for item in adjustments))
            for start,end in zip(values,values[1:]+[1920]):
                pieces=list(engine.pieces(start,end,grids))
                self.assertEqual(sum(piece[1] for piece in pieces),end-start)

    def test_score_edges_do_not_gain_out_of_bounds_notes(self):
        phases = {0:[.001,.02,.98],1:[.02]};grids = {0:16,1:16}
        positions,_,_ = engine._separate_quantized_attacks(phases,grids,{0:BINARY,1:BINARY},0,1920)
        self.assertTrue(all(0<=position<1920 for position in positions.values()))
        self.assertEqual(list(positions.values()),sorted(set(positions.values())))

    def test_no_collision_leaves_existing_grids_and_positions_untouched(self):
        grids={0:4,1:3};snapshot=dict(grids)
        self.assertEqual(engine._separate_quantized_attacks(
            {0:[.13,.49],1:[.01,1/3,2/3]},grids,{0:BINARY,1:TERNARY},0,1920),({},[],[]))
        self.assertEqual(grids,snapshot)

    def test_pipeline_keeps_pitches_order_and_duration_before_next_attack(self):
        quarters=list(range(4,12))+[12+40/96,12+93/96,13+2/96,13.5]+list(range(14,27))
        parsed,config=fixture(quarters,96)
        events,grids,report=engine.melody_events(parsed,config,4*960,28*960)
        self.assertEqual(len(events),len(quarters))
        self.assertEqual([n['pitch'] for n in events],[n['pitch'] for n in parsed['notes']])
        self.assertEqual(report['quantized_attack_collisions'],0)
        self.assertTrue(report['attack_position_adjustments'])
        for index,note in enumerate(events):
            self.assertGreater(note['end'],note['start'])
            self.assertLessEqual(note['end'],events[index+1]['start'] if index+1<len(events) else 24*960)
        points,ready,message=engine.review_points(parsed,config,dict(events=[]))
        self.assertTrue(ready,message)
        adjusted=[point for point in points if 'originalQuarter' in point]
        self.assertTrue(adjusted)
        for point in adjusted:
            self.assertAlmostEqual(point['quantizedQuarter']-point['originalQuarter'],point['adjustmentQuarters'])
            self.assertFalse(point['blocking'])

    def test_excess_internal_collisions_also_recover_without_losing_attacks(self):
        quarters=[beat+phase for beat in range(12) for phase in (.22,.27)]
        parsed,config=fixture(quarters)
        events,_,report=engine.melody_events(parsed,config,0,12*960)
        self.assertEqual(len(events),len(quarters))
        self.assertEqual(report['quantized_attack_collisions'],0)

    def test_reviewed_tolerated_internal_collisions_keep_previous_behavior(self):
        quarters=[.22,.27]+list(range(1,25))
        parsed,config=fixture(quarters)
        events,_,report=engine.melody_events(parsed,config,0,26*960)
        self.assertEqual(len(events),len(quarters)-1)
        self.assertEqual(report['quantized_attack_collisions'],1)
        self.assertNotIn('attack_position_adjustments',report)


if __name__ == '__main__':
    unittest.main(verbosity=2)
