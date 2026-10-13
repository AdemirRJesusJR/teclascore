"""Output transposition and localized evidence; synthetic sources remain immutable."""
from pathlib import Path
import copy,json,os,sys,tempfile,unittest
import xml.etree.ElementTree as E

sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'engine'))
os.environ.setdefault('PARTITURAS_JOB',str(Path(tempfile.gettempdir())/'TeclaScoreTransposeTests'))
import batch_score as engine

def fixture():
    ppq=480
    notes=[dict(start=round((4+i*.5)*ppq),end=round((4+i*.5+.4)*ppq),duration=round(.4*ppq),
                track=1,channel=1,program=0,pitch=[60,64,67,72][i%4],velocity=100) for i in range(36)]
    parsed=dict(path='synthetic.mid',sha256='synthetic',ppq=ppq,notes=notes,format=1,
                end_tick=notes[-1]['end'],tracks=[],warnings=[],events=[
                    dict(tick=0,track=0,kind='meta',type=88,data=bytes([4,2,24,8])),
                    dict(tick=12*ppq,track=0,kind='meta',type=88,data=bytes([6,3,24,8]))])
    config=dict(melody_sources=[dict(track=1,channel=1,program=0)])
    harmony=dict(key=dict(fifths=0,minor=False,root=0),key_changes=[dict(tick=12*ppq,fifths=-1,minor=True,root=2)],
                 events=[dict(tick=4*ppq,root=0,root_spelling='C',quality='',pitch_classes=[0,4,7],confidence=.9),
                         dict(tick=12*ppq,root=2,root_spelling='D',quality='m7',pitch_classes=[0,2,5,9],confidence=.4)])
    return parsed,config,harmony

class TransposeTests(unittest.TestCase):
    def test_validation(self):
        for invalid in (True,False,1.5,'2',-13,13,[],{}):
            with self.subTest(invalid=invalid),self.assertRaises(ValueError):engine.transpose_semitones(invalid)
        self.assertEqual(engine.transpose_semitones(None),0)
        for value in range(-12,13):self.assertEqual(engine.transpose_semitones(value),value)

    def test_every_key_interval_mode_and_chord_preserves_harmonic_relationships(self):
        for fifths in range(-7,8):
            for minor in (False,True):
                for shift in range(-12,13):
                    original=dict(key=dict(fifths=fifths,minor=minor,root=(7*fifths+(9 if minor else 0))%12),
                        events=[dict(tick=i,root=i%12,quality=q,pitch_classes=sorted((i+x)%12 for x in intervals),confidence=.8)
                                for i,(q,intervals) in enumerate(engine.CHORD_INTERVALS.items())])
                    before=copy.deepcopy(original);result=engine.transpose_harmony(original,shift)
                    self.assertEqual(original,before)
                    self.assertEqual((result['key']['root']-original['key']['root'])%12,shift%12)
                    self.assertEqual(result['key']['minor'],minor)
                    self.assertTrue(-7<=result['key']['fifths']<=7)
                    for source,target in zip(original['events'],result['events']):
                        self.assertEqual(target['root'],(source['root']+shift)%12)
                        self.assertEqual(target['quality'],source['quality'])
                        self.assertEqual(target['tick'],source['tick'])
                        self.assertEqual(target['pitch_classes'],sorted((pc+shift)%12 for pc in source['pitch_classes']))
                        _,_,diagram=engine.keyboard_asset(target['root'],target['quality'],2)
                        self.assertEqual({note%12 for note in diagram},set(target['pitch_classes']))

    def test_notes_graces_durations_meter_tempos_and_changes(self):
        parsed,config,harmony=fixture()
        # One of the original notes is a grace before its real target.
        config['grace_notes']=[dict(start_quarter=4,pitch=60,target_quarter=4.5)]
        config['melody_transpose_semitones']=-12
        saved=copy.deepcopy((parsed,config,harmony))
        with tempfile.TemporaryDirectory() as directory:
            xml,base=engine.build_musicxml(parsed,config,harmony,Path(directory),'Teste')
            base_xml=E.parse(xml)
            for shift in (-12,-2,2,12):
                xml,transposed=engine.build_musicxml(parsed,dict(config,transpose_semitones=shift),harmony,Path(directory),'Teste')
                target_xml=E.parse(xml)
                for source,target in zip(base['notes'],transposed['notes']):
                    self.assertEqual((target['start'],target['end']),(source['start'],source['end']))
                    self.assertEqual(target['pitch'],source['pitch']+shift)
                    self.assertEqual(target.get('graces',[]),[pitch+shift for pitch in source.get('graces',[])])
                self.assertEqual(base['bars'],transposed['bars'])
                self.assertEqual(base['origin_removed_quarters'],transposed['origin_removed_quarters'])
                self.assertEqual([node.text for node in base_xml.findall('.//duration')],[node.text for node in target_xml.findall('.//duration')])
                self.assertEqual([node.attrib for node in base_xml.findall('.//sound')],[node.attrib for node in target_xml.findall('.//sound')])
                for source,target in zip(base['chords'],transposed['chords']):
                    self.assertEqual((source['tick'],source['quality']),(target['tick'],target['quality']))
                    self.assertEqual(target['root'],(source['root']+shift)%12)
                self.assertEqual(len(base_xml.findall('.//key')),len(target_xml.findall('.//key')))
        self.assertEqual((parsed,config,harmony),saved)

    def test_out_of_range_is_rejected_without_clamping(self):
        parsed,config,harmony=fixture()
        parsed['notes'][-1]['pitch']=126
        with tempfile.TemporaryDirectory() as directory,self.assertRaisesRegex(engine.NeedsReview,'ultrapassa'):
            engine.build_musicxml(parsed,dict(config,transpose_semitones=2),harmony,Path(directory),'Teste')

class ReviewTests(unittest.TestCase):
    def test_position_matches_trimmed_origin_and_changed_meter(self):
        parsed,config,harmony=fixture()
        points,ready,message=engine.review_points(parsed,config,harmony)
        self.assertTrue(ready);self.assertEqual(message,'');self.assertEqual(len(points),1)
        point=points[0]
        self.assertEqual(point['kind'],'chord_low_confidence')
        self.assertEqual((point['quarter'],point['measure'],point['beat']),(12,3,1))
        self.assertEqual((point['measureStartQuarter'],point['measureEndQuarter']),(12,15))
        _,meters,_=engine.meta(parsed);origin,_,bars=engine.make_bars(parsed,meters)
        point=engine.locate_review_point(12.5,origin,bars,'test','test')
        self.assertEqual((point['measure'],point['beat']),(3,2))
        self.assertIsNone(engine.locate_review_point(0,origin,bars,'test','test'))

    def test_unresolved_boundary_preserves_two_actual_attack_locations(self):
        with self.assertRaises(engine.NeedsReview) as caught:
            engine._refine_boundary_grids({3:[.97],4:[.03]},{3:4,4:4},{3:(4,8,16),4:(4,8,16)},0,8*960)
        self.assertEqual(len(caught.exception.quarters),2)
        for actual,expected in zip(caught.exception.quarters,[3.97,4.03]):self.assertAlmostEqual(actual,expected)

    def test_refined_beats_are_mapped_without_inventing_collisions(self):
        parsed,config,harmony=fixture()
        ppq=384
        quarters=[4+i*.5 for i in range(6)]+[8.25,8.5,8.75,8+356/384,9,9.4375,9.5]+list(range(10,21))
        parsed['ppq']=ppq;parsed['events']=[]
        parsed['notes']=[dict(start=round(q*ppq),end=round((q+.3)*ppq),duration=round(.3*ppq),pitch=[62,69,71][i%3],track=1,channel=1,program=0,velocity=107) for i,q in enumerate(quarters)]
        parsed['end_tick']=parsed['notes'][-1]['end']
        harmony['events']=[]
        points,ready,_=engine.review_points(parsed,config,harmony)
        self.assertTrue(ready)
        self.assertEqual([(point['kind'],point['quarter'],point['measure']) for point in points],[('rhythm_refined',8,2)])
        self.assertFalse(points[0]['blocking'])

    def test_global_error_is_not_assigned_an_invented_measure(self):
        parsed,config,harmony=fixture();parsed['notes']=parsed['notes'][:2];harmony['events']=[]
        points,ready,message=engine.review_points(parsed,config,harmony)
        self.assertEqual(points,[]);self.assertFalse(ready);self.assertIn('Poucas notas',message)

    def test_blocking_collision_is_localized_in_analysis_and_generation(self):
        parsed,config,harmony=fixture();ppq=parsed['ppq'];harmony['events']=[]
        parsed['notes'][7]['start']=round(7.97*ppq)
        parsed['notes'][7]['end']=round(8.02*ppq)
        parsed['notes'][8]['start']=round(8.03*ppq)
        parsed['notes'][8]['end']=round(8.4*ppq)
        points,ready,message=engine.review_points(parsed,config,harmony)
        self.assertFalse(ready);self.assertEqual(len(points),2)
        self.assertEqual([point['measure'] for point in points],[1,2])
        self.assertTrue(all(point['blocking'] for point in points))
        with tempfile.TemporaryDirectory() as directory,self.assertRaises(engine.NeedsReview) as caught:
            engine.build_musicxml(parsed,config,harmony,Path(directory),'Teste')
        self.assertEqual(caught.exception.review_points,points)

if __name__=='__main__':unittest.main(verbosity=2)
