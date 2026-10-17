"""Dense diagrams stay full-size, with every musical event and asset preserved."""
from pathlib import Path
import copy,sys,unittest,xml.etree.ElementTree as E
import tempfile,zipfile,shutil,json,io
from unittest.mock import patch
from contextlib import redirect_stdout
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'engine'))
import diagram_lanes as lanes


def crowded():
    diagrams=[];harmonies=[]
    for i in range(8):
        x=2+i*11
        diagrams.append(dict(row=0,bbox=[x+3,-7,x+17,-3],harmony_index=i))
        harmonies.append(dict(row=0,bbox=[x,-5,x+3,-2]))
    return dict(diagrams=diagrams,harmonies=harmonies,rows=[dict(row=0,bbox=[0,0,100,4],
        diagrams=list(range(8)),pairing_by_order_safe=True)],warnings=[],clipped=[],overlaps=[dict(type='image-image',a=0,b=1)])


def archive():
    root=E.Element('museScore');score=E.SubElement(root,'Score');E.SubElement(score,'Division').text='480'
    staff=E.SubElement(score,'Staff');measure=E.SubElement(staff,'Measure');voice=E.SubElement(measure,'voice')
    for i in range(8):
        h=E.SubElement(voice,'Harmony');E.SubElement(h,'root').text=str(i)
        im=E.SubElement(voice,'Image');E.SubElement(im,'offset',x='3',y='-7')
        E.SubElement(im,'size',w='14',h='4');E.SubElement(im,'path').text='keys.svg'
        chord=E.SubElement(voice,'Chord');E.SubElement(chord,'durationType').text='eighth'
        E.SubElement(E.SubElement(chord,'Note'),'pitch').text=str(60+i)
    return {'score.mscx':E.tostring(root),'Pictures/keys.svg':b'keyboard',
        'score_style.mss':b'<museScore><Style><minSystemDistance>18</minSystemDistance></Style></museScore>'}


class DiagramLaneTests(unittest.TestCase):
    def test_lanes_have_clearance_and_stay_within_staff(self):
        page=crowded();before=copy.deepcopy(page);positions,_=lanes.plan([page]);boxes=[]
        for i,d in enumerate(page['diagrams']):
            shift=positions[i];b=d['bbox'];box=[b[0]+shift['x'],b[1]+shift['y'],b[2]+shift['x'],b[3]+shift['y']]
            self.assertGreaterEqual(box[0],0);self.assertLessEqual(box[2],100)
            self.assertEqual(box[2]-box[0],14);self.assertLess(box[3],-5)
            for a in boxes:self.assertFalse(min(a[2],box[2])>max(a[0],box[0]) and min(a[3],box[3])>max(a[1],box[1]))
            boxes.append(box)
        self.assertEqual(page,before);self.assertEqual(max(p['lane'] for p in positions.values()),1)

    def test_clean_rows_untouched_and_global_indices_preserved(self):
        clean=crowded();clean['overlaps']=[]
        self.assertIsNone(lanes.plan([clean]))
        positions,_=lanes.plan([clean,crowded()]);self.assertEqual(set(positions),set(range(8,16)))

    def test_ambiguous_missing_or_overwide_geometry_is_rejected(self):
        for change in ('warnings','pairing','bbox','width'):
            page=crowded()
            if change=='warnings':page['warnings']=['unpaired']
            elif change=='pairing':page['rows'][0]['pairing_by_order_safe']=False
            elif change=='bbox':del page['rows'][0]['bbox']
            else:page['diagrams'][0]['bbox'][2]=200
            self.assertIsNone(lanes.plan([page]))

    def test_scale_invariance(self):
        page=crowded();scaled=copy.deepcopy(page)
        for item in scaled['diagrams']+scaled['harmonies']+scaled['rows']:item['bbox']=[x*5.5 for x in item['bbox']]
        left,_=lanes.plan([page]);right,_=lanes.plan([scaled])
        for i in left:
            for axis in ('x','y'):self.assertAlmostEqual(left[i][axis],right[i][axis])

    def test_preparation_preserves_music_assets_sizes_and_regular_spacing(self):
        before=archive();snapshot=copy.deepcopy(before);after,details=lanes.prepare(before,[crowded()])
        self.assertEqual(before,snapshot);self.assertTrue(lanes.music_preserved(before,after))
        self.assertTrue(details['size_preserved']);self.assertEqual(details['lanes'],2)
        root=E.fromstring(after['score.mscx'])
        self.assertIsNotNone(root.find('Score/Staff/Measure/vspacerUp'))
        self.assertTrue(all(im.find('size').attrib==dict(w='14',h='4') for im in root.findall('.//Image')))
        self.assertEqual(E.fromstring(after['score_style.mss']).findtext('Style/minSystemDistance'),'18')

    def test_preservation_guard_rejects_changed_note_timing_harmony_or_asset(self):
        before=archive()
        for target in ('pitch','durationType','root','asset'):
            changed=copy.deepcopy(before)
            if target=='asset':changed['Pictures/keys.svg']=b'different'
            else:
                root=E.fromstring(changed['score.mscx']);root.find('.//'+target).text='changed';changed['score.mscx']=E.tostring(root)
            self.assertFalse(lanes.music_preserved(before,changed))

    def run_candidate(self, reject=False, corrupt=False):
        import batch_score as engine
        with tempfile.TemporaryDirectory() as directory,redirect_stdout(io.StringIO()):
            out=Path(directory);final=out/'original.mscz'
            with zipfile.ZipFile(final,'w') as z:
                for name,data in archive().items():z.writestr(name,data)
            (out/'qa-1.svg').write_bytes(b'old');(out/'pagina-1.png').write_bytes(b'old')
            snapshot={p.name:p.read_bytes() for p in out.iterdir()}
            def render(args,log,timeout=90):
                if args[0]=='-o':
                    shutil.copyfile(args[2],args[1])
                    if corrupt:
                        with zipfile.ZipFile(args[1]) as z:files={n:z.read(n) for n in z.namelist()}
                        files['Pictures/keys.svg']=b'corrupt'
                        with zipfile.ZipFile(args[1],'w') as z:
                            for name,data in files.items():z.writestr(name,data)
                else:
                    job=json.loads(Path(args[-1]).read_text())[0];folder=Path(job['in']).parent
                    (folder/'qa-1.svg').write_bytes(b'new');(folder/'pagina-1.png').write_bytes(b'new')
                    (folder/'reaberto.musicxml').write_bytes(b'music')
            clean=crowded();clean['overlaps']=[];clean['svg_path']='qa-1.svg'
            report={'chord_symbols':8}
            with patch.object(engine,'call_musescore',side_effect=render),patch.object(engine,'analyze_svg',return_value=clean), \
                 patch.object(engine.compact_score_layout,'page_quality',return_value=dict(clipped=[],overlaps=[dict(kinds=['Image','Note'],cross_system=False)] if reject else [])):
                accepted=engine.repair_diagram_lanes(final,report,out,[crowded()],[])
            if reject or corrupt:
                self.assertFalse(accepted);self.assertEqual(snapshot,{p.name:p.read_bytes() for p in out.iterdir()})
                self.assertNotIn('diagram_lane_layout',report)
            else:
                self.assertTrue(accepted);self.assertTrue(report['geometric_layout_qa']['passed'])
                self.assertEqual((out/'pagina-1.png').read_bytes(),b'new')
                self.assertFalse(any(p.is_dir() for p in out.iterdir()))

    def test_native_candidate_is_published_after_validation(self):self.run_candidate()
    def test_candidate_colliding_with_notes_is_discarded(self):self.run_candidate(reject=True)
    def test_candidate_changing_assets_is_discarded(self):self.run_candidate(corrupt=True)

if __name__=='__main__':unittest.main()
