"""Ordered manual sources share one staff without borrowing the highest other part."""
from pathlib import Path
import copy,io,json,os,random,struct,subprocess,sys,tempfile,unittest
from contextlib import redirect_stdout

sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'engine'))
os.environ.setdefault('PARTITURAS_JOB',str(Path(tempfile.gettempdir())/'TeclaScorePriorityTests'))
import batch_score as engine
import worker

PPQ=960
A=dict(track=1,channel=1,program=0)
B=dict(track=2,channel=2,program=73)
C=dict(track=3,channel=3,program=24)

def note(quarter,pitch,source=A,duration=.75,velocity=80):
    return dict(source,start=round(quarter*PPQ),end=round((quarter+duration)*PPQ),
                duration=round(duration*PPQ),pitch=pitch,velocity=velocity)

def parsed(notes):
    return dict(path='priority.mid',sha256='synthetic',ppq=PPQ,notes=notes,format=1,
                end_tick=max((n['end'] for n in notes),default=0),tracks=[],warnings=[],events=[])

def merge(notes,sources=(A,B)):
    return engine.priority_melody_notes(notes,list(sources),PPQ)

def write_midi(path):
    def vlq(value):
        result=[value&127];value>>=7
        while value:result.insert(0,(value&127)|128);value>>=7
        return bytes(result)
    def track(source,notes):
        events=[(0,bytes([0xc0+source['channel']-1,source['program']]))]
        for n in notes:
            events.extend([(n['start'],bytes([0x90+source['channel']-1,n['pitch'],n['velocity']])),
                           (n['end'],bytes([0x80+source['channel']-1,n['pitch'],0]))])
        data=b'';previous=0
        for tick,message in sorted(events,key=lambda e:(e[0],0 if e[1][0]&0xf0==0xc0 else 1,e[1][0])):
            data+=vlq(tick-previous)+message;previous=tick
        data+=b'\x00\xff\x2f\x00'
        return b'MTrk'+struct.pack('>I',len(data))+data
    conductor=b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    data=b'MThd'+struct.pack('>IHHH',6,1,4,PPQ)+b'MTrk'+struct.pack('>I',len(conductor))+conductor
    data+=track(A,[note(i,60+i%3,A) for i in range(12,36)])
    data+=track(B,[note(i,84+i%3,B) for i in range(36)])
    data+=track(C,[note(i,pitch,C,3.9) for i in range(0,36,4) for pitch in (48,52,55)])
    Path(path).write_bytes(data)

class PriorityMelodyTests(unittest.TestCase):
    def test_sequential_intro_and_melody_preserve_both_sources(self):
        notes=[note(i,72+i%3,B) for i in range(8)]+[note(i,60+i%3,A) for i in range(8,24)]
        before=copy.deepcopy(notes)
        events,_,report=engine.melody_events(parsed(notes),worker.manual_melody_config([A,B]),0,24*PPQ)
        self.assertEqual([n['pitch'] for n in events],[n['pitch'] for n in notes])
        self.assertEqual([r['quantizedAttacks'] for r in report['melody_merge']['sources']],[16,8])
        self.assertEqual(report['melody_merge']['suppressedAttacks'],0)
        self.assertEqual(notes,before)

    def test_simultaneous_lower_priority_high_pitch_never_replaces_lead(self):
        notes=[note(i,60+i%3,A) for i in range(24)]+[note(i,84+i%3,B) for i in range(24)]
        events,_,report=engine.melody_events(parsed(notes),worker.manual_melody_config([A,B]),0,24*PPQ)
        self.assertEqual([n['pitch'] for n in events],[60+i%3 for i in range(24)])
        self.assertEqual(report['melody_merge']['sources'][1]['suppressedAttacks'],24)
        points,ready,_=engine.review_points(parsed(notes),worker.manual_melody_config([A,B]),{'events':[]})
        self.assertTrue(ready)
        overlaps=[p for p in points if p['kind']=='melody_source_overlap']
        self.assertEqual([p['measure'] for p in overlaps],list(range(1,7)))
        self.assertTrue(all(not p['blocking'] for p in overlaps))

    def test_reversing_source_order_changes_only_the_selected_simultaneous_line(self):
        notes=[note(i,60+i%3,A) for i in range(24)]+[note(i,84+i%3,B) for i in range(24)]
        low=engine.melody_events(parsed(notes),worker.manual_melody_config([A,B]),0,24*PPQ)[0]
        high=engine.melody_events(parsed(notes),worker.manual_melody_config([B,A]),0,24*PPQ)[0]
        self.assertEqual([(e['start'],e['end']) for e in low],[(e['start'],e['end']) for e in high])
        self.assertEqual([n['pitch'] for n in high],[84+i%3 for i in range(24)])

    def test_exact_doubling_is_deduplicated_without_conflict_warning(self):
        notes=[note(i,60+i%3,A) for i in range(24)]+[note(i,60+i%3,B,velocity=110) for i in range(24)]
        events,_,report=engine.melody_events(parsed(notes),worker.manual_melody_config([A,B]),0,24*PPQ)
        self.assertEqual(len(events),24)
        self.assertEqual(report['melody_merge']['duplicateAttacks'],24)
        self.assertEqual(report['melody_merge']['overlaps'],[])
        points,_,_=engine.review_points(parsed(notes),worker.manual_melody_config([A,B]),{'events':[]})
        self.assertFalse(any(p['kind']=='melody_source_overlap' for p in points))

    def test_lower_tail_is_trimmed_and_never_restarted_when_priority_ends(self):
        notes=[note(0,80,B,4),note(1,60,A,1),note(5,82,B,1)]
        selected,report=merge(notes)
        self.assertEqual([(n['start'],n['end'],n['pitch']) for n in selected],[(0,960,80),(960,1920,60),(4800,5760,82)])
        self.assertEqual(report['trimmedNotes'],1)
        self.assertFalse(any(n['start']==1920 for n in selected))

    def test_masked_attack_is_not_resurrected_and_real_new_attack_can_fill_gap(self):
        selected,report=merge([note(0,60,A,2),note(1,84,B,4),note(3,86,B,.5),note(6,87,B,.5)])
        self.assertEqual([n['start'] for n in selected],[0,2880,5760])
        self.assertEqual([n['pitch'] for n in selected],[60,86,87])
        self.assertEqual(report['suppressedAttacks'],1)

    def test_same_source_legato_is_clipped_before_testing_other_source_activity(self):
        selected,_=merge([note(0,60,A,10),note(1,62,A,.5),note(2,84,B,.5)])
        self.assertEqual([(n['start'],n['end']) for n in selected],[(0,960),(960,1440),(1920,2400)])

    def test_absent_and_silent_sources_do_not_create_notes(self):
        selected,report=merge([note(3,67,B,.5)],(A,B,C))
        self.assertEqual([n['start'] for n in selected],[2880])
        self.assertEqual([s['keptAttacks'] for s in report['sources']],[0,1,0])
        empty,summary=merge([])
        self.assertEqual(empty,[]);self.assertEqual(summary['keptAttacks'],0)

    def test_source_key_includes_program_and_channel_even_with_shared_track(self):
        other=dict(A,program=48);channel=dict(A,channel=2)
        notes=[note(0,60,A),note(0,84,other),note(2,72,channel)]
        selected,report=merge(notes,[A,other,channel])
        self.assertEqual([n['pitch'] for n in selected],[60,72])
        self.assertEqual([r['inputNotes'] for r in report['sources']],[1,1,1])
        self.assertEqual(merge(notes,[other,A,channel])[0][0]['pitch'],84)

    def test_upper_line_is_selected_within_each_source_only(self):
        selected,_=merge([note(0,60,A),note(0,64,A),note(0,67,A),note(0,90,B),note(2,72,B),note(2,79,B)])
        self.assertEqual([n['pitch'] for n in selected],[67,79])

    def test_duplicate_input_order_cannot_change_selection(self):
        notes=[note(q,60+i%3,A,1.2) for i,q in enumerate(range(24))]
        notes += [note(q+.5,84+i%3,B) for i,q in enumerate(range(24))]+[dict(notes[0])]
        expected=merge(notes)
        random.Random(173).shuffle(notes)
        self.assertEqual(merge(notes),expected)

    def test_cross_source_close_attacks_use_one_global_quantization_without_loss(self):
        notes=[note(0.22,60,A,.01),note(.27,84,B,.1)]
        notes += [note(i,62+i%3,A) for i in range(1,24)]
        events,grids,report=engine.melody_events(parsed(notes),worker.manual_melody_config([A,B]),0,24*PPQ)
        self.assertEqual(len(events),len(notes))
        self.assertEqual([n['pitch'] for n in events],[n['pitch'] for n in notes])
        self.assertEqual(report['quantized_attack_collisions'],0)
        self.assertEqual(sum(s['quantizedAttacks'] for s in report['melody_merge']['sources']),len(notes))
        self.assertEqual(len({n['start'] for n in events}),len(events))
        for i,event in enumerate(events):
            self.assertLessEqual(event['end'],events[i+1]['start'] if i+1<len(events) else 24*PPQ)

    def test_review_warnings_cover_every_bar_of_a_masked_tail(self):
        notes=[note(i,60+i%3,A) for i in range(24)]+[note(0,84,B,20)]
        points,ready,_=engine.review_points(parsed(notes),worker.manual_melody_config([A,B]),{'events':[]})
        self.assertTrue(ready)
        self.assertEqual([p['measure'] for p in points if p['kind']=='melody_source_overlap'],[1,2,3,4,5])

    def test_single_source_and_non_opted_in_configs_keep_existing_behavior(self):
        notes=[note(i,60+i%3,A) for i in range(24)]+[note(i,84+i%3,B) for i in range(24)]
        plain={'melody_sources':[A]}
        forced=dict(plain,merge_mode='priority')
        self.assertEqual(engine.melody_events(parsed(notes),plain,0,24*PPQ),engine.melody_events(parsed(notes),forced,0,24*PPQ))
        reviewed={'melody_sources':[A,B]}
        events,_,report=engine.melody_events(parsed(notes),reviewed,0,24*PPQ)
        self.assertEqual([n['pitch'] for n in events],[84+i%3 for i in range(24)])
        self.assertNotIn('melody_merge',report)
        profiles=json.loads((ROOT/'engine/profiles.json').read_text(encoding='utf8'))
        approved=[p for p in profiles.values() if p['config']['status']=='ready']
        self.assertEqual(len(approved),18)
        self.assertTrue(all('merge_mode' not in p['config'] for p in approved))

    def test_musicxml_remains_one_staff_with_actual_merged_attack_sequence(self):
        notes=[note(i,72+i%3,B) for i in range(8)]+[note(i,60+i%3,A) for i in range(8,24)]
        config=worker.manual_melody_config([A,B]);harmony=dict(key={'fifths':0,'minor':False},events=[dict(tick=0,root=0,quality='',confidence=1.,pitch_classes=[0,4,7])])
        with tempfile.TemporaryDirectory() as directory,redirect_stdout(io.StringIO()):
            path,report=engine.build_musicxml(parsed(notes),config,harmony,Path(directory),'Priority')
            tree=engine.E.parse(path)
            self.assertEqual(len(tree.findall('./part')),1)
            self.assertEqual(report['melody_analysis']['melody_merge']['quantizedAttacks'],24)
            self.assertEqual([n['pitch'] for n in report['notes']],[n['pitch'] for n in notes])

    def test_manual_request_priority_is_stable_and_duplicates_do_not_enable_merge(self):
        self.assertEqual(worker.manual_melody_config([B,A,B])['melody_sources'],[B,A])
        self.assertEqual(worker.manual_melody_config([B,A])['merge_mode'],'priority')
        self.assertNotIn('merge_mode',worker.manual_melody_config([A,A]))
        for sources in ([],[dict(A,channel=10)],[dict(A,program=128)],[dict(A,channel=True)],[{'track':1}]):
            with self.assertRaises(ValueError):worker.manual_melody_config(sources)

    def test_worker_analysis_preserves_request_order_and_returns_merge_review_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);midi=root/'priority.mid';write_midi(midi)
            request=root/'request.json'
            request.write_text(json.dumps(dict(mode='analyze',workspace=str(root/'job'),inputMidi=str(midi),
                                              useReviewedProfile=False,melodySources=[A,B])),encoding='utf8')
            process=subprocess.run([sys.executable,'-B',str(ROOT/'engine/worker.py'),str(request)],capture_output=True,
                                   text=True,encoding='utf8',timeout=30)
            self.assertEqual(process.returncode,0,process.stdout+process.stderr)
            result=[json.loads(line)['data'] for line in process.stdout.splitlines() if json.loads(line)['kind']=='result'][0]
            analysis=json.loads(Path(result['analysisPath']).read_text(encoding='utf8'))
            self.assertEqual(analysis['config']['melody_sources'],[A,B])
            self.assertEqual(analysis['config']['merge_mode'],'priority')
            self.assertEqual(result['melodyMerge'],analysis['melodyMerge'])
            self.assertEqual(result['chords'],analysis['chords'])
            self.assertEqual(result['keyFifths'],analysis['keyFifths'])
            self.assertEqual(result['keyMinor'],analysis['keyMinor'])
            self.assertEqual([r['quantizedAttacks'] for r in result['melodyMerge']['sources']],[24,12])
            self.assertTrue(any(p['kind']=='melody_source_overlap' for p in result['reviewPoints']))
            self.assertTrue(result['rhythmReady'])

if __name__=='__main__':unittest.main(verbosity=2)
