"""Opt-in real MuseScore round trips: TECLAS_TEST_NATIVE_LABELS=1.

Set TECLAS_LABEL_QA to retain the isolated MIDIs, pages and validation reports.
"""
from pathlib import Path
import copy, hashlib, io, json, os, struct, tempfile, time, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
from test_instrument_labels import A, B, PPQ, ROOT, engine, note, worker, harmony, sequence


def write_midi(path,notes):
    def vlq(value):
        data=[value&127];value>>=7
        while value:data.insert(0,(value&127)|128);value>>=7
        return bytes(data)
    conductor=b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    count=max(n['track'] for n in notes)+1
    data=b'MThd'+struct.pack('>IHHH',6,1,count,PPQ)+b'MTrk'+struct.pack('>I',len(conductor))+conductor
    for index in range(1,count):
        events=[];programs={}
        for n in sorted((n for n in notes if n['track']==index),key=lambda n:n['start']):
            ch=n['channel']-1
            if programs.get(ch)!=n['program']:
                events.append((n['start'],1,bytes([0xc0+ch,n['program']])));programs[ch]=n['program']
            events.extend([(n['start'],2,bytes([0x90+ch,n['pitch'],n['velocity']])),
                           (n['end'],0,bytes([0x80+ch,n['pitch'],0]))])
        track=b'';previous=0
        for tick,_,message in sorted(events):track+=vlq(tick-previous)+message;previous=tick
        track+=b'\x00\xff\x2f\x00';data+=b'MTrk'+struct.pack('>I',len(track))+track
    path.write_bytes(data)


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS')=='1','Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeInstrumentLabelTests(unittest.TestCase):
    def setUp(self):
        self.temporary=None
        if os.environ.get('TECLAS_LABEL_QA'):self.base=Path(os.environ['TECLAS_LABEL_QA'])
        else:self.temporary=tempfile.TemporaryDirectory();self.base=Path(self.temporary.name)
        self.base.mkdir(parents=True,exist_ok=True)
    def tearDown(self):
        if self.temporary:self.temporary.cleanup()
    def render(self,name,notes,sources=(A,B),transpose=0):
        out=self.base/name;out.mkdir(parents=True,exist_ok=True)
        midi=out/'source.mid';write_midi(midi,notes);before=hashlib.sha256(midi.read_bytes()).hexdigest()
        parsed=engine.parse_midi(midi);config=worker.manual_melody_config(list(sources));config['transpose_semitones']=transpose
        with patch.object(engine,'BATCH',self.base),redirect_stdout(io.StringIO()):
            xml,report=engine.build_musicxml(parsed,config,harmony(),out,name)
            start=time.monotonic()
            try:final=engine.convert_score(xml,report,out,name)
            except engine.NeedsReview:
                (out/'validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
                raise
        report['elapsed_seconds']=round(time.monotonic()-start,3)
        self.assertEqual(hashlib.sha256(midi.read_bytes()).hexdigest(),before)
        self.assertTrue(report['native_reopened'])
        self.assertTrue(report['geometric_layout_qa']['passed'])
        if report.get('instrument_labels'):self.assertTrue(report['instrument_label_layout_qa']['passed'])
        (out/'validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
        with zipfile.ZipFile(final) as archive:
            native=engine.E.fromstring(archive.read(next(name for name in archive.namelist() if name.endswith('.mscx'))))
        self.assertFalse(native.findall('.//InstrumentChange'))
        self.assertEqual(len(native.findall('Score/Staff')),1)
        return report,native

    def test_return_transposition_and_tie_across_system_survive_reopen(self):
        notes=[n for n in sequence() if not 11<=n['start']/PPQ<14]
        next(n for n in notes if n['start']==10*PPQ)['end']=14*PPQ
        third=dict(track=3,channel=3,program=24)
        for n in notes:
            if n['start']>=24*PPQ:n.update(third)
        report,native=self.render('return-tie-transposed',notes,sources=(A,B,third),transpose=2)
        self.assertEqual([(x['quarter'],x['text']) for x in report['instrument_labels']],[(0,'Flute'),(8,'Acoustic Grand Piano'),(20,'Flute'),(24,'Acoustic Guitar nylon')])
        self.assertEqual([n['pitch'] for n in report['notes']],[n['pitch']+2 for n in notes])
        self.assertTrue(native.findall('.//Tie') or native.findall('.//Spanner'))
        starts=[report['bars'][index]['start'] for index in report['system_starts']]
        self.assertTrue(any(n['start']<tick<n['end'] for n in report['notes'] for tick in starts))

    def test_late_entry_reserves_complete_name_inside_right_margin(self):
        notes=[note(i,72+i%3,B) for i in range(35)]+[note(35,60,A)]
        report,_=self.render('late-entry',notes)
        self.assertEqual([x['quarter'] for x in report['instrument_labels']],[0,35])
        self.assertTrue(report['instrument_label_layout_qa']['passed'])

    def test_program_change_within_same_track_survives(self):
        second=dict(A,program=4)
        notes=[note(i,60+i%3,A if i<12 else second) for i in range(24)]
        report,_=self.render('program-change',notes,sources=(A,second))
        self.assertEqual([x['text'] for x in report['instrument_labels']],['Acoustic Grand Piano','Electric Piano 1'])

    def test_dense_alternation_with_large_margin_overflow_still_requires_review(self):
        other=dict(B,program=24)
        notes=[note(i,60+i%3,A if i%2==0 else other) for i in range(24)]
        with self.assertRaises(engine.NeedsReview) as caught:self.render('dense',notes,sources=(A,other))
        self.assertEqual(caught.exception.kind,'instrument_label_layout')
        self.assertTrue(caught.exception.review_points)
        report=json.loads((self.base/'dense/validation.json').read_text(encoding='utf8'))
        self.assertEqual(len(report['instrument_labels']),24)

    def test_crowded_alternation_reserves_lanes_without_missing_instruments(self):
        other=dict(B,program=24)
        notes=[note(i,60+i%3,other if i<8 and i%2 else A) for i in range(24)]
        report,native=self.render('crowded',notes,sources=(A,other))
        self.assertEqual(len(report['instrument_labels']),9)
        self.assertEqual(len(native.findall('.//StaffText')),9)
        self.assertTrue(report['instrument_label_layout_qa']['passed'])
        self.assertGreater(report['instrument_label_lanes']['count'],1)

    def test_single_source_has_no_new_text_or_layout_qa(self):
        report,native=self.render('single', [note(i,60+i%3,A) for i in range(24)],sources=(A,))
        self.assertFalse(native.findall('.//StaffText'))
        self.assertNotIn('instrument_label_layout_qa',report)
        self.assertNotIn('instrument_labels',report)


if __name__=='__main__':unittest.main(verbosity=2)
