"""Source annotations follow winning quantized attacks without changing music."""
from pathlib import Path
import copy, hashlib, io, json, os, struct, tempfile, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
from test_priority_melody import A, B, PPQ, ROOT, engine, note, parsed, worker


def harmony():
    return dict(key={'fifths':0,'minor':False},events=[dict(tick=0,root=0,quality='',confidence=1.,pitch_classes=[0,4,7])])


def sequence():
    return ([note(i,72+i%3,B) for i in range(8)]
            +[note(i,60+i%3,A) for i in range(8,20)]
            +[note(i,72+i%3,B) for i in range(20,28)])


class InstrumentLabelTests(unittest.TestCase):
    def test_first_entry_changes_and_return_use_gm_names(self):
        events,_,_=engine.melody_events(parsed(sequence()),worker.manual_melody_config([A,B]),0,28*PPQ)
        labels=engine.instrument_labels(events,4*PPQ)
        self.assertEqual([(x['tick']/PPQ,x['text']) for x in labels],[(0,'Flute'),(8,'Acoustic Grand Piano'),(20,'Flute')])
        self.assertEqual([x['original_quarter'] for x in labels],[4,12,24])
        self.assertEqual([x['source']['priority'] for x in labels],[2,1,2])

    def test_suppressed_source_and_duplicate_doubling_never_get_labels(self):
        for duplicate in (False,True):
            notes=[note(i,60+i%3,A) for i in range(24)]+[note(i,(60 if duplicate else 84)+i%3,B) for i in range(24)]
            events,_,_=engine.melody_events(parsed(notes),worker.manual_melody_config([A,B]),0,24*PPQ)
            labels=engine.instrument_labels(events)
            self.assertEqual([x['text'] for x in labels],['Acoustic Grand Piano'])
            self.assertTrue(all(x['source']['program']==0 for x in events))

    def test_same_track_channel_different_program_is_a_new_source(self):
        changed=dict(A,program=4)
        notes=[note(i,60+i%3,A if i<12 else changed) for i in range(24)]
        events,_,_=engine.melody_events(parsed(notes),worker.manual_melody_config([A,changed]),0,24*PPQ)
        self.assertEqual([x['text'] for x in engine.instrument_labels(events)],['Acoustic Grand Piano','Electric Piano 1'])

    def test_same_program_different_tracks_still_marks_source_change(self):
        changed=dict(B,program=0)
        notes=[note(i,60+i%3,A if i<12 else changed) for i in range(24)]
        events,_,_=engine.melody_events(parsed(notes),worker.manual_melody_config([A,changed]),0,24*PPQ)
        labels=engine.instrument_labels(events)
        self.assertEqual([x['text'] for x in labels],['Acoustic Grand Piano']*2)
        self.assertNotEqual(labels[0]['source']['track'],labels[1]['source']['track'])

    def test_quantized_not_raw_onset_anchors_the_annotation(self):
        notes=sequence();notes[8]['start']+=round(.08*PPQ)
        events,_,_=engine.melody_events(parsed(notes),worker.manual_melody_config([A,B]),0,28*PPQ)
        labels=engine.instrument_labels(events)
        piano=next(event for event in events if event['source']['program']==0)
        self.assertEqual(labels[1]['tick'],piano['start'])
        self.assertNotEqual(labels[1]['tick'],notes[8]['start'])

    def test_silence_alone_does_not_duplicate_the_same_source_label(self):
        events=[dict(start=0,source=A),dict(start=8*PPQ,source=A)]
        self.assertEqual(len(engine.instrument_labels(events)),1)

    def test_single_source_and_reviewed_paths_have_no_annotations_or_provenance(self):
        for config in (dict(melody_sources=[A]),dict(melody_sources=[A,B])):
            notes=[note(i,60+i%3,A) for i in range(24)]
            with tempfile.TemporaryDirectory() as directory,redirect_stdout(io.StringIO()):
                path,report=engine.build_musicxml(parsed(notes),config,harmony(),Path(directory),'Unchanged')
                self.assertNotIn('instrument_labels',report)
                self.assertTrue(all('source' not in event for event in report['notes']))
                self.assertFalse(engine.E.parse(path).findall('.//direction-type/words'))

    def test_tied_note_and_transposition_preserve_source_and_label_attack(self):
        notes=sequence();notes=[n for n in notes if not 11<=n['start']/PPQ<14]
        next(n for n in notes if n['start']==10*PPQ)['end']=14*PPQ
        config=worker.manual_melody_config([A,B]);config['transpose_semitones']=2
        with tempfile.TemporaryDirectory() as directory,redirect_stdout(io.StringIO()):
            path,report=engine.build_musicxml(parsed(notes),config,harmony(),Path(directory),'Ties')
            tree=engine.E.parse(path)
            self.assertEqual([x['quarter'] for x in report['instrument_labels']],[0,8,20])
            self.assertTrue(tree.findall('.//tie'))
            self.assertEqual([n['pitch'] for n in report['notes']],[n['pitch']+2 for n in notes])
            self.assertEqual(len(tree.findall('.//direction-type/words')),3)
            self.assertEqual(len(tree.findall('./part-list/score-part/midi-instrument')),1)
            self.assertEqual(tree.findtext('.//midi-program'),'1')

    def test_rendered_geometry_detects_label_collisions_and_margins(self):
        svg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 1400">'
        svg+=''.join(f'<polyline class="StaffLines" points="100,{y} 900,{y}"/>' for y in range(200,241,10))
        svg+='<path class="StaffText" d="M150 100 L250 100 L250 115 L150 115Z"/>'
        svg+='<path class="StaffText" d="M200 100 L290 100 L290 115 L200 115Z"/>'
        svg+='<path class="StaffText" d="M850 100 L930 100 L930 115 L850 115Z"/></svg>'
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'qa-1.svg';path.write_text(svg,encoding='utf8')
            result=engine.instrument_label_geometry(path)
            self.assertEqual(len(result['labels']),3)
            self.assertEqual(result['overlaps'],[{'index':0,'other_label':1,'kind':'StaffText'}])
            self.assertAlmostEqual(result['clipped'][0]['shift_spatium'],-3)

    def test_embedded_image_bounds_and_large_margin_shift_are_not_ignored(self):
        svg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 1400">'
        svg+=''.join(f'<polyline class="StaffLines" points="100,{y} 900,{y}"/>' for y in range(200,241,10))
        svg+='<path class="StaffText" d="M150 100 L250 100 L250 115 L150 115Z"/>'
        svg+='<image x="200" y="90" width="150" height="40"/>'
        svg+='<path class="StaffText" d="M910 100 L980 100 L980 115 L910 115Z"/></svg>'
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'qa-1.svg';path.write_text(svg,encoding='utf8')
            result=engine.instrument_label_geometry(path)
            self.assertIn({'index':0,'kind':'Image'},result['overlaps'])
            self.assertIn({'index':1,'reason':'correction_too_large'},result['clipped'])

    def test_dense_layout_returns_readable_review_points_without_hiding_names(self):
        geometry=dict(path='qa-1.svg',labels=[{},{}],overlaps=[dict(index=0,other_label=1,kind='StaffText')],clipped=[])
        labels=[dict(original_quarter=0),dict(original_quarter=1)]
        report=dict(instrument_labels=labels,origin_removed_quarters=0,bars=[dict(start=0,end=4*PPQ,meter=(4,4))])
        with tempfile.TemporaryDirectory() as directory,patch.object(engine,'instrument_label_geometry',return_value=geometry):
            out=Path(directory);(out/'qa-1.svg').write_text('<svg/>')
            with self.assertRaises(engine.NeedsReview) as caught:engine.verify_instrument_label_layout(out/'unused.mscz',report,out)
            self.assertEqual(caught.exception.kind,'instrument_label_layout')
            self.assertTrue(caught.exception.review_points)
            self.assertIn('instrumento',str(caught.exception))
            self.assertEqual(report['instrument_labels'],labels)

    def test_dense_names_use_separate_lanes_without_moving_attack_anchors(self):
        page=dict(rows=[dict(row=0,bbox=[100,400,900,440])],clipped=[],labels=[
            dict(row=0,bbox=[150,280,340,295]),dict(row=0,bbox=[180,280,270,295]),
            dict(row=0,bbox=[230,280,420,295]),dict(row=0,bbox=[450,280,640,295])])
        positions,extra=engine._instrument_label_lanes([page])
        self.assertEqual([positions[i]['lane'] for i in range(4)],[0,1,2,0])
        self.assertEqual([positions[i]['x'] for i in range(4)],[0]*4)
        self.assertGreater(extra,4)
        boxes=[]
        for i,text in enumerate(page['labels']):
            box=text['bbox'];dy=positions[i]['y']*10;boxes.append([box[0],box[1]+dy,box[2],box[3]+dy])
        self.assertFalse(any(engine.svg_geometry._intersection(a,b) for i,a in enumerate(boxes) for b in boxes[i+1:]))

    def test_lane_assignment_accounts_for_safe_margin_correction(self):
        page=dict(rows=[dict(row=0,bbox=[100,400,900,440])],clipped=[dict(index=1,shift_spatium=-3)],labels=[
            dict(row=0,bbox=[780,280,860,295]),dict(row=0,bbox=[850,280,930,295])])
        positions,_=engine._instrument_label_lanes([page])
        self.assertEqual(positions[1]['x'],-3)
        self.assertEqual(positions[1]['lane'],1)

    def test_unmeasurable_or_extreme_density_keeps_review_gate(self):
        page=dict(rows=[dict(row=0,bbox=[100,400,900,440])],clipped=[],labels=[dict(row=0,bbox=[150,280,340,295])]*9)
        self.assertIsNone(engine._instrument_label_lanes([page]))
        page['labels']=page['labels'][:1];page['clipped']=[dict(index=0,reason='page_height')]
        self.assertIsNone(engine._instrument_label_lanes([page]))


if __name__=='__main__':unittest.main(verbosity=2)
