"""A compact candidate is optional; musical or geometric uncertainty keeps fallback."""
from pathlib import Path
from unittest.mock import patch
import copy, io, json, os, sys, tempfile, unittest, zipfile
from contextlib import redirect_stdout

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'engine'))
import batch_score as engine
import compact_score_layout as compact


def contents():
    native=b'''<museScore><Score><Staff><Measure><voice><Chord><durationType>quarter</durationType><Note><pitch>60</pitch></Note></Chord><StaffText><offset x="1" y="-17.7"/><size>9</size><text>Acoustic Guitar steel</text></StaffText><Image><offset x="4.5" y="-6.7"/><path>abc.svg</path><size w="14" h="4.17"/></Image></voice></Measure><Measure><voice><Rest><durationType>whole</durationType></Rest></voice></Measure></Staff></Score></museScore>'''
    style=b'<museScore><Style><spatium>1.75</spatium><minSystemDistance>35.2</minSystemDistance><staffTextFontSize>9</staffTextFontSize></Style></museScore>'
    return {'score.mscx':native,'score_style.mss':style,'Pictures/abc.svg':b'original diagram'}


def archive(path, data):
    with zipfile.ZipFile(path,'w') as target:
        for name,value in data.items():target.writestr(name,value)


class CompactLayoutTests(unittest.TestCase):
    def test_local_space_only_where_needed_preserves_every_music_and_size_property(self):
        before=contents();after,spaces=compact.prepare(before)
        self.assertTrue(compact.content_preserved(before,after))
        self.assertEqual(spaces,[{'measure':1,'space':26.7}])
        root=engine.E.fromstring(after['score.mscx'])
        self.assertIsNone(root.findall('Score/Staff/Measure')[1].find('vspacerUp'))
        self.assertEqual(root.find('.//StaffText/offset').attrib,{'x':'1','y':'-17.7'})
        self.assertEqual(before,contents())

    def test_music_label_inversion_font_and_staff_size_changes_are_rejected(self):
        before=contents();after,_=compact.prepare(before)
        changes=[('score.mscx',b'<pitch>60',b'<pitch>61'),
                 ('score.mscx',b'Acoustic Guitar steel',b'Guitar'),
                 ('score.mscx',b'<size>9',b'<size>8'),
                 ('score.mscx',b'x="1"',b'x="2"'),
                 ('score_style.mss',b'<spatium>1.75',b'<spatium>1.65'),
                 ('Pictures/abc.svg',b'original',b'different')]
        for name,old,new in changes:
            mutated=after.copy();mutated[name]=mutated[name].replace(old,new)
            self.assertFalse(compact.content_preserved(before,mutated),name)

    def test_spacing_choice_sets_range_and_room_above_raised_items(self):
        for spacing,low,high,space in (('compacto',10,12,25.7),('normal',12,24,26.7),('amplo',18,32,32.7)):
            after,spaces=compact.prepare(contents(),spacing)
            style=engine.E.fromstring(after['score_style.mss'])
            self.assertEqual((style.findtext('Style/minSystemDistance'),style.findtext('Style/maxSystemSpread')),(str(low),str(high)),spacing)
            self.assertAlmostEqual(spaces[0]['space'],space,msg=spacing)
            self.assertTrue(compact.content_preserved(contents(),after))

    def test_existing_larger_local_space_is_preserved(self):
        before=contents();before['score.mscx']=before['score.mscx'].replace(b'<Measure>',b'<Measure><vspacerUp>40</vspacerUp>',1)
        after,spaces=compact.prepare(before)
        self.assertEqual(spaces[0]['space'],40)

    def test_unsupported_multistaff_or_missing_style_is_not_modified(self):
        before=contents();before.pop('score_style.mss')
        self.assertIsNone(compact.prepare(before))
        before=contents();before['score.mscx']=before['score.mscx'].replace(b'</Score>',b'<Staff/></Score>')
        self.assertIsNone(compact.prepare(before))

    def geometry(self, body):
        svg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 1400">'
        svg+=''.join(f'<polyline class="StaffLines" points="100,{y} 900,{y}"/>' for y in range(200,241,10))
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'qa-1.svg';path.write_text(svg+body+'</svg>')
            return compact.page_quality(path)

    def test_note_and_label_collision_is_rejected(self):
        result=self.geometry('<path class="Note" d="M150 100 L250 100 L250 115 L150 115Z"/><path class="StaffText" d="M200 100 L290 100 L290 115 L200 115Z"/>')
        self.assertFalse(result['passed'])

    def test_stem_collision_is_rejected_even_with_zero_width(self):
        result=self.geometry('<polyline class="Stem" points="210,80 210,130"/><path class="StaffText" d="M200 100 L290 100 L290 115 L200 115Z"/>')
        self.assertFalse(result['passed'])

    def test_header_collision_and_page_clipping_are_rejected(self):
        result=self.geometry('<path class="Text" d="M200 100 L290 100 L290 115 L200 115Z"/><path class="Text" d="M250 100 L310 100 L310 115 L250 115Z"/>')
        self.assertFalse(result['passed'])
        self.assertFalse(self.geometry('<path class="Tempo" d="M200 -5 L290 -5 L290 115 L200 115Z"/>')['passed'])

    def test_overlapping_noteheads_are_rejected_even_if_nearest_staff_is_same(self):
        result=self.geometry('<path class="Note" d="M200 100 L290 100 L290 115 L200 115Z"/><path class="Note" d="M250 100 L310 100 L310 115 L250 115Z"/>')
        self.assertFalse(result['passed'])

    def run_candidate(self, problem=False, candidate_pages=2, failure=None, baseline_problem=False, spacing=None, lanes=True):
        with tempfile.TemporaryDirectory() as directory,redirect_stdout(io.StringIO()):
            out=Path(directory);final=out/'original.mscz';archive(final,contents())
            for i in range(1,4):
                (out/f'qa-{i}.svg').write_bytes(b'old svg')
                (out/f'pagina-{i}.png').write_bytes(b'old png')
            (out/'reaberto.musicxml').write_bytes(b'<score-partwise><part><measure number="1"/></part></score-partwise>')
            baseline={path.name:path.read_bytes() for path in out.iterdir() if path.is_file()}
            report={'instrument_label_lanes':{},'instrument_labels':[{}],'chord_symbols':1,'measures':1,
                    'instrument_label_layout_qa':{'passed':True},'geometric_layout_qa':{'passed':True}}
            report['instrument_label_lanes']={'count':2}
            if not lanes:report.pop('instrument_label_lanes');report['instrument_labels']=[]
            if spacing:report['score_style']={'spacing':spacing}
            renders=[]
            def fake_musescore(args,log,timeout=90):
                renders.append(log.name)
                if failure=='render':raise engine.NeedsReview('renderer unavailable')
                # Um job grava a candidata e renderiza os SVG e o MusicXML (sem páginas PNG).
                job=json.loads(Path(args[-1]).read_text(encoding='utf8'))[0];folder=log.parent
                self.assertEqual([Path(path).name for path in job['out']],['compacta.mscz','qa.svg','reaberto.musicxml'])
                Path(job['out'][0]).write_bytes(Path(job['in']).read_bytes())
                for i in range(1,candidate_pages+1):
                    (folder/f'qa-{i}.svg').write_bytes(b'new svg')
                reopened=b'<score-partwise><part><measure number="1"/></part></score-partwise>'
                if failure=='incomplete_reopen':reopened=b'<score-partwise><part/></score-partwise>'
                (folder/'reaberto.musicxml').write_bytes(reopened)
            labels=1 if lanes else 0
            label=lambda path:dict(path=str(path),labels=[{}]*labels if path.name=='qa-1.svg' else [],overlaps=[],clipped=[])
            diagram=lambda path:dict(svg_path=str(path),diagrams=[{}] if path.name=='qa-1.svg' else [],overlaps=[],clipped=[],warnings=[])
            # The candidate renders in compactacao/; the validated fallback stays in out/.
            def ink(path):
                bad=(problem=='first' and len(renders)==1 or problem is True) if path.parent.name=='compactacao' else baseline_problem
                tie=[{'kinds':['TieSegment','Harmony'],'cross_system':False}]
                return dict(path=str(path),passed=not bad,overlaps=tie if bad else [],clipped=[])
            real_replace=os.replace;calls=0
            def replace(source,destination):
                nonlocal calls
                calls+=1
                if failure=='promotion' and calls==2:raise OSError('simulated interruption')
                return real_replace(source,destination)
            with patch.object(engine,'call_musescore',side_effect=fake_musescore),patch.object(engine,'instrument_label_geometry',side_effect=label),patch.object(engine,'analyze_svg',side_effect=diagram),patch.object(compact,'page_quality',side_effect=ink),patch.object(engine.os,'replace',side_effect=replace):
                engine.compact_instrument_label_layout(final,report,out)
            actual={path.name:path.read_bytes() for path in out.iterdir() if path.is_file()}
            if not report['compact_layout']['applied']:self.assertEqual(actual,baseline)
            else:
                # As páginas PNG antigas saem; render_final_pages reabre a partitura publicada.
                self.assertFalse(any(name.startswith('pagina-') for name in actual))
                if candidate_pages<3:self.assertNotIn('qa-3.svg',actual)
                self.assertEqual(actual['qa-1.svg'],b'new svg')
                if lanes:self.assertEqual(report['instrument_label_layout_qa']['pages'],candidate_pages)
            return report['compact_layout']

    def test_success_replaces_the_whole_page_set(self):
        self.assertTrue(self.run_candidate()['applied'])

    def test_unsafe_candidate_preserves_fallback_byte_for_byte(self):
        self.assertEqual(self.run_candidate(problem=True)['reason'],'candidate_geometry_not_safe')

    def test_finding_already_in_the_validated_score_does_not_block_the_candidate(self):
        result=self.run_candidate(problem=True,baseline_problem=True)
        self.assertTrue(result['applied']);self.assertEqual(result['checks']['inherited_problems'],2)

    def test_compact_choice_compacts_without_instrument_labels_and_at_equal_page_count(self):
        result=self.run_candidate(candidate_pages=3,spacing='compacto',lanes=False)
        self.assertTrue(result['applied']);self.assertEqual(result['spacing'],'compacto')

    def test_unsafe_tight_compact_candidate_gets_one_try_with_the_usual_room(self):
        result=self.run_candidate(problem='first',spacing='compacto')
        self.assertTrue(result['applied']);self.assertEqual(result['mode'],'compacto-seguro')
        self.assertEqual(result['first_attempt']['reason'],'candidate_geometry_not_safe')
        self.assertEqual(self.run_candidate(problem=True,spacing='compacto')['reason'],'candidate_geometry_not_safe')

    def test_normal_choice_without_instrument_labels_keeps_the_score(self):
        with tempfile.TemporaryDirectory() as directory:
            report={'score_style':{'spacing':'normal'}}
            engine.compact_instrument_label_layout(Path(directory)/'x.mscz',report,Path(directory))
            self.assertNotIn('compact_layout',report)

    def test_no_page_reduction_preserves_fallback(self):
        self.assertEqual(self.run_candidate(candidate_pages=3)['reason'],'no_page_reduction')

    def test_render_failure_preserves_fallback(self):
        self.assertEqual(self.run_candidate(failure='render')['reason'],'candidate_failed')

    def test_incomplete_musicxml_reopen_preserves_fallback(self):
        self.assertEqual(self.run_candidate(failure='incomplete_reopen')['reason'],'incomplete_native_reopen')

    def test_mid_promotion_failure_restores_every_original_page_and_score(self):
        self.assertEqual(self.run_candidate(failure='promotion')['reason'],'candidate_failed')


if __name__=='__main__':unittest.main(verbosity=2)
