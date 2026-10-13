"""Per-occurrence keyboard positions preserve harmonic and melodic content."""
from pathlib import Path
import copy,hashlib,json,os,subprocess,sys,tempfile,unittest
import xml.etree.ElementTree as E

sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'engine'))
os.environ.setdefault('PARTITURAS_JOB',str(Path(tempfile.gettempdir())/'TeclaScoreChordDiagramTests'))
import batch_score as engine


def fixture():
    ppq=480
    notes=[dict(start=i*ppq,end=round((i+.8)*ppq),duration=round(.8*ppq),
        track=1,channel=1,program=0,pitch=[60,64,67,72][i%4],velocity=90) for i in range(64)]
    parsed=dict(path='diagram-test.mid',sha256='synthetic',ppq=ppq,notes=notes,format=1,
        end_tick=notes[-1]['end'],tracks=[],warnings=[],events=[
            dict(tick=0,track=0,kind='meta',type=88,data=bytes([4,2,24,8]))])
    config=dict(melody_sources=[dict(track=1,channel=1,program=0)],diagram_inversion=0)
    events=[dict(tick=quarter*ppq,root=root,quality=quality,confidence=1.,
                 pitch_classes=sorted((root+n)%12 for n in engine.CHORD_INTERVALS[quality]),
                 diagram_inversion=inversion)
            for quarter,root,quality,inversion in [(0,0,'',2),(4,9,'m',0),(8,0,'',1),(12,0,'',None),(16,2,'m',None)]]
    del events[-1]['diagram_inversion']
    return parsed,config,dict(key=dict(fifths=0,minor=False,root=0),events=events)


class ChordDiagramTests(unittest.TestCase):
    def test_null_missing_and_zero_have_distinct_meanings(self):
        for default in range(3):
            self.assertEqual(engine.chord_diagram_inversion({},default),default)
            self.assertEqual(engine.chord_diagram_inversion({'diagram_inversion':None},default),default)
            for override in range(3):
                self.assertEqual(engine.chord_diagram_inversion({'diagram_inversion':override},default),override)

    def test_mixed_occurrences_remain_separate_and_do_not_change_music(self):
        parsed,config,harmony=fixture();before=copy.deepcopy((parsed,config,harmony))
        original_harmony=copy.deepcopy(harmony)
        for chord in original_harmony['events']:chord.pop('diagram_inversion',None)
        with tempfile.TemporaryDirectory() as directory:
            _,original=engine.build_musicxml(parsed,config,original_harmony,Path(directory),'Teste')
            xml,result=engine.build_musicxml(parsed,config,harmony,Path(directory),'Teste')
            self.assertEqual(original['notes'],result['notes'])
            self.assertEqual(original['bars'],result['bars'])
            self.assertEqual(original['melody_analysis'],result['melody_analysis'])
            real=[c for c in result['chords'] if not c.get('reminder')]
            self.assertEqual(len(real),5)
            self.assertEqual([engine.chord_diagram_inversion(c,0) for c in real],[2,0,1,0,0])
            self.assertEqual([engine.keyboard_asset(c['root'],c['quality'],engine.chord_diagram_inversion(c,0))[2]
                              for c in real[:3]],[[55,60,64],[57,60,64],[52,55,60]])
            self.assertTrue(result['custom_diagram_inversions'])
            self.assertIn('posições personalizadas',result['score_metadata']['subtitle'])
            self.assertEqual(len(E.parse(xml).findall('.//harmony')),len(result['chords']))
            reminders=[c for c in result['chords'] if c.get('reminder')]
            self.assertTrue(reminders,'Long source should repeat its active chord at a system start')
            for reminder in reminders:
                active=max((c for c in real if c['tick']<=reminder['tick']),key=lambda c:c['tick'])
                self.assertEqual(reminder.get('diagram_inversion'),active.get('diagram_inversion'))
        self.assertEqual((parsed,config,harmony),before)

    def test_transposition_preserves_positions_and_aligned_basses(self):
        parsed,config,harmony=fixture()
        with tempfile.TemporaryDirectory() as directory:
            for shift in (-12,-2,2,12):
                _,result=engine.build_musicxml(parsed,dict(config,transpose_semitones=shift),harmony,Path(directory),'Teste')
                real=[c for c in result['chords'] if not c.get('reminder')]
                for before,after in zip(harmony['events'],real):
                    self.assertEqual(before.get('diagram_inversion'),after.get('diagram_inversion'))
                    inversion=engine.chord_diagram_inversion(after,0)
                    _,_,pitches=engine.keyboard_asset(after['root'],after['quality'],inversion)
                    expected_interval=(0,3 if after['quality']=='m' else 4,7)[inversion]
                    self.assertEqual(pitches[0]%12,(before['root']+shift+expected_interval)%12)
                    self.assertEqual({p%12 for p in pitches},{(p+shift)%12 for p in before['pitch_classes']})

    def test_automatic_subtitle_changes_only_for_effective_override(self):
        parsed,config,harmony=fixture()
        with tempfile.TemporaryDirectory() as directory:
            for custom_text in ('','Meu subtítulo'):
                _,result=engine.build_musicxml(parsed,dict(config,scoreSubtitle=custom_text),harmony,Path(directory),'Teste')
                self.assertEqual(result['score_metadata']['subtitle'],custom_text)
            for chord in harmony['events']:chord['diagram_inversion']=0
            _,result=engine.build_musicxml(parsed,config,harmony,Path(directory),'Teste')
            self.assertFalse(result['custom_diagram_inversions'])
            self.assertTrue(result['score_metadata']['subtitle'].endswith('posição fundamental'))

    def test_invalid_late_override_is_rejected_even_after_valid_custom_position(self):
        parsed,config,harmony=fixture()
        with tempfile.TemporaryDirectory() as directory:
            for invalid in (True,False,1.0,'1',-1,3,[],{}):
                harmony['events'][-1]['diagram_inversion']=invalid
                with self.subTest(invalid=repr(invalid)),self.assertRaises(ValueError):
                    engine.build_musicxml(parsed,config,harmony,Path(directory),'Teste')

    def test_quantization_cannot_silently_discard_an_occurrence_override(self):
        parsed,config,harmony=fixture()
        harmony['events'][1]['tick']=1
        with tempfile.TemporaryDirectory() as directory,self.assertRaises(engine.NeedsReview) as raised:
            engine.build_musicxml(parsed,config,harmony,Path(directory),'Teste')
        self.assertEqual(raised.exception.kind,'diagram_collision')
        self.assertEqual(raised.exception.review_points[0]['measure'],1)
        self.assertTrue(raised.exception.review_points[0]['blocking'])

    def test_protocol_rejects_invalid_override_before_opening_musescore(self):
        with tempfile.TemporaryDirectory() as directory:
            base=Path(directory);source=base/'source.mid';source.write_bytes(b'not opened before request validation')
            analysis=base/'analysis.json'
            analysis.write_text(json.dumps(dict(source=str(source.resolve()),sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                config=dict(filename=source.name),ppq=480,harmony=dict(events=[]))),encoding='utf8')
            request=dict(mode='generate',inputMidi=str(source),workspace=str(base/'job'),analysisPath=str(analysis),
                chords=[dict(root='C',quality='',quarter=0,diagramInversion=3)])
            request_path=base/'request.json';request_path.write_text(json.dumps(request),encoding='utf8')
            run=subprocess.run([sys.executable,'-B',str(ROOT/'engine/worker.py'),str(request_path)],capture_output=True,text=True,encoding='utf8',timeout=30)
            self.assertEqual(run.returncode,1)
            messages=[json.loads(line) for line in run.stdout.splitlines() if line.startswith('{')]
            self.assertEqual(messages[-1]['kind'],'error')
            self.assertIn('inversão dos diagramas',messages[-1]['message'])
            self.assertFalse(any('MuseScore:' in item.get('message','') for item in messages))


if __name__=='__main__':unittest.main()
