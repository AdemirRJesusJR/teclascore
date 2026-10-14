"""Exclusive source ranges use the exact score measure map, including initial trim and meter changes."""
import copy, hashlib, io, json, subprocess, sys, tempfile, unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch
from test_priority_melody import A,B,C,PPQ,ROOT,note,parsed,write_midi,engine,worker
from melody_sections import measure_map,normalize_sections,selected_notes,all_sources
from inspect_midi import parse_midi

def section(first,last,source):
    return dict(firstMeasure=first,lastMeasure=last,source=copy.deepcopy(source))
def config(sections,sources=(A,)):
    return dict(worker.manual_melody_config(list(sources)),melody_sections=sections)
def rendered(document,options):
    _,meters,_=engine.meta(document);origin,end,_=engine.make_bars(document,meters)
    return engine.melody_events(document,options,origin,end)
def select(document,options):
    _,meters,_=engine.meta(document);origin,end,_=engine.make_bars(document,meters)
    return selected_notes(document,options,engine,origin,end)[0]
def source_at(events,quarter):
    return next(n['source'] for n in events if n['start']==round(quarter*engine.DIV))
def basic():
    return parsed([note(i,60+i%3,A) for i in range(48)]+[note(i,84+i%3,B) for i in range(48)])
def command(root,midi,**arguments):
    request=root/('request-'+str(len(list(root.glob('request-*'))))+'.json')
    request.write_text(json.dumps(dict(workspace=str(root/('job-'+request.stem)),inputMidi=str(midi),**arguments)),encoding='utf8')
    output=subprocess.run([sys.executable,'-B',str(ROOT/'engine/worker.py'),str(request)],capture_output=True,text=True,encoding='utf8',timeout=60)
    messages=[json.loads(line) for line in output.stdout.splitlines()]
    return output,messages

class MelodySectionsTests(unittest.TestCase):
    def test_explicit_source_outside_general_selection_and_fallback(self):
        events,_,report=rendered(basic(),config([section(2,3,B),section(7,8,B)]))
        self.assertEqual([n['pitch'] for n in events],[84+i%3 if 4<=i<12 or 24<=i<32 else 60+i%3 for i in range(48)])
        self.assertEqual([label['text'] for label in engine.instrument_labels(events)],
                         ['Acoustic Grand Piano','Flute','Acoustic Grand Piano','Flute','Acoustic Grand Piano'])
        self.assertEqual([label['quarter'] for label in engine.instrument_labels(events)],[0,4,12,24,32])
        self.assertEqual(report['melody_merge']['sections'],[section(2,3,B),section(7,8,B)])
        self.assertEqual(len(events),48)

    def test_range_is_exclusive_and_silence_never_borrows_general_priority(self):
        document=parsed([note(i,60+i%3,A) for i in range(48)]+[note(i,84+i%3,B) for i in range(8)])
        events,_,_=rendered(document,config([section(2,4,B)]))
        self.assertEqual([n['start']//engine.DIV for n in events],list(range(8))+list(range(16,48)))
        self.assertTrue(all(n['source']['track']==B['track'] for n in events if 4*engine.DIV<=n['start']<8*engine.DIV))

    def test_sustained_notes_clipped_at_exclusive_switch_and_not_restarted(self):
        document=parsed([note(0,60,A,10),note(1,72,B,10),note(5,74,B,.5),note(12,62,A,1)])
        selected=select(document,config([section(2,3,B)]))
        self.assertEqual([(n['start']/PPQ,n['end']/PPQ,n['pitch']) for n in selected],[(0,4,60),(5,5.5,74),(12,13,62)])
        self.assertFalse(any(n['start']==4*PPQ for n in selected))

    def test_note_can_continue_between_adjacent_windows_when_same_source_allowed(self):
        document=parsed([note(0,60,A,15),note(15,62,A,1)])
        selected=select(document,config([section(2,2,A),section(3,3,A)]))
        self.assertEqual(selected[0]['end'],15*PPQ)

    def test_global_priority_is_preserved_outside_sections(self):
        events,_,_=rendered(basic(),config([section(2,2,A)],sources=(B,A)))
        self.assertEqual([n['pitch'] for n in events],[60+i%3 if 4<=i<8 else 84+i%3 for i in range(48)])
        self.assertEqual(source_at(events,8)['track'],B['track'])

    def test_channel_and_program_are_part_of_exclusive_identity(self):
        alternate=dict(A,program=48);channel=dict(A,channel=2)
        document=parsed([note(i,60+i%3,A) for i in range(32)]+[note(i,72+i%3,alternate) for i in range(32)]+[note(i,84+i%3,channel) for i in range(32)])
        events,_,_=rendered(document,config([section(2,2,alternate),section(3,3,channel)]))
        self.assertEqual(source_at(events,4)['program'],48)
        self.assertEqual(source_at(events,8)['channel'],2)
        self.assertEqual(source_at(events,12)['program'],0)

    def test_map_shares_origin_meter_and_partial_bars_without_selection_dependency(self):
        document=parsed([note(i,60+i%3,A) for i in range(9,49)])
        meters=[dict(tick=0,numerator=4,denominator=4),dict(tick=14*PPQ,numerator=3,denominator=4),
                dict(tick=23*PPQ,numerator=6,denominator=8)]
        with patch.object(engine,'meta',return_value=({},meters,[])):
            mapping=measure_map(document,engine)
            origin,end,bars=engine.make_bars(document,meters)
            self.assertEqual(mapping['originQuarter'],8)
            self.assertEqual([(m['number'],m['startQuarter'],m['endQuarter'],m['numerator'],m['denominator'],m['partial']) for m in mapping['measures'][:5]],
                             [(1,8,12,4,4,False),(2,12,14,4,4,True),(3,14,17,3,4,False),(4,17,20,3,4,False),(5,20,23,3,4,False)])
            self.assertEqual(mapping['measures'][5]['denominator'],8)
            self.assertEqual([(m['startQuarter'],m['endQuarter']) for m in mapping['measures']],[( (origin+b['start'])/engine.DIV,(origin+b['end'])/engine.DIV) for b in bars])
            selected_notes(document,config([section(3,4,A)]),engine,origin,end)
            self.assertEqual(measure_map(document,engine),mapping)

    def test_midbar_metric_change_defines_exclusive_range_not_assumed_four_four(self):
        document=basic();meters=[dict(tick=0,numerator=4,denominator=4),dict(tick=6*PPQ,numerator=3,denominator=4)]
        with patch.object(engine,'meta',return_value=({},meters,[])):
            events,_,_=rendered(document,config([section(3,3,B)]))
        self.assertEqual([n['start']/engine.DIV for n in events if n['source']['track']==B['track']],[6,7,8])

    def test_invalid_ranges_reject_instead_of_normalizing_music(self):
        document=basic();bad=[section(0,2,A),section(3,2,A),section(1,99,A),section(True,2,A),
            section(1.5,2,A),dict(firstMeasure=1,lastMeasure=2),section(1,2,dict(A,track=99)),
            section(1,2,dict(A,channel=10)),section(1,2,dict(A,program=True)),
            [section(1,2,A),section(2,3,B)]]
        for value in bad:
            with self.subTest(value=value),self.assertRaises(ValueError):
                normalize_sections(value if isinstance(value,list) else [value],document,engine)
        for value in ({},'1-2',True,[section(1,1,A)]*701):
            with self.assertRaises(ValueError):normalize_sections(value,document,engine)
        self.assertEqual(normalize_sections([section(3,3,B),section(1,1,A)],document,engine),[section(1,1,A),section(3,3,B)])

    def test_empty_ranges_preserve_legacy_and_all_eighteen_reviewed_configs(self):
        document=basic()
        for options in (worker.manual_melody_config([A]),worker.manual_melody_config([B,A]),{'melody_sources':[A,B]}):
            self.assertEqual(rendered(document,options),rendered(document,dict(options,melody_sections=[])))
            self.assertEqual(all_sources(options),options['melody_sources'])
        profiles=json.loads((ROOT/'engine/profiles.json').read_text(encoding='utf8'))
        approved=[p['config'] for p in profiles.values() if p['config']['status']=='ready']
        self.assertEqual(len(approved),18)
        for options in approved:
            self.assertNotIn('melody_sections',options)
            self.assertEqual(all_sources(options),options['melody_sources'])

    def test_end_of_section_quantization_never_moves_attack_into_wrong_source(self):
        document=basic()
        document['notes'].append(note(7.999,75,B,.1))
        events,_,_=rendered(document,config([section(2,2,B)]))
        self.assertTrue(all(n['start']<8*engine.DIV and n['end']<=8*engine.DIV for n in events if n['pitch']==75))
        # Without a following attack to refine the grid, fail visibly rather than crossing the boundary.
        document['notes']=[n for n in document['notes'] if not(n['start']==8*PPQ and n['track']==A['track'])]
        with self.assertRaisesRegex(engine.NeedsReview,'troca de trecho'):
            rendered(document,config([section(2,2,B)]))

    def test_build_musicxml_keeps_single_staff_correct_instrument_names_and_source_content(self):
        options=config([section(2,2,B),section(4,4,B)])
        harmony=dict(key={'fifths':0,'minor':False},events=[dict(tick=0,root=0,quality='',confidence=1.,pitch_classes=[0,4,7])])
        with tempfile.TemporaryDirectory() as folder,redirect_stdout(io.StringIO()):
            path,report=engine.build_musicxml(basic(),options,harmony,Path(folder),'Trechos')
            xml=engine.E.parse(path)
        self.assertEqual(len(xml.findall('./part')),1)
        self.assertEqual([label['quarter'] for label in report['instrument_labels']],[0,4,8,12,16])
        self.assertEqual([label['text'] for label in report['instrument_labels']],['Acoustic Grand Piano','Flute','Acoustic Grand Piano','Flute','Acoustic Grand Piano'])
        self.assertEqual(len(report['notes']),48)

    def test_worker_protocol_map_and_sections_analysis_and_source_hash_guard(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);midi=root/'song.mid';write_midi(midi);before=midi.read_bytes();digest=hashlib.sha256(before).hexdigest()
            process,messages=command(root,midi,mode='measureMap')
            self.assertEqual(process.returncode,0,process.stdout+process.stderr)
            mapping=next(message['data'] for message in messages if message['kind']=='result')
            self.assertEqual(mapping,measure_map(parse_midi(midi),engine))
            chosen=[section(1,3,B),section(4,5,A)]
            process,messages=command(root,midi,mode='analyze',useReviewedProfile=False,melodySources=[B],melodySections=chosen,expectedSourceSha256=digest)
            self.assertEqual(process.returncode,0,process.stdout+process.stderr)
            result=next(message['data'] for message in messages if message['kind']=='result')
            analysis=json.loads(Path(result['analysisPath']).read_text(encoding='utf8'))
            self.assertEqual(analysis['config']['melody_sections'],chosen)
            self.assertEqual(analysis['config']['melody_sources'],[B])
            self.assertTrue(result['rhythmReady'])
            self.assertEqual([n['pitch'] for n in rendered(parse_midi(midi),analysis['config'])[0]],
                             [60+i%3 if 12<=i<20 else 84+i%3 for i in range(36)])
            process,messages=command(root,midi,mode='analyze',useReviewedProfile=False,melodySources=[A],melodySections=chosen,expectedSourceSha256='0'*64)
            self.assertNotEqual(process.returncode,0)
            self.assertIn('mudou',next(message['message'] for message in messages if message['kind']=='error'))
            self.assertEqual(midi.read_bytes(),before)

    def test_configuration_and_notes_remain_immutable(self):
        document=basic();options=config([section(2,3,B)]);before=copy.deepcopy((document,options))
        rendered(document,options)
        self.assertEqual((document,options),before)

if __name__=='__main__':unittest.main(verbosity=2)
