"""Layout repair must validate the rendered page, never accept clipped diagrams."""
from pathlib import Path
from unittest.mock import patch
import copy, json, os, shutil, sys, tempfile, unittest, zipfile
import xml.etree.ElementTree as E

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreSpacingTests'))
import batch_score as engine


def page_geometry():
    return dict(
        diagrams=[dict(index=0, row=0, bbox=[4.5, 0, 18.5, 4.2], harmony_index=0)],
        harmonies=[dict(index=0, row=0, bbox=[0, 0, 1.5, 3], diagram_index=0)],
        rows=[dict(row=0, diagrams=[0], harmonies=[0], pairing_by_order_safe=True)],
        warnings=[], overlaps=[], clipped=[])


def crowded_geometry(count=5, scale=1):
    page = dict(diagrams=[], harmonies=[], rows=[dict(row=0, diagrams=list(range(count)),
        harmonies=list(range(count)), pairing_by_order_safe=True)], warnings=[], clipped=[],
        overlaps=[dict(type='harmony-image', harmony=1, diagram=0, own_pair=False, width=2.5 * scale)])
    for index in range(count):
        x = 16 * index
        page['diagrams'].append(dict(index=index, row=0, harmony_index=index,
            bbox=[value * scale for value in (x + 4.5, 0, x + 18.5, 4.2)]))
        page['harmonies'].append(dict(index=index, row=0, diagram_index=index,
            bbox=[value * scale for value in (x, 0, x + 1.5, 3)]))
    return page


class DiagramSpacingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.out = Path(self.temporary.name)
        self.final = self.out / 'score.mscz'
        with zipfile.ZipFile(self.final, 'w') as score:
            score.writestr('score.mscx', '''<museScore><Score><Division>480</Division><Staff><Measure><voice>
                <Harmony><harmonyInfo><root>14</root></harmonyInfo></Harmony>
                <Image><offset x="4.5" y="-6.7"/><path>keyboard.svg</path><size w="14" h="4.2"/></Image>
                <Rest><durationType>quarter</durationType></Rest>
                </voice></Measure></Staff></Score></museScore>''')
            score.writestr('Pictures/keyboard.svg', 'unchanged keyboard asset')
        (self.out / 'qa-1.svg').touch()
        self.report = dict(chord_symbols=1, chords=[dict(tick=0)],
            bars=[dict(start=0, end=engine.DIV)], image_spacing=[0.0], end_spacing={})

    def fake_musescore(self, args, log, timeout=90):
        if args[0] == '-o':
            shutil.copyfile(args[2], args[1])
        else:
            (self.out / 'qa-1.svg').touch()

    def repair(self, geometry):
        # Fixed observations simulate a renderer that cannot resolve the defect.
        with patch.object(engine, 'analyze_svg', side_effect=lambda _: copy.deepcopy(geometry)), \
                patch.object(engine, 'call_musescore', side_effect=self.fake_musescore) as renderer:
            engine.repair_spacing(self.final, self.report, self.out)
        return renderer

    def test_valid_geometry_does_not_rewrite_music_or_relaunch_musescore(self):
        before = self.final.read_bytes()
        renderer = self.repair(page_geometry())
        renderer.assert_not_called()
        self.assertEqual(self.final.read_bytes(), before)
        self.assertTrue(self.report['geometric_layout_qa']['passed'])
        self.assertEqual(self.report['geometric_layout_qa']['passes'],
            [{'pass': 0, 'overlaps': 0, 'margin_overflows': 0}])
        saved = json.loads((self.out / 'geometria_verificada.json').read_text(encoding='utf8'))
        self.assertEqual(saved, [page_geometry()])

    def test_persistent_overlap_fails_with_structured_diagnostics(self):
        geometry = page_geometry()
        geometry['overlaps'] = [dict(type='harmony-image', harmony=0, diagram=0,
            own_pair=True, width=1.0)]
        with self.assertRaises(engine.NeedsReview) as raised:
            self.repair(geometry)
        self.assertEqual(raised.exception.kind, 'diagram_layout')
        self.assertGreater(len(raised.exception.layout_history), 1)
        self.assertLessEqual(len(raised.exception.layout_history), 10)
        self.assertTrue(all(item['overlaps'] for item in raised.exception.layout_history))
        self.assertNotIn('"pass"', str(raised.exception))
        self.assertFalse(self.report.get('geometric_layout_qa', {}).get('passed', False))

    def test_no_clipped_content_can_receive_passed_status(self):
        for kind in ('image', 'harmony'):
            for edge in ('staff_right', 'page_left', 'page_right', 'page_top', 'page_bottom'):
                with self.subTest(kind=kind, edge=edge):
                    geometry = page_geometry()
                    geometry['clipped'] = [dict(type=kind, index=0, edge=edge,
                        overflow=1.5, bbox=[4.5, 0, 18.5, 4.2])]
                    with self.assertRaises(engine.NeedsReview) as raised:
                        self.repair(geometry)
                    self.assertEqual(raised.exception.kind, 'diagram_layout')
                    self.assertFalse(self.report.get('geometric_layout_qa', {}).get('passed', False))

    def test_missing_diagram_is_not_accepted_as_clean_geometry(self):
        geometry = page_geometry()
        geometry['diagrams'] = []
        with self.assertRaises((AssertionError, engine.NeedsReview)):
            self.repair(geometry)
        self.assertFalse(self.report.get('geometric_layout_qa', {}).get('passed', False))

    def test_crowded_row_reclaims_padding_without_changing_diagram_size(self):
        for scale in (1, 5.5):
            with self.subTest(svg_scale=scale):
                geometry = crowded_geometry(scale=scale)
                before = copy.deepcopy(geometry)
                compacted = set()
                moves = engine._compact_diagram_offsets(geometry, 37, compacted)
                self.assertEqual(set(moves), set(range(37, 42)))
                for index, diagram in enumerate(geometry['diagrams']):
                    left = diagram['bbox'][0] + moves[index + 37] * scale
                    label_right = geometry['harmonies'][index]['bbox'][2]
                    self.assertAlmostEqual(left - label_right, .8 * scale)
                    self.assertAlmostEqual(diagram['bbox'][2] - diagram['bbox'][0], 14 * scale)
                self.assertEqual(geometry, before)
                self.assertEqual(engine._compact_diagram_offsets(geometry, 37, compacted), {})

    def test_long_chord_label_gets_positive_clearance_in_compacted_row(self):
        geometry = crowded_geometry()
        geometry['harmonies'][2]['bbox'][2] += 5
        moves = engine._compact_diagram_offsets(geometry, 0, set())
        self.assertGreater(moves[2], 0)
        self.assertAlmostEqual(geometry['diagrams'][2]['bbox'][0] + moves[2]
            - geometry['harmonies'][2]['bbox'][2], .8)

    def test_sparse_clean_or_unpaired_rows_keep_existing_positions(self):
        sparse = crowded_geometry(count=3)
        clean = crowded_geometry(); clean['overlaps'] = []
        unpaired = crowded_geometry(); unpaired['rows'][0]['pairing_by_order_safe'] = False
        for scenario, geometry in (('sparse', sparse), ('clean', clean), ('unpaired', unpaired)):
            with self.subTest(scenario=scenario):
                compacted = set()
                self.assertEqual(engine._compact_diagram_offsets(geometry, 0, compacted), {})
                self.assertEqual(compacted, set())

    def test_crowded_clipped_row_can_compact_even_without_overlap(self):
        geometry = crowded_geometry(); geometry['overlaps'] = []
        geometry['clipped'] = [dict(type='image', index=4, edge='staff_right', overflow=2)]
        self.assertEqual(len(engine._compact_diagram_offsets(geometry, 0, set())), 5)

    def test_repair_changes_only_layout_and_preserves_all_musical_events_and_assets(self):
        root = E.Element('museScore'); score = E.SubElement(root, 'Score')
        E.SubElement(score, 'Division').text = '480'
        voice = E.SubElement(E.SubElement(E.SubElement(score, 'Staff'), 'Measure'), 'voice')
        durations = ('quarter', 'eighth', 'quarter', 'eighth', 'eighth')
        for index, duration in enumerate(durations):
            harmony = E.SubElement(voice, 'Harmony')
            E.SubElement(harmony, 'root').text = str(14 + index % 2)
            image = E.SubElement(voice, 'Image')
            E.SubElement(image, 'offset', x='4.5', y='-6.7')
            E.SubElement(image, 'path').text = 'keyboard.svg'
            E.SubElement(image, 'size', w='14', h='4.2')
            rest = E.SubElement(voice, 'Rest')
            E.SubElement(rest, 'durationType').text = duration
        musical_before = [E.tostring(event) for event in voice if event.tag != 'Image']
        with zipfile.ZipFile(self.final, 'w') as archive:
            archive.writestr('score.mscx', E.tostring(root))
            archive.writestr('Pictures/keyboard.svg', b'unchanged keyboard asset')
        self.report.update(chord_symbols=5, chords=[dict(tick=round(t * engine.DIV))
            for t in (0, 1.5, 2, 3, 3.5)], bars=[dict(start=0, end=4 * engine.DIV)], image_spacing=[0.] * 5)
        failed = crowded_geometry(); clean = copy.deepcopy(failed); clean['overlaps'] = []
        with patch.object(engine, 'analyze_svg', side_effect=[failed, clean]), \
                patch.object(engine, 'call_musescore', side_effect=self.fake_musescore):
            engine.repair_spacing(self.final, self.report, self.out)
        with zipfile.ZipFile(self.final) as archive:
            after = E.fromstring(archive.read('score.mscx')).find('Score/Staff/Measure/voice')
            self.assertEqual(archive.read('Pictures/keyboard.svg'), b'unchanged keyboard asset')
        self.assertEqual([E.tostring(event) for event in after if event.tag != 'Image'], musical_before)
        self.assertEqual(len(after.findall('Image')), 5)
        for image in after.findall('Image'):
            self.assertAlmostEqual(float(image.find('offset').get('x')), 2.3)
            self.assertEqual(image.find('offset').get('y'), '-6.7')
            self.assertEqual(image.find('size').attrib, dict(w='14', h='4.2'))
        self.assertTrue(self.report['geometric_layout_qa']['passed'])


if __name__ == '__main__': unittest.main()
